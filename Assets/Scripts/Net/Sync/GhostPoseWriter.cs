using System;
using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts.Craft.FlightData;
using Assets.Scripts.Flight;
using Assets.Scripts.Flight.Sim;
using ModApi;
using ModApi.Craft;
using ModApi.Craft.Parts;
using ModApi.Flight.GameView;
using ModApi.Flight.Sim;
using ModApi.State;
using UnityEngine;
using Assets.Scripts.Net.CraftVisual;

namespace Assets.Scripts.Net.Sync
{
	/// <summary>
	/// 幽灵位姿写回(2026-09-22 重构:自 MpNetworkManager 静态方法逐字搬来)。
	/// 把状态包写进 CraftNode:GroundedSurface*(反射)/SetStateVectors/帧空间朝向/逻辑 comRot 基准的 body 摆放/FlightData 刷新。
	/// 坐标系公式注释是全项目最贵的知识,一字未动。
	/// </summary>
	internal static class GhostPoseWriter
	{
		internal static void ForceRemoteHeading(RemoteCraft rc, Mod.RemoteDataPack data)
		{
			if (rc.Node == null || rc.Node.CraftScript == null || rc.Node.Parent == null) return;
			// 与 ApplyRemoteState 一致:Transform.rotation 是帧空间,
			// 用 frame.PlanetToFrameRotation(行星自转 × SrfRel) 转回帧空间。
			IReferenceFrame frame = rc.Node.GameView != null ? rc.Node.GameView.ReferenceFrame : null;
			if (frame == null && FlightSceneScript.Instance != null && FlightSceneScript.Instance.ViewManager != null &&
				FlightSceneScript.Instance.ViewManager.GameView != null)
			{
				frame = FlightSceneScript.Instance.ViewManager.GameView.ReferenceFrame;
			}
			Quaternion headingFrame;
			if (frame != null)
			{
				headingFrame = frame.PlanetToFrameRotation(rc.Node.Parent.Rotation * data.SrfRel);
			}
			else
			{
				double aRecv = rc.Node.Parent.RotationAngle;
				headingFrame = Quaternion.AngleAxis((float)(aRecv * Mathf.Rad2Deg), Vector3.up) * data.SrfRel.ToQuaternion();
			}
			rc.Node.CraftScript.Transform.rotation = headingFrame;
			if (rc.Node.CraftScript.CenterOfMass != null)
			{
				rc.Node.CraftScript.CenterOfMass.rotation = headingFrame;
			}
			// 抽搐诊断(LateUpdate 路径):记录 LateUpdate 冻结的 comRot 位置(与 Update 路径冻结值对比,
			// 若 DiagComLinkM>0 说明写 body 连带移动 comRot,Update 与 LateUpdate 两次写入基准不同 → 帧内抖动)。
			// 同时记录 body[0] 在 LateUpdate 重写前后的位置差(DiagBody0DeltaLateM = 双写不一致幅度)。
			Vector3 b0LateBefore = Vector3.zero;
			bool hasB0 = false;
			try
			{
				IReadOnlyList<BodyData> lb = rc.Node.CraftScript.Data.Assembly.Bodies;
				if (lb != null && lb.Count > 0 && lb[0].BodyScript != null && lb[0].BodyScript.Transform != null)
				{
					b0LateBefore = lb[0].BodyScript.Transform.position;
					hasB0 = true;
				}
			}
			catch { }
			if (rc.Node.CraftScript.CenterOfMass != null) rc.DiagComPosLate = rc.Node.CraftScript.CenterOfMass.position;
			// 用"逻辑 comRot 位姿"(状态包导出)作 body 摆放基准,不再读接收端实时 comRot:
			// 实时 comRot 是根 body 内 ~3cm 偏移的后代,以它为基准每帧与游戏放置差固定 3cm → 抽搐(§〇之四)。
			Vector3 logicalComPos; Quaternion logicalComRot;
			TryGetLogicalComPose(rc, data, frame, out logicalComPos, out logicalComRot);
			ApplyRemoteBodyPoses(rc, data, logicalComPos, logicalComRot);
			if (hasB0)
			{
				Vector3 b0LateAfter = Vector3.zero;
				try
				{
					IReadOnlyList<BodyData> lb = rc.Node.CraftScript.Data.Assembly.Bodies;
					if (lb != null && lb.Count > 0 && lb[0].BodyScript != null && lb[0].BodyScript.Transform != null)
						b0LateAfter = lb[0].BodyScript.Transform.position;
				}
				catch { }
				rc.DiagBody0DeltaLateM = Vector3.Distance(b0LateAfter, b0LateBefore);
			}
		}

		/// <summary>
		/// 应用远程飞船的每 body 姿态:旋转(相对 comRot,既有 BodyRotations)+ 位置(相对 comRot,body-sync P0 BodyPositions)。
		/// 位置用"绝对写" body.Transform.position = comRot.TransformPoint(relPos),解决转轴/关节连接的
		/// 子装配"整体移动"(摆动主要是位置变化,枢轴不在 comRot,旋转同步覆盖不了)。
		/// 两列表(BodyRotations/BodyPositions)同长度同索引(发送端同循环采样),此处各自取 Mathf.Min 兜底。
		/// 见 plans/body-sync.md。
		/// 1.4.2 适配(BodyScript.MoveToCraft → SetParent(Game.InFlightScene ? null : craft, true)):
		/// 飞行中 body 脱离 craft 层级(parent=null),localRotation 不再是"相对根"而是世界旋转。
		/// 若仍写 localRotation=relCom(相对 comRot 的旋转),body 世界旋转会丢失 comRot 因子 → 整体转错。
		/// 故改为显式写世界旋转 = comRot.rotation × relCom(comRot 缺省时退化为根旋转,兼容旧层级);
		/// 1.4.102 下 comRot.rotation=根旋转=headingFrame,世界写与旧 localRotation 结果一致,双版本均正确。
		/// 另:comRot 是 RootPart.Transform 的后代(:1562),循环内写 body 位置会连带移动 comRot,
		/// 若每轮重读 comRot 会引入"上一 body 位移"的循环依赖漂移;故先冻结 comRot 位姿一次。
		/// 2026-09 修复(§〇之四反馈环):基准不再读接收端 comRot 的**实时** Transform,而是用
		/// <see cref="TryGetLogicalComPose"/> 从状态包直接导出的"逻辑 comRot 位姿"。
		/// 原因:游戏接地放置(GroundedSurfacePosition)会把根 body 放到包内位置,而接收端 comRot
		/// 是根 body 内偏移 ~3cm 的后代 —— 若以实时 comRot 为基准写 body[0],每帧与游戏放置
		/// 差固定 ~3cm,往复摆动(实测 comLink≈b0dLate≈0.0299m,静止也如此 = 可见抽搐)。
		/// 逻辑位姿与游戏放置基准一致 → 两次写入(Update/LateUpdate)与游戏三方一致,反馈环消失。
		/// </summary>
		internal static void ApplyRemoteBodyPoses(RemoteCraft rc, Mod.RemoteDataPack data, Vector3 comRotPos, Quaternion comRotRot)
		{
			if (data.BodyRotations == null || data.BodyRotations.Count == 0) return;
			IReadOnlyList<BodyData> bodies = rc.Node.CraftScript.Data.Assembly.Bodies;
			if (bodies == null) return;
			int n = Mathf.Min(bodies.Count, data.BodyRotations.Count);
			for (int i = 0; i < n; i++)
			{
				if (bodies[i].BodyScript != null && bodies[i].BodyScript.Transform != null)
				{
					Transform t = bodies[i].BodyScript.Transform;
					// 世界旋转 = 逻辑 comRot 旋转 × (相对 comRot 的旋转 relCom),双版本均正确。
					t.rotation = comRotRot * Quaternion.Euler(data.BodyRotations[i]);
					if (data.BodyPositions != null && i < data.BodyPositions.Count)
					{
						if (i == 0)
						{
							// 根 body(body[0])按"comPos − G"写出,与游戏的 comRot 锚定一致:
							// 游戏 RecalculateFrameState 每帧把 comRot 锚到 craft.Position(= 逻辑 comPos;
							// CraftScript.FramePosition 的 getter 就是 CenterOfMass.position,CraftScript.cs:400)。
							// body[0] 是 comRot 的父级 → 游戏会把 body[0] 放到 comPos − G
							// (G = comRot 相对 body[0] 的几何偏移,每船不同:实测 P1=0.1271m、P0=0.0850m)。
							// 若写 comPos,对抗 = |G|;若写 comPos+rot×rel0,对抗 = |W+G|(P1 恰 W=−G 时为 0)。
							// 按 comPos − G 写出 → 与游戏锚定完全一致 → 零对抗,无每帧 8~13cm 往复。
							Transform comRot = rc.Node.CraftScript.CenterOfMass;
							Vector3 gVec = Vector3.zero;
							if (comRot != null) gVec = comRot.position - t.position; // 写前读取当前几何
							t.position = comRotPos - gVec;
						}
						else
						{
							// 逻辑位姿版 comRot.TransformPoint(relPos) = comRotPos + comRotRot × relPos(scale=1)。
							t.position = comRotPos + comRotRot * data.BodyPositions[i];
						}
					}
				}
			}
		}

		/// <summary>
		/// 由状态包直接导出"逻辑 comRot 位姿"(帧空间):位置 = 包内 Position(地表坐标)转帧空间,
		/// 旋转 = 行星当前自转 × SrfRel 转帧空间(与 ApplyRemoteState/ForceRemoteHeading 的朝向公式一致)。
		/// 不读取接收端 comRot 的实时 Transform —— 详见 <see cref="ApplyRemoteBodyPoses"/> 注释(反馈环修复)。
		/// </summary>
		internal static bool TryGetLogicalComPose(RemoteCraft rc, Mod.RemoteDataPack data, IReferenceFrame frame,
			out Vector3 logicalComPos, out Quaternion logicalComRot)
		{
			logicalComPos = Vector3.zero;
			logicalComRot = Quaternion.identity;
			if (rc.Node == null || rc.Node.Parent == null) return false;
			IPlanetNode planet = rc.Node.Parent;
			Vector3d planetPos = planet.SurfaceVectorToPlanetVector(data.Position);
			if (frame != null)
			{
				logicalComRot = frame.PlanetToFrameRotation(planet.Rotation * data.SrfRel);
				logicalComPos = frame.PlanetToFramePosition(planetPos);
			}
			else
			{
				// 帧未就绪回退:近似(帧角≈行星角时成立)
				logicalComRot = Quaternion.AngleAxis((float)(planet.RotationAngle * Mathf.Rad2Deg), Vector3.up) * data.SrfRel.ToQuaternion();
				logicalComPos = (Vector3)planetPos;
			}
			return true;
		}

		/// <summary>
		/// 统一应用远程飞船状态（坐标系自洽，与采样端一一对应）：
		/// ① GroundedSurface*：让游戏"表面锁定+物理禁用"分支跟随远程状态，避免被拉回/坠落；
		/// ② 位置/速度：地面坐标 → 行星空间 SetStateVectors；
		/// ③ 视觉朝向：帧空间"质心旋转"直接赋给根 Transform（XML 是质心坐标系，根=质心 part 才正确），
		///    body 用相对质心的旋转 relCom 显式写"世界旋转 = comRot.rotation × relCom"
		///    （1.4.2 飞行中 body 脱离 craft 层级 parent=null，localRotation 即世界旋转，
		///     直接写 localRotation=relCom 会丢 comRot 因子导致整体转错；见 ApplyRemoteBodyPoses）；
		/// ④ 刷新 FrameState：让 Transform.position 跟随逻辑位置。
		/// </summary>
		internal static void ApplyRemoteState(RemoteCraft rc, Mod.RemoteDataPack data)
		{
			if (rc.Node == null || rc.Node.CraftScript == null) return;
			// 防御:确保远程飞船保持"物理禁用"。游戏可能在 GameView 加载/切换或初始化阶段
			// 重新启用物理;一旦物理启用,朝向会被物理与 RecenterTransformOnCoM 覆盖,
			// 导致 transformRot/comRot 偏离状态包(表现为接收端飞船朝向突变/错误)。
			// 用 Warp 原因禁用物理,避免触发 MapCraft→MapStaticOrbitItem 切换导致的 MapView NRE(见 InitializeRemoteCraft)。
			if (rc.Node.CraftScript.IsPhysicsEnabled)
			{
				rc.Node.SetPhysicsEnabled(false, PhysicsChangeReason.Warp);
			}
			IPlanetNode planet = rc.Node.Parent;
			if (planet == null) return;

			ApplyRemoteGroundedSurface(rc, data, planet);

			Vector3d planetPos = planet.SurfaceVectorToPlanetVector(data.Position);
			// 发送端 data.Velocity 是"地表相对速度"(见 TrySampleLocalCraft)。SurfaceVectorToPlanetVector 是
			// 纯逆旋转(不减自转项),直接转会得到 V_inertial−ω×r(静止船=-158.85m/s),一旦地表锁定被清
			// 游戏会按此速度推进轨道 → 幽灵漂移/掉地。必须加回行星自转线速度恢复正确的惯性速度。
			Vector3d planetVel = planet.SurfaceVectorToPlanetVector(data.Velocity) +
				planet.SurfaceVectorToPlanetVector(planet.CalculateSurfaceVelocity(data.Position));
			CraftUtils.SetStateVectorsAtDefaultTime(planetPos, planetVel, rc.Node);

			// ③ 视觉朝向(LunaMultiplayer 方案)：根=质心旋转；body=相对质心的局部旋转。
			// 发送端传 SrfRel(相对行星地表朝向)；接收端世界旋转 = 接收端行星当前自转 × SrfRel,
			// 保证"相对各自行星地表"朝向一致,不依赖双端自转/时间同步、无 warp 漂移。
			// 关键:游戏朝向权威来源是 CraftScript.FrameHeading = CenterOfMass.rotation
			// (CraftFlightData.Pitch/BankAngle、导航、相机等都读它),故必须同步 CenterOfMass.rotation。
			// frame 仅用于 ④ RecalculateFrameState(位置)与 ⑤ FlightData 刷新。
			IReferenceFrame frame = rc.Node.GameView != null ? rc.Node.GameView.ReferenceFrame : null;
			if (frame == null && FlightSceneScript.Instance != null && FlightSceneScript.Instance.ViewManager != null &&
				FlightSceneScript.Instance.ViewManager.GameView != null)
			{
				frame = FlightSceneScript.Instance.ViewManager.GameView.ReferenceFrame;
			}
			// 视觉朝向(LunaMultiplayer 方案):世界旋转 = 接收端行星当前自转 × 发送端相对地表朝向(SrfRel)。
			// 保证"相对各自行星地表"朝向一致,不依赖双端自转/时间同步,无 warp 漂移、无全局副作用。
			// 关键:Transform.rotation 是帧空间,须用 frame.PlanetToFrameRotation 把
			// "行星空间 = 行星当前自转 × SrfRel" 转回帧空间(即 RotateY(θ_planet - θ_frame) * SrfRel)。
			// 因双端表面锁定帧 θ_frame - θ_planet 为同一常量,两端帧空间朝向一致。
			Quaternion headingFrame;
			if (frame != null)
			{
				headingFrame = frame.PlanetToFrameRotation(planet.Rotation * data.SrfRel);
			}
			else
			{
				// 帧未就绪回退:直接乘行星自转(近似,帧角≈行星角时成立)
				headingFrame = Quaternion.AngleAxis((float)(planet.RotationAngle * Mathf.Rad2Deg), Vector3.up) * data.SrfRel.ToQuaternion();
			}
			// 烟雾同步:注入速度+角速度到幽灵 kinematic 刚体(须在写 rc.LastAppliedHeading 之前,以读到上一次朝向)
			EngineVisualSync.InjectGhostMotion(rc, data, planet, frame, headingFrame);
			rc.Node.CraftScript.Transform.rotation = headingFrame;
			if (rc.Node.CraftScript.CenterOfMass != null)
			{
				rc.Node.CraftScript.CenterOfMass.rotation = headingFrame;
			}
			rc.LastAppliedHeading = headingFrame; // 记录本次写入值(诊断:对比 transformRot 判断是否被覆盖)
			// 抽搐诊断(Update 路径):冻结 comRot 位姿前,记录跨帧漂移与冻结基准。
			// 1.4.2 comRot 是 RootPart(根 body 内)的后代,上一帧写 body 会连带移动 comRot,
			// 若本帧冻结值相对上帧已漂移(DiagComCrossFrameM>0),说明"写 body→连带移动 comRot→
			// 下帧冻结基准漂移→body 再写"形成反馈环,即静止抽搐的根源。
			Transform comRotDiag = rc.Node.CraftScript.CenterOfMass;
			if (comRotDiag != null)
			{
				rc.DiagComPosFrozen = comRotDiag.position;
				rc.DiagComCrossFrameM = Vector3.Distance(rc.DiagComPosFrozen, rc.DiagComPosPrevFrozen);
				rc.DiagComPosPrevFrozen = rc.DiagComPosFrozen;
			}
			// 用"逻辑 comRot 位姿"(状态包导出)作 body 摆放基准,不再读接收端实时 comRot(反馈环修复,§〇之四)。
			Vector3 logicalComPos; Quaternion logicalComRot;
			TryGetLogicalComPose(rc, data, frame, out logicalComPos, out logicalComRot);
			ApplyRemoteBodyPoses(rc, data, logicalComPos, logicalComRot);
			// 抽搐诊断:写完全部 body 后 comRot 的连带位移(写 body 前后 comRot 位置差)。
			// comRot 在根 body 内,写根 body 位置必然连带移动 comRot;该值即每帧"基准污染"量。
			if (comRotDiag != null)
			{
				rc.DiagComPosAfterBodies = comRotDiag.position;
				rc.DiagComLinkM = Vector3.Distance(rc.DiagComPosAfterBodies, rc.DiagComPosFrozen);
			}
			// 抽搐诊断:body[0] 逐帧世界位移(渲染层抽搐幅度;下一帧 Update 再对比,得到跨帧位移)。
			{
				IReadOnlyList<BodyData> diagBodies = rc.Node.CraftScript.Data.Assembly.Bodies;
				if (diagBodies != null && diagBodies.Count > 0 && diagBodies[0].BodyScript != null && diagBodies[0].BodyScript.Transform != null)
				{
					Vector3 b0w = diagBodies[0].BodyScript.Transform.position;
					rc.DiagBody0DeltaM = rc.DiagHasBody0Prev ? Vector3.Distance(b0w, rc.DiagBody0PrevWorld) : 0f;
					rc.DiagBody0PrevWorld = b0w;
					rc.DiagHasBody0Prev = true;
				}
			}

			// ④ 刷新帧状态（Transform.position 跟随逻辑位置）
			if (frame != null)
			{
				CraftUtils.RecalculateFrameState(frame, rc.Node);
			}

			// ⑤ 手动刷新对方飞船 FlightData 的缓存字段(PositionNormalized/CraftForward),
			// 使 FlightData.Pitch/BankAngle(游戏 UI/Vizzy 读取)跟随同步后的 CenterOfMass。
			// 注(2026-08 反编译复查):幽灵的引擎/部件 modifier 实际仍收 IFlightUpdate/IFlightFixedUpdate
			// (MonoBehaviourBase.OnEnable 注册只看 enabled,无物理过滤;CraftScript.EnablePhysics(false) 不禁用 MonoBehaviour)。
			// FlightData 仍可能因"游戏 FlightUpdate 读 CenterOfMass 先于本帧状态写入"而滞后,
			// 故此反射立即刷新保留(参见 plans/archive/engine-fx-sync-feasibility.md §3.5)。
			if (frame != null && rc.Node.CraftScript.CenterOfMass != null)
			{
				try
				{
					ICraftFlightData rfd = rc.Node.CraftScript.FlightData;
					if (rfd != null)
					{
						_flightPositionNormalizedProp?.SetValue(rfd, rc.Node.Position.normalized);
						Vector3d expCraftFwd = frame.FrameToPlanetVector(rc.Node.CraftScript.CenterOfMass.forward).normalized;
						_flightCraftForwardProp?.SetValue(rfd, expCraftFwd);
						// 诊断:确认反射属性是否取到、写后值(仅首次输出)
						if (!_flightDiagLogged)
						{
							_flightDiagLogged = true;
							Mod.Log("MP FlightData 刷新诊断: posNormProp=" + (_flightPositionNormalizedProp != null) +
								" fwdProp=" + (_flightCraftForwardProp != null) +
								" | 写后CraftForward=(" + rfd.CraftForward.x.ToString("F3") + "," + rfd.CraftForward.y.ToString("F3") + "," + rfd.CraftForward.z.ToString("F3") + ")" +
								" 期望=(" + expCraftFwd.x.ToString("F3") + "," + expCraftFwd.y.ToString("F3") + "," + expCraftFwd.z.ToString("F3") + ")");
						}
					}
				}
				catch (Exception e) { Mod.LogError("Refresh remote FlightData error (P" + rc.PlayerId + "): " + e.Message); }
			}

			// 记录最近一次实际应用的状态（供 LateUpdate 渲染前写回朝向复用，
			// 保证写回的是"插值后"状态而非"最新包"，避免朝向跳变）。
			rc.LastApplied = data;
			rc.HasApplied = true;
			// 变换漂移诊断:记录本帧写入后的实际 Transform.position,供下一帧写入前对比。
			if (rc.Node.CraftScript != null && rc.Node.CraftScript.Transform != null)
			{
				rc.LastWrittenFramePos = rc.Node.CraftScript.Transform.position;
			}

			// 引擎尾焰:快照最近应用状态的每引擎视觉 throttle(液体 override 闭包与航发驱动都读它)
			if (data.EngineThrottles != null) rc.SyncedThrottles = data.EngineThrottles;

			// 部件开关/展开状态(方案 B + P3):变沿 + 白名单应用(起落架/货舱/腿/太阳能/灯/SubPartRotator + 输入驱动部件),
			// 其余部件只记录不处理(引擎→EngineVisualSync;分离器/整流罩/对接→body 同步;伞→专用驱动 P2;InputBasedActivator→不触发)
			PartVisualSync.ApplyRemotePartActivated(rc, data);

			// 控制输入应用(P3):把同步的 Pitch/Yaw/Roll/Brake/Throttle/Slider1-4/Translate*/激活组写进幽灵活动舱 Controls,
			// 驱动输入驱动部件(舵面/Rotator/活塞/螺旋桨/RCS/gimbal 等)的远程姿态(机制见 plans §11)
			ControlVisualSync.ApplyRemoteControls(rc, data);
		}

		private static readonly PropertyInfo _groundedSurfacePositionProp =
			typeof(CraftNode).GetProperty("GroundedSurfacePosition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		private static readonly PropertyInfo _groundedSurfaceVelocityProp =
			typeof(CraftNode).GetProperty("GroundedSurfaceVelocity", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		private static readonly PropertyInfo _groundedSurfaceRotationProp =
			typeof(CraftNode).GetProperty("GroundedSurfaceRotation", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		// 幽灵的 FlightData 仍可能因执行序(游戏 FlightUpdate 先于本帧状态写入)滞后,
		// 用反射写 private set 立即刷新,使 FlightData.Pitch/BankAngle 跟随同步后的 CenterOfMass。
		private static readonly PropertyInfo _flightPositionNormalizedProp =
			typeof(CraftFlightData).GetProperty("PositionNormalized", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		private static readonly PropertyInfo _flightCraftForwardProp =
			typeof(CraftFlightData).GetProperty("CraftForward", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		private static bool _flightDiagLogged; // FlightData 反射刷新诊断(仅首次输出)

		/// <summary>
		/// 更新幽灵飞船的 GroundedSurface*（private set，用反射写入）。
		/// 物理禁用 + InContactWithPlanet 的飞船，游戏每帧会按这些值放置飞船（见 CraftNode.Update），
		/// 所以必须让它们跟随远程状态，否则会被拉回出生点（位置卡住/不更新）。
		/// </summary>
		internal static void ApplyRemoteGroundedSurface(RemoteCraft rc, Mod.RemoteDataPack data, IPlanetNode planet)
		{
			try
			{
				// 与游戏 UpdateSurfaceParameters 的公式一致：
				//   GroundedSurfaceRotation = Parent.RotationInverse * Heading
				//   GroundedSurfacePosition/Velocity = 地面坐标
				// data.Heading 已是"行星空间"朝向(发送端采样时已 FrameToPlanet)，直接使用，
				// 否则 CraftNode.Heading(行星字段) 会存帧空间值，导致游戏导航/相机/地图等逻辑朝向错误。
				Quaterniond planetHeading = data.Heading;
				if (_groundedSurfacePositionProp != null) _groundedSurfacePositionProp.SetValue(rc.Node, (Vector3d?)data.Position);
				if (_groundedSurfaceVelocityProp != null) _groundedSurfaceVelocityProp.SetValue(rc.Node, (Vector3d?)data.Velocity);
				if (_groundedSurfaceRotationProp != null) _groundedSurfaceRotationProp.SetValue(rc.Node, (Quaterniond?)(planet.RotationInverse * planetHeading));
			}
			catch (Exception e)
			{
				Mod.LogError("ApplyRemoteGroundedSurface error (player " + rc.PlayerId + "): " + e.Message);
			}
		}
	}
}
