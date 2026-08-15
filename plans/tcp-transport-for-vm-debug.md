# TCP 连接方式回归 —— 本地虚拟机 Debug 用（最小改动版）

> 项目：JNOmultiplayerTest（SimpleRockets 2 / JNO 联机 mod aMptest）
> 创建日期：2026-08-15
> 状态：规划中（待实现）

---

## 一、目标

当前 [`MpNetworkManager.cs:34`](../Assets/Scripts/Net/MpNetworkManager.cs:34) 把 `Transport` 硬编码为 `SteamTransport`。
需求：**把 TCP 加回来作为独立连接方式，仅用于本机 + 虚拟机 debug**，不做多余抽象、不加 UI/设置项，改动越小越好。

- 默认仍是 Steam（不动现有行为）；
- debug 时用 `TcpHostLobby` / `TcpJoinLobby` 命令切到 `TcpTransport`（[`TcpTransport.cs`](../Assets/Scripts/Net/TcpTransport.cs) 已完整可用）；
- 房主监听 `IPAddress.Any:端口`，虚拟机按宿主 IP:端口 连接。

---

## 二、为什么必须有一个小接口（不可省的最少抽象）

`MpNetworkManager` 全程用 `Transport.xxx`（`Start`/`StartClient`/`DrainIncoming`/`SendTo`/`Broadcast`/`CheckTimeouts`/`GetPeers`/`GetPeersCount`/事件…）调用传输层。
要让 `Transport` 既能是 `SteamTransport` 又能是 `TcpTransport`，**必须有一个共同引用类型**（接口或基类），否则就得把管理器里十几个调用点全部 if/else 复制两份——那才是真正增加工作量。

所以最小方案 = **一个很薄的 `IMpTransport` 接口**（两个传输类签名本来就完全一致，实现只是各加 `: IMpTransport` 一行），其余一律不加：
- ❌ 不加 `MpTransportType` 枚举 / `SetTransportMode` 选择器
- ❌ 不加 UI 按钮
- ❌ 不加 `ModSettings.TransportMode`
- ❌ 不碰 `LiteNetLibTransport`

---

## 三、改动清单

### 3.1 新增 `Assets/Scripts/Net/IMpTransport.cs`

只含接口，内容即 [`SteamTransport.cs`](../Assets/Scripts/Net/SteamTransport.cs) / [`TcpTransport.cs`](../Assets/Scripts/Net/TcpTransport.cs) 现有公共成员的声明：

```csharp
using System;
using System.Collections.Generic;

namespace Assets.Scripts.Net
{
    /// <summary>统一传输层契约（SteamTransport / TcpTransport 共用）。</summary>
    public interface IMpTransport : IDisposable
    {
        event Action<MpPeer, byte[]> OnDataReceived;
        event Action<MpPeer> OnPeerTimeout;
        int LocalPort { get; }
        bool IsRunning { get; }
        bool Start(int port);
        bool StartClient(string host, int port, byte[] helloPacket);
        void Stop();
        void DrainIncoming();
        void SendTo(MpPeer peer, byte[] data);
        void Broadcast(byte[] data);
        void CheckTimeouts(long timeoutMs);
        IReadOnlyCollection<MpPeer> GetPeers();
        int GetPeersCount();
    }
}
```

### 3.2 两个传输类实现接口（各 1 行）

- [`TcpTransport.cs:18`](../Assets/Scripts/Net/TcpTransport.cs:18)：`public class TcpTransport : IMpTransport`
- [`SteamTransport.cs:21`](../Assets/Scripts/Net/SteamTransport.cs:21)：`public class SteamTransport : IMpTransport`（`LocalSteamId` 是额外成员，保留，不进接口）
- [`LiteNetLibTransport.cs`](../Assets/Scripts/Net/LiteNetLibTransport.cs) **不动**

### 3.3 `MpNetworkManager`（[`MpNetworkManager.cs:34`](../Assets/Scripts/Net/MpNetworkManager.cs:34)）

```csharp
// 原：public SteamTransport Transport = new SteamTransport();
[NonSerialized]
public IMpTransport Transport = new SteamTransport(); // 默认 Steam
```

新增一个很小的运行时切换方法（`Awake` 里现有的订阅逻辑抽成公共步骤）：

```csharp
/// <summary>切换到指定传输（debug 用：TcpTransport）。停止当前会话并重新挂事件。</summary>
public void SetTransport(IMpTransport newTransport)
{
    if (ReferenceEquals(newTransport, Transport)) return;
    if (Transport != null)
    {
        if (Transport.IsRunning) Stop();
        Transport.OnDataReceived -= HandlePacket;
        Transport.OnPeerTimeout -= HandlePeerTimeout;
        Transport.Dispose();
    }
    Transport = newTransport;
    if (Transport != null)
    {
        Transport.OnDataReceived += HandlePacket;
        Transport.OnPeerTimeout += HandlePeerTimeout;
    }
}
```

`Awake()` / `OnDestroy()` 里的事件订阅/退订代码保持原样（对接口成员操作）。其余 `Host` / `Join` / `Update` 等调用点**零改动**。

### 3.4 控制台命令（[`Mod.cs:76`](../Assets/Scripts/Mod.cs:76) `RegisterMpCommands`）

新增两条命令，其余（`HostLobbyPort` / `JoinLobbyPort` / `SteamHostLobby` / `SteamJoinLobby` / `StopLobby`）不动：

```csharp
// TCP debug：先切到 TcpTransport 再开房 / 加入（虚拟机按宿主 IP:端口 连接）
DevConsoleApi.RegisterCommand<int>("TcpHostLobby", new Action<int>(port =>
{
    MpNetworkManager mgr = EnsureMpManager();
    mgr.SetTransport(new Net.TcpTransport());
    HostLobby(port);
}));
DevConsoleApi.RegisterCommand<string, int>("TcpJoinLobby", new Action<string, int>((host, port) =>
{
    MpNetworkManager mgr = EnsureMpManager();
    mgr.SetTransport(new Net.TcpTransport());
    JoinLobby(host, port);
}));
```

### 3.5 不改的文件

`UI.cs`、`ModSettings.cs`、`LiteNetLibTransport.cs`、`MpMessage.cs`、`MpPeer.cs` —— 全都不动。

---

## 四、实施步骤（todo）

1. 新增 `Assets/Scripts/Net/IMpTransport.cs`（接口）。
2. [`TcpTransport.cs`](../Assets/Scripts/Net/TcpTransport.cs) / [`SteamTransport.cs`](../Assets/Scripts/Net/SteamTransport.cs) 各加 `: IMpTransport`（编译期校验签名齐全）。
3. [`MpNetworkManager.cs:34`](../Assets/Scripts/Net/MpNetworkManager.cs:34) 字段改 `IMpTransport` + 加 `SetTransport()` 方法。
4. [`Mod.cs`](../Assets/Scripts/Mod.cs:76) 注册 `TcpHostLobby` / `TcpJoinLobby`。
5. 回 Unity 编译无报错；本机 + 虚拟机实测 TCP。

---

## 五、本地 VM debug 测试流程

1. **本机（房主）**：控制台 `TcpHostLobby 25555`；日志应出现 `TcpTransport.Start SUCCESS`。用 `ipconfig` 看宿主对虚拟机可达的 IP（VirtualBox host-only 常为 192.168.56.1，或桥接 LAN IP）。
2. **防火墙**：放行入站 TCP 25555（或临时关防火墙）。
3. **虚拟机（客户端）**：`TcpJoinLobby 192.168.56.1 25555`。
4. **验证日志链**：`TcpTransport.StartClient SUCCESS` → 房主 `OnHello (host): ... joined as PlayerId=1` → `OnCraftData (host): broadcast PlayerJoin` → 客户端 `OnCraftXmlResponse: received craft xml` → 飞船互见。
5. **回归**：不敲 `Tcp*` 命令时仍走 Steam，`SteamJoinLobby` 正常。

---

## 六、风险与注意

| 项 | 说明 |
|---|---|
| 接口签名不齐导致编译错 | 编译期列出缺失成员；两传输类签名本就一致，预期零改动量 |
| `Transport` 被 Unity 序列化 | 保持 `[NonSerialized]`（现已是） |
| Steam 回归 | 默认实例仍是 `new SteamTransport()`，仅 debug 命令才切 TCP；回归验证 Steam 双账号 |
| TCP 地址 | 监听 `IPAddress.Any`（已实现），客户端 `Dns.GetHostAddresses` 解析 host（IP 字符串直接可用） |
| 防火墙/虚拟网卡 | 需开入站端口；客户端填对宿主网卡 IP |
