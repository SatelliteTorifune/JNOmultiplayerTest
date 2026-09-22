using UnityEngine;

namespace Assets.Scripts.Net.Sync
{
	/// <summary>
	/// 联机同步的共享调参常量 + 数学工具(2026-09-22 重构:自 MpNetworkManager 集中而来;同日二次整理:MpMath 并入)。
	/// 供发送端(LocalCraftSender)与接收端外推/平滑(RemoteCraftDriver / RemoteCraftSmoothing)共用,
	/// 各文件用 `using static Assets.Scripts.Net.Sync.MpSyncUtil;` 以原名引用(保持与旧代码逐字一致)。
	/// </summary>
	internal static class MpSyncUtil
	{
		/// <summary>
		/// 远程船"冻结/解冻"时外推量过渡时长(秒)。冻结瞬间把外推量收敛到固定单向延迟(0.15s 内),
		/// 解冻瞬间再放回"延迟+包龄",避免状态切换本身造成位置跳变(抽搐)。
		/// </summary>
		public const float RemoteFreezeBlendSec = 0.15f;

		// --- 2 阶外推(acceleration-smoothing-2026-09-14):发送端采样 EMA/钳制 + 接收端开关 ---
		/// <summary>发送端加速度 EMA 系数(每包 20Hz;Acceleration 是刚体速度差分测量,一帧滞后+噪声,必须平滑)。</summary>
		public const float SenderAccelEmaRate = 0.2f;
		/// <summary>发送端角速度 EMA 系数(每包)。</summary>
		public const float SenderAngVelEmaRate = 0.2f;
		/// <summary>加速度幅值钳制(m/s²):外推项 ½·a·ext² 在 ext≤1s 时 ≤30m,超钳制值视为噪声/坏数据。</summary>
		public const float MaxAccelMs = 60f;
		/// <summary>角速度幅值钳制(rad/s,≈0.5 rev/s):防快速自旋/异常包让朝向外推过量。</summary>
		public const float MaxAngVelRad = 3f;
		/// <summary>body 角速度幅值钳制(rad/s,≈1700 RPM):旋翼叶片等高速旋转 body(300+ RPM≈31 rad/s、
		/// 螺旋桨可达 ~1500 RPM≈157 rad/s)远高于整船翻滚(MaxAngVelRad=3)。钳制仅防异常包,
		/// 正常旋翼/螺旋桨转速在钳制内。</summary>
		public const float MaxBodyAngVelRad = 180f;
		/// <summary>body 旋转外推(ω·ext)总开关(2026-09-22,rotating-body-sync):旋翼叶片位置快照在
		/// 20Hz 下每包相位跳 90°+ → 目标按"包内绝对相位绕 ω 轴旋转 ω·ext"外推,包间叶片连续转。</summary>
		public const bool EnableBodyRotationExtrap = true;
		/// <summary>body 视为"显著旋转"的角速度阈值(rad/s,≈57°/s):≥此值才用高收敛率(50·dt)跟随
		/// 外推目标并绕 ω 轴外推位置/朝向。低于此值(展开中的太阳能板等缓转部件)保持原 10·dt 平滑,
		/// 避免把包抖动透出。</summary>
		public const float BodySpinThresholdRad = 1.0f;
		/// <summary>body 差分线速度幅值钳制(m/s):防瞬时空/部件销毁重建时位置突变产生毛刺差分
		/// (叶片正常切线速度 ≈ ω×半径 ≤ 180 m/s;钳制仅防异常)。</summary>
		public const float MaxBodyVelMs = 180f;
		/// <summary>接收端平移 2 阶外推(½·a·ext²)总开关。加速度域无符号约定问题,可安全开启。</summary>
		public const bool EnableSecondOrderExtrap = true;
		/// <summary>
		/// 接收端朝向外推(ω·ext 右乘)总开关。ω 的 SR2 符号翻转约定已实测确认
		/// (2026-09-19 Steam sendDiag 自校验:errF+=5.08~7.75 < errF-=10.23~18.87 ≈ errR+,6 条一致,
		/// F+ = 翻转(-x,y,-z)正号 = 当前代码路径)→ 开启,消快速转向时的旋转步进。
		/// </summary>
		public const bool EnableRotationExtrap = true;
		/// <summary>朝向外推符号(+1 = 翻转(-x,y,-z),2026-09-19 实测确认)。</summary>
		public const float RotationExtrapSign = 1f;

		/// <summary>Vector3d 是否全为有限值(NaN/Inf 视为非法,防坏包污染平滑状态)。</summary>
		internal static bool IsFinite(Vector3d v)
		{
			return !double.IsNaN(v.x) && !double.IsNaN(v.y) && !double.IsNaN(v.z) &&
				!double.IsInfinity(v.x) && !double.IsInfinity(v.y) && !double.IsInfinity(v.z);
		}

		/// <summary>Vector3 是否全为有限值(2 阶外推的加速度/角速度坏值防御)。</summary>
		internal static bool IsFinite(Vector3 v)
		{
			return !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) &&
				!float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);
		}

		/// <summary>EMA 更新(指数移动平均;首次采样直接初始化)。</summary>
		internal static Vector3 UpdateEma(Vector3 ema, ref bool has, Vector3 sample, float rate)
		{
			if (!has) { has = true; return sample; }
			return Vector3.Lerp(ema, sample, rate);
		}

		/// <summary>幅值钳制(保留方向;maxMag≤0 → 归零)。</summary>
		internal static Vector3 ClampMagnitude(Vector3 v, float maxMag)
		{
			if (maxMag <= 0f) return Vector3.zero;
			float m = v.magnitude;
			return m > maxMag ? v * (maxMag / m) : v;
		}
	}
}
