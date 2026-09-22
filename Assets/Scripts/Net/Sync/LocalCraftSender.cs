using System;
using System.Collections.Generic;
using System.Xml.Linq;
using Assets.Scripts.Flight;
using Assets.Scripts.Flight.Sim;
using ModApi;
using ModApi.Craft;
using ModApi.Craft.Parts;
using ModApi.Flight.GameView;
using ModApi.Flight.Sim;
using UnityEngine;
using static Assets.Scripts.Net.Sync.MultiPlayerSyncUtil;
using Assets.Scripts.Net.CraftVisual;
using Assets.Scripts.Net.Session;

namespace Assets.Scripts.Net.Sync
{
	/// <summary>
	/// 发送端状态包管线(2026-09-22 重构:自 MultiPlayerNetworkManager 逐字搬来;同日二次整理:采样 + 发包节拍合并为一个类)。
	/// 采样:坐标系换算、body 位姿/角速度/线速度差分、加速度 EMA 与钳制、控制输入采集、发送端诊断缓冲。
	/// 节拍:状态包节流(F4/F5/F6b)、暂停降频、保活心跳、sendDiag 诊断行。
	/// </summary>
	internal class LocalCraftSender
	{
		/// <summary>
		/// 本机游戏暂停时的状态包发送间隔下限（ms，≈8Hz）。暂停中位置/速度都不变，
		/// 只需让对端知道"我还活着且处于暂停"，无需全速上报；恢复后立即回到 <see cref="SendIntervalMs"/>。
		/// </summary>
 const float PausedSendIntervalMs = 125f;

 float _sendTimer;
 float _keepAliveTimer;
		// --- 发送节奏诊断(2026-09-14,顿挫定位):实际发包间隔 EMA(sendGap,ms)。 ---
		// 与接收端 MultiPlayer gap(到达间隔)对账:sendGap 稳定≈50ms 而接收端 gap 大 → 网络突发(relay);
		// sendGap 本身大幅摆动 → 发送端自身突发(帧率不足/掉帧),先修发送端。
 float _lastSendTime = -1f;
 float _sendGapEmaMs = 0f;
 float _fpsEma = 0f;                 // 发送端渲染帧率 EMA(与对端 fps/gapEMA 对账:帧率-发包率耦合)

		private readonly NetworkManager _multiPlayer;

		internal LocalCraftSender(NetworkManager multiPlayer) { _multiPlayer = multiPlayer; }

		// --- 抽搐诊断(发送端):每包 body[0] 相对 comRot 采样位置抖动(静止时>0.01m → 发送端数据本身在抖) ---
		internal Vector3? _diagBody0Rel;
		internal float rcDiagBody0RelDelta;
		internal float _diagBody0RelLogTime;
		internal bool _diagBodyNamesLogged;       // 一次性 body id→部件名 dump 已输出(定位振荡部件)
		internal Vector3[] _diagPrevBodyRel;      // 上一 sendDiag 时刻的全部 body 相对 comRot 位姿(最大位移诊断)
		// 2026-09-19 body 索引稳定性:body 采样瞬时空时沿用上一帧已知位姿(保 BodyPositions 与装配同序同长)。
		internal Vector3[] _lastBodyPos;
		internal Vector3[] _lastBodyRot;
		internal Vector3[] _lastBodyAngVel;        // 2026-09-22 rotating-body-sync:body 局部系角速度占位(与 _lastBodyRot 平行)
		internal bool[] _hasLastBodyPose;
		internal float[] _diagBodyRbDeltas;       // 每 body |Transform.position − RigidBody.position|(帧空间,诊断振荡来源)
		internal float _lastBodySampleTime = -1f;  // rotating-body-sync:上一包 body 采样时刻(差分线速度用)
		// --- 2 阶外推(发送端):加速度/角速度 EMA 状态 + 原始采样诊断 + ω 符号自校验状态 ---
		internal Vector3 _accelEma; internal bool _hasAccelEma;
		internal Vector3 _angVelEma; internal bool _hasAngVelEma;
		internal Vector3 _accelRawDiag; internal Vector3 _angVelRawDiag;
		internal Quaternion? _diagPrevSrfRel;   // 上一 sendDiag 时刻的 SrfRel(ω 符号自校验)
		internal float _diagPrevSrfTime;
		// rotating-body-sync(2026-09-22):body ω 符号自校验。保存上一 sendDiag 时刻的 body 相对位姿,
		// 用"绕 ω 轴正/反向旋转 ω·dt"预测当前位置,与实际对比:误差小者 = 正确旋转方向。
		// 与朝向外推 errF+/errF- 同方法(proposal rotating-body-sync §三);叶片高速旋转时每包相位
		// 跳 90°+,预测位置误差直接反映外推方向是否正确。
		internal Vector3[] _diagPrevBodyPosRel;
		internal Vector3[] _diagPrevBodyRotRel;
		internal float _diagPrevBodyTime;

		internal static int GetLocalCraftNodeId()
		{
			try
			{
				if (FlightSceneScript.Instance != null && FlightSceneScript.Instance.CraftNode != null)
				{
					return FlightSceneScript.Instance.CraftNode.NodeId;
				}
			}
			catch { }
			return -1;
		}

		/// <summary>
		/// 取飞行状态中存储的本机飞船 craft XML（供联机交换）。
		/// 直接读取 FlightState 里已保存的飞船 XML，绕开 LoadCraftData()→LoadCraftImmediate()→CraftData 构造→GenerateXml()
		/// 这条容易在构造阶段触发空引用异常的链路。
		/// </summary>
		internal static string GetLocalCraftXml()
		{
			try
			{
				int nodeId = GetLocalCraftNodeId();
				if (nodeId < 0) return string.Empty;
				if (FlightSceneScript.Instance == null) return string.Empty;

				XElement xml = FlightSceneScript.Instance.FlightState.LoadCraftXml(nodeId);
				if (xml != null)
				{
					return xml.ToString(SaveOptions.DisableFormatting);
				}
				Mod.LogError("GetLocalCraftXml: FlightState.LoadCraftXml returned null for nodeId " + nodeId);
			}
			catch (Exception e)
			{
				Mod.LogError("GetLocalCraftXml (nodeId=" + GetLocalCraftNodeId() + "): " + e.GetType().Name + ": " + e.Message);
			}
			return string.Empty;
		}

		/// <summary>采样本机飞船状态（recdata 格式，地面坐标）。</summary>
		internal bool TrySampleLocalCraft(out Mod.RemoteDataPack data)
		{
			data = new Mod.RemoteDataPack();
			try
			{
				if (FlightSceneScript.Instance == null) return false;
				ICraftNode ic = FlightSceneScript.Instance.CraftNode;
				CraftNode craft = ic as CraftNode;
				if (craft == null || craft.CraftScript == null) return false;
				if (craft.Parent == null) return false;

				// 用地面坐标传输：PlanetVectorToSurfaceVector(craft.Position) 是网格固定坐标，
				// 跨端不变（craft.Position 是惯性坐标，随行星自转变化，不能直接传）。
				Vector3d pos = craft.Parent.PlanetVectorToSurfaceVector(craft.Position);
				// 速度必须换算成"地表相对速度"：PlanetVectorToSurfaceVector 只是纯旋转
				// (PlanetNode.cs:445 只 RotateVectorAroundYAxis,不减行星自转 ω×r 项)。
				// 直接用它转惯性速度 → 落地/静止船会得到恒定的行星自转线速度(本测试 158.85 m/s),
				// 接收端欠载外推 Position+Velocity*dt 把它放大成数十米瞬移(移动日志 move3s 高达 0.3~83m/3s 的元凶)。
				// 正确公式(与游戏 GroundedSurfaceVelocity/CraftNode.cs:1367 一致):
				//   地表相对速度 = 惯性速度转地表 − 该位置自转线速度(CalculateSurfaceVelocity)。
				Vector3d vel = craft.Parent.PlanetVectorToSurfaceVector(craft.Velocity) -
					craft.Parent.CalculateSurfaceVelocity(pos);
				// 朝向：传输"质心(CenterOfMass)的帧空间旋转"作为根朝向。
				// 依据（反编译+日志）：对方飞船 craft XML 的 body/part 是"质心坐标系"（GenerateXml 前
				// RecenterTransformOnCoM 把根移到质心），接收端根必须=质心(comRot)才能让 part 正确摆放
				// （实测用 comRot 时 rootPart 两端一致）。因此 heading 用 comRot 而非根 Transform。
				// 注意：CenterOfMass.rotation = commandPod.PilotSeatOrientation.rotation（座椅朝向），
				// 与根朝向可能差一个角度（实测约 17°）——这个偏差由 BodyRotations"相对质心"来消除。
				Quaterniond heading = craft.CraftScript.CenterOfMass != null
					? Quaterniond.FromQuaternion(craft.CraftScript.CenterOfMass.rotation)
					: Quaterniond.FromQuaternion(craft.CraftScript.Transform.rotation);
				// 朝向以"行星空间"传输(全局一致,不受两端 GameView 帧空间差异影响)：
				// 用本机飞船"逻辑参考系 craft.ReferenceFrame"做 帧→行星 转换,与接收端
				// rc.Node.ReferenceFrame 对称(反编译确认 CraftFlightData.Update 也用 craftNode.ReferenceFrame)。
				IReferenceFrame sendFrame = craft.ReferenceFrame;
				if (sendFrame == null && FlightSceneScript.Instance != null && FlightSceneScript.Instance.ViewManager != null &&
					FlightSceneScript.Instance.ViewManager.GameView != null)
				{
					sendFrame = FlightSceneScript.Instance.ViewManager.GameView.ReferenceFrame;
				}
				if (sendFrame != null)
				{
					heading = sendFrame.FrameToPlanetRotation(heading.ToQuaternion());
				}
				data = new Mod.RemoteDataPack(pos, vel, heading);

				// 通知接收端"本机游戏已暂停":暂停时位置/速度整体冻结,但 Velocity 仍是暂停前最后一刻的值。
				// 接收端若继续按速度外推(dead-reckoning),目标会在每个包到达时被拉回、包间又按速度前进
				// → 观察方看到"位置抽搐"(有速度时暂停尤其明显)。见 plans/latency-smoothing §9.7。
				data.Paused = FlightSceneScript.Instance.TimeManager != null &&
					FlightSceneScript.Instance.TimeManager.Paused;

				// 每引擎视觉 throttle(尾焰同步):按确定枚举顺序,与接收端一一对应
				data.EngineThrottles = EngineVisualSync.SampleEngineThrottles(craft);

				// 每部件开关/展开状态(方案 B):按 Data.Assembly.Parts 确定顺序,与接收端一一对应
				data.PartActivated = PartVisualSync.SamplePartActivated(craft);

				// 同步每个 body 的局部姿态。关键：BodyRotations 必须存"相对质心(comRot)"的旋转，
				// 因为接收端根=comRot（发送的 heading），且 XML 的 body/part 是质心坐标系。
				// 若采样"相对根"的 localRotation，而发送端根≠comRot（座椅朝向，实测差~17°），
				// 接收端按 comRot 摆放 body 时会整体转错 → "分裂/散架 + 朝向不一致"。
				Quaternion comRotUnity = craft.CraftScript.CenterOfMass != null
					? craft.CraftScript.CenterOfMass.rotation : craft.CraftScript.Transform.rotation;
				// body-sync P0 稳定采样基准(2026-09-19):不再用实时 comRot Transform 做 InverseTransformPoint。
				// 游戏 RecalculateCenterOfMass() 每帧把 comRot 锚到物理质心(Σ body.WorldCenterOfMass×mass,
				// CraftScript.cs:1343)—— 刚体振动时 comRot 相对节点位置 ±2~3m 摆动(实测静止暂停时
				// bodyMaxΔi 多索引轮流最大 = 共模摆动,而包 Position 稳定 pkΔ≈0)→ 用晃动基准采样会把晃动
				// 写进 BodyPositions → 接收端 ghost 部件绕稳定中心晃(bodyTgt≈5m、bodyDelta 0.6~0.9m = 卡顿)。
				// 改用包 Position 的帧空间对应点作稳定基准:接收端逻辑 comPos =
				// frame.PlanetToFramePosition(SurfaceVectorToPlanetVector(data.Position)) —— 与
				// sendFrame.PlanetToFramePosition(craft.Position) 同源互逆,采样基准与接收端摆放基准一致。
				Vector3 stableBodyAnchor;
				Quaternion invComRotUnity = Quaternion.Inverse(comRotUnity);
				if (sendFrame != null)
				{
					stableBodyAnchor = sendFrame.PlanetToFramePosition(craft.Position);
				}
				else
				{
					Transform comRotTransformFallback = craft.CraftScript.CenterOfMass != null
						? craft.CraftScript.CenterOfMass : craft.CraftScript.Transform;
					stableBodyAnchor = comRotTransformFallback.position;
				}
				// LunaMultiplayer 方案:传输"相对行星地表"朝向 SrfRel。
				// comRot 是帧空间;表面锁定帧 θ_frame = θ_planet + const(常量)。
				// 相对地表朝向 = RotateY(θ_frame - θ_planet) * comRot(与行星自转无关)。
				// 接收端用 RotateY(θ_planet_recv - θ_frame_recv) * SrfRel 渲染回帧空间,
				// 因双端同行星 const 相同 → 两端帧空间朝向一致,不依赖双端自转/时间同步、无 warp 漂移。
				double sendPlanetRot = craft.Parent.RotationAngle;
				double sendFrameRot = sendFrame != null ? sendFrame.RotationAngle : sendPlanetRot;
				Quaternion srfRelQ = Quaternion.AngleAxis((float)((sendFrameRot - sendPlanetRot) * Mathf.Rad2Deg), Vector3.up) * comRotUnity;
				data.SrfRel = Quaterniond.FromQuaternion(srfRelQ);
				IReadOnlyList<BodyData> bodyList = craft.CraftScript.Data.Assembly.Bodies;
				if (bodyList != null)
				{
					// 2026-09-19 索引稳定性:BodyScript/Transform 瞬时空(部件销毁重建/装配刷新)时**不跳过**,
					// 沿用上一帧已知位姿占位 —— 否则列表变短 → 接收端索引错位(实测 bodyMaxRelΔ 4~6m/1s)。
					// 同时采集 BodyData.Id,接收端按 id 重排到幽灵装配顺序(双保险,见 ReorderRemoteBodiesByGhost)。
					if (_lastBodyPos == null || _lastBodyPos.Length != bodyList.Count)
					{
						_lastBodyPos = new Vector3[bodyList.Count];
						_lastBodyRot = new Vector3[bodyList.Count];
						_lastBodyAngVel = new Vector3[bodyList.Count];
						_hasLastBodyPose = new bool[bodyList.Count];
						_diagBodyRbDeltas = new float[bodyList.Count];
					}
					for (int bi = 0; bi < bodyList.Count; bi++)
					{
						bool ok = bodyList[bi].BodyScript != null && bodyList[bi].BodyScript.Transform != null;
						Vector3 bPos;
						Vector3 bRotEuler;
						Vector3 bAngVelLocal = Vector3.zero; // rotating-body-sync:body 自身局部系角速度(弧度/秒)
						Vector3 bVelLocal = Vector3.zero;    // rotating-body-sync:body 相对 comRot 线速度(comRot 局部系,差分)
						if (ok)
						{
							// 振荡来源诊断:视觉 Transform vs 物理刚体位置差。若刚体稳而 Transform 晃
							// (或反之)→ 采样目标选错对象;两者同晃 → 真物理振荡(需按部件名定案)。
							try
							{
								if (bodyList[bi].BodyScript.RigidBody != null)
									_diagBodyRbDeltas[bi] = (bodyList[bi].BodyScript.Transform.position - bodyList[bi].BodyScript.RigidBody.position).magnitude;
							}
							catch { _diagBodyRbDeltas[bi] = -1f; }
							// 相对质心 = comRot⁻¹ * body世界旋转（帧空间）
							Quaternion relCom = invComRotUnity * bodyList[bi].BodyScript.Transform.rotation;
							// body-sync P0:相对 comRot 的位置(转轴/关节连接的子装配"整体移动"主要就是位置变化)。
							// 与 BodyRotations 同循环同索引,接收端 body.Transform.position = comRot.TransformPoint(relPos)。
							// 2026-09-19:用稳定基准(见 stableBodyAnchor)替代实时 comRot Transform,消除共模摆动。
							bPos = invComRotUnity * (bodyList[bi].BodyScript.Transform.position - stableBodyAnchor);
							bRotEuler = relCom.eulerAngles;
							// rotating-body-sync(2026-09-22):旋翼叶片等高速旋转 body 的角速度。
							// 局部系 = RigidBody.angularVelocity(世界系)转 body 自身局部系(Quaternion.Inverse(世界旋转)),
							// 与 BodyRotations 同基准(相对 comRot):接收端外推 "目标相位 = 包内绝对相位 × ω·ext" 时
							// 右乘 Euler(ω_local·ext) 即绕 body 自身轴转,与发送端叶片旋转轴一致。
							// 注意:叶片是独立 Rigidbody,angularVelocity 是真实物理转速(悬停时也 300+ RPM),
							// 不可用 craft 级 AngularVelocity(那是整船翻滚)。零/NaN 防御后入包。
							try
							{
								Rigidbody rb = bodyList[bi].BodyScript.RigidBody;
								if (rb != null)
								{
									Vector3 wWorld = rb.angularVelocity;
									if (!IsFinite(wWorld)) wWorld = Vector3.zero;
									bAngVelLocal = Quaternion.Inverse(bodyList[bi].BodyScript.Transform.rotation) * wWorld;
									if (!IsFinite(bAngVelLocal)) bAngVelLocal = Vector3.zero;
								}
							}
							catch { bAngVelLocal = Vector3.zero; }
							// rotating-body-sync:相对 comRot 线速度(数值差分上一包 bPos),再转 **body 局部系**。
							// 用途:叶片绕桨毂公转时,桨毂不在 comRot 上(绕 comRot 原点外推位置画错圆),
							// 接收端对旋转 body 用 v 逐帧积分位置(sp += v·dt)。v 存 body 局部系:
							// 叶片局部系中"切线方向"恒定(叶片随主轴公转,局部朝向同步转),接收端每帧
							// 用当前平滑朝向 sr·v_local 转出正确世界方向,自动随旋转累计(无需跨帧状态)。
							// 差分间隔 = 发包间隔(50ms@20Hz),叶片 31rad/s → 每包 1.55rad,信噪比充足。
							// 首包/瞬时空 dt 无效 → 0(接收端仅位置外推,无 v 时退回纯快照平滑)。
							if (_lastBodySampleTime >= 0f && _hasLastBodyPose[bi])
							{
								float dtSample = Time.unscaledTime - _lastBodySampleTime;
								if (dtSample > 0.001f)
								{
									Vector3 bVelCom = (bPos - _lastBodyPos[bi]) / dtSample;
									if (!IsFinite(bVelCom)) bVelCom = Vector3.zero;
									if (bVelCom.magnitude > MaxBodyVelMs) bVelCom = bVelCom.normalized * MaxBodyVelMs;
									bVelLocal = Quaternion.Inverse(relCom) * bVelCom;
									if (!IsFinite(bVelLocal)) bVelLocal = Vector3.zero;
								}
							}
							// 暂停时叶片物理停转(angularVelocity 保留暂停前值):若仍随包发送,接收端
							// 会按 ω·ext 继续外推旋转 → 暂停中叶片空转。清零与 Paused 语义一致
							// (接收端暂停期停止位置外推,旋转也不应继续)。
							if (data.Paused) { bAngVelLocal = Vector3.zero; bVelLocal = Vector3.zero; }
							_lastBodyPos[bi] = bPos;
							_lastBodyRot[bi] = bRotEuler;
							_lastBodyAngVel[bi] = bAngVelLocal;
							_hasLastBodyPose[bi] = true;
							// 抽搐诊断(发送端):每包 body[0] 相对 comRot 采样位置抖动。
							// 若静止时此处>0.01m,说明"发送端数据本身在抖"(来源:发送端自身 comRot/body 微动,
							// 或发送端 body 未静止),接收端平滑层只能衰减无法消除 → 需从发送端定位。
							if (bi == 0)
							{
								rcDiagBody0RelDelta = _diagBody0Rel.HasValue ? Vector3.Distance(bPos, _diagBody0Rel.Value) : 0f;
								_diagBody0Rel = bPos;
							}
						}
						else
						{
							// 瞬时空:沿用上一帧已知位姿,保索引对齐(接收端按 id 重排不受影响,双保险)。
							bPos = _hasLastBodyPose[bi] ? _lastBodyPos[bi] : Vector3.zero;
							bRotEuler = _hasLastBodyPose[bi] ? _lastBodyRot[bi] : Vector3.zero;
							bAngVelLocal = _hasLastBodyPose[bi] ? _lastBodyAngVel[bi] : Vector3.zero;
						}
						data.BodyRotations.Add(bRotEuler);
						data.BodyPositions.Add(bPos);
						data.BodyAngularVelocities.Add(bAngVelLocal);
						data.BodyVelocities.Add(bVelLocal);
						data.BodyIds.Add(bodyList[bi].Id);
					}
					_lastBodySampleTime = Time.unscaledTime;
					// 一次性部件名 dump(2026-09-19):body 索引/id → 部件名,定位振荡部件。
					// 实测 bodyMaxΔi 在 id 4,5,6,7 轮流最大 → 需知道它们是什么部件(轮子?机翼?起落架?)。
					if (!_diagBodyNamesLogged)
					{
						_diagBodyNamesLogged = true;
						string names = "";
						for (int bi = 0; bi < bodyList.Count; bi++)
						{
							string nm = "?";
							try
							{
								if (bodyList[bi].Parts != null && bodyList[bi].Parts.Count > 0 && bodyList[bi].Parts[0].Name != null)
									nm = bodyList[bi].Parts[0].Name;
							}
							catch { }
							if (bi > 0) names += ", ";
							names += bi + "(id=" + bodyList[bi].Id + ")=" + nm;
						}
						Mod.LogLobby("MultiPlayer bodyNames P" + _multiPlayer.PlayerId + ": " + names);
					}
				}

				// 2 阶外推数据(2026-09-14,acceleration-smoothing):采样加速度与角速度。
				// - Acceleration:行星系(含重力,根 body 刚体速度差分测量)→ 转地表系(纯旋转,同速度路径;
				//   Coriolis/离心项在 SR2 尺度 ≈0.1 m/s² 可忽略)。接收端外推加 ½·a·ext²。
				// - AngularVelocity:craft 局部系(ModApi 约定,SR2 符号翻转已内嵌)。接收端按 ω·ext 右乘外推朝向
				//   (符号约定待实测,见 plans/acceleration-smoothing-2026-09-14.md §六-1)。
				// ⚠️ 测量值必须 EMA + 钳制后才入包,否则差分噪声成为新抖动源;NaN/Inf 防御(坏值不污染)。
				Vector3 accelSurfaceRaw = Vector3.zero;
				Vector3 angVelLocalRaw = Vector3.zero;
				try
				{
					ICraftFlightData fd = craft.CraftScript.FlightData;
					if (fd != null)
					{
						accelSurfaceRaw = craft.Parent.PlanetVectorToSurfaceVector(fd.Acceleration).ToVector3();
						angVelLocalRaw = fd.AngularVelocity.ToVector3();
					}
				}
				catch { }
				// 原始采样保存给 sendDiag(accRaw=/wRaw=,ω 符号自校验数据源)
				_accelRawDiag = accelSurfaceRaw;
				_angVelRawDiag = angVelLocalRaw;
				if (!IsFinite(accelSurfaceRaw)) accelSurfaceRaw = Vector3.zero;
				if (!IsFinite(angVelLocalRaw)) angVelLocalRaw = Vector3.zero;
				_accelEma = UpdateEma(_accelEma, ref _hasAccelEma, accelSurfaceRaw, SenderAccelEmaRate);
				_angVelEma = UpdateEma(_angVelEma, ref _hasAngVelEma, angVelLocalRaw, SenderAngVelEmaRate);
				data.Acceleration = ClampMagnitude(_accelEma, MaxAccelMs);
				data.AngularVelocity = ClampMagnitude(_angVelEma, MaxAngVelRad);

				ICommandPod cp = craft.CraftScript.ActiveCommandPod;
				if (cp != null)
				{
					data.Pitch = cp.Controls.Pitch;
					data.Yaw = cp.Controls.Yaw;
					data.Roll = cp.Controls.Roll;
					data.Throttle = cp.Controls.Throttle;
					data.Brake = cp.Controls.Brake;
					data.Slider1 = cp.Controls.Slider1;
					data.Slider2 = cp.Controls.Slider2;
					data.Slider3 = cp.Controls.Slider3;
					data.Slider4 = cp.Controls.Slider4;
					data.TranslateForward = cp.Controls.TranslateForward;
					data.TranslateRight = cp.Controls.TranslateRight;
					data.TranslateUp = cp.Controls.TranslateUp;
					for (int i = 1; i <= 10; i++)
					{
						data.ActivationGroupStates.Add(cp.GetActivationGroupState(i));
					}
					data.Stage = cp.CurrentStage;
				}
				return true;
			}
			catch { return false; }
		}

		internal void ProcessOutgoing()
		{
			// 使用 unscaledDeltaTime：游戏暂停（Time.deltaTime==0）时状态包也照常发送，
			// 避免暂停导致对端远程飞船冻结/失步（暂停相关问题的临时处理）。
			_sendTimer += Time.unscaledDeltaTime * 1000f;
			// 暂停时位置/速度都不再变化,无需按全速上报;降到 ~8Hz 仍足以让对端确认"已暂停"
			// 并维持平滑层(带宽/CPU 都省),恢复后立即回到正常速率。
			bool localPaused = FlightSceneScript.Instance != null &&
				FlightSceneScript.Instance.TimeManager != null &&
				FlightSceneScript.Instance.TimeManager.Paused;
			float sendIntervalMs = localPaused ? Mathf.Max(_multiPlayer.SendIntervalMs, PausedSendIntervalMs) : _multiPlayer.SendIntervalMs;
			if (_sendTimer < sendIntervalMs) return;
			// F5(2026-09-15):帧率解耦 —— 每帧最多补发 2 包,≥10fps 也发满 20Hz。
			// (原实现每帧最多 1 包 → 10fps 的机器只发 10Hz → 对端 gapEMA≈95ms、每包位置跳变翻倍。
			//  2026-09-15 Steam 实测对端发包率 ~10.6Hz:gapEMA 95ms 且 jitterEMA 小=均匀、Steam 可靠通道
			//  不丢包、接收端 PollConnection 全排空无每帧限流 → 只可能是发送端帧率耦合。)
			// do/while + 预算:首次必发(已过阈值);同帧余量仍够再补发 ≤1 次(共 ≤2 包/帧)。
			// 携带余量而非清零:正常帧率(≥20fps)发满 20Hz;10fps 补发到 20Hz;卡顿恢复由 F4 兜底不泄洪。
			int sendBudget = 2;
			Vector3d? loopSentPos = null; // F6b(2026-09-19):同帧重复包抑制(见下方 TrySampleLocalCraft 处)
			do
			{
			_sendTimer -= sendIntervalMs;
			// F4(2026-09-14,smoothing-comparison §五 / README §三):帧卡顿后 timer 余量大 → 恢复后每帧泄洪一包
			// (发送端自身突发,接收端见成簇包,实测 sendGap 15~30ms 双峰)。钳制余量上限,
			// 卡顿恢复后按正常节奏补发,不一次性灌给网络。
			if (_sendTimer > sendIntervalMs * 2f) _sendTimer = sendIntervalMs * 2f;

			Mod.RemoteDataPack data;
			if (!TrySampleLocalCraft(out data)) break;
			// F6b(2026-09-19):同帧重复包抑制 —— 一帧内位置只能采样一次,F5 补发的第二包与第一包
			// 内容完全相同(dPos=0)→ 接收端 stall 误判"发送端冻结"(120Hz 实测 freeze 抖动 92 次,
			// vel 恒定 6m/s 但 pkΔ=0.0000m 即此因)且 mRate 被 0/正常 交替污染。
			// 首包必发;同帧再次采样位置完全相同则不再补发。跨帧相同位置(真暂停)仍发送 ——
			// 接收端靠 stall 时间阈值(F6b)+ Paused 标记判定冻结,不依赖此处。
			if (loopSentPos.HasValue &&
				data.Position.x == loopSentPos.Value.x &&
				data.Position.y == loopSentPos.Value.y &&
				data.Position.z == loopSentPos.Value.z) break;
			loopSentPos = data.Position;
			// 客户端在收到 Welcome（拿到 _multiPlayer.PlayerId）前不发状态包：
			// 否则会以 PlayerId=-1 发包，房主无法关联到已登记玩家（"state for player -1"）。
			if (_multiPlayer.PlayerId < 0) break;

			
			// 周期性本机朝向/位置诊断日志已移除（原为 if(false) 禁用块；
			// 其内曾被加入过早 return，导致 ProcessOutgoing 每帧提前返回、状态包完全停发）
			double time = FlightSceneScript.Instance.FlightState.Time;
			byte[] packet = MultiPlayerMessages.EncodeState(_multiPlayer.PlayerId, _multiPlayer.LocalNodeId, time, data);
			// 抽搐诊断(发送端):每 1s 输出本机采样数据抖动。若静止时 body0RelΔ 持续>0.01m,
			// 说明发送端数据本身在抖(comRot/body 微动),接收端平滑只能衰减无法消除。
			if (Time.unscaledTime - _diagBody0RelLogTime > 1f)
			{
				_diagBody0RelLogTime = Time.unscaledTime;
				// 发送端渲染帧率 EMA + 实际发包率(1000/sendGapEmaMs):F5 前发送循环每帧最多一包,
				// 10fps 机器只能发 10Hz(2026-09-15 Steam 实测对端 gapEMA≈95ms 即此因);
				// 与对端 3s 行 fps/recvHz 对账,验证 F5 后发包率与帧率解耦。
				float fpsNow = Time.unscaledDeltaTime > 0f ? 1f / Time.unscaledDeltaTime : 0f;
				_fpsEma = _fpsEma <= 0f ? fpsNow : _fpsEma * 0.9f + fpsNow * 0.1f;
				// body0Rel = 包内 body[0] 相对 comRot 的偏移绝对值(接收端根 body 写 comPos,不叠加它;
				// 该值即"游戏放置 vs 我们的写入"的对抗幅度,双端同版本时可直接核对)
				double body0Rel = 0.0;
				if (data.BodyPositions != null && data.BodyPositions.Count > 0)
				{
					body0Rel = data.BodyPositions[0].magnitude;
				}
				// bodyMaxRelΔ(2026-09-19):全部 body 相对 comRot 位姿与 1s 前最大位移。
				// body0RelΔ 只盯根 body,非根部件甩动/装配索引错位(计数不变)看不见 —— 实测接收端
				// bodyDelta 间歇 2~4.7m(部件在跳)而 body0RelΔ=0,需全 body 口径定位数据源。
				// bodyMaxΔi/bodyMaxId:最大位移的索引与 id —— 恒同索引 = 某部件真甩;索引漂移 = 装配顺序乱。
				double bodyMaxRelDelta = 0.0;
				int bodyMaxIdx = -1, bodyMaxId = -1;
				if (data.BodyPositions != null && data.BodyPositions.Count > 0 && _diagPrevBodyRel != null)
				{
					int bn = Mathf.Min(data.BodyPositions.Count, _diagPrevBodyRel.Length);
					for (int bi = 0; bi < bn; bi++)
					{
						double dd = (data.BodyPositions[bi] - _diagPrevBodyRel[bi]).magnitude;
						if (dd > bodyMaxRelDelta) { bodyMaxRelDelta = dd; bodyMaxIdx = bi; }
					}
					if (bodyMaxIdx >= 0 && data.BodyIds != null && bodyMaxIdx < data.BodyIds.Count) bodyMaxId = data.BodyIds[bodyMaxIdx];
				}
				if (data.BodyPositions != null)
				{
					if (_diagPrevBodyRel == null || _diagPrevBodyRel.Length != data.BodyPositions.Count)
						_diagPrevBodyRel = new Vector3[data.BodyPositions.Count];
					for (int bi = 0; bi < data.BodyPositions.Count; bi++) _diagPrevBodyRel[bi] = data.BodyPositions[bi];
				}
				// ω 符号自校验(2026-09-14,acceleration-smoothing §六-1):用上一 sendDiag 时刻的 SrfRel 按
				// 本段 ω(EMA 值,假设恒定)外推,与实际 SrfRel 对比。稳态转弯段误差最小者 = 正确符号约定:
				// errF+ / errF- = 对 ω 做 (-x,y,-z) 翻转还原 Unity 局部系后 sign± 右乘的预测误差;
				// errR+ = 不翻转直接用 ω sign+ 的预测误差。另 srfΔ(实际 SrfRel 转角) vs wΔ(|ω|×Δt)
				// 验证角速度量级是否与朝向变化一致。
				string wSignDiag = "-";
				try
				{
					if (_diagPrevSrfRel.HasValue && data.AngularVelocity.magnitude > 0.001f)
					{
						float dtDiag = Time.unscaledTime - _diagPrevSrfTime;
						Vector3 wEma = data.AngularVelocity;
						Vector3 wFlip = new Vector3(-wEma.x, wEma.y, -wEma.z);
						float wMag = wEma.magnitude;
						float srfDeltaDeg = Quaternion.Angle(_diagPrevSrfRel.Value, data.SrfRel.ToQuaternion());
						float wDeltaDeg = wMag * dtDiag * Mathf.Rad2Deg;
						Quaternion cur = data.SrfRel.ToQuaternion();
						float errFp = Quaternion.Angle(_diagPrevSrfRel.Value * Quaternion.Euler(wFlip * dtDiag * Mathf.Rad2Deg), cur);
						float errFm = Quaternion.Angle(_diagPrevSrfRel.Value * Quaternion.Euler(-wFlip * dtDiag * Mathf.Rad2Deg), cur);
						float errRp = Quaternion.Angle(_diagPrevSrfRel.Value * Quaternion.Euler(wEma * dtDiag * Mathf.Rad2Deg), cur);
						wSignDiag = "srfΔ=" + srfDeltaDeg.ToString("F2") + "deg wΔ=" + wDeltaDeg.ToString("F2") + "deg" +
							" errF+=" + errFp.ToString("F2") + " errF-=" + errFm.ToString("F2") + " errR+=" + errRp.ToString("F2");
					}
					_diagPrevSrfRel = data.SrfRel.ToQuaternion();
					_diagPrevSrfTime = Time.unscaledTime;
				}
				catch { }
				// body ω 符号自校验(rotating-body-sync):对旋转最快的 body,预测位置 = 上一时刻相对位置
				// 绕 ω 轴转 ω·dt。errB+/errB- = 正向/反向旋转的预测误差;errB+ < errB- → 当前符号正确。
				// 注意:外推旋转中心近似取 comRot(与接收端一致),桨毂偏离 comRot 时两方向误差都偏大,
				// 但较小者仍指示正确方向。仅当旋转体存在时输出。
				string bodyWSignDiag = "-";
				try
				{
					if (_diagPrevBodyPosRel != null && data.BodyPositions != null && data.BodyAngularVelocities != null &&
						_diagPrevBodyPosRel.Length == data.BodyPositions.Count &&
						_diagPrevBodyRotRel != null && _diagPrevBodyRotRel.Length == data.BodyRotations.Count &&
						Time.unscaledTime - _diagPrevBodyTime > 0.01f)
					{
						float dtDiag = Time.unscaledTime - _diagPrevBodyTime;
						// 找 |ω| 最大的 body(旋翼叶片;排除整船翻滚 ω 已含在 data.AngularVelocity)
						int wi = -1; float wmax = 0f;
						for (int bi = 0; bi < data.BodyAngularVelocities.Count; bi++)
						{
							float wm = data.BodyAngularVelocities[bi].magnitude;
							if (wm > wmax) { wmax = wm; wi = bi; }
						}
						if (wi >= 0 && wmax > BodySpinThresholdRad)
						{
							Vector3 wLocal = data.BodyAngularVelocities[wi];
							Vector3 axisPrev = Quaternion.Euler(_diagPrevBodyRotRel[wi]) * (wLocal / wmax);
							Vector3 pPrev = _diagPrevBodyPosRel[wi];
							Vector3 pCur = data.BodyPositions[wi];
							float theta = wmax * dtDiag * Mathf.Rad2Deg;
							// 与接收端逐帧积分同法(位置沿切线推进):预测 = 上一包位置 + 切线速度×dt。
							// 切线方向 = 上一包朝向把"v_local(差分切线速度)"转回 comRot 系;
							// err+ = 沿切线正向推进的误差(应小,因为 v 本身就是差分出来的真实切线速度),
							// err- = 反向推进的误差(应大)。err+ 显著 < err- → v 方向与接收端一致。
							Vector3 vAdv = Vector3.zero;
							if (data.BodyVelocities != null && wi < data.BodyVelocities.Count && dtDiag > 0.001f)
								vAdv = Quaternion.Euler(_diagPrevBodyRotRel[wi]) * data.BodyVelocities[wi];
							Vector3 predPlus = pPrev + vAdv * dtDiag;
							Vector3 predMinus = pPrev - vAdv * dtDiag;
							bodyWSignDiag = "bErr+=" + (predPlus - pCur).magnitude.ToString("F2") + "m" +
								" bErr-=" + (predMinus - pCur).magnitude.ToString("F2") + "m" +
								" bIdx=" + wi + " bω=" + wmax.ToString("F1") + "rad/s";
						}
					}
					if (data.BodyPositions != null && data.BodyPositions.Count > 0)
					{
						if (_diagPrevBodyPosRel == null || _diagPrevBodyPosRel.Length != data.BodyPositions.Count)
							_diagPrevBodyPosRel = new Vector3[data.BodyPositions.Count];
						if (_diagPrevBodyRotRel == null || _diagPrevBodyRotRel.Length != data.BodyRotations.Count)
							_diagPrevBodyRotRel = new Vector3[data.BodyRotations.Count];
						for (int bi = 0; bi < data.BodyPositions.Count; bi++) _diagPrevBodyPosRel[bi] = data.BodyPositions[bi];
						for (int bi = 0; bi < data.BodyRotations.Count; bi++) _diagPrevBodyRotRel[bi] = data.BodyRotations[bi];
						_diagPrevBodyTime = Time.unscaledTime;
					}
				}
				catch { }
				// rotating-body-sync(2026-09-22)发送端诊断:旋转 body 数 + 最大 |ω|(rad/s)。
				// wMax≈30 rad/s 量级 = 旋翼叶片高速旋转(300+RPM);wMax=0 → 对端旧版无此字段。
				int bSpinCount = 0; float bSpinMaxW = 0f;
				try
				{
					if (data.BodyAngularVelocities != null)
					{
						for (int bi = 0; bi < data.BodyAngularVelocities.Count; bi++)
						{
							float wm = data.BodyAngularVelocities[bi].magnitude;
							if (wm > 0.001f) { bSpinCount++; if (wm > bSpinMaxW) bSpinMaxW = wm; }
						}
					}
				}
				catch { }
				Mod.LogLobby("MultiPlayer sendDiag P" + _multiPlayer.PlayerId +
					": vel=" + data.Velocity.magnitude.ToString("F3") + "m/s" +
					" paused=" + (data.Paused ? 1 : 0) +
					" accRaw=" + _accelRawDiag.magnitude.ToString("F2") + "m/s²" +
					" acc=" + data.Acceleration.magnitude.ToString("F2") + "m/s²" +
					" wRaw=" + _angVelRawDiag.magnitude.ToString("F2") + "rad/s" +
					" w=" + data.AngularVelocity.magnitude.ToString("F2") + "rad/s" +
					" body0RelΔ=" + rcDiagBody0RelDelta.ToString("F4") + "m" +
					" bodyMaxRelΔ=" + bodyMaxRelDelta.ToString("F4") + "m" +
					" bodyMaxΔi=" + bodyMaxIdx + (bodyMaxId >= 0 ? "(id=" + bodyMaxId + ")" : "") +
					" rbΔ=" + (_diagBodyRbDeltas != null && bodyMaxIdx >= 0 && bodyMaxIdx < _diagBodyRbDeltas.Length ? _diagBodyRbDeltas[bodyMaxIdx].ToString("F3") : "?") + "m" +
					" body0Rel=" + body0Rel.ToString("F4") + "m" +
					" bodyCnt=" + (data.BodyPositions != null ? data.BodyPositions.Count : 0) +
					" bSpin=" + bSpinCount + " bWMax=" + bSpinMaxW.ToString("F1") + "rad/s" +
					" sendGap=" + (_lastSendTime >= 0f ? _sendGapEmaMs.ToString("F0") : "?") + "ms" +
					" sendHz=" + (_sendGapEmaMs > 0f ? (1000f / _sendGapEmaMs).ToString("F1") : "?") + "Hz" +
					" fps=" + _fpsEma.ToString("F0") +
					" " + wSignDiag + " " + bodyWSignDiag);
			}
			// 发送节奏诊断(2026-09-14):实际发包间隔 EMA。记录在真正发包处,过滤采样失败未发帧;
			// 与接收端 MultiPlayer gap(到达间隔)对账定位突发来源(发送端自身 vs 网络 relay)。
			if (_lastSendTime >= 0f)
			{
				float gap = (Time.unscaledTime - _lastSendTime) * 1000f;
				_sendGapEmaMs = _sendGapEmaMs <= 0f ? gap : _sendGapEmaMs * 0.9f + gap * 0.1f;
			}
			_lastSendTime = Time.unscaledTime;
			_multiPlayer.SendOrBroadcastToNet(packet);
			}
			while (_sendTimer >= sendIntervalMs && --sendBudget > 0); // F5:同帧余量仍够则补发(共 ≤2 包/帧)
		}

		/// <summary>
		/// 保活心跳：周期性发送 Ping，未进飞行场景时也能维持对端不超时。
		/// 使用 unscaledDeltaTime，避免游戏暂停时（Time.deltaTime==0）心跳停发导致对端 3 秒超时。
		/// </summary>
		internal void SendKeepAlive()
		{
			_keepAliveTimer -= Time.unscaledDeltaTime;
			if (_keepAliveTimer > 0f) return;
			_keepAliveTimer = 1f;

			byte[] ping = MultiPlayerMessages.EncodePing(DateTime.UtcNow.Ticks);
			_multiPlayer.SendOrBroadcastToNet(ping);
		}
	}
}
