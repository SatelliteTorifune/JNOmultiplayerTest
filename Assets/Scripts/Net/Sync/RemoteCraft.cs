using System;
using System.Collections.Generic;
using Assets.Scripts.Flight.Sim;
using ModApi.Craft;
using ModApi.Craft.Parts;
using UnityEngine;
using Assets.Scripts.Net.CraftVisual;

namespace Assets.Scripts.Net.Sync
{
	/// <summary>
	/// 一艘远程(幽灵)飞船的全部接收端状态(2026-09-22 重构:自 MpNetworkManager 嵌套类整体搬来,字段与方法逐字保留)。
	/// 状态缓冲 / 外推时钟 / 停顿检测 / 平滑状态 / body 重排缓存 / 引擎视觉缓存 / 诊断统计都在这里;
	/// 注释是 30 轮双端实测的决策记录,改动前先读 plans/archive/latency-smoothing-2026-08-22.md。
	/// </summary>
	public class RemoteCraft
	{
		public const int BufferCapacity = 32; // 插值缓冲容量 ≈ 1.6s @ 20Hz，容抖动/乱序

		public int PlayerId;
		public string PlayerName;   // 玩家名(诊断日志区分用)
		public CraftNode Node;
		public bool HasState;      // 是否已收到至少一个状态包
		public bool IsInitialized; // 幻影模式是否已应用（CraftScript 延迟构建后置 true）
		public float LastStateLogTime; // 周期性状态日志计时
		public float LastVisualLogTime; // 可见性诊断日志计时
		public Quaternion LastAppliedHeading; // ApplyRemoteState 最近一次写入的帧空间朝向(诊断用)

		// --- 烟雾速度注入(EngineVisualSync.InjectGhostMotion):上次注入的帧空间速度/角速度缓存 ---
		// 值未变化时跳过写入:Unity 对 kinematic 刚体每次写 velocity 都打告警(见 InjectGhostMotion),
		// 幽灵全 kinematic + 每帧每 body 写一次会把 Player.log 刷爆(~1.3M 条/会话)。
		public Vector3? LastInjectedVelocity;
		public Vector3? LastInjectedAngularVelocity;

		// --- 引擎尾焰同步(EngineVisualSync):最近应用状态中的每引擎视觉 throttle + 幽灵驱动表 ---
		public List<float> SyncedThrottles = new List<float>();
		public List<EngineVisualSync.EngineVisualDriver> EngineDrivers;

		// --- 平滑插帧：带时间戳环形缓冲（按到达端 unscaledTime 排列，暂停安全、容抖动/乱序） ---
		public readonly StateSample[] Buffer = new StateSample[BufferCapacity];
		public int BufferCount;          // 有效样本数
		public int BufferHead;           // 最旧样本索引（环形）
		public Mod.RemoteDataPack LastApplied;  // 最近一次实际应用的状态（LateUpdate 用，避免"最新包覆盖插值"）
		public bool HasApplied;          // LastApplied 是否已有效

		// --- 平滑/网络诊断统计（周期日志 Mod.LogLobby 用，无悬浮窗） ---
		public long TotalFrames;         // UpdateRemoteCrafts 已处理帧数
		public long SnapFrames;          // 欠载中直接返回最新原始包（冻结）的帧数
		public long ExtrapolatedFrames;  // 欠载中按速度外推（不冻结）的帧数
		public float InterpPct;          // 最近一次插值比例 0..1（欠载时=1）
		public float GapEmaMs;           // 包间到达间隔 EMA（ms）
		public float JitterEmaMs;        // 包间到达间隔抖动 EMA（ms）
		public float LatencyMs = -1f;    // 到该发送端的单向网络延迟估计(RTT/2,房主按 peer.PingMs,客户端按 ClientPingMs);-1=未测得
		public float NewestArrivalTime;  // 最新包到达时刻(Time.unscaledTime,连续外推用)
		public double LastPosErrorM;     // 最近渲染位置(插值/外推) vs 最新包位置 的距离（米）
		public float LastSmoothingLogTime; // 平滑诊断周期日志计时
		public float LastSlowmoLogTime;    // 慢放诊断周期日志计时(接收端/发送端慢放时 0.5s 输出)
		public bool DiagHasPrevSlowmo;     // 慢放诊断:是否有上一帧采样
		public Vector3 DiagPrevRootPos;    // 慢放诊断:上一帧 craft 根 transform 位置
		public Vector3 DiagPrevPartPos;    // 慢放诊断:上一帧首个部件世界位置
		public Vector3 DiagPrevComPos;     // 慢放诊断:上一帧 comRot 位置
		public float DiagMaxRootDelta;     // 慢放诊断:窗口内 craft 根最大单帧位移
		public float DiagMaxPartDelta;     // 慢放诊断:窗口内首个部件最大单帧位移
		public float DiagMaxComDelta;      // 慢放诊断:窗口内 comRot 最大单帧位移
		private float _lastPushTime = -1f; // PushSample 上次到达时间（抖动 EMA 用）

		// --- 平滑状态（P1：SP2 式指数平滑 + 近距快照 + 瞬移；首帧/body 数量变化时快照为 target） ---
		public Vector3d SmoothedPos;        // 平滑后位置（地面坐标，与 data.Position 同系）
		public Quaterniond SmoothedSrfRel;  // 平滑后朝向（相对地表 SrfRel）
		public Vector3[] SmoothedBodyPos;   // 每 body 平滑后相对位置（相对 comRot，与 BodyPositions 同索引）
		// 2026-09-19 body 索引错位修复:BodyData.Id → 幽灵装配索引映射 + 重排缓冲(见 ReorderRemoteBodiesByGhost)。
		public Dictionary<int, int> BodyIdMap;
		public int BodyIdMapVersion = -1;      // 建映射时的幽灵 body 数(变化则重建)
		public object BodyIdMapGhost;          // 建映射时的幽灵 Assembly 引用(被替换则重建,修 bRmap 掉到 2)
		public Vector3[] ReuseReorderPos;      // 重排结果(幽灵序)
		public Vector3[] ReuseReorderRot;
		public Vector3[] ReuseReorderAngVel;   // 2026-09-22 rotating-body-sync:重排 body 角速度(与 pos/rot 平行)
		public Vector3[] ReuseReorderVel;      // 2026-09-22 rotating-body-sync:重排 body 线速度(与 pos/rot 平行)
		public bool[] ReuseReorderFilled;
		public int BodyRemapCount;             // 本帧按 id 成功重排的 body 数(诊断:>0=重排生效)
		public bool BodyMapDiagLogged;         // 首次 bodyMap 诊断行已输出
		public Quaternion[] SmoothedBodyRot; // 每 body 平滑后相对旋转（相对 comRot）
		public bool HasSmoothed;            // 平滑状态是否已初始化

		// --- 每帧复用缓冲（P0/P1 平滑输出；避免热路径每帧分配 List 引发 GC 卡顿） ---
		public readonly List<Vector3> ReuseSmoothBodyPos = new List<Vector3>();
		public readonly List<Vector3> ReuseSmoothBodyRot = new List<Vector3>();
		// 1.4.2:EnforceRemoteCraftVisuals 每帧逐 body 遍历渲染器,复用缓冲避免每帧 GC(GetComponentsInCraft 每次 Clear)。
		public readonly List<Renderer> ReuseRenderers = new List<Renderer>();

		// --- 跳动诊断（定位"0 延迟静止仍跳动"：上一帧已应用位置/每 body 位姿 vs 本帧） ---
		public Vector3d LastRenderedPos;      // 上一帧实际应用的位置（地面坐标）
		public double LastMoveDeltaM;          // 本帧已应用位置相对上一帧的位移（米）
		public float LastBodyPoseDeltaM;       // 本帧每 body 最大位姿位移（米）
		// 2026-09-19 部件抖动诊断:平滑前"目标相对位姿 − 上一平滑值"的最大误差(米)+ 误差>1m 的 body 数。
		// 若 bodyDelta 大而 bodyTgt 小 → 平滑层自身问题;若 bodyTgt 大 → 目标数据跳(发送端甩动/装配重排)。
		public float LastBodyTgtErrM;
		public int LastBodyBigErr;
		public float WinBodyTgtMaxM;           // 3s 窗口:平滑前最大目标误差
		public int WinBodyBigSum;              // 3s 窗口:误差>1m 的 body 帧次累计
		// 变换漂移诊断:本帧写入 Transform 前,实际 Transform.position 相对上一帧写入值的位移(米)。
		// 若 moveDelta=0 而 tfDelta 持续>0 → 游戏层在 Update 写入之后移动了 ghost(滑动来自游戏而非我们的写入)。
		public Vector3 LastWrittenFramePos;    // 上一帧 ApplyRemoteState 后 Transform.position(帧空间)
		public float LastTfDriftM;             // 本帧写入前实际位置相对 LastWrittenFramePos 的漂移
		// 高精度漂移诊断(定位"双方静止仍滑动"):F2/F1 精度下 0.1m/s 级慢漂移显示为 0.00,
		// 必须用 3s 窗口累计量 + F4 位精度才能捕捉。
		public double MoveSumM;                // 3s 窗口内累计渲染位移(moveDelta 累加;0.1m/s 漂移 3s≈0.3m)
		public double PktJumpM;                // 3s 窗口内最大单包位置跳变(定位发送端数据跳变)
		public Vector3d LastPktPos;            // 上一包位置(计算 PktJump)
		public bool HasLastPktPos;
		public double HeadDeg3s;               // 3s 窗口内累计朝向(应用 SrfRel)变化(度;慢旋转也会被感知为滑动)
		public Quaterniond PrevSmoothedSrfRel; // 上一帧平滑朝向(计算 HeadDeg3s)

		// --- 暂停/冻结检测(2026-09:飞船"有速度时暂停"→观察方位置抽搐) ---
		// 暂停时发送端的 Position 冻结、但 Velocity 仍是暂停前最后一刻的值(非零)。
		// 接收端若照常做 Position+Velocity×age 的 dead-reckoning,目标位置会随每包到达被拉回、
		// 又在包间按速度前进 → 以发包频率来回摆动 → 观察方看到"抽搐"(见 plans/latency-smoothing §9.7)。
		// 判据(双保险,任一成立即视为"发送端已冻结"):
		//   ① 包内显式 Paused 标记(本 mod 双端都升级后最可靠);
		//   ② 连续多包位置零位移(兼容旧版本对端;也不依赖标记是否被中继/丢包)。
		public bool RemotePaused;              // 判定:发送端当前处于"位置冻结"(暂停/完全静止)
		public bool LastPktPausedFlag;         // 最新包携带的发送端暂停标记(包内显式字段)
		public long FrozenFrames;              // 完全冻结态(ramp=1)的帧数(诊断)
		public float RemotePausedRamp;         // 0..1 平滑过渡量(0=正常外推,1=完全停止速度外推),避免冻结/解冻瞬间跳变
		public int PktStallCount;              // 连续"位置零位移"包计数
		public int PktStallLimit = 2;          // F6b(2026-09-19):停顿判定阈值(包数→时间),见 PushSample
		public float PktFreezeDeltaM;          // 最近两包位置距离(诊断:是否真的零位移)
		public bool HasPktFreezePos;
		public Vector3d PktFreezePos;
		public double LastAgeNowSec;           // 本帧实际使用的外推量(诊断)
		public float LastAccelTermM;           // 2 阶外推:本帧加速度项位移(½|a|·ext²,m)
		public float LastAngExtRad;            // 2 阶外推:本帧朝向外推角(|ω|·ext,rad)
		// rotating-body-sync(2026-09-22)诊断:窗口内"旋转中的 body"计数与最大 |ω|(rad/s)。
		// 验收判据:叶片 bodyTgt 应从恒 ≈5.9m 降到平滑残差(<0.5m)、bodyBig 归零。
		public int SpinBodyCount;              // 本窗口内 ω>0.001 的 body 数(旋转 body 数)
		public float SpinBodyMaxW;             // 本窗口内最大 body |ω|(rad/s)

		// --- 突发/顿挫诊断(2026-09-14,smoothing-comparison §四:200ms+ 真实联机"一卡一卡"定位) ---
		// 两个候选机制:①mRate 被到达间隔(突发 0/几百 ms)污染 → ext 摆动 → 速度脉冲;
		// ②长静默冻结分支(age>max(3·gapEMA,0.25s),MpNetworkManager.cs:2056)误触发 → 停→冲。
		// 以下窗口量(3s 随 MP smoothing 行输出)+ 事件日志(MP gap / MP gapfreeze)用于区分主导机制。
		public float LastExtSec;                // 本帧外推量 ext(诊断,s)
		public bool GapFreezeActive;            // 本帧长静默冻结分支是否激活(机制 ②)
		public float WinGapFreezeHits;          // 3s 窗口:进入长静默冻结的次数(机制 ② 命中率)
		public float WinMRateMin = 1f;          // 3s 窗口:mRate 最小值(机制 ①:mRate 下摆)
		public float WinMRateMax = 1f;          // 3s 窗口:mRate 最大值(机制 ①:mRate 上摆)
		public float WinExtMax;                 // 3s 窗口:ext 最大值(s)
		public float WinMaxGapMs;               // 3s 窗口:最大到达间隔(ms,突发程度)
		public int WinLongGapCount;             // 3s 窗口:到达间隔 >250ms 的次数(突发静默次数)
		public float WinMoveMaxM;               // 3s 窗口:单帧渲染位移最大值(m,停→冲的"冲"幅度)
		public float LastGapLogTime;            // MP gap 事件日志节流(unscaledTime)

		// --- F1(2026-09-14,smoothing-comparison §五 / README §三):虚拟 age —— 目标推进时钟 ---
		// 突发到达(背靠背 0ms / 静默几百 ms)下,"距最新包到达的真实时间 age"会随包到达归零,
		// 而包位置按发送端间隔前移 → 目标每包锯齿(±V×Δt)→ 渲染层"一卡一卡"。
		// 改为自走时钟:每帧 +unscaledDeltaTime、每包到达 −SendIntervalEst(发送端发包间隔估计)。
		// 连续性证明:包到达瞬间目标跳变 = V×Δt_send − V×(age增长−age扣除) = V×Δt_send−V×(interval−Δt_send) − ... = 0
		// (匀速时目标连续;残留仅剩加速度误差 ½·a·Δt²)。慢放/暂停仍由 mRate/ramp 处理。
		public float VirtualAge;                // 自走外推时钟(替代"距最新包到达时间")
		public float SendIntervalEst;           // 发送端发包间隔估计(包时间差 EMA,钳[0.02,0.1]s)
		// F2'(2026-09-14):mRate 分母抗突发 —— 到达间隔慢 EMA(时间常数≈1s)。
		// 瞬时到达间隔在突发下 0/几百 ms 交替 → mRate 打到 0.03~1.14(实测) → ext 摆动;
		// 慢 EMA 收敛到"平均到达间隔"(=平均发包间隔)→ 稳态 mRate≈1,慢放检测依然有效。
		public float MArrivalEma;
		// F3(2026-09-14):单向延迟 EMA(ext 用;LatencyMs=RTT/2 裸值抖动会直接进 ext → 目标晃)。
		public float LatencyEmaMs = -1f;
		// F1b(2026-09-15):外推时钟(VirtualAge,有界)与静默检测(RealAgeSec,自重置)分离。
		// 2026-09-15 Steam 实测教训:VA 无界积分漂到 30s → gapfreeze 永久闩锁 → 目标退化为"每包一跳"
		// (ageNow=30s、extWin≈rtt/2、moveMax 13~33m 为证)。VA 有界后不可能 windup;
		// realAge 用于 gapfreeze/暂停判定,自重置(每包归零)不可能漂移。
		public float RealAgeSec;                 // 距最新包到达的真实时间(自重置,静默检测用)
		public long WinGapFreezeFrames;          // 3s 窗口:gapfreeze 激活帧数(持续态;旧 WinGapFreezeHits 只数跃迁,永久闩锁时误导为 0)
		public long FramesAtLastLog;             // 上次 3s 日志的 TotalFrames(接收端 fps 计算用)

		// --- 发送端时间倍率(2026-09:慢放时外推按真实时间推进而包位置按发送端缩放时间走 → 每包向后锯齿) ---
		// 相邻两包 FlightState.Time(发送端游戏时间)增量 / 真实到达时间增量;正常=1,慢放<1,暂停→0。
		// UpdateRemoteCrafts 把外推量 ext 乘以此倍率换算到发送端时间基 → 慢放时外推与发送端实际运动同步。
		// ⚠️ 2026-09-13 四轮:用户慢放是 Unity Time.timeScale<1,但游戏 FlightState.Time 不缩放
		// (对端实测 rate 恒 1.000)→ 包时间测不出慢放;而包位置位移确实按慢放速率缩小。
		// 改用 SenderMotionRate(基于包位置位移/速度×真实时间)测量发送端实际运动速率,更鲁棒。
		public float SenderTimeRate = 1f;
		private double _lastPktTime = -1;      // 上一包发送端 FlightState.Time(倍率测量)
		// --- 发送端运动倍率(2026-09 四轮,位置基) ---
		// 相邻两包位置位移 ÷ (速度 × 真实到达间隔):正常飞行≈1,发送端慢放=timeScale,静止/暂停→0。
		// 外推量 ext ×= 此倍率 → 外推与发送端实际运动同步(慢放时不再"外推超前→每包向后锯齿")。
		// ⚠️ 2026-09-13 六轮:单包测量尖刺(切换速度模式瞬间的大间隔/大位移包,实测 mRate 单包
		// 0.208→0.393)经 EMA 仍能透出 → ext 突变 → 幽灵单帧 1.97~2.7m 跳变。修复:速率变化钳制
		// MaxMotionRateStep/包(±0.15 @20Hz → 满量程 0.05↔1.0 收敛仅 ~0.3s,尖刺被压到 ±0.15/包)。
		public float SenderMotionRate = 1f;
		public const float MaxMotionRateStep = 0.15f; // mRate 每包最大变化量(切换瞬间尖刺抑制)

		/// <summary>
		/// 位置零位移判定阈值(米,地面坐标)。发送端暂停时相邻包位置完全相同(≈0);
		/// 0.5 m/s 的慢速漂移在 50ms 包间隔内也有 0.025m,故 0.02m 不会误判正常缓速运动。
		/// </summary>
		public const double PositionStallM = 0.02;
		/// <summary>连续多少个零位移包才认定发送端已冻结(过滤单次丢包/重复包造成的假静止)。</summary>
		public const int PositionStallPackets = 2;

		// --- 抽搐诊断(2026-09:双飞静止一方抽搐,定位"comRot 连带移动 + 双写不一致"反馈环) ---
		// 1.4.2 中 comRot 是 RootPart.Transform 的后代(CraftScript.cs:2172),RootPart 位于根 body 内,
		// 因此"写 body 世界位置"会连带移动 comRot;而 ApplyRemoteBodyPoses 又用 comRot 位姿作基准,
		// 若每帧冻结的 comRotPos 随上帧写入而漂移,body 位置将逐帧漂移(静止时肉眼可见"抽搐")。
		public Vector3 DiagComPosFrozen;       // ApplyRemoteBodyPoses 冻结的 comRot.position(帧空间)
		public Vector3 DiagComPosPrevFrozen;   // 上一帧 Update 路径冻结的 comRot.position(跨帧对比基准)
		public Vector3 DiagComPosAfterBodies;  // 写完所有 body 后 comRot.position(检测连带漂移)
		public Vector3 DiagComPosLate;         // LateUpdate ForceRemoteHeading 冻结的 comRot.position
		public float DiagComLinkM;             // 写 body 前后 comRot 连带位移(|AfterBodies - Frozen|)
		public float DiagComCrossFrameM;       // 跨帧 comRot 漂移(|Frozen - PrevFrozen|)
		public Vector3 DiagBody0PrevWorld;     // 上一帧 body[0] 世界位置(帧空间)
		public bool DiagHasBody0Prev;
		public float DiagBody0DeltaM;          // body[0] 逐帧世界位移(抽搐幅度)
		public float DiagBody0DeltaLateM;      // LateUpdate 重写后 body[0] 位移(双写不一致幅度)
		public Vector3 DiagSmoothedBody0;      // 平滑后 body[0] 相对 comRot 位置(目标)
		public float LastTwitchLogTime;        // 抽搐诊断周期日志计时

		// --- 帧级匀速性诊断(2026-09-22 纯观测,零行为改动;MP ext / MP frame / MP chain / MP diag) ---
		// 依据 acceleration-smoothing 文档 §六之七/十六/十八:r27 定位残余抖动在"目标速度抖"与
		// "帧显示节拍",r30 四层抖动(目标/可见物/部件/显示)并列 + SEG= 自打结论是最有效读法。
		// 所有字段只记录、不参与任何位置计算;总开关 ExtraDiagEnabled 改 false 即关闭全部新日志。
		public const bool ExtraDiagEnabled = true;
		public int DiagPktThisFrame;           // 本帧到达包数(PushSample 自增,UpdateRemoteCrafts 帧首清零)
		public float DiagVaRaw;                // 本帧 VA 钳制前原值(自造时钟是否活着:恒定 0/负 → 时钟死)
		public Vector3d DiagPrevTgtPos;        // 上一帧目标位置(外推后、平滑前;算 tgtMove)
		public bool DiagHasPrevTgtPos;
		public bool DiagClampHit;              // 本帧 maxStep 钳制命中(ApplyRemoteSmoothing 置位)
		public Vector3 DiagFramePrevComPos;    // 上一帧 comRot 世界位置(visAcc 采样)
		public bool DiagHasFramePrevComPos;
		public float DiagFramePrevComVel;      // 上一帧 comRot 速度(visAcc = |v_t − v_{t−1}|,对帧时长抖动免疫)
		public Vector3 DiagFramePrevPartPos;   // 上一帧首部件世界位置(partAcc 采样)
		public bool DiagHasFramePrevPartPos;
		public float DiagFramePrevPartVel;
		public struct FrameSample
		{
			public float DtMs;                 // 本帧时长(ms)
			public float StepM;                // 本帧渲染位移(平滑后,m)
			public float TgtM;                 // 本帧目标推进量(平滑前,m)
			public float Ratio;                // StepM ÷ (vEff×dt),理想恒 1.0
			public int Pkt;                    // 本帧到包数
			public bool Clamp;                 // 本帧是否被 maxStep 钳制
		}
		public const int FrameRingSize = 64;
		public readonly FrameSample[] FrameRing = new FrameSample[FrameRingSize];
		public int FrameRingHead;
		public int FrameRingCount;
		// 3s 窗口累加器(MP frame / MP diag 用;窗口结束时取局部再清零)
		public int DiagWinFrames;              // 窗口有效帧数
		public float DiagWinDtMaxMs, DiagWinDtMinMs = float.MaxValue, DiagWinDtSumMs;
		public float DiagWinRatioMin = float.MaxValue, DiagWinRatioMax;
		public double DiagWinRatioSum; public int DiagWinRatioN;
		public int DiagWinJerk;                // ratio 超出 [0.65,1.35] 的帧数
		public int DiagWinClampF;              // maxStep 钳制命中帧数
		public int DiagWinPktSum;
		public double DiagWinStepSum, DiagWinExpectSum;   // 渲染位移累计 vs 期望位移累计(应≈1)
		public double DiagWinLagSum; public float DiagWinLagMax;   // 目标−渲染距离(平滑器掉队量)
		public double DiagWinVisAccSum, DiagWinVisVelSum; public float DiagWinVisAccMax; public int DiagWinVisN;
		public double DiagWinTgtVelSum; public float DiagWinTgtVelMin = float.MaxValue, DiagWinTgtVelMax; public int DiagWinTgtVelN;
		public double DiagWinPartAccSum; public float DiagWinPartAccMax; public int DiagWinPartN;
		public float LastExtLogTime;           // MP ext 周期日志计时(1s)
		public float LastFrameLogTime;         // MP frame 周期日志计时(3s)
		public float LastDiagLogTime;          // MP diag 周期日志计时(2s)
		public float LastChainLogTime;         // MP chain dump 节流(防刷屏)

		public struct StateSample
		{
			public float ArrivalTime;  // 到达端 Time.unscaledTime（单调）
			public double PacketTime;  // 发送端 FlightState.Time（诊断用）
			public Mod.RemoteDataPack Data;
		}

		/// <summary>环形追加一条样本；按到达时间天然有序，满则覆盖最旧。</summary>
		public void PushSample(float arrivalTime, double packetTime, Mod.RemoteDataPack data)
		{
			// 帧级诊断:本帧到达包数(UpdateRemoteCrafts 帧首清零;供 MP frame/chain 判断"目标是否随包到达成串推进")
			if (RemoteCraft.ExtraDiagEnabled && DiagPktThisFrame < 999) DiagPktThisFrame++;
			// 2026-09-19:按 BodyData.Id 把发送端 body 列表重排为幽灵装配顺序(索引错位修复)。
			// 须在入缓冲前完成,让缓冲/平滑/应用全部按幽灵序索引对齐。
			ReorderRemoteBodiesByGhost(this, data);
			int idx = (BufferHead + BufferCount) % BufferCapacity;
			Buffer[idx] = new StateSample { ArrivalTime = arrivalTime, PacketTime = packetTime, Data = data };
			if (BufferCount < BufferCapacity) BufferCount++;
			else BufferHead = (BufferHead + 1) % BufferCapacity;
			HasState = true;
			NewestArrivalTime = arrivalTime;

			// 包间到达间隔与抖动 EMA（诊断：NetSim 注入的抖动应如实反映到这里）
			if (_lastPushTime >= 0f)
			{
				float gapMs = (arrivalTime - _lastPushTime) * 1000f;
				GapEmaMs = GapEmaMs <= 0f ? gapMs : GapEmaMs * 0.9f + gapMs * 0.1f;
				float dev = Mathf.Abs(gapMs - GapEmaMs);
				JitterEmaMs = JitterEmaMs <= 0f ? dev : JitterEmaMs * 0.9f + dev * 0.1f;
				// 突发窗口统计 + 事件日志(2026-09-14,smoothing-comparison §四):>250ms 到达间隔
				// = 突发静默(真实 Steam relay 特征,NetSim 均匀延迟下不会出现)。
				// 用于区分"网络突发"(接收端 gap 大)与"发送端突发"(对端 sendGap 大)。
				if (gapMs > WinMaxGapMs) WinMaxGapMs = gapMs;
				if (gapMs > 250f)
				{
					WinLongGapCount++;
					if (arrivalTime - LastGapLogTime > 1f)
					{
						LastGapLogTime = arrivalTime;
						Mod.LogLobby("MP gap P" + PlayerId + ": gap=" + gapMs.ToString("F0") + "ms" +
							" gapEMA=" + GapEmaMs.ToString("F0") + "ms" +
							" jitterEMA=" + JitterEmaMs.ToString("F0") + "ms" +
							" mRate=" + SenderMotionRate.ToString("F3") +
							" stall=" + PktStallCount);
					}
				}
			}
			// 保存上一包到达时间(倍率测量用),再覆盖 _lastPushTime。
			// ⚠️ 2026-09-13 五轮:此前 `_lastPushTime = arrivalTime` 在前、倍率块在后,
			// 差值恒为 0 → SenderTimeRate/SenderMotionRate 从未更新、恒 1.000 → 慢放外推从未缩放!
			// 这就是"慢放 1/20 时 1.5 个船身跳变"从未被修好的根因(倍率测量一直是死的)。
			float prevArrival = _lastPushTime;
			_lastPushTime = arrivalTime;

			// 发送端时间倍率:相邻两包 FlightState.Time(发送端游戏时间)增量 / 真实到达时间增量。
			// 慢放(发送端 timeScale<1)时发送端游戏时间推进慢于真实时间 → 倍率<1;暂停(时间冻结)→0。
			// 用于把 dead-reckoning 外推量换算到发送端时间基(见 UpdateRemoteCrafts),
			// 否则慢放时外推按真实时间推进、包位置却按发送端缩放时间走 → 每包向后锯齿(实测慢放最严重)。
			// ⚠️ 2026-09-13 四轮实测:用户的慢放是 Unity Time.timeScale<1,但游戏 FlightState.Time 不缩放
			// (对端 rate 恒 1.000)→ 包时间测不出慢放!真正可靠的测量是"包位置运动倍率"(见下)。
			// F1 修正(2026-09-14):给 VirtualAge 用的「包内容间隔」(见下方扣除处注释)。
			float contentGapSec = -1f;
			if (_lastPktTime >= 0 && prevArrival >= 0)
			{
				double dtPkt = packetTime - _lastPktTime;
				if (dtPkt > 0.0 && dtPkt < 600.0) contentGapSec = (float)dtPkt; // 坏包/时间回绕 → 不采用
				float dtReal = arrivalTime - prevArrival;
				if (dtReal > 0.001f)
				{
					float rate = dtPkt > 0.0 ? (float)(dtPkt / dtReal) : 0f; // 发送端时间冻结(暂停)→0
					if (rate >= 0f && rate < 10f) // 过滤坏包/时间回绕
					{
						SenderTimeRate = SenderTimeRate <= 0f ? rate : SenderTimeRate * 0.9f + rate * 0.1f;
					}
					// F1(2026-09-14):发送端发包间隔估计(包时间差 EMA,钳 [0.002,0.1]s)。
					// FlightState.Time 慢放不缩放 → 该值≈名义发包间隔,慢放由 mRate 管,不受影响;
					// 发送端卡顿(间隔变大)时被钳制到 0.1s,不无限膨胀。
					// ⚠️ 2026-09-19:下限原为 0.02s(按 20Hz 校准),房主把发包频率设到 120Hz 后真实间隔
					// 8.3ms 被钳到 20ms → SendIntervalEst 失真 → 停顿判定阈值缩放(F6a)跟着错。
					// 放宽到 0.002s(支持到 500Hz);上限 0.1s 保留(防重连大间隔污染)。
					if (dtPkt > 0.0)
					{
						float est = (float)Math.Min(Math.Max(dtPkt, 0.002), 0.1);
						SendIntervalEst = SendIntervalEst <= 0f ? est : SendIntervalEst * 0.9f + est * 0.1f;
					}
				}
			}
			_lastPktTime = packetTime;

			// F1(2026-09-14):虚拟 age 时钟(每帧 +dt,见 UpdateRemoteCrafts)。每包到达扣除
			// **该包与上一包的「内容时间增量」contentGapSec**(= 发送端 FlightState.Time 之差):
			// 突发背靠背时扣≈0.05 而非归零 → 目标连续(见字段注释的连续性证明)。
			// ⚠️ 2026-09-14 修:固定扣 SendIntervalEst(EMA,钳 [0.02,0.1])在**丢包**时少扣(内容增量
			// 是 2×间隔却只扣 1×)→ 每丢一包 age 永久多出约一个间隔 → 越过阈值后长期卡冻结。
			// 2026-09-15 Steam 实测:VA 无界漂到 30s,gapfreeze 永久闩锁,目标退化为"每包一跳"。
			// 改用真实内容增量后丢包自动配平;暂停期(发送端时间冻结,contentGapSec=0)回退 SendIntervalEst。
			VirtualAge -= contentGapSec > 0f ? contentGapSec : (SendIntervalEst > 0f ? SendIntervalEst : 0.05f);
			// F1b(2026-09-15):VirtualAge 有界 —— 开环积分器 + 小偏差(暂停期回退间隔、帧率抖动)必然
			// 缓慢漂移,无界时几分钟就攒到几十秒。硬钳到 [0, 2×间隔](≈0.1~0.4s,SP2 式有界外推):
			// 稳态包流下 VA 在 0~间隔 间小幅振荡(连续性保留);长静默时 VA 顶到上界 → 幽灵滑行至有界
			// 距离后停住,不再"瞬间停 + 恢复后追赶"(旧 gapfreeze 机制 B 的停→冲)。
			if (VirtualAge < 0f) VirtualAge = 0f;
			float vaCap = SendIntervalEst > 0f ? SendIntervalEst * 2f : 0.1f;
			if (VirtualAge > vaCap) VirtualAge = vaCap;

			// 发送端运动倍率(2026-09 四轮,位置基):相邻两包位置位移 ÷ (速度 × 每包时间)。
			// 正常飞行:位移 = v×dt → 倍率≈1;发送端慢放(Unity timeScale<1):位移 = v×dt×ts → 倍率=ts;
			// 静止/暂停:位移≈0 → 倍率→0(外推量归零,幽灵精确停包位)。
			if (HasLastPktPos && prevArrival >= 0)
			{
				float mDtReal = arrivalTime - prevArrival;
				// F2'(2026-09-14):到达间隔慢 EMA 作 mRate 分母的抗突发备选(瞬时间隔 0/几百 ms 交替
				// 会把 mRate 打到 0.03~1.14,实测)。慢 EMA 收敛到平均到达间隔。
				if (mDtReal > 0.001f)
				{
					MArrivalEma = MArrivalEma <= 0f ? mDtReal : MArrivalEma * 0.99f + mDtReal * 0.01f;
				}
				// F2'-b(2026-09-19):分母优先用「发送端包内容时间增量」contentGapSec(= FlightState.Time
				// 之差,与 dPos 同源,不受到达抖动/丢包/发包频率影响)—— dPos 是发送端相邻两包的真实
				// 位移 = v×ts×contentGapSec(FlightState.Time 不缩放,实测 rate 恒 1.000)→ mRate=ts 恒准。
				// 原用到达间隔(MArrivalEma)在 120Hz tick 下收敛到突发平均(20~30ms)而真实间隔 8.3ms
				// → mRate 被低估到 0.2~0.35(2026-09-19 VM 实测)→ ext 缩水 → 幽灵滞后。
				// 暂停(发送端时间冻结,contentGapSec=0)时回退 MArrivalEma;都无效则跳过。
				float mDtUse = contentGapSec > 0.001f ? contentGapSec : MArrivalEma;
				if (mDtUse > 0.001f)
				{
					double dPos = Vector3d.Distance(data.Position, LastPktPos);
					float v = (float)data.Velocity.magnitude;
					if (v > 1f && dPos > 0.001)
					{
						float mRate = (float)(dPos / (v * mDtUse));
						if (mRate > 0.0f && mRate < 10f) // 过滤坏包(加速段 v 突变会瞬时失真,EMA 摊平)
						{
							float newMotionRate = SenderMotionRate * 0.9f + mRate * 0.1f;
							// 速率变化钳制:切换速度模式瞬间的单包测量尖刺(实测 0.208→0.393/包 → 幽灵
							// 单帧 2m 跳)不允许直接透出;±0.15/包 @20Hz 下满量程收敛仍仅 ~0.3s。
							newMotionRate = Mathf.Clamp(newMotionRate,
								SenderMotionRate - MaxMotionRateStep, SenderMotionRate + MaxMotionRateStep);
							SenderMotionRate = newMotionRate;
						}
					}
					else
					{
						// 速度≈0 或位移≈0(静止/暂停):发送端没有实际运动 → 倍率收敛到 0,外推量归零。
						// ⚠️ F6b(2026-09-19):contentGapSec≈0 = 同帧重复包/发送端时间冻结(位置未变,
						// 并非"停止运动")—— 无脑收敛会把 mRate 与正常包交替污染到 0.2~0.35(120Hz 实测)
						// → ext 缩水 → 幽灵滞后。仅当发送端时间确实推进(contentGapSec>0)才收敛;
						// 重复包保持上一倍率(真暂停由 stall 时间阈值 + Paused 标记 + ramp 处理)。
						if (contentGapSec > 0.001f)
						{
							float newMotionRate = SenderMotionRate * 0.9f + 0f * 0.1f;
							newMotionRate = Mathf.Clamp(newMotionRate,
								SenderMotionRate - MaxMotionRateStep, SenderMotionRate + MaxMotionRateStep);
							SenderMotionRate = newMotionRate;
						}
					}
				}
			}

			// 包间位置跳变诊断:相邻两包的位置差(发送端数据是否在缓慢漂移/跳变)。
			// 双方"静止"时若此值持续>0,说明滑动来自发送端数据,而非接收端平滑层。
			if (HasLastPktPos)
			{
				double d = Vector3d.Distance(data.Position, LastPktPos);
				if (d > PktJumpM) PktJumpM = d;
			}
			LastPktPos = data.Position;
			HasLastPktPos = true;

			// 暂停/冻结检测:位置零位移连续计数(见字段区注释)。包内 Paused 标记用于"尽快进入"冻结态;
			// 退出冻结态一律以"位置重新开始变化"为准(计数清零)—— 这样对端刚恢复那一瞬间不会立刻
			// 按速度外推(那时包内位置仍是暂停前的旧值,一旦外推就会跳一下)。
			double freezeDelta = HasPktFreezePos ? Vector3d.Distance(data.Position, PktFreezePos) : double.MaxValue;
			PktFreezeDeltaM = HasPktFreezePos ? (float)freezeDelta : 0f;
			// F6a(2026-09-19):停顿判定阈值按发包间隔缩放 —— PositionStallM=0.02m 按 20Hz(50ms)校准,
			// 等价"速度 <0.4m/s ≈ 静止"。房主可设任意发包频率(实测 120Hz,间隔 8.3ms):固定 0.02m
			// 阈值在 120Hz 下每包位移 = v×8.3ms,2.4m/s 时恰好 0.02m → 慢速被反复误判静止 →
			// freeze ENTER/EXIT 抖动(2026-09-19 VM 实测 209 次)→ 幽灵低速一卡一卡。
			// 缩放:stallM = 0.02×(间隔/0.05),任意频率下等价于"速度 <0.4m/s 视为静止"。
			double stallM = PositionStallM * (SendIntervalEst > 0f ? (double)(SendIntervalEst / 0.05f) : 1.0);
			if (stallM < 0.001) stallM = 0.001;
			// F6b(2026-09-19):停顿判定从"包数"改"时间" —— 原 2 包阈值按 20Hz 校准(=100ms)。
			// 房主可设任意发包频率(实测 120Hz):2 包仅 17ms,任何"位置更新慢于发包"的正常情况
			// (同帧重复包/低物理帧率)都够得着 → freeze 抖动(2026-09-19 VM 实测 92 次,vel 恒定 6m/s
			// 但 pkΔ=0.0000m 即为同帧重复包)。limit = ceil(100ms/发包间隔):20Hz=2(不变)、120Hz=12。
			PktStallLimit = Mathf.Clamp((int)Mathf.Ceil(0.1f / Mathf.Max(SendIntervalEst, 0.001f)), 2, 30);
			if (HasPktFreezePos && freezeDelta <= stallM)
			{
				if (PktStallCount < 1000) PktStallCount++;
			}
			else
			{
				PktStallCount = 0;
			}
			PktFreezePos = data.Position;
			HasPktFreezePos = true;
			LastPktPausedFlag = data.Paused;
		}

		/// <summary>取最新样本。</summary>
		public bool TryGetNewest(out Mod.RemoteDataPack data)
		{
			data = default;
			if (BufferCount == 0) return false;
			data = Buffer[(BufferHead + BufferCount - 1) % BufferCapacity].Data;
			return true;
		}

		/// <summary>
		/// 2026-09-19 body 索引错位修复:按 BodyData.Id 把发送端 body 列表重排为接收端幽灵装配顺序。
		/// 实测(2026-09-19):发送端 bodyMaxRelΔ 4~6m/1s、接收端 bodyTgt≈5m 恒定、bodyBig 每帧 7~8 个部件
		/// —— 装配顺序/列表长度不一致(含发送端 BodyScript 瞬时空导致列表变短)把不同部件的位姿互写,
		/// 接收端平滑层持续追赶 → 部件抖动(肉眼"略有卡顿")。根 body 恒一致(body0RelΔ=0),非根错位。
		/// 仅当 data.BodyIds 与 BodyPositions 等长且能按 id 找到幽灵对应 body 时才重排;否则回退索引直用(旧对端)。
		/// 发送端有而幽灵没有的 id → 跳过(无法放置);幽灵有而发送端没有的槽位 → 沿用上一平滑值(部件保持原位)。
		/// 在 PushSample 入缓冲前调用一次,后续缓冲/平滑/应用全部按幽灵序索引对齐。
		/// </summary>
		internal static void ReorderRemoteBodiesByGhost(RemoteCraft rc, Mod.RemoteDataPack data)
		{
			if (data.BodyPositions == null || data.BodyPositions.Count == 0) return;
			if (data.BodyIds == null || data.BodyIds.Count != data.BodyPositions.Count) return;
			if (data.BodyRotations == null || data.BodyRotations.Count != data.BodyPositions.Count) return;
			if (rc.Node == null || rc.Node.CraftScript == null || rc.Node.CraftScript.Data == null) return;
			IReadOnlyList<BodyData> ghostBodies = rc.Node.CraftScript.Data.Assembly.Bodies;
			if (ghostBodies == null || ghostBodies.Count == 0) return;

			if (rc.BodyIdMap == null || rc.BodyIdMapVersion != ghostBodies.Count ||
				!ReferenceEquals(rc.BodyIdMapGhost, rc.Node.CraftScript.Data.Assembly))
			{
				// 2026-09-19:幽灵装配重建(结构刷新/重生成 XML)后 BodyData 实例替换但 count 不变,
				// 旧缓存映射用旧 id → 重排几乎全 miss(实测 bRmap 掉到 2/7,大部分 body 冻结在错误位置)。
				// 以 Assembly 对象引用为键,装配被替换即重建映射。
				rc.BodyIdMap = new Dictionary<int, int>(ghostBodies.Count);
				for (int g = 0; g < ghostBodies.Count; g++)
				{
					if (ghostBodies[g] != null) rc.BodyIdMap[ghostBodies[g].Id] = g;
				}
				rc.BodyIdMapVersion = ghostBodies.Count;
				rc.BodyIdMapGhost = rc.Node.CraftScript.Data.Assembly;
			}
			int gn = ghostBodies.Count;
			if (rc.ReuseReorderPos == null || rc.ReuseReorderPos.Length != gn)
			{
				rc.ReuseReorderPos = new Vector3[gn];
				rc.ReuseReorderRot = new Vector3[gn];
				rc.ReuseReorderAngVel = new Vector3[gn];
				rc.ReuseReorderVel = new Vector3[gn];
				rc.ReuseReorderFilled = new bool[gn];
			}
			for (int g = 0; g < gn; g++) rc.ReuseReorderFilled[g] = false;
			int n = data.BodyPositions.Count;
			int mapped = 0, miss = 0;
			for (int i = 0; i < n; i++)
			{
				int g;
				if (rc.BodyIdMap.TryGetValue(data.BodyIds[i], out g) && g >= 0 && g < gn)
				{
					rc.ReuseReorderPos[g] = data.BodyPositions[i];
					rc.ReuseReorderRot[g] = data.BodyRotations[i];
					rc.ReuseReorderAngVel[g] = (data.BodyAngularVelocities != null && i < data.BodyAngularVelocities.Count)
						? data.BodyAngularVelocities[i] : Vector3.zero;
					rc.ReuseReorderVel[g] = (data.BodyVelocities != null && i < data.BodyVelocities.Count)
						? data.BodyVelocities[i] : Vector3.zero;
					rc.ReuseReorderFilled[g] = true;
					mapped++;
				}
				else
				{
					miss++;
				}
			}
			rc.BodyRemapCount = mapped;
			// 一次性诊断:重排生效性(ids 是否上包、幽灵 id 是否匹配)。mapped=15 → 重排生效;
			// mapped=0 → BodyIds 未上包(旧端)或幽灵 id 与发送端不一致(需另查)。
			if (!rc.BodyMapDiagLogged)
			{
				rc.BodyMapDiagLogged = true;
				Mod.LogLobby("MP bodyMap P" + rc.PlayerId + ": ids=" + n + " ghost=" + gn + " mapped=" + mapped + " miss=" + miss +
					" (mapped=15 → 重排生效;mapped=0 → 旧对端/幽灵 id 不匹配)");
			}
			for (int g = 0; g < gn; g++)
			{
				if (!rc.ReuseReorderFilled[g])
				{
					rc.ReuseReorderPos[g] = (rc.SmoothedBodyPos != null && g < rc.SmoothedBodyPos.Length) ? rc.SmoothedBodyPos[g] : Vector3.zero;
					rc.ReuseReorderRot[g] = (rc.SmoothedBodyRot != null && g < rc.SmoothedBodyRot.Length) ? rc.SmoothedBodyRot[g].eulerAngles : Vector3.zero;
					rc.ReuseReorderAngVel[g] = Vector3.zero; // 未命中:无角速度 → 接收端该 body 无旋转外推(安全)
					rc.ReuseReorderVel[g] = Vector3.zero;    // 未命中:无线速度 → 接收端该 body 无位置外推(安全)
				}
			}
			data.BodyPositions.Clear();
			data.BodyRotations.Clear();
			data.BodyAngularVelocities?.Clear();
			data.BodyVelocities?.Clear();
			for (int g = 0; g < gn; g++)
			{
				data.BodyPositions.Add(rc.ReuseReorderPos[g]);
				data.BodyRotations.Add(rc.ReuseReorderRot[g]);
				if (data.BodyAngularVelocities != null) data.BodyAngularVelocities.Add(rc.ReuseReorderAngVel[g]);
				if (data.BodyVelocities != null) data.BodyVelocities.Add(rc.ReuseReorderVel[g]);
			}
			data.BodyIds = null; // 已重排为幽灵序,后续按索引直用
		}
	}
}
