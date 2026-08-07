using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Assets.Scripts.Net
{
    /// <summary>
    /// TCP 传输封装（主机中继模式），公共接口与 UdpTransport 完全兼容：
    /// - 房主：TcpListener 监听，为每个客户端建立独立连接 + 独立接收线程；
    /// - 客户端：TcpClient 连接房主；
    /// - 消息帧协议：[4字节长度][payload]，TCP 字节流自带可靠传输与 MTU 分块，
    ///   无需应用层分片/重组（大飞船 XML 压缩后可直接整包发送）。
    /// 本类不触碰任何 Unity API，可在网络线程安全运行。
    /// </summary>
    public class TcpTransport : IDisposable
    {
        public event Action<MpPeer, byte[]> OnDataReceived;
        public event Action<MpPeer> OnPeerTimeout;

        /// <summary>单条消息最大长度（128MB，含压缩后的大飞船 XML 与诊断数据）。</summary>
        private const int MaxMessageLength = 128 * 1024 * 1024;

        private TcpListener _listener; // 房主：监听
        private TcpClient _client; // 客户端：到房主的连接
        private Thread _acceptThread; // 房主：接受连接循环
        private Thread _recvThread; // 客户端：接收主连接数据
        private volatile bool _running;

        private readonly ConcurrentQueue<KeyValuePair<MpPeer, byte[]>> _incoming =
            new ConcurrentQueue<KeyValuePair<MpPeer, byte[]>>();

        private readonly Dictionary<string, MpPeer> _peers = new Dictionary<string, MpPeer>();
        private readonly Dictionary<string, TcpClient> _peerClients = new Dictionary<string, TcpClient>();
        private readonly object _peersLock = new object();

        public int LocalPort { get; private set; }
        public bool IsRunning => _running;

        /// <summary>毫秒级时间戳（纯 .NET，可在网络线程安全使用）。</summary>
        private static long NowMs => DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

        // ---------------- 生命周期 ----------------

        /// <summary>房主：开启 TCP 监听。</summary>
        public bool Start(int port)
        {
            Stop();
            try
            {
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start(16);
                LocalPort = port;
                _running = true;
                _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "MpTcpAccept" };
                _acceptThread.Start();
                Mod.LogLobby("TcpTransport.Start SUCCESS: TCP listening on port " + LocalPort + ", IsRunning=" +
                             _running);
                return true;
            }
            catch (Exception e)
            {
                Mod.LogLobby("TcpTransport.Start FAILED: port=" + port + ", error=" + e.Message +
                             " (port may be in use)");
                Mod.LogError("TcpTransport.Start failed: " + e.Message);
                return false;
            }
        }

        /// <summary>客户端：连接房主并发送首包（Hello），建立对端记录。</summary>
        public bool StartClient(string host, int port, byte[] helloPacket)
        {
            Stop();
            try
            {
                IPAddress[] addresses = Dns.GetHostAddresses(host);
                if (addresses == null || addresses.Length == 0)
                {
                    Mod.LogLobby("TcpTransport.StartClient FAILED: could not resolve host '" + host + "'");
                    return false;
                }

                IPAddress ip = addresses[0];
                _client = new TcpClient();
                _client.Connect(new IPEndPoint(ip, port));
                _client.NoDelay = true; // 禁用 Nagle，降低小包延迟
                _running = true;
                LocalPort = ((IPEndPoint)_client.Client.LocalEndPoint).Port;
                IPEndPoint remoteEp = (IPEndPoint)_client.Client.RemoteEndPoint;
                MpPeer server = GetOrAddPeer(remoteEp);
                server.IsServer = true;
                SendTo(server, helloPacket);
                _recvThread = new Thread(() => PeerReceiveLoop(server))
                    { IsBackground = true, Name = "MpTcpClientRecv" };
                _recvThread.Start();
                Mod.LogLobby("TcpTransport.StartClient SUCCESS: connected " + ip + ":" + port +
                             ", hello sent (" + (helloPacket == null ? 0 : helloPacket.Length) + " bytes), localPort=" +
                             LocalPort +
                             ", peers=" + GetPeersCount());
                return true;
            }
            catch (Exception e)
            {
                Mod.LogLobby("TcpTransport.StartClient FAILED: host=" + host + ":" + port + ", error=" + e.Message);
                Mod.LogError("TcpTransport.StartClient failed: " + e.Message);
                Stop();
                return false;
            }
        }

        public void Stop()
        {
            _running = false;
            try
            {
                if (_listener != null) _listener.Stop();
            }
            catch
            {
            }

            _listener = null;
            lock (_peersLock)
            {
                foreach (TcpClient c in _peerClients.Values)
                {
                    try
                    {
                        c.Close();
                    }
                    catch
                    {
                    }
                }

                _peerClients.Clear();
                _peers.Clear();
            }

            try
            {
                if (_client != null) _client.Close();
            }
            catch
            {
            }

            _client = null;
            _acceptThread = null;
            _recvThread = null;
        }

        public void Dispose() => Stop();

        // ---------------- 主线程轮询 ----------------

        public void DrainIncoming()
        {
            KeyValuePair<MpPeer, byte[]> item;
            while (_incoming.TryDequeue(out item))
            {
                try
                {
                    OnDataReceived?.Invoke(item.Key, item.Value);
                }
                catch (Exception e)
                {
                    Mod.LogError("TcpTransport.OnDataReceived error: " + e.Message);
                }
            }
        }

        // ---------------- 发送 ----------------

        public void SendTo(MpPeer peer, byte[] data)
        {
            if (data == null || data.Length == 0) return;
            TcpClient tc = GetClientForPeer(peer);
            if (tc == null) return;
            try
            {
                WriteMessage(tc, data);
            }
            catch (Exception e)
            {
                Mod.LogError("TcpTransport.SendTo error: " + e.Message);
            }
        }

        public void Broadcast(byte[] data)
        {
            if (data == null) return;
            lock (_peersLock)
            {
                foreach (MpPeer peer in _peers.Values)
                {
                    TcpClient tc;
                    if (_peerClients.TryGetValue(peer.EndPoint.ToString(), out tc))
                    {
                        try
                        {
                            WriteMessage(tc, data);
                        }
                        catch (Exception e)
                        {
                            Mod.LogError("TcpTransport.Broadcast error: " + e.Message);
                        }
                    }
                }
            }
        }

        /// <summary>写入一条消息：[4字节长度][payload]。</summary>
        private static void WriteMessage(TcpClient tc, byte[] data)
        {
            NetworkStream ns = tc.GetStream();
            byte[] len = BitConverter.GetBytes(data.Length);
            ns.Write(len, 0, 4);
            ns.Write(data, 0, data.Length);
            ns.Flush();
        }

        // ---------------- 连接管理 ----------------

        /// <summary>房主：接受客户端连接，每个连接一个接收线程。</summary>
        private void AcceptLoop()
        {
            while (_running && _listener != null)
            {
                TcpClient client = null;
                try
                {
                    client = _listener.AcceptTcpClient();
                    client.NoDelay = true;
                    IPEndPoint ep = (IPEndPoint)client.Client.RemoteEndPoint;
                    MpPeer peer = GetOrAddPeer(ep);
                    lock (_peersLock)
                    {
                        _peerClients[ep.ToString()] = client;
                    }

                    Thread t = new Thread(() => PeerReceiveLoop(peer))
                        { IsBackground = true, Name = "MpTcpPeer" + ep.Port };
                    t.Start();
                    Mod.LogLobby("TcpTransport: accepted client " + ep + ", total peers=" + GetPeersCount());
                }
                catch (SocketException)
                {
                }
                catch (Exception e)
                {
                    if (client != null)
                    {
                        try
                        {
                            client.Close();
                        }
                        catch
                        {
                        }
                    }

                    Mod.LogError("TcpTransport.AcceptLoop error: " + e.Message);
                }
            }
        }

        /// <summary>单个连接的接收循环：读 [4字节长度][payload]，完整包入队。</summary>
        private void PeerReceiveLoop(MpPeer peer)
        {
            TcpClient tc = GetClientForPeer(peer);
            if (tc == null) return;
            NetworkStream ns;
            try
            {
                ns = tc.GetStream();
            }
            catch
            {
                return;
            }

            byte[] lenBuf = new byte[4];
            while (_running)
            {
                try
                {
                    if (!ReadExactly(ns, lenBuf, 4)) break;
                    int len = BitConverter.ToInt32(lenBuf, 0);
                    if (len <= 0 || len > MaxMessageLength) break;
                    byte[] data = new byte[len];
                    if (!ReadExactly(ns, data, len)) break;
                    peer.LastReceiveTick = NowMs;
                    _incoming.Enqueue(new KeyValuePair<MpPeer, byte[]>(peer, data));
                }
				catch (Exception)
				{
					break;
				}
			}
			// 连接断开（EOF/异常/对方 Close）：从对端表移除并通知上层。
			// 注意：仅当仍在运行（非 Stop() 主动关闭）时才通知，避免清理时的噪音。
			MpPeer removedPeer = null;
			if (_running)
			{
				lock (_peersLock)
				{
					if (peer != null && peer.EndPoint != null && _peers.ContainsKey(peer.EndPoint.ToString()))
					{
						_peers.Remove(peer.EndPoint.ToString());
						TcpClient c;
						if (_peerClients.TryGetValue(peer.EndPoint.ToString(), out c))
						{
							try { c.Close(); } catch { }
						}
						_peerClients.Remove(peer.EndPoint.ToString());
						removedPeer = peer;
					}
				}
			}
			Mod.LogLobby("TcpTransport: peer " + peer.EndPoint + " read loop ended (disconnect/timeout)");
			if (removedPeer != null) OnPeerTimeout?.Invoke(removedPeer);
		}

        /// <summary>从流中精确读取 count 字节，EOF 返回 false。</summary>
        private static bool ReadExactly(NetworkStream ns, byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = ns.Read(buffer, offset, count - offset);
                if (read <= 0) return false;
                offset += read;
            }

            return true;
        }

        /// <summary>根据 peer 获取其 TCP 连接（房主按 endpoint 查表；客户端返回主连接）。</summary>
        private TcpClient GetClientForPeer(MpPeer peer)
        {
            if (peer == null || peer.EndPoint == null) return null;
            lock (_peersLock)
            {
                TcpClient c;
                if (_peerClients.TryGetValue(peer.EndPoint.ToString(), out c)) return c;
            }

            return _client; // 客户端模式：对端即房主主连接
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

        public void RemovePeer(MpPeer peer)
        {
            if (peer == null || peer.EndPoint == null) return;
            lock (_peersLock)
            {
                _peers.Remove(peer.EndPoint.ToString());
                TcpClient c;
                if (_peerClients.TryGetValue(peer.EndPoint.ToString(), out c))
                {
                    try
                    {
                        c.Close();
                    }
                    catch
                    {
                    }
                }

                _peerClients.Remove(peer.EndPoint.ToString());
            }
        }

		/// <summary>
		/// TCP 下此方法仅是"半开连接"兜底：正常情况下连接断开由 read loop 检测（EOF/异常）。
		/// 只有当对端长时间（timeoutMs）无任何数据、且连接仍处于半开状态时才触发移除。
		/// 注意：TCP 对端"暂时不发数据"（如主线程卡顿/GC）不代表断线，因此 timeoutMs 应设得较大。
		/// </summary>
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
                    foreach (MpPeer peer in expired)
                    {
                        _peers.Remove(peer.EndPoint.ToString());
                        TcpClient c;
                        if (_peerClients.TryGetValue(peer.EndPoint.ToString(), out c))
                        {
                            try
                            {
                                c.Close();
                            }
                            catch
                            {
                            }
                        }

                        _peerClients.Remove(peer.EndPoint.ToString());
                    }
                }
            }

            if (expired != null)
            {
                foreach (MpPeer peer in expired) OnPeerTimeout?.Invoke(peer);
            }
        }

        public IReadOnlyCollection<MpPeer> GetPeers()
        {
            lock (_peersLock)
            {
                return new List<MpPeer>(_peers.Values);
            }
        }

        public int GetPeersCount()
        {
            lock (_peersLock)
            {
                return _peers.Count;
            }
        }
    }
}