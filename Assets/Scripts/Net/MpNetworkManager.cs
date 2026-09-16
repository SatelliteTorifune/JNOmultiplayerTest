using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Xml.Linq;
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

namespace Assets.Scripts.Net
{
	/// <summary>
	/// 联机网络管理器（主机中继模式）：
	/// - 客户端把状态包发给房主，房主转发给其他所有客户端；
	/// - 房主负责房间管理（Hello/Welcome/PlayerJoin/PlayerLeave）。
	/// 由 Mod 在飞行场景挂载到独立 GameObject 上。
	/// </summary>
	[DefaultExecutionOrder(1000)]
	public class MpNetworkManager : MonoBehaviour
	{
		public static MpNetworkManager Instance { get; private set; }

		[NonSerialized] 
		// 传输层切换点：
		// - SteamTransport：Steam P2P（Steam Networking Sockets），零端口转发/零 frp，最推荐（SP2 的 FishySteamworks 同款）。
		// - TcpTransport：TCP，可走 frp/nginx 等纯 TCP 内网穿透（无 MTU 限制，无需分片）；缺点 head-of-line blocking。
		// - LiteNetLibTransport：UDP + 可靠/不可靠通道分离 + 应用层分片；缺点公网需 UDP 端口转发。
		// 默认 Steam；本地虚拟机 debug 时用控制台 TcpHostLobby / TcpJoinLobby 切到 TcpTransport（见 SetTransport）。
		public IMpTransport Transport = new SteamTransport();

		public bool IsServer { get; private set; }
		public bool IsConnected { get; private set; }
		public int PlayerId { get; private set; } = -1;    // 房主 = 0
		public int LocalNodeId { get; private set; } = -1; // 本机飞船 NodeId
		public string PlayerName { get; private set; } = "Player";

		[Tooltip("状态包发送间隔(ms)，默认 50ms = 20Hz")]
		public float SendIntervalMs = 50f;
		[Tooltip("远程飞船插值渲染延迟(ms)，容忍抖动/乱序；默认 100ms ≈ 2 包 @ 20Hz")]
		public float RenderDelayMs = 100f;
		/// <summary>当前状态包发送频率（Hz）。房主可用 SetTickRate 指令调整并广播给客户端（SP2 ServerTickRate 同款思路）。</summary>
		public int TickRate { get; private set; } = 30;
		/// <summary>本端到对端（房主）的往返延迟（RTT，毫秒），客户端侧显示自己延迟用；-1 = 尚未测得。</summary>
		public int ClientPingMs { get; private set; } = -1;
		[Tooltip("对端超时判定(ms)。TCP 下连接断开由 read loop 检测，此值仅用于半开连接兜底，应设得较大以容忍主线程卡顿/GC/场景加载/全屏切换")]
		public long TimeoutMs = 60000;

		/// <summary>客户端未收到房主 CraftDataAck 时，CraftData 重发间隔（秒）。</summary>
		private const float CraftResendIntervalSec = 1.5f;

		/// <summary>
		/// 本机游戏暂停时的状态包发送间隔下限（ms，≈8Hz）。暂停中位置/速度都不变，
		/// 只需让对端知道"我还活着且处于暂停"，无需全速上报；恢复后立即回到 <see cref="SendIntervalMs"/>。
		/// </summary>
		private const float PausedSendIntervalMs = 125f;

		/// <summary>
		/// 远程船"冻结/解冻"时外推量过渡时长(秒)。冻结瞬间把外推量收敛到固定单向延迟(0.15s 内),
		/// 解冻瞬间再放回"延迟+包龄",避免状态切换本身造成位置跳变(抽搐)。
		/// </summary>
		private const float RemoteFreezeBlendSec = 0.15f;

		// --- 2 阶外推(acceleration-smoothing-2026-09-14):发送端采样 EMA/钳制 + 接收端开关 ---
		/// <summary>发送端加速度 EMA 系数(每包 20Hz;Acceleration 是刚体速度差分测量,一帧滞后+噪声,必须平滑)。</summary>
		private const float SenderAccelEmaRate = 0.2f;
		/// <summary>发送端角速度 EMA 系数(每包)。</summary>
		private const float SenderAngVelEmaRate = 0.2f;
		/// <summary>加速度幅值钳制(m/s²):外推项 ½·a·ext² 在 ext≤1s 时 ≤30m,超钳制值视为噪声/坏数据。</summary>
		private const float MaxAccelMs = 60f;
		/// <summary>角速度幅值钳制(rad/s,≈0.5 rev/s):防快速自旋/异常包让朝向外推过量。</summary>
		private const float MaxAngVelRad = 3f;
		/// <summary>接收端平移 2 阶外推(½·a·ext²)总开关。加速度域无符号约定问题,可安全开启。</summary>
		private const bool EnableSecondOrderExtrap = true;
		/// <summary>
		/// 接收端朝向外推(ω·ext 右乘)总开关。ω 的 SR2 符号翻转约定待实测
		/// (发送端 sendDiag 自校验 errF+/errF-/errR+ 取最小者,见 plans/acceleration-smoothing-2026-09-14.md §六-1),
		/// 确认前默认关闭,避免朝向外推方向错误反而劣化现有平滑。
		/// </summary>
		private const bool EnableRotationExtrap = false;
		/// <summary>朝向外推符号(实测确认后 ±1)。</summary>
		private const float RotationExtrapSign = 1f;

		private readonly Dictionary<int, MpPeer> _playersByPlayerId = new Dictionary<int, MpPeer>();
		private readonly Dictionary<int, RemoteCraft> _remoteCrafts = new Dictionary<int, RemoteCraft>();
		private readonly HashSet<int> _spawnMissLogged = new HashSet<int>();
		private readonly Dictionary<int, float> _spawnAttemptTime = new Dictionary<int, float>(); // 生成尝试节流
		private float _sendTimer;
		private float _keepAliveTimer;
		// --- 发送节奏诊断(2026-09-14,顿挫定位):实际发包间隔 EMA(sendGap,ms)。 ---
		// 与接收端 MP gap(到达间隔)对账:sendGap 稳定≈50ms 而接收端 gap 大 → 网络突发(relay);
		// sendGap 本身大幅摆动 → 发送端自身突发(帧率不足/掉帧),先修发送端。
		private float _lastSendTime = -1f;
		private float _sendGapEmaMs = 0f;
		// --- 抽搐诊断(发送端):每包 body[0] 相对 comRot 采样位置抖动(静止时>0.01m → 发送端数据本身在抖) ---
		private Vector3? _diagBody0Rel;
		private float rcDiagBody0RelDelta;
		private float _diagBody0RelLogTime;
		// --- 2 阶外推(发送端):加速度/角速度 EMA 状态 + 原始采样诊断 + ω 符号自校验状态 ---
		private Vector3 _accelEma; private bool _hasAccelEma;
		private Vector3 _angVelEma; private bool _hasAngVelEma;
		private Vector3 _accelRawDiag; private Vector3 _angVelRawDiag;
		private Quaternion? _diagPrevSrfRel;   // 上一 sendDiag 时刻的 SrfRel(ω 符号自校验)
		private float _diagPrevSrfTime;
		private float _craftResendTimer; // 客户端重发 CraftData 节流计时
		private float _hostCraftResendTimer; // 房主重发 host craft（PlayerJoin）节流计时
		private bool _craftReported;      // 本机飞船已上报且被房主确认（客户端收到 CraftDataAck 才置 true）
		// 房主：记录"已发给客户端、但尚未收到 PlayerJoinAck 确认"的 host craft（key=peer.EndPoint）。
		private readonly Dictionary<string, float> _hostCraftResend = new Dictionary<string, float>();
		private string _localCraftXml = string.Empty;

		// SP2 按需下载：客户端缓存 hash->xml，避免重复下载同一飞船。
		private readonly Dictionary<string, string> _xmlCache = new Dictionary<string, string>();
		// 已请求但尚未收到响应的 playerId -> 请求时的 hash（hash 变化时重新请求，防止飞船更新后漏拉）。
		private readonly Dictionary<int, string> _pendingXmlRequests = new Dictionary<int, string>();

		// SP2 异步 prefab 预加载（plans/PLAN_AsyncPrefabPreload.md）：
		// 预加载期间状态包只刷新"最新状态"，不再重复起生成协程；玩家离开/场景切换时清理进度框与挂起状态。
		/// <summary>正在预加载/生成远程飞船的玩家集合（防止状态包在预加载期间重复起协程）。</summary>
		private readonly HashSet<int> _pendingSpawns = new HashSet<int>();
		/// <summary>预加载期间收到的最新状态包（生成时用最新位置，减少长时间预加载后的跳变）。</summary>
		private readonly Dictionary<int, Mod.RemoteDataPack> _pendingSpawnLatest = new Dictionary<int, Mod.RemoteDataPack>();
		/// <summary>玩家 -> 加载进度框（玩家离开/场景切换/停止时销毁，防残留）。</summary>
		private readonly Dictionary<int, MpCraftLoadingIndicator> _loadingIndicators = new Dictionary<int, MpCraftLoadingIndicator>();
		/// <summary>玩家 -> 预加载进度（0..1；供 MultiPlayerUI 玩家列表显示 "⏳ N%"）。</summary>
		private readonly Dictionary<int, float> _playerLoadProgress = new Dictionary<int, float>();

		/// <summary>收到远程玩家加入。</summary>
		public event Action<MpPeer> OnPlayerJoined;
		/// <summary>远程玩家离开/掉线。</summary>
		public event Action<MpPeer> OnPlayerLeft;
		/// <summary>收到远程飞船状态（playerId, nodeId, 时间, recdata）。</summary>
		public event Action<int, int, double, Mod.RemoteDataPack> OnRemoteState;

		// FlightUI 提示：已提示过"加入"的玩家（房主可能重发 PlayerJoin，避免重复提示）
		private readonly HashSet<int> _joinNoticeShown = new HashSet<int>();
		/// <summary>客户端刚加入后的宽限期：期间收到的 PlayerJoin 均为"已存在玩家"（含房主），不弹 joined 提示。</summary>
		private const float JoinNoticeGraceSec = 3f;
		private float _clientJoinedTime = -1f;

		private void Awake()
		{
			Instance = this;
			Transport.OnDataReceived += HandlePacket;
			Transport.OnPeerTimeout += HandlePeerTimeout;
			OnPlayerLeft += HandlePlayerLeft;
			OnPlayerJoined += ShowPlayerJoinedNotice;
			OnPlayerLeft += ShowPlayerLeftNotice;
			OnRemoteState += ApplyRemoteState;
			Mod.LogLobby("MpNetworkManager created on GameObject '" + gameObject.name + "' (Awake)");
		}

		private void OnDestroy()
		{
			Mod.LogLobby("MpNetworkManager destroyed (OnDestroy)");
			Transport.OnDataReceived -= HandlePacket;
			Transport.OnPeerTimeout -= HandlePeerTimeout;
			OnPlayerLeft -= HandlePlayerLeft;
			OnPlayerJoined -= ShowPlayerJoinedNotice;
			OnPlayerLeft -= ShowPlayerLeftNotice;
			OnRemoteState -= ApplyRemoteState;
			Transport.Stop();
			CancelPendingSpawns();
			if (Instance == this) Instance = null;
		}

		/// <summary>
		/// 判断某个 CraftNode 是否为"幽灵(远程)飞船"。
		/// 供 Harmony patch(JetEngineGhostPatch)在游戏飞行循环回调里快速判定:幽灵航发的
		/// IFlightFixedUpdate/IFlightUpdate 需跳过,尾焰改由 EngineVisualSync 直接驱动。
		/// </summary>
		public static bool IsRemoteCraftNode(CraftNode node)
		{
			if (node == null || Instance == null) return false;
			foreach (KeyValuePair<int, RemoteCraft> kv in Instance._remoteCrafts)
			{
				if (kv.Value.Node == node) return true;
			}
			return false;
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

		/// <summary>有玩家加入：FlightUI 提示（按 playerId 去重，只提示一次）。</summary>
		private void ShowPlayerJoinedNotice(MpPeer peer)
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
		private void ShowPlayerLeftNotice(MpPeer peer)
		{
			if (peer == null || peer.PlayerId < 0) return;
			// 客户端不把房主(playerId 0)离开当"玩家离开"提示（房主掉线由连接断开处理）
			if (!IsServer && peer.PlayerId == 0) return;
			string name = string.IsNullOrEmpty(peer.PlayerName) ? ("Player " + peer.PlayerId) : peer.PlayerName;
			ShowFlightMessage(Locale.GetString("MultiPlayer.MultiPlayerUI.PlayerLeft", name), false, 5f);
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
			LocalNodeId = GetLocalCraftNodeId();
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
			LocalNodeId = GetLocalCraftNodeId();
			_craftReported = false;
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
			_craftReported = false;
			_craftResendTimer = 0f;
			_hostCraftResendTimer = 0f;
			_hostCraftResend.Clear();
			_spawnMissLogged.Clear();
			_spawnAttemptTime.Clear();
			_joinNoticeShown.Clear();
			_clientJoinedTime = -1f;
			lock (_playersByPlayerId) _playersByPlayerId.Clear();
			// 停止联机时真正销毁所有远程飞船（避免 Stop 后场景里残留幽灵飞船），
			// 已销毁/已随场景卸载的节点跳过。
			foreach (RemoteCraft rc in _remoteCrafts.Values)
			{
				if (rc != null && rc.Node != null && !rc.Node.IsDestroyed)
				{
					try { rc.Node.DestroyCraft(); }
					catch (Exception e) { Mod.LogError("MP.Stop: DestroyCraft error: " + e.Message); }
				}
			}
			_remoteCrafts.Clear();
			// 清理预加载中的进度框与挂起状态（踢人/断开时无残留）
			CancelPendingSpawns();
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
				Transport.OnDataReceived -= HandlePacket;
				Transport.OnPeerTimeout -= HandlePeerTimeout;
				Transport.Dispose();
			}
			Transport = newTransport;
			if (Transport != null)
			{
				Transport.OnDataReceived += HandlePacket;
				Transport.OnPeerTimeout += HandlePeerTimeout;
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
			if (!IsConnected) return;
			int nodeId = GetLocalCraftNodeId();
			if (nodeId < 0) return;

			string craftXml = GetLocalCraftXml();
			bool changed = nodeId != LocalNodeId || !string.Equals(_localCraftXml, craftXml);
			LocalNodeId = nodeId;
			_localCraftXml = craftXml;

			if (IsServer)
			{
				// 房主：广播自己的飞船（只带 hash，XML 由客户端按需下载）给所有客户端；已广播过且未变化则跳过。
				if (_craftReported && !changed) return;
				string craftHash = MpMessages.ComputeXmlHash(craftXml);
				Transport.Broadcast(MpMessages.EncodePlayerJoin(PlayerId, LocalNodeId, PlayerName, craftHash));
				_craftReported = true; // 房主无需确认（新加入者由 OnHello 补发）
				// 对所有已连接 peer 登记待确认：客户端回 PlayerJoinAck 前周期性重发（防公网丢包）。
				foreach (MpPeer p in Transport.GetPeers())
				{
					_hostCraftResend[p.Id] = Time.unscaledTime;
				}
				Mod.LogLobby("MP.RefreshLocalCraft (host): broadcast PlayerJoin playerId=" + PlayerId + ", nodeId=" + LocalNodeId +
					", hash=" + craftHash +
					", xmlLen=" + (craftXml == null ? 0 : craftXml.Length) +
					", pendingAck=" + _hostCraftResend.Count);
			}
			else
			{
				// 客户端：发给房主。发出去后不置 _craftReported，
				// 必须等房主 CraftDataAck 确认（防大分片公网丢包：确认前每 1.5s 重发）。
				bool sent = false;
				foreach (MpPeer peer in Transport.GetPeers())
				{
					if (peer.IsServer)
					{
						Transport.SendTo(peer, MpMessages.EncodeCraftData(LocalNodeId, craftXml));
						Mod.LogLobby("MP.RefreshLocalCraft (client): sent CraftData nodeId=" + LocalNodeId + " to host " + peer.Id +
							", xmlLen=" + (craftXml == null ? 0 : craftXml.Length) +
							", acked=" + _craftReported);
						sent = true;
						break;
					}
				}
				if (!sent)
				{
					Mod.LogLobby("MP.RefreshLocalCraft (client): no server peer found yet, will retry (nodeId=" + LocalNodeId + ")");
				}
			}
			Mod.Log("MP: local craft NodeId=" + LocalNodeId + ", xmlLen=" + (craftXml == null ? 0 : craftXml.Length));
		}

		/// <summary>
		/// 进入飞行场景时由 Mod.OnSceneLoaded 调用：
		/// 清理上一场景遗留的远程飞船引用。场景重载/全屏切换会把旧 CraftNode 卸载销毁，
		/// 残留引用会导致新场景中状态包无法重新生成远程飞船（ApplyRemoteState 认为已存在）。
		/// 清空后收到状态包会按"尚未生成"分支用真实位置重新 SpawnCraft。
		/// </summary>
		public void OnFlightSceneLoaded()
		{
			_remoteCrafts.Clear();
			_spawnMissLogged.Clear();
			_spawnAttemptTime.Clear();
			// 场景切换：上一场景的进度框已随场景卸载销毁，清空登记与挂起状态（新场景可正常重新生成）
			CancelPendingSpawns();
			// Vizzy 隔离的幽灵 NodeId 记忆同样按飞行场景生命周期重置：NodeId 只在本次飞行内唯一，
			// 跨场景复用会把新场景里的本地船误判为幽灵（Vizzy 被误杀）。见 VizzyIsolationPatch。
			VizzyIsolationPatch.ClearGhostNodeCache();
			Mod.LogLobby("MP.OnFlightSceneLoaded: cleared stale remote crafts (count=" + _remoteCrafts.Count + ")");
		}

		// ---------------- 主循环 ----------------

		private void Update()
		{
			if (!IsConnected) return;
			Transport.DrainIncoming();
			if (!_craftReported)
			{
				// 本机飞船未上报/未确认：周期性重试（CraftData 分片在公网可能丢包，
				// 只发一次遇到丢片会导致房主永远收不齐，故确认前持续重发）。
				_craftResendTimer -= Time.unscaledDeltaTime;
				if (_craftResendTimer <= 0f)
				{
					_craftResendTimer = CraftResendIntervalSec;
					RefreshLocalCraft();
				}
			}
			// 房主：已发 host craft 但客户端尚未回 PlayerJoinAck 的 peer，周期性重发
			// （只重发 hash 小包，XML 由客户端按需下载；确认前持续重发防公网丢包）。
			if (IsServer && _hostCraftResend.Count > 0 && LocalNodeId >= 0 && !string.IsNullOrEmpty(_localCraftXml))
			{
				_hostCraftResendTimer -= Time.unscaledDeltaTime;
				if (_hostCraftResendTimer <= 0f)
				{
					_hostCraftResendTimer = CraftResendIntervalSec;
					byte[] hostJoin = MpMessages.EncodePlayerJoin(PlayerId, LocalNodeId, PlayerName, MpMessages.ComputeXmlHash(_localCraftXml));
					foreach (MpPeer peer in Transport.GetPeers())
					{
					if (_hostCraftResend.ContainsKey(peer.Id))
					{
						Transport.SendTo(peer, hostJoin);
						Mod.LogLobby("MP.Update (host): resend host craft PlayerJoin (nodeId=" + LocalNodeId + ") to " +
							peer.Id + " (unacked)");
						}
					}
				}
			}
			ProcessOutgoing();
			UpdateRemoteCrafts();
			EnforceRemoteCraftVisuals();
			SendKeepAlive();
			Transport.CheckTimeouts(TimeoutMs);
		}

		/// <summary>
		/// 游戏更新后、渲染前,强制应用远程飞船朝向(LunaMultiplayer 方案:RotateY(θ_recv_planet)×SrfRel)+ body,
		/// 防止游戏 Update 阶段覆盖 transform/CenterOfMass 朝向(如 RecalculateCenterOfMass 把质心朝向
		/// 覆盖为命令舱逻辑朝向)。
		/// </summary>
		private void LateUpdate()
		{
			if (!IsConnected || _remoteCrafts.Count == 0) return;
			foreach (RemoteCraft rc in _remoteCrafts.Values)
			{
				if (rc.Node == null || rc.Node.CraftScript == null || !rc.HasState || !rc.HasApplied) continue;
				try
				{
					// 用"最近一次实际应用"的插值状态写回朝向（而非最新包 Target），
					// 避免"Update 插值 → LateUpdate 被最新包覆盖"导致的朝向跳变。
					ForceRemoteHeading(rc, rc.LastApplied);
				}
				catch (Exception e) { Mod.LogError("LateUpdate refresh error (P" + rc.PlayerId + "): " + e.Message); }
			}
		}

		private static void ForceRemoteHeading(RemoteCraft rc, Mod.RemoteDataPack data)
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
		private static void ApplyRemoteBodyPoses(RemoteCraft rc, Mod.RemoteDataPack data, Vector3 comRotPos, Quaternion comRotRot)
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
		private static bool TryGetLogicalComPose(RemoteCraft rc, Mod.RemoteDataPack data, IReferenceFrame frame,
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

		private void ProcessOutgoing()
		{
			// 使用 unscaledDeltaTime：游戏暂停（Time.deltaTime==0）时状态包也照常发送，
			// 避免暂停导致对端远程飞船冻结/失步（暂停相关问题的临时处理）。
			_sendTimer += Time.unscaledDeltaTime * 1000f;
			// 暂停时位置/速度都不再变化,无需按全速上报;降到 ~8Hz 仍足以让对端确认"已暂停"
			// 并维持平滑层(带宽/CPU 都省),恢复后立即回到正常速率。
			bool localPaused = FlightSceneScript.Instance != null &&
				FlightSceneScript.Instance.TimeManager != null &&
				FlightSceneScript.Instance.TimeManager.Paused;
			float sendIntervalMs = localPaused ? Mathf.Max(SendIntervalMs, PausedSendIntervalMs) : SendIntervalMs;
			if (_sendTimer < sendIntervalMs) return;
			// 携带余量而非清零:清零会把发送率钳制在渲染帧率(30fps 时只有 15Hz → 对端 gapEMA≈70ms,
			// 高速机动时每包位置跳变更大、外推更易失准 → 顿挫)。减余量后任意 ≥20fps 都稳定发满 20Hz。
			_sendTimer -= sendIntervalMs;
			// F4(2026-09-14,smoothing-comparison §五 / README §三):帧卡顿后 timer 余量大 → 恢复后每帧泄洪一包
			// (发送端自身突发,接收端见成簇包,实测 sendGap 15~30ms 双峰)。钳制余量上限,
			// 卡顿恢复后按正常节奏补发,不一次性灌给网络。
			if (_sendTimer > sendIntervalMs * 2f) _sendTimer = sendIntervalMs * 2f;

			Mod.RemoteDataPack data;
			if (!TrySampleLocalCraft(out data)) return;
			// 客户端在收到 Welcome（拿到 PlayerId）前不发状态包：
			// 否则会以 PlayerId=-1 发包，房主无法关联到已登记玩家（"state for player -1"）。
			if (PlayerId < 0) return;

			
			// 周期性本机朝向/位置诊断日志已移除（原为 if(false) 禁用块；
			// 其内曾被加入过早 return，导致 ProcessOutgoing 每帧提前返回、状态包完全停发）
			double time = FlightSceneScript.Instance.FlightState.Time;
			byte[] packet = MpMessages.EncodeState(PlayerId, LocalNodeId, time, data);
			// 抽搐诊断(发送端):每 1s 输出本机采样数据抖动。若静止时 body0RelΔ 持续>0.01m,
			// 说明发送端数据本身在抖(comRot/body 微动),接收端平滑只能衰减无法消除。
			if (Time.unscaledTime - _diagBody0RelLogTime > 1f)
			{
				_diagBody0RelLogTime = Time.unscaledTime;
				// body0Rel = 包内 body[0] 相对 comRot 的偏移绝对值(接收端根 body 写 comPos,不叠加它;
				// 该值即"游戏放置 vs 我们的写入"的对抗幅度,双端同版本时可直接核对)
				double body0Rel = 0.0;
				if (data.BodyPositions != null && data.BodyPositions.Count > 0)
				{
					body0Rel = data.BodyPositions[0].magnitude;
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
				Mod.LogLobby("MP sendDiag P" + PlayerId +
					": vel=" + data.Velocity.magnitude.ToString("F3") + "m/s" +
					" paused=" + (data.Paused ? 1 : 0) +
					" accRaw=" + _accelRawDiag.magnitude.ToString("F2") + "m/s²" +
					" acc=" + data.Acceleration.magnitude.ToString("F2") + "m/s²" +
					" wRaw=" + _angVelRawDiag.magnitude.ToString("F2") + "rad/s" +
					" w=" + data.AngularVelocity.magnitude.ToString("F2") + "rad/s" +
					" body0RelΔ=" + rcDiagBody0RelDelta.ToString("F4") + "m" +
					" body0Rel=" + body0Rel.ToString("F4") + "m" +
					" bodyCnt=" + (data.BodyPositions != null ? data.BodyPositions.Count : 0) +
					" sendGap=" + (_lastSendTime >= 0f ? _sendGapEmaMs.ToString("F0") : "?") + "ms" +
					" " + wSignDiag);
			}
			// 发送节奏诊断(2026-09-14):实际发包间隔 EMA。记录在真正发包处,过滤采样失败未发帧;
			// 与接收端 MP gap(到达间隔)对账定位突发来源(发送端自身 vs 网络 relay)。
			if (_lastSendTime >= 0f)
			{
				float gap = (Time.unscaledTime - _lastSendTime) * 1000f;
				_sendGapEmaMs = _sendGapEmaMs <= 0f ? gap : _sendGapEmaMs * 0.9f + gap * 0.1f;
			}
			_lastSendTime = Time.unscaledTime;
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
		/// 保活心跳：周期性发送 Ping，未进飞行场景时也能维持对端不超时。
		/// 使用 unscaledDeltaTime，避免游戏暂停时（Time.deltaTime==0）心跳停发导致对端 3 秒超时。
		/// </summary>
		private void SendKeepAlive()
		{
			_keepAliveTimer -= Time.unscaledDeltaTime;
			if (_keepAliveTimer > 0f) return;
			_keepAliveTimer = 1f;

			byte[] ping = MpMessages.EncodePing(DateTime.UtcNow.Ticks);
			if (IsServer)
			{
				Transport.Broadcast(ping);
			}
			else
			{
				foreach (MpPeer peer in Transport.GetPeers())
				{
					if (peer.IsServer) { Transport.SendTo(peer, ping); break; }
				}
			}
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

		// ---------------- 消息处理 ----------------

		private void HandlePacket(MpPeer peer, byte[] packet)
		{
			MpMessageType type = MpMessages.PeekType(packet);
			switch (type)
			{
				case MpMessageType.Hello:
					OnHello(peer, packet);
					break;
				case MpMessageType.Welcome:
					OnWelcome(peer, packet);
					break;
				case MpMessageType.PlayerJoin:
					OnPlayerJoin(peer, packet);
					break;
				case MpMessageType.PlayerLeave:
					OnPlayerLeave(packet);
					break;
				case MpMessageType.CraftData:
					OnCraftData(peer, packet);
					break;
				case MpMessageType.State:
					OnState(packet);
					break;
				case MpMessageType.Pause:
					OnPause(packet);
					break;
				case MpMessageType.Ping:
					// 收到 Ping：回 Pong 并回显对方时间戳，供对方计算 RTT（延迟）
				{
					long tick;
					if (MpMessages.TryDecodePing(packet, out tick)) Transport.SendTo(peer, MpMessages.EncodePong(tick));
					break;
				}
				case MpMessageType.Pong:
					OnPong(peer, packet);
					break;
				case MpMessageType.Kick:
					OnKick(packet);
					break;
				case MpMessageType.CraftDataAck:
					OnCraftDataAck(packet);
					break;
				case MpMessageType.PlayerJoinAck:
					OnPlayerJoinAck(peer, packet);
					break;
				case MpMessageType.CraftXmlRequest:
					OnCraftXmlRequest(peer, packet);
					break;
				case MpMessageType.CraftXmlResponse:
					OnCraftXmlResponse(packet);
					break;
				case MpMessageType.TickRate:
					OnTickRate(packet);
					break;
			}
		}

		/// <summary>
		/// 客户端收到房主的 CraftDataAck：确认房主已完整收到本机飞船（nodeId 匹配），
		/// 此后停止周期性重发 CraftData。
		/// </summary>
		private void OnCraftDataAck(byte[] packet)
		{
			if (IsServer) return;
			int nodeId;
			if (!MpMessages.TryDecodeCraftDataAck(packet, out nodeId)) return;
			if (nodeId < 0) return;
			if (nodeId == LocalNodeId)
			{
				_craftReported = true;
				Mod.LogLobby("MP.OnCraftDataAck (client): host confirmed craft NodeId=" + nodeId + ", stop resending");
			}
		}

		/// <summary>
		/// 房主收到客户端的 PlayerJoinAck：该客户端已收到指定玩家的飞船 XML。
		/// 若确认的是房主自己的飞船（playerId == PlayerId），停止对该 peer 重发 host craft。
		/// </summary>
		private void OnPlayerJoinAck(MpPeer peer, byte[] packet)
		{
			if (!IsServer) return;
			int playerId;
			if (!MpMessages.TryDecodePlayerJoinAck(packet, out playerId)) return;
			if (peer == null) return;
			if (playerId == PlayerId)
			{
				if (_hostCraftResend.Remove(peer.Id))
				{
					Mod.LogLobby("MP.OnPlayerJoinAck (host): peer " + peer.Id + " confirmed host craft, stop resending");
				}
			}
		}

		private void OnHello(MpPeer peer, byte[] packet)
		{
			if (!IsServer) return;
			string name;
			if (!MpMessages.TryDecodeHello(packet, out name)) return;
			peer.PlayerName = name;
			peer.IsServer = false;

			// 分配 PlayerId（房主为 0，后续从 1 开始）
			// 注意：此时还不知道加入者飞船的 NodeId，
			// 需要等加入者进入飞行场景后通过 CraftData 消息上报（见 OnCraftData）。
			peer.PlayerId = NextPlayerId();
			// 立即登记：让房主玩家表在 CraftData 到达前就有该玩家，
			// 否则收到其 State 包时找不到玩家/飞船（"state for player x but no craft info to spawn"）。
			// NodeId/CraftXml 仍为 -1/空，后续由 OnCraftData 更新。
			RegisterPlayer(peer);

			// 回复 Welcome
			Transport.SendTo(peer, MpMessages.EncodeWelcome(peer.PlayerId, -1, DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond));
			// 同步当前状态包频率给新加入者（SP2 ServerTickRate 初始同步思路）
			Transport.SendTo(peer, MpMessages.EncodeTickRate(TickRate));
			Mod.LogLobby("MP.OnHello (host): '" + name + "' from " + peer.Id + " joined as PlayerId=" + peer.PlayerId +
				", sent Welcome + TickRate(" + TickRate + "Hz), total peers=" + Transport.GetPeersCount());

			// 把当前所有已登记玩家（含房主自己）的飞船信息同步给新加入者
			// SP2 方案：只发 hash，XML 由客户端按需下载（见 OnPlayerJoin / CraftXmlRequest）。
			foreach (MpPeer p in GetPlayers())
			{
				if (p.NodeId >= 0 && !string.IsNullOrEmpty(p.CraftXml))
				{
					string hash = MpMessages.ComputeXmlHash(p.CraftXml);
					Transport.SendTo(peer, MpMessages.EncodePlayerJoin(p.PlayerId, p.NodeId, p.PlayerName, hash));
					Mod.LogLobby("MP.OnHello (host): sent existing player " + p.PlayerId + " craft hash to new client (hash=" + hash + ")");
				}
			}
			if (LocalNodeId >= 0 && !string.IsNullOrEmpty(_localCraftXml))
			{
				string hostHash = MpMessages.ComputeXmlHash(_localCraftXml);
				Transport.SendTo(peer, MpMessages.EncodePlayerJoin(PlayerId, LocalNodeId, PlayerName, hostHash));
				Mod.LogLobby("MP.OnHello (host): sent host craft PlayerJoin (nodeId=" + LocalNodeId + ", hash=" + hostHash + ") to new client " + peer.Id);
				// 登记待确认：客户端回 PlayerJoinAck 前，每 1.5s 重发 host craft（防公网丢包）。
				_hostCraftResend[peer.Id] = Time.unscaledTime;
			}
		}

		/// <summary>
		/// 收到加入者上报的本机飞船 NodeId + craft XML（CraftData）。
		/// 房主登记映射，并向所有客户端广播 PlayerJoin。
		/// </summary>
		private void OnCraftData(MpPeer peer, byte[] packet)
		{
			if (!IsServer) return;
			int nodeId; string craftXml;
			if (!MpMessages.TryDecodeCraftData(packet, out nodeId, out craftXml)) return;
			if (nodeId < 0) return;

			peer.NodeId = nodeId;
			peer.CraftXml = craftXml;
			RegisterPlayer(peer);

			// 回 Ack 给上报者：告知已完整收到其飞船，客户端据此停止周期性重发。
			Transport.SendTo(peer, MpMessages.EncodeCraftDataAck(nodeId));

			// SP2 方案：广播 PlayerJoin 只带 hash，其他客户端按需下载 XML（见 OnCraftXmlRequest）。
			string craftHash = MpMessages.ComputeXmlHash(craftXml);
			Transport.Broadcast(MpMessages.EncodePlayerJoin(peer.PlayerId, peer.NodeId, peer.PlayerName, craftHash));
			Mod.LogLobby("MP.OnCraftData (host): '" + peer.PlayerName + "' craft registered NodeId=" + nodeId +
				", hash=" + craftHash +
				", xmlLen=" + (craftXml == null ? 0 : craftXml.Length) +
				", sent CraftDataAck, broadcast PlayerJoin to " + Transport.GetPeersCount() + " peer(s)");
			OnPlayerJoined?.Invoke(peer);
		}

		private void OnWelcome(MpPeer peer, byte[] packet)
		{
			if (IsServer) return;
			int playerId, nodeId; long serverTick;
			if (!MpMessages.TryDecodeWelcome(packet, out playerId, out nodeId, out serverTick)) return;
			PlayerId = playerId;
			// 修复：不要把 peer(房主连接) 的 PlayerId 设成客户端自己的 ID。
			// 房主 peer 的 PlayerId 应保持 0（房主身份），否则超时日志/寻址会错乱
			// （此前把 host peer 的 PlayerId 覆盖成客户端 ID，导致超时日志显示 PlayerId=1 等错乱）。
			peer.IsServer = true;
			// client 成功加入房间：FlightUI 提示；并记录加入时刻，宽限期内不提示"已存在玩家"
			_clientJoinedTime = Time.unscaledTime;
			string myName = string.IsNullOrEmpty(PlayerName) ? ("Player " + playerId) : PlayerName;
			ShowFlightMessage(Locale.GetString("MultiPlayer.MultiPlayerUI.ConnectedToHost", myName, playerId));
			Mod.LogLobby("MP.OnWelcome (client): received Welcome, PlayerId=" + playerId +
				", nodeId=" + nodeId + ", serverTick=" + serverTick + ", peer=" + peer.Id +
				", peer.PlayerId=" + peer.PlayerId + " (host peer, kept as-is)");
		}

		private void OnPlayerJoin(MpPeer peer, byte[] packet)
		{
			int playerId, nodeId; string playerName, craftXmlHash;
			if (!MpMessages.TryDecodePlayerJoin(packet, out playerId, out nodeId, out playerName, out craftXmlHash)) return;
			// 自己无需下载自己的飞船（房主广播时会带上报者本人，此处跳过）
			if (playerId == PlayerId) return;

			MpPeer p = new MpPeer { EndPoint = peer.EndPoint, SteamId = peer.SteamId, PlayerId = playerId, NodeId = nodeId, PlayerName = playerName };
			RegisterPlayer(p);
			Mod.LogLobby("MP.OnPlayerJoin: playerId=" + playerId + ", nodeId=" + nodeId +
				", hash=" + (craftXmlHash ?? "null") + ", peer=" + peer.Id +
				", isServer=" + IsServer + ", inFlightScene=" + (FlightSceneScript.Instance != null));

			// 客户端回 PlayerJoinAck：告知房主已收到该玩家飞船信息，房主据此停止重发（防公网丢包）。
			if (!IsServer && peer != null)
			{
				Transport.SendTo(peer, MpMessages.EncodePlayerJoinAck(playerId));
			}

			// SP2 按需下载：本地缓存命中直接使用；否则向房主请求该玩家飞船 XML。
			if (!IsServer)
			{
				if (string.IsNullOrEmpty(craftXmlHash))
				{
					Mod.Log("MP.OnPlayerJoin: player " + playerId + " has no craft hash, nothing to download");
					return;
				}
				if (_xmlCache.TryGetValue(craftXmlHash, out string cachedXml))
				{
					p.CraftXml = cachedXml;
					Mod.LogLobby("MP.OnPlayerJoin: cache hit for player " + playerId + " (hash=" + craftXmlHash + ", xmlLen=" + cachedXml.Length + ")");
					OnPlayerJoined?.Invoke(p);
				}
				else
				{
					// 若已在请求且 hash 未变，不重复请求；hash 变化（飞船更新）则重新请求。
					string pendingHash;
					bool alreadyPending = _pendingXmlRequests.TryGetValue(playerId, out pendingHash) && pendingHash == craftXmlHash;
					if (!alreadyPending)
					{
						_pendingXmlRequests[playerId] = craftXmlHash;
						Transport.SendTo(peer, MpMessages.EncodeCraftXmlRequest(playerId, craftXmlHash));
						Mod.LogLobby("MP.OnPlayerJoin: requested craft xml for player " + playerId + " (hash=" + craftXmlHash + ")");
					}
				}
			}
			else
			{
				OnPlayerJoined?.Invoke(p);
			}
			// M2：根据 craftXml 生成远程飞船（xml 到位后由 OnCraftXmlResponse 触发 OnPlayerJoined）
		}

		/// <summary>
		/// 房主：响应客户端的按需下载请求，把指定玩家的飞船 XML 发给请求者（大包，走可靠通道+分片）。
		/// </summary>
		private void OnCraftXmlRequest(MpPeer peer, byte[] packet)
		{
			if (!IsServer) return;
			int playerId; string hash;
			if (!MpMessages.TryDecodeCraftXmlRequest(packet, out playerId, out hash)) return;
			string craftXml = null;
			if (playerId == PlayerId)
			{
				// 房主自己（playerId=0）不在 _playersByPlayerId 表中，单独用 _localCraftXml 响应。
				craftXml = _localCraftXml;
			}
			else
			{
				MpPeer target = null;
				lock (_playersByPlayerId) { _playersByPlayerId.TryGetValue(playerId, out target); }
				if (target != null) craftXml = target.CraftXml;
			}
			if (string.IsNullOrEmpty(craftXml))
			{
				Mod.Log("MP.OnCraftXmlRequest: player " + playerId + " has no craft xml yet");
				return;
			}
			// 用实际 XML 的 hash 响应（而非请求带过来的 hash），保证客户端缓存 key 正确（飞船中途变化时）
			string actualHash = MpMessages.ComputeXmlHash(craftXml);
			Transport.SendTo(peer, MpMessages.EncodeCraftXmlResponse(playerId, actualHash, craftXml));
			Mod.LogLobby("MP.OnCraftXmlRequest (host): sent craft xml for player " + playerId + " to " + peer.Id +
				", reqHash=" + hash + ", actualHash=" + actualHash + ", xmlLen=" + craftXml.Length);
		}

		/// <summary>
		/// 客户端：收到按需下载的飞船 XML。填入玩家信息并触发 OnPlayerJoined（远程飞船在此后生成）。
		/// </summary>
		private void OnCraftXmlResponse(byte[] packet)
		{
			if (IsServer) return;
			int playerId; string hash; string craftXml;
			if (!MpMessages.TryDecodeCraftXmlResponse(packet, out playerId, out hash, out craftXml)) return;
			_pendingXmlRequests.Remove(playerId);
			if (!string.IsNullOrEmpty(hash) && !string.IsNullOrEmpty(craftXml))
			{
				_xmlCache[hash] = craftXml;
			}
			MpPeer p = null;
			lock (_playersByPlayerId) { _playersByPlayerId.TryGetValue(playerId, out p); }
			if (p == null)
			{
				Mod.Log("MP.OnCraftXmlResponse: player " + playerId + " not registered, xml discarded");
				return;
			}
			p.CraftXml = craftXml;
			Mod.LogLobby("MP.OnCraftXmlResponse (client): received craft xml for player " + playerId +
				", hash=" + hash + ", xmlLen=" + (craftXml == null ? 0 : craftXml.Length));
			OnPlayerJoined?.Invoke(p);
		}

		private void OnPlayerLeave(byte[] packet)
		{
			int playerId;
			if (!MpMessages.TryDecodePlayerLeave(packet, out playerId)) return;
			MpPeer removed = null;
			lock (_playersByPlayerId)
			{
				if (_playersByPlayerId.TryGetValue(playerId, out removed))
				{
					_playersByPlayerId.Remove(playerId);
				}
			}
			Mod.LogLobby("MP.OnPlayerLeave: playerId=" + playerId + (removed != null ? " removed" : " (not found)"));
			_pendingXmlRequests.Remove(playerId);
			if (removed != null) OnPlayerLeft?.Invoke(removed);
		}

		private void OnState(byte[] packet)
		{
			int playerId, nodeId; double time; Mod.RemoteDataPack data;
			if (!MpMessages.TryDecodeState(packet, out playerId, out nodeId, out time, out data)) return;
			if (playerId == PlayerId) return; // 忽略本机状态回显
			OnRemoteState?.Invoke(playerId, nodeId, time, data);

			// 主机中继：房主把状态转发给其他所有客户端
			if (IsServer)
			{
				Transport.Broadcast(packet);
			}
		}

		private void OnPause(byte[] packet)
		{
			// 临时禁用：暂停/恢复同步与本次"暂停相关问题"冲突，M2 验证阶段暂时关闭，
			// 待定位清楚后再恢复（避免一端暂停导致另一端状态/时间错乱）。
			// bool paused;
			// if (!MpMessages.TryDecodePause(packet, out paused)) return;
			// if (FlightSceneScript.Instance != null)
			// {
			// 	FlightSceneScript.Instance.TimeManager.RequestPauseChange(paused, false);
			// }
		}

		/// <summary>
		/// 客户端收到房主的 TickRate（状态包频率）：本地采纳该频率，保证双端发包节奏一致，
		/// 平滑插帧（P1）与渲染延迟据此自动校准。
		/// </summary>
		private void OnTickRate(byte[] packet)
		{
			int hz;
			if (!MpMessages.TryDecodeTickRate(packet, out hz)) return;
			SetTickRate(hz);
		}

		/// <summary>
		/// 收到 Pong：回显的时间戳即对方收到我们 Ping 的时刻。
		/// - 房主：对每个客户端测量 RTT 存入 peer.PingMs（供房主玩家列表显示延迟）；
		/// - 客户端：测量自己到房主的 RTT 存入 ClientPingMs（供客户端显示自己延迟）。
		/// </summary>
		private void OnPong(MpPeer peer, byte[] packet)
		{
			long tick;
			if (!MpMessages.TryDecodePong(packet, out tick) || tick <= 0) return;
			long rttMs = (DateTime.UtcNow.Ticks - tick) / TimeSpan.TicksPerMillisecond;
			if (rttMs < 0) rttMs = 0;
			if (IsServer)
			{
				// 轻微平滑，避免显示跳动
				peer.PingMs = peer.PingMs < 0 ? (int)rttMs : (int)(peer.PingMs * 0.7 + rttMs * 0.3);
				// 同步到该玩家的 RemoteCraft:单向延迟 ≈ RTT/2(幽灵连续外推用,修正 gapEMA 低估真实网络延迟的问题)
				if (_remoteCrafts.TryGetValue(peer.PlayerId, out RemoteCraft rc))
				{
					rc.LatencyMs = peer.PingMs / 2f;
				}
			}
			else
			{
				ClientPingMs = ClientPingMs < 0 ? (int)rttMs : (int)(ClientPingMs * 0.7 + rttMs * 0.3);
				// 客户端所有远端飞船都经房主转发,单向延迟 ≈ 自己到房主 RTT/2(缺少对端→房主一段,近似处理)
				foreach (RemoteCraft rc in _remoteCrafts.Values) rc.LatencyMs = ClientPingMs / 2f;
			}
		}

		/// <summary>客户端被房主踢出：提示后停止联机会话。</summary>
		private void OnKick(byte[] packet)
		{
			if (IsServer) return; // 房主不会收到 Kick
			if (!MpMessages.TryDecodeKick(packet)) return;
			Mod.LogLobby("MP.OnKick: kicked by host");
			try
			{
				global::ModApi.Ui.MessageDialogScript msg = Game.Instance.UserInterface.CreateMessageDialog(global::ModApi.Ui.MessageDialogType.Okay, null, true);
				msg.MessageText = Locale.GetString("MultiPlayer.MultiPlayerUI.Kicked");
			}
			catch (Exception e)
			{
				Mod.LogLobby("MP.OnKick: failed to show dialog: " + e.Message);
			}
			Stop();
		}

		/// <summary>
		/// 房主：踢出指定玩家（发 Kick 通知 + 断开传输连接 + 移除记录 + 广播 PlayerLeave + 触发 OnPlayerLeft 清理远程飞船）。
		/// </summary>
		public void KickPlayer(int playerId)
		{
			if (!IsServer) return;
			if (playerId == PlayerId) return; // 不能踢自己
			MpPeer target = null;
			lock (_playersByPlayerId) { _playersByPlayerId.TryGetValue(playerId, out target); }
			if (target == null) return;

			Mod.LogLobby("MP.KickPlayer: kicking player " + playerId + " ('" + target.PlayerName + "', " + target.Id + ")");
			try { Transport.SendTo(target, MpMessages.EncodeKick()); }
			catch (Exception e) { Mod.LogLobby("MP.KickPlayer: Kick send failed: " + e.Message); }
			Transport.DisconnectPeer(target);

			MpPeer removed = null;
			lock (_playersByPlayerId)
			{
				if (_playersByPlayerId.TryGetValue(playerId, out removed))
				{
					_playersByPlayerId.Remove(playerId);
				}
			}
			_pendingXmlRequests.Remove(playerId);
			_hostCraftResend.Remove(target.Id);
			if (removed != null)
			{
				Transport.Broadcast(MpMessages.EncodePlayerLeave(playerId));
				Mod.LogLobby("MP.KickPlayer: broadcast PlayerLeave playerId=" + playerId);
				OnPlayerLeft?.Invoke(removed);
			}
		}

		// ---------------- 工具 ----------------

		private int _nextPlayerId = 1;
		private int NextPlayerId() => _nextPlayerId++;

		private void RegisterPlayer(MpPeer peer)
		{
			if (peer.PlayerId < 0) return;
			lock (_playersByPlayerId)
			{
				_playersByPlayerId[peer.PlayerId] = peer;
			}
		}

		private void HandlePeerTimeout(MpPeer peer)
		{
			Mod.LogLobby("MP peer timeout: " + peer.Id + " (PlayerId=" + peer.PlayerId + ", NodeId=" + peer.NodeId + ")");
			if (peer != null) _hostCraftResend.Remove(peer.Id);
			MpPeer removed = null;
			lock (_playersByPlayerId)
			{
				MpPeer match = null;
				foreach (MpPeer p in _playersByPlayerId.Values)
				{
					if (p.Id == peer.Id) { match = p; break; }
				}
				if (match != null)
				{
					_playersByPlayerId.Remove(match.PlayerId);
					removed = match;
				}
			}
			if (removed != null)
			{
				_pendingXmlRequests.Remove(removed.PlayerId);
				if (IsServer)
				{
					Transport.Broadcast(MpMessages.EncodePlayerLeave(removed.PlayerId));
					Mod.LogLobby("MP peer timeout: broadcast PlayerLeave playerId=" + removed.PlayerId);
				}
				OnPlayerLeft?.Invoke(removed);
			}
		}

		public IReadOnlyCollection<MpPeer> GetPlayers()
		{
			lock (_playersByPlayerId) { return new List<MpPeer>(_playersByPlayerId.Values); }
		}

		// ---------------- 远程飞船管理（M2） ----------------

		public class RemoteCraft
		{
			public const int BufferCapacity = 32; // 插值缓冲容量 ≈ 1.6s @ 20Hz，容抖动/乱序

			public int PlayerId;
			public string PlayerName;   // 玩家名(诊断日志区分用)
			public CraftNode Node;
			public bool HasState;      // 是否已收到至少一个状态包
			public bool IsInitialized; // 幻影模式是否已应用（CraftScript 延迟构建后置 true）
			public float LastStateLogTime; // 周期性状态日志计时
			public float LastVisualLogTime; // 可见性诊断日志计时
			public Quaternion LastAppliedHeading; // ApplyRemoteState 最近一次写入的帧空间朝向(诊断用)

			// --- 烟雾速度注入(EngineVisualSync.InjectGhostMotion):上次注入的帧空间速度/角速度缓存 ---
			// 值未变化时跳过写入:Unity 对 kinematic 刚体每次写 velocity 都打告警(见 InjectGhostMotion),
			// 幽灵全 kinematic + 每帧每 body 写一次会把 Player.log 刷爆(~1.3M 条/会话)。
			public Vector3? LastInjectedVelocity;
			public Vector3? LastInjectedAngularVelocity;

			// --- 引擎尾焰同步(EngineVisualSync):最近应用状态中的每引擎视觉 throttle + 幽灵驱动表 ---
			public List<float> SyncedThrottles = new List<float>();
			public List<EngineVisualSync.EngineVisualDriver> EngineDrivers;

			// --- 平滑插帧：带时间戳环形缓冲（按到达端 unscaledTime 排列，暂停安全、容抖动/乱序） ---
			public readonly StateSample[] Buffer = new StateSample[BufferCapacity];
			public int BufferCount;          // 有效样本数
			public int BufferHead;           // 最旧样本索引（环形）
			public Mod.RemoteDataPack LastApplied;  // 最近一次实际应用的状态（LateUpdate 用，避免"最新包覆盖插值"）
			public bool HasApplied;          // LastApplied 是否已有效

			// --- 平滑/网络诊断统计（周期日志 Mod.LogLobby 用，无悬浮窗） ---
			public long TotalFrames;         // UpdateRemoteCrafts 已处理帧数
			public long UnderrunFrames;      // 命中"缓冲欠载"分支的帧数（冻结，等待新包）
			public long SnapFrames;          // 欠载中直接返回最新原始包（冻结）的帧数
			public long ExtrapolatedFrames;  // 欠载中按速度外推（不冻结）的帧数
			public float InterpPct;          // 最近一次插值比例 0..1（欠载时=1）
			public float GapEmaMs;           // 包间到达间隔 EMA（ms）
			public float JitterEmaMs;        // 包间到达间隔抖动 EMA（ms）
			public float LatencyMs = -1f;    // 到该发送端的单向网络延迟估计(RTT/2,房主按 peer.PingMs,客户端按 ClientPingMs);-1=未测得
			public float NewestArrivalTime;  // 最新包到达时刻(Time.unscaledTime,连续外推用)
			public double LastPosErrorM;     // 最近渲染位置(插值/外推) vs 最新包位置 的距离（米）
			public float LastSmoothingLogTime; // 平滑诊断周期日志计时
			public float LastSlowmoLogTime;    // 慢放诊断周期日志计时(接收端/发送端慢放时 0.5s 输出)
			public bool DiagHasPrevSlowmo;     // 慢放诊断:是否有上一帧采样
			public Vector3 DiagPrevRootPos;    // 慢放诊断:上一帧 craft 根 transform 位置
			public Vector3 DiagPrevPartPos;    // 慢放诊断:上一帧首个部件世界位置
			public Vector3 DiagPrevComPos;     // 慢放诊断:上一帧 comRot 位置
			public float DiagMaxRootDelta;     // 慢放诊断:窗口内 craft 根最大单帧位移
			public float DiagMaxPartDelta;     // 慢放诊断:窗口内首个部件最大单帧位移
			public float DiagMaxComDelta;      // 慢放诊断:窗口内 comRot 最大单帧位移
			private float _lastPushTime = -1f; // PushSample 上次到达时间（抖动 EMA 用）

			// --- 平滑状态（P1：SP2 式指数平滑 + 近距快照 + 瞬移；首帧/body 数量变化时快照为 target） ---
			public Vector3d SmoothedPos;        // 平滑后位置（地面坐标，与 data.Position 同系）
			public Quaterniond SmoothedSrfRel;  // 平滑后朝向（相对地表 SrfRel）
			public Vector3[] SmoothedBodyPos;   // 每 body 平滑后相对位置（相对 comRot，与 BodyPositions 同索引）
			public Quaternion[] SmoothedBodyRot; // 每 body 平滑后相对旋转（相对 comRot）
			public bool HasSmoothed;            // 平滑状态是否已初始化

			// --- 每帧复用缓冲（P0/P1 平滑输出；避免热路径每帧分配 List 引发 GC 卡顿） ---
			public readonly List<Vector3> ReuseInterpBodyPos = new List<Vector3>();
			public readonly List<Vector3> ReuseInterpBodyRot = new List<Vector3>();
			public readonly List<Vector3> ReuseSmoothBodyPos = new List<Vector3>();
			public readonly List<Vector3> ReuseSmoothBodyRot = new List<Vector3>();
			// 1.4.2:EnforceRemoteCraftVisuals 每帧逐 body 遍历渲染器,复用缓冲避免每帧 GC(GetComponentsInCraft 每次 Clear)。
			public readonly List<Renderer> ReuseRenderers = new List<Renderer>();

			// --- 跳动诊断（定位"0 延迟静止仍跳动"：上一帧已应用位置/每 body 位姿 vs 本帧） ---
			public Vector3d LastRenderedPos;      // 上一帧实际应用的位置（地面坐标）
			public double LastMoveDeltaM;          // 本帧已应用位置相对上一帧的位移（米）
			public float LastBodyPoseDeltaM;       // 本帧每 body 最大位姿位移（米）
			// 变换漂移诊断:本帧写入 Transform 前,实际 Transform.position 相对上一帧写入值的位移(米)。
			// 若 moveDelta=0 而 tfDelta 持续>0 → 游戏层在 Update 写入之后移动了 ghost(滑动来自游戏而非我们的写入)。
			public Vector3 LastWrittenFramePos;    // 上一帧 ApplyRemoteState 后 Transform.position(帧空间)
			public float LastTfDriftM;             // 本帧写入前实际位置相对 LastWrittenFramePos 的漂移
			// 高精度漂移诊断(定位"双方静止仍滑动"):F2/F1 精度下 0.1m/s 级慢漂移显示为 0.00,
			// 必须用 3s 窗口累计量 + F4 位精度才能捕捉。
			public double MoveSumM;                // 3s 窗口内累计渲染位移(moveDelta 累加;0.1m/s 漂移 3s≈0.3m)
			public double PktJumpM;                // 3s 窗口内最大单包位置跳变(定位发送端数据跳变)
			public Vector3d LastPktPos;            // 上一包位置(计算 PktJump)
			public bool HasLastPktPos;
			public double HeadDeg3s;               // 3s 窗口内累计朝向(应用 SrfRel)变化(度;慢旋转也会被感知为滑动)
			public Quaterniond PrevSmoothedSrfRel; // 上一帧平滑朝向(计算 HeadDeg3s)

			// --- 暂停/冻结检测(2026-09:飞船"有速度时暂停"→观察方位置抽搐) ---
			// 暂停时发送端的 Position 冻结、但 Velocity 仍是暂停前最后一刻的值(非零)。
			// 接收端若照常做 Position+Velocity×age 的 dead-reckoning,目标位置会随每包到达被拉回、
			// 又在包间按速度前进 → 以发包频率来回摆动 → 观察方看到"抽搐"(见 plans/latency-smoothing §9.7)。
			// 判据(双保险,任一成立即视为"发送端已冻结"):
			//   ① 包内显式 Paused 标记(本 mod 双端都升级后最可靠);
			//   ② 连续多包位置零位移(兼容旧版本对端;也不依赖标记是否被中继/丢包)。
			public bool RemotePaused;              // 判定:发送端当前处于"位置冻结"(暂停/完全静止)
			public bool LastPktPausedFlag;         // 最新包携带的发送端暂停标记(包内显式字段)
			public long FrozenFrames;              // 完全冻结态(ramp=1)的帧数(诊断)
			public float RemotePausedRamp;         // 0..1 平滑过渡量(0=正常外推,1=完全停止速度外推),避免冻结/解冻瞬间跳变
			public int PktStallCount;              // 连续"位置零位移"包计数
			public float PktFreezeDeltaM;          // 最近两包位置距离(诊断:是否真的零位移)
			public bool HasPktFreezePos;
			public Vector3d PktFreezePos;
			public double LastAgeNowSec;           // 本帧实际使用的外推量(诊断)
			public float LastAccelTermM;           // 2 阶外推:本帧加速度项位移(½|a|·ext²,m)
			public float LastAngExtRad;            // 2 阶外推:本帧朝向外推角(|ω|·ext,rad)

			// --- 突发/顿挫诊断(2026-09-14,smoothing-comparison §四:200ms+ 真实联机"一卡一卡"定位) ---
			// 两个候选机制:①mRate 被到达间隔(突发 0/几百 ms)污染 → ext 摆动 → 速度脉冲;
			// ②长静默冻结分支(age>max(3·gapEMA,0.25s),MpNetworkManager.cs:2056)误触发 → 停→冲。
			// 以下窗口量(3s 随 MP smoothing 行输出)+ 事件日志(MP gap / MP gapfreeze)用于区分主导机制。
			public float LastExtSec;                // 本帧外推量 ext(诊断,s)
			public bool GapFreezeActive;            // 本帧长静默冻结分支是否激活(机制 ②)
			public float WinGapFreezeHits;          // 3s 窗口:进入长静默冻结的次数(机制 ② 命中率)
			public float WinMRateMin = 1f;          // 3s 窗口:mRate 最小值(机制 ①:mRate 下摆)
			public float WinMRateMax = 1f;          // 3s 窗口:mRate 最大值(机制 ①:mRate 上摆)
			public float WinExtMax;                 // 3s 窗口:ext 最大值(s)
			public float WinMaxGapMs;               // 3s 窗口:最大到达间隔(ms,突发程度)
			public int WinLongGapCount;             // 3s 窗口:到达间隔 >250ms 的次数(突发静默次数)
			public float WinMoveMaxM;               // 3s 窗口:单帧渲染位移最大值(m,停→冲的"冲"幅度)
			public float LastGapLogTime;            // MP gap 事件日志节流(unscaledTime)

			// --- F1(2026-09-14,smoothing-comparison §五 / README §三):虚拟 age —— 目标推进时钟 ---
			// 突发到达(背靠背 0ms / 静默几百 ms)下,"距最新包到达的真实时间 age"会随包到达归零,
			// 而包位置按发送端间隔前移 → 目标每包锯齿(±V×Δt)→ 渲染层"一卡一卡"。
			// 改为自走时钟:每帧 +unscaledDeltaTime、每包到达 −SendIntervalEst(发送端发包间隔估计)。
			// 连续性证明:包到达瞬间目标跳变 = V×Δt_send − V×(age增长−age扣除) = V×Δt_send−V×(interval−Δt_send) − ... = 0
			// (匀速时目标连续;残留仅剩加速度误差 ½·a·Δt²)。慢放/暂停仍由 mRate/ramp 处理。
			public float VirtualAge;                // 自走外推时钟(替代"距最新包到达时间")
			public float SendIntervalEst;           // 发送端发包间隔估计(包时间差 EMA,钳[0.02,0.1]s)
			// F2'(2026-09-14):mRate 分母抗突发 —— 到达间隔慢 EMA(时间常数≈1s)。
			// 瞬时到达间隔在突发下 0/几百 ms 交替 → mRate 打到 0.03~1.14(实测) → ext 摆动;
			// 慢 EMA 收敛到"平均到达间隔"(=平均发包间隔)→ 稳态 mRate≈1,慢放检测依然有效。
			public float MArrivalEma;
			// F3(2026-09-14):单向延迟 EMA(ext 用;LatencyMs=RTT/2 裸值抖动会直接进 ext → 目标晃)。
			public float LatencyEmaMs = -1f;

			// --- 发送端时间倍率(2026-09:慢放时外推按真实时间推进而包位置按发送端缩放时间走 → 每包向后锯齿) ---
			// 相邻两包 FlightState.Time(发送端游戏时间)增量 / 真实到达时间增量;正常=1,慢放<1,暂停→0。
			// UpdateRemoteCrafts 把外推量 ext 乘以此倍率换算到发送端时间基 → 慢放时外推与发送端实际运动同步。
			// ⚠️ 2026-09-13 四轮:用户慢放是 Unity Time.timeScale<1,但游戏 FlightState.Time 不缩放
			// (对端实测 rate 恒 1.000)→ 包时间测不出慢放;而包位置位移确实按慢放速率缩小。
			// 改用 SenderMotionRate(基于包位置位移/速度×真实时间)测量发送端实际运动速率,更鲁棒。
			public float SenderTimeRate = 1f;
			private double _lastPktTime = -1;      // 上一包发送端 FlightState.Time(倍率测量)
			// --- 发送端运动倍率(2026-09 四轮,位置基) ---
			// 相邻两包位置位移 ÷ (速度 × 真实到达间隔):正常飞行≈1,发送端慢放=timeScale,静止/暂停→0。
			// 外推量 ext ×= 此倍率 → 外推与发送端实际运动同步(慢放时不再"外推超前→每包向后锯齿")。
			// ⚠️ 2026-09-13 六轮:单包测量尖刺(切换速度模式瞬间的大间隔/大位移包,实测 mRate 单包
			// 0.208→0.393)经 EMA 仍能透出 → ext 突变 → 幽灵单帧 1.97~2.7m 跳变。修复:速率变化钳制
			// MaxMotionRateStep/包(±0.15 @20Hz → 满量程 0.05↔1.0 收敛仅 ~0.3s,尖刺被压到 ±0.15/包)。
			public float SenderMotionRate = 1f;
			public const float MaxMotionRateStep = 0.15f; // mRate 每包最大变化量(切换瞬间尖刺抑制)

			/// <summary>
			/// 位置零位移判定阈值(米,地面坐标)。发送端暂停时相邻包位置完全相同(≈0);
			/// 0.5 m/s 的慢速漂移在 50ms 包间隔内也有 0.025m,故 0.02m 不会误判正常缓速运动。
			/// </summary>
			public const double PositionStallM = 0.02;
			/// <summary>连续多少个零位移包才认定发送端已冻结(过滤单次丢包/重复包造成的假静止)。</summary>
			public const int PositionStallPackets = 2;

			// --- 抽搐诊断(2026-09:双飞静止一方抽搐,定位"comRot 连带移动 + 双写不一致"反馈环) ---
			// 1.4.2 中 comRot 是 RootPart.Transform 的后代(CraftScript.cs:2172),RootPart 位于根 body 内,
			// 因此"写 body 世界位置"会连带移动 comRot;而 ApplyRemoteBodyPoses 又用 comRot 位姿作基准,
			// 若每帧冻结的 comRotPos 随上帧写入而漂移,body 位置将逐帧漂移(静止时肉眼可见"抽搐")。
			public Vector3 DiagComPosFrozen;       // ApplyRemoteBodyPoses 冻结的 comRot.position(帧空间)
			public Vector3 DiagComPosPrevFrozen;   // 上一帧 Update 路径冻结的 comRot.position(跨帧对比基准)
			public Vector3 DiagComPosAfterBodies;  // 写完所有 body 后 comRot.position(检测连带漂移)
			public Vector3 DiagComPosLate;         // LateUpdate ForceRemoteHeading 冻结的 comRot.position
			public float DiagComLinkM;             // 写 body 前后 comRot 连带位移(|AfterBodies - Frozen|)
			public float DiagComCrossFrameM;       // 跨帧 comRot 漂移(|Frozen - PrevFrozen|)
			public Vector3 DiagBody0PrevWorld;     // 上一帧 body[0] 世界位置(帧空间)
			public bool DiagHasBody0Prev;
			public float DiagBody0DeltaM;          // body[0] 逐帧世界位移(抽搐幅度)
			public float DiagBody0DeltaLateM;      // LateUpdate 重写后 body[0] 位移(双写不一致幅度)
			public Vector3 DiagSmoothedBody0;      // 平滑后 body[0] 相对 comRot 位置(目标)
			public float LastTwitchLogTime;        // 抽搐诊断周期日志计时

			public struct StateSample
			{
				public float ArrivalTime;  // 到达端 Time.unscaledTime（单调）
				public double PacketTime;  // 发送端 FlightState.Time（诊断用）
				public Mod.RemoteDataPack Data;
			}

			/// <summary>环形追加一条样本；按到达时间天然有序，满则覆盖最旧。</summary>
			public void PushSample(float arrivalTime, double packetTime, Mod.RemoteDataPack data)
			{
				int idx = (BufferHead + BufferCount) % BufferCapacity;
				Buffer[idx] = new StateSample { ArrivalTime = arrivalTime, PacketTime = packetTime, Data = data };
				if (BufferCount < BufferCapacity) BufferCount++;
				else BufferHead = (BufferHead + 1) % BufferCapacity;
				HasState = true;
				NewestArrivalTime = arrivalTime;

				// 包间到达间隔与抖动 EMA（诊断：NetSim 注入的抖动应如实反映到这里）
				if (_lastPushTime >= 0f)
				{
					float gapMs = (arrivalTime - _lastPushTime) * 1000f;
					GapEmaMs = GapEmaMs <= 0f ? gapMs : GapEmaMs * 0.9f + gapMs * 0.1f;
					float dev = Mathf.Abs(gapMs - GapEmaMs);
					JitterEmaMs = JitterEmaMs <= 0f ? dev : JitterEmaMs * 0.9f + dev * 0.1f;
					// 突发窗口统计 + 事件日志(2026-09-14,smoothing-comparison §四):>250ms 到达间隔
					// = 突发静默(真实 Steam relay 特征,NetSim 均匀延迟下不会出现)。
					// 用于区分"网络突发"(接收端 gap 大)与"发送端突发"(对端 sendGap 大)。
					if (gapMs > WinMaxGapMs) WinMaxGapMs = gapMs;
					if (gapMs > 250f)
					{
						WinLongGapCount++;
						if (arrivalTime - LastGapLogTime > 1f)
						{
							LastGapLogTime = arrivalTime;
							Mod.LogLobby("MP gap P" + PlayerId + ": gap=" + gapMs.ToString("F0") + "ms" +
								" gapEMA=" + GapEmaMs.ToString("F0") + "ms" +
								" jitterEMA=" + JitterEmaMs.ToString("F0") + "ms" +
								" mRate=" + SenderMotionRate.ToString("F3") +
								" stall=" + PktStallCount);
						}
					}
				}
				// 保存上一包到达时间(倍率测量用),再覆盖 _lastPushTime。
				// ⚠️ 2026-09-13 五轮:此前 `_lastPushTime = arrivalTime` 在前、倍率块在后,
				// 差值恒为 0 → SenderTimeRate/SenderMotionRate 从未更新、恒 1.000 → 慢放外推从未缩放!
				// 这就是"慢放 1/20 时 1.5 个船身跳变"从未被修好的根因(倍率测量一直是死的)。
				float prevArrival = _lastPushTime;
				_lastPushTime = arrivalTime;

				// 发送端时间倍率:相邻两包 FlightState.Time(发送端游戏时间)增量 / 真实到达时间增量。
				// 慢放(发送端 timeScale<1)时发送端游戏时间推进慢于真实时间 → 倍率<1;暂停(时间冻结)→0。
				// 用于把 dead-reckoning 外推量换算到发送端时间基(见 UpdateRemoteCrafts),
				// 否则慢放时外推按真实时间推进、包位置却按发送端缩放时间走 → 每包向后锯齿(实测慢放最严重)。
				// ⚠️ 2026-09-13 四轮实测:用户的慢放是 Unity Time.timeScale<1,但游戏 FlightState.Time 不缩放
				// (对端 rate 恒 1.000)→ 包时间测不出慢放!真正可靠的测量是"包位置运动倍率"(见下)。
				// F1 修正(2026-09-14):给 VirtualAge 用的「包内容间隔」(见下方扣除处注释)。
				float contentGapSec = -1f;
				if (_lastPktTime >= 0 && prevArrival >= 0)
				{
					double dtPkt = packetTime - _lastPktTime;
					if (dtPkt > 0.0 && dtPkt < 600.0) contentGapSec = (float)dtPkt; // 坏包/时间回绕 → 不采用
					float dtReal = arrivalTime - prevArrival;
					if (dtReal > 0.001f)
					{
						float rate = dtPkt > 0.0 ? (float)(dtPkt / dtReal) : 0f; // 发送端时间冻结(暂停)→0
						if (rate >= 0f && rate < 10f) // 过滤坏包/时间回绕
						{
							SenderTimeRate = SenderTimeRate <= 0f ? rate : SenderTimeRate * 0.9f + rate * 0.1f;
						}
						// F1(2026-09-14):发送端发包间隔估计(包时间差 EMA,钳 [0.02,0.1]s)。
						// FlightState.Time 慢放不缩放 → 该值≈名义发包间隔(50ms),慢放由 mRate 管,不受影响;
						// 发送端卡顿(间隔变大)时被钳制到 0.1s,不无限膨胀。
						if (dtPkt > 0.0)
						{
							float est = (float)Math.Min(Math.Max(dtPkt, 0.02), 0.1);
							SendIntervalEst = SendIntervalEst <= 0f ? est : SendIntervalEst * 0.9f + est * 0.1f;
						}
					}
				}
				_lastPktTime = packetTime;

				// F1(2026-09-14):虚拟 age 时钟。每包到达扣除发送端发包间隔估计 SendIntervalEst
				// (在上一块 dtPkt 处 EMA 更新)—— 突发背靠背时扣 0.05 而非归零 → 目标连续(见字段注释)。
				VirtualAge -= SendIntervalEst > 0f ? SendIntervalEst : 0.05f;
				// F1(2026-09-14):虚拟 age 时钟(每帧 +dt,见 UpdateRemoteCrafts)。每包到达扣除
				// **该包与上一包的「内容时间增量」contentGapSec**(= 发送端 FlightState.Time 之差):
				// 突发背靠背时扣≈0.05 而非归零 → 目标连续(见字段注释的连续性证明)。
				// ⚠️ 2026-09-14 修:原先固定扣 SendIntervalEst(EMA,钳 [0.02,0.1])——**丢一个包时实际内容增量
				// 是 2×间隔,却只扣 1×**,每丢一包就永久多出约一个间隔(只在下界钳 0、正向无界)→ age 单调累积,
				// 越过 gapFreeze 阈值后长期卡在"冻结"分支(ext 丢掉 age 项)且包恢复后也回不来 → "停→冲"顿挫
				// (MP gapfreeze 日志的来源之一)。改用真实内容增量后:每帧累加与每包扣减自动配平(丢包 / 长静默
				// 后下一包一次扣完),age 恒定不再漂移;长静默仍正常触发冻结,恢复后自动解冻。
				VirtualAge -= contentGapSec > 0f ? contentGapSec : (SendIntervalEst > 0f ? SendIntervalEst : 0.05f);
				if (VirtualAge < 0f) VirtualAge = 0f;

				// 发送端运动倍率(2026-09 四轮,位置基):相邻两包位置位移 ÷ (速度 × 真实到达间隔)。
				// 正常飞行:位移 = v×dtReal → 倍率≈1;发送端慢放(Unity timeScale<1):位移 = v×dtReal×ts → 倍率=ts;
				// 静止/暂停:位移≈0 → 倍率→0(外推量归零,幽灵精确停包位)。比包时间倍率鲁棒:
				// 用户慢放实测 FlightState.Time 不缩放(rate 恒 1.000),只有位置位移如实反映慢放。
				if (HasLastPktPos && prevArrival >= 0)
				{
					float mDtReal = arrivalTime - prevArrival;
					// F2'(2026-09-14):到达间隔慢 EMA 作 mRate 分母,抗突发(瞬时间隔 0/几百 ms 交替会把
					// mRate 打到 0.03~1.14,实测)。慢 EMA 收敛到平均间隔 → 稳态 mRate≈1。
					if (mDtReal > 0.001f)
					{
						MArrivalEma = MArrivalEma <= 0f ? mDtReal : MArrivalEma * 0.99f + mDtReal * 0.01f;
					}
					float mDtUse = MArrivalEma > 0f ? MArrivalEma : mDtReal;
					if (mDtUse > 0.001f)
					{
						double dPos = Vector3d.Distance(data.Position, LastPktPos);
						float v = (float)data.Velocity.magnitude;
						if (v > 1f && dPos > 0.001)
						{
							float mRate = (float)(dPos / (v * mDtUse));
							if (mRate > 0.0f && mRate < 10f) // 过滤坏包(加速段 v 突变会瞬时失真,EMA 摊平)
							{
								float newMotionRate = SenderMotionRate * 0.9f + mRate * 0.1f;
								// 速率变化钳制:切换速度模式瞬间的单包测量尖刺(实测 0.208→0.393/包 → 幽灵
								// 单帧 2m 跳)不允许直接透出;±0.15/包 @20Hz 下满量程收敛仍仅 ~0.3s。
								newMotionRate = Mathf.Clamp(newMotionRate,
									SenderMotionRate - MaxMotionRateStep, SenderMotionRate + MaxMotionRateStep);
								SenderMotionRate = newMotionRate;
							}
						}
						else
						{
							// 速度≈0 或位移≈0(静止/暂停):发送端没有实际运动 → 倍率收敛到 0,外推量归零。
							float newMotionRate = SenderMotionRate * 0.9f + 0f * 0.1f;
							newMotionRate = Mathf.Clamp(newMotionRate,
								SenderMotionRate - MaxMotionRateStep, SenderMotionRate + MaxMotionRateStep);
							SenderMotionRate = newMotionRate;
						}
					}
				}

				// 包间位置跳变诊断:相邻两包的位置差(发送端数据是否在缓慢漂移/跳变)。
				// 双方"静止"时若此值持续>0,说明滑动来自发送端数据,而非接收端平滑层。
				if (HasLastPktPos)
				{
					double d = Vector3d.Distance(data.Position, LastPktPos);
					if (d > PktJumpM) PktJumpM = d;
				}
				LastPktPos = data.Position;
				HasLastPktPos = true;

				// 暂停/冻结检测:位置零位移连续计数(见字段区注释)。包内 Paused 标记用于"尽快进入"冻结态;
				// 退出冻结态一律以"位置重新开始变化"为准(计数清零)—— 这样对端刚恢复那一瞬间不会立刻
				// 按速度外推(那时包内位置仍是暂停前的旧值,一旦外推就会跳一下)。
				double freezeDelta = HasPktFreezePos ? Vector3d.Distance(data.Position, PktFreezePos) : double.MaxValue;
				PktFreezeDeltaM = HasPktFreezePos ? (float)freezeDelta : 0f;
				if (HasPktFreezePos && freezeDelta <= PositionStallM)
				{
					if (PktStallCount < 1000) PktStallCount++;
				}
				else
				{
					PktStallCount = 0;
				}
				PktFreezePos = data.Position;
				HasPktFreezePos = true;
				LastPktPausedFlag = data.Paused;
			}

			/// <summary>取最新样本。</summary>
			public bool TryGetNewest(out Mod.RemoteDataPack data)
			{
				data = default;
				if (BufferCount == 0) return false;
				data = Buffer[(BufferHead + BufferCount - 1) % BufferCapacity].Data;
				return true;
			}

			/// <summary>清空缓冲（重建远程飞船时调用）。</summary>
			public void ClearBuffer()
			{
				BufferCount = 0;
				BufferHead = 0;
				HasApplied = false;
			}
		}

		private void HandlePlayerLeft(MpPeer peer)
		{
			RemoveRemoteCraft(peer.PlayerId);
		}

		/// <summary>
		/// 用远程玩家的首个（或预加载期间最新）状态包位置生成其远程飞船（幻影模式）。
		/// 飞船一出现就在远程玩家的真实位置，而不是先出现在本机玩家身上。
		/// CraftData / LaunchLocation / XML 由 SpawnRemoteCraftCoroutine 预加载前构建好（主 prefab 已热缓存）。
		/// </summary>
		private void SpawnRemoteCraftAtPosition(MpPeer peer, Mod.RemoteDataPack data, CraftData craftData, LaunchLocation location, XElement xml)
		{
			try
			{
				if (peer.PlayerId == PlayerId) return;                 // 自己
				if (_remoteCrafts.ContainsKey(peer.PlayerId)) return;  // 已生成
				if (FlightSceneScript.Instance == null) return;        // 不在飞行场景
				if (craftData == null || location == null || xml == null) return;

				CraftNode localNode = FlightSceneScript.Instance.CraftNode as CraftNode;
				IPlanetNode planet = localNode != null ? localNode.Parent : null;
				if (localNode == null || planet == null) return;

				CraftNode remote = FlightSceneScript.Instance.SpawnCraft(peer.PlayerName+"|"+craftData.Name, craftData, location, xml);
				if (remote == null)
				{
					Mod.LogError("SpawnRemoteCraftAtPosition: SpawnCraft returned null for player " + peer.PlayerId);
					return;
				}

				// [朝向诊断|远端P{pid}飞船] 生成时：Heading(行星字段) 应≈ 传入的 spawnHeading(行星空间)。
				//Mod.Log("[朝向诊断|远端P" + peer.PlayerId + "飞船] 生成: Heading(行星)=" + Q(remote.Heading) +
					//" | 传入spawnHeading(行星)=" + Q(spawnHeading));

				// 先登记（无论 CraftScript 是否已构建），避免状态包反复触发重新生成；
				// 并把首个状态包入插值缓冲（BufferCount=1，UpdateRemoteCrafts 直接应用）。
				RemoteCraft rc = new RemoteCraft { PlayerId = peer.PlayerId, PlayerName = peer.PlayerName, Node = remote };
				rc.PushSample(Time.unscaledTime, 0, data);
				_remoteCrafts[peer.PlayerId] = rc;

				// 立即进入"表面锁定"分支（防止游戏推进轨道导致坠落），并预置 GroundedSurface* 避免空引用
				rc.Node.InContactWithPlanet = true;
				IPlanetNode remotePlanet = remote.Parent != null ? remote.Parent : planet;
				if (remotePlanet != null)
				{
					ApplyRemoteGroundedSurface(rc, data, remotePlanet);
				}
				// 生成后立即用状态包位置/速度/朝向覆盖逻辑状态并刷新 Transform：
				// 否则 SurfaceLockedGround 会先把飞船放到地面上（AGL≈0），下一帧才被拉回正确高度，
				// 出现"生成瞬间贴地/掉进地下"。
				// CraftScript 尚未构建时该方法内部会安全跳过，待 InitializeRemoteCraft 补齐。
				ApplyRemoteState(rc, data);

				// 朝向诊断：核对 数据包heading / SpawnCraft 后 Heading / 视觉 Transform.rotation
				// 朝向诊断日志已暂时禁用（朝向已修复）
				//if (remote.CraftScript != null)
				//{
				//	Quaternion spawnRot = remote.CraftScript.Transform.rotation;
				//	Mod.Log("MP headingDiag spawn p" + peer.PlayerId + ": dataHeading=(" +
				//		data.Heading.x.ToString("F3") + "," + data.Heading.y.ToString("F3") + "," + data.Heading.z.ToString("F3") + "," + data.Heading.w.ToString("F3") + ")" +
				//		", spawnHeading=(" + remote.Heading.x.ToString("F3") + "," + remote.Heading.y.ToString("F3") + "," + remote.Heading.z.ToString("F3") + "," + remote.Heading.w.ToString("F3") + ")" +
				//		", spawnRot=(" + spawnRot.x.ToString("F3") + "," + spawnRot.y.ToString("F3") + "," + spawnRot.z.ToString("F3") + "," + spawnRot.w.ToString("F3") + ")");
				//}

				// 幻影模式 + 初始朝向：CraftScript 可能延迟构建，在 UpdateRemoteCrafts 里懒初始化（见 InitializeRemoteCraft）
				Mod.LogLobby("MP: spawned remote craft for player " + peer.PlayerId + " at remote position (nodeId=" + peer.NodeId + ", localNode=" + remote.NodeId + ")" +
					", surfacePos=(" + data.Position.x.ToString("F1") + "," + data.Position.y.ToString("F1") + "," + data.Position.z.ToString("F1") + ")" +
					", heading=(" + data.Heading.x.ToString("F3") + "," + data.Heading.y.ToString("F3") + "," + data.Heading.z.ToString("F3") + "," + data.Heading.w.ToString("F3") + ")");

				// 诊断：生成后立即记录远程飞船的可见性/渲染状态，用于定位"无法显示对方 craft"
				try
				{
					GameObject rgo = remote.GameObject;
					int rendererCount = 0, enabledCount = 0;
					if (rgo != null)
					{
						// 1.4.2:body 脱离 craft 层级后 GetComponentsInChildren 遍历不到 body 上的渲染器,
						// 改用逐 body 遍历(GetComponentsInCraft,等价游戏新 API)。
						List<Renderer> renderers = new List<Renderer>();
						CraftUtils.GetComponentsInCraft(remote, renderers, true);
						foreach (Renderer r in renderers) { rendererCount++; if (r.enabled) enabledCount++; }
					}
					Mod.LogLobby("MP spawnDiag p" + peer.PlayerId + ": goActive=" + (rgo != null ? rgo.activeSelf.ToString() : "null") +
						", craftScript=" + (remote.CraftScript != null ? "built" : "notBuilt") +
						", renderers=" + rendererCount + "/enabled=" + enabledCount +
						", inFlightState=" + IsNodeInFlightState(remote) +
						", isPlayer=" + remote.IsPlayer +
						", isLoadedInGameView=" + remote.IsLoadedInGameView);
				}
				catch (Exception e) { Mod.LogError("MP spawnDiag error: " + e.Message); }
			}
			catch (Exception e)
			{
				Mod.LogError("SpawnRemoteCraftAtPosition FAILED (player " + peer.PlayerId + "): " + e.Message);
			}
		}

		private void RemoveRemoteCraft(int playerId)
		{
			_spawnMissLogged.Remove(playerId);
			_spawnAttemptTime.Remove(playerId);
			// 玩家离开/踢出：若正在预加载（飞船尚未生成），销毁其加载进度框并结束挂起生成，防残留
			DestroyLoadingIndicator(playerId);
			EndSpawnAttempt(playerId);
			RemoteCraft rc;
			if (!_remoteCrafts.TryGetValue(playerId, out rc))
			{
				Mod.LogLobby("MP.RemoveRemoteCraft: player " + playerId + " not in _remoteCrafts (nothing to remove)");
				return;
			}
			_remoteCrafts.Remove(playerId);
			if (rc.Node != null)
			{
				// 诊断：记录移除前飞船节点状态，用于定位 MapView NRE（MapCraft.transform 为 null 但仍在 registry）
				bool inFlightState = false;
				string goActive = "null";
				try
				{
					if (FlightSceneScript.Instance != null && FlightSceneScript.Instance.FlightState != null)
					{
						foreach (CraftNode cn in FlightSceneScript.Instance.FlightState.CraftNodes)
						{
							if (cn == rc.Node) { inFlightState = true; break; }
						}
					}
					if (rc.Node.GameObject != null) goActive = rc.Node.GameObject.activeSelf.ToString();
				}
				catch (Exception e) { Mod.LogError("RemoveRemoteCraft: check FlightState error: " + e.Message); }
				Mod.LogLobby("MP: destroyed remote craft for player " + playerId +
					", nodeId=" + rc.Node.NodeId +
					", inFlightState=" + inFlightState +
					", goActive=" + goActive +
					", craftScriptNull=" + (rc.Node.CraftScript == null) +
					", isDestroyed=" + rc.Node.IsDestroyed);
				// 真正销毁远程飞船，不再用 SetActive(false) 隐藏（原机制会留下残影/僵尸飞船且不释放资源）：
				// DestroyCraft() 置 IsDestroyed=true 并触发 Destroyed 事件；
				// 游戏 FlightSceneScript.FlightLateUpdate 每帧调用 FlightState.ProcessDestroyedCraftNodes()
				// 从 FlightState 移除该节点、注销其数据并触发 CraftNodeRemoved，资源彻底释放。
				try
				{
					rc.Node.DestroyCraft();
				}
				catch (Exception e)
				{
					Mod.LogError("RemoveRemoteCraft: DestroyCraft error: " + e.Message);
				}
			}
			else
			{
				Mod.LogLobby("MP.RemoveRemoteCraft: player " + playerId + " rc.Node=null");
			}
		}

		/// <summary>
		/// 强制恢复远程飞船的视觉：游戏原生机制可能对"非活动/幽灵"飞船禁用 Renderer 或
		/// 停用 GameObject（实测靠近本机飞船时视觉模型消失但 CraftNode 仍在）。
		/// 每帧强制执行（不再节流），因为游戏可能在每帧都禁用幽灵飞船的 Renderer/GameObject，
		/// 尤其在真实远程联机（高延迟）场景下更为激进。
		/// </summary>
		private void EnforceRemoteCraftVisuals()
		{
			if (_remoteCrafts.Count == 0) return;
			// 每帧强制执行（不再节流 0.5s），防止游戏在帧间禁用幽灵飞船视觉
			foreach (RemoteCraft rc in _remoteCrafts.Values)
			{
				if (rc.Node == null || rc.Node.GameObject == null) continue;
				GameObject go = rc.Node.GameObject;
				try
				{
					if (!go.activeSelf)
					{
						go.SetActive(true);
						Mod.LogLobby("MP: re-activated remote craft GameObject for player " + rc.PlayerId);
					}
					// 1.4.2:body 脱离 craft 层级后 GetComponentsInChildren 遍历不到 body 上的渲染器,
					// 改用逐 body 遍历(GetComponentsInCraft,等价游戏新 API)。复用缓冲防每帧 GC。
					CraftUtils.GetComponentsInCraft(rc.Node, rc.ReuseRenderers, true);
					foreach (Renderer r in rc.ReuseRenderers)
					{
						if (!r.enabled)
						{
							r.enabled = true;
						}
					}
				}
				catch (Exception e)
				{
					Mod.LogError("EnforceRemoteCraftVisuals error (player " + rc.PlayerId + "): " + e.Message);
				}
			}
		}

		/// <summary>
		/// 收到远程状态包：若该玩家远程飞船尚未生成，则用首个状态包的真实位置生成；
		/// 否则更新插值目标。
		/// </summary>
		private void ApplyRemoteState(int playerId, int nodeId, double time, Mod.RemoteDataPack data)
		{
			RemoteCraft rc;
			if (!_remoteCrafts.TryGetValue(playerId, out rc) || rc.Node == null)
			{
				// 尚未生成：用首个状态包的位置生成远程飞船
				MpPeer peer = null;
				lock (_playersByPlayerId) { _playersByPlayerId.TryGetValue(playerId, out peer); }
				if (peer == null || string.IsNullOrEmpty(peer.CraftXml))
				{
					if (_spawnMissLogged.Add(playerId))
					{
						Mod.Log("MP: state for player " + playerId + " but no craft info to spawn");
					}
					return;
				}

				// 已有生成协程在预加载：只刷新"最新状态包"（生成时用最新位置），不再重复起协程
				// （否则预加载数秒期间每个状态包都会再起一个协程，重复预加载同一飞船）。
				if (_pendingSpawns.Contains(playerId))
				{
					_pendingSpawnLatest[playerId] = data;
					return;
				}

				// 生成尝试节流：状态包 20Hz 到达，失败时最多每 2 秒重试一次，避免刷屏
				float last;
				if (_spawnAttemptTime.TryGetValue(playerId, out last) && Time.unscaledTime - last < 2f) return;
				_spawnAttemptTime[playerId] = Time.unscaledTime;

				// 异步预加载 + 生成：SpawnCraft（LoadCraftImmediate + 实例化全部部件/渲染器）在
				// 大飞船时可能阻塞主线程数秒（白屏），且期间不读网络导致对端写阻塞（"卡到无响应"）。
				// 改为协程：先让本帧网络处理完（回 Ack/收状态包）→ 解析 XML/构建 CraftData（纯数据）→
				// 异步预加载部件 prefab（真实百分比加载框）→ 主 prefab 已热缓存后 SpawnCraft（只剩纯 Instantiate，快）。
				_pendingSpawns.Add(playerId);
				StartCoroutine(SpawnRemoteCraftCoroutine(peer, data));
				return;
			}

			// 平滑插帧：状态包入环形缓冲（按到达端 unscaledTime，暂停安全）。
			// 首个包也入缓冲（BufferCount=1 时下一帧直接应用），后续由 UpdateRemoteCrafts 取前后两包插值。
			rc.PushSample(Time.unscaledTime, time, data);
		}

		/// <summary>
		/// 协程异步生成远程飞船（SP2 异步预加载链路）：
		/// 先让网络处理几帧（DrainIncoming/回 Ack/状态包）→ 解析 XML + 构建 CraftData（纯数据，快）→
		/// 创建加载进度框（对方位置上方，真实百分比）→ 异步预加载部件 prefab（逐帧）→
		/// 主 prefab 已热缓存后 SpawnCraft（快，无秒级白屏）→ 走现有登记/表面锁定/幻影模式逻辑。
		/// </summary>
		private IEnumerator SpawnRemoteCraftCoroutine(MpPeer peer, Mod.RemoteDataPack data)
		{
			// 先跑完当前帧网络处理，再等 2 帧，让握手/回 Ack/状态包有充足时间流动
			yield return null;
			yield return null;

			// 协程延迟期间可能发生：离开飞行场景 / 玩家已离开 / 已被其他路径生成 / 飞船信息失效
			if (!IsSpawnAttemptStillValid(peer)) { EndSpawnAttempt(peer.PlayerId); yield break; }

			// 帧 A/B：解析 XML + 构建 CraftData（纯数据、不实例化 GameObject，快）
			XElement xml = null;
			CraftData craftData = null;
			try
			{
				xml = XElement.Parse(peer.CraftXml);
				craftData = Game.Instance.CraftLoader.LoadCraftImmediate(xml);
			}
			catch (Exception e)
			{
				Mod.LogError("SpawnRemoteCraftCoroutine: craft data load failed (player " + peer.PlayerId + "): " + e.Message);
				EndSpawnAttempt(peer.PlayerId);
				yield break;
			}
			if (xml == null || craftData == null || craftData.Assembly == null)
			{
				Mod.LogError("SpawnRemoteCraftCoroutine: craft data null for player " + peer.PlayerId);
				EndSpawnAttempt(peer.PlayerId);
				yield break;
			}
			if (!IsSpawnAttemptStillValid(peer)) { EndSpawnAttempt(peer.PlayerId); yield break; }

			// 发射点：状态包是地面坐标，用本端行星自转转成惯性坐标生成（与 SpawnRemoteCraftAtPosition 原逻辑一致）
			CraftNode localNode = FlightSceneScript.Instance.CraftNode as CraftNode;
			IPlanetNode planet = localNode != null ? localNode.Parent : null;
			if (localNode == null || planet == null) { EndSpawnAttempt(peer.PlayerId); yield break; }
			Vector3d planetPos = planet.SurfaceVectorToPlanetVector(data.Position);
			// 与 ApplyRemoteState 一致:data.Velocity 是地表相对速度,生成初始惯性速度需加回行星自转线速度。
			Vector3d planetVel = planet.SurfaceVectorToPlanetVector(data.Velocity) +
				planet.SurfaceVectorToPlanetVector(planet.CalculateSurfaceVelocity(data.Position));
			// data.Heading 已是"行星空间"朝向(发送端采样时已 FrameToPlanet)。
			// CreateLaunchLocation 内部会做 heading=RotationInverse*heading(把入参当行星空间)，
			// 再被 SpawnCraft 乘回 planet.Rotation → 最终 Heading=入参(行星空间)，故直接传入即可。
			Quaterniond spawnHeading = data.Heading;
			LaunchLocation location = LaunchLocation.CreateLaunchLocation(
				"MP_Remote_" + peer.PlayerId,
				planet, planetPos, planetVel, spawnHeading,
				localNode.ReferenceFrame,
				LaunchLocationType.SurfaceLockedGround);

			// 创建加载进度框：挂到对方位置上方（旋转白框 + 真实百分比）
			MpCraftLoadingIndicator indicator = CreateLoadingIndicator(peer.PlayerId, localNode.ReferenceFrame, planetPos);
			if (indicator == null) { EndSpawnAttempt(peer.PlayerId); yield break; }

			// 异步预加载部件 prefab（逐帧、真实 %）→ 进度框显示 N%；可随时取消（玩家离开/场景切换）
			int partCount = craftData.Assembly.Parts != null ? craftData.Assembly.Parts.Count : 0;
			Mod.LogLobby("MP: async preloading craft prefabs for player " + peer.PlayerId +
				" ('" + peer.PlayerName + "', craft='" + craftData.Name + "', parts=" + partCount + ")");
			yield return MpCraftPreloader.PreloadCraftPrefabs(craftData,
				progress => SetPlayerLoadProgress(peer.PlayerId, progress),
				() => !IsSpawnAttemptStillValid(peer));

			// 预加载后：玩家可能已离开 / 场景切换 / 已被其他路径生成
			if (!IsSpawnAttemptStillValid(peer))
			{
				DestroyLoadingIndicator(peer.PlayerId);
				EndSpawnAttempt(peer.PlayerId);
				yield break;
			}

			// 用预加载期间的最新状态包（长时间预加载后位置更准，减少跳变）
			Mod.RemoteDataPack spawnData = data;
			if (_pendingSpawnLatest.TryGetValue(peer.PlayerId, out Mod.RemoteDataPack latest)) spawnData = latest;

			// 清理进度框 + 挂起标记。SpawnRemoteCraftAtPosition 内同步登记 _remoteCrafts（无 yield），
			// 移除挂起标记到登记之间没有网络帧插入，不会重复起协程。
			DestroyLoadingIndicator(peer.PlayerId);
			EndSpawnAttempt(peer.PlayerId);

			SpawnRemoteCraftAtPosition(peer, spawnData, craftData, location, xml);
		}

		/// <summary>
		/// 校验生成协程当前是否仍有效：仍在飞行场景、玩家未离开、飞船信息仍在、且尚未被生成。
		/// （预加载期间每帧调用；玩家离开/场景切换/已生成 → false，协程提前停止。）
		/// </summary>
		private bool IsSpawnAttemptStillValid(MpPeer peer)
		{
			if (peer == null || FlightSceneScript.Instance == null) return false;
			if (peer.PlayerId == PlayerId) return false;
			bool stillThere;
			lock (_playersByPlayerId) { stillThere = _playersByPlayerId.ContainsKey(peer.PlayerId); }
			if (!stillThere) return false;
			if (_remoteCrafts.ContainsKey(peer.PlayerId)) return false;
			return !string.IsNullOrEmpty(peer.CraftXml);
		}

		/// <summary>生成协程退出时清理挂起状态（挂起标记、最新状态缓存、加载进度显示）。</summary>
		private void EndSpawnAttempt(int playerId)
		{
			_pendingSpawns.Remove(playerId);
			_pendingSpawnLatest.Remove(playerId);
			_playerLoadProgress.Remove(playerId);
		}

		/// <summary>取消并清理所有挂起的生成（停止联机 / 场景切换时调用）：销毁进度框 + 清空挂起状态。</summary>
		private void CancelPendingSpawns()
		{
			foreach (KeyValuePair<int, MpCraftLoadingIndicator> kv in _loadingIndicators)
			{
				if (kv.Value != null) kv.Value.DestroyIndicator();
			}
			_loadingIndicators.Clear();
			_pendingSpawns.Clear();
			_pendingSpawnLatest.Clear();
			_playerLoadProgress.Clear();
		}

		/// <summary>在指定玩家位置上方创建加载进度框并登记（离开/场景切换时可销毁）。</summary>
		private MpCraftLoadingIndicator CreateLoadingIndicator(int playerId, IReferenceFrame frame, Vector3d planetPos)
		{
			try
			{
				Vector3 worldPos = frame != null
					? frame.PlanetToFramePosition(planetPos) + Vector3.up * 4f
					: Vector3.zero;
				MpCraftLoadingIndicator ind = MpCraftLoadingIndicator.Create(worldPos);
				_loadingIndicators[playerId] = ind;
				return ind;
			}
			catch (Exception e)
			{
				Mod.LogError("CreateLoadingIndicator error (player " + playerId + "): " + e.Message);
				return null;
			}
		}

		/// <summary>销毁并移除指定玩家的加载进度框。</summary>
		private void DestroyLoadingIndicator(int playerId)
		{
			MpCraftLoadingIndicator ind;
			if (_loadingIndicators.TryGetValue(playerId, out ind))
			{
				if (ind != null) ind.DestroyIndicator();
				_loadingIndicators.Remove(playerId);
			}
		}

		/// <summary>记录玩家预加载进度（0..1）；达到 1f 视为完成，移除（玩家列表恢复延迟/状态显示）。</summary>
		private void SetPlayerLoadProgress(int playerId, float progress)
		{
			if (progress >= 1f) _playerLoadProgress.Remove(playerId);
			else _playerLoadProgress[playerId] = Mathf.Clamp01(progress);
		}

		/// <summary>指定玩家当前预加载进度（0..1）；未在加载返回 null（供 MultiPlayerUI 玩家列表显示 "⏳ N%"）。</summary>
		public float? GetPlayerLoadProgress(int playerId)
		{
			float p;
			if (_playerLoadProgress.TryGetValue(playerId, out p)) return p;
			return null;
		}

		/// <summary>
		/// 远程飞船懒初始化：等 CraftScript 构建好后应用幻影模式（禁止控制 + 禁用物理），
		/// 并把初始朝向设为首个状态包的 heading（直接赋值）。
		/// </summary>
		private void InitializeRemoteCraft(RemoteCraft rc)
		{
			if (rc.Node == null || rc.Node.CraftScript == null) return;
			try
			{
				rc.Node.AllowPlayerControl = false;

				// 真正关闭物理（停止重力/碰撞/物理对 Transform 的覆盖）。
				// 只靠 DisableCraftPhysicCalculation 清碰撞箱仍会被重力拉进地下、朝向被物理覆盖。
				// 注意：必须用 PhysicsChangeReason.Warp（而非 UnloadPhysics）！
				// 原因：MapCraft.OnCraftNodePhysicsDisabled 对 UnloadPhysics 会执行
				// MapItem.SwitchType<MapStaticOrbitItem>(this)，销毁 MapCraft GameObject 但 item 仍留在
				// registry，导致 MapView.UpdateMapItems 遍历时对已销毁的 MapCraft 调 transform.position → NRE。
				// Warp 被 IgnorePhysicsChange 视为"忽略"（不切换类型），可避免该 NRE。
				rc.Node.SetPhysicsEnabled(false, PhysicsChangeReason.Warp);
				CraftUtils.DisableCraftPhysicCalculation(ref rc.Node);

				// 强制进入"表面锁定 + 物理禁用"分支：
				// 该分支下游戏每帧会按 GroundedSurface* 放置飞船（见 CraftNode.Update），
				// 而我们会持续更新这些值使其跟随远程状态，避免游戏把飞船拉回出生点/掉进地下。
				rc.Node.InContactWithPlanet = true;

				// 额外把所有刚体设为 kinematic，防止任何残留物理移动（防穿地）
				foreach (BodyData body in rc.Node.CraftScript.Data.Assembly.Bodies)
				{
					if (body.BodyScript != null && body.BodyScript.RigidBody != null)
					{
						body.BodyScript.RigidBody.isKinematic = true;
					}
				}

				// 部件姿态不再在此"恢复"：part.Position/part.Rotation 是相对 craft 根的坐标，
				// 写进 part.localPosition/localRotation（相对 body）会摆错部件导致分裂/朝向偏差。
				// 反编译 PartData.Synchronize 证实该坐标系差异；且幽灵飞船从未启用物理，
				// SetPhysicsEnabled(false) 为 no-op，EnablePhysics(false)/RecenterTransformOnCoM 不会执行，
				// 部件本就被 CraftBuilder 按 XML 正确摆放，无需恢复。
				// body 的动态局部姿态改由状态包 BodyRotations 同步（见 ApplyRemoteState）。
				//Mod.Log("[朝向诊断|远端P" + rc.PlayerId + "飞船] 已跳过部件恢复循环(部件保持XML设计姿态)");

				if (rc.HasState)
				{
					// 初始状态：位置/速度/朝向一次性应用（含 RecalculateFrameState 刷新 Transform）
					if (rc.TryGetNewest(out Mod.RemoteDataPack newest))
					{
						ApplyRemoteState(rc, newest);
					}
				}
				// 幽灵船引擎尾焰:建立驱动表(液体 ExhaustThrottleOverride + 航发直接驱动)+ 抑制烟雾/加热副作用
				EngineVisualSync.SetupGhostEngineVisuals(rc);
				EngineVisualSync.DriveGhostEngineVisuals(rc);
				rc.IsInitialized = true;
				Mod.LogLobby("MP: remote craft initialized (ghost mode) for player " + rc.PlayerId);
			}
			catch (Exception e)
			{
				Mod.LogError("InitializeRemoteCraft FAILED (player " + rc.PlayerId + "): " + e.Message);
			}
		}

		/// <summary>每帧插值应用远程飞船状态（朝向直接赋值，与 Replay 一致）。</summary>
		private void UpdateRemoteCrafts()
		{
			if (FlightSceneScript.Instance == null) return;
			foreach (RemoteCraft rc in _remoteCrafts.Values)
			{
				if (rc.Node == null || !rc.HasState) continue;
				try
				{
					// 每帧强制走"表面锁定"分支：游戏可能把 InContactWithPlanet 清掉，
					// 一旦为 false 会走 else 分支推进轨道（带引力）导致飞船坠落/掉进地里。
					rc.Node.InContactWithPlanet = true;

					// CraftScript 可能延迟构建：先做一次懒初始化（幻影模式），未就绪则跳过本帧
					if (!rc.IsInitialized) InitializeRemoteCraft(rc);
					if (!rc.IsInitialized) continue;

					rc.TotalFrames++;
					// SP2 式:不用插值缓冲,始终拿最新包,按速度连续外推(dead-reckoning),再 per-frame 平滑。
					// 插值缓冲在 Steam 突发间隔下 gapEMA 滞后→lookback 过小→频繁欠载→幽灵跳到最新包
					// →"一卡一卡"。SP2 直接拿最新包,Lerp 平滑过渡,不发散不抖动,不依赖缓冲(CraftStateSerializer.cs:76-86)。
					if (rc.TryGetNewest(out Mod.RemoteDataPack latest))
					{
						// 连续外推(dead-reckoning):外推量 = 单向延迟(RTT/2) + 距最新包到达已过的时间(age)。
						// 目标随时间持续以最新速度前进 → 包到达/丢失/突发都不再让目标跳变 → 消除"一卡一卡"。
						// 旧逻辑外推量固定为 gapEMA(仅≈发包间隔,500ms+ 真实网络延迟下严重低估→幽灵恒滞后→大跳)。
						// SP2 用 physicsTime-senderTime 测单向延迟;我们没有时钟同步,用 RTT/2 近似(OnPong 更新)。
						// F3(2026-09-14,smoothing-comparison §五 / README §三):单向延迟 EMA —— RTT/2 裸值随 ping 抖动,
						// 直接进 ext 会晃目标(0.95/0.05 → 时间常数≈1s;首测直取)。
						float latencySec;
						if (rc.LatencyMs > 0f)
						{
							rc.LatencyEmaMs = rc.LatencyEmaMs <= 0f ? rc.LatencyMs : rc.LatencyEmaMs * 0.95f + rc.LatencyMs * 0.05f;
							latencySec = rc.LatencyEmaMs / 1000f;
						}
						else latencySec = rc.GapEmaMs / 1000f;
						// F1(2026-09-14):虚拟 age 时钟替代"距最新包到达的真实时间"。每帧 +dt,
						// 每包到达 −SendIntervalEst(PushSample 内)→ 突发到达时目标连续(锯齿消除,见字段注释)。
						rc.VirtualAge += Time.unscaledDeltaTime;
						float age = rc.VirtualAge;
						// ★ 暂停/冻结保护(2026-09,修"飞船有速度时暂停→观察方位置抽搐"):
						// 发送端暂停后 Position 冻结、Velocity 仍是非零旧值(暂停前最后一刻的速度)。
						// 此时若继续 Position + Velocity×age:每包到达把目标拉回近处、包间又按速度推进
						// → 目标以发包频率来回摆动 → 渲染层直接"抽搐"(高速船 k≈1 时平滑几乎不衰减)。
						// 修正:冻结期间把外推量按 RemotePausedRamp 收敛到固定单向延迟 latencySec(不带 age),
						// 即"停在最新包位置 + 网络传输本身占用的那段位移",不再人工推进目标。
						// 恢复运动(或对端解除暂停)时 ramp 在 0.15s 内回落到 0,重新把"包龄"加回外推量,避免瞬间跳变。
						bool pausedNow = rc.PktStallCount >= RemoteCraft.PositionStallPackets || rc.LastPktPausedFlag;
						if (pausedNow != rc.RemotePaused)
						{
							// 一次性状态跃迁日志(便于实测确认"暂停=冻结"是否按预期生效):
							// 进入冻结 → 速度外推被抑制,幽灵停在最新包位置;退出冻结 → 恢复正常 dead-reckoning。
							Mod.LogLobby("MP freeze P" + rc.PlayerId + ": " + (pausedNow ? "ENTER" : "EXIT") +
								" (flag=" + (rc.LastPktPausedFlag ? 1 : 0) + " stall=" + rc.PktStallCount +
								" pkΔ=" + rc.PktFreezeDeltaM.ToString("F4") + "m vel=" + latest.Velocity.magnitude.ToString("F2") + "m/s)");
						}
						rc.RemotePaused = pausedNow;
						rc.RemotePausedRamp = Mathf.MoveTowards(rc.RemotePausedRamp, pausedNow ? 1f : 0f,
							Time.unscaledDeltaTime / RemoteFreezeBlendSec);
						// 冻结期把外推量中的"包龄"部分收敛到 0 → ext → 固定单向延迟 latencySec,
						// 目标不再随包龄前进,也就没有"每包拉回/包间推进"的锯齿摆动。
						// ⚠️ 必须无条件收敛(不能加 ageNow>latencySec 守卫):暂停时发送端降频(8Hz),
						// 包龄往往小于 latencySec,旧守卫会漏钳制 → 高速船(如 85m/s)依旧锯齿(2026-09 实测发现)。
						float ageNow = age;
						if (rc.RemotePausedRamp > 0f)
						{
							ageNow = Mathf.Lerp(ageNow, 0f, rc.RemotePausedRamp);
						}
						rc.LastAgeNowSec = ageNow;
						float ext;
						float gapFreezeThr = Mathf.Max(rc.GapEmaMs * 3f / 1000f, 0.25f);
						bool gapFreezeNow = age > gapFreezeThr;
						if (gapFreezeNow)
						{
							// 发送端长时间无包(断连/暂停):冻结在最新已知位置+固定外推,不再随 age 前进(防幽灵飞走)
							// ⚠️ 2026-09-14 顿挫诊断:真实 Steam relay 突发下普通静默(>250ms)也会命中此分支
							// → 目标停止 → 包到达后追赶 → "停→冲"(smoothing-comparison §四 机制 B)。
							// MP gapfreeze 事件日志(配合 MP gap)用于确认是否被突发误触发。
							ext = latencySec;
						}
						else
						{
							ext = latencySec + ageNow; // 正常:持续外推;冻结期:ageNow→0 → ext→latencySec
						}
						if (gapFreezeNow != rc.GapFreezeActive)
						{
							rc.GapFreezeActive = gapFreezeNow;
							if (gapFreezeNow)
							{
								rc.WinGapFreezeHits++;
								Mod.LogLobby("MP gapfreeze P" + rc.PlayerId +
									": ENTER age=" + age.ToString("F3") + "s thr=" + gapFreezeThr.ToString("F3") + "s" +
									" gapEMA=" + rc.GapEmaMs.ToString("F0") + "ms" +
									" mRate=" + rc.SenderMotionRate.ToString("F3") +
									" vel=" + latest.Velocity.magnitude.ToString("F1") + "m/s" +
									" ext→" + ext.ToString("F3") + "s");
							}
							else
							{
								Mod.LogLobby("MP gapfreeze P" + rc.PlayerId +
									": EXIT age=" + age.ToString("F3") + "s thr=" + gapFreezeThr.ToString("F3") + "s" +
									" mRate=" + rc.SenderMotionRate.ToString("F3"));
							}
						}
						// 换算到发送端时间基:慢放(发送端 timeScale<1)时发送端包位置只按缩放时间推进,
						// 若外推仍按真实时间跑 → 目标每包"超前→拉回"锯齿(幅度 v×发包间隔×(1−倍率),慢放最严重)。
						// 正常飞行倍率≈1 → 行为不变;发送端暂停(倍率→0) → ext→0,幽灵精确停在包位置。
						// ⚠️ 用位置基 SenderMotionRate(发送端实际运动速率):用户慢放实测 FlightState.Time 不缩放
						// (包时间倍率恒 1.000),只有包位置位移如实反映慢放(2026-09-13 四轮)。
						ext *= rc.SenderMotionRate;
						if (ext > 1.0f) ext = 1.0f; // 安全上限 1s
						// 顿挫诊断窗口量(2026-09-14):ext / mRate 的 3s 摆动幅度 + 单帧渲染位移峰值。
						rc.LastExtSec = ext;
						if (ext > rc.WinExtMax) rc.WinExtMax = ext;
						if (rc.SenderMotionRate < rc.WinMRateMin) rc.WinMRateMin = rc.SenderMotionRate;
						if (rc.SenderMotionRate > rc.WinMRateMax) rc.WinMRateMax = rc.SenderMotionRate;
						latest.Position = latest.Position + latest.Velocity * ext;
						// 2 阶外推(2026-09-14,acceleration-smoothing):加速度项 ½·a·ext²。
						// ext 已 ×SenderMotionRate 换算到发送端时间基 → 加速度项 = ½·a·(ext·mRate)²,
						// 慢放/暂停天然兼容(暂停 mRate→0 → 两项都→0,与冻结逻辑无冲突)。
						// 发送端已 EMA+钳制,这里做二次防御(NaN/幅值),坏包不污染外推。
						rc.LastAccelTermM = 0f;
						rc.LastAngExtRad = 0f;
						if (EnableSecondOrderExtrap && ext > 0f)
						{
							Vector3 a = latest.Acceleration;
							if (!IsFinite(a)) a = Vector3.zero;
							float aMag = a.magnitude;
							if (aMag > MaxAccelMs) a *= (MaxAccelMs / aMag);
							if (aMag > 0.0001f)
							{
								latest.Position += a * (0.5f * ext * ext);
								rc.LastAccelTermM = 0.5f * aMag * ext * ext;
							}
						}
						// 朝向外推(2 阶域:旋转速率)。ω 为 craft 局部系(ModApi 约定),右乘
						// SrfRel *= Euler(ω_local·ext);符号约定待实测(EnableRotationExtrap 默认 false)。
						if (EnableRotationExtrap && ext > 0f)
						{
							Vector3 w = latest.AngularVelocity;
							if (!IsFinite(w)) w = Vector3.zero;
							float wMag = w.magnitude;
							if (wMag > MaxAngVelRad) w *= (MaxAngVelRad / wMag);
							if (wMag > 0.0001f)
							{
								Vector3 wLocal = new Vector3(-w.x, w.y, -w.z) * RotationExtrapSign;
								latest.SrfRel = Quaterniond.FromQuaternion(
									latest.SrfRel.ToQuaternion() * Quaternion.Euler(wLocal * ext * Mathf.Rad2Deg));
								rc.LastAngExtRad = wMag * ext;
							}
						}
						rc.ExtrapolatedFrames++; // 计数(SP2 风格:每帧按速度外推)
						if (rc.RemotePausedRamp >= 1f) rc.FrozenFrames++;
						rc.InterpPct = 1f;       // 始终在最新包(无缓冲插值)

						// P1:SP2 式平滑(指数收敛 + 近距快照 + 瞬移)
						latest = ApplyRemoteSmoothing(rc, latest, Time.unscaledDeltaTime);
						// 跳动诊断:本帧实际应用位置相对上一帧的位移(0 延迟+静止时应≈0;>0.5m 即跳动)。
						rc.LastMoveDeltaM = rc.HasApplied ? Vector3d.Distance(latest.Position, rc.LastRenderedPos) : 0.0;
						rc.LastRenderedPos = latest.Position;
						rc.MoveSumM += rc.LastMoveDeltaM;
						if (rc.LastMoveDeltaM > rc.WinMoveMaxM) rc.WinMoveMaxM = (float)rc.LastMoveDeltaM;
						// 朝向累计变化:慢旋转同样会被感知为"滑动"(尤其 body 相对质心有偏移时)
						if (rc.HasApplied)
						{
							rc.HeadDeg3s += Quaternion.Angle(rc.PrevSmoothedSrfRel.ToQuaternion(), rc.SmoothedSrfRel.ToQuaternion());
						}
						rc.PrevSmoothedSrfRel = rc.SmoothedSrfRel;
						ApplyRemoteState(rc, latest);
						// 每帧用插值后状态驱动幽灵船尾焰(液体 Route A 经 override、航发直接驱动)
						EngineVisualSync.DriveGhostEngineVisuals(rc);
						// 位置误差诊断:当前渲染位置 vs 最新包位置(SP2 式下始终≈0,因为始终用最新包)
						rc.LastPosErrorM = 0.0; // SP2 式:直接用最新包,无滞后
					}

					// 诊断：周期性记录平滑/网络状态（每 3 秒）：
					// 缓冲余量、实测包间间隔与抖动 EMA、欠载命中率、插值比例、渲染位置滞后。
					// 配合 NetSim 延迟模拟：抖动 EMA 应≈NetSim 抖动量；欠载%>0 即说明发生了"冻结-跳变"。
					if (Time.unscaledTime - rc.LastSmoothingLogTime > 3f)
					{
						rc.LastSmoothingLogTime = Time.unscaledTime;
						// 3s 窗口累计量:F2/F1 精度下 0.1m/s 级慢漂移显示为 0.00,必须用累计量+高精度捕捉。
						double move3s = rc.MoveSumM, pktJump = rc.PktJumpM;
						double headDeg = rc.HeadDeg3s;
						// 顿挫诊断窗口量(2026-09-14):⚠️ 先取局部、再清零、后打印(此前清零在前 → 恒打印 0)。
						float wMRateMin = rc.WinMRateMin, wMRateMax = rc.WinMRateMax, wExtMax = rc.WinExtMax;
						float wMaxGap = rc.WinMaxGapMs, wGapFreeze = rc.WinGapFreezeHits, wMoveMax = rc.WinMoveMaxM;
						int wLongGap = rc.WinLongGapCount;
						rc.MoveSumM = 0; rc.PktJumpM = 0; rc.HeadDeg3s = 0;
						rc.WinGapFreezeHits = 0; rc.WinMRateMin = 1f; rc.WinMRateMax = 1f; rc.WinExtMax = 0f;
						rc.WinMaxGapMs = 0f; rc.WinLongGapCount = 0; rc.WinMoveMaxM = 0f;
						string newestPos = "?", vel = "?", accStr = "?", wStr = "?";
						try
						{
							if (rc.TryGetNewest(out Mod.RemoteDataPack nw))
							{
								newestPos = nw.Position.x.ToString("F4") + "," + nw.Position.y.ToString("F4") + "," + nw.Position.z.ToString("F4");
								vel = nw.Velocity.magnitude.ToString("F3");
								accStr = nw.Acceleration.magnitude.ToString("F1");
								wStr = nw.AngularVelocity.magnitude.ToString("F2");
							}
						}
						catch { }
						// 朝向:应用 SrfRel 的 Yaw 角(连续日志对比可发现慢旋转——同样会被感知为"滑动")
						string headYaw = "?";
						try { headYaw = rc.SmoothedSrfRel.ToQuaternion().eulerAngles.y.ToString("F1"); } catch { }
						Mod.LogLobby("MP smoothing P" + rc.PlayerId +
							": buf=" + rc.BufferCount + "/" + RemoteCraft.BufferCapacity +
							" rtt/2=" + (rc.LatencyMs > 0f ? (rc.LatencyEmaMs > 0f ? rc.LatencyEmaMs : rc.LatencyMs).ToString("F0") : "?") + "ms" +
							" gapEMA=" + rc.GapEmaMs.ToString("F0") + "ms jitterEMA=" + rc.JitterEmaMs.ToString("F0") + "ms" +
							" frames=" + rc.TotalFrames + " snap=" + rc.SnapFrames + " extrap=" + rc.ExtrapolatedFrames +
							" frozen=" + rc.FrozenFrames + " paused=" + (rc.RemotePaused ? 1 : 0) +
							" rate=" + rc.SenderTimeRate.ToString("F3") + " mRate=" + rc.SenderMotionRate.ToString("F3") +
							" mRateWin=(" + wMRateMin.ToString("F2") + "," + wMRateMax.ToString("F2") + ")" +
							" extWin=(max=" + wExtMax.ToString("F3") + "s)" +
							" gapWin=(max=" + wMaxGap.ToString("F0") + "ms,>250ms=" + wLongGap + ")" +
							" gapFreeze=" + wGapFreeze.ToString("F0") +
							" moveMax=" + wMoveMax.ToString("F2") + "m" +
							" ageNow=" + rc.LastAgeNowSec.ToString("F3") + "s stall=" + rc.PktStallCount +
							" pkΔ=" + rc.PktFreezeDeltaM.ToString("F4") + "m" +
							" interpPct=" + rc.InterpPct.ToString("F2") +
							" moveDelta=" + rc.LastMoveDeltaM.ToString("F2") + "m bodyDelta=" + rc.LastBodyPoseDeltaM.ToString("F2") + "m" +
							" tfDrift=" + rc.LastTfDriftM.ToString("F2") + "m" +
							" move3s=" + move3s.ToString("F3") + "m pktJump=" + pktJump.ToString("F3") + "m" +
							" vel=" + vel + "m/s acc=" + accStr + "m/s² aExt=" + rc.LastAccelTermM.ToString("F2") + "m" +
							" w=" + wStr + "rad/s headYaw=" + headYaw + "deg head3s=" + headDeg.ToString("F1") + "deg" +
							" newest=(" + newestPos + ") posErr=" + rc.LastPosErrorM.ToString("F2") + "m");
					}

					// 慢放诊断(2026-09,0.5s 周期):接收端慢放(Time.timeScale<0.99)或发送端慢放(rate<0.99)时
					// 单独输出。背景:用户"本机慢放看静止对端船 → 整船一跳一跳",但 comRot/body 变换全恒定
					// (twitch 全 0),说明跳动在诊断采样点之外的渲染路径(根 transform/部件世界位置/地图渲染)。
					// 这里逐帧追踪 craft 根、首个部件、comRot 的最大单帧位移 + 观察距离,定位跳动来源。
					bool receiverSlow = Time.timeScale < 0.99f;
					if (receiverSlow || rc.SenderTimeRate < 0.99f || rc.SenderMotionRate < 0.99f)
					{
						Transform rootT = null, partT = null, comT = null;
						Vector3 rootP = Vector3.zero, partP = Vector3.zero, comP = Vector3.zero;
						try
						{
							if (rc.Node.CraftScript != null)
							{
								rootT = rc.Node.CraftScript.Transform;
								comT = rc.Node.CraftScript.CenterOfMass;
								if (rc.Node.CraftScript.Data != null && rc.Node.CraftScript.Data.Assembly.Parts.Count > 0)
									partT = rc.Node.CraftScript.Data.Assembly.Parts[0].PartScript.Transform;
							}
						}
						catch { }
						if (rootT != null) rootP = rootT.position;
						if (partT != null) partP = partT.position;
						if (comT != null) comP = comT.position;
						if (rc.DiagHasPrevSlowmo)
						{
							float dRoot = (rootP - rc.DiagPrevRootPos).magnitude;
							float dPart = (partP - rc.DiagPrevPartPos).magnitude;
							float dCom = (comP - rc.DiagPrevComPos).magnitude;
							if (dRoot > rc.DiagMaxRootDelta) rc.DiagMaxRootDelta = dRoot;
							if (dPart > rc.DiagMaxPartDelta) rc.DiagMaxPartDelta = dPart;
							if (dCom > rc.DiagMaxComDelta) rc.DiagMaxComDelta = dCom;
						}
						rc.DiagHasPrevSlowmo = true;
						rc.DiagPrevRootPos = rootP; rc.DiagPrevPartPos = partP; rc.DiagPrevComPos = comP;

						if (Time.unscaledTime - rc.LastSlowmoLogTime > 0.5f)
						{
							rc.LastSlowmoLogTime = Time.unscaledTime;
							string dist = "?";
							try
							{
								if (rc.Node.CraftScript != null && FlightSceneScript.Instance != null &&
									FlightSceneScript.Instance.CraftNode != null && FlightSceneScript.Instance.CraftNode.CraftScript != null)
								{
									dist = (rc.Node.CraftScript.FramePosition - FlightSceneScript.Instance.CraftNode.CraftScript.FramePosition).magnitude.ToString("F0");
								}
							}
							catch { }
							Mod.LogLobby("MP slowmo P" + rc.PlayerId +
								": timeScale=" + Time.timeScale.ToString("F3") +
								" rate=" + rc.SenderTimeRate.ToString("F3") +
								" mRate=" + rc.SenderMotionRate.ToString("F3") +
								" rootΔ=" + rc.DiagMaxRootDelta.ToString("F3") + "m" +
								" partΔ=" + rc.DiagMaxPartDelta.ToString("F3") + "m" +
								" comΔ=" + rc.DiagMaxComDelta.ToString("F3") + "m" +
								" dist=" + dist + "m" +
								" paused=" + (rc.RemotePaused ? 1 : 0) +
								" ageNow=" + rc.LastAgeNowSec.ToString("F3") + "s" +
								" moveDelta=" + rc.LastMoveDeltaM.ToString("F2") + "m" +
								" pkΔ=" + rc.PktFreezeDeltaM.ToString("F4") + "m");
							rc.DiagMaxRootDelta = 0f; rc.DiagMaxPartDelta = 0f; rc.DiagMaxComDelta = 0f;
						}
					}

					// 抽搐诊断(2026-09:双飞静止一方抽搐):每 1 秒高精度输出 comRot 连带/跨帧漂移
					// 与 body[0] 逐帧位移,定位反馈环来源:
					// - comLink   = 写 body 前后 comRot 连带位移(>0 说明"写 body 连带移动 comRot"存在);
					// - comCross = 跨帧冻结 comRot 漂移(>0 说明基准被上帧写入污染 → body 逐帧漂移);
					// - b0Δ      = body[0] 逐帧世界位移(渲染层抽搐幅度,静止时应≈0);
					// - b0ΔLate  = LateUpdate 重写造成的 body[0] 位移(Update/LateUpdate 双写不一致幅度);
					// - comLate  = LateUpdate 冻结的 comRot 位置(与 Update 冻结值差 >0 → 双写基准不同)。
					if (Time.unscaledTime - rc.LastTwitchLogTime > 1f)
					{
						rc.LastTwitchLogTime = Time.unscaledTime;
						Mod.LogLobby("MP twitch P" + rc.PlayerId +
							": comLink=" + rc.DiagComLinkM.ToString("F4") + "m" +
							" comCross=" + rc.DiagComCrossFrameM.ToString("F4") + "m" +
							" b0d=" + rc.DiagBody0DeltaM.ToString("F4") + "m" +
							" b0dLate=" + rc.DiagBody0DeltaLateM.ToString("F4") + "m" +
							" comFrozen=(" + rc.DiagComPosFrozen.x.ToString("F2") + "," + rc.DiagComPosFrozen.y.ToString("F2") + "," + rc.DiagComPosFrozen.z.ToString("F2") + ")" +
							" comAfter=(" + rc.DiagComPosAfterBodies.x.ToString("F2") + "," + rc.DiagComPosAfterBodies.y.ToString("F2") + "," + rc.DiagComPosAfterBodies.z.ToString("F2") + ")" +
							" comLate=(" + rc.DiagComPosLate.x.ToString("F2") + "," + rc.DiagComPosLate.y.ToString("F2") + "," + rc.DiagComPosLate.z.ToString("F2") + ")");
					}

					// 诊断：周期性记录远程飞船可见性（每 3 秒），用于定位"无法显示对方 craft"。
					// 若 goActive 变 false 或 renderer 被禁用，说明游戏原生机制在隐藏幽灵飞船。
					if (Time.unscaledTime - rc.LastVisualLogTime > 3f)
					{
						rc.LastVisualLogTime = Time.unscaledTime;
						try
						{
							GameObject rgo = rc.Node.GameObject;
							int rendererCount = 0, enabledCount = 0;
							if (rgo != null)
							{
								// 1.4.2:body 脱离 craft 层级后 GetComponentsInChildren 遍历不到 body 上的渲染器,
								// 改用逐 body 遍历(GetComponentsInCraft,等价游戏新 API)。
								List<Renderer> renderers = new List<Renderer>();
								CraftUtils.GetComponentsInCraft(rc.Node, renderers, true);
								foreach (Renderer r in renderers) { rendererCount++; if (r.enabled) enabledCount++; }
							}
						}
						catch (Exception e) { Mod.LogError("MP visualDiag error (p" + rc.PlayerId + "): " + e.Message); }
					}

					

					// 朝向诊断日志已暂时禁用（朝向已修复）
					//LogRemoteHeadingDiag(rc, rc.Target);
				}
				catch (Exception e) { Mod.LogError("UpdateRemoteCrafts error: " + e.Message); }
			}
		}

		/// <summary>直接应用远程状态：位置/速度经行星坐标设置，朝向直接赋值。</summary>
		private static void ApplyRemoteTransformDirect(RemoteCraft rc, Mod.RemoteDataPack data)
		{
			ApplyRemoteState(rc, data);
		}

		/// <summary>
		/// 平滑插帧：从远程飞船环形缓冲中取 renderTime 前后两包做插值（位置/速度线性、朝向球面插值）。
		/// 缓冲不足时回退为直接应用最新包（冻结），不产生跳变。
		/// </summary>
		private static bool TryGetInterpolatedState(RemoteCraft rc, float renderTime, out Mod.RemoteDataPack result)
		{
			result = default;
			if (rc.BufferCount == 0) return false;

			// 找到 renderTime 落入的区间 [i, i+1]（缓冲为 FIFO，按到达时间天然有序）
			int i = -1;
			for (int k = 0; k < rc.BufferCount; k++)
			{
				float at = rc.Buffer[(rc.BufferHead + k) % RemoteCraft.BufferCapacity].ArrivalTime;
				if (at <= renderTime) i = k;
				else break;
			}

			// renderTime 早于最旧样本：冻结在最旧样本（缓冲欠载，等新包）
			if (i < 0)
			{
				rc.UnderrunFrames++;
				rc.SnapFrames++;
				rc.InterpPct = 0f;
				result = rc.Buffer[rc.BufferHead].Data;
				return true;
			}
			// renderTime 晚于最新样本：缓冲欠载。冻结在最新已知位置(不按速度外推)。
			// SP2 的 velocity×dt 外推是为 50Hz 物理步设计的(外推量≈0.02s 量级,可忽略);
			// Steam 环境包间隔 50~100ms,外推 0.25s 封顶相当于 2~5 个包间隔的预测量,
			// 新包到达后平滑层追回 → 单帧大跳(Steam实测 P2 1~3m/帧,P3 52~74m/帧)。
			// 改为冻结:自适应 lookback 已保证大部分时间缓冲饱满、欠载罕见;冻结时短暂的
			// "微停顿"远优于"大幅度瞬移"(且新包到达后平滑层会自然过渡,不会跳变)。
			if (i >= rc.BufferCount - 1)
			{
				rc.UnderrunFrames++;
				RemoteCraft.StateSample newestS = rc.Buffer[(rc.BufferHead + rc.BufferCount - 1) % RemoteCraft.BufferCapacity];
				Mod.RemoteDataPack newest = newestS.Data;
				rc.SnapFrames++;
				rc.InterpPct = 1f;
				result = newest;
				return true;
			}

			int idxA = (rc.BufferHead + i) % RemoteCraft.BufferCapacity;
			int idxB = (rc.BufferHead + i + 1) % RemoteCraft.BufferCapacity;
			Mod.RemoteDataPack a = rc.Buffer[idxA].Data;
			Mod.RemoteDataPack b = rc.Buffer[idxB].Data;
			float tA = rc.Buffer[idxA].ArrivalTime;
			float tB = rc.Buffer[idxB].ArrivalTime;
			float pct = Mathf.Clamp01((renderTime - tA) / Mathf.Max(tB - tA, 0.0001f));
			rc.InterpPct = pct;

			// 位置/速度线性、朝向球面插值；body 相对位姿一并插值（位置 Lerp、旋转 Quaternion Slerp 防欧拉绕转），
			// 消除"每包 body 整体跳"（R1）。注意：interp= b 是浅拷贝，列表共享缓冲样本 → 插值结果必须新建列表。
			Mod.RemoteDataPack interp = b;
			interp.Position = Vector3d.Lerp(a.Position, b.Position, pct);
			interp.Velocity = Vector3d.Lerp(a.Velocity, b.Velocity, pct);
			interp.Heading = Quaterniond.FromQuaternion(Quaternion.Slerp(a.Heading.ToQuaternion(), b.Heading.ToQuaternion(), pct));
			interp.SrfRel = Quaterniond.FromQuaternion(Quaternion.Slerp(a.SrfRel.ToQuaternion(), b.SrfRel.ToQuaternion(), pct));
			// body 相对位姿插值：仅当 a/b 两包 body 列表**数量完全一致**时才按索引插值（发送端按 Assembly.Bodies
			// 顺序采样，数量一致即索引映射稳定）。数量不一致（对接/分离/残骸变化）时回退为沿用较新包 b 的整体拷贝，
			// 避免把"不同 body 的位姿"互相插值造成跳动。
			if (a.BodyPositions != null && b.BodyPositions != null &&
				a.BodyRotations != null && b.BodyRotations != null &&
				a.BodyPositions.Count > 0 && a.BodyPositions.Count == b.BodyPositions.Count &&
				a.BodyRotations.Count == b.BodyRotations.Count)
			{
				int bn = b.BodyPositions.Count;
				rc.ReuseInterpBodyPos.Clear();
				rc.ReuseInterpBodyRot.Clear();
				for (int bi = 0; bi < bn; bi++)
				{
					rc.ReuseInterpBodyPos.Add(Vector3.Lerp(a.BodyPositions[bi], b.BodyPositions[bi], pct));
					Quaternion trot = Quaternion.Slerp(Quaternion.Euler(a.BodyRotations[bi]), Quaternion.Euler(b.BodyRotations[bi]), pct);
					rc.ReuseInterpBodyRot.Add(trot.eulerAngles);
				}
				interp.BodyPositions = rc.ReuseInterpBodyPos;
				interp.BodyRotations = rc.ReuseInterpBodyRot;
			}
			result = interp;
			return true;
		}

		/// <summary>
		/// P1:SP2 式平滑——把 P0 的插值/外推结果(Target)做指数收敛 + 近距快照 + 大误差瞬移,
		/// 返回新的 RemoteDataPack(位置/朝向/每 body 位姿已平滑;BodyPositions/BodyRotations 为新建列表,
		/// 不改动 target/缓冲样本)。参考:SP2 CraftStateSerializer.cs:78-94(速度自适应 k + 瞬移 + Slerp)、
		/// BodyScript.cs:660-679(10·dt 平滑 + 近距快照)。首帧/body 数量变化时快照为 target。
		/// </summary>
		private static Mod.RemoteDataPack ApplyRemoteSmoothing(RemoteCraft rc, Mod.RemoteDataPack target, float dt)
		{
			// 防御:目标位姿含非有限值(坏包/越界)时直接快照,防 NaN 传播到 Transform 造成瞬移/消失。
			if (!IsFinite(target.Position) || !IsFinite(target.Velocity))
			{
				rc.SmoothedPos = target.Position;
				rc.SmoothedSrfRel = target.SrfRel;
				SnapSmoothedBodies(rc, target);
				rc.HasSmoothed = true;
				return target;
			}

			// 首帧/无平滑状态:直接快照为初始平滑值(避免从原点/上一艘船位置滑过来)
			if (!rc.HasSmoothed || dt <= 0f)
			{
				rc.SmoothedPos = target.Position;
				rc.SmoothedSrfRel = target.SrfRel;
				SnapSmoothedBodies(rc, target);
				rc.HasSmoothed = true;
				return target;
			}

			// --- 位置:静止锁定 + 速度自适应指数平滑(SP2 :84-85),大误差直接瞬移自愈(SP2 :78-81) ---
			float speed = (float)target.Velocity.magnitude;
			Vector3d smoothedPos;
			// 极低速且误差微小:直接锁到目标,杜绝指数平滑的缓慢蠕动(肉眼可见的"位置滑动")。
			// 判定放宽:speed<0.5 m/s(容忍发送端残余速度)且误差<0.05 m → 快照。
			// (旧判定 speed<0.05 && 误差<0.01m 过严:发送端残余速度稍>0.05 即永不锁死 → 平滑层永远在蠕动,
			//  且收敛时间常数≈1s,任何残差都被拖成肉眼可见的持续滑动,而 moveDelta<0.005m/帧 在 F2 日志里显示 0.00)
			if (speed < 0.5f && (target.Position - rc.SmoothedPos).sqrMagnitude < 0.0025f)
			{
				smoothedPos = target.Position;
			}
			else
			{
				float k = Mathf.Lerp(0.1f, 1f, Mathf.Min(1f, speed * 0.02f));
				// SP2 是"逐物理步(50Hz) Lerp(position, target, k)" → 等价帧率无关收敛速率 dt*50:
				// k=0.1 时时间常数≈0.2s(旧 dt*10 是≈1s,慢 5 倍 → 残差蠕动拖出可见滑动)。
				float alpha = 1f - Mathf.Pow(1f - k, dt * 50f);
				smoothedPos = Vector3d.Lerp(rc.SmoothedPos, target.Position, alpha);
				// 单帧位移上限(2026-09 三轮):平滑位置每帧最多移动 1.5×v×dt(物理可行上限)。
				// 高速机动/加减速时包到达会造成目标异常跳变(实测单帧 moveDelta 可达 5.8m、pkΔ 13m),
				// k→1 时 alpha≈1 全跟 → 渲染层单帧大跳 = 肉眼顿挫;改 k 上限又会给匀速飞行引入人工滞后。
				// 用速度上限摊平异常跳变:匀速飞行单帧目标移动 = v×dt < 上限 → 零影响(无滞后);
				// 异常跳变按上限逐帧收敛(13m → ~4 帧摊平),不滞后正常运动。
				// 静止锁定分支(上一行 if)不经过这里,低速蠕动抑制不受影响。
				Vector3d stepDelta = smoothedPos - rc.SmoothedPos;
				double maxStep = 1.5 * speed * dt;
				double stepLen = stepDelta.magnitude;
				if (stepLen > maxStep && maxStep > 1e-4)
				{
					smoothedPos = rc.SmoothedPos + stepDelta * (maxStep / stepLen);
				}
			}
			if (Vector3d.Distance(smoothedPos, target.Position) > 100.0) smoothedPos = target.Position;
			rc.SmoothedPos = smoothedPos;

			// --- 朝向:Slerp 平滑(SP2 :94 用 2.5*dt) ---
			float alphaRot = Mathf.Clamp01(2.5f * dt);
			Quaternion smoothedSrf = Quaternion.Slerp(rc.SmoothedSrfRel.ToQuaternion(), target.SrfRel.ToQuaternion(), alphaRot);
			rc.SmoothedSrfRel = Quaterniond.FromQuaternion(smoothedSrf);

			// --- 每 body 相对位姿:10·dt 平滑 + <0.01 近距快照(SP2 BodyScript.cs:660-679);数量变化时重对齐并快照 ---
			// 结果写进 rc.ReuseSmooth* 复用缓冲(ApplyRemoteState→LastApplied→LateUpdate 同帧读取,无跨帧别名问题),
			// 避免热路径每帧分配 List 引发 GC 卡顿。
			int n = target.BodyPositions != null ? target.BodyPositions.Count : 0;
			Mod.RemoteDataPack result = target;
			if (n > 0)
			{
				if (rc.SmoothedBodyPos == null || rc.SmoothedBodyPos.Length != n)
				{
					SnapSmoothedBodies(rc, target); // 重对齐
				}
				rc.ReuseSmoothBodyPos.Clear();
				rc.ReuseSmoothBodyRot.Clear();
				float alphaBody = Mathf.Clamp01(10f * dt);
				float maxBodyDelta = 0f;
				for (int i = 0; i < n; i++)
				{
					Vector3 tpos = (target.BodyPositions != null && i < target.BodyPositions.Count) ? target.BodyPositions[i] : rc.SmoothedBodyPos[i];
					Quaternion trot = (target.BodyRotations != null && i < target.BodyRotations.Count) ? Quaternion.Euler(target.BodyRotations[i]) : rc.SmoothedBodyRot[i];
					Vector3 prevSp = rc.SmoothedBodyPos[i];
					Vector3 sp = prevSp;
					Quaternion sr = rc.SmoothedBodyRot[i];
					if ((tpos - sp).sqrMagnitude < 0.01f) sp = tpos;
					else sp = Vector3.Lerp(sp, tpos, alphaBody);
					if (Quaternion.Angle(sr, trot) < 0.01f) sr = trot;
					else sr = Quaternion.Slerp(sr, trot, alphaBody);
					float bd = (sp - prevSp).magnitude;
					if (bd > maxBodyDelta) maxBodyDelta = bd;
					rc.SmoothedBodyPos[i] = sp;
					rc.SmoothedBodyRot[i] = sr;
					rc.ReuseSmoothBodyPos.Add(sp);
					rc.ReuseSmoothBodyRot.Add(sr.eulerAngles);
				}
				rc.LastBodyPoseDeltaM = maxBodyDelta;
				result.BodyPositions = rc.ReuseSmoothBodyPos;
				result.BodyRotations = rc.ReuseSmoothBodyRot;
			}

			// 组装结果(结构体拷贝;只替换平滑后的字段,其余沿用 target;列表用复用缓冲,不分配、不污染缓冲样本)
			result.Position = smoothedPos;
			result.SrfRel = Quaterniond.FromQuaternion(smoothedSrf);
			return result;
		}

		/// <summary>Vector3d 是否全为有限值(NaN/Inf 视为非法,防坏包污染平滑状态)。</summary>
		private static bool IsFinite(Vector3d v)
		{
			return !double.IsNaN(v.x) && !double.IsNaN(v.y) && !double.IsNaN(v.z) &&
				!double.IsInfinity(v.x) && !double.IsInfinity(v.y) && !double.IsInfinity(v.z);
		}

		/// <summary>Vector3 是否全为有限值(2 阶外推的加速度/角速度坏值防御)。</summary>
		private static bool IsFinite(Vector3 v)
		{
			return !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) &&
				!float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);
		}

		/// <summary>EMA 更新(指数移动平均;首次采样直接初始化)。</summary>
		private static Vector3 UpdateEma(Vector3 ema, ref bool has, Vector3 sample, float rate)
		{
			if (!has) { has = true; return sample; }
			return Vector3.Lerp(ema, sample, rate);
		}

		/// <summary>幅值钳制(保留方向;maxMag≤0 → 归零)。</summary>
		private static Vector3 ClampMagnitude(Vector3 v, float maxMag)
		{
			if (maxMag <= 0f) return Vector3.zero;
			float m = v.magnitude;
			return m > maxMag ? v * (maxMag / m) : v;
		}

		/// <summary>把 target 的 body 位姿快照进平滑数组(首帧 / body 数量变化时调用)。</summary>
		private static void SnapSmoothedBodies(RemoteCraft rc, Mod.RemoteDataPack target)
		{
			int n = target.BodyPositions != null ? target.BodyPositions.Count : 0;
			if (rc.SmoothedBodyPos == null || rc.SmoothedBodyPos.Length != n)
			{
				rc.SmoothedBodyPos = new Vector3[Mathf.Max(0, n)];
				rc.SmoothedBodyRot = new Quaternion[Mathf.Max(0, n)];
			}
			for (int i = 0; i < n; i++)
			{
				rc.SmoothedBodyPos[i] = (target.BodyPositions != null && i < target.BodyPositions.Count) ? target.BodyPositions[i] : Vector3.zero;
				rc.SmoothedBodyRot[i] = (target.BodyRotations != null && i < target.BodyRotations.Count) ? Quaternion.Euler(target.BodyRotations[i]) : Quaternion.identity;
			}
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
		private static void ApplyRemoteState(RemoteCraft rc, Mod.RemoteDataPack data)
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
		private static void ApplyRemoteGroundedSurface(RemoteCraft rc, Mod.RemoteDataPack data, IPlanetNode planet)
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

		// ---------------- 本机状态采样 ----------------

		private static int GetLocalCraftNodeId()
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

		/// <summary>诊断辅助：判断指定 CraftNode 是否仍登记在 FlightState.CraftNodes 中。</summary>
		private static bool IsNodeInFlightState(CraftNode node)
		{
			try
			{
				if (node == null || FlightSceneScript.Instance == null || FlightSceneScript.Instance.FlightState == null) return false;
				foreach (CraftNode cn in FlightSceneScript.Instance.FlightState.CraftNodes)
				{
					if (cn == node) return true;
				}
			}
			catch { }
			return false;
		}

		/// <summary>
		/// 取飞行状态中存储的本机飞船 craft XML（供联机交换）。
		/// 直接读取 FlightState 里已保存的飞船 XML，绕开 LoadCraftData()→LoadCraftImmediate()→CraftData 构造→GenerateXml()
		/// 这条容易在构造阶段触发空引用异常的链路。
		/// </summary>
		private static string GetLocalCraftXml()
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
		private bool TrySampleLocalCraft(out Mod.RemoteDataPack data)
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
				// body-sync P0:相对 comRot 位置采样需要 comRot 的 Transform(与接收端 TransformPoint 精确互逆,含 scale)
				Transform comRotTransform = craft.CraftScript.CenterOfMass != null
					? craft.CraftScript.CenterOfMass : craft.CraftScript.Transform;
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
					for (int bi = 0; bi < bodyList.Count; bi++)
					{
						if (bodyList[bi].BodyScript != null && bodyList[bi].BodyScript.Transform != null)
						{
							// 相对质心 = comRot⁻¹ * body世界旋转（帧空间）
							Quaternion relCom = Quaternion.Inverse(comRotUnity) * bodyList[bi].BodyScript.Transform.rotation;
							data.BodyRotations.Add(relCom.eulerAngles);
							// body-sync P0:相对 comRot 的位置(转轴/关节连接的子装配"整体移动"主要就是位置变化)。
							// 与 BodyRotations 同循环同索引,接收端 body.Transform.position = comRot.TransformPoint(relPos)。
							data.BodyPositions.Add(comRotTransform.InverseTransformPoint(bodyList[bi].BodyScript.Transform.position));
							// 抽搐诊断(发送端):每包 body[0] 相对 comRot 采样位置抖动。
							// 若静止时此处>0.01m,说明"发送端数据本身在抖"(来源:发送端自身 comRot/body 微动,
							// 或发送端 body 未静止),接收端平滑层只能衰减无法消除 → 需从发送端定位。
							if (bi == 0)
							{
								Vector3 s0 = comRotTransform.InverseTransformPoint(bodyList[bi].BodyScript.Transform.position);
								rcDiagBody0RelDelta = _diagBody0Rel.HasValue ? Vector3.Distance(s0, _diagBody0Rel.Value) : 0f;
								_diagBody0Rel = s0;
							}
						}
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

		// ---------------- 朝向诊断辅助 ----------------

		/// <summary>格式化 Quaterniond（朝向诊断日志用，短格式）。</summary>
		private static string Q(Quaterniond q)
		{
			return "(" + q.x.ToString("F3") + "," + q.y.ToString("F3") + "," + q.z.ToString("F3") + "," + q.w.ToString("F3") + ")";
		}

		/// <summary>格式化 Quaternion（朝向诊断日志用，短格式）。</summary>
		private static string Q(Quaternion q)
		{
			return "(" + q.x.ToString("F3") + "," + q.y.ToString("F3") + "," + q.z.ToString("F3") + "," + q.w.ToString("F3") + ")";
		}

	}
}
