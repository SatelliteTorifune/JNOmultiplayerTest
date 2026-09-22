# 远程飞船高延迟平滑方案(latency-smoothing)— 现行实现(§9)+ 调试工具(§8)

> 状态:✅ **已归档(2026-09-13 收工,用户确认;2026-09-21 随 acceleration-smoothing 回滚后仍为当前基线的实现事实)**。
> 目标:延迟 >100ms(RTT)时,对面 craft 同步位置**平滑**(不"一卡一卡"),含整船平移、朝向、每 body 相对位姿。
> 关联:[`body-sync-2026-08-18.md`](body-sync-2026-08-18.md)(BodyPoses 数据源,本方案在**接收端平滑层**上做文章);[`acceleration-smoothing-2026-09-14.md`](../acceleration-smoothing-2026-09-14.md)(2 阶外推扩展 + 回滚复盘,活跃文档)。
>
> **阅读提示**:本文档 **§9 为现行事实**。§0~§7(插值缓冲 + 自适应 lookback 时代)已整体被 §9 取代,内容已删除;只保留 §8(调试工具,代码仍在)与 §9(现行管线/参数/修复链)。残余死代码(`TryGetInterpolatedState`/`RenderDelayMs`/`UnderrunFrames`/`SnapFrames`)在代码里但**已不被使用**,待清理。

---

## 9. 【现行实现】SP2 式连续外推(dead-reckoning)

> 上一代"插值缓冲 + 自适应 lookback"在 Steam 实测中暴露结构性缺陷(见 §9.1),已整体替换。**这是当前真正在跑的接收端管线**(r10 基线)。

### 9.1 为什么放弃插值缓冲

- 插值缓冲要求"渲染时刻早于最新包到达时刻",回看量必须大于**实测包间隔**;Steam 环境下包间隔突发严重(`gapEMA` 可达 200ms 且抖动大),固定或"1.5×gapEMA"的自适应回看都追不上 → **恒定欠载**(实测 50~76%)→ 每欠载帧跳回最新包 → "一卡一卡"。
- 反向调大回看又让渲染滞后变大——**回看量本身没有安全取值**。
- SP2 的做法:不做插值,**始终拿最新包**,按速度把它"播到现在",失败模式是"位置估计略有偏差"而非"跳变"。

### 9.2 现行逐帧管线(接收端)

驱动:`MpNetworkManager : MonoBehaviour`,`[DefaultExecutionOrder(1000)]`,挂 `DontDestroyOnLoad` 的 `MPNetwork` 对象;主循环在 **`Update()`**,`LateUpdate()` 只重写朝向。

```
Update
 └ UpdateRemoteCrafts()
    ├ rc.Node.InContactWithPlanet = true                // 每帧重申,防游戏切回轨道推进
    ├ rc.TryGetNewest(out latest)                        // 取最新包(不再插值)
    ├ ext = latencySec + ageNow                          // ★ 连续外推量(见 9.3)
    ├ latest.Position += latest.Velocity * ext           // ★ dead-reckoning
    ├ latest.Position += a * (0.5*ext*ext)               // ★ 2 阶外推(acceleration-smoothing,已开)
    ├ latest.SrfRel *= Euler(Flip(ω)*ext*sign)           // ★ 朝向外推(已开)
    ├ latest = ApplyRemoteSmoothing(rc, latest, dt, ext) // ★ 指数平滑(见 9.4)
    ├ ApplyRemoteState(rc, latest)                       // 写 GroundedSurface*/根/朝向/body 位姿/部件/控制
    └ EngineVisualSync.DriveGhostEngineVisuals(rc)       // 尾焰视觉
```

写 Transform 的最终顺序:① `SetStateVectorsAtDefaultTime`(逻辑状态)→ ② `ApplyRemoteBodyPoses`(body 相对 comRot)→ ③ `RecalculateFrameState`(逐 body 加 positionDelta + 写根位置)→ ④ `LateUpdate` 只重写旋转。

### 9.3 外推量(核心公式)

```csharp
float latencySec = (rc.LatencyEmaMs > 0f ? rc.LatencyEmaMs : rc.GapEmaMs) / 1000f; // 单向延迟 EMA(RTT/2)
rc.VirtualAge += Time.unscaledDeltaTime;                 // 自走时钟:每帧 +dt
vaCap = 2 * SendIntervalEst;  if (VirtualAge > vaCap) VirtualAge = vaCap; // 每帧钳制(防无界漂移)
float ageNow = va;                                       // 外推用"包龄"= 有界 VirtualAge
if (RemotePausedRamp > 0) ageNow = Lerp(ageNow, 0, ramp); // 暂停:收敛到 0
float ext = (latencySec + ageNow) * SenderMotionRate;    // 发送端时间基换算(慢放/暂停兼容)
if (ext > 1.0f) ext = 1.0f;                              // 安全上限 1s
```

- `LatencyEmaMs` = 单向延迟 EMA(首测直取,之后 0.95/0.05),RTT 探测给出:RTT/2。无时钟同步 → 不用包内时间戳,只用本地 `Time.unscaledTime` 与包到达时刻。
- `VirtualAge` 每包到达 −= 内容增量(包时间差),突发到达时目标连续;**仅用于外推,不做判定**;判定用自重置的 `RealAgeSec`。
- 目标随时间**连续**前进 ⇒ 包到达/丢失/突发都不让目标跳变 ⇒ 消除"一卡一卡"。
- 速度是**地表相对速度**(发送端 `PlanetVectorToSurfaceVector(Velocity) − CalculateSurfaceVelocity(pos)`),接收端加回自转项,否则静止船会以 158.85 m/s 被外推(2026-08-22 第三轮定位的根因)。

### 9.4 平滑层 `ApplyRemoteSmoothing`(现行参数)

| 项 | 现行取值 | 说明 |
|---|---|---|
| NaN/Inf 防御 | `!IsFinite(Position/Velocity)` → 直接快照 | 坏包不污染平滑 |
| 首帧/`dt<=0` | 直接快照 + 返回 | 建立基准 |
| **静止锁定** | `speed < 0.5f && (target−smoothed).sqrMagnitude < 0.0025f`(0.05m)→ 快照 | 杜绝"双方不动"时的蠕动 |
| **速度自适应 k** | `k = Lerp(0.1, 0.6, min(1, speed*0.02))`(上限 0.6) | 慢船重平滑、快船近瞬时(上限 0.6:高速突发被低通吸收,不 1:1 透出) |
| **收敛(帧率无关)** | `alpha = 1 − Pow(1 − k, dt * 50)` | 时间常数 ≈0.2s(等价 SP2 每物理步 50Hz) |
| **单帧位移上限** | `maxStep = 1.5 × v × dt` | 异常跳变按上限逐帧摊平;匀速飞行零影响 |
| **瞬移阈值** | 距离 `> 100.0` m → 直接快照 | 生成/大修正/失步时秒对齐 |
| 旋转 | `Slerp(..., Clamp01(2.5 * dt))` | SP2 同款 |
| **每 body** | `alphaBody = Clamp01(10 * dt)`;位置 snap 阈值 `sqrMagnitude < 0.01f`(0.1m)、旋转 `Angle < 0.01f` 度 | 与 body-sync 的索引契约对齐;数量变化时重对齐+快照 |

### 9.5 完整修复链(结论;逐轮日志已删)

| 轮 | 症状 | 根因 | 修复 |
|---|---|---|---|
| 三轮 | 静止/慢速船幽灵突发位移(0.3~83m/3s) | **发送端速度帧错误**:`PlanetVectorToSurfaceVector` 是纯旋转不减自转项 → 静止船上报恒定 158.85 m/s | 发送端:地表相对速度 = 转地表 − `CalculateSurfaceVelocity(pos)`;接收端:惯性速度加回自转项 |
| 四轮 | 有速度时暂停 → 观察方位置抽搐 | 暂停后位置冻结但 `Velocity` 保留暂停前值 → dead-reckoning 放大 | 协议尾部追加 `Paused` 标记(旧包 EOF→false,向后兼容);暂停时上报间隔 ≥125ms;接收端冻结判定(标记 ∨ 连续 2 包零位移)+ `RemotePausedRamp`(0.15s 过渡)收敛外推量到固定单向延迟 |
| 五轮 | 静止一方"抽搐"(comRot 连带移动) | comRot 是根 body 内后代,写 body 世界位置连带移动 comRot;双写基准不同 → 反馈环 | 用**逻辑 comRot 位姿**(状态包导出 `TryGetLogicalComPose`)作 body 摆放基准,不读接收端实时 comRot |
| 六轮 | 慢放时外推超前 → 每包向后锯齿 | 慢放是 `timeScale<1`,但游戏 `FlightState.Time` 不缩放 → 包时间倍率测不出慢放 | 位置基 `SenderMotionRate`(相邻包位移 ÷ 速度×真实到达间隔);外推量 `ext ×= mRate`;`MaxMotionRateStep=0.15`/包钳制切换瞬间尖刺 |
| 七轮 | 包间隔突发下 ext 摆动 | mRate 被 0/几百 ms 交替的瞬时间隔污染(0.03~1.14) | 到达间隔**慢 EMA**(时间常数≈1s)作分母 |
| 八轮 | VA 无界漂移 → gapfreeze 永久闩锁 | VA 用作判定且无界积分漂到 30s | VA 与 RealAgeSec 分离;VA 每帧钳到 `2×SendIntervalEst`,仅做外推 |

### 9.6 接收端周期日志(`MP smoothing`,3s)

字段:`buf`(缓冲数,恒 1)、`rtt/2`、`gapEMA`/`jitterEMA`、`fps`(接收端渲染帧率)、`recvHz`(到达率)、`frames`、`snap`/`extrap`/`interpPct`(结构性常量)、`frozen`/`paused`/`ageNow`/`stall`/`pkΔ`、`rate`/`mRate`/`mRateWin`/`extWin`/`gapWin`/`gapFreezeF`、`moveMax`(单帧渲染位移峰值)、`move3s`/`pktJump`/`vel`/`acc`/`w`/`headYaw`/`head3s`、`newest`(最新包位置)、`posErr`、`moveDelta`/`bodyDelta`/`bodyTgt`/`bodyBig`/`bRmap`、`tfDrift`(游戏是否回推幽灵)。

---

## 8. 调试工具(代码仍在)

### 8.1 延迟模拟传输层 `LagSimTransport`(方案 A)

- **文件**:`Assets/Scripts/Net/LagSimTransport.cs`。实现 `IMpTransport` **装饰器**,包住任意底层传输(默认 `TcpTransport`),只在**接收路径**注入网络条件;对 `MpNetworkManager` 完全透明。
- **控制台命令**(DevConsole):`NetSimDelay <ms>`、`NetSimJitter <ms>`、`NetSimLoss <pct>`、`NetSimDuplicate <pct>`、`NetSimOn/Off/Reset`、`NetSim`(查看配置+投递统计)。数值命令与总开关分离;关闭=直通。
- **挂载点**:`TcpHostLobby/TcpJoinLobby`(Mod.cs)+ UI TCP 按钮创建传输时经 `LagSimTransport.MaybeWrap` 自动包装;Steam 不包装。
- 语义:延迟打"接收侧",RTT 探测如实累计;接收端看到的包到达分布与真实公网一致。

### 8.2 用法速查

```
NetSimDelay 150      # 只设数值
NetSimJitter 30
NetSimLoss 2
NetSimOn             # 总开关(开房时自动包装;活跃实例实时生效)
TcpHostLobby 25555   # 或客户端 TcpJoinLobby <ip> 25555
NetSim               # 查看配置与投递统计
```

诊断数值看 `Player.log` 的 `MP smoothing P#` 3s 周期行(无悬浮窗)。UI 方式:联机面板 →「网络延迟模拟(NetSim)」分组。
