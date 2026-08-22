# 同步"起落架开关等"部件展开/开关状�?�?可行性分�?
> 项目:JNOmultiplayerTest(aMptest)
> 反编译参�?`C:/renko/shitProgram/jnoCode`
> 定位:[`multi-craft-sync-2026-08-16.md`](multi-craft-sync-2026-08-16.md) 的补充分析——回�?幽灵船的起落架收�?货舱�?太阳能板/灯等**开关与展开状�?*能否同步、怎么同步、代价多�?
> 状�?**�?方案 B(P0)已实现并实测通过(起落�?货舱同步 OK,2026-08-18)**;**P3 控制输入应用已实�?*(§11,2026-08-18,编译通过待实�?。① 方案 B(per-part `Activated` �?;�?分离�?整流�?对接�?*涉及 body 改动的部件只记录、不处理**(归后�?body 同步);�?降落伞等特殊部件**先反编译确定原理**(�?§9),走专用视觉驱�?P2);�?输入驱动部件(rotator/舵面�?**�?开�?输入"双驱�?*,�?P3 控制输入应用解决(§11,已实�?。。① 方案 B(per-part `Activated` �?;�?分离�?级间、整流罩、对接等**涉及 body 改动的部件只记录、不处理**(归后�?body 同步);�?降落伞等特殊部件**先反编译确定原理**(�?§9),走专用视觉驱�?P2);�?输入驱动部件(rotator/舵面�?**�?开�?输入"双驱�?*,�?P3 控制输入应用解决(§11,用户 2026-08-18 指出)。实现记录见 §10;P1(相位对齐)/P2(伞专用驱�?/P3(控制输入应用)待排期�?> 结论先行:**同步"开关状�?可行且成本近�?*——机制是同步每个部件�?`Part.Activated`(开关位),幽灵�?*复用游戏自己�?FlightUpdate/动画器做本地仿真**(�?engine-fx 尾焰�?L1"输入/状态同�?+ 本地仿真"完全同套�?。不推翻 8.2-5"燃料/资源不同�?,只把"部件展开状�?�?8.2-5 的限制里摘出来�?
---

## 0. 现状(plan 已认定的限制)

- [`multi-craft-sync-2026-08-16.md`](multi-craft-sync-2026-08-16.md) 8.2-5 决策:**燃料/资源/部件状�?MVP 不同�?*,其中"part 损伤/展开/引擎/Vizzy 状�?都记为已知限制�?- engine-fx 尾焰已用"同步**视觉驱动�?*(throttle)"打破�?引擎视觉不同�?的边�?不涉及燃料数�?�?*起落架等开关是同一�?*:同步"开关状�?Part.Activated)",不同步任何燃�?资源数值�?- 现状代码:recdata **已含** `ActivationGroupStates`(10 bool)+ `Stage`(�?[`Mod.cs`](../Assets/Scripts/Mod.cs:132)),且已序列化传�?[`MpMessage.cs`](../Assets/Scripts/Net/MpMessage.cs:488)),�?**接收端从未应�?*(`MpNetworkManager.ApplyRemoteState` 只采样不应用)——是现成的半成品通道�?- 同理:`Pitch/Yaw/Roll/Throttle/Brake/Sliders` 也是"只采样、不应用"的死字段(`MpNetworkManager.cs:1951-1962`)�?
---

## 1. 起落架真实实�?反编译确�?

- **开�?= `PartData.Activated`**:`LandingGearScript.FlightUpdate` 每帧�?`base.Data.Part.Activated` �?`SetExtended(...)`([`LandingGearScript.cs:155`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/LandingGear/LandingGearScript.cs:155))�?- **动画是纯 Transform**:`ConfigurableGearScript.SetExtended` 只是转发�?`_animator`([`ConfigurableGearScript.cs:502`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/LandingGear/ConfigurableGearScript.cs:502));`LandingGearAnimator` �?Unity 原生 `Update`(不受物理门控),�?`Time.deltaTime` �?~4s 收放 + 舱门旋转([`LandingGearAnimator.cs:311/373`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/LandingGear/LandingGearAnimator.cs:311))�?- **真正的轮子物�?*�?`ResizableWheelColliderNew`,幽灵上已�?`DisableCraftPhysicCalculation` 关闭(不影响动�?�?- 幽灵 modifier 每帧�?`IFlightUpdate`(engine-fx §3.5 定论:注册只看 MonoBehaviour enabled,`EnablePhysics(false)` 不禁�?MonoBehaviour);动画器走原生 Update 也照跑�?
> **推论:接收端把幽灵对应部件�?`Part.Activated` 设成同步�?游戏自己�?FlightUpdate 就驱动收放动画平滑播放——无需逐帧驱动代码�?*

## 2. 为什�?现在"不同�?�?engine-fx 的差�?

- engine-fx 的根因是:幽灵物理关后游戏**强制把视觉关�?*(激活门�?+ 每帧归零 throttle)�?- 起落�?*没有**这种门控——`FlightUpdate` 每帧都读 `Part.Activated` 并驱动动�?**问题只在开关本身没被同�?*:幽灵�?`Part.Activated` 停留在加入时 XML 的设计�?StartExtended),发送端之后的一切收放幽灵都不跟随�?- 因此起落架同�?= **只补"开关位传输 + 接收端应�?**,比尾焰还简�?尾焰还要绕激活门�?起落架不需�?�?
## 3. 同步方案(两个选项,建议 B)

| | 方案 A:复用现有 ActivationGroupStates | **方案 B:per-part `Activated` �?推荐)** |
|---|---|---|
| 带宽 | 0(已有字段) | N bit(N=部件�?1000 部件=125B/�?20Hz�?.5KB/s,可忽�? |
| 覆盖 | �?挂激活组"的开�?| 激活组 + **stage 级联** + 飞行检查器直切 + self-governed 部件,全覆�?|
| 应用 | 幽灵遍历 parts,`ActivationGroup==i` 的按位调 `Activate()/Deactivate()` | �?按确定顺�?`Data.Assembly.Parts`,�?EngineThrottles 同一顺序契约),**排除 Detacher** |
| 漏网 | 不挂组的起落�?�?引擎点火/检查器直切 | �?|

- 应用入口:每包(�?*变沿**——变化才�?调用 `PartScript.Activate()/Deactivate()`([`PartScript.cs:521`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/PartScript.cs:521),幂等,内部�?`if(!Activated)` 守卫)�?- **持续应用的自愈好�?*:幽灵本地任何偏差(如伞按本地大气密度自切断)下一�?50ms)即被纠正�?- 进阶(可�?:起落架额外同�?`ExtensionPercent`(float)�?`SnapToExtensionPercent` 对齐动画相位;货舱 `OpenAmount`、SubPartRotator `CurrentEnabledPercent` 同理。MVP 可省(4s 动画自愈)�?
**【决�?2026-08-18):采用方案 B per-part `Activated` 位——P0 已实�?�?§10�?*,覆盖完整、带宽可忽略�?*接收端应用过�?只记录不处理 / 特殊处理)�?§4/§9**:`Detacher`、`Fairing`、`DockingPort` 位照常传输但**不应�?*;`Parachute` 不应�?`Activated`,走专用视觉驱动�?*应用位清�?白名单驱�?:起落�?/ 货舱�?/ 着陆腿 / 太阳�?/ 灯·信�?/ SubPartRotator�?* 阶段可用 `PartScript.Data.Activated` 直接驱动(不依赖激活组)�?*

**为什么不�?SP2 �?整机控制输入�?激活组�?(2026-08-18 决策依据,反编译对�?**:
- SP2 起落�?= 整机输入 `AircraftControls.LandingGearDown`,收放动画统一 `AnimateLandingGear(Controls.LandingGearDown)`([sp2 `NetworkAircraftControls.cs:89/116/149`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/NetworkAircraftControls.cs:89)),控制�?1 bit + `SetInputOverride` 即可带动全部起落架本地动画—�?*能这么做是因�?SP2 起落架被设计�?纯整机输入驱�?,部件层没有第二条驱动路径**;
- **SR2 �?`Part.Activated` 有三条入�?*:激活组(`ActivatePartsInActivationGroup`)、Stage(`ActivateStage`)�?*飞行检查器手动 override**(`PartScript.cs:830-831` �?Activate/Deactivate 按钮 �?`PartScript.Activate()/Deactivate()` [`PartScript.cs:521/666`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/PartScript.cs:521))。手�?override **不产生激活组位变化、不产生 Stage 变化** �?SP2 式输入位/激活组位抓不到 �?**必须 per-part 位才能全覆盖**;
- 附带稳健�?幽灵端游戏自身也可能驱动 `Part.Activated`(�?`PartScript.cs:714` �?`AutoActivateIfNoStageOrActivationGroup`),per-part **持续每包应用自带自愈**,SP2 的输入覆盖没有这�?�?SP2 无手�?override 所以也不需�?�?
**应用前提(2026-08-18 �?�?开关≠姿�?的穿�?**:
- **白名单只�?开关确定�?部件**:视觉姿态是 `Part.Activated` 的确定性函�?收放/开�?展开动画),**无输入依赖、无 body 结构依赖**;
- **输入驱动部件一律不进白名单**:Rotator/JointRotator(UI �?"Rotator",`_controller = GetInputController()`)、控制舵面、gimbal、活塞、螺旋桨桨距、车轮转�?电机、RCS —�?这些"开�?输入"部件�?`Activated` 不够,需 **P3 控制输入应用**(把同步的 Pitch/Yaw/Roll/Throttle/Brake/Sliders/Translate 写进幽灵 `CraftControls`;SP2 已验证此�?`ModApi.Craft.CraftControls` 是纯 public 可写属�?;
- **已知穿模风险(记录,P0 不阻�?**:展开状态可�?*穿过未同步的 body 结构**——整流罩投弃/分级/分离�?只记录不处理"、`Stage` 未应�?�?发送端已投弃整流罩并展开其内太阳�?幽灵仍显示整流罩未投�?�?太阳能穿整流罩。归 body 同步里程�?MC2 §8.1-4)解决�?
## 4. "相似逻辑"部件分级(反编译逐一确认)

| 部件 | 开关来�?| 幽灵行为 | 可行�?|
|---|---|---|---|
| **起落�?* LandingGear | `Part.Activated` �?纯动�?| 轮子物理已关,动画照播 | �?干净 |
| **货舱�?* CargoBay | `Part.Activated` �?`Data.Open`([`CargoBayScript.cs:55`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/CargoBayScript.cs:55)) | 门动�?碰撞体已�?| �?干净 |
| **着陆腿** LandingLeg | `Part.Activated`([`LandingLegCommon.cs:92/105`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/LandingLeg/LandingLegCommon.cs:92)) | 收放视觉 | �?干净 |
| **太阳能板** Solar | `Part.Activated` �?Open+展开([`SolarPanelArrayScript.cs:233`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/Solar/SolarPanelArrayScript.cs:233)) | 本地太阳位置自足 | �?干净 |
| **�?信标** Light/Beacon | `Activated && HasPower`([`LightScript.cs:308`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/Lights/LightScript.cs:308)) | ⚠️ 幽灵电池可能�?陈旧 �?需强制 HasPower 或接�?| ⚠️ 小坑 |
| **SubPartRotator** | `Part.Activated`,自带 `SyncActivationGroup`([`SubPartRotatorScript.cs:84`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/SubPartRotatorScript.cs:84)) | 反向写激活组,方案 A/B 都覆�?| ⚠️/�?|
| **轮子转向/刹车** | 控制输入驱动 | 控制输入接收端未应用(死字�?;视觉转向会读幽灵本地输入 | ⚠️ 另一条线,视觉小偏�?|
| **分离�?级间** Detacher | `OnActivated �?Detach()`([`DetacherScript.cs:150`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/DetacherScript.cs:150)) | **不受物理门控**:销毁关�?施加冲量+相机震动+声音([`DetacherScript.cs:37`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/DetacherScript.cs:37)) �?**body 图改�?*(body 分离) | 🔒 **只记�?不处�?*(�?body 同步) |
| **整流�?* Fairing | `OnActivated �?_jettisonNextFrame �?InitiateJettison`([`FairingScript.cs:89`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/FairingScript.cs:89)) | `QueuePartGroupForDestruction` + 新建 `FairingDebris` body + 声音([`FairingScript.cs:151`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/FairingScript.cs:151)) �?**body 图改�?* | 🔒 **只记�?不处�?*(�?body 同步) |
| **对接** DockingPort | `Activated` 使能对接 collider([`DockingPortScript.cs:152`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/DockingPortScript.cs:152)) | 对接 = body merge(�?`CraftNodeRemoved` + 重发 dominant XML,�?multi-craft-sync §8.1-4),幽灵不模�?| 🔒 **只记�?不处�?*(�?body 同步) |
| **降落�?* Parachute | `Activated �?DeployParachute`([`ParachuteScript.cs:262`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/ParachuteScript.cs:262)) | **激活时新建 collider+rigidbody+SpringJoint** + 密度/高度门控 + 自动切伞(详见 §9) | 🎯 **专用视觉驱动**(不应�?`Activated`,�?§9) |
| **引擎** | `Part.Activated` | OnActivated �?`IsPhysicsEnabled` 门控 | �?安全(尾焰已由 EngineVisualSync �? |
| **发生�?陀螺仪/RCS** | `Activated` | 幽灵电池副作�?无物理意�?| ⚠️ 低风�?|

## 5. 风险清单(幽灵特定)

1. **Detacher / Fairing / DockingPort 排除是硬要求**(否则幽灵�?body 分裂/被销毁部件组/相机震动)——应用循环按部件类型白名单过�?�?§3 决策)�?2. 动画相位:不传相位�?~4s 内自�?可接�?
3. 收放完成触发 `InitiateDragRecalculation`(动画�?428 �?:幽灵 `IncludeInDrag=false` �?无害,建议回归验证;
4. 幽灵电池副作�?generator/�?:只影响幽灵自身视�?不影响远程真实船;测试后决定是否屏�?
5. 激活时播放本地音效(舱门/收放�?,观感更好,无需处理�?
## 6. 带宽/性能(�?1000 起落�?问题)

- **同步本身边际成本 �?0**:
  - 带宽:per-part �?1000 部件 = 125B/�?2.5KB/s@20Hz),远小于同 craft 现有�?EngineThrottles(1000×4B=4KB/�?+ BodyRotations(�?body 3 float);
  - 应用循环:1000 �?bool 比较/�?× 20Hz = 2 万次/�?可忽�?`Activate()` 守卫使无变化时为空操�?
  - 动画期间:仅收放的 ~4s 窗口内做少量 Transform 写入�?- **真正的开销是游戏自身对 1000 �?gear 组件的模�?*(�?gear 每帧 `FlightUpdate` + 动画�?`Update` + **4 �?AudioSource**),�?*双端都存在、与同步无关**—�?000 起落架的 craft 单机就卡成个位数 FPS,是病�?craft 本身不可�?不是同步引入�?- SR2 沙盒**无硬性部件数上限**(仅生涯模式有可配�?`Craft.MaxPartCount`,�?[`CareerValidator.cs:229`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/State/Validation/CareerValidator.cs:229))�?- 极端防护(如需�?:**增量/变沿压缩**(只传变化�?(index,value),空闲 0 字节)+ 位打�?8bit/字节)+ 距离 LOD(MC2 慢发对象不发)。采样侧复用 EngineThrottles �?确定顺序"遍历,一次循环采多类字段�?
## 7. 与现�?plan 的关�?
- 不依�?MC1(�?craft 全局身份),单船可先�?同尾焰先�?;
- 依赖 MC2"每船状态包"加字段的格式(方案 B 需扩展);
- �?8.2-5"燃料/资源不同�?**不冲�?*(只同步开关状�?不含燃料/资源数�?;
- �?8.1-3 �?Harmony 拦截不冲�?只动部件状态、不�?pod);
- 相邻�?顺带可补 `Stage` 应用(原理�?但同样要避开 Detacher)�?
## 8. 结论与建议排�?
| 项目 | 可行�?| 成本 | 建议 |
|---|---|---|---|
| 起落架收�?开关同�? | �?�?本地仿真 | 近零 | **�?*(P0) |
| 货舱�?着陆腿/太阳�?| �?�?同机�?| 近零 | **�?*(�?P0 一�?同一条应用循�? |
| �?信标 | �?�?| �?| �?需处理 HasPower/电池) |
| SubPartRotator | �?�?| �?| �?方案 B 顺带覆盖) |
| 引擎点火 | �?已由尾焰覆盖 | �?| 无需额外 |
| 降落�?| 🎯 专用视觉驱动(�?§9) | �?| 反编译已定原�?P2 单做 |
| 分离�?级间、整流罩、对�?| 🔒 只记录不处理 | 0 | �?body 同步(记录�?不应�? |
| 粒子/逐帧展开相位 | �?不值得 | �?| 不做(4s 自愈) |

**排期建议**:
1. **P0** 方案 B:recdata �?per-part `Activated` �?复用 EngineThrottles 顺序枚举)+ 接收�?*白名单应�?*(起落�?货舱/�?太阳�?�?SubPartRotator;**Detacher/Fairing/DockingPort 只记录不应用**;Parachute 不应�?�?起落�?货舱/�?太阳�?灯一次到�?
2. **P1** 起落�?`ExtensionPercent` 相位对齐(可�?;
3. **P2** 降落伞专用视觉驱�?�?§9 方案)+ 发生�?灯逐个回归;
4. 回归:�?Steam 账号 �?TCP VM 实测收放动画同步(�?engine-fx §9 套路)�?
## 9. 降落伞等特殊部件(反编译定原理,2026-08-18)

### 9.1 降落伞工作原�?`ParachuteScript`)

| 环节 | 行为 | 反编译证�?|
|---|---|---|
| **展开触发** | `FlightUpdate`:�?`Part.Activated && !_deployed` �?`DeployParachute()` | [`ParachuteScript.cs:262`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/ParachuteScript.cs:262) |
| **展开门控** | `SurfaceVelocity < MaxDeploymentSpeed` �?高度/大气密度 满足(`ASLDeployment`/`DeploymentDensity`)才展开 | [`ParachuteScript.cs:110`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/ParachuteScript.cs:110) |
| **DeployParachute** | �?`_deployed=true`;缩放 base collider;**新建 SphereCollider**(chutePackage �?+ **新建 Rigidbody `_chuteBody`**(质量/无重�?+ **新建 SpringJoint** 连部�?body Rigidbody;�?chuteBody 位置/速度;激�?chute mesh | [`ParachuteScript.cs:108-150`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/ParachuteScript.cs:108) |
| **充气+阻力** | `FlightFixedUpdate`:充气动画 `_chute.localScale` �?`_inflateTime` �?`_chuteCollider.radius` 同步;**对部�?body �?chuteBody `AddForce` 施加阻力** | [`ParachuteScript.cs:165-257`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/ParachuteScript.cs:165) |
| **自动切伞** | 本地 `AirDensity < CutDensity`(或高�?�?`Part.Activated=false` | [`ParachuteScript.cs:190`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/ParachuteScript.cs:190) |
| **拉断** | 阻力超阈值时 `Activated=false` + 日志 | [`ParachuteScript.cs:235`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/ParachuteScript.cs:235) |
| **收起** | `FlightUpdate`:�?`!Activated && _deployTime>0` �?销�?joint + 销�?chuteBody + 禁用 collider + 隐藏 chute | [`ParachuteScript.cs:267-279`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/ParachuteScript.cs:267) |

### 9.2 为什么不能走通用 `Activated` 应用(幽灵�?

1. **DeployParachute 在激活瞬间新建物理组�?*(SphereCollider + �?kinematic Rigidbody + SpringJoint),幽灵初始化时�?`DisableCraftPhysicCalculation` **不覆盖这些后加组�?* �?幽灵上会留一个活�?collider 和一个被弹簧拖拽的非 kinematic chuteBody,不干净、可能乱�?
2. **密度/高度门控是本地量**:幽灵 `FlightData.AltitudeAboveSeaLevel`/`AtmosphereSample.AirDensity` 与发送端不同(�?mod 只刷�?PositionNormalized/CraftForward)�?可能出现"发送端已展开、幽灵因本地条件不满足而不展开"�?幽灵自动切伞"的错�?
3. 阻力 `AddForce` 在幽�?kinematic body 上无效、在�?kinematic chuteBody 上会漂�?
### 9.3 处理方案:专用"视觉-only"驱动(不入通用应用)

- **发送端采样**:反射�?`_deployed`(权威真实状�?已含全部门控/切伞判定)�?recdata �?`List<bool> ParachuteDeployed`(或并�?per-part �?+ 单独相位字段);可�?`_inflateTime` 归一化做充气相位;
- **接收�?*:**不应�?*伞部件的 `Part.Activated`(伞自己的 `FlightUpdate` �?`Activated=false && _deployTime==0` 时是 **no-op**,`FlightFixedUpdate` �?`_deployed=false` 也是 no-op �?**天然安全,无需 Harmony patch**,与航发加力情况不�?;
- **专用驱动**(可挂 `EngineVisualSync` 同层或独�?`PartVisualSync`):按同步值驱动视觉——`_chute.gameObject.SetActive(deployed)` + `_chute.localScale` 充气动画(本地 `_inflateTime` 推进,参数与游戏公式一�?+ 收起时复�?`_chutePackage`;**不创建任�?collider/rigidbody/joint**;
- 不传相位时退化为"展开瞬间直接全尺�?(可接�?MVP 先这�?�?
### 9.4 其余"特殊"部件(同型确认)

- **对接 DockingPort**:body merge(已在 multi-craft-sync §8.1-4),record-only;
- 其它激活时新建物理/�?body 图的部件(若有新发�?:一律归�?只记录不处理,�?body 同步处理"这条线�?
## 10. 实现记录(2026-08-18,P0 方案 B 已落�?编译通过待实�?

**改动文件**(均在本工�?:
1. [`Mod.cs`](../Assets/Scripts/Mod.cs):`RemoteDataPack` 新增 `List<bool> PartActivated` 字段 + 构造初始化;
2. [`MpMessage.cs`](../Assets/Scripts/Net/MpMessage.cs):`WriteRecdata/ReadRecdata` 序列�?`PartActivated`(count + N bool,�?EngineThrottles 同风�?;
3. [`PartVisualSync.cs`](../Assets/Scripts/Net/PartVisualSync.cs)(**新增**):`SamplePartActivated`(发送端采样)+ `ApplyRemotePartActivated`(接收端变�?+ 白名单应�?;
4. [`MpNetworkManager.cs`](../Assets/Scripts/Net/MpNetworkManager.cs):`TrySampleLocalCraft` 接入采样;`ApplyRemoteState` 末尾接入应用�?
**实现要点**:
- **顺序契约**:发�?接收同按 `Data.Assembly.Parts` 顺序,index 一一对应;接收�?`Mathf.Min` 越界兜底;
- **白名�?*(`PartVisualSync._applyModifierTypes`):`LandingGearScript` / `CargoBayScript` / `LandingLegScript` / `SolarPanelScript` / `SolarPanelArrayScript` / `LightScript` / `BeaconLightScript` / `SubPartRotatorScript` —�?命中任一 modifier 才应�?`Activate()/Deactivate()`;
- **只记录不处理**:引擎(EngineVisualSync �?火箭引擎还被强制 Activated=true 刷新膨胀�?不能在此应用)、Detacher/Fairing/DockingPort(body 改动)、Parachute(专用驱动 P2)——位照常进包,接收�?`ShouldApply` 返回 false 跳过;
- **变沿 + 幂等**:`PartScript.Activate()/Deactivate()` 内部�?`if(Activated)` 守卫;每包调用无变化即空操�?幽灵本地偏差下一包自�?
- **首包即校�?*:`InitializeRemoteCraft` 里首�?`ApplyRemoteState` 已含本应�?加入瞬间起落�?货舱等即对齐发送端;
- 构建:`dotnet build aMptest.csproj -c Debug` �?**0 警告 0 错误**(新增文件已手工加�?csproj `<Compile>`;Unity 重新导入后会自动包含)�?
**未做(后续)**:
- P1:起落�?`ExtensionPercent` 相位对齐(4s 动画自愈,可不�?;
- P2:降落伞专用视觉驱�?§9.3 方案)+ 发生�?灯回�?
- **P3:控制输入应用**——✅ **已实�?*(2026-08-18,�?§11):把同步的控制输入(Pitch/Yaw/Roll/Throttle/Brake/Sliders/Translate,recdata 已传但从未应�?写进幽灵 `CraftControls`;输入驱动部件(舵面/Rotator/活塞/螺旋�?车轮/RCS/电机)�?`Activated` 应用已放开;物理操纵杆绑定轴的特例未覆盖(§11.4);
- **已知限制(P0 记录)**:展开可能穿过未同�?body 结构(整流�?stage/分离器未应用),�?body 同步里程碑解�?
- 实测待办:�?Steam 账号 �?TCP VM 实测起落�?货舱/太阳能收放同步、幽灵本地偏差自愈、分离器不触�?回归清单�?§5)�?
## 11. P3 控制输入应用 —�?已实�?2026-08-18)

**结论**:P3 可行且干净,已落地。幽�?`CraftControls`(活动�?**游戏从不刷新**,每包直接写同步控制值即可驱动所�?*绑定 CraftControls 的输入驱动部�?*(舵面/gimbal/Rotator/活塞/螺旋桨桨�?车轮转向/RCS);配合方案 B �?per-part `Activated` �?*放开输入驱动部件**,完整复现"开�?输入"姿�?这正是用�?2026-08-18 指出�?`rotator 等还需开�?+ inputController 输入才会动` 的正确解�?�?
**11.1 幽灵 Controls 无人�?唯一写者是玩家 FlightControls)**
- 飞行中每帧写 `Controls.X` 的只�?`FlightControls.Update`([`FlightControls.cs:302-388`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/FlightControls.cs:302)):`Controls.Pitch/Yaw/Roll/Brake/Sliders = 原始输入 + Offset*`(原始输入为玩家键�?�?;
- 它是**单例**:`FlightSceneScript` �?`new FlightControls` 一�?[`FlightSceneScript.cs:907`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/FlightSceneScript.cs:907)),`SetCraftNode`([`:1551`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/FlightSceneScript.cs:1551))只绑定玩�?craft;远程幽灵 **永远不是目标** �?幽灵 Controls 恒为 XML 初值、可自由�?
- 其余写点均不碰非玩家 craft:设计�?`DesignerControls.cs`)、UI 滑杆(`InputSliderPanelController`/`ThrottleInputScript`,绑玩�?FlightControls)、地图导�?`NodeNavigator`,玩家)、EVA 乘组舱复�?`EvaScript.cs:1789`,仅带乘组舱的 craft);
- 舱间复制:活动�?�?其余�?`CraftControls.CopyControls`([`CommandPodScript.cs:329`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/CommandPodScript.cs:329))�?**单点�?`ActiveCommandPod.Controls` 即全舱生�?*�?
**11.2 输入�?�?Controls 的路�?**
- `GetInputController((CraftControls x)=>x.Pitch)` 找不到部件上的输�?modifier �?回退 **`SimpleInputController`**([ModApi `SimpleInputController.cs:74-85`](../C:/renko/shitProgram/jnoCode/ModApi/Craft/Parts/Input/SimpleInputController.cs:74)):`Value = getValue(commandPod.Controls)`,**门控 `partData.Activated || IgnorePartActivated`**(未激活返�?0);
- 部件上的 **`InputControllerScript`**(操纵�?滑块/自定义轴,`Assets/Scripts/Craft/Parts/Modifiers/Input/`):�?`_primaryInput`(物理轴或 CraftControls 属�?�?`Value`([`InputControllerScript.cs:135-219`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/Input/InputControllerScript.cs:135)),并受 `Activated` / **`ActivationGroup` 门控**([`:158-164`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/Input/InputControllerScript.cs:158),`commandPod.Controls.GetActivationGroup(�?`)�?
**11.3 落地路径(�?已实�?**
1. **每帧写幽�?Controls**:�?`ApplyRemoteState`(�?PartActivated 同处)�?`ActiveCommandPod.Controls.Pitch/Yaw/Roll/Brake/Throttle/Slider1-4/TranslateForward/Right/Up`(recdata 已传,现为死字�?;
2. **放开输入驱动部件�?Activated 应用**:方案 B 已对所有部件传 `PartActivated` �?只是 `ShouldApply` 白名单过�?P3 把输入驱动部�?舵面/gimbal/JointRotator/Rotator/活塞/螺旋�?车轮转向�?加入应用集合 �?激活门控满�?�?配合写入�?Controls 完整复现姿�?"开关≠姿�?不再成立);
3. **激活组门控**:`InputControllerScript` 受激活组门控�?应用 recdata �?`ActivationGroupStates` �?`Controls.SetActivationGroup(i, state)`([`CraftControls.cs:422-426`](../C:/renko/shitProgram/jnoCode/ModApi/Craft/CraftControls.cs:422) 已存�?;
4. **Throttle**:�?`Controls.Throttle` 也生�?幽灵�?FlightControls/油门 UI),驱动航发推力与推进器视觉;火箭尾焰/引擎仍走 EngineVisualSync,不冲突�?
**11.4 已知边界/风险**
- **物理操纵杆绑定的部件**(`InputControllerScript._primaryInput` 是本地物理轴):读本地玩家输�?�?跨端不同,特例不覆�?后续需单独同步该输入控制器部件的输出�?;
- **AutoActivate**:多数输入部件 `AutoActivateIfNoStageOrActivationGroup=true`(`PartScript.cs:714` FlightPostStart 自动激�?�?幽灵天然 Activated、`SimpleInputController` 可用;但发送端靠激活组/手动激活的部件幽灵端未激�?�?必须�?11.3.2 �?
- **写时�?*:�?PartActivated 同帧(ApplyRemoteState �?游戏更新�?,输入驱动部件读最新同步�?
- **带宽**:控制标量 ~13 float/�?@20Hz �?1KB/s,可忽�?
- 与现�?引擎尾焰/推力"解�?EngineVisualSync 已管引擎视觉,Controls.Throttle 只补充航发推进器/推力杆视�?无冲突�?
**11.5 实现记录(2026-08-18,P3 已落�?编译通过待实�?**
- 改动文件:
  1. [`ControlVisualSync.cs`](../Assets/Scripts/Net/ControlVisualSync.cs)(**新增**):`ApplyRemoteControls(rc, data)` —�?写幽�?`ActiveCommandPod.Controls` �?12 个控制标�?+ 10 个激活组(`SetActivationGroup`,幂等变沿),异常兜底;
  2. [`PartVisualSync.cs`](../Assets/Scripts/Net/PartVisualSync.cs):`_applyModifierTypes` 新增输入驱动部件 —�?`ControlSurfaceScript`/`JointRotatorScript`(Rotator)/`PistonScript`/`PropellerAssemblyScript`/`ResizableWheelScript`/`ReactionControlNozzleScript`/`ElectricMotorScript`/`ElectricMotorOldScript`/`LightPartScript`;
  3. [`MpNetworkManager.cs`](../Assets/Scripts/Net/MpNetworkManager.cs):`ApplyRemoteState` �?PartVisualSync 之后接入 `ControlVisualSync.ApplyRemoteControls`(�?PartActivated 同帧);
  4. [`aMptest.csproj`](../aMptest.csproj):Unity 自动加入新文件�?- **安全排除(仍只记录不处�?**:引擎(EngineVisualSync 冲突)、分离器/整流�?对接/�?body 改动/专用驱动)�?*InputBasedActivator**(�?`ActivateStage`/`ExplodePart`,绝不在本机触�?、舱/Cockpit/Vizzy(FlightProgram)/TestPilot;
- 构建:`dotnet build aMptest.csproj -c Debug` �?**0 错误**(FishNet 第三�?3 个既�?warning 与本改动无关);
- **2026-08-22 修复 `ApplyRemoteControls` 每帧异常(IndexOutOfRange,Player.log 418 次刷�?**:
  - **根因**:激活组在游戏里�?**1-indexed(1..10)**,`CommandPodScript.SetActivationGroupState` 内部 `ActivationGroupStates[group-1]` **只查上界不查下界**;接收端旧循环 `i=0..9` �?`c.SetActivationGroup(0)` �?`ActivationGroupStates[-1]` �?`IndexOutOfRange`。异常被 `ApplyRemoteControls` 外层 try/catch 吞掉,控制标量(已先�?不受影响,�?*激活组同步实际从未生效**,且每帧抛异常+写日志拖慢接收端;
  - **修复**([`ControlVisualSync.cs`](../Assets/Scripts/Net/ControlVisualSync.cs)):循环�?`for i=1..n`,取列�?`data.ActivationGroupStates[i-1]`(与发送端 `i=1..10` 采样一一对应);`GetActivationGroup(0)` 本就安全,`SetActivationGroup(1..10)` 永不越下�?�?异常消失、激活组门控真正生效;
  - 验证:`dotnet build aMptest.csproj -c Debug` �?0 错误 0 警告;
- 实测待办:双端验证 Rotator/舵面/RCS/车轮随远程输入显�?激活组门控部件(激活组绑定的输入部�?状态同�?确认幽灵 Controls 不被任何路径覆盖(理论已证,实测复核)�?