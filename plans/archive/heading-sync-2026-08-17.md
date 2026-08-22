# MP 远程飞船朝向同步 —�?最终方�?LunaMultiplayer srfRel 相对地表朝向)

> 项目:JNOmultiplayerTest(SimpleRockets 2 多人联机 mod)
> 反编译参�?`C:/renko/shitProgram/jnoCode`
> KSP 参�?`C:/renko/unityProjects/LunaMultiplayer`
> 状�?�?已修复并双端实测通过(相对地表朝向一致、warp 无漂移、Pitch/Bank 两端一�?

---

## 1. 问题

- mod 同步对方 craft 朝向有误:对方 `FlightData.Pitch/Bank` 与发送端不一�?
- 画面随行星自�?warp)出现 yaw 漂移;
- 手动对齐双端时间/自转后误差变�?证明"双端自转�?是根�?�?

## 2. 反编译根�?坐标系链)

- 朝向权威来源:`CraftScript.FrameHeading = CenterOfMass.rotation`([`CraftScript.cs:385`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/CraftScript.cs:385));游戏 [`CraftScript.cs:2049`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/CraftScript.cs:2049) 每帧把质心朝向覆盖为命令舱座椅朝向�?
- 行星自转:`RotationAngle = InitialRotation + AngularVelocity × Time`;双端时间不同 �?自转角度不同 �?同一表面点行星空间坐�?朝向不同�?
- `craft.ReferenceFrame = _gameView.ReferenceFrame`([`CraftNode.cs:477`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/Sim/CraftNode.cs:477),行星固定、纯�?Y)�?
- `FlightData.Pitch/Bank` 依赖 `CraftForward/PositionNormalized/CraftRight`(反编�?[`CraftFlightData.cs`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/FlightData/CraftFlightData.cs))�?

## 3. 方案演进(已试)

| 方案 | 结果 |
|------|------|
| 同步 CenterOfMass.rotation + 行星空间传输 | Pitch/Bank 部分修复 |
| A:PosNorm 反射�?PositionNormalized + Harmony `get_CraftRight` | Pitch/Bank 两端精确一�?|
| D:视觉补偿 `comRot·RotateY(−�?` | warp �?yaw 漂移(Δ 滞后 + 绝对朝向基准�? |
| C:强制同步 host �?`FlightState.Time`/行星自转 | 副作用大(500km 位置爆炸、地图空引用、client 飞起),回退 |
| 发送端自转外推补偿 `RotateY(−θ_send)×Heading` | warp 不动,�?*相对地表朝向不一�?* �?剩余固定误差 |
| 经纬�?ASL 定位 | 无效(位置不影响朝�?,回退 |
| **LunaMultiplayer srfRel(最�?** | �?双端实测通过(相对地表朝向一致、warp 无漂�? |

## 4. 最终方�?LunaMultiplayer srfRel(相对地表朝向)

KSP LunaMultiplayer([`VesselPositioner.cs`](../C:/renko/unityProjects/LunaMultiplayer/LmpClient/Systems/VesselPositionSys/ExtensionMethods/VesselPositioner.cs)):

```csharp
vessel.srfRelRotation = currentSurfaceRelRotation;   // 表面相对旋转(相对行星地表)
var rotation = (Quaternion)lerpedBody.rotation * currentSurfaceRelRotation;  // 世界 = 行星旋转 × 表面相对旋转
```

**核心**:传输 `srfRelRotation`(**飞船相对行星地表的朝�?*),接收�?`rotation = 行星当前旋转 × srfRelRotation` 渲染 �?**"相对各自行星地表"朝向一�?*。玩家站在各自行星上看到一�?不依赖双端自�?时间同步、无 warp 漂移、无全局副作用�?

**关键教训**:此前"绝对朝向一�?方案(`RotateY(−θ_send)×Heading` = comRot)在双端行星自转不同时,玩家感知的相对地表朝向不一�?�?剩余误差。LunaMultiplayer 从根上解�?用各自行星当前旋转渲染相对地表朝�?�?

## 5. 已实施代�?

- `recdata` 新增 `SrfRel`(Quaterniond);[`MpMessage.cs`](../Assets/Scripts/Net/MpMessage.cs) 序列化加 4×double;
- 新增 `LateUpdate` 渲染前写�?防游戏覆�?+ `[DefaultExecutionOrder(1000)]`;

### 5.1 坐标系关键事�?反编译确�?

- `Transform.rotation` / `CenterOfMass.rotation` �?*帧空�?*(GameView 参考系�?世界")�?
- 帧↔行星转换([`ReferenceFrame.cs`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/GameView/ReferenceFrame.cs:136)):
  - `FrameToPlanetRotation(q) = RotateY(θ_frame) * q`
  - `PlanetToFrameRotation(q) = RotateY(-θ_frame) * q`
- 表面锁定�?`θ_frame = θ_planet + _planetLocalRotation`(常量偏移,[`ReferenceFrame.cs:274`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/GameView/ReferenceFrame.cs:274))�?

### 5.2 �?bug(已修�?

原实现把 `comRot`(帧空�?直接�?`RotateY(±θ_planet)`,把帧空间当行星空�?缺了 `RotateY(θ_frame)` 因子:

- 发送端:`SrfRel = RotateY(-θ_send_planet) * comRot`(�?comRot 是帧空间)
- 接收�?`headingFrame = RotateY(θ_recv_planet) * SrfRel`(�?赋给帧空间却只乘行星�?

净效果 = `RotateY(θ_recv_planet - θ_send_planet) * comRot` �?**yaw 误差 = 双端行星自转角差**。这正解释了 plan 记录的三个现�?对方 Pitch/Bank 不一致、warp yaw 漂移、对齐双端自转后误差变小)。即原实现本质仍是已失败�?绝对朝向"方案,并未真正实现 srfRel�?

### 5.3 修正�?真正 srfRel)

- 发送端([`TrySampleLocalCraft`](../Assets/Scripts/Net/MpNetworkManager.cs)):`SrfRel = RotateY(θ_frame - θ_planet) * comRot`(相对地表朝向,与行星自转无�?;
- 接收�?[`ApplyRemoteState`](../Assets/Scripts/Net/MpNetworkManager.cs) + [`ForceRemoteHeading`](../Assets/Scripts/Net/MpNetworkManager.cs)):`headingFrame = frame.PlanetToFrameRotation(planet.Rotation * SrfRel) = RotateY(θ_planet - θ_frame) * SrfRel`;
- 数学验证:接收端帧空间 = `RotateY(θ_planet_recv - θ_frame_recv) * RotateY(θ_frame_send - θ_planet_send) * comRot`。因双端同行星表面锁定帧 `θ_frame - θ_planet` 为同一常量 �?结果 = `comRot`(发送端帧空间朝�?。两端帧空间朝向一�?随各自行星自转保持相对地表不�?�?warp 漂移、无时间同步依赖�?

## 6. 实测结果与下一�?

### 6.1 实测结果(�?已通过)

- 双端实测:对方飞船相对地表朝向两端一致、warp 无漂移、无全局副作�?
- `FlightData.Pitch/Bank` 两端一�?接收端已通过反射�?`PositionNormalized`/`CraftForward` 刷新 FlightData(见日�?`MP FlightData 刷新诊断`)�?

### 6.2 下一步重心【已归档修订 2026-08�?

> 本节为撰写当时的"下一�?，现状如下（均已实现或转移，勿再当作待办）：

1. **Body 同步**：当前仅同步 `BodyRotations`(�?body 相对根的欧拉�?。下一步做更完整的 body 级同�?位置/速度/角速度、分�?对接/残骸事件),彻底消除"分裂/散架";——【✅ 已转移】转�?[`multi-craft-sync-2026-08-16.md`](../../multi-craft-sync-2026-08-16.md) MC2�?
2. **平滑插帧**：当前是"前后两包线�?Slerp 插�?([`UpdateRemoteCrafts`](../Assets/Scripts/Net/MpNetworkManager.cs) 内联插�?。下一步改为带时间戳的环形缓冲 + 100~150ms 延迟补偿,容忍抖动与乱�?——【✅ 已实现】环形缓�?+ `RenderDelayMs`�?
3. **�?craft 支持**：当前只同步 `FlightSceneScript.Instance.CraftNode`(本机唯一玩家飞船)。下一步支持每玩家多艘飞船(NodeId �?CraftNode 映射)、残�?对接后的多节点同步。——【✅ 已转移】整体转�?[`multi-craft-sync-2026-08-16.md`](../../multi-craft-sync-2026-08-16.md)(方案研究阶段)�