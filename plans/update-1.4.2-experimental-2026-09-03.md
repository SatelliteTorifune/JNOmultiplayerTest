# 游戏 1.4.2(Experimental 分支)兼容适配方案

> 项目:JNOMultiPlayer(SimpleRockets 2 / JNO 联机 mod MultiPlayer)
> 状态:**P0 已全部执行,代码侧完成;1.4.2 朝向/body 同步 bug 已定位并修复;新报"双飞静止一方抽搐"已加诊断日志待实测**(2026-09-13:程序集已刷新为 1.4.2 并由用户确认;`GetComponentsInCraft` 迁移与粒子遍历迁移已落地,`dotnet build` 0 错误;2026-09 实测发现远程船朝向+body 位置错误,根因为 1.4.2 body 脱离 craft 层级导致 `localRotation` 语义变化,已修复 `ApplyRemoteBodyPoses`(见「〇之三」);后续实测报"双飞静止一方抽搐",已加 `MP twitch`/`MP sendDiag` 1s 周期诊断日志,怀疑 comRot 连带移动反馈环/双写不一致/平滑(见「〇之四」))
> 触发:devb 发布 **1.4.2 Experimental 分支**(版本 1.4.200;当前游戏为 1.4.102),反编译对比已完成(新反编译目录 `C:\renko\shitProgram\jnoCode` 即 1.4.2,`Game.Version = (1,4,200,0)`)
> 关联:本文档是「版本兼容」专项,不改动既有 plan 的机制;但 [`body-sync-2026-08-18.md`](archive/body-sync-2026-08-18.md)、[`part-switch-sync-2026-08-18.md`](archive/part-switch-sync-2026-08-18.md)、[`latency-smoothing-2026-08-22.md`](archive/latency-smoothing-2026-08-22.md)、[`vizzy-isolation-2026-08-22.md`](archive/vizzy-isolation-2026-08-22.md) 的既有功能都需在本版上回归

---

## 〇之二、执行状态核对(2026-09 代码复核 + 2026-09-13 执行,重要)

> 结论:**P0 三项已全部执行**(程序集刷新由用户完成,代码迁移由本次执行完成并编译通过),P1 仍需双端实测拍板。以下是逐项证据。

| 项 | 计划要求 | 现状 | 证据 |
|---|---|---|---|
| **P0-1 刷新参考程序集并重编译** | 用 1.4.2 的 `SimpleRockets2.dll` / `ModApi.dll` 替换后重编译 | ✅ **已做(用户,2026-09-13)** | `Assets/ModTools/Assemblies/**` 与 `EditorAssemblies/**` 的 `SimpleRockets2.dll`/`ModApi.dll` 已更新(大小与 1.4.2 安装目录一致);二进制内 `GetComponentsInCraft`/`SetPose`/`pendingRecenterDelta` 全部命中;`dotnet build MultiPlayer.csproj` **0 错误** |
| **P0-2 `GetComponentsInChildren` → `GetComponentsInCraft`** | 迁移到 1.4.2 新 API | ✅ **已做(2026-09-13)** | `MpNetworkManager.cs` 三处 renderer 遍历(:1260 spawnDiag / :1354 EnforceRemoteCraftVisuals / :1758 visualDiag)全部改用 `CraftUtils.GetComponentsInCraft`(逐 body 遍历,`includeInactive=true` 保留旧行为,热路径复用 `RemoteCraft.ReuseRenderers` 防每帧 GC) |
| **P0-3 `RecenterTransformOnCoM` 签名适配** | 旧二进制 1 参调用 → 新 DLL 无此重载 ⇒ `MissingMethodException` | ✅ **随 P0-1 重编译自动修复** | `CraftUtils.cs:41` `RecenterTransformOnCoM(true)` 对 1.4.2 签名 `(bool, Vector3? pendingRecenterDelta = null)` 仍合法(可选参数,默认补 null);重编译后不再有 1 参调用 |
| **P0-3b 粒子遍历迁移** | `CraftUtils.cs:78` 粒子遍历覆盖脱离层级的 body | ✅ **已做(2026-09-13)** | `craft.gameObject.GetComponentsInChildren<ParticleSystem>()` → `CraftUtils.GetComponentsInCraft(craft, list, false)`(逐 body 遍历,等价 1.4.2 `CraftScript.GetComponentsInCraft<T>`) |
| **EngineVisualSync.cs:231** | 复核 `GetComponentsInChildren<ExhaustDamageScript>` | ✅ **确认无需改** | 该处遍历的是 **part 自身 GameObject** 的子树(`part.PartScript.GameObject`),body 脱离 craft 层级不影响 part 内部结构,1.4.2 下仍正确 |
| P1-1 参考系重居中叠加防护 | 防双重平移 | ❌ 未做(待实测) | 反编译证据见 §3.2 更新:1.4.2 游戏自身 `CraftNode.RecalculateFrameState` 只在 `IsPhysicsEnabled` 时调 `RecenterTransformOnCoM`(:683-688),而 mod 远程船恒为物理禁用(`SetPhysicsEnabled(false)`),故游戏重居中路径不会动远程幽灵船 → 双重平移风险大幅降低,但需双端实测确认 |
| P1-2 `GroundedSurface` × `SetPose` 接地放置 | 适配 1.4.2 的 `SetPose` | ❌ 未做(待实测) | 反编译确认 `CraftNode.GroundedSurfacePosition/Velocity/Rotation` 在 1.4.2 **仍存在**(CraftNode.cs:334-344),反射写**不会静默失效**;`SetPose` 仅用于 warp/生成/MapView 路径(:669/:883/:889/:1047),接地 Update 分支仍读 `GroundedSurface*`(:1235-1240) |
| P1-3 游戏版本检查 / 实验版开关 | 版本门 + 开关 | ❌ 未做(待定) | 全仓库 `GameVersion`/`experimental`/`1.4.2` 仍 0 命中;握手消息仍无版本字段 |
| P1-4 1.4.2 调试设施接入 | `GameLoopTypeProfiler` / `FlatDecorationCulling` | ❌ 未做(观察) | 代码无引用 |

**顺带记录的两个当前真实风险(与 1.4.2 无关,但复核时发现)**:
1. **mod 版本号自相矛盾**:`Assets/ModData.asset` 的 `_versionMajor/_versionMinor` = **1.4**,而仓库根 `version.txt` = **1.5**。`ModUpdater` 用 `ModInfo.Version`(即 1.4)与 `version.txt`(1.5)比较 ⇒ **每次启动都会弹"有新版本"**,只能靠"不再提醒"消掉。(版本策略待统一,不在本文档范围)
2. **UI 起始引导图标路径不一致** **✅ 已解决(2026-09-14,工作区未提交)**:`MultiPlayerUI.cs:82` 请求 `MultiPlayer/Sprites/UIIcon`,`UIResourceDatabase.asset` 的 `PathPrefix` 与两条 entry 已同步改为 `MultiPlayer/`(旧 `aMptest/` 是改名遗留)。**反编译 `XmlLayout.dll` 确认**:`sprite` 属性运行时按 `XmlLayoutResourceDatabase.GetResource` 的条目路径**逐字匹配**,不自动拼前缀,故必须等于 entry 全串;旧值 `/Sprites/UIIcon`(相对前缀假设)取不到,已一并修正。

**执行顺序建议(不变)**:先做 P0-1(换程序集 + 重编译,这一步会把潜在的 P0-3 暴露成编译期提示)→ 再 P0-2(用 1.4.2 的 `GetComponentsInCraft` 或直接遍历 `craft.Data.Assembly.Bodies`)→ 双端实测 P0-2/P1-1/P1-2 → 再决定 P1-3/P1-4。

---

## 〇之三、1.4.2 朝向/body 同步 bug 修复(2026-09 双端实测后,重要)

> 实测现象:**远程船朝向错误,且 body 同步位置错误;与行星自转无关**(用户确认已排除 SrfRel/自转因素)。
> 定位结论:根因是 1.4.2 的「飞行中 body 脱离 craft 层级」(`BodyScript.MoveToCraft` → `SetParent(Game.InFlightScene ? null : craftScript.Transform, true)`,BodyScript.cs:597)改变了 `Transform.localRotation` 的语义。

### 根因链

| 版本 | body 层级 | `ApplyRemoteBodyPoses` 写 `localRotation = relCom` 的效果 |
|---|---|---|
| 1.4.102 | body 是 craft 根的子级(`SetParent(craftScript.Transform, true)`) | `localRotation` 相对父级(craft 根)解析 ⇒ 世界旋转 = 根旋转(≈comRot)× relCom = comRot × (comRot⁻¹ × bodyWorld) = bodyWorld ✅ |
| 1.4.2 | body parent = **null**(脱离层级) | `localRotation` 即**世界旋转** ⇒ 世界旋转 = relCom = comRot⁻¹ × bodyWorld ❌ **丢失 comRot 因子,整体转错** |

- 发送端语义不变: `relCom = comRot⁻¹ × bodyWorld`(相对质心), `relPos = comRot⁻¹ × bodyLocal`(相对质心)。
- 旧假设「接收端根=comRot,故 body.localRotation 直接可写」在 1.4.2 不再成立(body 已不在根下)。
- body 位置 `body.position = comRot.TransformPoint(relPos)` 本身是绝对写、与层级无关,但 `comRot` 是 `RootPart.Transform` 的后代(:1562),循环内写 body 位置会连带移动 comRot → 后续 body 参考漂移,需冻结位姿一次。

### 修复(2026-09 已落地,`dotnet build` 0 错误)

`MpNetworkManager.cs` `ApplyRemoteBodyPoses`:
1. **旋转改为显式世界写**: `body.Transform.rotation = comRotRot × Quaternion.Euler(data.BodyRotations[i])`(`comRotRot = comRot?.rotation ?? 根旋转`)。1.4.102 下 `comRot.rotation = 根旋转 = headingFrame`,世界写与旧 `localRotation` 结果**完全一致**,双版本均正确。
2. **冻结 comRot 位姿一次**: `comRotPos + comRotRot × relPos` 替代循环内每次 `comRot.TransformPoint(relPos)`,避免写 body 位置时 comRot(位于 RootPart 下)被连带移动导致后续 body 漂移。
3. 同步更新 `ApplyRemoteState` 步骤③ 的注释(去掉「body.localRotation 直接可写」的过时说法)。

### 待实测确认项

- [ ] 双端 1.4.2:远程船朝向与本地一致(尤其转动/机动后)
- [ ] 双端 1.4.2:多 body(转轴/关节连接)远程船无散架、body 相对位置正确
- [ ] 回退 1.4.102 程序集时朝向同步仍正常(世界写与旧 localRotation 等价的验证)

---

## 〇之四、双飞静止一方抽搐(2026-09 双端实测,**机制坐实,已修复**)

> 实测现象(用户报告):**双飞静止时,一方出现抽搐**;与行星自转无关(已排除)。
> 状态:**机制 1+2 坐实,2026-09-13 已修复**(逻辑 comRot 位姿作 body 基准,`dotnet build` 0 错误 0 警告),待双端实测确认。

### 实测数据(2026-09-13 Player.log,接收端 `MP twitch P1`,静止船 vel≈0.026m/s)

```
comLink=0.0299m comCross=0.0127m b0d=0.0127m b0dLate=0.0299m comFrozen=(34.25,116.42,-55.14) comAfter=(..) comLate=(..)
comLink=0.0299m ...  (恒定)
```

| 字段 | 值 | 判读 |
|---|---|---|
| `comLink` | **0.0299m 恒定** | 写 body 前后 comRot 连带位移 = 根 body 写入位移 = 每帧基准污染量 |
| `b0dLate` | **0.0299m 恒定** | LateUpdate 重写 body[0] 的位移 ≈ comLink ⇒ **Update/LateUpdate 双写基准不一致,同帧两次写入位置差 3cm** |
| `comCross`/`b0d` | 0.0001~0.05m 变化 | 跨帧基准漂移(小) |
| `frozen=1` `moveDelta=0.00` | — | 死区外推修复(§9.7)已收敛 → **残余抽搐与平滑无关,坐实为 comRot 反馈环** |

### 根因(机制 1+2 合并确认)

1.4.2 中 comRot 是 `RootPart.Transform` 的后代(根 body 内 ~3cm 偏移);
`ApplyRemoteBodyPoses` 以**接收端实时 comRot 位姿**为基准绝对写所有 body:
写 body[0](根 body)必然连带移动 comRot;而**游戏接地放置**(`GroundedSurfacePosition` = 包内位置)会把根 body 放到包内位置 —— 两者每帧差固定 ≈3cm(= comRot 在根 body 内的偏移),
形成"游戏放 root → 我们写 body[0] → comRot 被连带移走 → 下帧基准漂移"的固定幅度往复。
Update 与 LateUpdate 各写一次、各冻一次基准,进一步放大为帧内 3cm 抖动。

### 修复(2026-09-13)

**`ApplyRemoteBodyPoses` 改用"逻辑 comRot 位姿"作基准**:新增 `TryGetLogicalComPose(rc, data, frame, …)` ——
位置 = 包内 `Position`(地表坐标)经 `frame.PlanetToFramePosition` 转帧空间,旋转 = `frame.PlanetToFrameRotation(行星自转 × SrfRel)`,
**完全不读接收端 comRot 的实时 Transform**。Update(`ApplyRemoteState`)与 LateUpdate(`ForceRemoteHeading`)两处调用统一传逻辑位姿:

- 与游戏接地放置基准一致(根 body 落在包内位置) → 游戏不再每帧把我们写的 body 拉回去,反馈环消失;
- 两次写入基准相同 → `b0dLate → 0`;
- 发送端 body 相对位姿(relPos/relRot,相对**发送端** comRot)乘上接收端逻辑位姿,几何上正是发送端世界布局,正确性不受影响。

**预期复测**:静止双飞 `MP twitch` 行 `comLink≈0.0000m`、`b0dLate≈0.0000m`、`b0d≈0`。

### 二轮实测(2026-09-13 双端互看日志,逻辑基准已生效)

| 场景 | 实测 |
|---|---|
| 本机看对端静止船(12 body) | `comLink≈0.0000`、`b0dLate≈0.0000` ✅ 逻辑基准修复生效 |
| **对端看本机飞行+暂停船(46 body)** | `b0dLate=0.1188m` **恒定**(飞行/静止/暂停全程),`comFrozen≈comLate`、`comAfter` 为游戏放置与外推目标之差(飞行时 1~6.7m,发生在 Update 前,渲染以 LateUpdate 写入为准,无渲染影响) |

### 根因(二轮):根 body 与游戏放置基准的 rel0 对抗

游戏 `RecalculateFrameState` 每帧把**根 body 放到 `craft.Position`**(= 包内 Position = comRot 位置);
逻辑基准写 `body[0] = comPos + rot×rel0`。若**该船的 `rel0` ≠ 0**(body[0] 相对 comRot 的包内偏移,46 body 船实测 **0.1188m**,12 body 船 ≈0),
则每帧"游戏拉到 comPos → 我们写回 comPos+rel0"固定幅度往复 → 观察方恒定 ~12cm 抖动(与暂停/飞行无关)。

**修复(2026-09-13 二轮)**:`ApplyRemoteBodyPoses` 中 **`i==0`(根 body)直接写 `comPos`**,不叠加 `rel0`;
其余 body 仍按 `comPos + rot×relPos[i]`。根 body 模型跨整船,原点偏移 12cm 不可见;旋转仍按包内值。
- 与游戏放置基准一致 → `positionDelta≈0` → 游戏不再每帧拉回 → `b0dLate→0`;
- 静止船(rel0≈0)行为不变(本来写 comPos);
- 发送端 `MP sendDiag` 新增 `body0Rel=`(包内 rel0 绝对值)供下轮核对。

**二轮预期复测**:对端看 46 body 飞行船 `b0dLate≈0.0000m`(原 0.1188 恒定),`comLink` 随飞行运动的
1~6.7m 值属于"游戏 Update 前放置 vs 外推目标"的差,由 LateUpdate 写入压平,不影响渲染,可忽略。

### 三轮实测与修复(2026-09-13 晚,双端最新 build 互看)

| 项 | 数据 |
|---|---|
| 包内 rel0 核对 | 发送端 sendDiag `body0Rel=`:P0(46 body)=0.1056m、P1(12 body)=**0.1271m**(两艘船 rel0 都≠0!) |
| 二轮修复效果 | 根 body 写 comPos 后:本机看静止 P1 `b0dLate` 从 0.0000 → **0.1271m**(变差!);对端看 P0 从 0.1188 → 0.0850(未归零) |
| 用户反馈 | 正常飞行/慢放/暂停**均有抖动,慢放最严重** |

**根因(三轮,坐实)**:游戏 `CraftScript.FramePosition` 的 getter **就是 `CenterOfMass.position`**(CraftScript.cs:400)!
`RecalculateFrameState` 每帧把 **comRot 锚定到 craft.Position**(= 逻辑 comPos);body[0] 是 comRot 的**父级**,
因此被游戏放到 `comPos − G`(G = comRot 相对 body[0] 的**接收端自身几何偏移**,每船不同:P1=0.1271m、P0=0.0850m)。
- 写 `comPos+rot×rel0`(逻辑基准首版):对抗 = |W+G|(P1 恰 W=−G 时 =0,所以首版静止船看着是好的);
- 写 `comPos`(二轮):对抗 = |G|(两船都中招);
- **写 `comPos − G`(G 写前实时读取):与游戏锚定完全一致 → 对抗 = 0。**(三轮修复)

**慢放最严重的根因(独立问题)**:`age = Time.unscaledTime − NewestArrivalTime` 是**真实时间**;
慢放时发送端飞船按缩放时间移动(相邻包仅推进 `v×T_s×timeScale`),外推却按真实时间跑 →
每包目标"超前→拉回"锯齿,幅度 `v×T_s×(1−timeScale)`,timeScale 越小越严重(暂停=0 最极端,被冻结逻辑挡住,
所以**慢放是可见最坏档**)。**修复**:接收端测量**发送端时间倍率**(相邻两包 `FlightState.Time` 增量 / 真实到达增量,EMA),
外推量 `ext ×= SenderTimeRate` 换算到发送端时间基:正常飞行 rate≈1 行为不变;慢放 rate=timeScale 外推与运动同步;
发送端暂停 rate→0 → ext→0,幽灵精确停在包位置(比冻结逻辑的 latencySec 更准)。诊断:`MP smoothing` 行新增 `rate=`。

**三轮预期复测**:任意阶段 `MP twitch` 行 `b0dLate≈0.0000m`、`comLink≈0.0000m`;慢放时 `MP smoothing` 行
`rate≈发送端 timeScale(<1)`,`moveDelta` 与实际慢速运动一致(不再每包锯齿);正常飞行 rate≈1 行为不变。

### 四~六轮复测与修复(2026-09-13,双端新 build 互看 + 用户逐轮量测,收工)

- **四轮(实测)**:①慢放 = Unity `Time.timeScale=0.050`,但游戏 `FlightState.Time` **不缩放**(对端 `rate`
  恒 1.000)→ 包时间倍率测不出慢放 → 换**位置基运动倍率 `SenderMotionRate`**(相邻包位置位移 ÷ (速度×真实
  到达间隔),EMA 0.9/0.1);②真慢放期间静止 ghost 变换链全 0 位移(新增 `MP slowmo` 0.5s 诊断:
  rootΔ/partΔ/comΔ/dist)。
- **五轮(代码审查抓到死代码)**:`PushSample` 里 `_lastPushTime = arrivalTime` 在倍率测量**之前**执行 →
  `dtReal ≡ 0` → rate/mRate **从未更新**(恒 1.000)→ 慢放外推从未缩放 = "慢放 1.5 船身跳"、暂停回拉
  `v×ageNow` = "暂停 0.5m 跳" 的真根因。修复:`prevArrival` 先存后覆盖。
- **六轮(实测生效)**:对端慢放 `mRate≈0.05`、暂停指数衰减 `0.469→0.000`、恢复回升;剩"切换速度模式
  跳变" = 单包测量尖刺透出 EMA(0.208→0.393 → 单帧 ~2m)→ mRate 每包变化钳制 `±0.15`
  (MaxMotionRateStep,满量程收敛 ~0.3s)。
- **收工(用户确认)**:切换瞬间可见位移 ≤0.3m、高速加速段单帧 ≤ `1.5×v×dt` 物理上限(用户确认可忽略);
  `b0dLate` 全 0、gapEMA≈50ms、冻结进出干净。**用户:"过小的跳动目前来看可以忽略……收工"。**

> 注:诊断代码(comLink/comCross/b0d/b0dLate 字段与 `MP twitch` 日志)保留,用于验证修复;确认后可视情况收敛日志频率或删除。

### 怀疑机制(按嫌疑排序,与日志字段一一对应)

1. **comRot 连带移动反馈环(最大嫌疑)**:
   1.4.2 中 comRot 是 `RootPart.Transform` 的后代(CraftScript.cs:2172 `SetParent(RootPart.Transform,false)`),RootPart 位于**根 body 内**;
   `ApplyRemoteBodyPoses` 写 body 世界位置时,**根 body 的写入必然连带移动 comRot**。
   而 `ApplyRemoteBodyPoses` 又用**冻结的 comRot 位姿**作摆放基准 → 每帧"写 body → comRot 被连带移走 → 下帧冻结基准漂移 → body 再按漂移基准写"→ **反馈环**,静止时表现为原地抽搐。
   - 日志字段:`comLink`(写 body 前后 comRot 连带位移)、`comCross`(跨帧冻结基准漂移)。**若 comLink≈comCross>0.01m → 机制 1 坐实**。
   - 佐证:1.4.2 `RecalculateCenterOfMass`(:1343-1378)/`SetCenterOfMassGameObjectPosition`(:2167-2197)每帧(结构变化/质量变化时)会把 comRot 移到**质量加权质心**并覆盖 `comRot.rotation = PilotSeatOrientation.rotation`(:2189),进一步放大基准漂移。

2. **Update/LateUpdate 双写不一致**:
   `ApplyRemoteState`(Update)与 `ForceRemoteHeading`(LateUpdate)各调一次 `ApplyRemoteBodyPoses`,
   各冻结一次 comRot 位姿;若两次冻结之间 comRot 被连带移动,则**同帧两次写入 body 位置不同** → 抖动。
   - 日志字段:`b0dLate`(LateUpdate 重写 body[0] 造成的位移)、`comLate`(LateUpdate 冻结的 comRot 位置 vs Update 冻结值)。

3. **平滑层(用户怀疑方向)**:
   `ApplyRemoteSmoothing` 对每 body 用 `10·dt` 平滑 + 0.1m/0.01° 近距快照;若发送端 body 数据本身稳定,
   平滑应收敛不抖;但若 comRot 基准漂移,平滑后的**相对**位姿被"绝对写"到漂移基准上,照样抖。
   - 日志字段:`b0d`(渲染层 body[0] 逐帧位移,静止时应≈0)、发送端 `sendDiag body0RelΔ`(发送端采样数据本身是否抖)。

4. **发送端数据抖动**(需排除):发送端静止时若 `body0RelΔ>0.01m`,说明数据源在抖,接收端平滑只能衰减。
   - 日志字段:`MP sendDiag P#: body0RelΔ`。

### 日志速查(1s 周期,`Mod.LogLobby`,双端各自输出)

- 接收端:`MP twitch P{id}: comLink=X.XXXXm comCross=X.XXXXm b0d=X.XXXXm b0dLate=X.XXXXm comFrozen=(..) comAfter=(..) comLate=(..)`
- 发送端:`MP sendDiag P{id}: vel=..m/s body0RelΔ=X.XXXXm bodyCnt=..`

### 判定规则(拿到日志后)

| comLink | comCross | b0d | 结论 |
|---|---|---|---|
| >0.01m | ≈comLink | >0 | **机制 1 坐实**:写 body 连带移动 comRot 的反馈环 → 修 ApplyRemoteBodyPoses:先记录 body 相对 comRot 偏移后**把 comRot 临时移出 RootPart 层级**(SetParent(null) 再写、写后还原),或改用"逻辑位置+固定质心偏移"作基准 |
| 0 | 0 | >0 | 机制 3/发送端:看 `sendDiag body0RelΔ` 分流向 |
| 0 | 0 | 0 | 平滑已收敛,抽搐来自**渲染层其他源**(相机/参考系重居中),继续查 |
| comLink=0 但 b0dLate>0 | 0 | - | 机制 2 双写不一致 → LateUpdate 复用 Update 冻结的 comRot 位姿 |

### 已加代码(2026-09,`dotnet build` 0 错误)

- `MpNetworkManager.cs`:RemoteCraft 新增 `DiagComPosFrozen/PrevFrozen/AfterBodies/Late`、`DiagComLinkM/CrossFrameM`、`DiagBody0*` 等诊断字段;
  `ApplyRemoteState`(Update 路径)冻结位姿前记录跨帧漂移、写后记录连带位移、body[0] 逐帧位移;
  `ForceRemoteHeading`(LateUpdate 路径)记录 LateUpdate 冻结位姿与重写位移;
  `UpdateRemoteCrafts` 加 1s 周期 `MP twitch` 日志;`TrySampleLocalCraft` 加发送端 body0 相对采样抖动 + 1s 周期 `MP sendDiag` 日志。

---

## 〇、结论摘要(TL;DR)

1. **1.4.2 是一次「大优化」更新,不是新内容版**:剔除 ILSpy 的 `Token: 0x` 元数据噪音后,约 **105 个游戏脚本有实质改动 + 11 个新增文件**(GC/复用缓冲、渲染网格合并、参考系重居中重写、本地化重构、性能分析设施)。
2. **编译影响极小**:mod 用到的游戏 API 只有 **1 处签名变化** —— `CraftScript.RecenterTransformOnCoM(bool)` → `(bool, Vector3? pendingRecenterDelta = null)`。其余全部验证仍存在。
3. **行为影响集中 2 处**:① 飞行中 body **脱离 craft 层级**(打掉 `EnforceRemoteCraftVisuals` 的 `GetComponentsInChildren<Renderer>`);② **参考系重居中机制重写**(与 mod 的帧补偿 / GroundedSurface 接地 hack 叠加,需实测)。
4. **因 1.4.2 是实验版**:P0 兼容修复**确定要做**(不依赖转正);行为适配**按实测结果再定**;不做大投入,保留回退。

---

## 一、背景:1.4.2 更新内容(反编译对比已完成)

对比目录:`jnoCode`(1.4.200,即 1.4.2 实验版)vs 1.4.102 旧版(早期反编译,已并入 `jnoCode` 更新;1.4.2 后无独立旧目录)。完整逐文件 diff 见 `jnoCode/.diff_1.4.2_report.txt`。

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

| # | 改动 | 位置 | 说明 | 状态 |
|---|---|---|---|---|
| P0-1 | **刷新程序集并重编译** | `Assets/ModTools/Assemblies/EditorAssemblies/SimpleRockets2.dll`(+ `ModApi.dll`)→ 换成 1.4.2 实验版安装目录的同名 DLL | 否则 `RecenterTransformOnCoM` 抛 MissingMethodException。注意:1.4.2 是实验分支,ModTools 的「同步程序集」流程可能未适配,需手动拷贝;**保留 1.4.102 的编译产物用于回退** | ✅ 2026-09-13 用户完成 |
| P0-2 | **EnforceRemoteCraftVisuals 迁移** | `MpNetworkManager.cs` :1354(及 :1260 / :1758 的 renderer 统计) | `GetComponentsInChildren<Renderer>` → `GetComponentsInCraft<Renderer>`(或逐 body 遍历) | ✅ 2026-09-13 完成 |
| P0-3 | **CraftUtils 粒子遍历迁移** | `CraftUtils.cs` :86 | `GetComponentsInChildren<ParticleSystem>` → 逐 body 遍历(或 `GetComponentsInCraft`) | ✅ 2026-09-13 完成 |
| P0-4 | 双端回归 | 各活跃 plan 的既有验证 | 确认 body 位姿/部件开关/延迟平滑/Vizzy 隔离在 1.4.2 上不回归 | ⏳ 待双端实测 |

### 3.2 待实测后拍板 —— P1 行为适配(实验版未定)

| # | 待定项 | 决策条件 | 备注 |
|---|---|---|---|
| P1-1 | 参考系重居中叠加(双重平移/错位) | 双端远距离触发重居中实测 | **反编译新证据(2026-09-13)**:1.4.2 `CraftNode.RecalculateFrameState`(:662-690)只在 `_craftScript.IsPhysicsEnabled` 时调用 `RecenterTransformOnCoM(true, pendingRecenterDelta)`(:683-688);mod 的远程船恒为物理禁用(`ApplyRemoteState` 里 `SetPhysicsEnabled(false, Warp)`),故**游戏重居中路径不会移动远程幽灵船** → 双重平移风险大幅降低。仍建议 V3 实测确认 |
| P1-2 | GroundedSurface hack × `SetPose` 接地放置 | 地面幽灵船实测 | **反编译新证据(2026-09-13)**:`CraftNode.GroundedSurfacePosition/Velocity/Rotation`(private set)在 1.4.2 **仍存在**(:334-344),mod 反射写**不会静默失效**;`SetPose` 只在 warp(:669)/生成(:883/:889)/MapView(:1047)路径调用,接地 Update 分支(:1235-1240)仍读 `GroundedSurface*` + `SetStateVectorsAtDefaultTime` → 接地机制行为未变,待 V4 实测 |
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
| 刷新程序集 + 重编译(RecenterTransformOnCoM) | **确定要做** | 2026-09-03 | ✅ 2026-09-13 完成(用户刷新,编译通过) |
| EnforceRemoteCraftVisuals → `GetComponentsInCraft` | **确定要做** | 2026-09-03 | ✅ 2026-09-13 完成 |
| CraftUtils 粒子遍历 → 逐 body | **确定要做** | 2026-09-03 | ✅ 2026-09-13 完成 |
| 参考系重居中叠加适配 | **待实测后定** | 2026-09-03 | ❓ 待定;反编译证据:1.4.2 游戏只在 `IsPhysicsEnabled` 时调 `RecenterTransformOnCoM`,远程幽灵船物理禁用 ⇒ 双重平移风险低 |
| GroundedSurface × `SetPose` 接地适配 | **待实测后定** | 2026-09-03 | ❓ 待定;反编译证据:`GroundedSurface*` 属性仍存在,反射写不失效;`SetPose` 不覆盖接地分支 |
| 游戏版本检查 / 实验版开关 | **待定**(看 1.4.2 转正策略) | 2026-09-03 | ❓ 待定 |
| 调试设施接入(GameLoopTypeProfiler 等) | **观察,暂不接** | 2026-09-03 | ❓ 待定 |

> 说明:以上「待定」项均因 1.4.2 为实验版、行为可能再变而未拍板;转正或实测出现明确结论后,更新本表并同步 `README.md` 决策速查。

---

## 七、与现有 plan 的关系

| 文档 | 关系 |
|---|---|
| [`body-sync-2026-08-18.md`](archive/body-sync-2026-08-18.md) / [`latency-smoothing-2026-08-22.md`](archive/latency-smoothing-2026-08-22.md) / [`part-switch-sync-2026-08-18.md`](archive/part-switch-sync-2026-08-18.md) / [`vizzy-isolation-2026-08-22.md`](archive/vizzy-isolation-2026-08-22.md) | 既有机制的**版本回归**对象(P0-4);本方案不改动其方案 |
| [`multi-craft-sync-2026-08-16.md`](multi-craft-sync-2026-08-16.md) | 其研究基于 1.4.102 反编译(`jnoCode`);1.4.2 后新结论需在本方案下复核(如 body 脱离层级对多 craft 同步的影响) |
| [`AGENT_CONTEXT.md`](AGENT_CONTEXT.md) | §4「游戏内部 API 依赖反编译源码导航,游戏更新可能破坏,需固定版本」——本文档即该风险的专项记录 |

---

## 八、备注

- 1.4.2 反编译目录:`C:\renko\shitProgram\jnoCode`(即 1.4.200,`Game.Version = (1,4,200,0)`;本文档早期所述 `jnoCode1.4.2` 目录不存在,1.4.2 反编译就在 `jnoCode`,1.4.102 已无独立反编译目录);完整 diff 报告:`C:\renko\shitProgram\jnoCode\.diff_1.4.2_report.txt`(逐文件、剔除 Token 噪音)。
- 1.4.2 实际 DLL 位置:`C:\Program Files (x86)\Steam\steamapps\common\SimpleRockets2\SimpleRockets2_Data\Managed\SimpleRockets2.dll` / `ModApi.dll`(2026-09-02,已含 `GetComponentsInCraft`/`SetPose`/`pendingRecenterDelta`,即 1.4.2 实验版)。
- devb 原话要点:高度实验性、触及核心机制、可能破坏、备份后测试——与本方案「保留回退 + 分档决策」一致。
- 后续若 1.4.2 转正/出 1.4.3,把本 plan 的 P1 决策更新后移入 `archive/`。
