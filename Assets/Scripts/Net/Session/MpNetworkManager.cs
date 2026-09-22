using System;
using System.Collections.Generic;
using Assets.Scripts.Flight;
using Assets.Scripts.Flight.Sim;
using ModApi;
using ModApi.Craft.Parts;
using UnityEngine;
using Assets.Scripts.Net.Sync;

namespace Assets.Scripts.Net.Session
{
	/// <summary>
	/// 联机网络管理器（主机中继模式）——**瘦门面 + 组合根**（2026-09-22 重构）：
	/// - 只负责：Unity 生命周期、会话身份状态、对外 API（供 UI / LobbyManager / Harmony patch 使用）、每帧驱动各组件；
	/// - 具体职责已拆到：<see cref="MpPlayerRegistry"/>（玩家表）、<see cref="MpCraftCatalog"/>（飞船 XML 分发）、
	///   <see cref="LocalCraftSender"/>（发送端:采样 + 发包节拍）、
	///   <see cref="RemoteCraftManager"/>（幽灵生命周期）、<see cref="RemoteCraftDriver"/>（接收端外推/平滑驱动）、
	///   <see cref="MpMessageRouter"/>（协议分发）、FlightUI 提示(直接在本类内)；
	///   共享常量/工具在 <see cref="MpSyncTuning"/> / <see cref="MpMath"/> / <see cref="GhostPoseWriter"/> /
	///   <see cref="RemoteCraftSmoothing"/> / <see cref="GhostBodyRemapper"/>。
	/// - 协议、平滑算法、日志行、热路径零分配、注释均与重构前一致（纯搬移）。
	/// - 客户端把状态包发给房主，房主转发给其他所有客户端；房主负责房间管理（Hello/Welcome/PlayerJoin/PlayerLeave）。
	/// 由 Mod 在飞行场景挂载到独立 GameObject 上。
	/// </summary>
	[DefaultExecutionOrder(1000)]
	public class MpNetworkManager : MonoBehaviour
	{
		public static MpNetworkManager Instance { get; private set; }

		// 传输层切换点：
		// - SteamTransport：Steam P2P（Steam Networking Sockets），零端口转发/零 frp，最推荐（SP2 的 FishySteamworks 同款）。
		// - TcpTransport：TCP，可走 frp/nginx 等纯 TCP 内网穿透（无 MTU 限制，无需分片）；缺点 head-of-line blocking。
		// - LiteNetLibTransport：UDP + 可靠/不可靠通道分离 + 应用层分片；缺点公网需 UDP 端口转发。
		// 默认 Steam；本地虚拟机 debug 时用控制台 TcpHostLobby / TcpJoinLobby 切到 TcpTransport（见 SetTransport）。
		public IMpTransport Transport { get; private set; } = new SteamTransport();

		public bool IsServer { get; private set; }
		public bool IsConnected { get; private set; }
		public int PlayerId { get; internal set; } = -1;    // 房主 = 0
		public int LocalNodeId { get; internal set; } = -1; // 本机飞船 NodeId
		public string PlayerName { get; private set; } = "Player";

		[Tooltip("状态包发送间隔(ms)，默认 50ms = 20Hz")]
		public float SendIntervalMs = 50f;
		[Tooltip("远程飞船插值渲染延迟(ms)，容忍抖动/乱序；默认 100ms ≈ 2 包 @ 20Hz")]
		public float RenderDelayMs = 100f;
		/// <summary>当前状态包发送频率（Hz）。房主可用 SetTickRate 指令调整并广播给客户端（SP2 ServerTickRate 同款思路）。</summary>
		public int TickRate { get; private set; } = 30;
		/// <summary>本端到对端（房主）的往返延迟（RTT，毫秒），客户端侧显示自己延迟用；-1 = 尚未测得。</summary>
		public int ClientPingMs { get; internal set; } = -1;
		[Tooltip("对端超时判定(ms)。TCP 下连接断开由 read loop 检测，此值仅用于半开连接兜底，应设得较大以容忍主线程卡顿/GC/场景加载/全屏切换")]
		public long TimeoutMs = 60000;

		/// <summary>收到远程玩家加入。</summary>
		public event Action<MpPeer> OnPlayerJoined;
		/// <summary>远程玩家离开/掉线。</summary>
		public event Action<MpPeer> OnPlayerLeft;
		/// <summary>收到远程飞船状态（playerId, nodeId, 时间, recdata）。</summary>
		public event Action<int, int, double, Mod.RemoteDataPack> OnRemoteState;

		// FlightUI 提示：已提示过"加入"的玩家（房主可能重发 PlayerJoin，避免重复提示）
		// FlightUI 提示：已提示过"加入"的玩家（房主可能重发 PlayerJoin，避免重复提示）
		private readonly HashSet<int> _joinNoticeShown = new HashSet<int>();
		/// <summary>客户端刚加入后的宽限期：期间收到的 PlayerJoin 均为"已存在玩家"（含房主），不弹 joined 提示。</summary>
		private const float JoinNoticeGraceSec = 3f;
		private float _clientJoinedTime = -1f;

		// ---- 职责组件（2026-09-22 重构:原上帝类按职责拆分;门面在 Awake 里组装并每帧驱动） ----
		internal MpPlayerRegistry Registry { get; private set; }
		internal MpCraftCatalog Catalog { get; private set; }
		internal LocalCraftSender Sender { get; private set; }
		internal RemoteCraftManager Crafts { get; private set; }
		internal RemoteCraftDriver Driver { get; private set; }
		internal MpMessageRouter Router { get; private set; }

		private void Awake()
		{
			Instance = this;
			Registry = new MpPlayerRegistry(this);
			Catalog = new MpCraftCatalog(this);
			Sender = new LocalCraftSender(this);
			Crafts = new RemoteCraftManager(this);
			Driver = new RemoteCraftDriver(this);
			Router = new MpMessageRouter(this);
			Transport.OnDataReceived += Router.HandlePacket;
			Transport.OnPeerTimeout += Registry.HandlePeerTimeout;
			Mod.LogLobby("MP build r10 2026-09-19 (= r6 baseline: r4 id-remap + r5 stable-anchor + bodyNames/rbΔ diag; r7 orbit / r8 freeze / r9 SP2 dead-reckon all removed)");
			Mod.LogLobby("MpNetworkManager created on GameObject '" + gameObject.name + "' (Awake)");
		}

		private void OnDestroy()
		{
			Mod.LogLobby("MpNetworkManager destroyed (OnDestroy)");
			Transport.OnDataReceived -= Router.HandlePacket;
			Transport.OnPeerTimeout -= Registry.HandlePeerTimeout;
			Transport.Stop();
			Crafts.CancelPendingSpawns();
			if (Instance == this) Instance = null;
		}

		// ---------------- 对外广播(事件) ----------------
		// 原实现在 Awake 里"自己订阅自己的事件"(OnPlayerLeft += HandlePlayerLeft 等)形成隐式调用链;
		// 重构改为显式直调,调用顺序与原订阅顺序一致:内部清理 → UI 提示 → 对外事件。

		/// <summary>玩家加入:UI 提示后广播事件(顺序同重构前:ShowPlayerJoinedNotice → 订阅者)。</summary>
		internal void RaisePlayerJoined(MpPeer peer)
		{
			ShowPlayerJoinedNotice(peer);
			OnPlayerJoined?.Invoke(peer);
		}

		/// <summary>玩家离开:先清理幽灵飞船,再 UI 提示,最后广播事件(顺序同重构前)。</summary>
		internal void RaisePlayerLeft(MpPeer peer)
		{
			Crafts.HandlePlayerLeft(peer);
			ShowPlayerLeftNotice(peer);
			OnPlayerLeft?.Invoke(peer);
		}

		/// <summary>收到远程状态:先交给幽灵管理(生成/入缓冲),再广播事件(顺序同重构前)。</summary>
		internal void RaiseRemoteState(int playerId, int nodeId, double time, Mod.RemoteDataPack data)
		{
			Crafts.HandleRemoteState(playerId, nodeId, time, data);
			OnRemoteState?.Invoke(playerId, nodeId, time, data);
		}

		/// <summary>
		/// 发包出口（消重：原 ProcessOutgoing/SendKeepAlive 各写一遍同型分支）：
		/// 房主广播；客户端发给房主，由房主中继转发。
		/// </summary>
		internal void SendOrBroadcastToNet(byte[] packet)
		{
			if (IsServer)
			{
				Transport.Broadcast(packet);
			}
			else
			{
				// 客户端：发给房主，由房主转发
				foreach (MpPeer peer in Transport.GetPeers())
				{
					if (peer.IsServer) { Transport.SendTo(peer, packet); break; }
				}
			}
		}

		/// <summary>
		/// 判断某个 CraftNode 是否为"幽灵(远程)飞船"。
		/// 供 Harmony patch(JetEngineGhostPatch)在游戏飞行循环回调里快速判定:幽灵航发的
		/// IFlightFixedUpdate/IFlightUpdate 需跳过,尾焰改由 EngineVisualSync 直接驱动。
		/// </summary>
		public static bool IsRemoteCraftNode(CraftNode node)
		{
			if (node == null || Instance == null) return false;
			return Instance.Crafts.IsRemoteCraftNode(node);
		}

		/// <summary>通过 FlightUI 显示联机提示（仅飞行场景内可弹 UI；任何情况都写日志兜底）。</summary>
		public static void ShowFlightMessage(string message, bool isError = false, float duration = 6f)
		{
			Mod.LogLobby(message);
			try
			{
				if (FlightSceneScript.Instance != null && FlightSceneScript.Instance.FlightSceneUI != null)
				{
					FlightSceneScript.Instance.FlightSceneUI.ShowMessage(message, isError, duration);
				}
			}
			catch (Exception e) { Mod.LogError("ShowFlightMessage error: " + e.Message); }
		}

		// ---------------- 生命周期 API ----------------

		/// <summary>作为房主开启房间。</summary>
		public bool Host(int port)
		{
			Stop();
			// 房主名字也取 ModSettings 配置(否则默认 "Player" 覆盖设置值)
			try { PlayerName = ModSettings.Instance.PlayerName.Value; }
			catch { PlayerName = "Player"; }
			if (string.IsNullOrWhiteSpace(PlayerName)) PlayerName = "Player";
			Mod.LogLobby("MP.Host(): starting " + Transport.GetType().Name + " on port " + port + " ...");
			if (!Transport.Start(port))
			{
				Mod.LogError("MP.Host FAILED: Transport.Start(" + port + ") returned false (port may be in use)");
				return false;
			}
			IsServer = true;
			IsConnected = true;
			PlayerId = 0;
			LocalNodeId = LocalCraftSender.GetLocalCraftNodeId();
			Mod.LogLobby("MP.Host SUCCESS: port=" + port + ", boundLocalPort=" + Transport.LocalPort +
				", IsServer=" + IsServer + ", IsConnected=" + IsConnected +
				", PlayerId=" + PlayerId + ", LocalNodeId=" + LocalNodeId +
				", Transport.IsRunning=" + Transport.IsRunning +
				", peerCount=" + Transport.GetPeersCount());
			return true;
		}

		/// <summary>作为客户端加入房主。</summary>
		public bool Join(string host, int port, string playerName)
		{
			Stop();
			// 未显式传名时回退到 ModSettings 配置的玩家名(避免默认 "Player" 覆盖设置值)
			if (string.IsNullOrWhiteSpace(playerName))
			{
				try { playerName = ModSettings.Instance.PlayerName.Value; }
				catch { playerName = "Player"; }
				if (string.IsNullOrWhiteSpace(playerName)) playerName = "Player";
			}
			PlayerName = playerName;
			LocalNodeId = LocalCraftSender.GetLocalCraftNodeId();
			Catalog._craftReported = false;
			byte[] hello = MpMessages.EncodeHello(PlayerName);
			Mod.LogLobby("MP.Join(): connecting to " + host + ":" + port + " as '" + PlayerName + "' ...");
			if (!Transport.StartClient(host, port, hello))
			{
				Mod.LogError("MP.Join FAILED: Transport.StartClient(" + host + ":" + port + ") returned false");
				return false;
			}
			IsServer = false;
			IsConnected = true; // 握手完成后视为已连接（Welcome 用于同步身份）
			Mod.LogLobby("MP.Join SUCCESS: host=" + host + ":" + port +
				", boundLocalPort=" + Transport.LocalPort + ", IsConnected=" + IsConnected +
				", PlayerName='" + PlayerName + "', LocalNodeId=" + LocalNodeId +
				", peerCount=" + Transport.GetPeersCount() +
				" (waiting for Welcome to receive PlayerId)");
			return true;
		}

		public void Stop()
		{
			bool wasServer = IsServer;
			bool wasConnected = IsConnected;
			int wasPlayerId = PlayerId;
			Transport.Stop();
			IsServer = false;
			IsConnected = false;
			PlayerId = -1;
			LocalNodeId = -1;
			Catalog.OnSessionStopped();
			Crafts.ClearSpawnTracking();
			ResetForStop();
			Registry.Clear();
			Crafts.DestroyAllRemoteCrafts();
			Mod.LogLobby("MP.Stop: wasServer=" + wasServer + ", wasConnected=" + wasConnected +
				", wasPlayerId=" + wasPlayerId + ", Transport.IsRunning=" + Transport.IsRunning);
		}

		/// <summary>
		/// 切换到指定传输实例（debug 用：切到 TcpTransport 走本地 TCP，虚拟机按宿主 IP:端口 连接）。
		/// 会停止当前会话、退订旧传输事件、挂接新传输事件。默认仍为 SteamTransport，仅在显式调用时切换。
		/// </summary>
		public void SetTransport(IMpTransport newTransport)
		{
			if (ReferenceEquals(newTransport, Transport)) return;
			if (Transport != null)
			{
				if (Transport.IsRunning) Stop();
				Transport.OnDataReceived -= Router.HandlePacket;
				Transport.OnPeerTimeout -= Registry.HandlePeerTimeout;
				Transport.Dispose();
			}
			Transport = newTransport;
			if (Transport != null)
			{
				Transport.OnDataReceived += Router.HandlePacket;
				Transport.OnPeerTimeout += Registry.HandlePeerTimeout;
			}
			Mod.LogLobby("MP.SetTransport: switched to " + (Transport == null ? "<null>" : Transport.GetType().Name));
		}

		/// <summary>
		/// 刷新本机飞船 NodeId 并上报（进入飞行场景或飞船变化时调用）。
		/// 客户端把本机飞船（NodeId + craft XML）发给房主；
		/// 房主广播 PlayerJoin（含 XML）让所有客户端知道自己的飞船。
		/// </summary>
		public void RefreshLocalCraft()
		{
			Catalog.ReportLocalCraft();
		}

		/// <summary>
		/// 进入飞行场景时由 Mod.OnSceneLoaded 调用：
		/// 清理上一场景遗留的远程飞船引用。场景重载/全屏切换会把旧 CraftNode 卸载销毁，
		/// 残留引用会导致新场景中状态包无法重新生成远程飞船（ApplyRemoteState 认为已存在）。
		/// 清空后收到状态包会按"尚未生成"分支用真实位置重新 SpawnCraft。
		/// </summary>
		public void OnFlightSceneLoaded()
		{
			Crafts.OnFlightSceneLoaded();
		}

		// ---------------- 主循环 ----------------

		private void Update()
		{
			if (!IsConnected) return;
			Transport.DrainIncoming();
			// 本机飞船未上报/未确认的重发节流(客户端 CraftData / 房主 host craft)
			Catalog.UpdateResendTimers();
			Sender.ProcessOutgoing();
			Driver.UpdateRemoteCrafts();
			Crafts.EnforceRemoteCraftVisuals();
			Sender.SendKeepAlive();
			Transport.CheckTimeouts(TimeoutMs);
		}

		/// <summary>
		/// 游戏更新后、渲染前,强制应用远程飞船朝向(LunaMultiplayer 方案:RotateY(θ_recv_planet)×SrfRel)+ body,
		/// 防止游戏 Update 阶段覆盖 transform/CenterOfMass 朝向(如 RecalculateCenterOfMass 把质心朝向
		/// 覆盖为命令舱逻辑朝向)。
		/// </summary>
		private void LateUpdate()
		{
			Driver.LateUpdateWriteBacks();
		}

		/// <summary>
		/// 设置状态包发送频率（Hz）。房主调用会广播给所有客户端（客户端收到 TickRate 消息后同样调用本方法，不再广播）：
		/// - 发包间隔 SendIntervalMs = 1000 / hz（驱动 ProcessOutgoing）；
		/// - 插值渲染延迟 RenderDelayMs 自动校准为约 2 个发包周期，保证平滑插帧在任意 tickrate 下都成立。
		/// </summary>
		public void SetTickRate(int hz)
		{
			int clamped = Mathf.Clamp(hz, 1, 120);
			if (TickRate == clamped) return; // 无变化
			TickRate = clamped;
			SendIntervalMs = 1000f / clamped;
			RenderDelayMs = Mathf.Clamp(2000f / clamped, 40f, 400f);
			Mod.LogLobby("MP.SetTickRate: " + clamped + " Hz (interval=" + SendIntervalMs.ToString("F1") +
				"ms, renderDelay=" + RenderDelayMs.ToString("F1") + "ms, IsServer=" + IsServer + ")");
			if (IsServer)
			{
				Transport.Broadcast(MpMessages.EncodeTickRate(clamped));
			}
		}

		/// <summary>
		/// 房主：踢出指定玩家（发 Kick 通知 + 断开传输连接 + 移除记录 + 广播 PlayerLeave + 触发 OnPlayerLeft 清理远程飞船）。
		/// </summary>
		public void KickPlayer(int playerId)
		{
			Registry.KickPlayer(playerId);
		}

		public IReadOnlyCollection<MpPeer> GetPlayers()
		{
			return Registry.GetPlayers();
		}

		/// <summary>指定玩家当前预加载进度（0..1）；未在加载返回 null（供 MultiPlayerUI 玩家列表显示 "⏳ N%"）。</summary>
		public float? GetPlayerLoadProgress(int playerId)
		{
			return Crafts.GetPlayerLoadProgress(playerId);
		}

		/// <summary>客户端成功加入房间:记录加入时刻,宽限期内不把房主补发的「已存在玩家」当新加入提示。</summary>
		internal void OnWelcomeReceived() { _clientJoinedTime = Time.unscaledTime; }

		/// <summary>停止联机时复位提示去重状态(与旧 Stop() 等价)。</summary>
		internal void ResetForStop() { _joinNoticeShown.Clear(); _clientJoinedTime = -1f; }

		/// <summary>有玩家加入：FlightUI 提示（按 playerId 去重，只提示一次）。</summary>
		internal void ShowPlayerJoinedNotice(MpPeer peer)
		{
			if (peer == null || peer.PlayerId < 0) return;
			// 客户端不把房主(playerId 0)当"新加入"提示（房主是房间创建者，避免与"连接成功"混淆）
			if (!IsServer && peer.PlayerId == 0) return;
			// 客户端刚连接时，房主会把"已存在的玩家"补发过来——这些不是新加入，宽限期内不提示
			if (!IsServer && _clientJoinedTime >= 0f && Time.unscaledTime - _clientJoinedTime < JoinNoticeGraceSec)
			{
				return;
			}
			if (!_joinNoticeShown.Add(peer.PlayerId)) return;
			string name = string.IsNullOrEmpty(peer.PlayerName) ? ("Player " + peer.PlayerId) : peer.PlayerName;
			ShowFlightMessage(Locale.GetString("MultiPlayer.MultiPlayerUI.PlayerJoined", name));
		}


		/// <summary>有玩家离开：FlightUI 提示。</summary>
		internal void ShowPlayerLeftNotice(MpPeer peer)
		{
			if (peer == null || peer.PlayerId < 0) return;
			// 客户端不把房主(playerId 0)离开当"玩家离开"提示（房主掉线由连接断开处理）
			if (!IsServer && peer.PlayerId == 0) return;
			string name = string.IsNullOrEmpty(peer.PlayerName) ? ("Player " + peer.PlayerId) : peer.PlayerName;
			ShowFlightMessage(Locale.GetString("MultiPlayer.MultiPlayerUI.PlayerLeft", name), false, 5f);
		}
	}
}
