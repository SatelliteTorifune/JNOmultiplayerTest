# Craft 间碰撞可行性(craft-collision)— 幽灵墙方案 + 游戏碰撞链路

> 状态:📋 **方案研究完成,未实施**(2026-09-26 通读反编译碰撞链路 + mod 幽灵管线后成文;结论与工期见 §三/§六,实施与否待拍板)
> 日期:2026-09-26
> 关联:[`physics-sync-2026-09-14.md`](physics-sync-2026-09-14.md)(SP2 真实刚体路线已被其 §2.3 否决;本方案 = kinematic 路线的碰撞特化,结论一致);[`multi-craft-sync-2026-08-16.md`](multi-craft-sync-2026-08-16.md)(结构级同步 = 多 craft 身份层;本方案 v1 明确不依赖);[`remote-craft-velocity-2026-09-13.md`](../remote-craft-velocity-2026-09-13.md)(已知问题 #10 不影响碰撞伤害的依据)
> 主题:回答「联机里能不能让飞船互相撞」——游戏碰撞链路怎么走、幽灵现在为什么不撞、最小可行方案(L2「幽灵墙」)、以及必须封堵的坑

---

## 〇、一句话结论

**能做,且有一条零协议改动、零平滑管线改动的最小路径(L2「幽灵墙」)**:幽灵保持 kinematic + 物理禁用,只把**非 trigger 碰撞体重开**;本机船(dynamic)撞幽灵 = 撞"无限质量的墙",伤害/断裂/爆炸由游戏原生 `BodyCollisionHandler` 管线产出——**每台机器只处理自己船的损伤,双端各自模拟同一次接触,结果近似对称**(A 机上 A 撞 B 的幽灵墙,B 机上 B 被 A 的幽灵墙撞)。真正的工作量不在"开碰撞",而在**封堵幽灵侧的碰撞副作用**(幽灵被撞解体、对接口 trigger 把幽灵并进本机船)。工期约 3~5 个工作日;完全的动量守恒(SP2 式真实刚体 / 冲量回传)前者已被 physics-sync 否决、后者不必要,均不建议作为第一步。

---

## 一、现状:为什么现在互不碰撞

幽灵三件套(`Net/Sync/RemoteCraftManager.cs` `InitializeRemoteCraft` + `Assets/Scripts/CraftUtils.cs` `DisableCraftPhysicCalculation`):

1. `SetPhysicsEnabled(false, Warp)` — 停重力/物理推进(MapView NRE 原因见 plans/README §三);
2. `DisableCraftPhysicCalculation` — **所有 `Collider.enabled = false`**(另有 `PartScript.Colliders` 列表清空、`Damage`/`HeatShield` 拉满、`PreventDebris`、`IncludeInDrag=false`);
3. 全 body `RigidBody.isKinematic = true` + `InContactWithPlanet = true` 每帧重申。

效果:本机船从幽灵身体里穿过(连接触事件都不产生),幽灵也不可能有任何损伤反应——README 已知限制「无双向碰撞物理(靠 kinetic 视觉)」即此。唯一注入幽灵刚体的数据是 `Net/CraftVisual/EngineVisualSync.cs` `InjectGhostMotion`(速度/角速度,本意喂烟雾拖尾)——这份速度恰好就是碰撞所需的 `Collision.relativeVelocity` 输入(§2.5)。

## 二、游戏侧碰撞机制(反编译核实;行号 = 反编译源)

### 2.1 层与矩阵

- 部件/body 全部 **layer 31**(`CraftBuilder.cs:215,249,381`);地形 = 层 29/30(`ModApi/Masks.cs:33`,`603979776 = 0x24000000`)。
- 游戏原生两船相撞互伤:非地面碰撞分支、`OtherPartScript` 解析、对接口 trigger 邻近检测都是 part↔part 设计,层矩阵 31↔31 互撞。**开工前单机两船对撞复核一次**(§七)。

### 2.2 碰撞处理链

`BodyScript.OnCollisionEnter/Stay`(`BodyScript.cs:714/720`)→ `BodyCollisionHandler.OnCollisionEnter`(`BodyCollisionHandler.cs:224`)逐接触对构造 `PartFlightCollision`(`PartFlightCollision.cs:15-30`):

- `Impulse = collision.impulse × Clamp01(relVel/15)`;
- `RelativeVelocity` = Unity `Collision.relativeVelocity`;
- `IsGroundCollision` 按层掩码判定;**非地面时解析 `OtherPartScript`**(= 船撞船);

→ `HandleCollision`(`BodyCollisionHandler.cs:972`):

1. 先把碰撞发给**所有部件 modifier** 的 `OnCollision`(轮子/起落架/伞等,返回 true 则吃掉);
2. 全局闸 `BodyCollisionsEnabled`(静态;飞行场景 Start 置 false 出生无敌,`FlightSceneScript.cs:1001`,计时后置 true,`:518`;DevConsole IDDQD 可切);
3. `GetCollisionResponse`(`:355`):**非地面(船撞船)只按冲量阈值**(部件配置 `CollisionDisconnectImpulse`/`CollisionExplodeImpulse` × 质量尺度)→ Damage / Disconnect / Explode;
4. 伤害公式 `HandleDamage`(`:1167`):`TakeDamage(Lerp(0,100, 1.5×impulse/(CollisionExplodeImpulse×massScale)−0.5))`;
5. Disconnect/Explode → `DisconnectParts`(`:572`)孤岛分 body、`CreateBodyScript`(`:499`)→ **结构变更**(与撞地形同一机制,拆 body + 触发 CraftSplitter)。

**特效白送**:擦碰火花 `EmitSlidingSparks`(`:782`,非地形且 relVel>10m/s 出火花)、碰撞音 `PlayCollisionSound`;`CraftScript.OnPartCollisionEnter` → 公开事件 `PartCollisionEnter`(`CraftScript.cs:1281`,**无需 Harmony 即可订阅本机遥测**)。

### 2.3 kinematic 语义(方案基石,Unity 规则)

| 组合 | 接触/事件 | 对方案的意义 |
|---|---|---|
| dynamic(本机船)↔ kinematic(幽灵) | **双方都收 OnCollision*** | 双端各自产生接触;但幽灵侧也收 → 幽灵会被原生管线处理 → **必须封**(§五.1) |
| kinematic ↔ static(地形) | 无事件 | 重开幽灵碰撞体**不会**触发幽灵的地形碰撞 / `InContactWithPlanet` 副作用 |
| kinematic ↔ kinematic(两艘幽灵) | 无接触 | 幽灵互穿,仅旁观者视角失真(§五.4) |

### 2.4 对接是 trigger(最危险旁路)

`DockingColliderScript.OnTriggerEnter`(`DockingColliderScript.cs:10`)→ `DockingPortScript.OnTouchDockingPort` → `CraftSplitter.MergeCraftNode`。幽灵碰撞体重开后,本机对接口 trigger 碰到幽灵对接口 collider 即触发**并船尝试** → 幽灵节点被 DestroyCraft → 登记表 / 状态包错乱。**必须封**(§五.2)。

### 2.5 幽灵速度已就位

`InjectGhostMotion`(`Assets/Scripts/Net/CraftVisual/EngineVisualSync.cs:500`)把帧相对速度/角速度写进幽灵全部 kinematic 刚体(写法:临时切非 kinematic 写回,防告警刷屏),语义与本机船 `rigidbody.velocity` 完全一致。因此 **`Collision.relativeVelocity` 与 `collision.impulse` 对幽灵碰撞天然正确**(plans/README §八 #10 的注记即此:碰撞伤害不受该已知问题影响)。注意:目前写的是**整船统一速度**(旋翼叶片拿不到叶尖速度);协议已有每 body `BodyVelocities/BodyAngularVelocities`(rotating-body-sync),可选用(§四.5)。

### 2.6 轮子不认幽灵

轮子物理 raycast 只打地形层(`ResizableWheelCollider.cs:752`,mask=603979776)→ 本机船轮子不会"压上"幽灵,无额外坑。

## 三、方案分级与推荐

| 级 | 内容 | 依赖 | 评价 |
|---|---|---|---|
| L1 纯视觉 | 幽灵碰撞体 trigger 化 + 自写特效 | 无 | 不推荐单独做:原生特效/音效/伤害走同一条管线,做真碰撞全部白送 |
| **L2 幽灵墙(推荐 v1)** | 幽灵重开**非 trigger** 碰撞体(其余不变:kinematic、物理禁用、免伤);Harmony 封幽灵侧碰撞处理 + 对接 trigger | 零协议改动、零平滑改动 | 3~5 天;每台机器只伤自己的船(所有权权威,与今天"撞地形解体不同步"同级);双端近似对称 |
| L3 伤害/冲量同步 | 尾追加 `PartDamages`(EOF 容错,同 `Paused`/`BodyIds` 模式)同步损伤视觉;可选冲量回传 owner 修正动量 | L2 + 协议尾部追加 | 视觉一致性/公平性增量;冲量回传引入排序与作弊面,可选 |
| L4 SP2 真实刚体 | 远程船非 kinematic + 全游戏力隔离 | 全仓库级改动 | **physics-sync §2.3 已否决**(重开抽搐/暂停/慢放全套已修问题),碰撞不需要它 |
| 结构级同步 | 撞完解体各自成船、双端一致 | **multi-craft 身份层(Guid/多船)** | v1 明确不做:本机船解体今天同样不同步;**幽灵侧**解体才致命,所以 v1 封的是幽灵侧 |

## 四、L2 改动点(按文件)

1. **`Assets/Scripts/CraftUtils.cs` / `Net/Sync/RemoteCraftManager.cs`**:`InitializeRemoteCraft` 增加"碰撞模式"分支——`DisableCraftPhysicCalculation` 跳过(或回滚)collider 禁用,**只重开非 trigger 碰撞体**;建议禁用前快照每个 `Collider.enabled`,重开按快照恢复(尊重游戏运行时管理的开关,如伞/部件激活态)。轮子碰撞体建议保持禁用(§五.7)。
2. **`Assets/Scripts/HarmonyPatches/GhostCollisionPatch.cs`(新)**:
   - prefix `BodyScript.OnCollisionEnter` / `OnCollisionStay`:`VizzyIsolationPatch.IsGhostCraft(part)`(**复用三层判定入口,契约见 plans/README §三,不要只查登记表**)→ return false 跳过。这一处同时挡住幽灵侧 `HandleCollision` 全链(modifier.OnCollision / 伤害 / 断裂 / 爆炸 / 断体音效)。
   - prefix `DockingColliderScript.OnTriggerEnter`:任一侧是幽灵 → return false。
3. **出生宽限(可选)**:幽灵生成后 N 秒(或与本机船距离 > 阈值)再重开碰撞体,防"幽灵生成在本机船体内 → 瞬间弹射"。
4. **设置开关(待拍板 C1)**:游戏内 UI 面板分组(plans/README §五 约定;新 patch 放 `HarmonyPatches/`);本机局部 vs 房主广播。
5. **可选增强**:`InjectGhostMotion` 改用协议已有的每 body `BodyVelocities/BodyAngularVelocities` 写刚体 → 旋翼叶尖碰撞相对速度正确。

## 五、风险与坑(必须处理 / 明确接受)

| # | 风险 | 证据 | 处理 |
|---|---|---|---|
| 1 | **幽灵被撞解体**(必须封) | 幽灵刚体 kinematic 也收 OnCollision*(§2.3);`GetCollisionResponse` 非地面冲量阈值 → Disconnect/Explode → `DisconnectParts`/`CreateBodyScript` 幽灵断 body、CraftSplitter 可能开新节点 → 登记表/状态包全乱 | GhostCollisionPatch 跳过幽灵侧 |
| 2 | **对接 trigger 并船**(必须封) | `DockingColliderScript.OnTriggerEnter` → merge → 幽灵节点 DestroyCraft | GhostCollisionPatch 跳过(双侧判定) |
| 3 | 无限质量失真 | kinematic = ∞ 质量 | 接受:双方都似撞墙,反弹偏猛、动量凭空增加;伤害≈撞固定墙(冲量偏大)。可选:碰撞相关伤害缩放补偿(C3) |
| 4 | 幽灵互穿 | kinematic×kinematic 无接触 | 接受:涉事两端的机器上该碰撞照常发生(各自 vs 对方幽灵),仅第三方旁观视角失真 |
| 5 | 高速穿透 | 幽灵位姿每渲染帧 transform 直写,无 CCD sweep | 接受并记录:>100m/s 闭合速度可能穿透薄部件;v1 不做 CCD |
| 6 | 出生重叠弹射 | 幽灵生成位置 = 对端真实位置 | 宽限期/距离阈值(§四.3) |
| 7 | 幽灵轮子碰撞体 | `ResizableWheelColliderScript.OnCollisionEnter` 是独立 MonoBehaviour,不走 BodyCollisionHandler,prefix 挡不住 | v1 幽灵侧轮子碰撞体保持禁用;要全功能则单独 patch |
| 8 | 双端接触几何不一致 | 各机外推误差(cm~m 级) | 接受:损伤结果可能一端断件另一端不断(分布式物理固有,与"本机坠毁不同步"同类) |
| 9 | 性能 | 1~4 艘幽灵碰撞体 broadphase | 可忽略(低于多模拟一艘本机船) |
| 10 | 作弊/grief | 幽灵墙可挡路/碰瓷 | 记录:伤害只落在撞击方自己的机器;房主可踢;作弊面与位置伪造相同,无新增 |

## 六、实施路径(建议,单人 3~5 天)

1. **D1**:开关 + 碰撞体重开(快照/跳 trigger/跳轮子)+ `GhostCollisionPatch`(封 §五.1/§五.2);`dotnet build` 0/0;
2. **D2**:VM 双机(**高延迟场景改用 frp / 真实网络;NetSim 已于 2026-09-26 整体移除**):正碰(双端各自受伤)/ 追尾(仅后船)/ 擦碰(原生火花+音效)/ 对接口对吻(**不得并船**)/ 出生重叠 / 200m/s 高速穿透观察;另单机两船对撞复核层矩阵假设(§2.1);
3. **D3(可选)**:每 body 速度注入(旋翼)+ 宽限期 + 冲量补偿调参;
4. **D4~5**:§七 回归 + 文档同步(plans/README 索引与 §四 事实、根 README 已知限制行;若加 UI 开关则同步 EN-US/ZH-CN 文案)。

## 七、回归判据(开工后验收)

**保持**:构建 0/0;平滑指标不回归(`bSpdAbs`/`lSpeed` 量级、gapEMA/jitterEMA 不劣化);幽灵视觉强制、Vizzy 隔离、MapView 无 NRE;暂停/慢放行为;`drift*=0` 结论不被破坏。

**新增**:

- 正碰:双端 FlightLog/`PartCollisionEnter` 各自记录损伤;追尾仅后船损伤;
- 擦碰:火花与碰撞音出现(原生管线,零额外代码);
- 对接口接触零并船(无 `CraftNodeRemoved`、`CraftNodes` 数不变);
- 幽灵零解体(`BodyRemapCount` 不涨、幽灵 body 数不变);
- 出生重叠不弹射(宽限生效);
- 关闭开关后行为与现状一致(回滚路径)。

## 八、关联文档与待拍板项

- **待拍板**:**C1** 开关语义(本机局部 vs 房主广播,建议房主广播);**C2** 幽灵轮子碰撞体(禁用 vs patch);**C3** 无限质量伤害补偿(不做 vs 缩放);**C4** 是否顺手做 L3 `PartDamages` 损伤视觉同步(建议另开主题);
- [`physics-sync-2026-09-14.md`](physics-sync-2026-09-14.md):L4 否决依据;`BodyVelocities/BodyAngularVelocities` 协议基础;
- [`multi-craft-sync-2026-08-16.md`](multi-craft-sync-2026-08-16.md):结构级同步(撞完多船)的身份层依赖;
- [`../archive/rotating-body-sync-2026-09-22.md`](../archive/rotating-body-sync-2026-09-22.md):每 body 速度/角速度协议;
- [`../archive/vizzy-isolation-2026-08-22.md`](../archive/vizzy-isolation-2026-08-22.md):`IsGhostCraft` 三层判定契约(新 patch 必须复用)。
