# 联机 Mod 设计文档索引(plans/)

> 项目:JNOmultiplayerTest(SimpleRockets 2 / JNO 联机 mod aMptest)
> **新会话先读:[`AGENT_CONTEXT.md`](AGENT_CONTEXT.md)**(项目路径 / 反编译源码 / ModApi / 已定技术事实 / 开发约定,可直接作为提示词)。
> 说明:本文档是 `plans/` 的导航页。**当前活跃文档只有 [`multi-craft-sync.md`](multi-craft-sync.md)**,其余已完成/历史文档已移入 [`archive/`](archive/)。
> 约定:新 plan 建议单一主题一个文件,写清「状态 + 决策记录」,完成后移入 `archive/` 并在此更新索引。

---

## 一、当前活跃

| 文档 | 主题 | 状态 | 一句话摘要 |
|---|---|---|---|
| [`multi-craft-sync.md`](multi-craft-sync.md) | **多 Craft 同步**(研究阶段) | 📋 方案研究 + 边界排查 | 多节点身份/生命周期/对接/切换/EVA/无 pod 残骸/边界情况(jnoCode 排查),含 MC1~MC4 里程碑。主活跃文档 |
| [`part-switch-sync-feasibility.md`](part-switch-sync-feasibility.md) | 起落架开关等部件展开/开关状态同步(**已确认方案 B,待实施**) | 📋 方案研究 | 同步 per-part `Part.Activated` + 幽灵本地仿真,覆盖起落架/货舱/着陆腿/太阳能/灯等;分离器/整流罩/对接**只记录不处理**(归 body 同步);降落伞**专用视觉驱动**(§9 反编译定原理);含"1000 起落架"性能分析 |

> `multi-craft-sync.md` 承接了归档文档里遗留的"下一步"项(如 body 同步、平滑插帧),后续以它为准。
> `part-switch-sync-feasibility.md` 为"部件开关/展开状态"的补充分析(2026-08 新增,**已确认方案 B,待实施**)。

---

## 二、已归档(历史 / 已完成)

| 文档 | 主题 | 状态 | 一句话摘要 |
|---|---|---|---|
| [`archive/mp-heading-sync.md`](archive/mp-heading-sync.md) | 朝向同步(srfRel) | ✅ 已完成并双端实测通过 | 相对地表朝向同步的最终方案;当前实现在 `MpNetworkManager` + `recdata.SrfRel` |
| [`archive/replay-to-multiplayer-feasibility.md`](archive/replay-to-multiplayer-feasibility.md) | Replay→联机可行性(历史) | 📋 历史分析,大部分已落地 | 联机基础能力/架构/选型的早期论证;其中的"下一步重心"已被 multi-craft-sync 继承 |
| [`archive/steam-multiplayer-integration.md`](archive/steam-multiplayer-integration.md) | 传输层:Steam P2P | ✅ 已落地 | SteamTransport 已实现并设为默认(`MpNetworkManager.Transport`) |
| [`archive/tcp-transport-for-vm-debug.md`](archive/tcp-transport-for-vm-debug.md) | 传输层:TCP(VM debug) | ✅ 已落地 | `IMpTransport` + `TcpTransport` + `TcpHostLobby`/`TcpJoinLobby` 命令已实现 |
| [`archive/PLAN_AsyncPrefabPreload.md`](archive/PLAN_AsyncPrefabPreload.md) | 异步 prefab 预加载(消除加入白屏) | ✅ 已实现(MSBuild exit 0;游戏内实测待复跑) | `MpCraftPreloader` 协程预热主 prefab + 真实百分比旋转白框 + 玩家列表 "⏳ N%";见该文档「〇、经验教训」 |
| [`archive/engine-fx-sync-feasibility.md`](archive/engine-fx-sync-feasibility.md) | 幽灵引擎尾焰/烟雾/过膨胀同步 | ✅ 已实现并实测通过 | 尾焰(液体+航发两段加力 Route A/B)、烟雾(`InjectGhostMotion` 速度注入)、过膨胀(`ExpansionRatio` 双保险);含 kinematic 写 velocity 告警刷屏修法(§10.3.1),详见「〇、经验教训」 |

> ✅ 归档文档已修订为**最终状态**并附「〇、经验教训」小节(作为开发过程经验存档):文档头的"状态"均为最终结论,实施步骤的复选框标记了实际落地情况。**未勾选项 = 未留档的待验证项**(如"双 Steam 账号公网实测""Lobby 邀请"),按需复跑,勿当作当前待办执行。

---

## 三、决策速查(最新决策,详见 multi-craft-sync.md)

| 决策 | 结论 | 出处 |
|---|---|---|
| 跨行星联机 | **暂不做**;默认所有玩家同一行星系统(房主指定),不做生涯相关 | §8.1-1 |
| 幽灵船被 [ ] 劫持 | 用 **Harmony prefix 拦总入口** `ChangePlayersActiveCommandPodImmediate`(目标为远程幽灵时 return false) | §8.1-3 |
| 生涯/合约 | 不考虑,合约 spawn 的无主 craft 不处理 | §8.3-10 |
| 对接同步 | 走 `CraftNodeRemoved` + 重发 dominant XML,**无需显式 CraftMerge 消息** | §8.1-4 |
| 朝向同步 | srfRel(相对地表),已完成 | archive/mp-heading-sync.md |
| Lobby 邀请 | **不做**,维持"手动输入房主 SteamId" | archive/steam §Step3 |
| MVP 范围(燃料/资源/Vizzy) | **接受不同步**(幽灵物理关,引擎视觉本来不跑) | §8.2-5 |
| Steam 双账号公网联机 | **✅ 已实测可行**(零 frp/零端口转发) | archive/steam §Step4 |
| TCP VM debug | **✅ 已实测可行**(`TcpHostLobby`/`TcpJoinLobby`) | archive/tcp §四 |
| 起落架等部件开关同步 | **✅ 拍板:方案 B(per-part `Activated` 位)**;分离器/整流罩/对接 **只记录不处理**(归 body 同步);降落伞走 **专用视觉驱动**(反编译已定原理) | [part-switch-sync-feasibility.md](part-switch-sync-feasibility.md) §3/§4/§9 |

**当前待定(尚未拍板/未调研)**:A1 方案选型(A+B 混合?)、A2 里程碑顺序、A3 残骸同步策略、A4 观察他人第二艘船(部件开关同步方案 A/B 已于 2026-08-18 拍板,见上表);B1 跨机身份(Guid+InitialCraftNodeIds 溯源)、B2 对账参数、B3 轨道残骸 spawn 可行性、B4 未加载节点采样、B5 MapView 多船回归、B6 时钟对齐;D 类已决策项的实现暂缓。

---

## 四、维护约定

1. 新增/修改 plan:在文档头写清「状态:规划中 / 已落地 / 已归档」;
2. 完成一个主题后移到 `archive/`,并同步更新本索引;
3. 有明确结论时直接写进对应 plan(加「【决策:…】」标记),并汇总到上表。
