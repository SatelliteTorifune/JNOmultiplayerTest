using System;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts.Modifiers;
using Assets.Scripts.Flight.Sim;
using Assets.Scripts.Net;
using HarmonyLib;
using ModApi.Craft.Program;
using ModApi.Craft.Program.Craft;

namespace Assets.Scripts
{
	/// <summary>
	/// Vizzy 联机隔离:阻止幽灵(远程)飞船的 Vizzy 执行与跨 craft 数据传输。
	/// 方案见 plans/vizzy-isolation.md。
	///
	/// 包含两个 Harmony patch:
	///   1) BroadcastMessage — 联机下 AllCrafts 降级为仅本 craft 广播;
	///   2) FlightUpdate — 远程幽灵船直接跳过,不执行任何 Vizzy 指令(封堵
	///      RequestUserInput/SetTimeMode/SetCameraProperty 等所有侧信道)。
	///
	/// 开关:VizzyIsolationPatch.Enabled (默认 true),设 false 恢复原生行为。
	/// </summary>

	// Patch 1: 广播隔离
	[HarmonyPatch(typeof(FlightProgramScript), "BroadcastMessage",
		new Type[] { typeof(BroadcastScope), typeof(string), typeof(ExpressionResult) })]
	internal static class VizzyIsolationPatch_Broadcast
	{
		static bool Prefix(FlightProgramScript __instance, BroadcastScope scope, string messageName, ExpressionResult data)
		{
			if (scope != BroadcastScope.AllCrafts) return true;
			if (!VizzyIsolationPatch.Enabled) return true;

			MpNetworkManager mgr = MpNetworkManager.Instance;
			if (mgr == null || !mgr.IsConnected) return true;

			try
			{
				CraftScript craft = __instance.PartScript != null
					? __instance.PartScript.CraftScript as CraftScript
					: null;
				if (craft != null)
				{
					foreach (FlightProgramScript fps in craft.FlightProgramScripts)
						fps.OnReceiveMessage(messageName, data);
				}
			}
			catch (Exception e)
			{
				Mod.LogError("VizzyIsolation/Broadcast: same-craft fallback failed: " + e);
			}

			return false;
		}
	}

	// Patch 2: 幽灵船 Vizzy 执行拦截
	[HarmonyPatch(typeof(FlightProgramScript), "FlightUpdate")]
	internal static class VizzyIsolationPatch_FlightUpdate
	{
		static bool Prefix(FlightProgramScript __instance)
		{
			if (!VizzyIsolationPatch.Enabled) return true;

			try
			{
				CraftScript cs = __instance.PartScript != null
					? __instance.PartScript.CraftScript as CraftScript
					: null;
				if (cs != null && MpNetworkManager.IsRemoteCraftNode(cs.CraftNode as CraftNode))
					return false; // 幽灵船:跳过所有 Vizzy 指令执行
			}
			catch (Exception e)
			{
				Mod.LogError("VizzyIsolation/FlightUpdate: ghost check failed, allowing: " + e);
			}

			return true;
		}
	}

	/// <summary>
	/// 共享开关:是否启用 Vizzy 联机隔离。
	/// true(默认):拦截广播 + 禁止幽灵船 Vizzy 执行;
	/// false:恢复全部原生行为。
	/// </summary>
	internal static class VizzyIsolationPatch
	{
		public static bool Enabled = true;
	}
}