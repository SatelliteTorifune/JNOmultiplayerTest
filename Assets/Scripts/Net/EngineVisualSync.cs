using System;
using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts.Modifiers.Propulsion;
using Assets.Scripts.Flight.Sim;
using ModApi.Craft;
using ModApi.Craft.Parts;
using ModApi.Flight.GameView;
using ModApi.Flight.Sim;
using UnityEngine;

namespace Assets.Scripts.Net
{
	/// <summary>
	/// 幽灵船引擎尾焰同步(先做液体尾焰 + 航发加力尾焰,见 plans/engine-fx-sync-feasibility.md §3.5/§3.6)。
	/// - 发送端:按确定顺序(Data.Assembly.Parts → 每部件 modifiers)采样每引擎视觉 throttle;
	/// - 接收端:
	///   液体(EngineScript/RocketEngineScript):走 Route A —— 给 _engineCommon 设 ExhaustThrottleOverride,
	///   由游戏自身的 IFlightUpdate 每帧驱动尾焰(幽灵确实收 IFlightUpdate,已反编译定论);
	///   航发(JetEngineScript):Route B —— 其自身 IFlightFixedUpdate/IFlightUpdate 已被 Harmony patch 跳过,
	///   本类每帧直接驱动 _rocketExhaustSystem(加力大尾焰)与 EngineCommon.FlightUpdate(主喷嘴火焰/烟雾)。
	/// 顺序契约:发送/接收两端用同一枚举顺序,index 一一对应(同 XML 构建,parts 顺序一致)。
	/// </summary>
	public static class EngineVisualSync
	{
		/// <summary>单台引擎的幽灵驱动信息。</summary>
		public sealed class EngineVisualDriver
		{
			/// <summary>引擎公共逻辑(非 null;override 载体 + FlightUpdate 载体)。</summary>
			public EngineCommon EngineCommon;
			/// <summary>在 rc.SyncedThrottles 中的索引(与发送端枚举顺序一致)。</summary>
			public int ThrottleIndex;
			/// <summary>航发加力大尾焰(仅 jet;液体为 null)。</summary>
			public ExhaustSystemScript RocketExhaust;
			/// <summary>
			/// 是否由 MP 层每帧直接调 EngineCommon.FlightUpdate(1f,1f) 驱动:
			/// - EngineScript:false —— 游戏自身 IFlightUpdate 每帧无条件调,走 Route A,MP 层不重复调(避免纹理滚动 2x);
			/// - RocketEngineScript:true —— 其 IFlightUpdate 被 (Activated&&throttle>0)||_hasBeenActivated 门控,
			///   幽灵上 throttle=0 游戏不调,必须由 MP 层驱动;
			/// - JetEngineScript:true —— 其 IFlightUpdate 已被 Harmony patch 跳过。
			/// </summary>
			public bool DriveDirectly;
			/// <summary>单台驱动异常已记录(避免每帧刷日志)。</summary>
			public bool ErrorLogged;
		}

		// ---------------- 反射访问器(缓存) ----------------

		private static readonly Dictionary<Type, FieldInfo> _engineCommonFields = new Dictionary<Type, FieldInfo>();
		private static FieldInfo _afterburnerThrottleField;
		private static FieldInfo _rocketExhaustSystemField;

		private static FieldInfo GetEngineCommonField(Type modifierType)
		{
			FieldInfo f;
			if (!_engineCommonFields.TryGetValue(modifierType, out f))
			{
				f = modifierType.GetField("_engineCommon", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				_engineCommonFields[modifierType] = f;
			}
			return f;
		}

		internal static EngineCommon GetEngineCommon(PartModifierScript mod)
		{
			if (mod == null) return null;
			FieldInfo f = GetEngineCommonField(mod.GetType());
			return f != null ? f.GetValue(mod) as EngineCommon : null;
		}

		private static float GetAfterburnerThrottle(JetEngineScript jet)
		{
			if (_afterburnerThrottleField == null)
			{
				_afterburnerThrottleField = typeof(JetEngineScript).GetField("_afterburnerThrottle",
					BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			}
			if (_afterburnerThrottleField == null) return 0f;
			try { return (float)_afterburnerThrottleField.GetValue(jet); }
			catch { return 0f; }
		}

		private static ExhaustSystemScript GetRocketExhaust(JetEngineScript jet)
		{
			if (_rocketExhaustSystemField == null)
			{
				_rocketExhaustSystemField = typeof(JetEngineScript).GetField("_rocketExhaustSystem",
					BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			}
			if (_rocketExhaustSystemField == null) return null;
			try { return _rocketExhaustSystemField.GetValue(jet) as ExhaustSystemScript; }
			catch { return null; }
		}

		// ---------------- 发送端采样 ----------------

		/// <summary>
		/// 按确定顺序采样本机飞船每台引擎的视觉 throttle:
		/// 液体=_engineCommon.EngineThrottle;航发=_afterburnerThrottle(加力尾焰驱动值)。
		/// </summary>
		public static List<float> SampleEngineThrottles(CraftNode craft)
		{
			List<float> list = new List<float>();
			if (craft == null || craft.CraftScript == null || craft.CraftScript.Data == null || craft.CraftScript.Data.Assembly == null)
			{
				return list;
			}
			try
			{
				IReadOnlyList<PartData> parts = craft.CraftScript.Data.Assembly.Parts;
				for (int pi = 0; pi < parts.Count; pi++)
				{
					PartData part = parts[pi];
					if (part == null || part.PartScript == null) continue;
					foreach (PartModifierScript mod in part.PartScript.Modifiers)
					{
						if (mod is JetEngineScript)
						{
							list.Add(GetAfterburnerThrottle((JetEngineScript)mod));
						}
						else if (mod is EngineScript || mod is RocketEngineScript)
						{
							EngineCommon ec = GetEngineCommon(mod);
							list.Add(ec != null ? ec.EngineThrottle : 0f);
						}
					}
				}
			}
			catch (Exception e)
			{
				Mod.LogError("EngineVisualSync.SampleEngineThrottles error: " + e.Message);
			}
			return list;
		}

		// ---------------- 接收端:幽灵船设置与驱动 ----------------

		/// <summary>
		/// 为幽灵船建立引擎驱动表(与发送端同枚举顺序)并挂上 Route A override;
		/// 同时禁用 ExhaustDamageScript(防地形尘/加热)。烟雾不在此禁用——拖尾由
		/// InjectGhostMotion 注入的 rigidbody.velocity 驱动(见 plans §10)。
		/// 幂等:已建立则跳过。CraftScript 未就绪时返回 false,调用方下一帧重试。
		/// </summary>
		public static bool SetupGhostEngineVisuals(MpNetworkManager.RemoteCraft rc)
		{
			if (rc == null || rc.Node == null || rc.Node.CraftScript == null) return false;
			if (rc.EngineDrivers != null) return true; // 已建立

			List<EngineVisualDriver> drivers = new List<EngineVisualDriver>();
			int idx = 0;
			try
			{
				IReadOnlyList<PartData> parts = rc.Node.CraftScript.Data.Assembly.Parts;
				for (int pi = 0; pi < parts.Count; pi++)
				{
					PartData part = parts[pi];
					if (part == null || part.PartScript == null) continue;

					// 幽灵船的 ExhaustDamageScript 仍会跑 FixedUpdate 发地形尘/加热,禁用掉
					// (烟雾不在此禁用:烟雾拖尾由 InjectGhostMotion 注入的 rigidbody.velocity 驱动,见 plans §10)
					foreach (ExhaustDamageScript eds in part.PartScript.GameObject.GetComponentsInChildren<ExhaustDamageScript>(true))
					{
						eds.enabled = false;
					}

					foreach (PartModifierScript mod in part.PartScript.Modifiers)
					{
						// 注意:idx 只在"引擎 modifier"分支自增,与发送端 SampleEngineThrottles 的
						// 列表索引(只计引擎)严格对齐 —— 部件常含非引擎 modifier(结构/油箱等),不能按所有 modifier 计数。
						if (mod is JetEngineScript)
						{
							JetEngineScript jet = (JetEngineScript)mod;
							EngineCommon ec = GetEngineCommon(jet);
							if (ec != null)
							{
								// 替换构造函数里 () => _afterburnerThrottle 的闭包,改用同步值:
								// 航发自身 FlightUpdate 被 patch 跳过,由 DriveGhostEngineVisuals 调用 ec.FlightUpdate。
								EngineCommon captured = ec;
								MpNetworkManager.RemoteCraft capturedRc = rc;
								int capturedIdx = idx;
								captured.ExhaustThrottleOverride = () => GetSyncedThrottle(capturedRc, capturedIdx);
							}
							drivers.Add(new EngineVisualDriver
							{
								EngineCommon = ec,
								ThrottleIndex = idx,
								RocketExhaust = GetRocketExhaust(jet),
								DriveDirectly = true
							});
							idx++;
						}
						else if (mod is RocketEngineScript)
						{
							EngineCommon ec = GetEngineCommon(mod);
							if (ec != null)
							{
								EngineCommon captured = ec;
								MpNetworkManager.RemoteCraft capturedRc = rc;
								int capturedIdx = idx;
								captured.ExhaustThrottleOverride = () => GetSyncedThrottle(capturedRc, capturedIdx);
							}
							// RocketEngine:游戏 IFlightUpdate 被 throttle>0 门控,幽灵上不调 → 由 MP 层直接驱动
							drivers.Add(new EngineVisualDriver { EngineCommon = ec, ThrottleIndex = idx, DriveDirectly = true });
							idx++;
						}
						else if (mod is EngineScript)
						{
							EngineCommon ec = GetEngineCommon(mod);
							if (ec != null)
							{
								// Route A:游戏自身 IFlightUpdate 每帧无条件调 FlightUpdate,经此 override 驱动尾焰
								EngineCommon captured = ec;
								MpNetworkManager.RemoteCraft capturedRc = rc;
								int capturedIdx = idx;
								captured.ExhaustThrottleOverride = () => GetSyncedThrottle(capturedRc, capturedIdx);
							}
							drivers.Add(new EngineVisualDriver { EngineCommon = ec, ThrottleIndex = idx, DriveDirectly = false });
							idx++;
						}
					}
				}
			}
			catch (Exception e)
			{
				Mod.LogError("EngineVisualSync.SetupGhostEngineVisuals error: " + e.Message);
			}
			rc.EngineDrivers = drivers;
			return true;
		}

		/// <summary>每帧驱动幽灵船引擎尾焰(用最近应用状态的每引擎 throttle)。</summary>
		public static void DriveGhostEngineVisuals(MpNetworkManager.RemoteCraft rc)
		{
			if (rc == null) return;
			if (rc.EngineDrivers == null)
			{
				// CraftScript 延迟构建/重建后补一次设置
				if (!SetupGhostEngineVisuals(rc)) return;
			}
			for (int i = 0; i < rc.EngineDrivers.Count; i++)
			{
				EngineVisualDriver d = rc.EngineDrivers[i];
				if (d == null || d.EngineCommon == null) continue;
				try
				{
					float t = GetSyncedThrottle(rc, d.ThrottleIndex);
					if (d.RocketExhaust != null)
					{
						// 航发加力大尾焰
						d.RocketExhaust.UpdateExhaust(t);
					}
					if (d.DriveDirectly)
					{
						// RocketEngine(游戏门控不调)/航发(已 patch 跳过):由 MP 层每帧调 FlightUpdate 驱动主喷嘴火焰
						d.EngineCommon.FlightUpdate(1f, 1f);
					}
					// EngineScript:Route A —— 游戏自身 FlightUpdate 每帧经 override 驱动,MP 层不重复调
				}
				catch (Exception e)
				{
					if (!d.ErrorLogged)
					{
						d.ErrorLogged = true;
						Mod.LogError("EngineVisualSync.DriveGhostEngineVisuals error (P" + rc.PlayerId + "): " + e.Message);
					}
				}
			}
		}

		/// <summary>
		/// 幽灵船烟雾同步(见 plans/engine-fx-sync-feasibility.md §10):
		/// 把同步速度注入 kinematic 刚体,并从"最近两次应用的帧空间朝向之差"算角速度注入。
		/// - SmokeTrailScript 的拖尾读 rigidbody.velocity(反编译定论:不用传入的 surfaceVelocity 参数),
		///   幽灵 kinematic 刚体 velocity≈0 → 必须注入,否则烟雾原地堆积成一坨;
		/// - 角速度供 _smoothedCraftVelocity 的 Cross(angularVelocity, offset) 项(翻滚烟迹)。
		/// 调用点约定:在 ApplyRemoteState 里、写 rc.LastAppliedHeading 之前调用,
		/// 此时 LastAppliedHeading 仍是"上一次应用"的朝向,用于算旋转增量。
		/// kinematic 刚体的 velocity/angularVelocity 只作数据,Unity 不积分进位置,无物理副作用;
		/// 幽灵摆放走 GroundedSurface* + SetStateVectors,不读 rigidbody.velocity,不会双重移动。
		/// </summary>
		public static void InjectGhostMotion(MpNetworkManager.RemoteCraft rc, Mod.remoteDataPack data, IPlanetNode planet, IReferenceFrame frame, Quaternion headingFrame)
		{
			if (rc == null || rc.Node == null || rc.Node.CraftScript == null || planet == null ) return;

			// 线性速度:表面坐标 → 行星空间 → 接收端帧相对速度(与发送端 rigidbody.velocity 同语义)
			Vector3d planetVel = planet.SurfaceVectorToPlanetVector(data.Velocity);
			Vector3 frameVel = frame != null ? frame.PlanetToFrameVelocity(planetVel) : (Vector3)planetVel;

			// 角速度:本次 vs 上次应用的帧空间朝向之差(短路径轴角/帧时长);首次或朝向未变则为 0
			Vector3 angularVel = Vector3.zero;
			if (rc.HasApplied)
			{
				Quaternion prev = rc.LastAppliedHeading;
				float deg = Quaternion.Angle(prev, headingFrame);
				if (deg > 0.001f)
				{
					Vector3 axis;
					Quaternion delta = headingFrame * Quaternion.Inverse(prev);
					delta.ToAngleAxis(out deg, out axis);
					float dt = Time.deltaTime;
					if (dt > 0f && deg > 0.001f)
					{
						angularVel = axis * (deg * Mathf.Deg2Rad / dt);
					}
				}
			}

			IReadOnlyList<BodyData> bodies = rc.Node.CraftScript.Data.Assembly.Bodies;
			for (int i = 0; i < bodies.Count; i++)
			{
				BodyData b = bodies[i];
				if (b == null || b.BodyScript == null || b.BodyScript.RigidBody == null) continue;
				Rigidbody rb = b.BodyScript.RigidBody;
				if (!rb.isKinematic) continue; // 只写 kinematic(幽灵全 kinematic;真实/他人飞船不碰)
				rb.velocity = frameVel;
				if (angularVel != Vector3.zero) rb.angularVelocity = angularVel;
			}
		}

		internal static float GetSyncedThrottle(MpNetworkManager.RemoteCraft rc, int idx)
		{
			if (rc == null || rc.SyncedThrottles == null || idx < 0 || idx >= rc.SyncedThrottles.Count) return 0f;
			return rc.SyncedThrottles[idx];
		}
	}
}
