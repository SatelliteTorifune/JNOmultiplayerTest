using System;
using Assets.Scripts.Net;
using ModApi;
using ModApi.Scenes.Events;
using UnityEngine;

namespace Assets.Scripts
{
	/// <summary>
	/// 联机房间管理器（独立类）：负责开房 / 加入 / 停止 / 发包频率 / 网络管理器创建 / 场景事件。
	/// 从 Mod 主入口中抽象出来，降低主入口与联机逻辑的耦合。
	/// </summary>
	public class LobbyManager
	{
		public static LobbyManager Instance { get; private set; }

		private GameObject _mpGameObject;

		public LobbyManager()
		{
			Instance = this;
		}

		/// <summary>作为房主开启联机房间。</summary>
		public bool HostLobby(int port = 25555)
		{
			Mod.LogLobby("HostLobby() called: port=" + port);
			MpNetworkManager mgr = EnsureMpManager();
			if (mgr == null)
			{
				Mod.LogLobby("HostLobby FAILED: MpNetworkManager.Instance is null (EnsureMpManager returned null)");
				return false;
			}

			// 保护机制：Steam 传输下必须有本机 SteamId（Steam 未初始化/未登录时为 0），否则无法开房。
			// 非 Steam 传输（如 TCP debug）无 SteamId 概念，跳过此检查。
			// 注意：必须用静态 GetLocalSteamId() 直接查 Steam，不能用实例属性 LocalSteamId——
			// 该属性只在 Transport.Start() 成功后才赋值，开房预检时它恒为 0，会把 Steam 正常的情况误判为未登录。
			if (mgr.Transport is SteamTransport && SteamTransport.GetLocalSteamId() == 0)
			{
				Mod.LogLobby("HostLobby FAILED: SteamId=0 (Steam not initialized or not logged in)");
				ShowHostLobbyError(Locale.GetString("MultiPlayer.Mod.HostLobbyError"));
				return false;
			}

			bool ok = mgr.Host(port);
			Mod.LogLobby("HostLobby() finished: port=" + port + ", result=" + ok +
				", IsServer=" + mgr.IsServer + ", IsConnected=" + mgr.IsConnected +
				", PlayerId=" + mgr.PlayerId + ", LocalNodeId=" + mgr.LocalNodeId +
				", Transport.IsRunning=" + mgr.Transport.IsRunning +
				", LocalPort=" + mgr.Transport.LocalPort +
				", peerCount=" + mgr.Transport.GetPeersCount());
			if (!ok) Mod.LogLobby("HostLobby FAILED: see above for Transport start error (port " + port + " may already be in use)");
			return ok;
		}

		/// <summary>弹出开房失败提示对话框。</summary>
		private void ShowHostLobbyError(string message)
		{
			try
			{
				global::ModApi.Ui.MessageDialogScript msg = Game.Instance.UserInterface.CreateMessageDialog(global::ModApi.Ui.MessageDialogType.Okay, null, true);
				msg.MessageText = message;
			}
			catch (Exception e)
			{
				Mod.LogLobby("ShowHostLobbyError FAILED to show dialog: " + e.Message);
			}
		}

		/// <summary>作为客户端加入房主。</summary>
		public bool JoinLobby(string host, int port = 25555, string playerName = null)
		{
			// 未显式传名时读取 ModSettings 配置的玩家名,避免硬编码 "Player" 覆盖设置值
			if (string.IsNullOrWhiteSpace(playerName))
			{
				try { playerName = ModSettings.Instance.PlayerName.Value; }
				catch { playerName = "Player"; }
				if (string.IsNullOrWhiteSpace(playerName)) playerName = "Player";
			}
			Mod.LogLobby("JoinLobby() called: host=" + host + ":" + port + ", playerName='" + playerName + "'");
			MpNetworkManager mgr = EnsureMpManager();
			if (mgr == null)
			{
				Mod.LogLobby("JoinLobby FAILED: MpNetworkManager.Instance is null (EnsureMpManager returned null)");
				return false;
			}

			bool ok = mgr.Join(host, port, playerName);
			Mod.LogLobby("JoinLobby() finished: host=" + host + ":" + port + ", result=" + ok +
				", IsConnected=" + mgr.IsConnected + ", PlayerId=" + mgr.PlayerId +
				", LocalNodeId=" + mgr.LocalNodeId +
				", Transport.IsRunning=" + mgr.Transport.IsRunning +
				", LocalPort=" + mgr.Transport.LocalPort +
				", peerCount=" + mgr.Transport.GetPeersCount());
			return ok;
		}

		/// <summary>停止联机。</summary>
		public void StopLobby()
		{
			Mod.LogLobby("StopLobby() called" + (MpNetworkManager.Instance != null ? " (manager exists)" : " (manager is null, nothing to stop)"));
			if (MpNetworkManager.Instance != null)
			{
				MpNetworkManager.Instance.Stop();
			}
		}

		/// <summary>
		/// 房主调整状态包发送频率（Hz）的控制台指令实现（SetTickRate <hz>）。
		/// 仅房主设置会广播给所有客户端；客户端调用仅改本端（采纳房主广播值为准）。
		/// </summary>
		public void SetTickRate(int hz)
		{
			MpNetworkManager mgr = EnsureMpManager();
			if (mgr == null)
			{
				Mod.LogLobby("SetTickRate FAILED: MpNetworkManager.Instance is null (EnsureMpManager returned null)");
				return;
			}
			if (!mgr.IsServer)
			{
				Mod.LogLobby("SetTickRate: 仅房主可调整全局发包频率（当前为客户端，本端将采纳房主广播值）");
			}
			mgr.SetTickRate(hz);
		}

		/// <summary>确保联机网络管理器已创建并返回实例。</summary>
		public MpNetworkManager EnsureMpManager()
		{
			if (MpNetworkManager.Instance == null)
			{
				if (_mpGameObject == null) _mpGameObject = new GameObject("MPNetwork");
				// 关键：让管理器跨场景存活。切换全屏/退出菜单等触发场景重载时，
				// 普通场景 GameObject 会被销毁 → OnDestroy → Transport.Stop() 断线 → 远程飞船被移除。
				// DontDestroyOnLoad 保证联机会话在场景切换期间保持连接。
				GameObject.DontDestroyOnLoad(_mpGameObject);
				_mpGameObject.AddComponent<MpNetworkManager>();
				_mpGameObject.SetActive(true);
			}
			return MpNetworkManager.Instance;
		}

		/// <summary>场景加载事件：飞行场景下重建/刷新联机管理器。</summary>
		public void OnSceneLoaded(object sender, SceneEventArgs e)
		{
			if (Game.Instance.SceneManager.InFlightScene)
			{
				// 兜底：若管理器因场景重载被销毁（理论上 DontDestroyOnLoad 后不应发生），在此重建
				if (MpNetworkManager.Instance == null)
				{
					EnsureMpManager();
				}
				if (MpNetworkManager.Instance != null)
				{
					// 清理上一场景遗留的远程飞船引用（旧 CraftNode 已被场景卸载销毁），
					// 再上报/刷新本机飞船 NodeId
					MpNetworkManager.Instance.OnFlightSceneLoaded();
					MpNetworkManager.Instance.RefreshLocalCraft();
				}
			}
		}
	}
}
