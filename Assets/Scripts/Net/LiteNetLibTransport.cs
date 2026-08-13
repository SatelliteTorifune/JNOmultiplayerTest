using System;
using System.Collections.Generic;
using System.Net;
using LiteNetLib;
using LiteNetLib.Utils;

namespace Assets.Scripts.Net
{
    /// <summary>
    /// LiteNetLib 传输封装：接口与 TcpTransport 完全兼容，MpNetworkManager 只需把字段类型从
    /// TcpTransport 换成 LiteNetLibTransport 即可无缝切换，房间逻辑/游戏同步逻辑完全不动。
    ///
    /// 为什么用 LiteNetLib（FishNet 的 Tugboat 底层库）而不是 FishNet Broadcast：
    /// - FishNet 的 Broadcast/RPC 泛型序列化依赖 codegen（GenericWriter<T>），而模组的
    ///   aMptest.dll 是运行时加载的，codegen 不会为其生成序列化器 → "Write method not found"；
    /// - LiteNetLib 无 codegen、无框架约束，纯 UDP 字节通道，正好匹配我们已有的 MpMessages 序列化；
    /// - 自带可靠(ReliableOrdered)/不可靠(Unreliable)通道：状态包走不可靠，大 XML 走可靠。
    ///
    /// 重要：UDP 单包受 MTU 限制（LiteNetLib 默认 1432），且本 LiteNetLib 版本已移除自动分片
    /// （"LNL Fragmentation was removed"）。因此本类对超过 MTU 的大包（craft XML 可达数百 KB）
    /// 做**应用层分片/重组**，对调用方完全透明。
    ///
    /// 注意：LiteNetLib 是事件驱动 + 需每帧 PollEvents()（在 DrainIncoming 里调用）。
    /// </summary>
    public class LiteNetLibTransport : IDisposable
    {
        /// <summary>收到完整消息（主线程回调，由 PollEvents 触发）。</summary>
        public event Action<MpPeer, byte[]> OnDataReceived;
        /// <summary>对端超时/断开。</summary>
        public event Action<MpPeer> OnPeerTimeout;

        /// <summary>分片单包最大字节（小于 MTU 1432，留足余量）。</summary>
        private const int MaxChunkSize = 1200;
        /// <summary>分片消息首字节标记（不会与 MpMessageType 1~11 冲突）。</summary>
        private const byte FragmentMarker = 0xFC;
        /// <summary>分片头长度：marker(1) + totalLen(4) + chunkIndex(4) + chunkCount(4)。</summary>
        private const int FragmentHeaderSize = 13;

        private NetManager _nm;
        private EventBasedNetListener _listener;
        private bool _isServer;
        private volatile bool _running;

        // 房主：peer.Id -> MpPeer / NetPeer 映射
        private readonly Dictionary<int, MpPeer> _serverPeers = new();
        private readonly Dictionary<int, NetPeer> _serverNetPeers = new();
        private readonly object _peersLock = new();
        // 客户端：到房主的 peer
        private MpPeer _serverPeer;
        private NetPeer _clientNetPeer;
        // 客户端：连接建立后要发的首个包（Hello）
        private byte[] _pendingHello;

        // 分片重组缓冲：key = (peerId + 1)（server 用 peer.Id，client 用 0）；同时只有一个大消息在传。
        private class FragmentBuffer
        {
            public byte[] Data;
            public int Received;
            public int ChunkCount;
        }
        private readonly Dictionary<int, FragmentBuffer> _fragments = new();
        private readonly object _fragmentLock = new();

        public int LocalPort { get; private set; }
        public bool IsRunning => _running;

        /// <summary>毫秒级时间戳（纯 .NET）。</summary>
        private static long NowMs => DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

        /// <summary>根据消息首字节自动选投递方式：状态包不可靠，其余可靠有序。</summary>
        private static DeliveryMethod GetDelivery(byte[] data)
        {
            if (data != null && data.Length > 0 && data[0] == (byte)MpMessageType.State)
                return DeliveryMethod.Unreliable;
            return DeliveryMethod.ReliableOrdered;
        }

        // ---------------- 生命周期 ----------------

        /// <summary>房主：开启 LiteNetLib server（监听）。</summary>
        public bool Start(int port)
        {
            Stop();
            try
            {
                _isServer = true;
                _running = true;
                LocalPort = port;

                _listener = new EventBasedNetListener();
                _nm = new NetManager(_listener);
                _nm.AutoRecycle = true;
                WireListenerEvents();

                bool ok = _nm.Start(port);
                if (!ok)
                {
                    Mod.LogLobby("LiteNetLibTransport.Start FAILED: NetManager.Start(" + port + ") returned false");
                    Stop();
                    return false;
                }
                Mod.LogLobby("LiteNetLibTransport.Start: server listening on port " + port);
                return true;
            }
            catch (Exception e)
            {
                Mod.LogLobby("LiteNetLibTransport.Start FAILED: " + e.Message);
                Mod.LogError("LiteNetLibTransport.Start failed: " + e.Message);
                Stop();
                return false;
            }
        }

        /// <summary>客户端：连接房主，连接建立后自动发送首包（Hello）。</summary>
        public bool StartClient(string host, int port, byte[] helloPacket)
        {
            Stop();
            try
            {
                _isServer = false;
                _running = true;
                LocalPort = port;
                _pendingHello = helloPacket;

                _listener = new EventBasedNetListener();
                _nm = new NetManager(_listener);
                _nm.AutoRecycle = true;
                WireListenerEvents();

                // client 也要先 Start()（绑定随机本地端口）才能 Connect
                bool started = _nm.Start();
                if (!started)
                {
                    Mod.LogLobby("LiteNetLibTransport.StartClient FAILED: NetManager.Start() returned false");
                    Stop();
                    return false;
                }

                NetPeer peer = _nm.Connect(host, port, (string)null);
                if (peer == null)
                {
                    Mod.LogLobby("LiteNetLibTransport.StartClient FAILED: Connect(" + host + ":" + port + ") returned null");
                    Stop();
                    return false;
                }
                Mod.LogLobby("LiteNetLibTransport.StartClient: connecting to " + host + ":" + port +
                    ", peerState=" + peer.ConnectionState + ", peerEp=" + peer +
                    ", nm.IsRunning=" + _nm.IsRunning);
                return true;
            }
            catch (Exception e)
            {
                Mod.LogLobby("LiteNetLibTransport.StartClient FAILED: " + e.Message);
                Mod.LogError("LiteNetLibTransport.StartClient failed: " + e.Message);
                Stop();
                return false;
            }
        }

        public void Stop()
        {
            _running = false;
            try
            {
                if (_nm != null)
                {
                    UnwireListenerEvents();
                    _nm.Stop(true); // 发断开消息
                }
            }
            catch (Exception e)
            {
                Mod.LogError("LiteNetLibTransport.Stop error: " + e.Message);
            }
            _nm = null;
            _listener = null;
            lock (_peersLock)
            {
                _serverPeers.Clear();
                _serverNetPeers.Clear();
            }
            lock (_fragmentLock) _fragments.Clear();
            _serverPeer = null;
            _clientNetPeer = null;
            _pendingHello = null;
        }

        public void Dispose() => Stop();

        // ---------------- 主线程轮询 ----------------

        private float _diagTimer;

        /// <summary>每帧轮询 LiteNetLib 事件队列（连接/断开/接收在此触发事件）。</summary>
        public void DrainIncoming()
        {
            if (_nm != null && _running)
            {
                try { _nm.PollEvents(); }
                catch (Exception e) { Mod.LogError("LiteNetLibTransport.PollEvents error: " + e.Message); }
                // 诊断：每 1s 打印一次连接状态
                _diagTimer -= UnityEngine.Time.unscaledDeltaTime;
                if (_diagTimer <= 0f)
                {
                    _diagTimer = 1f;
                    Mod.LogLobby("LiteNetLibTransport diag: running=" + _running + ", isServer=" + _isServer +
                        ", nmRunning=" + _nm.IsRunning +
                        ", serverPeer=" + (_serverPeer != null) +
                        ", clientNetPeer=" + (_clientNetPeer != null ? _clientNetPeer.ConnectionState.ToString() : "null") +
                        ", serverPeers=" + GetPeersCount());
                }
            }
        }

        /// <summary>兼容 TcpTransport 接口；LiteNetLib 自带连接超时检测（DisconnectTimeout）。</summary>
        public void CheckTimeouts(long timeoutMs) { }

        // ---------------- 发送 ----------------

        public void SendTo(MpPeer peer, byte[] data)
        {
            if (data == null || data.Length == 0 || _nm == null || !_running) return;
            try
            {
                if (_isServer)
                {
                    int peerId = peer != null && peer.EndPoint != null ? peer.EndPoint.Port : -1;
                    NetPeer np;
                    lock (_peersLock) { _serverNetPeers.TryGetValue(peerId, out np); }
                    if (np == null || np.ConnectionState != ConnectionState.Connected) return;
                    SendChunked(np, data);
                }
                else
                {
                    if (_clientNetPeer != null && _clientNetPeer.ConnectionState == ConnectionState.Connected)
                        SendChunked(_clientNetPeer, data);
                }
            }
            catch (Exception e)
            {
                Mod.LogError("LiteNetLibTransport.SendTo error: " + e.Message);
            }
        }

        public void Broadcast(byte[] data)
        {
            if (data == null || data.Length == 0 || _nm == null || !_running) return;
            try
            {
                if (_isServer)
                {
                    // SendToAll 对超 MTU 的大包不支持分片，故遍历 peer 逐个发
                    List<NetPeer> peers;
                    lock (_peersLock) { peers = new List<NetPeer>(_serverNetPeers.Values); }
                    foreach (NetPeer np in peers)
                    {
                        if (np.ConnectionState == ConnectionState.Connected)
                            SendChunked(np, data);
                    }
                }
                else
                {
                    if (_clientNetPeer != null && _clientNetPeer.ConnectionState == ConnectionState.Connected)
                        SendChunked(_clientNetPeer, data);
                }
            }
            catch (Exception e)
            {
                Mod.LogError("LiteNetLibTransport.Broadcast error: " + e.Message);
            }
        }

        /// <summary>
        /// 发送一条消息；超过 MaxChunkSize 的自动分片（分片走可靠通道保证顺序/完整）。
        /// </summary>
        private void SendChunked(NetPeer target, byte[] data)
        {
            if (target == null) return;
            DeliveryMethod dm = GetDelivery(data);
            if (data.Length <= MaxChunkSize)
            {
                target.Send(data, dm);
                return;
            }
            // 大包：分片。分片必须可靠（ReliableOrdered），保证顺序与不丢。
            int count = (data.Length + MaxChunkSize - 1) / MaxChunkSize;
            for (int i = 0; i < count; i++)
            {
                int offset = i * MaxChunkSize;
                int len = Math.Min(MaxChunkSize, data.Length - offset);
                byte[] chunk = new byte[FragmentHeaderSize + len];
                chunk[0] = FragmentMarker;
                BitConverter.GetBytes(data.Length).CopyTo(chunk, 1); // totalLen
                BitConverter.GetBytes(i).CopyTo(chunk, 5);           // chunkIndex
                BitConverter.GetBytes(count).CopyTo(chunk, 9);       // chunkCount
                Buffer.BlockCopy(data, offset, chunk, FragmentHeaderSize, len);
                target.Send(chunk, DeliveryMethod.ReliableOrdered);
            }
            Mod.LogLobby("LiteNetLibTransport: fragmented " + data.Length + " bytes into " + count + " chunks");
        }

        // ---------------- 连接管理 ----------------

        public IReadOnlyCollection<MpPeer> GetPeers()
        {
            lock (_peersLock)
            {
                if (_isServer) return new List<MpPeer>(_serverPeers.Values);
                return _serverPeer == null ? new List<MpPeer>() : new List<MpPeer> { _serverPeer };
            }
        }

        public int GetPeersCount()
        {
            lock (_peersLock)
            {
                if (_isServer) return _serverPeers.Count;
                return _serverPeer == null ? 0 : 1;
            }
        }

        // ---------------- LiteNetLib 事件 ----------------

        private void WireListenerEvents()
        {
            if (_listener == null) return;
            _listener.ConnectionRequestEvent += OnConnectionRequest;
            _listener.PeerConnectedEvent += OnPeerConnected;
            _listener.PeerDisconnectedEvent += OnPeerDisconnected;
            _listener.NetworkReceiveEvent += OnNetworkReceive;
        }

        private void UnwireListenerEvents()
        {
            if (_listener == null) return;
            _listener.ConnectionRequestEvent -= OnConnectionRequest;
            _listener.PeerConnectedEvent -= OnPeerConnected;
            _listener.PeerDisconnectedEvent -= OnPeerDisconnected;
            _listener.NetworkReceiveEvent -= OnNetworkReceive;
        }

        /// <summary>房主：接受所有连接请求。</summary>
        private void OnConnectionRequest(ConnectionRequest request)
        {
            if (request != null)
            {
                request.Accept();
                Mod.LogLobby("LiteNetLibTransport: accepted connection from " + request.RemoteEndPoint);
            }
        }

        /// <summary>房主：客户端连上；客户端：连上房主。</summary>
        private void OnPeerConnected(NetPeer peer)
        {
            if (_isServer)
            {
                // 用 peer.Id 合成唯一 EndPoint（端口=peerId），MpPeer.EndPoint 兼容
                var mp = new MpPeer
                {
                    EndPoint = new IPEndPoint(IPAddress.Loopback, peer.Id),
                    IsServer = false,
                    LastReceiveTick = NowMs
                };
                lock (_peersLock)
                {
                    _serverPeers[peer.Id] = mp;
                    _serverNetPeers[peer.Id] = peer;
                }
                Mod.LogLobby("LiteNetLibTransport: client " + peer.Id + " connected, peers=" + GetPeersCount());
            }
            else
            {
                _clientNetPeer = peer;
                _serverPeer = new MpPeer
                {
                    EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                    IsServer = true,
                    LastReceiveTick = NowMs
                };
                // 连接建立后发首包（Hello）
                if (_pendingHello != null)
                {
                    peer.Send(_pendingHello, DeliveryMethod.ReliableOrdered);
                    _pendingHello = null;
                }
                Mod.LogLobby("LiteNetLibTransport: connected to host, peers=" + GetPeersCount());
            }
        }

        /// <summary>对端断开/超时。</summary>
        private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            if (_isServer)
            {
                MpPeer removed = null;
                lock (_peersLock)
                {
                    if (_serverPeers.TryGetValue(peer.Id, out removed))
                    {
                        _serverPeers.Remove(peer.Id);
                        _serverNetPeers.Remove(peer.Id);
                    }
                }
                lock (_fragmentLock) _fragments.Remove(peer.Id + 1);
                Mod.LogLobby("LiteNetLibTransport: client " + peer.Id + " disconnected (reason=" + info.Reason + ", wasRegistered=" + (removed != null) + ")");
                if (removed != null) OnPeerTimeout?.Invoke(removed);
            }
            else
            {
                MpPeer p = _serverPeer;
                _serverPeer = null;
                _clientNetPeer = null;
                _running = false;
                lock (_fragmentLock) _fragments.Clear();
                Mod.LogLobby("LiteNetLibTransport: disconnected from host (reason=" + info.Reason + ", wasConnected=" + (p != null) + ")");
                if (p != null) OnPeerTimeout?.Invoke(p);
            }
        }

        /// <summary>收到原始消息；若是分片则收集重组后触发 OnDataReceived。</summary>
        private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
        {
            if (reader == null) return;
            byte[] data = reader.GetRemainingBytes(); // 从当前读位置到末尾
            reader.Recycle(); // AutoRecycle=true 时内部 no-op
            if (data == null || data.Length == 0) return;

            // 分片重组
            if (data.Length >= FragmentHeaderSize && data[0] == FragmentMarker)
            {
                if (HandleFragment(peer, data)) return; // 未收齐，等待更多分片
                // 收齐后 data 被重组并继续走下面（见 HandleFragment 返回 false 时的处理）
                // 这里需要拿到重组后的完整数据，故 HandleFragment 直接返回 bool 并改由内部触发。
                return;
            }

            DeliverData(peer, data);
        }

        /// <summary>
        /// 处理一个分片。返回 true 表示尚未收齐（继续等待）；返回 false 表示已收齐并已触发 OnDataReceived。
        /// </summary>
        private bool HandleFragment(NetPeer peer, byte[] chunk)
        {
            int totalLen = BitConverter.ToInt32(chunk, 1);
            int chunkIndex = BitConverter.ToInt32(chunk, 5);
            int chunkCount = BitConverter.ToInt32(chunk, 9);
            int payloadLen = chunk.Length - FragmentHeaderSize;
            int key = peer.Id + 1;

            FragmentBuffer fb;
            lock (_fragmentLock)
            {
                if (!_fragments.TryGetValue(key, out fb))
                {
                    fb = new FragmentBuffer { Data = new byte[totalLen], Received = 0, ChunkCount = chunkCount };
                    _fragments[key] = fb;
                }
                if (chunkIndex < fb.Data.Length / MaxChunkSize + 1 && chunkIndex >= 0)
                {
                    int offset = chunkIndex * MaxChunkSize;
                    Buffer.BlockCopy(chunk, FragmentHeaderSize, fb.Data, offset, payloadLen);
                    fb.Received++;
                }
                if (fb.Received < chunkCount)
                {
                    return true; // 继续等待
                }
                _fragments.Remove(key);
            }

            // 收齐：触发
            DeliverData(peer, fb.Data);
            return false;
        }

        private void DeliverData(NetPeer peer, byte[] data)
        {
            if (_isServer)
            {
                MpPeer mp;
                lock (_peersLock) { _serverPeers.TryGetValue(peer.Id, out mp); }
                if (mp == null) return;
                mp.LastReceiveTick = NowMs;
                OnDataReceived?.Invoke(mp, data);
            }
            else
            {
                if (_serverPeer == null) return;
                _serverPeer.LastReceiveTick = NowMs;
                OnDataReceived?.Invoke(_serverPeer, data);
            }
        }
    }
}