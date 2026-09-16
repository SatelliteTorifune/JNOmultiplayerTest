# Vizzy 联机隔离方案:阻止跨 Craft 数据传输 + 禁止幽灵船 Vizzy 执行

> 项目:JNOMultiPlayer(SimpleRockets 2 / JNO 联机 mod MultiPlayer)
> 状态:✅ **已归档**(原状态:已实现——Harmony patch `BroadcastMessage` + `FlightUpdate`,含 `Enabled` 开关,默认开启;游戏内双端实测见 README 归档表备注)
> **1.4.2 复核(2026-09):未失效**,并已加固两个缺口(G1 生成窗口 / G2 广播静默丢弃)——见本文 §七;核证明细见 [`update-1.4.2-experimental-2026-09-03.md`](update-1.4.2-experimental-2026-09-03.md) 「〇之五」。
> 关联:本方案基于 [`proposals/multi-craft-sync-2026-08-16.md`](../proposals/multi-craft-sync-2026-08-16.md) §8.2-5 决策「MVP 不做 Vizzy 同步」的进一步扩展——不仅不同步,还**主动阻止**联机下跨 craft 的 Vizzy 数据传递。

---

## 一、背景与问题

### 1.1 Vizzy 广播机制(反编译确认)

游戏 Vizzy 编程系统通过 `FlightProgramScript.BroadcastMessage` 实现消息广播,有三个作用域(`BroadcastScope.cs`):

| 作用域 | 行为 | 联机风险 |
|---|---|---|
| `BroadcastScope.Program` | 仅发送给**同一个 FlightProgram**(自收) | 无 |
| `BroadcastScope.Craft` | 发送给**同一个 craft 上所有 FlightProgram** | 无(craft 内隔离天然正确) |
| `BroadcastScope.AllCrafts` | 发送给**场景中所有已加载 CraftNode 的所有 FlightProgram** | ⚠️ **高**——跨 craft/跨玩家 |

关键代码路径(`FlightProgramScript.cs:76-112`):

```csharp
public void BroadcastMessage(BroadcastScope scope, string messageName, ExpressionResult data)
{
    if (scope == BroadcastScope.Program) { this.OnReceiveMessage(...); return; }
    if (scope == BroadcastScope.Craft) { /* 遍历同 craft 所有 FlightProgramScript */ return; }
    if (scope == BroadcastScope.AllCrafts)
    {
        // ⚠️ 遍历场景中 ALL CraftNode(包括其他玩家的真实飞船 + 远程幽灵船)
        foreach (CraftNode craftNode in Game.Instance.FlightScene
            .ViewManager.GameView.PlanetNode.DynamicNodes.OfType<CraftNode>())
        {
            if (craftNode.IsLoadedInGameView)
            {
                foreach (FlightProgramScript fps in (craftNode.CraftScript as CraftScript).FlightProgramScripts)
                {
                    fps.OnReceiveMessage(messageName, data);
                }
            }
        }
    }
}
```

### 1.2 联机场景下的具体问题

假设 A 与 B 联机:

1. **A 的 Vizzy 广播打到 B 的真实飞船**:A 的 craft 上运行 `BroadcastMessage(AllCrafts, "foo", data)` 时遍历 `DynamicNodes` 会命中 B 的 craft(B 的 craft 也在同一场景中加载)→ B 的 Vizzy 收到消息并执行逻辑 → **A 的代码影响了 B 的游戏状态**。
2. **A 的 Vizzy 广播打到 A 的幽灵船(在 B 端)**:B 的机器上加载着 A 的远程幽灵船(`_remoteCrafts` 中的 `RemoteCraft`)。如果 B 端游戏仍在跑幽灵船的 Vizzy(虽然目前物理关了,但 `FlightProgramScript` 可能仍被 `FlightUpdate` 驱动),A 的广播在 B 端也会打到幽灵船 → 幽灵船执行逻辑可能产生副作用。
3. **MpNetworkManager 的广播**:A 的 Vizzy 广播打到 B 的 craft 后,B 的 craft 状态变化又被 `MpNetworkManager` 采样广播回 A → **双向污染反馈循环**。

### 1.3 当前状态

- `proposals/multi-craft-sync-2026-08-16.md` §8.2-5 决策:**MVP 不做 Vizzy 同步**(幽灵物理关,Vizzy 不跑)。
- 但该决策只覆盖了"不同步 Vizzy 状态数据",**没有覆盖"阻止 Vizzy 跨 craft 广播"**。
- 目前如果两个玩家联机,任意一方的 Vizzy 做 `AllCrafts` 广播,都会打到对方飞船。

---

## 二、方案设计

### 2.1 核心思路

**Harmony Prefix 拦截 `FlightProgramScript.BroadcastMessage`**,在 `BroadcastScope.AllCrafts` 分支**过滤掉远程玩家的幽灵船 craft**,使 `AllCrafts` 在联机下等价于「所有本地玩家的 craft」(而非字面"所有场景 craft")。

### 2.2 拦截点

| 项目 | 内容 |
|---|---|
| 目标方法 | `FlightProgramScript.BroadcastMessage(BroadcastScope, string, ExpressionResult)` |
| 拦截方式 | Harmony Prefix(返回 `false` 跳过原方法,在 Prefix 自行实现过滤后的逻辑) |
| 命名空间 | `Assets.Scripts.Craft.Parts.Modifiers.FlightProgramScript` |
| 所在程序集 | `SimpleRockets2.dll`(游戏本体,非 ModApi) |
| Patch 文件 | `Assets/Scripts/HarmonyPatches/VizzyIsolationPatch.cs`(新建) |

### 2.3 过滤逻辑

```
if (scope == BroadcastScope.AllCrafts && MpNetworkManager.Instance != null && MpNetworkManager.Instance.IsConnected)
{
    // 联机会话中:AllCrafts → 只广播到"本地玩家拥有的 craft"
    // 遍历 DynamicNodes,对每个 CraftNode:
    //   - 如果是远程幽灵船（MpNetworkManager.IsRemoteCraftNode(node)）→ 跳过
    //   - 如果是其他玩家的真实飞船（将来多 craft 场景）→ 跳过
    //   - 否则（本地玩家自己的 craft）→ 正常发送
    // MVP 阶段：本地玩家只有一个 craft，等价于降级为 Craft 作用域。
    // 将来多 craft 阶段：改为遍历"本地玩家拥有的所有 craft Guid 列表"
}
```

### 2.4 MVP 简化版

由于当前 MVP 阶段每玩家只有一个 craft(多 craft 同步仍在方案研究阶段),**MVP 实现可直接将 `AllCrafts` 降级为 `Craft`**:

```csharp
// 伪代码
static bool Prefix(FlightProgramScript __instance, BroadcastScope scope, string messageName, ExpressionResult data)
{
    if (scope == BroadcastScope.AllCrafts && IsInMultiplayerSession())
    {
        // 降级:AllCrafts → Craft(同一个 craft 内广播)
        BroadcastToSameCraft(__instance, messageName, data);
        return false; // 跳过原方法
    }
    return true; // 非联机或非 AllCrafts,走原逻辑
}
```

### 2.5 升级路径(多 craft 同步后)

当 `proposals/multi-craft-sync-2026-08-16.md` 的 MC1~MC4 里程碑落地后(每玩家可拥有多个 craft,有 Guid 身份体系):

- 为该 Patch 增加「本地玩家的 craft Guid 集合」查询;
- `AllCrafts` 遍历 `DynamicNodes` 时,只向**匹配本地 Guid 集合**的 craft 发送;
- 不再"降级为 `Craft`",而是真正的 "All MY Crafts"。

---

## 三、实现清单

### 3.1 新建文件

| 文件 | 说明 | 状态 |
|---|---|---|
| `Assets/Scripts/HarmonyPatches/VizzyIsolationPatch.cs` | 三个类:`VizzyIsolationPatch`(开关) + `VizzyIsolationPatch_Broadcast`(广播隔离) + `VizzyIsolationPatch_FlightUpdate`(幽灵船 Vizzy 执行拦截) | ✅ 已创建 |

### 3.2 Patch 详解

| Patch | 目标方法 | 行为 |
|---|---|---|
| `VizzyIsolationPatch_Broadcast` | `FlightProgramScript.BroadcastMessage` | 联机下 `AllCrafts` 降级为仅同 craft 广播(实现方式:Prefix 里直接调 `__instance.PartScript.CraftScript.FlightProgramScripts` 逐个 `OnReceiveMessage`,然后 `return false` 跳过原方法) |
| `VizzyIsolationPatch_FlightUpdate` | `FlightProgramScript.FlightUpdate` | 远程幽灵船跳过整个 Vizzy 执行(封堵 `RequestUserInput`/`SetTimeMode`/`SetCameraProperty` 等所有侧信道) |

> 实现细节:`Broadcast` Prefix 里对 `__instance.PartScript`/`CraftScript` 做了空值防护,同 craft 回退路径包在 try/catch 中;异常只记 `Mod.LogError("VizzyIsolation/Broadcast: ...")` 并**仍然 `return false`**(宁可少广播,不让污染流出去)。`FlightUpdate` Prefix 的异常分支相反:记 `Mod.LogError("VizzyIsolation/FlightUpdate: ghost check failed, allowing")` 并 `return true`(宁可放行,不误杀真实飞船的 Vizzy)。

### 3.3 修改文件

| 文件 | 改动 | 状态 |
|---|---|---|
| `plans/README.md` | 活跃文档表新增 `vizzy-isolation-2026-08-22.md` 条目;决策速查表新增 Vizzy 隔离决策 | ✅ 已完成 |

### 3.4 无需修改

- `Mod.cs`:`harmony.PatchAll()` 已自动发现 `[HarmonyPatch]` 标记的类,无需手动 Apply。
- `MpNetworkManager.cs`:已有的 `IsRemoteCraftNode` / `IsConnected` 可直接复用。

### 3.5 开关设计

- `VizzyIsolationPatch.Enabled`(`public static bool`,默认 `true`)同时控制两个 patch。
- `false` 时恢复全部原生行为,供未来多 craft 场景按需开启。
- 修改方式:任何代码直接写 `VizzyIsolationPatch.Enabled = false` 即可,无需重启。

---

## 四、边界情况

| 场景 | 处理 |
|---|---|
| **非联机(单人)** | 不走 Prefix,`AllCrafts` 行为不变(本来就是所有 craft) |
| **联机但 Transport 未连接** | `IsConnected == false` → 不走过滤,原逻辑 |
| **飞行场景未加载** | `FlightSceneScript.Instance == null` 时 `AllCrafts` 遍历可能 NRE,但非本方案引入;Prefix 内加空检查 |
| **幽灵船的 Vizzy 仍在跑** | ✅ 已由 `VizzyIsolationPatch_FlightUpdate` 处理:远程幽灵船的 `FlightProgramScript.FlightUpdate` 直接 return,所有 Vizzy 指令(`RequestUserInput`/`SetTimeMode`/`SetCameraProperty`/`PlayBeepSound`/`ActivateNextStage` 等)均不执行 |
| **Vizzy 变量/列表读写** | 每个 `FlightProgramScript` 有自己的 `FlightProgram.GlobalVariables`,天然隔离,无需处理 |
| **`CraftService` 其他跨 craft API** | ⚠️ `SetTarget`(按名称查找 craft)、`ChangePlayersActiveCommandPodImmediate`(已由 multi-craft-sync §8.1-3 的 Harmony 总入口拦截覆盖)——非本方案范围 |

---

## 五、决策记录

| 决策 | 结论 | 日期 |
|---|---|---|
| 拦截方式 | ✅ Harmony Prefix:`BroadcastMessage`(AllCrafts→Craft) + `FlightUpdate`(幽灵船直接跳过) | ✅ 已实现 |
| 开关控制 | `VizzyIsolationPatch.Enabled`(`public static bool`,默认 `true`),同时控制两个 patch | ✅ 已实现 |
| 多 craft 升级 | 等 multi-craft-sync 落地 Guid 体系后,BroadcastMessage 改为 "All MY Crafts" 过滤 | 将来 |
| 幽灵船 Vizzy 执行 | ✅ 已由 FlightUpdate patch 彻底禁止,一劳永逸 | ✅ 已实现 |

---

## 六、与现有 plan 的关系

| 文档 | 关系 |
|---|---|
| [`proposals/multi-craft-sync-2026-08-16.md`](../proposals/multi-craft-sync-2026-08-16.md) §8.2-5 | 原决策「MVP 不做 Vizzy 同步」——本方案是此决策的**安全加固**:不仅不同步,还要阻止跨 craft 广播 |
| [`proposals/multi-craft-sync-2026-08-16.md`](../proposals/multi-craft-sync-2026-08-16.md) §8.1-3 | 已规划 Harmony 拦截 `ChangePlayersActiveCommandPodImmediate` 防劫持——与本方案同属「联机安全 Harmony patch」系列 |
| [`part-switch-sync-2026-08-18.md`](part-switch-sync-2026-08-18.md) | 无关(部件开关同步) |
| [`body-sync-2026-08-18.md`](body-sync-2026-08-18.md) | 无关(body 位姿同步) |
| [`latency-smoothing-2026-08-22.md`](latency-smoothing-2026-08-22.md) | 无关(延迟平滑) |

---

## 七、1.4.2 复核与缺口加固(2026-09,归档后追加)

> 触发:P0-4 回归项要求确认本机制在 1.4.2(Experimental,`Game.Version = 1.4.200`)上未失效。
> 核证方式:用 Mono.Cecil 直接读**安装目录实际运行的** `SimpleRockets2.dll`,与 mod 编译参照程序集逐条比对 IL 文本(不依赖反编译目录,避免"目录 ≠ 安装版本")。

### 7.1 未失效的核证结论(全部为实测)

| 依赖点 | 1.4.2 实测 |
|---|---|
| `BroadcastMessage(BroadcastScope,string,ExpressionResult)` | 存在;108 条 IL **逐条文本与参照程序集完全相同** ⇒ patch 1 仍正确挂载 |
| `FlightUpdate(in FlightFrameData)` | 存在;139 条 IL 逐条相同 ⇒ patch 2 仍正确挂载 |
| `BroadcastScope` 枚举 | 游戏与参照 `ModApi.dll` 均为 `Program=0 / Craft=1 / AllCrafts=2` ⇒ §2.4 的作用域判断不错位 |
| `CraftScript.FlightProgramScripts` | 仍为 `IReadOnlyList<FlightProgramScript>` ⇒ patch 1 的同 craft 回退路径可用 |
| 接口分发 | `FlightProgramScript` 仍声明 `IFlightStart / IGameLoopItem / IFlightUpdate`,显式实现转发到 public 方法;`FlightGameLoop.Update` 仍注册于 `_scripts.Update` 并调用 `x.FlightUpdate(in frame)` ⇒ patch 会被调用 |
| **执行侧信道是否穷尽** | `Process.Update`(Vizzy 指令唯一执行入口)在 1.4.2 **全局仅 1 处调用者** = public `FlightUpdate`(`FlightProgramScript.cs:171`);`StartProgram` 仅反序列化 XML,不执行指令 ⇒ patch 2 是完整封堵 |
| `FlightUpdatePaused` | `FlightProgramScript` **不实现** `IFlightUpdatePaused`,暂停时游戏只跑该接口 ⇒ 暂停下 Vizzy 本就不执行,**无需额外 patch**(§四"一劳永逸封堵所有侧信道"的表述据此收严:准确说是"执行入口唯一,已封堵") |
| `CraftService.BroadcastMessage`(`CraftService.cs:250`) | Vizzy 指令的直通包装 → 仍进 patch 1,无绕过口 |
| 运行时日志 | `Player.log`(2026-09-15,MultiPlayer 1.51)含 `remote craft initialized (ghost mode)`,且 **0 条 `VizzyIsolation/…` 报错 / 0 条 `MissingMethodException`** |

### 7.2 已加固的三个缺口(只加固、不改机制)

| # | 缺口(与 1.4.2 无关) | 加固 |
|---|---|---|
| G1 | **生成窗口**:`MpNetworkManager.SpawnRemoteCraftAtPosition` 中 `SpawnCraft` 返回时 craft 已进场景,而 `_remoteCrafts[playerId] = rc` 赋值在其后;若中间抛异常(该路径整体包 try/catch),该幽灵永久留场且 `IsRemoteCraftNode` 恒为 false ⇒ 两个 patch 对它双双失效 | 幽灵判定提升为 `VizzyIsolationPatch.IsGhostCraft(IPartScript)`,并加 ③ **命名兜底** = 「对方玩家名 + 竖线 + 船名」(`MpNetworkManager.cs:1634`;`CraftNode.Name` 已核实存在)逐在册玩家比对。命中即拦 |
| **G3** | **断线/移除窗口**(双端实测后确认,2026-09-16):`RemoveRemoteCraft` 先 `_remoteCrafts.Remove(playerId)` 再 `rc.Node.DestroyCraft()`;而 `DestroyCraft()` 只置 `IsDestroyed = true`,节点要到**下一帧** `FlightState.ProcessDestroyedCraftNodes()` 才真正移除。若该移除发生在当帧 `FlightUpdate` **之前**,这个"已销毁但仍在场景"的幽灵会被 `FlightProgramScript.FlightUpdate` 跑一次 ⇒ **漏执行 + 漏广播**(此刻 `IsConnected` 仍为 true,patch 1 的降级也在但它已无同 craft 目标) | 判定升级为**三层**:新增 ② **NodeId 记忆** —— `IsGhostCraft` 在 ① 命中时把 `node.NodeId` 记入 `_ghostNodeIds`,① 失效后记忆继续拦截;命中时记一次日志 `VizzyIsolation: ghost nodeId=N still guarded after removal (destroy window)`。记忆按**飞行场景生命周期**重置(`MpNetworkManager.OnFlightSceneLoaded` → `ClearGhostNodeCache()`),因 NodeId 只在一次飞行内唯一(游戏 `FlightState.GetNextNodeId()` 单调分配) |
| G2 | **广播静默丢弃**:原 `craft == null` 分支直接 `return false`,不留痕,与 patch 2"记录并放行"方向相反,触发后难以定位 | 丢弃时记一次性日志(按 `FlightProgramScript` 实例 ID 去重)`VizzyIsolation/Broadcast: dropped AllCrafts broadcast '…'` |

> 设计取向未变:patch 2 异常分支仍"记录并**放行**"(宁可放行、不误杀真实飞船的 Vizzy);仅广播路径在无法定位同 craft 时"宁可少广播"。

### 7.2b 断线场景的实测证据(2026-09-16,宿主 + VM 客户端各一份 Player.log)

| 侧 | 关键日志 | 判读 |
|---|---|---|
| 宿主 | `MP: spawned remote craft … player 1` → `MP: remote craft initialized (ghost mode) for player 1` | 新 build 生效,幽灵正常生成并被 ① 命中 |
| 宿主 | `TcpTransport: peer 127.0.0.1:57975 read loop ended` → `MP peer timeout: … (PlayerId=1, NodeId=3)` → `MP peer timeout: broadcast PlayerLeave playerId=1` → `MP: destroyed remote craft for player 1, nodeId=2158, inFlightState=True` | **G3 现场**:`DestroyCraft()` 在当帧执行,节点下一帧才消失 ⇒ 中间那一帧的 Vizzy 执行/广播原实现拦不住 |
| VM 客户端 | `MP spawnDiag p0: goActive=null, craftScript=notBuilt, renderers=0/…` | 幽灵生成是异步的:节点先在、`CraftScript`/`GameObject` 后建 ⇒ `IsRemoteCraftNode` 之外必须有兜底(G1 的命名兜底正是覆盖这一段) |
| 两侧 | **0 条 `VizzyIsolation/…` 报错、0 条 `MissingMethodException`** | patch 挂载无误、未抛异常 |

### 7.2c 复测结果(2026-09-16 第二轮,新 build 3:54:34)与「输入框」症状定论

**G3 现场复现并被拦住(修复生效的直接证据)**:

| 侧 | 命中次数 | 日志 |
|---|---|---|
| 宿主 | 2 | `VizzyIsolation: ghost nodeId=2159 still guarded after removal (destroy window)`(玩家 1 被踢)<br>`VizzyIsolation: ghost nodeId=2160 still guarded after removal (destroy window)`(玩家 2 掉线) |
| VM 客户端 | 1 | `VizzyIsolation: ghost nodeId=5 still guarded after removal (destroy window)`(宿主掉线) |

⇒ G3 不再是推测:`DestroyCraft()` 后确实存在"节点仍在场景"的一帧,② NodeId 记忆把它拦住了。两侧 `VizzyIsolation` 报错仍为 **0**。

**用户报告症状「对方用需要输入的组件时,我这里也弹输入框」——已定位:是本地船自己弹的,不是幽灵船**:

```
(客户端) VizzyIsolation/UserInput: craft='New' nodeId=4 craftScript=yes isGhost=False
         [registry=False nodeIdMemo=False nameFallback=False] ⇒ local craft (patch 无责)     ← 出现 2 次
(宿主)   同一诊断 0 次
```

- `craft='New' nodeId=4` = **客户端自己的本地船**(其 `LocalNodeId=4`,见 `MP.Join SUCCESS: … LocalNodeId=4`);
  宿主的幽灵在客户端是 `nodeId=2158 / localNode=5 / craft='J-10-ABlockB'`(358 部件卫星)。
- 三层判定全 false + `isGhost=False` ⇒ 执行者是本地船。**隔离未漏**,patch 无责。
- 结论:`UserInputInstruction` 弹框的唯一路径就是本地 `CraftService.RequestUserInput`(`CraftService.cs:548-585`),
  所以"弹框"必然来自本机某条船的 Vizzy;本轮证据指向本地船自己。
- 若用户仍认为弹框与对方动作**严格同步**,剩下唯一解释是"两台机器跑着同一份带输入指令的程序"
  (各自本地执行、各自弹框),而不是跨 craft 泄漏 —— 需要用户自查该程序是否两边都有。

> 诊断(`VizzyIsolationPatch_UserInputDiag`)为**只读、不改变行为**,已保留:后续若再报同类症状,看这一行的
> `isGhost` 与方括号三层结果即可立刻定性。

**第三轮实测(2026-09-16,build 4:04:56)—— 症状"消失"了,但指令仍在执行 ⇒ 间歇性,尚未定论**

用户反馈本轮"弹框真的没弹,而且两边程序都没改"。日志事实:

| 项 | 宿主 | VM 客户端 |
|---|---|---|
| `VizzyIsolation/UserInput` | **0 次** | **1 次**(`craft='New' nodeId=5 isGhost=False ⇒ local craft`) |
| G3 守护命中 | 1 次(`nodeId=2160`) | 1 次(`nodeId=6`) |
| `MultiPlayerUI … failed` | **0** | **0**(只剩预期内日志 `inspector panel was destroyed by scene change, will rebuild on next open`) |

**关键推论(与"修好了"不同)**:

- 客户端本地船**这一轮仍然执行了** `UserInputInstruction`(诊断行照旧出现),但本轮用户**没有看到弹框**。
- 宿主侧三轮(两轮复测 + 本轮)`UserInput` **始终为 0** ⇒ "对方的输入动作触发我这边的框"这个因果链**从未在日志里出现过**。
- 因此症状是**间歇性**的,而**不是已修复**。已知的确定性因素:`CraftService.RequestUserInput` 只在
  `this._userInputRequest == null && !Game.Instance.UserInterface.AnyDialogsOpen` 时才真正建框
  (`CraftService.cs:550-553`) —— 即**当时本机是否有其它对话/输入框处于打开状态**会决定这一次是否弹出。
  这一条是目前唯一可解释"同一指令时弹时不弹"的机制,但**尚未经实测确认**,不要当成结论。
- 本轮未排查项:客户端弹框是否显示在 VM 窗口外的分辨率/位置(VM 是 800×600,日志有 `Fixing resolution` 行)。

> 结论:隔离机制(G1/G2/G3)本身**未发现问题**;输入框症状**停在此处**,既有诊断已足够在下次复现时一行定性
> (`isGhost=True` = 隔离漏,`False` = 本地船行为)。需要时的下一步见 §7.3。

### 7.2d 本轮顺带发现并修掉的 UI 生命周期缺陷(非隔离机制)

上一轮(§7.2b)已修 `OnSceneLoaded` 的假 null NRE;本轮在同一流程里又抓到它的**姊妹路径**:

```
(客户端被踢,日志 814/817)
MultiPlayerUI: ForceRebuildPanel failed: Object reference not set to an instance of an object
MultiPlayerUI: ReplaceGroup players failed: Object reference not set to an instance of an object
```

- **根因**:`Update()` 里的 `inspectorPanel != null` 对"被场景销毁的面板"仍为 true(Unity 假 null),
  而"玩家离开"事件恰好在场景卸载瞬间到达 ⇒ `ReplaceGroup`/`RebuildModelElements` 抛 NRE(被 catch 吞掉)。
- **后果**:面板静默失效且引用不清,玩家列表直到手动关开面板才恢复。
- **修复**:新增 `IsPanelAlive()`(`inspectorPanel is UnityEngine.Object uo && uo != null`),
  在 `Update()` / `ForceRebuildPanel()` / `RebuildPlayersIfChanged()` 三处入口判定:面板已销毁则清引用并跳过,
  下次打开面板时正常重建;不再产生无意义的 `failed` 日志。

| VM 客户端 | `StopLobby()` → 场景重载时 `NullReferenceException at Assets.Scripts.MultiPlayerUI.OnSceneLoaded … InspectorPanelScript.set_Visible` | **另一处真实 NRE(非隔离机制)**:`MultiPlayerUI` 是 `DontDestroyOnLoad`,但面板 GameObject 随场景卸载销毁 ⇒ `inspectorPanel != null` 仍为 true(Unity 假 null),`Visible` setter 里 `this.gameObject` 抛 NRE,**中断 `SceneLoaded` 事件链**(与 Volken 那次同类)。已修:`MultiPlayerUI.OnSceneLoaded` 加 Unity 显式销毁判定 + try/catch,异常不再冒泡 |

### 7.3 待双端实测(仍未完成)

- [ ] A 端 `AllCrafts` 广播 → B 端 craft 不应收到;B 端的 A 幽灵船不应执行;
- [ ] A 端 `AllCrafts` 广播在 A 自己船上仍正常(降级为同 craft,不能退化成完全不广播);
- [ ] 本地船 Vizzy 全功能正常(未被误杀);幽灵船无 Vizzy 进度增长;
- [x] **断线专项**:2026-09-16 复数轮实测 —— 宿主 3 次 / 客户端 3 次命中
      `still guarded after removal (destroy window)` ⇒ **G3 生效已实测确认**;
- [x] **用户输入专项(部分)**:实测定论 —— 出现过的输入框均来自 `isGhost=False` 的**本地船**;
      但症状**间歇性**(第三轮:指令执行了、框没弹),**未最终闭环**,见 §7.2c 第三轮小节;
- [ ] **(可选,未做)输入框间歇性**:卡住"时弹时不弹"的最可疑因素是
      `CraftService.RequestUserInput` 的 `!Game.Instance.UserInterface.AnyDialogsOpen` 门禁
      (`CraftService.cs:550-553`)—— 做法:复现时同时记录"当时是否有其它对话框打开",或给该门禁加一条诊断日志;
- [ ] 切场景专项:复数轮实测 `MultiPlayerUI` 的 `OnSceneLoaded` NRE 与 `ForceRebuildPanel/ReplaceGroup failed`
      **均已消失**(只剩预期内日志 `inspector panel was destroyed by scene change`)。
