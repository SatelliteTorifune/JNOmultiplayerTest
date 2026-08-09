# MP 远程飞船朝向同步 —— 根因定位与方案选型(C 方案)

> 项目:JNOmultiplayerTest(SimpleRockets 2 多人联机 mod)
> 反编译参考:`C:/renko/shitProgram/jnoCode`
> 状态:方案 C 已实现,待双端实测验证;方案 D 已清除

---

## 1. 问题

mod 同步对方 craft 朝向的代码有问题。症状(实测日志):

- 双端同一星球同一地点,对方飞船 `FlightData.Pitch/Bank` 与发送端本机不一致(如本机 `-1.5 / 0`,对方显示 `-33.01 / 30.07`);
- 进一步:画面随行星自转(warp)出现 yaw 漂移。

## 2. 反编译定位(根因链)

### 2.1 朝向权威来源

- [`CraftScript.cs`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/CraftScript.cs):`FrameHeading` getter = `CenterOfMass.rotation`;每帧 `SetCenterOfMassGameObjectPosition` 设 `CenterOfMass.rotation = PilotSeatOrientation.rotation`。
- 接收端原本只写根 `Transform.rotation`,从不同步 `CenterOfMass` → `FrameHeading` 一直错。
- 修复:同时写 `CenterOfMass.rotation`,并统一以"行星空间"传输 heading。

### 2.2 坐标系链

- **表面坐标(surface/网格固定)**:跨端一致(两端 `PlanetVectorToSurfaceVector` 均 ~9740xx)。
- **行星空间(planet)**:行星自转不同 → 同一表面点的行星空间径向/朝向不同(Host `PosNorm=0.120`,Client `-0.685`)。
- **帧空间(frame)**:`FrameToPlanetRotation = _rotation * q`、`PlanetToFrameRotation = _rotationInverse * q`;帧差 ≈ 自转差。
- 关键反编译:`RotationAngle = InitialRotation + AngularVelocity × Time`([`FlightState.cs:299`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/State/FlightState.cs:299));每帧 `PlanetNode.UpdateRotation` 使 `RotationAngle += AngularVelocity × elapsedTime`;任意时刻 `RotationAngleAt(t) = RotationAngle + AngularVelocity × (t − Time_now)`。
- **根因:双端 `FlightState.Time` 不同 → 行星自转角度不同步 → 同一表面点的行星空间坐标/径向/朝向不同 → Pitch/Bank 错;warp 时误差随时间放大。**

### 2.3 FlightData 指标依赖

- [`CraftFlightData.cs`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/FlightData/CraftFlightData.cs):
  - `Pitch = 90 − Acos(dot(CraftForward, PositionNormalized))`;
  - `BankAngle = Acos(dot(PositionNormalized, CraftRight)) − 90`;
  - `CraftForward` / `PositionNormalized` 有 private set(可反射写);
  - `CraftRight` 是**实时 getter**(`FrameToPlanetVector(CenterOfMass.right)`),无法反射覆盖 → 必须 Harmony;
  - `FlightData.Update` 每帧用 `craftNode.ReferenceFrame` 刷新。

## 3. 方案历史

| 方案 | 做法 | 结果 |
|------|------|------|
| (早期)全局 `PlanetRotationAngle` 同步 | client 直接设 `planet.RotationAngle = 发送端值` | 已回退:client 跳变、host 位置错误 |
| A(PosNorm 覆盖) | 反射写 `FlightData.PositionNormalized = 发送端 PosNorm`,`CraftForward = 发送端 heading.forward` | 保留:Pitch/Bank 修复的必需部分 |
| (Harmony) | `get_CraftRight` 补丁返回发送端 heading(行星空间)的 right | 保留:Bank 归零必需 |
| D(视觉补偿) | 接收端根旋转 = `comRot · RotateY(−Δ)`,`Δ = A_send − A_recv`;不改全局行星 | **已清除**:warp 时 yaw 漂移 |
| **C(强制同步 host 时间/自转)** | client 每 0.5s 用 host 状态包的 `time` 覆盖本端 `FlightState.Time`,用 `PlanetRotationAngle` 覆盖本端行星 `RotationAngle` | **最终方案** |

## 4. 最终方案 C(已实现)

### 原理

双端自转角差 = `AngularVelocity × ΔTime`。既然根因是双端时间不同步,直接让 client 强制同步 host 的时间与行星自转:

- 双端 `FlightState.Time` 一致 → 行星 `RotationAngle` 一致 → 同一表面点的行星空间坐标/径向/朝向一致;
- 方案 D 的 Δ 补偿自然归零,画面不再 yaw 漂移;
- 接收端根旋转可直接用发送端朝向(不再需要补偿)。

### 代码要点(`MpNetworkManager.cs`)

- 状态包已携带发送端 `time`(状态包外层字段)与 `data.PlanetRotationAngle`;
- `RemoteCraft.LastStateTime` 记录最近状态包的发送端 time;
- `SyncHostTime()`(仅 client,0.5s 节流):找第一个带 `PlanetRotationAngle` 的远程飞船 → 设 `hostRc.Node.Parent.RotationAngle = hostRc.Target.PlanetRotationAngle` 与 `FlightSceneScript.Instance.FlightState.Time = hostRc.LastStateTime`;
- 反编译确认 [`FlightSceneScript.cs:239`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/FlightSceneScript.cs:239) `public FlightState FlightState { get; private set; }` 是具体类 `FlightState`(实现 `IGameTime`,`Time` 有 setter)→ 可直接赋值。

### 预期副作用(可接受)

- 会改变本端时间与行星坐标系,可能引起本机飞船位置/视角跳变;
- 若出现位置大跳,后续可加插值或仅 warp 时同步。

## 5. 保留的修复(与方案 C 互补,非替代)

- **方案 A**:`PosNorm` 反射写 `PositionNormalized` + `CraftForward` 反射写发送端朝向 → 使 `FlightData.Pitch/Bank` 两端精确一致;
- **Harmony `PatchCraftRight`**:`get_CraftRight` 返回发送端 heading(行星空间)的 right → `BankAngle` 精确归零;
- **`BodyRotations`**:同步每个 body 相对质心的局部姿态(防分裂/散架);
- **`_remoteHeadingMap` / `TryGetRemoteHeading`**:供 Harmony 补丁查询发送端 heading。

## 6. 已清除的方案 D 代码(本次清理)

- `Mod.recdata.ComRotFrame` 字段及两处构造器初始化;
- `MpMessages.WriteRecdata` / `ReadRecdata` 中 ComRotFrame 的读写;
- `MpNetworkManager.ForceRemoteHeading`(方案 D 专用:含 `RotateY(−Δ)` 补偿 + body 姿态写回);
- `LateUpdate` 中 `ForceRemoteHeading` 调用(保留 `RefreshRemoteFlightData`);
- `ApplyRemoteState` 中的 Δ 补偿分支(`ComRotFrame * RotateY(−Δ)`),根旋转改为 `frame.PlanetToFrameRotation(data.Heading)`;
- `TrySampleLocalCraft` 中 `data.ComRotFrame = comRotFrame` 赋值及注释;
- 日志中的 `发送comRot` 字段。

## 7. 待验证

1. Unity 重新编译,双端实测;
2. 期望:client 端日志 `本地自转角` 趋近 `发送自转角`、`Δ→0`;warp 时对方飞船不再 yaw 漂移;对方 `Pitch/Bank` 保持精确一致;
3. 观察副作用:强制改时间/自转是否导致本机位置/视角跳变,是否需要插值或仅 warp 同步;
4. 若仍漂移,回到双端日志对比(`本地自转角` / `发送自转角` / `时间` / `Δ`)继续定位。
