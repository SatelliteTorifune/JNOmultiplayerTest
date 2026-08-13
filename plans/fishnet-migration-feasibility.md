# FishNet 迁移可行性评估

> 项目: JNOmultiplayerTest (SimpleRockets 2 多人联机 mod aMptest)
> 评估日期: 2026-08-12
> 来源: 当前自建 TCP 传输层 (~25KB) 已能跑通 M1~M3,趁屎山未堆起评估是否切换到成熟网络框架

---

## 一、结论

**可行,且现在是成本最低的时机。** 已从反编译源码确认: JNO 模组系统**原生支持一个模组携带多个程序集 + 跨程序集依赖自动解析**(见 4.1),打包 FishNet/LiteNetLib 在机制上可行。剩余验证点已从"能不能打包"降级为"ModTools builder 怎么加 DLL / `PreprocessAssembly` 是否改动第三方 IL / 运行时兼容性"这些细节(spike 一天可确认)。

## 二、当前传输层 vs 迁移范围

### 2.1 关键认知

**这个项目 90% 的复杂度不在传输层,而在游戏集成层**(远程飞船生成/状态应用/坐标系/幽灵物理/插值),这些已经写好且实测通过,换传输层**完全不受影响**。真正会被替换的只有:

| 会被替换 | 规模 | 说明 |
|---|---|---|
| [`TcpTransport`](Assets/Scripts/Net/TcpTransport.cs) | ~14KB | 自定义 TCP 主机中继: 连接/收发线程/超时/心跳 |
| [`MpNetworkManager`](Assets/Scripts/Net/MpNetworkManager.cs) 里的消息路由 | 少量 | `HandlePacket` switch → FishNet 通道/RPC |
| 握手/房间协议 | 少量 | Hello/Welcome/PlayerJoin 改为 RPC 或保持自定义消息 |

**保留不变**: `recdata` 序列化、远程飞船生成/插值/销毁、朝向同步、body/多 craft 逻辑——这些都是传输无关的。

### 2.2 当前传输层的问题

| 问题 | 现状 | FishNet 能否解决 |
|---|---|---|
| TCP 单通道,head-of-line blocking | 500KB craft XML 传输会堵住后续状态包 → 卡顿 | ✅ 可靠/不可靠通道分离: 大 XML 走可靠,状态包走不可靠,插值平滑度质变 |
| 手写连接管理/超时/心跳 | 最近刚加 `SendTimeout`,仍偶发问题 | ✅ 成熟的连接管理,省掉手写线程和超时处理 |
| 时间同步/插值缓冲 | 下一步要做"带时间戳环形缓冲 + 100~150ms 延迟补偿",需手写 | ✅ 内置时间同步/tick 系统,正好服务这个需求 |
| 扩展性 | 当前只有客户端→房主→广播,未来加 NAT 穿透/重新连接/房间管理都需手写 | ✅ 框架自带 |

## 三、收益

1. **可靠/不可靠通道分离 —— 最直接的收益**。当前 TCP 全可靠 → 大 XML 传输造成 head-of-line blocking,把后面的状态包也堵住。FishNet 让状态包走不可靠通道、大 XML 走可靠通道,插值平滑度是质变。
2. **内置时间同步/tick 系统** —— 正好服务"带时间戳环形缓冲 + 100~150ms 延迟补偿"这个下一步重心,省去手写时钟偏移校准。
3. 成熟的连接管理/掉线检测/keep-alive,省掉现在 `TcpTransport` 那堆手写线程和超时处理。
4. 反正是现在(传输层才 ~25KB)切最便宜,等 body/多craft 堆上去再切就贵了。

## 四、风险(按严重程度排序)

### 4.1 打包/加载机制(已从反编译源码确认,风险大幅下降)

**关键结论: JNO 的模组系统原生支持"一个模组携带多个程序集 + 跨程序集依赖自动解析",这是设计好的机制,不是 hack。**

反编译依据(`ModApi.Core.dll` + `Jundroo.ModTools.Core.dll` 的 XML 文档,以及 `jnoCode` 源码):

| 机制 | 说明 | 对 FishNet 的意义 |
|---|---|---|
| `ModManifest.AssemblyPaths` | manifest 里是**程序集路径列表**(可多个),对应 `ModData._assemblies` 列表 | ✅ 模组可以带多个 DLL |
| `ModManagerBase.LoadModAssemblies(LoadedMod, ModManifest)` | 按 manifest 逐个加载所有程序集 | ✅ FishNet 各 DLL 逐个加载 |
| `ModManagerBase.AssemblyResolve(sender, ResolveEventArgs)` | 挂了 AppDomain `AssemblyResolve`,**从所有已加载的 mod 程序集里找依赖** | ✅ FishNet.dll 引用 LiteNetLib.dll 等依赖能自动解析 |
| `PreprocessAssembly(byte[], ...)` | 加载前"可能改写程序集代码"(与 mod 的 code-execution 支持相关) | ⚠️ 需 spike 确认是否动第三方 IL |
| 从 bytes 加载(`LoadedModAssembly.AssemblyBytes` + `Assembly.Load`) | 程序集以字节流运行时加载 | ✅ 说明游戏**支持运行时加载托管程序集**(Mono 或 IL2CPP+解释器)→ FishNet 这种纯托管库可行 |
| `ScanLoadedAssembly(LoadedMod, Assembly)` | 扫描程序集找 mod 类型(部件/行星/设置等) | 对第三方 DLL 应无副作用 |

**结论**: 打包 FishNet/LiteNetLib 到 JNO 模组,**机制上可行**。引入方式二选一:
- **A**: 把 FishNet 源码/UPM 包引用进 [`aMptest.asmdef`](Assets/aMptest.asmdef),全部编成一个 `aMptest.dll`(最省事,无多程序集问题);
- **B**: 把 FishNet + LiteNetLib 的编译后 DLL 加进 `ModData._assemblies`,作为独立程序集随模组分发(依赖由 `AssemblyResolve` 解析)。

**剩余需 spike 验证的点**(不再是"能不能打包",而是细节):
1. ModTools 的 Mod Builder 窗口是否支持把 UPM 包/外部 DLL 加进 `AssemblyPaths`(可能要手改 manifest);
2. `PreprocessAssembly` 是否会对第三方程序集做 IL 改写(理论上只动已知 mod 模式,第三方库应原样通过);
3. 游戏 Mono 运行时与 FishNet 的目标 Unity 版本兼容性(模组工程是 Unity 2022,应匹配);

### 4.2 重测成本

已打通且实测过的 M1~M3 要重新验证一遍(换传输后握手/超时/心跳行为都变)。

### 4.3 集成需注意

FishNet 想接管 NetworkObject/场景——这里必须刻意"只用传输+RPC、不接管 SR2 场景"(与 plan 原第五节一致),要小心别让 FishNet 的自动管理碰到游戏飞船对象。

### 4.4 对多 craft 支持帮助有限

SR2 的飞船对象不属于 FishNet 管理,多飞船/残骸同步仍是自定义逻辑,FishNet 只提供传输和 RPC 通道。

## 五、备选方案: LiteNetLib(推荐作为 Plan B)

LiteNetLib 是 FishNet 底层所用的传输库,单个 ~1MB 纯 C# DLL。相比 FishNet 的优势:

| 维度 | FishNet | LiteNetLib |
|---|---|---|
| 打包风险 | 高(多程序集,需验证 Mono/AOT 兼容) | 低(单 DLL,纯 C#,无 Unity 依赖) |
| 通道分离 | ✅ | ✅ 可靠/不可靠/有序/无序通道 |
| 连接管理 | ✅ | ✅ `NetManager` 自带连接/NAT穿透/心跳 |
| 时间同步 | ✅ 内置 tick 系统 | ❌ 需手写(但状态包已带 `FlightState.Time`) |
| RPC/对象同步 | ✅ | ❌ 需手写消息分发(但现在 `HandlePacket` 已经写了) |
| 学习成本 | 中 | 低 |

**LiteNetLib 是"甜点区"**: 拿到通道分离 + 连接管理,同时保留当前项目已写好的消息路由(`MpMessages`/`HandlePacket`),迁移量最小、风险最低。

## 六、建议路径

### 6.0 下载与导入(Unity 2022.3.62f3)

**FishNet 官方下载渠道(任选其一):**

| 渠道 | 地址 | 说明 |
|---|---|---|
| **Unity Asset Store(推荐)** | 搜 "FishNet: Networking Evolved"(FirstGearGames,免费) | 在 Unity 里 Window → Asset Store 下载,点 Import 直接进工程,最省事 |
| **GitHub Releases** | `https://github.com/FirstGearGames/FishNet/releases` | 下载 `.unitypackage`(如 `FishNet-4.x.x.unitypackage`),Unity 里 Assets → Import Package → Custom Package 导入 |
| **UPM git URL** | `https://github.com/FirstGearGames/FishNet.git` | 在 Package Manager 里 Add package from git URL;或手改 [`Packages/manifest.json`](Packages/manifest.json) 加 `"com.fishnetworking.fishnet": "https://github.com/FirstGearGames/FishNet.git"` |

**版本选择**: 工程是 **Unity 2022.3.62f3**,选 **FishNet 4.x**(支持 2021/2022/2023)。FishNet 3.x 是旧版,别下。

**导入后确认**:
- 工程里出现 `Assets/Plugins/FirstGearGames`(FishNet 源码 + 示例);
- 编译无报错(若报错多为版本/依赖问题,优先用 Asset Store 版)。

**两种引入方式(对应 4.1):**
- **方式 A(推荐先试)**: FishNet 源码已在工程里,在 [`aMptest.asmdef`](Assets/aMptest.asmdef) 的 `references` 加 `"FirstGearGames.FishNet"`(FishNet 的 asmdef 名),全部编进 `aMptest.dll`;
- **方式 B**: 若不想让 FishNet 源码进工程,单独编译 FishNet 为 DLL,加进 `ModData._assemblies`。

### Step 1: Spike —— 验证 FishNet 打包(1 天)

**当前状态(2026-08-13): FishNet 已导入工程**(`Assets/FishNet/`,主 asmdef 为 `FishNet.Runtime`)。接下来:

1. 创建新 branch (`feature/fishnet-spike`)
2. **方式 A: 在 [`aMptest.asmdef`](Assets/aMptest.asmdef) 引用 FishNet**:
   ```json
   {
     "name": "aMptest",
     "references": [
       "UnityEngine.UI",
       "Unity.TextMeshPro",
       "Unity.Mathematics",
       "FishNet.Runtime"
     ]
   }
   ```
   (FishNet.Runtime 的 `autoReferenced: true`,理论上不写引用也能用,但显式引用更稳。)
3. **写最小 demo 验证连接**: 新建 `Assets/Scripts/Net/FishNetSpike.cs`(临时,spike 完删):
   - 一个 `MonoBehaviour`,在 `Start()` 里 `NetworkManager` 起 server(监听 25555)+ 本地 client 连自己;
   - 确认 `OnServerConnectionState` / `OnClientConnectionState` 回调触发、能连上;
   - 用 `NetworkManager` 的 `ServerManager.StartConnection()` / `ClientManager.StartConnection()`。
4. **用 Mod Builder 打包成 `.mod` 文件,在游戏里加载**,确认:
   - 能加载不崩溃(游戏 Mono 运行时);
   - `PreprocessAssembly` 对 FishNet 程序集不做破坏性 IL 改写;
   - 运行时无程序集冲突 / AOT 报错;
   - 若方式 A 有问题,再试**方式 B**: FishNet + LiteNetLib 独立 DLL 加进 `ModData._assemblies`,验证 `AssemblyResolve` 能解析依赖。
5. **结果判定**:
   - 能加载 → 切 FishNet,进 Step 2
   - 卡在打包/加载 → 退到 Step 3(LiteNetLib,单 DLL 更稳)

### Step 2: FishNet 完全迁移

1. 替换 `TcpTransport` → FishNet 的 `TransportManager`(LiteNetLib transport)
2. 消息路由改为 FishNet 通道 + RPC(状态包走不可靠通道,大 XML 走可靠通道)
3. 利用 FishNet tick 系统做时间戳对齐
4. 保留 `MpNetworkManager` 的游戏同步逻辑不变
5. 重新验证 M1~M3 所有功能

### Step 3(Plan B): LiteNetLib 迁移

1. 引入 LiteNetLib 单 DLL(从 NuGet 或 FishNet 源码里提取)
2. 替换 `TcpTransport` → `NetManager` + `NetPacketProcessor`(自定义消息分发)
3. 游戏同步逻辑完全保留,`MpMessage` 序列化不变
4. 重新验证 M1~M3

## 七、与下一步工作的关系

无论选 FishNet 还是 LiteNetLib,也不影响当前 master 分支继续推进:

- **body 同步/多 craft 支持** 是传输无关的,可以在 master 上并行开发;
- **平滑插帧** 的最佳形态(通道分离 + 时间戳缓冲)会因新传输层而受益,但即使不换也能做。

建议: 先在 master 上继续推进 body 同步和多 craft 映射,同时在新 branch 上做 FishNet/LiteNetLib spike。两个方向并行不冲突,spike 通过后再决定是否合并。