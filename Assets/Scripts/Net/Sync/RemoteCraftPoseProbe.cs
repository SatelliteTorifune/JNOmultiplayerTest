using System;
using System.Collections.Generic;
using System.Text;
using Assets.Scripts.Flight;
using ModApi;
using ModApi.Craft;
using ModApi.Craft.Parts;
using UnityEngine;
using Assets.Scripts.Net.Session;

namespace Assets.Scripts.Net.Sync
{
	/// <summary>
	/// 渲染前可见位姿探针(纯观测:只读游戏状态、只写日志,零行为改动)。
	///
	/// 动机(四轮修复失败的测量学原因):既有全部诊断的采样点都不在"玩家真正看到的那一层":
	///   - moveMax / LastRenderedPos / chain / frame → 采样"包推导出的平滑**目标**"(数据流),不是写入后的 Transform;
	///   - b0d / comLink / comCross(GhostPoseWriter.cs:245-273) → 采样点在 **mod 自己的写回路径内部**;
	///   - tfDrift(RemoteCraftDriver.cs:51) → 采样点是**下一帧 Update**,且只读 **root transform**
	///     (1.4.2 起可见几何在 body 上、body 已脱离 craft 层级)。
	/// 本探针以 <see cref="DefaultExecutionOrder"/> = 30000 的 LateUpdate 采样:
	///   游戏 Update/LateUpdate(默认 0)→ mod NetworkManager.Update/LateUpdate(1000)→ **探针(30000)→ 渲染**。
	///
	/// 2026-09-23 实测定案(见 plans/observer-tick-quantization-2026-09-23.md):并排飞行"前后抖动"的根因在**观察者侧**——
	/// 本机船/相机的位置只在物理固定步更新(CraftBuilder.cs:223 刚体 interpolation=None),而幽灵由 mod 每渲染帧写入连续位置;
	/// 实测相机逐帧位移恒为 v×fixedDt 的整数倍、幽灵为连续小数、drift*=0(无人覆盖 mod 写入)。
	///
	/// 输出(前缀可 grep):
	///   rprobeEnv       (每进程一次): fixedDt / maximumDeltaTime / vSync / targetFps / 帧是否 surface-lock / 本机船与幽灵逐 body 插值·kinematic
	///   rprobe P#       (1s/艘): 可见层 rootSeq/b0Seq(幽灵逐帧位移序列,cm)+ driftXxx(mod 写完→渲染前的漂移,非 0 即有第三方写者)
	///                            + gameDelta(游戏交给 RecalculateFrameState 的增量)+ pktPaused(本窗 Paused=1 的包数)
	///   rprobeObs P#    (1s/艘): 观察者层 dtSeq(帧毫秒)/stepsSeq(本帧物理步数)/lCoMSeq(本机船质心逐帧位移)
	///                            /camMoveSeq/camRotSeq;量化指标 lQuant·gQuant(位移落在"最小非零步长"整数倍上的帧占比)
	///                            + relSeq(幽灵相对本机船质心,即眼睛看到的相对运动)
	///   rprobeInterp    (事件): 本机船/幽灵逐 body 插值状态发生变化时(例如按 Ctrl+Shift+I 或 P0 生效)
	/// </summary>
	[DefaultExecutionOrder(30000)]
	internal class RemoteCraftPoseProbe : MonoBehaviour
	{
		/// <summary>每条序列保留的帧数(1s 窗口 ≈ 该机器的渲染帧数)。</summary>
		private const int SeqLen = 40;

		/// <summary>输出周期(秒)。</summary>
		private const float LogIntervalSec = 1f;

		/// <summary>沿运动轴投影的位移小于该值(cm)视为"停滞帧"。</summary>
		private const float StallCm = 1f;

		/// <summary>沿运动轴投影的位移小于该值(cm)视为"后退帧"。</summary>
		private const float BackCm = -0.5f;

		/// <summary>量化判定容差:位移 ÷ 最小非零步长 与最近整数之差小于该比例即算"落在整数倍上"。</summary>
		private const float QuantTol = 0.08f;

		/// <summary>量化的"最小非零步长"下限(cm):低于此值不参与判定。</summary>
		private const float QuantUnitMinCm = 0.5f;

		/// <summary>
		/// 探针总开关(独立于 <see cref="RemoteCraft.ExtraDiagEnabled"/>,便于只关本探针)。
		/// 用 static readonly(非 const)以免编译器把 <c>if (!ProbeEnabled) return;</c> 判成不可达代码(CS0162)。
		/// </summary>
		private static bool ProbeEnabled { get { return MultiPlayerDiag.Enabled && MultiPlayerDiag.ProbeEnabled; } }

		/// <summary>单艘幽灵的探针状态(按 playerId 缓存,幽灵销毁后自动清理)。</summary>
		private sealed class CraftProbe
		{
			public bool HasPrev;
			public Vector3 PrevRoot, PrevBody0, PrevCam, PrevLocalRoot, PrevLocalCoM;
			public Quaternion PrevCamRot = Quaternion.identity;
			public Vector3 PrevGhostFromCam, PrevLocalFromCam, PrevRelRoot, PrevRelCoM;
			public Vector3 Axis = Vector3.forward;
			// 帧序列
			public readonly float[] SeqRoot = new float[SeqLen];
			public readonly float[] SeqBody0 = new float[SeqLen];
			public readonly float[] SeqDtMs = new float[SeqLen];
			public readonly int[] SeqSteps = new int[SeqLen];
			public readonly float[] SeqLocalCoM = new float[SeqLen];
			public readonly float[] SeqCamMove = new float[SeqLen];
			public readonly float[] SeqCamRot = new float[SeqLen];
			public readonly float[] SeqGhostRelCam = new float[SeqLen];
			public readonly float[] SeqLocalRelCam = new float[SeqLen];
			public readonly float[] SeqRel = new float[SeqLen];
			public readonly float[] SeqRelCoM = new float[SeqLen];
			public readonly float[] SeqGameDelta = new float[SeqLen];
			// 位移 ÷ 本帧时长(cm/ms):把"停-走"(该比值本身有停顿/尖峰)与"帧时长抖动"
			// (该比值恒定、只是帧变长所以位移变大)分开 —— 残余抖动定性的关键量。
			public readonly float[] SeqGhostCmPerMs = new float[SeqLen];
			public readonly float[] SeqLocalCmPerMs = new float[SeqLen];
			public readonly float[] SeqRelCmPerMs = new float[SeqLen];
			public int Head;
			public int Count;
			// 窗口统计
			public int WinFrames;
			public int WinRootBack, WinRootStall, WinBody0Stall, WinLocalStall, WinLocalBack;
			public float WinRootMax, WinBody0Max, WinLocalMax, WinLocalMin = float.MaxValue;
			public float WinLocalCoMMax, WinRelCoMMax;
			public float WinGhostRelCamMax, WinLocalRelCamMax, WinRelMax, WinCamMoveMax, WinCamRotMax;
			public float WinDriftRootMax, WinDriftBody0Max, WinDriftComMax, WinGameDeltaMax;
			public int WinStepsMax, WinStepsMin = int.MaxValue, WinStepsSum;
			public float WinDtMaxMs, WinDtMinMs = float.MaxValue, WinDtSumMs;
			public float LastLogTime, LastSeenTime;
			public Vector3 LastDriftRoot, LastDriftBody0, LastDriftCom;
			// P2 验证:积分器自身输出速度 vs 渲染根速度(同帧同量纲比较,大小可比 |Δ| 因坐标变换保长)
			public Vector3d PrevIntegPos;
			public bool HasPrevInteg;
			public float WinISpeedMax, WinISpeedMin = float.MaxValue;
			public float WinGSpdAbsMax, WinGSpdAbsMin = float.MaxValue;
			public float WinIVelMax;
			// ★ 可见几何(幽灵是 isPhysics=1 的船:root transform 由游戏 RecenterTransformOnCoM 从物理 CoM 摆出,
			// 而**可见几何在 body 上、由 mod 每帧写**)。故 body[0] 的模长速度才是"眼睛看到的那条曲线"。
			public float WinBSpdAbsMax, WinBSpdAbsMin = float.MaxValue;
		}

		private readonly Dictionary<int, CraftProbe> _probes = new Dictionary<int, CraftProbe>();
		private readonly List<int> _stale = new List<int>();
		private readonly StringBuilder _sb = new StringBuilder(4096);
		private static bool _envLogged;
		private float _prevFixedTime = -1f;
		private int _lastSteps;
		// 插值状态快照(用于"状态变更即打一行"的事件日志)
		private string _lastLocalInterp = "";
		private string _lastGhostInterp = "";

		private void LateUpdate()
		{
			if (!ProbeEnabled) return;
			NetworkManager mgr = NetworkManager.Instance;
			if (mgr == null || !mgr.IsConnected) return;
			FlightSceneScript scene = FlightSceneScript.Instance;
			if (scene == null) return;
			Dictionary<int, RemoteCraft> crafts = mgr.Crafts != null ? mgr.Crafts._remoteCrafts : null;
			if (crafts == null || crafts.Count == 0) return;

			float now = Time.unscaledTime;
			float dt = Time.unscaledDeltaTime;

			// 本帧执行的物理固定步数(节拍量化的直接观测量;timeScale=1 时步长即固定步长)
			int steps = 0;
			try
			{
				float fixedNow = Time.fixedTime;
				float fixedDt = Time.fixedDeltaTime;
				if (_prevFixedTime >= 0f && fixedDt > 1e-5f)
				{
					steps = Mathf.Clamp(Mathf.RoundToInt((fixedNow - _prevFixedTime) / fixedDt), 0, 30);
				}
				_prevFixedTime = fixedNow;
				_lastSteps = steps;
			}
			catch { }

			// 相机:飞行相机(近相机即主渲染相机)
			Transform cam = null;
			try
			{
				if (scene.ViewManager != null && scene.ViewManager.GameView != null && scene.ViewManager.GameView.GameCamera != null)
				{
					Camera c = scene.ViewManager.GameView.GameCamera.NearCamera;
					if (c != null) cam = c.transform;
				}
			}
			catch { }
			Vector3 camPos = cam != null ? cam.position : Vector3.zero;
			Quaternion camRot = cam != null ? cam.rotation : Quaternion.identity;
			bool hasCam = cam != null;

			// 本机(观察者)船:root transform 飞行时通常不动;可见几何在 body,故同时采 CenterOfMass 与 body[0]
			Transform localRoot = null, localCoM = null, localBody0 = null;
			try
			{
				if (scene.CraftNode != null && scene.CraftNode.CraftScript != null)
				{
					localRoot = scene.CraftNode.CraftScript.Transform;
					localCoM = scene.CraftNode.CraftScript.CenterOfMass;
					IReadOnlyList<BodyData> lb = scene.CraftNode.CraftScript.Data.Assembly.Bodies;
					if (lb != null && lb.Count > 0 && lb[0].BodyScript != null) localBody0 = lb[0].BodyScript.Transform;
				}
			}
			catch { }
			Vector3 localRootPos = localRoot != null ? localRoot.position : Vector3.zero;
			Vector3 localCoMPos = localCoM != null ? localCoM.position : localRootPos;
			bool hasLocal = localCoM != null || localRoot != null;

			if (!_envLogged && crafts.Count > 0)
			{
				_envLogged = true;
				LogEnv(mgr, scene, crafts, hasCam, localRoot, localCoM, localBody0);
			}
			LogInterpChanges(scene, crafts);

			_stale.Clear();
			foreach (KeyValuePair<int, RemoteCraft> kv in crafts)
			{
				RemoteCraft rc = kv.Value;
				CraftProbe p;
				if (!_probes.TryGetValue(kv.Key, out p)) { p = new CraftProbe(); _probes[kv.Key] = p; }
				try
				{
					if (!ProbeOne(rc, p, dt, steps, now, hasCam, camPos, camRot, hasLocal, localRootPos, localCoMPos)) _stale.Add(kv.Key);
				}
				catch (Exception e)
				{
					if (now - p.LastLogTime > 5f) { p.LastLogTime = now; Mod.LogError("MultiPlayer rprobe error P" + kv.Key + ": " + e.Message); }
				}
			}
			for (int i = 0; i < _stale.Count; i++) _probes.Remove(_stale[i]);
		}

		/// <summary>采样一艘幽灵。返回 false 表示该幽灵当前不可采样(移出探针表)。</summary>
		private bool ProbeOne(RemoteCraft rc, CraftProbe p, float dt, int steps, float now,
			bool hasCam, Vector3 camPos, Quaternion camRot, bool hasLocal, Vector3 localRootPos, Vector3 localCoMPos)
		{
			if (rc == null) return false;
			if (rc.Node == null || rc.Node.CraftScript == null) return false;
			Transform root = rc.Node.CraftScript.Transform;
			if (root == null) return false;
			IReadOnlyList<BodyData> bodies = null;
			try { bodies = rc.Node.CraftScript.Data.Assembly.Bodies; } catch { }
			if (bodies == null || bodies.Count == 0) return false;
			Transform body0 = null;
			try { if (bodies[0].BodyScript != null) body0 = bodies[0].BodyScript.Transform; } catch { }
			Transform comT = rc.Node.CraftScript.CenterOfMass;

			Vector3 rootPos = root.position;
			Vector3 body0Pos = body0 != null ? body0.position : rootPos;
			Vector3 comPos = comT != null ? comT.position : rootPos;

			// 运动轴:优先用"本探针观测到的逐帧位移",退化时保持上一帧轴
			if (p.HasPrev)
			{
				Vector3 step = rootPos - p.PrevRoot;
				if (step.sqrMagnitude > 1e-6f) p.Axis = step.normalized;
			}

			// 游戏侧对抗量:|帧空间节点位置 − CenterOfMass.position|(游戏 UpdateCraft 交出的增量)
			float gameDelta = 0f;
			try
			{
				if (rc.Node.GameView != null && rc.Node.GameView.ReferenceFrame != null)
					gameDelta = (rc.Node.GameView.ReferenceFrame.PlanetToFramePosition(rc.Node.Position) - comPos).magnitude;
			}
			catch { }

			// mod 写完(LateUpdate)到本探针采样点之间的漂移量
			if (rc.DiagHasLateBody0Pos)
			{
				p.LastDriftBody0 = body0Pos - rc.DiagLateBody0Pos;
				float d = p.LastDriftBody0.magnitude;
				if (d > p.WinDriftBody0Max) p.WinDriftBody0Max = d;
			}
			if (rc.DiagHasLateRootPos)
			{
				p.LastDriftRoot = rootPos - rc.DiagLateRootPos;
				float d = p.LastDriftRoot.magnitude;
				if (d > p.WinDriftRootMax) p.WinDriftRootMax = d;
			}
			if (rc.DiagHasLateComPos)
			{
				p.LastDriftCom = comPos - rc.DiagLateComPos;
				float d = p.LastDriftCom.magnitude;
				if (d > p.WinDriftComMax) p.WinDriftComMax = d;
			}

			if (p.HasPrev)
			{
				float dRoot = Vector3.Dot(rootPos - p.PrevRoot, p.Axis) * 100f;
				float dBody0 = Vector3.Dot(body0Pos - p.PrevBody0, p.Axis) * 100f;
				float dLocalCoM = Vector3.Dot(localCoMPos - p.PrevLocalCoM, p.Axis) * 100f;
				float dCamMove = hasCam ? Vector3.Dot(camPos - p.PrevCam, p.Axis) * 100f : 0f;
				float camRotDeg = hasCam ? Quaternion.Angle(p.PrevCamRot, camRot) : 0f;
				float dGhostRelCam = 0f, dLocalRelCam = 0f, dRelRoot = 0f, dRelCoM = 0f;
				if (hasCam)
				{
					Vector3 gFromCam = rootPos - camPos;
					Vector3 lFromCam = localCoMPos - camPos;
					dGhostRelCam = Vector3.Dot(gFromCam - p.PrevGhostFromCam, p.Axis) * 100f;
					dLocalRelCam = Vector3.Dot(lFromCam - p.PrevLocalFromCam, p.Axis) * 100f;
					p.PrevGhostFromCam = gFromCam; p.PrevLocalFromCam = lFromCam;
				}
				if (hasLocal)
				{
					Vector3 relRoot = rootPos - localRootPos;
					Vector3 relCoM = rootPos - localCoMPos;
					dRelRoot = Vector3.Dot(relRoot - p.PrevRelRoot, p.Axis) * 100f;
					dRelCoM = Vector3.Dot(relCoM - p.PrevRelCoM, p.Axis) * 100f;
					p.PrevRelRoot = relRoot; p.PrevRelCoM = relCoM;
				}

				// 一帧只推进一次 head/count:所有数组写同一索引。
				// 修 2026-09-24 环形缓冲 bug:原实现每个数组各调一次 Push(内含 head++)→ head 每帧前进 12,
				// SeqLen=40 时每个数组只用到 10 个槽位,其余 30 个恒为 0.0 → 打印出"3 个 0 夹 1 个值"的
				// **假停顿图案**(上一轮据此误判"硬 4:1 停顿")。单个数值本身是真的,故逐值判定的量化结论不受影响。
				float dtMs = Mathf.Max(dt * 1000f, 0.001f);
				// P2 验证:同一帧内比较"积分器自身输出速度(iSpd)"与"渲染根速度(gSpdAbs)",均用位移模长 → 坐标变换保长、可比。
				// 若 iSpd 平整而 gSpdAbs 抖 ⇒ 调制发生在积分器下游(游戏 CoM/物理/写回链);
				// 若两者同样抖 ⇒ 积分器自身没做到匀速。
				float gSpdAbs = (rootPos - p.PrevRoot).magnitude * 100f / dtMs;
				float bSpdAbs = (body0Pos - p.PrevBody0).magnitude * 100f / dtMs;   // ★ 可见几何的真实速度
				float iSpd = 0f;
				if (p.HasPrevInteg)
				{
					iSpd = (float)((rc.IntegPos - p.PrevIntegPos).magnitude * 100.0 / dtMs);
					if (iSpd > p.WinISpeedMax) p.WinISpeedMax = iSpd;
					if (iSpd < p.WinISpeedMin) p.WinISpeedMin = iSpd;
				}
				p.PrevIntegPos = rc.IntegPos; p.HasPrevInteg = true;
				float iVelMag = (float)rc.IntegVel.magnitude * 100f; // 积分速度(cm/ms;与速度同量纲 ×100)
				if (iVelMag > p.WinIVelMax) p.WinIVelMax = iVelMag;
				if (gSpdAbs > p.WinGSpdAbsMax) p.WinGSpdAbsMax = gSpdAbs;
				if (gSpdAbs < p.WinGSpdAbsMin) p.WinGSpdAbsMin = gSpdAbs;
				if (bSpdAbs > p.WinBSpdAbsMax) p.WinBSpdAbsMax = bSpdAbs;
				if (bSpdAbs < p.WinBSpdAbsMin) p.WinBSpdAbsMin = bSpdAbs;
				int idx = p.Head;
				p.SeqRoot[idx] = dRoot;
				p.SeqBody0[idx] = dBody0;
				p.SeqDtMs[idx] = dtMs;
				p.SeqSteps[idx] = steps;
				p.SeqLocalCoM[idx] = dLocalCoM;
				p.SeqCamMove[idx] = dCamMove;
				p.SeqCamRot[idx] = camRotDeg;
				p.SeqGhostRelCam[idx] = dGhostRelCam;
				p.SeqLocalRelCam[idx] = dLocalRelCam;
				p.SeqRel[idx] = dRelRoot;
				p.SeqRelCoM[idx] = dRelCoM;
				p.SeqGameDelta[idx] = gameDelta * 100f;
				p.SeqGhostCmPerMs[idx] = dRoot / dtMs;
				p.SeqLocalCmPerMs[idx] = dLocalCoM / dtMs;
				p.SeqRelCmPerMs[idx] = dRelCoM / dtMs;
				p.Head = (p.Head + 1) % SeqLen;
				if (p.Count < SeqLen) p.Count++;

				p.WinFrames++;
				if (Mathf.Abs(dRoot) > p.WinRootMax) p.WinRootMax = Mathf.Abs(dRoot);
				if (Mathf.Abs(dBody0) > p.WinBody0Max) p.WinBody0Max = Mathf.Abs(dBody0);
				if (Mathf.Abs(dLocalCoM) > p.WinLocalCoMMax) p.WinLocalCoMMax = Mathf.Abs(dLocalCoM);
				if (Mathf.Abs(dRelCoM) > p.WinRelCoMMax) p.WinRelCoMMax = Mathf.Abs(dRelCoM);
				if (dRoot < BackCm) p.WinRootBack++;
				if (Mathf.Abs(dRoot) < StallCm) p.WinRootStall++;
				if (Mathf.Abs(dBody0) < StallCm) p.WinBody0Stall++;
				if (hasLocal)
				{
					if (Mathf.Abs(dLocalCoM) > p.WinLocalMax) p.WinLocalMax = Mathf.Abs(dLocalCoM);
					if (dLocalCoM < p.WinLocalMin) p.WinLocalMin = dLocalCoM;
					if (dLocalCoM < BackCm) p.WinLocalBack++;
					if (Mathf.Abs(dLocalCoM) < StallCm) p.WinLocalStall++;
				}
				if (Mathf.Abs(dGhostRelCam) > p.WinGhostRelCamMax) p.WinGhostRelCamMax = Mathf.Abs(dGhostRelCam);
				if (Mathf.Abs(dLocalRelCam) > p.WinLocalRelCamMax) p.WinLocalRelCamMax = Mathf.Abs(dLocalRelCam);
				if (Mathf.Abs(dRelRoot) > p.WinRelMax) p.WinRelMax = Mathf.Abs(dRelRoot);
				if (Mathf.Abs(dCamMove) > p.WinCamMoveMax) p.WinCamMoveMax = Mathf.Abs(dCamMove);
				if (camRotDeg > p.WinCamRotMax) p.WinCamRotMax = camRotDeg;
				if (gameDelta > p.WinGameDeltaMax) p.WinGameDeltaMax = gameDelta;
				if (steps > p.WinStepsMax) p.WinStepsMax = steps;
				if (steps < p.WinStepsMin) p.WinStepsMin = steps;
				p.WinStepsSum += steps;
				if (dt * 1000f > p.WinDtMaxMs) p.WinDtMaxMs = dt * 1000f;
				if (dt * 1000f < p.WinDtMinMs) p.WinDtMinMs = dt * 1000f;
				p.WinDtSumMs += dt * 1000f;
			}

			p.PrevRoot = rootPos; p.PrevBody0 = body0Pos; p.PrevLocalRoot = localRootPos; p.PrevLocalCoM = localCoMPos;
			p.PrevCam = camPos; p.PrevCamRot = camRot;
			p.HasPrev = true;
			p.LastSeenTime = now;

			if (now - p.LastLogTime >= LogIntervalSec && p.WinFrames > 0)
			{
				p.LastLogTime = now;
				LogWindow(rc, p, dt, hasCam, hasLocal, rootPos, localCoMPos, camPos, body0Pos, comPos);
				ResetWindow(p);
			}
			return now - p.LastSeenTime < 10f;
		}

		private static void Push(float[] seq, ref int head, ref int count, float v)
		{
			seq[head] = v;
			head = (head + 1) % SeqLen;
			if (count < SeqLen) count++;
		}

		private static void PushI(int[] seq, ref int head, ref int count, int v)
		{
			seq[head] = v;
			head = (head + 1) % SeqLen;
			if (count < SeqLen) count++;
		}

		private static int Oldest(int head, int count, int i) { return (head - count + i + SeqLen * 4) % SeqLen; }

		private void AppendSeq(string name, float[] seq, int head, int count, string fmt)
		{
			_sb.Append(' ').Append(name).Append("=[");
			for (int i = 0; i < count; i++)
			{
				if (i > 0) _sb.Append(',');
				_sb.Append(seq[Oldest(head, count, i)].ToString(fmt));
			}
			_sb.Append(']');
		}

		private void AppendSeqInt(string name, int[] seq, int head, int count)
		{
			_sb.Append(' ').Append(name).Append("=[");
			for (int i = 0; i < count; i++)
			{
				if (i > 0) _sb.Append(',');
				_sb.Append(seq[Oldest(head, count, i)]);
			}
			_sb.Append(']');
		}

		/// <summary>
		/// 量化占比:位移落在"本窗最小非零步长"整数倍上的帧占比(容差 <see cref="QuantTol"/>)。
		/// 相机/本机船若只在物理固定步更新,该值会接近 1.00;每渲染帧连续写入的幽灵则显著更低。
		/// </summary>
		private static void Quantize(float[] seq, int head, int count, out float fraction, out float unitCm)
		{
			fraction = 0f; unitCm = 0f;
			if (count < 8) return;
			float minNz = float.MaxValue;
			for (int i = 0; i < count; i++)
			{
				float v = Mathf.Abs(seq[Oldest(head, count, i)]);
				if (v > QuantUnitMinCm && v < minNz) minNz = v;
			}
			if (minNz == float.MaxValue || minNz < QuantUnitMinCm) return;
			unitCm = minNz;
			int hit = 0, nz = 0;
			for (int i = 0; i < count; i++)
			{
				float v = Mathf.Abs(seq[Oldest(head, count, i)]);
				if (v <= QuantUnitMinCm) continue;
				nz++;
				float r = v / minNz;
				if (Mathf.Abs(r - Mathf.Round(r)) < QuantTol) hit++;
			}
			if (nz > 0) fraction = (float)hit / nz;
		}

		/// <summary>
		/// 速度一致性统计(残余抖动定性):对"位移 ÷ 本帧时长"(cm/ms)序列求 均值/最大/最小 与
		/// "停滞帧占比"(|cm/ms| 低于均值 25% 的帧)。
		/// 判读:若该比值基本恒定(停滞占比≈0),说明位移变化只是**帧时长变化**造成的(显示层抖动,
		/// 与网络/同步无关);若该比值本身出现 0 与尖峰(停滞占比显著),才是真正的**停-走**(目标层)。
		/// </summary>
		private static void SpeedStats(float[] seq, int head, int count, out float mean, out float min, out float max, out float stallFrac)
		{
			mean = 0f; min = 0f; max = 0f; stallFrac = 0f;
			if (count < 8) return;
			float sum = 0f; min = float.MaxValue; max = float.MinValue;
			int n = 0;
			for (int i = 0; i < count; i++)
			{
				float v = seq[Oldest(head, count, i)];
				if (v < min) min = v;
				if (v > max) max = v;
				sum += v; n++;
			}
			if (n == 0) return;
			mean = sum / n;
			float thr = Mathf.Abs(mean) * 0.25f;
			int stall = 0;
			for (int i = 0; i < count; i++)
			{
				if (Mathf.Abs(seq[Oldest(head, count, i)]) < thr) stall++;
			}
			stallFrac = (float)stall / count;
		}

		private void LogWindow(RemoteCraft rc, CraftProbe p, float dt, bool hasCam, bool hasLocal,
			Vector3 rootPos, Vector3 localCoMPos, Vector3 camPos, Vector3 body0Pos, Vector3 comPos)
		{
			float qCam, camUnit, qLocal, localUnit, qGhost, ghostUnit;
			Quantize(p.SeqCamMove, p.Head, p.Count, out qCam, out camUnit);
			Quantize(p.SeqLocalCoM, p.Head, p.Count, out qLocal, out localUnit);
			Quantize(p.SeqRoot, p.Head, p.Count, out qGhost, out ghostUnit);

			// ---- 行 1:可见层(幽灵) ----
			_sb.Length = 0;
			_sb.Append("MultiPlayer rprobe P").Append(rc.PlayerId)
				.Append(": frames=").Append(p.WinFrames)
				.Append(" fps=").Append(dt > 0f ? (1f / dt).ToString("F0") : "?")
				.Append(" dtWin=").Append(p.WinDtMinMs.ToString("F1")).Append('~').Append(p.WinDtMaxMs.ToString("F1")).Append("ms")
				.Append(" dist=").Append((rootPos - localCoMPos).magnitude.ToString("F0")).Append("m")
				.Append(" camDist=").Append(hasCam ? (rootPos - camPos).magnitude.ToString("F0") : "?").Append('m');
			AppendSeq(" rootSeq", p.SeqRoot, p.Head, p.Count, "F1");
			AppendSeq(" b0Seq", p.SeqBody0, p.Head, p.Count, "F1");
			_sb.Append(" rootMax=").Append(p.WinRootMax.ToString("F1")).Append("cm b0Max=").Append(p.WinBody0Max.ToString("F1")).Append("cm")
				.Append(" rootBack=").Append(p.WinRootBack).Append(" rootStall=").Append(p.WinRootStall)
				.Append(" gQuant=").Append(qGhost.ToString("F2")).Append("(unit=").Append(ghostUnit.ToString("F1")).Append("cm)")
				.Append(" driftRoot=").Append(p.WinDriftRootMax.ToString("F3")).Append("m")
				.Append(" driftB0=").Append(p.WinDriftBody0Max.ToString("F3")).Append("m")
				.Append(" driftCom=").Append(p.WinDriftComMax.ToString("F3")).Append("m")
				.Append(" gameDeltaMax=").Append(p.WinGameDeltaMax.ToString("F3")).Append("m")
				.Append(" pktPaused=").Append(rc.DiagPausedPktCount);
			rc.DiagPausedPktCount = 0;
			MultiPlayerDiag.Log(_sb.ToString());

			// ---- 行 2:观察者层 + 相对量 ----
			_sb.Length = 0;
			_sb.Append("MultiPlayer rprobeObs P").Append(rc.PlayerId)
				.Append(": stepsWin=").Append(p.WinStepsMin).Append('~').Append(p.WinStepsMax)
				.Append(" stepsAvg=").Append(p.WinFrames > 0 ? ((float)p.WinStepsSum / p.WinFrames).ToString("F2") : "?")
				.Append(" fixedDt=").Append((Time.fixedDeltaTime * 1000f).ToString("F2")).Append("ms");
			AppendSeq(" dtSeq", p.SeqDtMs, p.Head, p.Count, "F1");
			AppendSeqInt(" stepsSeq", p.SeqSteps, p.Head, p.Count);
			AppendSeq(" lCoMSeq", p.SeqLocalCoM, p.Head, p.Count, "F1");
			AppendSeq(" camMoveSeq", p.SeqCamMove, p.Head, p.Count, "F1");
			_sb.Append(" lQuant=").Append(qLocal.ToString("F2")).Append("(unit=").Append(localUnit.ToString("F1")).Append("cm)")
				.Append(" camQuant=").Append(qCam.ToString("F2")).Append("(unit=").Append(camUnit.ToString("F1")).Append("cm)")
				.Append(" lMax=").Append(p.WinLocalCoMMax.ToString("F1")).Append("cm")
				.Append(" lMin=").Append(p.WinLocalMin == float.MaxValue ? "?" : p.WinLocalMin.ToString("F1")).Append("cm")
				.Append(" lBack=").Append(p.WinLocalBack).Append(" lStall=").Append(p.WinLocalStall)
				.Append(" camRotMax=").Append(p.WinCamRotMax.ToString("F2")).Append("deg")
				.Append(" camMoveMax=").Append(p.WinCamMoveMax.ToString("F1")).Append("cm");
			AppendSeq(" relSeq", p.SeqRel, p.Head, p.Count, "F1");
			AppendSeq(" relCoMSeq", p.SeqRelCoM, p.Head, p.Count, "F1");
			AppendSeq(" gRelCam", p.SeqGhostRelCam, p.Head, p.Count, "F1");
			AppendSeq(" gCmPerMs", p.SeqGhostCmPerMs, p.Head, p.Count, "F2");
			AppendSeq(" lCmPerMs", p.SeqLocalCmPerMs, p.Head, p.Count, "F2");
			float gm, gmin, gmax, gstall, lm, lmin, lmax, lstall;
			SpeedStats(p.SeqGhostCmPerMs, p.Head, p.Count, out gm, out gmin, out gmax, out gstall);
			SpeedStats(p.SeqLocalCmPerMs, p.Head, p.Count, out lm, out lmin, out lmax, out lstall);
			_sb.Append(" gSpeed=avg").Append(gm.ToString("F2")).Append("/min").Append(gmin.ToString("F2")).Append("/max").Append(gmax.ToString("F2"))
				.Append("cm/ms gStallPct=").Append((gstall * 100f).ToString("F0")).Append('%')
				.Append(" lSpeed=avg").Append(lm.ToString("F2")).Append("/min").Append(lmin.ToString("F2")).Append("/max").Append(lmax.ToString("F2"))
				.Append("cm/ms lStallPct=").Append((lstall * 100f).ToString("F0")).Append('%');
			// P2 验证块:积分器输出速度 vs 渲染根速度(同帧、同为位移模长/dt,cm/ms)
			_sb.Append(" | P2: iSpd=").Append(p.WinISpeedMin == float.MaxValue ? "?" : p.WinISpeedMin.ToString("F2"))
				.Append('/').Append(p.WinISpeedMax.ToString("F2")).Append("cm/ms")
				.Append(" gSpdAbs=").Append(p.WinGSpdAbsMin == float.MaxValue ? "?" : p.WinGSpdAbsMin.ToString("F2"))
				.Append('/').Append(p.WinGSpdAbsMax.ToString("F2")).Append("cm/ms")
				.Append(" bSpdAbs=").Append(p.WinBSpdAbsMin == float.MaxValue ? "?" : p.WinBSpdAbsMin.ToString("F2"))
				.Append('/').Append(p.WinBSpdAbsMax.ToString("F2")).Append("cm/ms")
				.Append(" iVelMax=").Append(p.WinIVelMax.ToString("F2")).Append("cm/ms")
				.Append(" integVel=(").Append(rc.IntegVel.x.ToString("F2")).Append(',').Append(rc.IntegVel.y.ToString("F2")).Append(',').Append(rc.IntegVel.z.ToString("F2")).Append(')');
			_sb.Append(" relMax=").Append(p.WinRelMax.ToString("F1")).Append("cm")
				.Append(" relCoMMax=").Append(p.WinRelCoMMax.ToString("F1")).Append("cm")
				.Append(" gRelCamMax=").Append(p.WinGhostRelCamMax.ToString("F1")).Append("cm")
				.Append(" lRelCamMax=").Append(p.WinLocalRelCamMax.ToString("F1")).Append("cm")
				.Append(" | localInterp=[").Append(_lastLocalInterp).Append(']')
				.Append(" ghostInterp=[").Append(_lastGhostInterp).Append(']');
			MultiPlayerDiag.Log(_sb.ToString());
		}

		private static void ResetWindow(CraftProbe p)
		{
			p.WinFrames = 0;
			p.WinRootBack = 0; p.WinRootStall = 0; p.WinBody0Stall = 0; p.WinLocalStall = 0; p.WinLocalBack = 0;
			p.WinRootMax = 0f; p.WinBody0Max = 0f; p.WinLocalMax = 0f; p.WinLocalMin = float.MaxValue;
			p.WinLocalCoMMax = 0f; p.WinRelCoMMax = 0f;
			p.WinGhostRelCamMax = 0f; p.WinLocalRelCamMax = 0f; p.WinRelMax = 0f; p.WinCamMoveMax = 0f; p.WinCamRotMax = 0f;
			p.WinDriftRootMax = 0f; p.WinDriftBody0Max = 0f; p.WinDriftComMax = 0f; p.WinGameDeltaMax = 0f;
			p.WinStepsMax = 0; p.WinStepsMin = int.MaxValue; p.WinStepsSum = 0;
			p.WinDtMaxMs = 0f; p.WinDtMinMs = float.MaxValue; p.WinDtSumMs = 0f;
			p.WinISpeedMax = 0f; p.WinISpeedMin = float.MaxValue;
			p.WinGSpdAbsMax = 0f; p.WinGSpdAbsMin = float.MaxValue; p.WinIVelMax = 0f;
			p.WinBSpdAbsMax = 0f; p.WinBSpdAbsMin = float.MaxValue;
		}

		/// <summary>本机船 / 幽灵逐 body 插值状态串(判定"是否已开插值")。</summary>
		private static string InterpString(IReadOnlyList<BodyData> bodies)
		{
			if (bodies == null || bodies.Count == 0) return "?";
			StringBuilder sb = new StringBuilder(bodies.Count * 10);
			for (int i = 0; i < bodies.Count; i++)
			{
				if (i > 0) sb.Append(',');
				Rigidbody rb = bodies[i] != null && bodies[i].BodyScript != null ? bodies[i].BodyScript.RigidBody : null;
				sb.Append(rb != null ? rb.interpolation.ToString() : "?");
			}
			return sb.ToString();
		}

		/// <summary>插值状态变更事件日志(例如玩家按 Ctrl+Shift+I,或 P0 落地生效)。</summary>
		private void LogInterpChanges(FlightSceneScript scene, Dictionary<int, RemoteCraft> crafts)
		{
			try
			{
				string local = "-";
				if (scene.CraftNode != null && scene.CraftNode.CraftScript != null)
				{
					local = InterpString(scene.CraftNode.CraftScript.Data.Assembly.Bodies);
					if (local != _lastLocalInterp)
					{
						_lastLocalInterp = local;
						MultiPlayerDiag.Log("MultiPlayer rprobeInterp: local craft interpolation -> [" + local + "]");
					}
				}
				foreach (KeyValuePair<int, RemoteCraft> kv in crafts)
				{
					if (kv.Value == null || kv.Value.Node == null || kv.Value.Node.CraftScript == null) continue;
					string g = InterpString(kv.Value.Node.CraftScript.Data.Assembly.Bodies);
					if (g != _lastGhostInterp)
					{
						_lastGhostInterp = g;
						MultiPlayerDiag.Log("MultiPlayer rprobeInterp: ghost P" + kv.Key + " interpolation -> [" + g + "]");
					}
				}
			}
			catch { }
		}

		/// <summary>一次性输出环境量(每进程一次)。</summary>
		private void LogEnv(NetworkManager mgr, FlightSceneScript scene, Dictionary<int, RemoteCraft> crafts,
			bool hasCam, Transform localRoot, Transform localCoM, Transform localBody0)
		{
			try
			{
				_sb.Length = 0;
				_sb.Append("MultiPlayer rprobeEnv: fixedDt=").Append((Time.fixedDeltaTime * 1000f).ToString("F2")).Append("ms")
					.Append(" maxDt=").Append(Time.maximumDeltaTime.ToString("F3")).Append("s")
					.Append(" timeScale=").Append(Time.timeScale.ToString("F2"))
					.Append(" vSync=").Append(QualitySettings.vSyncCount)
					.Append(" targetFps=").Append(Application.targetFrameRate)
					.Append(" screen=").Append(Screen.width).Append('x').Append(Screen.height)
					.Append(" autoSyncTransforms=").Append(Physics.autoSyncTransforms ? 1 : 0)
					.Append(" captureFps=").Append(Time.captureFramerate);
				try
				{
					if (scene.ViewManager != null && scene.ViewManager.GameView != null && scene.ViewManager.GameView.ReferenceFrame != null)
						_sb.Append(" surfaceLocked=").Append(scene.ViewManager.GameView.ReferenceFrame.IsSurfaceLocked ? 1 : 0);
				}
				catch { }
				// 本机船:插值 / kinematic / CoM 与 body[0] 是否存在(观察者侧量化的两个候选基准)
				try
				{
					if (scene.CraftNode != null && scene.CraftNode.CraftScript != null)
					{
						IReadOnlyList<BodyData> lb = scene.CraftNode.CraftScript.Data.Assembly.Bodies;
						_sb.Append(" | local: isPhysics=").Append(scene.CraftNode.CraftScript.IsPhysicsEnabled ? 1 : 0)
							.Append(" bodies=").Append(lb.Count)
							.Append(" hasCoM=").Append(localCoM != null ? 1 : 0)
							.Append(" hasBody0=").Append(localBody0 != null ? 1 : 0)
							.Append(" interp=[").Append(InterpString(lb)).Append(']')
							.Append(" kin=[").Append(KinString(lb)).Append(']');
					}
				}
				catch { }
				foreach (KeyValuePair<int, RemoteCraft> kv in crafts)
				{
					try
					{
						RemoteCraft rc = kv.Value;
						if (rc.Node == null || rc.Node.CraftScript == null) continue;
						IReadOnlyList<BodyData> gb = rc.Node.CraftScript.Data.Assembly.Bodies;
						_sb.Append(" | ghost P").Append(kv.Key)
							.Append(" isPhysics=").Append(rc.Node.CraftScript.IsPhysicsEnabled ? 1 : 0)
							.Append(" bodies=").Append(gb.Count)
							.Append(" interp=[").Append(InterpString(gb)).Append(']')
							.Append(" kin=[").Append(KinString(gb)).Append(']');
						if (rc.HasApplied) _sb.Append(" vel=").Append(rc.LastApplied.Velocity.magnitude.ToString("F1")).Append("m/s");
					}
					catch { }
				}
				MultiPlayerDiag.Log(_sb.ToString());
			}
			catch (Exception e) { Mod.LogError("MultiPlayer rprobeEnv error: " + e.Message); }
		}

		private static string KinString(IReadOnlyList<BodyData> bodies)
		{
			if (bodies == null || bodies.Count == 0) return "?";
			StringBuilder sb = new StringBuilder(bodies.Count * 4);
			for (int i = 0; i < bodies.Count; i++)
			{
				if (i > 0) sb.Append(',');
				Rigidbody rb = bodies[i] != null && bodies[i].BodyScript != null ? bodies[i].BodyScript.RigidBody : null;
				sb.Append(rb != null ? (rb.isKinematic ? "K" : "D") : "?");
			}
			return sb.ToString();
		}
	}
}
