using System;
using System.Collections.Generic;
using Assets.Scripts.Flight;
using ModApi;
using ModApi.Craft;
using ModApi.Craft.Parts;
using UnityEngine;

namespace Assets.Scripts.Net.Sync
{
	/// <summary>
	/// P0 修复(2026-09-23):保障"观察者(本机)船"开启渲染插值。
	///
	/// 根因(见 plans/observer-tick-quantization-2026-09-23.md):JNO 所有 craft body 默认
	/// <c>rigidbody.interpolation = RigidbodyInterpolation.None</c>(CraftBuilder.cs:223),
	/// 因此**本机船及其相机的位置只在物理固定步更新**(实测 fixedDt=10ms),而远程幽灵由 mod
	/// **每渲染帧**写入连续位置。并排飞行时以本机船为参照看对方,就看到幽灵按 v×fixedDt 的台阶
	/// 前后跳(实测相机逐帧位移恒为 v×fixedDt 的整数倍、幽灵为连续小数、drift*=0)。
	///
	/// 本类只做一件事:把**本机船**全部 body 设为 Interpolate,让 Unity 在渲染帧之间插值物理位姿。
	/// 这是游戏自带的调试能力(演示:飞行中按 Ctrl+Shift+I 对当前船全 body 切换 Interpolate,见
	/// FlightSceneScript.cs:1462-1473),本类只是把它自动化并限制在观察者船上。
	///
	/// 红线:**绝不给幽灵开插值**。幽灵是 mod 每渲染帧直接写 Transform 的刚体,开启插值会让它退化为
	/// "物理步采样 + 插值",反而把幽灵也变成台阶式抖动。
	///
	/// 副作用边界:Unity 的刚体插值只影响**渲染位姿**,不影响物理模拟与碰撞;相机/地图/Vizzy 读取的
	/// Transform 会被平滑(这正是目的)。可用控制台命令 `MpLocalInterp off` 立即关闭做 A/B 对照。
	/// </summary>
	internal static class LocalCraftInterpolation
	{
		/// <summary>总开关(控制台命令 `MpLocalInterp on|off` 可运行时切换)。</summary>
		internal static bool Enabled = true;

		/// <summary>复查间隔(秒):兼容 revert / 重发射 / 对接后重建 CraftScript,故周期复查而非只做一次。</summary>
		private const float RecheckIntervalSec = 1f;

		private static float _nextCheck;
		private static bool _applied;   // 是否已把本机船置为 Interpolate(用于 OFF 时回滚)

		/// <summary>由 NetworkManager.Update 每帧调用(内部自带节流)。</summary>
		internal static void Tick()
		{
			if (!Enabled)
			{
				// 2026-09-24 修:OFF 必须**回滚**成游戏默认 None。原实现只"停止强制",已设上的 Interpolate 会残留
				// → 开关看似无效(实测 A/B 数据 ON 1.001× vs OFF 1.002×,几乎无差,即此 bug)。
				if (_applied) RevertLocalCraft();
				return;
			}
			float now = Time.unscaledTime;
			if (now < _nextCheck) return;
			_nextCheck = now + RecheckIntervalSec;
			try
			{
				FlightSceneScript scene = FlightSceneScript.Instance;
				if (scene == null || scene.CraftNode == null || scene.CraftNode.CraftScript == null) return;
				IReadOnlyList<BodyData> bodies = scene.CraftNode.CraftScript.Data.Assembly.Bodies;
				if (bodies == null || bodies.Count == 0) return;
				int changed = 0;
				for (int i = 0; i < bodies.Count; i++)
				{
					Rigidbody rb = bodies[i] != null && bodies[i].BodyScript != null ? bodies[i].BodyScript.RigidBody : null;
					if (rb == null) continue;
					if (rb.interpolation != RigidbodyInterpolation.Interpolate)
					{
						rb.interpolation = RigidbodyInterpolation.Interpolate;
						changed++;
					}
				}
				_applied = true;
				if (changed > 0)
				{
					Mod.LogLobby("MultiPlayer: local craft interpolation -> Interpolate on " + changed +
						" body(ies) (observer-side physics-tick stepping fix; MpLocalInterp off to revert)");
				}
			}
			catch (Exception e) { Mod.LogError("MultiPlayer LocalCraftInterpolation error: " + e.Message); }
		}

		/// <summary>
		/// OFF 时把本机船恢复为游戏默认 <c>RigidbodyInterpolation.None</c>(A/B 对照必须真的回滚)。
		/// </summary>
		private static void RevertLocalCraft()
		{
			_applied = false;
			try
			{
				FlightSceneScript scene = FlightSceneScript.Instance;
				if (scene == null || scene.CraftNode == null || scene.CraftNode.CraftScript == null) return;
				IReadOnlyList<BodyData> bodies = scene.CraftNode.CraftScript.Data.Assembly.Bodies;
				if (bodies == null || bodies.Count == 0) return;
				int changed = 0;
				for (int i = 0; i < bodies.Count; i++)
				{
					Rigidbody rb = bodies[i] != null && bodies[i].BodyScript != null ? bodies[i].BodyScript.RigidBody : null;
					if (rb == null) continue;
					if (rb.interpolation != RigidbodyInterpolation.None)
					{
						rb.interpolation = RigidbodyInterpolation.None;
						changed++;
					}
				}
				Mod.LogLobby("MultiPlayer: local craft interpolation -> None on " + changed +
					" body(ies) (MpLocalInterp off: 已回滚为游戏默认,供 A/B 对照)");
			}
			catch (Exception e) { Mod.LogError("MultiPlayer LocalCraftInterpolation revert error: " + e.Message); }
		}
	}
}
