using UnityEngine;

namespace Assets.Scripts.Net.Sync
{
	/// <summary>
	/// 联机同步的共享调参常量 + 数学工具(2026-09-22 重构:自 MultiPlayerNetworkManager 集中而来;同日二次整理:MultiPlayerMath 并入)。
	/// 供发送端(LocalCraftSender)与接收端外推/平滑(RemoteCraftDriver / RemoteCraftSmoothing)共用,
	/// 各文件用 `using static Assets.Scripts.Net.Sync.MultiPlayerSyncUtil;` 以原名引用(保持与旧代码逐字一致)。
	/// </summary>
	internal static class MultiPlayerSyncUtil
	{
		/// <summary>
		/// 远程船"冻结/解冻"时外推量过渡时长(秒)。冻结瞬间把外推量收敛到固定单向延迟(0.15s 内),
		/// 解冻瞬间再放回"延迟+包龄",避免状态切换本身造成位置跳变(抽搐)。
		/// </summary>
		public const float RemoteFreezeBlendSec = 0.15f;

		/// <summary>
		/// P1(2026-09-23)接收端冻结判据去抖:包内 Paused=1 需**连续这么多包**才进入冻结。
		/// 单包即冻结会被发送端 TimeManager.Paused 的瞬时抖动触发 → 冻结↔解冻反复 → 幽灵按 V×VA 后退/前冲
		/// (实测 HOST 40/198 条 sendDiag 报 paused=1,含 10.5~16m/s 飞行中;VM 侧出现 24.9m/173m 级位置跳变)。
		/// 20Hz 下 2 包 = 100ms,不影响"发送端暂停即冻结"的原语义。
		/// </summary>
		public const int PausedFlagConfirmPackets = 2;

		// --- P2(2026-09-24)位置积分器:自由运行积分 + 有界误差回收(取代"锚点外推 + 指数平滑 + maxStep") ---
		/// <summary>
		/// 位置积分器总开关。开启后接收端位置不再"每帧向包推导目标收敛",而是:
		/// ① 速度向包速度做 EMA;② 位置严格按速度积分(Δpos ≡ V×dt,与包到达/帧时长无关);
		/// ③ 锚点(包位置 + 速度×(单向延迟 + EMA 包龄))只用于**有界误差回收**。
		/// 依据(2026-09-24 双端实测):幽灵逐帧速度 max/avg = 2.3~2.7×(与发包率 20~120Hz 基本无关、
		/// 与帧时长无关),同帧时长下本机船为 1.00× → 抖动源在 mod 自身的位置构造,不在游戏/渲染/网络节拍。
		/// 验收指标(探针内建):gSpeed max/avg → ≤1.3×;并排窗口 relCoMMax → ≈|Δv|×dt。
		/// ⚠️ 用 static(非 const):const 会让编译器把另一分支判成不可达代码(CS0162),本项目要求 0 警告。
		/// 运行时可用控制台命令 `MpPosIntegrator on|off` 切换做 A/B 对照。
		/// </summary>
		public static bool EnablePositionIntegrator = true;
		/// <summary>积分速度 EMA 时间常数(秒):包速度先低通,避免把包内速度噪声积分成位置抖动。</summary>
		public const float IntegVelTauSec = 0.15f;
		/// <summary>误差回收时间常数(秒):锚点残差按此收敛;越小越紧跟、越大越平滑。</summary>
		public const float IntegErrTauSec = 0.30f;
		/// <summary>包龄 EMA 时间常数(秒):锚点用的包龄必须平滑(禁止用逐包重置的瞬时 age,否则锯齿重新注入位置)。</summary>
		public const float IntegAgeEmaTauSec = 0.5f;
		/// <summary>单帧误差回收上限(×V×dt):0.25 ⇒ 单帧位移落在 [0.75,1.25]×V×dt,结构上不可能后退/停顿。</summary>
		public const float IntegMaxCorrFrac = 0.25f;
		/// <summary>锚点残差的"硬账"上限(秒×V):超出部分直接吞掉(视为瞬移/丢包积欠),不参与回收。</summary>
		public const float IntegMaxErrSec = 0.08f;

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
