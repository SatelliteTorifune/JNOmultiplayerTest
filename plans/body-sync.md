# Body 级姿态同步方案(body-sync)

> 状态:✅ 方案已定(**BodyPoses**,经 SP2 参考验证);**P0 已实现(2026-08-18,编译 0 错误,待游戏内实测)**;与 multi-craft-sync 分离为独立 plan
> 关联:[`part-switch-sync.md`](part-switch-sync.md)(部件开关 + P3 控制输入,转轴的输入驱动在此);[`multi-craft-sync.md`](multi-craft-sync.md)(多船生命周期,分离/对接后 body 数量变化归那边)

---

## 0. 定位(与 multi-craft 的区别)

- **body 同步** = **同一艘船内部**,各 body 之间的相对姿态;重点是"转轴/关节连接的子装配随转轴**整体移动**(绕铰链摆动)"。还有残骸小碎片、活塞/悬架等一切 body 级位移。
- **multi-craft 同步** = **多艘船之间**的身份/生命周期/对接/分离/残骸/切换。见 [`multi-craft-sync.md`](multi-craft-sync.md)。
- **两者的接口**:分离/对接会导致 body 数量与顺序变化 → 由 multi-craft 的生命周期对账(MC1/MC3)解决;本 plan 的"索引契约"依赖它成立。

---

## 1. 问题:转轴/关节连接部件的"整体移动"

> 用户聚焦点:转轴(JointRotator/Rotator)上连接的部件随转轴**整体移动**(绕铰链摆动)。当前 mod 只同步了每 body 的**旋转**,没同步**位置** → 幽灵上被连部件停在 XML 设计位、与发送端姿态不符。

**1.1 根因(反编译证实)**
- `JointRotatorScript` 复用一个**既有 ConfigurableJoint**([`SetupJoint`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/JointRotatorScript.cs:356)):`_joint = bodyJoint.GetJointForAttachPoint(attachPoint)`,`_connectedRigidBody = _joint.connectedBody`(**独立 body**);
- `FlightFixedUpdate` 把 `Data.Angle` 移向 `ComputeTargetAngle()`(读输入,即 P3 已同步的 Controls)并设 `_joint.targetRotation = Quaternion.Euler(-Angle, 0, 0)`([`:86-117`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/JointRotatorScript.cs:86))→ **物理驱动被连 body 绕铰链旋转**,其 `Transform` 跟随移动(Rigidbody 带 transform)→ 被连部件整体摆动;
- **幽灵**:所有 body `isKinematic=true` + 物理禁用 → ConfigurableJoint targetRotation **无物理效果**,被连 body 停 XML 位;但 `JointRotatorScript._visualMesh.localRotation = Euler(0,-Angle,0)`(纯 Transform,[`:149`](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/Parts/Modifiers/JointRotatorScript.cs:149))仍跑,且 P3 后 `Data.Angle` 已按同步输入本地演进 → **铰链视觉转、被连 body 不跟**。

**1.2 现状缺口(代码核实)**
- `BodyRotations` 采样 `relCom = Inverse(comRot) * body.Transform.rotation`([MpNetworkManager.cs:1961](../Assets/Scripts/Net/MpNetworkManager.cs:1961)),接收端只写 `body.Transform.localRotation = euler`([:472/1738](../Assets/Scripts/Net/MpNetworkManager.cs:1738))——**只转不位移**;
- 被连 body 摆动以**位置变化**为主(绕铰链弧线,枢轴不在 comRot)→ 正是 BodyRotations 没覆盖的部分。

**1.3 层级与不冲突前提(已证实)**
- `body.Transform.SetParent(craftScript.Transform)`([BodyScript.cs:531](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Craft/BodyScript.cs:531));`CenterOfMass`(comRot)是根的子节点(`localPosition = localCenterOfMass`,[CraftNode.cs:878](../C:/renko/shitProgram/jnoCode/SimpleRockets2/Assets/Scripts/Flight/Sim/CraftNode.cs:878));mod 每帧把根与 comRot 都转成 headingFrame → 现有 `localRotation` 写法 = 相对 comRot 旋转;
- mod 的 [`RecalculateFrameState`](../Assets/Scripts/CraftUtils.cs:29):每 body `transform.position += positionDelta`(**整体平移,相对姿态不变**),kinematic 只平移;`RecenterTransformOnCoM` 仅物理开启才跑 → 与直接写 body 位置不冲突(顺序:先 ApplyRemoteState 写姿态,后 RecalculateFrameState 平移修正)。

---

## 2. 方案:`BodyRotations` → `BodyPoses`(每 body 相对 comRot 的位置+旋转)

- **采样**(发送端,按 `Data.Assembly.Bodies` 顺序,与 BodyRotations 同契约):
  - `relPos = comRot.InverseTransformPoint(body.Transform.position)`;
  - `relRot = Inverse(comRot.rotation) * body.Transform.rotation`;
- **应用**(接收端,ApplyRemoteState 与 ForceRemoteHeading 两处同步改):
  - `body.Transform.position = comRot.TransformPoint(relPos)`(绝对写最稳,不依赖根与 comRot 原点重合);
  - `body.Transform.rotation = comRot.rotation * relRot`(根与 comRot 同朝向,`localRotation` 写法亦可保留);
- **带宽**:每 body ~6 float(位置 3 + 旋转 euler 3)≈ 24B;10 body craft @20Hz ≈ 4.8KB/s,与现有 BodyRotations 同量级,可接受(与 SP2 不同,我们整船全发、不按变化子集);
- **通用性**:一并覆盖多 craft 原 §7.7.5 的"IsDebris 小碎片只同步旋转"缺口,以及活塞/悬架等一切 body 级位移;
- **数据路径**:走现有插值缓冲(interp=最新包语义,与 BodyRotations 一致)→ 持续每包应用、自带自愈。

**实现形态(2026-08-18)**:`RemoteDataPack` 在既有 `BodyRotations`(List\<Vector3> euler)旁**新增平行列表 `BodyPositions`**(List\<Vector3>,相对 comRot),两列表同长度、同索引;`MpMessage` 序列化追加;采样/应用两处同步改。不动现有 BodyRotations 语义(最小 diff、低风险)。

---

## 3. SP2 参考:BodySyncData + PartSyncData(可抄性结论)

> 反编译源:`C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/`。结论:**SP2 恰好验证了本方案方向正确**,且提供几个可抄的优化。

**SP2 的做法**
1. **Body 级位姿同步是"转轴/关节连接部件整体移动"的同步层次**——SP2 **不**按转轴部件同步,而是同步每个 body 相对**父 body** 的位姿:
   - [`BodySyncData.Update`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/SyncData/BodySyncData.cs:89):`Rotation = Inverse(ParentBody.rotation)*body.rotation`;`Position = ParentBody.InverseTransformPoint(body.position)*ParentBody.lossyScale.x`;根 body(ParentBody==null)用绝对位姿;附 `AngularVelocity/Velocity`;
   - **父层级**由 SP2 独有的 [`BodyConfigurationState.SetParentBody`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/BodyConfigurationState.cs:714) 维护(`ParentBody.Id` 序列化);
2. **Delta 兴趣优先级 + top-N 子集**([`CraftStateSerializer.SerializeWrite`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/CraftStateSerializer.cs:149)):根 body 与子 body 各自 `Delta > 0.1f` 才入列,按 Delta 降序**每包只发 top-5**;接收端按 **`body.Id`** 查找([`:115`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/CraftStateSerializer.cs:115),`AircraftScript.GetBody(id)`),**不是顺序索引**;
3. **接收端应用**([`BodyScript.cs:660-679`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Craft/BodyScript.cs:660)):子 body 写 `transform.localPosition/localRotation`(相对父 body),带 **Lerp/Slerp 平滑**(`10f*Time.deltaTime`,偏差 <0.01 直接快照);根 body 写 RigidBody + velocity(SP2 远程船**物理保持开启**,靠 lerp 收敛);
4. **per-part 连续状态**:[`PartSyncData`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/SyncData/PartSyncData.cs) + [`ISyncValue`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/SyncData/ISyncValue.cs)(`SyncFloat/SyncVector/SyncBool`,[`SyncValue.cs:31-36`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Multiplayer/SyncData/SyncValue.cs:31) 的 `Value`(采样 Func)/`ValueRead`(应用 Action)/`LastValueSent`+`Delta`),按注册序序列化、每包 top-5 by delta;用于**非 body 连续状态**:轮子 RPM([`ResizableWheelScript.cs:231`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Craft/Parts/Modifiers/ResizableWheelScript.cs:231))、翼涡流数据、炮口闪光([`CannonScript.cs:451`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Craft/Parts/Modifiers/Weapons/CannonScript.cs:451))、发动机 throttle、螺旋桨桨距([`PropellerAssemblyScript.cs:435`](../C:/renko/shitProgram/反编译的/sp2/Game/Assets/Scripts/Craft/Parts/Modifiers/Propulsion/Propeller/PropellerAssemblyScript.cs:435));
5. **引擎集成**:SP2 自带的 Multiplayer 同步系统——`PartModifierScript.InitializePartSyncData`(虚拟方法)+ `PartScript.SyncData` + `PartData.cs:414` 自动建 `PartSyncData` 并回调每个 modifier 注册自己的值。**SP2 的 JointRotator 不注册 per-part 值**(`JointRotatorScript` 无 RegisterValue)→ 关节摆动全靠 BodySyncData。

**可抄清单(适配 SR2)**
- ✅ **BodyPoses 加 position 被 SP2 验证为正确层次**;接收端应用方式照抄:相对参考系写 `Transform.localPosition/localRotation`,可加 Lerp/Slerp 平滑(幽灵 kinematic 直接写更稳,平滑可选);
- ✅ **Quaternion32 压缩**(`WriteQuaternion32`,~4B/四元数)替代 3×float euler(12B),省 2/3 带宽(接收端 `ReadQuaternion32`);
- ✅ **Delta 兴趣优先级 + 变化才发**(可选,P2):应对"1000 转轴"级规模;**前提是先引入 body/part 稳定 Id**(SP2 用 Id 查找,我们用顺序索引——做 delta 子集必须换 Id);
- ✅ **Per-part SyncValue 模式**(可选,P3):若需比"P3 输入+本地仿真"更精确的连续值(JointRotator `Data.Angle` 精确值、wheel 转向角、舵面偏转角),抄 `SyncFloat/SyncVector + Value/ValueRead` 注册模式;

**抄不了/需适配**
- ❌ **`InitializePartSyncData`/`PartScript.SyncData` 引擎钩子**:SP2 自带 Multiplayer 系统;SR2 的 `PartModifierScript` 无此接口 → 只能自建采样/应用(已在 PartVisualSync/ControlVisualSync 做);
- ❌ **`ParentBody` 树/相对父 body**:SR2 body 无父层级(BodyScript 无 ParentBody),用关节连接、无单一 parent → **相对 comRot 是本方案的 SR2 等价物**;后期可试从关节构建父关系(成本高、收益存疑);
- ❌ **FishNet Writer/Reader**:映射到自有 MpMessage 二进制即可;
- ❌ **物理开启的远程船 + RigidBody 平滑**:SP2 远程船物理开、lerp RigidBody + 写 velocity;我们的幽灵 kinematic + 直接写 Transform 是既定模式(更简单、已实测),不抄 SP2 的物理平滑。

---

## 4. 风险/待验证

- body 顺序契约:发送/接收 `Data.Assembly.Bodies` 同 XML 同序(现有 BodyRotations 已依赖);
- 发送端被连 body 确实经 Rigidbody 带动 Transform(物理开,是);
- 接收端写绝对位置后 `RecalculateFrameState` 的 `positionDelta` 叠加(量级小,实测复核);
- 无 pod/残骸 body 的 `BodyScript.Transform` 可能为 null → 采样/应用兜底(现有已有 null 检查);
- 分离/对接后 body 数量/顺序变化 → 归 multi-craft 生命周期(对账)统一处理,BodyPoses 与 BodyRotations 同契约;
- 铰链视觉(_visualMesh)与 body 姿态分别演进:输入已同步(P3),body 姿态被 BodyPoses 覆盖 → 二者趋势一致;极端误差时以 body 姿态为准。

---

## 5. 里程碑

- **P0 ✅(2026-08-18 已实现)**:BodyPoses = `BodyRotations` + `BodyPositions`(相对 comRot),采样 + 两处接收端(ApplyRemoteState/ForceRemoteHeading)应用;编译 0 错误。
- **P1(可选)**:Quaternion32 压缩(4B/四元数,省 2/3 带宽)。
- **P2(可选)**:Delta 兴趣优先级 + 变化才发 + body/part 稳定 Id(应对超大规模;需先引 Id)。
- **P3(可选)**:per-part SyncValue 模式,同步精确连续值(JointRotator `Data.Angle` / wheel 转向角 / 舵面偏转角)。

---

## 6. 实施记录

- **2026-08-18**:方案定稿(BodyPoses,SP2 验证);与 multi-craft-sync 分离为独立 plan;实现 P0(BodyPositions 平行列表,采样+两处应用)。
  - `Assets/Scripts/Mod.cs`:`RemoteDataPack` 新增 `List<Vector3> BodyPositions`(相对 comRot),构造器初始化;
  - `Assets/Scripts/Net/MpMessage.cs`:`WriteRecdata/ReadRecdata` 在 BodyRotations 后追加 BodyPositions(count + 3×float);
  - `Assets/Scripts/Net/MpNetworkManager.cs`:
    - 采样(`TrySampleLocalCraft`):与 BodyRotations 同循环,`data.BodyPositions.Add(comRotTransform.InverseTransformPoint(body.Transform.position))`(comRot Transform 采样,与接收端 `TransformPoint` 精确互逆);
    - 新增 `ApplyRemoteBodyPoses(rc, data, comRot)`:每 body 先写 `localRotation = Euler(BodyRotations[i])`,再写 `position = comRot.TransformPoint(BodyPositions[i])`(绝对写),各自 `Mathf.Min` 兜底;
    - 两处应用点替换为调用:`ApplyRemoteState`(③ 视觉朝向块)与 `ForceRemoteHeading`(LateUpdate 写回)。
  - 编译:`dotnet build aMptest.csproj -c Debug` **0 错误 0 警告**。
  - 待实测:双端联机验证转轴/关节连接的子装配随转轴整体摆动(位置跟随);残骸小碎片位置跟随;`RecalculateFrameState` positionDelta 叠加量级复核。
