# JNO 联机 Mod 项目 —— 会话启动上下文(通用提示词)

> 用法:每次开新会话做本项目前,把本文档(或下面"0. 一句话定位 + 1. 关键路径"起的内容)作为首条上下文交给 AI,可省去大量重复调研。
> 本文件是**只读参考**,不是 plan;方案/决策类内容一律写进 [`multi-craft-sync-2026-08-16.md`](multi-craft-sync-2026-08-16.md) 并同步 [`README.md`](README.md)。

---

## 0. 一句话定位

给 **SimpleRockets 2 / JNO**(Steam AppID **870200**)写**联机 mod `aMptest`**(Unity **2022.3.62f3**,C#/.NET 4.x)。思路:反编译游戏源码找内部 API + 参考 KSP 的 LunaMultiplayer;已实现"幽灵船快照同步"单船原型,当前推进**多 craft 同步**(方案研究阶段)。

## 1. 关键路径

| 用途 | 路径 |
|---|---|
| 工程目录 | `C:\renko\unityProjects\JNOmultiplayerTest` |
| Mod 源码 | `Assets/Scripts/`(命名空间 `Assets.Scripts.*`) |
| **反编译游戏源码** | `C:\renko\shitProgram\jnoCode\SimpleRockets2\Assets\Scripts\`(即 `SimpleRockets2.sln`,只读参考) |
| **ModApi 源码** | `C:\renko\shitProgram\jnoCode\ModApi\`(即 `ModApi.sln`,只读参考) |
| KSP 联机参考 | `C:\renko\unityProjects\LunaMultiplayer` |
| 设计文档索引 | `plans/README.md` |
| 当前活跃 plan | `plans/multi-craft-sync-2026-08-16.md` |
| 参考程序集(编译期) | `Assets/ModTools/Assemblies/`(含 `SimpleRockets2.dll`、`ModApi.dll`、`Jundroo.ModTools.dll`、`com.rlabrecque.steamworks.net.dll`、`0Harmony.dll` 等) |
| 游戏本体(本地) | `C:\Program Files (x86)\Steam\steamapps\common\SimpleRockets 2\SimpleRockets2.exe` |

要点:
- 游戏内部命名空间是 `Assets.Scripts.*`(如 `Assets.Scripts.Flight.Sim.CraftNode`)——mod 直接 `using` 内部 API,因此**依赖反编译源码导航,游戏更新可能破坏**,需固定版本。
- ModApi 命名空间 `ModApi.*`(public API);ModTools 运行时 API 是 `Jundroo.ModTools`(`Jundroo.ModTools.dll`)。
- `Assets/ModTools/Assemblies/*.dll` 是 precompiled DLL,自动被 [`aMptest.asmdef`](../Assets/aMptest.asmdef) 引用(asmdef 只显式列了 `UnityEngine.UI / Unity.TextMeshPro / Unity.Mathematics / FishNet.Runtime`;Steamworks/Harmony/游戏程序集都不必显式列)。

## 2. 架构与关键文件(Assets/Scripts/)

| 文件 | 职责 |
|---|---|
| `Mod.cs` | 入口:`new Harmony("MPTest").PatchAll()`;`recdata` 状态结构;控制台命令;ModSettings |
| `LobbyManager.cs` | 房间生命周期(Host/Join/Stop,`DontDestroyOnLoad`,`OnSceneLoaded`→`OnFlightSceneLoaded`) |
| `Net/MpNetworkManager.cs` | **核心**:状态收发、幽灵船生成/移除、插值、房间 |
| `Net/MpMessage.cs` | 消息编码 `MpMessageType`(Hello=1..TickRate=14)+ GZip XML 按需下载 |
| `Net/IMpTransport.cs` | 传输层薄接口(Start/StartClient/DrainIncoming/SendTo/Broadcast…) |
| `Net/SteamTransport.cs` / `TcpTransport.cs` / `LiteNetLibTransport.cs` | 传输实现(Steam 默认、TCP debug、LiteNetLib 备用) |
| `Net/MpPeer.cs` | 对端(含 `SteamId` `ulong` 字段) |
| `CraftUtils.cs` | 状态应用(`RecalculateFrameState`)+ 物理禁用 |
| `MultiPlayerUI.cs` | 联机 UI(Host/Join 按钮 + IP/Port 输入) |
| `HarmonyPatches/` | 现有 Harmony patch(**新 patch 放这里**;已有 `LayoutRebuildPatch.cs`) |
| `Net/SteamSpike.cs` / `FishNetSpike.cs` | spike 验证脚本(结论已用,留作参考) |

## 3. 已确定的技术事实(不要再重复调研)

- **传输**:Steam P2P 默认(`SteamNetworkingSockets`;游戏启动已 `SteamAPI.Init()`,mod **不重复 Init**);TCP 仅 VM debug(`TcpHostLobby`/`TcpJoinLobby`);LiteNetLib 备用未启用。
- **FishNet 高层 API 被 codegen 否决**(运行时加载 mod DLL 无序列化器)→ 传输层自建、高层逻辑自持。
- **`NodeId` 每机自增、split 时重分配,跨机不唯一** → 多 craft 必须 mod 自生成 `Guid`(local `Dictionary<int,Guid>` nodeId→Guid,状态包带 `(ownerId, craftGuid)`)。
- **朝向同步 = `recdata.SrfRel`(相对地表朝向)**:解决①游戏每帧用 pod 座椅朝向覆盖根朝向(`CraftScript.cs:2046`)、②跨机行星自转角差。
- **幽灵模式**:`AllowPlayerControl=false` + 物理禁用(`SetPhysicsEnabled(false, Warp)` 避免 MapView NRE)+ colliders off + `PreventDebris=true`。
- **插值**:每 `RemoteCraft` 环形缓冲 32 采样 + `RenderDelayMs`(~100ms);`LateUpdate`(DefaultExecutionOrder 1000)重写朝向抗游戏覆盖。
- **状态包(recdata)**:Position/Velocity/Heading(行星空间)+ SrfRel + Pitch/Yaw/Roll/Throttle/Brake/Sliders + 激活组 + Stage + `BodyRotations`(每 body 相对质心欧拉)。**无燃料/部件状态**(已知限制)。
- **约定约束**:所有玩家**同一行星系统**(房主指定),暂不做跨行星/生涯;MVP 锁定 **1x 实时**(无 warp 同步);不做燃料/资源/Vizzy 同步。

## 4. 游戏 API 关键入口(反编译确认)

- `FlightSceneScript.Instance.CraftNode`(本机玩家节点)/ `CraftNodes` / `SpawnCraft` / `ChangePlayersActiveCommandPodImmediate` / `SwitchToNextCommandPod` / `FlightEnd`
- `FlightState.CraftNodes`(IReadOnlyList<CraftNode>)/ `CraftNodeAdded` / `CraftNodeRemoved` / `AddCraft` / `LoadCraftXml` / `PlayerNodeId`
- `CraftNode`: `NodeId` / `AllowPlayerControl` / `HasCommandPod` / `IsDebris` / `InContactWithPlanet` / `DestroyCraft` / `TransitionToNewSoi`
- `CraftSplitter`: `SplitCraftNode` / `MergeCraftNode` / `ProcessDisconnectedBody` / `DetermineCraftNodeEligibility`
- `EvaScript` / `CommandPodScript.IsEva` / `CrewCompartmentScript`(Drood EVA)
- 详见 `multi-craft-sync-2026-08-16.md` §七(切换/对接/EVA 研究)、§八(边界排查,含 Harmony 拦 `ChangePlayersActiveCommandPodImmediate` 防劫持、无 pod 残骸处理)。

## 5. 开发流程约定

1. **研究有明确结论 → 直接写进 `plans/multi-craft-sync-2026-08-16.md`**(加「【决策:…】」标记),并同步 `README.md` 的决策速查表。
2. 完成主题 → 移入 `plans/archive/`(已修订为最终状态 + 经验教训),并更新索引。
3. 改代码前先 `read` 目标文件;新 Harmony patch 放 `Assets/Scripts/HarmonyPatches/`。
4. 传输层改动需**双路径回归**:默认 Steam + TCP debug 命令。
5. 回复中给出改动的文件(带完整路径),方便点击。

## 6. 调试/验证

- 日志:`Mod.LogLobby`(联机流程日志)。
- 游戏内 DevConsole 命令:`HostLobbyPort <port>` / `JoinLobbyPort <ip> <port>` / `StopLobby` / `SteamHostLobby` / `SteamJoinLobby <hostSteamId>` / `TcpHostLobby <port>` / `TcpJoinLobby <ip> <port>` / `SetTickRate <hz>`。
- 本地 VM debug:本机 `TcpHostLobby 25555`(防火墙放行入站);VM `TcpJoinLobby <宿主IP> 25555`——**✅ 已实测可行(2026-08)**。
- Steam 双账号公网联机:**✅ 已实测可行(2026-08)**,零 frp/零端口转发(见 [`archive/steam-integration-2026-08-13.md`](archive/steam-integration-2026-08-13.md) Step 4)。Lobby 邀请不做,维持手动 SteamId。
- 反编译源码用 Rider/VS 打开 `.sln` 浏览;`jnoCode` 是只读参考,不要改动。

(End of file - total 81 lines)