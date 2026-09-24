using System;
using UnityEngine;

namespace Assets.Scripts.Net.Sync
{
	/// <summary>
	/// 联机诊断日志的**唯一出口 + 唯一开关**(2026-09-24 抽象)。
	///
	/// 背景:同步管线里的诊断输出原本散落在 RemoteCraftDriver / RemoteCraftManager / LocalCraftSender /
	/// GhostPoseWriter / RemoteCraftPoseProbe 各处,直接调 <c>Mod.LogLobby("MultiPlayer ...")</c>;
	/// 实测代价与风险都有:
	///   1) 无法一键静默 —— 真实联机(Steam)测试时需要"零诊断开销"的干净基线,只能逐处注释;
	///   2) 日志与算法代码混在一起,削弱了"诊断层可整体删除"的可维护性;
	///   3) 曾有"关诊断"操作把开关做成 const,导致分支被常量折叠(CS0162)且运行时无法切换。
	///
	/// 现在所有同步/幽灵诊断行都经 <see cref="Log"/> 输出,并由 <see cref="Enabled"/> 统一门控
	/// (运行时开关见控制台命令 `MpDiag`)。<see cref="ProbeEnabled"/> 单独控制渲染前探针
	/// (RemoteCraftPoseProbe,每帧采样 + 每秒两行,是最重的诊断,可在需要极限性能时单独关闭)。
	///
	/// ⚠️ 开关必须用可变 static(禁止 const):const 会被编译器常量折叠 → 运行时关不掉 + CS0162 警告。
	/// </summary>
	internal static class MultiPlayerDiag
	{
		/// <summary>全部同步/幽灵诊断日志总开关(不含游戏其它子系统与联机 UI 流程日志)。</summary>
		internal static bool Enabled = true;

		/// <summary>渲染前可见位姿探针开关(<see cref="RemoteCraftPoseProbe"/>)。默认跟随 <see cref="Enabled"/> 之外独立控制。</summary>
		internal static bool ProbeEnabled = false;

		/// <summary>诊断日志唯一出口:所有同步管线诊断行都必须经此输出(便于一键静默与集中改写)。</summary>
		internal static void Log(string line)
		{
			if (!Enabled) return;
			Mod.LogLobby(line);
		}

		/// <summary>
		/// 周期日志节流助手:距上次输出超过 <paramref name="intervalSec"/> 时返回 true 并刷新计时。
		/// 调用方仍负责组织文本(诊断文本与算法上下文强耦合,集中挪动风险高,故只集中"门控与出口")。
		/// </summary>
		internal static bool Due(ref float lastTime, float intervalSec)
		{
			if (!Enabled) return false;
			float now = Time.unscaledTime;
			if (now - lastTime < intervalSec) return false;
			lastTime = now;
			return true;
		}

		/// <summary>一键静默/恢复(控制台命令 `MpDiag off|on`),返回切换后的状态。</summary>
		internal static bool SetEnabled(bool on)
		{
			Enabled = on;
			return Enabled;
		}

		/// <summary>当前状态描述(供控制台回显,便于日志里留痕)。</summary>
		internal static string Describe()
		{
			return "Enabled=" + (Enabled ? "ON" : "OFF") + " Probe=" + (ProbeEnabled ? "ON" : "OFF");
		}
	}
}
