using System;
using System.Collections.Generic;
using UnityEngine;
using static Assets.Scripts.Net.Sync.MultiPlayerSyncUtil;

namespace Assets.Scripts.Net.Sync
{
	/// <summary>
	/// 接收端平滑(SP2 式指数收敛 + 近距快照 + 瞬移 + 旋转 body 逐帧积分;
	/// 2026-09-22 重构:自 MultiPlayerNetworkManager 静态方法逐字搬来,算法与参数一字未动)。
	/// </summary>
	internal static class RemoteCraftSmoothing
	{
		/// <summary>
		/// P1:SP2 式平滑——把 P0 的插值/外推结果(Target)做指数收敛 + 近距快照 + 大误差瞬移,
		/// 返回新的 RemoteDataPack(位置/朝向/每 body 位姿已平滑;BodyPositions/BodyRotations 为新建列表,
		/// 不改动 target/缓冲样本)。参考:SP2 CraftStateSerializer.cs:78-94(速度自适应 k + 瞬移 + Slerp)、
		/// BodyScript.cs:660-679(10·dt 平滑 + 近距快照)。首帧/body 数量变化时快照为 target。
		/// </summary>
		internal static Mod.RemoteDataPack ApplyRemoteSmoothing(RemoteCraft rc, Mod.RemoteDataPack target, float dt, float ext)
		{
			// 防御:目标位姿含非有限值(坏包/越界)时直接快照,防 NaN 传播到 Transform 造成瞬移/消失。
			if (!IsFinite(target.Position) || !IsFinite(target.Velocity))
			{
				rc.SmoothedPos = target.Position;
				rc.SmoothedSrfRel = target.SrfRel;
				SnapSmoothedBodies(rc, target);
				rc.HasSmoothed = true;
				return target;
			}

			// 首帧/无平滑状态:直接快照为初始平滑值(避免从原点/上一艘船位置滑过来)
			if (!rc.HasSmoothed || dt <= 0f)
			{
				rc.SmoothedPos = target.Position;
				rc.SmoothedSrfRel = target.SrfRel;
				SnapSmoothedBodies(rc, target);
				rc.HasSmoothed = true;
				return target;
			}

			// --- 位置:静止锁定 + 速度自适应指数平滑(SP2 :84-85),大误差直接瞬移自愈(SP2 :78-81) ---
			float speed = (float)target.Velocity.magnitude;
			Vector3d smoothedPos;
			// 极低速且误差微小:直接锁到目标,杜绝指数平滑的缓慢蠕动(肉眼可见的"位置滑动")。
			// 判定放宽:speed<0.5 m/s(容忍发送端残余速度)且误差<0.05 m → 快照。
			// (旧判定 speed<0.05 && 误差<0.01m 过严:发送端残余速度稍>0.05 即永不锁死 → 平滑层永远在蠕动,
			//  且收敛时间常数≈1s,任何残差都被拖成肉眼可见的持续滑动,而 moveDelta<0.005m/帧 在 F2 日志里显示 0.00)
			if (EnablePositionIntegrator)
			{
				// P2(2026-09-24):自由运行积分 + 有界误差回收,取代下面整段"指数平滑 + maxStep 钳制"。
				// 结构不变量:单帧位移 ∈ [1−IntegMaxCorrFrac, 1+IntegMaxCorrFrac]×V×dt
				// ⇒ 不会停顿、不会后退、不会出现 2~3× 速度尖峰(此前 0.5×~2.6× 抖动的来源)。
				smoothedPos = IntegrateGhostPosition(rc, target.Velocity, dt);
			}
			else if (speed < 0.5f && (target.Position - rc.SmoothedPos).sqrMagnitude < 0.0025f)
			{
				smoothedPos = target.Position;
			}
			else
			{
				// F7(2026-09-15):高速最低平滑 —— 原 k 在 v≥50m/s 时=1 → alpha≈1 → 渲染 1:1 跟随目标
				// 跳变(2026-09-15 Steam 实测 posErr=0、moveDelta 单帧 10~33m,帧率越低越明显)。
				// 上限 0.6:匀速飞行时引入的滞后是常数(不可见);残余目标跳变被低通吸收(~2-3 帧摊平,
				// 与下方 1.5·v·dt 步长上限叠加),观感不再 1:1 透出突发。低速段(<50m/s)行为不变。
				float k = Mathf.Lerp(0.1f, 0.6f, Mathf.Min(1f, speed * 0.02f));
				// SP2 是"逐物理步(50Hz) Lerp(position, target, k)" → 等价帧率无关收敛速率 dt*50:
				// k=0.1 时时间常数≈0.2s(旧 dt*10 是≈1s,慢 5 倍 → 残差蠕动拖出可见滑动)。
				float alpha = 1f - Mathf.Pow(1f - k, dt * 50f);
				smoothedPos = Vector3d.Lerp(rc.SmoothedPos, target.Position, alpha);
				// 单帧位移上限(2026-09 三轮):平滑位置每帧最多移动 1.5×v×dt(物理可行上限)。
				// 高速机动/加减速时包到达会造成目标异常跳变(实测单帧 moveDelta 可达 5.8m、pkΔ 13m),
				// k→1 时 alpha≈1 全跟 → 渲染层单帧大跳 = 肉眼顿挫;改 k 上限又会给匀速飞行引入人工滞后。
				// 用速度上限摊平异常跳变:匀速飞行单帧目标移动 = v×dt < 上限 → 零影响(无滞后);
				// 异常跳变按上限逐帧收敛(13m → ~4 帧摊平),不滞后正常运动。
				// 静止锁定分支(上一行 if)不经过这里,低速蠕动抑制不受影响。
				Vector3d stepDelta = smoothedPos - rc.SmoothedPos;
				double maxStep = 1.5 * speed * dt;
				double stepLen = stepDelta.magnitude;
				if (stepLen > maxStep && maxStep > 1e-4)
				{
					smoothedPos = rc.SmoothedPos + stepDelta * (maxStep / stepLen);
				}
			}
			if (Vector3d.Distance(smoothedPos, target.Position) > 100.0) smoothedPos = target.Position;
			rc.SmoothedPos = smoothedPos;

			// --- 朝向:Slerp 平滑(SP2 :94 用 2.5*dt) ---
			float alphaRot = Mathf.Clamp01(2.5f * dt);
			Quaternion smoothedSrf = Quaternion.Slerp(rc.SmoothedSrfRel.ToQuaternion(), target.SrfRel.ToQuaternion(), alphaRot);
			rc.SmoothedSrfRel = Quaterniond.FromQuaternion(smoothedSrf);

			// --- 每 body 相对位姿:10·dt 平滑 + <0.01 近距快照(SP2 BodyScript.cs:660-679);数量变化时重对齐并快照 ---
			// 结果写进 rc.ReuseSmooth* 复用缓冲(ApplyRemoteState→LastApplied→LateUpdate 同帧读取,无跨帧别名问题),
			// 避免热路径每帧分配 List 引发 GC 卡顿。
			int n = target.BodyPositions != null ? target.BodyPositions.Count : 0;
			Mod.RemoteDataPack result = target;
			if (n > 0)
			{
				if (rc.SmoothedBodyPos == null || rc.SmoothedBodyPos.Length != n)
				{
					SnapSmoothedBodies(rc, target); // 重对齐
				}
				rc.ReuseSmoothBodyPos.Clear();
				rc.ReuseSmoothBodyRot.Clear();
				float alphaBody = Mathf.Clamp01(10f * dt);
				// rotating-body-sync:旋转 body 用更高收敛率(50·dt)直接跟随外推目标 —— 10·dt 追不上
				// 旋翼转速(31 rad/s 叶片每帧转 ~0.5rad,10·dt 每帧只追 ~0.09rad → 永远滞后 + 跳)。
				// 高 alpha 让渲染紧贴"包内相位 × ω·ext"的连续旋转轨迹;非旋转 body 保持 10·dt 不变。
				float alphaBodySpin = Mathf.Clamp01(50f * dt);
				float maxBodyDelta = 0f;
				float maxBodyTgtErr = 0f;
				int bodyBigErr = 0;
				for (int i = 0; i < n; i++)
				{
					Vector3 tpos = (target.BodyPositions != null && i < target.BodyPositions.Count) ? target.BodyPositions[i] : rc.SmoothedBodyPos[i];
					Quaternion trot = (target.BodyRotations != null && i < target.BodyRotations.Count) ? Quaternion.Euler(target.BodyRotations[i]) : rc.SmoothedBodyRot[i];
					// rotating-body-sync(2026-09-22):旋翼叶片等高速旋转 body —— 20Hz 位置快照下
					// 每包相位跳 90°+(实测 bodyTgt≈5.9m 恒定),10·dt 平滑追不上 → 叶片"跳着转"。
					// 修复 = 模拟 SP2 的"写回刚体速度由 PhysX 积分":我们没有 PhysX(幽灵 kinematic),
					// 故每帧手动积分 —— 平滑状态先按发送端速度推进(位置 sp += v·dt、切线方向随 ω 旋转、
					// 朝向绕 ω 轴转),再向包内目标收敛(修正积分误差)。推进让包间叶片连续转,
					// 包到目标与推进后的状态只差积分误差(小)→ bodyTgt 从 5.9m 降到残差。
					//   - ω = body 局部系角速度(发送端采样 rigidbody.angularVelocity 转局部系);
					//   - v = body 相对 comRot 线速度(发送端数值差分 BodyPositions);
					//   - 轴 = ω 经"当前平滑朝向"转回 comRot 系:sr·ω̂(主轴在 comRot 系方向恒定)。
					// 桨毂不在 comRot 上也没关系:v 直接就是"相对位置变化率",推进不需要知道旋转中心。
					// 旧对端无 ω/v(EOF→null)→ 不进入此分支,行为不变。
					bool bodySpinning = false;
					float bodyWMag = 0f;
					Vector3 bodyV = Vector3.zero;
					if (EnableBodyRotationExtrap &&
						target.BodyAngularVelocities != null && i < target.BodyAngularVelocities.Count)
					{
						Vector3 wLocal = target.BodyAngularVelocities[i];
						if (IsFinite(wLocal))
						{
							float wMag = wLocal.magnitude;
							if (wMag > 0.001f)
							{
								if (wMag >= BodySpinThresholdRad) bodySpinning = true;
								bodyWMag = wMag;
								if (wMag > MaxBodyAngVelRad) { wLocal *= (MaxBodyAngVelRad / wMag); wMag = MaxBodyAngVelRad; }
								if (target.BodyVelocities != null && i < target.BodyVelocities.Count)
								{
									bodyV = target.BodyVelocities[i];
									if (!IsFinite(bodyV)) bodyV = Vector3.zero;
								}
							}
						}
					}
					Vector3 prevSp = rc.SmoothedBodyPos[i];
					// 部件抖动诊断(2026-09-19):平滑前目标误差 = 该 body 相对位姿单帧跳变(>1m 即异常:
					// 发送端部件甩动 或 装配索引错位/重排)。平滑后 bodyDelta 大 + bodyTgt 大 → 数据源跳。
					// rotating-body-sync:旋转 body 的 terr 因推进而大幅缩小(推进后状态≈包内相位)。
					float terr = (tpos - prevSp).magnitude;
					if (terr > maxBodyTgtErr) maxBodyTgtErr = terr;
					if (terr > 1.0f) bodyBigErr++;
					Vector3 sp = prevSp;
					Quaternion sr = rc.SmoothedBodyRot[i];
					float alphaUse = bodySpinning ? alphaBodySpin : alphaBody;
					if (bodySpinning)
					{
						// 逐帧积分推进:本帧旋转增量 qFrame(轴 = 主轴在 comRot 系方向,由当前平滑朝向转出)。
						Vector3 wLocal = target.BodyAngularVelocities[i];
						float wMag = wLocal.magnitude;
						if (wMag > MaxBodyAngVelRad) wLocal *= (MaxBodyAngVelRad / wMag);
						Vector3 axisCom = sr * (wLocal / wMag);
						if (!IsFinite(axisCom)) axisCom = Vector3.zero;
						if (axisCom.sqrMagnitude > 0.0001f)
						{
							Quaternion qFrame = Quaternion.AngleAxis(wMag * dt * Mathf.Rad2Deg, axisCom);
							// 位置:v 存 body 局部系(叶片局部切线方向恒定),用当前平滑朝向 sr 转回
							// comRot 系 → 方向随旋转自动累计(第 n 帧方向 = 包时刻方向转 n·帧角,即当前切线)。
							// 欧拉积分 sp += v·dt,误差 O(dt²),60fps 下 ~0.5rad/帧。
							Vector3 vAdv = sr * bodyV;
							sp = sp + vAdv * dt;
							// 朝向:绕主轴推进(叶片随桨毂公转时朝向绕主轴转)。
							sr = qFrame * sr;
						}
						// 向包内目标收敛:修正积分误差(推进≈真实运动 → 残差小;高 alpha 快速吸收)。
						if ((tpos - sp).sqrMagnitude < 0.01f) sp = tpos;
						else sp = Vector3.Lerp(sp, tpos, alphaUse);
						if (Quaternion.Angle(sr, trot) < 0.01f) sr = trot;
						else sr = Quaternion.Slerp(sr, trot, alphaUse);
					}
					else
					{
						if ((tpos - sp).sqrMagnitude < 0.01f) sp = tpos;
						else sp = Vector3.Lerp(sp, tpos, alphaUse);
						if (Quaternion.Angle(sr, trot) < 0.01f) sr = trot;
						else sr = Quaternion.Slerp(sr, trot, alphaUse);
					}
					float bd = (sp - prevSp).magnitude;
					if (bd > maxBodyDelta) maxBodyDelta = bd;
					rc.SmoothedBodyPos[i] = sp;
					rc.SmoothedBodyRot[i] = sr;
					rc.ReuseSmoothBodyPos.Add(sp);
					rc.ReuseSmoothBodyRot.Add(sr.eulerAngles);
					// rotating-body-sync 诊断:旋转 body 计数与最大 |ω|(smoothing 行输出)
					if (bodySpinning)
					{
						rc.SpinBodyCount++;
						if (bodyWMag > rc.SpinBodyMaxW) rc.SpinBodyMaxW = bodyWMag;
					}
				}
				rc.LastBodyPoseDeltaM = maxBodyDelta;
				rc.LastBodyTgtErrM = maxBodyTgtErr;
				rc.LastBodyBigErr = bodyBigErr;
				if (maxBodyTgtErr > rc.WinBodyTgtMaxM) rc.WinBodyTgtMaxM = maxBodyTgtErr;
				rc.WinBodyBigSum += bodyBigErr;
				result.BodyPositions = rc.ReuseSmoothBodyPos;
				result.BodyRotations = rc.ReuseSmoothBodyRot;
			}

			// 组装结果(结构体拷贝;只替换平滑后的字段,其余沿用 target;列表用复用缓冲,不分配、不污染缓冲样本)
			result.Position = smoothedPos;
			result.SrfRel = Quaterniond.FromQuaternion(smoothedSrf);
			return result;
		}

		/// <summary>
		/// P2(2026-09-24)自由运行位置积分器 + 有界误差回收(取代"锚点外推 + 指数平滑 + maxStep")。
		///
		/// 每帧三步:
		///   ① 速度向包速度做 EMA(时间常数 <see cref="IntegVelTauSec"/>)—— 低通,防止把包内速度噪声积分成位置抖动;
		///   ② 位置严格按速度积分 → **Δpos ≡ V×dt**,与包到达节奏、帧时长完全无关;
		///   ③ 用锚点(包位置 + 速度×(单向延迟 + EMA 包龄),由 RemoteCraftDriver 每帧写入 rc.IntegAnchorPos)
		///      做**有界**误差回收:残差 > V×<see cref="IntegMaxErrSec"/> 的部分直接吞掉(瞬移/丢包积欠,不回收),
		///      其余按 (1−exp(−dt/<see cref="IntegErrTauSec"/>)) 回收,且单帧上限 <see cref="IntegMaxCorrFrac"/>×V×dt。
		///
		/// 结构不变量:单帧位移 ∈ [1−IntegMaxCorrFrac, 1+IntegMaxCorrFrac]×V×dt
		/// ⇒ 结构上不可能停顿、不可能后退、不可能出现 2~3× 速度尖峰。
		/// (2026-09-24 实测旧管线:幽灵逐帧速度 max/avg = 2.3~2.7×,与发包率 20~120Hz 无关、
		///  与帧时长无关,而同帧时长下本机船为 1.00× → 抖动源就是旧的位置构造本身。)
		///
		/// 验收指标(RemoteCraftPoseProbe 内建):gSpeed max/avg → ≤1.3×;并排窗口 relCoMMax → ≈|Δv|×dt。
		/// 发送端暂停(RemotePausedRamp>0)时保持位置、速度衰减 → 沿用原"暂停即冻结"语义。
		/// </summary>
		internal static Vector3d IntegrateGhostPosition(RemoteCraft rc, Vector3d packetVel, float dt)
		{
			if (dt <= 0f) return rc.IntegHas ? rc.IntegPos : rc.IntegAnchorPos;
			if (!rc.IntegHas)
			{
				rc.IntegPos = rc.IntegAnchorValid ? rc.IntegAnchorPos : Vector3d.zero;
				rc.IntegVel = packetVel;
				rc.IntegHas = true;
			}
			// ① 速度 EMA(包速度低通)。P2b:优先用"位置流推导速度"(与锚点同源)——实测上报速度与
			// 位置推进速率不自洽会让积分器与锚点持续拉开(残差 2~7m、回收打满),改用同源速度后残差趋零。
			Vector3d velSrc = rc.HasPosDerivedVel ? rc.PosDerivedVel : packetVel;
			// P2b 诊断:两种速度源之差(m/s)。若该值远大于 0(尤其接近行星自转线速度 158.85 m/s 量级),
			// 即坐实"包内上报速度与位置推进不自洽"(历史问题 #10:游戏侧速度缺自转项)。
			if (rc.HasPosDerivedVel) rc.IntegVelSrcDiffMs = (float)(rc.PosDerivedVel - packetVel).magnitude;
			double kv = 1.0 - Math.Exp(-dt / IntegVelTauSec);
			rc.IntegVel = rc.IntegVel + (velSrc - rc.IntegVel) * kv;
			// 暂停/冻结:保持位置,速度向 0 衰减(等价原 ageNow→0 + mRate→0)
			if (rc.RemotePausedRamp > 0f)
			{
				rc.IntegVel = rc.IntegVel * (1.0 - rc.RemotePausedRamp);
				return rc.IntegPos;
			}
			// ② 积分:严格 V×dt(× mRate 换算发送端时间基,慢放兼容)
			float mRate = rc.SenderMotionRate > 0.01f ? rc.SenderMotionRate : 1f;
			rc.IntegPos = rc.IntegPos + rc.IntegVel * (double)(dt * mRate);
			// ③ 有界误差回收
			if (rc.IntegAnchorValid)
			{
				Vector3d e = rc.IntegAnchorPos - rc.IntegPos;
				double em = e.magnitude;
				double vMag = rc.IntegVel.magnitude;
				double maxErr = Math.Max(vMag * IntegMaxErrSec, 0.05);
				if (em > maxErr)
				{
					// 硬账:只保留 maxErr,其余直接吞掉(避免瞬移把可见位置拽走)
					rc.IntegPos = rc.IntegPos + e * (1.0 - maxErr / em);
					e = rc.IntegAnchorPos - rc.IntegPos;
					em = e.magnitude;
					if (rc.IntegHardResync < 1000000) rc.IntegHardResync++;
				}
				double corr = 1.0 - Math.Exp(-dt / IntegErrTauSec);
				Vector3d step = e * corr;
				double stepM = step.magnitude;
				double maxStep = Math.Max(vMag * IntegMaxCorrFrac * dt, 1e-4);
				if (stepM > maxStep)
				{
					step = step * (maxStep / stepM);
					stepM = maxStep;
					if (rc.IntegClampFrames < 1000000) rc.IntegClampFrames++;
				}
				rc.IntegPos = rc.IntegPos + step;
				rc.IntegLastErrM = (float)em;
				rc.IntegLastCorrM = (float)stepM;
			}
			return rc.IntegPos;
		}

		/// <summary>把 target 的 body 位姿快照进平滑数组(首帧 / body 数量变化时调用)。</summary>
		internal static void SnapSmoothedBodies(RemoteCraft rc, Mod.RemoteDataPack target)
		{
			int n = target.BodyPositions != null ? target.BodyPositions.Count : 0;
			if (rc.SmoothedBodyPos == null || rc.SmoothedBodyPos.Length != n)
			{
				rc.SmoothedBodyPos = new Vector3[Mathf.Max(0, n)];
				rc.SmoothedBodyRot = new Quaternion[Mathf.Max(0, n)];
			}
			for (int i = 0; i < n; i++)
			{
				rc.SmoothedBodyPos[i] = (target.BodyPositions != null && i < target.BodyPositions.Count) ? target.BodyPositions[i] : Vector3.zero;
				rc.SmoothedBodyRot[i] = (target.BodyRotations != null && i < target.BodyRotations.Count) ? Quaternion.Euler(target.BodyRotations[i]) : Quaternion.identity;
			}
		}
	}
}
