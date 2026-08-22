# 远程飞船高延迟平滑方�?latency-smoothing)

> 状�?�?已实�?**P0+P1+自适应lookback+速度帧修�?已实现并经双�?本地VM)实测确认解决静止滑动/高延迟卡�?*;调试工具已落�?
> 目标:延迟 >100ms(RTT)�?对面 craft 同步位置**平滑**(�?一卡一�?),包括整船平移、朝向、每 body 相对位姿(转轴/关节连接的子装配摆动)�?> 关联:[`body-sync-2026-08-18.md`](body-sync-2026-08-18.md)(BodyPoses 数据�?本方案在**接收端平滑层**上做文章);[`multi-craft-sync-2026-08-16.md`](multi-craft-sync-2026-08-16.md)(body 数量变化归生命周期对�?�?
---

## 0. 一句话结论

**现状卡顿根因:接收端插值只覆盖整船 Position/Heading,`BodyRotations/BodyPositions`(�?body 相对 comRot 位姿)�?每包最新值整体覆�?�?每个状态包(20Hz)所�?body 瞬间跳一�?加上缓冲欠载�?冻结→跳�?、无外推导致高延迟滞�?橡皮筋�?*
SP2 反编译给出了完整药方:**�?测得延迟×速度外推"补足延迟、速度自适应的指数平滑消跳变�?阈值直接瞬移自愈、每 body 相对位姿 10·dt 指数平滑+近距快照**。SR2 幽灵 kinematic 模型不能照抄 SP2 的物理集�?但上述平滑逻辑全部可直接移植�?
---

## 1. 现状与根�?代码核实)

接收端逐帧管线([`MpNetworkManager.cs`](../Assets/Scripts/Net/MpNetworkManager.cs)):

1. 收到状态包 �?`PushSample` �?32 槽环形缓�?�?*到达�?unscaledTime** 排序,[:1079-1091](../Assets/Scripts/Net/MpNetworkManager.cs:1079));
2. 每帧 `UpdateRemoteCrafts`:`renderTime = now - RenderDelayMs/1000`([:1593](../Assets/Scripts/Net/MpNetworkManager.cs:1593)),`TryGetInterpolatedState` �?renderTime 前后两包插�?[:1637-1680](../Assets/Scripts/Net/MpNetworkManager.cs:1637));
3. `ApplyRemoteState(rc, interp)` �?GroundedSurface*/SetStateVectors/朝向/�?body 位姿/尾焰/部件/控制([:1690-1798](../Assets/Scripts/Net/MpNetworkManager.cs:1690));
4. `LateUpdate` �?`rc.LastApplied`(插值后状�?重写朝向抗游戏覆�?[:422-436](../Assets/Scripts/Net/MpNetworkManager.cs:422))�?
**R1(主因):body 姿态不参与插�?每包整体跳�?*
`TryGetInterpolatedState` �?`Mod.RemoteDataPack interp = b;` 只覆�?`Position/Velocity/Heading/SrfRel`([:1673-1678](../Assets/Scripts/Net/MpNetworkManager.cs:1673)),`BodyRotations/BodyPositions/EngineThrottles/PartActivated/控制` 全部沿用**较新�?b 整体拷贝**;`ApplyRemoteBodyPoses` 再把�?body 的绝对位�?localRotation 直接写死([:474-491](../Assets/Scripts/Net/MpNetworkManager.cs:474))。→ 每个新包到达(�?0ms 一�?所�?body **瞬间跳到新相对位�?*;整船根是插值平滑的、body 却是跳的 �?机身"�?50ms 抖一�?,转轴/关节子装配像橡皮筋�?
**R2:缓冲欠载 �?冻结-跳变�?*
`renderTime �?最新样本到达时间` �?抖动尖峰、丢包、renderDelay 偏小),`TryGetInterpolatedState` 直接返回最新原始包([:1657-1662](../Assets/Scripts/Net/MpNetworkManager.cs:1657))�?飞船**原地冻结**,下一包到达才继续�?�?"卡一下、跳一�?。高延迟场景抖动/丢包更多,该分支触发更频繁�?
**R3:renderDelay 固定不自适应�?*
`RenderDelayMs` 默认 100ms(`SetTickRate` �?`Clamp(2000/hz,40,400)` �?[:45-47/552-563](../Assets/Scripts/Net/MpNetworkManager.cs:552))。不随实测抖�?延迟调整:过小→R2 欠载;过大→滞后更明显�?
**R4:无外�?�?高延迟滞�?橡皮筋�?*
渲染位置 = 最新包 + `RenderDelayMs` 的插值延�?**不把"网络延迟期间飞船应继续前�?补回�?*。RTT>100ms(单向>50ms)时对面实际已飞出很远,渲染还在 150ms+ 之前的位�?对面转向/刹车/加速后误差瞬间放大→被拉回→橡皮筋�?
**R5(潜在):body 欧拉角插值要防绕转�?*
若直�?`Lerp(BodyRotations[i] euler)` 会在 350°�?0° 这类边界绕一大圈;必须�?Quaternion �?`Slerp`(相对 comRot 的旋转无万向锁问�?�?
---

## 2. SP2 反编译参�?全部已核�?file:line)

> 反编译源:`C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/`。SP2 �?*远程船物理保持开�?*、FishNet tick 时钟同步;SR2 幽灵 kinematic + 无时钟同�?�?**平滑逻辑可抄,物理集成不抄**�?
### T1. 延迟×速度外推(SP2 核心,两处独立实现)

- [`CraftStateSerializer.SerializeRead`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/CraftStateSerializer.cs:55) `num2 = Clamp(physicsTime - num, 0, 0.25f)` = **网络延迟**(接收端当前物理时�?�?包内发送端时间,封顶 250ms);
- [`:76`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/CraftStateSerializer.cs:76) `vector5 = vector2 + vector + num2 * vector3`(**pos + velocity×延迟**)——把目标位置"播到应该现在的位",**不再需要大 renderDelay 去等延迟**,滞后被抵�?
- 旋转同样外推:[`:88-94`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/CraftStateSerializer.cs:88) �?`angularVelocity × 延迟` 转一个增量角�?Slerp;
- 松散 body 层同�?[`NetworkBodyScript.SerializeRead`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/NetworkBodyScript.cs:166) `SetPositionAndRotation(pos + delta + delay*vel, rot * AngleAxis(delay*angularVel))`�?
### T2. 速度自适应指数平滑(消跳�?永远�?�?目标)

- [`CraftStateSerializer.cs:84`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/CraftStateSerializer.cs:84) `num4 = Lerp(0.1f, 1f, |v|*0.02f)`—�?*慢速船重平�?0.1)、快速船近瞬�?�?)**;
- [`:85`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/CraftStateSerializer.cs:85) `position = Vector3.Lerp(position, target, num4)`——逐帧指数收敛,无离散跳�?
- 旋转 [`:94`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/CraftStateSerializer.cs:94) `Slerp(rotation, target, 2.5f*Time.deltaTime)`�?
### T3. 大误差直接瞬�?自愈,不慢�?

- [`CraftStateSerializer.cs:78-81`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/CraftStateSerializer.cs:78) `(target-pos).sqrMagnitude > 10000`(**>100m**)�?直接 `position = target`(生成/大修�?失步时秒对齐,避免全场慢滑)�?
### T4. �?body 相对位姿指数平滑 + 近距快照(对应我们�?BodyPositions/BodyRotations)

- �?body(�?ParentBody)收到状态只�?`SyncData.TargetPosition/TargetRotation`([`CraftStateSerializer.cs:109-123`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/CraftStateSerializer.cs:109)),实际应用�?[`BodyScript.OnUpdate`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Craft/BodyScript.cs:660):
  - 偏差 < 0.01 �?直接快照并清 Target(防持续微�?;
  - 否则 `localPosition = Vector3.Lerp(cur, target, 10f*Time.deltaTime)`、`localRotation = Quaternion.Slerp(cur, target, 10f*Time.deltaTime)`([`:667/679`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Craft/BodyScript.cs:667))�?  - **要点:平滑在接收端"每帧"做、与包到达节奏解�?* �?body 位姿在任�?tickrate 下都连续�?
### T5. 远程船物理开 + 每物理步写速度(SP2 独有,SR2 不抄物理,但可�?每帧写速度"思路)

- [`NetworkAircraftScript.FixedUpdate`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/NetworkAircraftScript.cs:416) 远程船每物理�?`RigidBody.velocity/angularVelocity = SyncData` �?刚体积分提供包间连续运动,T2 �?lerp 只是小修正�?- SR2 幽灵�?kinematic 不启用物理积�?�?`EngineVisualSync.InjectGhostMotion` 已在每帧�?kinematic 刚体写速度/角速度(烟雾�?[:499](../Assets/Scripts/Net/EngineVisualSync.cs:499));外推/速度注入思路可直接复用�?
### T6. 角色混合插�?外推(另一个通用范式,供选型)

- [`NetworkCharacterScript.FixedUpdate`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/NetworkCharacterScript.cs:289):
  - `num = 当前物理时间 - 最近包时间`;`t = Clamp01(num / 0.1s)`([`:300-301`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/NetworkCharacterScript.cs:300))�?**上一包→目标 0.1s 内插�?*;
  - 外推候�?`target + velocity×num`([`:322`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/NetworkCharacterScript.cs:322))与插值按 `_currentExtrapolationBlend` 混合;
  - 再叠�?`Lerp(cur, 结果, factor*dt*10)` 平滑([`:323`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/NetworkCharacterScript.cs:323));距离 >5m 直接瞬移([`:326-331`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/NetworkCharacterScript.cs:326))�?
### T7. 发送端 Delta 兴趣 + top-N(带宽,非平�?

- [`CraftStateSerializer.SerializeWrite`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/CraftStateSerializer.cs:149) �?body 全发、子 body �?`Delta>0.1f`,�?Delta 降序每包 top-5;`BodySyncData.Update/Delta`([`BodySyncData.cs:89-118`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/SyncData/BodySyncData.cs:89))�?*与我�?body 顺序索引契约冲突(需先引 Id,�?body-sync-2026-08-18.md P2),不在此方案内�?*

---

## 3. 方案(适配 SR2 幽灵 kinematic 模型)

### P0 —�?高价值最小改�?�?R1 + R2,卡顿主因)

**�?body 姿态参与插�?*(�?R1):�?`TryGetInterpolatedState`,在现�?Position/Heading 插值旁补齐:
- `BodyPositions`: `interp.BodyPositions[i] = Vector3.Lerp(a.BodyPositions[i], b.BodyPositions[i], pct)`(两列表同长同索引,各自 `Mathf.Min` + 越界兜底);
- `BodyRotations`: **�?Quaternion �?Slerp**(�?R5):`Quaternion.Slerp(Quaternion.Euler(a.BodyRotations[i]), Quaternion.Euler(b.BodyRotations[i]), pct).eulerAngles`�?
�?�?body 位姿�?20Hz 包间连续滑动,不再每包跳一次。这�?一卡一�?�?*直接消除�?*�?
**�?缓冲欠载�?外推"不冻�?*(�?R2 + 部分 R4):`renderTime �?最新样本` �?把最新包�?`Velocity` 外推:`interp.Position = newest.Position + newest.Velocity * (renderTime - newestArrivalTime)`(地面坐标,velocity 即地面坐�?可直接乘);朝向可冻结或按最近两包角速度估测前进(可�?;body 相对位姿冻结(随根前进,不跳)�?*外推封顶 0.25s(SP2 T1 同款)**,超时则冻�?防断流跑飞。→ 丢包/抖动期间飞船**继续平滑前进**,而不�?停→�?�?
**�?可�?顺手)体面控制 R2 命中�?*:`renderDelay` 仍保�?�?P2),但即便命中欠载也不可�?有②外推兜底)�?
### P1 —�?SP2 指数平滑 + 瞬移阈�?修抖/自愈,抗抖最�?

- 每个 `RemoteCraft` 增持久化"当前平滑状�?:`Vector3d SmoothedPos`、`Quaternion SmoothedHeading`、`BodyPose[] SmoothedBodies`(�?body 索引对齐)�?- 每帧:`interp`(P0 产物)作为 **Target**:
  - 位置:`k = Clamp01(Lerp(0.1f, 1f, |v|*0.02f))`(T2),`SmoothedPos = Vector3d.Lerp(SmoothedPos, targetPos, 1 - Pow(1-k, dt*rate))`;旋转 `SmoothedHeading = Slerp(SmoothedHeading, targetHeading, 2.5f*dt)`;
  - �?body:`SmoothedBodies[i].pos = Lerp(cur, target, 10*dt)`、`.rot = Slerp(cur, target, 10*dt)`;**偏差 < 0.01 直接快照**(T4);
  - **瞬移阈�?*:`(targetPos - SmoothedPos).magnitude > 50~100m` 或单 body 相对误差过大 �?直接快照(T3,生成/大修�?失步秒对�?�?- 应用�?`ApplyRemoteState`/`ForceRemoteHeading`)改喂 **Smoothed** 值。`LateUpdate` 写回逻辑不变�?- 收益:指数平滑**逐帧收敛、天然吃抖动**(包迟�?小跳只引起微小偏�?不自已纠正成跳变);与包节奏解�?�?tickrate 也顺。代�?额外滞后 �?时间常数(10·dt �?100ms 内收�?,�?P0 外推互补(外推补延迟、平滑消�?�?
### P2 —�?自适应 + 带宽(高延迟体验收�?

- **自适应 renderDelay / 外推�?*:RTT 已有现成测量(`ClientPingMs`/`peer.PingMs`,ping/pong,[:923-940](../Assets/Scripts/Net/MpNetworkManager.cs:923))。`oneWay �?RTT/2`;renderDelay �?`max(保底�?.5×发包间隔, ~2×EMA(包间隔抖�?)`;稳态外推量 `�?oneWay �?renderDelay`(SP2 直接用包时间戳差,我们无时钟同�?�?RTT/2 近似)�?- **per-body velocity/angularVelocity(对齐 SP2 T1/T5 的保真度)**:发送端每个 body 附加 `RigidBody.velocity/angularVelocity`(或相邻包差分估测)�?接收端对**�?body 相对位姿做延迟外�?*(转轴摆动/轮子转�?残骸翻滚在丢包间隙也连续)。带�?+~24B/body,10 body �?+240B/包�?- **Quaternion32 压缩**(SP2 Writer/Reader,~4B/四元�?:`BodyRotations` 12B→~4-5B,�?per-body velocity 腾带�?净增可忽略�?
---

## 4. 可抄 / 不可�?适配幽灵 kinematic)

**可抄(直接移植)**
- �?延迟×速度外推(T1,CraftStateSerializer/NetworkBodyScript 双例验证);
- �?速度自适应指数平滑 + 旋转 2.5·dt Slerp(T2);
- �?>100m 瞬移阈�?T3);
- �?�?body 相对位姿 `10·dt` 指数平滑 + <0.01 快照(T4,BodyScript.OnUpdate);
- �?RTT/2 估延�?+ jitter EMA 自适应(T2 配套);
- �?Quaternion32 压缩、per-body velocity/angularVelocity(T1/T5 数据�?�?
**需适配 / 不抄**
- �?**远程船物理保持开�?+ RigidBody 积分 + 每物理步写速度**(T5):SR2 幽灵�?kinematic、物理禁�?既定模式),**�?每帧直接�?Transform + P0/P1 平滑�?替代**;烟雾速度注入(EngineVisualSync.InjectGhostMotion)可复用为外推的视觉一致性�?- �?**FishNet tick 时钟同步**:SP2 �?`num = 接收端物理时�?�?包内发送时间` 依赖双端�?tick;SR2 无时钟同�?�?用到达时间差 + RTT/2 近似�?- �?**ParentBody �?/ Delta top-N 兴趣**:body 顺序索引契约(�?Id)暂不支持子集,保持整船全发(�?body-sync-2026-08-18.md)�?
---

## 5. 风险 / 待验�?
- 插�?平滑�?**body 数量变化**(分离/对接/残骸):`SmoothedBodies` 数组需�?body 数重�?或按索引 min 兜底;�?multi-craft 生命周期对账统一处理�?- **欧拉插值必须走 Quaternion Slerp**(R5),否则 350°�?0° 绕转�?- **外推�?急转/刹车"时短暂过�?*:SP2 �?0.25s 封顶 + 瞬移阈值吸�?SR2 用较小封�?0.1~0.25s)+ 速度自适应 k 缓解,待实测调参�?- `RecalculateFrameState` �?`positionDelta` 与平滑层写入顺序(P0 不引入新顺序依赖;P1 �?Smoothed 喂给现有应用�?顺序不变)�?- 物理禁用 + 直接�?Transform 的抖动上�?20Hz 包间 50ms 线性插值误�?�?v×50ms(高速船可达数米),P1 指数平滑 + P2 外推能压到亚米级;超阈值瞬移保证不漂移�?- �?tickrate(60Hz)�?P1 �?`10·dt` 平滑时间常数会缩�?60Hz�?6.7ms/�? �?�?`1-Pow(1-k, dt*rate)` 帧率无关写法�?
---

## 6. 里程�?
- **P0(�?已实�?**:�?BodyPoses 参与插�?Slerp body 旋转 + Lerp body 位置);�?缓冲欠载外推(velocity×(renderTime−arrival),封顶 0.25s)。改 `TryGetInterpolatedState` + `ApplyRemoteBodyPoses` 周边,`RemoteDataPack` 结构不动�?- **P1(�?已实�?**:`RemoteCraft` 增加 SmoothedPos/SmoothedSrfRel/SmoothedBodies + 速度自适应 k + 10·dt body 平滑 + <0.01 快照 + >100m 瞬移;应用端喂平滑值�?- **P2(可�?**:RTT/2 + jitter EMA 自适应 renderDelay/外推�?per-body velocity/angularVelocity;Quaternion32 压缩�?
---

## 7. 实施记录

- **2026-08-XX**:方案分析定稿。SP2 反编译核�?T1 外推(CraftStateSerializer.cs:55/76/88、NetworkBodyScript.cs:166)、T2 速度自适应平滑(CraftStateSerializer.cs:84-94)、T3 瞬移(CraftStateSerializer.cs:78-81)、T4 body 位姿平滑+快照(BodyScript.cs:660-679)、T5 每物理步写速度(NetworkAircraftScript.cs:416)、T6 角色混合插�?NetworkCharacterScript.cs:289-335)。SR2 现状核实:R1 body 不插�?`TryGetInterpolatedState` `interp=b`)、R2 欠载冻结([:1657-1662])、R3 renderDelay 固定、R4 无外推。P0/P1/P2 方案如上�?- **2026-08-XX**:调试工具落地(方案 A+B,�?§8)。编�?0 错误 0 警告�?- **2026-08-XX**:P0+P1 落地(`MpNetworkManager.cs`):
  - `RemoteCraft` 新增平滑状�?`SmoothedPos/SmoothedSrfRel/SmoothedBodyPos/SmoothedBodyRot/HasSmoothed` 与诊断计�?`ExtrapolatedFrames`;
  - `TryGetInterpolatedState`:`BodyPositions/BodyRotations` 参与插�?位置 Lerp、旋�?Quaternion Slerp 防欧拉绕�?浅拷贝共享缓冲列�?�?插值结�?*新建列表**);欠载分支�?速度外推(封顶 0.25s,�?ExtrapolatedFrames)"而非冻结;
  - 新增 `ApplyRemoteSmoothing`(P1:速度自适应 k 指数收敛 + 旋转 2.5·dt Slerp + �?body 10·dt 平滑�?<0.01 快照 + >100m 瞬移)+ `SnapSmoothedBodies`(首帧/body 数量变化快照重对�?;
  - `UpdateRemoteCrafts` �?`ApplyRemoteState` 前插�?`ApplyRemoteSmoothing`;周期日志�?`extrap=` 计数。编�?0 错误 0 警告�?- **2026-08-XX**:实测反馈"0 模拟延迟、双方静止仍位置跳动" �?排查与加�?编译 0 错误 0 警告):
  - **分析**:0 延迟静止时只�?插值分�?+ 平滑"运行,静态目标下平滑数学不会产生跳动;跳动源更可能是①每帧 4 �?List 分配 �?GC 卡顿/欠载毛刺;②发送端 `Assembly.Bodies` 顺序/数量在相邻包间变化时按索引插值把"不同 body 位姿"互插;③NaN 坏包污染平滑�?④欠载外推在微停顿后与新包插值路径的接缝�?  - **加固**:
    - body 插值加**数量一致性护�?*(a/b 两包 body 数量不一�?�?回退沿用较新�?b,不跨 body 插�?;
    - body 插�?平滑输出�?*�?RemoteCraft 复用缓冲**(`ReuseInterpBodyPos/Rot`、`ReuseSmoothBodyPos/Rot`),消除热路径每帧分�?
    - `ApplyRemoteSmoothing` �?**NaN/Inf 防御**(非法目标直接快照)+ **静止锁定**(速度<0.05m/s 且已贴近 0.01m �?直接锁目�?杜绝"双方不动"时的微动/漂移);
    - 新增**跳动诊断**:`LastMoveDeltaM`(本帧应用位置相对上帧位移)+ `LastBodyPoseDeltaM`(�?body 最大位�?,进周期日�?0 延迟+静止应≈0;>0.5m 即跳�?�?  - **待双端复�?*:看日�?`MP smoothing` �?`move`/`body` 两值定位跳动源(�?or body),再对症�?  - **待双端实�?�?**:NetSim 注入 150ms/30ms 抖动对比修复�?underrun 冻结-跳变、body 每包�?�?修复�?欠载外推继续前进、body 平滑)�?
- **2026-08-XX**:依据 `Player.log` 锁定跳动根源并修�?编译 0 错误 0 警告):
  - **日志实测(tick=120Hz, renderDelay=40ms, 全程 NetSim 未开)**:
    - 发送端(客户�?VM)实际发包间隔 `gapEMA=202ms`(�?-10Hz,远低�?120Hz tick)�?接收�?`underrun=71.5%`,`extrap=91`;此时 `moveDelta=0.35m`(**跳动确与"低速发�?+ 欠载"强相�?*,非静止本�?;
    - 包流恢复健康(gap�?5ms)�?`moveDelta=0.00m bodyDelta=0.00m posErr=0.0m`(**静止时平滑层纹丝不动,已验�?*);
    - **结论:根因不是平滑�?�?发送端有效发包率远低于 tick + 固定 renderDelay=40ms 远小于实测间�? �?恒定欠载 �?冻结/外推接缝跳动**;另发�?`ControlVisualSync.ApplyRemoteControls` 每帧�?"Index out of range"(418 次刷�?拖慢性能、淹没日�?�?  - **修复**:
    - **自适应渲染回看(治欠�?跳动)**:`lookback = clamp(max(固定RenderDelay, 1.5×gapEMA), �?00ms)`(per-remote-craft,实测间隔自适应)�?发送端低速时缓冲不再欠载,插值接�?平滑前进(代价:渲染滞后≈回看量,SP2 同思路);
    - **激活组 off-by-one(治日志刷�?+ 同步失效)**:`CommandPodScript` 激活组 1-indexed(1..10),接收端旧代码 `i=0..9` �?`SetActivationGroup(0)` �?`ActivationGroupStates[-1]` 每帧异常 �?�?`i=1..n` 对应列表 `[i-1]`(`ControlVisualSync.cs`);
    - 日志行加 `lookback=` 便于复测验证自适应是否生效�?  - **复测预期**:gapEMA�?00ms �?`lookback�?00ms`、`underrun%` 大幅下降、`moveDelta` 不再出现 0.35m 级单帧跳(变为平滑的插值位�?;异常刷屏消失�?
- **2026-08-22(静止�?滑动"定位与修�?编译 0 错误 0 警告)**:
  - **现象**:双方静止(0 延迟)仍见"位置滑动"(平滑漂移,非跳�?;用户判断与帧率无关�?  - **日志关键**:新日志所有行 `moveDelta=0.00 bodyDelta=0.00 posErr=0.0`(我写入的根位置恒定、与最新包一�?,`tfDrift` 新增诊断待复�?`extrap=77~119` 帧、`underrun 13.7~20.4%`(自适应 lookback 已把 71.5% 压下�?�?  - **两个可疑根源(皆在本码可修范围�?**:
    1. **P1 位置平滑收敛过慢**:�?`alpha=1-Pow(1-k, dt*10)` �?k=0.1 时时间常数≈**0.95s**,�?SP2(逐物理步 50Hz k=0.1 �?�?.2s)**�?5 �?*;任何残差/欠载外推造成的偏移都会拖成持续数秒的"滑动",�?�?.005m/�?�?F2 日志显示 0.00 �?与日志不矛盾�?    2. **静止锁定过严**:�?`speed<0.05 && 误差<0.01m` 才锁;发送端残余速度�?0.05 m/s 即永不锁�?�?平滑层永远在蠕动�?  - **修复**(`MpNetworkManager.cs ApplyRemoteSmoothing`):
    - 收敛速率 `dt*10 �?dt*50`(SP2 等价,时间常数�?.2s,帧率无关);
    - 锁定放宽 `speed<0.5 m/s && 误差<0.05m �?快照`(静止纹丝不动,杜绝蠕动);
    - 新增 `tfDrift` 诊断:本帧写入前读实际 `Transform.position` vs 上一帧写入�?�?`moveDelta=0` �?`tfDrift` 持续>0 �?滑动来自**游戏层在 Update 写入后移动了 ghost**(地表锁定/轨道推进/相机),而非我们的写�?只进周期日志�?  - **复测判据**:静止时若 `moveDelta=0 �?tfDrift=0` �?平滑层已纹丝不动,滑动来自游戏�?需进一步查 InContactWithPlanet 地表锁定/RecenterTransformOnCoM);�?`moveDelta` 出现�?0 小数 �?平滑收敛/锁定已生效�?
- **2026-08-22 第二轮日�?tfDrift 复测)与高精度诊断追加**:
  - **tfDrift=0.00m 全行** �?**游戏层没有移�?ghost 变换**(我写入后即稳)�?排除"游戏层移动根位置"假设�?  - `moveDelta` 绝大多数 0.00;偶发 0.19/0.08/0.03m 全部伴随 `posErr=0.1~0.2m` �?那些时段 craft 真实在动(~1.7m/s,posErr≈速度×lookback),属正确复�?
  - 剩唯一可能:**发送端数据缓慢漂移(~0.1m/s)** —�?0.003m/�?�?F2 显示 0.00、`posErr�?.01m` �?F1 也显�?0.0,全部旧日志测不出但肉眼可见�?  - **新增高精度诊�?*(`MpNetworkManager.cs`,3s 窗口):
    - `move3s=` 3s 累计渲染位移(F3)—�?.1m/s 漂移 3s�?.3m;
    - `pktJump=` 3s 内最大单包位置跳�?F3)——发送端数据是否跳变;
    - `vel=` 最新包速度(F3)——发送端上报的残余速度;
    - `newest=(x,y,z)` 最新包位置(F4)——跨日志对比是否漂移;
    - `headYaw=`/`head3s=` 应用朝向 Yaw �?3s 累计朝向变化(�?——慢旋转同样会被感知�?滑动"(body 相对质心有偏移时尤甚)�?  - **复测判据(3s 窗口)**:`vel�? �?move3s>0.3m` �?平滑层在常数目标下漂�?bug);`vel�?.1` �?发送端上报残余速度,"静止"实为慢漂;`pktJump>0 �?vel�?` �?发送端采样跳变;`head3s` 大�?`move3s�?` �?朝向漂移�?
- **2026-08-22 第三轮日�?高精度诊�?�?锁定真正根因 = 发送端速度帧错�?编译 0 错误 0 警告)**:
  - **日志铁证**:`vel=158.848 m/s` **恒定不变**(所有行相同),�?`newest` 位置只以 ~0.002 m/s 缓慢漂移、`pktJump` 最�?0.04m(包位置稳�?�?速度与位�?*自相矛盾**;`move3s` 却高�?0.3~83m/3s(幽灵突发大位�?�?  - **根因**:`PlanetVectorToSurfaceVector` �?*纯旋�?*(PlanetNode.cs:445 `RotateVectorAroundYAxis(v,-RotationAngle)`,**不减去行星自�?ω×r �?*)。发送端 `vel=PlanetVectorToSurfaceVector(craft.Velocity)` �?落地/静止船的惯性速度(≈行星自转线速度 158.85 m/s)被原样转进地表系 �?上报恒定�?158.85 m/s。接收端欠载外推 `Position+Velocity×dtCapped` �?158.85×0.25�?*40m** 反复注入 �?突发瞬移(正是用户看到�?位置滑动/跳动")。此�?`moveDelta`/`tfDrift` �?0 是因为外推的 target 很快被新包拉回、单帧采样恰�?0�?  - **修复**(`MpNetworkManager.cs`):
    1. **发送端**(`TrySampleLocalCraft`):正确地表相对速度 = `PlanetVectorToSurfaceVector(craft.Velocity) �?CalculateSurfaceVelocity(pos)`(与游�?GroundedSurfaceVelocity/CraftNode.cs:1367 同公�?�?静止�?vel�?,外推不再放大;
    2. **接收�?*(`ApplyRemoteState` + 生成路径 `CreateLaunchLocation`):惯性速度 = `SurfaceVectorToPlanetVector(data.Velocity) + SurfaceVectorToPlanetVector(CalculateSurfaceVelocity(data.Position))`(加回自转线速度,避免幽灵惯性速度错成 �?58.85 m/s 导致地表锁定被清后漂�?;
    3. 附带收益:静止锁定(原判�?speed<0.05 �?158.85 永不触发)现在能真正生效�?  - **复测判据**:静止�?`vel�?`、`move3s�?`、`moveDelta=0`、`pktJump�?` �?幽灵纹丝不动;移动�?`vel` 为真实地表相对速度、外推平滑不再瞬移�?
- **2026-08-22 双端复测(本地 VM)�?确认解决(编译 0 错误 0 警告)**:
  - **静止(零输�?�?NetSim 150ms/10ms 开启时�?**:
    - `vel�?.027~0.06 m/s`(修复前恒 **158.85 m/s**);
    - `move3s�?.01~0.13m/3s`(修复�?0.3~83m)、`moveDelta=0.00`、`pktJump�?.002~0.03m`、`posErr=0.00m`、`tfDrift=0.00m` �?**幽灵纹丝不动**;
  - **移动(用户操控)**:`vel=1~9 m/s`、`move3s=4~26m/3s`、`posErr≈速度×lookback(0.4~1.7m)` �?正确复刻真实运动,无瞬�?
  - **结论**:速度帧错误根因已�?高延�?150ms+10ms 抖动)下静�?平滑均成立。P0+P1+自适应 lookback+速度帧修�?全套生效�?
---

## 8. 调试工具(已实�?NetSim 延迟模拟 + 接收端平滑诊�?

> 动机:没有 Steam 好友�?TCP+本地 VM 无法暴露真实公网的延�?抖动/丢包 �?平滑代码(§3)的问题测不出来。落地两件套:

### 8.1 延迟模拟传输�?`LagSimTransport`(方案 A)

- **文件**:`Assets/Scripts/Net/LagSimTransport.cs`(新增,已入 aMptest.csproj)。实�?`IMpTransport` **装饰�?*,包住任意底层传输(默认场景 = `TcpTransport`),只在**接收路径**(`inner.OnDataReceived` �?延迟队列 �?到点再触发上�?注入网络条件;`SendTo/Broadcast/超时/生命周期` 全部直�?�?`MpNetworkManager` 完全透明�?- **控制台命�?*(DevConsole):
  - `NetSimDelay <ms>` 基础单向延迟;`NetSimJitter <ms>` 均匀抖动 ±ms;`NetSimLoss <pct>` 丢包�?`NetSimDuplicate <pct>` 重复�?
  - `NetSim` 查看配置+活跃实例投递统�?delivered/dropped/inFlight);
  - `NetSimReset` 归零(后续开房不再包�?已包装实例立即直�?�?- **挂载�?*:`TcpHostLobby/TcpJoinLobby`(Mod.cs)+ UI TCP 按钮(MultiPlayerUI.cs)创建传输时经 `LagSimTransport.MaybeWrap` 自动包装(仅当启用);Steam 暂不包装(�?`Transport is SteamTransport` 预检,见代码注�?�?- **可复�?*:开房前设好 �?`TcpHostLobby 25555` / `TcpJoinLobby <ip> 25555` 双端各自配置 �?模拟非对�?对称延迟;会话中改�?*逐包实时生效**;`NetSimDelay 50` �?`150 30` + `NetSimLoss 2` �?�?`50` 可观察平滑自愈�?- 语义说明:延迟�?接收�?,RTT 探测(ClientPingMs)会如实累计两端单向延�?接收端看到的包到达分布与真实公网一�?正好喂给 §3 的插�?外推逻辑�?
### 8.2 接收端平�?网络诊断(方案 B)

- **周期日志(唯一诊断出口)**:`MP smoothing P{pid}: ...` �?3 秒一�?�?Player.log 可回�?,�?
  - 缓冲 `buf`、`renderDelay`/`lookback`、`gapEMA`/`jitterEMA`、`underrun %`、`snap`/`extrap`/`interpPct`;
  - 跳动/漂移指标:`moveDelta`(本帧根位�?、`bodyDelta`、`tfDrift`(写入后游戏层是否又移�?ghost)�?    `move3s`/`pktJump`(3s 累计渲染位移 / 单包跳变)、`vel`(发送端上报速度)、`headYaw`/`head3s`(朝向漂移)�?    `newest`(最新包位置 F4)、`posErr`(渲染位置 vs 最新包)�?  - **已移除悬浮窗(`NetStatsUI`/`OnGUI`)**:用户不需要窗�?诊断只保�?Mod.LogLobby 日志�?- **实现位置**:`RemoteCraft` 诊断字段 + `PushSample` 抖动 EMA + `TryGetInterpolatedState` 欠载/插值计�?+ `UpdateRemoteCrafts` 周期日志(全在 MpNetworkManager.cs)�?- **验证闭环**:`NetSimDelay 150 30` 飞行观察日志:jit�?0ms、underrun%>0(�?P0②外推时)�?复现"一卡一�?;实现 §3 P0 后同一条件�?underrun 仍计数但**视觉不再冻结跳变**(外推接管),posErr 反映剩余滞后 �?定量验证 P0/P1 效果�?
### 8.3 用法速查

**UI 方式(推荐)**:联机面板 �?「网络延迟模�?NetSim)」分�?独立于调试组,不随 DebugMode 隐藏):
1. 输入「延�?ms)/抖动±(ms)/丢包(%)�?
2. 打开「启用延迟模拟」开�?
3. 用「TCP 创建大厅 / TCP 加入」开房联�?�?状态行显示 `ON·已生效`�?
**控制台方�?*:
```
NetSimDelay 150      # 只设数�?不开总开�?NetSimJitter 30
NetSimLoss 2
NetSimOn             # 总开�?开(开房时自动包装;活跃实例实时生效)
TcpHostLobby 25555   # 或客户端 TcpJoinLobby <ip> 25555
NetSim               # 查看当前配置与投递统�?日志)
NetSimOff            # 总开�?�?直�?其它 TCP 场景延迟尽量�?NetSimReset          # 清空数�?关总开�?```
诊断数值看 Player.log �?`MP smoothing P#` 3s 周期行�?
> **开关语�?*:数值命令与总开关分离——`NetSimDelay/Jitter/Loss/Duplicate` 只设数�?**实际生效�?`NetSimOn/Off`(�?UI 开�?控制**。关�?直�?不延迟不丢包),保证普�?TCP 测试延迟尽量�?已启用的活跃会话改开�?改�?*逐包实时生效**�?