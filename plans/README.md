# 联机 Mod 设计文档索引(plans/)

> 项目:JNOmultiplayerTest(SimpleRockets 2 / JNO 联机 mod aMptest)
> **新会话先读:[`AGENT_CONTEXT.md`](AGENT_CONTEXT.md)**(项目路径 / 反编译源码 / ModApi / 已定技术事实 / 开发约定,可直接作为提示词)。
> 说明:本文档是 `plans/` 的导航页。**当前活跃文档:`multi-craft-sync-2026-08-16.md`(多 craft)、`body-sync-2026-08-18.md`(body 级姿态同步)、`part-switch-sync-2026-08-18.md`(部件开关/控制输入)、`latency-smoothing-2026-08-22.md`(远程船高延迟平滑)、`vizzy-isolation-2026-08-22.md`(Vizzy 隔离)**，其余已完成/历史文档已移入 [`archive/`](archive/)。
> 约定:新 plan 建议单一主题一个文件,写清「状态 + 决策记录」,完成后移入 `archive/` 并在此更新索引。
> **调试日志路径**:`C:\Users\usami\AppData\LocalLow\Jundroo\SimpleRockets 2\Player.log`(Unity 运行时日志;`Mod.LogLobby` / `MP smoothing` 等输出在这里)。

---

## 一、当前活跃

| 文档 | 主题 | 状态 | 一句话摘要 |
|---|---|---|---|
| [`multi-craft-sync-2026-08-16.md`](multi-craft-sync-2026-08-16.md) | **多 Craft 同步**(研究阶段) | 📋 方案研究 + 边界排查 | 多节点身份/生命周期/对接/切换/EVA/无 pod 残骸/边界情况(jnoCode 排查),含 MC1~MC4 里程碑;**body 级姿态同步已于 2026-08-18 拆分为独立 plan [`body-sync-2026-08-18.md`](body-sync-2026-08-18.md)** |
| [`body-sync-2026-08-18.md`](body-sync-2026-08-18.md) | **Body 级姿态同步**(转轴/关节连接部件"整体移动")(**P0 已实现,待游戏内实测**) | ✅ 方案已定(BodyPoses) | `BodyRotations`→`BodyPoses`(相对 comRot 的位置+旋转),采样 + 两处接收端应用(`BodyPositions` 平行列表 + `ApplyRemoteBodyPoses`);一并覆盖残骸小碎片位置缺口;**SP2 参考(可抄:body 位姿同步层次/Quaternion32/Delta 优先级;不可抄:引擎钩子/ParentBody 树/FishNet/物理平滑)**;P1~P3 可选优化 |
| [`part-switch-sync-2026-08-18.md`](part-switch-sync-2026-08-18.md) | 起落架开关等部件展开/开关状态同步(**方案 B P0 已实测通过;P3 控制输入应用已实现**) | ✅ 方案 B + P3 落地 | 同步 per-part `Part.Activated` + 幽灵本地仿真,覆盖起落架/货舱/着陆腿/太阳能/灯等;分离器/整流罩/对接**只记录不处理**(归 [`body-sync-2026-08-18.md`](body-sync-2026-08-18.md));降落伞**专用视觉驱动**(§9,P2);**输入驱动部件**(rotator/舵面/活塞/螺旋桨/车轮/RCS/电机)由 **P3 控制输入应用**(§11,写幽灵 Controls)解决;含"1000 起落架"性能分析 |
| [`latency-smoothing-2026-08-22.md`](latency-smoothing-2026-08-22.md) | 远程船**高延迟平滑**(延迟>100ms 不"一卡一卡";**分析定稿;P0+P1+自适应lookback+速度帧修正 已实现并经双端(本地VM)实测确认解决静止滑动/高延迟卡顿**;调试工具已落地) | ✅ 已实现(接收端平滑层,双端实测通过) | 根因:body 位姿不参与插值(每包整体跳)+ 缓冲欠载冻结-跳变 + 无外推 + **发送端速度帧错误(`PlanetVectorToSurfaceVector` 纯旋转不减行星自转 ω×r → 静止船上报恒定 158.85 m/s → 外推放大成数十米瞬移)**;方案:**P0** BodyPoses 插值(Slerp/Lerp)+ 欠载外推(velocity×延迟);**P1** SP2 速度自适应指数平滑 + <0.01 快照 + >阈值瞬移;P2 RTT/2+jitter 自适应 + per-body velocity + Quaternion32。**调试工具已实现**:`LagSimTransport` 延迟模拟装饰器(包 TCP,无需 Steam 好友;数值与总开关分离,`NetSimDelay/Jitter/Loss` + `NetSimOn/Off`,会话中实时生效)+ 联机 UI 的 NetSim 分组(延迟/抖动/丢包输入框 + 启用开关 + 实时状态)+ `MP smoothing` 3s 周期日志(缓冲/抖动/欠载/位置误差/漂移指标,**诊断唯一出口,已移除悬浮窗**)。SP2 参考:[`CraftStateSerializer.cs:55-94`](file:///C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/CraftStateSerializer.cs:55) 外推/平滑/瞬移、[`BodyScript.cs:660-679`](file:///C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Craft/BodyScript.cs:660) body 位姿平滑 |
| [`vizzy-isolation-2026-08-22.md`](vizzy-isolation-2026-08-22.md) | **Vizzy 联机隔离:阻止跨 Craft 数据传输 + 禁止幽灵船 Vizzy 执行** | ✅ 已实现(双 patch:`BroadcastMessage` + `FlightUpdate`,含 `Enabled` 开关) | Harmony Prefix 拦截 `BroadcastMessage`(AllCrafts→Craft) + `FlightUpdate`(幽灵船跳过),封堵 `RequestUserInput`/`SetTimeMode`/`SetCameraProperty` 等所有侧信道;`VizzyIsolationPatch.Enabled` 默认 `true`,设 `false` 恢复原生 |

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

> ✅ 归档文档已修订为**最终状态**并附「〇、经验教训」小节(作为开发过程经验存档):文档头的"状态"均为最终结论,实施步骤的复选框标记了实际落地情况。**未勾选项 = 未留档的待验证项**(如"双 Steam 账号公网实测""Lobby 邀请"),按需复跑,勿当作当前待办执行。

---

## 三、决策速查(最新决策,详见 multi-craft-sync-2026-08-16.md)

| 决策 | 结论 | 出处 |
|---|---|---|
| 跨行星联机 | **暂不做**;默认所有玩家同一行星系统(房主指定),不做生涯相关 | §8.1-1 |
| 幽灵船被 [ ] 劫持 | 用 **Harmony prefix 拦总入口** `ChangePlayersActiveCommandPodImmediate`(目标为远程幽灵时 return false) | §8.1-3 |
| 生涯/合约 | 不考虑,合约 spawn 的无主 craft 不处理 | §8.3-10 |
| 对接同步 | 走 `CraftNodeRemoved` + 重发 dominant XML,**无需显式 CraftMerge 消息** | §8.1-4 |
| 朝向同步 | srfRel(相对地表),已完成 | archive/heading-sync-2026-08-17.md |
| Lobby 邀请 | **不做**,维持"手动输入房主 SteamId" | archive/steam §Step3 |
| MVP 范围(燃料/资源/Vizzy) | **接受不同步**(幽灵物理关,引擎视觉本来不跑) | §8.2-5 |
| Vizzy 跨 craft 数据传输(广播) + 幽灵船 Vizzy 执行 | **✅ 已实现:双 patch 主动阻止**——`BroadcastMessage`(AllCrafts→Craft) + `FlightUpdate`(幽灵船跳过),封堵 `RequestUserInput`/`SetTimeMode`/`SetCameraProperty` 等侧信道;`VizzyIsolationPatch.Enabled` 默认 `true` | [vizzy-isolation-2026-08-22.md](vizzy-isolation-2026-08-22.md) |
| Steam 双账号公网联机 | **✅ 已实测可行**(零 frp/零端口转发) | archive/steam §Step4 |
| TCP VM debug | **✅ 已实测可行**(`TcpHostLobby`/`TcpJoinLobby`) | archive/tcp §四 |
| 起落架等部件开关同步 | **✅ 方案 B(P0)已实测通过**(per-part `Activated` 位);分离器/整流罩/对接 **只记录不处理**(归 [`body-sync-2026-08-18.md`](body-sync-2026-08-18.md));降落伞走 **专用视觉驱动**(P2);**P3 控制输入应用已实现**(写幽灵 Controls + 放开输入驱动部件 Activated:rotator/舵面/活塞/螺旋桨/车轮/RCS/电机) | [part-switch-sync-2026-08-18.md](part-switch-sync-2026-08-18.md) §3/§4/§9/§10/§11 |
| body 级姿态同步(转轴连接部件整体移动) | **✅ 方案定稿:BodyPoses**(`BodyRotations`→相对 comRot 的位置+旋转;SP2 验证方向;**P0 已实现**);残骸小碎片位置缺口一并覆盖;不做 SP2 的 ParentBody 树/物理平滑 | [body-sync-2026-08-18.md](body-sync-2026-08-18.md) |
| 远程船高延迟平滑(>100ms 不卡顿) | **✅ 已实现并经双端(本地VM)实测确认**:P0(BodyPoses插值+欠载外推)+P1(速度自适应指数平滑+近距快照+瞬移阈值)+自适应lookback+**速度帧修正**(发送端`PlanetVectorToSurfaceVector`不减行星自转ω×r→静止船报158.85m/s→外推放大成瞬移,已修复);调试工具(`LagSimTransport`+UI+`MP smoothing`周期日志)已落地 | [latency-smoothing-2026-08-22.md](latency-smoothing-2026-08-22.md) |

**当前待定(尚未拍板/未调研)**:A1 方案选型(推荐 A+B 混合,待正式决策)、A2 里程碑顺序、A3 残骸同步策略、A4 观察他人第二艘船(部件开关方案 B 已于 2026-08-18 拍板,见上表;但"观察他人控制"本身待定);B1 跨机身份(Guid+InitialCraftNodeIds 溯源)、B2 对账参数、B3 轨道残骸 spawn 可行性、B4 未加载节点采样、B5 MapView 多船回归、B6 时钟对齐;D 类已决策项的实现暂缓。

---

## 四、维护约定

0. **文件命名规范**:所有 plan 文件采用 `<topic>-YYYY-MM-DD.md` 格式(kebab-case + 日期后缀)。`README.md` 和 `AGENT_CONTEXT.md` 为索引/参考文档,不加日期。归档文件同样遵守此规范,置于 `archive/` 子目录。
1. 新增/修改 plan:在文档头写清「状态:规划中 / 已落地 / 已归档」;
2. 完成一个主题后移到 `archive/`,并同步更新本索引;
3. 有明确结论时直接写进对应 plan(加「【决策:…】」标记),并汇总到上表。

(End of file - total 70 lines)