using System;
using System.Reflection;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts.Modifiers.Propulsion;
using Assets.Scripts.Flight.Sim;
using Assets.Scripts.Net;
using HarmonyLib;
using ModApi.GameLoop.Interfaces;

namespace Assets.Scripts
{
	/// <summary>
	/// 幽灵(远程)航发:跳过 JetEngineScript 自身的 IFlightFixedUpdate / IFlightUpdate。
	/// 反编译定论(plans/archive/engine-fx-sync-feasibility.md §3.5):幽灵引擎 modifier 确实收到游戏飞行循环回调;
	/// 对液体这正好让 Route A(ExhaustThrottleOverride)生效,但对航发是个坑 —— 其 FlightFixedUpdate 每 FixedUpdate
	/// 会把 _afterburnerThrottle 归零并调 _rocketExhaustSystem.UpdateExhaust(0) 反打(§3.6),故必须跳过,
	/// 尾焰改由 EngineVisualSync.DriveGhostEngineVisuals 用同步值直接驱动。
	///
	/// 应用方式:不用 [HarmonyPatch] 自动发现,而是由 Mod.OnModInitialized 显式调用 Apply() 手动打补丁
	/// —— 目标方法找不到时静默跳过(液体尾焰仍可用),不会像 [HarmonyPatch]+TargetMethod 那样
	/// 返回 null 直接抛 HarmonyException 打断整个 mod 初始化(实测报错,见 plans §9.2 第 6 条)。
	/// Prefix 只对"幽灵飞船"返回 false(见 MpNetworkManager.IsRemoteCraftNode),本机/他人真船不受影响。
	/// </summary>
	public static class JetEngineGhostPatch
	{
		/// <summary>由 Mod.OnModInitialized 在 PatchAll() 之后调用。</summary>
		public static void Apply(Harmony harmony)
		{
			if (harmony == null) return;

			MethodInfo prefix = typeof(JetEngineGhostPatch).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.Public);
			if (prefix == null) return;

			MethodInfo fixedUpdate = FindInterfaceMethod(typeof(IFlightFixedUpdate), "FlightFixedUpdate");
			if (fixedUpdate != null)
			{
				harmony.Patch(fixedUpdate, prefix: new HarmonyMethod(prefix));
			}

			MethodInfo update = FindInterfaceMethod(typeof(IFlightUpdate), "FlightUpdate");
			if (update != null)
			{
				harmony.Patch(update, prefix: new HarmonyMethod(prefix));
			}
		}

		/// <summary>Prefix:幽灵航发返回 false 跳过原方法;真实/他人飞船返回 true 放行。</summary>
		public static bool Prefix(JetEngineScript __instance)
		{
			return !IsGhostJet(__instance);
		}

		internal static bool IsGhostJet(JetEngineScript jet)
		{
			try
			{
				CraftScript cs = jet != null && jet.PartScript != null ? jet.PartScript.CraftScript as CraftScript : null;
				if (cs == null) return false;
				return MpNetworkManager.IsRemoteCraftNode(cs.CraftNode as CraftNode);
			}
			catch
			{
				return false;
			}
		}

		/// <summary>
		/// 在显式接口实现中按方法名精确选目标。
		/// 实测坑(dotnet/Mono 一致):显式接口实现的 MethodInfo.Name 是带接口前缀的
		/// "IFoo.Bar"(如 "IFlightFixedUpdate.FlightFixedUpdate"),不是简单名 "Bar" ——
		/// 只比简单名会永远匹配不上。因此用 "简单名 或 以 .方法名 结尾" 匹配
		/// (带命名空间则形如 "...IFlightUpdate.FlightUpdate",EndsWith 同样命中),
		/// 并留 GetMethods 兜底覆盖运行时差异。
		/// (实测 GetInterfaceMap(typeof(IFlightUpdate)) 只含自身方法,不把继承的
		/// IGameLoopItem 成员放进 TargetMethods,故按名过滤已足够精确。)
		/// </summary>
		private static MethodInfo FindInterfaceMethod(Type interfaceType, string methodName)
		{
			try
			{
				InterfaceMapping map = typeof(JetEngineScript).GetInterfaceMap(interfaceType);
				foreach (MethodInfo m in map.TargetMethods)
				{
					if (m == null || m.IsAbstract) continue;
					if (IsNamed(m, methodName)) return m;
				}
			}
			catch
			{
			}
			// 兜底:直接扫全部实例方法(覆盖各运行时对显式接口实现 Name 的语义差异)
			try
			{
				foreach (MethodInfo m in typeof(JetEngineScript).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
				{
					if (IsNamed(m, methodName)) return m;
				}
			}
			catch
			{
			}
			return null;
		}

		private static bool IsNamed(MethodInfo m, string methodName)
		{
			string n = m.Name;
			return n == methodName || (n.Length > methodName.Length && n.EndsWith("." + methodName));
		}
	}
}
