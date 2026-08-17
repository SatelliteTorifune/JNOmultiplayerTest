using System;
using System.Collections.Generic;

namespace Assets.Scripts.Net
{
	/// <summary>
	/// 统一传输层契约：SteamTransport / TcpTransport 共用。
	/// 让 MpNetworkManager 的 Transport 字段可在两者间切换（TCP 用于本地虚拟机 debug），
	/// 管理器所有调用点（Start/StartClient/DrainIncoming/SendTo/Broadcast/CheckTimeouts/GetPeers/事件…）无需改动。
	/// </summary>
	public interface IMpTransport : IDisposable
	{
		event Action<MpPeer, byte[]> OnDataReceived;
		event Action<MpPeer> OnPeerTimeout;

		int LocalPort { get; }
		bool IsRunning { get; }

		/// <summary>房主：开启监听。</summary>
		bool Start(int port);

		/// <summary>客户端：连接房主并发送首包（Hello）。</summary>
		bool StartClient(string host, int port, byte[] helloPacket);

		void Stop();
		void DrainIncoming();
		void SendTo(MpPeer peer, byte[] data);
		void Broadcast(byte[] data);
		void CheckTimeouts(long timeoutMs);
		IReadOnlyCollection<MpPeer> GetPeers();
		int GetPeersCount();

		/// <summary>房主：断开与指定对端的连接（踢人用）。断开后传输层会移除该 peer 并触发 OnPeerTimeout。</summary>
		void DisconnectPeer(MpPeer peer);
	}
}
