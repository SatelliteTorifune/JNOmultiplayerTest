# JNOMultiPlayer —— 会话上下文 + 设计文档索引(plans/README.md)

> 项目:JNOMultiPlayer(SimpleRockets 2 / JNO 联机 mod `MultiPlayer`;Steam AppID **870200**;Unity **2022.3.62f3**;C# 命名空间 `Assets.Scripts.*`)。思路:**反编译游戏源码导航内部 API** + 参考 KSP LunaMultiplayer 与 SP2(SimplePlanes 2)的联机实现。
> **当前进度**:单船"幽灵船"原型已通过 **Steam 双账号公网实测**;§六 现有 **1 个活跃 plan**(2 阶外推期 1 已落地)+ **6 个待拍板**(含 1 份参考资料)+ **14 个已归档**;1.4.2 适配 / 部件开关回归 / 高延迟平滑 / Vizzy 隔离均已结案。
> 用法:新会话第一条上下文直接投喂本文档(§一~§五 即提示词核心)。本文档是**原 `AGENT_CONTEXT.md`(会话上下文)+ 原 `README.md`(索引/决策/规则)的合并版**,只读参考;**方案 / 决策类内容一律写进对应主题 plan**,再同步本文档索引与决策速查。
> **职责边界(重要)**:mod 的**打包 / 部署 / DLL 更新 / 发布链条**(装进游戏的 DLL、AssetBundle、版本号、GitHub Releases)**全部由用户负责**——agent 不执行、不代劳、不为此改版本号或构建产物;agent 只负责**源码改动 + 文档同步 + `dotnet build MultiPlayer.csproj -c Debug` 验证(0 错误 0 警告)**。
> 调试日志:`<USERPROFILE>\AppData\LocalLow\Jundroo\SimpleRockets 2\Player.log`(Unity 运行时日志;`Mod.LogLobby` / `MP smoothing` 等输出在这里)。

---

## 一、关键路径

| 用途 | 路径 |
|---|---|
| 工程目录 | `<PROJECT>` |
| Mod 源码 | `Assets/Scripts/`(命名空间 `Assets.Scripts.*`) |
| **反编译游戏源码(SR2)** | `<JNO_CODE>\SimpleRockets2\Assets\Scripts\`(即 `SimpleRockets2.sln`,只读参考) |
| **ModApi 源码(官方公共 API)** | `<MOD_API>\`(即 `ModApi.sln`,只读;`ICraftFlightData`/`ICraftScript` 等接口在此,反编译游戏源码里搜不到接口定义) |
| SP2 联机参考(可抄的平滑/序列化实现) | `<SP2_MP>\`(联机核心在 `Multiplayer\` 子目录) |
| KSP 联机参考 | `<LUNA_MP>` |
| 游戏本体(本地) | `<SR2_GAME>\`(目录名**没有空格**;Steam 显示名 "Juno: New Origins") |
| 参考程序集(编译期) | `Assets/ModTools/Assemblies/`(`SimpleRockets2.dll`、`ModApi.dll`、`Jundroo.ModTools.dll`、`com.rlabrecque.steamworks.net.dll`、`0Harmony.dll` 等) |
| 游戏内 UI 文案 | `Assets/Content/Languages/EN-US.xml` / `ZH-CN.xml`(key 前缀 `MultiPlayer.*`) |
| 游戏内 UI 资源库 | `Assets/Content/XML UI/UIResourceDatabase.asset`(`PathPrefix: MultiPlayer/`,条目 `MultiPlayer/Sprites/UIIcon`;运行时按条目路径**逐字匹配**,不自动拼前缀,详见 §八 #2) |
| Mod 元数据 / 版本 | `Assets/ModData.asset`(`_name: MultiPlayer`、`_versionMajor/_versionMinor`)+ 仓库根 `version.txt`(**发布用 mod 版本**,ModUpdater 读它比对) |
| **本机路径映射(真实值,仅本地)** | `plans/LOCAL_PATHS.md`(已被 `.gitignore` 排除,**严禁上传**;本文档的 `<TOKEN>` 按它解析) |

要点:

- 游戏内部命名空间是 `Assets.Scripts.*`(如 `Assets.Scripts.Flight.Sim.CraftNode`)——mod 直接 `using` 内部 API,故**依赖反编译源码导航,游戏更新可能破坏**。
- **`<JNO_CODE>` 根结构**:`SimpleRockets2/`(游戏内部 API)+ `ModApi/`(官方公共 API,编译为 `ModApi.dll`)+ `sr2_curves/`;两个 `.sln` 都在根。同库还有 `KSP` / `Fenxi` / `Utils` 目录,全部**只读参考,不要改动**。
- ModTools 运行时 API 是 `Jundroo.ModTools`(`Jundroo.ModTools.dll`);`Assets/ModTools/Assemblies/*.dll` 是 precompiled DLL,自动被 [`MultiPlayer.asmdef`](../Assets/MultiPlayer.asmdef) 引用(asmdef 只显式列 `UnityEngine.UI / Unity.TextMeshPro / Unity.Mathematics / FishNet.Runtime`,Steamworks/Harmony/游戏程序集不必显式列)。
- **命名遗留**:工程原名 `aMptest`,已改名 **MultiPlayer**(asmdef / 输出 DLL / csproj / 日志前缀 `[MultiPlayer]`);见到残留 `aMptest` 视为旧名。**`version.txt` 是 mod 版本,与"游戏版本"无关,不要混用。**
- 编译验证用仓库根 `MultiPlayer.csproj`(`dotnet build MultiPlayer.csproj -c Debug`),由 Unity 生成、列显式 `<Compile Include>`;新增 `.cs` 后 Unity 刷新会补条目。**编译仅用于验证;打包 / 部署由用户负责。**

## 二、架构与关键文件(Assets/Scripts/)

| 文件 | 职责 |
|---|---|
| `Mod.cs` | 入口:`Harmony("MPTest").PatchAll()` + `JetEngineGhostPatch.Apply`;`RemoteDataPack`(状态包结构体);DevConsole 命令注册;UI 对象创建;`ModVersion` + `ModUpdater` 启动 |
| `LobbyManager.cs` | 房间生命周期(Host/Join/Stop、`DontDestroyOnLoad`、`SceneLoaded`→`OnFlightSceneLoaded`、创建/持有 `MpNetworkManager`) |
| `Net/MpNetworkManager.cs` | **核心(~3100 行)**:状态收发、幽灵船生成/移除、**连续外推 + 平滑**、房间转发、诊断日志 |
| `Net/MpMessage.cs` | 二进制消息编码 `MpMessageType`(Hello=1 … **Kick=15**)+ GZip XML 按需下载(`CraftXmlRequest/Response`) |
| `Net/IMpTransport.cs` | 传输层薄接口(Start/StartClient/DrainIncoming/SendTo/Broadcast…) |
| `Net/SteamTransport.cs` / `TcpTransport.cs` / `LiteNetLibTransport.cs` | 传输实现(Steam 默认、TCP debug、LiteNetLib 备用未启用) |
| `Net/SteamLobbyBrowser.cs` | **Steam 大厅浏览器(房间列表)**:开房(CreateLobby+SetLobbyData→复用 HostLobby)/列表(RequestLobbyList 版本过滤)/加入(LobbyEnter→GetLobbyOwner→复用 `SteamTransport`)/邀请;回调引用持有防 GC |
| `Net/LagSimTransport.cs` | **延迟模拟装饰器**(NetSim:延迟/抖动/丢包/重复,只包 TCP,无需 Steam 好友) |
| `Net/MpPeer.cs` | 对端(含 `SteamId` `ulong`、`NodeId`、`PingMs`、`CraftXml`) |
| `Net/MpCraftPreloader.cs` | 幽灵船 prefab 异步预热(消除加入白屏 + 真实加载百分比) |
| `Net/EngineVisualSync.cs` | 引擎尾焰/烟雾/过膨胀 + `InjectGhostMotion` 速度注入(kinematic 幽灵视觉) |
| `Net/PartVisualSync.cs` | 部件开关白名单应用(`PartActivated` → `Activate()/Deactivate()`) |
| `Net/ControlVisualSync.cs` | 幽灵 `CraftControls` 控制输入 + 激活组应用(P3) |
| `ModUpdater.cs` | 更新检查(GitHub Releases API + `version.txt` 兜底、看门狗超时、三按钮弹窗) |
| `ModUtils.cs` | 日志封装(`Log`/`LogError`/`LogLobby`/`LogUpdate`,受 `DebugMode` 控制) |
| `ModSettings.cs` | Mod 设置项 |
| `CraftUtils.cs` | 状态应用(`RecalculateFrameState`)+ 幽灵物理禁用(`DisableCraftPhysicCalculation`) |
| `MultiPlayerUI.cs` | 联机 UI(房间/玩家列表、踢人、TickRate、NetSim 分组、TCP debug 分组、加载进度) |
| `HarmonyPatches/` | Harmony patch(**新 patch 放这里**):`JetEngineGhostPatch.cs`、`LayoutRebuildPatch.cs`、`VizzyIsolationPatch.cs` |
| `Net/SteamSpike.cs` / `FishNetSpike.cs` | spike 验证脚本(结论已用,留作参考) |

## 三、已确定的技术事实(不要再重复调研)

**传输 / 房间**
- **传输**:Steam P2P 默认(`SteamNetworkingSockets`;游戏启动已 `SteamAPI.Init()`,mod **不重复 Init**);TCP 仅 VM / 公网 debug(`TcpHostLobby`/`TcpJoinLobby`);LiteNetLib 备用未启用。
- **房主 = 中继**:客户端之间的状态包经房主转发(`IsServer` 时 `Transport.Broadcast`)。
- **FishNet 高层 API 被 codegen 否决**(运行时加载 mod DLL 无序列化器)→ 传输层自建、高层逻辑自持。
- **加入方式**:Steam 房间列表——"开房可见、点列表加入";手动输入房主 SteamId 仍保留于控制台 `SteamJoinLobby <hostSteamId>`。

**幽灵船(remote craft)**
- **幽灵模式**:`AllowPlayerControl=false` + 物理禁用 `SetPhysicsEnabled(false, PhysicsChangeReason.Warp)`(**必须用 Warp**:`UnloadPhysics` 会让 MapCraft 被销毁却留在注册表里 → MapView NRE)+ `CraftUtils.DisableCraftPhysicCalculation`(colliders off、`PreventDebris=true`、`IncludeInDrag=false`、`Damage`/`HeatShield` 拉满)+ 所有 body `RigidBody.isKinematic = true`。
- `InContactWithPlanet` **每帧重申为 true**,并同步 `GroundedSurface*`(private set,反射写),避免游戏把它拉回轨道/坠落。
- 幽灵 modifier(引擎等)**仍会收到 `IFlightUpdate`/`IFlightFixedUpdate`** —— 这是 `MpNetworkManager.IsRemoteCraftNode` 存在的原因(JetEngineGhostPatch / Vizzy 隔离都靠它)。
- **幽灵判定统一入口:`VizzyIsolationPatch.IsGhostCraft(IPartScript)`,三层判定**——① 权威:登记表 `IsRemoteCraftNode`;② **NodeId 记忆** `_ghostNodeIds`(封「断线 / 移除窗口」:`SpawnCraft` 返回后到 `_remoteCrafts` 赋值之间、以及移除后登记表已清但对象仍在的一帧);③ 兜底:命名约定「`对方玩家名|船名`」(`SpawnRemoteCraftAtPosition` 的命名约定)。**新写的"幽灵跳过"补丁请复用该入口,不要只查登记表。**
- **契约:`VizzyIsolationPatch.ClearGhostNodeCache()` 必须由 `MpNetworkManager.OnFlightSceneLoaded`(`MpNetworkManager.cs:419`)在飞行场景加载 / 卸载时调用**,连同各诊断去重集合一起清空——NodeId 由 `FlightState.GetNextNodeId()` 单调分配、场景内唯一但**跨场景会复用**,不清空会把下一飞行里的本地船误判成幽灵(本地 Vizzy 被误杀)。
- 可见性**每帧强制恢复**(`EnforceRemoteCraftVisuals`,所有子 Renderer `enabled = true`)。

**状态包(recdata = `Mod.cs` 的 `RemoteDataPack`)**
- Position/Velocity/Heading(行星空间)+ `SrfRel`(相对地表朝向)+ Pitch/Yaw/Roll/Throttle/Brake/Sliders/Translate + `ActivationGroupStates` + `Stage` + `BodyRotations` + `BodyPositions`(每 body 相对 comRot)+ `EngineThrottles` + `PartActivated` + `Paused` + `Acceleration`/`AngularVelocity`(2 阶外推,2026-09-14;尾部追加字段,EOF 容错)。
- **无燃料 / 资源数值、无部件损伤**(已知限制);**无 craft id / 无多船数组**(多 craft 未做)。
- 消息类型 `MpMessageType`:Hello=1、Welcome=2、PlayerJoin=3、PlayerLeave=4、State=5、Pause=6、CraftData=7、Ping=8、Pong=9、CraftDataAck=10、PlayerJoinAck=11、CraftXmlRequest=12、CraftXmlResponse=13、TickRate=14、Kick=15。

**朝向 / 速度坐标系(最容易踩坑)**
- **朝向同步 = `recdata.SrfRel`(相对地表朝向)**:解决①游戏每帧用 pod 座椅朝向覆盖根朝向、②跨机行星自转角差。`LateUpdate`(`[DefaultExecutionOrder(1000)]`)重写朝向以抗游戏覆盖。
- **速度必须是"地表相对速度"**:`PlanetVectorToSurfaceVector` 是**纯旋转,不减行星自转项**。发送端 = `PlanetVectorToSurfaceVector(craft.Velocity) − CalculateSurfaceVelocity(pos)`;接收端 = `SurfaceVectorToPlanetVector(data.Velocity) + SurfaceVectorToPlanetVector(CalculateSurfaceVelocity(data.Position))`。**漏掉自转项会让静止船上报 ≈ 158.85 m/s**(测试行星),被外推放大成数十米瞬移。

**接收端平滑(现行实现,详见 `archive/latency-smoothing-2026-08-22.md` §9)**
- **不做插值缓冲**:始终取最新包(`TryGetNewest`)+ **连续外推(dead-reckoning)**:`ext = 单向延迟 + 包龄`,封顶 1.0s(`ext > 1.0f` 截断);包龄 `> max(3×gapEMA, 0.25s)` 时冻结为 `ext = 单向延迟`(防幽灵飞走)。
- **2026-09-14 同期落地的四项旁路改动**(代码注释记作 **F1~F4**;其中 F3 = `smoothing-comparison` §五 的 **R2**,F1/F2'/F4 未列入该清单):
  - **F1 虚拟 age 时钟**(`RemoteCraft.VirtualAge`):每帧 `+unscaledDeltaTime`,每包到达**扣除该包与上一包的「内容时间增量」**(发送端 `FlightState.Time` 之差)——替代"距最新包到达时间",消除突发到达造成的目标锯齿。⚠️ **2026-09-14 修**:原先固定扣 `SendIntervalEst`(钳 [0.02,0.1] 的 EMA),而丢一个包时内容增量本是 2×间隔却只扣 1× → age 只在下界钳 0、正向无界 ⇒ 每丢一包永久多出约一个间隔,累积越过冻结阈值后长期卡在 `gapFreeze` 分支(ext 丢掉 age 项)、包恢复也回不来 → "停→冲"顿挫(`MP gapfreeze` 日志来源之一);改扣真实内容增量后每帧累加与每包扣减自动配平。
  - **F2' 到达间隔慢 EMA**(`MArrivalEma`,α=0.01,≈1s 时间常数)作 mRate 分母,抗突发(瞬时间隔 0/几百 ms 交替会把 mRate 打到 0.03~1.14)。
  - **F3 单向延迟 EMA**(`LatencyEmaMs`,α=0.05、跑在 20Hz 的 `OnPong` 上 ⇒ ≈1s 时间常数)= **R2**;注意 `MpPeer.PingMs` 在 `OnPong` 侧已有 0.7/0.3 平滑,故这是第二层 EMA(延迟阶跃响应因此更慢)。
  - **F4 发送端 `sendTimer` 余量钳制**(`> 2×SendIntervalMs` 截断):帧卡顿后按正常节奏补发,避免恢复瞬间每帧泄洪一包。
- **平滑层** `ApplyRemoteSmoothing`:`k = Lerp(0.1, 1, min(1, |v|×0.02))`、`alpha = 1 − (1−k)^(dt×50)`、静止锁定 `|v|<0.5 && 误差<0.05m`、瞬移阈值 `>100m`、旋转 `2.5·dt`、每 body `10·dt` + `0.01` 快照。
- **死代码警告**:`TryGetInterpolatedState` / `RenderDelayMs` / `UnderrunFrames` / `SnapFrames` / `ClearBuffer` / `ReuseInterpBody*` 仍存在但**已不参与渲染**(`snap=`/`interpPct=`/`posErr=` 因此是结构性常量)。看到这些标识符不要以为插值缓冲还在跑。
- **2 阶外推(2026-09-14)**:协议尾部追加 `Acceleration`(地表系)/`AngularVelocity`(craft 局部系,发送端 EMA 0.2 + 钳制 60 m/s² / 3 rad/s + NaN 防御);接收端平移加 `½·a·ext²`(ext 已×mRate → 自动 mRate²,暂停 / 慢放兼容)**已生效**;朝向外推 `SrfRel *= Euler(Flip(ω)·ext·sign)` 已实现但 `EnableRotationExtrap=false` **默认关闭**(ω 符号约定待 `MP sendDiag` 自校验 `errF+/errF-/errR+` 实测定案后一行开启)。

**约定约束**
- 所有玩家**同一行星系统**(房主指定),暂不做跨行星 / 生涯;MVP 锁定 **1x 实时**(无 warp 同步);不做燃料 / 资源 / Vizzy 同步。
- `NodeId` 每机自增、split 时重分配,**跨机不唯一** → 多 craft 必须由 mod 自生成 `Guid`(local `Dictionary<int,Guid>` nodeId→Guid,状态包带 `(ownerId, craftGuid)`);当前代码里**还没有任何 Guid**。

## 四、游戏 API 关键入口(反编译确认)

- `FlightSceneScript.Instance.CraftNode`(本机玩家节点)/ `CraftNodes` / `SpawnCraft` / `ChangePlayersActiveCommandPodImmediate` / `SwitchToNextCommandPod` / `FlightEnd`
- `FlightState.CraftNodes`(IReadOnlyList<CraftNode>)/ `CraftNodeAdded` / `CraftNodeRemoved` / `AddCraft` / `LoadCraftXml` / `PlayerNodeId`
- `CraftNode`:`NodeId` / `AllowPlayerControl` / `HasCommandPod` / `IsDebris` / `InContactWithPlanet` / `DestroyCraft` / `TransitionToNewSoi` / `SetPhysicsEnabled(reason)` / `GroundedSurfacePosition|Velocity|Rotation`(private set)
- `CraftSplitter`:`SplitCraftNode` / `MergeCraftNode` / `ProcessDisconnectedBody` / `DetermineCraftNodeEligibility`
- `CommandPodScript`(激活组 1-indexed 1..10、`SetActivationGroupState`)/ `PartScript.Activate()/Deactivate()` / `CraftControls`(public 可写)
- `EvaScript` / `CommandPodScript.IsEva` / `CrewCompartmentScript`(Drood EVA)
- 详见 `proposals/multi-craft-sync-2026-08-16.md` §七(切换 / 对接 / EVA)、§八(边界排查,含 Harmony 拦 `ChangePlayersActiveCommandPodImmediate` 防劫持、无 pod 残骸处理)。
- **EVA 机制定论(2026-09-18,EVA 专题)**:出舱 = `EvaScript.TakeControl` → `UnloadFromCrewCompartment`(销毁 body 关节)→ `CraftSplitter.ProcessDisconnectedBody/SplitCraftNode` → 新 `CraftNode` + `CraftNodeAdded` → `ChangePlayersActiveCommandPodImmediate` 接管;回舱 = `LoadIntoCrewCompartment` → `ConnectParts` → `CraftSplitter.MergeCraftNode`(吸收 EVA 节点并 `DestroyCraft` → `CraftNodeRemoved`)。本机换节点信号 = `FlightSceneScript.CraftChanged`。**乘组成员走存档级 roster(`EvaData.cs:295` 按 `crewId` 查本地 roster),联机下必须另行处理**。详见 [`proposals/eva-sync-2026-09-18.md`](proposals/eva-sync-2026-09-18.md) §一、§四。

## 五、调试与验证

- **日志**:`Mod.LogLobby`(联机流程)、`Mod.LogUpdate`(更新检查,不受 `DebugMode` 限制)、`Mod.Log`(通用)。平滑诊断 `MP smoothing P<id>: ...` 每 3 秒一条;2 阶外推自校验 `MP sendDiag`、抽搐定位 `MP twitch` 各 1 秒一条(只进 `Player.log`,无悬浮窗)。
- **DevConsole 命令**:
  - 房间:`HostLobbyPort <port>` / `JoinLobbyPort <ip> <port>` / `StopLobby` / `SteamHostLobby <port>` / `SteamJoinLobby <hostSteamId>` / `TcpHostLobby <port>` / `TcpJoinLobby <ip> <port>` / `SetTickRate <hz>`(1~120,房主广播);房间列表:`SteamLobbyList` / `SteamLobbyListWorld` / `SteamLobbyCreate <名>` / `SteamLobbyJoin <id>` / `SteamLobbyLeave`;延迟模拟(NetSim,需 TCP):`NetSimDelay/Jitter/Loss/Duplicate/On/Off/Reset/NetSim`(**数值与总开关分离**);历史 spike:`FishNetSpike` / `SteamSpike`。
- **本地 VM debug**:本机 `TcpHostLobby 25555`(防火墙放行入站);VM `TcpJoinLobby <宿主IP> 25555` —— **✅ 已实测可行**。
- **Steam 双账号公网联机**:**✅ 已实测可行**,零 frp / 零端口转发(见 `archive/steam-integration-2026-08-13.md` Step 4)。
- 反编译源码用 Rider/VS 打开 `.sln` 浏览;`<JNO_CODE>` / `<SP2_MP>` 均属**只读参考**。
- **文本编码**:`.md` / `.cs` 一律 **UTF-8 无 BOM、LF**(历史事故:提交 `16eb58d` 整批文档被有损转码,已从父提交 `7d4925c` 恢复);完整规则与自检见 §十.8 / §十.9。

---

## 六、文档索引(三区)

> 索引行只给**一句话结论**;完整结论、决策记录、实施细节都在各文档内(归档文档另有「〇、经验教训」小节)。
> **三区流转**:根目录 = 活跃(已动手) → `proposals/` = 已论证可行、待拍板(未动手) → `archive/` = 已完成 / 历史。

### 6.1 当前活跃(尚有未完成工作)

| 文档 | 主题 | 状态 | 一句话摘要 |
|---|---|---|---|
| [`acceleration-smoothing-2026-09-14.md`](acceleration-smoothing-2026-09-14.md) | **远程船 2 阶外推**(加速度 + 旋转速率) | 🔧 **期 1 已落地**(2026-09-14,`dotnet build` 0 错误 0 警告) | 平移 `½·a·ext²` **已生效**;朝向外推 `ω·ext` 已实现但**默认关闭**(待 ω 符号实测,`MP sendDiag` 的 `errF+/errF-/errR+` 取最小者定案);回归判据 §四、实测指令 §六之二 |

### 6.2 已论证可行 · 待拍板(`proposals/`,尚未动手)

| 文档 | 主题 | 状态 | 一句话摘要 |
|---|---|---|---|
| [`proposals/multi-craft-sync-2026-08-16.md`](proposals/multi-craft-sync-2026-08-16.md) | **多 Craft 同步**(多节点身份 / 生命周期 / 对接 / 切换 / EVA / 无 pod 残骸 / 边界) | 📋 方案研究 + 边界排查 | ⚠️ 现状是**每玩家一船**、`_remoteCrafts` 按 `int playerId` 索引、**无任何 `Guid`**、状态包无船标识;含 MC1~MC4 里程碑(见 §〇) |
| [`proposals/remote-craft-velocity-2026-09-13.md`](proposals/remote-craft-velocity-2026-09-13.md) | **远程船游戏侧速度缺行星自转项**(静止船读到 ≈0)根因分析 | 📋 **分析完成,修复未做** | 接收端写 `GroundedSurfaceVelocity=data.Velocity`(地表相对速度)漏 ω×r → 游戏 `CraftNode.UpdateCraft` 换算出的 `craft.Velocity` 缺分量(测试行星 158.85 m/s);修复方向见 §五,已并入 physics-sync P0 |
| [`proposals/smoothing-comparison-2026-09-14.md`](proposals/smoothing-comparison-2026-09-14.md) | **平滑方案对照**(SP2 / LunaMultiplayer / 现行)+ 改进清单 R1~R6 | 📋 **研究完成,未实施**(2026-09-14 拍板"只存档") | 病灶:旋转无外推(R1)、延迟估计未平滑(R2)、外推上限 1.0s 过宽(R3)、无时钟同步(R5)、速度源不准(R6);**建议先 R1+R2+R3** 小改一轮实测。⚠️ 现状:**R2 已由同批的 F3(`LatencyEmaMs`)覆盖**,另有 F1/F2'/F4 三项旁路改动(见 §三 平滑段);R1/R3/R5/R6 仍未实施 |
| [`proposals/physics-sync-2026-09-14.md`](proposals/physics-sync-2026-09-14.md) | **SP2 式物理同步移植评估**(每 body 速度注入 + 开销 / 工期) | 📋 **研究完成,未实施**(建议 P0+P1 约 3~5 天) | 开销:每包 +24B/body(≈9.6 KB/s@20Hz×20body,可忽略)、CPU≈0;P0 = 协议速度 + 修 §八 #10,1~2 天;P1 = 旋转 1 阶外推 0.5~1 天;**不建议照搬真实刚体架构(P3,高风险)** |
| [`proposals/eva-sync-2026-09-18.md`](proposals/eva-sync-2026-09-18.md) | **EVA 出舱 / 回舱联机同步**(Drood 机制 / 乘组 roster / 幽灵 EVA 加固 / M0~M4) | 📋 **方案研究,代码零实现** | **EVA 就是 craft(出舱=split、回舱=merge)→ 必须并到多 craft 身份层,不能单开小灶**;现状"一按 EVA 就错位"(状态包旧 NodeId 装新节点坐标);特有难点 = `CrewMember` 是存档级 roster;另发现 EVA 专有控制通道(`Eva*`)一个字段都没传;含 8 条必做的既有坑(P1 拦 `ChangePlayersActiveCommandPodImmediate` 的 patch **尚未写**) |
| [`proposals/eva-internals-2026-09-18.md`](proposals/eva-internals-2026-09-18.md) | **EVA 底层机制参考**(逐方法级事实清单 + 全局静态状态) | 📋 参考资料,不是 plan | 上一行的**支撑材料**:出舱/入舱调用链、持久化状态、相机耦合、`IsPlayerCraft` 单玩家假设;**文末「复核与修正」已推翻初稿的"ghost 受 G 力伤害"结论**,引用前先读该节 |

### 6.3 已归档(历史 / 已完成)

| 文档 | 主题 | 状态 | 一句话摘要 |
|---|---|---|---|
| [`archive/body-sync-2026-08-18.md`](archive/body-sync-2026-08-18.md) | Body 级姿态同步(转轴 / 关节连接部件"整体移动") | ✅ 已实现归档(BodyPoses) | `BodyRotations`→`BodyPoses`(相对 comRot 位置 + 旋转),含残骸小碎片位置缺口;P1~P3 可选优化未排期 |
| [`archive/part-switch-sync-2026-08-18.md`](archive/part-switch-sync-2026-08-18.md) | 起落架等部件展开 / 开关状态同步 | ✅ 核心已实现归档(方案 B P0 实测通过;P3 控制输入已实现) | 同步 per-part `Activated` + 幽灵本地仿真;分离器 / 整流罩 / 对接只记录不处理;剩余项:降落伞驱动(P2)、`ExtensionPercent` 相位对齐(P1)、`Stage` 应用;**1.4.2 回归结案(§12):任一方暂停时不跟随,判定不修** |
| [`archive/latency-smoothing-2026-08-22.md`](archive/latency-smoothing-2026-08-22.md) | 远程船高延迟平滑(>100ms 不"一卡一卡") | ✅ 已实现归档(2026-09-13 收工,用户确认) | 根因演进 ①~⑪ + **现行实现:不做插值缓冲,始终取最新包 + 连续外推**;慢放 / 暂停 / 切换速度模式跳动全部修复(§9.7~§9.16);含 NetSim 调试工具 |
| [`archive/vizzy-isolation-2026-08-22.md`](archive/vizzy-isolation-2026-08-22.md) | Vizzy 联机隔离(阻止跨 Craft 传输 + 禁止幽灵船 Vizzy 执行) | ✅ 已实现归档(1.4.2 复核未失效;G1/G2/G3 已封,G3 于 2026-09-16 双端实测确认) | 双 patch 拦截 `BroadcastMessage`(AllCrafts→Craft)+ `FlightUpdate`(幽灵跳过),封堵 `RequestUserInput`/`SetTimeMode`/`SetCameraProperty` 等侧信道;`Enabled` 默认 `true`;含 `IsGhostCraft` 三层判定与断线 / 移除窗口加固(§七) |
| [`archive/update-reminder-port-2026-09-10.md`](archive/update-reminder-port-2026-09-10.md) | 移植 Volken 的 `ModUpdater.cs`(更新检查 + 三按钮弹窗) | ✅ 已实现并接线归档(游戏内实测待复跑) | 双通道取版本(GitHub Releases API → 仓库根 `version.txt` 兜底)、看门狗超时;UI 文案已进 `EN-US.xml`/`ZH-CN.xml` |
| [`archive/steam-lobby-2026-09-12.md`](archive/steam-lobby-2026-09-12.md) | Steam 大厅系统移植(房间列表替代手动输入 SteamId) | ✅ 已落地归档(2026-09-12 拍板执行;旧决策"Lobby 不做"翻案) | `SteamLobbyBrowser`(SteamMatchmaking 直调:开房 / 列表版本过滤 / 加入 / 邀请)复用 `SteamTransport`,传输零改动;2026-09-13 修回调刷屏 |
| [`archive/update-1.4.2-experimental-2026-09-03.md`](archive/update-1.4.2-experimental-2026-09-03.md) | 游戏 1.4.2(Experimental 分支)兼容适配 | ✅ **已归档**(2026-09,用户确认修复并双端实测完成;原状态:🔧 部分落地) | P0 三项全执行(程序集刷新 / API 迁移 / body 脱离层级修复);朝向 + body 同步 bug 定位并修复;Vizzy 1.4.2 核证 + G1/G2/G3 已封;P1-1~P1-4 与「双飞静止一方抽搐」已实测结案 |
| [`archive/volken-sceneloaded-nre-2026-08-27.md`](archive/volken-sceneloaded-nre-2026-08-27.md) | Volken 冲突:JNO 的 NRE 中断 `SceneLoaded` 事件链 | ✅ 排查完成归档(根因已定位,JNO 侧修复已提交) | `MultiPlayerUI.OnSceneLoaded` 的 `inspectorPanel` 为 null → NRE 中断 .NET 多播事件链 → 排在后面的 Volken `OnSceneLoaded` 不执行(看不到云);护栏见 §九 #4 |
| [`archive/heading-sync-2026-08-17.md`](archive/heading-sync-2026-08-17.md) | 朝向同步(srfRel) | ✅ 已完成并双端实测通过 | 相对地表朝向同步的最终方案;当前实现在 `MpNetworkManager` + `recdata.SrfRel` |
| [`archive/replay-to-multiplayer-2026-08-04.md`](archive/replay-to-multiplayer-2026-08-04.md) | Replay→联机可行性(历史) | 📋 历史分析,大部分已落地 | 联机基础能力 / 架构 / 选型的早期论证;其中"下一步重心"已被 multi-craft-sync 继承 |
| [`archive/steam-integration-2026-08-13.md`](archive/steam-integration-2026-08-13.md) | 传输层:Steam P2P | ✅ 已落地 | `SteamTransport` 已实现并设为默认(`MpNetworkManager.Transport`) |
| [`archive/tcp-transport-2026-08-15.md`](archive/tcp-transport-2026-08-15.md) | 传输层:TCP(VM debug) | ✅ 已落地 | `IMpTransport` + `TcpTransport` + `TcpHostLobby`/`TcpJoinLobby` 命令已实现 |
| [`archive/async-prefab-preload-2026-08-18.md`](archive/async-prefab-preload-2026-08-18.md) | 异步 prefab 预加载(消除加入白屏) | ✅ 已实现(MSBuild exit 0;游戏内实测待复跑) | `MpCraftPreloader` 协程预热主 prefab + 真实百分比旋转白框 + 玩家列表 "⏳ N%" |
| [`archive/engine-fx-sync-2026-08-18.md`](archive/engine-fx-sync-2026-08-18.md) | 幽灵引擎尾焰 / 烟雾 / 过膨胀同步 | ✅ 已实现并实测通过 | 尾焰(液体 + 航发两段加力)、烟雾(`InjectGhostMotion` 速度注入)、过膨胀(`ExpansionRatio` 双保险);含 kinematic 写 velocity 告警刷屏修法(§10.3.1) |

> ✅ 归档文档已修订为**最终状态**并附「〇、经验教训」小节:文档头"状态"均为最终结论,实施步骤的复选框标记实际落地情况。**未勾选项 = 未留档的待验证项**,或**归档时仍挂起的剩余项**(如 part-switch 的 P1/P2),按需复跑,**勿当作当前待办执行**。

## 七、决策速查

| 决策 | 结论 | 出处 |
|---|---|---|
| 跨行星联机 | **暂不做**;默认所有玩家同一行星系统(房主指定),不做生涯相关 | §8.1-1 |
| 幽灵船被劫持(防他人切换操控) | 用 **Harmony prefix 拦总入口** `ChangePlayersActiveCommandPodImmediate`(目标为远程幽灵时 return false) | §8.1-3 |
| 生涯 / 合约 | 不考虑,合约 spawn 的无主 craft 不处理 | §8.3-10 |
| 对接同步 | 走 `CraftNodeRemoved` + 重发 dominant XML,**无需显式 CraftMerge 消息** | §8.1-4 |
| 朝向同步 | `SrfRel`(相对地表),已完成 | archive/heading-sync-2026-08-17.md |
| Lobby 邀请 | **✅ 2026-09-12 翻案落地**:Steam 房间列表(开房可见、点列表加入);手动输入 SteamId 保留于控制台 | [archive/steam-lobby-2026-09-12.md](archive/steam-lobby-2026-09-12.md) |
| MVP 范围(燃料 / 资源 / Vizzy) | **接受不同步**(幽灵物理关,引擎视觉本来不跑) | §8.2-5 |
| Vizzy 跨 craft 传输 + 幽灵船 Vizzy 执行 | **✅ 已实现:双 patch 主动阻止**;`Enabled` 默认 `true`;1.4.2 复核未失效 + G1/G2 加固 | [archive/vizzy-isolation-2026-08-22.md](archive/vizzy-isolation-2026-08-22.md) §七 |
| Steam 双账号公网联机 | **✅ 已实测可行**(零 frp / 零端口转发) | archive/steam-integration-2026-08-13.md |
| TCP VM debug | **✅ 已实测可行**(`TcpHostLobby`/`TcpJoinLobby`) | archive/tcp-transport-2026-08-15.md |
| 起落架等部件开关同步 | **✅ 方案 B(P0)已实测通过** + P3 控制输入应用已实现;1.4.2 回归结案(任一方暂停时不跟随,判定不修) | [archive/part-switch-sync-2026-08-18.md](archive/part-switch-sync-2026-08-18.md) §12 |
| body 级姿态同步 | **✅ 方案定稿 BodyPoses**;不做 SP2 的 ParentBody 树 / 物理平滑 | [archive/body-sync-2026-08-18.md](archive/body-sync-2026-08-18.md) |
| 远程船高延迟平滑 | **✅ 已实现,架构已换代**:速度帧修正 + 弃用插值缓冲改连续外推(封顶 1s、长静默冻结)+ 每帧指数平滑 | [archive/latency-smoothing-2026-08-22.md](archive/latency-smoothing-2026-08-22.md) §9 |
| 远程船 2 阶外推 | 🔧 **期 1 已落地**:平移 `½·a·ext²` 生效;朝向外推默认关闭(待 ω 符号实测) | [acceleration-smoothing-2026-09-14.md](acceleration-smoothing-2026-09-14.md) |
| 平滑改进 R1~R6(SP2 / LMP 对照) | 📋 **已研究未实施**(2026-09-14 拍板"只存档");建议先 R1 + R2 + R3 | [proposals/smoothing-comparison-2026-09-14.md](proposals/smoothing-comparison-2026-09-14.md) |
| 游戏 1.4.2 Experimental 兼容(P0) | ✅ **已归档**(用户确认修复并双端实测完成);P1-1~P1-4 与「双飞静止一方抽搐」已实测结案 | [archive/update-1.4.2-experimental-2026-09-03.md](archive/update-1.4.2-experimental-2026-09-03.md) |
| 更新检查(ModUpdater) | **✅ 已实现并接线**(`Mod.OnModInitialized` 末尾调用) | [archive/update-reminder-port-2026-09-10.md](archive/update-reminder-port-2026-09-10.md) |
| Volken 冲突(`SceneLoaded` 链 NRE) | **✅ 根因已定位并修复**(`OnSceneLoaded` 空值护栏) | [archive/volken-sceneloaded-nre-2026-08-27.md](archive/volken-sceneloaded-nre-2026-08-27.md) |
| 多 craft 同步 | 📋 **方案研究,代码零实现**(每玩家一船、无 `Guid`、状态包无船标识) | [proposals/multi-craft-sync-2026-08-16.md](proposals/multi-craft-sync-2026-08-16.md) §〇 |
| **EVA 出舱 / 回舱同步** | 📋 **方案研究,代码零实现**;**定论:EVA 就是 craft(出舱=split / 回舱=merge)→ 必须并到多 craft 身份层,不做"每玩家两艘"特例** | [proposals/eva-sync-2026-09-18.md](proposals/eva-sync-2026-09-18.md) §〇、§二 |

**当前待定(尚未拍板 / 未调研)**:

- **多 craft**:A1 方案选型(推荐 A+B 混合)、A2 里程碑顺序、A3 残骸同步策略、A4 观察他人第二艘船;B1 跨机身份(Guid + `InitialCraftNodeIds` 溯源)、B2 对账参数、B3 轨道残骸 spawn 可行性、B4 未加载节点采样、B5 MapView 多船回归、B6 时钟对齐——见 [proposals/multi-craft-sync-2026-08-16.md](proposals/multi-craft-sync-2026-08-16.md)。
- **EVA 同步**:E1 乘组处理选型(C1 影子成员 / C2 名字占位,建议先 C2)、E2 里程碑是否按 M0→M1→M2→M3 顺序推进(**M1 换节点即时性可独立先做**)、E3 `EvaGhostPatch` 是否与 `JetEngineGhostPatch` 合并为一个"幽灵飞行循环总闸"、E4 是否补 `CraftSituation`(轨道出舱,与 MC2 合并)、E5 舱内可见乘员是否需要在母船包里带"舱内乘组"——见 [proposals/eva-sync-2026-09-18.md](proposals/eva-sync-2026-09-18.md) §五、§六、§八、§九。
- **SP2 式物理同步**:研究完成待拍板,建议 P0(每 body 速度进协议 + 修 §八 #10)+ P1(旋转 1 阶外推)约 3~5 天——见 [proposals/physics-sync-2026-09-14.md](proposals/physics-sync-2026-09-14.md)。
- **速度修复项**:远程船游戏侧速度缺自转项,分析完成待实施,修复已并入 physics-sync P0——见 [proposals/remote-craft-velocity-2026-09-13.md](proposals/remote-craft-velocity-2026-09-13.md)。
- **平滑剩余项**:朝向外推待 ω 符号实测(定案后一行开启 `EnableRotationExtrap`);R1~R6 待拍板——见 [acceleration-smoothing-2026-09-14.md](acceleration-smoothing-2026-09-14.md)、[proposals/smoothing-comparison-2026-09-14.md](proposals/smoothing-comparison-2026-09-14.md)。
- **部件同步剩余项**(已归档 [archive/part-switch-sync-2026-08-18.md](archive/part-switch-sync-2026-08-18.md)):降落伞专用视觉驱动(P2)、`ExtensionPercent` 相位对齐(P1)、`Stage` 应用(目前只采样不应用)。

## 八、当前代码里的已知问题(2026-09-14 复核)

> 记录**已核实、但还没动手修**的问题,供下次开工直接取用。已解决的保留记录并标记 ✅,不整行删除。

| # | 问题 | 证据 | 影响 |
|---|---|---|---|
| 1 | ~~mod 版本号自相矛盾~~ **✅ 已解决** | `Assets/ModData.asset:35-36` vs `version.txt`(现均为 1.51) | ~~ModUpdater 每次启动弹"有新版本"~~ 不再误报 |
| 2 | **UI 图标资源路径不一致** **✅ 已解决(2026-09-14,工作区未提交)**:资源库前缀与条目已统一为 `MultiPlayer/`,代码请求同名完整路径。关键结论(反编译 `XmlLayout.dll`):`sprite` 经 `ToSprite → LoadResource → XmlLayoutResourceDatabase.GetResource` 按**条目路径逐字匹配(OrdinalIgnoreCase)**,运行时**不自动拼 `PathPrefix`** | `MultiPlayerUI.cs:82`(工作区改动)vs `Content/XML UI/UIResourceDatabase.asset:15,21-23` | 旧值 `/Sprites/UIIcon` 匹配不到条目 ⇒ `[XmlLayout] Unable to load sprite...`、NavPanel 图标空白;改后需游戏内验证再提交 |
| 3 | **日志前缀不统一**:`Log`/`LogError` 已改 `[MultiPlayer]`,但 `LogLobby`/`LogUpdate` 仍是 `[Mptest]` | `ModUtils.cs:21,30` vs `:39,49` | 仅可读性;按前缀过滤日志会漏 |
| 4 | ~~`FlightEnded` 订阅在空值护栏之外~~ **✅ 已修复**:`OnSceneLoaded` 已加 `inspectorPanel` 护栏(2026-08-27 排查;2026-09 又补 Unity 假 null + try/catch 双保险) | `MultiPlayerUI.cs:824-860`;根因见 [archive/volken-sceneloaded-nre-2026-08-27.md](archive/volken-sceneloaded-nre-2026-08-27.md) | ~~NRE 中断 `SceneLoaded` 事件链(Volken 看不到云)~~ 已消除 |
| 5 | **TickRate 默认值自相矛盾**:`TickRate = 30` 但 `SendIntervalMs = 50f`(20Hz) | `MpNetworkManager.cs:49` vs `:45`;`SetTickRate` 同值早退 `:769` | 未调用 `SetTickRate` 前,上报频率与实际发包间隔不一致;UI 滑块初始值也对不上 |
| 6 | **插值时代死代码未清**:`TryGetInterpolatedState`、`RenderDelayMs`、`UnderrunFrames`、`SnapFrames`、`ClearBuffer`、`ReuseInterpBody*` | `MpNetworkManager.cs:2438/47/1300/1301/1606/1328-1329` | `snap=`/`interpPct=`/`posErr=` 成为结构性常量,排查时会误导;`RenderDelayMs` 仍被赋值打印 |
| 7 | **暂停动作同步仍未实现** | `MpNetworkManager.cs` 的 `OnPause` 仍整体注释掉 | `Pause` 消息类型仍在协议里分发但无效果;**但"飞船有速度时暂停 → 观察方抽搐"已全部修复收工(2026-09-13)**:发送端 `Paused` 标记 + 降速上报、接收端冻结期停外推(§9.7~§9.16);**另:任一方暂停时部件开关同样不跟随(2026-09-16 实测,判定可接受、不修**——观察侧暂停时游戏只分发 `*Paused` 接口,幽灵 `IFlightUpdate` 不跑) |
| 8 | **NetSim 无法包 Steam**;`NetSimDuplicate` 无 UI | `LagSimTransport.cs:63-65`;`MultiPlayerUI.cs:175-212` | 延迟模拟只能配合 TCP;重复包只能走控制台 |
| 9 | **`LiteNetLibTransport` 是死代码**(未实现 `IMpTransport`) | `LiteNetLibTransport.cs:25` | 备用传输实际不可选 |
| 10 | **远程飞船游戏侧速度缺自转项(静止船读到 ≈0)**:`ApplyRemoteGroundedSurface` 写 `GroundedSurfaceVelocity=data.Velocity`(地表相对速度),但游戏约定该字段是"地表系惯性速度"(含自转项),`CraftNode.UpdateCraft` 每帧纯旋转换算回行星空间 → `Orbit.Velocity` 缺 ω×r;`CraftFlightData` 在游戏阶段快照该值、mod 反射刷新不覆盖速度字段、`CraftScript.FrameVelocity` 从 kinematic 刚体推导≈0 | `MpNetworkManager.cs:2866`(写)vs 反编译 `CraftNode.cs:1235-1240/1366-1371`、`CraftFlightData.cs:578-584`、`CraftScript.cs:410-441`;详见 [proposals/remote-craft-velocity-2026-09-13.md](proposals/remote-craft-velocity-2026-09-13.md) | 相对速度计算、HUD / 导航球 / Vizzy 读远程船速度出错(静止船读到 0,运动船缺 158.85 m/s 分量);碰撞伤害走 Unity `Collision.relativeVelocity` 不受影响 |
| 11 | **防幽灵被 `[ ]` 接管的 Harmony patch 只决策未落地** | §七 决策行 + multi-craft plan §8.1-3 已定"用 Harmony prefix 拦总入口 `FlightSceneScript.ChangePlayersActiveCommandPodImmediate`",但 `Assets/Scripts/HarmonyPatches/` 目前只有 `JetEngineGhostPatch` / `LayoutRebuildPatch` / `VizzyIsolationPatch`(全仓库 grep `ChangePlayersActiveCommandPodImmediate` **0 命中**) | 接收端按 `[ ]` / 地图 inspector / Vizzy 可切到带 pod 的远程幽灵 → 该类末尾 `craftNode.AllowPlayerControl = true`(`FlightSceneScript.cs:383`)→ 幽灵变本机船 → 双向污染。**EVA 上线后风险放大**(Drood 部件本身就是命令舱,`CommandPodScript.IsEva`)——见 [proposals/eva-sync-2026-09-18.md](proposals/eva-sync-2026-09-18.md) §七 P1 |

## 九、开发流程约定

1. **文档写入与维护一律按 §十**(命名 / 状态单一事实源 / 决策记录 / 交叉链接 / 分区流转 / 编码校验 / 自检清单)。研究有明确结论 → 直接写进对应主题 plan(加「【决策:YYYY-MM-DD】」),并同步本文档决策速查表。
2. 完成主题 → 按 §十.5 归档流程移入 `plans/archive/`(头部改「✅ 已归档」+ 修链接 + 更新索引)。
3. 改代码前先 `read` 目标文件;新 Harmony patch 放 `Assets/Scripts/HarmonyPatches/`。
4. 传输层改动需**双路径回归**:默认 Steam + TCP / NetSim debug 命令。
5. 新增游戏内可见文案 → 同步改 `Assets/Content/Languages/EN-US.xml` 与 `ZH-CN.xml`(key 必须同前缀)。
6. 回复中给出改动的文件(带完整路径),方便点击。
7. **打包 / 部署 / DLL 更新全部由用户负责**:agent 不执行 AssetBundle 打包、不把 mod / DLL 装进游戏目录、不上传 GitHub Releases、不因发布改版本号(`version.txt` / `ModData.asset` / ModUpdater 配套)。需要这些动作时在回复里**提醒用户**即可;agent 侧交付标准 = `dotnet build MultiPlayer.csproj -c Debug` 0 错误 0 警告 + 文档同步。

## 十、文档写入规则(维护约定)

> 适用:`plans/**` 全部 `.md`;本文档自身同样遵守 0 命名与 8 编码。

### 0. 命名与分区
- plan 文件 `<topic>-YYYY-MM-DD.md`(kebab-case + 日期,补零);索引 / 参考文档(本文档)不加日期;禁中文名 / 无日期名(教训:`Volken-冲突排查-…NRE.md` 因不合规范游离在索引外)。
- **三区**:根 = 活跃(已动手);`proposals/` = 已论证可行待拍板(未动手);`archive/` = 已完成 / 历史;三者内部命名规则相同。

### 1. 状态与单一事实源
- **plan 头部「状态:」是唯一事实源**;§六 索引行、§七 决策速查、待定段、§八 已知问题表都只是镜像。
- 受控词表(禁自造):📋 规划中 / 研究 / 评估中 → 🔧 部分落地(动手未完成)→ ✅ 已实现 / 已落地 → ✅ 已归档;同一主题同一时刻只有一个状态。
- 改状态时**同一次改动**内更新:§六 索引行(含分区移动)+ §七 决策速查 + 待定段。教训:`update-1.4.2` 的 P0 早已执行,索引却仍写「规划中」,打架数月。

### 2. 结构模板(新建 plan)
- 头部:`状态:` / `日期:` / `关联:`(相关 plan + 一句关系)/ 一句话定位;正文:动机 → 现状 → 方案·决策 → 实施记录(按日期)→ 剩余项·回归判据;结论先行,被取代的方案标「已被取代,保留作决策记录」。

### 3. 决策记录
- 格式「【决策:YYYY-MM-DD】」:写进对应 plan + 汇总 §七(出处 = 链接 + 小节号);翻案保留旧记录并链接新决策;影响"已定技术事实"的决策同步 §三。

### 4. 交叉链接
- 一律相对路径:同目录用裸文件名;根 → `archive/x.md`、`proposals/x.md`;`archive/` → `../x.md`、`../proposals/x.md`;`proposals/` → `../x.md`、`../archive/x.md`。
- **移动 / 归档后必须全仓库 grep 修链接** + 跑死链校验(§8);站内不写本机绝对路径(令牌化,§10);源码引用用工程相对路径 `Assets/Scripts/...`。

### 5. 分区流转 / 归档流程
1. 研究完成未拍板 → `git mv` 进 `proposals/`(状态保持 📋);拍板动手 → 移回根目录并进 §6.1;
2. 完成 → 头部改「✅ 已归档(原状态:…)」+ 文档内写明未排期剩余项(并同步进本文档待定段)+ `git mv` 进 `archive/` + 按 §4 修链接;
3. 更新本文档:§6.1 → §6.3,同步 §七 出处列。**归档 ≠ 删除**,摘要可随时找回。

### 6. 已知问题登记
- 已核实未修 → 登记 §八(问题 / 证据 / 影响;证据给 `文件:行号` + 反编译出处);修复后标「✅ 已解决」并保留该行,不整行删除。

### 7. 与代码同改、同提交
- 文档与对应代码同一次提交,禁止"改了一半不提交"(教训:曾出现 HEAD / 工作区 / 文档三方漂移);涉及文案、命令、路径时核对本文档 §一 / §二 / §五。

### 8. 编码与校验
- 一律 **UTF-8 无 BOM、行尾 LF**。**禁用会替换非 UTF-8 字节或加 BOM 的工具**:PowerShell 5.1 `Set-Content -Encoding UTF8`(加 BOM)与 `-replace`(处理含 `[` / 反引号的 Markdown 链接会吃字符)。历史事故:`16eb58d` 整批文档被有损转码毁掉 10~16% 汉字,已从 `7d4925c` 恢复。
- 自检(任一为 True 即有问题):`$b=[IO.File]::ReadAllBytes($p); $t=[Text.Encoding]::UTF8.GetString($b); "BOM=$($b[0] -eq 0xEF -and $b[1] -eq 0xBB) CRLF=$($t.Contains("`r`n")) FFFD=$($t.Contains([char]0xFFFD))"`

### 9. 改后自检清单
- [ ] 站内 `](...)` 链接全可解析(无死链)
- [ ] 无 BOM / 无 CRLF / 无 U+FFFD
- [ ] plan 头部「状态:」与 §六 索引行一致(分区正确)
- [ ] 新决策已进 §七(出处带链接)
- [ ] 归档文档已从 §6.1 移入 §6.3,且全仓库无指向旧位置的链接
- [ ] 代码 / 文案改动与文档同批提交
- [ ] 无本机绝对路径 / 用户名 / IP / SteamID(令牌化,§10)

### 10. 隐私红线(公开仓库强制)
- **本机绝对路径 / 用户名 / IP / SteamID 一律不进被跟踪文档**;真实值只存在于本机被 `.gitignore` 排除的 `plans/LOCAL_PATHS.md`(严禁提交,公开仓库不含该文件)。
- **令牌**:`<JNO_CODE>` 反编译游戏源码根(含 `SimpleRockets2/`、`ModApi/` 与两个 `.sln`)/ `<MOD_API>` ModApi 官方公共 API 源码 / `<SP2_MP>` SP2 反编译联机参考 / `<LUNA_MP>` KSP LunaMultiplayer / `<VOLKEN2>` Volken2 / `<SR2_GAME>` 游戏安装目录 / `<USERPROFILE>` 本机用户目录 / `<PROJECT>` 本工程目录 / `<VM_IP>` 测试 VM 地址。
- 引用反编译源码保留「文件名 + 行号」(如 `` `CraftNode.cs:1235-1240` ``),**不写**本机路径形式的站外链接(GitHub 上是死链,且泄路径)。
- **上传前自查**(脚本 / CI 已不维护,靠约定 + 自查):全仓库搜盘符绝对路径、file URI、17 位 SteamID、IPv4、本机链接残留;发现即令牌化并核对 `LOCAL_PATHS.md`。
