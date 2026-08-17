# 同步发动机尾焰与粒子效果 — 可行性分析

> 项目:JNOmultiplayerTest(aMptest)
> 反编译参考:`C:/renko/shitProgram/jnoCode`
> 定位:[`multi-craft-sync.md`](multi-craft-sync.md) 的补充分析——回答"幽灵船的引擎尾焰/烟雾/热畸变能否同步、怎么同步、代价多大"
> 结论先行:**火焰(尾焰)可同步且成本几乎为零;烟雾/热畸变/RCS 只能做"输入同步 + 本地仿真"(形态一致、非逐粒子一致);粒子级精确同步不可行;幽灵重开物理不可取。**

---

## 0. 现状(plan 已认定的限制)

- [`multi-craft-sync.md`](multi-craft-sync.md) 8.2-5 决策:**燃料/资源/部件状态 MVP 不同步**,并记为"幽灵物理关 → 引擎视觉本来不跑"。
- 幽灵船 = `AllowPlayerControl=false` + `SetPhysicsEnabled(false, Warp)` + 全 body kinematic + `DisableCraftPhysicCalculation`(清全部碰撞体)。

> 本文不推翻 8.2-5:同步的是"视觉驱动值"(throttle),不是燃料数值;顺带能在视觉上反映"油尽熄火"(throttle→0)。

---

## 1. 尾焰/粒子的真实构成(反编译确认)

| 效果 | 实现 | 关键代码 | 本质 |
|---|---|---|---|
| **尾焰本体** | `ExhaustSystemScript`:`MeshRenderer` + 自定义 shader,由材质参数驱动 | `UpdateExhaust(throttle)` → `UpdateProperties`([ExhaustSystemScript.cs:507](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/Propulsion/ExhaustSystemScript.cs:507)) | **确定性**:形状/长度/扩张/亮度/颜色全是 throttle 的纯函数;唯一随时间的是 `_TextShift` 纹理滚动(装饰性) |
| **引擎烟雾** | `SmokeTrailScript`:`ParticleSystem` 本地发粒子 | `EngineNozzleScript.FlightUpdate` → `smokeTrail.FlightUpdate(surfaceVelocity)` + `LateUpdate` 里 `FlightScene.EmitParticle(EngineSmoke,…)`([SmokeTrailScript.cs:149](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/SmokeTrailScript.cs:149)) | 发射率/速度/大小/朝向由 **throttle × 速度 × 排气方向 × 大气密度** 决定——输入全可共享 → 本地仿真 |
| **热畸变** | `DistortionEffectScript`:`ParticleSystem` + `_Distortion` 材质 float | `FlightUpdate(intensity)`([DistortionEffectScript.cs:50](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/Propulsion/DistortionEffectScript.cs:50)) | intensity → emission enable + 材质值,本地仿真 |
| **RCS** | `ReactionControlNozzleScript`:独立 `ParticleSystem` | `ToggleParticles(bool)` + `FlightUpdate` 读 RCS 输入([ReactionControlNozzleScript.cs:362](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/ReactionControlNozzleScript.cs:362)) | 由控制输入驱动,本地仿真 |
| 撞击/地形尘 | `BodyCollisionHandler` / `EjectaProjectileScript` / `ExhaustDamageScript._dust` | 物理事件触发 | 瞬态、物理驱动,不值得同步 |

---

## 2. 为什么幽灵上现在不显示(根因)

幽灵物理禁用后,游戏自己的引擎逻辑**强制把视觉关掉**:

1. [`EngineCommon.FlightFixedUpdate`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/Propulsion/EngineCommon.cs:213) 里 `OnActivated()` 被 `partScript.CraftScript.IsPhysicsEnabled` **门控** → 幽灵引擎永远不激活(`_active=false`);
2. [`EngineCommon.FlightUpdate`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/Propulsion/EngineCommon.cs:263):`!this._active → UpdateEngineThrottle(0f)` → 每帧把视觉 throttle 归零 → 尾焰 `UpdateExhaust(0)` 隐藏;
3. `OnDeactivated` 在物理禁用时额外调 `DisableSmokeParticleSystem()`([EngineNozzleScript.cs:490](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/Propulsion/EngineNozzleScript.cs:490)) → 烟雾也被隐藏。

**结论:必须绕过游戏这套"激活门控",直接驱动视觉。**

---

## 3. 关键发现:游戏提供了现成钩子 `ExhaustThrottleOverride`

- [`EngineCommon.ExhaustThrottleOverride`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/Propulsion/EngineCommon.cs:130) 是 **public 可写 `Func<float>`**,在 `FlightUpdate` 里**无视 `_active` 与否**直接覆盖最终喂给 nozzle 的视觉 throttle;
- 三种引擎(`EngineScript` / `RocketEngineScript` / `JetEngineScript`)**全部**走同一个 `EngineCommon`;
- **JetEngine 已用它做加力(afterburner)**([JetEngineScript.cs:604](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/Propulsion/JetEngineScript.cs:604))——说明这是官方留的"视觉覆盖"通道,不是 hack;
- 只需给每个幽灵引擎的 `_engineCommon` 设 `ExhaustThrottleOverride = () => 同步的throttle[i]`,火焰就会按同步值渲染,游戏自己每帧驱动它(前提已由 §3.5 定论成立)。

### 3.5 定论:幽灵引擎 modifier **确实**收到 `IFlightUpdate` / `IFlightFixedUpdate`

反编译逐层确认(非推测):

| 环节 | 证据 | 结论 |
|---|---|---|
| 注册 | `MonoBehaviourBase.OnEnable → Game.Loop.Register`([MonoBehaviourBase.cs:22](../C:/renko/shitProgram/jnoCode/ModApi/GameLoop/MonoBehaviourBase.cs:22));引擎 modifier 继承链 `PartModifierScript → MonoBehaviourBase` | 注册只看 MonoBehaviour 是否 enabled,**无物理过滤** |
| 分组 | `FlightUpdateGroupCollection.Register`([FlightUpdateGroupCollection.cs:160](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/GameLoop/FlightUpdateGroupCollection.cs:160))按接口(`IFlightUpdate`/`IFlightFixedUpdate`)入组 | **无 `IsPhysicsEnabled` 检查** |
| 关物理 | `SetPhysicsEnabled(false) → CraftScript.EnablePhysics(false)`([CraftScript.cs:1635](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/CraftScript.cs:1635))只置 flag + body kinematic + 调 `OnBeforePhysicsChanged/OnPhysicsChanged` 虚钩子 | **不**禁用 MonoBehaviour、**不**隐藏 GameObject → 不触发 `OnDisable` 反注册 |
| 幽灵处理 | `CraftUtils.DisableCraftPhysicCalculation`([CraftUtils.cs:97](../Assets/Scripts/CraftUtils.cs:97))只清碰撞体/置标记 | 不影响注册 |
| 派发 | `FlightGameLoop.FixedUpdate/Update`([FlightGameLoop.cs:153](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/GameLoop/FlightGameLoop.cs:153))对全部已注册项调用 | 非暂停/非 warp(本 mod 常态)下正常派发 |

**结论:幽灵的引擎 modifier 每帧收 `IFlightUpdate.FlightUpdate`、每 FixedUpdate 收 `IFlightFixedUpdate.FlightFixedUpdate`。** 现有注释([MpNetworkManager.cs:1477](../Assets/Scripts/Net/MpNetworkManager.cs:1477))"幽灵飞船不参与 IFlightUpdate"与代码不符(FlightData 陈旧更可能是执行序:游戏 FlightUpdate 读 `CenterOfMass` 先于 mod 写入,至多一帧滞后)。

> ⚠️ 这个定论对液体是**好消息**(Route A 可行),对 jet 加力却是**必须处理的坑**(见 §3.6)。

### 3.6 液体 vs 航发加力(按既定目标拆解)

**液体发动机(`EngineScript` / `RocketEngineScript`):Route A 干净可行**
- 幽灵上引擎 `FlightUpdate` 每帧跑,`EngineCommon.FlightUpdate` 的 `num3 = ExhaustThrottleOverride()` **无条件覆盖**视觉 throttle(与 `_active` 无关);
- `FlightFixedUpdate` 的激活门控在幽灵上不激活(`IsPhysicsEnabled=false`),且 `else-if (SupportsDeactivation)` 分支**不会**被进 → 不存在"每帧 `UpdateExhaust(0)` 反打";
- 做法:反射取 `_engineCommon`,设 `ExhaustThrottleOverride = () => syncThrottle[i]` → 火焰随同步值渲染;
- ⚠️ RocketEngine 特有:`FlightUpdate` 传 `smokeOpacity = num²·num`(num=`AdjustedThrottle()`,幽灵上=0)→ **烟雾 opacity=0 不发射**。火焰 MVP 不受影响;做烟雾时需额外 shim 或直接注入。

**航发加力(`JetEngineScript`):必须先中和它自己的 `FlightFixedUpdate`**
- 幽灵的 JetEngineScript `FlightFixedUpdate` 每 FixedUpdate:性能分支失败 → `_afterburnerThrottle=0`([JetEngineScript.cs:401](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/Propulsion/JetEngineScript.cs:401))+ `_rocketExhaustSystem.UpdateExhaust(0)`([JetEngineScript.cs:441](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/Propulsion/JetEngineScript.cs:441))→ **每帧反打**,不能只注入 `_afterburnerThrottle`(会被重置);
- 做法:用 Harmony prefix 跳过幽灵 JetEngineScript 的 `IFlightFixedUpdate`(与 8.1-3 拦总入口同套路),然后 mod 每帧直接驱动:
  - `_rocketExhaustSystem.UpdateExhaust(syncAfterburnerThrottle)`(反射取字段);
  - 主 nozzle 的 `ExhaustSystemScript`(加力态主火焰也由 afterburner 驱动);
  - 可选同值 `_engineCommon.ExhaustThrottleOverride`,避免 Update 阶段干扰。
- **发送端采样**:jet 用 `_afterburnerThrottle`(加力尾焰精确驱动值,幽灵上恒 0,必须显式同步);液体用 `_engineCommon.EngineThrottle`。

---

## 4. 同步分级(三层)

### L1 · 视觉状态同步(推荐)

| 项目 | 做法 |
|---|---|
| **同步数据** | 每引擎 `throttle`(0..1)。**MVP:直接用现有 `recdata.Throttle`(全局,0 新增字节)**,`throttle>0` 视为激活;精确版:`N×1byte throttle + N×1bit activated`(按引擎数,一般 1~8,20Hz≈0~400B/s,可忽略) |
| **尾焰(确定性)** | 两端 throttle 相同 → 火焰形状/长度/颜色几乎完全一致;唯一差异是纹理滚动 `_TextShift` 的随机初值(肉眼不可辨) |
| **烟雾(本地仿真)** | 共享 throttle/速度/朝向/大气 → 同参数各自发粒子 → **形态一致、随机序列不同**(观感正确,无人能分辨逐粒子差异) |
| **热畸变 / RCS** | 同参数本地仿真(throttle / 控制输入驱动) |
| **一致性** | 这是 **KSP 多人 mod(Luna/DMP)的标准做法:不传粒子,只传驱动状态** |

### L2 · 粒子级精确同步(不建议)

- 需要同步 Unity `ParticleSystem` 的 Random 内部状态 + 逐帧逐粒子位置/速度/寿命 → 一帧数百粒子 × 20Hz,带宽远超整个状态包;
- 双端帧序/时间不同步会让粒子持续失配,且玩家肉眼看不出差异 → **收益趋近于零,成本爆炸**。

### L3 · 幽灵重开物理做本地仿真(不建议)

- 为了"真实火焰"重新开物理,会重新引入 plan 反复规避的重力漂移/碰撞/Transform 覆盖/`[ ]` 接管污染问题 → **与幻影模式的设计相悖,得不偿失**。

---

## 5. 落地要点(接收端)

1. **初始化钩子**(挂在 `InitializeRemoteCraft` / `UpdateRemoteCrafts` 懒初始化里):
   - 枚举幽灵上 `IReactionEngine`(`EngineScript`/`RocketEngineScript`/`JetEngineScript`),反射取私有 `_engineCommon`,设 `ExhaustThrottleOverride = () => syncThrottle[i]`;可选 `DistortionIntensity = () => throttle`。
2. **副作用净化**(重要):
   - 幽灵碰撞体已被 [`CraftUtils.DisableCraftPhysicCalculation`](../Assets/Scripts/CraftUtils.cs:97) 全部 `enabled=false` → 尾焰 trigger collider 不会触发碰撞/加热;
   - 但 [`ExhaustDamageScript`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/Propulsion/ExhaustDamageScript.cs:74) 仍会每 FixedUpdate 跑(发地形尘 `_dust`) → 建议把其 MonoBehaviour `enabled=false`。
3. **烟雾速度**(关键细节):
   - 幽灵 body 全 kinematic,而 `RecalculateFrameState` 只对非 kinematic 刚体累加速度([CraftUtils.cs:63](../Assets/Scripts/CraftUtils.cs:63)) → 幽灵 `rigidbody.velocity≈0` → 烟迹不拖尾;
   - 解决:每帧把同步 `recdata.Velocity`(转帧空间)写入幽灵 kinematic rigidbody 的 `velocity`(无副作用),`SmokeTrailScript.LateUpdate` 即会发出正确拖尾。
4. **尾焰朝向**:无 gimbal 时火焰沿机轴;需要时**只复制 `UpdateNozzle` 的旋转计算(转视觉 nozzle,绝不施加力)**,用同步 `Pitch/Yaw/Roll`。
5. **平滑**:`recdata.Throttle` 已走现有插值缓冲 → 松油门时火焰平滑收小,不阶跃。
6. **LOD**:低频残骸(plan MC2 慢发对象)可只做火焰不做烟雾,或距离外跳过,省粒子预算。

---

## 6. 与现有 plan 的关系

- **不依赖 MC1**(多 craft 全局身份),可在当前单船架构上先做(火焰 MVP 只依赖现有 `recdata.Throttle`);
- 依赖 **MC2** 的"每船状态包"扩展(加每引擎 throttle 字段);
- 与 8.2-5"燃料不同步"决策**不冲突**(只同步视觉驱动值);
- 与 7.4 的朝向链路兼容:控制输入已在状态包,`ExhaustThrottleOverride` 与 srfRel 同步互不干扰。

---

## 7. 风险与开放问题

1. ~~幽灵引擎 modifier 是否真的收到 `IFlightUpdate`?~~ **✅ 已定论:收到**(见 §3.5 证据链)。液体走 Route A;jet 加力需先中和其 `FlightFixedUpdate`(见 §3.6)。仍建议首次联机时加一行打点,确认与静态结论一致。
2. **`ExhaustDamageScript` 残留行为**(地形尘/加热)需禁用后回归验证。
3. **`smokeOpacity` / `light` 输入在幽灵上陈旧**(读 `AtmosphereSample`/`FlightData`)→ 烟雾透明度/亮度轻微差异,可接受或显式覆盖。
4. **加力喷气**:JetEngine 的 `ExhaustThrottleOverride` 已被加力逻辑占用 → 幽灵上覆盖后以同步值(发送端 `EngineThrottle` 已含加力)为准,正确。
5. **热畸变受本地画质设置**(HeatDistortion On/Off)影响——跨端可能一个开一个关,这本来就属于画质差异,无需同步。

---

## 8. 结论与建议排期

| 效果 | 可行性 | 成本 | 建议 |
|---|---|---|---|
| 尾焰(火焰 mesh+shader) | ✅ 可,确定性 | 近零(0~400B/s) | **做**(优先级高,观感提升最大) |
| 引擎烟雾 | ✅ 输入同步+本地仿真 | 低 | **做**(按 LOD) |
| 热畸变 / RCS | ✅ 输入同步+本地仿真 | 低 | 做(可选) |
| 撞击/地形尘 | 瞬态,物理事件 | — | 不做 |
| 粒子级精确同步 | ❌ 带宽/收益不可行 | 极高 | 不做 |
| 幽灵重开物理 | ❌ 引入同步回归 | — | 不做 |

**排期建议**:① 单船火焰 MVP(只依赖 `recdata.Throttle`)→ ② 烟雾(velocity 注入 + ExhaustDamage 禁用)→ ③ RCS/热畸变 → ④ 并入 MC2 多船状态包的每引擎 throttle 字段与 LOD。

---

## 9. 实施状态(尾焰 MVP 已落地)

已按"液体尾焰 + 航发加力尾焰"优先实现(2025 落地,见代码内注释 `plans/engine-fx-sync-feasibility.md §3.5/§3.6`):

### 9.1 已实现文件

| 文件 | 改动 |
|---|---|
| [`Mod.cs`](../Assets/Scripts/Mod.cs) | `recdata` 增加 `List<float> EngineThrottles`(每引擎视觉 throttle)+ 构造初始化 |
| [`MpMessage.cs`](../Assets/Scripts/Net/MpMessage.cs) | `WriteRecdata`/`ReadRecdata` 追加 count+N 个 float |
| [`EngineVisualSync.cs`](../Assets/Scripts/Net/EngineVisualSync.cs) | 新增:发送端采样、幽灵驱动表、反射访问器 |
| [`MpNetworkManager.cs`](../Assets/Scripts/Net/MpNetworkManager.cs) | `RemoteCraft` 改 internal + `SyncedThrottles`/`EngineDrivers`;采样/设置/每帧驱动接入;`IsRemoteCraftNode` |
| [`JetEngineGhostPatch.cs`](../Assets/Scripts/HarmonyPatches/JetEngineGhostPatch.cs) | 新增:幽灵航发跳过 `IFlightFixedUpdate`/`IFlightUpdate` |

### 9.2 实现要点(含与 §3.6 的修正)

1. **发送端采样**(`SampleEngineThrottles`):确定顺序 = `Data.Assembly.Parts` → 每部件 `Modifiers`;液体取 `_engineCommon.EngineThrottle`,航发取 `_afterburnerThrottle`(加力驱动值,构造函数 `ExhaustThrottleOverride=()=>_afterburnerThrottle` 同源)。
2. **接收端三档驱动**(`EngineVisualDriver.DriveDirectly`):
   - **`EngineScript`(基础液体)** → `DriveDirectly=false`:Route A,游戏自身 `IFlightUpdate` 每帧**无条件**调 `EngineCommon.FlightUpdate`,经 override 驱动;MP 层不重复调(避免 `_textureShiftSpeed` 双倍滚动)。
   - **`RocketEngineScript`** → `DriveDirectly=true`:**新增修正**——其 `IFlightUpdate.FlightUpdate` 被 `(Activated && throttle>0) || _hasBeenActivated` 门控,幽灵上 `AdjustedThrottle()==0` 时游戏**不调** `EngineCommon.FlightUpdate`,必须由 MP 层每帧直接调。
   - **`JetEngineScript`** → `DriveDirectly=true`:自身 `FlightFixedUpdate`(每 FixedUpdate 归零 `_afterburnerThrottle` 并 `UpdateExhaust(0)` 反打)与 `FlightUpdate` 均被 Harmony patch 跳过;MP 层每帧驱动 `_rocketExhaustSystem.UpdateExhaust(t)`(加力大尾焰)+ `EngineCommon.FlightUpdate(1f,1f)`(主喷嘴火焰)。
3. **幽灵副作用抑制**:`SetupGhostEngineVisuals` 把每部件下 `SmokeTrailScript` GO 置 inactive(烟雾先行,LateUpdate 不再发粒子)+ `ExhaustDamageScript.enabled=false`(防地形尘/加热)。
4. **顺序契约**:两端同 XML 构建 → parts/modifiers 顺序一致,index 一一对应;读取端越界兜底 0。
5. **平滑**:throttle 跟随现有插值缓冲,`ApplyRemoteState` 时快照进 `rc.SyncedThrottles`,override 闭包与驱动都读它。
6. **航发 patch 选目标**:显式接口实现的 `MethodInfo.Name` 是带接口前缀的 `"IFoo.Bar"`(**不是**简单名 `"Bar"`)——只比简单名会匹配不上,`TargetMethod()` 返回 null 导致 Harmony `Patching exception`(首次联机实测报错 `[MpTest] Init failed: HarmonyException ... TargetMethod() returned an unexpected result: null`)。已独立 dotnet 测试复现(显式接口实现 `Name='IFoo.Bar'`)。修正两处:
   - 用 `IsNamed`(简单名 或 `EndsWith(".方法名")`)匹配 + `GetMethods` 兜底;
   - **改用手动打补丁**:去掉 `[HarmonyPatch]` 自动发现,由 `Mod.OnModInitialized` 在 `PatchAll()` 后调 `JetEngineGhostPatch.Apply(harmony)`,目标方法找不到时只 `LogError` 降级(液体尾焰仍可用),不再抛异常打断整个 mod 初始化。
   (实测 `GetInterfaceMap(typeof(IFlightUpdate))` 只含自身方法,不把继承的 `IGameLoopItem` 成员放进 TargetMethods。)

### 9.3 已知留白 / 后续

- **烟雾拖尾**:幽灵 body 全 kinematic → `rigidbody.velocity≈0` → 烟雾粒子不拖尾;当前先禁用烟雾,后续按 §5.3 做 velocity 注入。
- **RocketEngine 视觉近似**:发送端真实火焰由 `AdjustedThrottle()`(含推力曲线/MinThrottle)驱动,采样用 `EngineThrottle`,二者通常相等,仅推力曲线/限流例外(可接受)。
- **`Data.Activated=true && HasFuel=false` 边角**:幽灵 `FlightFixedUpdate` 会调 `OnDeactivated()`→`UpdateExhaust(0)` 每 FixedUpdate 反打(幽灵油箱恒满、无消耗,实际几乎不会触发);若测试出现"火焰闪烁"再给液体补 `IFlightFixedUpdate` patch。
- **gimbal 尾焰朝向 / 热畸变 / RCS**:未做,见 §5.4/§8 排期。
