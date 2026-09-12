# 远程飞船高延迟平滑方案(latency-smoothing)

> 状态:✅ **已实现,且接收端架构已再次重构**——**现行实现 = SP2 式连续外推(dead-reckoning)+ 每帧指数平滑**(见 §9);本文 §0~§7 记载的**插值缓冲 + 自适应 lookback** 方案为**已被取代的上一代实现**,保留作决策记录。
> 目标:延迟 >100ms(RTT)时,对面 craft 同步位置**平滑**(不"一卡一卡"),包括整船平移、朝向、每 body 相对位姿(转轴/关节连接的子装配摆动)。
> 关联:[`body-sync-2026-08-18.md`](body-sync-2026-08-18.md)(BodyPoses 数据源,本方案在**接收端平滑层**上做文章);[`multi-craft-sync-2026-08-16.md`](multi-craft-sync-2026-08-16.md)(body 数量变化归生命周期对账);[`part-switch-sync-2026-08-18.md`](part-switch-sync-2026-08-18.md) §11.5(同一轮排查中发现并修复的激活组 off-by-one)。
>
> ⚠️ **阅读提示(2026-09 校订)**:本文档前几节的"渲染回看 / 插值缓冲 / underrun%"等描述**已不代表当前代码**。当前接收端**不再用插值缓冲渲染**,而是"始终取最新包 + 按 `RTT/2 + 包龄` 连续外推 + 平滑收敛"。以 **§9 为现行事实**,§0~§7 为历史演进。相关残留物(`TryGetInterpolatedState`、`RenderDelayMs`、`UnderrunFrames`)仍在代码里但**已不被使用**。

---

## 9. 【现行实现】SP2 式连续外推(dead-reckoning) — 取代插值缓冲

> 这是**当前真正在跑的接收端管线**。上一代"插值缓冲 + 自适应 lookback"在 Steam 实测中暴露了结构性缺陷(见 §9.1),已被整体替换。

### 9.1 为什么放弃插值缓冲

- 插值缓冲要求"渲染时刻早于最新包到达时刻",回看量必须大于**实测包间隔**;Steam 环境下包间隔突发严重(`gapEMA` 可达 200ms 且抖动大),固定或"1.5×gapEMA"的自适应回看都追不上 → **恒定欠载** → 每个欠载帧跳回最新包 → "一卡一卡"(Steam 实测欠载率 50~76%)。
- 反向调大回看又会让渲染滞后变大,高延迟下橡皮筋更明显——**回看量本身没有安全取值**。
- SP2 的做法更简单:不做插值,**始终拿最新包**,按速度把它"播到现在",失败模式是"位置估计略有偏差",而不是"跳变"。

### 9.2 现行逐帧管线(接收端)

驱动:`MpNetworkManager : MonoBehaviour`,`[DefaultExecutionOrder(1000)]`,挂 `DontDestroyOnLoad` 的 `MPNetwork` 对象上;主循环在 **`Update()`**,`LateUpdate()` 只重写朝向。

```
Update
 └ UpdateRemoteCrafts()
    ├ rc.Node.InContactWithPlanet = true                // 每帧重申,防游戏切回轨道推进
    ├ rc.TryGetNewest(out latest)                        // 取最新包(不再插值)
    ├ ext = latencySec + age                             // ★ 连续外推量(见 9.3)
    ├ latest.Position += latest.Velocity * ext           // ★ dead-reckoning
    ├ latest = ApplyRemoteSmoothing(rc, latest, dt)      // ★ 指数平滑(见 9.4)
    ├ ApplyRemoteState(rc, latest)                       // 写 GroundedSurface*/根/朝向/body 位姿/部件/控制
    └ EngineVisualSync.DriveGhostEngineVisuals(rc)       // 尾焰视觉
```

写 Transform 的最终顺序:① `SetStateVectorsAtDefaultTime`(逻辑状态)→ ② `ApplyRemoteBodyPoses`(body 相对 comRot)→ ③ `RecalculateFrameState`(逐 body 加 positionDelta + 写根位置)→ ④ `LateUpdate` 只重写旋转。

### 9.3 外推量(核心公式)

```csharp
float latencySec = (rc.LatencyMs > 0f ? rc.LatencyMs : rc.GapEmaMs) / 1000f;
float age        = Time.unscaledTime - rc.NewestArrivalTime;
float ext        = latencySec + age;                 // 正常:随包龄持续前进
if (age > Mathf.Max(rc.GapEmaMs * 3f / 1000f, 0.25f))
    ext = latencySec;                                // 长时间无包:冻结在"最新包 + 固定延迟",防幽灵飞走
if (ext > 1.0f) ext = 1.0f;                          // 安全上限 1s
latest.Position += latest.Velocity * ext;
```

- `rc.LatencyMs` = **单向延迟估计**,由 RTT 探测给出:房主侧 `peer.PingMs / 2`,客户端侧 `ClientPingMs / 2`(客户端所有远端船都经房主转发,缺少"对端→房主"那一段,属近似)。
- 无时钟同步,因此**不用包内时间戳**,只用本地 `Time.unscaledTime` 与包到达时刻。
- 目标随时间**连续**前进 ⇒ 包到达/丢失/突发都不会让目标跳变 ⇒ 消除"一卡一卡"。
- 与发送端速度帧配合:速度是**地表相对速度**(发送端 `PlanetVectorToSurfaceVector(Velocity) − CalculateSurfaceVelocity(pos)`),接收端加回自转项(§7 第三轮),否则静止船会以 158.85 m/s 被外推。

### 9.4 平滑层 `ApplyRemoteSmoothing`(现行参数)

| 项 | 现行取值 | 说明 |
|---|---|---|
| NaN/Inf 防御 | `!IsFinite(Position/Velocity)` → 直接快照 | 坏包不污染平滑 |
| 首帧/`dt<=0` | 直接快照 + 返回 | 建立基准 |
| **静止锁定** | `speed < 0.5f && (target−smoothed).sqrMagnitude < 0.0025f`(0.05m)→ 快照 | 杜绝"双方不动"时的蠕动 |
| **速度自适应 k** | `k = Lerp(0.1, 1, min(1, speed * 0.02))`(|v|≥50 m/s 即 k=1) | 慢船重平滑、快船近瞬时 |
| **收敛(帧率无关)** | `alpha = 1 − Pow(1 − k, dt * 50)` | 时间常数 ≈0.2s(等价 SP2 每物理步 50Hz) |
| **瞬移阈值** | 距离 `> 100.0` m → 直接快照 | 生成/大修正/失步时秒对齐 |
| 旋转 | `Slerp(..., Clamp01(2.5 * dt))` | SP2 同款 |
| **每 body** | `alphaBody = Clamp01(10 * dt)`;位置 snap 阈值 `sqrMagnitude < 0.01f`(0.1m)、旋转 `Angle < 0.01f` 度 | 与 body-sync 的索引契约对齐 |
| body 数量变化 | `SmoothedBodyPos.Length != n` → `SnapSmoothedBodies` 重建 | 分离/对接后重对齐 |
| 输出 | 复用缓冲 `ReuseSmoothBodyPos/Rot`(消除每帧 List 分配) | 热路径零 GC |

### 9.5 诊断日志(现行字段)

每 **3 秒**一条 `MP smoothing P<玩家id>:`(只写 `Player.log`,**已移除悬浮窗**):

```
buf=n/32  rtt/2=<F0|?>ms  gapEMA=<F0>ms  jitterEMA=<F0>ms
frames=<n>  snap=<n>  extrap=<n>  interpPct=<F2>
moveDelta=<F2>m  bodyDelta=<F2>m  tfDrift=<F2>m
move3s=<F3>m  pktJump=<F3>m  vel=<F3>m/s  headYaw=<F1>deg  head3s=<F1>deg
newest=(<F4>,<F4>,<F4>)  posErr=<F2>m
```

**已不存在的字段**:`underrun`(计数器已无自增点)、`lookback`、`renderDelay` 参与渲染。注意 `snap=` / `interpPct=` / `posErr=` 现为**结构性常量**(不再反映实际行为),排查时不要依赖。

### 9.6 现行实现的已知弱点 / 待办事

1. **外推调参是经验值**:代码注释记录 Steam 实测曾出现单帧 1~3m / 52~74m 的大跳(0.25s 封顶 + 包间隔 50~100ms 的组合),现改为 `latencySec + age` + 1s 封顶后仍需公网双账号复测确认。
2. **单向延迟是粗糙近似**:客户端侧用"自己到房主的 RTT/2"代替"对端到房主 + 房主到自己"的完整链路,高延迟下会低估。
3. **包内 `PacketTime` 未被使用**(仅作诊断字段存着),未来若做时钟同步可改用它替代 `RTT/2` 近似。
4. **`RenderDelayMs` 字段已失效**但仍在 UI/`SetTickRate` 里被赋值和打印,容易误导(标记待清理)。
5. **死代码残留**:`TryGetInterpolatedState` / `ApplyRemoteTransformDirect` / `ClearBuffer` / `ReuseInterpBody*` / `UnderrunFrames` / `SnapFrames` 已不被调用,建议后续清理。
6. **暂停同步仍是关掉的**(`OnPause` 整体注释掉),类型还在协议里分发但什么都不做。

---

## 0. 一句话结论【历史:插值缓冲时代】

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

- [`CraftStateSerializer.SerializeWrite`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/CraftStateSerializer.cs:149) 根 body 全发、子 body 仅 `Delta>0.1f`,按 Delta 降序每包 top-5;`BodySyncData.Update/Delta`([`BodySyncData.cs:89-118`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/SyncData/BodySyncData.cs:89))。**与我们 body 顺序索引契约冲突(需先引 Id,见 body-sync-2026-08-18.md P2),不在此方案内。**

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
- ❌ **ParentBody 树 / Delta top-N 兴趣**:body 顺序索引契约(无 Id)暂不支持子集,保持整船全发(见 body-sync-2026-08-18.md)。

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

- **P0(✅ 已实现)**:① BodyPoses 参与插值(Slerp body 旋转 + Lerp body 位置);② 缓冲欠载外推(velocity×(renderTime−arrival),封顶 0.25s)。改 `TryGetInterpolatedState` + `ApplyRemoteBodyPoses` 周边,`RemoteDataPack` 结构不动。
- **P1(✅ 已实现)**:`RemoteCraft` 增加 SmoothedPos/SmoothedSrfRel/SmoothedBodyPos/SmoothedBodyRot + 速度自适应 k + 10·dt body 平滑 + <0.01 快照 + >100m 瞬移;应用端喂平滑值。**2026-08-22 参数修正**:收敛速率 `dt*10 → dt*50`(时间常数 ≈0.2s,与 SP2 等价);静止锁定放宽为 `speed<0.5 m/s && 误差<0.05m → 快照`。
- **P2(可选)**:RTT/2 + jitter EMA 自适应 renderDelay/外推量(自适应 lookback 已作为其"最小实现"落地,见 §7);per-body velocity/angularVelocity;Quaternion32 压缩。

---

## 7. 实施记录【含已被取代的上一代实现】

- **2026-08-XX**:方案分析定稿。SP2 反编译核对:T1 外推(CraftStateSerializer.cs:55/76/88、NetworkBodyScript.cs:166)、T2 速度自适应平滑(CraftStateSerializer.cs:84-94)、T3 瞬移(CraftStateSerializer.cs:78-81)、T4 body 位姿平滑+快照(BodyScript.cs:660-679)、T5 每物理步写速度(NetworkAircraftScript.cs:416)、T6 角色混合插值(NetworkCharacterScript.cs:289-335)。SR2 现状核实:R1 body 不插值(`TryGetInterpolatedState` `interp=b`)、R2 欠载冻结([:1657-1662])、R3 renderDelay 固定、R4 无外推。P0/P1/P2 方案如上。
- **2026-08-XX**:调试工具落地(方案 A+B,见 §8)。编译 0 错误 0 警告。
- **2026-08-XX**:P0+P1 落地(`MpNetworkManager.cs`):
  - `RemoteCraft` 新增平滑状态 `SmoothedPos/SmoothedSrfRel/SmoothedBodyPos/SmoothedBodyRot/HasSmoothed` 与诊断计数 `ExtrapolatedFrames`;
  - `TryGetInterpolatedState`:`BodyPositions/BodyRotations` 参与插值(位置 Lerp、旋转 Quaternion Slerp 防欧拉绕转;浅拷贝共享缓冲列表 → **插值结果新建列表**);欠载分支走速度外推(封顶 0.25s,计 `ExtrapolatedFrames`)而非冻结;
  - 新增 `ApplyRemoteSmoothing`(P1:速度自适应 k 指数收敛 + 旋转 2.5·dt Slerp + 每 body 10·dt 平滑 + <0.01 快照 + >100m 瞬移)+ `SnapSmoothedBodies`(首帧/body 数量变化快照重对齐);
  - `UpdateRemoteCrafts` 在 `ApplyRemoteState` 前插入 `ApplyRemoteSmoothing`;周期日志含 `extrap=` 计数。编译 0 错误 0 警告。
- **2026-08-XX**:实测反馈"0 模拟延迟、双方静止仍位置跳动" → 排查与加固(编译 0 错误 0 警告):
  - **分析**:0 延迟静止时只有"插值分支 + 平滑"运行,静态目标下平滑数学不会产生跳动;跳动源更可能是①每帧 4 个 List 分配 → GC 卡顿/欠载毛刺;②发送端 `Assembly.Bodies` 顺序/数量在相邻包间变化时按索引插值把"不同 body 位姿"互插;③NaN 坏包污染平滑;④欠载外推在微停顿后与新包插值路径的接缝。
  - **加固**:
    - body 插值加**数量一致性护栏**(a/b 两包 body 数量不一致 → 回退沿用较新的 b,不跨 body 插值);
    - body 插值/平滑输出走 **`RemoteCraft` 复用缓冲**(`ReuseInterpBodyPos/Rot`、`ReuseSmoothBodyPos/Rot`),消除热路径每帧分配;
    - `ApplyRemoteSmoothing` 加 **NaN/Inf 防御**(非法目标直接快照)+ **静止锁定**(速度<0.05m/s 且已贴近 0.01m → 直接锁目标,杜绝"双方不动"时的微动/漂移);
    - 新增**跳动诊断**:`LastMoveDeltaM`(本帧应用位置相对上帧位移)+ `LastBodyPoseDeltaM`(单 body 最大位移),进周期日志,0 延迟+静止应≈0;>0.5m 即跳变。
  - **待双端复测**:看日志 `MP smoothing` 的 `move`/`body` 两值定位跳动源(根 or body),再对症下药。
  - **待双端实测项**:NetSim 注入 150ms/30ms 抖动,对比修复前后(underrun 冻结-跳变、body 每包跳变 → 修复后:欠载外推继续前进、body 平滑)。
- **2026-08-XX**:依据 `Player.log` 锁定跳动根源并修复(编译 0 错误 0 警告):
  - **日志实测(tick=120Hz, renderDelay=40ms, 全程 NetSim 未开)**:
    - 发送端(客户端 VM)实际发包间隔 `gapEMA=202ms`(≈5~10Hz,远低于 120Hz tick);接收端 `underrun=71.5%`,`extrap=91`;此时 `moveDelta=0.35m`(**跳动确与"低速发包 + 欠载"强相关**,非静止本身);
    - 包流恢复健康(gap≈5ms)时 `moveDelta=0.00m bodyDelta=0.00m posErr=0.0m`(**静止时平滑层纹丝不动,已验证**);
    - **结论:根因不是平滑层** —— 发送端有效发包率远低于 tick + 固定 `renderDelay=40ms` 远小于实测间隔 → 恒定欠载 → 冻结/外推接缝跳动;另发现 `ControlVisualSync.ApplyRemoteControls` 每帧抛 "Index out of range"(418 次刷屏)拖慢性能、淹没日志。
  - **修复**:
    - **自适应渲染回看(治欠载/跳动)**:`lookback = clamp(max(固定RenderDelay, 1.5×gapEMA), …, 100ms)`(per-remote-craft,按实测间隔自适应)→ 发送端低速时缓冲不再欠载,插值接缝平滑前进(代价:渲染滞后≈回看量,与 SP2 同思路);
    - **激活组 off-by-one(治日志刷屏 + 同步失效)**:`CommandPodScript` 激活组 1-indexed(1..10),接收端旧代码 `i=0..9` 调 `SetActivationGroup(0)` → `ActivationGroupStates[-1]` 每帧异常 → 改为 `i=1..n` 对应列表 `[i-1]`(见 [`part-switch-sync-2026-08-18.md`](part-switch-sync-2026-08-18.md) §11.5);
    - 日志行加 `lookback=` 便于复测验证自适应是否生效。
  - **复测预期**:gapEMA≈200ms 时 `lookback≈300ms`、`underrun%` 大幅下降、`moveDelta` 不再出现 0.35m 级单帧跳(变为平滑的插值位移);异常刷屏消失。
- **2026-08-22(静止"位置滑动"定位与修复,编译 0 错误 0 警告)**:
  - **现象**:双方静止(0 延迟)仍见"位置滑动"(平滑漂移,非跳变);用户判断与帧率无关。
  - **日志关键**:新日志所有行 `moveDelta=0.00 bodyDelta=0.00 posErr=0.0`(我写入的根位置恒定、与最新包一致),`tfDrift` 新增诊断待复测;`extrap=77~119` 帧、`underrun 13.7~20.4%`(自适应 lookback 已把 71.5% 压下来)。
  - **两个可疑根源(皆在本码可修范围内)**:
    1. **P1 位置平滑收敛过慢**:原 `alpha=1-Pow(1-k, dt*10)`,k=0.1 时时间常数≈**0.95s**,比 SP2(逐物理步 50Hz k=0.1 → ≈0.2s)**慢 5 倍**;任何残差/欠载外推造成的偏移都会拖成持续数秒的"滑动",速率≈0.005m/s(F2 日志显示 0.00,与日志不矛盾);
    2. **静止锁定过严**:原 `speed<0.05 && 误差<0.01m` 才锁;发送端残余速度≥0.05 m/s 即永不锁 → 平滑层永远在蠕动。
  - **修复**(`MpNetworkManager.cs` `ApplyRemoteSmoothing`):
    - 收敛速率 `dt*10 → dt*50`(SP2 等价,时间常数≈0.2s,帧率无关);
    - 锁定放宽 `speed<0.5 m/s && 误差<0.05m → 快照`(静止纹丝不动,杜绝蠕动);
    - 新增 `tfDrift` 诊断:本帧写入前读实际 `Transform.position` vs 上一帧写入值 → `moveDelta=0` 而 `tfDrift` 持续>0 ⇒ 滑动来自**游戏层在 Update 写入后移动了 ghost**(地表锁定/轨道推进/相机),而非我们的写入。只进周期日志。
  - **复测判据**:静止时若 `moveDelta=0 且 tfDrift=0` ⇒ 平滑层已纹丝不动,滑动来自游戏层(需进一步查 `InContactWithPlanet` 地表锁定/`RecenterTransformOnCoM`);若 `moveDelta` 出现非 0 小数 ⇒ 平滑收敛/锁定已生效。
- **2026-08-22 第二轮日志(tfDrift 复测)与高精度诊断追加**:
  - **`tfDrift=0.00m` 全行** ⇒ **游戏层没有移动 ghost 变换**(我写入后即稳)⇒ 排除"游戏层移动根位置"假设。
  - `moveDelta` 绝大多数 0.00;偶发 0.19/0.08/0.03m 全部伴随 `posErr=0.1~0.2m` ⇒ 那些时段 craft 真实在动(≈1.7m/s,posErr≈速度×lookback),属正确复现。
  - 剩唯一可能:**发送端数据缓慢漂移(≈0.1m/s)** —— ≈0.003m/帧;F2 显示 0.00、`posErr≈0.01m`、F1 也显示 0.0,全部旧日志测不出但肉眼可见。
  - **新增高精度诊断**(`MpNetworkManager.cs`,3s 窗口):
    - `move3s=` 3s 累计渲染位移(F3)——≈0.1m/s 漂移 3s≈0.3m;
    - `pktJump=` 3s 内最大单包位置跳变(F3)——发送端数据是否跳变;
    - `vel=` 最新包速度(F3)——发送端上报的残余速度;
    - `newest=(x,y,z)` 最新包位置(F4)——跨日志对比是否漂移;
    - `headYaw=`/`head3s=` 应用朝向 Yaw 与 3s 累计朝向变化(F3)——慢旋转同样会被感知为"滑动"(body 相对质心有偏移时尤甚)。
  - **复测判据(3s 窗口)**:`vel≈0` 但 `move3s>0.3m` ⇒ 平滑层在常数目标下漂移(bug);`vel≈0.1` ⇒ 发送端上报残余速度,"静止"实为慢漂;`pktJump>0 且 vel≈0` ⇒ 发送端采样跳变;`head3s` 大而 `move3s≈0` ⇒ 朝向漂移。
- **2026-08-22 第三轮日志(高精度诊断)→ 锁定真正根因 = 发送端速度帧错误(编译 0 错误 0 警告)**:
  - **日志铁证**:`vel=158.848 m/s` **恒定不变**(所有行相同),而 `newest` 位置只以 ~0.002 m/s 缓慢漂移、`pktJump` 最大 0.04m(包位置稳定)⇒ 速度与位置**自相矛盾**;`move3s` 却高达 0.3~83m/3s(幽灵突发大位移)。
  - **根因**:`PlanetVectorToSurfaceVector` 是**纯旋转**(`PlanetNode.cs:445` `RotateVectorAroundYAxis(v,-RotationAngle)`,**不减去行星自转项 ω×r**)。发送端 `vel=PlanetVectorToSurfaceVector(craft.Velocity)` 把落地/静止船的惯性速度(≈行星自转线速度 158.85 m/s)原样转进地表系 ⇒ 上报恒定 `158.85 m/s`。接收端欠载外推 `Position+Velocity×dtCapped` → `158.85×0.25` ≈ **40m** 反复注入 ⇒ 突发瞬移(正是用户看到的"位置滑动/跳动")。此前 `moveDelta`/`tfDrift` 为 0 是因为外推的 target 很快被新包拉回、单帧采样恰好≈0。
  - **修复**(`MpNetworkManager.cs`):
    1. **发送端**(`TrySampleLocalCraft`):正确地表相对速度 = `PlanetVectorToSurfaceVector(craft.Velocity) − CalculateSurfaceVelocity(pos)`(与游戏 `GroundedSurfaceVelocity`/`CraftNode.cs:1367` 同公式)⇒ 静止时 `vel≈0`,外推不再放大;
    2. **接收端**(`ApplyRemoteState` + 生成路径 `CreateLaunchLocation`):惯性速度 = `SurfaceVectorToPlanetVector(data.Velocity) + SurfaceVectorToPlanetVector(CalculateSurfaceVelocity(data.Position))`(加回自转线速度,避免幽灵惯性速度错成 ≈158.85 m/s 导致地表锁定被清后漂移);
    3. 附带收益:静止锁定(原判据 `speed<0.05`,因 158.85 永不触发)现在能真正生效。
  - **复测判据**:静止时 `vel≈0`、`move3s≈0`、`moveDelta=0`、`pktJump≈0` ⇒ 幽灵纹丝不动;移动时 `vel` 为真实地表相对速度、外推平滑不再瞬移。
- **2026-08-22 双端复测(本地 VM)→ 确认解决(编译 0 错误 0 警告)**:
  - **静止(零输入,NetSim 150ms/10ms 开启与否均成立)**:
    - `vel≈0.027~0.06 m/s`(修复前恒 **158.85 m/s**);
    - `move3s≈0.01~0.13m/3s`(修复前 0.3~83m)、`moveDelta=0.00`、`pktJump≈0.002~0.03m`、`posErr=0.00m`、`tfDrift=0.00m` ⇒ **幽灵纹丝不动**;
  - **移动(用户操控)**:`vel=1~9 m/s`、`move3s=4~26m/3s`、`posErr≈速度×lookback(0.4~1.7m)` ⇒ 正确复刻真实运动,无瞬移。
  - **结论**:速度帧错误根因已除,高延迟(150ms+10ms 抖动)下静止与平滑均成立。**P0+P1+自适应 lookback+速度帧修正**全套生效。
- **2026-09(Steam 实测后)架构换代:插值缓冲 → SP2 式连续外推**:
  - **起因**:上述"自适应 lookback"方案在 **Steam 公网**下暴露结构性缺陷——包间隔突发严重(`gapEMA` 可达 200ms 且抖动大),无论回看取"固定 renderDelay"还是"1.5×gapEMA"都追不上实测间隔,**恒定欠载**(实测欠载率 50~76%),每个欠载帧跳回最新包 ⇒ 用户仍反馈"一卡一卡";反向调大回看又放大渲染滞后。**结论:回看量不存在安全取值,方案本身不成立**。
  - **换法**:改为 SP2 式**不做插值、始终取最新包 + 按速度连续外推(dead-reckoning)**,再叠每帧指数平滑。实现见 **§9**。
  - **改动要点**(`MpNetworkManager.cs`):
    - `UpdateRemoteCrafts` 不再调用 `TryGetInterpolatedState`,改 `rc.TryGetNewest(out latest)` + `ext = latencySec + age` 外推;
    - 新增 `rc.LatencyMs`(单向延迟估计)与 `rc.NewestArrivalTime`(最新包到达时刻),由 `OnPong` 写入(RTT/2);
    - 长静默保护:包龄 `> max(3×gapEMA, 0.25s)` 时冻结为 `ext = latencySec`,防幽灵飞走;外推总量封顶 **1.0s**;
    - 周期日志字段随之变化:`lookback=` / `underrun=` **移除**,新增 `rtt/2=`;
    - `RenderDelayMs` 自此**不再参与渲染**(仅被赋值、打印,标记待清理)。
  - **遗留(未清理)**:`TryGetInterpolatedState` / `ApplyRemoteTransformDirect` / `ClearBuffer` / `ReuseInterpBody*` / `UnderrunFrames` / `SnapFrames` 成为死代码;`snap=` / `interpPct=` / `posErr=` 变为结构性常量。
  - **待复测**:Steam 公网双账号在"静止 / 移动 / 高延迟抖动 / 丢包"四类场景下确认外推不产生单帧大跳(代码注释记录过 0.25s 封顶时期曾出现单帧 1~3m、极端 52~74m 的跳变)。

---

## 8. 调试工具(已实现:NetSim 延迟模拟 + 接收端平滑诊断)

> 动机:没有 Steam 好友时,TCP+本地 VM 无法暴露真实公网的延迟/抖动/丢包 → 平滑代码(§3)的问题测不出来。落地两件套:

### 8.1 延迟模拟传输层 `LagSimTransport`(方案 A)

- **文件**:`Assets/Scripts/Net/LagSimTransport.cs`(新增,已入 MultiPlayer.csproj)。实现 `IMpTransport` **装饰器**,包住任意底层传输(默认场景 = `TcpTransport`),只在**接收路径**(`inner.OnDataReceived` → 延迟队列 → 到点再触发上层)注入网络条件;`SendTo/Broadcast/超时/生命周期` 全部直通,对 `MpNetworkManager` 完全透明。
- **控制台命令**(DevConsole):
  - `NetSimDelay <ms>` 基础单向延迟;`NetSimJitter <ms>` 均匀抖动 ±ms;`NetSimLoss <pct>` 丢包率;`NetSimDuplicate <pct>` 重复包;
  - `NetSim` 查看配置+活跃实例投递统计(delivered/dropped/inFlight);
  - `NetSimReset` 归零(后续开房不再包装;已包装实例立即直通)。
- **挂载点**:`TcpHostLobby/TcpJoinLobby`(Mod.cs)+ UI TCP 按钮(MultiPlayerUI.cs)创建传输时经 `LagSimTransport.MaybeWrap` 自动包装(仅当启用);Steam 暂不包装(保 `Transport is SteamTransport` 预检,见代码注释)。
- **可复现**:开房前设好 → `TcpHostLobby 25555` / `TcpJoinLobby <ip> 25555` 双端各自配置 → 模拟非对称/对称延迟;会话中改值**逐包实时生效**;`NetSimDelay 50` → `150 30` + `NetSimLoss 2` → 回 `50` 可观察平滑自愈。
- 语义说明:延迟打"接收侧",RTT 探测(ClientPingMs)会如实累计两端单向延迟;接收端看到的包到达分布与真实公网一致,正好喂给 §3 的插值/外推逻辑。

### 8.2 接收端平滑/网络诊断(方案 B)

- **周期日志(唯一诊断出口)**:`MP smoothing P{pid}: ...` 每 3 秒一条,进 `Player.log` 可回看;字段:
  - 缓冲与网络:`buf`、`renderDelay`/`lookback`、`gapEMA`/`jitterEMA`、`underrun %`、`snap`/`extrap`/`interpPct`;
  - 跳动/漂移指标:`moveDelta`(本帧根位置位移)、`bodyDelta`(本帧单 body 最大位移)、`tfDrift`(写入后游戏层是否又移动了 ghost);
  - 高精度(3s 窗口):`move3s`/`pktJump`(3s 累计渲染位移 / 单包最大跳变)、`vel`(发送端上报速度)、`headYaw`/`head3s`(朝向漂移)、`newest`(最新包位置 F4)、`posErr`(渲染位置 vs 最新包)。
- **已移除悬浮窗(`NetStatsUI`/`OnGUI`)**:用户不需要窗口,诊断只保留 `Mod.LogLobby` 日志(避免 GUI 开销干扰测量)。
- **实现位置**:`RemoteCraft` 诊断字段 + `PushSample` 抖动 EMA + `TryGetInterpolatedState` 欠载/插值计数 + `UpdateRemoteCrafts` 周期日志 + `ApplyRemoteSmoothing` 跳变/漂移指标(全在 `MpNetworkManager.cs`)。
- **验证闭环**:`NetSimDelay 150 30` 飞行观察日志:jit≈30ms、underrun%>0(P0②外推未实现时)→ 复现"一卡一卡";实现 §3 P0 后同一条件下 underrun 仍计数但**视觉不再冻结跳变**(外推接管),posErr 反映剩余滞后 → 定量验证 P0/P1 效果。最终闭环见 §7 的 2026-08-22 三轮日志(该日志正是靠这套字段定位到发送端速度帧错误)。

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
NetSim               # 查看当前配置与投递统计
NetSimOff            # 总开关:关=直通,其它 TCP 场景延迟尽量小
NetSimReset          # 清空数值+关总开关
```
诊断数值看 `Player.log` 的 `MP smoothing P#` 3s 周期行(无悬浮窗)。

> **开关语义**:数值命令与总开关分离——`NetSimDelay/Jitter/Loss/Duplicate` 只设数值;**实际生效由 `NetSimOn/Off`(或 UI 开关)控制**。关闭=直通(不延迟不丢包),保证普通 TCP 测试延迟尽量小;已启用的活跃会话改开关/改值**逐包实时生效**。
