# 观察者侧节拍量化 + 幽灵位置积分器(observer-tick-quantization)—— 并排飞行"前后抖动"真根因与修复

> 状态:🔧 **活跃 · 阶段性收尾(2026-09-24)** —— P0 / P2 / P2b 已实现并通过**双机 A/B 客观验证**(构建 0/0);**待 Steam 真实环境实测确认观感**(用户下一步执行)
> 日期:2026-09-23(创建);2026-09-24(阶段性收尾)
> 关联:[`archive/smoothing-reset-2026-09-23.md`](archive/smoothing-reset-2026-09-23.md)(⛔ 前一轮"归零重建 / SP2 全量对齐",其结论「台阶由游戏自身渲染管线固定节拍驱动、mod 侧已达极限」**方向对了一半、归因错了一层**,见 §3.4);[`archive/acceleration-smoothing-2026-09-14.md`](archive/acceleration-smoothing-2026-09-14.md)(前三轮平滑调整史);[`archive/latency-smoothing-2026-08-22.md`](archive/latency-smoothing-2026-08-22.md) §9(接收端现行实现事实);[`proposals/remote-craft-velocity-2026-09-13.md`](proposals/remote-craft-velocity-2026-09-13.md)(游戏侧速度缺自转项,与本主题 §3.2 同族但不同因)
> 主题:两架 craft 并排飞行时"对方船位置前后抖动"的**真根因(两层)与修复**:①观察参照系——本机船/相机只在物理固定步推进(`CraftBuilder.cs:223` 刚体插值 `None`),而幽灵每渲染帧写入;②mod 自身位置构造——"锚点外推 + 指数平滑 + maxStep"产生 0.5×~2.6× 的逐帧速度调制

---

## 〇、当前状态速览(先读这节)

### 0.1 一句话结论

抖动是**两层叠加**,且都在 mod 与"观察者侧",不在网络节拍:

| 层 | 根因 | 修复 | 状态 |
|---|---|---|---|
| **观察者侧** | JNO 所有 craft body 默认 `RigidbodyInterpolation.None`(`CraftBuilder.cs:223`)→ **本机船/相机位置只在物理固定步更新**(实测 `fixedDt=10ms`),而幽灵由 mod 每渲染帧写入连续位置 ⇒ 并排飞行时以自己为参照看对方,看到对方按 `v×fixedDt` 台阶跳(v=94m/s → ±94cm) | **P0**:本机船全 body 强制 `Interpolate`(游戏自带调试键 `Ctrl+Shift+I` 同效,`FlightSceneScript.cs:1462-1473`) | ✅ 已落地 + A/B 证实 |
| **幽灵侧** | 旧位置管线 `锚点外推 + 指数平滑(dt×50) + maxStep(1.5·v·dt)` 的组合使**渲染位移自身**在 0.5×~2.6× 间摆(与发包率 20~120Hz 无关、与帧时长无关) | **P2/P2b**:自由运行速度积分(Δpos ≡ V×dt)+ 有界误差回收 + 速度用**位置流长基线**推导并与锚点同源 | ✅ 已落地 + A/B 证实 |

### 0.2 已落地清单

| # | 内容 | 文件 |
|---|---|---|
| P0 | 本机(观察者)船全 body → `RigidbodyInterpolation.Interpolate`;**OFF 时回滚为 `None`**(回滚缺失曾是 A/B 失效的原因) | `Net/Sync/LocalCraftInterpolation.cs`(新)+ `NetworkManager.Update` 接线 |
| P2 | 幽灵位置改为"自由运行积分 + 有界误差回收";`IntegPos/IntegVel/IntegAnchorPos` 等状态 | `Net/Sync/RemoteCraftSmoothing.IntegrateGhostPosition` + `RemoteCraft.cs` 字段 + `RemoteCraftDriver` 锚点接线 |
| P2b | 积分速度改用**位置流推导(≥80ms 多包基线)**,锚点前导项与积分器**同源** | `RemoteCraft.PushSample` + `RemoteCraftDriver` 锚点 |
| P1 | 暂停标志去抖(发送端连续 2 次采样、接收端连续 2 包) | `LocalCraftSender` / `RemoteCraft` / `RemoteCraftDriver` / `MultiPlayerSyncUtil` |
| 诊断 | 渲染前探针(可见位姿/观察者/相对量/积分器)+ 诊断唯一出口与开关 | `Net/Sync/RemoteCraftPoseProbe.cs`、`Net/Sync/MultiPlayerDiag.cs`(新) |
| 操作面 | **全部调试开关进 UI**(检查器面板 Debug / 同步修复开关两组);**DevConsole 注册已全部删除** | `MultiPlayerUI.cs`、`Mod.cs`、`EN-US.xml`/`ZH-CN.xml` |

### 0.3 验收指标与当前实测(双机 A/B,2026-09-24)

> 关键:`bSpdAbs` = **可见几何(body)** 的逐帧速度 max/min;**`lSpeed`** = 本机船同理。`root transform` **不是**可见几何(幽灵 `isPhysics=1`,root 由游戏 `RecenterTransformOnCoM` 从物理 CoM 摆出,发生在 mod 写回之前)。

| 状态 | 幽灵可见 `bSpdAbs` max/min | 本机船 `lSpeed` max/avg | `lQuant`/`camQuant` |
|---|---|---|---|
| **两开关 ON(当前默认)** | **1.22×**(目标 ≤1.2×) | **~1.00×** | 0 / 0.13 |
| 位置积分器 OFF(旧路径) | **23.78×**(本轮)/ 1.94~3.21×(上一轮) | 1.006× | 0.25 / 0.23 |
| 本机船插值 OFF | 1.34× | **1.259×** | **0.43 / 0.43** |

### 0.4 下一步:Steam 实测协议

1. 双端部署本构建;
2. **默认(两开关 ON)先做一轮纯观感**:并排高速飞行 + 一次暂停/恢复 —— 这是最终判据;
3. 需要对照就用面板二选一各自飞 30~40 秒(切开关会写日志行 `MpPosIntegrator (UI) -> ON/OFF`,可直接按时间对齐 A/B);
4. 需要数据:保持"诊断日志 + 渲染前探针"ON,取 `MultiPlayer rprobeObs/ rprobe / smoothing / sendDiag`;追求性能则把两者关掉(零诊断开销)。

**若仍有可见残差**,按下表收最后 ~20%(三项都很小,已定位):

| 旋钮 | 现值 → 建议 | 作用 / 代价 |
|---|---|---|
| `IntegMaxCorrFrac` | 0.25 → **0.15** | 单帧位移收窄到 ≤1.15×V×dt(直接压残差);瞬移/丢包后收敛略慢 |
| 暂停窗口不累积锚点误差 | 加守卫 | 消除 `hard` 偶发重同步(冻结期积分器保持而锚点前进 → 残差累积到硬账上限) |
| `IntegVelTauSec` | 0.15 → **0.22s** | 速度更平(高帧率机更明显);加减速跟随略钝 |

---

## 一、症状与四轮失败史

**症状**:两架 craft 并排飞行时对方(幽灵)船位置沿运动方向"前后抖动";速度越高越明显;**与发包率(实测 17~120Hz)无关、与网络延迟无关(TCP 直连 RTT 11~20ms 仍复现、frp 高延迟同样复现)、双端锁 30fps 仍复现、mod 侧四轮改动全部无效**;且"部署包 DLL 与当前源码症状相同"。

| # | 尝试 | 结果 |
|---|---|---|
| 1 | 2 阶外推 + R1~R8(acceleration-smoothing) | ⛔ 无效 |
| 2 | VA 时钟 → realAge 重置 | ⛔ 部分改善未治愈 |
| 3 | 归零重建(直写基线)→ SP2 全量对齐(阶段 A 物理时钟 + 阶段 B PhysX 接管) | ⛔ 无效,代码回滚 `d09be5c` |
| 4 | 最终配置(全 kinematic + `GhostCraftNodeUpdatePatch` + LateUpdate 逐帧写) | ⛔ 无效,任务终止 |

**共同点**:四轮都在"mod 写出的幽灵位姿"这一层做文章,**从没有任何诊断采样过"玩家真正看到的那一层"**(见 §二)。

---

## 二、定位方法论(为什么必须换采样点)

### 2.1 三处探测盲区

| 既有诊断 | 采样点 | 覆盖不到 |
|---|---|---|
| `moveMax` / `LastRenderedPos` / `chain` / `frame` | **包推导出的平滑目标**(数据流) | 写入后的 Transform、可见 body、屏幕相对量 |
| `b0d` / `comLink` / `comCross`(`GhostPoseWriter.cs:245-273`) | **mod 自己的写回路径内部** | 写完之后的任何写者 |
| `tfDrift`(`RemoteCraftDriver.cs:51`) | **下一帧 Update**,且只读 **root transform** | 帧内晚段写者;可见几何在 **body** 上(1.4.2 起 body 已脱离 craft 层级) |

### 2.2 渲染前探针(`RemoteCraftPoseProbe`,`[DefaultExecutionOrder(30000)]` 的 LateUpdate)

采样顺序:游戏 Update/LateUpdate(默认 0)→ mod `NetworkManager.Update/LateUpdate`(**1000**)→ **探针(30000)→ 渲染** = "所有写者跑完之后、渲染之前的最终可见位姿"。纯只读,零行为改动。

输出:`rprobeEnv`(一次:fixedDt/maxDt/vSync/插值·kinematic/是否 surface-lock)、`rprobe P#`(可见层 sequence + drift + gameDelta + pktPaused)、`rprobeObs P#`(观察者层 dtSeq/stepsSeq/lCoMSeq/camMoveSeq + 量化占比 + 相对量 + `bSpdAbs`/`iSpd`)、`rprobeInterp`(插值状态变更事件)。

### 2.3 三个"证伪"结果(110/110 窗口,双端一致)

| 指标 | 实测 | 判决 |
|---|---|---|
| `driftRoot/driftB0/driftCom`(mod 写完 → 渲染前漂移) | **全 0.000m** | "游戏在 mod 写完后还动幽灵"→ **证伪** |
| `gameDelta`(`|帧空间节点位置 − CenterOfMass.position|`,游戏交给 `RecalculateFrameState` 的增量) | **全 0.000m** | game↔mod 对抗循环 → **证伪** |
| `rootBack` | 全 0 | 幽灵从不后退(是"快慢交替"而非倒退) |

### 2.4 量化证据(观察者侧,决定性)

VM 端(v≈94m/s,渲染 137fps,`fixedDt=10ms` → 每物理步位移 **94cm**):

- `camMoveSeq` = `[86.4, 86.4, 172.7, 87.0, 86.5, 172.8, …]` → **6 个窗口 40/40 帧全部是 94cm 的整数倍** ⇒ 相机位置只在物理步更新;
- 同帧幽灵 `rootSeq` = `[5, 234, 331, 403, 174, 0.4, …]` → 连续小数值 ⇒ 幽灵每渲染帧平滑写入;
- 发送端 `pkΔ` = v×10ms(整物理步)⇒ 位置流本身也是台阶式的,接收端把它插值成连续。

### 2.5 两个探针自身的坑(务必记住,否则会误判)

1. **环形缓冲重复推进**:`Push(seq, ref head, ref count, v)` 每调一次 `head++`,12 个数组各调一次 ⇒ `head` 每帧前进 12,`SeqLen=40` 时每个数组只写 10 槽、其余恒 0.0 ⇒ 打印出"3 个 0 夹 1 个值"的**假 4:1 停顿图案**(曾据此误判硬停顿)。
   修法:一帧只推进一次 head,所有数组写同一索引。**单值仍真实**,故"逐值判定"的量化结论不受影响;窗口计数(代码内累加)也不受影响。
2. **root ≠ 可见几何**:幽灵 `isPhysics=1` ⇒ root transform 由游戏从物理 CoM 摆出,**可见几何在 body 上**(由 mod 每帧写)。实测同一窗口 `iSpd`(积分器输出=写进 body 的值)1.45× vs `gSpdAbs`(root)4.84× ⇒ **历史上一大批基于 root 的指标(`rootSeq`/`tfDrift` 的"判决性"结论)测的都是不对的对象**。真指标是 **`bSpdAbs`**(body[0] 模长速度)。

---

## 三、根因

### 3.1 观察者侧:物理节拍量化

`CraftBuilder.CreateBodyScript` 给每个 craft body 设 `rigidbody.interpolation = RigidbodyInterpolation.None`(`CraftBuilder.cs:223`);本机船物理在固定步推进(`Time.fixedDeltaTime = 10ms`,`TimeManager.cs:290`),刚体 Transform 只在固定步刷新;相机/`CameraTarget`/`CenterOfMass` 跟随该位姿 ⇒ **相机被量化到物理步**。幽灵则每渲染帧写 Transform ⇒ 相对参照系里幽灵按 `v×fixedDt` 台阶来回。幅度 = `v×fixedDt`(v=20m/s→20cm;94m/s→94cm;125m/s→1.25m);频率 = 物理频率与渲染帧率的拍频(渲染≫物理时台阶全暴露;渲染<物理时表现为速度 ±14% 摆动 + 长帧 2~3× 跳变)。

### 3.2 幽灵侧:位置构造不平滑

旧管线:锚点(包位置 + 速度×ext)+ 指数平滑(k≤0.6、`alpha=1−(1−k)^(dt×50)`)+ `maxStep=1.5·v·dt`。实测其**渲染位移**在 0.5×~2.6× 间摆,而同帧时长下本机船 1.00× ⇒ 抖动源就是该构造本身。

同一轮还查出两条相关事实(均已修/已量化):

- **包内 `Velocity` 与位置推进不自洽**:单包差分推导速度与上报速度之差中位 8.5m/s、最大 44.9m/s —— 但这不是行星自转量级(158.85m/s),而是**发送端物理步量化污染单包差分**(位置每 10ms 整步跳,单包间隔 ~13ms 时差值落 1~2 个量化步 ⇒ 0.77~1.54×v)。改用 **≥80ms 多包基线**后降到 **0.6/1.4 m/s**。
- **锚点与积分器速度源不同源**:锚点前导项用"上报速度"、积分器用"位置流速度"⇒ 每次对拉 0.4~2.2m,残差常驻、误差回收每帧饱和(`err` 中位 2.0m、`clamp`/`hard` 打满)⇒ 渲染位移又被加回 ±25% 调制。改为同源后 `err` 降到 0.76~1.06m。

### 3.3 为什么四轮修复必然无效(反证)

| 四轮动作 | 为什么碰不到根因 |
|---|---|
| 改外推时钟(VA↔realAge) | 改的是**目标**;幽灵写入本来平滑(`drift=0` 证明写入即所见) |
| 改平滑参数(k/alpha/maxStep) | 同上;且扰动周期≈发包间隔时指数平滑本就无效 |
| 归零直写 / SP2 PhysX 接管 / 全 kinematic | 改的是**幽灵刚体模式**,而另一层量化发生在**观察者的船与相机**上 |
| 把目标层指标调到"看起来正常" | 采样点覆盖不到"渲染前可见位姿"与"相机相对量",且 root 不是可见几何 |

### 3.4 对上一轮结论(smoothing-reset)的修正

「台阶由**游戏自身渲染管线**固定节拍驱动、mod 侧已达极限」:**方向正确**(节拍来自游戏侧),**归因错层**(节拍不在幽灵的渲染路径上——`drift=0` 证明无人覆盖 mod 写入,而在**观察者侧**),故"mod 侧已达极限"应更正为"**该修复路线本来就无法触及该层**"。

---

## 四、修复与实现

### P0 · 本机(观察者)船渲染插值

- 实现:`LocalCraftInterpolation`(1s 周期复查,兼容 revert/重发射/对接后重建),把本机船全 body 设为 `Interpolate`;**总开关 `Enabled`;关闭时立即回滚为游戏默认 `None` 并写日志**(回滚缺失曾使 A/B 失效 —— 实测 ON 1.001× vs OFF 1.002× 毫无差别,即此 bug)。
- 红线:**绝不给幽灵开插值**(幽灵是 mod 每帧写 Transform 的刚体,开插值会把它也变成物理步量化)。
- 副作用边界:Unity 刚体插值只影响**渲染位姿**,不影响物理与碰撞;相机/地图/Vizzy 读到的 Transform 会被平滑(正是目的)。

### P2 · 位置积分器(自由运行 + 有界误差回收)

每帧三步(与包到达无关):

1. 速度向"位置流推导速度"做 EMA(时间常数 `IntegVelTauSec=0.15s`);
2. **位置严格按速度积分**:`Pos += Vel×dt×mRate` ⇒ **Δpos ≡ V×dt**;
3. 用锚点做**有界**误差回收:残差 > `V×IntegMaxErrSec(0.08s)` 的部分**直接吞掉**(瞬移/丢包积欠),其余按 `1−exp(−dt/IntegErrTauSec(0.3s))` 回收,**单帧上限 `IntegMaxCorrFrac(0.25)×V×dt`**。
   锚点 = `包位置 + 位置流速度×(单向延迟 + EMA 平滑包龄)`(`IntegAgeEmaTauSec=0.5s`);**必须用 EMA 包龄**(逐包重置的瞬时 age 会把锯齿经误差回收注回位置)。

**结构不变量**:单帧位移 ∈ `[0.75,1.25]×V×dt` ⇒ 不可能停顿、不可能后退、不可能出现 2~3× 尖峰。
发送端暂停(`RemotePausedRamp>0`)时保持位置、速度衰减 → 沿用"暂停即冻结"语义。

### P2b · 位置流速度 + 锚点同源

`RemoteCraft.PushSample` 用 `(最新包位置 − ≥80ms 前的包位置)/内容间隔` 推导速度(基线越长量化噪声越低);积分器与锚点**同用该速度源**。

### P1 · 暂停标志去抖(附带修)

发送端 `TimeManager.Paused` 连续 ≥2 次采样为真才上报 `data.Paused`;接收端需 `Paused=1` **连续 ≥2 包**才进冻结。背景:实测 HOST 40/198 条 `sendDiag` 报 `paused=1`(含 10.5~16m/s 飞行中,且连续 8 条同速度 = 确被冻结)、VM 9/111 含 `vel=94.4 paused=1`;每次 pause↔unpause 都让幽灵按 `V×VA` 后退/前冲(高速时数米,曾见 24.9m/173m 级跳变)。

### 未做 / 备选

- **不做**:把幽灵也量化到观察者同一物理节拍(牺牲已做好的连续外推,且在双端物理频率不同设置下失效)。
- **备选**:见 §0.4 三个旋钮(等 Steam 实测结论后再决定动哪个)。

---

## 五、诊断与操作设施

### 5.1 诊断唯一出口与开关:`MultiPlayerDiag`

- `MultiPlayerDiag.Log(line)`:全部同步诊断行的唯一出口(调用点已从 `Mod.LogLobby` 全部改道);
- `MultiPlayerDiag.Enabled`(总开关,含探针)、`ProbeEnabled`(探针单独开关);
- `RemoteCraft.ExtraDiagEnabled` 由 `const bool` 改为**转发属性**(const 会被常量折叠 ⇒ 运行时关不掉 + CS0162);
- 探针门控 = `Enabled && ProbeEnabled`。

### 5.2 操作面:全部开关进 UI(命令已删)

**2026-09-24:DevConsole 里 `MultiPlayer` 的 28 条命令全部移除**,理由:"带参数命令裸敲只查询、易被当成已生效"(实测踩过两次,浪费一轮 A/B)。现全部在检查器面板(需 `DebugMode`):

| 分组 | 控件 |
|---|---|
| 主面板 | Host / Join / Disconnect / 踢人 / 玩家列表 / 房间列表(刷新·**跨区刷新**·创建·加入·邀请·离开) |
| 房主设置 | TickRate 滑条(1~120Hz,房主广播) |
| **Debug** | TCP 开房/加入、NetSim 状态·统计·总开关·延迟·抖动·丢包·**重复包**·**复位**、**Steam 身份自检** |
| **同步修复开关** | **位置积分器(P2)** / **本机船渲染插值(P0)** / **诊断日志** / **渲染前探针** |

三个修复开关均为**本机侧局部开关(不随网络同步)**;切换即写日志行(`MpPosIntegrator (UI) -> ON/OFF` 等)⇒ A/B 相位在日志里自证。

### 5.3 判读表

| 日志签名 | 判决 |
|---|---|
| `bSpdAbs max/min` ≤1.2、`integ=(err=…cm, clamp=0, hard=0)` | 幽灵侧达标 |
| `lSpeed max/avg` ≈1.00、`lQuant/camQuant`≈0 | 观察者侧达标 |
| `drift*=0`、`gameDelta=0` | mod 写入无人覆盖(持续成立) |
| `integ=(hard=…)` 持续增长 | 锚点/速度估计发散(查 `velSrcDiff`、包龄) |
| `pktPaused>0` 且伴随 `freeze ENTER/EXIT` | 命中 §四 P1 场景 |

---

## 六、实测数据(A/B 证据)

### 6.1 观察者侧(P0)

| 相位 | `lSpeed` max/avg | `lQuant`/`camQuant` |
|---|---|---|
| 本机船插值 **ON** | **1.006×** | 0 / 0.23 |
| 本机船插值 **OFF**(已确认真回滚) | **1.259×** | **0.43 / 0.43** |

⇒ 用户"开 `MpLocalInterpOn` 有明显改善"的观感与量化一致。

### 6.2 幽灵侧(P2/P2b)

| 相位 | 幽灵可见 `bSpdAbs` max/min |
|---|---|
| 积分器 **ON** | **1.22×**(167 窗)/ 1.25~1.39×(前几轮) |
| 积分器 **OFF**(旧路径) | **23.78×**(本轮 6 窗)/ 1.94~3.21×(上一轮 29/12 窗) |

### 6.3 副产物

- 发送端位置流是**整物理步**跳的(`pkΔ = v×10ms`),接收端必须以长基线取速度(否则 `velSrcDiff` 8.5~44.9m/s 的假象);
- root transform 在本例中**不是可见几何**(§2.5.2),历史结论需按此重新阅读;
- 暂停标志会瞬时抖动(§四 P1)。

---

## 七、未决问题与风险

1. **Steam 实测观感**(本轮唯一未验项):frp 高延迟下已验证;Steam P2P/relay 下待测。
2. **开插值对本机船视觉的副作用面**:近/远相机、地图视图、Vizzy、着陆、对接需各跑一遍(Unity 插值只影响渲染位姿,风险低但需实测)。
3. **幽灵 `isPhysics=1` 的架构问题**:root 由游戏从物理 CoM 摆出、可见几何在 body,二者由不同代码路径驱动;当前靠 mod 每帧写 body 保证观感,长期应评估"幽灵是否应保持物理禁用 + kinematic"(与 `archive/smoothing-reset-2026-09-23.md` 阶段 B 的结论有关)。
4. **`mRate` 乘进积分的系统性偏置**:`Vel×dt×mRate`(mRate 常 1.0~1.17)会让积分器略快于锚点,由有界回收吸收;若残差偏大,先查此项。
5. **幽灵未被量化到与观察者同节拍**:若 Steam 实测仍有"同节拍拍频"观感,备选方案见 §四"未做"。

---

## 八、方法论教训(可复用)

1. **测量点必须在玩家看到的那一层**:四轮的指标全停在"数据流 / 自己的写回路径 / 下一帧 root",没有一条落在**渲染前最终可见位姿**,也没有一条是**相对观察者**的。
2. **"与 mod 侧改动无关"是强信号**:症状对 mod 所有写入方式都不敏感时,优先怀疑**共有的、mod 之外的层**(引擎/游戏/观察者)。
3. **要先确认"测的是不是可见几何"**:本例 root transform 与可见 body 分离,直接导致多轮结论错向。
4. **量化台阶要用"整数倍检验"**:`camMoveSeq` 一眼定案,是因为它只取 `v×fixedDt` 的整数倍;均值/最大值类统计看不出来。
5. **不要用 `const` 做开关**:常量折叠 ⇒ 运行时关不掉 + CS0162(本项目已两次踩到:诊断开关、位置积分器开关)。
6. **A/B 的"关"必须真的回滚**:只停止强制设置而残留旧值 ⇒ 开关看起来无效(P0 曾如此,浪费一轮)。
7. **参数化控制台命令在游戏内 DevConsole 上不安全**(裸敲=只查询)⇒ 一律无参数 + On/Off 后缀,或直接进 UI。

---

## 九、代码与文档清单

| 文件 | 内容 |
|---|---|
| `Net/Sync/LocalCraftInterpolation.cs` | **P0**:本机船插值强制 + OFF 回滚 |
| `Net/Sync/RemoteCraftSmoothing.cs` | **P2/P2b**:`IntegrateGhostPosition`(积分 + 有界回收;速度源优先级) |
| `Net/Sync/RemoteCraftDriver.cs` | 锚点与包龄 EMA 接线;`integ=(...)` 诊断;`Paused` 去抖判据 |
| `Net/Sync/RemoteCraft.cs` | 积分器状态字段、暂停标志连续计数、位置流速度推导、诊断基准字段 |
| `Net/Sync/MultiPlayerSyncUtil.cs` | P2/P2b 常量与 `EnablePositionIntegrator`;`PausedFlagConfirmPackets` |
| `Net/Sync/MultiPlayerDiag.cs` | **诊断唯一出口 + 唯一开关** |
| `Net/Sync/RemoteCraftPoseProbe.cs` | 渲染前探针(可见位姿/观察者/相对量/积分器诊断) |
| `Net/Session/NetworkManager.cs` | 挂载探针、调用 P0 保障 |
| `MultiPlayerUI.cs` + `Mod.cs` + `Content/Languages/{EN-US,ZH-CN}.xml` | UI 开关与文案;DevConsole 注册清理 |
| 本文档 | 主题 plan(活跃) |
