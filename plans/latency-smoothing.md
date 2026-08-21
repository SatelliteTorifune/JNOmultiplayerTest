# 远程飞船高延迟平滑方案(latency-smoothing)

> 状态:📋 方案分析(**SP2 反编译参考已核对**;P0 待实施)
> 目标:延迟 >100ms(RTT)时,对面 craft 同步位置**平滑**(不"一卡一卡"),包括整船平移、朝向、每 body 相对位姿(转轴/关节连接的子装配摆动)。
> 关联:[`body-sync.md`](body-sync.md)(BodyPoses 数据源,本方案在**接收端平滑层**上做文章);[`multi-craft-sync.md`](multi-craft-sync.md)(body 数量变化归生命周期对账)。

---

## 0. 一句话结论

**现状卡顿根因:接收端插值只覆盖整船 Position/Heading,`BodyRotations/BodyPositions`(每 body 相对 comRot 位姿)是"每包最新值整体覆盖"→ 每个状态包(20Hz)所有 body 瞬间跳一次;加上缓冲欠载时"冻结→跳变"、无外推导致高延迟滞后/橡皮筋。**
SP2 反编译给出了完整药方:**用"测得延迟×速度外推"补足延迟、速度自适应的指数平滑消跳变、>阈值直接瞬移自愈、每 body 相对位姿 10·dt 指数平滑+近距快照**。SR2 幽灵 kinematic 模型不能照抄 SP2 的物理集成,但上述平滑逻辑全部可直接移植。

---

## 1. 现状与根因(代码核实)

接收端逐帧管线([`MpNetworkManager.cs`](../Assets/Scripts/Net/MpNetworkManager.cs)):

1. 收到状态包 → `PushSample` 入 32 槽环形缓冲(按**到达端 unscaledTime** 排序,[:1079-1091](../Assets/Scripts/Net/MpNetworkManager.cs:1079));
2. 每帧 `UpdateRemoteCrafts`:`renderTime = now - RenderDelayMs/1000`([:1593](../Assets/Scripts/Net/MpNetworkManager.cs:1593)),`TryGetInterpolatedState` 找 renderTime 前后两包插值([:1637-1680](../Assets/Scripts/Net/MpNetworkManager.cs:1637));
3. `ApplyRemoteState(rc, interp)` 写 GroundedSurface*/SetStateVectors/朝向/每 body 位姿/尾焰/部件/控制([:1690-1798](../Assets/Scripts/Net/MpNetworkManager.cs:1690));
4. `LateUpdate` 用 `rc.LastApplied`(插值后状态)重写朝向抗游戏覆盖([:422-436](../Assets/Scripts/Net/MpNetworkManager.cs:422))。

**R1(主因):body 姿态不参与插值,每包整体跳。**
`TryGetInterpolatedState` 里 `Mod.RemoteDataPack interp = b;` 只覆盖 `Position/Velocity/Heading/SrfRel`([:1673-1678](../Assets/Scripts/Net/MpNetworkManager.cs:1673)),`BodyRotations/BodyPositions/EngineThrottles/PartActivated/控制` 全部沿用**较新包 b 整体拷贝**;`ApplyRemoteBodyPoses` 再把每 body 的绝对位置/localRotation 直接写死([:474-491](../Assets/Scripts/Net/MpNetworkManager.cs:474))。→ 每个新包到达(≈50ms 一次)所有 body **瞬间跳到新相对位姿**;整船根是插值平滑的、body 却是跳的 → 机身"每 50ms 抖一下",转轴/关节子装配像橡皮筋。

**R2:缓冲欠载 → 冻结-跳变。**
`renderTime ≥ 最新样本到达时间` 时(抖动尖峰、丢包、renderDelay 偏小),`TryGetInterpolatedState` 直接返回最新原始包([:1657-1662](../Assets/Scripts/Net/MpNetworkManager.cs:1657))→ 飞船**原地冻结**,下一包到达才继续动 → "卡一下、跳一下"。高延迟场景抖动/丢包更多,该分支触发更频繁。

**R3:renderDelay 固定不自适应。**
`RenderDelayMs` 默认 100ms(`SetTickRate` 按 `Clamp(2000/hz,40,400)` 设,[:45-47/552-563](../Assets/Scripts/Net/MpNetworkManager.cs:552))。不随实测抖动/延迟调整:过小→R2 欠载;过大→滞后更明显。

**R4:无外推 → 高延迟滞后/橡皮筋。**
渲染位置 = 最新包 + `RenderDelayMs` 的插值延迟,**不把"网络延迟期间飞船应继续前进"补回来**。RTT>100ms(单向>50ms)时对面实际已飞出很远,渲染还在 150ms+ 之前的位置;对面转向/刹车/加速后误差瞬间放大→被拉回→橡皮筋。

**R5(潜在):body 欧拉角插值要防绕转。**
若直接 `Lerp(BodyRotations[i] euler)` 会在 350°↔10° 这类边界绕一大圈;必须转 Quaternion 后 `Slerp`(相对 comRot 的旋转无万向锁问题)。

---

## 2. SP2 反编译参考(全部已核对,file:line)

> 反编译源:`C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/`。SP2 是**远程船物理保持开启**、FishNet tick 时钟同步;SR2 幽灵 kinematic + 无时钟同步 → **平滑逻辑可抄,物理集成不抄**。

### T1. 延迟×速度外推(SP2 核心,两处独立实现)

- [`CraftStateSerializer.SerializeRead`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/CraftStateSerializer.cs:55) `num2 = Clamp(physicsTime - num, 0, 0.25f)` = **网络延迟**(接收端当前物理时间 − 包内发送端时间,封顶 250ms);
- [`:76`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/CraftStateSerializer.cs:76) `vector5 = vector2 + vector + num2 * vector3`(**pos + velocity×延迟**)——把目标位置"播到应该现在的位",**不再需要大 renderDelay 去等延迟**,滞后被抵消;
- 旋转同样外推:[`:88-94`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/CraftStateSerializer.cs:88) 按 `angularVelocity × 延迟` 转一个增量角再 Slerp;
- 松散 body 层同款:[`NetworkBodyScript.SerializeRead`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/NetworkBodyScript.cs:166) `SetPositionAndRotation(pos + delta + delay*vel, rot * AngleAxis(delay*angularVel))`。

### T2. 速度自适应指数平滑(消跳变,永远在"追"目标)

- [`CraftStateSerializer.cs:84`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/CraftStateSerializer.cs:84) `num4 = Lerp(0.1f, 1f, |v|*0.02f)`——**慢速船重平滑(0.1)、快速船近瞬移(≈1)**;
- [`:85`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/CraftStateSerializer.cs:85) `position = Vector3.Lerp(position, target, num4)`——逐帧指数收敛,无离散跳变;
- 旋转 [`:94`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/CraftStateSerializer.cs:94) `Slerp(rotation, target, 2.5f*Time.deltaTime)`。

### T3. 大误差直接瞬移(自愈,不慢滑)

- [`CraftStateSerializer.cs:78-81`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/CraftStateSerializer.cs:78) `(target-pos).sqrMagnitude > 10000`(**>100m**)→ 直接 `position = target`(生成/大修正/失步时秒对齐,避免全场慢滑)。

### T4. 每 body 相对位姿指数平滑 + 近距快照(对应我们的 BodyPositions/BodyRotations)

- 子 body(有 ParentBody)收到状态只存 `SyncData.TargetPosition/TargetRotation`([`CraftStateSerializer.cs:109-123`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/CraftStateSerializer.cs:109)),实际应用在 [`BodyScript.OnUpdate`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Craft/BodyScript.cs:660):
  - 偏差 < 0.01 → 直接快照并清 Target(防持续微抖);
  - 否则 `localPosition = Vector3.Lerp(cur, target, 10f*Time.deltaTime)`、`localRotation = Quaternion.Slerp(cur, target, 10f*Time.deltaTime)`([`:667/679`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Craft/BodyScript.cs:667))。
  - **要点:平滑在接收端"每帧"做、与包到达节奏解耦** → body 位姿在任意 tickrate 下都连续。

### T5. 远程船物理开 + 每物理步写速度(SP2 独有,SR2 不抄物理,但可抄"每帧写速度"思路)

- [`NetworkAircraftScript.FixedUpdate`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/NetworkAircraftScript.cs:416) 远程船每物理步 `RigidBody.velocity/angularVelocity = SyncData` → 刚体积分提供包间连续运动,T2 的 lerp 只是小修正。
- SR2 幽灵全 kinematic 不启用物理积分,但 `EngineVisualSync.InjectGhostMotion` 已在每帧给 kinematic 刚体写速度/角速度(烟雾用,[:499](../Assets/Scripts/Net/EngineVisualSync.cs:499));外推/速度注入思路可直接复用。

### T6. 角色混合插值+外推(另一个通用范式,供选型)

- [`NetworkCharacterScript.FixedUpdate`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/NetworkCharacterScript.cs:289):
  - `num = 当前物理时间 - 最近包时间`;`t = Clamp01(num / 0.1s)`([`:300-301`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/NetworkCharacterScript.cs:300))→ **上一包→目标 0.1s 内插值**;
  - 外推候选 `target + velocity×num`([`:322`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/NetworkCharacterScript.cs:322))与插值按 `_currentExtrapolationBlend` 混合;
  - 再叠加 `Lerp(cur, 结果, factor*dt*10)` 平滑([`:323`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/NetworkCharacterScript.cs:323));距离 >5m 直接瞬移([`:326-331`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/NetworkCharacterScript.cs:326))。

### T7. 发送端 Delta 兴趣 + top-N(带宽,非平滑)

- [`CraftStateSerializer.SerializeWrite`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/CraftStateSerializer.cs:149) 根 body 全发、子 body 仅 `Delta>0.1f`,按 Delta 降序每包 top-5;`BodySyncData.Update/Delta`([`BodySyncData.cs:89-118`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/SyncData/BodySyncData.cs:89))。**与我们 body 顺序索引契约冲突(需先引 Id,见 body-sync.md P2),不在此方案内。**

---

## 3. 方案(适配 SR2 幽灵 kinematic 模型)

### P0 —— 高价值最小改动(修 R1 + R2,卡顿主因)

**① body 姿态参与插值**(修 R1):改 `TryGetInterpolatedState`,在现有 Position/Heading 插值旁补齐:
- `BodyPositions`: `interp.BodyPositions[i] = Vector3.Lerp(a.BodyPositions[i], b.BodyPositions[i], pct)`(两列表同长同索引,各自 `Mathf.Min` + 越界兜底);
- `BodyRotations`: **转 Quaternion 后 Slerp**(修 R5):`Quaternion.Slerp(Quaternion.Euler(a.BodyRotations[i]), Quaternion.Euler(b.BodyRotations[i]), pct).eulerAngles`。

→ 每 body 位姿在 20Hz 包间连续滑动,不再每包跳一次。这是"一卡一卡"的**直接消除项**。

**② 缓冲欠载改"外推"不冻结**(修 R2 + 部分 R4):`renderTime ≥ 最新样本` 时,把最新包按 `Velocity` 外推:`interp.Position = newest.Position + newest.Velocity * (renderTime - newestArrivalTime)`(地面坐标,velocity 即地面坐标,可直接乘);朝向可冻结或按最近两包角速度估测前进(可选);body 相对位姿冻结(随根前进,不跳)。**外推封顶 0.25s(SP2 T1 同款)**,超时则冻结,防断流跑飞。→ 丢包/抖动期间飞船**继续平滑前进**,而不是"停→跳"。

**③(可选,顺手)体面控制 R2 命中率**:`renderDelay` 仍保底(见 P2),但即便命中欠载也不可见(有②外推兜底)。

### P1 —— SP2 指数平滑 + 瞬移阈值(修抖/自愈,抗抖最稳)

- 每个 `RemoteCraft` 增持久化"当前平滑状态":`Vector3d SmoothedPos`、`Quaternion SmoothedHeading`、`BodyPose[] SmoothedBodies`(与 body 索引对齐)。
- 每帧:`interp`(P0 产物)作为 **Target**:
  - 位置:`k = Clamp01(Lerp(0.1f, 1f, |v|*0.02f))`(T2),`SmoothedPos = Vector3d.Lerp(SmoothedPos, targetPos, 1 - Pow(1-k, dt*rate))`;旋转 `SmoothedHeading = Slerp(SmoothedHeading, targetHeading, 2.5f*dt)`;
  - 每 body:`SmoothedBodies[i].pos = Lerp(cur, target, 10*dt)`、`.rot = Slerp(cur, target, 10*dt)`;**偏差 < 0.01 直接快照**(T4);
  - **瞬移阈值**:`(targetPos - SmoothedPos).magnitude > 50~100m` 或单 body 相对误差过大 → 直接快照(T3,生成/大修正/失步秒对齐)。
- 应用端(`ApplyRemoteState`/`ForceRemoteHeading`)改喂 **Smoothed** 值。`LateUpdate` 写回逻辑不变。
- 收益:指数平滑**逐帧收敛、天然吃抖动**(包迟到/小跳只引起微小偏移,不自已纠正成跳变);与包节奏解耦,低 tickrate 也顺。代价:额外滞后 ≈ 时间常数(10·dt ≈ 100ms 内收敛),与 P0 外推互补(外推补延迟、平滑消抖)。

### P2 —— 自适应 + 带宽(高延迟体验收敛)

- **自适应 renderDelay / 外推量**:RTT 已有现成测量(`ClientPingMs`/`peer.PingMs`,ping/pong,[:923-940](../Assets/Scripts/Net/MpNetworkManager.cs:923))。`oneWay ≈ RTT/2`;renderDelay 取 `max(保底≈1.5×发包间隔, ~2×EMA(包间隔抖动))`;稳态外推量 `≈ oneWay − renderDelay`(SP2 直接用包时间戳差,我们无时钟同步,用 RTT/2 近似)。
- **per-body velocity/angularVelocity(对齐 SP2 T1/T5 的保真度)**:发送端每个 body 附加 `RigidBody.velocity/angularVelocity`(或相邻包差分估测)→ 接收端对**每 body 相对位姿做延迟外推**(转轴摆动/轮子转速/残骸翻滚在丢包间隙也连续)。带宽 +~24B/body,10 body ≈ +240B/包。
- **Quaternion32 压缩**(SP2 Writer/Reader,~4B/四元数):`BodyRotations` 12B→~4-5B,为 per-body velocity 腾带宽,净增可忽略。

---

## 4. 可抄 / 不可抄(适配幽灵 kinematic)

**可抄(直接移植)**
- ✅ 延迟×速度外推(T1,CraftStateSerializer/NetworkBodyScript 双例验证);
- ✅ 速度自适应指数平滑 + 旋转 2.5·dt Slerp(T2);
- ✅ >100m 瞬移阈值(T3);
- ✅ 每 body 相对位姿 `10·dt` 指数平滑 + <0.01 快照(T4,BodyScript.OnUpdate);
- ✅ RTT/2 估延迟 + jitter EMA 自适应(T2 配套);
- ✅ Quaternion32 压缩、per-body velocity/angularVelocity(T1/T5 数据面)。

**需适配 / 不抄**
- ❌ **远程船物理保持开启 + RigidBody 积分 + 每物理步写速度**(T5):SR2 幽灵全 kinematic、物理禁用(既定模式),**用"每帧直接写 Transform + P0/P1 平滑层"替代**;烟雾速度注入(EngineVisualSync.InjectGhostMotion)可复用为外推的视觉一致性。
- ❌ **FishNet tick 时钟同步**:SP2 的 `num = 接收端物理时间 − 包内发送时间` 依赖双端同 tick;SR2 无时钟同步 → 用到达时间差 + RTT/2 近似。
- ❌ **ParentBody 树 / Delta top-N 兴趣**:body 顺序索引契约(无 Id)暂不支持子集,保持整船全发(见 body-sync.md)。

---

## 5. 风险 / 待验证

- 插值/平滑后 **body 数量变化**(分离/对接/残骸):`SmoothedBodies` 数组需随 body 数重建,或按索引 min 兜底;归 multi-craft 生命周期对账统一处理。
- **欧拉插值必须走 Quaternion Slerp**(R5),否则 350°↔10° 绕转。
- **外推在"急转/刹车"时短暂过冲**:SP2 靠 0.25s 封顶 + 瞬移阈值吸收;SR2 用较小封顶(0.1~0.25s)+ 速度自适应 k 缓解,待实测调参。
- `RecalculateFrameState` 的 `positionDelta` 与平滑层写入顺序(P0 不引入新顺序依赖;P1 的 Smoothed 喂给现有应用点,顺序不变)。
- 物理禁用 + 直接写 Transform 的抖动上限:20Hz 包间 50ms 线性插值误差 ≈ v×50ms(高速船可达数米),P1 指数平滑 + P2 外推能压到亚米级;超阈值瞬移保证不漂移。
- 高 tickrate(60Hz)下 P1 的 `10·dt` 平滑时间常数会缩水(60Hz→16.7ms/步) → 用 `1-Pow(1-k, dt*rate)` 帧率无关写法。

---

## 6. 里程碑

- **P0(待实施)**:① BodyPoses 参与插值(Slerp body 旋转 + Lerp body 位置);② 缓冲欠载外推(velocity×(renderTime−arrival),封顶 0.25s)。改 `TryGetInterpolatedState` + `ApplyRemoteBodyPoses` 周边,`RemoteDataPack` 结构不动。
- **P1(待实施)**:`RemoteCraft` 增加 SmoothedPos/SmoothedHeading/SmoothedBodies + 速度自适应 k + 10·dt body 平滑 + <0.01 快照 + >阈值瞬移;应用端喂平滑值。
- **P2(可选)**:RTT/2 + jitter EMA 自适应 renderDelay/外推量;per-body velocity/angularVelocity;Quaternion32 压缩。

---

## 7. 实施记录

- **2026-08-XX**:方案分析定稿。SP2 反编译核对:T1 外推(CraftStateSerializer.cs:55/76/88、NetworkBodyScript.cs:166)、T2 速度自适应平滑(CraftStateSerializer.cs:84-94)、T3 瞬移(CraftStateSerializer.cs:78-81)、T4 body 位姿平滑+快照(BodyScript.cs:660-679)、T5 每物理步写速度(NetworkAircraftScript.cs:416)、T6 角色混合插值(NetworkCharacterScript.cs:289-335)。SR2 现状核实:R1 body 不插值(`TryGetInterpolatedState` `interp=b`)、R2 欠载冻结([:1657-1662])、R3 renderDelay 固定、R4 无外推。P0/P1/P2 方案如上。
- **2026-08-XX**:调试工具落地(方案 A+B,见 §8)。编译 0 错误 0 警告。

---

## 8. 调试工具(已实现:NetSim 延迟模拟 + 接收端平滑诊断)

> 动机:没有 Steam 好友时,TCP+本地 VM 无法暴露真实公网的延迟/抖动/丢包 → 平滑代码(§3)的问题测不出来。落地两件套:

### 8.1 延迟模拟传输层 `LagSimTransport`(方案 A)

- **文件**:`Assets/Scripts/Net/LagSimTransport.cs`(新增,已入 aMptest.csproj)。实现 `IMpTransport` **装饰器**,包住任意底层传输(默认场景 = `TcpTransport`),只在**接收路径**(`inner.OnDataReceived` → 延迟队列 → 到点再触发上层)注入网络条件;`SendTo/Broadcast/超时/生命周期` 全部直通,对 `MpNetworkManager` 完全透明。
- **控制台命令**(DevConsole):
  - `NetSimDelay <ms>` 基础单向延迟;`NetSimJitter <ms>` 均匀抖动 ±ms;`NetSimLoss <pct>` 丢包率;`NetSimDuplicate <pct>` 重复包;
  - `NetSim` 查看配置+活跃实例投递统计(delivered/dropped/inFlight);
  - `NetSimReset` 归零(后续开房不再包装;已包装实例立即直通)。
- **挂载点**:`TcpHostLobby/TcpJoinLobby`(Mod.cs)+ UI TCP 按钮(MultiPlayerUI.cs)创建传输时经 `LagSimTransport.MaybeWrap` 自动包装(仅当启用);Steam 暂不包装(保 `Transport is SteamTransport` 预检,见代码注释)。
- **可复现**:开房前设好 → `TcpHostLobby 25555` / `TcpJoinLobby <ip> 25555` 双端各自配置 → 模拟非对称/对称延迟;会话中改值**逐包实时生效**;`NetSimDelay 50` → `150 30` + `NetSimLoss 2` → 回 `50` 可观察平滑自愈。
- 语义说明:延迟打"接收侧",RTT 探测(ClientPingMs)会如实累计两端单向延迟;接收端看到的包到达分布与真实公网一致,正好喂给 §3 的插值/外推逻辑。

### 8.2 接收端平滑/网络诊断(方案 B)

- **悬浮窗**:`NetStatsUI 1/0`。每远程船显示:
  - `buf` 缓冲余量、`gap` 实测包间间隔 EMA、`jit` 抖动 EMA(**应≈NetSim 注入的抖动量**);
  - `underrun %` 缓冲欠载命中率(**>0 即发生了"冻结-跳变"**)、`interpPct` 插值比例、`posErr` 渲染位置 vs 最新包位置滞后(米)。
- **周期日志**:`MP smoothing P{pid}: ...` 每 3 秒一条(同上指标,进 Player.log 可回看)。
- **实现位置**:`RemoteCraft` 诊断字段 + `PushSample` 抖动 EMA + `TryGetInterpolatedState` 欠载/插值计数 + `UpdateRemoteCrafts` 周期日志 + `OnGUI` 悬浮窗(全在 MpNetworkManager.cs)。
- **验证闭环**:开 `NetStatsUI 1` + `NetSimDelay 150 30`,飞行观察:jit≈30ms、underrun%>0(无 P0②外推时)→ 复现"一卡一卡";实现 §3 P0 后同一条件下 underrun 仍计数但**视觉不再冻结跳变**(外推接管),posErr 反映剩余滞后 → 定量验证 P0/P1 效果。

### 8.3 用法速查

**UI 方式(推荐)**:联机面板 → 「网络延迟模拟(NetSim)」分组(独立于调试组,不随 DebugMode 隐藏):
1. 输入「延迟(ms)/抖动±(ms)/丢包(%)」;
2. 打开「启用延迟模拟」开关;
3. 用「TCP 创建大厅 / TCP 加入」开房联机 → 状态行显示 `ON·已生效`。

**控制台方式**:
```
NetSimDelay 150      # 只设数值,不开总开关
NetSimJitter 30
NetSimLoss 2
NetSimOn             # 总开关:开(开房时自动包装;活跃实例实时生效)
TcpHostLobby 25555   # 或客户端 TcpJoinLobby <ip> 25555
NetStatsUI 1         # 悬浮窗观察缓冲/抖动/欠载/位置误差
NetSimOff            # 总开关:关=直通,其它 TCP 场景延迟尽量小
NetSimReset          # 清空数值+关总开关
```

> **开关语义**:数值命令与总开关分离——`NetSimDelay/Jitter/Loss/Duplicate` 只设数值;**实际生效由 `NetSimOn/Off`(或 UI 开关)控制**。关闭=直通(不延迟不丢包),保证普通 TCP 测试延迟尽量小;已启用的活跃会话改开关/改值**逐包实时生效**。
