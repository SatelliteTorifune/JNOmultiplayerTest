# 联机 Mod 设计文档索引(plans/)

> 项目:JNOMultiPlayer(SimpleRockets 2 / JNO 联机 mod MultiPlayer)
> **新会话先读:[`AGENT_CONTEXT.md`](AGENT_CONTEXT.md)**(项目路径 / 反编译源码 / ModApi / 已定技术事实 / 开发约定,可直接作为提示词)。
> 说明:本文档是 `plans/` 的导航页。**当前活跃文档:`multi-craft-sync-2026-08-16.md`(多 craft)、`update-1.4.2-experimental-2026-09-03.md`(1.4.2 Experimental 兼容适配)、`remote-craft-velocity-2026-09-13.md`(远程船游戏侧速度缺失根因)、`acceleration-smoothing-2026-09-14.md`(2 阶外推评估:加速度+旋转速率)**,其余已完成/历史文档已移入 [`archive/`](archive/)。
> 约定:新 plan 建议单一主题一个文件,写清「状态 + 决策记录」,完成后移入 `archive/` 并在此更新索引;**完整文档写入规则见 [§四](#四文档写入规则维护约定)**。
> **调试日志路径**:`<USERPROFILE>\AppData\LocalLow\Jundroo\SimpleRockets 2\Player.log`(Unity 运行时日志;`Mod.LogLobby` / `MP smoothing` 等输出在这里)。

---

## 一、当前活跃(尚有未完成工作)

| 文档 | 主题 | 状态 | 一句话摘要 |
|---|---|---|---|
| [`multi-craft-sync-2026-08-16.md`](multi-craft-sync-2026-08-16.md) | **多 Craft 同步**(研究阶段,**代码零实现**) | 📋 方案研究 + 边界排查 | 多节点身份/生命周期/对接/切换/EVA/无 pod 残骸/边界情况(`<JNO_CODE>` 排查),含 MC1~MC4 里程碑;⚠️ **现状是每玩家一船、`_remoteCrafts` 按 `int playerId` 索引、无任何 `Guid`、状态包无船标识**(见该文档 §〇) |
| [`update-1.4.2-experimental-2026-09-03.md`](update-1.4.2-experimental-2026-09-03.md) | **游戏 1.4.2 Experimental 兼容适配** | 📋 **P0 已全部执行,代码侧完成**(程序集已刷新;`GetComponentsInCraft` 迁移已落地;1.4.2 朝向/body bug 已修;**新报「双飞静止一方抽搐」已加诊断日志待实测**) | ①刷新参考程序集并重编译(已完成,2026-09-13);②`GetComponentsInChildren` → `GetComponentsInCraft`/逐 body(已完成);③1.4.2 body 脱离 craft 层级导致 `localRotation` 语义变化 → 已修 `ApplyRemoteBodyPoses`(见该文档「〇之三」);另:`GroundedSurface*` 走反射(改名会静默失效)、全仓库无游戏版本检查/实验版开关;P1 各项仍待定 |
| [`remote-craft-velocity-2026-09-13.md`](remote-craft-velocity-2026-09-13.md) | **远程飞船游戏侧速度缺失(缺自转项)根因分析** | 📋 **分析完成,修复未做** | 状态包链路带速度,但接收端 `ApplyRemoteGroundedSurface` 写 `GroundedSurfaceVelocity=data.Velocity`(地表相对速度)漏行星自转项 → 游戏 `CraftNode.UpdateCraft` 每帧纯旋转换算出的 `craft.Velocity`/`FlightData.Velocity` 缺 ω×r(测试行星 158.85 m/s,静止船≈0),相对速度等功能出错;修复方向:字段补自转项 + FlightData 速度字段刷新(见该文档 §五) |
| [`acceleration-smoothing-2026-09-14.md`](acceleration-smoothing-2026-09-14.md) | **远程船 2 阶外推**(加速度 + 旋转速率;平移 1 阶 / 旋转 0 阶的两个缺口) | 🔧 **期 1 已落地**(2026-09-14,dotnet build 0 错误 0 警告) | dev 讨论:飞船时刻变速度/变朝向,只有 1 阶外推必然"每包跳状态";2 阶 + 旋转速率是 key,输入突变 unavoidable 但会更 subtle。**已实现**:协议尾部追加 `Acceleration/AngularVelocity`(EOF 容错)、发送端 `FlightData` 采样(EMA+钳制+NaN 防御)、接收端**平移 2 阶外推 `½·a·ext²` 已生效**、sendDiag ω 符号自校验(`errF+/errF-/errR+`);**朝向外推默认关闭**(待 ω 符号实测后一行开启);回归判据见该文档 §四、实测指令 §六之二 |
| [`physics-sync-2026-09-14.md`](physics-sync-2026-09-14.md) | **SP2 式物理同步移植评估**(每 body 速度注入 + 开销/工期两问) | 📋 **研究完成,未实施**(2026-09-14;建议 P0+P1 约 3~5 天,待拍板) | 解析 SP2「每 body 速度注入真实刚体」机制 → 差距表 G1~G4;开销=每包 +24B/body(≈9.6 KB/s@20Hz×20body,可忽略)、CPU≈0;工期=P0(协议速度+游戏侧速度修复 #10)1~2 天 + P1(旋转 1 阶外推)0.5~1 天;**不建议照搬 SP2 真实刚体架构(P3,1~2 周高风险)**;实施路径见该文档 §六 |

---

## 二、已归档(历史 / 已完成)

| 文档 | 主题 | 状态 | 一句话摘要 |
|---|---|---|---|
| [`archive/body-sync-2026-08-18.md`](archive/body-sync-2026-08-18.md) | **Body 级姿态同步**(转轴/关节连接部件"整体移动") | ✅ 已实现归档(BodyPoses) | `BodyRotations`→`BodyPoses`(相对 comRot 的位置+旋转),采样 + 两处接收端应用(`BodyPositions` 平行列表 + `ApplyRemoteBodyPoses`);一并覆盖残骸小碎片位置缺口;SP2 参考结论;P1~P3 可选优化未排期 |
| [`archive/part-switch-sync-2026-08-18.md`](archive/part-switch-sync-2026-08-18.md) | 起落架开关等部件展开/开关状态同步 | ✅ 核心已实现归档(**方案 B P0 已实测通过;P3 控制输入应用已实现**) | 同步 per-part `Part.Activated` + 幽灵本地仿真;分离器/整流罩/对接**只记录不处理**;输入驱动部件由 **P3 控制输入应用**(写幽灵 Controls)解决;剩余项:降落伞专用视觉驱动(P2)、`ExtensionPercent` 相位对齐(P1)、`Stage` 应用(只采样不应用),均未排期 |
| [`archive/latency-smoothing-2026-08-22.md`](archive/latency-smoothing-2026-08-22.md) | 远程船**高延迟平滑**(延迟>100ms 不"一卡一卡") | ✅ 已实现归档(**2026-09-13 收工,用户确认**) | 根因演进 ①~⑪ + **现行实现:不做插值缓冲,始终取最新包 + 连续外推(dead-reckoning)**`ext = (单向延迟(RTT/2) + 包龄) × SenderMotionRate`(mRate:相邻包位置位移÷(速度×真实到达间隔),每包变化钳制 ±0.15),封顶 1s、长静默冻结;每帧指数平滑(`dt×50`、静止锁定、>100m 瞬移、每 body `10·dt`);慢放/暂停/切换速度模式跳动全部修复(§9.7~§9.16);调试工具(`LagSimTransport` + UI NetSim 分组 + `MP smoothing` 3s 日志);2 阶外推为后续项,见 [acceleration-smoothing-2026-09-14.md](acceleration-smoothing-2026-09-14.md) |
| [`archive/vizzy-isolation-2026-08-22.md`](archive/vizzy-isolation-2026-08-22.md) | **Vizzy 联机隔离:阻止跨 Craft 数据传输 + 禁止幽灵船 Vizzy 执行** | ✅ 已实现归档(双 patch;游戏内双端实测待复跑) | Harmony Prefix 拦截 `BroadcastMessage`(AllCrafts→Craft) + `FlightUpdate`(幽灵船跳过),封堵 `RequestUserInput`/`SetTimeMode`/`SetCameraProperty` 等所有侧信道;`VizzyIsolationPatch.Enabled` 默认 `true`,设 `false` 恢复原生 |
| [`archive/update-reminder-port-2026-09-10.md`](archive/update-reminder-port-2026-09-10.md) | 移植 Volken 的 `ModUpdater.cs`(更新检查 + 三按钮提醒弹窗)到 MultiPlayer | ✅ 已实现并接线归档(游戏内实测待复跑) | 已落地 `Assets/Scripts/ModUpdater.cs` 并在 `Mod.OnModInitialized()` 末尾调用;双通道取版本(GitHub Releases API → 仓库根 `version.txt` 兜底)、看门狗超时防卡死;UI 文案已进 `EN-US.xml`/`ZH-CN.xml` |
| [`archive/steam-lobby-2026-09-12.md`](archive/steam-lobby-2026-09-12.md) | **Steam 大厅系统移植(房间列表替代手动输入房主 SteamId)** | ✅ 已落地归档(2026-09-12 拍板执行;旧决策"Lobby 不做"翻案) | 新增 `SteamLobbyBrowser`(SteamMatchmaking 直调:CreateLobby/RequestLobbyList 版本过滤/JoinLobby→GetLobbyOwner→复用 `SteamTransport.StartClient`,传输零改动);UI 动态 GroupModel"Steam 房间列表"分组;手动 SteamId 路径保留于控制台;**2026-09-13 修刷屏**(托管 CallbackDispatcher 反射补初始化,失败降级靠游戏 native 泵) |
| [`archive/volken-sceneloaded-nre-2026-08-27.md`](archive/volken-sceneloaded-nre-2026-08-27.md) | **Volken 冲突排查:JNOMultiPlayer 的 NRE 中断 SceneLoaded 事件链** | ✅ 排查完成归档(**根因已定位,JNO 侧修复已提交**) | `MultiPlayerUI.OnSceneLoaded` 在从未打开过联机面板时 `inspectorPanel` 为 null → NRE 中断 .NET 多播事件链 → 排在后面的 Volken `OnSceneLoaded` 不执行(看不到云/自带云开关锁死);JNO 侧空值护栏已提交(`MultiPlayerUI.cs:791`),Volken 侧自愈初始化已实施;见已知问题 #4 |
| [`archive/heading-sync-2026-08-17.md`](archive/heading-sync-2026-08-17.md) | 朝向同步(srfRel) | ✅ 已完成并双端实测通过 | 相对地表朝向同步的最终方案;当前实现在 `MpNetworkManager` + `recdata.SrfRel` |
| [`archive/replay-to-multiplayer-2026-08-04.md`](archive/replay-to-multiplayer-2026-08-04.md) | Replay→联机可行性(历史) | 📋 历史分析,大部分已落地 | 联机基础能力/架构/选型的早期论证;其中的"下一步重心"已被 multi-craft-sync 继承 |
| [`archive/steam-integration-2026-08-13.md`](archive/steam-integration-2026-08-13.md) | 传输层:Steam P2P | ✅ 已落地 | SteamTransport 已实现并设为默认(`MpNetworkManager.Transport`) |
| [`archive/tcp-transport-2026-08-15.md`](archive/tcp-transport-2026-08-15.md) | 传输层:TCP(VM debug) | ✅ 已落地 | `IMpTransport` + `TcpTransport` + `TcpHostLobby`/`TcpJoinLobby` 命令已实现 |
| [`archive/async-prefab-preload-2025-01-01.md`](archive/async-prefab-preload-2025-01-01.md) | 异步 prefab 预加载(消除加入白屏) | ✅ 已实现(MSBuild exit 0;游戏内实测待复跑) | `MpCraftPreloader` 协程预热主 prefab + 真实百分比旋转白框 + 玩家列表 "⏳ N%";见该文档「〇、经验教训」 |
| [`archive/engine-fx-sync-2026-08-18.md`](archive/engine-fx-sync-2026-08-18.md) | 幽灵引擎尾焰/烟雾/过膨胀同步 | ✅ 已实现并实测通过 | 尾焰(液体+航发两段加力 Route A/B)、烟雾(`InjectGhostMotion` 速度注入)、过膨胀(`ExpansionRatio` 双保险);含 kinematic 写 velocity 告警刷屏修法(§10.3.1),详见「〇、经验教训」 |

> ✅ 归档文档已修订为**最终状态**并附「〇、经验教训」小节(作为开发过程经验存档):文档头的"状态"均为最终结论,实施步骤的复选框标记了实际落地情况。**未勾选项 = 未留档的待验证项**(如"双 Steam 账号公网实测")、或**归档时仍挂起的剩余项**(如 part-switch 的 P1/P2),按需复跑,勿当作当前待办执行。

---

## 三、决策速查(最新决策,详见 multi-craft-sync-2026-08-16.md)

| 决策 | 结论 | 出处 |
|---|---|---|
| 跨行星联机 | **暂不做**;默认所有玩家同一行星系统(房主指定),不做生涯相关 | §8.1-1 |
| 幽灵船被劫持(防他人切换操控) | 用 **Harmony prefix 拦总入口** `ChangePlayersActiveCommandPodImmediate`(目标为远程幽灵时 return false) | §8.1-3 |
| 生涯/合约 | 不考虑,合约 spawn 的无主 craft 不处理 | §8.3-10 |
| 对接同步 | 走 `CraftNodeRemoved` + 重发 dominant XML,**无需显式 CraftMerge 消息** | §8.1-4 |
| 朝向同步 | srfRel(相对地表),已完成 | archive/heading-sync-2026-08-17.md |
| Lobby 邀请 | **✅ 2026-09-12 翻案落地:Steam 房间列表(大厅浏览器)**——"开房可见、点列表加入";`SteamLobbyBrowser` 实现开房/列表(版本过滤)/加入(`GetLobbyOwner`→复用 `SteamTransport`)/好友邀请;手动输入 SteamId 保留于控制台 | [archive/steam-lobby-2026-09-12.md](archive/steam-lobby-2026-09-12.md) |
| MVP 范围(燃料/资源/Vizzy) | **接受不同步**(幽灵物理关,引擎视觉本来不跑) | §8.2-5 |
| Vizzy 跨 craft 数据传输(广播) + 幽灵船 Vizzy 执行 | **✅ 已实现:双 patch 主动阻止**——`BroadcastMessage`(AllCrafts→Craft) + `FlightUpdate`(幽灵船跳过),封堵 `RequestUserInput`/`SetTimeMode`/`SetCameraProperty` 等侧信道;`VizzyIsolationPatch.Enabled` 默认 `true` | [archive/vizzy-isolation-2026-08-22.md](archive/vizzy-isolation-2026-08-22.md) |
| Steam 双账号公网联机 | **✅ 已实测可行**(零 frp/零端口转发) | archive/steam §Step4 |
| TCP VM debug | **✅ 已实测可行**(`TcpHostLobby`/`TcpJoinLobby`) | archive/tcp §四 |
| 起落架等部件开关同步 | **✅ 方案 B(P0)已实测通过**(per-part `Activated` 位);分离器/整流罩/对接 **只记录不处理**(归 body-sync);降落伞走 **专用视觉驱动**(P2,未排期);**P3 控制输入应用已实现**(写幽灵 Controls + 放开输入驱动部件 Activated:rotator/舵面/活塞/螺旋桨/车轮/RCS/电机) | [archive/part-switch-sync-2026-08-18.md](archive/part-switch-sync-2026-08-18.md) §3/§4/§9/§10/§11 |
| body 级姿态同步(转轴连接部件整体移动) | **✅ 方案定稿:BodyPoses**(`BodyRotations`→相对 comRot 的位置+旋转;SP2 验证方向;**P0 已实现**);残骸小碎片位置缺口一并覆盖;不做 SP2 的 ParentBody 树/物理平滑 | [archive/body-sync-2026-08-18.md](archive/body-sync-2026-08-18.md) |
| 远程船高延迟平滑(>100ms 不卡顿) | **✅ 已实现,架构已换代**:①**速度帧修正**(发送端 `PlanetVectorToSurfaceVector` 不减行星自转 ω×r → 静止船报 158.85 m/s → 外推放大成瞬移,已修);②接收端**弃用插值缓冲**,改为 **SP2 式连续外推(dead-reckoning)**:始终取最新包 + `ext = 单向延迟(RTT/2) + 包龄`(封顶 1s、长静默冻结)+ 每帧指数平滑(`dt×50` 收敛、静止锁定、>100m 瞬移、每 body `10·dt`);调试工具(`LagSimTransport` + UI NetSim 分组 + `MP smoothing` 3s 日志)已落地;**2026-09-13 收工** | [archive/latency-smoothing-2026-08-22.md](archive/latency-smoothing-2026-08-22.md) §9(现行实现) |
| 远程船 2 阶外推(加速度/旋转速率) | 🔧 **期 1 已落地**(2026-09-14):平移加 `½·a·ext²`(ext 已×mRate → 自动 mRate²,慢放/暂停兼容)已生效;旋转按 `ω·ext` 右乘外推已实现但默认关闭(`EnableRotationExtrap=false`,待 ω 符号实测——sendDiag 自校验 `errF+/errF-/errR+` 定案);发送端 `FlightData.Acceleration/AngularVelocity` 采样 + EMA/钳制/NaN 防御 | [acceleration-smoothing-2026-09-14.md](acceleration-smoothing-2026-09-14.md) |
| 游戏 1.4.2 Experimental 兼容(P0) | 📋 **P0 已全部执行**(2026-09-13):①ModTools 程序集已刷新为 1.4.2 并重编译;②`GetComponentsInChildren` → `GetComponentsInCraft`/逐 body 迁移已落地;③1.4.2 body 脱离层级 → `ApplyRemoteBodyPoses` 已修复;仍待:游戏版本检查/实验版开关、新报「双飞静止一方抽搐」诊断实测 | [update-1.4.2-experimental-2026-09-03.md](update-1.4.2-experimental-2026-09-03.md) |
| 更新检查(ModUpdater) | **✅ 已实现并接线**(`Mod.OnModInitialized` 末尾调用):GitHub Releases API → 仓库根 `version.txt` 兜底;15s 总看门狗 + 10s 单请求超时;三按钮弹窗(Download / Later / 不再提醒) | [archive/update-reminder-port-2026-09-10.md](archive/update-reminder-port-2026-09-10.md) |
| Volken 冲突(SceneLoaded 事件链 NRE) | **✅ 根因已定位并修复**:`MultiPlayerUI.OnSceneLoaded` 空值护栏已提交(`MultiPlayerUI.cs:791`);Volken 侧自愈初始化已实施 | [archive/volken-sceneloaded-nre-2026-08-27.md](archive/volken-sceneloaded-nre-2026-08-27.md) |
| 多 craft 同步 | 📋 **方案研究,代码零实现**:仍是**每玩家一船**、`_remoteCrafts` 按 `int playerId` 索引、**无任何 `Guid`**、状态包无船标识、无残骸路径 | [multi-craft-sync-2026-08-16.md](multi-craft-sync-2026-08-16.md) §〇 |

**当前待定(尚未拍板/未调研)**:A1 方案选型(推荐 A+B 混合,待正式决策)、A2 里程碑顺序、A3 残骸同步策略、A4 观察他人第二艘船(部件开关方案 B 已于 2026-08-18 拍板,见上表;但"观察他人控制"本身待定);B1 跨机身份(Guid+InitialCraftNodeIds 溯源)、B2 对账参数、B3 轨道残骸 spawn 可行性、B4 未加载节点采样、B5 MapView 多船回归、B6 时钟对齐;D 类已决策项的实现暂缓。**SP2 式物理同步(新,2026-09-14)**:研究完成待拍板——建议 P0(每 body 速度进协议 + 游戏侧速度修复 #10)+ P1(旋转 1 阶外推)合计约 3~5 天,不做真实刚体架构(见 [physics-sync-2026-09-14.md](physics-sync-2026-09-14.md))。**游戏 1.4.2(Experimental)相关待定**(详见 [update-1.4.2-experimental-2026-09-03.md](update-1.4.2-experimental-2026-09-03.md)):新报「双飞静止一方抽搐」待实测定位、P1-1 参考系重居中叠加(PreSimulation+pendingRecenterDelta vs mod 帧补偿,双重平移风险)、P1-2 GroundedSurface×`SetPose` 接地放置、P1-3 游戏版本检查/实验版开关、P1-4 1.4.2 调试设施接入(GameLoopTypeProfiler/SetFlatDecorationCulling)——均待 1.4.2 实验版双端实测后拍板。**部件同步剩余项**(已归档 [archive/part-switch-sync-2026-08-18.md](archive/part-switch-sync-2026-08-18.md)):降落伞专用视觉驱动(P2)、`ExtensionPercent` 相位对齐(P1)、`Stage` 应用(目前只采样不应用)。**平滑剩余项**:2 阶外推——平移 `½·a·ext²` 已落地(期 1,2026-09-14),**朝向外推待 ω 符号实测**(sendDiag 自校验 `errF+/errF-/errR+` 定案后一行开启 `EnableRotationExtrap`),见 [acceleration-smoothing-2026-09-14.md](acceleration-smoothing-2026-09-14.md)。**速度修复项**:远程船游戏侧速度缺自转项,分析完成待实施([remote-craft-velocity-2026-09-13.md](remote-craft-velocity-2026-09-13.md));其修复已并入 [physics-sync-2026-09-14.md](physics-sync-2026-09-14.md) 的 P0。

---

## 三之二、当前代码里的已知问题(2026-09-14 复核)

> 这一节记录**已核实、但还没动手修**的问题,供下次开工直接取用。#1/#4 已在上次复核后解决,保留记录并标记。

| # | 问题 | 证据 | 影响 |
|---|---|---|---|
| 1 | ~~mod 版本号自相矛盾~~ **✅ 已解决**:`ModData.asset` 已更新为 1.5,与 `version.txt` 1.5 一致 | `Assets/ModData.asset:35-36` vs `version.txt` | ~~ModUpdater 每次启动弹"有新版本"~~ 不再误报 |
| 2 | **UI 图标资源路径不一致** **✅ 已解决(2026-09-14,工作区未提交)**:资源库前缀与条目已统一为 `MultiPlayer/`(`PathPrefix: MultiPlayer/`、条目 `MultiPlayer/Sprites/UIIcon`),代码请求同名完整路径 `MultiPlayer/Sprites/UIIcon`。**关键结论(反编译 `XmlLayout.dll`):`sprite` 属性经 `ToSprite → LoadResource → XmlLayoutResourceDatabase.GetResource` 按**条目路径逐字匹配(OrdinalIgnoreCase)**,运行时**不自动拼 `PathPrefix`**(该字段只是编辑器里改条目路径的工具);故路径必须等于 `entries[].path` 全串 | `MultiPlayerUI.cs:82`(工作区改动) vs `Content/XML UI/UIResourceDatabase.asset:15,21-23` | 旧值 `/Sprites/UIIcon` 匹配不到条目 ⇒ 日志 `[XmlLayout] Unable to load sprite...`,NavPanel 图标空白;现改后需游戏内验证再提交 |
| 3 | **日志前缀不统一**:`Log`/`LogError` 已改 `[MultiPlayer]`,但 `LogLobby`/`LogUpdate` 仍是 `[Mptest]` | `ModUtils.cs:21,30` vs `:39,49` | 仅可读性;按前缀过滤日志会漏 |
| 4 | ~~`FlightEnded` 订阅在空值护栏之外~~ **✅ 已修复**:`OnSceneLoaded` 已加 `inspectorPanel != null` 护栏(2026-08-27 排查,修复已提交) | `MultiPlayerUI.cs:784-797`;根因分析见 [archive/volken-sceneloaded-nre-2026-08-27.md](archive/volken-sceneloaded-nre-2026-08-27.md) | ~~NRE 中断 SceneLoaded 事件链(Volken 看不到云)~~ 已消除 |
| 5 | **TickRate 默认值自相矛盾**:`TickRate = 30` 但 `SendIntervalMs = 50f`(20Hz) | `MpNetworkManager.cs:49` vs `:45`;`SetTickRate` 同值早退 `:558` | 未调用 `SetTickRate` 前,上报频率与实际发包间隔不一致;UI 滑块初始值也对不上 |
| 6 | **插值时代死代码未清**:`TryGetInterpolatedState`、`RenderDelayMs`、`UnderrunFrames`、`SnapFrames`、`ClearBuffer`、`ReuseInterpBody*` | `MpNetworkManager.cs:1783/47/1086/1087/1176/1106` | `snap=`/`interpPct=`/`posErr=` 成为结构性常量,排查时会误导;`RenderDelayMs` 仍被赋值打印 |
| 7 | **暂停动作同步仍未实现** | `MpNetworkManager.cs` 的 `OnPause` 仍整体注释掉 | `Pause` 消息类型仍在协议里分发但无效果;**但"飞船有速度时暂停 → 观察方位置抽搐"已全部修复并收工(2026-09-13)**:发送端 `Paused` 标记 + 降速上报,接收端冻结期停止速度外推;钳制漏洞 + 冻结外推公式二修;comRot 反馈环(静止船 3cm 往复)+ 根 body rel0 对抗(12cm 往复,comPos−G);慢放锯齿(位置基运动倍率 mRate + 每包变化钳制 ±0.15,prevArrival 修复测量死代码),见 [archive/latency-smoothing §9.7~§9.16](archive/latency-smoothing-2026-08-22.md) 与 [update-1.4.2 §〇之四](update-1.4.2-experimental-2026-09-03.md);用户实测确认:慢放/暂停/切换速度模式跳动均降至可忽略;**仅"暂停状态不搬给对端"这一点保持原样** |
| 8 | **NetSim 无法包 Steam**;`NetSimDuplicate` 无 UI | `LagSimTransport.cs:63-65`;`MultiPlayerUI.cs:175-212` | 延迟模拟只能配合 TCP;重复包只能走控制台 |
| 9 | **`LiteNetLibTransport` 是死代码**(未实现 `IMpTransport`) | `LiteNetLibTransport.cs:25` | 备用传输实际不可选 |
| 10 | **远程飞船游戏侧速度缺自转项(静止船读到 ≈0)**:`ApplyRemoteGroundedSurface` 写 `GroundedSurfaceVelocity=data.Velocity`(地表相对速度),但游戏约定该字段是"地表系惯性速度"(含自转项),`CraftNode.UpdateCraft` 每帧纯旋转换算回行星空间 → `Orbit.Velocity` 缺 ω×r(测试行星 158.85 m/s);`CraftFlightData` 在游戏阶段快照该值、mod 反射刷新不覆盖速度字段、`CraftScript.FrameVelocity` 从 kinematic 刚体推导≈0 | `MpNetworkManager.cs:2606`(写) vs 反编译 `CraftNode.cs:1235-1240/1366-1371`、`CraftFlightData.cs:578-584`、`CraftScript.cs:410-441`;详见 [remote-craft-velocity-2026-09-13.md](remote-craft-velocity-2026-09-13.md) | 相对速度计算、HUD/导航球/Vizzy 读远程船速度出错(静止船读到 0,运动船缺 158.85 m/s 分量);碰撞伤害走 Unity `Collision.relativeVelocity`(读刚体速度)不受影响 |

---

## 四、文档写入规则(维护约定)

> 适用:所有 `plans/**` 下的 `.md`;`README.md` / `AGENT_CONTEXT.md` 本身也遵守 0 命名与 8 编码规则。

### 0. 命名规范
- plan 文件:`<topic>-YYYY-MM-DD.md`(kebab-case + 日期后缀;日期 = 创建/事件日期,补零,如 `volken-sceneloaded-nre-2026-08-27.md`)。
- 索引/参考文档(`README.md`、`AGENT_CONTEXT.md`)不加日期;归档文件同样遵守 `<topic>-YYYY-MM-DD.md`,置于 `archive/` 子目录。
- 单一主题一个文件;禁止中文名 / 无日期名(历史教训:`Volken-冲突排查-…NRE.md` 因不合规范而游离在索引外)。

### 1. 状态与单一事实源
- **plan 头部 blockquote 的「状态:」是唯一事实源**;README 索引行、决策速查表、待定段、AGENT_CONTEXT 里的进度/活跃列表都只是它的镜像。
- 状态词汇受控,禁止自造:📋 规划中 / 研究 / 评估中 → ✅ 已实现 / 已落地 → ✅ 已归档。同一主题同一时刻只能有一个状态。
- **改 plan 头部状态时,必须同一次改动里同步更新**:README 索引行(§一↔§二 分区移动)、决策速查表、待定段,以及 AGENT_CONTEXT 相关行。
- 教训:`update-1.4.2` 的 P0 早已执行,README 却仍写「规划中、P0 未执行」,索引与文档打架数月。

### 2. 结构模板(新建 plan 建议)
- 头部 blockquote:`状态:` / `日期:`(或 `创建日期:`) / `关联:`(相关 plan 链接 + 一句关系说明) / 一句话主题定位。
- 正文顺序:动机 → 现状 → 方案 / 决策 → 实施记录(按日期分小节)→ 剩余项 / 回归判据。
- 结论先行;被取代的方案要标注「已被取代,保留作决策记录」(如 latency-smoothing §0~§7)。

### 3. 决策记录
- 格式:「【决策:YYYY-MM-DD】」;明确结论写进对应 plan,并汇总到 README「决策速查」表(出处列 = plan 链接 + 小节号)。
- 翻案决策:保留旧记录,加「翻案说明」链接到新决策文档(如 Lobby:不做 → 2026-09-12 翻案落地)。
- 影响"已确定技术事实"的决策,还要更新 `AGENT_CONTEXT.md` §3 并引用出处。

### 4. 交叉链接
- 一律相对路径;同目录内互链用裸文件名 `x.md`。
- root 引用归档:`archive/x.md`;归档引用 root:`../x.md`;**归档内互链:裸文件名(不要加 `../`)**。
- **移动 / 归档一个文档后,必须全仓库 grep 修正所有指向它的链接**,并跑一次死链校验(见 8)。
- 站内不写本机绝对路径(一律用令牌,见 §10「隐私红线」);源码引用用工程相对路径 `Assets/Scripts/...`。

### 5. 归档流程(完成一个主题后)
1. 头部状态改「✅ 已归档(原状态:…)」;
2. 未排期剩余项 / 待复跑项:文档内写明,并同步进 README「当前待定」段;
3. `git mv` 移入 `archive/`(保留历史)→ 按 §4 修正全部链接;
4. 更新 README:从 §一 移到 §二,同步决策速查表出处列;
5. 归档 ≠ 删除:仍被索引引用,按摘要可随时找回。

### 6. 已知问题与修复记录
- 已核实未修的问题 → 登记进 README「三之二」表(问题 / 证据 / 影响;证据给 `文件:行号` + 反编译出处)。
- 修复后 → 该行标记「✅ 已解决」并保留记录,不整行删除;必要时更新证据列(如 #1/#4 的处理方式)。

### 7. 与代码同改、同提交
- 文档与对应代码改动放同一次提交;禁止"改了一半不提交"(教训:README/代码曾大量未提交,git HEAD、工作区、文档三方漂移)。
- 涉及游戏内文案、命令、路径时,同步核对 `AGENT_CONTEXT.md` 关键路径表与 §6 调试/验证段。

### 8. 编码与校验
- 一律 **UTF-8 无 BOM、行尾 LF**。
- **不要用会把非 UTF-8 字节替换成 `U+FFFD` 或加 BOM 的工具**:尤其 PowerShell 5.1 的 `Set-Content -Encoding UTF8`(加 BOM)与 `-replace`(处理含 `[`/反引号的 Markdown 链接会吃字符)。历史事故:`16eb58d` 整批文档被有损转码毁掉约 10~16% 汉字,已从父提交 `7d4925c` 恢复。
- 改完用脚本自检(三者任一为 True 即有问题):

```powershell
$p = '<文件路径>'
$b = [IO.File]::ReadAllBytes($p); $t = [Text.Encoding]::UTF8.GetString($b)
$bom = $b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF
"BOM=$bom CRLF=$($t.Contains("`r`n")) U+FFFD=$($t.Contains([char]0xFFFD))"
```

### 9. 改后自检清单
- [ ] 所有 `](...)` 站内链接可解析(相对路径无死链)
- [ ] 无 BOM / 无 CRLF / 无 U+FFFD,行尾 LF
- [ ] 头部「状态:」与 README 索引行一致(§一/§二 分区正确)
- [ ] 新决策已进 README「决策速查」表(出处带链接)
- [ ] 归档文档已从 §一 移入 §二,且全仓库无指向旧位置的链接
- [ ] 涉及代码/文案的改动已与文档同批提交
- [ ] 无本机绝对路径 / 用户名 / IP / SteamID(一律令牌化,见 §10)

### 10. 隐私红线(公开仓库强制)
- **本机绝对路径 / 用户名 / IP / SteamID 一律不进被跟踪文档**:本机路径统一用 `<TOKEN>` 引用。令牌→真实路径的映射**只存在本机被 `.gitignore` 排除的 `plans/LOCAL_PATHS.md`**(严禁提交;公开仓库不含该文件)。
- **令牌速查**(真实值见本机 `plans/LOCAL_PATHS.md`,此处只给含义,不给真实路径):

| 令牌 | 含义 |
|---|---|
| `<JNO_CODE>` | 反编译游戏源码根(只读;含 `SimpleRockets2/`、`ModApi/` 与两个 `.sln`) |
| `<MOD_API>` | ModApi 官方公共 API 源码 |
| `<SP2_MP>` | SP2(SimplePlanes 2)反编译联机参考 |
| `<LUNA_MP>` | KSP LunaMultiplayer 参考 |
| `<VOLKEN2>` | Volken2 参考 |
| `<SR2_GAME>` | 本机游戏安装目录 |
| `<USERPROFILE>` | 本机用户目录(日志等用,如 `<USERPROFILE>\AppData\LocalLow\Jundroo\SimpleRockets 2\Player.log`) |
| `<PROJECT>` | 本工程目录 |
| `<VM_IP>` | 联机测试虚拟机地址 |

- 引用反编译源码时,保留「文件名 + 行号」(如 `` `CraftNode.cs:1235-1240` ``),**不要**写本机路径形式的站外链接(在 GitHub 上是死链,且泄路径)。
- **上传前自查**(工具脚本 / CI 已不维护,靠约定 + 自查维持):全仓库搜索确认无盘符绝对路径、file URI、SteamID 类长数字、IPv4、本机链接残留;发现即改为令牌并核对 `plans/LOCAL_PATHS.md`。
