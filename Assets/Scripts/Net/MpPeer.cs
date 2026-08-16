using System;
using System.Net;

namespace Assets.Scripts.Net
{
	/// <summary>
	/// 一个远端对等端（玩家）的标识与元信息。
	/// </summary>
	public class MpPeer : IEquatable<MpPeer>
	{
		public IPEndPoint EndPoint;
		public ulong SteamId;          // Steam 传输下的对端 SteamId（Steam P2P 寻址用）
		public int PlayerId = -1;      // 由房主分配
		public int NodeId = -1;        // 该玩家飞船的 NodeId

		/// <summary>
		/// 统一的对端标识字符串（用于字典 key / 日志）：
		/// Steam 传输下为 SteamId 字符串；IP 传输（TCP/UDP）下为 EndPoint。
		/// </summary>
		public string Id => SteamId != 0 ? SteamId.ToString() : (EndPoint != null ? EndPoint.ToString() : "<null>");

		// 玩家名：setter 存入 backing field（绝不能自我赋值，否则 setter 无限递归 → StackOverflowException）；
		// getter 在未设置时回退到 ModSettings 的默认玩家名。
		private string _playerName = string.Empty;
		public string PlayerName
		{
			get => string.IsNullOrEmpty(_playerName) ? ModSettings.Instance.PlayerName : _playerName;
			set => _playerName = value;
		}

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
}
