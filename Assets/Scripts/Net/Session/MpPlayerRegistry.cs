using System;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Net.Session
{
	/// <summary>
	/// 房间玩家表(2026-09-22 重构:自 MpNetworkManager 逐字搬来):PlayerId 分配、登记、超时/踢出/离开的统一移除出口。
	/// 会话期间行为与旧实现一致(含 Stop 不复位 _nextPlayerId 的既有行为,见 plans/README.md §八 #13)。
	/// </summary>
	internal class MpPlayerRegistry
	{
		private readonly MpNetworkManager _mp;

		internal MpPlayerRegistry(MpNetworkManager mp) { _mp = mp; }

		/// <summary>停止联机时清空玩家表(与旧 Stop() 中的 lock+Clear 等价)。</summary>
		internal void Clear() { lock (_playersByPlayerId) _playersByPlayerId.Clear(); }

		internal readonly Dictionary<int, MpPeer> _playersByPlayerId = new Dictionary<int, MpPeer>();

		/// <summary>
		/// 房主：踢出指定玩家（发 Kick 通知 + 断开传输连接 + 移除记录 + 广播 PlayerLeave + 触发 OnPlayerLeft 清理远程飞船）。
		/// </summary>
		internal void KickPlayer(int playerId)
		{
			if (!_mp.IsServer) return;
			if (playerId == _mp.PlayerId) return; // 不能踢自己
			MpPeer target = null;
			lock (_playersByPlayerId) { _playersByPlayerId.TryGetValue(playerId, out target); }
			if (target == null) return;

			Mod.LogLobby("MP.KickPlayer: kicking player " + playerId + " ('" + target.PlayerName + "', " + target.Id + ")");
			try { _mp.Transport.SendTo(target, MpMessages.EncodeKick()); }
			catch (Exception e) { Mod.LogLobby("MP.KickPlayer: Kick send failed: " + e.Message); }
			_mp.Transport.DisconnectPeer(target);

			MpPeer removed = null;
			lock (_playersByPlayerId)
			{
				if (_playersByPlayerId.TryGetValue(playerId, out removed))
				{
					_playersByPlayerId.Remove(playerId);
				}
			}
			_mp.Catalog._pendingXmlRequests.Remove(playerId);
			_mp.Catalog._hostCraftResend.Remove(target.Id);
			if (removed != null)
			{
				_mp.Transport.Broadcast(MpMessages.EncodePlayerLeave(playerId));
				Mod.LogLobby("MP.KickPlayer: broadcast PlayerLeave playerId=" + playerId);
				_mp.RaisePlayerLeft(removed);
			}
		}

		// ---------------- 工具 ----------------

		private int _nextPlayerId = 1;
		internal int NextPlayerId() => _nextPlayerId++;

		internal void RegisterPlayer(MpPeer peer)
		{
			if (peer.PlayerId < 0) return;
			lock (_playersByPlayerId)
			{
				_playersByPlayerId[peer.PlayerId] = peer;
			}
		}

		internal void HandlePeerTimeout(MpPeer peer)
		{
			Mod.LogLobby("MP peer timeout: " + peer.Id + " (PlayerId=" + peer.PlayerId + ", NodeId=" + peer.NodeId + ")");
			if (peer != null) _mp.Catalog._hostCraftResend.Remove(peer.Id);
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
				_mp.Catalog._pendingXmlRequests.Remove(removed.PlayerId);
				if (_mp.IsServer)
				{
					_mp.Transport.Broadcast(MpMessages.EncodePlayerLeave(removed.PlayerId));
					Mod.LogLobby("MP peer timeout: broadcast PlayerLeave playerId=" + removed.PlayerId);
				}
				_mp.RaisePlayerLeft(removed);
			}
		}

		internal IReadOnlyCollection<MpPeer> GetPlayers()
		{
			lock (_playersByPlayerId) { return new List<MpPeer>(_playersByPlayerId.Values); }
		}
	}
}
