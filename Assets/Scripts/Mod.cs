using System;
using System.Collections.Generic;
using Assets.Packages.DevConsole;
using ModApi.Mods;
using ModApi.Scenes.Events;

using Assets.Scripts.Net;

using Jundroo.ModTools;
using UnityEngine;

using HarmonyLib;

namespace Assets.Scripts
{
	public partial class Mod : GameMod
	{
		private Mod()
		{
			
		}

		public static Mod Instance { get; } = GameModBase.GetModInstance<Mod>();
		public GameObject MPGameObject = null;

		public static void Log(object message)
		{
			if (!ModSettings.Instance.DebugMode)
			{
				return;	
			}
			UnityEngine.Debug.Log("[Mptest] " + message);
		}

		public static void LogError(object message)
		{
			if (!ModSettings.Instance.DebugMode)
			{
				return;
			}
			UnityEngine.Debug.LogError("[Mptest] " + message);
		}

		/// <summary>
		/// 联机生命周期日志：不受 DebugMode 限制，始终输出到控制台。
		/// 用于确认 Host/Join/Stop 等关键节点确实执行成功。
		/// </summary>
		public static void LogLobby(object message)
		{
			UnityEngine.Debug.Log("[Mptest][Lobby] " + message);
		}

		protected override void OnModInitialized()
		{
			try
			{
				base.OnModInitialized();
				new Harmony("MPTest").PatchAll();

				RegisterMpCommands();

				//联机网络管理器
				EnsureMpManager();
			}
			catch (Exception e)
			{
				Log("Init failed: " + e.ToString());
			}
			BuildUi();
			Game.Instance.SceneManager.SceneLoaded += OnSceneLoaded;
		}

		/// <summary>注册联机控制台命令（HostLobby / JoinLobby / StopLobby）。</summary>
		

		private void RegisterMpCommands()
		{
			DevConsoleApi.RegisterCommand<int>("HostLobbyPort", new Action<int>(port => HostLobby(port)));
			DevConsoleApi.RegisterCommand<string, int>("JoinLobbyPort", new Action<string, int>((host, port) => JoinLobby(host, port)));
			DevConsoleApi.RegisterCommand("StopLobby", new Action(() => StopLobby()));
		}

		/// <summary>作为房主开启联机房间。</summary>
		public bool HostLobby(int port = 25555)
		{
			LogLobby("HostLobby() called: port=" + port);
			MpNetworkManager mgr = EnsureMpManager();
			if (mgr == null)
			{
				LogLobby("HostLobby FAILED: MpNetworkManager.Instance is null (EnsureMpManager returned null)");
				return false;
			}

			bool ok = mgr.Host(port);
			LogLobby("HostLobby() finished: port=" + port + ", result=" + ok +
				", IsServer=" + mgr.IsServer + ", IsConnected=" + mgr.IsConnected +
				", PlayerId=" + mgr.PlayerId + ", LocalNodeId=" + mgr.LocalNodeId +
				", Transport.IsRunning=" + mgr.Transport.IsRunning +
				", LocalPort=" + mgr.Transport.LocalPort +
				", peerCount=" + mgr.Transport.GetPeersCount());
			if (!ok) LogLobby("HostLobby FAILED: see above for TcpTransport start error (port " + port + " may already be in use)");
			return ok;
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
			LogLobby("JoinLobby() called: host=" + host + ":" + port + ", playerName='" + playerName + "'");
			MpNetworkManager mgr = EnsureMpManager();
			if (mgr == null)
			{
				LogLobby("JoinLobby FAILED: MpNetworkManager.Instance is null (EnsureMpManager returned null)");
				return false;
			}

			bool ok = mgr.Join(host, port, playerName);
			LogLobby("JoinLobby() finished: host=" + host + ":" + port + ", result=" + ok +
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
			LogLobby("StopLobby() called" + (MpNetworkManager.Instance != null ? " (manager exists)" : " (manager is null, nothing to stop)"));
			if (MpNetworkManager.Instance != null)
			{
				MpNetworkManager.Instance.Stop();
			}
		}

		/// <summary>确保联机网络管理器已创建并返回实例。</summary>
		public MpNetworkManager EnsureMpManager()
		{
			if (MpNetworkManager.Instance == null)
			{
				if (MPGameObject == null) MPGameObject = new GameObject("MPNetwork");
				MPGameObject.AddComponent<MpNetworkManager>();
				MPGameObject.SetActive(true);
			}
			return MpNetworkManager.Instance;
		}

		public void OnSceneLoaded(object sender, SceneEventArgs e)
		{
			if (Game.Instance.SceneManager.InFlightScene && MpNetworkManager.Instance != null)
			{
				//进入飞行场景后上报/刷新本机飞船 NodeId
				MpNetworkManager.Instance.RefreshLocalCraft();
			}
		}

		public struct recdata
		{
			public Vector3d Position;
			public Vector3d Velocity;
			public Quaterniond Heading;

			/// <summary>
			/// 发送端飞船"相对行星地表"的朝向(surface-relative rotation,仿 KSP LunaMultiplayer 的 srfRelRotation):
			/// = RotateY(θ_frame - θ_send_planet) * comRot(comRot 是帧空间;表面锁定帧 θ_frame-θ_planet 为常量)。
			/// 接收端用 frame.PlanetToFrameRotation(行星自转 × SrfRel) 渲染回帧空间,
			/// 因双端同行星 θ_frame-θ_planet 相同 → 两端帧空间朝向一致,不依赖双端自转/时间同步、无 warp 漂移。
			/// </summary>
			public Quaterniond SrfRel;

			public float Pitch;
			public float Yaw;
			public float Roll;

			public float Throttle;
			public float Brake;

			public float Slider1;
			public float Slider2;
			public float Slider3;
			public float Slider4;

			public float TranslateForward;
			public float TranslateRight;
			public float TranslateUp;

			public List<bool> ActivationGroupStates;

			public int Stage;

			/// <summary>
			/// 每个 body 相对"craft 根"的局部旋转（欧拉角，与 BodyData.Rotation 同语义）。
			/// 发送端飞行中的 body 姿态是动态的（安装角/关节偏转），XML 只含设计态(identity)，
			/// 必须随状态包同步，接收端才能复现远程飞船的 body 朝向，避免"分裂/散架"。
			/// </summary>
			public List<Vector3> BodyRotations;

			public recdata(Vector3d position, Vector3d velocity, Quaterniond heading)
			{
				Position = position;
				Velocity = velocity;
				Heading = heading;
				SrfRel = Quaterniond.identity;

				Pitch = 0;
				Yaw = 0;
				Roll = 0;

				Throttle = 0;
				Brake = 0;

				Slider1 = 0;
				Slider2 = 0;
				Slider3 = 0;
				Slider4 = 0;

				TranslateForward = 0;
				TranslateRight = 0;
				TranslateUp = 0;

				ActivationGroupStates = new List<bool>();
				Stage = 0;
				BodyRotations = new List<Vector3>();
			}

		}
	}
}
