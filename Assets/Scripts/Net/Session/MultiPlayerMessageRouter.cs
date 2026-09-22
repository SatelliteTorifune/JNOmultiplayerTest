using System;
using System.Collections.Generic;
using Assets.Scripts.Flight;
using ModApi;
using UnityEngine;
using Assets.Scripts.Net.Sync;

namespace Assets.Scripts.Net.Session
{
	/// <summary>
	/// 协议分发与房间流程(2026-09-22 重构:自 MultiPlayerNetworkManager 逐字搬来):HandlePacket 分发 + Hello/Welcome/
	/// PlayerJoin/PlayerLeave/State/Ping-Pong/Kick/TickRate 处理;CraftData 与 XML 类消息转发给 MultiPlayerCraftCatalog。
	/// </summary>
	internal class MultiPlayerMessageRouter
	{
		private readonly NetworkManager _multiPlayer;

		internal MultiPlayerMessageRouter(NetworkManager multiPlayer) { _multiPlayer = multiPlayer; }

		internal void HandlePacket(MultiPlayerPeer peer, byte[] packet)
		{
			MultiPlayerMessageType type = MultiPlayerMessages.PeekType(packet);
			switch (type)
			{
				case MultiPlayerMessageType.Hello:
					_multiPlayer.Router.OnHello(peer, packet);
					break;
				case MultiPlayerMessageType.Welcome:
					_multiPlayer.Router.OnWelcome(peer, packet);
					break;
				case MultiPlayerMessageType.PlayerJoin:
					_multiPlayer.Router.OnPlayerJoin(peer, packet);
					break;
				case MultiPlayerMessageType.PlayerLeave:
					_multiPlayer.Router.OnPlayerLeave(packet);
					break;
				case MultiPlayerMessageType.CraftData:
					_multiPlayer.Catalog.HandleCraftData(peer, packet);
					break;
				case MultiPlayerMessageType.State:
					_multiPlayer.Router.OnState(packet);
					break;
				case MultiPlayerMessageType.Pause:
					_multiPlayer.Router.OnPause(packet);
					break;
				case MultiPlayerMessageType.Ping:
					// 收到 Ping：回 Pong 并回显对方时间戳，供对方计算 RTT（延迟）
				{
					long tick;
					if (MultiPlayerMessages.TryDecodePing(packet, out tick)) _multiPlayer.Transport.SendTo(peer, MultiPlayerMessages.EncodePong(tick));
					break;
				}
				case MultiPlayerMessageType.Pong:
					_multiPlayer.Router.OnPong(peer, packet);
					break;
				case MultiPlayerMessageType.Kick:
					_multiPlayer.Router.OnKick(packet);
					break;
				case MultiPlayerMessageType.CraftDataAck:
					_multiPlayer.Catalog.HandleCraftDataAck(packet);
					break;
				case MultiPlayerMessageType.PlayerJoinAck:
					_multiPlayer.Catalog.HandlePlayerJoinAck(peer, packet);
					break;
				case MultiPlayerMessageType.CraftXmlRequest:
					_multiPlayer.Catalog.HandleCraftXmlRequest(peer, packet);
					break;
				case MultiPlayerMessageType.CraftXmlResponse:
					_multiPlayer.Catalog.HandleCraftXmlResponse(packet);
					break;
				case MultiPlayerMessageType.TickRate:
					_multiPlayer.Router.OnTickRate(packet);
					break;
			}
		}

		internal void OnHello(MultiPlayerPeer peer, byte[] packet)
		{
			if (!_multiPlayer.IsServer) return;
			string name;
			if (!MultiPlayerMessages.TryDecodeHello(packet, out name)) return;
			peer.PlayerName = name;
			peer.IsServer = false;

			// 分配 PlayerId（房主为 0，后续从 1 开始）
			// 注意：此时还不知道加入者飞船的 NodeId，
			// 需要等加入者进入飞行场景后通过 CraftData 消息上报（见 OnCraftData）。
			peer.PlayerId = _multiPlayer.Registry.NextPlayerId();
			// 立即登记：让房主玩家表在 CraftData 到达前就有该玩家，
			// 否则收到其 State 包时找不到玩家/飞船（"state for player x but no craft info to spawn"）。
			// NodeId/CraftXml 仍为 -1/空，后续由 OnCraftData 更新。
			_multiPlayer.Registry.RegisterPlayer(peer);

			// 回复 Welcome
			_multiPlayer.Transport.SendTo(peer, MultiPlayerMessages.EncodeWelcome(peer.PlayerId, -1, DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond));
			// 同步当前状态包频率给新加入者（SP2 ServerTickRate 初始同步思路）
			_multiPlayer.Transport.SendTo(peer, MultiPlayerMessages.EncodeTickRate(_multiPlayer.TickRate));
			Mod.LogLobby("MultiPlayer.OnHello (host): '" + name + "' from " + peer.Id + " joined as PlayerId=" + peer.PlayerId +
				", sent Welcome + TickRate(" + _multiPlayer.TickRate + "Hz), total peers=" + _multiPlayer.Transport.GetPeersCount());

			// 把当前所有已登记玩家（含房主自己）的飞船信息同步给新加入者
			// SP2 方案：只发 hash，XML 由客户端按需下载（见 OnPlayerJoin / CraftXmlRequest）。
			foreach (MultiPlayerPeer p in _multiPlayer.Registry.GetPlayers())
			{
				if (p.NodeId >= 0 && !string.IsNullOrEmpty(p.CraftXml))
				{
					string hash = MultiPlayerMessages.ComputeXmlHash(p.CraftXml);
					_multiPlayer.Transport.SendTo(peer, MultiPlayerMessages.EncodePlayerJoin(p.PlayerId, p.NodeId, p.PlayerName, hash));
					Mod.LogLobby("MultiPlayer.OnHello (host): sent existing player " + p.PlayerId + " craft hash to new client (hash=" + hash + ")");
				}
			}
			if (_multiPlayer.LocalNodeId >= 0 && !string.IsNullOrEmpty(_multiPlayer.Catalog._localCraftXml))
			{
				string hostHash = MultiPlayerMessages.ComputeXmlHash(_multiPlayer.Catalog._localCraftXml);
				_multiPlayer.Transport.SendTo(peer, MultiPlayerMessages.EncodePlayerJoin(_multiPlayer.PlayerId, _multiPlayer.LocalNodeId, _multiPlayer.PlayerName, hostHash));
				Mod.LogLobby("MultiPlayer.OnHello (host): sent host craft PlayerJoin (nodeId=" + _multiPlayer.LocalNodeId + ", hash=" + hostHash + ") to new client " + peer.Id);
				// 登记待确认：客户端回 PlayerJoinAck 前，每 1.5s 重发 host craft（防公网丢包）。
				_multiPlayer.Catalog._hostCraftResend[peer.Id] = Time.unscaledTime;
			}
		}

		internal void OnWelcome(MultiPlayerPeer peer, byte[] packet)
		{
			if (_multiPlayer.IsServer) return;
			int playerId, nodeId; long serverTick;
			if (!MultiPlayerMessages.TryDecodeWelcome(packet, out playerId, out nodeId, out serverTick)) return;
			_multiPlayer.PlayerId = playerId;
			// 修复：不要把 peer(房主连接) 的 PlayerId 设成客户端自己的 ID。
			// 房主 peer 的 PlayerId 应保持 0（房主身份），否则超时日志/寻址会错乱
			// （此前把 host peer 的 PlayerId 覆盖成客户端 ID，导致超时日志显示 PlayerId=1 等错乱）。
			peer.IsServer = true;
			// client 成功加入房间：FlightUI 提示；并记录加入时刻，宽限期内不提示"已存在玩家"
			_multiPlayer.OnWelcomeReceived();
			string myName = string.IsNullOrEmpty(_multiPlayer.PlayerName) ? ("Player " + playerId) : _multiPlayer.PlayerName;
			NetworkManager.ShowFlightMessage(Locale.GetString("MultiPlayer.MultiPlayerUI.ConnectedToHost", myName, playerId));
			Mod.LogLobby("MultiPlayer.OnWelcome (client): received Welcome, PlayerId=" + playerId +
				", nodeId=" + nodeId + ", serverTick=" + serverTick + ", peer=" + peer.Id +
				", peer.PlayerId=" + peer.PlayerId + " (host peer, kept as-is)");
		}

		internal void OnPlayerJoin(MultiPlayerPeer peer, byte[] packet)
		{
			int playerId, nodeId; string playerName, craftXmlHash;
			if (!MultiPlayerMessages.TryDecodePlayerJoin(packet, out playerId, out nodeId, out playerName, out craftXmlHash)) return;
			// 自己无需下载自己的飞船（房主广播时会带上报者本人，此处跳过）
			if (playerId == _multiPlayer.PlayerId) return;

			MultiPlayerPeer p = new MultiPlayerPeer { EndPoint = peer.EndPoint, SteamId = peer.SteamId, PlayerId = playerId, NodeId = nodeId, PlayerName = playerName };
			_multiPlayer.Registry.RegisterPlayer(p);
			Mod.LogLobby("MultiPlayer.OnPlayerJoin: playerId=" + playerId + ", nodeId=" + nodeId +
				", hash=" + (craftXmlHash ?? "null") + ", peer=" + peer.Id +
				", isServer=" + _multiPlayer.IsServer + ", inFlightScene=" + (FlightSceneScript.Instance != null));

			// 客户端回 PlayerJoinAck：告知房主已收到该玩家飞船信息，房主据此停止重发（防公网丢包）。
			if (!_multiPlayer.IsServer && peer != null)
			{
				_multiPlayer.Transport.SendTo(peer, MultiPlayerMessages.EncodePlayerJoinAck(playerId));
			}

			// SP2 按需下载：本地缓存命中直接使用；否则向房主请求该玩家飞船 XML。
			if (!_multiPlayer.IsServer)
			{
				if (string.IsNullOrEmpty(craftXmlHash))
				{
					Mod.Log("MultiPlayer.OnPlayerJoin: player " + playerId + " has no craft hash, nothing to download");
					return;
				}
				if (_multiPlayer.Catalog._xmlCache.TryGetValue(craftXmlHash, out string cachedXml))
				{
					p.CraftXml = cachedXml;
					Mod.LogLobby("MultiPlayer.OnPlayerJoin: cache hit for player " + playerId + " (hash=" + craftXmlHash + ", xmlLen=" + cachedXml.Length + ")");
					_multiPlayer.RaisePlayerJoined(p);
				}
				else
				{
					// 若已在请求且 hash 未变，不重复请求；hash 变化（飞船更新）则重新请求。
					string pendingHash;
					bool alreadyPending = _multiPlayer.Catalog._pendingXmlRequests.TryGetValue(playerId, out pendingHash) && pendingHash == craftXmlHash;
					if (!alreadyPending)
					{
						_multiPlayer.Catalog._pendingXmlRequests[playerId] = craftXmlHash;
						_multiPlayer.Transport.SendTo(peer, MultiPlayerMessages.EncodeCraftXmlRequest(playerId, craftXmlHash));
						Mod.LogLobby("MultiPlayer.OnPlayerJoin: requested craft xml for player " + playerId + " (hash=" + craftXmlHash + ")");
					}
				}
			}
			else
			{
				_multiPlayer.RaisePlayerJoined(p);
			}
			// M2：根据 craftXml 生成远程飞船（xml 到位后由 OnCraftXmlResponse 触发 OnPlayerJoined）
		}

		internal void OnPlayerLeave(byte[] packet)
		{
			int playerId;
			if (!MultiPlayerMessages.TryDecodePlayerLeave(packet, out playerId)) return;
			MultiPlayerPeer removed = null;
			lock (_multiPlayer.Registry._playersByPlayerId)
			{
				if (_multiPlayer.Registry._playersByPlayerId.TryGetValue(playerId, out removed))
				{
					_multiPlayer.Registry._playersByPlayerId.Remove(playerId);
				}
			}
			Mod.LogLobby("MultiPlayer.OnPlayerLeave: playerId=" + playerId + (removed != null ? " removed" : " (not found)"));
			_multiPlayer.Catalog._pendingXmlRequests.Remove(playerId);
			if (removed != null) _multiPlayer.RaisePlayerLeft(removed);
		}

		internal void OnState(byte[] packet)
		{
			int playerId, nodeId; double time; Mod.RemoteDataPack data;
			if (!MultiPlayerMessages.TryDecodeState(packet, out playerId, out nodeId, out time, out data)) return;
			if (playerId == _multiPlayer.PlayerId) return; // 忽略本机状态回显
			_multiPlayer.RaiseRemoteState(playerId, nodeId, time, data);

			// 主机中继：房主把状态转发给其他所有客户端
			if (_multiPlayer.IsServer)
			{
				_multiPlayer.Transport.Broadcast(packet);
			}
		}

		internal void OnPause(byte[] packet)
		{
			// 临时禁用：暂停/恢复同步与本次"暂停相关问题"冲突，M2 验证阶段暂时关闭，
			// 待定位清楚后再恢复（避免一端暂停导致另一端状态/时间错乱）。
			// bool paused;
			// if (!MultiPlayerMessages.TryDecodePause(packet, out paused)) return;
			// if (FlightSceneScript.Instance != null)
			// {
			// 	FlightSceneScript.Instance.TimeManager.RequestPauseChange(paused, false);
			// }
		}

		/// <summary>
		/// 客户端收到房主的 TickRate（状态包频率）：本地采纳该频率，保证双端发包节奏一致，
		/// 平滑插帧（P1）与渲染延迟据此自动校准。
		/// </summary>
		internal void OnTickRate(byte[] packet)
		{
			int hz;
			if (!MultiPlayerMessages.TryDecodeTickRate(packet, out hz)) return;
			_multiPlayer.SetTickRate(hz);
		}

		/// <summary>
		/// 收到 Pong：回显的时间戳即对方收到我们 Ping 的时刻。
		/// - 房主：对每个客户端测量 RTT 存入 peer.PingMs（供房主玩家列表显示延迟）；
		/// - 客户端：测量自己到房主的 RTT 存入 _multiPlayer.ClientPingMs（供客户端显示自己延迟）。
		/// </summary>
		internal void OnPong(MultiPlayerPeer peer, byte[] packet)
		{
			long tick;
			if (!MultiPlayerMessages.TryDecodePong(packet, out tick) || tick <= 0) return;
			long rttMs = (DateTime.UtcNow.Ticks - tick) / TimeSpan.TicksPerMillisecond;
			if (rttMs < 0) rttMs = 0;
			if (_multiPlayer.IsServer)
			{
				// 轻微平滑，避免显示跳动
				peer.PingMs = peer.PingMs < 0 ? (int)rttMs : (int)(peer.PingMs * 0.7 + rttMs * 0.3);
				// 同步到该玩家的 RemoteCraft:单向延迟 ≈ RTT/2(幽灵连续外推用,修正 gapEMA 低估真实网络延迟的问题)
				if (_multiPlayer.Crafts._remoteCrafts.TryGetValue(peer.PlayerId, out RemoteCraft rc))
				{
					rc.LatencyMs = peer.PingMs / 2f;
				}
			}
			else
			{
				_multiPlayer.ClientPingMs = _multiPlayer.ClientPingMs < 0 ? (int)rttMs : (int)(_multiPlayer.ClientPingMs * 0.7 + rttMs * 0.3);
				// 客户端所有远端飞船都经房主转发,单向延迟 ≈ 自己到房主 RTT/2(缺少对端→房主一段,近似处理)
				foreach (RemoteCraft rc in _multiPlayer.Crafts._remoteCrafts.Values) rc.LatencyMs = _multiPlayer.ClientPingMs / 2f;
			}
		}

		/// <summary>客户端被房主踢出：提示后停止联机会话。</summary>
		internal void OnKick(byte[] packet)
		{
			if (_multiPlayer.IsServer) return; // 房主不会收到 Kick
			if (!MultiPlayerMessages.TryDecodeKick(packet)) return;
			Mod.LogLobby("MultiPlayer.OnKick: kicked by host");
			try
			{
				global::ModApi.Ui.MessageDialogScript msg = Game.Instance.UserInterface.CreateMessageDialog(global::ModApi.Ui.MessageDialogType.Okay, null, true);
				msg.MessageText = Locale.GetString("MultiPlayer.MultiPlayerUI.Kicked");
			}
			catch (Exception e)
			{
				Mod.LogLobby("MultiPlayer.OnKick: failed to show dialog: " + e.Message);
			}
			_multiPlayer.Stop();
		}
	}
}
