using System;
using System.Collections.Generic;
using System.Reflection;
using System.Xml.Linq;
using Assets.Scripts.Flight;
using Assets.Scripts.Flight.Sim;
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
	public class MpNetworkManager : MonoBehaviour
	{
		public static MpNetworkManager Instance { get; private set; }

		[NonSerialized] public TcpTransport Transport = new TcpTransport();

		public bool IsServer { get; private set; }
		public bool IsConnected { get; private set; }
		public int PlayerId { get; private set; } = -1;    // 房主 = 0
		public int LocalNodeId { get; private set; } = -1; // 本机飞船 NodeId
		public string PlayerName { get; private set; } = "Player";

		[Tooltip("状态包发送间隔(ms)，默认 50ms = 20Hz")]
		public float SendIntervalMs = 50f;
		[Tooltip("对端超时判定(ms)。TCP 下连接断开由 read loop 检测，此值仅用于半开连接兜底，应设得较大以容忍主线程卡顿/GC/场景加载")]
		public long TimeoutMs = 15000;

		/// <summary>客户端未收到房主 CraftDataAck 时，CraftData 重发间隔（秒）。</summary>
		private const float CraftResendIntervalSec = 1.5f;

		private readonly Dictionary<int, MpPeer> _playersByPlayerId = new Dictionary<int, MpPeer>();
		private readonly Dictionary<int, RemoteCraft> _remoteCrafts = new Dictionary<int, RemoteCraft>();
		private readonly HashSet<int> _spawnMissLogged = new HashSet<int>();
		private readonly Dictionary<int, float> _spawnAttemptTime = new Dictionary<int, float>(); // 生成尝试节流
		private float _sendTimer;
		private float _keepAliveTimer;
		private float _selfStateLogTimer;
		private float _craftResendTimer; // 客户端重发 CraftData 节流计时
		private float _hostCraftResendTimer; // 房主重发 host craft（PlayerJoin）节流计时
		private float _remoteVisualTimer; // 远程飞船视觉强制恢复节流计时（游戏可能原生隐藏非活动幽灵飞船的 Renderer）
		private long _lastPingTick;
		private bool _craftReported;      // 本机飞船已上报且被房主确认（客户端收到 CraftDataAck 才置 true）
		// 房主：记录"已发给客户端、但尚未收到 PlayerJoinAck 确认"的 host craft（key=peer.EndPoint）。
		private readonly Dictionary<string, float> _hostCraftResend = new Dictionary<string, float>();
		private string _localCraftXml = string.Empty;
		private static float _headingDiagTimer; // 朝向诊断日志节流计时器

		/// <summary>收到远程玩家加入。</summary>
		public event Action<MpPeer> OnPlayerJoined;
		/// <summary>远程玩家离开/掉线。</summary>
		public event Action<MpPeer> OnPlayerLeft;
		/// <summary>收到远程飞船状态（playerId, nodeId, 时间, recdata）。</summary>
		public event Action<int, int, double, Mod.recdata> OnRemoteState;

		private void Awake()
		{
			Instance = this;
			Transport.OnDataReceived += HandlePacket;
			Transport.OnPeerTimeout += HandlePeerTimeout;
			OnPlayerLeft += HandlePlayerLeft;
			OnRemoteState += ApplyRemoteState;
			Mod.LogLobby("MpNetworkManager created on GameObject '" + gameObject.name + "' (Awake)");
		}

		private void OnDestroy()
		{
			Mod.LogLobby("MpNetworkManager destroyed (OnDestroy)");
			Transport.OnDataReceived -= HandlePacket;
			Transport.OnPeerTimeout -= HandlePeerTimeout;
			OnPlayerLeft -= HandlePlayerLeft;
			OnRemoteState -= ApplyRemoteState;
			Transport.Stop();
			if (Instance == this) Instance = null;
		}

		// ---------------- 生命周期 API ----------------

		/// <summary>作为房主开启房间。</summary>
		public bool Host(int port)
		{
			Stop();
			Mod.LogLobby("MP.Host(): attempting to bind TCP on port " + port + " ...");
			if (!Transport.Start(port))
			{
				Mod.LogError("MP.Host FAILED: TcpTransport.Start(" + port + ") returned false (port may be in use)");
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
			PlayerName = string.IsNullOrWhiteSpace(playerName) ? "Player" : playerName;
			LocalNodeId = GetLocalCraftNodeId();
			_craftReported = false;
			byte[] hello = MpMessages.EncodeHello(PlayerName);
			Mod.LogLobby("MP.Join(): connecting to " + host + ":" + port + " as '" + PlayerName + "' ...");
			if (!Transport.StartClient(host, port, hello))
			{
				Mod.LogError("MP.Join FAILED: TcpTransport.StartClient(" + host + ":" + port + ") returned false");
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
			lock (_playersByPlayerId) _playersByPlayerId.Clear();
			_remoteCrafts.Clear();
			Mod.LogLobby("MP.Stop: wasServer=" + wasServer + ", wasConnected=" + wasConnected +
				", wasPlayerId=" + wasPlayerId + ", Transport.IsRunning=" + Transport.IsRunning);
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
				// 房主：广播自己的飞船给所有客户端；已广播过且未变化则跳过。
				if (_craftReported && !changed) return;
				Transport.Broadcast(MpMessages.EncodePlayerJoin(PlayerId, LocalNodeId, craftXml));
				_craftReported = true; // 房主无需确认（新加入者由 OnHello 补发）
				// 对所有已连接 peer 登记待确认：客户端回 PlayerJoinAck 前周期性重发（防公网大分片丢失）。
				foreach (MpPeer p in Transport.GetPeers())
				{
					_hostCraftResend[p.EndPoint.ToString()] = Time.unscaledTime;
				}
				Mod.LogLobby("MP.RefreshLocalCraft (host): broadcast PlayerJoin playerId=" + PlayerId + ", nodeId=" + LocalNodeId +
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
						Mod.LogLobby("MP.RefreshLocalCraft (client): sent CraftData nodeId=" + LocalNodeId + " to host " + peer.EndPoint +
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
			// （房主飞船 XML 可能达 500KB+，公网 UDP 大分片会丢，必须确认前持续重发）。
			if (IsServer && _hostCraftResend.Count > 0 && LocalNodeId >= 0 && !string.IsNullOrEmpty(_localCraftXml))
			{
				_hostCraftResendTimer -= Time.unscaledDeltaTime;
				if (_hostCraftResendTimer <= 0f)
				{
					_hostCraftResendTimer = CraftResendIntervalSec;
					byte[] hostJoin = MpMessages.EncodePlayerJoin(PlayerId, LocalNodeId, _localCraftXml);
					foreach (MpPeer peer in Transport.GetPeers())
					{
						if (_hostCraftResend.ContainsKey(peer.EndPoint.ToString()))
						{
							Transport.SendTo(peer, hostJoin);
							Mod.LogLobby("MP.Update (host): resend host craft PlayerJoin (nodeId=" + LocalNodeId + ") to " +
								peer.EndPoint + " (unacked)");
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

			// 周期性本机状态日志（每 10 秒一次，便于与接收端目标状态核对）
			_selfStateLogTimer -= Time.unscaledDeltaTime;
			if (_selfStateLogTimer <= 0f)
			{
				_selfStateLogTimer = 10f;
				CraftNode selfNode = FlightSceneScript.Instance.CraftNode as CraftNode;
				Quaternion tr = (selfNode != null && selfNode.CraftScript != null) ? selfNode.CraftScript.Transform.rotation : Quaternion.identity;
				Quaterniond craftHeading = selfNode != null ? selfNode.Heading : Quaterniond.identity;
				double planetAngle = (selfNode != null && selfNode.Parent != null) ? selfNode.Parent.RotationAngle : double.NaN;
				double frameAngle = (selfNode != null && selfNode.GameView != null) ? selfNode.GameView.ReferenceFrame.RotationAngle : double.NaN;
				Quaternion rootPartRot = (selfNode != null && selfNode.CraftScript != null && selfNode.CraftScript.RootPart != null && selfNode.CraftScript.RootPart.Transform != null)
					? selfNode.CraftScript.RootPart.Transform.rotation : Quaternion.identity;
				Quaternion comRot = (selfNode != null && selfNode.CraftScript != null && selfNode.CraftScript.CenterOfMass != null)
					? selfNode.CraftScript.CenterOfMass.rotation : Quaternion.identity;
				// [朝向诊断|本机P{pid}] 发送端朝向快照（每10秒）。
				// 发送heading=帧空间CoM旋转(即将发出)；行星空间=FrameToPlanet(发送heading)；
				// 根Transform=本机根(帧空间)；rootPart=命令舱世界旋转；游戏Heading=行星字段。
				// fwd/up=飞船视觉"前方/上方"方向；body摘要=首/次/末body局部欧拉角(供与接收端对比)。
				Quaterniond headingToPlanet = (selfNode != null && selfNode.GameView != null)
					? selfNode.GameView.ReferenceFrame.FrameToPlanetRotation(data.Heading.ToQuaternion()) : Quaterniond.identity;
				Vector3 fwd = (selfNode != null && selfNode.CraftScript != null) ? selfNode.CraftScript.Transform.forward : Vector3.zero;
				Vector3 up = (selfNode != null && selfNode.CraftScript != null) ? selfNode.CraftScript.Transform.up : Vector3.zero;
				/*Mod.Log("[朝向诊断|本机P" + PlayerId + "] 发送heading(帧)=" + Q(data.Heading) +
					" | 行星空间=" + Q(headingToPlanet) +
					" | 根Transform=" + Q(tr) +
					" | comRot=" + Q(comRot) +
					" | rootPartRot=" + Q(rootPartRot) +
					" | 游戏Heading(行星)=" + Q(craftHeading) +
					" | fwd=(" + fwd.x.ToString("F2") + "," + fwd.y.ToString("F2") + "," + fwd.z.ToString("F2") + ")" +
					" up=(" + up.x.ToString("F2") + "," + up.y.ToString("F2") + "," + up.z.ToString("F2") + ")" +
					" | planetAngle=" + planetAngle.ToString("F2") + " frameAngle=" + frameAngle.ToString("F4") +
					" | body:" + BuildBodyPoseSummary(selfNode != null ? selfNode.CraftScript : null));*/
			}

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
					_lastPingTick = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
					break;
				case MpMessageType.CraftDataAck:
					OnCraftDataAck(packet);
					break;
				case MpMessageType.PlayerJoinAck:
					OnPlayerJoinAck(peer, packet);
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
			if (peer == null || peer.EndPoint == null) return;
			if (playerId == PlayerId)
			{
				if (_hostCraftResend.Remove(peer.EndPoint.ToString()))
				{
					Mod.LogLobby("MP.OnPlayerJoinAck (host): peer " + peer.EndPoint + " confirmed host craft, stop resending");
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
			Mod.LogLobby("MP.OnHello (host): '" + name + "' from " + peer.EndPoint + " joined as PlayerId=" + peer.PlayerId +
				", sent Welcome, total peers=" + Transport.GetPeersCount());

			// 把当前所有已登记玩家（含房主自己）的飞船信息同步给新加入者
			foreach (MpPeer p in GetPlayers())
			{
				if (p.NodeId >= 0 && !string.IsNullOrEmpty(p.CraftXml))
				{
					Transport.SendTo(peer, MpMessages.EncodePlayerJoin(p.PlayerId, p.NodeId, p.CraftXml));
					Mod.LogLobby("MP.OnHello (host): sent existing player " + p.PlayerId + " craft to new client");
				}
			}
			if (LocalNodeId >= 0 && !string.IsNullOrEmpty(_localCraftXml))
			{
				Transport.SendTo(peer, MpMessages.EncodePlayerJoin(PlayerId, LocalNodeId, _localCraftXml));
				Mod.LogLobby("MP.OnHello (host): sent host craft PlayerJoin (nodeId=" + LocalNodeId + ") to new client " + peer.EndPoint);
				// 登记待确认：客户端回 PlayerJoinAck 前，每 1.5s 重发 host craft（防公网大分片丢失）。
				_hostCraftResend[peer.EndPoint.ToString()] = Time.unscaledTime;
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

			// 广播给所有客户端（含新玩家本人，用于互相生成对方飞船）
			Transport.Broadcast(MpMessages.EncodePlayerJoin(peer.PlayerId, peer.NodeId, craftXml));
			Mod.LogLobby("MP.OnCraftData (host): '" + peer.PlayerName + "' craft registered NodeId=" + nodeId +
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
			peer.PlayerId = playerId;
			peer.IsServer = true;
			Mod.LogLobby("MP.OnWelcome (client): received Welcome, PlayerId=" + playerId +
				", nodeId=" + nodeId + ", serverTick=" + serverTick + ", peer=" + peer.EndPoint);
		}

		private void OnPlayerJoin(MpPeer peer, byte[] packet)
		{
			int playerId, nodeId; string craftXml;
			if (!MpMessages.TryDecodePlayerJoin(packet, out playerId, out nodeId, out craftXml)) return;
			MpPeer p = new MpPeer { EndPoint = peer.EndPoint, PlayerId = playerId, NodeId = nodeId, CraftXml = craftXml };
			RegisterPlayer(p);
			Mod.LogLobby("MP.OnPlayerJoin: playerId=" + playerId + ", nodeId=" + nodeId +
				", craftXmlLen=" + (craftXml == null ? 0 : craftXml.Length) + ", peer=" + peer.EndPoint);
			// 客户端回 PlayerJoinAck：告知房主已收到该玩家飞船，房主据此停止重发（防公网大分片丢失）。
			if (!IsServer && peer != null && peer.EndPoint != null)
			{
				Transport.SendTo(peer, MpMessages.EncodePlayerJoinAck(playerId));
			}
			OnPlayerJoined?.Invoke(p);
			// M2：根据 craftXml 生成远程飞船
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

		// ---------------- 工具 ----------------

		/// <summary>房主广播暂停/恢复（临时禁用，见 OnPause）。</summary>
		public void BroadcastPause(bool paused)
		{
			Mod.LogLobby("BroadcastPause temporarily disabled (paused=" + paused + ")");
			// if (!IsServer) return;
			// Transport.Broadcast(MpMessages.EncodePause(paused));
			// if (FlightSceneScript.Instance != null)
			// {
			// 	FlightSceneScript.Instance.TimeManager.RequestPauseChange(paused, false);
			// }
		}

		/// <summary>房主广播玩家离开。</summary>
		public void BroadcastPlayerLeave(int playerId)
		{
			Transport.Broadcast(MpMessages.EncodePlayerLeave(playerId));
		}

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
			Mod.LogLobby("MP peer timeout: " + peer.EndPoint + " (PlayerId=" + peer.PlayerId + ", NodeId=" + peer.NodeId + ")");
			if (peer != null && peer.EndPoint != null) _hostCraftResend.Remove(peer.EndPoint.ToString());
			MpPeer removed = null;
			lock (_playersByPlayerId)
			{
				MpPeer match = null;
				foreach (MpPeer p in _playersByPlayerId.Values)
				{
					if (p.EndPoint != null && p.EndPoint.Equals(peer.EndPoint)) { match = p; break; }
				}
				if (match != null)
				{
					_playersByPlayerId.Remove(match.PlayerId);
					removed = match;
				}
			}
			if (removed != null)
			{
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
			public int PlayerId;
			public CraftNode Node;
			public Mod.recdata Prev;
			public Mod.recdata Target;
			public bool HasState;      // 是否已收到至少一个状态包
			public bool HasPrev;       // 是否已进入插值阶段
			public bool IsInitialized; // 幻影模式是否已应用（CraftScript 延迟构建后置 true）
			public float InterpStartTime;
			public float LastStateLogTime; // 周期性状态日志计时
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
				// data.Heading 是帧空间旋转，而 CreateLaunchLocation 内部会做 heading=RotationInverse*heading
				// （把入参当行星空间），再被 SpawnCraft 乘回 planet.Rotation → 最终 Heading=入参。
				// 因此这里必须传入"行星空间"朝向，否则初始 CraftNode.Heading 会存帧空间值。
				Quaterniond spawnHeading = data.Heading;
				if (localNode.GameView != null && localNode.GameView.ReferenceFrame != null)
				{
					spawnHeading = localNode.GameView.ReferenceFrame.FrameToPlanetRotation(data.Heading.ToQuaternion());
				}
				LaunchLocation location = LaunchLocation.CreateLaunchLocation(
					"MP_Remote_" + peer.PlayerId,
					planet, planetPos, planetVel, spawnHeading,
					localNode.ReferenceFrame,
					LaunchLocationType.SurfaceLockedGround);

				CraftNode remote = FlightSceneScript.Instance.SpawnCraft(peer.PlayerName + " (MP)", craftData, location, xml);
				if (remote == null)
				{
					Mod.LogError("SpawnRemoteCraftAtPosition: SpawnCraft returned null for player " + peer.PlayerId);
					return;
				}

				// [朝向诊断|远端P{pid}飞船] 生成时：Heading(行星字段) 应≈ 传入的 spawnHeading(行星空间)。
				//Mod.Log("[朝向诊断|远端P" + peer.PlayerId + "飞船] 生成: Heading(行星)=" + Q(remote.Heading) +
					//" | 传入spawnHeading(行星)=" + Q(spawnHeading));

				// 先登记（无论 CraftScript 是否已构建），避免状态包反复触发重新生成
				RemoteCraft rc = new RemoteCraft { PlayerId = peer.PlayerId, Node = remote, Target = new Mod.recdata() };
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
				if (remote.CraftScript != null)
				{
					Quaternion spawnRot = remote.CraftScript.Transform.rotation;
					Mod.Log("MP headingDiag spawn p" + peer.PlayerId + ": dataHeading=(" +
						data.Heading.x.ToString("F3") + "," + data.Heading.y.ToString("F3") + "," + data.Heading.z.ToString("F3") + "," + data.Heading.w.ToString("F3") + ")" +
						", spawnHeading=(" + remote.Heading.x.ToString("F3") + "," + remote.Heading.y.ToString("F3") + "," + remote.Heading.z.ToString("F3") + "," + remote.Heading.w.ToString("F3") + ")" +
						", spawnRot=(" + spawnRot.x.ToString("F3") + "," + spawnRot.y.ToString("F3") + "," + spawnRot.z.ToString("F3") + "," + spawnRot.w.ToString("F3") + ")");
				}

				// 幻影模式 + 初始朝向：CraftScript 可能延迟构建，在 UpdateRemoteCrafts 里懒初始化（见 InitializeRemoteCraft）
				Mod.LogLobby("MP: spawned remote craft for player " + peer.PlayerId + " at remote position (nodeId=" + peer.NodeId + ", localNode=" + remote.NodeId + ")" +
					", surfacePos=(" + data.Position.x.ToString("F1") + "," + data.Position.y.ToString("F1") + "," + data.Position.z.ToString("F1") + ")" +
					", heading=(" + data.Heading.x.ToString("F3") + "," + data.Heading.y.ToString("F3") + "," + data.Heading.z.ToString("F3") + "," + data.Heading.w.ToString("F3") + ")");
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
			if (!_remoteCrafts.TryGetValue(playerId, out rc)) return;
			_remoteCrafts.Remove(playerId);
			if (rc.Node != null && rc.Node.CraftScript != null)
			{
				rc.Node.GameObject.SetActive(false); // MVP：隐藏而非销毁
				Mod.LogLobby("MP: removed (hidden) remote craft for player " + playerId);
			}
		}

		/// <summary>
		/// 强制恢复远程飞船的视觉：游戏原生机制可能对"非活动/幽灵"飞船禁用 Renderer 或
		/// 停用 GameObject（实测靠近本机飞船时视觉模型消失但 CraftNode 仍在）。
		/// 节流恢复，避免每帧遍历 Renderer 的开销。
		/// </summary>
		private void EnforceRemoteCraftVisuals()
		{
			if (_remoteCrafts.Count == 0) return;
			_remoteVisualTimer -= Time.unscaledDeltaTime;
			if (_remoteVisualTimer > 0f) return;
			_remoteVisualTimer = 0.5f;

			foreach (RemoteCraft rc in _remoteCrafts.Values)
			{
				if (rc.Node == null || rc.Node.GameObject == null) continue;
				GameObject go = rc.Node.GameObject;
				try
				{
					if (!go.activeSelf)
					{
						go.SetActive(true);
						Mod.Log("MP: re-activated remote craft GameObject for player " + rc.PlayerId);
					}
					foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
					{
						if (!r.enabled)
						{
							r.enabled = true;
							Mod.Log("MP: re-enabled renderer on remote craft for player " + rc.PlayerId);
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

				SpawnRemoteCraftAtPosition(peer, data);
				if (!_remoteCrafts.TryGetValue(playerId, out rc) || rc.Node == null) return;
			}

			if (rc.HasState)
			{
				rc.Prev = rc.Target;
				rc.HasPrev = true;
			}
			else
			{
				rc.Prev = data;
			}
			rc.Target = data;
			rc.HasState = true;
			rc.InterpStartTime = Time.unscaledTime;
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
				rc.Node.SetPhysicsEnabled(false, PhysicsChangeReason.UnloadPhysics);
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
					ApplyRemoteState(rc, rc.Target);
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

					if (!rc.HasPrev)
					{
						ApplyRemoteTransformDirect(rc, rc.Target);
					}
					else
					{
						float interval = Mathf.Max(SendIntervalMs / 1000f, 0.001f);
						float t = (Time.unscaledTime - rc.InterpStartTime) / interval;
						if (t >= 1f)
						{
							ApplyRemoteTransformDirect(rc, rc.Target);
						}
						else
						{
							// 内联插值（与原 InterpolatedTransform 一致：位置/速度线性、朝向球面插值），
							// 并在更新 GroundedSurface* 后统一应用，确保游戏表面锁定分支也跟随插值状态。
							float pct = Mathf.Clamp01(t);
							Vector3d interpPos = Vector3d.Lerp(rc.Prev.Position, rc.Target.Position, pct);
							Vector3d interpVel = Vector3d.Lerp(rc.Prev.Velocity, rc.Target.Velocity, pct);
							Quaterniond interpHeading = new Quaterniond(
								Quaternion.Slerp(
									new Quaternion((float)rc.Prev.Heading.x, (float)rc.Prev.Heading.y, (float)rc.Prev.Heading.z, (float)rc.Prev.Heading.w),
									new Quaternion((float)rc.Target.Heading.x, (float)rc.Target.Heading.y, (float)rc.Target.Heading.z, (float)rc.Target.Heading.w),
									pct));
							// 用插值后的位置/速度/朝向生成一个临时状态应用（body 姿态沿用最新 Target）
							Mod.recdata interp = rc.Target;
							interp.Position = interpPos;
							interp.Velocity = interpVel;
							interp.Heading = interpHeading;
							ApplyRemoteState(rc, interp);
						}
					}

					// 周期性状态日志（每 5 秒一次，便于核对位置/朝向同步）
					if (Time.unscaledTime - rc.LastStateLogTime > 5f)
					{
						rc.LastStateLogTime = Time.unscaledTime;
						Quaternion tr = rc.Node.CraftScript != null ? rc.Node.CraftScript.Transform.rotation : Quaternion.identity;
						Vector3d remoteSurface = rc.Node.Parent != null ? rc.Node.Parent.PlanetVectorToSurfaceVector(rc.Node.Position) : Vector3d.zero;
						Mod.Log("MP state player " + rc.PlayerId + ": target surfacePos=(" +
							rc.Target.Position.x.ToString("F1") + "," + rc.Target.Position.y.ToString("F1") + "," + rc.Target.Position.z.ToString("F1") + ")" +
							", heading=(" + rc.Target.Heading.x.ToString("F3") + "," + rc.Target.Heading.y.ToString("F3") + "," + rc.Target.Heading.z.ToString("F3") + "," + rc.Target.Heading.w.ToString("F3") + ")" +
							", remotePos=(" + rc.Node.Position.x.ToString("F1") + "," + rc.Node.Position.y.ToString("F1") + "," + rc.Node.Position.z.ToString("F1") + ")" +
							", remoteSurface=(" + remoteSurface.x.ToString("F1") + "," + remoteSurface.y.ToString("F1") + "," + remoteSurface.z.ToString("F1") + ")" +
							", alt=" + rc.Node.Altitude.ToString("F1") +
							", altAgl=" + rc.Node.AltitudeAgl.ToString("F1") +
							", transformRot=(" + tr.x.ToString("F3") + "," + tr.y.ToString("F3") + "," + tr.z.ToString("F3") + "," + tr.w.ToString("F3") + ")");
					}

					// 朝向诊断日志（低频，姿态应用已由 ApplyRemoteState 完成，此处仅核对）
					LogRemoteHeadingDiag(rc, rc.Target);
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
			IPlanetNode planet = rc.Node.Parent;
			if (planet == null) return;

			ApplyRemoteGroundedSurface(rc, data, planet);

			Vector3d planetPos = planet.SurfaceVectorToPlanetVector(data.Position);
			Vector3d planetVel = planet.SurfaceVectorToPlanetVector(data.Velocity);
			CraftUtils.SetStateVectorsAtDefaultTime(planetPos, planetVel, rc.Node);

			// ③ 视觉朝向：根=质心旋转；body=相对质心的局部旋转
			rc.Node.CraftScript.Transform.rotation = data.Heading.ToQuaternion();
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
			IReferenceFrame frame = rc.Node.GameView != null ? rc.Node.GameView.ReferenceFrame : null;
			if (frame == null && FlightSceneScript.Instance != null &&
				FlightSceneScript.Instance.ViewManager != null &&
				FlightSceneScript.Instance.ViewManager.GameView != null)
			{
				frame = FlightSceneScript.Instance.ViewManager.GameView.ReferenceFrame;
			}
			if (frame != null)
			{
				CraftUtils.RecalculateFrameState(frame, rc.Node);
			}
		}

		private static readonly PropertyInfo _groundedSurfacePositionProp =
			typeof(CraftNode).GetProperty("GroundedSurfacePosition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		private static readonly PropertyInfo _groundedSurfaceVelocityProp =
			typeof(CraftNode).GetProperty("GroundedSurfaceVelocity", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		private static readonly PropertyInfo _groundedSurfaceRotationProp =
			typeof(CraftNode).GetProperty("GroundedSurfaceRotation", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

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
				// data.Heading 是"帧空间"旋转，而游戏要求 Heading 为"行星空间"。
				// 先经参考系转成行星空间，否则 CraftNode.Heading(行星字段) 会存帧空间值，
				// 导致游戏导航/相机/地图等逻辑朝向错误。
				Quaterniond planetHeading = data.Heading;
				if (rc.Node.GameView != null && rc.Node.GameView.ReferenceFrame != null)
				{
					planetHeading = rc.Node.GameView.ReferenceFrame.FrameToPlanetRotation(data.Heading.ToQuaternion());
				}
				if (_groundedSurfacePositionProp != null) _groundedSurfacePositionProp.SetValue(rc.Node, (Vector3d?)data.Position);
				if (_groundedSurfaceVelocityProp != null) _groundedSurfaceVelocityProp.SetValue(rc.Node, (Vector3d?)data.Velocity);
				if (_groundedSurfaceRotationProp != null) _groundedSurfaceRotationProp.SetValue(rc.Node, (Quaterniond?)(planet.RotationInverse * planetHeading));
			}
			catch (Exception e)
			{
				Mod.LogError("ApplyRemoteGroundedSurface error (player " + rc.PlayerId + "): " + e.Message);
			}
		}

		/// <summary>
		/// 接收端朝向诊断日志（每10秒，由 UpdateRemoteCrafts 调用）。
		/// 姿态应用已统一在 ApplyRemoteState 中完成；此处仅输出核对数据：
		/// 收到heading=状态包帧空间朝向；根Transform=赋给根的旋转(应=收到heading)；
		/// rootPart=命令舱世界旋转；comRot=本机质心旋转；
		/// 游戏Heading=行星字段(应=FrameToPlanet(收到heading))；
		/// fwd/up=对方飞船在本机的视觉"前方/上方"方向(应与发送端本机一致)；
		/// body摘要=首/次/末body相对质心的欧拉角(判断分裂)。
		/// </summary>
		private static void LogRemoteHeadingDiag(RemoteCraft rc, Mod.recdata data)
		{
			if (rc.Node == null || rc.Node.CraftScript == null) return;
			_headingDiagTimer -= Time.unscaledDeltaTime;
			if (_headingDiagTimer > 0f) return;
			_headingDiagTimer = 10f;

			Quaterniond craftHeading = rc.Node.Heading;
			Quaternion outRot = rc.Node.CraftScript.Transform.rotation;
			Quaternion rootPartRot = (rc.Node.CraftScript.RootPart != null && rc.Node.CraftScript.RootPart.Transform != null)
				? rc.Node.CraftScript.RootPart.Transform.rotation : Quaternion.identity;
			Quaternion comRot = rc.Node.CraftScript.CenterOfMass != null ? rc.Node.CraftScript.CenterOfMass.rotation : Quaternion.identity;
			double planetAngle = rc.Node.Parent != null ? rc.Node.Parent.RotationAngle : double.NaN;
			Quaterniond headingToPlanet = Quaterniond.identity;
			if (rc.Node.GameView != null)
			{
				headingToPlanet = rc.Node.GameView.ReferenceFrame.FrameToPlanetRotation(data.Heading.ToQuaternion());
			}
			Vector3 outFwd = rc.Node.CraftScript.Transform.forward;
			Vector3 outUp = rc.Node.CraftScript.Transform.up;
			/*
			Mod.Log("[朝向诊断|远端P" + rc.PlayerId + "飞船] 收到heading(帧)=" + Q(data.Heading) +
				" | 根Transform=" + Q(outRot) +
				" | rootPartRot=" + Q(rootPartRot) +
				" | comRot=" + Q(comRot) +
				" | 游戏Heading(行星)=" + Q(craftHeading) +
				" | 应=FrameToPlanet(收到)=" + Q(headingToPlanet) +
				" | fwd=(" + outFwd.x.ToString("F2") + "," + outFwd.y.ToString("F2") + "," + outFwd.z.ToString("F2") + ")" +
				" up=(" + outUp.x.ToString("F2") + "," + outUp.y.ToString("F2") + "," + outUp.z.ToString("F2") + ")" +
				" | planetAngle=" + planetAngle.ToString("F2") +
				" | body:" + BuildBodyPoseSummary(rc.Node.CraftScript));*/
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
				data = new Mod.recdata(pos, vel, heading);

				// 同步每个 body 的局部姿态。关键：BodyRotations 必须存"相对质心(comRot)"的旋转，
				// 因为接收端根=comRot（发送的 heading），且 XML 的 body/part 是质心坐标系。
				// 若采样"相对根"的 localRotation，而发送端根≠comRot（座椅朝向，实测差~17°），
				// 接收端按 comRot 摆放 body 时会整体转错 → "分裂/散架 + 朝向不一致"。
				Quaternion comRotUnity = craft.CraftScript.CenterOfMass != null
					? craft.CraftScript.CenterOfMass.rotation : craft.CraftScript.Transform.rotation;
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

		/// <summary>
		/// body 姿态摘要：只列数量与首/次/末 body 的"相对质心"欧拉角（避免刷屏，用于判断分裂）。
		/// 用相对质心的基准：接收端根=comRot，body 的 localRotation 即相对质心；
		/// 发送端 root≠comRot（差~17°），若用相对根的 localRotation 会与接收端不可比。
		/// </summary>
		private static string BuildBodyPoseSummary(ICraftScript craft)
		{
			if (craft == null || craft.Data == null || craft.Data.Assembly == null) return "count=0";
			Quaternion comRot = craft.CenterOfMass != null ? craft.CenterOfMass.rotation : craft.Transform.rotation;
			var bodies = craft.Data.Assembly.Bodies;
			int n = bodies.Count;
			System.Text.StringBuilder sb = new System.Text.StringBuilder();
			sb.Append("count=").Append(n);
			for (int i = 0; i < n; i++)
			{
				if (i != 0 && i != 1 && i != n - 1) continue; // 只列首、次、末
				if (bodies[i].BodyScript == null || bodies[i].BodyScript.Transform == null) continue;
				Vector3 e = (Quaternion.Inverse(comRot) * bodies[i].BodyScript.Transform.rotation).eulerAngles;
				sb.Append(" b").Append(bodies[i].Id)
					.Append("=(").Append(e.x.ToString("F0")).Append(",").Append(e.y.ToString("F0")).Append(",").Append(e.z.ToString("F0")).Append(")");
			}
			return sb.ToString();
		}
	}
}
