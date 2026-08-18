# 同步"起落架开关等"部件展开/开关状态 — 可行性分析

> 项目:JNOmultiplayerTest(aMptest)
> 反编译参考:`C:/renko/shitProgram/jnoCode`
> 定位:[`multi-craft-sync.md`](multi-craft-sync.md) 的补充分析——回答"幽灵船的起落架收放/货舱门/太阳能板/灯等**开关与展开状态**能否同步、怎么同步、代价多大"
> 状态:**方案研究(已确认方案 B,待实施)**——2026-08-18 已拍板:① **方案 B**(per-part `Activated` 位);② **分离器/级间、整流罩、对接等涉及 body 改动的部件只记录、不处理**(归后续 body 同步);③ **降落伞等特殊部件先反编译定原理**(见 §9),走专用视觉驱动。正文「【决策:…】」为最终结论,实施待排期。
> 结论先行:**同步"开关状态"可行且成本近零**——机制是同步每个部件的 `Part.Activated`(开关位),幽灵端**复用游戏自己的 FlightUpdate/动画器做本地仿真**(与 engine-fx 尾焰的 L1"输入/状态同步 + 本地仿真"完全同套路)。不推翻 8.2-5"燃料/资源不同步",只把"部件展开状态"从 8.2-5 的限制里摘出来。

---

## 0. 现状(plan 已认定的限制)

- [`multi-craft-sync.md`](multi-craft-sync.md) 8.2-5 决策:**燃料/资源/部件状态 MVP 不同步**,其中"part 损伤/展开/引擎/Vizzy 状态"都记为已知限制。
- engine-fx 尾焰已用"同步**视觉驱动值**(throttle)"打破了"引擎视觉不同步"的边界(不涉及燃料数值)。**起落架等开关是同一类**:同步"开关状态(Part.Activated)",不同步任何燃料/资源数值。
- 现状代码:recdata **已含** `ActivationGroupStates`(10 bool)+ `Stage`(见 [`Mod.cs`](../Assets/Scripts/Mod.cs:132)),且已序列化传输([`MpMessage.cs`](../Assets/Scripts/Net/MpMessage.cs:488)),但 **接收端从未应用**(`MpNetworkManager.ApplyRemoteState` 只采样不应用)——是现成的半成品通道。
- 同理:`Pitch/Yaw/Roll/Throttle/Brake/Sliders` 也是"只采样、不应用"的死字段(`MpNetworkManager.cs:1951-1962`)。

---

## 1. 起落架真实实现(反编译确认)

- **开关 = `PartData.Activated`**:`LandingGearScript.FlightUpdate` 每帧读 `base.Data.Part.Activated` → `SetExtended(...)`([`LandingGearScript.cs:155`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/LandingGear/LandingGearScript.cs:155))。
- **动画是纯 Transform**:`ConfigurableGearScript.SetExtended` 只是转发给 `_animator`([`ConfigurableGearScript.cs:502`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/LandingGear/ConfigurableGearScript.cs:502));`LandingGearAnimator` 走 Unity 原生 `Update`(不受物理门控),按 `Time.deltaTime` 做 ~4s 收放 + 舱门旋转([`LandingGearAnimator.cs:311/373`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/LandingGear/LandingGearAnimator.cs:311))。
- **真正的轮子物理**在 `ResizableWheelColliderNew`,幽灵上已被 `DisableCraftPhysicCalculation` 关闭(不影响动画)。
- 幽灵 modifier 每帧收 `IFlightUpdate`(engine-fx §3.5 定论:注册只看 MonoBehaviour enabled,`EnablePhysics(false)` 不禁用 MonoBehaviour);动画器走原生 Update 也照跑。

> **推论:接收端把幽灵对应部件的 `Part.Activated` 设成同步值,游戏自己的 FlightUpdate 就驱动收放动画平滑播放——无需逐帧驱动代码。**

## 2. 为什么"现在"不同步(与 engine-fx 的差异)

- engine-fx 的根因是:幽灵物理关后游戏**强制把视觉关掉**(激活门控 + 每帧归零 throttle)。
- 起落架**没有**这种门控——`FlightUpdate` 每帧都读 `Part.Activated` 并驱动动画,**问题只在开关本身没被同步**:幽灵上 `Part.Activated` 停留在加入时 XML 的设计值(StartExtended),发送端之后的一切收放幽灵都不跟随。
- 因此起落架同步 = **只补"开关位传输 + 接收端应用"**,比尾焰还简单(尾焰还要绕激活门控,起落架不需要)。

## 3. 同步方案(两个选项,建议 B)

| | 方案 A:复用现有 ActivationGroupStates | **方案 B:per-part `Activated` 位(推荐)** |
|---|---|---|
| 带宽 | 0(已有字段) | N bit(N=部件数;1000 部件=125B/包,20Hz≈2.5KB/s,可忽略) |
| 覆盖 | 仅"挂激活组"的开关 | 激活组 + **stage 级联** + 飞行检查器直切 + self-governed 部件,全覆盖 |
| 应用 | 幽灵遍历 parts,`ActivationGroup==i` 的按位调 `Activate()/Deactivate()` | 同,按确定顺序(`Data.Assembly.Parts`,与 EngineThrottles 同一顺序契约),**排除 Detacher** |
| 漏网 | 不挂组的起落架/伞/引擎点火/检查器直切 | 无 |

- 应用入口:每包(或**变沿**——变化才调)调用 `PartScript.Activate()/Deactivate()`([`PartScript.cs:521`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/PartScript.cs:521),幂等,内部有 `if(!Activated)` 守卫)。
- **持续应用的自愈好处**:幽灵本地任何偏差(如伞按本地大气密度自切断)下一包(50ms)即被纠正。
- 进阶(可选):起落架额外同步 `ExtensionPercent`(float)用 `SnapToExtensionPercent` 对齐动画相位;货舱 `OpenAmount`、SubPartRotator `CurrentEnabledPercent` 同理。MVP 可省(4s 动画自愈)。

**【决策(2026-08-18):采用方案 B per-part `Activated` 位**,覆盖完整、带宽可忽略。**接收端应用过滤(只记录不处理 / 特殊处理)见 §4/§9**:`Detacher`、`Fairing`、`DockingPort` 位照常传输但**不应用**;`Parachute` 不应用 `Activated`,走专用视觉驱动。**应用位清单(白名单驱动):起落架 / 货舱门 / 着陆腿 / 太阳能 / 灯·信标 / SubPartRotator。** 阶段可用 `PartScript.Data.Activated` 直接驱动(不依赖激活组)。**

## 4. "相似逻辑"部件分级(反编译逐一确认)

| 部件 | 开关来源 | 幽灵行为 | 可行性 |
|---|---|---|---|
| **起落架** LandingGear | `Part.Activated` → 纯动画 | 轮子物理已关,动画照播 | ✅ 干净 |
| **货舱门** CargoBay | `Part.Activated` → `Data.Open`([`CargoBayScript.cs:55`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/CargoBayScript.cs:55)) | 门动画,碰撞体已关 | ✅ 干净 |
| **着陆腿** LandingLeg | `Part.Activated`([`LandingLegCommon.cs:92/105`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/LandingLeg/LandingLegCommon.cs:92)) | 收放视觉 | ✅ 干净 |
| **太阳能板** Solar | `Part.Activated` → Open+展开([`SolarPanelArrayScript.cs:233`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/Solar/SolarPanelArrayScript.cs:233)) | 本地太阳位置自足 | ✅ 干净 |
| **灯/信标** Light/Beacon | `Activated && HasPower`([`LightScript.cs:308`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/Lights/LightScript.cs:308)) | ⚠️ 幽灵电池可能空/陈旧 → 需强制 HasPower 或接受 | ⚠️ 小坑 |
| **SubPartRotator** | `Part.Activated`,自带 `SyncActivationGroup`([`SubPartRotatorScript.cs:84`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/SubPartRotatorScript.cs:84)) | 反向写激活组,方案 A/B 都覆盖 | ⚠️/✅ |
| **轮子转向/刹车** | 控制输入驱动 | 控制输入接收端未应用(死字段);视觉转向会读幽灵本地输入 | ⚠️ 另一条线,视觉小偏差 |
| **分离器/级间** Detacher | `OnActivated → Detach()`([`DetacherScript.cs:150`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/DetacherScript.cs:150)) | **不受物理门控**:销毁关节+施加冲量+相机震动+声音([`DetacherScript.cs:37`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/DetacherScript.cs:37)) → **body 图改动**(body 分离) | 🔒 **只记录,不处理**(归 body 同步) |
| **整流罩** Fairing | `OnActivated → _jettisonNextFrame → InitiateJettison`([`FairingScript.cs:89`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/FairingScript.cs:89)) | `QueuePartGroupForDestruction` + 新建 `FairingDebris` body + 声音([`FairingScript.cs:151`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/FairingScript.cs:151)) → **body 图改动** | 🔒 **只记录,不处理**(归 body 同步) |
| **对接** DockingPort | `Activated` 使能对接 collider([`DockingPortScript.cs:152`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/DockingPortScript.cs:152)) | 对接 = body merge(走 `CraftNodeRemoved` + 重发 dominant XML,见 multi-craft-sync §8.1-4),幽灵不模拟 | 🔒 **只记录,不处理**(归 body 同步) |
| **降落伞** Parachute | `Activated → DeployParachute`([`ParachuteScript.cs:262`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/ParachuteScript.cs:262)) | **激活时新建 collider+rigidbody+SpringJoint** + 密度/高度门控 + 自动切伞(详见 §9) | 🎯 **专用视觉驱动**(不应用 `Activated`,见 §9) |
| **引擎** | `Part.Activated` | OnActivated 被 `IsPhysicsEnabled` 门控 | ✅ 安全(尾焰已由 EngineVisualSync 管) |
| **发生器/陀螺仪/RCS** | `Activated` | 幽灵电池副作用/无物理意义 | ⚠️ 低风险 |

## 5. 风险清单(幽灵特定)

1. **Detacher / Fairing / DockingPort 排除是硬要求**(否则幽灵船 body 分裂/被销毁部件组/相机震动)——应用循环按部件类型白名单过滤(见 §3 决策)。
2. 动画相位:不传相位时 ~4s 内自愈,可接受;
3. 收放完成触发 `InitiateDragRecalculation`(动画器 428 行):幽灵 `IncludeInDrag=false` → 无害,建议回归验证;
4. 幽灵电池副作用(generator/灯):只影响幽灵自身视觉,不影响远程真实船;测试后决定是否屏蔽;
5. 激活时播放本地音效(舱门/收放音),观感更好,无需处理。

## 6. 带宽/性能(含"1000 起落架"问题)

- **同步本身边际成本 ≈ 0**:
  - 带宽:per-part 位 1000 部件 = 125B/包(2.5KB/s@20Hz),远小于同 craft 现有的 EngineThrottles(1000×4B=4KB/包)+ BodyRotations(每 body 3 float);
  - 应用循环:1000 次 bool 比较/包 × 20Hz = 2 万次/秒,可忽略;`Activate()` 守卫使无变化时为空操作;
  - 动画期间:仅收放的 ~4s 窗口内做少量 Transform 写入。
- **真正的开销是游戏自身对 1000 个 gear 组件的模拟**(每 gear 每帧 `FlightUpdate` + 动画器 `Update` + **4 个 AudioSource**),且**双端都存在、与同步无关**——1000 起落架的 craft 单机就卡成个位数 FPS,是病态 craft 本身不可玩,不是同步引入。
- SR2 沙盒**无硬性部件数上限**(仅生涯模式有可配置 `Craft.MaxPartCount`,见 [`CareerValidator.cs:229`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/State/Validation/CareerValidator.cs:229))。
- 极端防护(如需要):**增量/变沿压缩**(只传变化的 (index,value),空闲 0 字节)+ 位打包(8bit/字节)+ 距离 LOD(MC2 慢发对象不发)。采样侧复用 EngineThrottles 的"确定顺序"遍历,一次循环采多类字段。

## 7. 与现有 plan 的关系

- 不依赖 MC1(多 craft 全局身份),单船可先做(同尾焰先例);
- 依赖 MC2"每船状态包"加字段的格式(方案 B 需扩展);
- 与 8.2-5"燃料/资源不同步"**不冲突**(只同步开关状态,不含燃料/资源数值);
- 与 8.1-3 的 Harmony 拦截不冲突(只动部件状态、不换 pod);
- 相邻项:顺带可补 `Stage` 应用(原理同,但同样要避开 Detacher)。

## 8. 结论与建议排期

| 项目 | 可行性 | 成本 | 建议 |
|---|---|---|---|
| 起落架收放(开关同步) | ✅ 可,本地仿真 | 近零 | **做**(P0) |
| 货舱门/着陆腿/太阳能 | ✅ 可,同机制 | 近零 | **做**(随 P0 一起,同一条应用循环) |
| 灯/信标 | ✅ 可 | 低 | 做(需处理 HasPower/电池) |
| SubPartRotator | ✅ 可 | 低 | 做(方案 B 顺带覆盖) |
| 引擎点火 | ✅ 已由尾焰覆盖 | — | 无需额外 |
| 降落伞 | 🎯 专用视觉驱动(见 §9) | 中 | 反编译已定原理,P2 单做 |
| 分离器/级间、整流罩、对接 | 🔒 只记录不处理 | 0 | 归 body 同步(记录位,不应用) |
| 粒子/逐帧展开相位 | ❌ 不值得 | — | 不做(4s 自愈) |

**排期建议**:
1. **P0** 方案 B:recdata 加 per-part `Activated` 位(复用 EngineThrottles 顺序枚举)+ 接收端**白名单应用**(起落架/货舱/腿/太阳能/灯/SubPartRotator;**Detacher/Fairing/DockingPort 只记录不应用**;Parachute 不应用)→ 起落架/货舱/腿/太阳能/灯一次到位;
2. **P1** 起落架 `ExtensionPercent` 相位对齐(可选);
3. **P2** 降落伞专用视觉驱动(按 §9 方案)+ 发生器/灯逐个回归;
4. 回归:双 Steam 账号 或 TCP VM 实测收放动画同步(同 engine-fx §9 套路)。

## 9. 降落伞等特殊部件(反编译定原理,2026-08-18)

### 9.1 降落伞工作原理(`ParachuteScript`)

| 环节 | 行为 | 反编译证据 |
|---|---|---|
| **展开触发** | `FlightUpdate`:若 `Part.Activated && !_deployed` → `DeployParachute()` | [`ParachuteScript.cs:262`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/ParachuteScript.cs:262) |
| **展开门控** | `SurfaceVelocity < MaxDeploymentSpeed` 且 高度/大气密度 满足(`ASLDeployment`/`DeploymentDensity`)才展开 | [`ParachuteScript.cs:110`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/ParachuteScript.cs:110) |
| **DeployParachute** | 置 `_deployed=true`;缩放 base collider;**新建 SphereCollider**(chutePackage 上)+ **新建 Rigidbody `_chuteBody`**(质量/无重力)+ **新建 SpringJoint** 连部件 body Rigidbody;置 chuteBody 位置/速度;激活 chute mesh | [`ParachuteScript.cs:108-150`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/ParachuteScript.cs:108) |
| **充气+阻力** | `FlightFixedUpdate`:充气动画 `_chute.localScale` 按 `_inflateTime` 涨;`_chuteCollider.radius` 同步;**对部件 body 与 chuteBody `AddForce` 施加阻力** | [`ParachuteScript.cs:165-257`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/ParachuteScript.cs:165) |
| **自动切伞** | 本地 `AirDensity < CutDensity`(或高度)时 `Part.Activated=false` | [`ParachuteScript.cs:190`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/ParachuteScript.cs:190) |
| **拉断** | 阻力超阈值时 `Activated=false` + 日志 | [`ParachuteScript.cs:235`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/ParachuteScript.cs:235) |
| **收起** | `FlightUpdate`:若 `!Activated && _deployTime>0` → 销毁 joint + 销毁 chuteBody + 禁用 collider + 隐藏 chute | [`ParachuteScript.cs:267-279`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/ParachuteScript.cs:267) |

### 9.2 为什么不能走通用 `Activated` 应用(幽灵上)

1. **DeployParachute 在激活瞬间新建物理组件**(SphereCollider + 非 kinematic Rigidbody + SpringJoint),幽灵初始化时的 `DisableCraftPhysicCalculation` **不覆盖这些后加组件** → 幽灵上会留一个活的 collider 和一个被弹簧拖拽的非 kinematic chuteBody,不干净、可能乱动;
2. **密度/高度门控是本地量**:幽灵 `FlightData.AltitudeAboveSeaLevel`/`AtmosphereSample.AirDensity` 与发送端不同(且 mod 只刷新 PositionNormalized/CraftForward)→ 可能出现"发送端已展开、幽灵因本地条件不满足而不展开"或"幽灵自动切伞"的错位;
3. 阻力 `AddForce` 在幽灵 kinematic body 上无效、在非 kinematic chuteBody 上会漂。

### 9.3 处理方案:专用"视觉-only"驱动(不入通用应用)

- **发送端采样**:反射取 `_deployed`(权威真实状态,已含全部门控/切伞判定)→ recdata 加 `List<bool> ParachuteDeployed`(或并入 per-part 位 + 单独相位字段);可选 `_inflateTime` 归一化做充气相位;
- **接收端**:**不应用**伞部件的 `Part.Activated`(伞自己的 `FlightUpdate` 见 `Activated=false && _deployTime==0` 时是 **no-op**,`FlightFixedUpdate` 见 `_deployed=false` 也是 no-op → **天然安全,无需 Harmony patch**,与航发加力情况不同);
- **专用驱动**(可挂 `EngineVisualSync` 同层或独立 `PartVisualSync`):按同步值驱动视觉——`_chute.gameObject.SetActive(deployed)` + `_chute.localScale` 充气动画(本地 `_inflateTime` 推进,参数与游戏公式一致)+ 收起时复位 `_chutePackage`;**不创建任何 collider/rigidbody/joint**;
- 不传相位时退化为"展开瞬间直接全尺寸"(可接受,MVP 先这样)。

### 9.4 其余"特殊"部件(同型确认)

- **对接 DockingPort**:body merge(已在 multi-craft-sync §8.1-4),record-only;
- 其它激活时新建物理/改 body 图的部件(若有新发现):一律归入"只记录不处理,由 body 同步处理"这条线。
