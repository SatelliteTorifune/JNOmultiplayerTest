# 同步发动机尾焰与粒子效果 — 可行性分析

> 项目:JNOmultiplayerTest(aMptest)
> 反编译参考:`C:/renko/shitProgram/jnoCode`
> 状态:**✅ 已归档**。尾焰(液体+航发两段加力)、烟雾(速度注入)、过膨胀(膨胀比)同步均已实现并实测通过(2026-08)。本文档为开发经验存档,细节以代码内注释为准。
> 定位:~~[`multi-craft-sync.md`](../multi-craft-sync.md) 的补充分析~~ → 已由「尾焰/烟雾/过膨胀同步」实测闭环,移入 `plans/archive/`。回答"幽灵船的引擎尾焰/烟雾/热畸变能否同步、怎么同步、代价多大"
> 结论先行:**火焰(尾焰)可同步且成本几乎为零;烟雾/热畸变/RCS 只能做"输入同步 + 本地仿真"(形态一致、非逐粒子一致);粒子级精确同步不可行;幽灵重开物理不可取。**

---

## 〇、经验教训(归档修订)

> 本文方案已按 §3~§10 落地(尾焰 Route A/B、烟雾 `InjectGhostMotion`、过膨胀 `ExpansionRatio` 同步),归档为开发经验记录。

**经验教训**:

1. **「无副作用」要分面看**:写 kinematic 刚体的 `velocity` 对**物理**无副作用(不积分),但 Unity 会对**每次 setter 调用打告警**——幽灵全 kinematic + 每帧每 body 写一次会刷爆 `Player.log`(单会话 ~1.3M 条)。修法:值变化阈值门控 + 写入时临时 `isKinematic=false`→写→改回(调用点在 `Update`、物理步在帧末,刚体不会被真正积分),见 §10.3.1。
2. **幽灵引擎的"每帧更新"路径会静默失效**,且各有各的失效方式:
   - 航发:`JetEngineGhostPatch` 跳过 `FlightFixedUpdate`/`FlightUpdate` → 膨胀比(过膨胀)冻结;
   - 液体火箭:`UpdateExhaustExpansionRatio` 被 `Data.Activated` 门控,幽灵上 false → 永不更新。
   - 排查要**反编译定位"这个视觉量由哪个方法、在什么门控下更新"**,而不是只驱动 throttle。
3. **让游戏自己算最稳**:火箭过膨胀最终用「幽灵引擎置 `Data.Activated=true`」让游戏自身的 `UpdateExhaustExpansionRatio` 跑(物理已禁用 → 不会真点火/耗燃料),再以公式写入作冗余兜底——不依赖反射/找对象。
4. **`AltitudeCompensation>0` 的高度补偿引擎 `ExitPressure` 会被置 0**,本来就不该有过膨胀,不是 bug。
5. **调试日志要节流 + 带删除标记**:过膨胀验证阶段加了 Setup/每 5s 的 `MP engineVisual ...` 日志,验证通过后已删除。
6. **注意幽灵大气压** `CraftScript.AtmosphereSample.AirPressure`(结构体,不可判空;由 CraftFlightData 按位置刷新)——所有大气相关视觉(膨胀比/烟雾/热畸变)都依赖它正确。
7. **加力/普通节流阀不能混绑**:发送端航发可见尾焰 = `_afterburnerThrottle`(JetEngineScript 的 `ExhaustThrottleOverride = () => _afterburnerThrottle`),非加力段(油门<afterburnerThrottleStart)本就无尾焰;若接收端写成 `flame = ab>0 ? Lerp(t,1,ab) : t`(t=普通节流阀)会让幽灵"该熄火时出火、加力段偏亮"(加力/普通节流阀绑定错误)。正确做法:只绑 `ab = Clamp01((t-start)/(1-start))`(EngineVisualSync.ComputeAfterburnerThrottle)。

**✅ 实测完成(2026-08,用户确认)**:航发(非加力+加力两段)与液体火箭的尾焰、拖尾、过膨胀在联机中与发送端一致;诊断日志已移除,`dotnet build` exit 0 / 0 警告 / 0 错误。

---

## 0. 现状(plan 已认定的限制)

- [`multi-craft-sync.md`](../multi-craft-sync.md) 8.2-5 决策:**燃料/资源/部件状态 MVP 不同步**,并记为"幽灵物理关 → 引擎视觉本来不跑"。
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

**航发尾焰(`JetEngineScript`):两段 —— 非加力 + 加力,必须先中和它自己的 `FlightFixedUpdate`**
- 幽灵的 JetEngineScript `FlightFixedUpdate` 每 FixedUpdate:性能分支失败 → `_afterburnerThrottle=0`([JetEngineScript.cs:401](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/Propulsion/JetEngineScript.cs:401))+ `_rocketExhaustSystem.UpdateExhaust(0)`([JetEngineScript.cs:441](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/Propulsion/JetEngineScript.cs:441))→ **每帧反打**,不能只注入 `_afterburnerThrottle`(会被重置);
- 做法:用 Harmony prefix 跳过幽灵 JetEngineScript 的 `IFlightFixedUpdate`(与 8.1-3 拦总入口同套路),然后 mod 每帧直接驱动:
  - **非加力段**(主喷嘴火焰/烟雾门控)= 同步 `EngineThrottle`,经 `ExhaustThrottleOverride=()=>sync[i]` + `EngineCommon.FlightUpdate(1f,1f)` 驱动(主 nozzle 的 ExhaustSystemScript);
  - **加力段** = 接收端用幽灵自身 `JetEngineData`(`hasAfterburner`/`afterburnerThrottleStart`,双端同 XML)从同步 `EngineThrottle` 推导
    `ab = Clamp01((t-start)/(1-start))`,再以 `flame = ab>0 ? Lerp(t,1,ab) :  t` 作为**最后一笔**写入同一 `ExhaustSystemScript`(它与主 nozzle 的 exhaust 是同一对象:`Nozzle/ExhaustSystem`;发送端 `_afterburnerThrottle` 正是这个式子)。
- **发送端采样**:jet 用 `_engineCommon.EngineThrottle`(非加力段;加力段接收端本地推导,不额外占带宽);液体用 `_engineCommon.EngineThrottle`。
- **库存部件事实**(自游戏资源 resources.assets 提取):Whiplash `hasAfterburner=true`、`afterburnerThrottleStart` 默认 0.8(另一 0.6+LOX 变体);Wheesley/Goliath 无 `hasAfterburner`(非加力段火焰也要显示,驱动随同步 throttle 即可)。

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
   - 解决:每帧把同步 `recdata.Velocity`(转帧空间)写入幽灵 kinematic rigidbody 的 `velocity`,`SmokeTrailScript.LateUpdate` 即会发出正确拖尾。
     ⚠️ 写入方式有讲究:Unity 对 kinematic 刚体写 velocity 每次打告警,曾刷爆 Player.log —— 2026-08 已修(值不变跳过 + 临时切非 kinematic),详见 §10.3.1。
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

1. ~~幽灵引擎 modifier 是否真的收到 `IFlightUpdate`?~~ **✅ 已定论:收到**(见 §3.5 证据链)。液体走 Route A;jet 需先中和其 `FlightFixedUpdate`(见 §3.6)。仍建议首次联机时加一行打点,确认与静态结论一致。
2. **`ExhaustDamageScript` 残留行为**(地形尘/加热)需禁用后回归验证。
3. **`smokeOpacity` / `light` 输入在幽灵上陈旧**(读 `AtmosphereSample`/`FlightData`)→ 烟雾透明度/亮度轻微差异,可接受或显式覆盖。
4. **加力喷气**:JetEngine 的 `ExhaustThrottleOverride` 已被加力逻辑占用 → 幽灵上覆盖为同步**非加力段** `EngineThrottle`,加力段由接收端本地推导并作最后一笔写入(见 §3.6/§9.2-2),正确。
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

已按"液体尾焰 + 航发尾焰(非加力 + 加力两段)"实现(2025 落地,见代码内注释 `plans/archive/engine-fx-sync-feasibility.md §3.5/§3.6`):

### 9.1 已实现文件

| 文件 | 改动 |
|---|---|
| [`Mod.cs`](../Assets/Scripts/Mod.cs) | `recdata` 增加 `List<float> EngineThrottles`(每引擎视觉 throttle)+ 构造初始化 |
| [`MpMessage.cs`](../Assets/Scripts/Net/MpMessage.cs) | `WriteRecdata`/`ReadRecdata` 追加 count+N 个 float |
| [`EngineVisualSync.cs`](../Assets/Scripts/Net/EngineVisualSync.cs) | 新增:发送端采样、幽灵驱动表、反射访问器 |
| [`MpNetworkManager.cs`](../Assets/Scripts/Net/MpNetworkManager.cs) | `RemoteCraft` 改 internal + `SyncedThrottles`/`EngineDrivers`;采样/设置/每帧驱动接入;`IsRemoteCraftNode` |
| [`JetEngineGhostPatch.cs`](../Assets/Scripts/HarmonyPatches/JetEngineGhostPatch.cs) | 新增:幽灵航发跳过 `IFlightFixedUpdate`/`IFlightUpdate`(手动 `Apply` 打补丁,无日志) |

### 9.2 实现要点(含与 §3.6 的修正)

1. **发送端采样**(`SampleEngineThrottles`):确定顺序 = `Data.Assembly.Parts` → 每部件 `Modifiers`;液体与航发都取 `_engineCommon.EngineThrottle`。航发的**加力段不单独占带宽**,接收端用幽灵自身 `JetEngineData`(`hasAfterburner`/`afterburnerThrottleStart`,双端同 XML)从该值推导。
2. **接收端三档驱动**(`EngineVisualDriver.DriveDirectly`):
   - **`EngineScript`(基础液体)** → `DriveDirectly=false`:Route A,游戏自身 `IFlightUpdate` 每帧**无条件**调 `EngineCommon.FlightUpdate`,经 override 驱动;MP 层不重复调(避免 `_textureShiftSpeed` 双倍滚动)。
   - **`RocketEngineScript`** → `DriveDirectly=true`:**新增修正**——其 `IFlightUpdate.FlightUpdate` 被 `(Activated && throttle>0) || _hasBeenActivated` 门控,幽灵上 `AdjustedThrottle()==0` 时游戏**不调** `EngineCommon.FlightUpdate`,必须由 MP 层每帧直接调。
   - **`JetEngineScript`** → `DriveDirectly=true`:**航发尾焰分两段**——自身 `FlightFixedUpdate`(每 FixedUpdate 归零 `_afterburnerThrottle` 并 `UpdateExhaust(0)` 反打)与 `FlightUpdate` 均被 Harmony patch 跳过;MP 层先 `EngineCommon.FlightUpdate(1f,1f)`(override=同步非加力值 → 主喷嘴火焰/烟雾门控),再以加力 boost 值(本地推导)`UpdateExhaust(flame)` 作最后一笔写入同一 `ExhaustSystemScript`(它与主 nozzle 的 exhaust 是同一对象 `Nozzle/ExhaustSystem`)。修正前只同步 `_afterburnerThrottle` → 非加力态(油门<afterburnerThrottleStart)幽灵无火焰无烟,已修。
   - **【修正记录 2026-08:加力/普通节流阀绑定错误】**:旧实现把"普通节流阀 t"与"加力 boost ab"用 `flame = ab>0 ? Lerp(t,1,ab) : t` 绑定写入同一 `ExhaustSystemScript`——但发送端可见尾焰只由 `_afterburnerThrottle = ab` 决定(`ExhaustThrottleOverride = () => _afterburnerThrottle`,见 §3.6/§9.2-2),即**非加力段(油门<AfterburnerThrottleStart)发送端本就无尾焰、加力段亮度 = ab**,不是 t 与 ab 的 Lerp。修法:幽灵 override 与最后一笔都只写 `ComputeAfterburnerThrottle` 推导的 ab(普通节流阀 t 只用于烟雾门控 `ApplyJetSmokeVisuals` / 膨胀比 `SyncJetExpansionRatio`);无加力引擎(HasAfterburner=false)恒无尾焰,与发送端一致。改动文件 `EngineVisualSync.cs`(`SetupGhostEngineVisuals` override + `DriveGhostEngineVisuals` 写入 + 新增 `ComputeAfterburnerThrottle`)。
   - **航发烟雾颜色 / SpeedOverride**(`ApplyJetSmokeVisuals`,每帧补):发送端在 jet 自身 `FlightUpdate` 里设(加力→`_afterburnerSmokeColor` + `1.0×SmokeSpeed`;非加力→近白 `alpha=0.1×throttle` + `0.75×SmokeSpeed`;有自定义烟色 `TryGetSmokeColor` 时 RGB 取自定义;`EmissionEnabled=HasSmoke && throttle>0`),幽灵已被 patch 跳过 → 由 MP 层按同公式重写 `SmokeTrailScript.Color/SpeedOverride/EmissionEnabled/Throttle`。Setup 时把 `HasSmoke`/`SmokeSpeed`/自定义烟色/`_afterburnerSmokeColor`(反射)缓存进驱动表。
3. **幽灵副作用抑制**:`SetupGhostEngineVisuals` 把每部件下 `ExhaustDamageScript.enabled=false`(防地形尘/加热)。**烟雾不再禁用**:`SmokeTrailScript` GO 保持 active,拖尾由 `InjectGhostMotion` 注入的 rigidbody.velocity 驱动(见 §10)。
4. **顺序契约**:两端同 XML 构建 → parts/modifiers 顺序一致,index 一一对应;读取端越界兜底 0。
5. **平滑**:throttle 跟随现有插值缓冲,`ApplyRemoteState` 时快照进 `rc.SyncedThrottles`,override 闭包与驱动都读它。
6. **航发 patch 选目标**:显式接口实现的 `MethodInfo.Name` 是带接口前缀的 `"IFoo.Bar"`(**不是**简单名 `"Bar"`)——只比简单名会匹配不上,`TargetMethod()` 返回 null 导致 Harmony `Patching exception`(首次联机实测报错 `[MpTest] Init failed: HarmonyException ... TargetMethod() returned an unexpected result: null`)。已独立 dotnet 测试复现(显式接口实现 `Name='IFoo.Bar'`)。修正两处:
   - 用 `IsNamed`(简单名 或 `EndsWith(".方法名")`)匹配 + `GetMethods` 兜底;
   - **改用手动打补丁**:去掉 `[HarmonyPatch]` 自动发现,由 `Mod.OnModInitialized` 在 `PatchAll()` 后调 `JetEngineGhostPatch.Apply(harmony)`,目标方法找不到时静默跳过(液体尾焰仍可用),不再抛异常打断整个 mod 初始化。
   (实测 `GetInterfaceMap(typeof(IFlightUpdate))` 只含自身方法,不把继承的 `IGameLoopItem` 成员放进 TargetMethods。)
7. **过膨胀(尾焰膨胀比)同步**(✅ 已实现,2026-08):幽灵引擎的 `ExhaustSystemScript.ExpansionRatio` 更新路径全部失效——航发被 `JetEngineGhostPatch` 跳过(`JetEngineScript.FlightFixedUpdate` 的 `81060/pressure` 公式不跑),液体火箭被 `Data.Activated` 门控(`RocketEngineScript.FlightUpdate` 幽灵上不跑)→ 尾焰形状冻结(高空该膨胀不膨胀)。修法:`DriveGhostEngineVisuals` 每帧按发送端同式用**幽灵自身大气压**(`CraftScript.AtmosphereSample.AirPressure`,已由 CraftFlightData 按位置刷新)补算写入:
   - **航发**:`81060.0012 / max(1,p)` → clamp `[ExhaustExpansionRange.x, MaxExpansionRatio]`(MaxExpansionRatio 幽灵 FlightStart 已算);
   - **液体火箭**:`sqrt(ExitPressure/max(p,15)) × (1-0.85×AltComp)` → clamp `[ExhaustExpansionRange.x, .y]`。**双保险**:
     ①主机制 = Setup 时把幽灵引擎置 `Data.Activated=true`(物理已禁用 → `OnActivated` 不会真正点火/耗燃料,只让游戏自身 `FlightUpdate` 里的 `UpdateExhaustExpansionRatio` 每帧按幽灵大气压跑,用**真实** `_params.Dynamic.ExitPressure`);②冗余兜底 = `SyncRocketExpansionRatio` 按同式写入(`ExitPressure` 反射自 `_params.Dynamic.ExitPressure`,Setup 时缓存)。两处值同源,幂等。
   - 注意:`AltitudeCompensation>0` 的高度补偿引擎 `CalculateStaticPerformance` 会把 `ExitPressure` 置 0 → 本来就不该过膨胀(与真实一致,不是 bug)。
   - 诊断:验证阶段加了 Setup 一条 + `SyncRocketExpansionRatio` 每 5s 一条的 `MP engineVisual ...` 日志;实测通过(2026-08)后**已删除**。
   写入时机在 `EngineCommon.FlightUpdate`(主喷嘴)与 `UpdateExhaust`(加力段)之前,熄火跳过。改动文件 `EngineVisualSync.cs`(`EngineVisualDriver` + `SetupGhostEngineVisuals` + `DriveGhostEngineVisuals` + `SyncJetExpansionRatio`/`SyncRocketExpansionRatio`/`GetRocketExitPressure`)。

### 9.3 已知留白 / 后续

- **烟雾拖尾**:✅ 已实现(§10.3/§10.5)——`InjectGhostMotion` 逐帧向幽灵 kinematic 刚体注入同步速度+角速度,烟雾 GO 不再禁用。剩余可选项:发射率精确化(§10.4-2)、远处 LOD。
- **RocketEngine 视觉近似**:发送端真实火焰由 `AdjustedThrottle()`(含推力曲线/MinThrottle)驱动,采样用 `EngineThrottle`,二者通常相等,仅推力曲线/限流例外(可接受)。
- **`Data.Activated=true && HasFuel=false` 边角**:幽灵 `FlightFixedUpdate` 会调 `OnDeactivated()`→`UpdateExhaust(0)` 每 FixedUpdate 反打(幽灵油箱恒满、无消耗,实际几乎不会触发);若测试出现"火焰闪烁"再给液体补 `IFlightFixedUpdate` patch。
- **gimbal 尾焰朝向 / 热畸变 / RCS**:未做,见 §5.4/§8 排期。

---

## 10. 引擎烟雾粒子同步(分析与设计)

> 定位:尾焰 MVP(§9)之后的第一优先级增强。沿用"**输入同步 + 本地仿真**"结论(§4/§8):
> 烟雾不逐粒子传输,把**驱动烟雾的输入**(throttle、速度、朝向、大气)同步过来,由接收端游戏自身的
> `SmokeTrailScript` 本地发粒子 → 形态一致、带宽≈0(只多算一次速度注入)。

### 10.1 反编译定论:烟雾拖尾到底依赖什么

`SmokeTrailScript` 的发射链,关键两处:

1. **`FlightUpdate(surfaceVelocity)`([SmokeTrailScript.cs:149](file:///C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/SmokeTrailScript.cs:149))**,第 171 行:
   `_smoothedCraftVelocity = rigidBody.velocity + Cross(rigidBody.angularVelocity, offset)`
   —— **用的是刚体 velocity,不是传入的 `surfaceVelocity` 参数**。
2. **`LateUpdate`(发射)([SmokeTrailScript.cs:186](file:///C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/SmokeTrailScript.cs:186))**:
   - 第 217 行 `vector3 = rigidBody.velocity` → 发射率相对速度、粒子位置回插(第 265 行)都靠它;
   - 第 213 行 `emissionFrame.Velocity = _smoothedCraftVelocity + 排气方向*(maxParticleSpeed*Throttle*SpeedOverride)`;
   - 第 268 行粒子速度 = `vector4(-FrameSurfaceVelocity) + (vector6-vector4)*exp(-AirDensity*dt)`(向空气帧拖拽衰减)。
   - 结论:粒子**速度/位置插值/发射率**全部读 `rigidBody.velocity`。

**幽灵上的后果**:全 kinematic + `RecalculateFrameState` 只对非 kinematic 累加速度([CraftUtils.cs:63](file:///C:/renko/unityProjects/JNOmultiplayerTest/Assets/Scripts/CraftUtils.cs:63))
→ `rigidbody.velocity≈0` → 即使放开烟雾,粒子也在喷嘴原地堆积成一坨,不拖尾。**必须注入速度。**

### 10.2 其它烟雾输入:当前已就绪(无需额外同步)

| 输入 | 幽灵当前状态 | 说明 |
|---|---|---|
| `EmissionEnabled` / `Throttle` / `Intensity` | ✅ 正确 | 由 `EngineNozzleScript.FlightUpdate` 按同步 throttle 设置(尾焰驱动链已带出) |
| `EmissionOpacity` | ✅ 正确 | `EngineCommon.FlightUpdate(smokeOpacity=1,…)` × 大气密度 |
| `AtmosphereSample`(AirDensity 门槛/透明度) | ✅ 正确 | `CraftFlightData.Update` 每帧按当前位置 `SampleAltitude` 刷新([CraftFlightData.cs:570](file:///C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/FlightData/CraftFlightData.cs:570)) |
| `ExhaustSystemScript.ExpansionRatio`(烟雾门槛 num>5) | ✅ 正确 | 由 `UpdateExhaust` 从幽灵自身大气/throttle 计算 |
| `ExhaustDamageScript`(地形尘/加热) | ✅ 已禁用 | §9 已处理 |
| **`rigidBody.velocity`(拖尾)** | ❌ ≈0(写入会触发 Unity kinematic velocity 告警,见 §10.3.1) | **唯一缺口,见 §10.3** |

### 10.3 设计:逐帧注入同步速度(核心,已实现)

在 `ApplyRemoteState` 里(已有 `data`/`planet`/`frame` 上下文)调 `EngineVisualSync.InjectGhostMotion(rc, data, planet, frame, headingFrame)`,
对每个幽灵 kinematic body 写:

```
frameVel = frame.PlanetToFrameVelocity( planet.SurfaceVectorToPlanetVector(data.Velocity) )
needWrite = 速度/角速度相比上次注入变化超过阈值        # 2026-08:值不变则跳过(见 §10.3.1)
if needWrite:
    foreach body in ghost.Assembly.Bodies:
        if body.RigidBody.isKinematic:
            body.RigidBody.isKinematic = false         # 2026-08:临时切回非 kinematic,消除 Unity 告警
            body.RigidBody.velocity = frameVel
            body.RigidBody.angularVelocity = (本次朝向 - 上次朝向) 的轴角 / 帧时长   # §10.4-1
            body.RigidBody.isKinematic = true
```

- **空间自洽**:发送端 `recdata.Velocity = PlanetVectorToSurfaceVector(craft.Velocity)`([MpNetworkManager.cs:1891](file:///C:/renko/unityProjects/JNOmultiplayerTest/Assets/Scripts/Net/MpNetworkManager.cs:1891)),
  而发送端 `rigidBody.velocity` 是帧相对速度;`PlanetToFrameVelocity` 把行星空间速度转成接收端帧相对速度
  (与发送端同语义;`FrameSurfaceVelocity` 双端同为帧表面速度,`vector4` 相互抵消)。
- **角速度**:用 `rc.LastAppliedHeading`(上一次应用的帧空间朝向)与本次 `headingFrame` 之差,
  短路径轴角 `headingFrame * Inverse(prev)` → `axis * (angle_rad / Time.deltaTime)`。
  **调用点约束**:必须在 `ApplyRemoteState` 里、写 `rc.LastAppliedHeading = headingFrame` **之前**调用,才能读到上一次朝向。
- **无副作用**:kinematic 刚体不把 velocity/angularVelocity 积分进位置;幽灵摆放走 `GroundedSurface*` + `SetStateVectors`(mod 每帧写),
  不读 rigidbody.velocity → 不会双重移动。附带好处:幽灵 `MachNumber`(读 velocity)也随之正确。
  ⚠️ 这里的"无副作用"仅指**物理面**;**日志面**有例外(Unity 对 kinematic 写 velocity 打告警、曾刷爆 Player.log),见 §10.3.1。
- **烟雾放开**:`SetupGhostEngineVisuals` 不再把 `SmokeTrailScript` GO `SetActive(false)`
  (飞行场景下默认 active;发射由 `EmissionEnabled=throttle>0` 门控,熄火无烟)。
- **抽帧/带宽**:无需新增字段,直接用现有 `recdata.Velocity`。
- **时序**:MpNetworkManager `DefaultExecutionOrder(1000)`,`InjectGhostMotion` 在 Update 末尾写入,`SmokeTrailScript.LateUpdate`
  在其后发射 → 当帧生效。EngineScript 走游戏 Route A(顺序 0)时 `_smoothedCraftVelocity` 滞后一帧(≈16ms),不可感知。

### 10.3.1 修正记录(2026-08):Unity kinematic velocity 告警刷屏

§10.3「无副作用」只讲了**物理面**(kinematic 刚体不积分 velocity、不移动位置)——这没错,但漏了**日志面**:
Unity 2022.3 对 kinematic 刚体**每次**写 `Rigidbody.velocity` / `angularVelocity` 都会打告警
`Setting linear velocity of a kinematic body is not supported.`(及 angular 版)。

- **现象**:幽灵船全 kinematic + 每帧每 body 写一次 → `Player.log` 单会话刷出 **~131.98 万条 linear + 15.2 万条 angular**,占日志 **99.8%**(1,474,400 行里只有 2,813 行非告警)。
- **根因链路**:`InitializeRemoteCraft` 把所有 body 设 `isKinematic=true` → `UpdateRemoteCrafts` 每帧 `ApplyRemoteState` → `InjectGhostMotion` 特意只写 kinematic body(`if (!rb.isKinematic) continue;`)→ 每帧每 body 触发 Unity 告警,从首个幽灵初始化一路刷到退出。
- **修法**(2026-08,已 `dotnet build aMptest.csproj` 编译通过 exit 0 / 0 警告 / 0 错误):
  1. **值不变则跳过**:`RemoteCraft` 新增 `LastInjectedVelocity` / `LastInjectedAngularVelocity` 缓存;线性变化阈值 ~0.1 m/s、角速度 ~0.03 rad/s → 平稳飞行时几乎零写入,点火/变向时才写;
  2. **写入时临时切回非 kinematic**:`rb.isKinematic=false → 写 velocity/angularVelocity → rb.isKinematic=true`。所有调用点都在 `Update`(物理步在帧末),刚体永远不会被真正积分;velocity 数据照常存储,`SmokeTrailScript` 读 `rigidbody.velocity` 不受影响,物理行为不变。
- **改动文件**:`Assets/Scripts/Net/EngineVisualSync.cs`(`InjectGhostMotion`)、`Assets/Scripts/Net/MpNetworkManager.cs`(`RemoteCraft` 缓存字段)。
- **回归注意**:修的是"写入方式",不改变发送/插值/烟雾逻辑;验证时除看日志归零外,还要确认幽灵船尾焰拖尾与翻滚烟迹仍正常——那才是注入还活着的证据。

### 10.4 可选精修(按需)

1. **角速度注入**:`_smoothedCraftVelocity` 还含 `Cross(angularVelocity, offset)`(翻滚时排气口切向速度)。
   从每帧朝向旋转增量算 `rigidBody.angularVelocity` 写入(物理面无副作用;日志面告警按 §10.3.1 的临时切非 kinematic 处理);不做则翻滚中的幽灵烟迹略"呆"。
2. **发射率精确化**:`num5` 用 `FlightData.SurfaceVelocityFrame`([CraftFlightData.cs:582](file:///C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/FlightData/CraftFlightData.cs:582))
   = `_craftScript.FrameVelocity + FrameSurfaceVelocity`,而幽灵 `FrameVelocity` 可能陈旧 → 发射率偏差。
   可反射写 `CraftScript._frameVelocity` 或用 `num5` 兜底(`_smoothedCraftVelocity.magnitude`)。
   不做也能出正确拖尾,只是发射密度略差。
3. **LOD/预算**:远距离幽灵跳过烟雾注入或直接维持禁用(游戏自带 `ParticleCategory.EngineSmoke` 全局预算自动限流)。

### 10.5 排期与验收

- **① 速度注入 + 放开烟雾**(✅ 已实现):改动在 `EngineVisualSync`(`SetupGhostEngineVisuals` 不再禁用烟雾 GO + 新增 `InjectGhostMotion`)与 `ApplyRemoteState`(每帧调注入)。验收:两架带液体发动机的火箭,油门推满起飞 → 接收端看到尾焰 + 拉出正确拖尾;熄火 → 无烟;高空 → 无烟(大气门槛生效)。
- **② 角速度注入**(✅ 已实现,§10.4-1):`InjectGhostMotion` 从"最近两次应用的帧空间朝向之差"算 `rigidbody.angularVelocity` 注入(短路径轴角/帧时长),覆盖翻滚烟迹(`Cross(angularVelocity, offset)` 项)。验收:翻滚的幽灵火箭烟迹应带螺旋轨迹。
- **② 发射率 / LOD**(可选,未做):见 §10.4-2/3。
- **③ RCS / 热畸变**:仍按 §8 排期(独立于烟雾)。
