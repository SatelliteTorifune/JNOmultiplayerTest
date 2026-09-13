# JNO 联机 Mod 项目 —— 会话启动上下文(通用提示词)

> 用法:每次开新会话做本项目前,把本文档(或下面"0. 一句话定位 + 1. 关键路径"起的内容)作为首条上下文交给 AI,可省去大量重复调研。
> 本文件是**只读参考**,不是 plan;方案/决策类内容一律写进对应主题 plan(索引见 [`README.md`](README.md))并同步更新索引与决策速查表。

---

## 0. 一句话定位

给 **SimpleRockets 2 / JNO**(Steam AppID **870200**)写**联机 mod `MultiPlayer`**(Unity **2022.3.62f3**,C#/.NET 4.x,C# 命名空间 `Assets.Scripts.*`)。思路:反编译游戏源码找内部 API + 参考 KSP 的 LunaMultiplayer。

**当前进度**:单船"幽灵船"联机原型**已跑通并通过 Steam 双账号公网实测**;已实现 body 级位姿同步、部件开关/控制输入同步、Vizzy 联机隔离、高延迟平滑(SP2 式连续外推)、延迟模拟调试工具、更新检查(ModUpdater)、**Steam 房间列表(大厅浏览器,2026-09-12 落地)**。**多 craft 同步仍是"方案研究阶段"(未实现)**。

## 1. 关键路径

| 用途 | 路径 |
|---|---|
| 工程目录 | `C:\renko\unityProjects\JNOMultiPlayer` |
| Mod 源码 | `Assets/Scripts/`(命名空间 `Assets.Scripts.*`) |
| **反编译游戏源码** | `C:\renko\shitProgram\jnoCode\SimpleRockets2\Assets\Scripts\`(即 `SimpleRockets2.sln`,只读参考) |
| **ModApi 源码** | `C:\renko\shitProgram\jnoCode\ModApi\`(即 `ModApi.sln`,只读参考) |
| SP2 联机参考(可抄的平滑/序列化实现) | `C:\renko\shitProgram\反编译的\sp2\Game\Assets\Scripts\` |
| KSP 联机参考 | `C:\renko\unityProjects\LunaMultiplayer` |
| 设计文档索引 | `plans/README.md` |
| 活跃 plan | `multi-craft-sync-2026-08-16.md`、`body-sync-2026-08-18.md`、`part-switch-sync-2026-08-18.md`、`latency-smoothing-2026-08-22.md`、`vizzy-isolation-2026-08-22.md` |
| 游戏内 UI 文案 | `Assets/Content/Languages/EN-US.xml` / `ZH-CN.xml`(key 前缀 `MultiPlayer.*`) |
| 游戏内 UI 资源库 | `Assets/Content/XML UI/UIResourceDatabase.asset`(`PathPrefix: aMptest/` ← **与代码引用不一致,待修**) |
| 参考程序集(编译期) | `Assets/ModTools/Assemblies/`(含 `SimpleRockets2.dll`、`ModApi.dll`、`Jundroo.ModTools.dll`、`com.rlabrecque.steamworks.net.dll`、`0Harmony.dll` 等) |
| Mod 元数据 / 版本 | `Assets/ModData.asset`(`_name: MultiPlayer`、`_versionMajor/_versionMinor`)+ 仓库根 `version.txt`(**发布用的 mod 版本**,ModUpdater 读它比对) |
| 游戏本体(本地) | `C:\Program Files (x86)\Steam\steamapps\common\SimpleRockets2\`(**注意目录名没有空格**;Steam 里的显示名是 "Juno: New Origins") |
| 游戏运行日志 | `C:\Users\usami\AppData\LocalLow\Jundroo\SimpleRockets 2\Player.log` |

要点:
- 游戏内部命名空间是 `Assets.Scripts.*`(如 `Assets.Scripts.Flight.Sim.CraftNode`)——mod 直接 `using` 内部 API,因此**依赖反编译源码导航,游戏更新可能破坏**。
- ModApi 命名空间 `ModApi.*`(public API);ModTools 运行时 API 是 `Jundroo.ModTools`(`Jundroo.ModTools.dll`)。
- `Assets/ModTools/Assemblies/*.dll` 是 precompiled DLL,自动被 [`MultiPlayer.asmdef`](../Assets/MultiPlayer.asmdef) 引用(asmdef 只显式列了 `UnityEngine.UI / Unity.TextMeshPro / Unity.Mathematics / FishNet.Runtime`;Steamworks/Harmony/游戏程序集都不必显式列)。
- **命名遗留**:工程原名 `aMptest`,已改名 **MultiPlayer**(asmdef → `MultiPlayer.asmdef`、输出 `MultiPlayer.dll`、csproj → `MultiPlayer.csproj`、日志前缀 `[MultiPlayer]`)。文档/代码里仍可能残留 `aMptest`,见到即视为旧名。**注意 `version.txt` 是 mod 版本,与"游戏版本"无关,不要混用。**
- MSBuild 编译用仓库根的 `MultiPlayer.csproj`(`dotnet build MultiPlayer.csproj -c Debug`),它由 Unity 生成、列显式 `<Compile Include>`;新增 `.cs` 后 Unity 刷新会补条目。

## 2. 架构与关键文件(Assets/Scripts/)

| 文件 | 职责 |
|---|---|
| `Mod.cs` | 入口:`Harmony("MPTest").PatchAll()` + `JetEngineGhostPatch.Apply`;`RemoteDataPack`(状态包结构体);DevConsole 命令注册;UI 对象创建;`ModVersion` + `ModUpdater` 启动 |
| `LobbyManager.cs` | 房间生命周期(Host/Join/Stop、`DontDestroyOnLoad`、`SceneLoaded`→`OnFlightSceneLoaded`、创建/持有 `MpNetworkManager`) |
| `Net/MpNetworkManager.cs` | **核心**(~2300 行):状态收发、幽灵船生成/移除、**连续外推 + 平滑**、房间转发、诊断日志 |
| `Net/MpMessage.cs` | 二进制消息编码 `MpMessageType`(Hello=1 … **Kick=15**)+ GZip XML 按需下载(`CraftXmlRequest/Response`) |
| `Net/IMpTransport.cs` | 传输层薄接口(Start/StartClient/DrainIncoming/SendTo/Broadcast…) |
| `Net/SteamTransport.cs` / `TcpTransport.cs` / `LiteNetLibTransport.cs` | 传输实现(Steam 默认、TCP debug、LiteNetLib 备用) |
| `Net/SteamLobbyBrowser.cs` | **Steam 大厅浏览器(房间列表)**:开房(CreateLobby+SetLobbyData→复用 HostLobby)/列表(RequestLobbyList 版本过滤)/加入(LobbyEnter→GetLobbyOwner→复用 SteamTransport)/邀请(overlay+GameLobbyJoinRequested);回调引用持有防 GC |
| `Net/LagSimTransport.cs` | **延迟模拟装饰器**(NetSim:延迟/抖动/丢包/重复,包 TCP,无需 Steam 好友) |
| `Net/MpPeer.cs` | 对端(含 `SteamId` `ulong`、`NodeId`、`PingMs`、`CraftXml`) |
| `Net/MpCraftPreloader.cs` | 幽灵船 prefab 异步预热(消除加入白屏 + 真实加载百分比) |
| `Net/EngineVisualSync.cs` | 引擎尾焰/烟雾/过膨胀 + `InjectGhostMotion` 速度注入(kinematic 幽灵视觉) |
| `Net/PartVisualSync.cs` | 部件开关白名单应用(`PartActivated` → `Activate()/Deactivate()`) |
| `Net/ControlVisualSync.cs` | 幽灵 `CraftControls` 控制输入 + 激活组应用(P3) |
| `ModUpdater.cs` | 更新检查(GitHub Releases API + `version.txt` 兜底、看门狗超时、三按钮弹窗) |
| `ModUtils.cs` | 日志封装(`Log`/`LogError`/`LogLobby`/`LogUpdate`,前缀 `[MultiPlayer]`,受 `DebugMode` 控制) |
| `ModSettings.cs` | Mod 设置项 |
| `CraftUtils.cs` | 状态应用(`RecalculateFrameState`)+ 幽灵物理禁用(`DisableCraftPhysicCalculation`) |
| `MultiPlayerUI.cs` | 联机 UI(房间/玩家列表、踢人、TickRate、NetSim 分组、TCP debug 分组、加载进度) |
| `HarmonyPatches/` | Harmony patch(**新 patch 放这里**):`JetEngineGhostPatch.cs`、`LayoutRebuildPatch.cs`、`VizzyIsolationPatch.cs` |
| `Net/SteamSpike.cs` / `FishNetSpike.cs` | spike 验证脚本(结论已用,留作参考) |

## 3. 已确定的技术事实(不要再重复调研)

**传输 / 房间**
- **传输**:Steam P2P 默认(`SteamNetworkingSockets`;游戏启动已 `SteamAPI.Init()`,mod **不重复 Init**);TCP 仅 VM/公网 debug(`TcpHostLobby`/`TcpJoinLobby`);LiteNetLib 备用未启用。
- **房主 = 中继**:客户端之间的状态包经房主转发(`IsServer` 时 `Transport.Broadcast`)。
- **FishNet 高层 API 被 codegen 否决**(运行时加载 mod DLL 无序列化器)→ 传输层自建、高层逻辑自持。
- **加入方式**:Steam 房间列表(大厅浏览器)——"开房可见、点列表加入";`SteamLobbyBrowser`(SteamMatchmaking 直调)实现开房/列表(版本过滤)/加入(`GetLobbyOwner`→复用 `SteamTransport`)/好友邀请;**手动输入房主 SteamId 仍保留于控制台** `SteamJoinLobby <hostSteamId>`(见 `steam-lobby-2026-09-12.md`,已落地)。

**幽灵船(remote craft)**
- **幽灵模式**:`AllowPlayerControl=false` + 物理禁用 `SetPhysicsEnabled(false, PhysicsChangeReason.Warp)`(**必须用 Warp**:`UnloadPhysics` 会让 MapCraft 被销毁却留在注册表里 → MapView NRE)+ `CraftUtils.DisableCraftPhysicCalculation`(colliders off、`PreventDebris=true`、`IncludeInDrag=false`、`Damage`/`HeatShield` 拉满)+ 所有 body `RigidBody.isKinematic = true`。
- `InContactWithPlanet` **每帧重申为 true**,并同步 `GroundedSurface*`(private set,反射写),避免游戏把它拉回轨道/坠落。
- 幽灵 modifier(引擎等)**仍会收到 `IFlightUpdate`/`IFlightFixedUpdate`** —— 这是 `MpNetworkManager.IsRemoteCraftNode` 存在的原因(JetEngineGhostPatch / Vizzy 隔离都靠它)。
- 可见性**每帧强制恢复**(`EnforceRemoteCraftVisuals`,所有子 Renderer `enabled = true`)。

**状态包(recdata,`Mod.cs` 的 `RemoteDataPack`)**
- Position/Velocity/Heading(行星空间)+ `SrfRel`(相对地表朝向)+ Pitch/Yaw/Roll/Throttle/Brake/Sliders/Translate + `ActivationGroupStates` + `Stage` + `BodyRotations`(每 body 相对 comRot 欧拉)+ `BodyPositions`(每 body 相对 comRot 位置)+ `EngineThrottles` + `PartActivated`。
- **无燃料/资源数值、无部件损伤**(已知限制);**无 craft id / 无多船数组**(多 craft 未做)。
- 消息类型(`MpMessageType`):Hello=1、Welcome=2、PlayerJoin=3、PlayerLeave=4、State=5、Pause=6、CraftData=7、Ping=8、Pong=9、CraftDataAck=10、PlayerJoinAck=11、CraftXmlRequest=12、CraftXmlResponse=13、TickRate=14、Kick=15。

**朝向 / 速度坐标系(容易踩坑)**
- **朝向同步 = `recdata.SrfRel`(相对地表朝向)**:解决①游戏每帧用 pod 座椅朝向覆盖根朝向、②跨机行星自转角差。`LateUpdate`(`[DefaultExecutionOrder(1000)]`)重写朝向抗游戏覆盖。
- **速度必须是"地表相对速度"**:`PlanetVectorToSurfaceVector` 是**纯旋转,不减行星自转项**。发送端 = `PlanetVectorToSurfaceVector(craft.Velocity) − CalculateSurfaceVelocity(pos)`;接收端 = `SurfaceVectorToPlanetVector(data.Velocity) + SurfaceVectorToPlanetVector(CalculateSurfaceVelocity(data.Position))`。漏掉自转项会让静止船上报 ≈**158.85 m/s**,被外推放大成数十米瞬移。

**接收端平滑(现行实现,见 `latency-smoothing-2026-08-22.md` §9)**
- **不做插值缓冲**:始终取最新包(`TryGetNewest`)+ **连续外推(dead-reckoning)**:`ext = 单向延迟(RTT/2) + 包龄`,封顶 1.0s;包龄 `> max(3×gapEMA, 0.25s)` 时冻结为 `ext = 单向延迟`(防幽灵飞走)。
- **平滑层** `ApplyRemoteSmoothing`:`k = Lerp(0.1, 1, min(1, |v|×0.02))`、`alpha = 1 − (1−k)^(dt×50)`、静止锁定 `|v|<0.5 && 误差<0.05m`、瞬移阈值 `>100m`、旋转 `2.5·dt`、每 body `10·dt` + `0.01` 快照。
- **死代码警告**:`TryGetInterpolatedState` / `RenderDelayMs` / `UnderrunFrames` / `SnapFrames` / `ClearBuffer` / `ReuseInterpBody*` 仍存在但**已不参与渲染**(`snap=`/`interpPct=`/`posErr=` 因此是结构性常量)。看到这些标识符不要以为插值缓冲还在跑。

**约定约束**
- 所有玩家**同一行星系统**(房主指定),暂不做跨行星/生涯;MVP 锁定 **1x 实时**(无 warp 同步);不做燃料/资源/Vizzy 同步。
- `NodeId` 每机自增、split 时重分配,**跨机不唯一** → 多 craft 必须 mod 自生成 `Guid`(local `Dictionary<int,Guid>` nodeId→Guid,状态包带 `(ownerId, craftGuid)`);当前代码里**还没有任何 Guid**。

## 4. 游戏 API 关键入口(反编译确认)

- `FlightSceneScript.Instance.CraftNode`(本机玩家节点)/ `CraftNodes` / `SpawnCraft` / `ChangePlayersActiveCommandPodImmediate` / `SwitchToNextCommandPod` / `FlightEnd`
- `FlightState.CraftNodes`(IReadOnlyList<CraftNode>)/ `CraftNodeAdded` / `CraftNodeRemoved` / `AddCraft` / `LoadCraftXml` / `PlayerNodeId`
- `CraftNode`: `NodeId` / `AllowPlayerControl` / `HasCommandPod` / `IsDebris` / `InContactWithPlanet` / `DestroyCraft` / `TransitionToNewSoi` / `SetPhysicsEnabled(reason)` / `GroundedSurfacePosition|Velocity|Rotation`(private set)
- `CraftSplitter`: `SplitCraftNode` / `MergeCraftNode` / `ProcessDisconnectedBody` / `DetermineCraftNodeEligibility`
- `CommandPodScript`(激活组 1-indexed 1..10、`SetActivationGroupState`)/ `PartScript.Activate()/Deactivate()` / `CraftControls`(public 可写)
- `EvaScript` / `CommandPodScript.IsEva` / `CrewCompartmentScript`(Drood EVA)
- 详见 `multi-craft-sync-2026-08-16.md` §七(切换/对接/EVA 研究)、§八(边界排查,含 Harmony 拦 `ChangePlayersActiveCommandPodImmediate` 防劫持、无 pod 残骸处理)。

## 5. 开发流程约定

1. **研究有明确结论 → 直接写进对应主题 plan**(加「【决策:…】」标记),并同步 `README.md` 的决策速查表。
2. 完成主题 → 移入 `plans/archive/`(修订为最终状态 + 经验教训),并更新索引。
3. 改代码前先 `read` 目标文件;新 Harmony patch 放 `Assets/Scripts/HarmonyPatches/`。
4. 传输层改动需**双路径回归**:默认 Steam + TCP/NetSim debug 命令。
5. 新增游戏内可见文案 → 同步改 `Assets/Content/Languages/EN-US.xml` 与 `ZH-CN.xml`(key 必须同前缀)。
6. 回复中给出改动的文件(带完整路径),方便点击。

## 6. 调试/验证

- **日志**:`Mod.LogLobby`(联机流程)、`Mod.LogUpdate`(更新检查,不受 `DebugMode` 限制)、`Mod.Log`(通用)。接收端平滑诊断:`MP smoothing P<id>: ...` 每 3 秒一条,只进 `Player.log`(无悬浮窗)。
- **DevConsole 命令**:
  - 房间:`HostLobbyPort <port>` / `JoinLobbyPort <ip> <port>` / `StopLobby` / `SteamHostLobby <port>` / `SteamJoinLobby <hostSteamId>` / `TcpHostLobby <port>` / `TcpJoinLobby <ip> <port>` / `SetTickRate <hz>`(1~120,房主设置后广播)。
  - Steam 房间列表(大厅):`SteamLobbyList`(Regional 距离过滤)/ `SteamLobbyListWorld`(WorldWide)/ `SteamLobbyCreate <房间名>` / `SteamLobbyJoin <lobbyId>` / `SteamLobbyLeave`。
  - 延迟模拟(NetSim,需 TCP):`NetSimDelay <ms>` / `NetSimJitter <ms>` / `NetSimLoss <pct>` / `NetSimDuplicate <pct>` / `NetSimOn` / `NetSimOff` / `NetSimReset` / `NetSim`(查看配置与投递统计)。**数值与总开关分离**,会话中改值实时生效。
  - spike(历史):`FishNetSpike` / `SteamSpike`。
- **本地 VM debug**:本机 `TcpHostLobby 25555`(防火墙放行入站);VM `TcpJoinLobby <宿主IP> 25555`——**✅ 已实测可行**。
- **Steam 双账号公网联机**:**✅ 已实测可行**,零 frp/零端口转发(见 [`archive/steam-integration-2026-08-13.md`](archive/steam-integration-2026-08-13.md) Step 4)。
- 反编译源码用 Rider/VS 打开 `.sln` 浏览;`jnoCode` / `反编译的` 都是**只读参考**,不要改动。
- **文本编码约定**:`.md` / `.cs` 一律 **UTF-8 无 BOM、LF**。历史上 `plans/` 曾被一次有损转码毁掉约 10~16% 汉字(提交 `16eb58d 狗屎`),已从父提交 `7d4925c` 恢复——**改文档时不要用会把非 UTF-8 字节替换成 `U+FFFD` 的工具**(尤其 PowerShell 5.1 的 `Set-Content -Encoding UTF8` 与 `-replace`,前者加 BOM、后者在处理含 `[`/反引号的 Markdown 链接时会吃字符)。
