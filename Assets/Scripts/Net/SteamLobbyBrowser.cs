using System;
using System.Collections.Generic;
using System.Reflection;
using ModApi;
using Steamworks;
using UnityEngine;

namespace Assets.Scripts.Net
{
	/// <summary>
	/// Steam 大厅浏览器（房间列表，替代手动输入房主 SteamId）。
	/// 移植自 SP2 的 SteamLobbyManager（仅 SteamMatchmaking 部分；FishNet / FishySteamworks 那半段零依赖、用不上）：
	/// - 开房：CreateLobby(Public, max) → LobbyCreated_t → SetLobbyData（名字/版本/房主 SteamId）→ 复用 LobbyManager.HostLobby(0) 起 P2P 监听；
	/// - 列表：RequestLobbyList + 版本数值过滤 + DistanceFilter → LobbyMatchList_t → 逐房 GetLobbyByIndex 读元数据 → 事件抛给 UI；
	/// - 加入：JoinLobby(lobbyId) → LobbyEnter_t → GetLobbyOwner → 复用 LobbyManager.JoinLobby(ownerSteamId, 0)
	///   （内部走 SteamTransport.StartClient + Hello 握手），传输/握手/状态同步零改动；
	/// - 好友邀请：ActivateGameOverlayInviteDialog + GameLobbyJoinRequested_t 自动加入（顺手支持）。
	///
	/// 回调泵：游戏每帧 SteamAPI.RunCallbacks() + 本类 Update 补一次保险；回调引用必须持有，防 GC 自动退订
	/// （SteamTransport._connStatusCallback 同款模式）。
	/// 依赖：游戏已 SteamAPI.Init()（不重复 Init），Steamworks.NET 直调（com.rlabrecque.steamworks.net.dll，见 SteamTransport）。
	/// 见 plans/steam-lobby-2026-09-12.md。
	/// </summary>
	public class SteamLobbyBrowser : MonoBehaviour
	{
		public static SteamLobbyBrowser Instance { get; private set; }

		/// <summary>默认房间人数上限（Steam 大厅成员上限；P2P 本身不限制，满员由 Steam 拒绝加入）。</summary>
		public const int DefaultMaxPlayers = 8;

		// ---- lobby data key（写入大厅元数据；数值 key 供 RequestLobbyList 过滤用）----
		private const string KeyName = "mp_name";
		private const string KeyDescription = "mp_desc";
		private const string KeyOwner = "mp_owner";
		private const string KeyVerMajor = "mp_ver_major";
		private const string KeyVerMinor = "mp_ver_minor";
		private const string KeyVerBuild = "mp_ver_build";

		// 回调引用必须持有（防 GC 自动退订）。
		private Callback<LobbyCreated_t> _onLobbyCreated;
		private Callback<LobbyMatchList_t> _onLobbyMatchList;
		private Callback<LobbyEnter_t> _onLobbyEnter;
		private Callback<GameLobbyJoinRequested_t> _onGameLobbyJoinRequested;

		// 开房 pending 参数（LobbyCreated_t 回调里使用）
		private string _pendingRoomName;
		private int _pendingMaxPlayers = DefaultMaxPlayers;

		/// <summary>当前所在 Steam 大厅 id（0 = 不在任何大厅）。</summary>
		public ulong CurrentLobbyId { get; private set; }

		/// <summary>是否当前大厅的房主（仅大厅内有效；供 UI 显示"邀请好友"按钮用）。</summary>
		public bool IsCurrentLobbyOwner
		{
			get
			{
				if (CurrentLobbyId == 0) return false;
				try
				{
					return SteamMatchmaking.GetLobbyOwner(new CSteamID(CurrentLobbyId)).m_SteamID == SteamUser.GetSteamID().m_SteamID;
				}
				catch (Exception e)
				{
					Mod.LogLobby("SteamLobbyBrowser.IsCurrentLobbyOwner error: " + e.Message);
					return false;
				}
			}
		}

		/// <summary>列表刷新完成（主线程）。</summary>
		public event Action<IReadOnlyList<LobbyInfo>> OnLobbyListReceived;
		/// <summary>开房成功且 P2P 监听已启动（主线程）。</summary>
		public event Action<LobbyInfo> OnLobbyHosted;
		/// <summary>错误（开房/加入失败、无房主 SteamId、Steam 未初始化），message 为可直接展示的文案（主线程）。</summary>
		public event Action<string> OnLobbyError;

		/// <summary>列表中的一间房。</summary>
		public class LobbyInfo
		{
			public ulong LobbyId;
			public string Name = "";
			public string Description = "";
			public ulong OwnerSteamId;
			public int MemberCount;
			public int MaxMembers;
			public string Version = "";
		}

		// ---------------- Unity 生命周期 ----------------

		private void Awake()
		{
			Instance = this;
			RegisterCallbacks();
			Mod.LogLobby("SteamLobbyBrowser created (Steam running=" + SteamAPI.IsSteamRunning() + ")");
		}

		private void OnDestroy()
		{
			UnregisterCallbacks();
			LeaveLobby();
			if (Instance == this) Instance = null;
		}

		private void Update()
		{
			// 保险回调泵：游戏通常每帧调 SteamAPI.RunCallbacks()；这里补一次，确保大厅回调及时分发。
			// ⚠️ 2026-09 实测发现：本游戏经自有 Steam 互操作做 native 初始化，**不一定初始化
			// Steamworks.NET 的托管 CallbackDispatcher** —— 直接调 SteamAPI.RunCallbacks() 会每帧抛
			// "Callback dispatcher is not initialized." 并刷爆 Player.log(实测一个会话 6645 条)。
			// 修法：先反射确保托管 dispatcher 已初始化(internal static Initialize，幂等)；
			// 失败则放弃泵(大厅回调改由游戏原生泵分发)并只警告一次，不再每帧刷屏。
			try
			{
				if (!SteamAPI.IsSteamRunning()) return;
				EnsureCallbackDispatcherInitialized();
				if (IsCallbackDispatcherInitialized()) SteamAPI.RunCallbacks();
			}
			catch (Exception e)
			{
				if (!_runCallbacksWarned)
				{
					_runCallbacksWarned = true;
					Mod.LogLobby("SteamLobbyBrowser.Update RunCallbacks error: " + e.Message + " (仅提示一次，不再每帧刷屏)");
				}
			}
		}

		private bool _dispatcherTried;
		private bool _runCallbacksWarned;

		/// <summary>托管 CallbackDispatcher 是否已初始化（Steamworks.NET 内部类型，只能反射）。</summary>
		private static bool IsCallbackDispatcherInitialized()
		{
			try
			{
				Type cd = Type.GetType("Steamworks.CallbackDispatcher, com.rlabrecque.steamworks.net");
				if (cd == null) return false;
				PropertyInfo pi = cd.GetProperty("IsInitialized", BindingFlags.Static | BindingFlags.Public);
				return pi != null && (bool)pi.GetValue(null, null);
			}
			catch { return false; }
		}

		/// <summary>确保托管 CallbackDispatcher 已初始化（internal static Initialize，反射调用，幂等）。</summary>
		private void EnsureCallbackDispatcherInitialized()
		{
			if (IsCallbackDispatcherInitialized() || _dispatcherTried) return;
			_dispatcherTried = true;
			try
			{
				Type cd = Type.GetType("Steamworks.CallbackDispatcher, com.rlabrecque.steamworks.net");
				if (cd == null)
				{
					Mod.LogLobby("SteamLobbyBrowser: CallbackDispatcher 类型不可见(Steamworks.NET 版本变化)，大厅回调依赖游戏原生泵");
					return;
				}
				MethodInfo mi = cd.GetMethod("Initialize", BindingFlags.Static | BindingFlags.NonPublic);
				if (mi == null)
				{
					Mod.LogLobby("SteamLobbyBrowser: CallbackDispatcher.Initialize 不存在(Steamworks.NET 版本变化)，大厅回调依赖游戏原生泵");
					return;
				}
				mi.Invoke(null, null);
				Mod.LogLobby("SteamLobbyBrowser: 已初始化 Steamworks 托管 CallbackDispatcher(游戏未初始化它，由 mod 补上，RunCallbacks 不再刷屏)");
			}
			catch (Exception e)
			{
				Mod.LogLobby("SteamLobbyBrowser: CallbackDispatcher.Initialize 失败(" + e.Message + ")，大厅回调依赖游戏原生泵");
			}
		}

		// ---------------- 回调注册（持有引用，防 GC 退订） ----------------

		private void RegisterCallbacks()
		{
			_onLobbyCreated = Callback<LobbyCreated_t>.Create(OnLobbyCreated);
			_onLobbyMatchList = Callback<LobbyMatchList_t>.Create(OnLobbyMatchList);
			_onLobbyEnter = Callback<LobbyEnter_t>.Create(OnLobbyEnter);
			_onGameLobbyJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnGameLobbyJoinRequested);
		}

		private void UnregisterCallbacks()
		{
			if (_onLobbyCreated != null) { _onLobbyCreated.Dispose(); _onLobbyCreated = null; }
			if (_onLobbyMatchList != null) { _onLobbyMatchList.Dispose(); _onLobbyMatchList = null; }
			if (_onLobbyEnter != null) { _onLobbyEnter.Dispose(); _onLobbyEnter = null; }
			if (_onGameLobbyJoinRequested != null) { _onGameLobbyJoinRequested.Dispose(); _onGameLobbyJoinRequested = null; }
		}

		// ---------------- 对外 API ----------------

		/// <summary>开房：创建 Public 大厅，成功后写元数据并复用现有 HostLobby 起 P2P 监听。</summary>
		public void CreateLobby(string roomName, int maxPlayers = DefaultMaxPlayers)
		{
			if (!SteamAPI.IsSteamRunning())
			{
				OnLobbyError?.Invoke(Locale.GetString("MultiPlayer.MultiPlayerUI.LobbyUnavailable"));
				return;
			}
			LeaveLobby();
			_pendingRoomName = string.IsNullOrWhiteSpace(roomName) ? GetPersonaName() : roomName.Trim();
			_pendingMaxPlayers = Mathf.Clamp(maxPlayers, 2, 250);
			Mod.LogLobby("SteamLobbyBrowser.CreateLobby: name='" + _pendingRoomName + "', max=" + _pendingMaxPlayers);
			SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePublic, _pendingMaxPlayers);
		}

		/// <summary>
		/// 刷新房间列表：版本数值过滤（Major/Minor/Build 全等，Build=-1 时跳过）+ 距离过滤 + 数量上限，
		/// 结果经 OnLobbyListReceived 事件返回（LobbyMatchList_t 回调）。
		/// </summary>
		public void RefreshLobbyList(bool worldwide = false)
		{
			if (!SteamAPI.IsSteamRunning())
			{
				OnLobbyError?.Invoke(Locale.GetString("MultiPlayer.MultiPlayerUI.LobbyUnavailable"));
				return;
			}
			try
			{
				System.Version v = GetModVersion();
				// 版本过滤：避免旧版 mod 玩家看到/加入新版房（SP2 ServerVersionMajor/Minor/Build 同款）
				if (v != null)
				{
					if (v.Major >= 0) SteamMatchmaking.AddRequestLobbyListNumericalFilter(KeyVerMajor, v.Major, ELobbyComparison.k_ELobbyComparisonEqual);
					if (v.Minor >= 0) SteamMatchmaking.AddRequestLobbyListNumericalFilter(KeyVerMinor, v.Minor, ELobbyComparison.k_ELobbyComparisonEqual);
					if (v.Build >= 0) SteamMatchmaking.AddRequestLobbyListNumericalFilter(KeyVerBuild, v.Build, ELobbyComparison.k_ELobbyComparisonEqual);
				}
				SteamMatchmaking.AddRequestLobbyListDistanceFilter(worldwide
					? ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide
					: ELobbyDistanceFilter.k_ELobbyDistanceFilterDefault);
				SteamMatchmaking.AddRequestLobbyListResultCountFilter(50);
				SteamMatchmaking.RequestLobbyList();
				Mod.LogLobby("SteamLobbyBrowser.RefreshLobbyList: worldwide=" + worldwide + ", ver=" + (v == null ? "?" : v.ToString()));
			}
			catch (Exception e)
			{
				Mod.LogLobby("SteamLobbyBrowser.RefreshLobbyList error: " + e.Message);
				OnLobbyError?.Invoke(Locale.GetString("MultiPlayer.MultiPlayerUI.LobbyJoinFailed", e.Message));
			}
		}

		/// <summary>加入房间：JoinLobby → LobbyEnter_t → GetLobbyOwner → 复用现有 LobbyManager.JoinLobby（Steam P2P）。</summary>
		public void JoinLobby(ulong lobbyId)
		{
			if (lobbyId == 0) return;
			if (!SteamAPI.IsSteamRunning())
			{
				OnLobbyError?.Invoke(Locale.GetString("MultiPlayer.MultiPlayerUI.LobbyUnavailable"));
				return;
			}
			LeaveLobby();
			Mod.LogLobby("SteamLobbyBrowser.JoinLobby: lobbyId=" + lobbyId);
			SteamMatchmaking.JoinLobby(new CSteamID(lobbyId));
		}

		/// <summary>离开当前大厅（断开/停房时调用；已不在大厅则为 no-op）。</summary>
		public void LeaveLobby()
		{
			if (CurrentLobbyId == 0) return;
			ulong id = CurrentLobbyId;
			CurrentLobbyId = 0;
			try { SteamMatchmaking.LeaveLobby(new CSteamID(id)); }
			catch (Exception e) { Mod.LogLobby("SteamLobbyBrowser.LeaveLobby error: " + e.Message); }
			Mod.LogLobby("SteamLobbyBrowser: left lobby " + id);
		}

		/// <summary>打开 Steam overlay 邀请好友对话框（需已在大厅内）。</summary>
		public void OpenInviteDialog()
		{
			if (CurrentLobbyId == 0) return;
			try { SteamFriends.ActivateGameOverlayInviteDialog(new CSteamID(CurrentLobbyId)); }
			catch (Exception e) { Mod.LogLobby("SteamLobbyBrowser.OpenInviteDialog error: " + e.Message); }
		}

		// ---------------- Steam 回调 ----------------

		private void OnLobbyCreated(LobbyCreated_t result)
		{
			try
			{
				if (result.m_eResult != EResult.k_EResultOK)
				{
					Mod.LogLobby("SteamLobbyBrowser: CreateLobby FAILED result=" + result.m_eResult);
					CurrentLobbyId = 0;
					OnLobbyError?.Invoke(Locale.GetString("MultiPlayer.MultiPlayerUI.LobbyCreateFailed", result.m_eResult));
					return;
				}
				CurrentLobbyId = result.m_ulSteamIDLobby;
				CSteamID lobby = new CSteamID(CurrentLobbyId);
				ulong me = SteamUser.GetSteamID().m_SteamID;
				System.Version v = GetModVersion();
				SteamMatchmaking.SetLobbyData(lobby, KeyName, _pendingRoomName);
				SteamMatchmaking.SetLobbyData(lobby, KeyDescription, "");
				SteamMatchmaking.SetLobbyData(lobby, KeyOwner, me.ToString());
				if (v != null)
				{
					if (v.Major >= 0) SteamMatchmaking.SetLobbyData(lobby, KeyVerMajor, v.Major.ToString());
					if (v.Minor >= 0) SteamMatchmaking.SetLobbyData(lobby, KeyVerMinor, v.Minor.ToString());
					if (v.Build >= 0) SteamMatchmaking.SetLobbyData(lobby, KeyVerBuild, v.Build.ToString());
				}
				Mod.LogLobby("SteamLobbyBrowser: lobby created id=" + CurrentLobbyId +
					", name='" + _pendingRoomName + "', max=" + _pendingMaxPlayers +
					", ver=" + (v == null ? "?" : v.ToString()));

				// 起 P2P 监听（复用现有 HostLobby；确保走 Steam 传输，防止停留在 TCP debug 传输上）
				MpNetworkManager mgr = LobbyManager.Instance.EnsureMpManager();
				if (mgr != null && !(mgr.Transport is SteamTransport)) mgr.SetTransport(new SteamTransport());
				bool ok = LobbyManager.Instance.HostLobby(0);
				if (!ok)
				{
					LeaveLobby();
					OnLobbyError?.Invoke(Locale.GetString("MultiPlayer.MultiPlayerUI.LobbyCreateFailed", "HostLobby"));
					return;
				}
				OnLobbyHosted?.Invoke(ReadLobbyInfo(lobby));
			}
			catch (Exception e)
			{
				Mod.LogLobby("SteamLobbyBrowser.OnLobbyCreated error: " + e.Message);
				OnLobbyError?.Invoke(Locale.GetString("MultiPlayer.MultiPlayerUI.LobbyCreateFailed", e.Message));
			}
		}

		private void OnLobbyMatchList(LobbyMatchList_t result)
		{
			try
			{
				List<LobbyInfo> lobbies = new List<LobbyInfo>();
				ulong me = SteamUser.GetSteamID().m_SteamID;
				int count = (int)result.m_nLobbiesMatching;
				for (int i = 0; i < count; i++)
				{
					CSteamID lobby = SteamMatchmaking.GetLobbyByIndex(i);
					if (lobby.m_SteamID == 0) continue;
					LobbyInfo info = ReadLobbyInfo(lobby);
					if (info == null) continue;
					if (info.OwnerSteamId == me) continue; // 不显示自己的房间（开房中时）
					lobbies.Add(info);
				}
				Mod.LogLobby("SteamLobbyBrowser: lobby list received, total=" + count + ", shown=" + lobbies.Count);
				OnLobbyListReceived?.Invoke(lobbies);
			}
			catch (Exception e)
			{
				Mod.LogLobby("SteamLobbyBrowser.OnLobbyMatchList error: " + e.Message);
				OnLobbyError?.Invoke(Locale.GetString("MultiPlayer.MultiPlayerUI.LobbyJoinFailed", e.Message));
			}
		}

		private void OnLobbyEnter(LobbyEnter_t info)
		{
			try
			{
				if (info.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
				{
					string msg;
					if (info.m_EChatRoomEnterResponse == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseFull)
						msg = Locale.GetString("MultiPlayer.MultiPlayerUI.LobbyJoinFailedFull");
					else if (info.m_EChatRoomEnterResponse == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseDoesntExist)
						msg = Locale.GetString("MultiPlayer.MultiPlayerUI.LobbyJoinFailedMissing");
					else
						msg = Locale.GetString("MultiPlayer.MultiPlayerUI.LobbyJoinFailed", info.m_EChatRoomEnterResponse);
					Mod.LogLobby("SteamLobbyBrowser: JoinLobby FAILED response=" + info.m_EChatRoomEnterResponse);
					CurrentLobbyId = 0;
					OnLobbyError?.Invoke(msg);
					return;
				}
				CurrentLobbyId = info.m_ulSteamIDLobby;
				// 大厅内可查房主；兜底从 lobby data 读 mp_owner（列表阶段写好的）
				ulong owner = SteamMatchmaking.GetLobbyOwner(new CSteamID(CurrentLobbyId)).m_SteamID;
				if (owner == 0)
				{
					ulong.TryParse(SteamMatchmaking.GetLobbyData(new CSteamID(CurrentLobbyId), KeyOwner), out owner);
				}
				ulong me = SteamUser.GetSteamID().m_SteamID;
				Mod.LogLobby("SteamLobbyBrowser: entered lobby id=" + CurrentLobbyId + ", owner=" + owner);
				if (owner == me)
				{
					// 自己创建的大厅同样会收到 LobbyEnter_t（创建者自动进入）：这是开房流程（OnLobbyCreated 已写元数据并起 P2P 监听），
					// 绝不能走"加入"逻辑——否则 GetLobbyOwner 返回自己 → 把自己当房主去 Join → MP.Stop() 停掉开房会话 +
					// ConnectP2P 连自己必然失败，房间创建整体回滚。
					Mod.LogLobby("SteamLobbyBrowser: entered own lobby (host), skip join flow");
					return;
				}
				if (owner == 0)
				{
					LeaveLobby();
					OnLobbyError?.Invoke(Locale.GetString("MultiPlayer.MultiPlayerUI.LobbyJoinNoOwner"));
					return;
				}
				// 复用现有 Steam P2P 加入流程（SteamTransport.StartClient + Hello 握手），传输/同步零改动
				MpNetworkManager mgr = LobbyManager.Instance.EnsureMpManager();
				if (mgr != null && !(mgr.Transport is SteamTransport)) mgr.SetTransport(new SteamTransport());
				bool ok = LobbyManager.Instance.JoinLobby(owner.ToString(), 0);
				if (!ok)
				{
					LeaveLobby();
					OnLobbyError?.Invoke(Locale.GetString("MultiPlayer.MultiPlayerUI.LobbyJoinFailed", "StartClient"));
				}
			}
			catch (Exception e)
			{
				Mod.LogLobby("SteamLobbyBrowser.OnLobbyEnter error: " + e.Message);
				OnLobbyError?.Invoke(Locale.GetString("MultiPlayer.MultiPlayerUI.LobbyJoinFailed", e.Message));
			}
		}

		/// <summary>好友点了"加入游戏"（overlay 邀请接受 / 好友游戏内邀请）：自动加入该大厅。</summary>
		private void OnGameLobbyJoinRequested(GameLobbyJoinRequested_t info)
		{
			ulong lobbyId = info.m_steamIDLobby.m_SteamID;
			Mod.LogLobby("SteamLobbyBrowser: GameLobbyJoinRequested lobby=" + lobbyId + ", friend=" + info.m_steamIDFriend.m_SteamID);
			JoinLobby(lobbyId);
		}

		// ---------------- 工具 ----------------

		/// <summary>读取一间房的信息（列表用；读不到返回 null）。</summary>
		private static LobbyInfo ReadLobbyInfo(CSteamID lobby)
		{
			try
			{
				LobbyInfo info = new LobbyInfo { LobbyId = lobby.m_SteamID };
				info.Name = SteamMatchmaking.GetLobbyData(lobby, KeyName);
				info.Description = SteamMatchmaking.GetLobbyData(lobby, KeyDescription);
				ulong.TryParse(SteamMatchmaking.GetLobbyData(lobby, KeyOwner), out info.OwnerSteamId);
				string vmaj = SteamMatchmaking.GetLobbyData(lobby, KeyVerMajor);
				string vmin = SteamMatchmaking.GetLobbyData(lobby, KeyVerMinor);
				string vbuild = SteamMatchmaking.GetLobbyData(lobby, KeyVerBuild);
				string ver = "";
				if (!string.IsNullOrEmpty(vmaj)) ver = vmaj;
				if (!string.IsNullOrEmpty(vmin)) ver = (ver.Length > 0 ? ver + "." : "") + vmin;
				if (!string.IsNullOrEmpty(vbuild)) ver = (ver.Length > 0 ? ver + "." : "") + vbuild;
				info.Version = ver;
				if (string.IsNullOrEmpty(info.Name)) info.Name = "Room " + lobby.m_SteamID;
				info.MemberCount = SteamMatchmaking.GetNumLobbyMembers(lobby);
				info.MaxMembers = SteamMatchmaking.GetLobbyMemberLimit(lobby);
				return info;
			}
			catch (Exception e)
			{
				Mod.LogLobby("SteamLobbyBrowser.ReadLobbyInfo failed: lobby=" + lobby.m_SteamID + ": " + e.Message);
				return null;
			}
		}

		/// <summary>本地 mod 版本（ModInfo.Version，如 1.4）；未初始化时为 null。</summary>
		private static System.Version GetModVersion()
		{
			try { return Mod.Instance != null ? Mod.Instance.ModVersion : null; }
			catch { return null; }
		}

		private static string GetPersonaName()
		{
			try
			{
				string n = SteamFriends.GetPersonaName();
				return string.IsNullOrWhiteSpace(n) ? "Player" : n;
			}
			catch { return "Player"; }
		}
	}
}
