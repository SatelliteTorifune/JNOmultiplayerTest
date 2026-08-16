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
		public int TickRate { get; private set; } = 20;
		[Tooltip("对端超时判定(ms)。TCP 下连接断开由 read loop 检测，此值仅用于半开连接兜底，应设得较大以容忍主线程卡顿/GC/场景加载/全屏切换")]
		public long TimeoutMs = 60000;

		/// <summary>客户端未收到房主 CraftDataAck 时，CraftData 重发间隔（秒）。</summary>
		private const float CraftResendIntervalSec = 1.5f;

		private readonly Dictionary<int, MpPeer> _playersByPlayerId = new Dictionary<int, MpPeer>();
		private readonly Dictionary<int, RemoteCraft> _remoteCrafts = new Dictionary<int, RemoteCraft>();
		private readonly HashSet<int> _spawnMissLogged = new HashSet<int>();
		private readonly Dictionary<int, float> _spawnAttemptTime = new Dictionary<int, float>(); // 生成尝试节流
		private float _sendTimer;
		private float _keepAliveTimer;
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

		/// <summary>收到远程玩家加入。</summary>
		public event Action<MpPeer> OnPlayerJoined;
		/// <summary>远程玩家离开/掉线。</summary>
		public event Action<MpPeer> OnPlayerLeft;
		/// <summary>收到远程飞船状态（playerId, nodeId, 时间, recdata）。</summary>
		public event Action<int, int, double, Mod.recdata> OnRemoteState;

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
			if (Instance == this) Instance = null;
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

		private static void ForceRemoteHeading(RemoteCraft rc, Mod.recdata data)
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
			if (data.BodyRotations != null)
			{
				IReadOnlyList<BodyData> bodies = rc.Node.CraftScript.Data.Assembly.Bodies;
				int n = Mathf.Min(bodies.Count, data.BodyRotations.Count);
				for (int i = 0; i < n; i++)
				{
					if (bodies[i].BodyScript != null && bodies[i].BodyScript.Transform != null)
					{
						bodies[i].BodyScript.Transform.localRotation = Quaternion.Euler(data.BodyRotations[i]);
					}
				}
			}
		}

		private void ProcessOutgoing()
		{
			// 使用 unscaledDeltaTime：游戏暂停（Time.deltaTime==0）时状态包也照常发送，
			// 避免暂停导致对端远程飞船冻结/失步（暂停相关问题的临时处理）。
			_sendTimer += Time.unscaledDeltaTime * 1000f;
			if (_sendTimer < SendIntervalMs) return;
			_sendTimer = 0f;

			Mod.recdata data;
			if (!TrySampleLocalCraft(out data)) return;
			// 客户端在收到 Welcome（拿到 PlayerId）前不发状态包：
			// 否则会以 PlayerId=-1 发包，房主无法关联到已登记玩家（"state for player -1"）。
			if (PlayerId < 0) return;

			
			// 周期性本机朝向/位置诊断日志已移除（原为 if(false) 禁用块；
			// 其内曾被加入过早 return，导致 ProcessOutgoing 每帧提前返回、状态包完全停发）
			double time = FlightSceneScript.Instance.FlightState.Time;
			byte[] packet = MpMessages.EncodeState(PlayerId, LocalNodeId, time, data);
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
					Transport.SendTo(peer, MpMessages.EncodePong(0));
					break;
				case MpMessageType.Pong:
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
			int playerId, nodeId; double time; Mod.recdata data;
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

		private class RemoteCraft
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

			// --- 平滑插帧：带时间戳环形缓冲（按到达端 unscaledTime 排列，暂停安全、容抖动/乱序） ---
			public readonly StateSample[] Buffer = new StateSample[BufferCapacity];
			public int BufferCount;          // 有效样本数
			public int BufferHead;           // 最旧样本索引（环形）
			public Mod.recdata LastApplied;  // 最近一次实际应用的状态（LateUpdate 用，避免"最新包覆盖插值"）
			public bool HasApplied;          // LastApplied 是否已有效

			public struct StateSample
			{
				public float ArrivalTime;  // 到达端 Time.unscaledTime（单调）
				public double PacketTime;  // 发送端 FlightState.Time（诊断用）
				public Mod.recdata Data;
			}

			/// <summary>环形追加一条样本；按到达时间天然有序，满则覆盖最旧。</summary>
			public void PushSample(float arrivalTime, double packetTime, Mod.recdata data)
			{
				int idx = (BufferHead + BufferCount) % BufferCapacity;
				Buffer[idx] = new StateSample { ArrivalTime = arrivalTime, PacketTime = packetTime, Data = data };
				if (BufferCount < BufferCapacity) BufferCount++;
				else BufferHead = (BufferHead + 1) % BufferCapacity;
				HasState = true;
			}

			/// <summary>取最新样本。</summary>
			public bool TryGetNewest(out Mod.recdata data)
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
		/// 用远程玩家的首个状态包位置生成其远程飞船（幻影模式）。
		/// 飞船一出现就在远程玩家的真实位置，而不是先出现在本机玩家身上。
		/// </summary>
		private void SpawnRemoteCraftAtPosition(MpPeer peer, Mod.recdata data)
		{
			try
			{
				if (peer.PlayerId == PlayerId) return;                 // 自己
				if (_remoteCrafts.ContainsKey(peer.PlayerId)) return;  // 已生成
				if (FlightSceneScript.Instance == null) return;        // 不在飞行场景
				if (string.IsNullOrEmpty(peer.CraftXml)) return;

				CraftNode localNode = FlightSceneScript.Instance.CraftNode as CraftNode;
				IPlanetNode planet = localNode != null ? localNode.Parent : null;
				if (localNode == null || planet == null) return;

				XElement xml = XElement.Parse(peer.CraftXml);
				CraftData craftData = Game.Instance.CraftLoader.LoadCraftImmediate(xml);

				// 发射点：状态包是地面坐标，用本端行星自转转成惯性坐标生成
				Vector3d planetPos = planet.SurfaceVectorToPlanetVector(data.Position);
				Vector3d planetVel = planet.SurfaceVectorToPlanetVector(data.Velocity);
				// data.Heading 已是"行星空间"朝向(发送端采样时已 FrameToPlanet)。
				// CreateLaunchLocation 内部会做 heading=RotationInverse*heading(把入参当行星空间)，
				// 再被 SpawnCraft 乘回 planet.Rotation → 最终 Heading=入参(行星空间)，故直接传入即可。
				Quaterniond spawnHeading = data.Heading;
				LaunchLocation location = LaunchLocation.CreateLaunchLocation(
					"MP_Remote_" + peer.PlayerId,
					planet, planetPos, planetVel, spawnHeading,
					localNode.ReferenceFrame,
					LaunchLocationType.SurfaceLockedGround);

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
						foreach (Renderer r in rgo.GetComponentsInChildren<Renderer>(true)) { rendererCount++; if (r.enabled) enabledCount++; }
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
					foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
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
		private void ApplyRemoteState(int playerId, int nodeId, double time, Mod.recdata data)
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

				// 生成尝试节流：状态包 20Hz 到达，失败时最多每 2 秒重试一次，避免刷屏
				float last;
				if (_spawnAttemptTime.TryGetValue(playerId, out last) && Time.unscaledTime - last < 2f) return;
				_spawnAttemptTime[playerId] = Time.unscaledTime;

				// 异步延迟生成：SpawnCraft（LoadCraftImmediate + 实例化全部部件/渲染器）在
				// 大飞船时可能阻塞主线程数秒（白屏），且期间不读网络导致对端写阻塞（"卡到无响应"）。
				// 改为协程延迟几帧再生成：先让本帧网络处理完（回 Ack/收状态包），再执行重量级生成。
				StartCoroutine(SpawnRemoteCraftCoroutine(peer, data));
				return;
			}

			// 平滑插帧：状态包入环形缓冲（按到达端 unscaledTime，暂停安全）。
			// 首个包也入缓冲（BufferCount=1 时下一帧直接应用），后续由 UpdateRemoteCrafts 取前后两包插值。
			rc.PushSample(Time.unscaledTime, time, data);
		}

		/// <summary>
		/// 协程延迟生成远程飞船：先让网络处理几帧（DrainIncoming/回 Ack/状态包），
		/// 再执行重量级 SpawnCraft，降低"加入时主线程长时间阻塞（白屏）/对端写阻塞（无响应）"。
		/// </summary>
		private IEnumerator SpawnRemoteCraftCoroutine(MpPeer peer, Mod.recdata data)
		{
			// 先跑完当前帧网络处理，再等 2 帧，让握手/回 Ack/状态包有充足时间流动
			yield return null;
			yield return null;

			// 协程延迟期间可能发生：离开飞行场景 / 玩家已离开 / 已被其他路径生成 / 飞船信息失效
			if (peer == null || FlightSceneScript.Instance == null) yield break;
			if (peer.PlayerId == PlayerId) yield break;
			bool stillThere = false;
			lock (_playersByPlayerId) { stillThere = _playersByPlayerId.ContainsKey(peer.PlayerId); }
			if (!stillThere) yield break;
			if (_remoteCrafts.ContainsKey(peer.PlayerId)) yield break;
			if (string.IsNullOrEmpty(peer.CraftXml)) yield break;

			SpawnRemoteCraftAtPosition(peer, data);
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
					if (rc.TryGetNewest(out Mod.recdata newest))
					{
						ApplyRemoteState(rc, newest);
					}
				}
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

					// 平滑插帧：renderTime = now - renderDelay，取前后两包插值；
					// 渲染延迟吸收抖动/乱序，避免"最新包覆盖"导致的橡皮筋/跳变。
					float renderTime = Time.unscaledTime - RenderDelayMs / 1000f;
					if (TryGetInterpolatedState(rc, renderTime, out Mod.recdata interp))
					{
						ApplyRemoteState(rc, interp);
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
								foreach (Renderer r in rgo.GetComponentsInChildren<Renderer>(true)) { rendererCount++; if (r.enabled) enabledCount++; }
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
		private static void ApplyRemoteTransformDirect(RemoteCraft rc, Mod.recdata data)
		{
			ApplyRemoteState(rc, data);
		}

		/// <summary>
		/// 平滑插帧：从远程飞船环形缓冲中取 renderTime 前后两包做插值（位置/速度线性、朝向球面插值）。
		/// 缓冲不足时回退为直接应用最新包（冻结），不产生跳变。
		/// </summary>
		private static bool TryGetInterpolatedState(RemoteCraft rc, float renderTime, out Mod.recdata result)
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
				result = rc.Buffer[rc.BufferHead].Data;
				return true;
			}
			// renderTime 晚于/等于最新样本：直接应用最新（缓冲不足，无前瞻样本）
			if (i >= rc.BufferCount - 1)
			{
				result = rc.Buffer[(rc.BufferHead + rc.BufferCount - 1) % RemoteCraft.BufferCapacity].Data;
				return true;
			}

			int idxA = (rc.BufferHead + i) % RemoteCraft.BufferCapacity;
			int idxB = (rc.BufferHead + i + 1) % RemoteCraft.BufferCapacity;
			Mod.recdata a = rc.Buffer[idxA].Data;
			Mod.recdata b = rc.Buffer[idxB].Data;
			float tA = rc.Buffer[idxA].ArrivalTime;
			float tB = rc.Buffer[idxB].ArrivalTime;
			float pct = Mathf.Clamp01((renderTime - tA) / Mathf.Max(tB - tA, 0.0001f));

			// 位置/速度线性、朝向球面插值；body 姿态/激活组沿用最新包（避免欧拉角绕转问题）
			Mod.recdata interp = b;
			interp.Position = Vector3d.Lerp(a.Position, b.Position, pct);
			interp.Velocity = Vector3d.Lerp(a.Velocity, b.Velocity, pct);
			interp.Heading = Quaterniond.FromQuaternion(Quaternion.Slerp(a.Heading.ToQuaternion(), b.Heading.ToQuaternion(), pct));
			interp.SrfRel = Quaterniond.FromQuaternion(Quaternion.Slerp(a.SrfRel.ToQuaternion(), b.SrfRel.ToQuaternion(), pct));
			result = interp;
			return true;
		}

		/// <summary>
		/// 统一应用远程飞船状态（坐标系自洽，与采样端一一对应）：
		/// ① GroundedSurface*：让游戏"表面锁定+物理禁用"分支跟随远程状态，避免被拉回/坠落；
		/// ② 位置/速度：地面坐标 → 行星空间 SetStateVectors；
		/// ③ 视觉朝向：帧空间"质心旋转"直接赋给根 Transform（XML 是质心坐标系，根=质心 part 才正确），
		///    body 用相对质心的局部旋转（根=质心，故 body.localRotation 直接可写）；
		/// ④ 刷新 FrameState：让 Transform.position 跟随逻辑位置。
		/// </summary>
		private static void ApplyRemoteState(RemoteCraft rc, Mod.recdata data)
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
			Vector3d planetVel = planet.SurfaceVectorToPlanetVector(data.Velocity);
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
			rc.Node.CraftScript.Transform.rotation = headingFrame;
			if (rc.Node.CraftScript.CenterOfMass != null)
			{
				rc.Node.CraftScript.CenterOfMass.rotation = headingFrame;
			}
			rc.LastAppliedHeading = headingFrame; // 记录本次写入值(诊断:对比 transformRot 判断是否被覆盖)
			if (data.BodyRotations != null && data.BodyRotations.Count > 0)
			{
				IReadOnlyList<BodyData> bodies = rc.Node.CraftScript.Data.Assembly.Bodies;
				int n = Mathf.Min(bodies.Count, data.BodyRotations.Count);
				for (int i = 0; i < n; i++)
				{
					if (bodies[i].BodyScript != null && bodies[i].BodyScript.Transform != null)
					{
						bodies[i].BodyScript.Transform.localRotation = Quaternion.Euler(data.BodyRotations[i]);
					}
				}
			}

			// ④ 刷新帧状态（Transform.position 跟随逻辑位置）
			if (frame != null)
			{
				CraftUtils.RecalculateFrameState(frame, rc.Node);
			}

			// ⑤ 手动刷新对方飞船 FlightData 的缓存字段(PositionNormalized/CraftForward),
			// 使 FlightData.Pitch/BankAngle(游戏 UI/Vizzy 读取)跟随同步后的 CenterOfMass。
			// 反编译确认:幽灵飞船(物理禁用/非玩家)不参与 IFlightUpdate, FlightData.Update 不被调用,
			// CraftForward/PositionNormalized 停在生成初值 → 对方 Pitch/Bank 显示旧朝向。
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
		}

		private static readonly PropertyInfo _groundedSurfacePositionProp =
			typeof(CraftNode).GetProperty("GroundedSurfacePosition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		private static readonly PropertyInfo _groundedSurfaceVelocityProp =
			typeof(CraftNode).GetProperty("GroundedSurfaceVelocity", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		private static readonly PropertyInfo _groundedSurfaceRotationProp =
			typeof(CraftNode).GetProperty("GroundedSurfaceRotation", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		// 幽灵飞船不参与 IFlightUpdate, FlightData 的 PositionNormalized/CraftForward 停旧值,
		// 需手动刷新(用反射写 private set),使 FlightData.Pitch/BankAngle 跟随同步后的 CenterOfMass。
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
		private static void ApplyRemoteGroundedSurface(RemoteCraft rc, Mod.recdata data, IPlanetNode planet)
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
		private bool TrySampleLocalCraft(out Mod.recdata data)
		{
			data = new Mod.recdata();
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
				Vector3d vel = craft.Parent.PlanetVectorToSurfaceVector(craft.Velocity);
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
				data = new Mod.recdata(pos, vel, heading);

				// 同步每个 body 的局部姿态。关键：BodyRotations 必须存"相对质心(comRot)"的旋转，
				// 因为接收端根=comRot（发送的 heading），且 XML 的 body/part 是质心坐标系。
				// 若采样"相对根"的 localRotation，而发送端根≠comRot（座椅朝向，实测差~17°），
				// 接收端按 comRot 摆放 body 时会整体转错 → "分裂/散架 + 朝向不一致"。
				Quaternion comRotUnity = craft.CraftScript.CenterOfMass != null
					? craft.CraftScript.CenterOfMass.rotation : craft.CraftScript.Transform.rotation;
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
						}
					}
				}

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
