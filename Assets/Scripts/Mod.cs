using System;
using System.Collections.Generic;
using Assets.Packages.DevConsole;
using Assets.Scripts.Net;
using ModApi.Mods;
using ModApi.Scenes.Events;
using UnityEngine;

using HarmonyLib;
using Jundroo.ModTools;

namespace Assets.Scripts
{
	/// <summary>
	/// Mod 主入口：负责初始化、控制台命令注册与联机状态包数据结构 recdata。
	/// 联机房间操作已抽象到独立的 LobbyManager 类（见 LobbyManager.cs），降低与主入口的耦合。
	/// </summary>
	public partial class Mod : GameMod
	{
		private Mod()
		{
			
		}

		public static Mod Instance { get; } = GameModBase.GetModInstance<Mod>();

		protected override void OnModInitialized()
		{
			try
			{
				base.OnModInitialized();
				Harmony harmony = new Harmony("MPTest");
				harmony.PatchAll();
				// 航发幽灵 patch:手动按条件打补丁(目标方法缺失时只降级告警,不打断初始化)
				JetEngineGhostPatch.Apply(harmony);

				// 联机房间管理器（独立类，负责网络管理器创建与场景事件）
				new LobbyManager();
				LobbyManager.Instance.EnsureMpManager();
				Game.Instance.SceneManager.SceneLoaded += LobbyManager.Instance.OnSceneLoaded;

				RegisterMpCommands();
				InitializeUserInterface();
			}
			catch (Exception e)
			{
				Log("Init failed: " + e.ToString());
			}
		}

		/// <summary>创建常驻 UI 对象（跨场景存活）。</summary>
		private void InitializeUserInterface()
		{
			GameObject UiObject=new GameObject("UI");
			UiObject.AddComponent<MultiPlayerUI>();
			UiObject.SetActive(true);
			GameObject.DontDestroyOnLoad(UiObject);
		}

		/// <summary>注册联机控制台命令（HostLobby / JoinLobby / StopLobby）。</summary>
		private void RegisterMpCommands()
		{
			DevConsoleApi.RegisterCommand<int>("HostLobbyPort", new Action<int>(port => LobbyManager.Instance.HostLobby(port)));
			DevConsoleApi.RegisterCommand<string, int>("JoinLobbyPort", new Action<string, int>((host, port) => LobbyManager.Instance.JoinLobby(host, port)));
			DevConsoleApi.RegisterCommand("StopLobby", new Action(() => LobbyManager.Instance.StopLobby()));
			// FishNet spike 临时验证命令：起本地 server+client 验证连接
			DevConsoleApi.RegisterCommand("FishNetSpike", new Action(() =>
			{
				Log("FishNetSpike: creating spike object");
				new GameObject("FishNetSpike").AddComponent<Net.FishNetSpike>();
			}));
			// Steam API 可行性 spike：反射 SocialExt 验证 mod 能否拿到 Steam 身份
			DevConsoleApi.RegisterCommand("SteamSpike", new Action(() =>
			{
				LogLobby("SteamSpike: creating spike object");
				new GameObject("SteamSpike").AddComponent<Net.SteamSpike>();
			}));
			// Steam P2P：房主开房（port 忽略，Steam 无端口）
			DevConsoleApi.RegisterCommand<int>("SteamHostLobby", new Action<int>(port => LobbyManager.Instance.HostLobby(port)));
			// Steam P2P：客户端按房主 SteamId 加入
			DevConsoleApi.RegisterCommand<string>("SteamJoinLobby", new Action<string>(hostSteamId => LobbyManager.Instance.JoinLobby(hostSteamId, 0)));
			// TCP debug（本地虚拟机联机调试）：先切到 TcpTransport 再开房 / 加入。
			// 房主监听 IPAddress.Any:port；客户端按宿主局域网 IP:port 连接（如 192.168.56.1:25555）。
			DevConsoleApi.RegisterCommand<int>("TcpHostLobby", new Action<int>(port =>
			{
				MpNetworkManager mgr = LobbyManager.Instance.EnsureMpManager();
				if (mgr != null) mgr.SetTransport(new Net.TcpTransport());
				LobbyManager.Instance.HostLobby(port);
			}));
			DevConsoleApi.RegisterCommand<string, int>("TcpJoinLobby", new Action<string, int>((host, port) =>
			{
				MpNetworkManager mgr = LobbyManager.Instance.EnsureMpManager();
				if (mgr != null) mgr.SetTransport(new Net.TcpTransport());
				LobbyManager.Instance.JoinLobby(host, port);
			}));
			// 房主调整状态包发送频率（Hz）：SetTickRate 20 → 50ms（默认）；5 → 200ms；60 → ~16.7ms。
			// 房主设置后广播给所有客户端（SP2 ServerTickRate 同款思路）。
			DevConsoleApi.RegisterCommand<int>("SetTickRate", new Action<int>(hz => LobbyManager.Instance.SetTickRate(hz)));
		}

		/// <summary>联机状态包数据结构。</summary>
		public struct RemoteDataPack
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

			/// <summary>
			/// 每个 body 相对 comRot(CenterOfMass)的"局部位置"(与 BodyRotations 平行、同长度同索引,body-sync P0)。
			/// 发送端采样 comRot.InverseTransformPoint(body.Transform.position),接收端写 body.Transform.position = comRot.TransformPoint(relPos)。
			/// 解决"转轴/关节连接的子装配随转轴整体移动"(摆动主要是位置变化)以及残骸小碎片位置缺口。
			/// 见 plans/body-sync.md。
			/// </summary>
			public List<Vector3> BodyPositions;

			/// <summary>
			/// 每台引擎的"视觉 throttle"(0..1)，按确定顺序(Data.Assembly.Parts 顺序→每部件 modifiers 顺序)
			/// 与接收端一一对应：液体引擎=EngineThrottle，航发=AfterburnerThrottle(加力尾焰驱动值)。
			/// 接收端据此驱动幽灵船尾焰(液体走 ExhaustThrottleOverride;航发加力由 MP 层直接驱动)。
			/// </summary>
			public List<float> EngineThrottles;

			/// <summary>
			/// 每部件"开关/展开状态"(PartData.Activated)，按 Data.Assembly.Parts 确定顺序与接收端一一对应(方案 B)。
			/// 接收端只对白名单部件(起落架/货舱门/着陆腿/太阳能/灯·信标/SubPartRotator)应用 Activate()/Deactivate()
			/// 让游戏自身 FlightUpdate/动画器驱动本地视觉;引擎走 EngineVisualSync(不在此应用);
			/// 分离器/整流罩/对接 = 只记录不处理(归 body 同步);降落伞 = 专用视觉驱动(P2)。
			/// 见 plans/part-switch-sync-feasibility.md §3/§4/§9。
			/// </summary>
			public List<bool> PartActivated;

			public RemoteDataPack(Vector3d position, Vector3d velocity, Quaterniond heading)
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
				BodyPositions = new List<Vector3>();
				EngineThrottles = new List<float>();
				PartActivated = new List<bool>();
			}

		}
	}
}
