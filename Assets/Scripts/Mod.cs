using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Assets.Packages.DevConsole;
using ModApi;
using ModApi.Mods;
using UnityEngine;

using HarmonyLib;
using Jundroo.ModTools;
using Assets.Scripts.Net.MultiPlayerTransport;
using Assets.Scripts.Net.Session;
using ModApi.Ui;
using UI.Xml;

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

		/// <summary>
		/// 本地 Mod 版本（= ModInfo.Version，类型 System.Version，如 0.1）。
		/// 由 OnModInitialized 赋值，供 ModUpdater 做"网站最新 vs 本地"比较。
		/// </summary>
		public Version ModVersion { get; private set; }

		protected override void OnModInitialized()
		{
			try
			{
				base.OnModInitialized();
				//HarmonyPatch部署
				DeployHarmony();
				ChatUiButtonSetUp();
				// 联机房间管理器（独立类，负责网络管理器创建与场景事件）
				new LobbyManager();
				LobbyManager.Instance.EnsureMultiPlayerManager();
				Game.Instance.SceneManager.SceneLoaded += LobbyManager.Instance.OnSceneLoaded;

				// 常驻 UI（跨场景存活）：MultiPlayer 检查器面板；所有调试开关都在该面板里
				// （2026-09-24：原 DevConsole 注册的调试命令已全部迁移到 UI 并删除注册）。
				InitializeUserInterface();

				// 更新检查（移植自 Volken2 ModUpdater，含防卡死机制）：
				// 必须在 ModVersion 赋值之后调用，否则 ModUpdater 会因本地版本为空而跳过。
				this.ModVersion = this.ModInfo.Version;
				new ModUpdater().CheckForUpdate();
			}
			catch (Exception e)
			{
				Log("Init failed: " + e.ToString());
			}
		}

		private void DeployHarmony()
		{
			Harmony harmony = new Harmony("MultiPlayer");
			harmony.PatchAll();
			JetEngineGhostPatch.Apply(harmony);
		}

		/// <summary>创建常驻 UI 对象（跨场景存活）。</summary>
		private void InitializeUserInterface()
		{
			GameObject UiObject=new GameObject("UI");
			UiObject.AddComponent<MultiPlayerUI>();
			UiObject.SetActive(true);
			GameObject.DontDestroyOnLoad(UiObject);

			// Steam 大厅浏览器（房间列表）：独立对象跨场景常驻，任何场景都泵回调（SteamAPI.RunCallbacks 保险），
			// 并处理好友"加入游戏"邀请（GameLobbyJoinRequested_t）。见 plans/steam-lobby-2026-09-12.md。
			GameObject lobbyObject = new GameObject("MultiPlayerSteamLobbyBrowser");
			lobbyObject.AddComponent<Net.SteamLobbyBrowser>();
			GameObject.DontDestroyOnLoad(lobbyObject);
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
			/// 每个 body 的稳定标识(BodyData.Id,craft XML 的 id 属性,与 BodyPositions 平行同索引)。
			/// 2026-09-19:发送端装配顺序/列表可能与本机幽灵不一致(实测 bodyMaxRelΔ 4~6m/1s、
			/// 接收端 bodyTgt≈5m 恒定 → 索引错位把不同部件位姿互写 → 部件持续追赶抖动)。
			/// 接收端按 id 重排到幽灵装配顺序(见 MultiPlayerNetworkManager.ReorderRemoteBodiesByGhost);
			/// 旧对端无此字段时回退索引直用。仅在包尾传输,双向兼容。
			/// </summary>
			public List<int> BodyIds;

			/// <summary>
			/// 每个 body 的角速度(**body 自身局部系**,弧度/秒,与 BodyPositions 平行同索引,rotating-body-sync)。
			/// 旋翼叶片等高速旋转 body 在 20Hz 位置快照下每包相位跳 90°+(实测接收端 bodyTgt≈5.9m 恒定、
			/// bodyBig 数百) → 10·dt 平滑追不上 → 叶片"跳着转"。接收端据此做"刚体旋转外推":
			/// 目标位置/朝向 = 包内值绕 ω 轴旋转 ω·ext(与朝向外推同手法)。
			/// 局部系定义:发送端采样 Quaternion.Inverse(body.Transform.rotation) * rigidbody.angularVelocity。
			/// 协议尾部追加(2026-09-22):旧对端包读到 EOF → 零值(无外推,行为不变)。
			/// </summary>
			public List<Vector3> BodyAngularVelocities;

			/// <summary>
			/// 每个 body 相对 comRot 的**线速度**(comRot 局部系,米/秒,与 BodyPositions 平行同索引,
			/// rotating-body-sync)。旋翼叶片绕桨毂公转时位置快照每包跳 90°+,且桨毂不在 comRot 上
			/// (绕 comRot 原点外推位置会画错圆) → 发送端直接传"相对 comRot 位置的变化率"
			/// (数值差分 BodyPositions),接收端对旋转 body 逐帧积分:sp += v·dt,切线方向随 ω 旋转。
			/// 不需要知道旋转中心,逐帧小步积分(60fps 下 θ_frame≈0.5rad)天然精确。
			/// 协议尾部追加(2026-09-22):旧对端包读到 EOF → 零值(无位置外推,行为不变)。
			/// </summary>
			public List<Vector3> BodyVelocities;

			/// <summary>
			/// 每台引擎的"视觉 throttle"(0..1)，按确定顺序(Data.Assembly.Parts 顺序→每部件 modifiers 顺序)
			/// 与接收端一一对应：液体引擎=EngineThrottle，航发=EngineThrottle(接收端据此推导加力尾焰驱动值 ab)。
			/// 接收端据此驱动幽灵船尾焰(液体走 ExhaustThrottleOverride;航发加力由 MultiPlayer 层直接驱动)。
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

			/// <summary>
			/// 发送端游戏是否处于暂停(TimeManager.Paused,即 Time.timeScale==0)。
			/// 用途:暂停时发送端位置是"冻结"的,但 Velocity 仍保留暂停前最后一刻的非零速度。
			/// 接收端若仍按 "Position + Velocity×外推量" 做 dead-reckoning,目标位置会在
			/// 每包到达时被拉回、又在包间按速度前进 → 以发包频率来回摆动 → 观察方看到"位置抽搐"
			/// (见 plans/latency-smoothing-2026-08-22.md §9.7)。
			/// 接收端据此把目标锁在"最新包位置"上,不再用速度外推。
			/// </summary>
			public bool Paused;

			/// <summary>
			/// 2 阶外推数据(2026-09-14,acceleration-smoothing):
			/// Acceleration = 发送端飞船加速度(行星系,含重力,根 body 刚体速度差分测量)
			/// 转"地表系"后的值(纯旋转,Coriolis/离心项在 SR2 尺度 ≈0.1 m/s² 可忽略);
			/// 接收端外推加 ½·a·ext²。
			/// AngularVelocity = 发送端飞船角速度,**craft 局部系**(ModApi 约定,SR2 符号翻转已内嵌);
			/// 接收端按 ω·ext 右乘外推朝向(符号约定待实测,见 plans/acceleration-smoothing-2026-09-14.md §六-1)。
			/// 协议尾部追加字段:旧对端包读到 EOF → 零值(退化到 1 阶外推,行为不变)。
			/// </summary>
			public Vector3 Acceleration;
			public Vector3 AngularVelocity;

			
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
				BodyIds = new List<int>();
				BodyAngularVelocities = new List<Vector3>();
				BodyVelocities = new List<Vector3>();
				EngineThrottles = new List<float>();
				PartActivated = new List<bool>();
				Paused = false;
				Acceleration = Vector3.zero;
				AngularVelocity = Vector3.zero;
			}

		}
		
		private void ChatUiButtonSetUp()
		{
			Game.Instance.UserInterface.AddBuildUserInterfaceXmlAction("Ui/Xml/Flight/ViewPanel", OnBuildViewPanel); 
			Game.Instance.SceneManager.SceneLoaded += (sender, e) => 
			{
				if (Game.Instance.SceneManager.InFlightScene)
				{
					Game.Instance.FlightScene.GameObject.GetComponentsInChildren<XmlElement>().ToList().ForEach(x =>
					{
						// id 必须与 OnBuildViewPanel 注入的 ContentButton id 一致(CraftSp 同款模式)
						if (x.id == "multiplayer-chat")
						{
							x.AddOnClickEvent(OnChatUiButtonClicked);
						}
					});
				}
			};
		}
		private static void OnBuildViewPanel(BuildUserInterfaceXmlRequest request)
		{
			var cameraPanelButton =
				request.XmlDocument.Descendants(XmlLayoutConstants.XmlNamespace + "ContentButton")
					.FirstOrDefault(n => n.Attribute("id")?.Value == "toggle-camera-panel-button");

			if (cameraPanelButton != null)
			{
				cameraPanelButton.AddAfterSelf(
					XElement.Parse(
						$"<ContentButton name=\"MultiPlayerChat\" id=\"multiplayer-chat\" class=\"view-button audio-btn-click\" tooltip=\"{Locale.GetString("MultiPlayer.Chat.ButtonTooltip")}\" xmlns=\"{XmlLayoutConstants.XmlNamespace}\">" +
						"    <Image sprite=\"MultiPlayer/Sprites/ChatIcon\" />" +
						"</ContentButton>"
					)
				);
			}
		}

		/// <summary>ViewPanel 聊天按钮点击:转发聊天窗切换(XmlLayout 原生窗,未连接/不在飞行场景时不生效,见 MultiPlayerChatWindow.Toggle)。</summary>
		public void OnChatUiButtonClicked()
		{
			MultiPlayerChatWindow.Toggle();
		}
	}
}
