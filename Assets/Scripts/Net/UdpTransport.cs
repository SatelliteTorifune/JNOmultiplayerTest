using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Assets.Scripts.Net
{
	/// <summary>
	/// 一个远端对等端（玩家）的标识与元信息。
	/// </summary>
	public class MpPeer : IEquatable<MpPeer>
	{
		public IPEndPoint EndPoint;
		public int PlayerId = -1;      // 由房主分配
		public int NodeId = -1;        // 该玩家飞船的 NodeId
		public string PlayerName = "Player";
		public string CraftXml = string.Empty; // 该玩家飞船的 craft XML（联机交换用）
		public long LastReceiveTick;   // 最近收到数据的时间（环境时间戳 ms）
		public bool IsServer;

		public bool Equals(MpPeer other)
		{
			return other != null && EndPoint != null && other.EndPoint != null && EndPoint.Equals(other.EndPoint);
		}

		public override bool Equals(object obj) => Equals(obj as MpPeer);
		public override int GetHashCode() => EndPoint == null ? 0 : EndPoint.GetHashCode();
	}

	/// <summary>
	/// 底层 UDP 传输封装。
	/// 后台线程负责阻塞接收，数据放入并发队列；主线程轮询 DrainIncoming() 取包。
	/// 支持大报文分片/重组：craft XML 可能远超单个 UDP 数据报上限（~64KB），
	/// 发送时自动切成多个数据报，接收端重组后再触发 OnDataReceived。
	/// 本类不触碰任何 Unity API，可安全地在网络线程运行。
	/// </summary>
	public class UdpTransport : IDisposable
	{
		public event Action<MpPeer, byte[]> OnDataReceived;
		public event Action<MpPeer> OnPeerTimeout;

		private const byte FragmentMarker = 0x7F;                       // 分片包头标记（与原消息类型字节区分）
		private const int FragmentHeaderSize = 1 + 4 + 4 + 4;           // marker + fragId(int) + total(int) + index(int)
		// 单个 UDP 数据报负载上限。必须远小于 MTU(1500) 以避免 IP 层分片：
		// 实测经 frp 内网穿透时，>MTU 的大数据报会触发 IP 分片，而公网 NAT/frp 会丢弃分片，
		// 导致 58KB 分片全部丢失（小包却能通）。
		// 进一步实测：1400 字节分片在"公网客户端 -> frps"方向仍被丢弃（frpc->frps 方向却通），
		// 说明 frps 对公网入站 UDP 包大小限制更低。降到 1000 字节更安全（覆盖常见 1024 限制）。
		private const int MaxDatagramSize = 1000;
		private const int MaxFragmentPayload = MaxDatagramSize - FragmentHeaderSize;
		private const long FragmentTimeoutMs = 15000;                   // 分片重组超时（丢弃不完整分片）

		// 分片接收诊断（节流打印，用于确认大分片是否穿越公网到达本端）
		private long _fragDiagLastLogTick;
		private int _fragDiagCount;

		private UdpClient _client;
		private Thread _recvThread;
		private volatile bool _running;
		private int _fragCounter;
		private readonly ConcurrentQueue<KeyValuePair<MpPeer, byte[]>> _incoming = new ConcurrentQueue<KeyValuePair<MpPeer, byte[]>>();
		private readonly Dictionary<string, MpPeer> _peers = new Dictionary<string, MpPeer>();
		private readonly object _peersLock = new object();
		private readonly Dictionary<string, Dictionary<int, FragmentBuffer>> _fragmentBuffers = new Dictionary<string, Dictionary<int, FragmentBuffer>>();

		public int LocalPort { get; private set; }
		public bool IsRunning => _running;

		/// <summary>毫秒级时间戳（纯 .NET，可在网络线程安全使用）。</summary>
		private static long NowMs => DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

		private class FragmentBuffer
		{
			public byte[] Data;
			public int Total;
			public bool[] Received;
			public int ReceivedCount;
			public int LastChunkLength;
			public long LastReceiveTick;
		}

		/// <summary>开启主机监听（也可用于客户端接收）。</summary>
		public bool Start(int port)
		{
			Stop();
			try
			{
				_client = new UdpClient(port);
				_client.Client.ReceiveTimeout = 0;
				_client.Client.ReceiveBufferSize = 1024 * 1024; // 1MB，容纳大分片
				_client.Client.SendBufferSize = 1024 * 1024;
				LocalPort = port;
				_running = true;
				_recvThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "MpUdpRecv" };
				_recvThread.Start();
				Mod.LogLobby("UdpTransport.Start SUCCESS: bound UDP on local port " + LocalPort +
					", recvThread started (IsAlive=" + _recvThread.IsAlive + "), IsRunning=" + _running);
				return true;
			}
			catch (Exception e)
			{
				Mod.LogLobby("UdpTransport.Start FAILED: port=" + port + ", error=" + e.Message +
					" (this usually means the port is already in use by another process)");
				Mod.LogError("UdpTransport.Start failed: " + e.Message);
				return false;
			}
		}

		/// <summary>客户端模式：绑定随机端口并向主机发送首包，建立对端记录。</summary>
		public bool StartClient(string host, int port, byte[] helloPacket)
		{
			if (!Start(0)) return false;
			try
			{
				IPAddress[] addresses = Dns.GetHostAddresses(host);
				if (addresses == null || addresses.Length == 0)
				{
					Mod.LogLobby("UdpTransport.StartClient FAILED: could not resolve host '" + host + "'");
					Stop();
					return false;
				}
				IPAddress ip = addresses[0];
				IPEndPoint serverEp = new IPEndPoint(ip, port);
				int sent = _client.Send(helloPacket, helloPacket.Length, serverEp);
				GetOrAddPeer(serverEp).IsServer = true;
				Mod.LogLobby("UdpTransport.StartClient SUCCESS: resolved '" + host + "' -> " + ip + ":" + port +
					", hello sent (" + sent + " bytes), localPort=" + LocalPort +
					", peers=" + GetPeersCount());
				return true;
			}
			catch (Exception e)
			{
				Mod.LogLobby("UdpTransport.StartClient FAILED: host=" + host + ":" + port + ", error=" + e.Message);
				Mod.LogError("UdpTransport.StartClient failed: " + e.Message);
				Stop();
				return false;
			}
		}

		private void ReceiveLoop()
		{
			IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
			while (_running && _client != null)
			{
				try
				{
					byte[] data = _client.Receive(ref remote);
					if (data == null || data.Length == 0) continue;
					IPEndPoint source = new IPEndPoint(remote.Address, remote.Port);
					if (data[0] == FragmentMarker)
					{
						// 分片包：重组后再入队
						HandleFragment(source, data);
						continue;
					}
					MpPeer peer = GetOrAddPeer(source);
					peer.LastReceiveTick = NowMs;
					_incoming.Enqueue(new KeyValuePair<MpPeer, byte[]>(peer, data));
				}
				catch (SocketException)
				{
					// 正常关闭
				}
				catch (Exception e)
				{
					Mod.LogError("UdpTransport.ReceiveLoop error: " + e.Message);
				}
			}
		}

		/// <summary>主线程调用：取出所有收到的数据包并触发事件。</summary>
		public void DrainIncoming()
		{
			KeyValuePair<MpPeer, byte[]> item;
			while (_incoming.TryDequeue(out item))
			{
				try { OnDataReceived?.Invoke(item.Key, item.Value); }
				catch (Exception e) { Mod.LogError("UdpTransport.OnDataReceived error: " + e.Message); }
			}
		}

		public void SendTo(MpPeer peer, byte[] data)
		{
			if (_client == null || data == null || data.Length == 0) return;
			try { SendData(peer.EndPoint, data); }
			catch (Exception e) { Mod.LogError("UdpTransport.SendTo error: " + e.Message); }
		}

		public void Broadcast(byte[] data)
		{
			if (_client == null || data == null) return;
			lock (_peersLock)
			{
				foreach (MpPeer peer in _peers.Values)
				{
					try { SendData(peer.EndPoint, data); }
					catch (Exception e) { Mod.LogError("UdpTransport.Broadcast error: " + e.Message); }
				}
			}
		}

		/// <summary>发送数据：超过单个数据报上限时自动分片。</summary>
		private void SendData(IPEndPoint endpoint, byte[] data)
		{
			if (data.Length <= MaxDatagramSize)
			{
				_client.Send(data, data.Length, endpoint);
				return;
			}

			int total = (data.Length + MaxFragmentPayload - 1) / MaxFragmentPayload;
			int fragId = _fragCounter++;
			for (int i = 0; i < total; i++)
			{
				int offset = i * MaxFragmentPayload;
				int len = Math.Min(MaxFragmentPayload, data.Length - offset);
				byte[] frag = new byte[FragmentHeaderSize + len];
				frag[0] = FragmentMarker;
				WriteInt32(frag, 1, fragId);
				WriteInt32(frag, 5, total);
				WriteInt32(frag, 9, i);
				Array.Copy(data, offset, frag, FragmentHeaderSize, len);
				_client.Send(frag, frag.Length, endpoint);
			}
			Mod.Log("UdpTransport: fragmented " + data.Length + " bytes into " + total + " datagrams (id=" + fragId + ")");
		}

		/// <summary>接收分片并重组，完整后入队。</summary>
		private void HandleFragment(IPEndPoint source, byte[] fragData)
		{
			if (fragData.Length < FragmentHeaderSize) return;
			int fragId = ReadInt32(fragData, 1);
			int total = ReadInt32(fragData, 5);
			int index = ReadInt32(fragData, 9);
			int payloadLen = fragData.Length - FragmentHeaderSize;
			if (total <= 0 || index < 0 || index >= total || payloadLen <= 0) return;

			// 诊断：统计分片接收（节流打印），确认大分片是否穿越公网到达本端。
			_fragDiagCount++;
			long nowDiag = NowMs;
			if (nowDiag - _fragDiagLastLogTick > 2000)
			{
				Mod.Log("UdpTransport: received " + _fragDiagCount + " fragments in last 2s (sample id=" + fragId +
					" idx=" + index + "/" + total + " from " + source + ")");
				_fragDiagLastLogTick = nowDiag;
				_fragDiagCount = 0;
			}

			string key = source.ToString();
			Dictionary<int, FragmentBuffer> byId;
			if (!_fragmentBuffers.TryGetValue(key, out byId))
			{
				byId = new Dictionary<int, FragmentBuffer>();
				_fragmentBuffers[key] = byId;
			}

			FragmentBuffer fb;
			if (!byId.TryGetValue(fragId, out fb))
			{
				fb = new FragmentBuffer
				{
					Data = new byte[total * MaxFragmentPayload],
					Total = total,
					Received = new bool[total],
					ReceivedCount = 0
				};
				byId[fragId] = fb;
			}

			if (fb.Received[index]) return; // 重复分片
			Array.Copy(fragData, FragmentHeaderSize, fb.Data, index * MaxFragmentPayload, payloadLen);
			fb.Received[index] = true;
			fb.ReceivedCount++;
			fb.LastReceiveTick = NowMs;
			if (index == total - 1) fb.LastChunkLength = payloadLen;

			if (fb.ReceivedCount == total)
			{
				byId.Remove(fragId);
				int fullLen = (total - 1) * MaxFragmentPayload + fb.LastChunkLength;
				byte[] full = new byte[fullLen];
				Array.Copy(fb.Data, 0, full, 0, fullLen);

				MpPeer peer = GetOrAddPeer(source);
				peer.LastReceiveTick = NowMs;
				_incoming.Enqueue(new KeyValuePair<MpPeer, byte[]>(peer, full));
				Mod.Log("UdpTransport: reassembled " + fullLen + " bytes from " + total + " fragments (id=" + fragId + ")");
			}
			else if (NowMs - fb.LastReceiveTick > FragmentTimeoutMs)
			{
				// 重组超时：丢弃不完整分片，防止内存泄漏
				byId.Remove(fragId);
			}
		}

		public IEnumerable<MpPeer> GetPeers()
		{
			lock (_peersLock)
			{
				return new List<MpPeer>(_peers.Values);
			}
		}

		/// <summary>当前已知的对端数量（含已建立连接的对象）。</summary>
		public int GetPeersCount()
		{
			lock (_peersLock)
			{
				return _peers.Count;
			}
		}

		public void RemovePeer(MpPeer peer)
		{
			lock (_peersLock) { _peers.Remove(peer.EndPoint.ToString()); }
		}

		public void CheckTimeouts(long timeoutMs)
		{
			long now = NowMs;
			List<MpPeer> expired = null;
			lock (_peersLock)
			{
				foreach (MpPeer peer in _peers.Values)
				{
					if (now - peer.LastReceiveTick > timeoutMs)
					{
						if (expired == null) expired = new List<MpPeer>();
						expired.Add(peer);
					}
				}
				if (expired != null)
				{
					foreach (MpPeer peer in expired) _peers.Remove(peer.EndPoint.ToString());
				}
			}
			if (expired != null)
			{
				foreach (MpPeer peer in expired) OnPeerTimeout?.Invoke(peer);
			}
		}

		private MpPeer GetOrAddPeer(IPEndPoint ep)
		{
			lock (_peersLock)
			{
				string key = ep.ToString();
				MpPeer peer;
				if (!_peers.TryGetValue(key, out peer))
				{
					peer = new MpPeer { EndPoint = ep, LastReceiveTick = NowMs };
					_peers[key] = peer;
				}
				return peer;
			}
		}

		public void Stop()
		{
			_running = false;
			try { if (_client != null) { _client.Close(); } } catch { }
			_client = null;
			try { if (_recvThread != null && _recvThread.IsAlive) _recvThread.Join(500); } catch { }
			_recvThread = null;
			lock (_peersLock) { _peers.Clear(); }
			lock (_fragmentBuffers) { _fragmentBuffers.Clear(); }
			KeyValuePair<MpPeer, byte[]> tmp;
			while (_incoming.TryDequeue(out tmp)) { }
		}

		public void Dispose() => Stop();

		// ---------------- 二进制工具 ----------------

		private static void WriteInt32(byte[] buffer, int offset, int value)
		{
			buffer[offset] = (byte)(value & 0xFF);
			buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
			buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
			buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
		}

		private static int ReadInt32(byte[] buffer, int offset)
		{
			return buffer[offset]
				| (buffer[offset + 1] << 8)
				| (buffer[offset + 2] << 16)
				| (buffer[offset + 3] << 24);
		}
	}
}
