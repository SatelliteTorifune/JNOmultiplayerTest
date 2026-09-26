using System;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Net.Session
{
	/// <summary>
	/// 房间玩家表(2026-09-22 重构:自 MultiPlayerNetworkManager 逐字搬来):PlayerId 分配、登记、超时/踢出/离开的统一移除出口。
	/// 会话期间行为与旧实现一致(含 Stop 不复位 _nextPlayerId 的既有行为,见 plans/README.md §八 #13)。
	/// </summary>
	internal class MultiPlayerRegistry
	{
		private readonly NetworkManager _multiPlayer;

		internal MultiPlayerRegistry(NetworkManager multiPlayer) { _multiPlayer = multiPlayer; }

		/// <summary>停止联机时清空玩家表(与旧 Stop() 中的 lock+Clear 等价)。</summary>
		internal void Clear() { lock (_playersByPlayerId) _playersByPlayerId.Clear(); }

		internal readonly Dictionary<int, MultiPlayerPeer> _playersByPlayerId = new Dictionary<int, MultiPlayerPeer>();

		/// <summary>
		/// 房主：踢出指定玩家（发 Kick 通知 + 断开传输连接 + 移除记录 + 广播 PlayerLeave + 触发 OnPlayerLeft 清理远程飞船）。
		/// </summary>
		internal void KickPlayer(int playerId)
		{
			if (!_multiPlayer.IsServer) return;
			if (playerId == _multiPlayer.PlayerId) return; // 不能踢自己
			MultiPlayerPeer target = null;
			lock (_playersByPlayerId) { _playersByPlayerId.TryGetValue(playerId, out target); }
			if (target == null) return;

			Mod.LogLobby("MultiPlayer.KickPlayer: kicking player " + playerId + " ('" + target.PlayerName + "', " + target.Id + ")");
			try { _multiPlayer.Transport.SendTo(target, MultiPlayerMessages.EncodeKick()); }
			catch (Exception e) { Mod.LogLobby("MultiPlayer.KickPlayer: Kick send failed: " + e.Message); }
			_multiPlayer.Transport.DisconnectPeer(target);

			MultiPlayerPeer removed = null;
			lock (_playersByPlayerId)
			{
				if (_playersByPlayerId.TryGetValue(playerId, out removed))
				{
					_playersByPlayerId.Remove(playerId);
				}
			}
			_multiPlayer.Catalog._pendingXmlRequests.Remove(playerId);
			_multiPlayer.Catalog._hostCraftResend.Remove(target.Id);
			if (removed != null)
			{
				_multiPlayer.Transport.Broadcast(MultiPlayerMessages.EncodePlayerLeave(playerId));
				Mod.LogLobby("MultiPlayer.KickPlayer: broadcast PlayerLeave playerId=" + playerId);
				_multiPlayer.RaisePlayerLeft(removed);
			}
		}

		// ---------------- 工具 ----------------

		private int _nextPlayerId = 1;
		internal int NextPlayerId() => _nextPlayerId++;

		internal void RegisterPlayer(MultiPlayerPeer peer)
		{
			if (peer.PlayerId < 0) return;
			lock (_playersByPlayerId)
			{
				_playersByPlayerId[peer.PlayerId] = peer;
			}
		}

		internal void HandlePeerTimeout(MultiPlayerPeer peer)
		{
			Mod.LogLobby("MultiPlayer peer timeout: " + peer.Id + " (PlayerId=" + peer.PlayerId + ", NodeId=" + peer.NodeId + ")");
			if (peer != null) _multiPlayer.Catalog._hostCraftResend.Remove(peer.Id);
			MultiPlayerPeer removed = null;
			lock (_playersByPlayerId)
			{
				MultiPlayerPeer match = null;
				foreach (MultiPlayerPeer p in _playersByPlayerId.Values)
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
				_multiPlayer.Catalog._pendingXmlRequests.Remove(removed.PlayerId);
				if (_multiPlayer.IsServer)
				{
					_multiPlayer.Transport.Broadcast(MultiPlayerMessages.EncodePlayerLeave(removed.PlayerId));
					Mod.LogLobby("MultiPlayer peer timeout: broadcast PlayerLeave playerId=" + removed.PlayerId);
				}
				_multiPlayer.RaisePlayerLeft(removed);
			}
		}

		internal IReadOnlyCollection<MultiPlayerPeer> GetPlayers()
		{
			lock (_playersByPlayerId) { return new List<MultiPlayerPeer>(_playersByPlayerId.Values); }
		}

		/// <summary>按 PlayerId 查玩家(聊天名字解析等);未登记返回 null。</summary>
		internal MultiPlayerPeer GetPlayer(int playerId)
		{
			lock (_playersByPlayerId)
			{
				MultiPlayerPeer peer;
				_playersByPlayerId.TryGetValue(playerId, out peer);
				return peer;
			}
		}
	}
}
