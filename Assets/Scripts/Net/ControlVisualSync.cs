using System;
using ModApi.Craft;
using ModApi.Craft.Parts;
using UnityEngine;

namespace Assets.Scripts.Net
{
	/// <summary>
	/// 幽灵"控制输入"应用(P3,见 plans/part-switch-sync-feasibility.md §11)。
	///
	/// 机制(已反编译证实):
	///  - 飞行中唯一每帧写 Controls.X 的是 FlightControls.Update([FlightControls.cs:302-388]),它是单例且只绑玩家 craft
	///    ([FlightSceneScript.cs:907/1551])→ 幽灵 Controls 游戏从不刷新、恒为 XML 初值,可直接自由写;
	///  - 输入驱动部件(舵面/RCS/gimbal/Rotator/活塞/螺旋桨等)读 CraftControls 属性:
	///      * 表达式输入 (CraftControls x)=&gt;x.Pitch → SimpleInputController(ModApi SimpleInputController.cs:74-85) 直读 CommandPod.Controls;
	///      * 命名输入 "Throttle"/"Brake"/"Slider1".. → InputControllerInput.Create 直读同名 CraftControls 属性(InputControllerInput.cs:94-106);
	///      * 全局引用(@Yaw/@Roll 等)→ InputControllerExpression.CraftControlsVariable,同样读 Controls;
	///  - 门控:SimpleInputController 需 Data.Activated(未激活返回 0)、InputControllerScript 需 Activated/激活组
	///    (InputControllerScript.cs:158-164)→ 由 PartVisualSync 对输入驱动部件放开 Activated 应用来满足(方案 B 已对所有部件传位)。
	/// 本类只负责把同步的控制标量 + 激活组状态写进幽灵 ActiveCommandPod.Controls;
	/// 活动舱 → 其余舱由游戏 CommandPodScript.FlightFixedUpdate 的 CopyControls([CommandPodScript.cs:329])自动复制,单点写即全舱生效。
	/// 幂等:每包(ApplyRemoteState)写入,值恒等于发送端最新采样,自带自愈;无部分更新、无覆盖冲突(幽灵无本地输入源)。
	/// </summary>
	public static class ControlVisualSync
	{
		/// <summary>把同步控制输入 + 激活组状态应用到幽灵活动舱 Controls(输入驱动部件由此获得远程姿态)。</summary>
		public static void ApplyRemoteControls(MpNetworkManager.RemoteCraft rc, Mod.RemoteDataPack data)
		{
			if (rc == null || rc.Node == null || rc.Node.CraftScript == null)
			{
				return;
			}
			try
			{
				ICommandPod pod = rc.Node.CraftScript.ActiveCommandPod;
				if (pod == null) return;
				CraftControls c = pod.Controls;
				if (c == null) return;

				// 控制标量:recdata 已传,原为死字段;现写入幽灵 Controls,驱动所有读这些属性的输入驱动部件
				c.Pitch = data.Pitch;
				c.Yaw = data.Yaw;
				c.Roll = data.Roll;
				c.Brake = data.Brake;
				c.Throttle = data.Throttle;
				c.Slider1 = data.Slider1;
				c.Slider2 = data.Slider2;
				c.Slider3 = data.Slider3;
				c.Slider4 = data.Slider4;
				c.TranslateForward = data.TranslateForward;
				c.TranslateRight = data.TranslateRight;
				c.TranslateUp = data.TranslateUp;

				// 激活组状态:InputControllerScript 等受 Controls.GetActivationGroup 门控;写入使门控与发送端一致
				if (data.ActivationGroupStates != null)
				{
					int n = Mathf.Min(data.ActivationGroupStates.Count, 10);
					for (int i = 0; i < n; i++)
					{
						bool synced = data.ActivationGroupStates[i];
						if (synced != c.GetActivationGroup(i)) c.SetActivationGroup(i, synced);
					}
				}
			}
			catch (Exception e)
			{
				Mod.LogError("ControlVisualSync.ApplyRemoteControls error (P" + rc.PlayerId + "): " + e.Message);
			}
		}
	}
}
