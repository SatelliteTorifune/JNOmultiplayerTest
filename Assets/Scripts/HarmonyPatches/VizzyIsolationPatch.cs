using System;
using System.Collections.Generic;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts.Modifiers;
using Assets.Scripts.Flight.Sim;
using Assets.Scripts.Net;
using HarmonyLib;
using ModApi.Craft.Program;
using ModApi.Craft.Program.Craft;
using Assets.Scripts.Net.Session;

namespace Assets.Scripts
{
	/// <summary>
	/// Vizzy 联机隔离:阻止幽灵(远程)飞船的 Vizzy 执行与跨 craft 数据传输。
	/// 方案见 plans/archive/vizzy-isolation-2026-08-22.md,1.4.2 版本核证见
	/// plans/update-1.4.2-experimental-2026-09-03.md §〇之五。
	///
	/// 包含两个 Harmony patch:
	///   1) BroadcastMessage — 联机下 AllCrafts 降级为仅本 craft 广播;
	///   2) FlightUpdate — 远程幽灵船直接跳过,不执行任何 Vizzy 指令(封堵
	///      RequestUserInput/SetTimeMode/SetCameraProperty 等所有侧信道)。
	///
	/// 开关:VizzyIsolationPatch.Enabled (默认 true),设 false 恢复原生行为。
	///
	/// --- 1.4.2 反编译核证(2026-09,对安装目录 SimpleRockets2.dll 逐条比对 IL)---
	///   · BroadcastMessage(BroadcastScope,string,ExpressionResult) / FlightUpdate(in FlightFrameData)
	///     两个目标方法签名与方法体 **逐条 IL 与 1.4.2 参照程序集完全一致** ⇒ patch 仍正确挂载;
	///   · BroadcastScope 枚举值 Program=0/Craft=1/AllCrafts=2 未变 ⇒ 作用域判断不错位;
	///   · Vizzy 指令的唯一执行入口 `Process.Update` 在 1.4.2 全局只有一处调用者,
	///     即 public FlightUpdate(:171) ⇒ patch 2 即为完整封堵(暂停时游戏只跑 IFlightUpdatePaused,
	///     FlightProgramScript 未实现该接口,故暂停下 Vizzy 本就不执行,无需额外 patch);
	///   · `CraftService.BroadcastMessage`(:250)是 Vizzy 指令的直通包装,最终仍进本 patch 1。
	/// </summary>

	// Patch 3(诊断,不改变行为):Vizzy「请求用户输入」指令执行时记录"是哪条船在执行"。
	// 用途:用户实测反馈"对方用需要输入的组件时,我这里也弹输入框"。输入框只可能由
	// `UserInputInstruction.Execute` → `CraftService.RequestUserInput` → `CreateInputDialog` 弹出,
	// 因此这条日志能一次性定位:是本地船在执行(patch 无责)还是幽灵船在执行(隔离漏了 + 漏在哪一层)。
	// 只读、不改判定,可直接保留。
	[HarmonyPatch(typeof(ModApi.Craft.Program.Instructions.UserInputInstruction), "Execute")]
	internal static class VizzyIsolationPatch_UserInputDiag
	{
		static void Prefix(ModApi.Craft.Program.IThreadContext context)
		{
			VizzyIsolationPatch.LogUserInputExecute(context);
		}
	}

	// Patch 1: 广播隔离
	[HarmonyPatch(typeof(FlightProgramScript), "BroadcastMessage",
		new Type[] { typeof(BroadcastScope), typeof(string), typeof(ExpressionResult) })]
	internal static class VizzyIsolationPatch_Broadcast
	{
		static bool Prefix(FlightProgramScript __instance, BroadcastScope scope, string messageName, ExpressionResult data)
		{
			if (scope != BroadcastScope.AllCrafts) return true;
			if (!VizzyIsolationPatch.Enabled) return true;

			NetworkManager mgr = NetworkManager.Instance;
			if (mgr == null || !mgr.IsConnected) return true;

			// 幽灵船自己的 Vizzy 不应向外广播(正常已被 patch 2 拦在执行前;此处兜住
			// 「远程船尚未登记进 _remoteCrafts」的生成窗口)。
			if (VizzyIsolationPatch.IsGhostCraft(__instance.PartScript))
			{
				VizzyIsolationPatch.LogDroppedBroadcast(__instance, messageName);
				return false;
			}

			CraftScript craft = __instance.PartScript != null
				? __instance.PartScript.CraftScript as CraftScript
				: null;
			if (craft == null)
			{
				// 1.4.2 起游戏的 AllCrafts 分支必读 PartScript/CraftScript,走到这里说明
				// 该 FlightProgramScript 已脱离部件(被拆除/拆分)。此时无法定位同 craft 的
				// 其他 FlightProgram,只能丢弃;记一次日志避免"广播静默消失"难以定位。
				VizzyIsolationPatch.LogDroppedBroadcast(__instance, messageName);
				return false;
			}

			try
			{
				foreach (FlightProgramScript fps in craft.FlightProgramScripts)
					fps.OnReceiveMessage(messageName, data);
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
				if (VizzyIsolationPatch.IsGhostCraft(__instance.PartScript))
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
	/// 共享开关 + 幽灵船判定。
	/// true(默认):拦截广播 + 禁止幽灵船 Vizzy 执行;
	/// false:恢复全部原生行为。
	/// </summary>
	internal static class VizzyIsolationPatch
	{
		/// <summary>是否启用 Vizzy 联机隔离。</summary>
		public static bool Enabled = true;

		/// <summary>
		/// 幽灵(远程)船判定。三层:
		///   ① 权威:`MultiPlayerNetworkManager.IsRemoteCraftNode(node)`(查 _remoteCrafts 登记表);
		///   ② 记忆:`_ghostNodeIds` 里出现过的 NodeId —— 封「断线/移除窗口」:
		///      `RemoveRemoteCraft` 先 `_remoteCrafts.Remove()` 再 `DestroyCraft()`,而节点要到
		///      下一帧 `ProcessDestroyedCraftNodes` 才真正消失;这中间 ① 已返回 false,
		///      但该幽灵的 `FlightProgramScript.FlightUpdate` 仍可能被游戏跑一次(会漏执行、漏广播)。
		///   ③ 兜底:craft 名 =「对方玩家名 + 竖线 + 船名」(SpawnRemoteCraftAtPosition 的命名约定),
		///      封「生成窗口」:SpawnCraft 返回后 craft 已进场景,但 _remoteCrafts 赋值在其后;
		///      若这中间抛异常(生成路径有 try/catch),该幽灵会永久留在场景中且 ① 永远返回 false。
		/// 三层皆否 = 允许执行(异常时方向安全:绝不误杀本地船的 Vizzy)。
		/// </summary>
		public static bool IsGhostCraft(ModApi.Craft.Parts.IPartScript partScript)
		{
			CraftScript cs = partScript != null ? partScript.CraftScript as CraftScript : null;
			return IsGhostNode(cs != null ? cs.CraftNode as CraftNode : null);
		}

		/// <summary>
		/// 幽灵判定核心(按 <see cref="CraftNode"/> 走上面三层)。**拿不到 `IPartScript` 的调用点必须走本方法**,
		/// 不要写 `ics as IPartScript`:游戏的 `CraftScript` 只实现 `ModApi.Craft.ICraftScript`、**不实现
		/// `IPartScript`**,那种 cast 恒得 null → 判定恒 false(2026-09 的 UserInput 诊断日志曾因此系统性
		/// 误报「本地船、隔离未漏」,该结论无效)。
		/// </summary>
		public static bool IsGhostNode(CraftNode node)
		{
			if (node == null) return false;

			NetworkManager mgr = NetworkManager.Instance;
			if (mgr == null) return false;

			// ① 权威:仍在登记表中 —— 顺便把 NodeId 记进记忆(此刻它确定是幽灵)
			if (NetworkManager.IsRemoteCraftNode(node))
			{
				if (node.NodeId != 0) _ghostNodeIds.Add(node.NodeId);
				return true;
			}

			// ② 记忆:已被移出登记表(断线/被踢/销毁中),但本次飞行内确认过是幽灵
			if (node.NodeId != 0 && _ghostNodeIds.Contains(node.NodeId))
			{
				if (_ghostCacheLogged.Add(node.NodeId))
					Mod.LogLobby("VizzyIsolation: ghost nodeId=" + node.NodeId +
						" still guarded after removal (destroy window)");
				return true;
			}

			return IsRemoteCraftName(node);
		}

		// 曾经确认为幽灵的节点 NodeId(断线/移除后仍保留到本次飞行场景卸载)。
		// 节点 id 由游戏 `FlightState.GetNextNodeId()` 单调递增分配,场景内唯一 ⇒ 不会误判本地船。
		private static readonly HashSet<int> _ghostNodeIds = new HashSet<int>();
		private static readonly HashSet<int> _ghostCacheLogged = new HashSet<int>();

		/// <summary>
		/// 飞行场景加载/卸载时清空幽灵 NodeId 记忆(由 `MultiPlayerNetworkManager.OnFlightSceneLoaded` 调用):
		/// 否则下一次飞行里复用的 NodeId 会被误判成幽灵,导致本地船的 Vizzy 被误杀。
		/// </summary>
		public static void ClearGhostNodeCache()
		{
			_ghostNodeIds.Clear();
			_ghostCacheLogged.Clear();
			_loggedDroppedBroadcasts.Clear();
			_loggedUserInputExecutes.Clear();
		}

		// 「请求用户输入」诊断的每船一次性去重。
		private static readonly HashSet<int> _loggedUserInputExecutes = new HashSet<int>();

		/// <summary>
		/// 诊断:Vizzy「请求用户输入」指令即将执行(即即将弹出输入框)时,记录是哪条船在执行,
		/// 以及幽灵判定的逐层结果。用于定位"对方按输入框、我这边也弹框"的漏点。
		/// </summary>
		public static void LogUserInputExecute(ModApi.Craft.Program.IThreadContext context)
		{
			try
			{
				NetworkManager mgr = NetworkManager.Instance;
				if (mgr == null || !mgr.IsConnected) return; // 单人会话不打扰

				string craftName = "<unknown>";
				string nodeIdText = "?";
				bool hasScript = false;
				bool isRemoteNode = false;
				bool inRegistry = false;

				ModApi.Craft.ICraftScript ics = context != null && context.Craft != null ? context.Craft.CraftScript : null;
				if (ics != null)
				{
					hasScript = true;
					craftName = ics.Data != null && ics.Data.Name != null ? ics.Data.Name : craftName;
					CraftNode cn = ics.CraftNode as CraftNode;
					if (cn != null)
					{
						nodeIdText = cn.NodeId.ToString();
						isRemoteNode = NetworkManager.IsRemoteCraftNode(cn);
						inRegistry = _ghostNodeIds.Contains(cn.NodeId);
					}
				}

				// ⚠️ 不要写 `IsGhostCraft(ics as IPartScript)`:CraftScript 不实现 IPartScript,该 cast 恒 null
				// → isGhost 恒 false、日志系统性误报"本地船(patch 无责)"(2026-09 修,见 IsGhostNode 注释)。
				CraftScript cs = ics as CraftScript;
				CraftNode diagNode = (cs != null ? cs.CraftNode : null) as CraftNode;
				bool nameFallback = diagNode != null && IsRemoteCraftName(diagNode);
				bool ghost = IsGhostNode(diagNode);

				int key = (craftName + "#" + nodeIdText).GetHashCode();
				if (!_loggedUserInputExecutes.Add(key)) return;

				Mod.LogLobby("VizzyIsolation/UserInput: craft='" + craftName + "' nodeId=" + nodeIdText +
					" craftScript=" + (hasScript ? "yes" : "null") +
					" isGhost=" + ghost +
					" [registry=" + isRemoteNode + " nodeIdMemo=" + inRegistry + " nameFallback=" + nameFallback + "]" +
					" ⇒ " + (ghost ? "GHOST should have been blocked by patch 2 (isolation hole!)"
								  : "local craft (patch 无责)"));
			}
			catch (Exception e)
			{
				Mod.LogError("VizzyIsolation/UserInput diag failed: " + e.Message);
			}
		}

		/// <summary>按「玩家名|船名」命名约定识别远程船(SpawnRemoteCraftAtPosition 命名)。</summary>
		private static bool IsRemoteCraftName(CraftNode node)
		{
			if (node == null) return false;
			string name = node.Name;
			if (string.IsNullOrEmpty(name)) return false;

			NetworkManager mgr = NetworkManager.Instance;
			if (mgr == null || mgr.Transport == null) return false;

			foreach (MultiPlayerPeer peer in mgr.Transport.GetPeers())
			{
				if (peer == null || string.IsNullOrEmpty(peer.PlayerName)) continue;
				if (name.Length <= peer.PlayerName.Length) continue;
				if (string.CompareOrdinal(name, 0, peer.PlayerName, 0, peer.PlayerName.Length) != 0) continue;
				if (name[peer.PlayerName.Length] == '|') return true;
			}
			return false;
		}

		// 一次性日志去重:避免每帧刷屏,同时保证"广播被丢弃"可定位。
		private static readonly HashSet<int> _loggedDroppedBroadcasts = new HashSet<int>();

		/// <summary>记录一次被隔离丢弃的广播(每个 FlightProgramScript 每个联机会话只记一次)。</summary>
		public static void LogDroppedBroadcast(FlightProgramScript fps, string messageName)
		{
			if (fps == null) return;
			try
			{
				NetworkManager mgr = NetworkManager.Instance;
				if (mgr != null && !mgr.IsConnected)
					_loggedDroppedBroadcasts.Clear(); // 新会话:让诊断日志重新可用

				int id = fps.GetInstanceID();
				if (!_loggedDroppedBroadcasts.Add(id)) return;
				Mod.LogLobby("VizzyIsolation/Broadcast: dropped AllCrafts broadcast '" + messageName +
					"' (craft not resolvable / ghost craft)");
			}
			catch (Exception)
			{
				// 日志失败绝不影响主流程
			}
		}
	}
}
