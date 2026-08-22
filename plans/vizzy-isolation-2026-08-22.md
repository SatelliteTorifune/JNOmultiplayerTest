# Vizzy 联机隔离方案：阻止跨 Craft 数据传输

> 项目:JNOmultiplayerTest(SimpleRockets 2 / JNO 联机 mod aMptest)
> 状�?�?已实�?�?Harmony patch:`BroadcastMessage` + `FlightUpdate`,�?`Enabled` 开�?默认开�?,待游戏内双端实测
> 关联:本方案基�?[`multi-craft-sync-2026-08-16.md`](multi-craft-sync-2026-08-16.md) §8.2-5 决策「MVP 不做 Vizzy 同步」的进一步扩展——不仅不同步，还�?*主动阻止**联机下跨 craft �?Vizzy 数据传递�?
---

## 一、背景与问题

### 1.1 Vizzy 广播机制（反编译确认�?
游戏 Vizzy 编程系统通过 `FlightProgramScript.BroadcastMessage` 实现消息广播，有三个作用域（[`BroadcastScope.cs`](file:///C:/renko/shitProgram/jnoCode/ModApi/Craft/Program/Craft/BroadcastScope.cs)）：

| 作用�?| 行为 | 联机风险 |
|---|---|---|
| `BroadcastScope.Program` | 仅发送给**同一�?FlightProgram**（自收） | �?|
| `BroadcastScope.Craft` | 发送给**同一�?craft 上所�?FlightProgram** | 无（craft 内隔离天然正确） |
| `BroadcastScope.AllCrafts` | 发送给**场景中所有已加载 CraftNode 的所�?FlightProgram** | **�?*——跨 craft/跨玩�?|

关键代码路径（[`FlightProgramScript.cs:76-112`](file:///C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/FlightProgramScript.cs:76)）：

```csharp
public void BroadcastMessage(BroadcastScope scope, string messageName, ExpressionResult data)
{
    if (scope == BroadcastScope.Program) { this.OnReceiveMessage(...); return; }
    if (scope == BroadcastScope.Craft) { /* 遍历�?craft 所�?FlightProgramScript */ return; }
    if (scope == BroadcastScope.AllCrafts)
    {
        // ⚠️ 遍历场景�?ALL CraftNode（包括其他玩家的真实飞船 + 远程幽灵船）
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

假设 A �?B 联机�?
1. **A �?Vizzy 广播�?B 的真实飞�?*：A �?craft 上运�?`BroadcastMessage(AllCrafts, "foo", data)` �?遍历 `DynamicNodes` 会命�?B �?craft（B �?craft 也在同一场景中加载）�?B �?Vizzy 收到消息并执行逻辑 �?**A 的代码影响了 B 的游戏状�?*�?
2. **A �?Vizzy 广播�?A 的幽灵船（在 B 端）**：B 的机器上加载�?A 的远程幽灵船（`_remoteCrafts` 中的 `RemoteCraft`）。如�?B 端游戏仍在跑幽灵船的 Vizzy（虽然目前物理关了，�?`FlightProgramScript` 可能仍被 `FlightUpdate` 驱动），A 的广播在 B 端也会打到幽灵船 �?幽灵船执行逻辑可能产生副作用�?
3. **MpNetworkManager 的广�?*：A �?Vizzy 广播�?B �?craft 后，B �?craft 状态变化又�?`MpNetworkManager` 采样广播�?A �?**双向污染反馈循环**�?
### 1.3 当前状�?
- `multi-craft-sync-2026-08-16.md` §8.2-5 决策�?*MVP 不做 Vizzy 同步**（幽灵物理关，Vizzy 不跑）�?- 但该决策只覆盖了"不同�?Vizzy 状态数�?�?*没有覆盖"阻止 Vizzy �?craft 广播"**�?- 目前如果两个玩家联机，任意一�?Vizzy �?`AllCrafts` 广播，都会打到对方飞船�?
---

## 二、方案设�?
### 2.1 核心思路

**Harmony Prefix 拦截 `FlightProgramScript.BroadcastMessage`**，在 `BroadcastScope.AllCrafts` 分支�?*过滤掉远程玩�?幽灵�?craft**，使 `AllCrafts` 在联机下等价于「所有本地玩家的 craft」（而非字面�?所有场�?craft"）�?
### 2.2 拦截�?
| 项目 | 内容 |
|---|---|
| 目标方法 | `FlightProgramScript.BroadcastMessage(BroadcastScope, string, ExpressionResult)` |
| 拦截方式 | Harmony Prefix（返�?`false` 跳过原方法，�?Prefix 自行实现过滤后的逻辑�?|
| 命名空间 | `Assets.Scripts.Craft.Parts.Modifiers.FlightProgramScript` |
| 所在程序集 | `SimpleRockets2.dll`（游戏本体，�?ModApi�?|
| Patch 文件 | `Assets/Scripts/HarmonyPatches/VizzyIsolationPatch.cs`（新建） |

### 2.3 过滤逻辑

```
if (scope == BroadcastScope.AllCrafts && MpNetworkManager.Instance != null && MpNetworkManager.Instance.IsConnected)
{
    // 联机会话中：AllCrafts �?只广播到"本地玩家拥有�?craft"
    // 遍历 DynamicNodes，对每个 CraftNode�?    //   - 如果是远程幽灵船（MpNetworkManager.IsRemoteCraftNode(node)）→ 跳过
    //   - 如果是其他玩家的真实飞船（将来多 craft 场景）→ 跳过
    //   - 否则（本地玩家自己的 craft）→ 正常发�?    
    // MVP 阶段：本地玩家只有一�?craft，等价于降级�?Craft 作用�?    // 将来�?craft 阶段：改为遍�?本地玩家拥有的所�?craft Guid 列表"
}
```

### 2.4 MVP 简化版

由于当前 MVP 阶段每玩家只有一�?craft（多 craft 同步仍在方案研究阶段），**MVP 实现可直接将 `AllCrafts` 降级�?`Craft`**�?
```csharp
// 伪代�?static bool Prefix(FlightProgramScript __instance, BroadcastScope scope, string messageName, ExpressionResult data)
{
    if (scope == BroadcastScope.AllCrafts && IsInMultiplayerSession())
    {
        // 降级：AllCrafts �?Craft（同一�?craft 内广播）
        BroadcastToSameCraft(__instance, messageName, data);
        return false; // 跳过原方�?    }
    return true; // 非联机或�?AllCrafts，走原逻辑
}
```

### 2.5 升级路径（多 craft 同步后）

�?`multi-craft-sync-2026-08-16.md` �?MC1~MC4 里程碑落地后（每玩家可拥有多�?craft，有 Guid 身份体系）：

- 为该 Patch 增加「本地玩�?craft Guid 集合」查询；
- `AllCrafts` 遍历 `DynamicNodes` 时，只向**匹配本地 Guid 集合**�?craft 发送；
- 不再�?降级�?Craft"，而是真正�?All MY Crafts"�?
---

## 三、实现清�?
### 3.1 新建文件

| 文件 | 说明 | 状�?|
|---|---|---|
| `Assets/Scripts/HarmonyPatches/VizzyIsolationPatch.cs` | 三个�?`VizzyIsolationPatch`(开�? + `VizzyIsolationPatch_Broadcast`(广播隔离) + `VizzyIsolationPatch_FlightUpdate`(幽灵�?Vizzy 执行拦截) | �?已创�?|

### 3.2 Patch 详解

| Patch | 目标方法 | 行为 |
|---|---|---|
| `VizzyIsolationPatch_Broadcast` | `FlightProgramScript.BroadcastMessage` | 联机�?`AllCrafts` 降级为仅�?craft 广播 |
| `VizzyIsolationPatch_FlightUpdate` | `FlightProgramScript.FlightUpdate` | 远程幽灵船跳过整�?Vizzy 执行(封堵 `RequestUserInput`/`SetTimeMode`/`SetCameraProperty` 等所有侧信道) |

### 3.3 修改文件

| 文件 | 改动 | 状�?|
|---|---|---|
| `plans/README.md` | 活跃文档表新�?`vizzy-isolation-2026-08-22.md` 条目；决策速查表新�?Vizzy 隔离决策 | �?已完�?|

### 3.4 无需修改

- `Mod.cs`：`harmony.PatchAll()` 已自动发�?`[HarmonyPatch]` 标记的类，无需手动 Apply�?- `MpNetworkManager.cs`：已有的 `IsRemoteCraftNode` / `IsConnected` 可直接复用�?
### 3.5 开关设�?
- `VizzyIsolationPatch.Enabled`（`public static bool`，默�?`true`）同时控制两�?patch�?- `false` 时恢复全部原生行为，供未来多 craft 场景按需开启�?- 修改方式：任何代码直�?`VizzyIsolationPatch.Enabled = false` 即可，无需重启�?
---

## 四、边界情�?
| 场景 | 处理 |
|---|---|
| **非联机（单人�?* | 不走 Prefix，`AllCrafts` 行为不变（本来就是所�?craft�?|
| **联机�?Transport 未连�?* | `IsConnected == false` �?不走过滤，原逻辑 |
| **飞行场景未加�?* | `FlightSceneScript.Instance == null` �?�?`AllCrafts` 遍历可能 NRE，但非本方案引入；Prefix 内加空检�?|
| **幽灵船的 Vizzy 仍在�?* | �?已由 `VizzyIsolationPatch_FlightUpdate` 处理：远程幽灵船�?`FlightProgramScript.FlightUpdate` 直接 return，所�?Vizzy 指令（`RequestUserInput`/`SetTimeMode`/`SetCameraProperty`/`PlayBeepSound`/`ActivateNextStage` 等）均不执行 |
| **Vizzy 变量/列表读写** | 每个 `FlightProgramScript` 有自己的 `FlightProgram.GlobalVariables`，天然隔离，无需处理 |
| **`CraftService` 其他�?craft API** | �?`SetTarget`（按名称查找 craft）、`ChangePlayersActiveCommandPodImmediate`（已�?multi-craft-sync §8.1-3 �?Harmony 总入口拦截覆盖）——非本方案范�?|

---

## 五、决策记�?
| 决策 | 结论 | 日期 |
|---|---|---|
| 拦截方式 | �?Harmony Prefix:`BroadcastMessage`(AllCrafts→Craft) + `FlightUpdate`(幽灵船直接跳�? | �?已实�?|
| 开关控�?| `VizzyIsolationPatch.Enabled`（`public static bool`，默�?`true`），同时控制两个 patch | �?已实�?|
| �?craft 升级 | �?multi-craft-sync 落地�?Guid 体系后，BroadcastMessage 改为"All MY Crafts"过滤 | 将来 |
| 幽灵�?Vizzy 执行 | �?已由 FlightUpdate patch 彻底禁止，一劳永�?| �?已实�?|

---

## 六、与现有 plan 的关�?
| 文档 | 关系 |
|---|---|
| [`multi-craft-sync-2026-08-16.md`](multi-craft-sync-2026-08-16.md) §8.2-5 | 原决策「MVP 不做 Vizzy 同步」——本方案是此决策�?*安全加固**：不仅不同步，还要阻止跨 craft 广播 |
| [`multi-craft-sync-2026-08-16.md`](multi-craft-sync-2026-08-16.md) §8.1-3 | 已规�?Harmony 拦截 `ChangePlayersActiveCommandPodImmediate` 防劫持——与本方案同属「联机安�?Harmony patch」系�?|
| [`part-switch-sync-2026-08-18.md`](part-switch-sync-2026-08-18.md) | 无关（部件开关同步） |
| [`body-sync-2026-08-18.md`](body-sync-2026-08-18.md) | 无关（body 位姿同步�?|
| [`latency-smoothing-2026-08-22.md`](latency-smoothing-2026-08-22.md) | 无关（延迟平滑） |

(End of file - total 149 lines)