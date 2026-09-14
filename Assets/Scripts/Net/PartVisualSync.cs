using System;
using System.Collections.Generic;
using Assets.Scripts.Craft.Parts.Modifiers;
using Assets.Scripts.Craft.Parts.Modifiers.LandingGear;
using Assets.Scripts.Craft.Parts.Modifiers.LandingLeg;
using Assets.Scripts.Craft.Parts.Modifiers.Lights;
using Assets.Scripts.Craft.Parts.Modifiers.Propulsion;
using Assets.Scripts.Craft.Parts.Modifiers.Solar;
using Assets.Scripts.Craft.Parts.Modifiers.Wing;
using Assets.Scripts.Flight.Sim;
using ModApi.Craft.Parts;
using ModApi.Flight.Sim;
using UnityEngine;

namespace Assets.Scripts.Net
{
	/// <summary>
	/// 幽灵船"部件开关/展开状态"同步(方案 B + P3,见 plans/part-switch-sync-feasibility.md §3/§4/§9/§11)。
	/// - 发送端:按确定顺序(Data.Assembly.Parts)采样每部件 PartData.Activated;
	/// - 接收端:对**白名单部件**应用 Activate()/Deactivate(),让游戏自身 FlightUpdate/动画器驱动本地视觉:
	///   ① 开关确定性部件(起落架/货舱门/着陆腿/太阳能/灯·信标/SubPartRotator —— 读 Part.Activated 驱动动画,纯 Transform);
	///   ② 输入驱动部件(舵面/Rotator/活塞/螺旋桨/车轮/RCS/电机 —— P3 放开):姿态 = Activated(激活门控) + Controls
	///      (ControlVisualSync 每包写入幽灵 Controls),二者配合才显远程姿态,见 §11;
	/// - **只记录不处理**(位照常传输,但不应用):引擎(EngineVisualSync 管,火箭还被强制 Activated=true 刷膨胀比)、
	///   分离器/整流罩/对接(涉及 body 改动,归 body 同步)、降落伞(新建物理组件 + 密度门控,走专用视觉驱动 P2)、
	///   InputBasedActivator(会 ActivateStage/ExplodePart,不能在本机触发)、舱/Vizzy/自动驾驶等特殊部件。
	/// 为什么不用"激活组位"(SP2 式整机输入位):SR2 的 Part.Activated 有三条入口(激活组/Stage/飞行检查器手动 override),
	/// 手动 override 不产生激活组位变化 → 必须同步 per-part 位才能全覆盖(见 plan §3 决策依据)。
	/// 顺序契约:发送/接收两端同 XML 构建 → Data.Assembly.Parts 顺序一致,index 一一对应;读取端越界兜底。
	/// 变沿应用:PartScript.Activate()/Deactivate() 内部有 if(Activated) 守卫,无变化时为空操作;
	/// 持续每包应用自带自愈(幽灵本地任何改写下一包即纠正)。
	/// </summary>
	public static class PartVisualSync
	{
		/// <summary>
		/// 接收端要应用 Part.Activated 的部件 modifier 类型白名单。
		/// 组成:
		///  1) 开关确定性部件(纯 Activated 驱动动画,无输入依赖):起落架/货舱门/着陆腿/太阳能/灯·信标/SubPartRotator;
		///  2) 输入驱动部件(P3 放开,2026-08-18):舵面/gimbal/Rotator(JointRotator)/活塞/螺旋桨/车轮/RCS/电机/旋转灯——
		///     它们的姿态 = Activated(激活门控) + Controls(ControlVisualSync 已同步写入),见 plans §11。
		/// 永远不应用(只记录不处理):
		///  - 引擎(EngineVisualSync 负责尾焰;火箭引擎还被强制 Activated=true 刷新膨胀比,此处应用会冲突);
		///  - 分离器/整流罩/对接/伞(涉及 body 改动,归 body 同步 / 伞走专用驱动 P2);
		///  - InputBasedActivator(会 ActivateStage/ExplodePart,绝不能在本机幽灵触发);
		///  - 舱/Cockpit/Vizzy(FlightProgram)/自动驾驶(TestPilot)等特殊部件。
		/// </summary>
		private static readonly Type[] _applyModifierTypes = new Type[]
		{
			typeof(LandingGearScript),          // 起落架收放动画(纯 Transform;车轮 Turn/Motor 亦输入驱动,已含)
			typeof(CargoBayScript),             // 货舱门开合
			typeof(LandingLegScript),           // 着陆腿收放
			typeof(SolarPanelScript),           // 太阳能板展开
			typeof(SolarPanelArrayScript),      // 太阳能阵列展开
			typeof(LightScript),                // 灯
			typeof(BeaconLightScript),          // 信标灯
			typeof(SubPartRotatorScript),       // 子部件旋转展开
			// ---- 以下为 P3 放开:输入驱动部件(需 Activated 门控 + ControlVisualSync 写 Controls 才显远程姿态)----
			typeof(ControlSurfaceScript),       // 舵面偏转(Pitch/Yaw/Roll/Brake/Throttle/Slider1-4)
			typeof(JointRotatorScript),         // Rotator:关节旋转(输入→目标角)
			typeof(PistonScript),               // 活塞伸缩
			typeof(PropellerAssemblyScript),    // 螺旋桨桨距(BladeAngle)
			typeof(ResizableWheelScript),       // 车轮转向/转动(Turn/RPM/Motor)
			typeof(ReactionControlNozzleScript),// RCS 喷口推力/姿态输入
			typeof(ElectricMotorScript),        // 电机(Motor/Brake)
			typeof(ElectricMotorOldScript),     // 旧版电机
			typeof(LightPartScript),            // 旋转/伸缩探照灯(LightRotation/LightExtension)
		};

		// ---------------- 发送端采样 ----------------

		/// <summary>
		/// 按确定顺序(Data.Assembly.Parts)采样本机飞船每部件 PartData.Activated。
		/// 与接收端 ApplyRemotePartActivated 的枚举顺序严格一一对应。
		/// </summary>
		public static List<bool> SamplePartActivated(CraftNode craft)
		{
			List<bool> list = new List<bool>();
			if (craft == null || craft.CraftScript == null || craft.CraftScript.Data == null || craft.CraftScript.Data.Assembly == null)
			{
				return list;
			}
			try
			{
				IReadOnlyList<PartData> parts = craft.CraftScript.Data.Assembly.Parts;
				for (int pi = 0; pi < parts.Count; pi++)
				{
					PartData part = parts[pi];
					list.Add(part != null && part.Activated);
				}
			}
			catch (Exception e)
			{
				Mod.LogError("PartVisualSync.SamplePartActivated error: " + e.Message);
			}
			return list;
		}

		// ---------------- 接收端:幽灵船应用 ----------------

		/// <summary>
		/// 把同步的每部件开关状态应用到幽灵船(变沿 + 白名单)。
		/// 白名单部件调 Activate()/Deactivate() 让游戏自身驱动视觉;其余部件只记录不处理。
		/// 幂等:每次 ApplyRemoteState 调用,无状态变化时为空操作;幽灵本地偏差下一包自愈。
		/// </summary>
		public static void ApplyRemotePartActivated(MpNetworkManager.RemoteCraft rc, Mod.RemoteDataPack data)
		{
			if (rc == null || rc.Node == null || rc.Node.CraftScript == null || data.PartActivated == null)
			{
				return;
			}
			try
			{
				IReadOnlyList<PartData> parts = rc.Node.CraftScript.Data.Assembly.Parts;
				int n = Mathf.Min(parts.Count, data.PartActivated.Count);
				for (int i = 0; i < n; i++)
				{
					PartData part = parts[i];
					if (part == null || part.PartScript == null) continue;

					bool synced = data.PartActivated[i];
					if (synced == part.Activated) continue; // 变沿:无变化不操作
					if (!ShouldApply(part.PartScript)) continue; // 只记录不处理

					try
					{
						if (synced)
						{
							part.PartScript.Activate();
						}
						else
						{
							part.PartScript.Deactivate();
						}
					}
					catch { }
				}
			}
			catch (Exception e)
			{
				Mod.LogError("PartVisualSync.ApplyRemotePartActivated error (P" + rc.PlayerId + "): " + e.Message);
			}
		}

		/// <summary>部件是否含白名单 modifier(有则应用开关,否则只记录不处理)。</summary>
		private static bool ShouldApply(IPartScript partScript)
		{
			if (partScript == null) return false;
			foreach (PartModifierScript mod in partScript.Modifiers)
			{
				if (mod == null) continue;
				Type t = mod.GetType();
				for (int k = 0; k < _applyModifierTypes.Length; k++)
				{
					if (_applyModifierTypes[k].IsAssignableFrom(t)) return true;
				}
			}
			return false;
		}
	}
}
