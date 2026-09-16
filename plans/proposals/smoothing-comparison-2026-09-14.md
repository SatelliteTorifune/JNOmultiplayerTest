# 平滑方案对照研究(SP2 / LunaMultiplayer / 现行实现)+ 改进清单(smoothing-comparison)

> 状态:📋 **研究完成,未实施**(2026-09-14;动机:用户反馈"目前平滑效果不是很好",通读 SP2 与 LunaMultiplayer 反编译/源码后成文;改进清单 R1~R6 见 §五,待拍板实施)
> 关联:[`archive/latency-smoothing-2026-08-22.md`](../archive/latency-smoothing-2026-08-22.md)(现行接收端管线 §9 的"方案出处");[`acceleration-smoothing-2026-09-14.md`](../acceleration-smoothing-2026-09-14.md)(2 阶外推期 1 已落地,R1 旋转外推即其中待开部分);[`physics-sync-2026-09-14.md`](physics-sync-2026-09-14.md)(SP2 物理同步机制详析 + P0/P1 建议,R6 与其 P0 重叠)
> 参考源码(只读):SP2 `<SP2_MP>\Multiplayer\`;LMP `<LUNA_MP>\LmpClient\Systems\`(均已核实)

---

## 〇、一句话结论

- **SP2** = 服务器权威**物理时钟**(漂移校正)+ **1 阶外推(钳 0.25s)** + **旋转外推(右乘 `q·AngleAxis(ω·t)`)** + **每包/每 FixedUpdate 把速度写回真实刚体**(物理连续)。
- **LMP** = **时间戳队列 + 双状态插值 + 自适应补时**,渲染刻意落后一个"InterpolationOffset",**完全不做外推**,空缓冲时**冻结保持不跳变**(KSP 有全局 UniversalTime 可依赖)。
- 我们 = SP2 式"最新包 + 1 阶外推(latencySec+age,钳 1.0s)+ 每帧指数平滑",**无时钟同步、旋转外推关着、速度不写回**。
- **改进优先级**:R1 开旋转外推 > R2 延迟估计 EMA > R3 外推上限收紧(小改立竿见影);R4 LMP 式自适应补时 / R5 时钟同步 / R6 速度写回(较大,独立排期)。

---

## 一、SP2 方案(SimplePlanes 2 官方联机)

源码:`Multiplayer/CraftStateSerializer.cs`(收发)、`NetworkAircraftScript.cs`(FixedUpdate 施速)、`FlightSceneNetworkScript.cs`(时钟)、`BodySyncData.cs`(优先级)。

### 1.1 时钟(权威 + 漂移校正)
- 服务器 `_physicsTime` 每 FixedUpdate +`fixedDeltaTime`(`FlightSceneNetworkScript.cs:55,486-496,615-634`);客户端本地 `_physicsTime` 自走,`_targetPhysicsTime = 服务器值 + RTT/2`,漂移 >0.05s 且持续 10 帧才硬对齐。
- 包内带时间戳(发送时 `PhysicsTime`),外推量 = `Clamp(本地同步时钟 − 包时间戳, 0, 0.25f)`(`CraftStateSerializer.cs:55`)——**确定、无 RTT 抖动**。

### 1.2 位置(1 阶,钳 0.25s)
```csharp
float num2 = Mathf.Clamp(physicsTime - num, 0f, 0.25f);      // 外推量
Vector3 vector5 = bodyRel + craftOrigin + velocity * num2;   // pos + v·num2
if ((vector5 - cur).sqrMagnitude > 10000f) snap;             // >100m 瞬移
else Lerp(cur, vector5, Lerp(0.1f, 1f, |v| * 0.02f));        // 速度自适应
```

### 1.3 旋转外推(右乘,与我们期 1 公式一致)
```csharp
Quaternion quat3 = Quaternion.AngleAxis(|ω|*num2*Rad2Deg, ω.normalized);
body.RigidBody.rotation = Slerp(cur, quat2 * quat3, 2.5f * dt);  // ★ 右乘
```

### 1.4 速度写回刚体(物理连续)
- 每包:把 `Velocity/AngularVelocity` 写进 `body.SyncData` 并**写回 RigidBody**(`:100-103`);
- **每个 FixedUpdate 重新施加**(`NetworkAircraftScript.cs:408-421`):非 owner 根 body 刚体 `velocity/angularVelocity = SyncData.*` → 两包之间刚体带最后网络速度滑行,不抖不跳。

### 1.5 发送(优先级挑 body,控制包体)
- 每包最多 **5 根 body + 5 子 body + 5 部件**;优先级 `Delta = (位移×20 + 旋转角×1) × (距上次发送+1)²`;根 body 发 6 元组(位置+Quaternion32+速度+角速度),子 body 只发位置+旋转(本地 Lerp)(`:149-247`,`BodySyncData.cs`)。

---

## 二、LunaMultiplayer 方案(KSP)

源码:`LmpClient/Systems/VesselPositionSys/VesselPositionUpdate.cs`(插值核心)、`VesselPositioner.cs`(应用)、`VesselFlightStateUpdate.cs`(控制输入)、`Systems/TimeSync`(时钟)、`Systems/SettingsSys`(插值偏移设置)。

### 2.1 时钟
KSP 全局 `UniversalTime` 经 `TimeSyncSystem` 同步(游戏自带统一时钟,KSP 专属);消息带 `GameTimeStamp` + `PingSec`。

### 2.2 双状态插值(队列 + 每 FixedUpdate 推进)
- 每个 vessel 一个消息队列(`TargetVesselUpdateQueue`);取最新两条做 `Update`(旧)/`Target`(新);
- `LerpPercentage = CurrentFrame / NumFrames`,`NumFrames = InterpolationDuration / fixedDeltaTime + 1`;位置/朝向 = `Lerp/Slerp(Update, Target, percentage)`(`VesselPositionUpdate.cs:75-81,176`)。

### 2.3 自适应补时(核心:渲染刻意落后)
- `InterpolationDuration = Clamp(Target.GameTimeStamp − GameTimeStamp + ExtraInterpolationTime, 0, Max)`(`:77`);
- `ExtraInterpolationTime` 由 `TimeDifference = UniversalTime − msgTime − PingSec偏移` 自适应(`:288-341`):**落后 → `CurrentFrame=MaxValue` 快速消费**;**超前 → `GetInterpolationFixFactor()` 加补时放慢**(按误差帧数阶梯放大,最小 fixedDeltaTime,`:346-375`);
- 意图 = "每条消息在 `发送时间 + InterpolationOffset` 处回放",缓冲始终维持在落后一个偏移量的深度(LMP 设置里用户可调;服务器延迟过高时 LMP 自动增大偏移,有 `IncreasedInterpolationOffset` 警告)。

### 2.4 空缓冲 = 冻结保持(不跳变、不外推)
源码注释原文:"ALWAYS set the position of the vessel even if we don't have anything in the queue. Otherwise its position will shake"(`:175-176`)——队列空时用**最后状态**继续摆位,不跳到最新包、也不按速度外推。

### 2.5 其它
- **无角速度外推**(消息只有 `SrfRelRotation`,旋转纯 Slerp);
- 落地 lat/lon/alt Lerp + **轨道参数在 UniversalTime 插值**(KSP 专属);`srfRelRotation` 同步;
- 物理:`VesselPositioner.SetVesselPositionAndRotation` 对 loaded 部件 **rigidbody 直写 position/rotation + `part.ResumeVelocity()`**,防物理引擎下一帧把插值位置弹回去(`:71-107`)。

---

## 三、三方对照

| 维度 | SP2 | LMP | 我们(现行) |
|---|---|---|---|
| 时钟 | 服务器物理时钟+漂移校正 | KSP UniversalTime(TimeSyncSystem) | **无时钟同步,`RTT/2+age`** |
| 取包 | 最新包 | 队列双状态插值 | 最新包 |
| 外推 | 1 阶 `pos+v·t`,**钳 0.25s** | **无外推** | 1 阶 `pos+v·ext`,**钳 1.0s** |
| 旋转 | `q·AngleAxis(ω·t)` 右乘 | Slerp | Slerp(ω 右乘已实现,`EnableRotationExtrap=false` 关着) |
| 速度写回刚体 | ✅ 每包 + 每 FixedUpdate | ✅ rb 直写 + ResumeVelocity | ❌ kinematic 幽灵,仅视觉注入(`InjectGhostMotion`) |
| 空缓冲 | n/a | **冻结保持** | 长静默冻结(ext→latencySec) |
| 慢放 | n/a | warp 子系统 | mRate(位置基倍率) |
| 发送 | 优先级挑 body(5+5+5) | 全量+队列 | 全 body 每包 |

---

## 四、诊断:为什么我们"效果不是很好"

基于 §三 差距 + latency-smoothing §9 的实测历史,候选病灶(按可能性排序):

1. **旋转无外推**(R1):转弯船朝向滞后 ≈ 0.4s(2.5·dt 平滑时间常数)+ latencySec → 视觉上"船头跟不上",转弯越急越明显。
2. **延迟估计直接进 ext,抖动未平滑**(R2):`rc.LatencyMs` 取自 OnPong 的 `RTT/2`,ping 抖动直接进 `ext` → 目标每包前后晃 → 平滑层追着晃(速度自适应 k 在高速时≈1,几乎不过滤)。
3. **外推上限过宽**(R3):SP2 钳 0.25s,我们 1.0s;高延迟下 `1s×v` 的 1 阶外推误差大,包到纠偏明显(我们的加速度项期 1 才补,且旋转项仍关)。
4. **无时钟同步**(R5,结构性):`latencySec + age` 对包龄/发包率敏感(§9 的 mRate/冻结/暂停全是它的补丁);LMP 靠时钟 + 补时彻底绕开,SP2 靠权威时钟绕开。
5. **速度源不准**(R6):外推输入是速度,但游戏侧速度字段缺自转项(remote-craft-velocity #10),physics-sync P0 未做。
6. 已排除/已处理:插值缓冲欠载(§9.1 弃用)、暂停抽搐(§9.7)、慢放锯齿(§9.10~9.16)、comRot 反馈环(§9.9/9.11)。

---

## 五、改进清单(待拍板)

| # | 改进 | 内容 | 成本 | 预期收益 |
|---|---|---|---|---|
| **R1** | **开启旋转外推** | 完成 sendDiag ω 符号自校验(`errF+/errF-/errR+`,单机即可定案)→ `EnableRotationExtrap=true` + `RotationExtrapSign=±1`;SP2 `q·AngleAxis(ω·t)` 右乘已验证同公式 | 极小(一行 + 已测) | 转弯滞后/橡皮筋明显改善,SP2 同款 |
| **R2** | **延迟估计 EMA** | `rc.LatencyMs`(OnPong RTT/2)做 EMA(如 α=0.1),或与 gapEMA 平滑混合;诊断加 `latEma=` | 小 | ext 不再随 ping 抖动 → 目标稳定,高速段抖动下降 |
| **R3** | **外推上限收紧** | 1.0s → ~0.4s(可配,DevConsole 或常量);长静默冻结保持;与 R2 配合降低纠偏 | 小 | 高延迟下误差/纠偏幅度下降,SP2 式"有界滞后"更稳 |
| **R4** | **LMP 式自适应补时**(可选) | 空缓冲=保持最近渲染位置(强化现有 freeze);gapEMA 突发时自动缩短外推 | 中 | 突发丢包/大间隔下不"跳",LMP 已验证思路 |
| **R5** | **时钟同步**(较大,SP2 思路) | mod 侧服务器权威时钟(随 unscaled 时间自走,非 `FlightState.Time`——慢放不缩放)+ 客户端 RTT/2 + 漂移校正;包带 mod 时钟时间戳;ext=同步时钟差(钳制);mRate 保留用于慢放 | 大 | 外推完全确定,去掉 RTT/2+age 抖动与 mRate/冻结补丁的根源 |
| **R6** | **速度写回/物理连续性**(独立) | = physics-sync P0:每 body 速度进协议 + 游戏侧速度修复(#10);kinematic 幽灵的"速度写回"仅做视觉/碰撞相对速度 | 大(另案排期) | 外推输入(速度)变准;游戏侧读速度正确 |

> **建议实施顺序**:R1+R2+R3 一轮小改 → 双端/单机实测 → 按需再 R4 / R5;R6 与 physics-sync 合并排期,独立于平滑。

---

## 六、决策记录 / 待办

- 【决策:2026-09-14】用户拍板:**先存档本研究,不实现**。R1~R6 待后续拍板。
- 【实施旁记:2026-09-14】代码侧同期已落地四项**未列入 R 清单**的旁路改动(代码注释记作 F1 虚拟 age 时钟 / F2' 到达间隔 EMA / F3 单向延迟 EMA / F4 发送端 sendTimer 余量钳制),其中 **F3 即 R2**;故 R2 可视为已落地(F3 形式),R1 / R3 / R5 / R6 仍未实施。现状描述以 `README.md` §三 平滑段为准,下次拍板时回填本清单与状态。
- 待办(实施时回填):
  1. R1:跑 sendDiag ω 符号自校验(单机,稳态转弯 ≥3s)→ 记录 `errF+/errF-/errR+` 最小值 → 定 `RotationExtrapSign`;
  2. R2:LatencyMs EMA + 诊断;
  3. R3:外推上限收紧可配;
  4. 双端 NetSim 回归(§四 + latency-smoothing §9.17 指标:不变差即可);
  5. 完成 → 更新本文件状态与 `plans/README.md` 索引。
