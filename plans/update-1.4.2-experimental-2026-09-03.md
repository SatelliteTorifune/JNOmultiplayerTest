# 游戏 1.4.2(Experimental 分支)兼容适配方案

> 项目:JNOMultiPlayer(SimpleRockets 2 / JNO 联机 mod MultiPlayer)
> 状态:**规划中 — P0 三项均未执行**(2026-09 代码复核确认:程序集未刷新、`GetComponentsInCraft`/`SetPose` 未采用、无版本检查;详见「〇之二、执行状态核对」)
> 触发:devb 发布 **1.4.2 Experimental 分支**(版本 1.4.200;当前游戏为 1.4.102),反编译对比已完成(新反编译目录 `C:\renko\shitProgram\jnoCode1.4.2`)
> 关联:本文档是「版本兼容」专项,不改动既有 plan 的机制;但 [`body-sync-2026-08-18.md`](body-sync-2026-08-18.md)、[`part-switch-sync-2026-08-18.md`](part-switch-sync-2026-08-18.md)、[`latency-smoothing-2026-08-22.md`](latency-smoothing-2026-08-22.md)、[`vizzy-isolation-2026-08-22.md`](vizzy-isolation-2026-08-22.md) 的既有功能都需在本版上回归

---

## 〇之二、执行状态核对(2026-09 代码复核,重要)

> 结论:**P0 三项都没有执行**,当前代码与 1.4.102 时期的程序集/API 形态一致。以下是逐项证据(全部为仓库现状实测)。

| 项 | 计划要求 | 现状 | 证据 |
|---|---|---|---|
| **P0-1 刷新参考程序集并重编译** | 用 1.4.2 的 `SimpleRockets2.dll` / `ModApi.dll` 替换后重编译 | ❌ **未做** | `Assets/ModTools/Assemblies/**` 与 `Assemblies/EditorAssemblies/**` 全部仍是 **2026-07-28 03:13** 时间戳(早于本文档 09-03 与"准备 1.4.2 对应更新"提交 09-12) |
| **P0-2 `GetComponentsInChildren` → `GetComponentsInCraft`** | 迁移到 1.4.2 新 API | ❌ **未做** | `GetComponentsInCraft` 全仓库 **0 命中**;`GetComponentsInChildren` 仍在用:`CraftUtils.cs:78`(ParticleSystem)、:119/:123/:127、`MpNetworkManager.cs:1354`(EnforceRemoteCraftVisuals)、:1260/:1758(诊断计数)、`EngineVisualSync.cs:231` |
| **P0-3 `RecenterTransformOnCoM` 签名适配** | 旧二进制 1 参调用 → 新 DLL 无此重载 ⇒ `MissingMethodException` | ⚠️ **源码未改**(仍 1 参),但因**程序集也还没换**,目前不会有异常 | `CraftUtils.cs:41` `RecenterTransformOnCoM(true)`,全仓库仅此 1 处调用 |
| P1-1 参考系重居中叠加防护 | 防双重平移 | ❌ 未做 | 帧补偿路径 `CraftUtils.RecalculateFrameState` 仍是老节奏,无防重入 |
| P1-2 `GroundedSurface` × `SetPose` 接地放置 | 适配 1.4.2 的 `SetPose` | ❌ 未做 | `SetPose` 全仓库 **0 命中**;接地仍走 `SetStateVectors` + **反射写** `GroundedSurfacePosition/Velocity/Rotation`(`MpNetworkManager.cs:2112-2117` 缓存 PropertyInfo、:2131-2149 写入)——1.4.2 若改名会**静默失效**(不会抛异常,只是接地行为退化) |
| P1-3 游戏版本检查 / 实验版开关 | 版本门 + 开关 | ❌ 未做 | 全仓库 **0 命中** `GameVersion` / `experimental` / `1.4.2` / `Application.version`;握手消息亦无版本字段(`MpMessage.EncodePlayerJoin` 只有 playerId/nodeId/name/craftXmlHash) |
| P1-4 1.4.2 调试设施接入 | `GameLoopTypeProfiler` / `FlatDecorationCulling` | ❌ 未做 | 代码无引用 |

**顺带记录的两个当前真实风险(与 1.4.2 无关,但复核时发现)**:
1. **mod 版本号自相矛盾**:`Assets/ModData.asset` 的 `_versionMajor/_versionMinor` = **1.4**,而仓库根 `version.txt` = **1.5**。`ModUpdater` 用 `ModInfo.Version`(即 1.4)与 `version.txt`(1.5)比较 ⇒ **每次启动都会弹"有新版本"**,只能靠"不再提醒"消掉。(版本策略待统一,不在本文档范围)
2. **UI 起始引导图标路径不一致**:`MultiPlayerUI.cs:71` 请求 `MultiPlayer/Sprites/UIIcon`,但 `Assets/Content/XML UI/UIResourceDatabase.asset` 的 `PathPrefix` 仍是 `aMptest/`(条目 `aMptest/Sprites/UIIcon`)。资源库路径前缀需随改名同步,否则图标取不到。

**执行顺序建议(不变)**:先做 P0-1(换程序集 + 重编译,这一步会把潜在的 P0-3 暴露成编译期提示)→ 再 P0-2(用 1.4.2 的 `GetComponentsInCraft` 或直接遍历 `craft.Data.Assembly.Bodies`)→ 双端实测 P0-2/P1-1/P1-2 → 再决定 P1-3/P1-4。

---

## 〇、结论摘要(TL;DR)

1. **1.4.2 是一次「大优化」更新,不是新内容版**:剔除 ILSpy 的 `Token: 0x` 元数据噪音后,约 **105 个游戏脚本有实质改动 + 11 个新增文件**(GC/复用缓冲、渲染网格合并、参考系重居中重写、本地化重构、性能分析设施)。
2. **编译影响极小**:mod 用到的游戏 API 只有 **1 处签名变化** —— `CraftScript.RecenterTransformOnCoM(bool)` → `(bool, Vector3? pendingRecenterDelta = null)`。其余全部验证仍存在。
3. **行为影响集中 2 处**:① 飞行中 body **脱离 craft 层级**(打掉 `EnforceRemoteCraftVisuals` 的 `GetComponentsInChildren<Renderer>`);② **参考系重居中机制重写**(与 mod 的帧补偿 / GroundedSurface 接地 hack 叠加,需实测)。
4. **因 1.4.2 是实验版**:P0 兼容修复**确定要做**(不依赖转正);行为适配**按实测结果再定**;不做大投入,保留回退。

---

## 一、背景:1.4.2 更新内容(反编译对比已完成)

对比目录:`jnoCode`(1.4.102)vs `jnoCode1.4.2`(1.4.200)。完整逐文件 diff 见 `jnoCode/.diff_1.4.2_report.txt`。

| 类别 | 1.4.2 实质改动 | 对 mod 的影响 |
|---|---|---|
| 反编译表面 | 1350+ 文件「全变」 = ILSpy Token 注释噪音(新增类型/成员会平移全部 RID),非实质变化 | — |
| 飞行结构 | **body 脱离 craft 层级**:`BodyScript.MoveToCraft` → `SetParent(Game.InFlightScene ? null : craftScript.Transform, true)`;新增 `CraftScript.SetPose()`、`GetComponentsInCraft<T>()` | **高**:任何 `craft.transform.GetComponentsInChildren` 遍历不到 body 上的组件 |
| 参考系重居中 | `GameViewScript.RecenterIfRequired(preSimulation)`:非暂停非 warp 在物理步后(`OnPostFixedUpdate`)重居中;新增 `pendingRecenterDelta`(只挪醒着且非 kinematic 刚体);`CraftNode.Update` 直接写 transform 改为 `SetPose` | **高**:与 mod 的帧补偿/接地 hack 叠加 |
| API 签名 | `CraftScript.RecenterTransformOnCoM(bool)` → `(bool, Vector3? = null)`(可选参数非重载,旧二进制 1 参调用不存在) | **高(仅此一处)**:不重编译则 MissingMethodException |
| 渲染/材质 | `PartGroupScript` 单合并网格 → 多网格列表;`Theme` 共享材质引用计数;`DesignerMeshCombiner`(>25 部件);标签/MFD `FlatDecorationCulling` | 中(视觉,远程船可能被剔除/材质异常) |
| 游戏循环 | `UpdateGroup` 每 item 包 try/catch + `GameLoopTypeProfiler`(默认关) | 低(mod 的 IFlightUpdate 异常被吞,更稳但隐藏错误) |
| 物理/性能 | 增量质量重算、水物理门禁(`IsWaterReachable`)、拖拽缓存、拆船重写、静态缓冲 | 低(MP 逻辑未直接触及) |
| 本地化 | `Locale.GetLocalizedDisplayName` 新 API 替代 `CareerUtilities`;合同/关卡文本就地本地化 | 无(mod 未用) |
| 其他 | 版本号 1.4.200、Burst 重新生成、`TimeManager` 退 warp 修复 | 低 |

---

## 二、影响评估(反编译确认的事实,不再重复调研)

### 2.1 编译/API 层面(唯一硬伤)

- **`CraftUtils.cs`:41** `((CraftScript)craft.CraftScript).RecenterTransformOnCoM(true);`
  - 旧 DLL 签名 `void RecenterTransformOnCoM(bool)`;1.4.2 为 `void RecenterTransformOnCoM(bool, Vector3? pendingRecenterDelta = null)`。
  - **可选参数不是重载**:旧二进制编译出 1 参调用,新 DLL 里没有 1 参方法 → 运行时 `MissingMethodException`(在远程船帧补偿路径,取决于调用点 try/catch,表现为远程船不校正或刷异常日志)。
  - **重新编译即修复**(源码 `(true)` 仍合法,默认参数补 `null`)。
- 其余用到的 API **全部验证仍存在**:`CraftScript.FramePosition/FrameVelocity/DestroyBody/RootPart/IsPhysicsEnabled/RepositionParticleSystem`、`CraftNode.SetStateVectors`、`GroundedSurfacePosition/Velocity/Rotation`(反射)、`BodyScript.Transform/RigidBody/IsDebris/Disconnected/OnRecentered`、`FlightSceneScript.SpawnCraft`、`PartScript.Activate/Deactivate`、`ConfigData.*`。
- **Harmony patch 目标全部安全**:`JetEngineScript`(未变)、`NavPanelController`(未变)、`FlightProgramScript`(变了但 `FlightUpdate`/`BroadcastMessage` 签名未变);`EngineVisualSync` 反射字段(`JetEngineScript._rocketExhaustSystem` / `_afterburnerSmokeColor` / `RocketEngineScript._params`)均未变。

### 2.2 行为层面(需适配/实测)

1. **body 脱离 craft 层级**(新结构):
   - `MpNetworkManager.EnforceRemoteCraftVisuals`(约 :1354,每帧 `go.GetComponentsInChildren<Renderer>(true)` 强开远程船渲染)→ 1.4.2 下遍历不到 body 上的渲染器 → **强制恢复远程船视觉失效**,远程船可能隐形。
   - 连带 :1260(spawnDiag)、:1758(visualDiag 3s)的 renderer 统计同样失真。
   - `CraftUtils.RecalculateFrameState`(约 :86)`craft.gameObject.GetComponentsInChildren<ParticleSystem>()` → 漏掉 body 上的尾焰粒子偏移。
   - **修法**:改用 1.4.2 新 API `CraftScript.GetComponentsInCraft<T>(List<T>)`(显式遍历所有 body),或手动遍历 `craft.Data.Assembly.Bodies`。
2. **参考系重居中重写**(新机制):
   - 本地玩家飞船现在由游戏在物理步后做 **PreSimulation 重居中**;mod 若仍按老节奏做帧补偿(`CraftUtils.RecalculateFrameState`、`ApplyRemoteState` 帧↔行星换算、`BodyScript.OnRecentered`),可能**双重平移**。
   - 远程船接地放置:1.4.2 的 `CraftNode.Update` 改走 `SetPose`(整体移动所有 body);mod 的 `GroundedSurface*` 反射 hack 依赖「游戏每帧按该值放置」,且 mod 自己也逐 body 写 → 可能**重复移动/错位**。
   - **需双端实测**再决定是否加防重入/协调逻辑。
3. **游戏循环 try/catch**:`UpdateGroup` 现在包裹每个 update item → mod 的 `IFlightUpdate`/`IFlightFixedUpdate` 抛异常不再冒泡(更稳,但隐藏错误)。

### 2.3 低影响/观察项

- `FlatDecorationCulling`(标签/MFD 剔除):远程船标签/MFD 可能被新剔除规则藏掉,纯视觉。
- 共享材质引用计数 + `DesignerMeshCombiner`:若出现「远程船材质/选中高亮异常」再查。
- 增量质量 / 水物理门禁 / 拆船重写 / `CraftSplitter` 碎片处理:MP 逻辑未直接触及。
- `TimeManager` 退 warp 修复:mod 仅 1 处引用,无碍。

---

## 三、方案设计(分档:确定 / 待实测 / 观察)

### 3.1 确定要做 —— P0 兼容修复(不依赖 1.4.2 是否转正)

| # | 改动 | 位置 | 说明 |
|---|---|---|---|
| P0-1 | **刷新程序集并重编译** | `Assets/ModTools/Assemblies/EditorAssemblies/SimpleRockets2.dll`(+ `ModApi.dll`)→ 换成 1.4.2 实验版安装目录的同名 DLL | 否则 `RecenterTransformOnCoM` 抛 MissingMethodException。注意:1.4.2 是实验分支,ModTools 的「同步程序集」流程可能未适配,需手动拷贝;**保留 1.4.102 的编译产物用于回退** |
| P0-2 | **EnforceRemoteCraftVisuals 迁移** | `MpNetworkManager.cs` :1354(及 :1260 / :1758 的 renderer 统计) | `GetComponentsInChildren<Renderer>` → `GetComponentsInCraft<Renderer>`(或逐 body 遍历) |
| P0-3 | **CraftUtils 粒子遍历迁移** | `CraftUtils.cs` :86 | `GetComponentsInChildren<ParticleSystem>` → 逐 body 遍历(或 `GetComponentsInCraft`) |
| P0-4 | 双端回归 | 各活跃 plan 的既有验证 | 确认 body 位姿/部件开关/延迟平滑/Vizzy 隔离在 1.4.2 上不回归 |

### 3.2 待实测后拍板 —— P1 行为适配(实验版未定)

| # | 待定项 | 决策条件 | 备注 |
|---|---|---|---|
| P1-1 | 参考系重居中叠加(双重平移/错位) | 双端远距离触发重居中实测 | 若抖动/错位,再加「仅处理远程船 / 跳过本地船重居中补偿」的协调 |
| P1-2 | GroundedSurface hack × `SetPose` 接地放置 | 地面幽灵船实测 | 若重复移动,改为只用一侧写(游戏 SetPose 或 mod 逐 body 二选一) |
| P1-3 | 游戏版本检查 / 实验版开关 | 看 1.4.2 是否转正 + 版本策略 | 防玩家用错版本联机(实验版 vs 正式版互相不可联机) |
| P1-4 | 1.4.2 调试设施接入 | 看实验版是否稳定 | `GameLoopTypeProfiler` / `SetFlatDecorationCulling` / `LastRecenter*Ms` 是很好的联机诊断补充(`LagSimTransport` 之外),先观望 |

### 3.3 观察项 —— 暂不做(记录原因)

- `FlatDecorationCulling`(标签/MFD 剔除):影响远程船视觉,实验版表现待观察;若影响可读性再考虑接 `SetFlatDecorationCulling` 开关。
- 共享材质 / `DesignerMeshCombiner`:仅在出现「远程船材质/高亮异常」时排查。
- 水物理门禁 / 增量质量 / 拆船重写:MP 逻辑未触及,不投入。

---

## 四、风险与回退

1. **1.4.2 是实验版**:行为可能再变(hotfix / 转正版本号变化),本文档的 P1 结论可能失效 → 重跑 §五 实测清单即可。
2. **保留回退**:重编译前备份 1.4.102 的编译产物;如 P1 适配投入大,可暂不随 mod 默认启用,用开关(如 `ModSettings` 加 `GameVersionCompatibility` 项)控制。
3. **程序集刷新风险**:实验版 `SimpleRockets2.dll` 与 1.4.102 混用会出签名/类型不一致问题;同一时间只对同一版本编译、发布。

---

## 五、验证清单(双端实测)

| # | 场景 | 通过标准 |
|---|---|---|
| V1 | 旧 mod 二进制在 1.4.2 上运行 | 确认 `RecenterTransformOnCoM` MissingMethodException(证明 P0-1 必要) |
| V2 | 重编译后远程船生成/隐形回归 | 远程船全程可见;`EnforceRemoteCraftVisuals` 迁移后渲染器能被遍历到(spawnDiag/visualDiag 计数正常) |
| V3 | 远距离触发参考系重居中 | 本地船/远程船位置、朝向一致,无抖、无漂移、无双重重置 |
| V4 | 地面幽灵船(接地 hack) | `GroundedSurface*` 在 `SetPose` 放置下正常,不贴地/不重复移动 |
| V5 | 既有功能回归 | body 位姿 / 部件开关 / 引擎尾焰 / 延迟平滑 / Vizzy 隔离(复用各 plan 既有验证) |

---

## 六、决策记录

| 决策 | 结论 | 日期 | 状态 |
|---|---|---|---|
| 刷新程序集 + 重编译(RecenterTransformOnCoM) | **确定要做** | 2026-09-03 | ⏳ 待执行 |
| EnforceRemoteCraftVisuals → `GetComponentsInCraft` | **确定要做** | 2026-09-03 | ⏳ 待执行 |
| CraftUtils 粒子遍历 → 逐 body | **确定要做** | 2026-09-03 | ⏳ 待执行 |
| 参考系重居中叠加适配 | **待实测后定** | 2026-09-03 | ❓ 待定 |
| GroundedSurface × `SetPose` 接地适配 | **待实测后定** | 2026-09-03 | ❓ 待定 |
| 游戏版本检查 / 实验版开关 | **待定**(看 1.4.2 转正策略) | 2026-09-03 | ❓ 待定 |
| 调试设施接入(GameLoopTypeProfiler 等) | **观察,暂不接** | 2026-09-03 | ❓ 待定 |

> 说明:以上「待定」项均因 1.4.2 为实验版、行为可能再变而未拍板;转正或实测出现明确结论后,更新本表并同步 `README.md` 决策速查。

---

## 七、与现有 plan 的关系

| 文档 | 关系 |
|---|---|
| [`body-sync-2026-08-18.md`](body-sync-2026-08-18.md) / [`latency-smoothing-2026-08-22.md`](latency-smoothing-2026-08-22.md) / [`part-switch-sync-2026-08-18.md`](part-switch-sync-2026-08-18.md) / [`vizzy-isolation-2026-08-22.md`](vizzy-isolation-2026-08-22.md) | 既有机制的**版本回归**对象(P0-4);本方案不改动其方案 |
| [`multi-craft-sync-2026-08-16.md`](multi-craft-sync-2026-08-16.md) | 其研究基于 1.4.102 反编译(`jnoCode`);1.4.2 后新结论需在本方案下复核(如 body 脱离层级对多 craft 同步的影响) |
| [`AGENT_CONTEXT.md`](AGENT_CONTEXT.md) | §4「游戏内部 API 依赖反编译源码导航,游戏更新可能破坏,需固定版本」——本文档即该风险的专项记录 |

---

## 八、备注

- 1.4.2 反编译目录:`C:\renko\shitProgram\jnoCode1.4.2`(仅 `SimpleRockets2`);完整 diff 报告:`C:\renko\shitProgram\jnoCode\.diff_1.4.2_report.txt`(逐文件、剔除 Token 噪音)。
- devb 原话要点:高度实验性、触及核心机制、可能破坏、备份后测试——与本方案「保留回退 + 分档决策」一致。
- 后续若 1.4.2 转正/出 1.4.3,把本 plan 的 P1 决策更新后移入 `archive/`。
