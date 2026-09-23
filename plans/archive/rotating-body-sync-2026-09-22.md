# 旋转 body(旋翼)位置快照同步修理(rotating-body-sync)— 每 body 角速度进协议 + 接收端旋转外推

> 状态:✅ **已实现归档**(2026-09-22 拍板,同日编码完成 + 双端实测通过,用户确认"修好了"收工)
> 日期:2026-09-22
> 关联:[`../proposals/physics-sync-2026-09-14.md`](../proposals/physics-sync-2026-09-14.md)(SP2 式每 body 速度注入的全局评估;本提案 = 其 **P1「旋转 1 阶外推」在旋翼场景的聚焦论证**,P0 游戏侧速度修复仍归 physics-sync,不在本案);[`acceleration-smoothing-2026-09-14.md`](acceleration-smoothing-2026-09-14.md)(朝向外推 ω·ext 已落地,本提案把同一手法扩展到每 body;已归档);[`body-sync-2026-08-18.md`](body-sync-2026-08-18.md)(BodyPoses 每 body 位姿域,本提案的协议基础);SP2 参考:`<SP2_MP>\Multiplayer\NetworkBodyScript.cs`(只读)
> 主题:旋翼叶片等**高速旋转 body** 在现行"每包绝对位置快照 + 10·dt 指数平滑"下的固有缺陷——20Hz 采样追不上叶片转速 → 接收端叶片"跳着转"。最小修理 = 协议补每 body 角速度 + 接收端包间旋转外推,复用已落地的朝向外推手法。
> 一句话结论:**改 2 个平行 List + 1 段外推,≈1~2 天;不做 physics-sync 的 P0(游戏侧速度修复),不做真实刚体。**

---

## 〇、经验教训(2026-09-22 实测通过后固化)

1. **高速旋转 body 的位置快照必须配"相对位置变化率 v"逐帧积分,单靠角速度 ω 不够**:编码初期只传 ω、接收端绕 comRot 原点转位置——但桨毂不在 comRot 上(直升机偏移 0.5~2m),ω·ext 外推画错圆、bodyTgt 降不下来。改成 ω(朝向)+ v(位置差分)双字段后一次通过。**v 直接是"相对位置变化率",不需要知道旋转中心**,这是关键。
2. **v 存 body 局部系,接收端用当前平滑朝向 `sr·v` 转出方向**:叶片局部系中切线方向恒定,方向随旋转自动累计,无需跨帧状态(第 n 帧方向 = 包时刻方向转 n·帧角)。
3. **旋转 body 的平滑必须"推进再收敛"(50·dt),10·dt 追不上转速**(31rad/s 叶片每帧转 ~0.5rad,10·dt 每帧只追 ~0.09rad)。
4. **ω 只影响朝向旋转方向,位置由 v 独立推进** → 位置侧天然无符号问题;朝向符号按朝向外推的 errF+/errF- 实测法验证(本案内置发送端 bErr+/bErr-)。
5. 暂停时叶片物理停转但 `angularVelocity` 保留暂停前值 → 发送端必须随 `Paused` 清零 ω/v,否则接收端暂停中叶片空转。

---

## 〇、动机(2026-09-22 双端日志实证)

VM = 直升机(CommandDisc + **Rotor Blade×8** + Pusher Prop),本机 = 火箭。双方 r10 基线。

**发送端(VM)自检 —— 只有叶片在跳,机身稳:**
```
MP sendDiag P1: body0RelΔ=0.0000m body0Rel=0.6615m bodyCnt=15
                bodyMaxRelΔ=4.0515m bodyMaxΔi=9(id=10)   ← Rotor Blade
                bodyMaxRelΔ=2.2409~5.5700m bodyMaxΔi=3/9(id=4/10)  ← 恒为叶片
```
`body0`(CommandDisc)相对 comRot 恒稳 0.6615m,`bodyMaxΔi` **永远落在 Rotor Blade** 上 → 数据干净,问题局限在旋转 body。

**接收端(本机)smoothing P1 —— 叶片目标误差恒定 5.9m,平滑层追不上:**
```
bodyTgt=5.93m bodyBig=248   bodyDelta=1.20m
bodyTgt=5.67m bodyBig=712
bodyTgt=5.31m bodyBig=512   ... 全程 bodyTgt≈4.7~5.98m, bodyBig 数百
```
中段 VM 加速飞行(vel 4~39m/s)时 bodyTgt 仍 4.87~5.98m —— **叶片跳动与船速无关,纯旋转速度问题**。

**对照(同一段日志):平移管线全绿** —— `mRate=1.001、stall=0、move3s=122m、moveMax=6.7m`。旋转 body 是唯一未修的观感源。

---

## 一、根因(为什么位置快照治不了旋翼)

### 1.1 机制(JNO 反编译)

旋翼是**独立 Rigidbody 物理旋转**,不是视觉动画:
- `PropellerAssemblyScript.cs:422`:`_propContainer.transform.localRotation *= Quaternion.Euler(0, num, 0)`(num = −30·dt·Invert)
- `:439-440`:从 `_propellerBody.angularVelocity` 算 `RpmPhysical` → **叶片 Transform 由刚体角速度连续驱动**,帧间相位连续变化

### 1.2 三处固有缺陷

| # | 缺陷 | 说明 |
|---|---|---|
| **R1** | **采样混叠(aliasing)** | 20Hz 包采样 vs 叶片 300+ RPM(≈5 rev/s)→ 每包相位转过 90°+,欠采样 → 接收端只能看到"离散相位跳",重建不出连续旋转 |
| **R2** | **对称叶片相位歧义** | 8 片对称叶片,θ 与 θ+2πk/8 位置重合 → 位置快照**语义上无法表达"转了几圈"**,只有角速度能解 |
| **R3** | **10·dt 平滑追不上** | `alphaBody=Clamp01(10·dt)` 对每包 5.9m 的目标跳变,每帧只能追一小段 → 叶片"跳着转"而非平滑转 |

### 1.3 三家对照(为什么别人没有这个问题)

| | 旋转 body 处理 | 出处 |
|---|---|---|
| **SP2** | 每 body 传 `angularVelocity`,接收端 `num2=Clamp(now−pktTime,0,0.25)`,**旋转外推 `q·AngleAxis(ω·num2)` + Slerp 2.5·dt 追残差**,再写回刚体 → 包间叶片连续转 | `NetworkBodyScript.cs:166-168`;`CraftStateSerializer.cs:44-146` |
| **LMP(KSP)** | 远端状态写回刚体 `velocity/angularVelocity`,物理引擎连续积分(与 SP2 同思路,不做逐 body 位置快照) | `<LUNA_MP>` 各 `Vessel*Sync` |
| **我们的 mod** | **只传每包绝对位置快照 + 10·dt 平滑,无角速度** → R1/R2/R3 全中 | `MpNetworkManager.cs` `ApplyRemoteState` body 段 |

**关键差异**:SP2/LMP 都同步**角速度**让接收端"包间连续转 + 包到修正",我们只有"包到跳 + 平滑追"。这正是 `bodyTgt` 恒 5.9m 的直接原因。

---

## 二、方案(最小修理,复用既有手法)

### 2.1 内容(四件套)

| # | 改动 | 落点 | 说明 |
|---|---|---|---|
| 1 | **协议尾部追加 `BodyAngularVelocities`**(每 body 3×float,body 局部系,EOF 容错同 `Paused`) | `MpMessage.cs` `WriteRecdata/ReadRecdata` | 旧包读不到 → 空 → 零值,单端升级安全 |
| 2 | **协议尾部追加 `BodyVelocities`**(每 body 3×float,body 局部系,EOF 容错同 `Paused`) | `MpMessage.cs` `WriteRecdata/ReadRecdata` | 见下方"实施中修正":桨毂不在 comRot 上,单 ω 位置外推画错圆 → 需 v 逐帧积分 |
| 3 | **发送端采样**:在既有 body 遍历里同步读 `RigidBody.angularVelocity`(→body 局部系 ω)+ `BodyPositions` 数值差分(→body 局部系 v),与 `BodyPositions` 同索引平行 List | `MpNetworkManager.cs` 采样段 | 零额外循环;同 physics-sync §3.3 CPU≈0 |
| 4 | **接收端逐帧积分外推**:旋转 body 的平滑状态每帧"推进再收敛"——位置 `sp += (sr·v)·dt`、朝向 `sr ← qFrame·sr`(绕 ω 轴),再向包内绝对相位 50·dt 收敛 → **包间连续、包到只修小残差** | `MpNetworkManager.cs` `ApplyRemoteState` body 段 | 模拟 SP2"写回刚体由 PhysX 积分"(我们无 PhysX,改手动逐帧积分);非旋转 body 保持 10·dt 不变 |

> **方案修正(2026-09-22,编码时发现、实测确认)**:原方案 §2.1 只做"朝向 ω·ext + 位置绕 comRot 轴转"。但直升机桨毂**不在 comRot 上**(偏移 0.5~2m),ω=31rad/s、ext≈0.15s 时位置外推误差 ≈ 偏移×θ 可达数米 → bodyTgt 降不下来。故改为**位置用 v(相对 comRot 线速度,差分)逐帧积分**:v 直接是"相对位置变化率",不需要知道旋转中心,欧拉积分 60fps 下误差 ~0.07m/帧、包到修正。**带宽相应 +12B/body(共 +24B/body)**。

### 2.2 带宽(引用 physics-sync §3.2 测算)

| 新增 | 每 body | 20Hz×20 body | 占当前比例 |
|---|---|---|---|
| BodyAngularVelocities(3 float) | +12B | +4.8 KB/s | +36% |
| BodyVelocities(3 float) | +12B | +4.8 KB/s | +36% |
| **合计** | **+24B** | **+9.6 KB/s** | **+72%** |

**绝对值 ≈ +9.6 KB/s,仍可忽略**(Steam P2P / 局域网 TCP 均无压力);不引入 physics-sync 的 P0 速度字段(那是游戏侧静止船读速度≈0 的问题,与本协议字段无关)。

### 2.3 明确不做(范围外)

- ❌ 游戏侧速度字段修复(静止船读 ≈0)→ 仍归 physics-sync P0
- ❌ **每 body 线速度注入到真实刚体**(SP2 `RigidBodyRemote` 等价物)→ physics-sync P3 已判不推荐;本案的 `BodyVelocities` 是**协议字段 + 手动逐帧积分**,非刚体写入
- ❌ 真实刚体架构(SP2 `RigidBodyRemote` 等价物)→ physics-sync P3 已判不推荐
- ❌ **低速悬停被误判冻结**(stall 0.4m/s 阈值,2026-09-22 同批日志的**另一个**独立根因)→ 另案,不在本提案

---

## 三、风险与决策点

| # | 风险 | 缓解 |
|---|---|---|
| 1 | **ω 符号 / 轴序 / 局部系基准**右乘前错误 → 叶片反向或乱转 | 复用朝向外推已定案的 `Flip(ω)` + 符号实测流程(acceleration-smoothing §一;2026-09-19 sendDiag `errF+/errF-` 实测法)。本案已内置发送端 `bErr+/bErr-` 自校验(用上一包 v_local 推进预测位置,误差小者 = 正确方向);ω 只影响朝向旋转方向,位置由 v 独立推进(v 方向经双端一致坐标变换,天然正确,无符号问题) |
| 2 | **对称叶片歧义(R2)在"只传 ω、不传相位增量"下仍残留** | 每包**绝对相位快照仍在**(现状不变),ω 只负责包间推进、包到用快照收敛 → 相位歧义只影响"包间 90° 内",不影响稳态;若实测仍跳,加"ω 一致性校验"(|ω| 与快照相位差分不符时信任快照) |
| 3 | 高转速叶片 ω 采样噪声 | 发送端对 `angularVelocity` 做钳制(`MaxBodyAngVelRad=180`)+ NaN/Inf 防御;v 差分同样钳制(`MaxBodyVelMs=180`)+ 有限值防御 |
| 4 | **v 差分毛刺**(瞬时空/部件重建时位置突变) | `MaxBodyVelMs` 钳制;瞬时空沿用上一帧位姿,v=0;接收端 50·dt 收敛 + 包到绝对快照硬修正 |
| 5 | **积分误差累积**(逐帧欧拉积分 vs 真实圆弧) | 60fps 下每帧 ~0.5rad,欧拉误差 ~0.07m/帧、包间(50ms)累积 ~0.2m,包到被绝对快照收敛吸收;若弱机帧率低(fps 11~27)误差增大 → 观感判定只用 VM 强机(§五) |
| 6 | 带宽 +72% 在弱机/双开下被放大 | 绝对值 +9.6 KB/s 仍极小;若 P2 预算化(SP2 5+5 优先级)总包体反降 |

---

## 四、实施路径(拍板后,单人约 1~2 天)

1. **数据验证**(半天):发送端临时日志打印每 body `RigidBody.angularVelocity` 量级/静止是否≈0/叶片对称性;同时确认 `BodyPositions` 采样点与 `RigidBody` 引用可得性;
2. **协议 + 采样 + 外推**(0.5~1 天):三个改动件(§2.1 表),全部挂现行 `ApplyRemoteState` body 段,复用朝向外推的 `ω·ext` 与钳制;
3. **验证**(半天):直升机悬停 + 加速飞两段,判据见 §五;NetSim 150ms/30ms 双端。

---

## 五、回归判据(验收,全部二进制可判定)

- **目标达成**:接收端 `bodyTgt` 从恒 ≈5.9m 降到平滑残差量级(<0.5m),`bodyBig` 归零;VM 强机目视叶片**平滑旋转不跳**;
- **不回归**(latency-smoothing §9.5 终态):`b0dLate=0`、`gapEMA≈50ms`、暂停/慢放/切换速度模式三项、`move3s≈pkt3s` 两端成立;
- **带宽**:加角速度后 `gapEMA/jitterEMA` 不劣化;
- **观感判定**:只用 VM 客户端(本机弱机 fps 11~27 会把任何平滑差异放大,文档 §六之二十一)。

---

## 六、决策记录

- 【决策:✅ 已实现并双端实测通过】2026-09-22 用户"动手,开始修理这个" → 实施;同日完成编码 + 双端实测,用户确认"修好了" → 归档。编码中发现:桨毂不在 comRot 上,单 ω 位置外推画错圆 → **升级为 ω(朝向)+ v(位置差分)双字段逐帧积分**(§2.1 修正,带宽 +24B/body)。代码落地:`Mod.cs`(字段)、`MpMessage.cs`(序列化)、`MpNetworkManager.cs`(发送端采样 ω/v + 接收端逐帧积分 + bErr+/bErr- 自校验 + spin/wMax 诊断);`dotnet build` 0 警告 0 错误。
- 关联待办(不在本案):低速悬停误判冻结(stall 0.4m/s)另案评估。
