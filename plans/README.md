# 联机 Mod 设计文档索引(plans/)

> 项目:JNOMultiPlayer(SimpleRockets 2 / JNO 联机 mod MultiPlayer)
> **新会话先读:[`AGENT_CONTEXT.md`](AGENT_CONTEXT.md)**(项目路径 / 反编译源码 / ModApi / 已定技术事实 / 开发约定,可直接作为提示词)。
> 说明:本文档是 `plans/` 的导航页。**当前活跃文档:`multi-craft-sync-2026-08-16.md`(多 craft)、`body-sync-2026-08-18.md`(body 级姿态同步)、`part-switch-sync-2026-08-18.md`(部件开关/控制输入)、`latency-smoothing-2026-08-22.md`(远程船高延迟平滑)、`vizzy-isolation-2026-08-22.md`(Vizzy 隔离)、`update-1.4.2-experimental-2026-09-03.md`(1.4.2 Experimental 兼容适配)、`update-reminder-port-2026-09-10.md`(Volken ModUpdater 移植分析)**，其余已完成/历史文档已移入 [`archive/`](archive/)。
> 约定:新 plan 建议单一主题一个文件,写清「状态 + 决策记录」,完成后移入 `archive/` 并在此更新索引。
> **调试日志路径**:`C:\Users\usami\AppData\LocalLow\Jundroo\SimpleRockets 2\Player.log`(Unity 运行时日志;`Mod.LogLobby` / `MP smoothing` 等输出在这里)。

---

## 一、当前活跃

| 文档 | 主题 | 状态 | 一句话摘要 |
|---|---|---|---|
| [`multi-craft-sync-2026-08-16.md`](multi-craft-sync-2026-08-16.md) | **多 Craft 同步**(研究阶段,**代码零实现**) | 📋 方案研究 + 边界排查 | 多节点身份/生命周期/对接/切换/EVA/无 pod 残骸/边界情况(jnoCode 排查),含 MC1~MC4 里程碑;⚠️ **现状是每玩家一船、`_remoteCrafts` 按 `int playerId` 索引、无任何 `Guid`、状态包无船标识**(见该文档 §〇);**body 级姿态同步已于 2026-08-18 拆分为独立 plan [`body-sync-2026-08-18.md`](body-sync-2026-08-18.md)** |
| [`body-sync-2026-08-18.md`](body-sync-2026-08-18.md) | **Body 级姿态同步**(转轴/关节连接部件"整体移动")(**P0 已实现,待游戏内实测**) | ✅ 方案已定(BodyPoses) | `BodyRotations`→`BodyPoses`(相对 comRot 的位置+旋转),采样 + 两处接收端应用(`BodyPositions` 平行列表 + `ApplyRemoteBodyPoses`);一并覆盖残骸小碎片位置缺口;**SP2 参考(可抄:body 位姿同步层次/Quaternion32/Delta 优先级;不可抄:引擎钩子/ParentBody 树/FishNet/物理平滑)**;P1~P3 可选优化 |
| [`part-switch-sync-2026-08-18.md`](part-switch-sync-2026-08-18.md) | 起落架开关等部件展开/开关状态同步(**方案 B P0 已实测通过;P3 控制输入应用已实现**) | ✅ 方案 B + P3 落地 | 同步 per-part `Part.Activated` + 幽灵本地仿真,覆盖起落架/货舱/着陆腿/太阳能/灯等;分离器/整流罩/对接**只记录不处理**(归 [`body-sync-2026-08-18.md`](body-sync-2026-08-18.md));降落伞**专用视觉驱动**(§9,P2);**输入驱动部件**(rotator/舵面/活塞/螺旋桨/车轮/RCS/电机)由 **P3 控制输入应用**(§11,写幽灵 Controls)解决;含"1000 起落架"性能分析 |
| [`latency-smoothing-2026-08-22.md`](latency-smoothing-2026-08-22.md) | 远程船**高延迟平滑**(延迟>100ms 不"一卡一卡") | ✅ 已实现(**架构已换代:SP2 式连续外推**,详见该文档 §9;**2026-09 另修「有速度时暂停→观察方抽搐」,见 §9.7/§9.8/§9.9;同会话另修 comRot 反馈环 → 静止船 3cm 往复 + 根 body rel0 对抗 → 12cm 往复,见 [`update-1.4.2` §〇之四](update-1.4.2-experimental-2026-09-03.md)**) | 根因演进:①body 位姿不参与插值(每包整体跳)+ 缓冲欠载冻结-跳变;②**发送端速度帧错误**(`PlanetVectorToSurfaceVector` 纯旋转不减行星自转 ω×r → 静止船上报恒定 158.85 m/s → 外推放大成数十米瞬移,已修);③**暂停时位置冻结但速度仍非零 → 目标以发包频率往复摆动 = 抽搐**(已修:包内 `Paused` 标记 + 连续零位移判据 → 冻结期外推量收敛到固定单向延迟,不再按速度推进);④**2026-09-13 二修**:首版钳制有漏钳制漏洞(`ageNow>latencySec` 守卫在降频发包时永不触发 → 高速船照旧锯齿)且冻结期外推公式变 `2×latencySec`,已一并修正(§9.8);⑤**2026-09-13 双端实测**:冻结修复确认生效,残余 12cm 恒定振荡 = 根 body 包内 rel0(46 body 船 0.1188m)与游戏放置基准对抗,根 body 改写 comPos 修复(§9.9);⑥**2026-09-13 三轮**:游戏 `FramePosition` getter 就是 `CenterOfMass.position` → 每帧把 **comRot 锚到 craft.Position**,根 body 是 comRot 父级 → 被放到 `comPos−G`(G=接收端自身几何偏移,每船不同),根 body 按 `comPos−G` 写出零对抗(§9.11);另修**慢放**:外推 `age` 用接收端真实时间、而发送端包位置按缩放时间走 → 每包向后锯齿(幅度 `v×T_s×(1−timeScale)`,慢放最严重),外推量乘**发送端时间倍率**(相邻包 FlightState.Time 增量/真实到达增量)换算到发送端时间基(§9.10);⑦**2026-09-13 三轮复测**:根 body 修复两端验证通过(b0dLate 全 0),新发现正常飞行顿挫 = 发送率被渲染帧率钳制(30fps→15Hz,`_sendTimer` 改携带余量 → 20Hz)+ 高速时 k=1 无平滑 + 单帧位移上限 `1.5×v×dt` 摊平包到达跳变(§9.12);⑧**2026-09-13 四轮**:用户慢放实测 = Unity `Time.timeScale=0.05` 但游戏 `FlightState.Time` 不缩放 → 包时间倍率恒 1.000 测不出慢放;且真慢放期间静止 ghost 变换链(root/部件/comRot/body)全 0 位移 → 换**位置基运动倍率 mRate**(相邻包位移 ÷ 速度×真实间隔,§9.13/9.14);⑨**2026-09-13 五轮**:代码审查抓到倍率测量是死的(`_lastPushTime` 先覆盖后取差值 → dtReal≡0,rate/mRate 从未更新)→ `prevArrival` 先存后覆盖修复;慢放 1.5 船身跳 = 外推全速 vs 包位置 0.05× 的每包向后锯齿,暂停 0.5m 跳 = 冻结进入回拉 `v×ageNow`(§9.15);⑩**2026-09-13 六轮**:实测 mRate≈0.05/暂停→0 生效,剩"切换速度模式跳变" = 单包测量尖刺透出 EMA(0.208→0.393 → 单帧 ~2m)→ mRate 每包变化钳制 `±0.15`(§9.16);⑪**2026-09-13 收尾**:切换瞬间可见位移 ≤0.3m、高速加速段单帧 ≤ `1.5×v×dt` 物理上限,`b0dLate` 全 0、gapEMA≈50ms、冻结进出干净 —— **用户确认"没什么问题了",收工**。**现行实现**:不做插值缓冲,始终取最新包 + **连续外推(dead-reckoning)**`ext = (单向延迟(RTT/2) + 包龄) × SenderMotionRate`(mRate:相邻包位置位移÷(速度×真实到达间隔),每包变化钳制 ±0.15;封顶 1s,长静默冻结)+ 每帧指数平滑(`alpha=1−(1−k)^(dt×50)`、静止锁定、>100m 瞬移、单帧位移上限 `1.5×v×dt`、每 body 10·dt);插值缓冲/lookback/underrun 已成**死代码**。调试工具:`LagSimTransport`(包 TCP,数值与总开关分离)+ 联机 UI 的 NetSim 分组 + `MP smoothing` 3s 周期日志(含 `rate=`/`mRate=`)+ `MP slowmo` 0.5s 慢放/切换诊断(rootΔ/partΔ/comΔ/dist)+ `MP freeze` 状态跃迁日志(**诊断唯一出口,无悬浮窗**)。SP2 参考:[`CraftStateSerializer.cs:55-94`](file:///C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/CraftStateSerializer.cs:55)、[`BodyScript.cs:660-679`](file:///C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Craft/BodyScript.cs:660) |
| [`vizzy-isolation-2026-08-22.md`](vizzy-isolation-2026-08-22.md) | **Vizzy 联机隔离:阻止跨 Craft 数据传输 + 禁止幽灵船 Vizzy 执行** | ✅ 已实现(双 patch:`BroadcastMessage` + `FlightUpdate`,含 `Enabled` 开关) | Harmony Prefix 拦截 `BroadcastMessage`(AllCrafts→Craft) + `FlightUpdate`(幽灵船跳过),封堵 `RequestUserInput`/`SetTimeMode`/`SetCameraProperty` 等所有侧信道;`VizzyIsolationPatch.Enabled` 默认 `true`,设 `false` 恢复原生 |
| [`update-1.4.2-experimental-2026-09-03.md`](update-1.4.2-experimental-2026-09-03.md) | **游戏 1.4.2 Experimental 兼容适配** | 📋 **规划中(P0 三项均未执行)** | ①刷新参考程序集并重编译——`Assets/ModTools/Assemblies/**` 仍全部是 **2026-07-28** 的旧程序集,**未刷新**;②`GetComponentsInCraft` 全仓库 0 命中,`GetComponentsInChildren` 仍在 `CraftUtils.cs:78/119/123/127`、`MpNetworkManager.cs:1354`、`EngineVisualSync.cs:231` 使用,**未迁移**;③`SetPose` 0 命中,**未采用**;④`GroundedSurface*` 走**反射**写入(Node 属性,重命名会静默失效);⑤全仓库**无任何游戏版本检查/实验版开关**,握手也无版本字段;P1 各项均未开始 |
| [`update-reminder-port-2026-09-10.md`](update-reminder-port-2026-09-10.md) | 移植 Volken 的 `ModUpdater.cs`(更新检查 + 三按钮提醒弹窗)到 MultiPlayer | ✅ **已实现并接线**(分析文档保留) | 已落地 `Assets/Scripts/ModUpdater.cs` 并在 `Mod.OnModInitialized()` 末尾调用(在 `ModVersion = ModInfo.Version` 赋值之后);双通道取版本(GitHub Releases API → 仓库根 `version.txt` 兜底)、看门狗超时防卡死;UI 文案已进 `EN-US.xml`/`ZH-CN.xml` |
| [`steam-lobby-2026-09-12.md`](steam-lobby-2026-09-12.md) | **Steam 大厅系统移植(房间列表替代手动输入房主 SteamId)** | ✅ **已落地(2026-09-12 拍板执行;旧决策"Lobby 不做"翻案)** | 新增 `SteamLobbyBrowser`(SteamMatchmaking 直调:CreateLobby/RequestLobbyList 版本过滤/JoinLobby→GetLobbyOwner→复用 `SteamTransport.StartClient`,传输零改动);UI 用动态 GroupModel(方案 3 MVP):inspector 面板新增"Steam 房间列表"分组(刷新/邀请/房间按钮行);手动 SteamId 路径保留于控制台命令;**2026-09-13 修刷屏**:原假设"游戏每帧托管 RunCallbacks()"被实测推翻(托管 CallbackDispatcher 从未初始化,直接泵每帧抛异常,一个会话 6645 条)→ 反射补初始化再泵,失败降级靠游戏 native 泵并只警告一次 |

> `multi-craft-sync-2026-08-16.md` 承接了归档文档里遗留的"下一步"项(如多船身份/生命周期/残骸),后续以它为准;**body 同步已独立成 [`body-sync-2026-08-18.md`](body-sync-2026-08-18.md)**,不再是 multi-craft 的子项。
> `part-switch-sync-2026-08-18.md` 为"部件开关/展开状态"的补充分析(2026-08 新增,**方案 B P0 已实测通过,P3 控制输入应用已实现**)。

---

## 二、已归档(历史 / 已完成)

| 文档 | 主题 | 状态 | 一句话摘要 |
|---|---|---|---|
| [`archive/heading-sync-2026-08-17.md`](archive/heading-sync-2026-08-17.md) | 朝向同步(srfRel) | ✅ 已完成并双端实测通过 | 相对地表朝向同步的最终方案;当前实现在 `MpNetworkManager` + `recdata.SrfRel` |
| [`archive/replay-to-multiplayer-2026-08-04.md`](archive/replay-to-multiplayer-2026-08-04.md) | Replay→联机可行性(历史) | 📋 历史分析,大部分已落地 | 联机基础能力/架构/选型的早期论证;其中的"下一步重心"已被 multi-craft-sync 继承 |
| [`archive/steam-integration-2026-08-13.md`](archive/steam-integration-2026-08-13.md) | 传输层:Steam P2P | ✅ 已落地 | SteamTransport 已实现并设为默认(`MpNetworkManager.Transport`) |
| [`archive/tcp-transport-2026-08-15.md`](archive/tcp-transport-2026-08-15.md) | 传输层:TCP(VM debug) | ✅ 已落地 | `IMpTransport` + `TcpTransport` + `TcpHostLobby`/`TcpJoinLobby` 命令已实现 |
| [`archive/async-prefab-preload-2025-01-01.md`](archive/async-prefab-preload-2025-01-01.md) | 异步 prefab 预加载(消除加入白屏) | ✅ 已实现(MSBuild exit 0;游戏内实测待复跑) | `MpCraftPreloader` 协程预热主 prefab + 真实百分比旋转白框 + 玩家列表 "⏳ N%";见该文档「〇、经验教训」 |
| [`archive/engine-fx-sync-2026-08-18.md`](archive/engine-fx-sync-2026-08-18.md) | 幽灵引擎尾焰/烟雾/过膨胀同步 | ✅ 已实现并实测通过 | 尾焰(液体+航发两段加力 Route A/B)、烟雾(`InjectGhostMotion` 速度注入)、过膨胀(`ExpansionRatio` 双保险);含 kinematic 写 velocity 告警刷屏修法(§10.3.1),详见「〇、经验教训」 |

> ✅ 归档文档已修订为**最终状态**并附「〇、经验教训」小节(作为开发过程经验存档):文档头的"状态"均为最终结论,实施步骤的复选框标记了实际落地情况。**未勾选项 = 未留档的待验证项**(如"双 Steam 账号公网实测"),按需复跑,勿当作当前待办执行。

---

## 三、决策速查(最新决策,详见 multi-craft-sync-2026-08-16.md)

| 决策 | 结论 | 出处 |
|---|---|---|
| 跨行星联机 | **暂不做**;默认所有玩家同一行星系统(房主指定),不做生涯相关 | §8.1-1 |
| 幽灵船被 [ ] 劫持 | 用 **Harmony prefix 拦总入口** `ChangePlayersActiveCommandPodImmediate`(目标为远程幽灵时 return false) | §8.1-3 |
| 生涯/合约 | 不考虑,合约 spawn 的无主 craft 不处理 | §8.3-10 |
| 对接同步 | 走 `CraftNodeRemoved` + 重发 dominant XML,**无需显式 CraftMerge 消息** | §8.1-4 |
| 朝向同步 | srfRel(相对地表),已完成 | archive/heading-sync-2026-08-17.md |
| Lobby 邀请 | **✅ 2026-09-12 翻案落地:Steam 房间列表(大厅浏览器)**——"开房可见、点列表加入";`SteamLobbyBrowser` 实现开房/列表(版本过滤)/加入(`GetLobbyOwner`→复用 `SteamTransport`)/好友邀请;手动输入 SteamId 保留于控制台 | [steam-lobby-2026-09-12.md](steam-lobby-2026-09-12.md) |
| MVP 范围(燃料/资源/Vizzy) | **接受不同步**(幽灵物理关,引擎视觉本来不跑) | §8.2-5 |
| Vizzy 跨 craft 数据传输(广播) + 幽灵船 Vizzy 执行 | **✅ 已实现:双 patch 主动阻止**——`BroadcastMessage`(AllCrafts→Craft) + `FlightUpdate`(幽灵船跳过),封堵 `RequestUserInput`/`SetTimeMode`/`SetCameraProperty` 等侧信道;`VizzyIsolationPatch.Enabled` 默认 `true` | [vizzy-isolation-2026-08-22.md](vizzy-isolation-2026-08-22.md) |
| Steam 双账号公网联机 | **✅ 已实测可行**(零 frp/零端口转发) | archive/steam §Step4 |
| TCP VM debug | **✅ 已实测可行**(`TcpHostLobby`/`TcpJoinLobby`) | archive/tcp §四 |
| 起落架等部件开关同步 | **✅ 方案 B(P0)已实测通过**(per-part `Activated` 位);分离器/整流罩/对接 **只记录不处理**(归 [`body-sync-2026-08-18.md`](body-sync-2026-08-18.md));降落伞走 **专用视觉驱动**(P2);**P3 控制输入应用已实现**(写幽灵 Controls + 放开输入驱动部件 Activated:rotator/舵面/活塞/螺旋桨/车轮/RCS/电机) | [part-switch-sync-2026-08-18.md](part-switch-sync-2026-08-18.md) §3/§4/§9/§10/§11 |
| body 级姿态同步(转轴连接部件整体移动) | **✅ 方案定稿:BodyPoses**(`BodyRotations`→相对 comRot 的位置+旋转;SP2 验证方向;**P0 已实现**);残骸小碎片位置缺口一并覆盖;不做 SP2 的 ParentBody 树/物理平滑 | [body-sync-2026-08-18.md](body-sync-2026-08-18.md) |
| 远程船高延迟平滑(>100ms 不卡顿) | **✅ 已实现,架构已换代**:①**速度帧修正**(发送端 `PlanetVectorToSurfaceVector` 不减行星自转 ω×r → 静止船报 158.85 m/s → 外推放大成瞬移,已修);②接收端**弃用插值缓冲**,改为 **SP2 式连续外推(dead-reckoning)**:始终取最新包 + `ext = 单向延迟(RTT/2) + 包龄`(封顶 1s、长静默冻结)+ 每帧指数平滑(`dt×50` 收敛、静止锁定、>100m 瞬移、每 body `10·dt`);调试工具(`LagSimTransport` + UI NetSim 分组 + `MP smoothing` 3s 日志)已落地 | [latency-smoothing-2026-08-22.md](latency-smoothing-2026-08-22.md) §9(现行实现) |
| 游戏 1.4.2 Experimental 兼容(P0) | 📋 **决策已定但三项都未执行**:①刷新 ModTools 程序集(现仍是 2026-07-28 的旧程序集)重编译;②`GetComponentsInChildren` → `GetComponentsInCraft`/逐 body(现 `GetComponentsInCraft` 0 命中);③`RecenterTransformOnCoM` 1 参调用(换程序集后会 `MissingMethodException`);另:`GroundedSurface*` 走反射(改名会静默失效)、全仓库无游戏版本检查/实验版开关 | [update-1.4.2-experimental-2026-09-03.md](update-1.4.2-experimental-2026-09-03.md) §〇之二 |
| 更新检查(ModUpdater) | **✅ 已实现并接线**(`Mod.OnModInitialized` 末尾调用):GitHub Releases API → 仓库根 `version.txt` 兜底;15s 总看门狗 + 10s 单请求超时;三按钮弹窗(Download / Later / 不再提醒) | [update-reminder-port-2026-09-10.md](update-reminder-port-2026-09-10.md) |
| 多 craft 同步 | 📋 **方案研究,代码零实现**:仍是**每玩家一船**、`_remoteCrafts` 按 `int playerId` 索引、**无任何 `Guid`**、状态包无船标识、无残骸路径 | [multi-craft-sync-2026-08-16.md](multi-craft-sync-2026-08-16.md) §〇 |

**当前待定(尚未拍板/未调研)**:A1 方案选型(推荐 A+B 混合,待正式决策)、A2 里程碑顺序、A3 残骸同步策略、A4 观察他人第二艘船(部件开关方案 B 已于 2026-08-18 拍板,见上表;但"观察他人控制"本身待定);B1 跨机身份(Guid+InitialCraftNodeIds 溯源)、B2 对账参数、B3 轨道残骸 spawn 可行性、B4 未加载节点采样、B5 MapView 多船回归、B6 时钟对齐;D 类已决策项的实现暂缓。**游戏 1.4.2(Experimental)相关待定**(详见 [update-1.4.2-experimental-2026-09-03.md](update-1.4.2-experimental-2026-09-03.md)):P1-1 参考系重居中叠加(PreSimulation+pendingRecenterDelta vs mod 帧补偿,双重平移风险)、P1-2 GroundedSurface×`SetPose` 接地放置、P1-3 游戏版本检查/实验版开关、P1-4 1.4.2 调试设施接入(GameLoopTypeProfiler/SetFlatDecorationCulling)——均待 1.4.2 实验版双端实测后拍板。**部件同步剩余项**:降落伞专用视觉驱动(P2)、`ExtensionPercent` 相位对齐(P1)、`Stage` 应用(目前只采样不应用)。

---

## 三之二、当前代码里的已知问题(2026-09 复核,尚未修)

> 这一节记录**已核实、但还没动手修**的问题,供下次开工直接取用。

| # | 问题 | 证据 | 影响 |
|---|---|---|---|
| 1 | **mod 版本号自相矛盾**:本地 `ModData.asset` 是 **1.4**,发布用 `version.txt` 是 **1.5** | `Assets/ModData.asset:35-36` vs `version.txt` | `ModUpdater` 判定 `1.5 > 1.4` ⇒ **每次启动都弹"有新版本"**,只能"不再提醒"消掉 |
| 2 | **UI 图标资源路径不一致**:代码请求 `MultiPlayer/Sprites/UIIcon`,资源库前缀仍是 `aMptest/` | `MultiPlayerUI.cs:71` vs `Content/XML UI/UIResourceDatabase.asset:15,21` | NavPanel 联机按钮图标可能取不到(改名遗留) |
| 3 | **日志前缀不统一**:`Log`/`LogError` 已改 `[MultiPlayer]`,但 `LogLobby`/`LogUpdate` 仍是 `[Mptest]` | `ModUtils.cs:21,30` vs `:39,49` | 仅可读性;按前缀过滤日志会漏 |
| 4 | **`FlightEnded` 订阅在空值护栏之外** | `MultiPlayerUI.cs:630`(护栏在 `:625-629`) | `OnSceneLoaded` 仍可能抛异常并**打断 `SceneLoaded` 事件链**,使链上其它 mod(如 Volken)的场景回调不执行 |
| 5 | **TickRate 默认值自相矛盾**:`TickRate = 30` 但 `SendIntervalMs = 50f`(20Hz) | `MpNetworkManager.cs:49` vs `:45`;`SetTickRate` 同值早退 `:558` | 未调用 `SetTickRate` 前,上报频率与实际发包间隔不一致;UI 滑块初始值也对不上 |
| 6 | **插值时代死代码未清**:`TryGetInterpolatedState`、`RenderDelayMs`、`UnderrunFrames`、`SnapFrames`、`ClearBuffer`、`ReuseInterpBody*` | `MpNetworkManager.cs:1783/47/1086/1087/1176/1106` | `snap=`/`interpPct=`/`posErr=` 成为结构性常量,排查时会误导;`RenderDelayMs` 仍被赋值打印 |
| 7 | **暂停动作同步仍未实现** | `MpNetworkManager.cs` 的 `OnPause` 仍整体注释掉 | `Pause` 消息类型仍在协议里分发但无效果;**但"飞船有速度时暂停 → 观察方位置抽搐"已全部修复并收工(2026-09-13)**:发送端 `Paused` 标记 + 降速上报,接收端冻结期停止速度外推;钳制漏洞 + 冻结外推公式二修;comRot 反馈环(静止船 3cm 往复)+ 根 body rel0 对抗(12cm 往复,comPos−G);慢放锯齿(位置基运动倍率 mRate + 每包变化钳制 ±0.15,prevArrival 修复测量死代码),见 [latency-smoothing §9.7~§9.16](latency-smoothing-2026-08-22.md) 与 [update-1.4.2 §〇之四](update-1.4.2-experimental-2026-09-03.md);用户实测确认:慢放/暂停/切换速度模式跳动均降至可忽略;**仅"暂停状态不搬给对端"这一点保持原样** |
| 8 | **NetSim 无法包 Steam**;`NetSimDuplicate` 无 UI | `LagSimTransport.cs:63-65`;`MultiPlayerUI.cs:175-212` | 延迟模拟只能配合 TCP;重复包只能走控制台 |
| 9 | **`LiteNetLibTransport` 是死代码**(未实现 `IMpTransport`) | `LiteNetLibTransport.cs:25` | 备用传输实际不可选 |

---

## 四、维护约定

0. **文件命名规范**:所有 plan 文件采用 `<topic>-YYYY-MM-DD.md` 格式(kebab-case + 日期后缀)。`README.md` 和 `AGENT_CONTEXT.md` 为索引/参考文档,不加日期。归档文件同样遵守此规范,置于 `archive/` 子目录。
1. 新增/修改 plan:在文档头写清「状态:规划中 / 已落地 / 已归档」;
2. 完成一个主题后移到 `archive/`,并同步更新本索引;
3. 有明确结论时直接写进对应 plan(加「【决策:…】」标记),并汇总到上表。
4. **不要用会产生 `U+FFFD` 或加 BOM 的工具改这些文档**(历史事故:整批文档被有损转码毁掉约 10~16% 汉字);改完请确认无 `U+FFFD`、行尾 LF。