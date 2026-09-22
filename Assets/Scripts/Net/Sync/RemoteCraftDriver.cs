using System;
using System.Collections.Generic;
using Assets.Scripts.Flight;
using ModApi;
using ModApi.Craft.Parts;
using UnityEngine;
using static Assets.Scripts.Net.Sync.GhostPoseWriter;
using static Assets.Scripts.Net.Sync.MpSyncUtil;
using static Assets.Scripts.Net.Sync.RemoteCraftSmoothing;
using Assets.Scripts.Net.CraftVisual;
using Assets.Scripts.Net.Session;

namespace Assets.Scripts.Net.Sync
{
	/// <summary>
	/// 接收端每帧驱动(2026-09-22 重构:自 MpNetworkManager 逐字搬来):外推时钟推进、冻结/停顿处理、
	/// 2 阶外推、平滑目标计算、位姿写回,以及 LateUpdate 的朝向强制刷新。算法一字未动。
	/// </summary>
	internal class RemoteCraftDriver
	{
		private readonly MpNetworkManager _mp;

		internal RemoteCraftDriver(MpNetworkManager mp) { _mp = mp; }

		/// <summary>每帧插值应用远程飞船状态（朝向直接赋值，与 Replay 一致）。</summary>
		internal void UpdateRemoteCrafts()
		{
			if (FlightSceneScript.Instance == null) return;
			foreach (RemoteCraft rc in _mp.Crafts._remoteCrafts.Values)
			{
				if (rc.Node == null || !rc.HasState) continue;
				try
				{
					// 每帧强制走"表面锁定"分支：游戏可能把 InContactWithPlanet 清掉，
					// 一旦为 false 会走 else 分支推进轨道（带引力）导致飞船坠落/掉进地里。
					rc.Node.InContactWithPlanet = true;

					// CraftScript 可能延迟构建：先做一次懒初始化（幻影模式），未就绪则跳过本帧
					if (!rc.IsInitialized) _mp.Crafts.InitializeRemoteCraft(rc);
					if (!rc.IsInitialized) continue;

					rc.TotalFrames++;
					// 帧级诊断(2026-09-22 纯观测):帧首清零计数;writeDrift = 游戏在帧间自己动了幽灵多少
					// (本帧开头读到的 Transform.position − 上次 mod 写完记下的值;r27 判决性指标,>0.005m 即游戏侧移动)。
					if (RemoteCraft.ExtraDiagEnabled)
					{
						rc.DiagPktThisFrame = 0;
						rc.DiagClampHit = false;
						if (rc.HasApplied && rc.Node.CraftScript != null && rc.Node.CraftScript.Transform != null)
						{
							try { rc.LastTfDriftM = (rc.Node.CraftScript.Transform.position - rc.LastWrittenFramePos).magnitude; }
							catch { }
						}
					}
					// SP2 式:不用插值缓冲,始终拿最新包,按速度连续外推(dead-reckoning),再 per-frame 平滑。
					// 插值缓冲在 Steam 突发间隔下 gapEMA 滞后→lookback 过小→频繁欠载→幽灵跳到最新包
					// →"一卡一卡"。SP2 直接拿最新包,Lerp 平滑过渡,不发散不抖动,不依赖缓冲(CraftStateSerializer.cs:76-86)。
					if (rc.TryGetNewest(out Mod.RemoteDataPack latest))
					{
						// 连续外推(dead-reckoning):外推量 = 单向延迟(RTT/2) + 距最新包到达已过的时间(age)。
						// 目标随时间持续以最新速度前进 → 包到达/丢失/突发都不再让目标跳变 → 消除"一卡一卡"。
						// 旧逻辑外推量固定为 gapEMA(仅≈发包间隔,500ms+ 真实网络延迟下严重低估→幽灵恒滞后→大跳)。
						// SP2 用 physicsTime-senderTime 测单向延迟;我们没有时钟同步,用 RTT/2 近似(OnPong 更新)。
						// F3(2026-09-14,smoothing-comparison §五 / README §三):单向延迟 EMA —— RTT/2 裸值随 ping 抖动,
						// 直接进 ext 会晃目标(0.95/0.05 → 时间常数≈1s;首测直取)。
						float latencySec;
						if (rc.LatencyMs > 0f)
						{
							rc.LatencyEmaMs = rc.LatencyEmaMs <= 0f ? rc.LatencyMs : rc.LatencyEmaMs * 0.95f + rc.LatencyMs * 0.05f;
							latencySec = rc.LatencyEmaMs / 1000f;
						}
						else latencySec = rc.GapEmaMs / 1000f;
						// F1b(2026-09-15):外推时钟与静默检测分离。
						// VirtualAge(有界,见 PushSample 钳制):负责目标连续性 —— 每帧 +dt、每包 −内容增量,
						// 突发到达时目标连续(锯齿消除)。**仅用于外推,不做任何判定**(有界积分器仍有残余偏差)。
						// RealAgeSec(自重置):距最新包到达的真实时间,用于 gapfreeze/暂停等"静默判定"。
						// ⚠️ 2026-09-15 Steam 实测教训:曾用 VirtualAge 做判定 → 无界漂到 30s → gapfreeze 永久
						// 闩锁 → age 分量被砍 → 目标只在包到达时前进 → "一卡一卡"(extWin≈rtt/2、ageNow≈30s 为证)。
						rc.VirtualAge += Time.unscaledDeltaTime;
						// F1b-4(2026-09-19):VA 钳制必须在每帧执行 —— 原只在 PushSample(每包)钳制,
						// 静默期无包 → 钳制不运行 → VA 无界增长(2026-09-19 Steam 实测 ageNow 涨到 11s、
						// ext 顶到 1.0s 上限 → 幽灵滑出 V×1s、恢复期每包目标跳 V×contentGapSec)。
						// 每帧钳制后:ageNow ≤ 2×间隔,ext ≤ latencySec+2×间隔 < 1.0 永不触顶;
						// 连续性不破(钳制只缩短外推量,包到达时 VA−=contentGapSec 照常配平,
						// contentGapSec≤cap 时包到达零跳变;>cap 的发送端卡顿残留 V×(gap−cap),由平滑层摊平)。
						float vaCapFrame = rc.SendIntervalEst > 0f ? rc.SendIntervalEst * 2f : 0.1f;
						if (rc.VirtualAge > vaCapFrame) rc.VirtualAge = vaCapFrame;
						float age = Time.unscaledTime - rc.NewestArrivalTime; // realAge:静默检测(自重置)
						rc.RealAgeSec = age;
						float va = rc.VirtualAge;                            // 有界外推时钟(目标连续性)
						// ★ 暂停/冻结保护(2026-09,修"飞船有速度时暂停→观察方位置抽搐"):
						// 发送端暂停后 Position 冻结、Velocity 仍是非零旧值(暂停前最后一刻的速度)。
						// 此时若继续 Position + Velocity×age:每包到达把目标拉回近处、包间又按速度推进
						// → 目标以发包频率来回摆动 → 渲染层直接"抽搐"(高速船 k≈1 时平滑几乎不衰减)。
						// 修正:冻结期间把外推量按 RemotePausedRamp 收敛到固定单向延迟 latencySec(不带 age),
						// 即"停在最新包位置 + 网络传输本身占用的那段位移",不再人工推进目标。
						// 恢复运动(或对端解除暂停)时 ramp 在 0.15s 内回落到 0,重新把"包龄"加回外推量,避免瞬间跳变。
						bool pausedNow = rc.PktStallCount >= rc.PktStallLimit || rc.LastPktPausedFlag; // F6b:时间制阈值
						if (pausedNow != rc.RemotePaused)
						{
							// 一次性状态跃迁日志(便于实测确认"暂停=冻结"是否按预期生效):
							// 进入冻结 → 速度外推被抑制,幽灵停在最新包位置;退出冻结 → 恢复正常 dead-reckoning。
							Mod.LogLobby("MP freeze P" + rc.PlayerId + ": " + (pausedNow ? "ENTER" : "EXIT") +
								" (flag=" + (rc.LastPktPausedFlag ? 1 : 0) + " stall=" + rc.PktStallCount +
								" pkΔ=" + rc.PktFreezeDeltaM.ToString("F4") + "m vel=" + latest.Velocity.magnitude.ToString("F2") + "m/s)");
						}
						rc.RemotePaused = pausedNow;
						rc.RemotePausedRamp = Mathf.MoveTowards(rc.RemotePausedRamp, pausedNow ? 1f : 0f,
							Time.unscaledDeltaTime / RemoteFreezeBlendSec);
						// 冻结期把外推量中的"包龄"部分收敛到 0 → ext → 固定单向延迟 latencySec,
						// 目标不再随包龄前进,也就没有"每包拉回/包间推进"的锯齿摆动。
						// ⚠️ 必须无条件收敛(不能加 ageNow>latencySec 守卫):暂停时发送端降频(8Hz),
						// 包龄往往小于 latencySec,旧守卫会漏钳制 → 高速船(如 85m/s)依旧锯齿(2026-09 实测发现)。
						float ageNow = va; // 外推用的"包龄"= 有界 VirtualAge(判定用 realAge,见上)
						if (rc.RemotePausedRamp > 0f)
						{
							ageNow = Mathf.Lerp(ageNow, 0f, rc.RemotePausedRamp);
						}
						rc.LastAgeNowSec = ageNow;
						float gapFreezeThr = Mathf.Max(rc.GapEmaMs * 3f / 1000f, 0.25f);
						bool gapFreezeNow = age > gapFreezeThr;
						// F1b(2026-09-15):长静默不再强制 ext=latencySec —— VA 已钳到 [0,2×间隔],
						// 静默期间 ext = latencySec + 顶格 VA(恒定)→ 幽灵滑行至有界距离后停住(SP2 式有界外推)。
						// 旧逻辑在 >250ms 突发间隙即触发"停→冲"(smoothing-comparison §四 机制 B;实测 gapfreeze
						// 永久闩锁后目标每包一跳)。检测与日志保留,仅作诊断;持续激活帧数进 3s 窗口统计。
						float ext = latencySec + ageNow; // 正常:持续外推;暂停期:ageNow→0 → ext→latencySec
						if (gapFreezeNow) rc.WinGapFreezeFrames++;
						if (gapFreezeNow != rc.GapFreezeActive)
						{
							rc.GapFreezeActive = gapFreezeNow;
							if (gapFreezeNow)
							{
								rc.WinGapFreezeHits++;
								Mod.LogLobby("MP gapfreeze P" + rc.PlayerId +
									": ENTER age=" + age.ToString("F3") + "s thr=" + gapFreezeThr.ToString("F3") + "s" +
									" gapEMA=" + rc.GapEmaMs.ToString("F0") + "ms" +
									" mRate=" + rc.SenderMotionRate.ToString("F3") +
									" vel=" + latest.Velocity.magnitude.ToString("F1") + "m/s" +
									" ext→" + ext.ToString("F3") + "s");
							}
							else
							{
								Mod.LogLobby("MP gapfreeze P" + rc.PlayerId +
									": EXIT age=" + age.ToString("F3") + "s thr=" + gapFreezeThr.ToString("F3") + "s" +
									" mRate=" + rc.SenderMotionRate.ToString("F3"));
							}
						}
						// 换算到发送端时间基:慢放(发送端 timeScale<1)时发送端包位置只按缩放时间推进,
						// 若外推仍按真实时间跑 → 目标每包"超前→拉回"锯齿(幅度 v×发包间隔×(1−倍率),慢放最严重)。
						// 正常飞行倍率≈1 → 行为不变;发送端暂停(倍率→0) → ext→0,幽灵精确停在包位置。
						// ⚠️ 用位置基 SenderMotionRate(发送端实际运动速率):用户慢放实测 FlightState.Time 不缩放
						// (包时间倍率恒 1.000),只有包位置位移如实反映慢放(2026-09-13 四轮)。
						ext *= rc.SenderMotionRate;
						if (ext > 1.0f) ext = 1.0f; // 安全上限 1s
						// 顿挫诊断窗口量(2026-09-14):ext / mRate 的 3s 摆动幅度 + 单帧渲染位移峰值。
						rc.LastExtSec = ext;
						if (ext > rc.WinExtMax) rc.WinExtMax = ext;
						if (rc.SenderMotionRate < rc.WinMRateMin) rc.WinMRateMin = rc.SenderMotionRate;
						if (rc.SenderMotionRate > rc.WinMRateMax) rc.WinMRateMax = rc.SenderMotionRate;
						latest.Position = latest.Position + latest.Velocity * ext;
						// 2 阶外推(2026-09-14,acceleration-smoothing):加速度项 ½·a·ext²。
						// ext 已 ×SenderMotionRate 换算到发送端时间基 → 加速度项 = ½·a·(ext·mRate)²,
						// 慢放/暂停天然兼容(暂停 mRate→0 → 两项都→0,与冻结逻辑无冲突)。
						// 发送端已 EMA+钳制,这里做二次防御(NaN/幅值),坏包不污染外推。
						rc.LastAccelTermM = 0f;
						rc.LastAngExtRad = 0f;
						if (EnableSecondOrderExtrap && ext > 0f)
						{
							Vector3 a = latest.Acceleration;
							if (!IsFinite(a)) a = Vector3.zero;
							float aMag = a.magnitude;
							if (aMag > MaxAccelMs) a *= (MaxAccelMs / aMag);
							if (aMag > 0.0001f)
							{
								latest.Position += a * (0.5f * ext * ext);
								rc.LastAccelTermM = 0.5f * aMag * ext * ext;
							}
						}
						// 朝向外推(2 阶域:旋转速率)。ω 为 craft 局部系(ModApi 约定),右乘
						// SrfRel *= Euler(ω_local·ext)。符号已实测确认(2026-09-19):F+ = (-x,y,-z) 正号。
						if (EnableRotationExtrap && ext > 0f)
						{
							Vector3 w = latest.AngularVelocity;
							if (!IsFinite(w)) w = Vector3.zero;
							float wMag = w.magnitude;
							if (wMag > MaxAngVelRad) w *= (MaxAngVelRad / wMag);
							if (wMag > 0.0001f)
							{
								Vector3 wLocal = new Vector3(-w.x, w.y, -w.z) * RotationExtrapSign;
								latest.SrfRel = Quaterniond.FromQuaternion(
									latest.SrfRel.ToQuaternion() * Quaternion.Euler(wLocal * ext * Mathf.Rad2Deg));
								rc.LastAngExtRad = wMag * ext;
							}
						}
						rc.ExtrapolatedFrames++; // 计数(SP2 风格:每帧按速度外推)
						if (rc.RemotePausedRamp >= 1f) rc.FrozenFrames++;
						rc.InterpPct = 1f;       // 始终在最新包(无缓冲插值)

						// P1:SP2 式平滑(指数收敛 + 近距快照 + 瞬移)
						latest = ApplyRemoteSmoothing(rc, latest, Time.unscaledDeltaTime, ext);
						// 跳动诊断:本帧实际应用位置相对上一帧的位移(0 延迟+静止时应≈0;>0.5m 即跳动)。
						rc.LastMoveDeltaM = rc.HasApplied ? Vector3d.Distance(latest.Position, rc.LastRenderedPos) : 0.0;
						rc.LastRenderedPos = latest.Position;
						rc.MoveSumM += rc.LastMoveDeltaM;
						if (rc.LastMoveDeltaM > rc.WinMoveMaxM) rc.WinMoveMaxM = (float)rc.LastMoveDeltaM;
						// 朝向累计变化:慢旋转同样会被感知为"滑动"(尤其 body 相对质心有偏移时)
						if (rc.HasApplied)
						{
							rc.HeadDeg3s += Quaternion.Angle(rc.PrevSmoothedSrfRel.ToQuaternion(), rc.SmoothedSrfRel.ToQuaternion());
						}
						rc.PrevSmoothedSrfRel = rc.SmoothedSrfRel;
						ApplyRemoteState(rc, latest);
						// 每帧用插值后状态驱动幽灵船尾焰(液体 Route A 经 override、航发直接驱动)
						EngineVisualSync.DriveGhostEngineVisuals(rc);
						// 位置误差诊断:当前渲染位置 vs 最新包位置(SP2 式下始终≈0,因为始终用最新包)
						rc.LastPosErrorM = 0.0; // SP2 式:直接用最新包,无滞后
					}

					// 诊断：周期性记录平滑/网络状态（每 3 秒）：
					// 缓冲余量、实测包间间隔与抖动 EMA、欠载命中率、插值比例、渲染位置滞后。
					// 配合 NetSim 延迟模拟：抖动 EMA 应≈NetSim 抖动量；欠载%>0 即说明发生了"冻结-跳变"。
					if (Time.unscaledTime - rc.LastSmoothingLogTime > 3f)
					{
						// 接收端帧率(窗口内 TotalFrames 增量/窗口时长;F5 前发送循环每帧最多一包,帧率直接
						// 决定发包率 → 与对端 sendDiag 的 fps/sendHz 对账才能发现帧率-发包率耦合)。
						float winDt = Time.unscaledTime - rc.LastSmoothingLogTime;
						float winFps = winDt > 0.1f ? (rc.TotalFrames - rc.FramesAtLastLog) / winDt : 0f;
						rc.FramesAtLastLog = rc.TotalFrames;
						rc.LastSmoothingLogTime = Time.unscaledTime;
						// 3s 窗口累计量:F2/F1 精度下 0.1m/s 级慢漂移显示为 0.00,必须用累计量+高精度捕捉。
						double move3s = rc.MoveSumM, pktJump = rc.PktJumpM;
						double headDeg = rc.HeadDeg3s;
						// 顿挫诊断窗口量(2026-09-14):⚠️ 先取局部、再清零、后打印(此前清零在前 → 恒打印 0)。
						float wMRateMin = rc.WinMRateMin, wMRateMax = rc.WinMRateMax, wExtMax = rc.WinExtMax;
						float wMaxGap = rc.WinMaxGapMs, wMoveMax = rc.WinMoveMaxM;
						float wBodyTgtMax = rc.WinBodyTgtMaxM; int wBodyBig = rc.WinBodyBigSum; // 2026-09-19 部件抖动诊断
						long wGapFreeze = rc.WinGapFreezeFrames; // 持续激活帧数(而非跃迁计数,永久闩锁也能看见)
						int wLongGap = rc.WinLongGapCount;
						rc.MoveSumM = 0; rc.PktJumpM = 0; rc.HeadDeg3s = 0;
						rc.WinGapFreezeHits = 0; rc.WinGapFreezeFrames = 0; rc.WinMRateMin = 1f; rc.WinMRateMax = 1f; rc.WinExtMax = 0f;
						rc.WinMaxGapMs = 0f; rc.WinLongGapCount = 0; rc.WinMoveMaxM = 0f;
						rc.WinBodyTgtMaxM = 0f; rc.WinBodyBigSum = 0;
						rc.SpinBodyCount = 0; rc.SpinBodyMaxW = 0f;
						string newestPos = "?", vel = "?", accStr = "?", wStr = "?";
						try
						{
							if (rc.TryGetNewest(out Mod.RemoteDataPack nw))
							{
								newestPos = nw.Position.x.ToString("F4") + "," + nw.Position.y.ToString("F4") + "," + nw.Position.z.ToString("F4");
								vel = nw.Velocity.magnitude.ToString("F3");
								accStr = nw.Acceleration.magnitude.ToString("F1");
								wStr = nw.AngularVelocity.magnitude.ToString("F2");
							}
						}
						catch { }
						// 朝向:应用 SrfRel 的 Yaw 角(连续日志对比可发现慢旋转——同样会被感知为"滑动")
						string headYaw = "?";
						try { headYaw = rc.SmoothedSrfRel.ToQuaternion().eulerAngles.y.ToString("F1"); } catch { }
						Mod.LogLobby("MP smoothing P" + rc.PlayerId +
							": buf=" + rc.BufferCount + "/" + RemoteCraft.BufferCapacity +
							" fps=" + winFps.ToString("F0") +
							" rtt/2=" + (rc.LatencyMs > 0f ? (rc.LatencyEmaMs > 0f ? rc.LatencyEmaMs : rc.LatencyMs).ToString("F0") : "?") + "ms" +
							" gapEMA=" + rc.GapEmaMs.ToString("F0") + "ms jitterEMA=" + rc.JitterEmaMs.ToString("F0") + "ms" +
							" recvHz=" + (rc.GapEmaMs > 0f ? (1000f / rc.GapEmaMs).ToString("F1") : "?") +
							" frames=" + rc.TotalFrames + " snap=" + rc.SnapFrames + " extrap=" + rc.ExtrapolatedFrames +
							" frozen=" + rc.FrozenFrames + " paused=" + (rc.RemotePaused ? 1 : 0) +
							" rate=" + rc.SenderTimeRate.ToString("F3") + " mRate=" + rc.SenderMotionRate.ToString("F3") +
							" mRateWin=(" + wMRateMin.ToString("F2") + "," + wMRateMax.ToString("F2") + ")" +
							" extWin=(max=" + wExtMax.ToString("F3") + "s)" +
							" gapWin=(max=" + wMaxGap.ToString("F0") + "ms,>250ms=" + wLongGap + ")" +
							" gapFreezeF=" + wGapFreeze +
							" moveMax=" + wMoveMax.ToString("F2") + "m" +
							" ageNow=" + rc.LastAgeNowSec.ToString("F3") + "s age=" + rc.RealAgeSec.ToString("F3") + "s stall=" + rc.PktStallCount +
							" pkΔ=" + rc.PktFreezeDeltaM.ToString("F4") + "m" +
							" interpPct=" + rc.InterpPct.ToString("F2") +
							" moveDelta=" + rc.LastMoveDeltaM.ToString("F2") + "m bodyDelta=" + rc.LastBodyPoseDeltaM.ToString("F2") + "m" +
							" bodyTgt=" + wBodyTgtMax.ToString("F2") + "m bodyBig=" + wBodyBig + " bRmap=" + rc.BodyRemapCount +
							" spin=" + rc.SpinBodyCount + " wMax=" + rc.SpinBodyMaxW.ToString("F1") + "rad/s" +
							" tfDrift=" + rc.LastTfDriftM.ToString("F2") + "m" +
							" move3s=" + move3s.ToString("F3") + "m pktJump=" + pktJump.ToString("F3") + "m" +
							" vel=" + vel + "m/s acc=" + accStr + "m/s² aExt=" + rc.LastAccelTermM.ToString("F2") + "m" +
							" w=" + wStr + "rad/s headYaw=" + headYaw + "deg head3s=" + headDeg.ToString("F1") + "deg" +
							" newest=(" + newestPos + ") posErr=" + rc.LastPosErrorM.ToString("F2") + "m");
					}

					// 慢放诊断(2026-09,0.5s 周期):接收端慢放(Time.timeScale<0.99)或发送端慢放(rate<0.99)时
					// 单独输出。背景:用户"本机慢放看静止对端船 → 整船一跳一跳",但 comRot/body 变换全恒定
					// (twitch 全 0),说明跳动在诊断采样点之外的渲染路径(根 transform/部件世界位置/地图渲染)。
					// 这里逐帧追踪 craft 根、首个部件、comRot 的最大单帧位移 + 观察距离,定位跳动来源。
					bool receiverSlow = Time.timeScale < 0.99f;
					if (receiverSlow || rc.SenderTimeRate < 0.99f || rc.SenderMotionRate < 0.99f)
					{
						Transform rootT = null, partT = null, comT = null;
						Vector3 rootP = Vector3.zero, partP = Vector3.zero, comP = Vector3.zero;
						try
						{
							if (rc.Node.CraftScript != null)
							{
								rootT = rc.Node.CraftScript.Transform;
								comT = rc.Node.CraftScript.CenterOfMass;
								if (rc.Node.CraftScript.Data != null && rc.Node.CraftScript.Data.Assembly.Parts.Count > 0)
									partT = rc.Node.CraftScript.Data.Assembly.Parts[0].PartScript.Transform;
							}
						}
						catch { }
						if (rootT != null) rootP = rootT.position;
						if (partT != null) partP = partT.position;
						if (comT != null) comP = comT.position;
						if (rc.DiagHasPrevSlowmo)
						{
							float dRoot = (rootP - rc.DiagPrevRootPos).magnitude;
							float dPart = (partP - rc.DiagPrevPartPos).magnitude;
							float dCom = (comP - rc.DiagPrevComPos).magnitude;
							if (dRoot > rc.DiagMaxRootDelta) rc.DiagMaxRootDelta = dRoot;
							if (dPart > rc.DiagMaxPartDelta) rc.DiagMaxPartDelta = dPart;
							if (dCom > rc.DiagMaxComDelta) rc.DiagMaxComDelta = dCom;
						}
						rc.DiagHasPrevSlowmo = true;
						rc.DiagPrevRootPos = rootP; rc.DiagPrevPartPos = partP; rc.DiagPrevComPos = comP;

						if (Time.unscaledTime - rc.LastSlowmoLogTime > 0.5f)
						{
							rc.LastSlowmoLogTime = Time.unscaledTime;
							string dist = "?";
							try
							{
								if (rc.Node.CraftScript != null && FlightSceneScript.Instance != null &&
									FlightSceneScript.Instance.CraftNode != null && FlightSceneScript.Instance.CraftNode.CraftScript != null)
								{
									dist = (rc.Node.CraftScript.FramePosition - FlightSceneScript.Instance.CraftNode.CraftScript.FramePosition).magnitude.ToString("F0");
								}
							}
							catch { }
							Mod.LogLobby("MP slowmo P" + rc.PlayerId +
								": timeScale=" + Time.timeScale.ToString("F3") +
								" rate=" + rc.SenderTimeRate.ToString("F3") +
								" mRate=" + rc.SenderMotionRate.ToString("F3") +
								" rootΔ=" + rc.DiagMaxRootDelta.ToString("F3") + "m" +
								" partΔ=" + rc.DiagMaxPartDelta.ToString("F3") + "m" +
								" comΔ=" + rc.DiagMaxComDelta.ToString("F3") + "m" +
								" dist=" + dist + "m" +
								" paused=" + (rc.RemotePaused ? 1 : 0) +
								" ageNow=" + rc.LastAgeNowSec.ToString("F3") + "s" +
								" moveDelta=" + rc.LastMoveDeltaM.ToString("F2") + "m" +
								" pkΔ=" + rc.PktFreezeDeltaM.ToString("F4") + "m");
							rc.DiagMaxRootDelta = 0f; rc.DiagMaxPartDelta = 0f; rc.DiagMaxComDelta = 0f;
						}
					}

					// 抽搐诊断(2026-09:双飞静止一方抽搐):每 1 秒高精度输出 comRot 连带/跨帧漂移
					// 与 body[0] 逐帧位移,定位反馈环来源:
					// - comLink   = 写 body 前后 comRot 连带位移(>0 说明"写 body 连带移动 comRot"存在);
					// - comCross = 跨帧冻结 comRot 漂移(>0 说明基准被上帧写入污染 → body 逐帧漂移);
					// - b0Δ      = body[0] 逐帧世界位移(渲染层抽搐幅度,静止时应≈0);
					// - b0ΔLate  = LateUpdate 重写造成的 body[0] 位移(Update/LateUpdate 双写不一致幅度);
					// - comLate  = LateUpdate 冻结的 comRot 位置(与 Update 冻结值差 >0 → 双写基准不同)。
					if (Time.unscaledTime - rc.LastTwitchLogTime > 1f)
					{
						rc.LastTwitchLogTime = Time.unscaledTime;
						Mod.LogLobby("MP twitch P" + rc.PlayerId +
							": comLink=" + rc.DiagComLinkM.ToString("F4") + "m" +
							" comCross=" + rc.DiagComCrossFrameM.ToString("F4") + "m" +
							" b0d=" + rc.DiagBody0DeltaM.ToString("F4") + "m" +
							" b0dLate=" + rc.DiagBody0DeltaLateM.ToString("F4") + "m" +
							" comFrozen=(" + rc.DiagComPosFrozen.x.ToString("F2") + "," + rc.DiagComPosFrozen.y.ToString("F2") + "," + rc.DiagComPosFrozen.z.ToString("F2") + ")" +
							" comAfter=(" + rc.DiagComPosAfterBodies.x.ToString("F2") + "," + rc.DiagComPosAfterBodies.y.ToString("F2") + "," + rc.DiagComPosAfterBodies.z.ToString("F2") + ")" +
							" comLate=(" + rc.DiagComPosLate.x.ToString("F2") + "," + rc.DiagComPosLate.y.ToString("F2") + "," + rc.DiagComPosLate.z.ToString("F2") + ")");
					}

					// 诊断：周期性记录远程飞船可见性（每 3 秒），用于定位"无法显示对方 craft"。
					// 若 goActive 变 false 或 renderer 被禁用，说明游戏原生机制在隐藏幽灵飞船。
					if (Time.unscaledTime - rc.LastVisualLogTime > 3f)
					{
						rc.LastVisualLogTime = Time.unscaledTime;
						try
						{
							GameObject rgo = rc.Node.GameObject;
							int rendererCount = 0, enabledCount = 0;
							if (rgo != null)
							{
								// 1.4.2:body 脱离 craft 层级后 GetComponentsInChildren 遍历不到 body 上的渲染器,
								// 改用逐 body 遍历(GetComponentsInCraft,等价游戏新 API)。
								List<Renderer> renderers = new List<Renderer>();
								CraftUtils.GetComponentsInCraft(rc.Node, renderers, true);
								foreach (Renderer r in renderers) { rendererCount++; if (r.enabled) enabledCount++; }
							}
						}
						catch (Exception e) { Mod.LogError("MP visualDiag error (p" + rc.PlayerId + "): " + e.Message); }
					}

					

					// 朝向诊断日志已暂时禁用（朝向已修复）
					//LogRemoteHeadingDiag(rc, rc.Target);
				}
				catch (Exception e) { Mod.LogError("UpdateRemoteCrafts error: " + e.Message); }
			}
		}

		/// <summary>
		/// 游戏更新后、渲染前,强制应用远程飞船朝向(LunaMultiplayer 方案:RotateY(θ_recv_planet)×SrfRel)+ body,
		/// 防止游戏 Update 阶段覆盖 transform/CenterOfMass 朝向(如 RecalculateCenterOfMass 把质心朝向
		/// 覆盖为命令舱逻辑朝向)。
		/// </summary>
		internal void LateUpdateWriteBacks()
		{
			if (!_mp.IsConnected || _mp.Crafts._remoteCrafts.Count == 0) return;
			foreach (RemoteCraft rc in _mp.Crafts._remoteCrafts.Values)
			{
				if (rc.Node == null || rc.Node.CraftScript == null || !rc.HasState || !rc.HasApplied) continue;
				try
				{
					// 用"最近一次实际应用"的插值状态写回朝向（而非最新包 Target），
					// 避免"Update 插值 → LateUpdate 被最新包覆盖"导致的朝向跳变。
					ForceRemoteHeading(rc, rc.LastApplied);
				}
				catch (Exception e) { Mod.LogError("LateUpdate refresh error (P" + rc.PlayerId + "): " + e.Message); }
			}
		}
	}
}
