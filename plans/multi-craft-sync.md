# 多 Craft 同步方案(分析文档)

> 项目:JNOmultiplayerTest(SimpleRockets 2 / JNO 联机 mod aMptest)
> 反编译参考:`C:/renko/shitProgram/jnoCode`
> KSP 参考:`C:/renko/unityProjects/LunaMultiplayer`
> 状态:📋 方案研究阶段(已整理候选方案,待定具体实现与难度评估)
> 定位:**当前唯一活跃 plan**(索引见 [`README.md`](README.md));已完成/历史文档见 `archive/`

---

## 一、当前项目现状摘要(多 craft 相关)

核心同步链路集中在 [`MpNetworkManager.cs`](../Assets/Scripts/Net/MpNetworkManager.cs) + [`MpMessage.cs`](../Assets/Scripts/Net/MpMessage.cs)。

已落地且扎实的部分:
- 飞船交换:SP2 风格 hash 按需下载 XML(gzip + MD5 缓存去重,`CraftXmlRequest/Response`);
- 状态同步:`recdata`(位置/速度/Heading/SrfRel 朝向 + 控制 + BodyRotations),20~30Hz,带时间戳环形缓冲 + 100~150ms 渲染延迟插值;
- 朝向同步:已按 LunaMultiplayer `srfRelRotation` 思路落地(相对地表朝向,见 [`archive/mp-heading-sync.md`](archive/mp-heading-sync.md)),并解决游戏覆盖朝向问题(LateUpdate 写回、物理禁用走 `Warp` 原因避免 MapView NRE);
- 远程飞船为"幻影模式":`AllowPlayerControl=false` + 物理禁用 + kinematic + 反射写 `GroundedSurface*` / FlightData。

**多 craft 的关键缺口(都指向同一个根因)**:

| # | 现状 | 后果 |
|---|---|---|
| 1 | `_remoteCrafts` 是 `Dictionary<int, RemoteCraft>`,按 playerId 键控 | 每玩家只能有 1 艘远程飞船 |
| 2 | `MpPeer` 只有单个 `NodeId` + 单个 `CraftXml` | 每玩家只能上报 1 艘 |
| 3 | 采样/取 XML 只读 `FlightSceneScript.Instance.CraftNode`(本机唯一"玩家飞船") | debris、分离件完全不入流 |
| 4 | 从未订阅 `FlightState.CraftNodeAdded / CraftNodeRemoved`(仅两处 for 循环诊断) | 游戏自己分裂出的新节点永远发现不了,也不会清理 |

> ⚠️ **关键事实**:游戏的 `NodeId` 是**每机自增、分裂时重新分配**(`FlightState.AddCraft` → `GetNextNodeId`),**跨机不可作唯一键**。当前用 playerId 当键正是这个原因的产物。

---

## 二、多 craft 同步的实质

多 craft 不是"多一个映射",而是:**同步整个 `FlightState.CraftNodes` 集合**(玩家飞船 + 分离/残骸/对接产生的所有节点)。每节点要解决四件事:

1. **身份**:跨机唯一、分裂/合并后仍稳定的键;
2. **生命周期**:新增/分离/合并/销毁的事件发现与广播;
3. **内容**:每节点的状态包(位置/朝向/body/情形);
4. **频率**:按"活动/次要"分级发包,防带宽爆炸。

### 游戏原生多节点能力(反编译确认)

- [`FlightState.CraftNodes`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/State/FlightState.cs:120) 是 `IReadOnlyList<CraftNode>`,游戏本身就管理多艘飞船(残骸、对接、多节点);
- `FlightState.AddCraft(CraftNode, originalNode)`([`FlightState.cs:320`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/State/FlightState.cs:320)):自动分配 NodeId、触发 `CraftNodeAdded`;
- 运行时分裂由 [`CraftSplitter.SplitCraftNode`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/Sim/CraftSplitter.cs:105) 完成:新 CraftNode → `AddCraft` → 触发 `CraftNodeAdded`,即**分离/残骸天然走同一事件通道**;
- `CraftNode.DestroyCraft()`([`CraftNode.cs:578`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/Sim/CraftNode.cs:578)):置 `IsDestroyed` → 游戏 `ProcessDestroyedCraftNodes()` 下一帧移除并触发 `CraftNodeRemoved`;
- `FlightState.LoadCraftXml(nodeId)` 可拿任意节点的 XML(分裂节点经 `CraftNodeDataDynamic` 也会入库,可取到);
- `FlightSceneScript.SpawnCraft(...)` + `ICraftLoader.LoadCraftImmediate(...)` 是生成远程飞船的入口(已在 M2 使用)。

---

## 三、候选方案对比

### 方案 A:事件驱动 + 每机全量上报(当前风格的最小扩展)

- 每客户端在进入飞行场景、以及收到 `CraftNodeAdded` 时,把它**所有** `FlightState.CraftNodes`(不只活动节点)上报:
  - 给每个本地节点分配一个 **mod 自造的全局 `Guid`**(Luna 的 VesselId 同款),本地维护 `Dictionary<int, Guid>` nodeId→Guid,发现即分配;
  - 上报 `Guid + xmlHash`;房主广播 `CraftInfo`;
- 状态包由 `(playerId, nodeId)` 改为 `(ownerId, craftGuid, ...)`,`_remoteCrafts` 改为 `Dictionary<Guid, RemoteCraft>`;
- `CraftNodeRemoved` / `DestroyCraft` → 广播 `CraftRemove(Guid)`,接收端 `DestroyCraft()`(现有 [`RemoveRemoteCraft`](../Assets/Scripts/Net/MpNetworkManager.cs:1080) 逻辑直接复用)。

| 优点 | 缺点 |
|---|---|
| 改动小,与现有 Hello/Welcome/PlayerJoin 流程同构 | **靠可靠消息兜底**:丢一条消息就少一艘船(公网 UDP/Steam 下需 ack 重发,现有 CraftData 重发模式可抄) |

### 方案 B:注册表对账(LunaMultiplayer 模型,推荐)

照搬 Luna 的 `VesselSyncSystem` + `VesselProtoSystem` 双层:

- **快路径(事件)**:同方案 A,新增/分裂/合并立即广播;
- **慢路径(对账)**:每 ~10s 客户端把"我拥有的 craft Guid 列表(+hash)"发给房主;房主持有**全局注册表**,diff 后把"你缺的船 / 该删的船"回给每个客户端,客户端按 hash 走现有的按需下载补齐 XML;
- **自愈**:任何丢包/时序错乱在下一个对账周期内被修正;玩家中途加入也天然覆盖(注册表全量下发),不用像现在这样在 `OnHello` 里逐个补发。

| 优点 | 缺点 |
|---|---|
| 健壮、可自愈、天然支持加入/离开/分裂/合并 | 多一套注册表逻辑 |

> LunaMultiplayer 事实就是这个模型。其 [`VesselLoader.LoadVessel`](../C:/renko/unityProjects/LunaMultiplayer/LmpClient/VesselUtilities/VesselLoader.cs:44) 还做了"结构没变就 early-out、变了才毁掉重建"的增量加载——JNO 的幽灵船模式更省(不用重建,物理本来就不开)。

### 方案 C:整态快照对账(仅作修复通道)

周期序列化整个 FlightState 的全部节点状态(位置 + XML)一次性下发,只用于"检测到分歧时修复",不作为实时同步通道。

| 优点 | 缺点 |
|---|---|
| 实现最直接、能强收敛 | 带宽大;只适合做 B 的补充(如长连接恢复后强对账),不建议当主通道 |

### 结论

**推荐:A 快路径 + B 慢对账 混合**(Luna 的成熟做法),C 视情况做修复用。

---

## 四、落地设计点(对照 LunaMultiplayer 具体文件)

| 设计点 | 做法 | LunaMultiplayer 对应实现 |
|---|---|---|
| **全局身份** | mod 自造 `Guid`(本地 `Dictionary<int, Guid>` nodeId→Guid,发现即分配),状态包带 `(ownerId, craftGuid)`;本地 NodeId 仍用于 `FlightState.LoadCraftXml(nodeId)` 取 XML | `VesselPositionUpdate.VesselId`(Guid)、`CurrentVesselUpdate: ConcurrentDictionary<Guid, ...>` |
| **生命周期钩子** | 订阅 `FlightState.CraftNodeAdded/Removed`;合并用 `CraftSplitter.MergeCraftNode` 钩子(广播 `CraftMerge(keepGuid, removeGuid)`,接收端删被并掉的船);分裂由 `SplitCraftNode → AddCraft → CraftNodeAdded` 自动触发,天然进上报路径 | `VesselProtoEvents`(PartCoupled/PartDecoupled)、`VesselCoupleSys/VesselDecoupleSys`、`VesselRemoveSystem` |
| **每船内容** | 复用 `recdata`,补 `CraftGuid` + `Situation`(地面/轨道)+ `IsDebris/HasCommandPod`;轨道残骸补 `Parent` 行星名(接收端在同行星 spawn,呼应"同一行星系统"约束)。残骸共享设计 hash → 现有 `_xmlCache` 可天然去重 | `VesselPositionMsgData`:lat/lon/alt + velocity + 8 元素 orbit + normal + BodyName |
| **频率分级(LOD)** | 活动船 20~30Hz;**次要/残骸降到 1~5Hz**;且当"附近有其他玩家船"(`PlayerVesselsNearby`)才快发,否则慢发——防残骸一多带宽爆炸 | `SendVesselPositionUpdates` vs `SendSecondaryVesselPositionUpdates` + `TimeToSendVesselUpdate` |

**可直接复用、只换键的现有资产**:
- 幽灵船初始化 [`InitializeRemoteCraft`](../Assets/Scripts/Net/MpNetworkManager.cs:1233)(物理禁用走 Warp 原因);
- 插值缓冲 [`UpdateRemoteCrafts`](../Assets/Scripts/Net/MpNetworkManager.cs:1290) / `TryGetInterpolatedState`;
- 朝向 srfRel 链路(发送端 `TrySampleLocalCraft` / 接收端 `ApplyRemoteState` + `LateUpdate` 写回);
- 移除清理 [`RemoveRemoteCraft`](../Assets/Scripts/Net/MpNetworkManager.cs:1080)(`DestroyCraft()`);
- 按需下载 + hash 去重([`MpMessage.cs`](../Assets/Scripts/Net/MpMessage.cs) `CraftXmlRequest/Response` + `_xmlCache`)。

**需要新增/改造**:
- 消息类型:`CraftInfo(guid, ownerId, xmlHash)`、`CraftRemove(guid)`、`CraftMerge(keepGuid, removeGuid)`、`CraftRegistrySync`(对账用);
- 状态包加 `CraftGuid`;`MpPeer` 升级为"每玩家 N 艘"(或拆出独立的 craft 表);
- 生成侧 spawn 队列限速(现有 2s 重试节流思路扩展),防大量残骸一次性 `SpawnCraft` 白屏。

---

## 五、风险与开放问题

**风险**:
1. **多船 XML 下发量**:按需下载 + hash 去重已缓解;建议只对"有命令舱/有意义的船"全量 spawn,纯小残骸可只做简化指示物(或不 spawn,仅同步活动船 + 玩家残骸)。
2. **每船每 tick 序列化开销**:靠分级频率控住。
3. **轨道残骸**:需从"地面坐标"扩展到"轨道参数"(lat/lon/alt + velocity 已够,MVP 先锁 1x 实时,不做 warp)。
4. **地图/残骸可视**:Luna 有专门的 `VesselRemoveSystem`(2.5s 清理 kill list)防"已删但还在渲染";JNO 侧已用 `DestroyCraft()` 真销毁,需在多船场景下回归验证 MapView。

**开放问题(待研究确认)**:
- 跨机稳定身份:用 mod 自造 `Guid` 是否足够?分裂/合并时如何保持"同一艘船"的语义(是否结合 `CraftNode.InitialCraftNodeIds` 溯源)?
- 对账周期与消息量:10s 对账 + 2.5s 定义广播(Luna 参数)对 JNO 的 Steam/TCP 通道是否合适?
- 残骸策略:哪些节点值得同步成完整幽灵船,哪些跳过(按是否 `HasCommandPod` / 部件数 / 距玩家距离)?
- 玩家"换船控制"(JNO 单活动节点):多人各控各的船时,`FlightSceneScript.Instance.CraftNode` 只代表本机活动船,是否需要支持"观察他人控制的第二艘船"?

---

## 六、实施里程碑(建议,待定)

### MC1 · 全局身份 + 全量上报(A 基础)
- [ ] `Dictionary<int, Guid>` 本地 nodeId→Guid;订阅 `CraftNodeAdded/Removed`;
- [ ] `CraftInfo` / `CraftRemove` 消息;`_remoteCrafts` 改 `Dictionary<Guid, RemoteCraft>`;
- [ ] `TrySampleLocalCraft` / `RefreshLocalCraft` 遍历 `FlightState.CraftNodes` 上报全部节点。

### MC2 · 每船状态 + 分级频率
- [ ] 状态包加 `CraftGuid` + `Situation` + `IsDebris`;每船插值缓冲按 Guid 索引;
- [ ] 活动船快发、残骸慢发(1~5Hz);附近有玩家船才快发。

### MC3 · 注册表对账(B)
- [ ] 房主持有全局注册表;客户端周期上报 Guid 列表;diff 补齐/清理;
- [ ] 合并(对接)消息 `CraftMerge`;`CraftSplitter.MergeCraftNode` 钩子。

### MC4 · 打磨
- [ ] spawn 队列限速;多船回归测试(残骸/分离/对接/玩家加入离开);
- [ ] 残骸显示策略与带宽统计。

---

## 七、切换 / 对接(Luna 做法)+ JNO 控制单元切换 / Drood EVA 研究

> 本节补充两个与"多 craft"直接相关的场景:① 玩家切换控制单元/控制节点;② 对接与分离(含 JNO 的 Drood EVA)。Luna 的做法作为参照,最后落到 JNO 的对应钩子。

### 7.1 前置:身份与锁模型(Luna)

- 每艘船全局唯一 `VesselId`(Guid);**对接后 dominant 船保 Guid、weak 船 Guid 删除**;分离产生的新船 Guid 由"分离发起方"生成并广播,**接收端强制覆盖本地自动生成的 id**——这是跨机一致性的核心约定。
- 权限靠锁(服务端 `LockSystem` 裁决,客户端 `LockSystem` 收发 `LockAcquireMsg`):
  - **Control Lock**(控制锁):一艘船同时 1 把、一个玩家同时 1 把(服务端会清多余);
  - **Update Lock / UnloadedUpdate Lock**(更新锁):拥有者负责发状态、其他人只接收;
  - **Spectator Lock**(观察锁):观战者。

### 7.2 Luna 切换 craft(控制权交接)

Luna 不自己实现切换动作,而是挂钩 KSP `onVesselChange`(玩家按 `[`/`]` 或地图切换活动船时触发),在 [`VesselLockEvents.OnVesselChange`](../C:/renko/unityProjects/LunaMultiplayer/LmpClient/Systems/VesselLockSys/VesselLockEvents.cs:14) 里做权限交接:

1. 目标船控制锁本来就是我的(如重载自己)→ 直接跳过,不释放锁;
2. 否则**先释放我全部 Control Lock**(一人只能控一艘);
3. 看目标船控制锁归属:
   - **别人的船** → [`StartSpectating`](../C:/renko/unityProjects/LunaMultiplayer/LmpClient/Systems/VesselLockSys/VesselLockSystem.cs:94):`IsSpectating=true` + `InputLockManager.SetControlLock(BlockAllControls)` 锁死操作 + 拿 Spectator Lock + 释放我的 Control/Update/Kerbal/UnloadedUpdate 锁;
   - **无人控制** → `AcquireControlLock(vesselId)` 向服务端申请。

拿锁成功([`LockAcquire`](../C:/renko/unityProjects/LunaMultiplayer/LmpClient/Systems/VesselLockSys/VesselLockEvents.cs:90)):
- **我拿到锁**:把该船从"接收同步系统"移除(本地权威控制,停止应用别人状态包);**清零油门**(`ctrlState.mainThrottle = 0f`,防继承上个控制者最后的油门);补拿 Update/UnloadedUpdate/Kerbal 锁;若在观战则停止。
- **别人拿到锁**:如果抢的是我活动船 → 我转观战;我的 Update Lock 降级成 UnloadedUpdate Lock。

切换即时性:[`PositionEvents.OnVesselSwitching`](../C:/renko/unityProjects/LunaMultiplayer/LmpClient/Systems/VesselPositionSys/PositionEvents.cs:19)(KSP `onVesselSwitching`)→ **立即广播新船位置(带 orbit/body),不等定时发包**。

兜底:[`VesselSwitcherSystem.SwitchToVessel`](../C:/renko/unityProjects/LunaMultiplayer/LmpClient/Systems/VesselSwitcherSys/VesselSwitcherSystem.cs:36) 协程等飞船加载(100 FixedUpdate → 1s → 强制 `Load()`)再 `ForceSetActiveVessel`;杀活动船时切到最近可用船(`VesselRemoveSystem.SwitchVesselIfKillingActiveVessel`)。

### 7.3 Luna 对接(Docking)/分离(Decouple/Undock)

**对接**——KSP 事件 `onPartCoupling`(开始)+ `onPartCoupled`(完成,带 `removedVesselId`),注意注释:**"couple 事件由 WEAK 船触发"**(被吸收的那艘)。

发起端 [`VesselCoupleEvents.CoupleComplete`](../C:/renko/unityProjects/LunaMultiplayer/LmpClient/Systems/VesselCoupleSys/VesselCoupleEvents.cs:25):
1. 判断 trigger(DockingNode / GrappleNode / Kerbal / Other);
2. 发 `VesselCouple` 消息:`(dominantVesselId, weakVesselId, 两船对接 partFlightId, trigger)`——**只同步事件语义,不传船体**;
3. `SendVesselRemove(weakVesselId)` + `DelayedKillVessel(weakVesselId, …, 500ms)`(延迟杀 weak,先让对接本地完成);
4. `ReleaseAllVesselLocks(weakVesselId)`;对方在更未来时间线则 `WarpIfSubspaceIsMoreAdvanced`(时间同步)。

接收端 [`VesselCoupleMessageHandler`](../C:/renko/unityProjects/LunaMultiplayer/LmpClient/Systems/VesselCoupleSys/VesselCoupleMessageHandler.cs:16) + [`VesselCouple.ProcessCouple`](../C:/renko/unityProjects/LunaMultiplayer/LmpClient/Systems/VesselCoupleSys/VesselCouple.cs:65):
1. 按 `VesselId` 排队、按 `GameTime` 排序(消息时间 > 本地 `UniversalTime` 入队等时间到;队头时间更新 → 玩家回退,清空队列);
2. `FindVessel` 两船(含我活动船则强制 `Load()`),按 partFlightId 找两零件;
3. **调用 KSP 真实对接 API 把 weak 接上 dominant**:DockingNode→`DockToVessel`;GrappleNode→反射 `Grapple`;Kerbal→上座;Other→`Couple(...)`;用 `IgnoreEvents=true` 包住防递归;
4. `AfterCouplingEvent`:我活动船是 weak → `ForceSetActiveVessel(dominant)`;是 dominant → `MakeActive()`;
5. 失败兜底:`coupleResult==false` → `KillVessel(weakVesselId)`。

合并后 dominant 的新结构:[`VesselProtoEvents.PartCoupled`](../C:/renko/unityProjects/LunaMultiplayer/LmpClient/Systems/VesselProtoSys/VesselProtoEvents.cs:152) → `SendVesselMessage(dominant)` 重发合并后的完整 proto;`LocalTopologyTracker.RecordMutation` 拉黑两船 id,防止过期旧 proto 把已删 weak 船"复活"。

**分离**:
- Decouple(分离器/分级)[`VesselDecoupleEvents.DecoupleComplete`](../C:/renko/unityProjects/LunaMultiplayer/LmpClient/Systems/VesselDecoupleSys/VesselDecoupleEvents.cs:19):分离完成 → 立即 `SendVesselPositionUpdate(新船,true)` + 发 `VesselDecouple(原船, partFlightId, breakForce, NewVesselId)`;接收端 [`VesselDecouple.ProcessDecouple`](../C:/renko/unityProjects/LunaMultiplayer/LmpClient/Systems/VesselDecoupleSys/VesselDecouple.cs:24) 本地 `decouple(BreakForce)`,然后 **`partRef.vessel.id = NewVesselId` 强制覆盖本地自动分配的 id** → 全局 Guid 唯一且跨机一致,再 ForceUpdate 位置。
- Undock(对接解除):新分离段作为新船发 proto([`VesselProtoEvents.PartUndocked`](../C:/renko/unityProjects/LunaMultiplayer/LmpClient/Systems/VesselProtoSys/VesselProtoEvents.cs:120))。

### 7.4 JNO 同一 craft 内切换控制单元(ActiveCommandPod)

**机制**(反编译):
- API:[`CraftScript.SetActiveCommandPod(pod)`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/CraftScript.cs:1419) + 事件 `ActiveCommandPodChanging/Changed`;统一入口 [`FlightSceneScript.ChangePlayersActiveCommandPodImmediate(pod, node, ignoreDistance)`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/FlightSceneScript.cs:361)(清零控制、重定相机、`SetActiveCommandPod`、必要时 `SetCraftNode`、`AllowPlayerControl=true`)。
- 触发点:`CommandPodScript.SetActiveCommandPod()`(部件菜单"Take control",[`CommandPodScript.cs:789`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/CommandPodScript.cs:789))、`EvaScript.TakeControl`、地图 inspector(`SelectedModel.cs:482`)、CockpitScript。
- **关键(对同步的影响)**:[`CraftScript` 每帧 `_centerOfMassTransform.rotation = ActiveCommandPod.PilotSeatOrientation.rotation`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/CraftScript.cs:2046)。而 mod 采样朝向就是 `CenterOfMass.rotation`(→SrfRel)。**→ 同一 craft 内切换控制单元,朝向/控制自动跟随现有 srfRel 同步,无需新增包**。`Controls`(Pitch/Yaw/Roll/Throttle)也来自 `ActiveCommandPod` → 已在状态包。
- `Data.ActiveCommandPodId` 持久化(可能进 XML)→ 切换后 XML hash 变化 → 触发按需 re-download(良性,不重建已存在的幽灵船)。
- 可选增强:接收端如需 staging/相机/地图一致,可广播 `ActiveCommandPodChanged(partId)` 让远端 `SetActiveCommandPod`;当前 FlightData 已用反射刷新(见 `ApplyRemoteState` ⑤)。

### 7.5 JNO Drood EVA(关键发现:是独立 CraftNode)

- **Drood 是带 `EvaScript` 的 CommandPod 部件**(`IsEva = EvaScript != null`),坐在 `CrewCompartmentScript` 里,不是独立实体。
- **出舱 EVA**([`CrewCompartmentScript.UnloadCrewMember`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/Eva/CrewCompartmentScript.cs:361) → `EvaScript.TakeControl` → [`UnloadFromCrewCompartment`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/Eva/EvaScript.cs:2259) 摧毁物理关节 → body 断开):
  - 断开 body → `CraftSplitter.ProcessDisconnectedBody` → `MoveBodyToNewCraft` / [`SplitCraftNode`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/Sim/CraftSplitter.cs:105) → **生成新的独立 CraftNode**(新 NodeId;`EvaScript.OnMovedToNewCraft` 更新 `CrewMember.NodeId = newCraft.NodeId`,[`EvaScript.cs:2037`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/Eva/EvaScript.cs:2037));
  - `SwitchToCommandPod` → `ChangePlayersActiveCommandPodImmediate` 接管新 EVA 节点([`EvaScript.cs:2249`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/Eva/EvaScript.cs:2249))。
- **回舱**(`LoadIntoCrewCompartment` → 部件重新连接)→ **合并回原 craft**(`CraftSplitter.MergeCraftNode`)。
- **结论:JNO 原生"多 craft"场景之一——出舱=split/新节点,回舱=merge。** 当前 mod 只上报 `FlightSceneScript.Instance.CraftNode`,远端永远看不到 EVA 的 Drood。
- 且玩家切到 EVA 节点后 `FlightSceneScript.Instance.CraftNode` 会变 → 现 mod 的 `LocalNodeId`/`_localCraftXml` 仍是旧节点,但 `TrySampleLocalCraft` 每帧采样当前节点 → **状态包用旧 NodeId + 新节点状态,远端错位**。

### 7.6 对 JNO 多 craft 方案的增量影响(结论表)

| Luna 机制 | JNO 对应钩子 | 借鉴做法 |
|---|---|---|
| **新船 Guid 发起方生成 + 接收端覆盖本地 id**(decouple 的 `vessel.id = NewVesselId`) | 分裂走 `SplitCraftNode → AddCraft → CraftNodeAdded`(NodeId 每机自增) | 本机 `CraftNodeAdded` 时分配 `Guid` 广播 `CraftInfo(guid,…)`;接收端 spawn 后用它取代本机 NodeId 作键 |
| **对接=事件语义(dominant/weak+零件),不传船体;延迟杀 weak** | `CraftSplitter.MergeCraftNode(source, target)` | 发 `CraftMerge(keepGuid, removeGuid)`,接收端删被吸收幽灵船、保留 dominant;合并后重发 dominant XML |
| **Dominant 重发 proto + RecordMutation 防复活** | 合并后 `LoadCraftXml(nodeId)` 可取新 XML | 合并后重发 dominant XML(hash 变化触发按需下载);记住 removeGuid 一段时间,忽略其后续状态包 |
| **切换即广播 + 清零控制** | `ChangePlayersActiveCommandPodImmediate` 可切 `SetCraftNode`(同 craft 换 pod / 切到另一节点) | 同 craft 换 pod:朝向/控制自动跟随 srfRel,无需新包;切到另一节点(如 EVA Drood)→ **必须 `RefreshLocalCraft()` 更新 LocalNodeId+XML 并立即发一包** |
| **Lock 裁决切换权** | 无锁系统,self-authoritative | MVP 不需要;做"多玩家抢船/观战"才引入服务端裁决 |
| **GameTime 排队 + 未来 subspace 延迟处理** | 状态包已带 `FlightState.Time` | 对接/分离事件带时间戳入队,到时间再执行 |
| **Drood 出舱/回舱** | 出舱=`CraftNodeAdded`;回舱=`MergeCraftNode` | 天然被 MC1 生命周期钩子覆盖;EVA 节点单部件可降频或跳过,但玩家 EVA 时是活动节点应同步 |

**一句话总结**:Luna——切换走"控制权交接(锁)+ 拿锁即停收该船状态 + 立即广播新船位置";对接走"事件语义(dominant/weak+零件)+ 接收端真实 API 复现 + 延迟杀 weak + dominant 重发 proto";多船身份统一靠"发起方定 Guid、接收端覆盖本地 id"。JNO 侧对应钩子为 `CraftNodeAdded/Removed`(出舱/分离)+ `CraftSplitter.MergeCraftNode`(回舱/对接)+ `ChangePlayersActiveCommandPodImmediate`(换 pod / 换节点,后者需 `RefreshLocalCraft`)。

### 7.7 分离出"没有 CommandPod 的 craft"的处理(结论)

**前提**:JNO 的分离有两条路径([`CraftSplitter.ProcessDisconnectedBody`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/Sim/CraftSplitter.cs:80) → [`DetermineCraftNodeEligibility`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/Sim/CraftSplitter.cs:193)):

| 条件(任一满足) | 结果 | MP 处理 |
|---|---|---|
| 含 `PreventDebris` 部件,或包围盒任轴 > 10m | 生成**新 CraftNode**(`SplitCraftNode`,HasCommandPod 可能为 false;无 pod 时取第一个 part 作 root) | **当独立 craft 同步**(见下) |
| 都不满足(小碎片) | 留在原 craft 内,body 标记 `IsDebris=true`、`CommandPod=null` | 不是新节点,不建 craft 条目;属 body 级缺口 |

**游戏本身对无 pod craft**:照常物理模拟、在 `FlightState.CraftNodes`、地图可见;但无控制权(`SwitchToNextCommandPod` 只遍历 `HasCommandPod`;[`FlightSceneScript.cs:1581`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/FlightSceneScript.cs:1581);`IsResumable = HasCommandPod && AllowPlayerControl`)。`CraftScript` 每帧覆盖朝向用 `ActiveCommandPod ?? PrimaryCommandPod`,两者都 null 时**不覆盖**([`CraftScript.cs:2046`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/CraftScript.cs:2046))→ 远端幽灵朝向不会被游戏改写。生命周期:撞击/无部件/加载失败/菜单手动删(`IsDebris = !HasCommandPod && ContractTrackingId==null`)才 `DestroyCraft`;**飞行中无自动清理**。

**处理方案(结论)**:

1. **要同步**:所有"分离出的新 CraftNode"(含无 pod)走现有幽灵船 + 低频(1~5Hz)+ 生命周期钩子;控制字段无意义(`ActiveCommandPod` 为 null 采样自然为零)。
2. **轨道残骸**:需 `Situation`(地面/轨道)字段,接收端用 `LaunchLocationType.Orbital` 或直接 `SetStateVectors`,不能固定 `SurfaceLockedGround`(对应 MC2)。
3. **过滤策略(可选,MC4)**:残骸只在"距任一玩家较近 / 部件数>阈值"时生成完整幽灵船;超远/极小跳过。
4. **超时清理**:低频下 5~10s 无状态包(>3 周期)→ 远端删幽灵船,兜住"owner 侧已毁但 CraftRemove 丢 / owner 掉线"。
5. **已知缺口**:同 craft 内 `IsDebris` 小碎片只同步旋转不同步位置(`BodyRotations` 限制),MC2 可选补 body 级位置。
6. **最高优先级场景 C**:"玩家分离掉唯一 pod" → `SplitCraftNode` 自动 `ChangePlayersActiveCommandPodImmediate` 切走控制权([`CraftSplitter.cs:133`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/Sim/CraftSplitter.cs:133)),原 craft 变无 pod 残骸、`FlightSceneScript.Instance.CraftNode` 自动换节点 → mod 必须 **`RefreshLocalCraft()`** 且把原 craft 从"活动船"降级为"残骸(低频)"继续上报。

---

## 八、jnoCode 边界情况排查(当前 plan 未覆盖 / 需补)

> 系统性扫过 `C:/renko/shitProgram/jnoCode` 生命周期/坐标/控制/资源相关路径后的补充。按优先级分三档。

### 8.1 高优先级(直接影响多 craft 正确性,必做)

1. **SOI 切换 / 换母星(Parent 变化) —【决策:暂不做跨行星】**:
   - 事实:[`TransitionShipToNewPlanetSoi`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/FlightSceneScript.cs:1745) → [`CraftNode.TransitionToNewSoi`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/Sim/CraftNode.cs:810) 改变 `craft.Parent`;玩家船还会触发 `PlayerChangedSoi` 事件。状态包的位置/速度是 **`craft.Parent` 的 surface 坐标**,朝向也相对 parent。
   - **决策(2026-08-16)**:暂时不考虑跨行星联机,**默认约定:所有玩家在同一行星系统(房主指定),不做生涯相关**。SOI 换星暂不处理,状态包不引入 `ParentPlanetName`;但**同一星球内的"地面 / 轨道"区分(`Situation`)保留**——分离的助推器/整流罩仍可能在本星轨道上。
   - 备注:将来要做跨行星时,再补 `ParentPlanetName` 检测不符 → `TransitionToNewSoi`(或重建)+ `PlayerChangedSoi` 广播。

2. **未加载节点的采样缺口**:
   - 事实:[`TrySampleLocalCraft`](../Assets/Scripts/Net/MpNetworkManager.cs:1606) 依赖 `craft.CraftScript != null`(line 1614)并读 `CraftScript.CenterOfMass / Assembly.Bodies / ActiveCommandPod`。**owner 的非活动节点(残骸、对接的第二艘、远距离船)可能未加载 → 采样直接失败**。
   - 方案:每节点采样加"node 级回退"——`CraftScript==null` 时用 `CraftNode.Position/Velocity/Heading`(数据层,轨道模拟仍更新)+ `Data.ActiveCommandPodId` 恢复 pod;`BodyRotations` 缺失则回退 XML 设计态。

3. **远端幽灵船被 [ ] 接管风险(会炸的坑) —【决策:用 Harmony 拦截,且拦在总入口】**:
   - 事实:[`SwitchToNextCommandPod`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/FlightSceneScript.cs:1575) 只查 `HasCommandPod && IsLoadedInGameView`,**不查 `AllowPlayerControl`**;而 `ChangePlayersActiveCommandPodImmediate` 末尾还会强制 `AllowPlayerControl = true`([`FlightSceneScript.cs:383`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/FlightSceneScript.cs:383))。
   - 风险:接收端玩家按 [ ] 切到带 pod 的远程幽灵 → 幽灵被 `SetIsPlayer(true)` → 接收端开始把"远程幽灵"当自己的船采样广播 → 双向污染。
   - **决策(2026-08-16)**:用 **Harmony prefix 拦在总入口 `FlightSceneScript.ChangePlayersActiveCommandPodImmediate(ICommandPod, ICraftNode, bool)`**,当目标 `craftNode` 是本机登记的远程幽灵(用 Guid 标记区分)时直接返回 false、跳过原方法。
   - **为什么拦总入口而非 `SwitchToNextCommandPod`**:换控制不止 [ ] 一条路——地图 inspector "Take control"、部件菜单 "Take control"、`EvaScript.TakeControl`、Vizzy `CraftService.ChangePlayersActiveCommandPodImmediate`([`CraftService.cs:825`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Vizzy/Craft/CraftService.cs:825))都汇到这一个方法;拦总入口一处全覆盖。且地图/部件菜单的按钮本身因 `AllowPlayerControl=false` 已隐藏,实际漏网口主要是 [ ] 循环和直接 API 调用。

4. **JNO 原生对接 = MergeCraftNode(dominant 保身份),比 7.6 假设更简单**:
   - 事实:[`DockingPortScript.CompleteDockConnection`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/DockingPortScript.cs:523):**玩家船 `IsPlayer` 优先为 dominant**(line 527-533 交换)→ `CraftSplitter.MergeCraftNode(source, target)`(line 552)→ source `DestroyCraft()`([`CraftSplitter.cs:73`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/Sim/CraftSplitter.cs:73))→ 走 `CraftNodeRemoved`。
   - 结论:**对接 = 被吸收方走 CraftNodeRemoved + 重发 dominant 新 XML(hash 变化),无需显式 CraftMerge 消息**;dominant 判定规则是"IsPlayer 优先"(对应 Luna 的"谁控制谁 dominant"但规则更简单)。

### 8.2 中优先级(正确性影响有限,需决策)

5. **燃料/资源/部件状态不同步** —**【决策 2026-08:MVP 接受不同步】**:`CraftFuelSource.TotalFuel`([`CraftFuelSource.cs:236`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Fuel/CraftFuelSource.cs:236))、part 损伤/展开/引擎/Vizzy 状态都未同步(Luna 有 `VesselResourceSystem` + `VesselPartSync*`)。幽灵物理关 → 引擎视觉本来不跑;但燃料表/性能数据会不一致。MVP 记为已知限制(用户已确认接受);后续如需再加"每 fuel source 燃料量"字段。

6. **水(浮水)**:`CraftNode.InContactWithWater`([`CraftNode.cs:363`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/Sim/CraftNode.cs:363),由 [`CraftScript.cs:2141`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/CraftScript.cs:2141) 设置)、`FlightData.InWater`。现有 GroundedSurface 路径假设地面;浮水需同套处理 + `InContactWithWater` 标记。

7. **玩家切 craft 节点 = SetIsPlayer 切换(确认 7.5/7.6 机制)**:`SetCraftNode`([`FlightSceneScript.cs:1530`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/FlightSceneScript.cs:1530)) 对旧节点 `SetIsPlayer(false)`、新节点 `SetIsPlayer(true)`,`FlightState.PlayerNodeId` 随 `ActiveCommandPodChanged` 更新。→ mod 应监听 `FlightScene.ActiveCommandPodChanged`(回调带 craftNode),NodeId 变化即 `RefreshLocalCraft()`。

### 8.3 低优先级 / 已知限制(仅记录)

8. **回收/离场景销毁**:`CraftRecovery`(菜单/地图侧,[`CraftRecovery.cs:188`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/CraftRecovery.cs:188))、`DestroyOnExitFlightScene`([`CraftNode.cs:254`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/Sim/CraftNode.cs:254),FlightEnd 时销毁)——场景切换已被 `LobbyManager.OnSceneLoaded` 兜住。
9. **Vizzy 状态**:程序状态不同步,幽灵不跑 Vizzy;MVP 外。
10. **合约生成的 craft** —**【决策:不适用,不考虑生涯】**:`SpawnCraftRequirement` 可在飞行中 spawn **无主 craft**(无玩家可上报)。既然不做生涯/合约(见 8.1-1 决策),此场景不处理;若将来开生涯再定(约定"合约 spawn 的船仅房主同步"或忽略)。
11. **地图显示**:[`MapViewScript.AddCraft`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/MapView/MapViewScript.cs:789) `IsPlayer || IsLoadedInGameView` → 动态图标,幽灵已加载会显示;配合 8.1-3 需限制选择/接管。
