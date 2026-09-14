# 远程飞船游戏侧速度缺失（缺自转项）根因分析

> 状态：📋 **分析完成，修复未做**（根因已定位并核实，待拍板后实施）
> 日期：2026-09-13
> 主题：联机生成的远程飞船（幽灵）"只有位置信息而不带速度"——游戏侧读速度 ≈ 0 / 缺行星自转项，导致相对速度等功能出错
> 关键文件：`Assets/Scripts/Net/MpNetworkManager.cs`（mod 接收端）、游戏反编译 `CraftNode.cs` / `CraftScript.cs` / `CraftFlightData.cs` / `PlanetNode.cs` / `ReferenceFrame.cs` / `FlightGameLoop.cs`

---

## 〇、结论（一句话）

**问题不在协议/数据链路，而在接收端把速度写进游戏字段时"约定用错了"**：mod 把**地表相对速度**直接写进游戏的 `GroundedSurfaceVelocity`，而该字段的游戏约定是**"地表系惯性速度"（需含行星自转项 ω×r）**。游戏每帧的表面锁定更新用它纯旋转换算回行星空间 → `craft.Velocity` 缺自转项（测试行星 ≈ **158.85 m/s**），静止远程船直接读成 **0**。mod 随后虽用 `SetStateVectors` 补写了正确值，但时序在游戏之后、且 `CraftFlightData` 已在游戏阶段快照了缺自转项的值、mod 的 FlightData 反射刷新又不覆盖速度字段 → **游戏侧几乎所有读速度的功能拿到的都是"只有位置没速度"的幽灵**。

## 一、现象与影响

- 现象：远程飞船位置/朝向正常，但游戏侧读它的速度 ≈ 0（静止/慢速船）或恒缺行星自转分量（运动船）。
- 影响示例：
  - 相对速度计算（`localCraft.Velocity − remoteCraft.Velocity`，或基于 `FlightData` 的接近速度）必然出错；
  - `FlightData.Velocity / VelocityMagnitude / SurfaceVelocity* / VerticalSurfaceVelocity / LateralSurfaceVelocity`、`CraftScript.FrameVelocity`、`craft.Velocity`（游戏阶段读时）全部受影响；
  - 碰撞伤害走 Unity `Collision.relativeVelocity`（读刚体速度，随 `InjectGhostMotion` 注入的是正确相对速度）——**基本不受影响，是唯一幸存的路径**。

## 二、数据链路核对（这部分没问题）

| 环节 | 位置 | 状态 |
|---|---|---|
| 采样（地表相对速度 = 惯性转地表 − 自转项） | `MpNetworkManager.cs:2686-2694`（`TrySampleLocalCraft`） | ✅ 正确 |
| 序列化（写 3 个 double） | `MpMessage.cs:480`（`WriteRecdata`） | ✅ 带速度 |
| 反序列化 | `MpMessage.cs:533-535` | ✅ |
| 应用：`SetStateVectors`（**已**加回自转项） | `MpNetworkManager.cs:2445-2447`（`ApplyRemoteState`） | ✅ 值正确（但被游戏覆盖，见 §三.2） |

## 三、根因链

### 1. 游戏每帧用 `GroundedSurfaceVelocity` 覆盖逻辑速度，且换算**不加自转项**

幽灵船（物理禁用 + `InContactWithPlanet=true`）每帧走游戏 `CraftNode.UpdateCraft` 表面锁定分支（反编译 `CraftNode.cs:1235-1240`）：

```csharp
else if (this.InContactWithPlanet)
{
    this.Heading = Parent.Rotation * this.GroundedSurfaceRotation.Value;
    this.SetStateVectorsAtDefaultTime(
        Parent.SurfaceVectorToPlanetVector(this.GroundedSurfacePosition.Value),   // 位置：纯旋转，正确
        Parent.SurfaceVectorToPlanetVector(this.GroundedSurfaceVelocity.Value));  // ← 速度：纯旋转，不加自转！
    this.RecalculateFrameState(...);
}
```

- `SurfaceVectorToPlanetVector` 是**纯 Y 轴旋转**（`PlanetNode.cs:508-511`，`RotateVectorAroundYAxis(v, +RotationAngle)`），不减/不加自转项。
- **游戏的字段约定**：`GroundedSurfaceVelocity` 应存"地表系惯性速度"。游戏自己写它时用的是 `CalculateSurfaceVelocity(...)`（`CraftNode.cs:1370`，`UpdateSurfaceParameters`）——静止贴地船的惯性速度 = 自转线速度，转回行星空间恰好正确。即游戏约定 `GroundedSurfaceVelocity = CalculateSurfaceVelocity(pos) + V_rel`（地表系）。
- **mod 的写法**：`ApplyRemoteGroundedSurface`（`MpNetworkManager.cs:2606`）直接写 `data.Velocity`（地表**相对**速度），**漏了 `CalculateSurfaceVelocity(data.Position)` 这一项**。

于是游戏每帧算出的 `Orbit.Velocity = RotateY(V_rel)`：静止船 → **0**（正确应为 158.85 m/s 自转速度）；运动船 → 恒缺 158.85 m/s 分量。

> 注：`CalculateSurfaceVelocity`（`PlanetNode.cs:293-303`）返回 `ω×r`（行星空间东向切向速度）；`RotateY(ω×r_surface, +θ) = ω×r_planet`（绕同一轴旋转与叉积可交换），故游戏"写地表系、读行星系"的自洽性成立。mod 在 `ApplyRemoteState` 里 `SurfaceVectorToPlanetVector(data.Velocity) + SurfaceVectorToPlanetVector(CalculateSurfaceVelocity(data.Position))` 正是补回自转项的正确公式——但只用在 `SetStateVectors`，没有用在 `GroundedSurfaceVelocity`。

### 2. 时序：游戏先写（错值），mod 后写（对值），帧内振荡

- 游戏 `FlightGameLoop.Update`（MonoBehaviour 默认执行序 0）→ `FlightSceneScript.OnUpdate`（`FlightSceneScript.cs:543-575`）→ `ProcessNodeTree(FlightUpdate)` → `CraftNode.FlightUpdate` → `UpdateCraft`（写缺自转项速度）→ 随后 `_scripts.Update` 组 → `CraftScript.FlightUpdate`（`CraftScript.cs:959-961`）→ `UpdateFlightData` → `CraftFlightData.Update`（`CraftFlightData.cs:578-584`）快照 `Velocity = craftNode.Velocity`（**缺自转项**）、`SurfaceVelocityFrame = FrameVelocity + FrameSurfaceVelocity`。
- mod `MpNetworkManager.Update` 是 `[DefaultExecutionOrder(1000)]`（`MpNetworkManager.cs:25`）→ **之后**才跑 `UpdateRemoteCrafts` → `ApplyRemoteState` 的 `SetStateVectors`（补自转项）。

→ `craft.Velocity` 在帧内"游戏阶段=缺自转项、mod 阶段=正确"来回振荡；**谁在游戏阶段读谁就拿到错值**（绝大多数游戏功能都是）。

### 3. `CraftScript.FrameVelocity` 从 kinematic 刚体推导 ≈ 0

- `CraftScript.FrameVelocity`（`CraftScript.cs:410-441`）是惰性缓存，按质量加权平均 `RigidBody.velocity`。
- 幽灵全 kinematic；`InjectGhostMotion`（`EngineVisualSync.cs:499-549`）只为烟雾注入**不含自转项**的帧相对速度、且仅当变化 >0.1 m/s 才写（阈值 0.01 m²/s²）。
- 游戏每帧 `RecalculateFrameState`（`CraftScript.cs:1385` 清缓存）与 `FlightLateUpdate`（`CraftScript.cs:949` 清缓存）还会把缓存清空。

→ `FrameVelocity ≈ 0`（静止）/缺自转项（运动），`FlightData.SurfaceVelocityFrame` 随之错。

### 4. `CraftFlightData` 在游戏阶段快照 + mod 反射刷新不覆盖速度字段

- `CraftFlightData.Update`（`CraftFlightData.cs:560-596`）每帧在游戏阶段跑：`Velocity = craftNode.Velocity`（当时的缺自转项值）、`SurfaceVelocityFrame = FrameVelocity + FrameSurfaceVelocity`（表面锁定帧 `FrameSurfaceVelocity = 0`，见 `ReferenceFrame.cs:277`，故 ≈ 注入的缺自转项相对速度）。
- mod 的"FlightData 立即刷新"（`ApplyRemoteState` ⑤，`MpNetworkManager.cs:2530-2552`）只用反射刷新了 `PositionNormalized` 和 `CraftForward`（为修 Pitch/BankAngle），**没有刷新 `Velocity / VelocityMagnitude / SurfaceVelocity* / VerticalSurfaceVelocity / LateralSurfaceVelocity / Acceleration*`**。

→ `FlightData` 速度字段停留在游戏阶段快照的缺自转项值上，直到下一帧被再次覆盖。

## 四、证据清单（反编译源码，只读参考）

| 事实 | 位置 |
|---|---|
| 表面锁定分支每帧 `SetStateVectorsAtDefaultTime(SurfaceVectorToPlanetVector(GroundedSurfaceVelocity))` | `jnoCode/.../Flight/Sim/CraftNode.cs:1235-1240` |
| 游戏写 `GroundedSurfaceVelocity = CalculateSurfaceVelocity(...)`（地表系惯性速度约定） | `CraftNode.cs:1366-1371` |
| `SurfaceVectorToPlanetVector` = 纯旋转 | `Flight/Sim/PlanetNode.cs:508-511` |
| `CalculateSurfaceVelocity` = ω×r（行星空间东向切向） | `PlanetNode.cs:293-303` |
| `CraftFlightData.Update`：`Velocity = craftNode.Velocity`、`SurfaceVelocityFrame = FrameVelocity + FrameSurfaceVelocity` | `Craft/FlightData/CraftFlightData.cs:560-596` |
| `CraftScript.FrameVelocity` 惰性推导自 `RigidBody.velocity` | `Craft/CraftScript.cs:410-441` |
| `CraftScript.FlightUpdate` → `UpdateFlightData`（游戏 Update 阶段） | `CraftScript.cs:959-961, 2250-2252` |
| 游戏 Update 阶段先于 mod（`OnUpdate` 内 `ProcessNodeTree(FlightUpdate)` 在 `_scripts.Update` 组之前） | `GameLoop/FlightGameLoop.cs:372-406` |
| mod 写 `GroundedSurfaceVelocity = data.Velocity`（漏自转项） | `Assets/Scripts/Net/MpNetworkManager.cs:2606` |
| mod `ApplyRemoteState` 补自转项写 `SetStateVectors`（但时序在后） | `MpNetworkManager.cs:2441-2447` |
| mod FlightData 刷新只覆盖 `PositionNormalized`/`CraftForward` | `MpNetworkManager.cs:2530-2552` |
| `InjectGhostMotion` 注入缺自转项速度、仅变化时写 | `Assets/Scripts/Net/EngineVisualSync.cs:499-549` |

## 五、修复方向（待拍板，未实施）

1. **最小修复（推荐）**：`ApplyRemoteGroundedSurface` 改写
   `GroundedSurfaceVelocity = data.Velocity + planet.CalculateSurfaceVelocity(data.Position)`
   与游戏自身约定一致 → 游戏每帧换算出的 `Orbit.Velocity` 天然正确，帧内振荡消失（`craft.SurfaceVelocity` 属性语义会随之变为"地表系惯性速度"，与游戏原生行为一致，需一并核对消费方）。
2. **补 FlightData 缺口**：`ApplyRemoteState` ⑤ 增加速度字段反射刷新（`Velocity / VelocityMagnitude / SurfaceVelocity / SurfaceVelocityFrame / SurfaceVelocityMagnitude / VerticalSurfaceVelocity / LateralSurfaceVelocity`），或复用 `CraftFlightData.Update` 的等价计算。
3. **可选**：`InjectGhostMotion` 注入时补自转项 + 写后清 `_frameVelocity` 缓存（`rb.velocity` 写入后 `_frameVelocity = null` 的时机已由游戏 FlightLateUpdate 每帧清），覆盖 HUD 地表速度/导航球读数。

## 六、相关历史（不要重复调研）

- **发送端**缺自转项问题（`PlanetVectorToSurfaceVector` 纯旋转不减自转 → 静止船上报 158.85 m/s → 接收端外推放大成瞬移）**已修复**：`TrySampleLocalCraft` 减 `CalculateSurfaceVelocity(pos)`。见 `archive/latency-smoothing-2026-08-22.md` §7（§529-534）与 `AGENT_CONTEXT.md:86`。
- 本次是**接收端**同一自转项的镜像问题（写回 `GroundedSurfaceVelocity` 时不加自转项），此前未记录。
- 旁注："幽灵 `FrameVelocity` 可能陈旧" 已在 `archive/engine-fx-sync-2026-08-18.md:312` 提到，但未定位到根因。

## 七、验证思路（实施后复测）

- 静止远程船：`craft.Velocity.magnitude` 应 ≈ 行星自转线速度（测试行星 158.85 m/s）而非 0；`FlightData.VelocityMagnitude` 同理。
- 运动远程船：`craft.Velocity` = 相对速度 + 自转项，帧内不再振荡（游戏阶段/mod 阶段一致）。
- 相对速度：`local.Velocity − remote.Velocity` 的模在双船同向/反向/静止场景下与地表相对速度差一致。
- `MP smoothing` 日志 `vel=` 是包内相对速度（本来就对），不能作为本问题判据；需直接读游戏侧字段。
