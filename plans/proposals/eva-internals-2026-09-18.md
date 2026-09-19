# Juno: New Origins — Drood / EVA 内部机制技术报告

> **本文件定位**:EVA 机制的**补充参考资料**(逐方法级别的事实清单 + 引用)。
> **权威结论请以 [`eva-sync-2026-09-18.md`](eva-sync-2026-09-18.md) 为准**——方案、决策、协议设计、实施里程碑都在那里;本文件只提供底层细节支撑。
> 生成方式:反编译源码 + `ilspycmd` 对安装目录 `SimpleRockets2.dll` 复核。
> ⚠️ **本文件已被复核并修正过一处错误结论**,见文末「复核与修正(2026-09-18)」。修订前请先读该节。


> 反编译源码路径根：`C:\renko\shitProgram\jnoCode\SimpleRockets2\Assets\Scripts\`（只读参考，未修改任何文件）
> ModApi 路径根：`C:\renko\shitProgram\jnoCode\ModApi\`
> 下文所有 `file.cs:line` 均指这两个根下的相对路径。
> 目标读者：需要在多人 Mod 中同步 EVA 的工程师。

---

## 关键结论

1. **Drood/EVA 在结构上就是「一个普通 part」**：part 类型 id 为 `"Eva"` / `"Eva-Tourist"`（`Craft/Parts/Modifiers/Eva/EvaData.cs:14,156`），该 part 上挂 `CommandPodScript` + `EvaScript` + `EvaData`（`Craft/Parts/Modifiers/CommandPodScript.cs:476-477`）。它**同时**是一个合法 command pod（`IsEva == true`）。

2. **EVA 的三种存在形态（互斥）**：
   - **A. 收入 crew compartment**：`EvaScript.CrewCompartment != null`，part 与母船同属一个 body/craft node，`EvaActive == false`（`EvaScript.cs:440-446`）。
   - **B. 「坐在椅子上」但仍激活**：`ActiveWhileInCrewCompartment == true`（`EvaScript.cs:302-330`），控制方案切到 `EvaControlSchemeType.EvaInChair`（`EvaScript.cs:450-460`）。
   - **C. 真正出舱**：`EvaActive == true`，part 脱离母船，**独立成为一个 `CraftNode`**（有自己的 `NodeId`、自己的 Rigidbody、自己的 `CraftScript`）。

3. **成为独立 `CraftNode` 的时机**：不是立即的。`TakeControl()` 只做两件事——**销毁 EVA part 与 crew compartment 之间的物理关节**，然后切控制。真正创建 `CraftNode` 发生在**后续 `LateUpdate` 阶段的断体处理链**：
   `BodyJoint.Destroy()` → `CraftScript.SetStructureChanged()` → `CraftScript.IFlightLateUpdate` → `ProcessDisconnectedBodies()` → `CraftSplitter.ProcessDisconnectedBody()` → `DetermineCraftNodeEligibility()` → `MoveBodyToNewCraftNode()` → `CraftSplitter.SplitCraftNode()` → `new CraftNode(...)` + `FlightState.AddCraft()`（**NodeId 由 `FlightStateData.GetNextNodeId()` 分配**）→ 抛 `CraftNodeAdded`。

4. **回舱不是「反向 Split」，而是 `MergeCraftNode`**：`LoadIntoCrewCompartment` 内部重新建 joint 后把 EVA 的整个 assembly 并入母船 assembly，最后 `sourceCraftNode.DestroyCraft()`（`Flight/Sim/CraftSplitter.cs:75`），EVA 的 `CraftNode` 被销毁，`CraftNodeRemoved` 在**下一次 `FlightSceneScript.OnLateUpdate`** 中通过 `FlightState.ProcessDestroyedCraftNodes()` 抛出（`State/FlightState.cs:415-440`，调用点 `Flight/FlightSceneScript.cs:501`）。
   **注意：`SplitCraftNode` 不销毁老节点**（它只被「掏空」——失去 body/part/theme，`NodeId` 保留）；**`MergeCraftNode` 自身也不抛任何 FlightState 事件**，被吸收的 source 节点要等到下一次 `ProcessDestroyedCraftNodes` 扫描才产生 `CraftNodeRemoved`。

5. **联网最关键的三条**：
   - `CraftNodeAdded` / `CraftNodeRemoved` 是**唯一的节点增删权威事件**（`State/FlightState.cs:111,116`），EVA 出舱/回舱必然触发其中之一。**node id 在两端不保证一致**（基于 `MinCraftNodeId` 自增，`State/FlightStateData.cs:304-314`），所以同步必须带自己的 id 映射。
   - 玩家的相机/控制绑定是 `FlightSceneScript.ChangePlayersActiveCommandPodImmediate(ICommandPod, ICraftNode, bool)` + 私有 `SetCraftNode(CraftNode)`（`Flight/FlightSceneScript.cs:361-387, 1539-1581`）。**这是纯本地的**：`SetCraftNode` 会 `SetIsPlayer(false)` 旧节点、`SetIsPlayer(true)` 新节点，并触发 `CraftChanged`。
   - EVA 的运动是**纯 Rigidbody 物理驱动**（`AddForce` / `AddTorque` / `MoveRotation` / `velocity` 赋值），**没有 kinematic 模式**。把远端 EVA 做成 physics-disabled ghost 会让它完全不动（见第 7 节）。

6. **持久化的东西很少**：`EvaData` 的 designer 字段（`_crewId` / `_crewName` / `_jetpackEnabled` / `_jetpackPowerScalar` / `_jumpPowerScalar` / `_grapplingHookEnabled` / `_gDamageScale` / `_gTolerance` / `_jetpackAvailable`）写进 craft XML；`CrewMember` 写进 GameState 的 `<CrewMembers>`（含 `nodeId` / `location` / `state`）；`Craft` 节点写进 FlightState XML（含 `playerNodeId`）。**运行期 EVA 状态（EvaActive、jetpack 消耗、grappling hook、`_activeWhileInCrewCompartment`）不序列化。**

---

## 0. 文件与类型速查

| 类型 | 位置 | 角色 |
|---|---|---|
| `EvaScript` | `Craft/Parts/Modifiers/Eva/EvaScript.cs`（3003 行） | EVA part 的 modifier script；实现 `IFlightUpdate, IGameLoopItem, IFlightFixedUpdate, IFlightLateUpdate, IEvaScript, ICameraTarget, IReactionEngine, IFuelConsumer`（`:45`） |
| `EvaData` | `Craft/Parts/Modifiers/Eva/EvaData.cs` | `PartModifierData<EvaScript>`，`[PartModifierTypeId("Eva")]`（`:14,16`） |
| `CrewCompartmentScript` | `Craft/Parts/Modifiers/Eva/CrewCompartmentScript.cs` | 乘员舱；持有 `List<EvaScript> Crew`（`:55`），实现 `IFlightStart, IGameLoopItem, IFlightUpdate`（`:18`） |
| `CrewCompartmentData` | `Craft/Parts/Modifiers/Eva/CrewCompartmentData.cs` | `[PartModifierTypeId("CrewCompartment")]`（`:14`） |
| `EvaPerformanceData` | `Craft/Parts/Modifiers/EvaPerformanceData.cs` | 移动/跳跃/转向力参数（`ForceForwardGround=1000` 等，`:13-68`），`EvaScript._perfData`（`EvaScript.cs:240-241`） |
| `EvaSharedCamerasScript` | `Craft/Parts/Modifiers/Eva/EvaSharedCamerasScript.cs` | **单例**，全局共享 FPS/第三人称控制器（`:17,30-41`） |
| `CrewMember` / `CrewManager` / `CrewMemberState` | `State/CrewMember.cs` / `State/CrewManager.cs` / `State/CrewMemberState.cs` | 名册状态 |
| `CraftSplitter` | `Flight/Sim/CraftSplitter.cs` | 拆/合 craft node |
| `FlightState` | `State/FlightState.cs` | craft node 容器 + `CraftNodeAdded/Removed` 事件 |
| `FlightSceneScript` | `Flight/FlightSceneScript.cs` | 玩家控制/相机切换权威 |
| `EvaPanelController` | `Flight/UI/EvaPanelController.cs` | EVA HUD 面板（只跟随 `ActiveCommandPod.EvaScript`） |
| `MoveCrewRequest` | `Flight/UI/MoveCrewRequest.cs` | 乘员转移请求（**不是 EVA 的入口**） |
| `FlightCrewPanelScript` | `Flight/UI/FlightCrewPanelScript.cs` | **空壳类，9 行，无任何成员**（`:6-8`）— 不是 EVA 流程的一部分 |

---

## 1. Drood/EVA 在结构上到底是什么

### 1.1 它是一个 part（永久如此）

- part 类型 id：`"Eva"`、`"Eva-Tourist"`（`Craft/Parts/PartScript.cs:842`；`EvaData.cs:156`）。
- `EvaScript` 是 `PartModifierScript<EvaData>`（`EvaScript.cs:45`），通过 `EvaData`（`PartModifierData<EvaScript>`）绑定。
- 它**同时**是一个 command pod：`CommandPodScript.OnModifiersCreated` 里
  ```csharp
  this.EvaScript = base.GetComponentInChildren<EvaScript>();
  this.IsEva = this.EvaScript != null;
  ```
  （`Craft/Parts/Modifiers/CommandPodScript.cs:476-477`）。
  即：EVA part 的 GameObject 上有一个 `CommandPodScript`，`IsEva` 为 `true`。
- `EvaScript` 通过 `base.PartScript.CommandPod` 访问自己的指令舱（`EvaScript.cs:632, 636, 840, 2650` 等）。`PartScript.CommandPod` 由 craft 装配流程赋值（`PartScript.cs:206-212`，赋值见 `Craft/CraftScript.cs:1560-1561`）。

### 1.2 它属于哪个 CraftNode —— 分阶段

| 阶段 | `EvaScript.CrewCompartment` | `EvaActive` | 所属 CraftNode |
|---|---|---|---|
| 在 designer / 母船内 | != null | false（`:440-446`） | 与 crew compartment 同一个（`GetCrewCompartment` 通过 `PartConnections` 查找，`EvaScript.cs:1414-1433`） |
| 出舱瞬间（关节已断，但还没处理断体） | null | true | **仍然是母船 CraftScript**，直到 split 发生 |
| 出舱完成 | null | true | **自己的 CraftNode**（`CraftSplitter.SplitCraftNode` 创建） |
| 重新入舱 | != null again | false | 母船 CraftNode（自己的 node 已被 `DestroyCraft`） |

### 1.3 成为独立 CraftNode 的确切方法与 NodeId 来源

创建代码在 `CraftSplitter.SplitCraftNode`：

```csharp
// Flight/Sim/CraftSplitter.cs:111-125
public static void SplitCraftNode(CraftNode craftNode, global::ModApi.Craft.Assembly assembly)
{
    CraftScript craftScript = CraftSplitter.CreateCraftScriptFromDisconnectedBodies(craftNode.CraftScript, assembly);
    Vector3d vector3d = craftNode.GameView.ReferenceFrame.FrameToPlanetPosition(craftScript.FramePosition);
    ...
    CraftNode craftNode2 = new CraftNode(vector3d, vector3d2, quaterniond, craftNode.FlightState,
                                         craftNode.Parent.PlanetData.Mass, craftScript.Data, craftScript);
    craftNode2.HasCommandPod = craftScript.RootPart.Data.PartType.IsCommandPod;
    craftNode.Parent.AddChildNode(craftNode2);
    craftNode.FlightState.AddCraft(craftNode2, craftNode);   // <-- NodeId 在这里分配
    craftScript.CraftNode = craftNode2;
```

**NodeId 分配**在 `FlightState.AddCraft`：

```csharp
// State/FlightState.cs:320-347
public void AddCraft(CraftNode craftNode, CraftNode originalNode)
{
    int nextNodeId = this._data.GetNextNodeId();
    if (string.IsNullOrEmpty(craftNode.Name)) { ... craftNode.Name = string.Format("{0}-{1}", originalNode.Name, nextNodeId); ... }
    craftNode.NodeId = nextNodeId;
    this._data.AddCraftNode(new CraftNodeDataDynamic(craftNode, null));
    this._craftNodes.Add(craftNode);
    FlightState.CraftNodeDelegate craftNodeAdded = this.CraftNodeAdded;
    if (craftNodeAdded != null) craftNodeAdded(craftNode);
    ...
}
```

`GetNextNodeId` 是**单调自增**（取所有现存 node 的最大 id 再 +1）：

```csharp
// State/FlightStateData.cs:304-314
public int GetNextNodeId()
{
    int num = this.MinCraftNodeId;
    foreach (ICraftNodeData craftNodeData in this._craftNodes) num = Mathf.Max(craftNodeData.NodeId, num);
    num++;
    this.MinCraftNodeId = num;
    return num;
}
```
`MinCraftNodeId` 会被持久化为 `minNodeId` 属性（`State/FlightStateData.cs:270` 写、`:59` 读），**没有找到任何递减/重置路径** → node id 在同一个存档内不复用。
> ⚠️ **联网含义**：node id 由各机本地计数器决定，**两端不保证一致**。同步 EVA 必须建立 host↔client 的 node id 映射表，不能直接拿 `NodeId` 当跨端主键。`CraftNode.NodeId` 虽然有 public setter（`CraftNode.cs:444`），但 `FlightStateData` 以 node id 为 key（`GetCraftNodeData`，`State/FlightStateData.cs:291-301`）并为每个 node 写 `Craft-{id}.xml` 文件（`GetCraftXmlFilePath`，`:459-462`），**本地改 id 会让存档数据整体错位**，必须同时重建 data list。

`SplitCraftNode` 里还有两个对 EVA 特别重要的行为：

```csharp
// Flight/Sim/CraftSplitter.cs:140-144
if (craftNode.CraftScript.ActiveCommandPod != null && assembly.ContainsPart(craftNode.CraftScript.ActiveCommandPod.Part))
{
    FlightSceneScript.Instance.ChangePlayersActiveCommandPodImmediate(craftNode.CraftScript.ActiveCommandPod, craftNode2, true);
    craftNode.CraftScript.SetActiveCommandPod(null);
}
```
→ 如果出舱的正好是玩家当前控制的 pod（EVA 出舱时就是这种情况），**控制权在 split 里被自动移交**给新 node，并且**老 craft 的 ActiveCommandPod 被置为 null**。
（另注：`craftScript.IsPhysicsEnabled = true` 在 `:139`，即在抛 `CraftNodeAdded` / 切控制**之前**就已置好；`craftNode.GameView.AddGameViewObject(craftNode2)` 在 `:137`，而 `CraftNodeAdded` 在 `:121` 就已抛出 → **事件到达时新节点还没进 game view**。）

**split 时的 Transform 处理**：`CraftSplitter.CreateCraftScriptFromDisconnectedBodies` 会 `craftScript.Transform.SetParent(sourceCraftScript.Transform.parent, false)`（`:199`），而 body 的 Transform 归属由 `BodyScript.MoveToCraft(CraftScript)` 处理：
```csharp
// Craft/BodyScript.cs:595-600
public void MoveToCraft(CraftScript craftScript)
{
    this._transform.SetParent(Game.InFlightScene ? null : craftScript.Transform, true);   // 飞行场景下 body 是根物体
    this.CraftScript = craftScript;
    this._bodyCollisionHandler = new BodyCollisionHandler(this, craftScript);
}
```
→ **飞行场景下 body 的 Transform 是无父物体的根节点**，位置由帧坐标系统一管理；这对「远端 ghost 如何摆放」有直接影响。

`CraftSplitter.MergeCraftNode` 的逆操作也很干净，最后销毁源节点：

```csharp
// Flight/Sim/CraftSplitter.cs:55-77
if (sourceCraftNode.CraftScript.Data.Themes.Count <= 0)
{
    CraftScript craftScript = sourceCraftNode.CraftScript as CraftScript;
    (targetCraftNode.CraftScript as CraftScript).AbsorbCraftScript(craftScript);
    ...
    sourceCraftNode.GameView.RemoveGameViewObject(sourceCraftNode, false);
    sourceCraftNode.DestroyCraft();
    return;
}
```

**关键补充（自动化交叉验证确认）**：
- `CraftSplitter` 里**完全没有 EVA / Drood / CrewCompartment 特判**。全部 331 行里没有 `Eva`、`Drood`、`CrewCompartment`、`Crew` 任何一次出现。唯一的 command pod 相关逻辑是通用的：`PartType.IsCommandPod`（`:119, 311`）、`partData.CommandPod`（`:101, 318-320`）、`Config.PreventDebris`（`:223`）。
- `CraftSplitter` **从不销毁关节**。断体是「已经被断开」之后才被处理的（`GetConnectedBodies` 跳过 `bodyJoint.PartConnection.IsDestroyed` 的边，`:260`）。
- **Debris 分支不创建 CraftNode、不抛任何事件**（`CraftSplitter.cs:93-105`）：只做 `bodyScript2.IsDebris = true`（`:97`）、`bodyScript2.PartIsland = partLookup`（`:98`）、`partData.CommandPod = null`（`:101`）、`craftScript.UpdateFuelSourcesForDebris(partLookup)`（`:105`）。
- `MergeCraftNode` **不抛任何 FlightState 事件**，只有 `CraftNode.CraftNodeMerged`（声明 `CraftNode.cs:117`，抛出点 `CraftNode.cs:651-659`，被 `CraftSplitter.cs:72-73` 调用两次）。target 的 `NodeId` **永不改变**；只有 name/contract id 可能被 source 覆盖（`CraftSplitter.cs:67-71`）。
- 关节的销毁/创建 API 名称（`CraftSplitter` 之外）：
  - 销毁：`IBodyJoint.Destroy()`（`ModApi/Craft/IBodyJoint.cs:69`）、`BodyJoint.DestroyPhysicsJoints()`（`Craft/BodyJoint.cs:118-135`，置 `Broken = true` + 销毁 Unity `Joint`）、`PartConnection.DestroyConnection()`（`ModApi/Craft/Parts/PartConnection.cs:317-339`，置 `IsDestroyed = true`、`BodyJointData = null`）
  - 创建：`CraftBuilder.CreateBodyJoint(PartConnection)`（EVA 用于 `EvaScript.cs:1409`）
  - 其他断关节的调用者：`DetacherScript.cs:58`、`BodyCollisionHandler.cs:964`、`PistonScript.cs:351`、`SuspensionScript.cs:109,194`、`DockingPortScript.cs:407`

---

## 2. 完整 EVA 生命周期（精确调用顺序）

### 触发点

**(2a) 飞行中玩家按「EVA」** —— 两个入口，都最终调用 `CrewCompartmentScript.UnloadCrewMember(EvaScript, bool takeControl)`：

- 乘员舱 inspector 的 exit 图标：
  ```csharp
  // CrewCompartmentScript.cs:201-205 （按钮回调）
  IconButtonModel iconButtonModel = new IconButtonModel("Ui/Sprites/Flight/IconCrewExit",
      delegate(IconButtonModel x) { ...OnUnloadCrewButtonClicked(model, rowModel, crew); }, null);
  // CrewCompartmentScript.cs:500-511
  private void OnUnloadCrewButtonClicked(...) { this.UnloadCrewMember(model, rowModel, crew); this.RefreshInspectorPanel(true); }
  private void UnloadCrewMember(PartInspectorModel model, IconButtonRowModel rowModel, EvaScript crew)
  { model.Remove<IconButtonRowModel>(rowModel, null); this.UnloadCrewMember(crew, true); }
  ```
- EVA part 自身 inspector 的 "Eva" 按钮：
  ```csharp
  // EvaScript.cs:1068-1073
  TextButtonModel textButtonModel2 = new TextButtonModel(Locale.GetString("Parts.EvaScript.Eva"),
      delegate(TextButtonModel b) { this.CrewCompartment.UnloadCrewMember(this, true); }, null, () => this.CrewCompartment != null);
  ```
- 部件被摧毁 / 连接被销毁时也会走这条路径：
  `OnCrewDestroyed` → `UnloadCrewMember(..., false)`（`CrewCompartmentScript.cs:427-430`）；
  `OnCrewPartConnectionDestroyed` → `UnloadCrewMember(evaScript, true)`（`CrewCompartmentScript.cs:433-439`）。

**(2b) `MoveCrewRequest` 是「乘员转移」，不是 EVA 出舱。** 它用来把乘员在**不同 crew compartment 之间**搬运：
- 入口：`EvaScript.OnTransferButtonClicked` → `FlightSceneInterfaceScript.AddMoveCrewRequest(this)`（`EvaScript.cs:2090`；`Flight/UI/FlightSceneInterfaceScript.cs:213-224`）
- 目标舱确认搬运：`CrewCompartmentScript` 的 "MoveHere" 按钮 → `LoadCrewMember(crew, …)`（`CrewCompartmentScript.cs:224-277`）
- 它只影响 `CrewManager`/UI 高亮与 `CrewCompartmentScript.Crew` 列表，**不会触发 split/merge**（`MoveCrewRequest.cs:96-150`）。

**(2c) `EvaPanelController` / `FlightCrewPanelScript` 不参与出舱。** `EvaPanelController` 只是 HUD：它订阅 `FlightScene.ActiveCommandPodChanged`，把 `EvaScript` 设为 `ActiveCommandPod.EvaScript`（`Flight/UI/EvaPanelController.cs:239-244`），然后根据 `EvaControlScheme` 切换面板可见性（`:284-344`）。它的按键只写 `FlightControls`（`OnJumpButtonDown` → `_flightControls.EvaJumpUI = 1f`，`:260-281`）。`FlightCrewPanelScript` 是空类（`Flight/UI/FlightCrewPanelScript.cs:6-8`）。

### 出舱调用链（按执行顺序）

**阶段 1 —— UI 回调（同步执行）**

1. `CrewCompartmentScript.UnloadCrewMember(EvaScript crew, bool takeControl)` — `CrewCompartmentScript.cs:361-375`
   ```csharp
   this.Crew.Remove(crew);
   crew.PartScript.PartDestroyed -= this.OnCrewDestroyed;
   if (takeControl) crew.TakeControl();        // <-- 同步进入
   ...
   crewExit?.Invoke(crew);                     // CrewExit 事件
   ```

2. `EvaScript.TakeControl()` — `EvaScript.cs:1267-1280`
   ```csharp
   public void TakeControl()
   {
       if (Game.InDesignerScene)
       {
           this.UnloadFromCrewCompartment(true);
           this.ActivateEvaForDisconnectedAstronaut();
           return;
       }
       if (!this.EvaActive) this.UnloadFromCrewCompartment(true);   // 飞行场景路径
       this.SwitchToCommandPod();
   }
   ```

3. `EvaScript.UnloadFromCrewCompartment(bool setTransform = true)` — `EvaScript.cs:2259-2308`，**关节在这里被销毁**：
   ```csharp
   this.UnloadingFromCrewCompartmentInProgress = true;                      // :2263
   foreach (AttachPoint attachPoint in base.PartScript.Data.AttachPoints)
     foreach (PartConnection partConnection in attachPoint.PartConnections.ToArray())
       if (partConnection.IsPhysicsJoint)
         foreach (IBodyJoint bodyJoint in base.PartScript.BodyScript.Joints.ToArray())
           if (partConnection == bodyJoint.PartConnection)
             if (bodyJoint2 != null && !bodyJoint2.PartConnection.IsDestroyed)
             {
                 bodyJoint2.Destroy();                                        // :2277
                 base.PartScript.BodyScript.RigidBody.WakeUp();               // :2278
             }
   // setTransform: base.transform.rotation = ...BodyScript.Transform.rotation   // :2286
   // 然后ExecuteYield<WaitForEndOfFrame>，2 帧后把 UnloadingFromCrewCompartmentInProgress 置回 false  // :2288-2296
   EvaScript.SetTransformToCrewExit(base.PartScript.BodyScript.RigidBody.transform, this._controllerCollider, this.CrewCompartment); // :2300
   ```
   - `SetTransformToCrewExit`（`EvaScript.cs:1444-1462`）把 EVA 放到 `CrewCompartmentData.CrewExitPosition/CrewExitRotation`，并做 `GetAgl` + `DepenetrateCollider` 修正。

4. `BodyJoint.Destroy()` — `Craft/BodyJoint.cs:60-80`
   ```csharp
   this.PartConnection.Destroyed -= this.OnPartConnectionDestroyed;
   this.DestroyPhysicsJoints();
   this.Body.Joints.Remove(this);
   this.ConnectedBody.Joints.Remove(this);
   if (destroyPartConnection)
   {
       this.PartConnection.DestroyConnection();
       this.Body.CraftScript.SetStructureChanged();     // :77  <-- 关键：置脏标记
   }
   ```

5. `CraftScript.SetStructureChanged()` — `Craft/CraftScript.cs:1567-1570`，仅 `this._structureChanged = true;`（**延迟处理**）。

6. `EvaScript.SwitchToCommandPod()` — `EvaScript.cs:2249-2256`
   ```csharp
   FlightSceneScript instance = FlightSceneScript.Instance;
   if (!instance.ChangePlayersActiveCommandPodImmediate(base.PartScript.CommandPod, base.PartScript.CraftScript.CraftNode, false))
       instance.FlightSceneUI.ShowMessage(Locale.GetString("Flight.Eva.TooFarToControlFormat", ...), false, 5f);
   ```
   > ⚠️ 注意：**此时 craftNode 还是母船 node**（split 还没发生）。控制权先切到「母船 node 里的 EVA pod」，真正切到新 node 是第 11 步由 `SplitCraftNode` 完成的。

7. `FlightSceneScript.ChangePlayersActiveCommandPodImmediate(ICommandPod commandPod, ICraftNode craftNode, bool ignoreDistance)` — `Flight/FlightSceneScript.cs:361-387`
   ```csharp
   double num = (double)(Game.Instance.QualitySettings.Physics.PhysicsDistance * 1000 - 100);
   ...
   if ((double)(this.CraftNode.FramePosition - commandPod.Part.PartScript.Transform.position).magnitude < num || ignoreDistance)
   {
       CraftControls.ZeroControls(this.CraftNode.Controls, false);                 // :371
       this.ViewManager.GameView.GameCamera.Recenter(true);                        // :372
       CraftNode craftNode2 = craftNode as CraftNode;
       if (craftNode.CraftScript.ActiveCommandPod != commandPod)
           craftNode2.CraftScript.SetActiveCommandPod(commandPod);                 // :376
       if (this.CraftNode != craftNode) this.SetCraftNode(craftNode2);             // :380（幂等：相同 node 不重设）
       craftNode.CraftScript.SetStructureChanged();                                // :382
       craftNode.AllowPlayerControl = true;                                        // :383
       return true;
   }
   return false;
   ```
8. `CraftScript.SetActiveCommandPod(ICommandPod)` — `Craft/CraftScript.cs:1484-1504`；抛 `ActiveCommandPodChanging` → 赋值 `ActiveCommandPod` → 抛 `ActiveCommandPodChanged` → `this.Data.ActiveCommandPodId = commandPod.Part.Id`（:1500）。

9. `EvaScript.OnActiveCommandPodChanging` 会被触发（订阅于 `EvaScript.Start`，`EvaScript.cs:1247-1253`，实现 `:1780-1791`）：若 `ActiveWhileInCrewCompartment` 则把舱内 pod 的 activation group 与 throttle 复制过来。

**阶段 2 —— 下一次 `LateUpdate`（延迟处理）**

游戏循环顺序（`GameLoop/CustomPlayerLoop.cs:100-102`）：
`PreFixedUpdate → ScriptRunBehaviourFixedUpdate → PostFixedUpdate → PreUpdate → ScriptRunBehaviourUpdate → PostUpdate → PreLateUpdate → ScriptRunBehaviourLateUpdate → PostLateUpdate`。
`FlightGameLoop.LateUpdate` 在 `ScriptRunBehaviourLateUpdate` **之前**执行（`GameLoop/FlightGameLoop.cs:189-214`）。
**推论：UI 按钮（Unity Update 期间的回调）里触发的 `SetStructureChanged()`，至少要到下一帧的 `PreLateUpdate/FlightGameLoop.LateUpdate` 才被处理。**

10. `CraftScript.IFlightLateUpdate.FlightLateUpdate(in FlightFrameData frame)` — `Craft/CraftScript.cs:896-925`
    ```csharp
    if (this._structureChanged) { this.RebuildCraftStructure(true); this._structureChanged = false; this._bodySeparated = false; }
    else if (this._processDisconnectedBodies) { if (this.ProcessDisconnectedBodies()) {...} else {...} }
    ```
11. `CraftScript.RebuildCraftStructure(bool)` — `Craft/CraftScript.cs:1994-2067`：重新从 root part 的 part island 计算连通性；不在 island 里的 part/body 保持 `Disconnected = true`；飞行场景下置 `this._processDisconnectedBodies = true`（`:2064`）。

12. **下一次 `FlightLateUpdate`**：`CraftScript.ProcessDisconnectedBodies()` — `Craft/CraftScript.cs:1980-1991`
    ```csharp
    foreach (BodyData bodyData in this.Data.Assembly.Bodies)
        if (!bodyData.IsDestroyed && bodyData.BodyScript.Disconnected && !bodyData.BodyScript.IsDebris)
        { CraftSplitter.ProcessDisconnectedBody(bodyData, this); return true; }
    ```
    > 注意：**每帧只处理一个断体**（`return true`）— 多个 EVA 同时出舱会分多帧处理。

13. `CraftSplitter.ProcessDisconnectedBody(BodyData body, CraftScript craftScript)` — `Flight/Sim/CraftSplitter.cs:82-108`
    ```csharp
    CraftSplitter.GetConnectedBodies(body.BodyScript, list, new HashSet<IBodyScript>());
    if (CraftSplitter.DetermineCraftNodeEligibility(list)) CraftSplitter.MoveBodyToNewCraftNode(list, craftScript);
    else { /* 标记为 debris: bodyScript2.IsDebris = true; partData.CommandPod = null; ... */ }
    ```

14. `CraftSplitter.DetermineCraftNodeEligibility(List<IBodyScript>)` — `CraftSplitter.cs:216-250`
    ```csharp
    foreach (... partData ...) {
        if (partData.Config.PreventDebris) return true;                       // :223
        ... 累积所有 Collider 的 AABB ...
        if (bounds.Value.size.x > 10f || .y > 10f || .z > 10f) return true;   // :243
    }
    return false;
    ```
    > ⚠️ **未确认**：我无法确认 Eva part 的配置 XML 中 `preventDebris` 的值（`SimpleRockets2/Assets` 中没有 part 配置资源，只有反编译的 `.cs`）。`ConfigData._preventDebris` 默认 `false`（`ModApi/Craft/Parts/Modifiers/ConfigData.cs:354-360`）。要让宇航员成为独立 craft node，必须满足 `preventDebris == true` 或 AABB 任一维 > 10（宇航员高度约 2m，靠 AABB 不太可能）。**这一点对多人 Mod 很关键** —— 请在游戏内 inspect EVA part 的 `PreventDebris` 开关确认。

15. `CraftSplitter.MoveBodyToNewCraftNode(List<IBodyScript>, CraftScript)` — `CraftSplitter.cs:304-329`：把 body 从源 assembly 移到新 assembly；若新 assembly 里有 command pod 则选为 `partData`，否则用第一个 part；`partData.IsRootPart = true;`（`:327`）；然后调用 `SplitCraftNode`。

16. `CraftSplitter.SplitCraftNode(CraftNode, Assembly)` — `CraftSplitter.cs:111-151`（见 1.3 节摘录）。关键子调用顺序：
    - `CreateCraftScriptFromDisconnectedBodies`（`:169-213`）→ 新建 `GameObject("Craft-Disconnected")`、`CraftData.CreateEmptyCraftDataFromSource`、`InitializeFromSourceCraft`、对所有 part `OnMovingToNewCraft(craftScript)`（`:205`）、`craftScript.OnCraftLoaded(true, false)`（`:207`）、`BodyScript.MoveToCraft(craftScript)`（`:210`）
    - `new CraftNode(...)`（`:118`）
    - `craftNode.Parent.AddChildNode(craftNode2)`（`:120`）
    - **`craftNode.FlightState.AddCraft(craftNode2, craftNode)`（`:121`）→ 分配 NodeId + 抛 `CraftNodeAdded`**
    - `craftScript.CraftNode = craftNode2`（`:122`）、`craftNode2.Initialize()`（`:124`）、`craftNode2.FlightStart()`（`:125`）
    - 所有 part `OnMovedToNewCraft(craftScript)`（`:128`）→ 会触发 `PartScript.MovedToNewCraft` 事件 → **`EvaScript.OnMovedToNewCraft`（`EvaScript.cs:2037-2044`）写入 `CrewMember.NodeId`**
    - `craftNode.GameView.AddGameViewObject(craftNode2)`（`:137`）
    - `craftScript.IsPhysicsEnabled = true`（`:139`）
    - **`ChangePlayersActiveCommandPodImmediate(activePod, craftNode2, true)` + `craftNode.CraftScript.SetActiveCommandPod(null)`（`:140-144`）** ← 这才是玩家 node 真正切换的点
    - `craftNode.CraftScript.RaiseCraftSplitEvent()`（`:149`）

17. `EvaScript.OnMovedToNewCraft(ICraftScript oldCraft, ICraftScript newCraft)` — `EvaScript.cs:2037-2044`
    ```csharp
    if (base.Data.CrewMember != null) base.Data.CrewMember.NodeId = newCraft.CraftNode.NodeId;
    this.UpdateCrewPointOfViewRegistration();
    ```
    > **这是 `CrewMember.NodeId` 唯一的写入点**（全仓只有这一处赋值）。`CrewMember.Location` 在 `UpdateCrewMemberLocation`（`EvaScript.cs:2420-2426`）写为母天体名。

18. `EvaScript.OnActiveCommandPodChanged`（`CommandPodScript.cs:772-789`）→ 抛 `CommandPodScript.IsPlayerControlledChanged` → `EvaScript.OnCommandPodIsPlayerControlledChanged`（`EvaScript.cs:1833-1845`）：设置准星、`UpdateCommandReplication`、提示「Switched to X」、`UpdateZoomEnabled`、`SetEvaCameraModesInUse`、`UpdateCrewPointOfViewRegistration`。/ 并且 `CraftNode.SetIsPlayer` → 每个 modifier 的 `OnIsPlayerCraftChanged`（`CraftNode.cs:705-739`、`CommandPodScript.cs:459-471`）。

### 出舱时序图

```
[Unity Update]  UI click
  └ CrewCompartmentScript.UnloadCrewMember(crew, true)                 CrewCompartmentScript.cs:361
      └ EvaScript.TakeControl()                                        EvaScript.cs:1267
          ├ EvaScript.UnloadFromCrewCompartment(true)                  EvaScript.cs:2259
          │   └ BodyJoint.Destroy()                                    BodyJoint.cs:60
          │       └ CraftScript.SetStructureChanged()  (置 _structureChanged)  CraftScript.cs:1567
          │   └ SetTransformToCrewExit(...)                            EvaScript.cs:2300
          └ EvaScript.SwitchToCommandPod()                             EvaScript.cs:2249
              └ FlightSceneScript.ChangePlayersActiveCommandPodImmediate(pod, 母船node, false)  FlightSceneScript.cs:361
                  ├ CraftControls.ZeroControls(old)                    :371
                  ├ CraftScript.SetActiveCommandPod(pod)               CraftScript.cs:1484  → ActiveCommandPodChanged
                  ├ (this.CraftNode == craftNode → 不调用 SetCraftNode) :378
                  ├ CraftScript.SetStructureChanged()                  :382
                  └ craftNode.AllowPlayerControl = true                :383

[next frame, FlightGameLoop.LateUpdate]
  └ CraftScript.IFlightLateUpdate.FlightLateUpdate                    CraftScript.cs:896
      └ RebuildCraftStructure(true)  → _processDisconnectedBodies = true  CraftScript.cs:1994 / :2064

[next frame, FlightGameLoop.LateUpdate]
  └ CraftScript.ProcessDisconnectedBodies()                           CraftScript.cs:1980
      └ CraftSplitter.ProcessDisconnectedBody(body, craftScript)       CraftSplitter.cs:82
          ├ DetermineCraftNodeEligibility(list)                        CraftSplitter.cs:216
          └ MoveBodyToNewCraftNode(list, craftScript)                  CraftSplitter.cs:304
              └ CraftSplitter.SplitCraftNode(craftNode, assembly)      CraftSplitter.cs:111
                  ├ CreateCraftScriptFromDisconnectedBodies(...)       CraftSplitter.cs:169
                  ├ new CraftNode(...)                                 CraftSplitter.cs:118
                  ├ craftNode.Parent.AddChildNode(craftNode2)          CraftSplitter.cs:120
                  ├ FlightState.AddCraft(craftNode2, craftNode)        CraftSplitter.cs:121
                  │     ├ CraftNodeDataDynamic → _data.AddCraftNode
                  │     ├ craftNode.NodeId = GetNextNodeId()           FlightState.cs:340 / FlightStateData.cs:304
                  │     └ ★ FlightState.CraftNodeAdded(craftNode)      FlightState.cs:343-347
                  ├ craftNode2.Initialize() / FlightStart()            CraftSplitter.cs:124-125
                  ├ PartScript.OnMovedToNewCraft → EvaScript.OnMovedToNewCraft
                  │     └ CrewMember.NodeId = newCraft.CraftNode.NodeId EvaScript.cs:2041
                  ├ craftNode.GameView.AddGameViewObject(craftNode2)   CraftSplitter.cs:137
                  └ ★ ChangePlayersActiveCommandPodImmediate(pod, craftNode2, true)  CraftSplitter.cs:142
                        └ FlightSceneScript.SetCraftNode(craftNode2)   FlightSceneScript.cs:1539
                              ├ old.SetIsPlayer(false, new)            CraftNode.cs:705
                              ├ new.SetIsPlayer(true, old)             CraftNode.cs:1559
                              ├ FlightControls.SetCraftNode(new)       FlightSceneScript.cs:1560
                              └ ★ CraftChanged(new)                    FlightSceneScript.cs:1561-1565
```

> 另有一个 `PartScript.MovedToNewCraft` / `OnMovingToNewCraft` 的语义（`PartScript.cs:1256-1277`）：`OnMovingToNewCraft` 先改 `CraftScript`，`OnMovedToNewCraft` 之后才抛事件（用 `_oldCraftScript` 作为 old 参数）。

---

## 3. 重新入舱（回到飞船）

### 入口

- 乘员舱 inspector 的 "Enter" 按钮 → `CrewCompartmentScript.OnLoadCrewIntoCompartmentButtonClicked(EvaScript crew)`（`CrewCompartmentScript.cs:442-472`）
- 距离/容量检查：`IsCloseEnoughToEnterCompartment`（`:142-145`）、`GetMaxDistanceToEnterCrewCompartment` = `Data.Radius + (crew.IsGrounded ? 10f : 2f)`（`:395-398`）
- 转移流程（MoveHere）里也会调 `LoadCrewMember`（`:239-246`）
- 直接调用 API：`EvaScript.LoadIntoCrewCompartment(CrewCompartmentScript, Action onCompleted, bool announceBoarding = true)`（`EvaScript.cs:947-964`）

### 调用顺序

1. `CrewCompartmentScript.LoadCrewMember(EvaScript crew, Action onCompleted, bool announceBoarding = true)` — `CrewCompartmentScript.cs:401-408`
   ```csharp
   if (crew.CrewCompartment != null) crew.CrewCompartment.UnloadCrewMember(crew, false);  // 先从原舱摘除
   crew.LoadIntoCrewCompartment(this, onCompleted, announceBoarding);
   ```

2. `EvaScript.LoadIntoCrewCompartment(...)` — `EvaScript.cs:947-964`
   ```csharp
   if (crewCompartment != null && !this._loadingIntoCrewCompartmentInProgress)
   {
       this._loadingIntoCrewCompartmentInProgress = true;
       if (Game.InFlightScene && this.EvaActive)
       {
           UnityEventDispatcher.Instance.ExecuteYield<WaitForEndOfFrame>(new Action(local.<LoadIntoCrewCompartment>g__LoadIntoCompartment|0));
           return;                            // 延迟到帧末执行
       }
       local.<LoadIntoCrewCompartment>g__LoadIntoCompartment|0();
   }
   ```
   > ⚠️ **未确认（源码被反编译器剥离）**：`g__LoadIntoCompartment` 是编译器生成的局部函数，其方法体在本地反编译产物中**不存在**（`EvaScript.cs` 只留下引用，没有 `EvaScript.<>c__DisplayClass209_0` 类型定义；全仓 grep `LoadIntoCompartment` 只命中这 3 行）。**因此第 3 步之后的精确顺序是我基于所有可观测调用点与 `CraftSplitter.MergeCraftNode` 的语义推断的**，请在 ILSpy/dnSpy 中补齐该方法体验证。

3. **（推断）**局部函数体内应执行：销毁 EVA body 与目标 crew compartment 之间的物理关节（与 `UnloadFromCrewCompartment` 对称）→ 把 EVA body 平移到舱内位置（`EvaScript.SetTransformToCrewCompartment`，`EvaScript.cs:1436-1441`）→ 建立 `PartConnection` + `BodyJointData`/`CraftBuilder.CreateBodyJoint`（`EvaScript.ConnectParts` 是同类的静态辅助，`EvaScript.cs:1376-1411`）→ `EvaScript.CrewCompartment = crewCompartment`（setter `:401-411`）→ 目标 `CrewCompartmentScript` 通过 `OnCrewMemberLoaded` 把 EVA 加回 `Crew` 列表（`CrewCompartmentScript.cs:158-181`）。

4. `EvaScript.CrewCompartment` setter — `EvaScript.cs:395-412`
   ```csharp
   private set {
       CrewCompartmentScript crewCompartment = this._crewCompartment;
       this._crewCompartment = value;
       if (this._crewCompartment != crewCompartment || !this._crewCompartmentHasBeenSet) {
           this._crewCompartmentHasBeenSet = true;
           this.UpdateActiveInCrewCompartment();          // → ActiveWhileInCrewCompartment
           this.OnCrewCompartmentStateChanged(crewCompartment);
       }
   }
   ```
   → `EvaActive` 变为 `false`（因为 `CrewCompartment != null`，`:444`）。

5. `EvaScript.OnCrewCompartmentStateChanged(CrewCompartmentScript oldVal)` — `EvaScript.cs:1894-1945`：`Data.Enabled = CrewCompartment.Data.CommandPodEnabledInCompartment`（:1907）、`WaterPhysics.Enabled = EvaActive`（:1914）、`FlightScene.UpdateActiveControlMaps(...)`（:1923）、renderer 可见性、订阅 `CrewOrientationChanged`/`CrewAnimationChanged`。

6. **合并 craft node**：`EvaScript.ConnectParts(partA, attachPointA, partB, attachPointB)` — `EvaScript.cs:1376-1411`（designer 路径使用；飞行路径应等价）。核心：
   ```csharp
   if (partB.CraftScript != partA.CraftScript)
   {
       ...
       CraftSplitter.MergeCraftNode(partB.CraftScript.CraftNode as CraftNode, partA.CraftScript.CraftNode as CraftNode);  // :1393
   }
   PartConnection partConnection = new PartConnection(partA.Data, partB.Data);        // :1395
   partConnection.AddAttachment(attachPointA, attachPointB);                          // :1396
   partA.CraftScript.Data.Assembly.AddPartConnection(partConnection);                 // :1397
   partConnection.BodyJointData = new BodyJointData(partConnection);                  // :1400
   ...
   CraftBuilder.CreateBodyJoint(partConnection);                                      // :1409
   ```
   注意 `:1378-1386`：如果 `partB` 是玩家 craft，则交换 A/B——**即 `MergeCraftNode(source=EVA, target=母船)`，母船永远是 target**。

7. `CraftSplitter.MergeCraftNode(CraftNode sourceCraftNode, CraftNode targetCraftNode)` — `Flight/Sim/CraftSplitter.cs:22-79`
   - 把 source 的所有 body 移到 target assembly（`MoveBodiesToAssembly`，`:29` / `:269-301`）
   - 每个 body `MoveToCraft(target)`（`:35`），每个 part 依次 `OnMovingToNewCraft` → `OnMovedToNewCraft` → `OnCraftLoaded(target, true)`（`:40-42`）
   - `targetCraftNode.CopyInitialCraftNodeData(sourceCraftNode)`（`:66`）
   - `targetCraftNode.OnMergedWithCraftNode(...)` / `sourceCraftNode.OnMergedWithCraftNode(...)`（`:72-73`，实现于 `Flight/Sim/CraftNode.cs:651`）
   - `sourceCraftNode.GameView.RemoveGameViewObject(sourceCraftNode, false)`（`:74`）
   - **`sourceCraftNode.DestroyCraft()`（`:75`）** ← EVA 的 CraftNode 在这里被销毁

8. `CraftNode.DestroyCraft()` — `Flight/Sim/CraftNode.cs:579-596`
   ```csharp
   if (!base.IsDestroyed) { base.IsDestroyed = true; try { base.RaiseDestroyedEvent(); return; } catch ... }
   else Debug.LogError("Attempting to destroy a craft that has already been destroyed");
   ```

9. **`CraftNodeRemoved` 不是立刻抛**：`FlightState.ProcessDestroyedCraftNodes()` 在 `FlightSceneScript.OnLateUpdate` 中被调用：
   ```csharp
   // Flight/FlightSceneScript.cs:492-501
   public void OnLateUpdate(in FlightFrameData frame) {
       ...
       this.FlightState.ProcessNodeTree(FlightSceneScript.ProcessNodeTreeFlightLateUpdate.FlightLateUpdate);   // :500
       this.FlightState.ProcessDestroyedCraftNodes();                                                          // :501
   ```
   ```csharp
   // State/FlightState.cs:415-440
   public void ProcessDestroyedCraftNodes() {
       for (...) if (craftNode.IsDestroyed) this._deletedCraftsPending.Add(craftNode);
       foreach (CraftNode craftNode2 in this._deletedCraftsPending) {
           if (!craftNode2.IsLoadedInGameView) craftNode2.Parent.RemoveChildNode(craftNode2);
           this._craftNodes.Remove(craftNode2);
           this._data.RemoveCraftNode(this._data.GetCraftNodeData(craftNode2.NodeId));
           ★ CraftNodeRemoved?.Invoke(craftNode2);
       }
       this._deletedCraftsPending.Clear();
   }
   ```
   （另一处调用点在 `Flight/FlightSceneScript.cs:1228`，`FlightEnd` 内。）

### EVA body/part 之后怎样？

**没有被销毁。** 整个流程是「part 在 craft 之间搬移」：
- `MergeCraftNode` 只移动 body/part 的归属（`MoveBodiesToAssembly` → `BodyScript.MoveToCraft`），**不销毁 GameObject**。
- 回到母船后，EVA part 的 GameObject 按 `CrewCompartmentData.CommandPodEnabledInCompartment` 决定 `Data.Enabled`（`EvaScript.cs:1907`），并关闭 renderer / 粒子（`:1908, 1926-1931`）。
- `EvaScript.CrewCompartment` 恢复引用，`EvaActive` 变 false，`EvaScript`/`EvaData` 对象保持存活，`EvaData.CrewMember` 引用不变。
- 玩家控制权：`MergeCraftNode` 的 target 是母船，source（EVA node）被销毁；**玩家 node 需要另行切回母船**（游戏内是玩家在母船上再点 Take Control，或 pod 被自动选中）。注意 `ChangePlayersActiveCommandPodImmediate` 会在下一步由 UI 触发（EvaScript/CommandPod inspector 的 Take Control 按钮，`EvaScript.cs:1063-1067`、`CommandPodScript.cs:432-435`）。
  > ⚠️ 我没有找到 `MergeCraftNode` 内部**主动**切回玩家 node 的代码 —— 这意味着如果玩家自己就是那个 EVA（EVA node 是 player node），合并后 `FlightSceneScript._craftNode` 会指向一个已销毁的 node。**这是一个潜在的原版脆弱点/需要 Mod 特意处理的点。** 标记为「未确认：未见自动切回逻辑」。

---

## 4. 持久化 / 序列化状态

### 4.1 `CrewMember`（GameState 级别，非 craft XML）

```csharp
// State/CrewMember.cs:20-29
public CrewMember(XElement xml) {
    this.Id = xml.GetIntAttribute("id", 0);
    this.NodeId = xml.GetIntAttribute("nodeId", -1);
    this.Name = xml.GetStringAttribute("name", null);
    this.Location = xml.GetStringAttribute("location", null);
    this.State = xml.GetEnumAttribute("state", CrewMemberState.Available, null);
    this.UseAlternateJetpack = this.Name.StartsWith("Yuri G") || this.Name.StartsWith("Sally R");
}
// State/CrewMember.cs:62-71
public XElement GenerateXml() {
    XElement xelement = new XElement("CrewMember");
    xelement.SetAttributeValue("id", this.Id);
    xelement.SetAttributeValue("nodeId", this.NodeId);
    xelement.SetAttributeValue("name", this.Name);
    xelement.SetAttributeValue("location", this.Location);
    xelement.SetAttributeValue("state", this.State);
    return xelement;
}
```
字段：`Id`（`:34`）、`NodeId`（`:39`）、`Location`（`:44`）、`Name`（`:49`）、`State`（`:54`，`Available|InFlight|Deceased`，`State/CrewMemberState.cs:6-14`）、`UseAlternateJetpack`（`:59`，由名字推导，**不可写**）。

**`CrewMember.NodeId` 的写入点（全仓唯一）**：
```csharp
// EvaScript.cs:2037-2044
private void OnMovedToNewCraft(ICraftScript oldCraft, ICraftScript newCraft) {
    if (base.Data.CrewMember != null) base.Data.CrewMember.NodeId = newCraft.CraftNode.NodeId;
    ...
}
```
`CrewMember.Location` 写入点：`EvaScript.UpdateCrewMemberLocation()`（`EvaScript.cs:2420-2426`）= `CraftScript.CraftNode.Parent.Name`（天体名）；由 `Start()`（`:1262`）和 `OnChangedSoI`（`:1800-1804`）调用。

`CrewMember.State` 的写入点：
- `Available` ← `EvaData.OnPartRecovered`（`EvaData.cs:267-274`）
- `InFlight` ← `EvaScript.OnInitialLaunch`（`:1129-1136`）
- `Deceased` ← `EvaScript.OnPartDestroyed`（`:1196-1210`）

### 4.2 `CrewManager`（GameState，`<CrewMembers nextId="...">`）

- 持有 `List<CrewMember> _members` + `int _nextCrewMemberId`（`State/CrewManager.cs:20,23`）
- **实例位置**：`Game.Instance.GameState.Crew`（构造于 `State/GameState.cs:57`：`this.Crew = new CrewManager(xelement.Element("CrewMembers"), this);`）。**不是跨机共享的单例**——每个玩家的存档各自持有。
- `Members`（`:52-58`）、`CreateCrewMember()`（`:61-71`，**会立刻 `_gameState.Save()`**）、`GenerateXml()`（`:74-83`）、`GetAvailableCrew(Assembly)`（`:86-103`）、`GetCrewMember(int crewId)`（`:106-109`）
- **载入时的一致性修正**（重要！）：
  ```csharp
  // State/CrewManager.cs:35-43
  List<int> list = gameState.LoadFlightStateData().CraftNodes.Select((ICraftNodeData x) => x.NodeId).ToList<int>();
  foreach (XElement xelement in enumerable) {
      CrewMember crewMember = new CrewMember(xelement);
      if (crewMember.State == CrewMemberState.InFlight && crewMember.NodeId >= 0 && !list.Contains(crewMember.NodeId))
      { crewMember.NodeId = -1; crewMember.State = CrewMemberState.Available; }
      this._members.Add(crewMember);
  }
  ```
  → **`CrewMember.NodeId` 只有在对应的 craft node 仍存在于 FlightState 时才保留。** 多人环境若两端 craft node 集合不同步，这一条会把乘员错误地重置为 `Available`。**必须同步 `FlightStateData.CraftNodes` 的 node id 集合。**

### 4.3 `EvaData`（craft XML 内，designer 属性）

全部是 `[SerializeField] [PartModifierProperty]`，会经 `PartModifierData.GenerateStateXml`（`ModApi/Craft/Parts/PartModifierData.cs:977`）序列化，并由 `PartData.GenerateXml` 收集（`ModApi/Craft/Parts/PartData.cs:943-951`）：

```
_c CrewId            int    EvaData.cs:22-24     (属性 CrewId :77-83)
_crewName            string EvaData.cs:27-29     (属性 CrewName :93-103, DisplayCrewName :107-113)
_grapplingHookEnabled bool  EvaData.cs:32-34     (:128-138)
_gDamageScale        float  EvaData.cs:37-39     (:117-123)
_gTolerance          float  EvaData.cs:42-44     (:142-148)
_jetpackAvailable    bool   EvaData.cs:47-49     (:163-173)
_jetpackEnabled      bool   EvaData.cs:52-54     (:178-188)
_jetpackPowerScalar  float  EvaData.cs:57-59     (:193-203)
_jumpPowerScalar     float  EvaData.cs:62-64     (:208-218)
_requiresCrewMember  bool   EvaData.cs:67-69     (:223-233)
```
`CrewMember` 是**运行期**引用（`public CrewMember CrewMember { get; private set; }`，`EvaData.cs:88`），在 `OnInitialized` 里通过 `gameState.Crew.GetCrewMember(this._crewId)` 重建（`:291-296`），不序列化。`AssignCrewMember(CrewMember)`（`:247-264`）写入 `_crewId`/`_crewName` 并回调 `Script.OnCrewMemberChanged()`。

**`EvaData` 没有 override `GenerateStateXml`** → 除上述 designer 字段外无额外 EVA 状态入 XML。

`EvaScript` 层面：有 `[SerializeField] EvaPerformanceData _perfData`（`EvaScript.cs:240-241`）和 `[SerializeField] float _fuelConsumption = 0.12f`（`:280-282`），但这些是 prefab 默认值 / designer 数据，**不是飞行状态**。`EvaScript` 没有 override `GenerateStateXml`。

### 4.4 Craft XML / FlightState XML

- 每个 CraftNode 的 craft XML 单独存盘：`FlightState.SaveCraftXml(nodeId, xml)`（`State/FlightState.cs:459-462`）→ `FlightStateData.SaveCraftXml`；路径见于 `FlightStateData.LoadCraftXml(nodeId)`（`State/FlightStateData.cs:330-333`）。**EVA craft node 出舱时通过 `CraftNodeDataDynamic` 生成一份新的 craft XML 文件**（`CraftNode.cs:1171-1188` `SavePendingCraftXmlChanges`）。
- FlightState 根 XML：`FlightStateData.GenerateXml()` 写 `playerNodeId`（`:269`）、`minNodeId`（`:270`）、所有 planet node 与所有 craft node（`:280-283`）。
- Craft node 属性：`ModApi/Scripts/State/CraftNodeData.cs:238-282` —— `id`、`name`、`parent`、`position`、`velocity`、`heading`、`inContactWithPlanet`、`hasCommandPod`、`craftMass`、`craftPartCount`、`craftBoundsRadius`、`contractTrackingId`、`allowPlayerControl`（`:257`）、`waterDepth`、`initialNodes`（`:259`）、surface 三元组、`<InitialCrafts>`。

### 4.5 `FlightState.PlayerNodeId`

```csharp
// State/FlightState.cs:185-195
public int PlayerNodeId { get => this._data.PlayerNodeId; set => this._data.PlayerNodeId = value; }
```
写入点（全仓）：
- `Flight/FlightSceneScript.cs:392`（`ChangePlayersActiveCraftNode`）
- `Flight/FlightSceneScript.cs:932`（启动时从 `ResumeCraftNodeId` 恢复）
- `Flight/FlightSceneScript.cs:1320`（`LaunchNewCraft`）
- `State/FlightState.cs:508-511`（`OnPlayerCraftActiveCommandPodChanged(ICraftNode craftNode)` → `this.PlayerNodeId = craftNode.NodeId;`，订阅于 `State/FlightState.cs:104`）

> 出舱/回舱**不会**直接写 `PlayerNodeId` —— 只有 `ChangePlayersActiveCraftNode`（会重载整个 flight scene）或启动/发射、以及 `FlightScene.ActiveCommandPodChanged` 事件时才写。所以游戏内实时切 node 时 `PlayerNodeId` 会滞后到下一个 `ActiveCommandPodChanged`，否则要到下一次 save。

### 4.6 联网必须保持一致的跨机状态

| 数据 | 位置 | 是否序列化 | 备注 |
|---|---|---|---|
| `CrewMember.Id / Name / NodeId / Location / State` | GameState `<CrewMembers>` | 是 | `CrewManager.cs:26-48 / 74-83`；`NodeId` 是**本地 node id**，跨机需映射 |
| craft node 的 `NodeId` | FlightState XML | 是 | 本地自增（`FlightStateData.cs:304-314`），**两端会不同** |
| craft node 的 position/velocity/heading | FlightState XML | 是 | `CraftNodeData.cs:244-246` |
| craft node 的 `InitialCraftNodeId`（每个 part 上） | craft XML | 是 | `CraftSplitter.cs:145-148`；`CraftNode.cs:908-915` |
| EVA 是否出舱 | `EvaScript.EvaActive` / `CrewCompartment` | **否** | 只能由 craft 结构（node 划分 + joint）推断 |
| jetpack 燃料 | `FuelTankScript` 的 part state XML | 是（part state） | `EvaScript.cs:1061, 2747-2750` |
| 绳索（grappling hook） | 运行期对象 | **否** | `EvaScript.cs:1698-1739` |
| jetpack/tether 的控制输入 | `CraftControls`（`evaMoveFwdAft` 等） | `CommandPodData.GenerateStateXml`（`CommandPodData.cs:640-646`）+ `CraftControls.cs:666-670` | 输入会入 part state XML |

---

## 5. 控制与相机耦合

### 5.1 玩家 `CraftNode` 的变化

`FlightSceneScript.CraftNode`：
```csharp
// Flight/FlightSceneScript.cs:183-193
public ICraftNode CraftNode { get { return this._craftNode; } }
```
切换只通过私有 `SetCraftNode(CraftNode)`：

```csharp
// Flight/FlightSceneScript.cs:1539-1581
private void SetCraftNode(CraftNode craftNode)
{
    if (this._craftNode != null) {
        this._craftNode.UpdateTarget(true);
        this._craftNode.SetIsPlayer(false, craftNode);                                        // :1544  <-- 老 craft 失去玩家身份
        this._craftNode.CraftScript.ActiveCommandPodChanged -= this.OnPlayerCraftActiveCommandPodChanged;
        this._craftNode.CraftScript.CraftStructureChanged -= this.OnPlayerCraftStructureChanged;
    }
    CraftNode craftNode2 = this._craftNode;
    this._craftNode = craftNode;
    if (this._craftNode.IsLoadedInGameView) {
        this._craftNode.CraftScript.ActiveCommandPodChanged += this.OnPlayerCraftActiveCommandPodChanged;
        this._craftNode.CraftScript.CraftStructureChanged += this.OnPlayerCraftStructureChanged;
    } else {
        this._craftNode.LoadedIntoGameView += this.<SetCraftNode>g__CraftLoaded|168_0;
    }
    this._craftNode.SetIsPlayer(true, craftNode2);                                            // :1559
    this.FlightControls.SetCraftNode(this._craftNode);                                        // :1560
    FlightSceneCraftHandler craftChanged = this.CraftChanged;
    if (craftChanged != null) craftChanged(this._craftNode);                                  // :1561-1565
    ...
    this.OnPlayerCraftActiveCommandPodChanged(craftScript, commandPod, commandPod2);           // :1579
    this._craftNode.UpdateTarget(false);                                                      // :1580
}
```

`CraftNode.SetIsPlayer` 遍历所有 part modifier 调 `OnIsPlayerCraftChanged(isPlayer, other)`：
```csharp
// Flight/Sim/CraftNode.cs:705-739
public override void SetIsPlayer(bool isPlayer, ICraftNode other) {
    this._isPlayer = isPlayer;
    ... foreach (PartData partData in assembly.Parts)
        foreach (PartModifierScript modifiers[i] in partData.PartScript.Modifiers)
            modifiers[i].OnIsPlayerCraftChanged(isPlayer, other);
}
```
`CommandPodScript.OnIsPlayerCraftChanged` 只在「自己就是 ActiveCommandPod」时向下派发：
```csharp
// Craft/Parts/Modifiers/CommandPodScript.cs:459-471
public override void OnIsPlayerCraftChanged(bool isPlayer, ICraftNode other) {
    base.OnIsPlayerCraftChanged(isPlayer, other);
    if (base.PartScript.CraftScript.ActiveCommandPod == this)
        isPlayerControlledChanged?.Invoke(this.IsPlayerControlled, this, other.CraftScript.ActiveCommandPod);
}
```
`IsPlayerControlled` 定义为：
```csharp
// CommandPodScript.cs:146-150
public bool IsPlayerControlled { get { return base.PartScript.CraftScript.ActiveCommandPod == this && base.PartScript.CraftScript.CraftNode.IsPlayer; } }
```

### 5.2 `EvaScript` 侧的反应

- `OnCommandPodIsPlayerControlledChanged(bool isPlayer, ICommandPod source, ICommandPod other)` — `EvaScript.cs:1833-1845`
  ```csharp
  EvaScript.UpdateCrosshairsVisibility(new bool?(isPlayer));
  this.UpdateCommandReplication(isPlayer, other);
  if (isPlayer) Game.Instance.FlightScene.FlightSceneUI.ShowMessage(Locale.GetString("Flight.Eva.SwitchedToFormat", ...), false, 5f);
  this.UpdateZoomEnabled();
  bool flag = isPlayer || (other == null || !other.IsEva);
  this.SetEvaCameraModesInUse(isPlayer, flag);
  this.UpdateCrewPointOfViewRegistration();
  ```
- `SetEvaCameraModesInUse(bool evaCamerasInUse, bool updateCameraState)` — `EvaScript.cs:2202-2231`：`_sharedCameraScript.SetEvaCamerasEnabled(...)`、`_fpsCameraController.SetVantageScript(_fpsVantage)`、`_thirdPersonCameraController.StaticTarget = this`、订阅/退订 `IsSelectedChanged`、`SetEnabled`。
- `EvaScript.TakeControl()` — `EvaScript.cs:1267-1280`（见第 2 节）。**`TakeControl` 本身不切 camera**，它只做 `UnloadFromCrewCompartment` + `SwitchToCommandPod`；相机切换是 `IsPlayerControlledChanged` 事件的副作用。
- `EvaScript.IsPlayerCraft` — `EvaScript.cs:607-613`：`Game.Instance.FlightScene.CraftNode == base.PartScript.CraftScript.CraftNode`。**这是判断「本地玩家是否在控制这个 EVA」的唯一权威方式**，多人 Mod 里必须重新定义（否则远端的 EVA 会被当成本地玩家 craft）。
- `EvaScript.ForceForward/ForceStrafe`（`:463-488`）、`TurningResponsiveness`（`:758-768`）等只依赖物理状态，不依赖 IsPlayer。
- **`AllowPlayerControl` 的语义**：`CraftNode.AllowPlayerControl { get; set; } = true`（`CraftNode.cs:152`）。它**不是**运行时门禁，只用于：
  - `FlightState.AddCraft` 的成就计数（`FlightState.cs:350`）
  - map view 的「CanTakeControl」（`Flight/MapView/UI/Inspector/InspectorItemViewModel.cs:102`）
  - 存档列表的 IsResumable / IsPlayerAllowed（`Menu/ListView/ActiveCraftsDetails.cs:88-89`）
  - 契约的 spawn craft（`Career/Contracts/Requirements/SpawnCraftRequirement.cs:247`）
  - `ChangePlayersActiveCommandPodImmediate` 里把它置回 `true`（`FlightSceneScript.cs:383`）
  **所以「把远端 craft 的 AllowPlayerControl 设为 false」并不能阻止本地控制，Mod 必须自己用 `IsPlayerCraft` 的替代实现来屏蔽。**（`EvaScript.TakeControl` 自身也不检查 `AllowPlayerControl`。）

### 5.3 相机单例

```csharp
// Craft/Parts/Modifiers/Eva/EvaSharedCamerasScript.cs:30-41
public static EvaSharedCamerasScript Instance {
    get {
        if (EvaSharedCamerasScript._instance == null) {
            EvaSharedCamerasScript._instance = Game.Instance.FlightScene.ViewManager.GameView.GameCamera.Transform.gameObject.AddComponent<EvaSharedCamerasScript>();
            EvaSharedCamerasScript._instance.Initialize();
        }
        return EVA...;
    }
}
```
**这是全局单例**，所有 EVA 共享同一对 `FpsController` / `ThirdPersonConroller`（`:56,61`）。
`EvaScript.OnPreNodeLoaded` 里每个 EVA 都抓同一份引用：
```csharp
// EvaScript.cs:1223-1238
this._sharedCameraScript = EvaSharedCamerasScript.Instance;
this._fpsCameraController = this._sharedCameraScript.FpsController;
...
this._fpsVantage = base.GetComponent<CameraVantageScript>();
this._fpsVantage.CameraController = this._fpsCameraController;
...
```
**后果**：多个 EVA 的 `SetEvaCameraModesInUse` 会互相覆写 `_fpsCameraController.SetVantageScript` / `StaticTarget` / `NearClipOverride`（`:2206-2230`、`1915-1922`）。**原版只支持「单个被玩家控制的 EVA」使用 FPS 相机**。多人 Mod 如果要让多个玩家同时 EVA，这个单例是硬冲突点。
（原版本身是单人游戏，`EvaScript.GetShouldInterpolate`（`:1742-1771`）里甚至显式假设「玩家 craft 的 active command pod 如果是 EVA，则那个 EVA 就是 `_fpsCameraController` 的持有者」。）

`GrapplingHookManagerScript` 也是单例（`Eva/GrapplingHookManagerScript.cs:12-33`），`GrapplingHookScript.EvaFrom/EvaTo` 涉及 EVA 之间的绳索（`EvaScript.cs:2522-2541` 处理 EVA↔EVA 燃料转移）。

---

## 6. 多 EVA / 外来 EVA

### 6.1 能不能同时存在多个 EVA？

**能，且是设计支持的**：
- `CrewCompartmentData.Capacity` 默认 3（`CrewCompartmentData.cs:21`），`CrewCompartmentScript.Crew` 是 `List<EvaScript>`（`CrewCompartmentScript.cs:55`）
- `CraftSplitter.ProcessDisconnectedBody` 每帧只处理一个断体（`CraftScript.cs:1980-1991` 里 `return true`），但会连续多帧处理完
- 每个 EVA 出舱后都是独立 `CraftNode`，`FlightState.CraftNodes` 是 `List<CraftNode>`（`FlightState.cs:29,120`）
- `CraftScript.NumAstronauts` 会统计同一 craft 内的 EVA part 数量（`CraftScript.cs:2035-2039`）
- 成就计数「>= 20 个有 command pod 且 AllowPlayerControl 的 craft node」（`FlightState.cs:348-355`）说明引擎预期大量 node

### 6.2 假设「只有单个 EVA」的地方

| 位置 | 假设 |
|---|---|
| `EvaSharedCamerasScript._instance`（`EvaSharedCamerasScript.cs:17`） | 全局唯一相机控制器对；多 EVA 互相覆写（见 5.3） |
| `EvaScript._fpsCameraController` / `_thirdPersonCameraController`（`EvaScript.cs:174,262`） | 每个 EVA 都指向同一实例 |
| `EvaScript.UpdateCrosshairsVisibility`（`EvaScript.cs:776-796`） | 只查 `FlightScene.CraftNode.CraftScript.ActiveCommandPod.EvaScript` —— **单玩家视角** |
| `EvaScript.IsPlayerCraft`（`EvaScript.cs:607-613`） | 全局唯一玩家 node |
| `Game.Instance.FlightScene.CraftNode`（大量使用） | 单玩家 node |
| `CameraManagerScript.Instance.RegisterCrewPointOfView`（`EvaScript.cs:2438`） | 相机管理器单例 |
| `EvaScript.CameraFollowSpeed`（`EvaScript.cs:292`，**static**） | 全局静态 |
| `EvaScript._achievementUnlockedSpacewalk` / `_achievementUnlockedWalkOnLuna`（`:57,60`，**static**） | 全局静态 |
| `EvaScript._waitForFixedUpdate`（`:63`，static） | 全局静态 |
| `CrewCompartmentScript._currentCrewEva`（`CrewCompartmentScript.cs:27`） | 每个 compartment **单个**「当前 EVA」（由 `FlightScene.ActiveCommandPodChanged` 更新，`:411-424`） |
| `FlightSceneInterfaceScript.ActiveMoveCrewRequest`（`Flight/UI/FlightSceneInterfaceScript.cs:57`） | 全局唯一乘员转移请求 |
| `MoveCrewRequest.UpdateAccessibleCompartments` 遍历**所有** craft node（`MoveCrewRequest.cs:132-146`） | 会跨 craft（甚至跨玩家）查找可进入的乘员舱 → **多人下会看到别人的舱** |

### 6.3 全局/静态状态清单（同步时必须注意）

- `EvaSharedCamerasScript._instance`（`EvaSharedCamerasScript.cs:17`）
- `GrapplingHookManagerScript._instance`（`GrapplingHookManagerScript.cs:12`）
- `EvaScript.CameraFollowSpeed`（`EvaScript.cs:292`）、三个 `_achievementUnlocked*`/`_waitForFixedUpdate`（`:57,60,63`）
- `CameraManagerScript.Instance`（`EvaScript.cs:1978, 2431, 2438, 2443`）
- `FlightSceneScript._instance` / `Instance`（`Flight/FlightSceneScript.cs:54,153`）、`OnSingletonUpdated`（`:164`）
- `CraftSplitter._eligibilityColliders`（`CraftSplitter.cs:19`，static 复用缓冲 —— **不是线程安全的**，多线程调用 `DetermineCraftNodeEligibility` 会串数据；原版在 `FlightLateUpdate` 串行执行，所以没问题）
- `FlightState._achievementUnlockedActiveCraftCount`（`FlightState.cs:26`）

### 6.4 「外来 EVA / 别人 craft 上的 EVA」

原版**没有任何权限检查**：
- `CrewCompartmentScript.UnloadCrewMember`（`:361`）不检查所属 craft 是否是玩家 craft
- `EvaScript.TakeControl`（`EvaScript.cs:1267-1280`）不检查
- 唯一的距离限制在 `ChangePlayersActiveCommandPodImmediate`（`FlightSceneScript.cs:363-369`，`PhysicsDistance*1000-100` 米），并且有 `ignoreDistance` 参数（`CraftSplitter.cs:142` 传 `true`）

→ **多人 Mod 必须自己在这些入口加权限门禁。** 好消息是入口很集中（第 8 节的 hook 清单）。

---

## 7. 确定性 / 逐帧更新

### 7.1 `EvaScript` 的更新接口

```csharp
// EvaScript.cs:45
public class EvaScript : PartModifierScript<EvaData>, IFlightUpdate, IGameLoopItem, IFlightFixedUpdate, IFlightLateUpdate, IEvaScript, ICameraTarget, IReactionEngine, IFuelConsumer
```

游戏循环（`GameLoop/FlightGameLoop.cs:390-405` 的 `Update`，`:179-185` 的 `FixedUpdate`，`:207-213` 的 `LateUpdate`）：
- `IFlightFixedUpdate.FlightFixedUpdate` — **暂停时也会被调用**（`FixedUpdate` 里只有 `IsPaused` 时走 `FixedUpdateCommon` 分支，`:156-163`）；`IsWarping` 时走 `IFlightFixedUpdateWarp` 分支（EVA 不实现该接口，`EvaScript.cs:830` 只实现 `IFlightFixedUpdate`），**所以时间加速时 EVA 的固定帧逻辑完全停摆**。
- `IFlightUpdate.FlightUpdate` — Unity Update 阶段；暂停时走 `IFlightUpdatePaused`（EVA 不实现 → 暂停时停摆）。
- `IFlightLateUpdate.FlightLateUpdate` — 只在 `FrameData.IsPaused == false` 时执行（`:192-202`）。

### 7.2 `IFlightFixedUpdate.FlightFixedUpdate`（`EvaScript.cs:830-906`）—— 主物理逻辑

```csharp
if (this.EvaActive && !this._timeManager.CurrentMode.WarpMode)     // :832  ← 只有真正出舱才跑
{
    this._fuelBurned = 0.0;                                        // :834
    this.IsGrounded = this._collisionCount > 0;                    // :835
    this.AutoUprightCharacter = this.IsGrounded || (inWater && !fps);  // :836
    this.AllowBodyRotation = !this.IsGrounded;                     // :837
    this.UseKinematicTurning = !this.AllowBodyRotation || this.IsFpsActive;  // :838
    Rigidbody rigidBody = base.PartScript.BodyScript.RigidBody;
    CraftControls controls = base.PartScript.CommandPod.Controls;  // :840
    ...
    rigidBody.drag = ...;                  // :847
    rigidBody.angularDrag = ...;           // :848
    rigidBody.interpolation = ...;         // :849
    // Ground/water: 把 velocity 按转向旋转  rigidBody.velocity = quaternion * rigidBody.velocity;  :854
    ...
    if (this.IsPlayerCraft) this.UpdateMovement(out zero, out zero2);   // :871-873  ★ 输入驱动部分
    if (!this.ShouldInterpolate) {
        if (this.UseKinematicTurning) this.UpdateKinematicTurning();    // :879
        if (this.AutoUprightCharacter) this.UprightCharacter();         // :883
    }
    if (zero.sqrMagnitude == 0f && this.IsGrounded) this.SlowDownCharacter(rigidBody, vector, this._bodySpeed);  // :886-889
    this._movementForceJetpack = zero2 * 0.01f;                     // :890
    this._crewAnimController.SideInput = controls.EvaStrafe;        // :892
    this._oldForward = rigidBody.transform.forward;                 // :893
    this.CurrentThrust = _movementForceJetpackMag + _turningTorqueJetpackMag * Mass;  // :894
}
this.UpdateAcceleration(base.PartScript.BodyScript.RigidBody);      // :896  ★ 无条件执行
this._smoothGs = Mathf.Lerp(...);                                   // :897
if (GTolerance > 0 && GDamageScale > 0 && ImpactDamageScale > 0)
    base.PartScript.TakeDamage(... PartDamageType.GForce);          // :898-901
this._collisionCount = 0; this._overFlowCollisions = 0;             // :902-903
this.IsGroundedTerrain = false; this._colliderRigidBody = null;     // :904-905
```

### 7.3 `UpdateMovement`（`EvaScript.cs:2641-2752`）—— 纯物理、纯输入

```csharp
float num2 = controls.EvaMoveFwdAft;  float evaStrafe = controls.EvaStrafe;
float evaRoll = controls.EvaRoll;     float evaPitch = controls.EvaPitch;
...
if (this.IsGrounded || this.IsInWater || flag) {                  // flag = JetpackEnabled && has fuel
    if (evaStrafe != 0f) totalForce += (1f - num3) * evaStrafe * this.ForceStrafe * right;   // :2665-2669
    if (num2 != 0f) { ... totalForce += (1f - num4) * num2 * this.ForceForward * forward; }  // :2670-2681
    if (!this.IsGrounded) { totalForceJetpack += totalForce; num += ...; }
}
switch (this._jumpState) {
  case JumpState.Start:    rigidBody.AddForce(vector * this.JumpPowerScalar, ForceMode.Impulse);  // :2695-2696
  case JumpState.Continue: rigidBody.AddForce(6.25f * JumpStrength * JumpPowerScalar * up);       // :2701
}
...
rigidBody.AddTorque(vector2);                                                     // :2739
if (evaMoveUpDown != 0f && (flag || IsInWater))
    totalForce += ForceUpJetpack * ... * evaMoveUpDown * up;                      // :2743
if (!this.IsSwimmingEnabled && craftFuelSource != null)
    this._fuelBurned += craftFuelSource.RemoveFuel(num * _fuelConsumption * Time.deltaTime * JetpackPowerScalar);  // :2749
rigidBody.AddForce(totalForce * 0.01f);                                           // :2751
```

### 7.4 `IFlightUpdate.FlightUpdate`（`EvaScript.cs:918-944`）—— 表现层 / 只在玩家 craft 上做交互

```csharp
if (this.EvaActive && !this._timeManager.CurrentMode.WarpMode) {
    this.IsWalking = base.PartScript.CommandPod.Controls.EvaWalk;                 // :922
    if (this.UseKinematicTurning && this.ShouldInterpolate) this.UpdateKinematicTurning();  // :923-926
    this.UpdateAnimationController();                                            // :927
    if (this.IsPlayerCraft) {                                                    // :928  ★ 只有玩家控制时
        this.UpdateJumpState();        // :930   读 Game.Instance.Inputs.EvaJump
        this.UpdateGrapplingHook();    // :931   读鼠标/准星、射线检测
        this.UpdateNozzles();          // :932   推进器粒子
        this.CheckAchievements();      // :933
    }
} else if (this.ActiveWhileInCrewCompartment && this.IsPlayerCraft) {
    this.UpdateGrapplingHook();        // :938
}
if (!this.GrapplingHookEnabled && this.GrapplingHook != null) this.DestroyGrapplingHook(false, true);  // :940-943
```

`IFlightLateUpdate.FlightLateUpdate`（`:909-915`）：只在 `AutoUprightCharacter && ShouldInterpolate` 时补一次 `UprightCharacter()`。

另外还有一条**独立协程**（不是游戏循环）：
```csharp
// EvaScript.cs:2145-2160  ProcessCompletedPhysicsCycle (在 Awake 里 StartCoroutine, :1560)
for (;;) {
    yield return EvaScript._waitForFixedUpdate;                 // WaitForFixedUpdate
    if (this.AutoUprightCharacter && this.ShouldInterpolate) this.UprightCharacter();
    if (this.EvaActive) this.DesiredUp = this.CalculateDesiredUp();   // :2156
}
```
`CalculateDesiredUp()`（`:1567-1664`）用**碰撞接触点**做地面法线/重力对齐计算。

### 7.5 输入驱动 vs 物理驱动 分类

| 项 | 驱动方式 | 位置 |
|---|---|---|
| 前进/横移/上下推力 | 输入 × `EvaPerformanceData` 力 → `Rigidbody.AddForce` | `:2665-2669, 2670-2681, 2743, 2751` |
| 跳跃 | 输入（`Inputs.EvaJump` / `Controls.EvaAnalogJump`）→ `AddForce(Impulse)` | `:2551-2579`（`UpdateJumpState`）+ `:2688-2706` |
| 滚转/俯仰/偏航力矩 | 输入 → `AddTorque`（用 `rigidBody.inertiaTensor`） | `:2707-2739` |
| 旋转（着地/水/FPS） | **`rigidBody.MoveRotation(quaternion)`**（kinematic 式直接设旋转） | `UpdateKinematicTurning` `:2582-2620` |
| 自动站直 | `rigidBody.MoveRotation` | `UprightCharacter` `:2817-2829` |
| 着地速度重定向 | `rigidBody.velocity = quaternion * rigidBody.velocity` | `:853-854` |
| 着地减速 | `body.AddForce(-velocity * k, ForceMode.Acceleration)` | `SlowDownCharacter` `:2240-2246` |
| 阻力/角阻力 | `rigidBody.drag` / `angularDrag`（`float.MaxValue` 锁定旋转，`:848`） | `:847-848` |
| 插值模式 | `rigidBody.interpolation` | `:849`（`GetShouldInterpolate` `:1742-1771`） |
| 动画 | 读输入/速度 → `_crewAnimController` 参数，`Update()` | `UpdateAnimationController` `:2342-2357` |
| 推进器粒子 | 读 `_movementForceJetpack` / `_turningTorqueJetpack` → 改 `Transform.forward` + 粒子开关 | `UpdateNozzles` `:2755-2791` |
| 关节/碰撞体父级 | 按 `AllowBodyRotation && inWater` 把 `CapsuleCollider` 挂到 hips 或 root | `UpdateControllerColiderParent` `:2398-2417` |
| 碰撞/接地判定 | Unity `Collision` 回调 → `_collisions[]` 环形缓冲 | `OnCollisionEnter/Stay` `:1807-1830`，`ProcessCollision` `:2104-2142` |

### 7.6 ⚠️ 对「physics-disabled ghost」的影响（多人 Mod 核心风险）

`CraftScript.EnablePhysics`：
```csharp
// Craft/CraftScript.cs:1734-1768
private void EnablePhysics(bool enable) {
    this._physicsEnabled = enable;
    foreach (part) foreach (modifier) modifier.OnBeforePhysicsChanged(enable);   // :1741
    foreach (BodyData bodyData in this.Data.Assembly.Bodies) {
        if (enable) bodyData.BodyScript.RigidBody.isKinematic = false;
        else        bodyData.BodyScript.RigidBody.isKinematic = true;            // :1752  ★ 直接 kinematic
    }
    this._frameVelocity = null;
    if (!enable) { this.RecenterDebris(...); this.RecenterTransformOnCoM(true, null); }   // :1758-1759  ★ 会把 transform 归零到 CoM
    foreach (part) foreach (modifier) modifier.OnPhysicsChanged(enable);         // :1765
}
```
`CraftNode.SetPhysicsEnabled`：
```csharp
// Flight/Sim/CraftNode.cs:748-783
if (this._craftScript != null && this._craftScript.IsPhysicsEnabled != enabled) {
    this._craftScript.IsPhysicsEnabled = enabled;
    if (enabled) { ... SetVelocity(...) 重建速度 ... PhysicsEnabled?.Invoke(this, reason); }
    else { if (this.InContactWithPlanet) this.UpdateSurfaceParameters(); PhysicsDisabled?.Invoke(this, reason); }
}
```

**EVA 在 physics-disabled 下的行为：**
1. `EvaScript.OnBeforePhysicsChanged(bool enabled)`（`:967-974`）：`enabled == false` 时把 `collisionDetectionMode` 设为 `Discrete`。**没有做别的补偿。**
2. `EvaScript.OnPhysicsChanged(bool enabled)`（`:1213-1220`）：`enabled == true` 时按 `EvaActive` 设 `ContinuousDynamic`。**没有做别的补偿。**
3. `UpdateMovement` 里所有 `AddForce`/`AddTorque`/`AddForce(Impulse)` 在 kinematic Rigidbody 上**全部无效**。
4. `UpdateKinematicTurning` / `UprightCharacter` 用 `rigidBody.MoveRotation(...)` —— 对 kinematic Rigidbody **仍然有效**（会直接改 transform 与旋转）。但 `DesiredUp` 依赖 `ProcessCompletedPhysicsCycle` 的协程 + `CalculateDesiredUp()`，后者需要 `_collisions[]`（碰撞回调），而 kinematic body 不产生碰撞回调 → `DesiredUp` 会退化为 `rigidBody.transform.up` 或 `-GravityNormal`（`CalculateDesiredUp` `:1626-1662`）。
5. `rigidBody.velocity = ...`（`:854`）对 kinematic body **无效**。
6. `rigidBody.drag` / `angularDrag` / `interpolation` 对 kinematic body 无意义。
7. `EvaScript.FlightFixedUpdate` 的 `if (this.IsPlayerCraft) UpdateMovement(...)`（`:871-873`）意味着**远端 EVA 本来就不跑输入驱动的移动**，只跑 `UpdateAcceleration` / `_smoothGs` / G 力伤害（`:896-901`）。
8. **`EnablePhysics(false)` 还会调用 `RecenterTransformOnCoM(true, null)`（`CraftScript.cs:1759`）—— 这会把整个 EVA 的 transform 位置/旋转重置到质心对齐姿态。** 对 ghost 渲染来说，这意味着**设完位置后立刻被重置**，必须在这之后重新写 transform，否则外观会跳。

**结论**：把远端 EVA 做成 physics-disabled ghost 时：
- ✅ 可以冻结位置 / 让 `MoveRotation` 之类的直接变换生效；
- ❌ 所有 jetpack 推力、跳跃、速度、着地减速、drag 全部失效；
- ❌ 碰撞回调消失 → `IsGrounded`（`:835`）恒为 `false` → `AutoUprightCharacter` 恒 `false`（`:836`）→ `AllowBodyRotation` 恒 `true`；
- ❌ 动画的 `InAir` 判定（`UpdateAnimationController` `:2355` 依赖 `IsGrounded`/`GroundedOnFeet`）恒定错误；
- ⚠️ `RecenterTransformOnCoM` 会在 disable 瞬间重置 transform；
- ⚠️ `FlightFixedUpdate` 仍会调用 `TakeDamage`（G 力，`:898-901`），但 `UpdateAcceleration` 靠 `body.velocity` 队列（`:2311-2333`）——kinematic body 的 velocity 是 0，**→ `Acceleration` 恒为 0**（`(0-0)/num`），`_smoothGs` 由 `|0 - GravityForce|*0.102f` 项**驱动向 0 收敛**，故 `Max(0, _smoothGs - GTolerance) == 0` → **不扣血**。（初稿曾写成"会被缓慢打死"，**已修正**——见文末「复核与修正」。）

`CraftNode.OnTimeMultiplierModeChanged` 走的是同一条 physics disable 路径（`CraftNode.cs:1141-1162`）：
```csharp
if (e.EnteredWarpMode)  { this._warp = true; this._physicsEnabledBeforeWarp = this.IsPhysicsEnabled; this.SetPhysicsEnabled(false, PhysicsChangeReason.Warp); }
else if (e.ExitedWarpMode) { ... this.SetPhysicsEnabled(physicsEnabledBeforeWarp, PhysicsChangeReason.Warp); }
```

---

## 8. 边界情况

### 8.1 母船没有 command pod 时 EVA

- EVA part 自己**就是** command pod（`CommandPodScript.IsEva`，`CommandPodScript.cs:142,476-477`），所以 `EvaScript.base.PartScript.CommandPod` 永远非空（EVA part 自带 `CommandPodScript`）。
- `MoveBodyToNewCraftNode` 会优先选新 assembly 里的 command pod 作为 root part；因为 EVA part 是 command pod，**它自己会变成 root part**（`CraftSplitter.cs:311-327`）：
  ```csharp
  foreach (PartData partData2 in assembly.Parts) if (partData == null && partData2.PartType.IsCommandPod) partData = partData2;
  ...
  partData.IsRootPart = true;
  ```
- `SplitCraftNode` 里 `craftNode2.HasCommandPod = craftScript.RootPart.Data.PartType.IsCommandPod`（`:119`）→ EVA node `HasCommandPod = true`。
- `EvaScript.OnCraftConfigurationChanged`（`:1848-1870`）在飞行场景下：
  ```csharp
  if (this.IsCrewCompartmentAttachPointInUse) { if (CrewCompartment == null) LoadIntoCrewCompartment(GetCrewCompartment(this), null, true); }
  else if (!Game.InFlightScene || base.PartScript.CraftScript.RootPart.CommandPod == base.PartScript.CommandPod)
      this.ActivateEvaForDisconnectedAstronaut();     // :1868
  ```
  → 如果「EVA 的 attach point 没被占用」且「EVA 自己是 root part 的 command pod」，它会**自动激活为断连宇航员**（`ActivateEvaForDisconnectedAstronaut`，`:1477-1509`）：`CrewCompartment = null`、切 `ContinuousDynamic`、挂 `CollisionNotifier`、`ResetInertiaTensor()`、`UpdateNodeName(node, CrewName)`。
- `ActivateEvaForDisconnectedAstronaut` 里有 `EvaScript.UpdateNodeName`（`:1465-1474`）：**把 craft node 名字改成乘员名**，并刷新 map view 搜索列表：
  ```csharp
  (node as CraftNode).Name = name;
  ... mapViewSearchPanel.RefreshSearchItemList();
  ```
  → 出舱后 craft node 名字 = 乘员名（如 "Jeb"）。
- `EvaScript.OnNodeNameChanged`（`:2047-2057`）：如果 EVA 在舱内（`!EvaActive`）且是 root part，改名会同步到 `PartData.PreferredNodeName`。

### 8.2 地面 EVA vs 轨道 EVA

差异全部来自 `IsGrounded` / `IsInWater` / `IsGroundedOnRigidBody`：
- `ForceForward`/`ForceStrafe` 在地面用 `ForceForwardGround=1000`/`ForceStrafeGround=1000`；空中用 `ForceForwardJetpack`/`ForceStrafeJetpack=500`（`EvaScript.cs:463-488`）
- `TurningResponsiveness` 地面 `1.0` / 空中 `0.0`（`:758-768`；`EvaPerformanceData.cs:53,58`）
- `UpdateMaxSpeeds`（`:2623-2638`）：地面有上限（`MaxForwardSpeedGround=15`, `MaxStrafeSpeedGround=5`），空中 `float.MaxValue`
- `UprightCharacter`（`:2817-2829`）用 `GravityMagnitude * mass` 缩放 —— 低重力星球（`SurfaceGravity < 4`）会切低重力动画（`UpdateGravitySuitableAnimationController` `:2545-2548`）
- `GetShouldInterpolate`（`:1742-1771`）：地面 `IsGroundedTerrain` 时插值
- `SlowDownCharacter` 只在 `IsGrounded` 时跑（`:886-889`）
- 入水：`WaterPhysics.Enabled = EvaActive`（`:1914`）、`UpdateWaterPhysics` 设 `PrecisionMode = High`（`:2805-2808`）、`rigidBody.drag = 0.25f`（`:847`）
- 0G 环境：`ZeroGeeAnimation = !ReferenceFrame.IsSurfaceLocked`（`UpdateAnimationController` `:2347`）
- 轨道 EVA 的物理仍靠 `Physics.gravity = this._craftNode.CraftScript.GravityForce`（`FlightSceneScript.cs:485`）——**注意：重力用的是玩家 craft 的**，不是 EVA 自己的！这是单玩家假设的又一处体现。

### 8.3 从别人的 craft 出舱

见 6.4：原版无权限检查。唯一限制是距离检查（`ChangePlayersActiveCommandPodImmediate`，`FlightSceneScript.cs:363-369`），而且 `SplitCraftNode` 内部用 `ignoreDistance: true` 绕过（`CraftSplitter.cs:142`）。

### 8.4 时间加速 / 暂停下的 EVA

- **时间加速（warp）**：
  - `FlightFixedUpdate` 的守卫 `!this._timeManager.CurrentMode.WarpMode`（`:832`）→ EVA 移动逻辑全停
  - `FlightUpdate` 同样守卫（`:920`）
  - `CraftNode.OnTimeMultiplierModeChanged` 会 `SetPhysicsEnabled(false, Warp)`（`CraftNode.cs:1147`）→ 见 7.6 的所有后果
  - `EvaScript.OnTimeMultiplierModeChanging`（`:2066-2069`）：`SetAnimatorEnabled(!e.CurrentMode.WarpMode)`
  - `IReactionEngine.SupportsWarpBurn => false`（`:2913-2919`）
  - `UpdateGrapplingHook` 里 `AdjustTetherLength` 会 `ShowMessage`（`:1520-1529`）
- **暂停（pause）**：
  - `FlightUpdate` 不会被调用（`FlightGameLoop.cs:375-384` 走 `IFlightUpdatePaused`，EVA 未实现）
  - `FlightLateUpdate` 不会被调用（`:192-202`）
  - **`FlightFixedUpdate` 仍会被调用**（`:156-163` 只走 `FixedUpdateCommon` 分支 → 对 `IFlightUpdate`/`IFlightFixedUpdate` 的 UpdateGroup 而言，暂停时 `_scripts.FixedUpdate` 组不执行）。注意 `FixedUpdate` 方法里 `IsPaused` 时 `return`（`:162`），**所以 `_scripts.FixedUpdate` 组（含 EVA）在暂停时不执行**。但 `_scripts.FixedUpdateCommon`（`IFixedUpdate`）会执行。
  - **但 `CraftScript.IFlightLateUpdate`（`:896`）在暂停时也不执行** → **暂停期间出舱，`SetStructureChanged()` 不会被处理，EVA 会卡在「关节已断但还没 split」的状态**，直到解除暂停。这是一个重要的时序边界。`EvaDetails`：`ProcessDestroyedCraftNodes` 在 `OnLateUpdate` 里（`:501`），也不在暂停时执行 → **`CraftNodeRemoved` 也不会在暂停时抛出**。

### 8.5 父 craft node 被摧毁时 `EvaScript` 做什么

1. **part 被摧毁** → `EvaScript.OnPartDestroyed()`（`:1196-1210`）：
   ```csharp
   if (Game.InFlightScene) {
       if (base.Data.CrewMember != null) base.Data.CrewMember.State = CrewMemberState.Deceased;    // :1203
       if (this.IsPlayerCraft) this.OnCommandPodIsPlayerControlledChanged(false, null, null);      // :1207
   }
   ```
2. **乘员舱被摧毁** → `CrewCompartmentScript.OnPartDestroyed()`（`:316-327`）对**舱内每个** `EvaScript` 施加 100 点伤害：
   ```csharp
   foreach (EvaScript evaScript in this.Crew) evaScript.PartScript.TakeDamage(100f, PartDamageType.Basic);
   Game.Instance.FlightScene.ActiveCommandPodChanged -= this.OnActiveCommandPodChanged;
   ```
3. **绳钩连接的对象被摧毁** → `EvaScript.GrapplingHookPartDestroyed`（`:1774-1777`）→ `DestroyGrapplingHook(false, true)`
4. **craft node 整体被摧毁**：`CraftNode.DestroyCraft()`（`CraftNode.cs:579-596`）只置 `IsDestroyed` + 抛事件；真正的移除在 `FlightState.ProcessDestroyedCraftNodes`（`State/FlightState.cs:415-440`）。EVA 作为独立 node 时若被摧毁，`CrewMember.State` 只有在**part 级** `OnPartDestroyed` 触发时才变 `Deceased`。
5. **玩家 craft 高空爆炸**（`CraftNode.cs:1342-1349`）：
   ```csharp
   if (this.IsPlayer) { FlightLog.LogTotalCraftDestruction(...); rootPart.BodyScript.ExplodePart(rootPart, 100f); return; }
   this.DestroyCraft();
   ```
   → 玩家 craft 的 root part 被炸。如果 root part 是 EVA part，走第 1 条。
6. `EvaScript.OnDestroy()`（`:1954-2017`）清理：Dispose 三个 `EventMigrator`、退订 `MovedToNewCraft`、`FlightSceneScript.CraftChanged`、`CameraManagerScript.UnregisterCrewPointOfView`、`WaterPhysics.Changed`、`TimeMultiplierModeChanging`、`IsSelectedChanged`。

---

## 对联机同步的影响点

### A. 需要订阅的事件（权威 hooks）

| 事件 | 声明位置 | 触发时机 | 用途 |
|---|---|---|---|
| `FlightState.CraftNodeAdded` | `State/FlightState.cs:111` | `AddCraft` 内（`FlightState.cs:343-347`） | **EVA 出舱必然触发**（`CraftSplitter.cs:121`）；也是普通发射/分裂 |
| `FlightState.CraftNodeRemoved` | `State/FlightState.cs:116` | `ProcessDestroyedCraftNodes`（`FlightState.cs:433-437`），由 `FlightSceneScript.OnLateUpdate`（`:501`）驱动 | **EVA 回舱合并后必然触发**（`CraftSplitter.cs:75` → `CraftNode.cs:579`） |
| `FlightSceneScript.CraftChanged` | `Flight/FlightSceneScript.cs:104`；在 `SetCraftNode` 里抛（`:1561-1565`） | 玩家切换控制对象 | 本地玩家 node 变化 |
| `FlightSceneScript.ActiveCommandPodChanged` | `Flight/FlightSceneScript.cs:94`；由 `OnPlayerCraftActiveCommandPodChanged`（`:1337-1346`）抛 | 玩家 craft 的 active pod 变化 | EVA 面板/控制方案切换 |
| `FlightSceneScript.ActiveCommandPodStateChanged` | `Flight/FlightSceneScript.cs:99`；`RaiseActiveCommandPodStateChanged()`（`:627`） | 由 `EvaScript.OnCraftStructureChanged`（`EvaScript.cs:1012-1023`）延迟 2 帧触发 | EVA 状态 UI 刷新 |
| `ICraftScript.ActiveCommandPodChanging` / `ActiveCommandPodChanged` | `Craft/CraftScript.cs`（`SetActiveCommandPod`，`:1484-1504`） | pod 切换 | |
| `ICommandPod.IsPlayerControlledChanged` | `Craft/Parts/Modifiers/CommandPodScript.cs:62` | `OnIsPlayerCraftChanged`（`:459-471`）/ `OnActiveCommandPodChanged`（`:772-789`） | **EVA 相机/控制耦合的真正入口** |
| `CrewCompartmentScript.CrewEnter` / `CrewExit` | `Craft/Parts/Modifiers/Eva/CrewCompartmentScript.cs:40,45`；抛出点 `:175-180` / `:369-374` | 乘员进出舱 | **最语义化的 EVA 出/回舱 hook** |
| `EvaScript.ActiveWhileInCrewCompartmentChanged` | `Craft/Parts/Modifiers/Eva/EvaScript.cs:287`（接口 `ModApi/Craft/Parts/IEvaScript.cs:18`） | `ActiveWhileInCrewCompartment` setter（`:308-329`） | 椅子上/出舱状态变化 |
| `IPartScript.PartDestroyed` | `Craft/Parts/PartScript.cs`（`OnPartDestroyed` 抛，`:1289-1295`） | part 摧毁 | 乘员死亡 |
| `IPartScript.MovedToNewCraft` | `Craft/Parts/PartScript.cs:123`；抛出点 `:1264-1268` | **part 在 craft 之间搬移**（split/merge 的核心） | 追踪 EVA part 归属 |
| `CraftNode.PhysicsEnabled` / `PhysicsDisabled` | `Flight/Sim/CraftNode.cs:132,127` | `SetPhysicsEnabled`（`:748-783`） | 远端 ghost 的物理开关 |
| `TimeManager.TimeMultiplierModeChanging` | `EvaScript.cs:1558` / `CraftNode.cs:1141-1162` | warp 进出 | 冻结判定 |

### B. 可以/需要 Patch 的方法（按侵入度排序）

**推荐 patch（语义清晰、单点）**
1. `CraftSplitter.SplitCraftNode(CraftNode, Assembly)` — `Flight/Sim/CraftSplitter.cs:111`
   → Prefix/Postfix：拿到 `craftNode2.NodeId` 与新 assembly 的 part 列表，生成「EVA 出舱」同步包。
2. `CraftSplitter.MergeCraftNode(CraftNode source, CraftNode target)` — `CraftSplitter.cs:22`
   → 生成「EVA 回舱」同步包（`source.NodeId` → 消失）。
3. `EvaScript.TakeControl()` — `EvaScript.cs:1267`
   → 出舱意图的最上游拦截点（做权限检查 / 广播意图）。
4. `EvaScript.LoadIntoCrewCompartment(CrewCompartmentScript, Action, bool)` — `EvaScript.cs:947`
   → 回舱意图的最上游拦截点。
5. `CrewCompartmentScript.UnloadCrewMember(EvaScript, bool)` — `CrewCompartmentScript.cs:361`
   → 所有出舱路径的汇聚点（含 `OnCrewDestroyed` / `OnCrewPartConnectionDestroyed`）。
6. `FlightSceneScript.ChangePlayersActiveCommandPodImmediate(ICommandPod, ICraftNode, bool)` — `Flight/FlightSceneScript.cs:361`
   → 拦截「试图控制别人 craft」。
7. `EvaScript.GetShouldInterpolate()` — `EvaScript.cs:1742`（private）
   → 远端 EVA 的插值策略覆盖点。
8. `EvaScript.UpdateMovement(out, out)` — `EvaScript.cs:2641`（private）
   → 若要远端 EVA 不做本地输入驱动，可在此提前 return（**注意原版已经用 `IsPlayerCraft` 门禁（`:871`），只要修好 `IsPlayerCraft` 就够了**）。

**备选**
- `CraftScript.ProcessDisconnectedBodies()` — `Craft/CraftScript.cs:1980`（private）
- `FlightState.AddCraft(CraftNode, CraftNode)` — `State/FlightState.cs:320`（唯一 NodeId 分配点）
- `FlightState.ProcessDestroyedCraftNodes()` — `State/FlightState.cs:415`
- `CraftSplitter.DetermineCraftNodeEligibility(List<IBodyScript>)` — `CraftSplitter.cs:216`（private static）→ 若原版 EVA 真靠 `PreventDebris`，可在这里强制返回 true

**必须替换而不是 patch 的**
- `EvaScript.IsPlayerCraft`（`EvaScript.cs:607-613`）与所有 `Game.Instance.FlightScene.CraftNode == ...` 比较 → 需要换成「本地玩家 craft」的概念。
- `EvaSharedCamerasScript.Instance` 单例（`EvaSharedCamerasScript.cs:17-41`）→ 多玩家 FPS 相机的硬冲突，必须为每个玩家实例化独立相机控制器。
- `EvaScript.UpdateCrosshairsVisibility`（`EvaScript.cs:776-796`）→ 只考虑单玩家。

### C. 需要复制的数据（最小集）

**出舱（Split）时必须同步**
| 字段 | 来源 | 说明 |
|---|---|---|
| 新 node 的 id（host 侧） | `CraftNode.NodeId`（`CraftNode.cs:444`） | 自定义或映射，不能直接用 |
| 新 node 的 name | `CraftNode.Name`（`ChangePlayersActiveCommandPodImmediate` 后由 `EvaScript.UpdateNodeName` 改为乘员名，`EvaScript.cs:1465-1474, 1507`） | |
| position / velocity / heading | `CraftNodeData.cs:244-246` | 帧坐标 |
| parent planet | `CraftNode.Parent.Name` | |
| `AllowPlayerControl` | `CraftNode.AllowPlayerControl`（`:152`） | |
| `HasCommandPod` | `CraftSplitter.cs:119` | |
| 该 node 的完整 craft XML | `CraftNode.SavePendingCraftXmlChanges`（`CraftNode.cs:1171-1188`）/ `FlightState.SaveCraftXml`（`FlightState.cs:459-462`） | 含 `EvaData` designer 字段（crewId/crewName/jetpack…） |
| `InitialCraftNodeIds` | `CraftSplitter.cs:145-146` | |
| 乘员绑定 | `EvaData.CrewId`（`EvaData.cs:77-83`）+ `CrewMember.NodeId`（写入于 `EvaScript.cs:2041`） | `CrewMember.NodeId` 必须在两端保持一致或被 `CrewManager` 重置（`CrewManager.cs:39-43`） |
| 每个 EVA 的 `CrewMember.Id` | `EvaData.cs:77` | 跨机主键 |

**逐帧/高频同步（远端 EVA 的位置与姿态）**
- 权威姿态源：`base.PartScript.BodyScript.RigidBody.transform`（position/rotation）、`rigidBody.velocity`、`rigidBody.angularVelocity`。
- `EvaScript` 暴露的可用于动画同步的只读量（都是每帧由本地物理算出的，远端应改为网络量驱动）：
  - `_bodySpeed`（`:865`）→ `CrewAnimController.Speed`（`:2350`）
  - `_currentVerticalSpeed` / `_currentForwardSpeed` / `_currentStrafeSpeed`（`:866-868`）→ animator（`:2351-2353`）
  - `CrewAnimController.ForwardInput = controls.EvaMoveFwdAft`、`TurnInput = controls.EvaTurn`（`:2348-2349`）
  - `SideInput = controls.EvaStrafe`（`:892`）
  - `InAir`（`:2355`）、`InWater`（`:2354`）、`ZeroGeeAnimation`（`:2347`）、`InsideAtmosphere`（`:2346`）
  - `DesiredUp`（`:436`，由协程写）、`IsGrounded`（`:563`）、`IsGroundedTerrain`（`:593`）
  - `JetpackEnabled` / `IsWalking` / `GrapplingHookEnabled`（`:643, 628, 493`）—— 这些是**可写**的（会写回 `Data`）
  - `CurrentThrust`（`:431`）、`Gs`（`:507`）
- **建议的最小同步包**：`{ nodeId, position, rotation, velocity, angularVelocity, isWalking, jetpackEnabled, animatorParams(forward/turn/side/speed/inAir/inWater/zeroG), grapplingHookTargetPartId+tetherLength }`。
  推导来源见 `UpdateAnimationController`（`EvaScript.cs:2342-2357`）。

### D. 已知的、Mod 必须自己解决的坑（按严重度）

1. **node id 不跨机一致**（`FlightStateData.cs:304-314`）→ 必须自建映射表。`CrewManager` 载入时会按 `NodeId` 校验（`CrewManager.cs:39-43`），不同步会导致乘员被重置为 `Available`。
2. **物理驱动**：EVA 的移动 100% 靠 `AddForce`/`AddTorque`/`velocity`（`EvaScript.cs:2641-2752`），ghost 模式下全部失效（见 7.6）。
3. **`EnablePhysics(false)` 会 `RecenterTransformOnCoM`**（`CraftScript.cs:1759`）→ ghost 设置位置后会被重置。
4. ~~**G 力伤害在有 bug 的 ghost 上会持续扣血**~~ —— **已复核推翻**:`Acceleration` 在 kinematic body 上恒为 0（`velocity - vector == 0`,`EvaScript.cs:2313-2332`），`_smoothGs` 向 0 收敛 ⇒ `TakeDamage` 入参为 0（`:897-901`）。**不需要处理**。见文末「复核与修正」。
5. **`EvaSharedCamerasScript` 单例**（`EvaSharedCamerasScript.cs:17`）→ 多玩家 FPS 相机冲突。
6. **`IsPlayerCraft` 单玩家假设**（`EvaScript.cs:607-613`）贯穿 `FlightUpdate` 的交互部分（`:928-934`）、`GetShouldInterpolate`（`:1746-1765`）、`OnPartDestroyed`（`:1205`）。
7. **暂停期间出舱/回舱不会推进结构变更**（`CraftScript.cs:896` 的 `IFlightLateUpdate` 与 `FlightSceneScript.cs:501` 的 `ProcessDestroyedCraftNodes` 都不在暂停路径上）→ 状态会卡住到解除暂停。
8. **回舱后玩家 node 可能悬空**：`MergeCraftNode`（`CraftSplitter.cs:75`）销毁 source node，但内部**没有** `ChangePlayersActiveCommandPodImmediate` 切回母船。如果玩家自己就是那个 EVA，`FlightSceneScript._craftNode` 会指向已销毁 node。**标记为「未确认：未见自动切回逻辑」，需要实测验证。**
9. **`MoveCrewRequest.UpdateAccessibleCompartments` 遍历所有 craft node**（`MoveCrewRequest.cs:132-146`）→ 多人下会列出别人的乘员舱并高亮，且 `LoadCrewMember` 会真的把乘员搬过去（无权限检查）。
10. **`FlightSceneScript.ProcessInputs` 的重力来源**：`Physics.gravity = this._craftNode.CraftScript.GravityForce`（`FlightSceneScript.cs:485`）用玩家 craft 的重力 → 多个玩家在不同天体时的物理不一致（对 EVA 影响尤其明显）。
11. **`EvaScript.OnCraftConfigurationChanged` 的自动激活**（`:1848-1870`）→ 结构变化时可能**自动**把 EVA 变成 `ActivateEvaForDisconnectedAstronaut` 状态，这是隐式状态跃迁，同步时容易漏。
12. **`OnCraftStructureChanged` 的延迟 2 帧 `RaiseActiveCommandPodStateChanged`**（`EvaScript.cs:1012-1023`）→ UI 状态事件有延迟，不要把它当作即时状态源。

---

## 未确认 / 需要进一步验证的点

1. **`EvaScript.<>c__DisplayClass209_0.<LoadIntoCrewCompartment>g__LoadIntoCompartment|0` 的方法体缺失**（`EvaScript.cs:947-964` 只留下引用；全仓 grep `LoadIntoCompartment` 只命中 3 行）。第 3 节的第 3–7 步顺序是**基于调用点语义推断**的，请用 ILSpy/dnSpy 补齐该方法体验证。
2. **Eva part 配置中 `preventDebris` 的实际值**（决定 `DetermineCraftNodeEligibility` 是否让宇航员成为独立 craft node，`CraftSplitter.cs:216-250`）。本地只有反编译 `.cs`，没有 part 配置资源。请在游戏内 inspect EVA part 的 `PreventDebris` 开关，或从 `SimpleRockets2/Assets/.../PartConfig` 之类资源中确认。
3. **回舱后是否自动把玩家 node 切回母船**（`MergeCraftNode` 内未见 `ChangePlayersActiveCommandPodImmediate`）。
4. **`CraftNode.OnMergedWithCraftNode`（`CraftNode.cs:651`）的完整实现**只做了部分阅读，可能有额外的玩家/相机处理。
5. **暂停时 `IFlightFixedUpdate` 是否真的不执行**：我从 `GameLoop/FlightGameLoop.cs:156-163` 的 `IsPaused` 分支推断 `_scripts.FixedUpdate`（`IFlightFixedUpdate`）在暂停时**不**被 update，但这依赖 `UpdateGroup<T>.Update` 只在 "非 Common" 组执行的实现细节 —— `UpdateGroup` 内部（`GameLoop/UpdateGroup.cs`）未逐行核对。
6. **`FlightState.OnPlayerCraftActiveCommandPodChanged`（`FlightState.cs:508-512`）写 `PlayerNodeId` 的完整上下文**未完全核对。

---

## 复核与修正(2026-09-18,由主 agent 独立核对)

> 本节记录对上面正文的**独立复核结果**。正文保留原样以便追溯,以下为裁决。

### 已推翻的结论(不要再当成坑)

1. **"ghost EVA 会被 G 力伤害慢慢打死"——不成立。**
   机理:`UpdateAcceleration`(`EvaScript.cs:2311-2333`)取 `body.velocity` 队列差分;kinematic 刚体 `velocity` 恒为 0 ⇒ `Acceleration = (0-0)/num = 0`。
   代入 `_smoothGs`(`:897`)的增量项 `(|0 - GravityForce| * 0.102f - _smoothGs)` 被钳到 `[-1, 1]`,即**每帧把 `_smoothGs` 往 0 拉**,不会升高。
   于是 `TakeDamage(Max(0, _smoothGs - GTolerance) * …)`(`:898-901`)入参为 0 ⇒ **无伤害**。
   (初稿把"速度恒 0"误推成"加速度恒定",方向反了。)

### 已确认成立的结论(可直接用于设计)

2. **`EvaSharedCamerasScript` 是单例**(`EvaSharedCamerasScript.cs:14-40`,`_instance` + `GameCamera.Transform.gameObject.AddComponent`),挂在本机 GameCamera 上。
   **但**幽灵 EVA 触发它的路径受 `IsPlayerCraft` 守卫(`EvaScript.cs:2429-2445` 的 `UpdateCrewPointOfViewRegistration`),而 `IsPlayerCraft` 在幽灵上恒 `false` ⇒ **不会给本机玩家凭空多出视角选项**。结论从"冲突风险"降级为"低风险,顺带确认"。
3. **`EnablePhysics(false)` 会 `RecenterTransformOnCoM(true, null)`**(`CraftScript.cs:1758-1759`)——成立,且现有 mod 已按正确顺序规避:`InitializeRemoteCraft` 先 `SetPhysicsEnabled(false, Warp)`(`MpNetworkManager.cs:2078`)、**之后**才 `ApplyRemoteState` 写位置/朝向(`:2103-2110`)。**新增 EVA 幽灵必须沿用这个顺序**。
4. **回舱后玩家活动节点是否被自动切回母船——仍未确认**,需运行时验证。
   补充证据(有利于"会自动切回"):`LoadIntoCompartment` 在 `ConnectParts`→`MergeCraftNode` **之前**就先调了 `ChangePlayersActiveCommandPodImmediate(crewCompartment.PartScript.CommandPod, 舱所在节点)`(`EvaScript.cs` IL 行 800),即**先把玩家切回母船、再合并**。因此正常路径下不会悬空;**但如果该调用因距离校验失败**(`ignoreDistance` 缺省 `false`,`FlightSceneScript.cs:369`),则可能悬空 —— 这正是需要实测的那一支。

### 仍待验证(保留)

- 原「未确认」第 2 条(EVA part 的 `preventDebris` 实际配置):**已由旁证消解**——即便 `preventDebris` 为 false,单部件 Drood 的碰撞包围盒也会命中"任轴 > 10m"分支(`CraftSplitter.cs:243-246`),故 EVA body 仍会成为独立节点。
- 第 1 条(`LoadIntoCrewCompartment` local function 缺体):**已解决**——用 `ilspycmd` 从 `SimpleRockets2.dll` 取到完整实现(见主文档 §十)。

