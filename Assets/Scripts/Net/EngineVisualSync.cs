using System;
using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts.Modifiers;
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
	/// 幽灵船引擎尾焰同步(液体尾焰 + 航发尾焰,见 plans/archive/engine-fx-sync-feasibility.md §3.5/§3.6)。
	/// - 发送端:按确定顺序(Data.Assembly.Parts → 每部件 modifiers)采样每引擎视觉 throttle;
	/// - 接收端:
	///   液体(EngineScript/RocketEngineScript):走 Route A —— 给 _engineCommon 设 ExhaustThrottleOverride,
	///   由游戏自身的 IFlightUpdate 每帧驱动尾焰(幽灵确实收 IFlightUpdate,已反编译定论);
	///   航发(JetEngineScript):Route B —— 其自身 IFlightFixedUpdate/IFlightUpdate 已被 Harmony patch 跳过,
	///   本类每帧驱动。航发可见尾焰 = 加力节流阀 ab(与发送端 _afterburnerThrottle 同式):
	///      ab = Clamp01((EngineThrottle - AfterburnerThrottleStart) / (1 - AfterburnerThrottleStart));
	///      非加力段(油门<AfterburnerThrottleStart)发送端本就无尾焰;加力段亮度 = ab。
	///      经 ExhaustThrottleOverride(=ComputeAfterburnerThrottle)→EngineCommon.FlightUpdate 驱动主喷嘴,
	///      再加力段最后一笔 UpdateExhaust(ab) 写入同一 ExhaustSystemScript;
	///      烟雾/膨胀比仍按同步 EngineThrottle 处理(ApplyJetSmokeVisuals/SyncJetExpansionRatio)。
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
			/// <summary>航发是否带加力(从幽灵自身 JetEngineData 读;双端同 XML)。</summary>
			public bool HasAfterburner;
			/// <summary>加力起始油门(默认 0.8;双端同 XML)。</summary>
			public float AfterburnerThrottleStart = 0.8f;
			/// <summary>航发是否发烟(数据 HasSmoke;发送端烟雾门控也用它)。</summary>
			public bool HasSmoke;
			/// <summary>烟雾基础速度(数据 SmokeSpeed,默认 1;SpeedOverride=0.75/1.0 × 它)。</summary>
			public float SmokeSpeed = 1f;
			/// <summary>数据是否自定义了烟雾 RGB(TryGetSmokeColor;否则加力用 _afterburnerSmokeColor,非加力用白)。</summary>
			public bool HasCustomSmokeColor;
			/// <summary>自定义烟雾 RGB(TryGetSmokeColor 的解析结果)。</summary>
			public Color CustomSmokeColor;
			/// <summary>加力烟雾色(_afterburnerSmokeColor,幽灵 FlightStart 已算)。</summary>
			public Color AfterburnerSmokeColor = Color.white;
			/// <summary>发动机膨胀比区间(数据 ExhaustExpansionRange.x/y;双端同 XML,默认(-1,-1)=不限制)。</summary>
			public Vector2 ExhaustExpansionRange = new Vector2(-1f, -1f);
			/// <summary>是否为液体火箭(RocketEngineScript);膨胀比用 sqrt(ExitPressure/压强) 公式。</summary>
			public bool IsRocket;
			/// <summary>火箭排气压强 ExitPressure(_params.Dynamic.ExitPressure,反射;双端同引擎,静态)。</summary>
			public double RocketExitPressure = 1f;
			/// <summary>火箭高度补偿 AltitudeCompensation(数据,静态)。</summary>
			public float RocketAltitudeCompensation;
			/// <summary>液体火箭的尾焰 ExhaustSystemScript(写 ExpansionRatio 用;仅 IsRocket,航发走 RocketExhaust)。</summary>
			public ExhaustSystemScript RocketExhaustSystem;
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
		private static FieldInfo _rocketExhaustSystemField;
		private static FieldInfo _afterburnerSmokeColorField;

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

		/// <summary>
		/// 航发加力烟雾颜色(FlightStart 里由 LOX/RP1 燃料色 + 本机 jet 燃料烟雾 alpha 组成,幽灵上已算好)。
		/// </summary>
		private static Color GetAfterburnerSmokeColor(JetEngineScript jet)
		{
			if (_afterburnerSmokeColorField == null)
			{
				_afterburnerSmokeColorField = typeof(JetEngineScript).GetField("_afterburnerSmokeColor",
					BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			}
			if (_afterburnerSmokeColorField == null) return Color.white;
			try { return (Color)_afterburnerSmokeColorField.GetValue(jet); }
			catch { return Color.white; }
		}

		private static FieldInfo _rocketParamsField;
		private static PropertyInfo _rocketParamsDynamicProp;
		private static PropertyInfo _rocketDynamicExitPressureProp;

		/// <summary>
		/// 反射取 RocketEngineScript._params.Dynamic.ExitPressure(设计期确定的排气压强,双端同引擎,静态)。
		/// 用于幽灵液体火箭膨胀比同步(RocketEngineScript.UpdateExhaustExpansionRatio 的同式)。
		/// 取不到时回退 1(约等于海平面普通喷管,不致命)。
		/// </summary>
		private static double GetRocketExitPressure(RocketEngineScript res)
		{
			try
			{
				if (_rocketParamsField == null)
				{
					_rocketParamsField = typeof(RocketEngineScript).GetField("_params",
						BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				}
				if (_rocketParamsField == null) return 1f;
				object p = _rocketParamsField.GetValue(res);
				if (p == null) return 1f;
				if (_rocketParamsDynamicProp == null) _rocketParamsDynamicProp = p.GetType().GetProperty("Dynamic");
				if (_rocketParamsDynamicProp == null) return 1f;
				object dp = _rocketParamsDynamicProp.GetValue(p);
				if (dp == null) return 1f;
				if (_rocketDynamicExitPressureProp == null) _rocketDynamicExitPressureProp = dp.GetType().GetProperty("ExitPressure");
				if (_rocketDynamicExitPressureProp == null) return 1f;
				return (double)_rocketDynamicExitPressureProp.GetValue(dp);
			}
			catch { return 1f; }
		}

		// ---------------- 发送端采样 ----------------

		/// <summary>
		/// 按确定顺序采样本机飞船每台引擎的视觉 throttle:
		/// 液体=_engineCommon.EngineThrottle;航发=_engineCommon.EngineThrottle。
		/// 航发的可见尾焰由接收端从该值推导加力节流阀 ab(用幽灵自身 JetEngineData:
		/// afterburnerThrottleStart/hasAfterburner,双端同 XML) —— 尾焰 = ab 单段,见 ComputeAfterburnerThrottle。
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
							// 航发:同步非加力段 EngineThrottle;加力段接收端本地推导(见类注释)
							EngineCommon ec = GetEngineCommon(mod);
							list.Add(ec != null ? ec.EngineThrottle : 0f);
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
							JetEngineData jd = jet.Data;
							Color customSmoke=new Color();
							bool hasCustomSmoke = jd != null && jd.TryGetSmokeColor(out customSmoke);
							if (ec != null)
							{
								// 替换构造函数里 () => _afterburnerThrottle 的闭包,改用"从同步 EngineThrottle 推导的加力节流阀 ab"(ComputeAfterburnerThrottle):
								// 发送端航发可见尾焰只由 _afterburnerThrottle 决定;普通节流阀 EngineThrottle 只用于烟雾门控(ApplyJetSmokeVisuals)。
								// 航发自身 FlightUpdate 被 patch 跳过,由 DriveGhostEngineVisuals 调用 ec.FlightUpdate。
								EngineCommon captured = ec;
								MpNetworkManager.RemoteCraft capturedRc = rc;
								int capturedIdx = idx;
								bool hasAb = jd != null && jd.HasAfterburner;
								float abStart = jd != null ? jd.AfterburnerThrottleStart : 0.8f;
								captured.ExhaustThrottleOverride = () => ComputeAfterburnerThrottle(capturedRc, capturedIdx, hasAb, abStart);
							}
							drivers.Add(new EngineVisualDriver
							{
								EngineCommon = ec,
								ThrottleIndex = idx,
								RocketExhaust = GetRocketExhaust(jet),
								DriveDirectly = true,
								HasAfterburner = jd != null && jd.HasAfterburner,
								AfterburnerThrottleStart = jd != null ? jd.AfterburnerThrottleStart : 0.8f,
								HasSmoke = jd != null && jd.HasSmoke,
								SmokeSpeed = jd != null ? jd.SmokeSpeed : 1f,
								HasCustomSmokeColor = hasCustomSmoke,
								CustomSmokeColor = customSmoke,
								AfterburnerSmokeColor = GetAfterburnerSmokeColor(jet),
								ExhaustExpansionRange = jd != null ? jd.ExhaustExpansionRange : new Vector2(-1f, -1f)
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
							RocketEngineScript res = (RocketEngineScript)mod;
							RocketEngineData rd = res.Data;
							ExhaustSystemScript resEx = part.PartScript.GameObject.GetComponentInChildren<ExhaustSystemScript>(true);
							// 过膨胀(膨胀比)同步·主机制:幽灵液体火箭的 RocketEngineScript.FlightUpdate 里
							// UpdateExhaustExpansionRatio 被 Data.Activated 门控(幽灵上 false → 永不跑 → 膨胀比冻结)。
							// 把幽灵引擎置为 Activated:物理已禁用,EngineCommon.OnActivated 不会真正点火/耗燃料/推力,
							// 仅让游戏自身 FlightUpdate 每帧按幽灵大气压计算膨胀比(真实 _params.Dynamic.ExitPressure)。
							try
							{
								if (part.PartScript != null && part.PartScript.Data != null && !part.PartScript.Data.Activated)
								{
									part.PartScript.Data.Activated = true;
								}
							}
							catch { }
							double exitP = GetRocketExitPressure(res);
							drivers.Add(new EngineVisualDriver
							{
								EngineCommon = ec,
								ThrottleIndex = idx,
								DriveDirectly = true,
								IsRocket = true,
								RocketExitPressure = exitP,
								RocketAltitudeCompensation = rd != null ? rd.AltitudeCompensation : 0f,
								ExhaustExpansionRange = rd != null ? rd.ExhaustExpansionRange : new Vector2(-1f, -1f),
								RocketExhaustSystem = resEx
							});
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
					// 过膨胀(膨胀比)同步:幽灵引擎的 ExpansionRatio 更新路径全部失效——
					//   航发:JetEngineGhostPatch 跳过 FlightFixedUpdate/FlightUpdate(JetEngineScript 的膨胀比在此算);
					//   液体火箭:RocketEngineScript.FlightUpdate 被 Data.Activated 门控,幽灵上不跑。
					// 用幽灵自身大气压按发送端同式补算写入,须在 EngineCommon.FlightUpdate(驱动主喷嘴)
					// 与 UpdateExhaust(加力段)之前,两者才能读到新值。熄火(t==0)时尾焰隐藏,无需更新。
					if (t > 0f)
					{
						if (d.RocketExhaust != null) SyncJetExpansionRatio(rc, d);
						else if (d.IsRocket && d.RocketExhaustSystem != null) SyncRocketExpansionRatio(rc, d);
					}
					if (d.DriveDirectly)
					{
						// RocketEngine(游戏门控不调)/航发(已 patch 跳过):由 MP 层每帧调 FlightUpdate 驱动主喷嘴火焰。
						// 航发时 override=推导的加力节流阀 ab(ComputeAfterburnerThrottle)→ 主喷嘴 exhaust 走加力值;烟雾门控仍由 ApplyJetSmokeVisuals 按同步 EngineThrottle 重写。
						d.EngineCommon.FlightUpdate(1f, 1f);
					}
					if (d.RocketExhaust != null)
					{
						// 航发加力大尾焰:与主喷嘴是同一个 ExhaustSystemScript(Nozzle/ExhaustSystem),
						// 发送端可见尾焰 = _afterburnerThrottle(加力节流阀),见 JetEngineScript.OnModifiersCreated 的
						// ExhaustThrottleOverride = () => _afterburnerThrottle —— 非加力段(油门<AfterburnerThrottleStart)发送端本就无尾焰,
						// 且加力段亮度 = ab,不是 t 与 ab 的 Lerp。这里只写 ab,不能把普通节流阀 t 一起算(加力/普通节流阀绑定错误)。
						float ab = ComputeAfterburnerThrottle(rc, d.ThrottleIndex, d.HasAfterburner, d.AfterburnerThrottleStart);
						d.RocketExhaust.UpdateExhaust(ab);
						// 航发烟雾颜色 / SpeedOverride(发送端 JetEngineScript.FlightUpdate 里做,幽灵已被 patch 跳过):
						// 复制其公式 —— 加力段用加力烟色 + 1.0×SmokeSpeed,非加力段用近白低透明 + 0.75×SmokeSpeed;
						// 有自定义烟色时 RGB 取自定义值。EmissionEnabled/Throttle 也按发送端口径重写(含 HasSmoke 门控)。
						ApplyJetSmokeVisuals(d, t);
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
		/// 幽灵航发尾焰膨胀比(过膨胀)同步:复制发送端 JetEngineScript.FlightFixedUpdate 里的计算
		/// (81060.0012 / max(1, AirPressure) → clamp 到 [ExhaustExpansionRange.x, MaxExpansionRatio])。
		/// 原因:幽灵航发 FlightFixedUpdate/FlightUpdate 被 JetEngineGhostPatch 跳过,ExpansionRatio 永不更新
		/// → 高空该膨胀不膨胀、地面该收窄不收窄,尾焰形状与发送端不一致。MaxExpansionRatio 已由幽灵
		/// FlightStart 的 ApplyNozzleExhaustSettings 算好;AirPressure 由 CraftFlightData 按当前位置刷新(正确)。
		/// 只读不写其他字段,幂等;仅 tail 活跃(油门&gt;0)时由 DriveGhostEngineVisuals 调用。
		/// </summary>
		private static void SyncJetExpansionRatio(MpNetworkManager.RemoteCraft rc, EngineVisualDriver d)
		{
			ExhaustSystemScript ex = d.RocketExhaust;
			if (ex == null || rc == null || rc.Node == null || rc.Node.CraftScript == null) return;
			if (ex.MaxExpansionRatio <= 0f) return;
			float pressure = 0f;
			try
			{
				// AtmosphereSample 是 ModApi.Planet 的结构体(非引用类型,不能判空);
				// _flightData 未就绪时访问会抛 NRE,故用 try/catch 兜底为 0。
				pressure = rc.Node.CraftScript.AtmosphereSample.AirPressure;
			}
			catch { pressure = 0f; }
			float expansion = (float)(81060.00120788813 / Math.Max(1.0, (double)pressure));
			expansion = Mathf.Min(expansion, ex.MaxExpansionRatio);
			if (d.ExhaustExpansionRange.x > expansion) expansion = d.ExhaustExpansionRange.x;
			expansion = Mathf.Min(expansion, ex.MaxExpansionRatio);
			ex.ExpansionRatio = expansion;
		}

		/// <summary>
		/// 幽灵液体火箭尾焰膨胀比(过膨胀)同步:复制发送端 RocketEngineScript.UpdateExhaustExpansionRatio 的计算
		/// (sqrt(ExitPressure / max(pressure,15)) × (1 - 0.85×AltitudeCompensation) → clamp [ExhaustExpansionRange.x, .y])。
		/// 原因:该更新在 RocketEngineScript.FlightUpdate 里被 Data.Activated 门控,幽灵上 Data.Activated=false → 永不更新。
		/// 所需参数(RocketExitPressure / RocketAltitudeCompensation / ExhaustExpansionRange)已在 Setup 时缓存(双端同引擎)。
		/// 只写 ExpansionRatio,幂等;由 DriveGhostEngineVisuals 在 EngineCommon.FlightUpdate 之前调用。
		/// </summary>
		private static void SyncRocketExpansionRatio(MpNetworkManager.RemoteCraft rc, EngineVisualDriver d)
		{
			ExhaustSystemScript ex = d.RocketExhaustSystem;
			if (ex == null || rc == null || rc.Node == null || rc.Node.CraftScript == null) return;
			float pressure = 0f;
			try { pressure = rc.Node.CraftScript.AtmosphereSample.AirPressure; } catch { pressure = 0f; }
			if (pressure < 15f) pressure = 15f;
			float expansion = (float)(Math.Sqrt(d.RocketExitPressure / (double)pressure) * (1.0 - 0.85 * (double)d.RocketAltitudeCompensation));
			if (d.ExhaustExpansionRange.x > expansion) expansion = d.ExhaustExpansionRange.x;
			if (d.ExhaustExpansionRange.y > 0f && d.ExhaustExpansionRange.y < expansion) expansion = d.ExhaustExpansionRange.y;
			ex.ExpansionRatio = expansion;
		}

		/// <summary>
		/// 复制发送端航发烟雾视觉(见 JetEngineScript.FlightUpdate 485-507):
		/// - 加力段(_afterburnerThrottle>0):烟色=AfterburnerSmokeColor,速度=1.0×SmokeSpeed;
		/// - 非加力段:近白 RGB、alpha=0.1×throttle,速度=0.75×SmokeSpeed;
		/// - 数据自定义烟色(HasCustomSmokeColor)时 RGB 用自定义值,alpha 保持上面的计算值;
		/// - EmissionEnabled=HasSmoke && throttle>0,Throttle=throttle。
		/// 发送端在 jet 自身 FlightUpdate 里设,幽灵已被 Harmony patch 跳过 → 由本方法每帧补上。
		/// </summary>
		private static void ApplyJetSmokeVisuals(EngineVisualDriver d, float throttle)
		{
			if (d == null || d.EngineCommon == null) return;
			EngineNozzleScript[] nozzles = d.EngineCommon.Nozzles;
			if (nozzles == null || nozzles.Length == 0) return;

			float ab = d.HasAfterburner ? Mathf.Clamp01((throttle - d.AfterburnerThrottleStart) / (1f - d.AfterburnerThrottleStart)) : 0f;
			bool afterburner = ab > 0f;
			Color color = afterburner ? d.AfterburnerSmokeColor : new Color(1f, 1f, 1f, 0.1f * throttle);
			if (d.HasCustomSmokeColor)
			{
				color = new Color(d.CustomSmokeColor.r, d.CustomSmokeColor.g, d.CustomSmokeColor.b, color.a);
			}
			float speed = (afterburner ? 1f : 0.75f) * d.SmokeSpeed;
			bool enabled = d.HasSmoke && throttle > 0f;

			for (int i = 0; i < nozzles.Length; i++)
			{
				if (nozzles[i] == null) continue;
				SmokeTrailScript st = nozzles[i].SmokeTrail;
				if (st == null) continue;
				st.EmissionEnabled = enabled;
				st.Throttle = throttle;
				st.Color = color;
				st.SpeedOverride = speed;
			}
		}

		/// <summary>
		/// 幽灵船烟雾同步(见 plans/archive/engine-fx-sync-feasibility.md §10):
		/// 把同步速度注入 kinematic 刚体,并从"最近两次应用的帧空间朝向之差"算角速度注入。
		/// - SmokeTrailScript 的拖尾读 rigidbody.velocity(反编译定论:不用传入的 surfaceVelocity 参数),
		///   幽灵 kinematic 刚体 velocity≈0 → 必须注入,否则烟雾原地堆积成一坨;
		/// - 角速度供 _smoothedCraftVelocity 的 Cross(angularVelocity, offset) 项(翻滚烟迹)。
		/// 调用点约定:在 ApplyRemoteState 里、写 rc.LastAppliedHeading 之前调用,
		/// 此时 LastAppliedHeading 仍是"上一次应用"的朝向,用于算旋转增量。
		/// kinematic 刚体的 velocity/angularVelocity 只作数据,Unity 不积分进位置,无物理副作用;
		/// 幽灵摆放走 GroundedSurface* + SetStateVectors,不读 rigidbody.velocity,不会双重移动。
		/// 注意(2026-08 修日志刷屏):Unity 对 kinematic 刚体写 velocity/angularVelocity 每次都会打
		/// "Setting linear velocity of a kinematic body is not supported." 告警(§10 的"无副作用"遗漏了日志面),
		/// 幽灵全 kinematic + 每帧每 body 写一次 → 单会话刷出 ~1.3M 条。修法:只在值实际变化时写 +
		/// 写入时临时切回非 kinematic 再写回(调用点在 Update、物理步在帧末,刚体不会被真正积分,
		/// velocity 数据照常存储,SmokeTrailScript 读 rigidbody.velocity 不受影响)。
		/// </summary>
		public static void InjectGhostMotion(MpNetworkManager.RemoteCraft rc, Mod.RemoteDataPack data, IPlanetNode planet, IReferenceFrame frame, Quaternion headingFrame)
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

			// 只在值实际变化时写入(线性阈值 ~0.1 m/s,角速度 ~0.03 rad/s):
			// 与下文"临时切回非 kinematic"共同消除 Unity kinematic velocity 告警刷屏。
			bool velChanged = rc.LastInjectedVelocity == null ||
				Vector3.SqrMagnitude(frameVel - rc.LastInjectedVelocity.Value) > 0.01f;
			bool angChanged = rc.LastInjectedAngularVelocity == null ||
				Vector3.SqrMagnitude(angularVel - rc.LastInjectedAngularVelocity.Value) > 0.001f;
			bool needWrite = velChanged || (angChanged && angularVel != Vector3.zero);
			if (!needWrite) return;

			IReadOnlyList<BodyData> bodies = rc.Node.CraftScript.Data.Assembly.Bodies;
			for (int i = 0; i < bodies.Count; i++)
			{
				BodyData b = bodies[i];
				if (b == null || b.BodyScript == null || b.BodyScript.RigidBody == null) continue;
				Rigidbody rb = b.BodyScript.RigidBody;
				if (!rb.isKinematic) continue; // 只写 kinematic(幽灵全 kinematic;真实/他人飞船不碰)
				rb.isKinematic = false;
				if (velChanged) rb.velocity = frameVel;
				if (angChanged && angularVel != Vector3.zero) rb.angularVelocity = angularVel;
				rb.isKinematic = true;
			}
			rc.LastInjectedVelocity = frameVel;
			rc.LastInjectedAngularVelocity = angularVel;
		}

		internal static float GetSyncedThrottle(MpNetworkManager.RemoteCraft rc, int idx)
		{
			if (rc == null || rc.SyncedThrottles == null || idx < 0 || idx >= rc.SyncedThrottles.Count) return 0f;
			return rc.SyncedThrottles[idx];
		}

		/// <summary>
		/// 与发送端 JetEngineScript._afterburnerThrottle 同式推导(UpdatePerformance):
		/// ab = Clamp01((EngineThrottle - AfterburnerThrottleStart) / (1 - AfterburnerThrottleStart));
		/// 无加力(HasAfterburner=false)时恒 0 —— 发送端非加力航发可见尾焰本就是 0(尾焰=加力火焰)。
		/// 注意:发送端 JetEngineScript.OnModifiersCreated 的 ExhaustThrottleOverride = () => _afterburnerThrottle,
		/// 即航发可见尾焰只由加力节流阀决定(普通节流阀 EngineThrottle 只用于烟雾门控/ApplyJetSmokeVisuals),
		/// 因此幽灵必须绑定 ab,不能把普通节流阀 t 一起 Lerp 进去(旧实现 = 加力/普通节流阀绑定错误)。
		/// </summary>
		private static float ComputeAfterburnerThrottle(MpNetworkManager.RemoteCraft rc, int idx, bool hasAfterburner, float afterburnerThrottleStart)
		{
			if (!hasAfterburner) return 0f;
			if (afterburnerThrottleStart >= 1f) return 0f;
			float t = GetSyncedThrottle(rc, idx);
			return Mathf.Clamp01((t - afterburnerThrottleStart) / (1f - afterburnerThrottleStart));
		}
	}
}
