using System;
using System.Collections;
using System.Collections.Generic;
using System.Xml.Linq;
using Assets.Scripts.Flight;
using Assets.Scripts.Flight.Sim;
using ModApi;
using ModApi.Craft;
using ModApi.Craft.Parts;
using ModApi.Flight.Sim;
using ModApi.Flight.GameView;
using ModApi.State;
using UnityEngine;
using static Assets.Scripts.Net.Sync.GhostPoseWriter;
using Assets.Scripts.Net.CraftVisual;
using Assets.Scripts.Net.Session;

namespace Assets.Scripts.Net.Sync
{
	/// <summary>
	/// 幽灵(远程)飞船生命周期(2026-09-22 重构:自 MultiPlayerNetworkManager 逐字搬来):登记表、异步预加载生成协程、
	/// 加载进度框、懒初始化幻影模式、移除/可见性强制、场景加载清理。
	/// </summary>
	internal class RemoteCraftManager
	{
		private readonly NetworkManager _multiPlayer;

		internal RemoteCraftManager(NetworkManager multiPlayer) { _multiPlayer = multiPlayer; }

		internal readonly Dictionary<int, RemoteCraft> _remoteCrafts = new Dictionary<int, RemoteCraft>();
		private readonly HashSet<int> _spawnMissLogged = new HashSet<int>();
		private readonly Dictionary<int, float> _spawnAttemptTime = new Dictionary<int, float>(); // 生成尝试节流

		// SP2 异步 prefab 预加载（plans/PLAN_AsyncPrefabPreload.md）：
		// 预加载期间状态包只刷新"最新状态"，不再重复起生成协程；玩家离开/场景切换时清理进度框与挂起状态。
		/// <summary>正在预加载/生成远程飞船的玩家集合（防止状态包在预加载期间重复起协程）。</summary>
		private readonly HashSet<int> _pendingSpawns = new HashSet<int>();
		/// <summary>预加载期间收到的最新状态包（生成时用最新位置，减少长时间预加载后的跳变）。</summary>
		private readonly Dictionary<int, Mod.RemoteDataPack> _pendingSpawnLatest = new Dictionary<int, Mod.RemoteDataPack>();
		/// <summary>玩家 -> 加载进度框（玩家离开/场景切换/停止时销毁，防残留）。</summary>
		private readonly Dictionary<int, MultiPlayerCraftLoadingIndicator> _loadingIndicators = new Dictionary<int, MultiPlayerCraftLoadingIndicator>();
		/// <summary>玩家 -> 预加载进度（0..1；供 MultiPlayerUI 玩家列表显示 "⏳ N%"）。</summary>
		private readonly Dictionary<int, float> _playerLoadProgress = new Dictionary<int, float>();

		/// <summary>
		/// 判断某个 CraftNode 是否为"幽灵(远程)飞船"。
		/// 供 Harmony patch(JetEngineGhostPatch)在游戏飞行循环回调里快速判定:幽灵航发的
		/// IFlightFixedUpdate/IFlightUpdate 需跳过,尾焰改由 EngineVisualSync 直接驱动。
		/// </summary>
		internal bool IsRemoteCraftNode(CraftNode node)
		{
			if (node == null) return false;
			foreach (KeyValuePair<int, RemoteCraft> kv in _remoteCrafts)
			{
				if (kv.Value.Node == node) return true;
			}
			return false;
		}

		internal void HandlePlayerLeft(MultiPlayerPeer peer)
		{
			RemoveRemoteCraft(peer.PlayerId);
		}

		/// <summary>
		/// 用远程玩家的首个（或预加载期间最新）状态包位置生成其远程飞船（幻影模式）。
		/// 飞船一出现就在远程玩家的真实位置，而不是先出现在本机玩家身上。
		/// CraftData / LaunchLocation / XML 由 SpawnRemoteCraftCoroutine 预加载前构建好（主 prefab 已热缓存）。
		/// </summary>
		internal void SpawnRemoteCraftAtPosition(MultiPlayerPeer peer, Mod.RemoteDataPack data, CraftData craftData, LaunchLocation location, XElement xml)
		{
			try
			{
				if (peer.PlayerId == _multiPlayer.PlayerId) return;                 // 自己
				if (_remoteCrafts.ContainsKey(peer.PlayerId)) return;  // 已生成
				if (FlightSceneScript.Instance == null) return;        // 不在飞行场景
				if (craftData == null || location == null || xml == null) return;

				CraftNode localNode = FlightSceneScript.Instance.CraftNode as CraftNode;
				IPlanetNode planet = localNode != null ? localNode.Parent : null;
				if (localNode == null || planet == null) return;

				CraftNode remote = FlightSceneScript.Instance.SpawnCraft(peer.PlayerName+"|"+craftData.Name, craftData, location, xml);
				if (remote == null)
				{
					Mod.LogError("SpawnRemoteCraftAtPosition: SpawnCraft returned null for player " + peer.PlayerId);
					return;
				}

				// [朝向诊断|远端P{pid}飞船] 生成时：Heading(行星字段) 应≈ 传入的 spawnHeading(行星空间)。
				//Mod.Log("[朝向诊断|远端P" + peer.PlayerId + "飞船] 生成: Heading(行星)=" + Q(remote.Heading) +
					//" | 传入spawnHeading(行星)=" + Q(spawnHeading));

				// 先登记（无论 CraftScript 是否已构建），避免状态包反复触发重新生成；
				// 并把首个状态包入插值缓冲（BufferCount=1，UpdateRemoteCrafts 直接应用）。
				RemoteCraft rc = new RemoteCraft { PlayerId = peer.PlayerId, PlayerName = peer.PlayerName, Node = remote };
				rc.PushSample(Time.unscaledTime, 0, data);
				_remoteCrafts[peer.PlayerId] = rc;

				// 立即进入"表面锁定"分支（防止游戏推进轨道导致坠落），并预置 GroundedSurface* 避免空引用
				rc.Node.InContactWithPlanet = true;
				IPlanetNode remotePlanet = remote.Parent != null ? remote.Parent : planet;
				if (remotePlanet != null)
				{
					ApplyRemoteGroundedSurface(rc, data, remotePlanet);
				}
				// 生成后立即用状态包位置/速度/朝向覆盖逻辑状态并刷新 Transform：
				// 否则 SurfaceLockedGround 会先把飞船放到地面上（AGL≈0），下一帧才被拉回正确高度，
				// 出现"生成瞬间贴地/掉进地下"。
				// CraftScript 尚未构建时该方法内部会安全跳过，待 InitializeRemoteCraft 补齐。
				ApplyRemoteState(rc, data);

				// 朝向诊断：核对 数据包heading / SpawnCraft 后 Heading / 视觉 Transform.rotation
				// 朝向诊断日志已暂时禁用（朝向已修复）
				//if (remote.CraftScript != null)
				//{
				//	Quaternion spawnRot = remote.CraftScript.Transform.rotation;
				//	Mod.Log("MultiPlayer headingDiag spawn p" + peer.PlayerId + ": dataHeading=(" +
				//		data.Heading.x.ToString("F3") + "," + data.Heading.y.ToString("F3") + "," + data.Heading.z.ToString("F3") + "," + data.Heading.w.ToString("F3") + ")" +
				//		", spawnHeading=(" + remote.Heading.x.ToString("F3") + "," + remote.Heading.y.ToString("F3") + "," + remote.Heading.z.ToString("F3") + "," + remote.Heading.w.ToString("F3") + ")" +
				//		", spawnRot=(" + spawnRot.x.ToString("F3") + "," + spawnRot.y.ToString("F3") + "," + spawnRot.z.ToString("F3") + "," + spawnRot.w.ToString("F3") + ")");
				//}

				// 幻影模式 + 初始朝向：CraftScript 可能延迟构建，在 UpdateRemoteCrafts 里懒初始化（见 InitializeRemoteCraft）
				Mod.LogLobby("MultiPlayer: spawned remote craft for player " + peer.PlayerId + " at remote position (nodeId=" + peer.NodeId + ", localNode=" + remote.NodeId + ")" +
					", surfacePos=(" + data.Position.x.ToString("F1") + "," + data.Position.y.ToString("F1") + "," + data.Position.z.ToString("F1") + ")" +
					", heading=(" + data.Heading.x.ToString("F3") + "," + data.Heading.y.ToString("F3") + "," + data.Heading.z.ToString("F3") + "," + data.Heading.w.ToString("F3") + ")");

				// 诊断：生成后立即记录远程飞船的可见性/渲染状态，用于定位"无法显示对方 craft"
				try
				{
					GameObject rgo = remote.GameObject;
					int rendererCount = 0, enabledCount = 0;
					if (rgo != null)
					{
						// 1.4.2:body 脱离 craft 层级后 GetComponentsInChildren 遍历不到 body 上的渲染器,
						// 改用逐 body 遍历(GetComponentsInCraft,等价游戏新 API)。
						List<Renderer> renderers = new List<Renderer>();
						CraftUtils.GetComponentsInCraft(remote, renderers, true);
						foreach (Renderer r in renderers) { rendererCount++; if (r.enabled) enabledCount++; }
					}
					Mod.LogLobby("MultiPlayer spawnDiag p" + peer.PlayerId + ": goActive=" + (rgo != null ? rgo.activeSelf.ToString() : "null") +
						", craftScript=" + (remote.CraftScript != null ? "built" : "notBuilt") +
						", renderers=" + rendererCount + "/enabled=" + enabledCount +
						", inFlightState=" + IsNodeInFlightState(remote) +
						", isPlayer=" + remote.IsPlayer +
						", isLoadedInGameView=" + remote.IsLoadedInGameView);
				}
				catch (Exception e) { Mod.LogError("MultiPlayer spawnDiag error: " + e.Message); }
			}
			catch (Exception e)
			{
				Mod.LogError("SpawnRemoteCraftAtPosition FAILED (player " + peer.PlayerId + "): " + e.Message);
			}
		}

		internal void RemoveRemoteCraft(int playerId)
		{
			_spawnMissLogged.Remove(playerId);
			_spawnAttemptTime.Remove(playerId);
			// 玩家离开/踢出：若正在预加载（飞船尚未生成），销毁其加载进度框并结束挂起生成，防残留
			DestroyLoadingIndicator(playerId);
			EndSpawnAttempt(playerId);
			RemoteCraft rc;
			if (!_remoteCrafts.TryGetValue(playerId, out rc))
			{
				Mod.LogLobby("MultiPlayer.RemoveRemoteCraft: player " + playerId + " not in _remoteCrafts (nothing to remove)");
				return;
			}
			_remoteCrafts.Remove(playerId);
			if (rc.Node != null)
			{
				// 诊断：记录移除前飞船节点状态，用于定位 MapView NRE（MapCraft.transform 为 null 但仍在 registry）
				bool inFlightState = false;
				string goActive = "null";
				try
				{
					if (FlightSceneScript.Instance != null && FlightSceneScript.Instance.FlightState != null)
					{
						foreach (CraftNode cn in FlightSceneScript.Instance.FlightState.CraftNodes)
						{
							if (cn == rc.Node) { inFlightState = true; break; }
						}
					}
					if (rc.Node.GameObject != null) goActive = rc.Node.GameObject.activeSelf.ToString();
				}
				catch (Exception e) { Mod.LogError("RemoveRemoteCraft: check FlightState error: " + e.Message); }
				Mod.LogLobby("MultiPlayer: destroyed remote craft for player " + playerId +
					", nodeId=" + rc.Node.NodeId +
					", inFlightState=" + inFlightState +
					", goActive=" + goActive +
					", craftScriptNull=" + (rc.Node.CraftScript == null) +
					", isDestroyed=" + rc.Node.IsDestroyed);
				// 真正销毁远程飞船，不再用 SetActive(false) 隐藏（原机制会留下残影/僵尸飞船且不释放资源）：
				// DestroyCraft() 置 IsDestroyed=true 并触发 Destroyed 事件；
				// 游戏 FlightSceneScript.FlightLateUpdate 每帧调用 FlightState.ProcessDestroyedCraftNodes()
				// 从 FlightState 移除该节点、注销其数据并触发 CraftNodeRemoved，资源彻底释放。
				try
				{
					rc.Node.DestroyCraft();
				}
				catch (Exception e)
				{
					Mod.LogError("RemoveRemoteCraft: DestroyCraft error: " + e.Message);
				}
			}
			else
			{
				Mod.LogLobby("MultiPlayer.RemoveRemoteCraft: player " + playerId + " rc.Node=null");
			}
		}

		/// <summary>
		/// 强制恢复远程飞船的视觉：游戏原生机制可能对"非活动/幽灵"飞船禁用 Renderer 或
		/// 停用 GameObject（实测靠近本机飞船时视觉模型消失但 CraftNode 仍在）。
		/// 每帧强制执行（不再节流），因为游戏可能在每帧都禁用幽灵飞船的 Renderer/GameObject，
		/// 尤其在真实远程联机（高延迟）场景下更为激进。
		/// </summary>
		internal void EnforceRemoteCraftVisuals()
		{
			if (_remoteCrafts.Count == 0) return;
			// 每帧强制执行（不再节流 0.5s），防止游戏在帧间禁用幽灵飞船视觉
			foreach (RemoteCraft rc in _remoteCrafts.Values)
			{
				if (rc.Node == null || rc.Node.GameObject == null) continue;
				GameObject go = rc.Node.GameObject;
				try
				{
					if (!go.activeSelf)
					{
						go.SetActive(true);
						Mod.LogLobby("MultiPlayer: re-activated remote craft GameObject for player " + rc.PlayerId);
					}
					// 1.4.2:body 脱离 craft 层级后 GetComponentsInChildren 遍历不到 body 上的渲染器,
					// 改用逐 body 遍历(GetComponentsInCraft,等价游戏新 API)。复用缓冲防每帧 GC。
					CraftUtils.GetComponentsInCraft(rc.Node, rc.ReuseRenderers, true);
					foreach (Renderer r in rc.ReuseRenderers)
					{
						if (!r.enabled)
						{
							r.enabled = true;
						}
					}
				}
				catch (Exception e)
				{
					Mod.LogError("EnforceRemoteCraftVisuals error (player " + rc.PlayerId + "): " + e.Message);
				}
			}
		}

		/// <summary>
		/// 收到远程状态包：若该玩家远程飞船尚未生成，则用首个状态包的真实位置生成；
		/// 否则更新插值目标。
		/// </summary>
		internal void HandleRemoteState(int playerId, int nodeId, double time, Mod.RemoteDataPack data)
		{
			RemoteCraft rc;
			if (!_remoteCrafts.TryGetValue(playerId, out rc) || rc.Node == null)
			{
				// 尚未生成：用首个状态包的位置生成远程飞船
				MultiPlayerPeer peer = null;
				lock (_multiPlayer.Registry._playersByPlayerId) { _multiPlayer.Registry._playersByPlayerId.TryGetValue(playerId, out peer); }
				if (peer == null || string.IsNullOrEmpty(peer.CraftXml))
				{
					if (_spawnMissLogged.Add(playerId))
					{
						Mod.Log("MultiPlayer: state for player " + playerId + " but no craft info to spawn");
					}
					return;
				}

				// 已有生成协程在预加载：只刷新"最新状态包"（生成时用最新位置），不再重复起协程
				// （否则预加载数秒期间每个状态包都会再起一个协程，重复预加载同一飞船）。
				if (_pendingSpawns.Contains(playerId))
				{
					_pendingSpawnLatest[playerId] = data;
					return;
				}

				// 生成尝试节流：状态包 20Hz 到达，失败时最多每 2 秒重试一次，避免刷屏
				float last;
				if (_spawnAttemptTime.TryGetValue(playerId, out last) && Time.unscaledTime - last < 2f) return;
				_spawnAttemptTime[playerId] = Time.unscaledTime;

				// 异步预加载 + 生成：SpawnCraft（LoadCraftImmediate + 实例化全部部件/渲染器）在
				// 大飞船时可能阻塞主线程数秒（白屏），且期间不读网络导致对端写阻塞（"卡到无响应"）。
				// 改为协程：先让本帧网络处理完（回 Ack/收状态包）→ 解析 XML/构建 CraftData（纯数据）→
				// 异步预加载部件 prefab（真实百分比加载框）→ 主 prefab 已热缓存后 SpawnCraft（只剩纯 Instantiate，快）。
				_pendingSpawns.Add(playerId);
				_multiPlayer.StartCoroutine(SpawnRemoteCraftCoroutine(peer, data));
				return;
			}

			// 平滑插帧：状态包入环形缓冲（按到达端 unscaledTime，暂停安全）。
			// 首个包也入缓冲（BufferCount=1 时下一帧直接应用），后续由 UpdateRemoteCrafts 取前后两包插值。
			rc.PushSample(Time.unscaledTime, time, data);
		}

		/// <summary>
		/// 协程异步生成远程飞船（SP2 异步预加载链路）：
		/// 先让网络处理几帧（DrainIncoming/回 Ack/状态包）→ 解析 XML + 构建 CraftData（纯数据，快）→
		/// 创建加载进度框（对方位置上方，真实百分比）→ 异步预加载部件 prefab（逐帧）→
		/// 主 prefab 已热缓存后 SpawnCraft（快，无秒级白屏）→ 走现有登记/表面锁定/幻影模式逻辑。
		/// </summary>
		internal IEnumerator SpawnRemoteCraftCoroutine(MultiPlayerPeer peer, Mod.RemoteDataPack data)
		{
			// 先跑完当前帧网络处理，再等 2 帧，让握手/回 Ack/状态包有充足时间流动
			yield return null;
			yield return null;

			// 协程延迟期间可能发生：离开飞行场景 / 玩家已离开 / 已被其他路径生成 / 飞船信息失效
			if (!IsSpawnAttemptStillValid(peer)) { EndSpawnAttempt(peer.PlayerId); yield break; }

			// 帧 A/B：解析 XML + 构建 CraftData（纯数据、不实例化 GameObject，快）
			XElement xml = null;
			CraftData craftData = null;
			try
			{
				xml = XElement.Parse(peer.CraftXml);
				craftData = Game.Instance.CraftLoader.LoadCraftImmediate(xml);
			}
			catch (Exception e)
			{
				Mod.LogError("SpawnRemoteCraftCoroutine: craft data load failed (player " + peer.PlayerId + "): " + e.Message);
				EndSpawnAttempt(peer.PlayerId);
				yield break;
			}
			if (xml == null || craftData == null || craftData.Assembly == null)
			{
				Mod.LogError("SpawnRemoteCraftCoroutine: craft data null for player " + peer.PlayerId);
				EndSpawnAttempt(peer.PlayerId);
				yield break;
			}
			if (!IsSpawnAttemptStillValid(peer)) { EndSpawnAttempt(peer.PlayerId); yield break; }

			// 发射点：状态包是地面坐标，用本端行星自转转成惯性坐标生成（与 SpawnRemoteCraftAtPosition 原逻辑一致）
			CraftNode localNode = FlightSceneScript.Instance.CraftNode as CraftNode;
			IPlanetNode planet = localNode != null ? localNode.Parent : null;
			if (localNode == null || planet == null) { EndSpawnAttempt(peer.PlayerId); yield break; }
			Vector3d planetPos = planet.SurfaceVectorToPlanetVector(data.Position);
			// 与 ApplyRemoteState 一致:data.Velocity 是地表相对速度,生成初始惯性速度需加回行星自转线速度。
			Vector3d planetVel = planet.SurfaceVectorToPlanetVector(data.Velocity) +
				planet.SurfaceVectorToPlanetVector(planet.CalculateSurfaceVelocity(data.Position));
			// data.Heading 已是"行星空间"朝向(发送端采样时已 FrameToPlanet)。
			// CreateLaunchLocation 内部会做 heading=RotationInverse*heading(把入参当行星空间)，
			// 再被 SpawnCraft 乘回 planet.Rotation → 最终 Heading=入参(行星空间)，故直接传入即可。
			Quaterniond spawnHeading = data.Heading;
			LaunchLocation location = LaunchLocation.CreateLaunchLocation(
				"MultiPlayer_Remote_" + peer.PlayerId,
				planet, planetPos, planetVel, spawnHeading,
				localNode.ReferenceFrame,
				LaunchLocationType.SurfaceLockedGround);

			// 创建加载进度框：挂到对方位置上方（旋转白框 + 真实百分比）
			MultiPlayerCraftLoadingIndicator indicator = CreateLoadingIndicator(peer.PlayerId, localNode.ReferenceFrame, planetPos);
			if (indicator == null) { EndSpawnAttempt(peer.PlayerId); yield break; }

			// 异步预加载部件 prefab（逐帧、真实 %）→ 进度框显示 N%；可随时取消（玩家离开/场景切换）
			int partCount = craftData.Assembly.Parts != null ? craftData.Assembly.Parts.Count : 0;
			Mod.LogLobby("MultiPlayer: async preloading craft prefabs for player " + peer.PlayerId +
				" ('" + peer.PlayerName + "', craft='" + craftData.Name + "', parts=" + partCount + ")");
			yield return MultiPlayerCraftPreloader.PreloadCraftPrefabs(craftData,
				progress => SetPlayerLoadProgress(peer.PlayerId, progress),
				() => !IsSpawnAttemptStillValid(peer));

			// 预加载后：玩家可能已离开 / 场景切换 / 已被其他路径生成
			if (!IsSpawnAttemptStillValid(peer))
			{
				DestroyLoadingIndicator(peer.PlayerId);
				EndSpawnAttempt(peer.PlayerId);
				yield break;
			}

			// 用预加载期间的最新状态包（长时间预加载后位置更准，减少跳变）
			Mod.RemoteDataPack spawnData = data;
			if (_pendingSpawnLatest.TryGetValue(peer.PlayerId, out Mod.RemoteDataPack latest)) spawnData = latest;

			// 清理进度框 + 挂起标记。SpawnRemoteCraftAtPosition 内同步登记 _remoteCrafts（无 yield），
			// 移除挂起标记到登记之间没有网络帧插入，不会重复起协程。
			DestroyLoadingIndicator(peer.PlayerId);
			EndSpawnAttempt(peer.PlayerId);

			SpawnRemoteCraftAtPosition(peer, spawnData, craftData, location, xml);
		}

		/// <summary>
		/// 校验生成协程当前是否仍有效：仍在飞行场景、玩家未离开、飞船信息仍在、且尚未被生成。
		/// （预加载期间每帧调用；玩家离开/场景切换/已生成 → false，协程提前停止。）
		/// </summary>
		internal bool IsSpawnAttemptStillValid(MultiPlayerPeer peer)
		{
			if (peer == null || FlightSceneScript.Instance == null) return false;
			if (peer.PlayerId == _multiPlayer.PlayerId) return false;
			bool stillThere;
			lock (_multiPlayer.Registry._playersByPlayerId) { stillThere = _multiPlayer.Registry._playersByPlayerId.ContainsKey(peer.PlayerId); }
			if (!stillThere) return false;
			if (_remoteCrafts.ContainsKey(peer.PlayerId)) return false;
			return !string.IsNullOrEmpty(peer.CraftXml);
		}

		/// <summary>生成协程退出时清理挂起状态（挂起标记、最新状态缓存、加载进度显示）。</summary>
		internal void EndSpawnAttempt(int playerId)
		{
			_pendingSpawns.Remove(playerId);
			_pendingSpawnLatest.Remove(playerId);
			_playerLoadProgress.Remove(playerId);
		}

		/// <summary>取消并清理所有挂起的生成（停止联机 / 场景切换时调用）：销毁进度框 + 清空挂起状态。</summary>
		internal void CancelPendingSpawns()
		{
			foreach (KeyValuePair<int, MultiPlayerCraftLoadingIndicator> kv in _loadingIndicators)
			{
				if (kv.Value != null) kv.Value.DestroyIndicator();
			}
			_loadingIndicators.Clear();
			_pendingSpawns.Clear();
			_pendingSpawnLatest.Clear();
			_playerLoadProgress.Clear();
		}

		/// <summary>在指定玩家位置上方创建加载进度框并登记（离开/场景切换时可销毁）。</summary>
		internal MultiPlayerCraftLoadingIndicator CreateLoadingIndicator(int playerId, IReferenceFrame frame, Vector3d planetPos)
		{
			try
			{
				Vector3 worldPos = frame != null
					? frame.PlanetToFramePosition(planetPos) + Vector3.up * 4f
					: Vector3.zero;
				MultiPlayerCraftLoadingIndicator ind = MultiPlayerCraftLoadingIndicator.Create(worldPos);
				_loadingIndicators[playerId] = ind;
				return ind;
			}
			catch (Exception e)
			{
				Mod.LogError("CreateLoadingIndicator error (player " + playerId + "): " + e.Message);
				return null;
			}
		}

		/// <summary>销毁并移除指定玩家的加载进度框。</summary>
		internal void DestroyLoadingIndicator(int playerId)
		{
			MultiPlayerCraftLoadingIndicator ind;
			if (_loadingIndicators.TryGetValue(playerId, out ind))
			{
				if (ind != null) ind.DestroyIndicator();
				_loadingIndicators.Remove(playerId);
			}
		}

		/// <summary>记录玩家预加载进度（0..1）；达到 1f 视为完成，移除（玩家列表恢复延迟/状态显示）。</summary>
		internal void SetPlayerLoadProgress(int playerId, float progress)
		{
			if (progress >= 1f) _playerLoadProgress.Remove(playerId);
			else _playerLoadProgress[playerId] = Mathf.Clamp01(progress);
		}

		/// <summary>指定玩家当前预加载进度（0..1）；未在加载返回 null（供 MultiPlayerUI 玩家列表显示 "⏳ N%"）。</summary>
		internal float? GetPlayerLoadProgress(int playerId)
		{
			float p;
			if (_playerLoadProgress.TryGetValue(playerId, out p)) return p;
			return null;
		}

		/// <summary>
		/// 远程飞船懒初始化：等 CraftScript 构建好后应用幻影模式（禁止控制 + 禁用物理），
		/// 并把初始朝向设为首个状态包的 heading（直接赋值）。
		/// </summary>
		internal void InitializeRemoteCraft(RemoteCraft rc)
		{
			if (rc.Node == null || rc.Node.CraftScript == null) return;
			try
			{
				rc.Node.AllowPlayerControl = false;

				// 真正关闭物理（停止重力/碰撞/物理对 Transform 的覆盖）。
				// 只靠 DisableCraftPhysicCalculation 清碰撞箱仍会被重力拉进地下、朝向被物理覆盖。
				// 注意：必须用 PhysicsChangeReason.Warp（而非 UnloadPhysics）！
				// 原因：MapCraft.OnCraftNodePhysicsDisabled 对 UnloadPhysics 会执行
				// MapItem.SwitchType<MapStaticOrbitItem>(this)，销毁 MapCraft GameObject 但 item 仍留在
				// registry，导致 MapView.UpdateMapItems 遍历时对已销毁的 MapCraft 调 transform.position → NRE。
				// Warp 被 IgnorePhysicsChange 视为"忽略"（不切换类型），可避免该 NRE。
				rc.Node.SetPhysicsEnabled(false, PhysicsChangeReason.Warp);
				CraftUtils.DisableCraftPhysicCalculation(ref rc.Node);

				// 强制进入"表面锁定 + 物理禁用"分支：
				// 该分支下游戏每帧会按 GroundedSurface* 放置飞船（见 CraftNode.Update），
				// 而我们会持续更新这些值使其跟随远程状态，避免游戏把飞船拉回出生点/掉进地下。
				rc.Node.InContactWithPlanet = true;

				// 额外把所有刚体设为 kinematic，防止任何残留物理移动（防穿地）
				foreach (BodyData body in rc.Node.CraftScript.Data.Assembly.Bodies)
				{
					if (body.BodyScript != null && body.BodyScript.RigidBody != null)
					{
						body.BodyScript.RigidBody.isKinematic = true;
					}
				}

				// 部件姿态不再在此"恢复"：part.Position/part.Rotation 是相对 craft 根的坐标，
				// 写进 part.localPosition/localRotation（相对 body）会摆错部件导致分裂/朝向偏差。
				// 反编译 PartData.Synchronize 证实该坐标系差异；且幽灵飞船从未启用物理，
				// SetPhysicsEnabled(false) 为 no-op，EnablePhysics(false)/RecenterTransformOnCoM 不会执行，
				// 部件本就被 CraftBuilder 按 XML 正确摆放，无需恢复。
				// body 的动态局部姿态改由状态包 BodyRotations 同步（见 ApplyRemoteState）。
				//Mod.Log("[朝向诊断|远端P" + rc.PlayerId + "飞船] 已跳过部件恢复循环(部件保持XML设计姿态)");

				if (rc.HasState)
				{
					// 初始状态：位置/速度/朝向一次性应用（含 RecalculateFrameState 刷新 Transform）
					if (rc.TryGetNewest(out Mod.RemoteDataPack newest))
					{
						ApplyRemoteState(rc, newest);
					}
				}
				// 幽灵船引擎尾焰:建立驱动表(液体 ExhaustThrottleOverride + 航发直接驱动)+ 抑制烟雾/加热副作用
				EngineVisualSync.SetupGhostEngineVisuals(rc);
				EngineVisualSync.DriveGhostEngineVisuals(rc);
				rc.IsInitialized = true;
				Mod.LogLobby("MultiPlayer: remote craft initialized (ghost mode) for player " + rc.PlayerId);
			}
			catch (Exception e)
			{
				Mod.LogError("InitializeRemoteCraft FAILED (player " + rc.PlayerId + "): " + e.Message);
			}
		}

		/// <summary>诊断辅助：判断指定 CraftNode 是否仍登记在 FlightState.CraftNodes 中。</summary>
		internal static bool IsNodeInFlightState(CraftNode node)
		{
			try
			{
				if (node == null || FlightSceneScript.Instance == null || FlightSceneScript.Instance.FlightState == null) return false;
				foreach (CraftNode cn in FlightSceneScript.Instance.FlightState.CraftNodes)
				{
					if (cn == node) return true;
				}
			}
			catch { }
			return false;
		}

		/// <summary>进入飞行场景时清理上一场景遗留的远程飞船引用(门面 OnFlightSceneLoaded 调用;Vizzy 契约见 plans/README.md §三)。</summary>
		internal void OnFlightSceneLoaded()
		{
			_remoteCrafts.Clear();
			_spawnMissLogged.Clear();
			_spawnAttemptTime.Clear();
			// 场景切换：上一场景的进度框已随场景卸载销毁，清空登记与挂起状态（新场景可正常重新生成）
			CancelPendingSpawns();
			// Vizzy 隔离的幽灵 NodeId 记忆同样按飞行场景生命周期重置：NodeId 只在本次飞行内唯一，
			// 跨场景复用会把新场景里的本地船误判为幽灵（Vizzy 被误杀）。见 VizzyIsolationPatch。
			VizzyIsolationPatch.ClearGhostNodeCache();
			Mod.LogLobby("MultiPlayer.OnFlightSceneLoaded: cleared stale remote crafts (count=" + _remoteCrafts.Count + ")");
		}

		/// <summary>停止联机时清空生成节流/未命中登记(与旧 Stop() 中两行等价)。</summary>
		internal void ClearSpawnTracking() { _spawnMissLogged.Clear(); _spawnAttemptTime.Clear(); }

		/// <summary>停止联机时真正销毁所有远程飞船并清空挂起生成(与旧 Stop() 中等价,避免 Stop 后场景残留幽灵)。</summary>
		internal void DestroyAllRemoteCrafts()
		{
			// 停止联机时真正销毁所有远程飞船（避免 Stop 后场景里残留幽灵飞船），
			// 已销毁/已随场景卸载的节点跳过。
			foreach (RemoteCraft rc in _remoteCrafts.Values)
			{
				if (rc != null && rc.Node != null && !rc.Node.IsDestroyed)
				{
					try { rc.Node.DestroyCraft(); }
					catch (Exception e) { Mod.LogError("MultiPlayer.Stop: DestroyCraft error: " + e.Message); }
				}
			}
			_remoteCrafts.Clear();
			// 清理预加载中的进度框与挂起状态（踢人/断开时无残留）
			CancelPendingSpawns();
		}
	}
}
