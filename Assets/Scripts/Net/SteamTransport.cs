using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Steamworks;
using UnityEngine;

namespace Assets.Scripts.Net
{
	/// <summary>
	/// Steam 传输封装（Steam Networking Sockets P2P），接口与 TcpTransport / LiteNetLibTransport 完全兼容：
	/// - 房主：CreateListenSocketP2P 监听；客户端：ConnectP2P 按 SteamId 连接；
	/// - 穿透由 Steam 自动处理（NAT 打洞 + Relay），玩家零端口转发、零 frp；
	/// - 消息走可靠通道（大 XML / 握手均可靠）；如需状态包不可靠可加 flag 区分（见 SendFlags）。
	///
	/// 寻址：Steam 下没有 IP:port，使用 SteamId（64 位）寻址。
	/// StartClient 的 host 参数传房主 SteamId 字符串（如 "76561199127915239"），port 忽略（占位）。
	///
	/// 依赖：com.rlabrecque.steamworks.net.dll（游戏 Managed 自带，已复制到 ModTools/Assemblies）。
	/// 注意：游戏启动时已 SteamAPI.Init()，本类不重复初始化，直接用 SteamNetworkingSockets。
	/// </summary>
	public class SteamTransport : IMpTransport
	{
		public event Action<MpPeer, byte[]> OnDataReceived;
		public event Action<MpPeer> OnPeerTimeout;

		// Steam P2P 虚拟端口（两台机器协商一致即可，无需真实端口/端口转发）
		private const int VirtualPort = 0;

		private HSteamListenSocket _listenSocket;
		private bool _isServer;
		private volatile bool _running;

		// 房主：对端 SteamId(ulong) -> MpPeer / HSteamNetConnection 映射
		private readonly Dictionary<ulong, MpPeer> _serverPeers = new Dictionary<ulong, MpPeer>();
		private readonly Dictionary<ulong, HSteamNetConnection> _serverConnections = new Dictionary<ulong, HSteamNetConnection>();
		// 客户端：到房主的连接
		private MpPeer _serverPeer;
		private HSteamNetConnection _clientConnection;
		private ulong _pendingConnectSteamId; // 客户端：要连接的房主 SteamId

		// Steam Networking Sockets 连接状态回调
		private Callback<SteamNetConnectionStatusChangedCallback_t> _connStatusCallback;

		public int LocalPort { get; private set; } // Steam 下无真实端口，恒为 0（兼容占位）
		public bool IsRunning => _running;
		public ulong LocalSteamId { get; private set; } // 本机 SteamId

		/// <summary>毫秒级时间戳（纯 .NET）。</summary>
		private static long NowMs => DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

		// ---------------- 生命周期 ----------------

		/// <summary>房主：创建 Steam P2P 监听（Steam 无真实端口，port 忽略）。</summary>
		public bool Start(int port)
		{
			Stop();
			try
			{
				LocalSteamId = SteamUser.GetSteamID().m_SteamID;
				if (LocalSteamId == 0)
				{
					Mod.LogLobby("SteamTransport.Start FAILED: SteamId=0 (Steam not initialized?)");
					return false;
				}
				_isServer = true;
				_running = true;
				ListenCallback();

				_listenSocket = SteamNetworkingSockets.CreateListenSocketP2P(VirtualPort, 0, null);
				if (_listenSocket.m_HSteamListenSocket == 0)
				{
					Mod.LogLobby("SteamTransport.Start FAILED: CreateListenSocketP2P returned 0");
					Stop();
					return false;
				}
				Mod.LogLobby("SteamTransport.Start SUCCESS: SteamId=" + LocalSteamId +
					", listenSocket=" + _listenSocket.m_HSteamListenSocket + ", virtualPort=" + VirtualPort);
				return true;
			}
			catch (Exception e)
			{
				Mod.LogLobby("SteamTransport.Start FAILED: " + e.Message);
				Mod.LogError("SteamTransport.Start failed: " + e.Message);
				Stop();
				return false;
			}
		}

		/// <summary>客户端：连接房主。host 传房主 SteamId 字符串，port 忽略。连接后发 Hello。</summary>
		public bool StartClient(string hostSteamId, int port, byte[] helloPacket)
		{
			Stop();
			try
			{
				LocalSteamId = SteamUser.GetSteamID().m_SteamID;
				if (LocalSteamId == 0)
				{
					Mod.LogLobby("SteamTransport.StartClient FAILED: SteamId=0 (Steam not initialized?)");
					return false;
				}
				if (!ulong.TryParse(hostSteamId, out ulong hostId) || hostId == 0)
				{
					Mod.LogLobby("SteamTransport.StartClient FAILED: invalid host SteamId '" + hostSteamId + "'");
					return false;
				}
				_isServer = false;
				_running = true;
				ListenCallback();
				_pendingConnectSteamId = hostId;

				SteamNetworkingIdentity remote = new SteamNetworkingIdentity();
				remote.SetSteamID64(hostId);
				_clientConnection = SteamNetworkingSockets.ConnectP2P(ref remote, VirtualPort, 0, null);
				if (_clientConnection.m_HSteamNetConnection == 0)
				{
					Mod.LogLobby("SteamTransport.StartClient FAILED: ConnectP2P returned 0");
					Stop();
					return false;
				}
				Mod.LogLobby("SteamTransport.StartClient SUCCESS: localSteamId=" + LocalSteamId +
					", connecting to host SteamId=" + hostId + ", conn=" + _clientConnection.m_HSteamNetConnection +
					", hello=" + (helloPacket == null ? 0 : helloPacket.Length) + " bytes");
				_pendingHello = helloPacket;
				return true;
			}
			catch (Exception e)
			{
				Mod.LogLobby("SteamTransport.StartClient FAILED: " + e.Message);
				Mod.LogError("SteamTransport.StartClient failed: " + e.Message);
				Stop();
				return false;
			}
		}

		private byte[] _pendingHello;

		public void Stop()
		{
			_running = false;
			try
			{
				if (_connStatusCallback != null)
				{
					_connStatusCallback.Dispose();
					_connStatusCallback = null;
				}
				if (_listenSocket.m_HSteamListenSocket != 0)
				{
					SteamNetworkingSockets.CloseListenSocket(_listenSocket);
					_listenSocket = default;
				}
				if (_clientConnection.m_HSteamNetConnection != 0)
				{
					SteamNetworkingSockets.CloseConnection(_clientConnection, 0, "stop", false);
					_clientConnection = default;
				}
				lock (_serverConnections)
				{
					foreach (HSteamNetConnection c in _serverConnections.Values)
					{
						if (c.m_HSteamNetConnection != 0)
							SteamNetworkingSockets.CloseConnection(c, 0, "stop", false);
					}
					_serverConnections.Clear();
					_serverPeers.Clear();
				}
			}
			catch (Exception e)
			{
				Mod.LogError("SteamTransport.Stop error: " + e.Message);
			}
			_serverPeer = null;
			_pendingHello = null;
		}

		public void Dispose() => Stop();

		// ---------------- 主线程轮询 ----------------

		/// <summary>每帧轮询 Steam Networking Sockets（触发回调/收包）。</summary>
		public void DrainIncoming()
		{
			if (_running)
			{
				try
				{
					// 必须先 RunCallbacks 处理连接状态回调，再收消息
					SteamNetworkingSockets.RunCallbacks();
					PollIncoming();
				}
				catch (Exception e)
				{
					Mod.LogError("SteamTransport.DrainIncoming error: " + e.Message);
				}
			}
		}

		/// <summary>收取所有连接上的消息并分发。</summary>
		private void PollIncoming()
		{
			// 客户端：房主连接
			if (!_isServer && _clientConnection.m_HSteamNetConnection != 0)
			{
				PollConnection(_clientConnection, _serverPeer);
			}
			// 房主：所有已连接对端
			if (_isServer)
			{
				KeyValuePair<ulong, HSteamNetConnection>[] snapshot;
				lock (_serverConnections)
				{
					snapshot = new List<KeyValuePair<ulong, HSteamNetConnection>>(_serverConnections).ToArray();
				}
				foreach (var kv in snapshot)
				{
					MpPeer peer;
					lock (_serverPeers) { _serverPeers.TryGetValue(kv.Key, out peer); }
					PollConnection(kv.Value, peer);
				}
			}
		}

		/// <summary>收取单个连接的消息。</summary>
		private void PollConnection(HSteamNetConnection conn, MpPeer peer)
		{
			IntPtr[] msgs = new IntPtr[16];
			while (true)
			{
				int n = SteamNetworkingSockets.ReceiveMessagesOnConnection(conn, msgs, msgs.Length);
				if (n <= 0) break;
				for (int i = 0; i < n; i++)
				{
					IntPtr p = msgs[i];
					try
					{
						SteamNetworkingMessage_t msg = Marshal.PtrToStructure<SteamNetworkingMessage_t>(p);
						byte[] data = new byte[msg.m_cbSize];
						if (msg.m_cbSize > 0 && msg.m_pData != IntPtr.Zero)
							Marshal.Copy(msg.m_pData, data, 0, msg.m_cbSize);
						if (peer != null) peer.LastReceiveTick = NowMs;
						OnDataReceived?.Invoke(peer, data);
					}
					finally
					{
						// 释放消息
						IntPtr releasePtr = p;
						try
						{
							SteamNetworkingMessage_t mm = Marshal.PtrToStructure<SteamNetworkingMessage_t>(releasePtr);
							mm.Release();
						}
						catch { }
					}
				}
			}
		}

		/// <summary>兼容 TcpTransport 接口；Steam 自带连接状态检测（超时由回调处理）。</summary>
		public void CheckTimeouts(long timeoutMs) { }

		// ---------------- 发送 ----------------

		public void SendTo(MpPeer peer, byte[] data)
		{
			if (data == null || data.Length == 0 || !_running) return;
			try
			{
				if (_isServer)
				{
					ulong steamId = peer != null ? peer.SteamId : 0;
					HSteamNetConnection conn;
					lock (_serverConnections) { _serverConnections.TryGetValue(steamId, out conn); }
					if (conn.m_HSteamNetConnection != 0)
						SendReliable(conn, data);
				}
				else
				{
					if (_clientConnection.m_HSteamNetConnection != 0)
						SendReliable(_clientConnection, data);
				}
			}
			catch (Exception e)
			{
				Mod.LogError("SteamTransport.SendTo error: " + e.Message);
			}
		}

		public void Broadcast(byte[] data)
		{
			if (data == null || !_running) return;
			try
			{
				if (_isServer)
				{
					KeyValuePair<ulong, HSteamNetConnection>[] snapshot;
					lock (_serverConnections) { snapshot = new List<KeyValuePair<ulong, HSteamNetConnection>>(_serverConnections).ToArray(); }
					foreach (var kv in snapshot)
						SendReliable(kv.Value, data);
				}
				else
				{
					if (_clientConnection.m_HSteamNetConnection != 0)
						SendReliable(_clientConnection, data);
				}
			}
			catch (Exception e)
			{
				Mod.LogError("SteamTransport.Broadcast error: " + e.Message);
			}
		}

		/// <summary>房主：踢人用——关闭与指定对端的 Steam 连接。先移除映射再 CloseConnection，
		/// 避免 OnConnectionStatusChanged 回调再次移除/触发 OnPeerTimeout（重复清理）。</summary>
		public void DisconnectPeer(MpPeer peer)
		{
			if (peer == null || peer.SteamId == 0) return;
			HSteamNetConnection conn = default;
			lock (_serverConnections)
			{
				if (_serverConnections.TryGetValue(peer.SteamId, out conn))
				{
					_serverConnections.Remove(peer.SteamId);
				}
			}
			lock (_serverPeers) { _serverPeers.Remove(peer.SteamId); }
			if (conn.m_HSteamNetConnection != 0)
			{
				try { SteamNetworkingSockets.CloseConnection(conn, 1, "kicked", false); }
				catch (Exception e) { Mod.LogError("SteamTransport.DisconnectPeer error: " + e.Message); }
			}
		}

		/// <summary>可靠通道发送（Steam 自动重传，无需应用层分片）。</summary>
		private static void SendReliable(HSteamNetConnection conn, byte[] data)
		{
			IntPtr buf = Marshal.AllocHGlobal(data.Length);
			try
			{
				Marshal.Copy(data, 0, buf, data.Length);
				long outNum;
				// k_nSteamNetworkingSend_Reliable = 8（见 Steamworks.Constants）
				SteamNetworkingSockets.SendMessageToConnection(conn, buf, (uint)data.Length,
					Constants.k_nSteamNetworkingSend_Reliable, out outNum);
			}
			finally
			{
				Marshal.FreeHGlobal(buf);
			}
		}

		// ---------------- 连接管理 ----------------

		public IReadOnlyCollection<MpPeer> GetPeers()
		{
			lock (_serverPeers)
			{
				if (_isServer) return new List<MpPeer>(_serverPeers.Values);
				return _serverPeer == null ? new List<MpPeer>() : new List<MpPeer> { _serverPeer };
			}
		}

		public int GetPeersCount()
		{
			lock (_serverPeers)
			{
				if (_isServer) return _serverPeers.Count;
				return _serverPeer == null ? 0 : 1;
			}
		}

		// ---------------- Steam 连接状态回调 ----------------

		private void ListenCallback()
		{
			// 游戏已 SteamAPI.Init()；这里注册 NetworkingSockets 连接状态回调
			if (_connStatusCallback != null)
			{
				_connStatusCallback.Dispose();
				_connStatusCallback = null;
			}
			_connStatusCallback = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionStatusChanged);
		}

		private void OnConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t info)
		{
			try
			{
				HSteamNetConnection conn = info.m_hConn;
				ulong remoteSteamId = info.m_info.m_identityRemote.GetSteamID64();
				ESteamNetworkingConnectionState newState = info.m_info.m_eState;

				Mod.LogLobby("SteamTransport: conn=" + conn.m_HSteamNetConnection +
					", remote=" + remoteSteamId + ", state=" + newState + ", old=" + info.m_eOldState);

				if (_isServer)
				{
					switch (newState)
					{
						case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
							// 接受入站连接
							SteamNetworkingSockets.AcceptConnection(conn);
							Mod.LogLobby("SteamTransport (host): accepted connection from SteamId=" + remoteSteamId);
							break;
						case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
							// 登记对端
							MpPeer peer = new MpPeer
							{
								SteamId = remoteSteamId,
								IsServer = false,
								LastReceiveTick = NowMs
							};
							lock (_serverPeers) { _serverPeers[remoteSteamId] = peer; }
							lock (_serverConnections) { _serverConnections[remoteSteamId] = conn; }
							Mod.LogLobby("SteamTransport (host): peer connected SteamId=" + remoteSteamId +
								", peers=" + GetPeersCount());
							break;
						case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
						case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
						case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Dead:
							MpPeer removed = null;
							lock (_serverPeers)
							{
								if (_serverPeers.TryGetValue(remoteSteamId, out removed))
								{
									_serverPeers.Remove(remoteSteamId);
									_serverConnections.Remove(remoteSteamId);
								}
							}
							Mod.LogLobby("SteamTransport (host): peer disconnected SteamId=" + remoteSteamId +
								", state=" + newState + ", wasRegistered=" + (removed != null));
							if (removed != null) OnPeerTimeout?.Invoke(removed);
							break;
					}
				}
				else
				{
					switch (newState)
					{
						case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
							_serverPeer = new MpPeer
							{
								SteamId = remoteSteamId,
								IsServer = true,
								LastReceiveTick = NowMs
							};
							Mod.LogLobby("SteamTransport (client): connected to host SteamId=" + remoteSteamId +
								", peers=" + GetPeersCount());
							// 连接建立后发 Hello
							if (_pendingHello != null)
							{
								SendReliable(conn, _pendingHello);
								_pendingHello = null;
							}
							break;
						case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
						case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
						case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Dead:
							MpPeer p = _serverPeer;
							_serverPeer = null;
							_running = false;
							Mod.LogLobby("SteamTransport (client): disconnected from host (state=" + newState +
								", wasConnected=" + (p != null) + ")");
							if (p != null) OnPeerTimeout?.Invoke(p);
							break;
					}
				}
			}
			catch (Exception e)
			{
				Mod.LogError("SteamTransport.OnConnectionStatusChanged error: " + e.Message);
			}
		}
	}
}
