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
6. **暂停同步仍未实现**(`OnPause` 整体注释掉),类型还在协议里分发但什么都不做;但**"暂停导致观察方位置抽搐"已于 2026-09 修复**(见 §9.7,发送端只加 Paused 标记 + 降速上报,并未做暂停动作同步)。

### 9.7 【2026-09 修复】"飞船有速度时暂停 → 观察方看到位置抽搐"

> 现象(用户报告):飞船 A **在有速度时暂停**,在飞船 B 的视角下 A 的位置持续**抽搐**。
> 状态:**已修复**(`dotnet build` 0 错误 0 警告),待双端实测确认。**2026-09-13 二修**:发现首版冻结钳制有漏钳制漏洞,且冻结期外推公式会变成 `2×latencySec`(见 §9.8);**同会话另修 comRot 反馈环导致的静止船 3cm 往复**(见 update-1.4.2 §〇之四)。

**根因(代码核实)**

暂停(时间倍率 0,`Time.timeScale = 0`)时发送端会发生两件互相矛盾的事:

1. `CraftNode.UpdateCraft` 走暂停分支不再推进,位置整体**冻结**(相邻状态包 `Position` 完全相同);
2. 但状态包里上报的 `Velocity` 仍是**暂停前最后一刻的非零速度**(物理停了,`craft.Velocity` 字段没被清零)。

接收端的现行管线是 `Position + Velocity × ext`(dead-reckoning,§9.3),于是:

- 每个包到达 → `age` 归零 → 目标位置被**拉回** `Position + Velocity×latencySec`;
- 包间 → `age` 增长 → 目标位置又**按速度前进** `Velocity×age`。

→ 目标位置以**发包频率来回摆动**(幅度 ≈ `|v| × 发包间隔`),而 §9.4 的速度自适应 `k = Lerp(0.1, 1, |v|×0.02)` 在高速时 **k=1 → alpha=1**,平滑层对高频摆动**几乎零衰减** → 渲染层直接"抽搐"。低速/静止时因为 `|v|×发包间隔 ≈ 0` 而完全看不出来,所以此前一直没暴露。

**修复(接收端:冻结时不按速度外推)**

| 端 | 改动 |
|---|---|
| 发送端(`TrySampleLocalCraft`) | 状态包新增尾部字段 `Paused`(`RemoteDataPack.Paused = TimeManager.Paused`,即 `Time.timeScale==0`)。`WriteRecdata`/`ReadRecdata` 均放在**所有既有字段之后**(读侧用 try/catch,旧版本对端发来的包读到 EOF → 保持 `false`) |
| 发送端(`ProcessOutgoing`) | 暂停时上报降到 ≥`PausedSendIntervalMs`(125ms ≈ 8Hz):位置/速度都不变,无需全速上报,但仍让对端知道"我还活着且已暂停";恢复后立即回到 `SendIntervalMs` |
| **接收端(`UpdateRemoteCrafts`)** | **判据双保险**:①包内 `Paused` 标记;②**连续 2 包位置零位移**(`≤0.02m`,兼容旧版本对端/丢包)。任一成立即判定"发送端已冻结" |
| 接收端 | 冻结期间把外推量按 `RemotePausedRamp` 收敛到**固定单向延迟** `latencySec`(**不带 `age`**),即"停在最新包位置 + 传输本身占用的那段位移";过渡时长 `RemoteFreezeBlendSec = 0.15s`(`Mathf.MoveTowards`),冻结/解冻瞬间都不跳变 |

要点:

- **退出冻结以"位置重新开始变化"为准**(零位移计数清零),不只看 `Paused` 标记 —— 对端刚恢复那一瞬间包内位置还是暂停前的旧值,若立刻按速度外推会跳一下。
- **零位移判据不会被正常慢速运动误判**:0.5 m/s 在 50ms 包间隔内也有 0.025m > 0.02m 阈值;而真正静止的船(实测残余 `0.026 m/s` → 每包 ≈0.001m)本来也不需要外推。
- 冻结判定最长有 ~2 个包(≈250ms)的延迟,期间会按旧速度前进一小段(恰是"暂停"语义,不是错误);本修复消除的是此后**每包往复**的持续摆动。
- **没有做暂停动作同步**:仍然只同步"位置/速度/部件/控制",不把 A 的暂停状态搬到 B(游戏不允许远程暂停本地)。

**诊断字段(新增)**

- 接收端周期性 `MP smoothing` 行加:`frozen=<n>`(完全冻结帧数)、`paused=<0|1>`、`ageNow=<F3>s`(本帧实际外推量)、`stall=<n>`(连续零位移包计数)、`pkΔ=<F4>m`(最近两包位置距离);
- 接收端**状态跃迁一次性日志**:`MP freeze P<id>: ENTER|EXIT (flag= stall= pkΔ= vel=)`;
- 发送端 `MP sendDiag` 行加 `paused=<0|1>`。

**复测判据**

| 场景 | 期望日志 |
|---|---|
| A 有速度时暂停 | 接收端出 `MP freeze P1: ENTER (flag=1 …)`(同版本双端立即命中;仅靠零位移判据则 ~2 包后命中);此后 `paused=1`、`ageNow≈0.000s`、`moveDelta≈0.00`、`pkΔ≈0.0000m` ⇒ **幽灵停在原地,不再抽搐**(冻结期 `ageNow→0`,外推 = 固定单向延迟 `latencySec`,目标恒定) |
| A 解除暂停 | 出 `MP freeze P1: EXIT`;`paused=0`、`ageNow` 恢复随包龄增长、位置平滑跟上,**无单帧大跳** |
| 双方正常飞行 | 不再出现 `ENTER`(慢速漂移不触发);`move3s`/`vel` 与真实运动一致 |
| 单端升级(旧包无 Paused 字节) | 不做任何事也不报错(标记保持 `false`),靠零位移判据同样生效 |

> ⚠️ **混版本注意**:`Paused` 是**尾部追加**字段 → 新端解旧包安全(EOF → `false`),但**旧端解新包不安全**(该字节会被当成 `ActivationGroupStates` 计数 → 后续字段整体错位 → `EndOfStreamException` → 包被丢弃)。房主中继是**原样转发字节**(`OnState` → `Transport.Broadcast(packet)`,不重新编码),所以风险只在"新旧解包方向"。结论:**双端需同时升级**;若暂不升级,零位移判据(判据②)也能独立修复本 bug——只是旧端仍会抽搐、且判定要晚 ~2 个包。

### 9.8 【2026-09-13 二修】首版冻结钳制两个缺陷(高速船仍抽搐的残余根因)

> 现象(复测):用户反馈"问题依旧存在"。首版 §9.7 修复后,接收端看**静止**船 `moveDelta=0.00` 已收敛,但**高速船**(实测暂停时 85.192 m/s)视角仍抽搐。逐行复查 `UpdateRemoteCrafts` 冻结分支,发现两处缺陷:

1. **漏钳制漏洞(主犯)**:钳制条件是 `if (ramp>0 && ageNow > latencySec)`,但暂停时发送端降频到 8Hz,包龄 `age` 在 0~125ms 间循环,**往往小于单向延迟** `latencySec`(80ms+ 时大部分时间都小于)→ 条件永不成立 → 冻结期外推量依旧随包龄前进/回拉 → 高速船(85 m/s × 0.045s ≈ **3.8m**)每包往复,照旧抽搐。静止船(0.026 m/s)幅度 ≈0.001m,日志上看不出来 —— 首版"已验证"实为静止场景的假阴性。
2. **外推公式错误**:即便钳制命中,`ageNow` 被 Lerp 到 `latencySec` 后 `ext = latencySec + ageNow` → **`ext → 2×latencySec`**(目标偏移翻倍),恢复瞬间会产生一次明显纠偏。

**二修(已提交,`dotnet build` 0 错误 0 警告)**

- 去掉 `ageNow > latencySec` 守卫,**冻结期无条件**把包龄部分收敛:`ageNow = Lerp(ageNow, 0, ramp)`(ramp=0 时恒等,正常飞行行为完全不变);
- 冻结期 `ext = latencySec + ageNow → latencySec`(固定单向延迟,目标恒定,不再随包龄摆动),恢复时 `ageNow` 经 0.15s ramp 平滑回到包龄;
- 判据②(连续 2 包零位移 ≤0.02m)对**旧端发来的包**同样生效,混版本下该缺陷也能被覆盖。

**复测判据(二修后)**

| 场景 | 期望日志 |
|---|---|
| A 以 85m/s 有速度时暂停,同版本双端 | `MP freeze P0: ENTER (flag=1 …)`(观察方视角,发暂停标记的一侧);此后 `ageNow≈0.000s` 恒定、`moveDelta≈0.00`、`pkΔ≈0.0000m`、`frozen` 持续增长 ⇒ **高速下也停在原地,无每包往复** |
| A 解除暂停 | `MP freeze P0: EXIT`;`ageNow` 经 0.15s 平滑恢复随包龄增长,无单帧大跳 |
| 仅观察方升级(发送端旧包无 Paused 字节) | 判据②独立生效(零位移计数),延迟 ~2 包,同样不抽搐 |

> 注:若观察方仍未升级(旧版无冻结逻辑),旧端依旧会按 `Position + Velocity×age` 每包拉回 → 抽搐照旧。**双端必须同版本**。

### 9.9 【2026-09-13 双端实测确认】二修生效 + 残余振荡定位

> 双端互看日志(本机 P0 飞行+反复暂停 166m/s,对端 P1 静止):
> ① 冻结修复**实测确认**:对端看 P0 暂停期 `paused=1`、`ageNow=0.000s`、`moveDelta=0.00`、`frozen` 持续增长;
> 解除暂停瞬间首个 3s 窗口 `moveDelta=2.77m` = 166m/s×16.7ms 的真实运动,**无大跳**;
> ② 发送端 `body0RelΔ=0.0000m`(最高 0.0027m)→ 发送数据干净;
> ③ **残余恒定振荡**:对端看飞行中 P0 `b0dLate=0.1188m` 恒定 —— 根因是**根 body 的包内 rel0 ≠ 0**(46 body 船 0.1188m)与游戏 `RecalculateFrameState` 放置基准(craft.Position=comPos)的对抗(详见 §9.11 三轮根因)。

### 9.10 【2026-09-13 慢放修复】外推量换算到发送端时间基

> 用户反馈:正常飞行/慢放/暂停**均有抖动,慢放最严重**。定位:
> `age = Time.unscaledTime − NewestArrivalTime` 是**接收端真实时间**;慢放(发送端 timeScale<1)时
> 发送端飞船按缩放时间移动,相邻状态包仅推进 `v×T_s×timeScale`,而 dead-reckoning 外推仍按真实时间推进
> `v×age` → 每包目标"超前→拉回"向后锯齿,幅度 `v×T_s×(1−timeScale)`(166m/s、50ms、0.2× → 6.6m/包)。
> 暂停(timeScale=0)最极端,但被 §9.7/9.8 的冻结逻辑挡住 → **慢放成为可见最坏档**。
>
> **修复**:接收端在 `PushSample` 测量**发送端时间倍率** `SenderTimeRate` =
> 相邻两包 `FlightState.Time`(发送端游戏时间)增量 ÷ 真实到达时间增量(EMA,过滤 0~10 倍外坏值;暂停时 dtPkt=0 → 倍率→0);
> `UpdateRemoteCrafts` 把 `ext ×= SenderTimeRate` 换算到发送端时间基:
> - 正常飞行 rate≈1 → 行为完全不变(零回归);
> - 慢放 rate=timeScale → 外推与发送端实际运动同步,锯齿消除;
> - 发送端暂停 rate→0 → ext→0,幽灵精确停在包位置(比"冻结在 latencySec"更准)。
>
> ⚠️ **2026-09-13 四轮实测修正(§9.14)**:用户的慢放是 **Unity `Time.timeScale<1`**,但游戏
> `FlightState.Time` **不缩放**(对端 `rate` 恒 1.000,两版本日志都证实)→ **基于包时间的倍率测不到这种慢放**。
> 改用**位置基 `SenderMotionRate`**(§9.14):相邻两包位置位移 ÷ (速度 × 真实到达间隔) = 发送端**实际运动速率**,
> `ext ×= SenderMotionRate`。包位置位移如实反映任何慢放机制。`MP smoothing` 行同时输出 `rate=`(包时间)
> 与 `mRate=`(位置)供对照。
>
> 诊断:`MP smoothing` 行新增 `rate=`/`mRate=`(正常≈1;发送端慢放时 mRate≈timeScale)。
> 注意:仅双端同速(一起慢放)时按缩放外推;发送端正常而接收端慢放时 rate/mRate=1,幽灵以真实速度移动
> (显示对端真实运动,正确)。

### 9.11 【2026-09-13 三轮】根 body 与游戏 comRot 锚定的 G 对抗(8~13cm 恒定抖动,全阶段)

> 详见 [update-1.4.2 §〇之四](update-1.4.2-experimental-2026-09-03.md) 三轮。要点:
> 游戏 `CraftScript.FramePosition` getter = `CenterOfMass.position` → `RecalculateFrameState` 每帧把
> **comRot 锚到 craft.Position**;body[0] 是 comRot 父级 → 被游戏放到 `comPos − G`(G = 接收端自身几何偏移,每船不同)。
> 根 body 按 `comPos − G`(G 写前实时读取)写出 → 与游戏锚定一致 → `b0dLate→0`,全阶段 8~13cm 恒定抖动消除。

### 9.12 【2026-09-13 三轮复测 + 发送率/单帧位移上限】双端新 build 实测 + 正常飞行顿挫

> **复测结果(双端最新 build,新日志)**:根 body 修复**两端验证通过** —— 本机看静止 P1 与对端看 P0 的
> `b0dLate` 全部 0.0000m,静止时 comLink/comCross/b0d 全 0;冻结逻辑正常(paused=1 → ageNow=0.000s、
> moveDelta=0.00m);`rate=` 字段正常上报但**两份日志均无慢放段(rate 恒 1.000)→ Fix B(§9.10)未获实测数据**。
>
> **新发现(正常飞行段,对端看 P0)**:发送速率仅 ~13-15Hz(gapEMA 66~80ms,配置 20Hz)——
> `ProcessOutgoing` 的 `_sendTimer = 0f` 整帧清零把发送率钳制在渲染帧率(30fps → 15Hz);
> 且高速机动时 `k=Lerp(0.1,1,v×0.02)` 在 ≥50m/s 时 =1 → alpha=1 → 平滑全跟,
> 166m/s 急转/加减速时每包校正巨大(pkΔ 13.3m、pktJump 23m、单帧 moveDelta 5.84m)→ 渲染层单帧大跳 = 顿挫。
>
> **修复 C(发送率)**:`_sendTimer = 0f` → `_sendTimer -= sendIntervalMs`(携带余量),
> 任意 ≥20fps 的帧率都稳定发满 20Hz(30fps 时 15→20Hz,每包位移缩小 ~35%)。
> **修复 D(单帧位移上限)**:平滑位置每帧最多移动 `1.5×v×dt`(物理可行上限,放在 alpha 分支内、>100m 瞬移检查前):
> 匀速飞行单帧目标移动 = v×dt < 上限 → 零影响(无人工滞后);包到达/加减速的异常跳变按上限逐帧摊平
> (13m → ~4 帧),不滞后正常运动。静止锁定分支不经过上限,低速蠕动抑制不受影响。
>
> **复测判据(下轮)**:①`MP smoothing` 行 `gapEMA≈50ms`(原 66~80);②正常飞行 `moveDelta` 单帧不再出现
> 5.8m 级跳变(摊平为 3.3m×2~3 帧);③**务必补测慢放段**(两份新日志都缺):慢放时 `rate≈发送端 timeScale(<1)`,
> 暂停 `rate→0`;④暂停/静止维持 b0dLate=0。

### 9.13 【2026-09-13 四轮】用户慢放实测 = "仅接收端慢放";静止 ghost 整船跳不在场景 transform

> **用户答复**:①慢放按了、持续数秒(但双端 rate 恒 1.000);②抖动 = **本机看静止对端船整船位置一跳一跳**;
> ③暂停已不抖。→ **关键修正**:用户的慢放是**仅本机(接收端)慢放,发送端没慢放** → `FlightState.Time` 不缩放
> → §9.10 的 `SenderTimeRate`(发送端时间基)在此场景恒 1.0,**Fix B 覆盖的是"发送端慢放",不是用户的场景**。
>
> **第二个关键事实**:整段日志本机 twitch P1 全部 ≤5cm(comLink/comCross/b0d 全 ≤0.05m)——
> **静止 ghost 的 comRot/body 场景变换从未移动**。反编译确认 `RecalculateFrameState` 只移动 debris/粒子/修饰器
> (CraftScript.cs:1381),主 body 位置 = 我们的写入;CraftBuilder.cs:375/566-568/676 证实部件经
> partGroup 挂在 **body** 上(渲染链 = body transform = 我们的写入)。→ **"整船跳"发生在诊断采样点之外的
> 渲染路径**(craft 根 transform / 部件世界位置 / 地图渲染 / 游戏自身视觉同步),而不是 body 写入。
>
> **新诊断(已提交)**:`MP slowmo` 行改为**接收端慢放(Time.timeScale<0.99)或发送端慢放(rate<0.99)都触发**,
> 0.5s 周期,逐帧追踪 craft 根 / 首个部件 / comRot 的**最大单帧位移**(rootΔ/partΔ/comΔ)+ 观察距离 dist,
> 定位跳动来源。下轮测试:本机慢放 ≥3s 看静止对端船,读 `MP slowmo` 行哪个 Δ 非零。

### 9.14 【2026-09-13 四轮】慢放真数据到手:ts=0.05 全静止;慢放 = Unity timeScale,包时间不缩放 → 位置基运动倍率

> **慢放实测数据(本机新日志,8 行 `MP slowmo` timeScale=0.050 = 5% 速度)**:
> `rootΔ=0.000m partΔ=0.000m comΔ=0.000m`,twitch 全 0,comFrozen 恒定 —— **46~93m 近距离、对端船暂停
> (paused=1)的整条变换链(根/部件/comRot/body)在真慢放期间纹丝不动**。对端日志同段:
> `MP slowmo P0 timeScale=0.000 ... rootΔ/partΔ/comΔ 全 0`,**rate 恒 1.000(65 行 smoothing P0 全 1.000)**。
>
> **坐实的机制**:
> 1. **用户的慢放 = Unity `Time.timeScale`(0.050),但游戏 `FlightState.Time` 不缩放** → 发送端包时间
>    按全速推进 → §9.10 的包时间倍率 `rate` 恒 1.000,**测不出慢放**(这也解释了此前所有日志缺慢放段)。
> 2. **包位置确实按 0.05× 推进**(发送端物理被 Unity timeScale 缩放)→ 接收端外推(按真实时间跑)超前
>    实际运动 → 发送端慢放时对端看 P0 存在"外推超前→每包向后锯齿"(幅度 v×T_s×(1−ts))。
> 3. **本机慢放时看的静止 ghost(46-93m):所有场景变换 0 位移,用户仍报"整船跳"** → 跳动不在我们写入的
>    任何 transform,指向游戏自身渲染/相机路径(相机平滑跟随用缩放 deltaTime、quadsphere LOD、或地图渲染),
>    与网络代码无关 —— 需视频/截图或更精确描述(跳幅/周期)才能继续定位。
>
> **修复(2026-09-13 已提交)**:`ext ×=` 改为**位置基 `SenderMotionRate`**:
> `相邻两包位置位移 ÷ (速度 × 真实到达间隔)` = 发送端**实际运动速率**(正常≈1、发送端慢放=timeScale、
> 静止/暂停→0,EMA 0.9/0.1,过滤 0~10);`MP smoothing` 输出 `mRate=`。包位置位移如实反映任何慢放机制,
> 不受 FlightState.Time 不缩放影响。发送端慢放时外推与发送端实际运动同步,锯齿消除。
>
> **下轮判据**:①发送端慢放时对端 `mRate≈timeScale`(≈0.05),`moveDelta` 无每包锯齿;②`rate=1.000`
> 但 `mRate<1` 属正常(用户场景);③静止/暂停维持全 0;④若本机看静止 ghost 仍见"整船跳",请录屏或
> 描述跳幅(米/船身比例)与周期(每几秒跳一次)。

### 9.15 【2026-09-13 五轮】根因坐实:倍率测量是死的(dtReal=0)→ 慢放锯齿/暂停回拉从未被修

> **用户新量测**:①暂停时 **0.5m 级跳变**;②慢放(1/20)时 **1.5 个船身跳变**。新日志双端确认:
> 本机 22 行 `MP slowmo timeScale=0.050` + 28 行 ts=0.000,`mRate=1.000` 恒值;对端 smoothing P0
> `rate=1.000 mRate=1.000` 恒值,`vel=131.59m/s` 在暂停段冻结在包内。
>
> **代码审查抓到真根因**:`PushSample` 里 `_lastPushTime = arrivalTime;` 在倍率测量**之前**执行,
> 两个倍率块再算 `dtReal = arrivalTime − _lastPushTime ≡ 0` → 守卫 `>0.001f` 恒失败 →
> **`SenderTimeRate` 与 `SenderMotionRate` 自实现起从未更新(恒 1.000)**,§9.10/§9.14 的外推缩放
> **从未实际生效**。这同时解释了:
> - **慢放 1.5 船身跳** = 发送端慢放时外推按全速跑、包位置按 0.05× 走 → 每包向后锯齿
>   (幅度 v×T_s×(1−ts),131m/s×0.05s×0.95 ≈ 6.2m/包 ≈ 1.5 个船身);
> - **暂停 0.5m 跳** = 冻结进入瞬间 ageNow 0.15s 内收敛到 0 → 目标回拉 v×ageNow
>   (13m/s×0.039s ≈ 0.5m);且冻结期 ext 恒 latencySec(131m/s → 偏移 10m),位置本就不准。
>
> **修复**:测量块改用保存的上一包到达时间 `prevArrival`(先存后覆盖),`dtReal = arrivalTime − prevArrival`
> 真正生效。修复后:慢放 mRate≈0.05 → 外推与实际运动同步,锯齿消除;暂停 dPos=0 → mRate→0 → ext→0,
> 幽灵精确停在包位置(不再悬在 v×latencySec),进入/退出冻结平滑收敛。
>
> **下轮判据**:①本机慢放时**对端**日志 `mRate≈0.05` 且出现 `MP slowmo` 行(此前对端 0 行);②对端看
> P0 慢放无每包锯齿;③本机暂停时对端 P0 冻结于精确包位(moveDelta=0,无 0.5m 回拉);④回归:
> 正常飞行 mRate≈1.000,行为不变;b0dLate=0、gapEMA≈50ms、静止 ghost 全 0 保持。

### 9.16 【2026-09-13 六轮】mRate 实测生效(慢放≈0.05/暂停→0);剩"切换速度模式"跳变 = 单包尖刺

> **用户反馈:很大改善,只剩"切换速度模式时出现跳变"。** 双端新日志证实:
> - **对端**:慢放段 `mRate=0.028~0.050`(≈发送端 timeScale ✓),暂停段指数衰减
>   `0.469→0.485→0.353→0.232→0.152→0.100→0.065→0.043→0.025→0.017→0.012→0.008→0.005→0.003→0.002→0.001→0.000`
>   ✓,恢复回升 ✓ → §9.15 的 prevArrival 修复彻底生效,慢放每包锯齿已消除。
> - **本机(用户屏幕)切换瞬间**:`MP slowmo P1` 行在发送端恢复运动时 `mRate 0.208→0.393`(单包测量尖刺
>   透出 EMA)→ `moveDelta=1.97m`、`rootΔ=2.669m`(单帧 ~2m 跳)≈ 用户看到的"切换跳变"。
>
> **机制**:切换速度模式瞬间,单包测量 mRate 可能大幅偏离(大间隔/大位移包),EMA 0.9/0.1 只能摊平
> 10%,尖刺仍使 ext 突变 → 幽灵单帧大跳。**修复:速率变化钳制** —— `SenderMotionRate` 每包最多变化
> `±0.15`(MaxMotionRateStep):满量程 0.05↔1.0 收敛仅 ~0.3s(6-7 包),切换瞬间的尖刺被压到
> ≤0.15/包(0.208→0.358 而非 →0.393,ext 突变降 ~4×)。暂停衰减(0.9×/包,变化 <0.15)不受影响。
>
> **下轮判据**:①切换速度模式时 `MP slowmo` 行 mRate 变化 ≤0.15/0.5s,`moveDelta` 无 >0.5m 单帧;
> ②慢放 mRate≈0.05、暂停→0、正常≈1 保持;③b0dLate=0、gapEMA≈50ms 回归保持。

### 9.17 【2026-09-13 收工】用户确认"没什么问题了" —— 修复链终态

> **双端新日志(仅本机)全量复核**:①mRate 步进钳制生效(0.365→0.250→0.164→0.119→…,每步 ≤0.15),
> 切换瞬间可见 body 位移 ≤0.3m(上轮 1.97~2.7m);②`MP slowmo` 80 行 ts=1.000(本机正常速逼近
> 静止 P1,rootΔ≤0.05m / partΔ≤0.001m —— 曾经"整船跳"的场景现在纹丝不动);③高速加速段残留单帧
> ≤ `1.5×v×dt` 物理上限(20→80m/s 加速时 ~2m/帧,发送端真实加速所致,step-limit 已封顶,用户确认
> "过小的跳动目前来看可以忽略");④b0dLate>0 次数 = 0、gapEMA 均值 52ms、冻结进出干净
> (EXIT pkΔ ≤0.0263m)。**用户:"过小的跳动目前来看可以忽略,诚实的记录并更新md文档,收工"。**
>
> **整条修复链(§9.7~§9.17)**:①暂停锯齿 → 包内 Paused 标记 + 冻结收敛(§9.7,二修 §9.8);
> ②comRot 反馈环 → 逻辑基准(§9.9);③根 body rel0 对抗 → comPos−G(§9.11);④慢放外推超前
> → 发送端运动倍率 mRate(§9.10/§9.14);⑤倍率测量死代码(dtReal≡0)→ prevArrival(§9.15);
> ⑥切换单包尖刺 → 每包变化钳制 ±0.15(§9.16)。**终态**:`ext = (latencySec + ageNow) × mRate`,
> mRate = 相邻包位置位移÷(速度×真实间隔),每包变化钳制 ±0.15;步进上限 `1.5×v×dt`;
> 诊断:`MP smoothing`(3s,含 rate/mRate)、`MP slowmo`(0.5s,rootΔ/partΔ/comΔ/dist)、
> `MP freeze`(跃迁)、`MP twitch`(1s)。

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
- **2026-09(用户实测反馈「飞船A在有速度时暂停 → B 视角位置抽搐」)修复,`dotnet build` 0 错误 0 警告**:
  - **排查**:核对 `Player.log`(107 条 `MP smoothing`)未见成规模的抽搐记录(该次会话 P0 已落地静止、`vel≈0.026`、`moveDelta=0.00`),故改为**代码层面定位机制**并用反编译确认暂停语义:`FlightGameLoop` 暂停时 `FixedUpdate`/`Update` 分组整体跳过(`:156/:375`)→ `CraftNode.UpdateCraft` 不推进 → 位置冻结;但 `craft.Velocity` 字段保留暂停前值。**根因即"位置冻结 + 速度非零"的组合被 dead-reckoning 放大**(详见 §9.7)。
  - **改动**:`Mod.cs` `RemoteDataPack` 加 `Paused` 字段;`MpMessage.WriteRecdata/ReadRecdata` 尾部追加该字节(旧包 EOF → `false`,向后兼容);`TrySampleLocalCraft` 写 `TimeManager.Paused`;`ProcessOutgoing` 暂停时上报间隔 ≥125ms;`UpdateRemoteCrafts` 加冻结判定(标记 ∨ 连续 2 包零位移)+ `RemotePausedRamp`(0.15s 过渡)把外推量收敛到固定单向延迟;`RemoteCraft` 加 `RemotePaused/LastPktPausedFlag/FrozenFrames/PktStallCount/PktFreezeDeltaM/LastAgeNowSec` 等诊断字段与 `MP freeze` 跃迁日志;`MP smoothing` / `MP sendDiag` 行加 `frozen= paused= ageNow= stall= pkΔ=`。
  - **验证方式(双端实测)**:A 有速度时按暂停 → 接收端应出 `MP freeze P1: ENTER` 且 `moveDelta≈0.00`;解除暂停出 `EXIT` 且无单帧大跳。

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
