using System;
using System.Collections.Generic;
using UnityEngine;
using static Assets.Scripts.Net.Sync.LocalCraftSender;
using Assets.Scripts.Net.Sync;

namespace Assets.Scripts.Net.Session
{
	/// <summary>
	/// 飞船内容分发(2026-09-22 重构:自 MpNetworkManager 逐字搬来):本机飞船上报、XML 按需下载(SP2 方案)、
	/// 客户端 CraftData 与房主 host craft 的双向重发确认状态机。
	/// </summary>
	internal class MpCraftCatalog
	{
		private readonly MpNetworkManager _mp;

		internal MpCraftCatalog(MpNetworkManager mp) { _mp = mp; }

		/// <summary>停止联机时复位上报/重发状态(与旧 Stop() 中四行等价)。</summary>
		internal void OnSessionStopped()
		{
			_craftReported = false;
			_craftResendTimer = 0f;
			_hostCraftResendTimer = 0f;
			_hostCraftResend.Clear();
		}

		/// <summary>客户端未收到房主 CraftDataAck 时，CraftData 重发间隔（秒）。</summary>
		private const float CraftResendIntervalSec = 1.5f;

		internal float _craftResendTimer; // 客户端重发 CraftData 节流计时
		internal float _hostCraftResendTimer; // 房主重发 host craft（PlayerJoin）节流计时
		internal bool _craftReported;      // 本机飞船已上报且被房主确认（客户端收到 CraftDataAck 才置 true）
		// 房主：记录"已发给客户端、但尚未收到 PlayerJoinAck 确认"的 host craft（key=peer.EndPoint）。
		internal readonly Dictionary<string, float> _hostCraftResend = new Dictionary<string, float>();
		internal string _localCraftXml = string.Empty;

		// SP2 按需下载：客户端缓存 hash->xml，避免重复下载同一飞船。
		internal readonly Dictionary<string, string> _xmlCache = new Dictionary<string, string>();
		// 已请求但尚未收到响应的 playerId -> 请求时的 hash（hash 变化时重新请求，防止飞船更新后漏拉）。
		internal readonly Dictionary<int, string> _pendingXmlRequests = new Dictionary<int, string>();

		/// <summary>
		/// 刷新本机飞船 NodeId 并上报（进入飞行场景或飞船变化时调用）。
		/// 客户端把本机飞船（NodeId + craft XML）发给房主；
		/// 房主广播 PlayerJoin（含 XML）让所有客户端知道自己的飞船。
		/// </summary>
		internal void ReportLocalCraft()
		{
			if (!_mp.IsConnected) return;
			int nodeId = GetLocalCraftNodeId();
			if (nodeId < 0) return;

			string craftXml = GetLocalCraftXml();
			bool changed = nodeId != _mp.LocalNodeId || !string.Equals(_localCraftXml, craftXml);
			_mp.LocalNodeId = nodeId;
			_localCraftXml = craftXml;

			if (_mp.IsServer)
			{
				// 房主：广播自己的飞船（只带 hash，XML 由客户端按需下载）给所有客户端；已广播过且未变化则跳过。
				if (_craftReported && !changed) return;
				string craftHash = MpMessages.ComputeXmlHash(craftXml);
				_mp.Transport.Broadcast(MpMessages.EncodePlayerJoin(_mp.PlayerId, _mp.LocalNodeId, _mp.PlayerName, craftHash));
				_craftReported = true; // 房主无需确认（新加入者由 OnHello 补发）
				// 对所有已连接 peer 登记待确认：客户端回 PlayerJoinAck 前周期性重发（防公网丢包）。
				foreach (MpPeer p in _mp.Transport.GetPeers())
				{
					_hostCraftResend[p.Id] = Time.unscaledTime;
				}
				Mod.LogLobby("MP.RefreshLocalCraft (host): broadcast PlayerJoin playerId=" + _mp.PlayerId + ", nodeId=" + _mp.LocalNodeId +
					", hash=" + craftHash +
					", xmlLen=" + (craftXml == null ? 0 : craftXml.Length) +
					", pendingAck=" + _hostCraftResend.Count);
			}
			else
			{
				// 客户端：发给房主。发出去后不置 _craftReported，
				// 必须等房主 CraftDataAck 确认（防大分片公网丢包：确认前每 1.5s 重发）。
				bool sent = false;
				foreach (MpPeer peer in _mp.Transport.GetPeers())
				{
					if (peer.IsServer)
					{
						_mp.Transport.SendTo(peer, MpMessages.EncodeCraftData(_mp.LocalNodeId, craftXml));
						Mod.LogLobby("MP.RefreshLocalCraft (client): sent CraftData nodeId=" + _mp.LocalNodeId + " to host " + peer.Id +
							", xmlLen=" + (craftXml == null ? 0 : craftXml.Length) +
							", acked=" + _craftReported);
						sent = true;
						break;
					}
				}
				if (!sent)
				{
					Mod.LogLobby("MP.RefreshLocalCraft (client): no server peer found yet, will retry (nodeId=" + _mp.LocalNodeId + ")");
				}
			}
			Mod.Log("MP: local craft NodeId=" + _mp.LocalNodeId + ", xmlLen=" + (craftXml == null ? 0 : craftXml.Length));
		}

		/// <summary>周期性重发本机飞船信息(客户端 CraftData / 房主 host craft),直到对端确认(防公网丢包)。</summary>
		internal void UpdateResendTimers()
		{
			if (!_craftReported)
			{
				// 本机飞船未上报/未确认：周期性重试（CraftData 分片在公网可能丢包，
				// 只发一次遇到丢片会导致房主永远收不齐，故确认前持续重发）。
				_craftResendTimer -= Time.unscaledDeltaTime;
				if (_craftResendTimer <= 0f)
				{
					_craftResendTimer = CraftResendIntervalSec;
					ReportLocalCraft();
				}
			}
			// 房主：已发 host craft 但客户端尚未回 PlayerJoinAck 的 peer，周期性重发
			// （只重发 hash 小包，XML 由客户端按需下载；确认前持续重发防公网丢包）。
			if (_mp.IsServer && _hostCraftResend.Count > 0 && _mp.LocalNodeId >= 0 && !string.IsNullOrEmpty(_localCraftXml))
			{
				_hostCraftResendTimer -= Time.unscaledDeltaTime;
				if (_hostCraftResendTimer <= 0f)
				{
					_hostCraftResendTimer = CraftResendIntervalSec;
					byte[] hostJoin = MpMessages.EncodePlayerJoin(_mp.PlayerId, _mp.LocalNodeId, _mp.PlayerName, MpMessages.ComputeXmlHash(_localCraftXml));
					foreach (MpPeer peer in _mp.Transport.GetPeers())
					{
					if (_hostCraftResend.ContainsKey(peer.Id))
					{
						_mp.Transport.SendTo(peer, hostJoin);
						Mod.LogLobby("MP.Update (host): resend host craft PlayerJoin (nodeId=" + _mp.LocalNodeId + ") to " +
							peer.Id + " (unacked)");
						}
					}
				}
			}
		}

		/// <summary>
		/// 客户端收到房主的 CraftDataAck：确认房主已完整收到本机飞船（nodeId 匹配），
		/// 此后停止周期性重发 CraftData。
		/// </summary>
		internal void HandleCraftDataAck(byte[] packet)
		{
			if (_mp.IsServer) return;
			int nodeId;
			if (!MpMessages.TryDecodeCraftDataAck(packet, out nodeId)) return;
			if (nodeId < 0) return;
			if (nodeId == _mp.LocalNodeId)
			{
				_craftReported = true;
				Mod.LogLobby("MP.OnCraftDataAck (client): host confirmed craft NodeId=" + nodeId + ", stop resending");
			}
		}

		/// <summary>
		/// 房主收到客户端的 PlayerJoinAck：该客户端已收到指定玩家的飞船 XML。
		/// 若确认的是房主自己的飞船（playerId == _mp.PlayerId），停止对该 peer 重发 host craft。
		/// </summary>
		internal void HandlePlayerJoinAck(MpPeer peer, byte[] packet)
		{
			if (!_mp.IsServer) return;
			int playerId;
			if (!MpMessages.TryDecodePlayerJoinAck(packet, out playerId)) return;
			if (peer == null) return;
			if (playerId == _mp.PlayerId)
			{
				if (_hostCraftResend.Remove(peer.Id))
				{
					Mod.LogLobby("MP.OnPlayerJoinAck (host): peer " + peer.Id + " confirmed host craft, stop resending");
				}
			}
		}

		/// <summary>
		/// 收到加入者上报的本机飞船 NodeId + craft XML（CraftData）。
		/// 房主登记映射，并向所有客户端广播 PlayerJoin。
		/// </summary>
		internal void HandleCraftData(MpPeer peer, byte[] packet)
		{
			if (!_mp.IsServer) return;
			int nodeId; string craftXml;
			if (!MpMessages.TryDecodeCraftData(packet, out nodeId, out craftXml)) return;
			if (nodeId < 0) return;

			peer.NodeId = nodeId;
			peer.CraftXml = craftXml;
			_mp.Registry.RegisterPlayer(peer);

			// 回 Ack 给上报者：告知已完整收到其飞船，客户端据此停止周期性重发。
			_mp.Transport.SendTo(peer, MpMessages.EncodeCraftDataAck(nodeId));

			// SP2 方案：广播 PlayerJoin 只带 hash，其他客户端按需下载 XML（见 OnCraftXmlRequest）。
			string craftHash = MpMessages.ComputeXmlHash(craftXml);
			_mp.Transport.Broadcast(MpMessages.EncodePlayerJoin(peer.PlayerId, peer.NodeId, peer.PlayerName, craftHash));
			Mod.LogLobby("MP.OnCraftData (host): '" + peer.PlayerName + "' craft registered NodeId=" + nodeId +
				", hash=" + craftHash +
				", xmlLen=" + (craftXml == null ? 0 : craftXml.Length) +
				", sent CraftDataAck, broadcast PlayerJoin to " + _mp.Transport.GetPeersCount() + " peer(s)");
			_mp.RaisePlayerJoined(peer);
		}

		/// <summary>
		/// 房主：响应客户端的按需下载请求，把指定玩家的飞船 XML 发给请求者（大包，走可靠通道+分片）。
		/// </summary>
		internal void HandleCraftXmlRequest(MpPeer peer, byte[] packet)
		{
			if (!_mp.IsServer) return;
			int playerId; string hash;
			if (!MpMessages.TryDecodeCraftXmlRequest(packet, out playerId, out hash)) return;
			string craftXml = null;
			if (playerId == _mp.PlayerId)
			{
				// 房主自己（playerId=0）不在 _mp.Registry._playersByPlayerId 表中，单独用 _localCraftXml 响应。
				craftXml = _localCraftXml;
			}
			else
			{
				MpPeer target = null;
				lock (_mp.Registry._playersByPlayerId) { _mp.Registry._playersByPlayerId.TryGetValue(playerId, out target); }
				if (target != null) craftXml = target.CraftXml;
			}
			if (string.IsNullOrEmpty(craftXml))
			{
				Mod.Log("MP.OnCraftXmlRequest: player " + playerId + " has no craft xml yet");
				return;
			}
			// 用实际 XML 的 hash 响应（而非请求带过来的 hash），保证客户端缓存 key 正确（飞船中途变化时）
			string actualHash = MpMessages.ComputeXmlHash(craftXml);
			_mp.Transport.SendTo(peer, MpMessages.EncodeCraftXmlResponse(playerId, actualHash, craftXml));
			Mod.LogLobby("MP.OnCraftXmlRequest (host): sent craft xml for player " + playerId + " to " + peer.Id +
				", reqHash=" + hash + ", actualHash=" + actualHash + ", xmlLen=" + craftXml.Length);
		}

		/// <summary>
		/// 客户端：收到按需下载的飞船 XML。填入玩家信息并触发 OnPlayerJoined（远程飞船在此后生成）。
		/// </summary>
		internal void HandleCraftXmlResponse(byte[] packet)
		{
			if (_mp.IsServer) return;
			int playerId; string hash; string craftXml;
			if (!MpMessages.TryDecodeCraftXmlResponse(packet, out playerId, out hash, out craftXml)) return;
			_pendingXmlRequests.Remove(playerId);
			if (!string.IsNullOrEmpty(hash) && !string.IsNullOrEmpty(craftXml))
			{
				_xmlCache[hash] = craftXml;
			}
			MpPeer p = null;
			lock (_mp.Registry._playersByPlayerId) { _mp.Registry._playersByPlayerId.TryGetValue(playerId, out p); }
			if (p == null)
			{
				Mod.Log("MP.OnCraftXmlResponse: player " + playerId + " not registered, xml discarded");
				return;
			}
			p.CraftXml = craftXml;
			Mod.LogLobby("MP.OnCraftXmlResponse (client): received craft xml for player " + playerId +
				", hash=" + hash + ", xmlLen=" + (craftXml == null ? 0 : craftXml.Length));
			_mp.RaisePlayerJoined(p);
		}
	}
}
