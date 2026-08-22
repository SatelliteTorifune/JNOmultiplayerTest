# �?Craft 同步方案(分析文档)

> 项目:JNOmultiplayerTest(SimpleRockets 2 / JNO 联机 mod aMptest)
> 反编译参�?`C:/renko/shitProgram/jnoCode`
> KSP 参�?`C:/renko/unityProjects/LunaMultiplayer`
> 状�?📋 方案研究阶段(已整理候选方�?待定具体实现与难度评�?2026-08-18 **body 级姿态同步已拆分为独�?plan [`body-sync-2026-08-18.md`](body-sync-2026-08-18.md)**,本文件只专注�?craft)
> 定位:**当前唯一活跃 plan**(索引�?[`README.md`](README.md));已完�?历史文档�?`archive/`

---

## 一、当前项目现状摘�?�?craft 相关)

核心同步链路集中�?[`MpNetworkManager.cs`](../Assets/Scripts/Net/MpNetworkManager.cs) + [`MpMessage.cs`](../Assets/Scripts/Net/MpMessage.cs)�?

已落地且扎实的部�?
- 飞船交换:SP2 风格 hash 按需下载 XML(gzip + MD5 缓存去重,`CraftXmlRequest/Response`);
- 状态同�?`recdata`(位置/速度/Heading/SrfRel 朝向 + 控制 + BodyRotations),20~30Hz,带时间戳环形缓冲 + 100~150ms 渲染延迟插�?
- 朝向同步:已按 LunaMultiplayer `srfRelRotation` 思路落地(相对地表朝向,�?[`archive/heading-sync-2026-08-17.md`](archive/heading-sync-2026-08-17.md)),并解决游戏覆盖朝向问�?LateUpdate 写回、物理禁用走 `Warp` 原因避免 MapView NRE);
- 远程飞船�?幻影模式":`AllowPlayerControl=false` + 物理禁用 + kinematic + 反射�?`GroundedSurface*` / FlightData�?

**�?craft 的关键缺�?都指向同一个根�?**:

| # | 现状 | 后果 |
|---|---|---|
| 1 | `_remoteCrafts` �?`Dictionary<int, RemoteCraft>`,�?playerId 键控 | 每玩家只能有 1 艘远程飞�?|
| 2 | `MpPeer` 只有单个 `NodeId` + 单个 `CraftXml` | 每玩家只能上�?1 �?|
| 3 | 采样/�?XML 只读 `FlightSceneScript.Instance.CraftNode`(本机唯一"玩家飞船") | debris、分离件完全不入�?|
| 4 | 从未订阅 `FlightState.CraftNodeAdded / CraftNodeRemoved`(仅两�?for 循环诊断) | 游戏自己分裂出的新节点永远发现不�?也不会清�?|

> ⚠️ **关键事实**:游戏�?`NodeId` �?*每机自增、分裂时重新分配**(`FlightState.AddCraft` �?`GetNextNodeId`),**跨机不可作唯一�?*。当前用 playerId 当键正是这个原因的产物�?

---

## 二、多 craft 同步的实�?

�?craft 不是"多一个映�?,而是:**同步整个 `FlightState.CraftNodes` 集合**(玩家飞船 + 分离/残骸/对接产生的所有节�?。每节点要解决四件事:

1. **身份**:跨机唯一、分�?合并后仍稳定的键;
2. **生命周期**:新增/分离/合并/销毁的事件发现与广�?
3. **内容**:每节点的状态包(位置/朝向/body/情形);
4. **频率**:�?活动/次要"分级发包,防带宽爆炸�?

### 游戏原生多节点能�?反编译确�?

- [`FlightState.CraftNodes`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/State/FlightState.cs:120) �?`IReadOnlyList<CraftNode>`,游戏本身就管理多艘飞�?残骸、对接、多节点);
- `FlightState.AddCraft(CraftNode, originalNode)`([`FlightState.cs:320`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/State/FlightState.cs:320)):自动分配 NodeId、触�?`CraftNodeAdded`;
- 运行时分裂由 [`CraftSplitter.SplitCraftNode`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/Sim/CraftSplitter.cs:105) 完成:�?CraftNode �?`AddCraft` �?触发 `CraftNodeAdded`,�?*分离/残骸天然走同一事件通道**;
- `CraftNode.DestroyCraft()`([`CraftNode.cs:578`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/Sim/CraftNode.cs:578)):�?`IsDestroyed` �?游戏 `ProcessDestroyedCraftNodes()` 下一帧移除并触发 `CraftNodeRemoved`;
- `FlightState.LoadCraftXml(nodeId)` 可拿任意节点�?XML(分裂节点�?`CraftNodeDataDynamic` 也会入库,可取�?;
- `FlightSceneScript.SpawnCraft(...)` + `ICraftLoader.LoadCraftImmediate(...)` 是生成远程飞船的入口(已在 M2 使用)�?

---

## 三、候选方案对�?

### 方案 A:事件驱动 + 每机全量上报(当前风格的最小扩�?

- 每客户端在进入飞行场景、以及收�?`CraftNodeAdded` �?把它**所�?* `FlightState.CraftNodes`(不只活动节点)上报:
  - 给每个本地节点分配一�?**mod 自造的全局 `Guid`**(Luna �?VesselId 同款),本地维护 `Dictionary<int, Guid>` nodeId→Guid,发现即分�?
  - 上报 `Guid + xmlHash`;房主广播 `CraftInfo`;
- 状态包�?`(playerId, nodeId)` 改为 `(ownerId, craftGuid, ...)`,`_remoteCrafts` 改为 `Dictionary<Guid, RemoteCraft>`;
- `CraftNodeRemoved` / `DestroyCraft` �?广播 `CraftRemove(Guid)`,接收�?`DestroyCraft()`(现有 [`RemoveRemoteCraft`](../Assets/Scripts/Net/MpNetworkManager.cs:1080) 逻辑直接复用)�?

| 优点 | 缺点 |
|---|---|
| 改动�?与现�?Hello/Welcome/PlayerJoin 流程同构 | **靠可靠消息兜�?*:丢一条消息就少一艘船(公网 UDP/Steam 下需 ack 重发,现有 CraftData 重发模式可抄) |

### 方案 B:注册表对�?LunaMultiplayer 模型,推荐)

照搬 Luna �?`VesselSyncSystem` + `VesselProtoSystem` 双层:

- **快路�?事件)**:同方�?A,新增/分裂/合并立即广播;
- **慢路�?对账)**:�?~10s 客户端把"我拥有的 craft Guid 列表(+hash)"发给房主;房主持有**全局注册�?*,diff 后把"你缺的船 / 该删的船"回给每个客户�?客户端按 hash 走现有的按需下载补齐 XML;
- **自愈**:任何丢包/时序错乱在下一个对账周期内被修�?玩家中途加入也天然覆盖(注册表全量下�?,不用像现在这样在 `OnHello` 里逐个补发�?

| 优点 | 缺点 |
|---|---|
| 健壮、可自愈、天然支持加�?离开/分裂/合并 | 多一套注册表逻辑 |

> LunaMultiplayer 事实就是这个模型。其 [`VesselLoader.LoadVessel`](../C:/renko/unityProjects/LunaMultiplayer/LmpClient/VesselUtilities/VesselLoader.cs:44) 还做�?结构没变�?early-out、变了才毁掉重建"的增量加载——JNO 的幽灵船模式更省(不用重建,物理本来就不开)�?

### 方案 C:整态快照对�?仅作修复通道)

周期序列化整�?FlightState 的全部节点状�?位置 + XML)一次性下�?只用�?检测到分歧时修�?,不作为实时同步通道�?

| 优点 | 缺点 |
|---|---|
| 实现最直接、能强收�?| 带宽�?只适合�?B 的补�?如长连接恢复后强对账),不建议当主通道 |

### 结论

**推荐:A 快路�?+ B 慢对�?混合**(Luna 的成熟做�?,C 视情况做修复用�?

---

## 四、落地设计点(对照 LunaMultiplayer 具体文件)

| 设计�?| 做法 | LunaMultiplayer 对应实现 |
|---|---|---|
| **全局身份** | mod 自�?`Guid`(本地 `Dictionary<int, Guid>` nodeId→Guid,发现即分�?,状态包�?`(ownerId, craftGuid)`;本地 NodeId 仍用�?`FlightState.LoadCraftXml(nodeId)` �?XML | `VesselPositionUpdate.VesselId`(Guid)、`CurrentVesselUpdate: ConcurrentDictionary<Guid, ...>` |
| **生命周期钩子** | 订阅 `FlightState.CraftNodeAdded/Removed`;合并�?`CraftSplitter.MergeCraftNode` 钩子(广播 `CraftMerge(keepGuid, removeGuid)`,接收端删被并掉的�?;分裂�?`SplitCraftNode �?AddCraft �?CraftNodeAdded` 自动触发,天然进上报路�?| `VesselProtoEvents`(PartCoupled/PartDecoupled)、`VesselCoupleSys/VesselDecoupleSys`、`VesselRemoveSystem` |
| **每船内容** | 复用 `recdata`,�?`CraftGuid` + `Situation`(地面/轨道)+ `IsDebris/HasCommandPod`;轨道残骸�?`Parent` 行星�?接收端在同行�?spawn,呼应"同一行星系统"约束)。残骸共享设�?hash �?现有 `_xmlCache` 可天然去�?| `VesselPositionMsgData`:lat/lon/alt + velocity + 8 元素 orbit + normal + BodyName |
| **频率分级(LOD)** | 活动�?20~30Hz;**次要/残骸降到 1~5Hz**;且当"附近有其他玩家船"(`PlayerVesselsNearby`)才快�?否则慢发——防残骸一多带宽爆�?| `SendVesselPositionUpdates` vs `SendSecondaryVesselPositionUpdates` + `TimeToSendVesselUpdate` |

**可直接复用、只换键的现有资�?*:
- 幽灵船初始化 [`InitializeRemoteCraft`](../Assets/Scripts/Net/MpNetworkManager.cs:1233)(物理禁用�?Warp 原因);
- 插值缓�?[`UpdateRemoteCrafts`](../Assets/Scripts/Net/MpNetworkManager.cs:1290) / `TryGetInterpolatedState`;
- 朝向 srfRel 链路(发送端 `TrySampleLocalCraft` / 接收�?`ApplyRemoteState` + `LateUpdate` 写回);
- 移除清理 [`RemoveRemoteCraft`](../Assets/Scripts/Net/MpNetworkManager.cs:1080)(`DestroyCraft()`);
- 按需下载 + hash 去重([`MpMessage.cs`](../Assets/Scripts/Net/MpMessage.cs) `CraftXmlRequest/Response` + `_xmlCache`)�?

**需要新�?改�?*:
- 消息类型:`CraftInfo(guid, ownerId, xmlHash)`、`CraftRemove(guid)`、`CraftMerge(keepGuid, removeGuid)`、`CraftRegistrySync`(对账�?;
- 状态包�?`CraftGuid`;`MpPeer` 升级�?每玩�?N �?(或拆出独立的 craft �?;
- 生成�?spawn 队列限�?现有 2s 重试节流思路扩展),防大量残骸一次�?`SpawnCraft` 白屏�?

---

## 五、风险与开放问�?

**风险**:
1. **多船 XML 下发�?*:按需下载 + hash 去重已缓�?建议只对"有命令舱/有意义的�?全量 spawn,纯小残骸可只做简化指示物(或不 spawn,仅同步活动船 + 玩家残骸)�?
2. **每船�?tick 序列化开销**:靠分级频率控住�?
3. **轨道残骸**:需�?地面坐标"扩展�?轨道参数"(lat/lon/alt + velocity 已够,MVP 先锁 1x 实时,不做 warp)�?
4. **地图/残骸可视**:Luna 有专门的 `VesselRemoveSystem`(2.5s 清理 kill list)�?已删但还在渲�?;JNO 侧已�?`DestroyCraft()` 真销�?需在多船场景下回归验证 MapView�?

**开放问�?待研究确�?**:
- 跨机稳定身份:�?mod 自�?`Guid` 是否足够?分裂/合并时如何保�?同一艘船"的语�?是否结合 `CraftNode.InitialCraftNodeIds` 溯源)?
- 对账周期与消息量:10s 对账 + 2.5s 定义广播(Luna 参数)�?JNO �?Steam/TCP 通道是否合�?
- 残骸策略:哪些节点值得同步成完整幽灵船,哪些跳过(按是�?`HasCommandPod` / 部件�?/ 距玩家距�??
- 玩家"换船控制"(JNO 单活动节�?:多人各控各的船时,`FlightSceneScript.Instance.CraftNode` 只代表本机活动船,是否需要支�?观察他人控制的第二艘�??

---

## 六、实施里程碑(建议,待定)

### MC1 · 全局身份 + 全量上报(A 基础)
- [ ] `Dictionary<int, Guid>` 本地 nodeId→Guid;订阅 `CraftNodeAdded/Removed`;
- [ ] `CraftInfo` / `CraftRemove` 消息;`_remoteCrafts` �?`Dictionary<Guid, RemoteCraft>`;
- [ ] `TrySampleLocalCraft` / `RefreshLocalCraft` 遍历 `FlightState.CraftNodes` 上报全部节点�?

### MC2 · 每船状�?+ 分级频率
- [ ] 状态包�?`CraftGuid` + `Situation` + `IsDebris`;每船插值缓冲按 Guid 索引;
- [ ] 活动船快发、残骸慢�?1~5Hz);附近有玩家船才快发�?

### MC3 · 注册表对�?B)
- [ ] 房主持有全局注册�?客户端周期上�?Guid 列表;diff 补齐/清理;
- [ ] 合并(对接)消息 `CraftMerge`;`CraftSplitter.MergeCraftNode` 钩子�?

### MC4 · 打磨
- [ ] spawn 队列限�?多船回归测试(残骸/分离/对接/玩家加入离开);
- [ ] 残骸显示策略与带宽统计�?

---

## 七、切�?/ 对接(Luna 做法)+ JNO 控制单元切换 / Drood EVA 研究

> 本节补充两个�?�?craft"直接相关的场�?�?玩家切换控制单元/控制节点;�?对接与分�?�?JNO �?Drood EVA)。Luna 的做法作为参�?最后落�?JNO 的对应钩子�?

### 7.1 前置:身份与锁模型(Luna)

- 每艘船全局唯一 `VesselId`(Guid);**对接�?dominant 船保 Guid、weak �?Guid 删除**;分离产生的新�?Guid �?分离发起�?生成并广�?**接收端强制覆盖本地自动生成的 id**——这是跨机一致性的核心约定�?
- 权限靠锁(服务�?`LockSystem` 裁决,客户�?`LockSystem` 收发 `LockAcquireMsg`):
  - **Control Lock**(控制�?:一艘船同时 1 把、一个玩家同�?1 �?服务端会清多�?;
  - **Update Lock / UnloadedUpdate Lock**(更新�?:拥有者负责发状态、其他人只接�?
  - **Spectator Lock**(观察�?:观战者�?

### 7.2 Luna 切换 craft(控制权交�?

Luna 不自己实现切换动�?而是挂钩 KSP `onVesselChange`(玩家�?`[`/`]` 或地图切换活动船时触�?,�?[`VesselLockEvents.OnVesselChange`](../C:/renko/unityProjects/LunaMultiplayer/LmpClient/Systems/VesselLockSys/VesselLockEvents.cs:14) 里做权限交接:

1. 目标船控制锁本来就是我的(如重载自�?�?直接跳过,不释放锁;
2. 否则**先释放我全部 Control Lock**(一人只能控一�?;
3. 看目标船控制锁归�?
   - **别人的船** �?[`StartSpectating`](../C:/renko/unityProjects/LunaMultiplayer/LmpClient/Systems/VesselLockSys/VesselLockSystem.cs:94):`IsSpectating=true` + `InputLockManager.SetControlLock(BlockAllControls)` 锁死操作 + �?Spectator Lock + 释放我的 Control/Update/Kerbal/UnloadedUpdate �?
   - **无人控制** �?`AcquireControlLock(vesselId)` 向服务端申请�?

拿锁成功([`LockAcquire`](../C:/renko/unityProjects/LunaMultiplayer/LmpClient/Systems/VesselLockSys/VesselLockEvents.cs:90)):
- **我拿到锁**:把该船从"接收同步系统"移除(本地权威控制,停止应用别人状态包);**清零油门**(`ctrlState.mainThrottle = 0f`,防继承上个控制者最后的油门);补拿 Update/UnloadedUpdate/Kerbal �?若在观战则停止�?
- **别人拿到�?*:如果抢的是我活动�?�?我转观战;我的 Update Lock 降级�?UnloadedUpdate Lock�?

切换即时�?[`PositionEvents.OnVesselSwitching`](../C:/renko/unityProjects/LunaMultiplayer/LmpClient/Systems/VesselPositionSys/PositionEvents.cs:19)(KSP `onVesselSwitching`)�?**立即广播新船位置(�?orbit/body),不等定时发包**�?

兜底:[`VesselSwitcherSystem.SwitchToVessel`](../C:/renko/unityProjects/LunaMultiplayer/LmpClient/Systems/VesselSwitcherSys/VesselSwitcherSystem.cs:36) 协程等飞船加�?100 FixedUpdate �?1s �?强制 `Load()`)�?`ForceSetActiveVessel`;杀活动船时切到最近可用船(`VesselRemoveSystem.SwitchVesselIfKillingActiveVessel`)�?

### 7.3 Luna 对接(Docking)/分离(Decouple/Undock)

**对接**——KSP 事件 `onPartCoupling`(开�?+ `onPartCoupled`(完成,�?`removedVesselId`),注意注释:**"couple 事件�?WEAK 船触�?**(被吸收的那艘)�?

发起�?[`VesselCoupleEvents.CoupleComplete`](../C:/renko/unityProjects/LunaMultiplayer/LmpClient/Systems/VesselCoupleSys/VesselCoupleEvents.cs:25):
1. 判断 trigger(DockingNode / GrappleNode / Kerbal / Other);
2. �?`VesselCouple` 消息:`(dominantVesselId, weakVesselId, 两船对接 partFlightId, trigger)`—�?*只同步事件语�?不传船体**;
3. `SendVesselRemove(weakVesselId)` + `DelayedKillVessel(weakVesselId, �? 500ms)`(延迟杀 weak,先让对接本地完成);
4. `ReleaseAllVesselLocks(weakVesselId)`;对方在更未来时间线则 `WarpIfSubspaceIsMoreAdvanced`(时间同步)�?

接收�?[`VesselCoupleMessageHandler`](../C:/renko/unityProjects/LunaMultiplayer/LmpClient/Systems/VesselCoupleSys/VesselCoupleMessageHandler.cs:16) + [`VesselCouple.ProcessCouple`](../C:/renko/unityProjects/LunaMultiplayer/LmpClient/Systems/VesselCoupleSys/VesselCouple.cs:65):
1. �?`VesselId` 排队、按 `GameTime` 排序(消息时间 > 本地 `UniversalTime` 入队等时间到;队头时间更新 �?玩家回退,清空队列);
2. `FindVessel` 两船(含我活动船则强制 `Load()`),�?partFlightId 找两零件;
3. **调用 KSP 真实对接 API �?weak 接上 dominant**:DockingNode→`DockToVessel`;GrappleNode→反�?`Grapple`;Kerbal→上�?Other→`Couple(...)`;�?`IgnoreEvents=true` 包住防递归;
4. `AfterCouplingEvent`:我活动船�?weak �?`ForceSetActiveVessel(dominant)`;�?dominant �?`MakeActive()`;
5. 失败兜底:`coupleResult==false` �?`KillVessel(weakVesselId)`�?

合并�?dominant 的新结构:[`VesselProtoEvents.PartCoupled`](../C:/renko/unityProjects/LunaMultiplayer/LmpClient/Systems/VesselProtoSys/VesselProtoEvents.cs:152) �?`SendVesselMessage(dominant)` 重发合并后的完整 proto;`LocalTopologyTracker.RecordMutation` 拉黑两船 id,防止过期�?proto 把已�?weak �?复活"�?

**分离**:
- Decouple(分离�?分级)[`VesselDecoupleEvents.DecoupleComplete`](../C:/renko/unityProjects/LunaMultiplayer/LmpClient/Systems/VesselDecoupleSys/VesselDecoupleEvents.cs:19):分离完成 �?立即 `SendVesselPositionUpdate(新船,true)` + �?`VesselDecouple(原船, partFlightId, breakForce, NewVesselId)`;接收�?[`VesselDecouple.ProcessDecouple`](../C:/renko/unityProjects/LunaMultiplayer/LmpClient/Systems/VesselDecoupleSys/VesselDecouple.cs:24) 本地 `decouple(BreakForce)`,然后 **`partRef.vessel.id = NewVesselId` 强制覆盖本地自动分配�?id** �?全局 Guid 唯一且跨机一�?�?ForceUpdate 位置�?
- Undock(对接解除):新分离段作为新船�?proto([`VesselProtoEvents.PartUndocked`](../C:/renko/unityProjects/LunaMultiplayer/LmpClient/Systems/VesselProtoSys/VesselProtoEvents.cs:120))�?

### 7.4 JNO 同一 craft 内切换控制单�?ActiveCommandPod)

**机制**(反编�?:
- API:[`CraftScript.SetActiveCommandPod(pod)`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/CraftScript.cs:1419) + 事件 `ActiveCommandPodChanging/Changed`;统一入口 [`FlightSceneScript.ChangePlayersActiveCommandPodImmediate(pod, node, ignoreDistance)`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/FlightSceneScript.cs:361)(清零控制、重定相机、`SetActiveCommandPod`、必要时 `SetCraftNode`、`AllowPlayerControl=true`)�?
- 触发�?`CommandPodScript.SetActiveCommandPod()`(部件菜单"Take control",[`CommandPodScript.cs:789`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/CommandPodScript.cs:789))、`EvaScript.TakeControl`、地�?inspector(`SelectedModel.cs:482`)、CockpitScript�?
- **关键(对同步的影响)**:[`CraftScript` 每帧 `_centerOfMassTransform.rotation = ActiveCommandPod.PilotSeatOrientation.rotation`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/CraftScript.cs:2046)。�?mod 采样朝向就是 `CenterOfMass.rotation`(→SrfRel)�?*�?同一 craft 内切换控制单�?朝向/控制自动跟随现有 srfRel 同步,无需新增�?*。`Controls`(Pitch/Yaw/Roll/Throttle)也来�?`ActiveCommandPod` �?已在状态包�?
- `Data.ActiveCommandPodId` 持久�?可能�?XML)�?切换�?XML hash 变化 �?触发按需 re-download(良�?不重建已存在的幽灵船)�?
- 可选增�?接收端如需 staging/相机/地图一�?可广�?`ActiveCommandPodChanged(partId)` 让远�?`SetActiveCommandPod`;当前 FlightData 已用反射刷新(�?`ApplyRemoteState` �?�?

### 7.5 JNO Drood EVA(关键发现:是独�?CraftNode)

- **Drood 是带 `EvaScript` �?CommandPod 部件**(`IsEva = EvaScript != null`),坐在 `CrewCompartmentScript` �?不是独立实体�?
- **出舱 EVA**([`CrewCompartmentScript.UnloadCrewMember`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/Eva/CrewCompartmentScript.cs:361) �?`EvaScript.TakeControl` �?[`UnloadFromCrewCompartment`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/Eva/EvaScript.cs:2259) 摧毁物理关节 �?body 断开):
  - 断开 body �?`CraftSplitter.ProcessDisconnectedBody` �?`MoveBodyToNewCraft` / [`SplitCraftNode`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/Sim/CraftSplitter.cs:105) �?**生成新的独立 CraftNode**(�?NodeId;`EvaScript.OnMovedToNewCraft` 更新 `CrewMember.NodeId = newCraft.NodeId`,[`EvaScript.cs:2037`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/Eva/EvaScript.cs:2037));
  - `SwitchToCommandPod` �?`ChangePlayersActiveCommandPodImmediate` 接管�?EVA 节点([`EvaScript.cs:2249`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/Eva/EvaScript.cs:2249))�?
- **回舱**(`LoadIntoCrewCompartment` �?部件重新连接)�?**合并回原 craft**(`CraftSplitter.MergeCraftNode`)�?
- **结论:JNO 原生"�?craft"场景之一——出�?split/新节�?回舱=merge�?* 当前 mod 只上�?`FlightSceneScript.Instance.CraftNode`,远端永远看不�?EVA �?Drood�?
- 且玩家切�?EVA 节点�?`FlightSceneScript.Instance.CraftNode` 会变 �?�?mod �?`LocalNodeId`/`_localCraftXml` 仍是旧节�?�?`TrySampleLocalCraft` 每帧采样当前节点 �?**状态包用旧 NodeId + 新节点状�?远端错位**�?

### 7.6 �?JNO �?craft 方案的增量影�?结论�?

| Luna 机制 | JNO 对应钩子 | 借鉴做法 |
|---|---|---|
| **新船 Guid 发起方生�?+ 接收端覆盖本�?id**(decouple �?`vessel.id = NewVesselId`) | 分裂�?`SplitCraftNode �?AddCraft �?CraftNodeAdded`(NodeId 每机自增) | 本机 `CraftNodeAdded` 时分�?`Guid` 广播 `CraftInfo(guid,�?`;接收�?spawn 后用它取代本�?NodeId 作键 |
| **对接=事件语义(dominant/weak+零件),不传船体;延迟杀 weak** | `CraftSplitter.MergeCraftNode(source, target)` | �?`CraftMerge(keepGuid, removeGuid)`,接收端删被吸收幽灵船、保�?dominant;合并后重�?dominant XML |
| **Dominant 重发 proto + RecordMutation 防复�?* | 合并�?`LoadCraftXml(nodeId)` 可取�?XML | 合并后重�?dominant XML(hash 变化触发按需下载);记住 removeGuid 一段时�?忽略其后续状态包 |
| **切换即广�?+ 清零控制** | `ChangePlayersActiveCommandPodImmediate` 可切 `SetCraftNode`(�?craft �?pod / 切到另一节点) | �?craft �?pod:朝向/控制自动跟随 srfRel,无需新包;切到另一节点(�?EVA Drood)�?**必须 `RefreshLocalCraft()` 更新 LocalNodeId+XML 并立即发一�?* |
| **Lock 裁决切换�?* | 无锁系统,self-authoritative | MVP 不需�?�?多玩家抢�?观战"才引入服务端裁决 |
| **GameTime 排队 + 未来 subspace 延迟处理** | 状态包已带 `FlightState.Time` | 对接/分离事件带时间戳入队,到时间再执行 |
| **Drood 出舱/回舱** | 出舱=`CraftNodeAdded`;回舱=`MergeCraftNode` | 天然�?MC1 生命周期钩子覆盖;EVA 节点单部件可降频或跳�?但玩�?EVA 时是活动节点应同�?|

**一句话总结**:Luna——切换走"控制权交�?�?+ 拿锁即停收该船状�?+ 立即广播新船位置";对接�?事件语义(dominant/weak+零件)+ 接收端真�?API 复现 + 延迟杀 weak + dominant 重发 proto";多船身份统一�?发起方定 Guid、接收端覆盖本地 id"。JNO 侧对应钩子为 `CraftNodeAdded/Removed`(出舱/分离)+ `CraftSplitter.MergeCraftNode`(回舱/对接)+ `ChangePlayersActiveCommandPodImmediate`(�?pod / 换节�?后者需 `RefreshLocalCraft`)�?

### 7.7 分离�?没有 CommandPod �?craft"的处�?结论)

**前提**:JNO 的分离有两条路径([`CraftSplitter.ProcessDisconnectedBody`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/Sim/CraftSplitter.cs:80) �?[`DetermineCraftNodeEligibility`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/Sim/CraftSplitter.cs:193)):

| 条件(任一满足) | 结果 | MP 处理 |
|---|---|---|
| �?`PreventDebris` 部件,或包围盒任轴 > 10m | 生成**�?CraftNode**(`SplitCraftNode`,HasCommandPod 可能�?false;�?pod 时取第一�?part �?root) | **当独�?craft 同步**(见下) |
| 都不满足(小碎�? | 留在�?craft �?body 标记 `IsDebris=true`、`CommandPod=null` | 不是新节�?不建 craft 条目;�?body 级缺�?|

**游戏本身对无 pod craft**:照常物理模拟、在 `FlightState.CraftNodes`、地图可�?但无控制�?`SwitchToNextCommandPod` 只遍�?`HasCommandPod`;[`FlightSceneScript.cs:1581`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/FlightSceneScript.cs:1581);`IsResumable = HasCommandPod && AllowPlayerControl`)。`CraftScript` 每帧覆盖朝向�?`ActiveCommandPod ?? PrimaryCommandPod`,两者都 null �?*不覆�?*([`CraftScript.cs:2046`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/CraftScript.cs:2046))�?远端幽灵朝向不会被游戏改写。生命周�?撞击/无部�?加载失败/菜单手动�?`IsDebris = !HasCommandPod && ContractTrackingId==null`)�?`DestroyCraft`;**飞行中无自动清理**�?

**处理方案(结论)**:

1. **要同�?*:所�?分离出的�?CraftNode"(含无 pod)走现有幽灵船 + 低频(1~5Hz)+ 生命周期钩子;控制字段无意�?`ActiveCommandPod` �?null 采样自然为零)�?
2. **轨道残骸**:需 `Situation`(地面/轨道)字段,接收端用 `LaunchLocationType.Orbital` 或直�?`SetStateVectors`,不能固定 `SurfaceLockedGround`(对应 MC2)�?
3. **过滤策略(可�?MC4)**:残骸只在"距任一玩家较近 / 部件�?阈�?时生成完整幽灵船;超远/极小跳过�?
4. **超时清理**:低频�?5~10s 无状态包(>3 周期)�?远端删幽灵船,兜住"owner 侧已毁但 CraftRemove �?/ owner 掉线"�?
5. **已知缺口(已转�?**:�?craft �?`IsDebris` 小碎片只同步旋转不同步位�?`BodyRotations` 限制)�?**已由独立 plan [`body-sync-2026-08-18.md`](body-sync-2026-08-18.md) �?BodyPoses 覆盖**(位置+旋转相对 comRot)�?
6. **最高优先级场景 C**:"玩家分离掉唯一 pod" �?`SplitCraftNode` 自动 `ChangePlayersActiveCommandPodImmediate` 切走控制�?[`CraftSplitter.cs:133`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/Sim/CraftSplitter.cs:133)),�?craft 变无 pod 残骸、`FlightSceneScript.Instance.CraftNode` 自动换节�?�?mod 必须 **`RefreshLocalCraft()`** 且把�?craft �?活动�?降级�?残骸(低频)"继续上报�?

---

## 八、jnoCode 边界情况排查(当前 plan 未覆�?/ 需�?

> 系统性扫�?`C:/renko/shitProgram/jnoCode` 生命周期/坐标/控制/资源相关路径后的补充。按优先级分三档�?

### 8.1 高优先级(直接影响�?craft 正确�?必做)

1. **SOI 切换 / 换母�?Parent 变化) —【决�?暂不做跨行星�?*:
   - 事实:[`TransitionShipToNewPlanetSoi`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/FlightSceneScript.cs:1745) �?[`CraftNode.TransitionToNewSoi`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/Sim/CraftNode.cs:810) 改变 `craft.Parent`;玩家船还会触�?`PlayerChangedSoi` 事件。状态包的位�?速度�?**`craft.Parent` �?surface 坐标**,朝向也相�?parent�?
   - **决策(2026-08-16)**:暂时不考虑跨行星联�?**默认约定:所有玩家在同一行星系统(房主指定),不做生涯相关**。SOI 换星暂不处理,状态包不引�?`ParentPlanetName`;�?*同一星球内的"地面 / 轨道"区分(`Situation`)保留**——分离的助推�?整流罩仍可能在本星轨道上�?
   - 备注:将来要做跨行星时,再补 `ParentPlanetName` 检测不�?�?`TransitionToNewSoi`(或重�?+ `PlayerChangedSoi` 广播�?

2. **未加载节点的采样缺口**:
   - 事实:[`TrySampleLocalCraft`](../Assets/Scripts/Net/MpNetworkManager.cs:1606) 依赖 `craft.CraftScript != null`(line 1614)并读 `CraftScript.CenterOfMass / Assembly.Bodies / ActiveCommandPod`�?*owner 的非活动节点(残骸、对接的第二艘、远距离�?可能未加�?�?采样直接失败**�?
   - 方案:每节点采样加"node 级回退"——`CraftScript==null` 时用 `CraftNode.Position/Velocity/Heading`(数据�?轨道模拟仍更�?+ `Data.ActiveCommandPodId` 恢复 pod;`BodyRotations` 缺失则回退 XML 设计态�?

3. **远端幽灵船被 [ ] 接管风险(会炸的坑) —【决�?�?Harmony 拦截,且拦在总入口�?*:
   - 事实:[`SwitchToNextCommandPod`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/FlightSceneScript.cs:1575) 只查 `HasCommandPod && IsLoadedInGameView`,**不查 `AllowPlayerControl`**;�?`ChangePlayersActiveCommandPodImmediate` 末尾还会强制 `AllowPlayerControl = true`([`FlightSceneScript.cs:383`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/FlightSceneScript.cs:383))�?
   - 风险:接收端玩家按 [ ] 切到�?pod 的远程幽�?�?幽灵�?`SetIsPlayer(true)` �?接收端开始把"远程幽灵"当自己的船采样广�?�?双向污染�?
   - **决策(2026-08-16)**:�?**Harmony prefix 拦在总入�?`FlightSceneScript.ChangePlayersActiveCommandPodImmediate(ICommandPod, ICraftNode, bool)`**,当目�?`craftNode` 是本机登记的远程幽灵(�?Guid 标记区分)时直接返�?false、跳过原方法�?
   - **为什么拦总入口而非 `SwitchToNextCommandPod`**:换控制不�?[ ] 一条路——地�?inspector "Take control"、部件菜�?"Take control"、`EvaScript.TakeControl`、Vizzy `CraftService.ChangePlayersActiveCommandPodImmediate`([`CraftService.cs:825`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Vizzy/Craft/CraftService.cs:825))都汇到这一个方�?拦总入口一处全覆盖。且地图/部件菜单的按钮本身因 `AllowPlayerControl=false` 已隐�?实际漏网口主要是 [ ] 循环和直�?API 调用�?

4. **JNO 原生对接 = MergeCraftNode(dominant 保身�?,�?7.6 假设更简�?*:
   - 事实:[`DockingPortScript.CompleteDockConnection`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/DockingPortScript.cs:523):**玩家�?`IsPlayer` 优先�?dominant**(line 527-533 交换)�?`CraftSplitter.MergeCraftNode(source, target)`(line 552)�?source `DestroyCraft()`([`CraftSplitter.cs:73`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/Sim/CraftSplitter.cs:73))�?�?`CraftNodeRemoved`�?
   - 结论:**对接 = 被吸收方�?CraftNodeRemoved + 重发 dominant �?XML(hash 变化),无需显式 CraftMerge 消息**;dominant 判定规则�?IsPlayer 优先"(对应 Luna �?谁控制谁 dominant"但规则更简�?�?

### 8.2 中优先级(正确性影响有�?需决策)

5. **燃料/资源/部件状态不同步** �?*【决�?2026-08:MVP 接受不同步�?*:`CraftFuelSource.TotalFuel`([`CraftFuelSource.cs:236`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Fuel/CraftFuelSource.cs:236))、part 损伤/展开/引擎/Vizzy 状态都未同�?Luna �?`VesselResourceSystem` + `VesselPartSync*`)。幽灵物理关 �?引擎视觉本来不跑;但燃料表/性能数据会不一致。MVP 记为已知限制(用户已确认接�?;后续如需再加"�?fuel source 燃料�?字段�?

6. **�?浮水)**:`CraftNode.InContactWithWater`([`CraftNode.cs:363`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/Sim/CraftNode.cs:363),�?[`CraftScript.cs:2141`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/CraftScript.cs:2141) 设置)、`FlightData.InWater`。现�?GroundedSurface 路径假设地面;浮水需同套处理 + `InContactWithWater` 标记�?

7. **玩家�?craft 节点 = SetIsPlayer 切换(确认 7.5/7.6 机制)**:`SetCraftNode`([`FlightSceneScript.cs:1530`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/FlightSceneScript.cs:1530)) 对旧节点 `SetIsPlayer(false)`、新节点 `SetIsPlayer(true)`,`FlightState.PlayerNodeId` �?`ActiveCommandPodChanged` 更新。→ mod 应监�?`FlightScene.ActiveCommandPodChanged`(回调�?craftNode),NodeId 变化�?`RefreshLocalCraft()`�?

### 8.3 低优先级 / 已知限制(仅记�?

8. **回收/离场景销�?*:`CraftRecovery`(菜单/地图�?[`CraftRecovery.cs:188`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/CraftRecovery.cs:188))、`DestroyOnExitFlightScene`([`CraftNode.cs:254`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/Sim/CraftNode.cs:254),FlightEnd 时销�?——场景切换已�?`LobbyManager.OnSceneLoaded` 兜住�?
9. **Vizzy 状�?*:程序状态不同步,幽灵不跑 Vizzy;MVP 外�?
10. **合约生成�?craft** �?*【决�?不适用,不考虑生涯�?*:`SpawnCraftRequirement` 可在飞行�?spawn **无主 craft**(无玩家可上报)。既然不做生�?合约(�?8.1-1 决策),此场景不处理;若将来开生涯再定(约定"合约 spawn 的船仅房主同�?或忽�?�?
11. **地图显示**:[`MapViewScript.AddCraft`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/MapView/MapViewScript.cs:789) `IsPlayer || IsLoadedInGameView` �?动态图�?幽灵已加载会显示;配合 8.1-3 需限制选择/接管�?

### 8.4 body 级姿态同�?—�?已拆分为独立 plan

> **2026-08-18**:body 级姿态同�?转轴/关节连接部件�?整体移动",BodyRotations→BodyPoses 方案 + SP2 参考可抄性结�?�?multi-craft 是两个独立目�?**已移�?[`body-sync-2026-08-18.md`](body-sync-2026-08-18.md)**。本文件只保留多 craft(身份/生命周期/对接/残骸/切换)内容�?
>
> 与本文件相关的接�?�?§7.7.5 "IsDebris 小碎片只同步旋转不同步位�?缺口 �?�?[`body-sync-2026-08-18.md`](body-sync-2026-08-18.md) �?BodyPoses 覆盖;分离/对接�?body 数量与顺序变�?�?由本文件 MC1/MC3 生命周期对账解决(body-sync 的索引契约依赖它)�?
