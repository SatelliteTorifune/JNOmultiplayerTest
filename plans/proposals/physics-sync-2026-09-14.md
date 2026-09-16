# SP2 式物理同步移植评估(physics-sync)— 每 body 速度注入 + 开销/工期

> 状态:📋 **研究完成,未实施**(2026-09-14 通读 SP2 反编译 + JNO 现状后成文;两问「开销多大 / 多复杂多久」已在 §四、§五作答,实施路径见 §六)
> 日期:2026-09-14
> 关联:[`acceleration-smoothing-2026-09-14.md`](../acceleration-smoothing-2026-09-14.md)(旋转 1 阶外推与 2 阶外推的姊妹篇;本方案 P1 即其中期 1);[`remote-craft-velocity-2026-09-13.md`](remote-craft-velocity-2026-09-13.md)(游戏侧速度缺自转项根因,本方案 P0 直接覆盖其修复);[`body-sync-2026-08-18.md`](../archive/body-sync-2026-08-18.md)(每 body 位姿域,本方案 P0 的协议基础);SP2 参考:`<SP2_MP>\Multiplayer\`(只读)
> 主题:把 SP2(SimplePlanes 2 官方联机)的「每 body 速度注入真实刚体」式物理同步机制解析清楚,对照 JNO 现状给出差距表,回答开销与工期,并给出**低风险移植路径(不照搬真实刚体架构)**

---

## 〇、一句话结论

SP2 解决「物理同步」的实质是:**接收端不重新算物理,而是让远程刚体"跟着网络速度滑"**——远程船保持真实非 kinematic 的 PhysX 刚体(但所有 `AddForce` 抛异常,不吃游戏力),位置/朝向由网络包 + 1 阶外推驱动,`velocity / angularVelocity` 每物理帧写回刚体。这样物理连续性、碰撞相对速度、游戏侧读速度全部"免费"正确。

对 JNO 的启示:**收益在"把速度写进游戏侧速度字段/刚体",代价不在"换真实刚体架构"**。JNO 的 kinematic 幽灵 + 连续外推已是 SP2 同款平滑管线,真正缺的是 **①协议里没有每 body 速度/角速度 ②游戏侧速度字段没写对(已知问题 #10)③旋转无外推**。这三项的带宽开销 ≈ 每包 +24B/body(可忽略),工期 ≈ 3~5 个工作日(含双端实测)。**不建议**为了照搬 SP2 把远程船改成真实刚体(1~2 周 + 高风险,且会重开已修完的抽搐/暂停/慢放问题)。

---

## 一、SP2 物理同步机制回顾(反编译结论)

SP2 联机在 `Multiplayer/` 下,核心五件套:

| # | 机制 | 位置 | 内容 |
|---|---|---|---|
| 1 | **权威时钟** | `FlightSceneNetworkScript.cs:55,486-496,615-634` | 服务器 `_serverPhysicsTime` SyncVar 每 FixedUpdate +`fixedDeltaTime`;客户端本地 `_physicsTime` 自走,`_targetPhysicsTime = serverValue + RTT/2`,漂移 >0.05s 且持续 10 帧才硬对齐。**所有接收端共享同一把全局物理时间**,包内时间戳才有意义 |
| 2 | **发送:按优先级挑 body** | `CraftStateSerializer.cs:149-247`;`BodySyncData.cs:89-118` | 每包最多 **5 根 body + 5 子 body**;优先级 `Delta = (位移×20 + 旋转角×1) × (距上次发送+1)²`——变化大 + 久未发的优先。根 body 发 6 元组(位置+32bit 四元数+速度+角速度);子 body 只发相对父体位置+旋转(不发速度,靠接收端局部 Lerp) |
| 3 | **接收:1 阶外推 + 钳制 + 写回刚体** | `CraftStateSerializer.cs:44-146` | 旧包拒绝(`_lastPhysicsTime<=num`);`num2=Clamp(now−pktTime,0,0.25f)`;**位置外推 `pos+v·num2`**;**旋转外推 `q·AngleAxis(ω·num2)`**;误差²>10000(>100m)瞬移,否则位置 Lerp `Lerp(0.1,1,|v|·0.02)`、旋转 Slerp `2.5·dt`;随后**把 `velocity/angularVelocity` 写回刚体**(`:100-103`) |
| 4 | **每 FixedUpdate 重新施加速度** | `NetworkAircraftScript.cs:408-421` | 非 owner 每物理帧给根 body 刚体重新赋 `velocity/angularVelocity=网络值` → 两包之间远程船不停不抖,刚体带最后速度继续滑 |
| 5 | **游戏侧配合:远程船不吃游戏力** | `AircraftScript.cs:535-543`(`RigidBodyRemote`);`RigidBodyRemote.cs:321-348`;`CraftUpdateScript.cs:64,134,212,251`;`AircraftScript.cs:388-393` | 远程船刚体**非 kinematic**,但 `AddForce/AddTorque/SetInertiaTensor` 抛 `NotSupportedException`;`CraftUpdate` 对 `IsRemoteCraft` 跳过所有施力/部件物理步骤(保留 `OnUpdate` 做视觉平滑);`AircraftScript.AngularVelocity` 对远程船直接返回网络 SyncData |

旁路同构:脱离母船的 body(`NetworkBodyScript.cs:152-171`)、投掷物(`NetworkFlightObjectRigidBodyScript.cs:63-97`)、EVA 角色(`NetworkCharacterScript.cs:467-527`)全是"时间戳 + 1 阶外推 + 写回刚体"同一套路。

---

## 二、JNO 现状与差距

### 2.1 已对齐(无需再做)

| 维度 | 现状 | 出处 |
|---|---|---|
| 平移 1 阶外推 | `ext = (单向延迟+包龄)×mRate`,封顶 1s,每帧 `pos+v·ext` | `MpNetworkManager.cs:1995-2012` |
| 速度自适应指数平滑 + 近距快照 + >100m 瞬移 | `k=Lerp(0.1,1,|v|·0.02)`,`alpha=1−(1−k)^(dt·50)` | `MpNetworkManager.cs:2308-2341` |
| 旋转 Slerp `2.5·dt`、每 body `10·dt` 平滑 | 对齐 SP2 | `MpNetworkManager.cs:2343-2384` |
| 旧包/暂停/慢放处理 | `Paused` 标记 + 冻结外推 + mRate | 协议 `RemoteDataPack.Paused` |
| 时钟估计 | `RTT/2 + 包龄`(无全局时钟,但有近似) | — |

### 2.2 差距(SP2 有、JNO 没有)

| # | 差距 | SP2 依据 | JNO 现状 | 影响 |
|---|---|---|---|---|
| G1 | **协议无每 body 速度/角速度** | 根 body 发 `velocity+angularVelocity` | `RemoteDataPack` 只有 `BodyPositions/BodyRotations`(`Mod.cs:234-242`;`MpMessage.cs:497-513`) | 子 body 无法速度外推;游戏侧读不到每 body 速度 |
| G2 | **游戏侧速度字段没写对(自转项)** | 刚体被喂正确速度 → 游戏读刚体即对 | `ApplyRemoteGroundedSurface` 写 `GroundedSurfaceVelocity=data.Velocity`(漏自转项,`MpNetworkManager.cs:2606`);`FlightData` 刷新不覆盖速度字段 | **已知问题 #10**:静止远程船游戏侧读 ≈0,相对速度/导航/HUD/Vizzy 全错 |
| G3 | **旋转无外推(0 阶)** | `q·AngleAxis(ω·num2)` | 只有 `Slerp 2.5·dt` 追最新朝向 | 转弯船恒定滞后 ≈ 0.4s + latency |
| G4 | 平移无 2 阶(加速度) | —(SP2 也只做 1 阶) | 同 SP2 | 恒加速段每包纠偏(见 acceleration-smoothing) |

### 2.3 架构差异(SP2 真实刚体 vs JNO kinematic 幽灵)——**不建议照搬**

JNO 的 `SetPhysicsEnabled(false, Warp)` + 全 kinematic + collider 关闭 + `InContactWithPlanet=true` + `GroundedSurface*` 反射写,是经过 latency-smoothing §9.7~§9.16 多轮修出来的稳定形态(抽搐/暂停/慢放全收工)。SP2 的真实刚体依赖 `RigidBodyRemote` 贯穿整个游戏代码(`RemoteAircraft` 标志遍布 ~50 处)——JNO 是 Harmony 补丁改出来的 mod,没有这个贯穿渠道,强行改真实刚体会重开全部已修问题。

**结论:取其收益(速度正确),不取其架构(真实刚体)。**

---

## 三、协议与带宽测算(开销 = 带宽 + CPU)

### 3.1 当前状态包体积(`MpMessage.cs:477-528`)

| 段 | 大小 |
|---|---|
| Position/Velocity(各 3×double) | 48B |
| Heading/SrfRel(各 4×double) | 64B |
| 控制(12 float)+ Stage(int) | 52B |
| ActivationGroupStates(1 int + 10 bool) | 14B |
| `Paused` | 1B |
| **基础包小计** | **≈179B** |
| BodyRotations `4+12n` | n=20 → 244B |
| BodyPositions `4+12n` | n=20 → 244B |
| EngineThrottles / PartActivated | 4+4e / 4+p |

当前典型(20 body,默认 20Hz):`20×(179+488) ≈ 13.3 KB/s`。

### 3.2 加"每 body 速度/角速度"后的增量

| 新增 | 每 body | n=20 | 20Hz 增量 | 占当前比例 |
|---|---|---|---|---|
| BodyVelocities(3 float) | +12B | +240B/包 | +4.8 KB/s | +36% |
| BodyAngularVelocities(3 float) | +12B | +240B/包 | +4.8 KB/s | +36% |
| **合计** | **+24B/body** | **+480B/包** | **+9.6 KB/s** | **+72%** |

- **绝对值极小**:9.6 KB/s 对 Steam P2P(实际可用 1~10 MB/s 量级)和局域网 TCP 都是可忽略的;即便 50 body 也只有 +24 KB/s。
- **对比**:SP2 每包限 5 根+5 子 body,若 JNO 也做带宽预算(§六 P2 可选),总包体反而**下降**。
- **可选压缩**:速度/角速度可量化(`Vector3Short(2)` 同 `NetworkFlightObjectRigidBodyScript.cs:77-78`,精度 ~2⁻²),体积再减半 → P2。

### 3.3 CPU 开销

- 发送端采样:每 body 读 `RigidBody.velocity/angularVelocity`(或 `BodyScript.RigidBody` 已有引用)——**已在遍历 body 采样 `BodyPositions`,零额外循环成本**,每次几十 ns。
- 接收端写回:每 body 两次赋值 + `SetStateVectors` 复用——同样可忽略。
- **唯一可能的 CPU 大头是"真实刚体模拟"**:SP2 让远程刚体非 kinematic,PhysX 每步真积分(碰撞检测、重力)。JNO **保持 kinematic + collider 关闭 → 零额外 PhysX 成本**。
- 结论:**加每 body 速度的 CPU 开销 ≈ 0**;只有选择 §六-P3 的真实刚体路径才会有可感知的 PhysX 成本(仍远低于本机船,因只 1~4 艘远程船)。

---

## 四、问题 1 回答:加入物理同步开销有多大?

| 开销维度 | 量级 | 结论 |
|---|---|---|
| 带宽(每 body 速度+角速度) | +24B/body/包;20Hz×20 body ≈ +9.6 KB/s(+72%) | **可忽略**(Steam P2P/TCP 均无压力);P2 可压缩减半 |
| CPU(采样/写回) | O(bodies),每 body 几十 ns | **≈0** |
| CPU(真实刚体 PhysX)——仅当照搬 SP2 架构 | 每远程船每物理步积分 + 碰撞检测 | **建议不做**;kinematic 路径零成本 |
| 协议/兼容 | 尾部追加(同 `Paused` 的 EOF 容错模式) | 单端升级安全(旧包读不到 → 零值) |

**一句话:开销是"尾数级"的——每包 +24B/body,CPU ≈ 0;真正的成本不在开销,在实现与回归(见 §五)。**

---

## 五、问题 2 回答:复杂性和工期?

### 5.1 为什么"不复杂"的部分其实已具备

JNO 平滑管线(采样→序列化→外推→平滑→应用)已存在且稳定,加每 body 速度是**在既有 List 平行结构上再加两个平行 List**(`BodyVelocities/BodyAngularVelocities`,与 `BodyPositions` 同索引),完全复用现有遍历/EOF 容错/钳制/诊断模式——无新架构。

### 5.2 复杂性清单与工期(按阶段,单人、熟悉本仓库估算)

| 阶段 | 内容 | 触及文件 | 工期 |
|---|---|---|---|
| **P0** | ① 协议尾部追加 `BodyVelocities/BodyAngularVelocities`(EOF 容错);② 发送端 `TrySampleLocalCraft` 采样每 body 速度;③ 接收端写回 kinematic 刚体 + **游戏侧速度修复**(`GroundedSurfaceVelocity += CalculateSurfaceVelocity` + `FlightData` 速度字段反射刷新 = remote-craft-velocity §五修复) | `Mod.cs`、`MpMessage.cs`、`MpNetworkManager.cs`(采样/ApplyRemoteState/ApplyRemoteGroundedSurface) | **1~2 天** |
| **P1** | 旋转 1 阶外推 `SrfRel · AngleAxis(ω·ext)` + 每 body 速度外推;钳制/防御/诊断(即 acceleration-smoothing 期 1) | `MpNetworkManager.cs`(外推段 1995-2018 + 平滑 2343) | **0.5~1 天** |
| **P2**(可选) | 带宽预算化(SP2 式 5+5 优先级挑选)+ 速度量化压缩 | `MpNetworkManager.cs` + `MpMessage.cs` | **1~2 天** |
| **P3**(不推荐) | 照搬 SP2 真实刚体(`RigidBodyRemote` 等价物) | 全仓库 + Harmony 新 patch | **1~2 周,高回归风险** |

**推荐路径(P0+P1)合计:约 3~5 个工作日,含 NetSim(150ms/30ms)双端实测与回归。**

### 5.3 风险点(诚实版)

1. `FlightData` 速度字段是游戏阶段快照,mod 反射刷新有时序窗口(remote-craft-velocity §三.2)——P0 需在 `ApplyRemoteState` ⑤ 之后立即写,且与游戏 `UpdateCraft` 的覆盖节奏核对;
2. 每 body 速度采样若直接用 `RigidBody.velocity`,含关节/根 body 连带速度,可能带噪——P0 先对静止船验证(速度应≈0),必要时对子 body 用"父体速度 + ω×r 合成"而非原始值;
3. `ω` 的符号/轴序(参考 acceleration-smoothing §五-3)右乘前必须双端实测;
4. kinematic 刚体写 `velocity` 对 PhysX 无推进作用(只被 `InjectGhostMotion`/游戏读),所以**游戏侧速度正确性必须靠 P0 的字段写入**,不能靠"喂刚体"——这是与 SP2 最大的执行差异,别做错方向。

---

## 六、建议实施路径(低风险优先)

> 顺序即依赖:先 P0 把"游戏侧读速度正确"这件事落地(修 #10),再 P1 做旋转外推;P2 视包体压力再定;P3 明确不做(记录在案)。

1. **P0-数据验证**(半天):发送端临时日志打印每 body `RigidBody.velocity/angularVelocity`(量级/静止是否≈0/关节 body 是否带噪),真实飞行 + 转弯段各一次;
2. **P0-协议+采样+写回**(1~1.5 天):`RemoteDataPack` 加两个平行 List → `WriteRecdata/ReadRecdata` 尾部追加(EOF→空)→ 采样 → 接收端写回 kinematic 刚体 + `ApplyRemoteGroundedSurface` 补自转项 + `FlightData` 速度字段反射刷新;
3. **P0-验证**(半天):静止远程船 `craft.Velocity.magnitude ≈ 158.85 m/s`(测试行星)非 0;运动船无帧内振荡;`MP smoothing` 诊断加 `bodyVel=`;NetSim 150ms/30ms 双端;
4. **P1-旋转 1 阶外推**(0.5~1 天):朝向外推 + 每 body 速度外推,钳制/防御/诊断(回填 acceleration-smoothing 期 1 并更新其状态);
5. **收尾**:更新本文件状态 → `plans/README.md` 索引/决策速查 → 与代码同批提交。

---

## 七、回归判据(开工后验收)

- **保持**:§9.17 终态全部指标不回归——`b0dLate=0`、`gapEMA≈50ms`、暂停/慢放/切换速度模式三项(见 latency-smoothing §9.7~§9.16、update-1.4.2 §〇之四);
- **新增**:
  - 静止远程船游戏侧 `craft.Velocity.magnitude ≈ 行星自转线速度`(≠0),`FlightData.VelocityMagnitude` 同理;
  - 运动远程船 `craft.Velocity` 帧内游戏阶段/mod 阶段一致(无振荡);
  - 转弯段对端朝向无恒定滞后(旋转外推生效),`head3s` 与包内 SrfRel 变化率吻合;
  - 相对速度 `local.Velocity − remote.Velocity` 双船同向/反向/静止场景与地表相对速度差一致;
  - 包体增大后 `gapEMA/jitterEMA` 不劣化;40+ body 大船不卡(带宽预算 P2 若做,包体反降)。

---

## 八、关联文档与决策记录

- 决策建议(待用户拍板):**【建议:2026-09-14】先做 P0+P1(3~5 天),不做 P3 真实刚体**;拍板后回填本文件状态并同步 `plans/README.md`。
- [`remote-craft-velocity-2026-09-13.md`](remote-craft-velocity-2026-09-13.md):P0 直接执行其 §五修复方案(字段补自转项 + FlightData 速度刷新);
- [`acceleration-smoothing-2026-09-14.md`](../acceleration-smoothing-2026-09-14.md):P1 执行其期 1(旋转 `ω·ext` + 平移 2 阶可选);其 §二 已核实 `FlightData.Acceleration/AngularVelocity` 可直接采样;
- [`archive/latency-smoothing-2026-08-22.md`](../archive/latency-smoothing-2026-08-22.md) §9:现行接收端管线,本方案全部改动挂在其上;
- SP2 参考(只读):`<SP2_MP>\Multiplayer\CraftStateSerializer.cs` / `NetworkBodyScript.cs` / `RigidBodyRemote.cs`。
