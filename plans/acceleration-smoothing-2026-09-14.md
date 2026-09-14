# 远程船 2 阶外推方案(acceleration-smoothing)— 加速度 + 旋转速率

> 状态:📋 **评估完成,未实施**(2026-09-14 与 dev 讨论后用户拍板「先记录、不急着动手」)
> 关联:[`latency-smoothing-2026-08-22.md`](archive/latency-smoothing-2026-08-22.md)(本方案是 §9 现行 1 阶外推管线的 **2 阶扩展**,§9.18 有交叉引用);[`body-sync-2026-08-18.md`](archive/body-sync-2026-08-18.md)(每 body 位姿域;本方案只做 craft 级,body 级角速度外推列为后续,不在 P0/P1)
> 目标:恒加速段(爬升/重力转弯/刹车)不再系统性滞后/每包纠偏;转弯船朝向不再恒定滞后。

---

## 〇、动机(dev 讨论要点,2026-09-14)

> dev 原话:"Acceleration and rate of rotation (the 2nd derivative in both domains) are key to get it right.
> Crafts are constantly changing speed and heading, if you don't know by how much it'll be jumping states constantly.
> Even the 2nd derivative isn't enough sometimes because people may be changing inputs mid flight, that's unavoidable
> but it should be a lot more subtle."

译文要点:
- **加速度 + 旋转速率(两个域的 2 阶导)是关键**:飞船时刻在变速度/变朝向,不知道变化率就永远在"每包跳状态";
- **2 阶也不是万能**:玩家中途改输入(收油门/推杆)不可预测,那是无法避免的;但有了 2 阶项,这类突变造成的纠偏会**小得多(subtle)**——误差 ∝ 加速度的*变化量*而不是全量。

**评估结论**:观点成立,且精确对应现行实现的两个真实缺口——**平移只有 1 阶外推、旋转完全没有外推**(见 §一)。

**【决策:2026-09-14】** 用户与 dev 讨论后拍板:**先存档评估、不急着动手**。开工顺序见 §六,实施时回填本文件状态。

---

## 一、现状映射(代码/文档核对)

| 域 | 现行实现 | 缺口 |
|---|---|---|
| 平移 | 1 阶 dead-reckoning `pos += v·ext`,`ext = (latencySec + age) × mRate`,封顶 1s(`MpNetworkManager.cs:2012`,latency-smoothing §9.3) | **无 2 阶(加速度)项** → 恒加速段系统性漏算 `½·a·ext²`,包到即被拉回 = 每包纠偏 |
| 旋转 | **无外推**,只有 `Slerp(…, Clamp01(2.5·dt))` 平滑追最新朝向(§9.4) | 转弯船恒定滞后 ≈ 平滑时间常数 0.4s + latencySec 的旋转量 |
| 已有"症状级"缓解 | 指数平滑(速度自适应 k)、`1.5×v×dt` 单帧上限、>100m 瞬移、冻结(§9.7/9.8)、mRate(§9.14~§9.16) | 压的是**表现**,不消除**系统性偏差** |

**量级估算**(「每包跳状态」的数值来源):
- `ext=0.3s`(RTT≈300ms 常见值)、`a=30 m/s²` → `½·a·ext² ≈ 1.35m`;`ext=1s`(安全上限)→ **15m**;
- 重力:弹道段 `½·g·ext² ≈ 0.8m`@0.4s(1 阶外推同样漏算);
- 输入突变后的残余误差 ∝ `½·Δa·ext²`(加速度的**变化量**,不是全量)→ 远小于 `½·a·ext²` → 正是 dev 说的 "a lot more subtle" 的数学含义,方案成立。

---

## 二、数据可得性(反编译已核实 ✅)

发送端 `craft.CraftScript.FlightData`(`CraftScript.cs:358`)直接可取,零额外成本:

| 数据 | 语义 | 出处 |
|---|---|---|
| `FlightData.Acceleration` | Vector3d,**行星系**、**含重力**(根 body 刚体速度差分测量) | `CraftFlightData.cs:58-74`(`AccelerationFrame = bodyScript.Acceleration` :593;`BodyScript.cs:513`) |
| `FlightData.AngularVelocity` | Vector3d,**craft 局部系**(根 body 刚体角速度 + SR2 符号翻转 `(-x,y,-z)`) | `CraftFlightData.cs:113-129` |

坐标系注意:
- 加速度转地表系**只做纯旋转**(同速度的 `PlanetVectorToSurfaceVector` 路径);Coriolis/离心项在测试行星 ≈0.1 m/s²,相对 craft 加速度(10~100 m/s²)可忽略;
- `ω` 保持 craft 局部系 → 朝向外推**右乘**:`SrfRel' = SrfRel * AngleAxis(|ω|·ext, ω̂)`;⚠️ 符号翻转/轴序约定必须双端实测验证(§六-1)。

---

## 三、方案(两期;均复用 `Paused` 的"尾部追加 + EOF 容错"协议模式,见 latency-smoothing §9.7)

### 期 0 —— 零协议改动,接收端自推导(单端升级安全)
- `a` = 相邻包速度差分 ÷ 真实到达间隔,EMA + 幅值钳制;外推加 `½·a·ext²·mRate²`;
- `ω` = 相邻包 `SrfRel` 角度差分,EMA + 钳制;朝向右乘外推;
- 缺点:测量噪声 + 一包滞后(~50ms)+ 快速自旋混叠(20Hz 采样,Nyquist 10Hz)。

### 期 1 —— 协议尾部追加 `Acceleration` + `AngularVelocity`(双端同版)
- 发送端 `TrySampleLocalCraft`(`MpNetworkManager.cs:2673`)直接采样 `FlightData`,尾部追加(旧包 EOF → 零值,同 `Paused`;`WriteRecdata/ReadRecdata` `MpMessage.cs:477/530`);
- 接收端 `UpdateRemoteCrafts`(`:2012` 附近):`pos += v·ext + ½·a·ext²`(2 阶项乘 `mRate²`,慢放兼容),朝向 `SrfRel *= AngleAxis(|ω|·ext, ω̂)` 后再进平滑;
- 带宽:+6 floats ≈ 24B/包(可压缩至 ~12B,§P2 Quaternion32 同思路)。

### 兼容性护栏(沿用/新增)
- **冻结期兼容**:暂停时 mRate→0 → ext→0 → 2 阶项自然→0,与 §9.7/9.8 冻结逻辑无冲突;
- NaN/Inf 防御、`a`/`ω` 幅值钳制(如 `|a|≤60 m/s²`、`|ω|≤3 rad/s`,可配)、`>100m` 瞬移、`1.5×v×dt` 单帧上限全保留;
- 诊断:`MP smoothing` 行加 `acc=`/`aExt=`/`ω=`;发送端 `MP sendDiag` 行加 `accRaw=`/`ωRaw=`。

### 风险(诚实版)
1. `Acceleration` 是**测量值**(刚体速度差分,一帧滞后 + 噪声)→ 发送端 EMA + 钳制必须到位,否则 2 阶项自己成为新抖动源;
2. **输入突变**(unavoidable):误差 ∝ `½·Δa·ext²`,比 1 阶时代小一个量级,但不会为零——预期"更 subtle",不要指望消失;
3. `ω` 的 SR2 符号翻转/轴序约定,右乘前必须双端实测(转弯段对端朝向不超前不滞后);
4. 慢放:2 阶项必须乘 `mRate²`(位置推进 ∝ t²),否则慢放段反而引入新误差;
5. 期 1 混版本:双端需同版(同 §9.7 结论);期 0 无此问题。

---

## 四、回归判据(开工后按此验收)

- 保持:§9.17 终态全部指标——`b0dLate=0`、`gapEMA≈50ms`、暂停/慢放/切换速度模式三项不回归;
- 新增:恒推力爬升段/重力转弯段 `moveDelta`、`pktJump` 应**明显小于当前**(2 阶项吃掉系统性纠偏);转弯段朝向无恒定滞后;
- 突变场景(收油门/急转):单帧位移 ≤ `1.5×v×dt` 物理上限保持,纠偏幅度比 1 阶时代小。

---

## 五、关联文档

- [`latency-smoothing-2026-08-22.md`](archive/latency-smoothing-2026-08-22.md) §9(现行接收端管线;§9.18 有本方案交叉引用)
- [`body-sync-2026-08-18.md`](archive/body-sync-2026-08-18.md)(body 级位姿;body 级角速度外推列为后续,不在本方案 P0/P1)
- [`remote-craft-velocity-2026-09-13.md`](remote-craft-velocity-2026-09-13.md)(速度域的另一处 1 阶缺口,与本方案独立)

---

## 六、待办 / 开工顺序(实施时按此推进并回填)

1. **数据有效性验证(必须先做)**:发送端临时日志打印 `FlightData.Acceleration / AngularVelocity`(量级/方向/暂停时行为),真实飞行 + 转弯/自旋段各一次;
2. 期 1:协议尾部追加两个字段(`RemoteDataPack` + `WriteRecdata/ReadRecdata` EOF 容错);
3. 期 1:接收端 2 阶外推(平移 `½·a·ext²·mRate²` + 朝向 `ω·ext` 右乘)+ 钳制/防御/诊断;
4. NetSim(150ms/30ms)+ §四 回归判据双端实测;
5. 完成 → 更新本文件状态与 `plans/README.md` 索引。
