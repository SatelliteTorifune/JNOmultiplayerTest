# MP 远程飞船朝向同步 —— 最终方案(LunaMultiplayer srfRel 相对地表朝向)

> 项目:JNOmultiplayerTest(SimpleRockets 2 多人联机 mod)
> 反编译参考:`C:/renko/shitProgram/jnoCode`
> KSP 参考:`C:/renko/unityProjects/LunaMultiplayer`
> 状态:已修正坐标系转换 bug(帧空间 vs 行星空间),待双端实测

---

## 1. 问题

- mod 同步对方 craft 朝向有误:对方 `FlightData.Pitch/Bank` 与发送端不一致;
- 画面随行星自转(warp)出现 yaw 漂移;
- 手动对齐双端时间/自转后误差变小(证明"双端自转差"是根因)。

## 2. 反编译根因(坐标系链)

- 朝向权威来源:`CraftScript.FrameHeading = CenterOfMass.rotation`([`CraftScript.cs:385`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/CraftScript.cs:385));游戏 [`CraftScript.cs:2049`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/CraftScript.cs:2049) 每帧把质心朝向覆盖为命令舱座椅朝向。
- 行星自转:`RotationAngle = InitialRotation + AngularVelocity × Time`;双端时间不同 → 自转角度不同 → 同一表面点行星空间坐标/朝向不同。
- `craft.ReferenceFrame = _gameView.ReferenceFrame`([`CraftNode.cs:477`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/Sim/CraftNode.cs:477),行星固定、纯绕 Y)。
- `FlightData.Pitch/Bank` 依赖 `CraftForward/PositionNormalized/CraftRight`(反编译 [`CraftFlightData.cs`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/FlightData/CraftFlightData.cs))。

## 3. 方案演进(已试)

| 方案 | 结果 |
|------|------|
| 同步 CenterOfMass.rotation + 行星空间传输 | Pitch/Bank 部分修复 |
| A:PosNorm 反射写 PositionNormalized + Harmony `get_CraftRight` | Pitch/Bank 两端精确一致 |
| D:视觉补偿 `comRot·RotateY(−Δ)` | warp 时 yaw 漂移(Δ 滞后 + 绝对朝向基准错) |
| C:强制同步 host 的 `FlightState.Time`/行星自转 | 副作用大(500km 位置爆炸、地图空引用、client 飞起),回退 |
| 发送端自转外推补偿 `RotateY(−θ_send)×Heading` | warp 不动,但**相对地表朝向不一致** → 剩余固定误差 |
| 经纬度+ASL 定位 | 无效(位置不影响朝向),回退 |
| **LunaMultiplayer srfRel(最终)** | 待实测 |

## 4. 最终方案:LunaMultiplayer srfRel(相对地表朝向)

KSP LunaMultiplayer([`VesselPositioner.cs`](../C:/renko/unityProjects/LunaMultiplayer/LmpClient/Systems/VesselPositionSys/ExtensionMethods/VesselPositioner.cs)):

```csharp
vessel.srfRelRotation = currentSurfaceRelRotation;   // 表面相对旋转(相对行星地表)
var rotation = (Quaternion)lerpedBody.rotation * currentSurfaceRelRotation;  // 世界 = 行星旋转 × 表面相对旋转
```

**核心**:传输 `srfRelRotation`(**飞船相对行星地表的朝向**),接收端 `rotation = 行星当前旋转 × srfRelRotation` 渲染 → **"相对各自行星地表"朝向一致**。玩家站在各自行星上看到一致,不依赖双端自转/时间同步、无 warp 漂移、无全局副作用。

**关键教训**:此前"绝对朝向一致"方案(`RotateY(−θ_send)×Heading` = comRot)在双端行星自转不同时,玩家感知的相对地表朝向不一致 → 剩余误差。LunaMultiplayer 从根上解决(用各自行星当前旋转渲染相对地表朝向)。

## 5. 已实施代码

- `recdata` 新增 `SrfRel`(Quaterniond);[`MpMessage.cs`](../Assets/Scripts/Net/MpMessage.cs) 序列化加 4×double;
- 新增 `LateUpdate` 渲染前写回(防游戏覆盖)+ `[DefaultExecutionOrder(1000)]`;

### 5.1 坐标系关键事实(反编译确认)

- `Transform.rotation` / `CenterOfMass.rotation` 是**帧空间**(GameView 参考系即"世界")。
- 帧↔行星转换([`ReferenceFrame.cs`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/GameView/ReferenceFrame.cs:136)):
  - `FrameToPlanetRotation(q) = RotateY(θ_frame) * q`
  - `PlanetToFrameRotation(q) = RotateY(-θ_frame) * q`
- 表面锁定帧:`θ_frame = θ_planet + _planetLocalRotation`(常量偏移,[`ReferenceFrame.cs:274`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/GameView/ReferenceFrame.cs:274))。

### 5.2 原 bug(已修正)

原实现把 `comRot`(帧空间)直接乘 `RotateY(±θ_planet)`,把帧空间当行星空间,缺了 `RotateY(θ_frame)` 因子:

- 发送端:`SrfRel = RotateY(-θ_send_planet) * comRot`(错,comRot 是帧空间)
- 接收端:`headingFrame = RotateY(θ_recv_planet) * SrfRel`(错,赋给帧空间却只乘行星角)

净效果 = `RotateY(θ_recv_planet - θ_send_planet) * comRot` → **yaw 误差 = 双端行星自转角差**。这正解释了 plan 记录的三个现象(对方 Pitch/Bank 不一致、warp yaw 漂移、对齐双端自转后误差变小)。即原实现本质仍是已失败的"绝对朝向"方案,并未真正实现 srfRel。

### 5.3 修正后(真正 srfRel)

- 发送端([`TrySampleLocalCraft`](../Assets/Scripts/Net/MpNetworkManager.cs)):`SrfRel = RotateY(θ_frame - θ_planet) * comRot`(相对地表朝向,与行星自转无关);
- 接收端([`ApplyRemoteState`](../Assets/Scripts/Net/MpNetworkManager.cs) + [`ForceRemoteHeading`](../Assets/Scripts/Net/MpNetworkManager.cs)):`headingFrame = frame.PlanetToFrameRotation(planet.Rotation * SrfRel) = RotateY(θ_planet - θ_frame) * SrfRel`;
- 数学验证:接收端帧空间 = `RotateY(θ_planet_recv - θ_frame_recv) * RotateY(θ_frame_send - θ_planet_send) * comRot`。因双端同行星表面锁定帧 `θ_frame - θ_planet` 为同一常量 → 结果 = `comRot`(发送端帧空间朝向)。两端帧空间朝向一致,随各自行星自转保持相对地表不变,无 warp 漂移、无时间同步依赖。

## 6. 待实测与后续

1. 重新编译双端实测:对方飞船相对地表朝向两端一致、warp 无漂移、无全局副作用;
2. `FlightData.Pitch/Bank` 目前走基线逻辑(接收端口径);如需两端精确一致,补 PosNorm(反射写 `PositionNormalized`)+ Harmony `get_CraftRight`;
3. 若仍有余差:提高发包频率、或发送端 comRot 采样稳定性(座椅 vs body)。