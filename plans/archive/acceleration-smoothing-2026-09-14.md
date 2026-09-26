# 远程船平滑(acceleration-smoothing)— 2 阶外推 + 回滚复盘 + SP2/LMP 对照

> 状态:✅ **已归档(2026-09-23)**——本主题裁定失败后转彻底重构,归档为历史决策记录;后续主题见 [`smoothing-reset-2026-09-23.md`](smoothing-reset-2026-09-23.md)(⛔ 同已归档失败)。原状态:⛔ **失败(2026-09-23 夜)**——realAge 尝试后用户仍判定观感不平滑,**本主题整体裁定失败,不再做增量修补**;用户决定在**新对话中彻底重构**(详见本文 §〇.4 2026-09-23 夜条目)。本文档保留为历史决策记录:§〇 的 VA/realAge 结论、§二~§五 的排除表、4 个真 bug、方法论教训、R1~R8 对照,供重构时参考。原状态(再往前):🔧 **重新开工(2026-09-22)**——原"一卡一卡"定案**不在位置管线**(⛔ 回滚收工,见 §二/§三);2026-09-22 直升机双端实测定位到**新的真 bug:低速悬停被误判"发送端冻结"**(stall 0.4m/s 阈值误伤悬停,见 §〇 当前在修);旋翼旋转 body 同步已另案修复归档([`rotating-body-sync-2026-09-22.md`](rotating-body-sync-2026-09-22.md))。
> 日期:2026-09-14(创建);2026-09-22(重新开工);2026-09-23(裁定失败,转彻底重构);2026-09-23(归档)
> 关联:[`rotating-body-sync-2026-09-22.md`](rotating-body-sync-2026-09-22.md)(旋翼旋转 body 修复,已结案;本主题不含它);[`refactor-mpnetworkmanager-2026-09-22.md`](refactor-mpnetworkmanager-2026-09-22.md)(上帝类重构,平滑代码现位于 `Net/Sync/` 分层类中);[`latency-smoothing-2026-08-22.md`](latency-smoothing-2026-08-22.md) §9(被移除管线的实现事实)
> 本文档为**历史决策记录**(2026-09-23 归档;原 `proposals/smoothing-comparison-2026-09-14.md` 已并入 §五)。

## 〇、当前在修(2026-09-22 重新开工)— 待重定位

> ⚠️ **2026-09-22 用户反证**:高速并排飞行(非悬停)也能看到**明显卡顿** → "低速悬停误判冻结"**不是当前主因(至少不唯一)**,此前凭单次悬停日志定位属武断(§四 教训 3)。本主题根因**待重定位**,以下为已收集的事实与候选,不是结论。

### 〇.0 用户观察(2026-09-22)

- 高空高速**并排飞行**(两船同向高速、非悬停)接收端可见明显卡顿 —— 与"发送端悬停/不发送"无关。
- 待确认:卡顿表现形态(周期性单帧大跳 / 持续不平滑 / 忽快忽慢)、观察端是哪台(弱机本机 or VM 强机)、是否有双端日志。

### 〇.1 已收集事实(2026-09-22 双端实测:VM=悬停直升机,本机=火箭)

**事实 A —— 低速悬停段误判冻结(真,但用户说高速也卡 → 非主因):**
```
stall:   8→41→101→162→360→678→962→1000   paused=1 长时间
frozen:  44/103 → 141/201 → 212/272 → 283/343 → 352/412 ...  (占比持续 80%+)
mRate:   0.185 → 0.000(后段几乎全程 0)   extMax=0.000   ageNow=0.000
```
VM 自报 `paused=0`(没暂停,只是悬停)。**高速段对照完全正常**(mRate=1.001、stall=0)。

**事实 B —— 高速段也有一次单帧大跳(同批日志,VM 看本机火箭):**
```
fps=19  gapWin=(max=1249ms,>250ms=4)  moveMax=17.80m  pkΔ=0.4391m
fps=38  gapWin=(max=830ms,>250ms=2)   moveMax=15.94m
```
`moveMax=17.8m` = 渲染层**单帧位移 17.8 米**。高速(v≈25m/s)+ 长帧(dt 大)→ `maxStep=1.5·v·dt` 无 dt 钳制 → 长帧下 maxStep 巨大 → 渲染单帧大跳。**这更贴合"高速并排也卡顿"**。

### 〇.2 候选根因(2026-09-23 本地直连实测后:根因坐实 = VA 时钟锯齿)

**实测场景**:用户高空高速并排飞行(HOST=火箭 8 body / VM=直升机)。双端 `MultiPlayer env/ext/frame/chain/diag` 全量采集。frp 中继 → 本地直连两次对照。

**环境差异(先决条件)**:
```
HOST: vSync=1 targetFPS=-1 refresh=165.0Hz maxDeltaTime=0.05s  ← vsync 开 + 高刷屏
VM:   vSync=0 targetFPS=-1 refresh=60.0Hz  maxDeltaTime=0.333s ← 默认
```

**两次对照结论**:

| 指标 | frp 中继(上轮) | 本地直连(本轮) |
|---|---|---|
| RTT | 80~600ms | **11~20ms** |
| gapfreeze 频率 | 每 ~0.25s 一次 | **全程仅 2 次** |
| HOST SEG C1 | 26 次 | **1 次** |
| VM SEG C1 | 4 次 | **0 次** |
| 发送端 gapWin | — | VM=(7-26ms avg15),HOST=(24-103ms avg35),均 0>250ms |
| **观感** | 卡 | **卡顿仍在,持续不平滑(用户确认)** |

→ frp 攒批是"C1 maxStep 钳制 + 周期性突发"的来源,已坐实并消除;**但持续不平滑在直连下仍在** → 根因在 mod 平滑/外推本身,与帧率无关(用户双端 30fps 复现)、与延迟无关(直连 11ms 复现)、与外部 mod 无关。

**根因(2026-09-23 坐实):VA 自走时钟的锯齿 → ext 摆动 → 目标位置摆动**:
- 证据:`MultiPlayer ext` 高速段 `ageNow`(外推包龄)在 **0 ~ 0.054s 间锯齿**(avg 0.024s、max 0.064s),与钳制上限 `SendIntervalEst×2≈0.07s` 吻合;
- 机制:VA 每帧 +dt、每包 −contentGapSec。发包率(30Hz)与渲染帧率(60fps)不成整数比 → VA 在 [0, 2×间隔] 间来回锯齿(无包帧 +16ms、有包帧 +16−33=−17ms 被钳 0)→ `ext = latencySec + ageNow` 摆动 → 目标位置 `= 包位置 + V×ext` 摆动 **V×锯齿 = 100×0.033 ≈ 3.3m/帧级**;
- 链上证据:`chain` 的 `tg`(目标帧间推进)在 **0.00 / 1.04 / 1.95m 剧烈跳动**(HOST 看 VM 船:tg avg=3.13、max=13.62m!),渲染跟着抖 = "持续不平滑";
- **与帧率无关**:双端 30fps 时发包≈20~30Hz vs 渲染 30fps 仍不成整数比 → 锯齿仍在;
- frp 时更严重:到达突发 → contentGapSec 扣除不规律 → 锯齿更深 + 触发 C1。直连后 C1 归零,但 VA 锯齿是平滑算法固有,仍在。

**修复方向(⚠️ 2026-09-23 夜已实施并裁定失败,以下为历史结论)**:
- ⛔ **2026-09-23 曾实施:外推包龄改用真实包龄 realAge**(RemoteCraftDriver)。**实测部分改善未治愈,已标记失败**(详见 §〇.4 2026-09-23 夜条目):VA 恒 0/顶格/漂移类时钟病消除(clamp 55~71f→0~5f),但**包率<帧率时目标"停-走"帧仍在 → 残留 jerk 5~16%** → 用户观感仍不平滑 → 整体失败,转入彻底重构。
  - 曾经的机制结论(供重构参考):VA 自造时钟在锁 30fps(1 帧 1 包)时配平失效 → ageNow 恒 0 → 目标只在包到达时跳 V×c;realAge 修复了这点,但暴露下一层:**"最新包 + 时钟归零"外推在包率<帧率时必然产生停-走帧**。
- 备选(未实施):对 ext 本身做低通;或 VA 扣减改用慢 EMA 的到达间隔(需评估连续性)。
- 不动:发送端采样、传输层(TcpTransport NoDelay 已确认)、暂停保护逻辑。

**已修采样 bug(2026-09-23)**:visAcc 计算先覆盖 `DiagFramePrevComPos` 再取差值 → `DiagFramePrevComVel` 恒 0 → visAcc 错误等于本帧速度(日志 `visVel=84.20 visAcc=84.1976` 全等为证)。已改为先算 vel 再更新 prev(comRot/part 两处)。首帧 `step=1274405m` 为采样伪影(LastRenderedPos 初始 0 vs 行星系绝对坐标),非真实位移。

### 〇.3 修复方向(2026-09-23 实测裁定后聚焦)

- **主战场 = 接收端帧节拍(环境侧),非发送端/网络/平滑算法**:HOST 高速段 clamp 30~60%、dt 8x,VM 高速段 clamp 0~7%、dt 2~4x —— 同一算法两端表现天壤之别,根因在接收端渲染节拍。
- **第一步(环境,非 mod)**:确认观感在哪台。若本机(165Hz):游戏设置关 vSync 或限帧 60(vSync=1@165Hz + 渲染能力不足 → 帧时长 20~166ms 锯齿是源头)。这是**用户侧设置**,改完即可复测,零代码。
- **mod 侧可做的(仅当环境调整后仍卡)**:`RemoteCraftSmoothing.maxStep` 对长帧做**位移预算/钳制**(长帧时 `1.5·v·dt` 巨大 → 单帧大跳),改 dt 生效上限或分帧摊平——单独一处、可二值验收(clamp 命中率下降、moveMax 降到 <5m)。
- **低速悬停误判冻结(C3)**:确认存在但非高速主因,维持降级;可作独立小改(stall 判定加"发送端时间推进"条件)。
- **不做**:不再往位置管线堆机制(§四 教训 4);不引入第二套运动模型;每轮只改一处。

### 〇.4 实施记录

- 2026-09-22:定位"低速悬停误判冻结"并写入本文档(未动手);**同日用户反证:高速并排也明显卡顿 → 该判断降级,本主题待重定位**。
- 2026-09-22(晚):**帧级诊断日志接线完成**(重构后 `RemoteCraft` 字段在 2026-09-22 重构时已保留,但 `MultiPlayer ext/frame/chain/diag/env` 日志从未落地——本次补齐)。全部纯观测、零行为改动、`RemoteCraft.ExtraDiagEnabled` 门控,`dotnet build` 0 错误 0 警告。落点:
  - `RemoteCraftSmoothing.cs`:`DiagClampHit` 置位(maxStep 钳制命中 = **C1 直接证据**);
  - `RemoteCraftDriver.cs`:② `DiagVaRaw` 采样(VA 钳前原值);③a 目标推进量 `tgtMove`(平滑前)、③b `FrameSample` 入环 + 3s 窗口累加(dt 摆幅/ratio/jerk/clamp/step-vs-expect/lag/tgtVel)、③c visAcc(comRot/part 速度差分,帧时长免疫);④ `MultiPlayer env`(一次性:vSync/限帧/maxDeltaTime)、`ext`(1s:latency/ageNow/vaRaw/mRate/ext)、`frame`(3s:帧级统计)、`chain`(条件:clamp 或 step>3m 时 dump 最近 16 帧)、`diag`(2s:四层抖动并列 + **SEG= 自打结论** C1/C2/tgtVelJitter)。
  - 读法:`diag SEG=C1 maxStepClamp` → 长帧+高速单帧大跳坐实,修 `maxStep`;`SEG=C2 frameBeat` → 帧显示节拍,查帧节奏治理;`chain` 展开看卡顿那一帧的 dt/step/tgt。
- 下一步:**用户自己飞"高空高速并排"双端实测**,收 `MultiPlayer env/ext/frame/chain/diag` 裁定 C1 vs C2。
- 2026-09-23:**双端实测完成,裁定已下**(见 §〇.2/〇.3):主源 = HOST 接收端帧节拍(vSync=1@165Hz,dt 8x,clamp 30~60%);VM(vSync=0@60Hz)高速段 clamp 0~7% 基本平滑。另修 visAcc 采样 bug(prev 覆盖顺序)。待用户确认观感屏幕 + 尝试关 HOST vsync/限帧复测。
- 2026-09-23(晚):**用户反证裁定** —— ①双端均锁 30fps 仍复现 → C2 帧节拍非主因,收回;②看过其他 mod 代码 → 排除外部 mod;③**新信息:VM 联机走内网穿透转发服务器(frp),非本地直连** → 方向转向"中继突发到达"。
- 2026-09-23(晚):**中继对照日志增强(纯观测,0 错误 0 警告)**,目标 = 区分「发送端自身突发」vs「中继/frp 攒批到达」:
  - 接收端 `RemoteCraft`:新增 `LastContentGapSec`/`WinMaxContentGapSec`/`WinMinGapMs` 字段;`MultiPlayer gap` 事件加 `cGap`/`cGapMax`(该包与上包的发送端内容时间增量 —— gapMs 大但 cGap 小 → 发送端一直在发、中继攒批;cGap 也大 → 发送端真停);
  - 接收端 `RemoteCraftDriver`:`gapfreeze ENTER` 加 `cGap`;`smoothing` 3s 行 `gapWin` 加 min + `cGapMax`;
  - 发送端 `LocalCraftSender`:`sendDiag` 加 `gapWin=(min-max,avg,>250ms=N)` —— EMA 平滑掉突发,min/max 直接暴露"发一批停一下";
  - 传输层确认:`TcpTransport` 双端 `NoDelay=true`(Nagle 已禁),排除 Nagle;frp 与本地直连同走 TcpTransport(控制台 `TcpHostLobby`/`TcpJoinLobby`)。
- 下一步:**用户本地 IP 直连对照**(VM 用 `TcpJoinLobby <宿主IP> 25555`,与 frp 同一代码路径)对比两端 `sendDiag gapWin` vs `smoothing gapWin/cGapMax/gapfreeze` —— 若直连下 gapfreeze 消失 → 坐实 frp 攒批;若仍在 → 发送端/接收端自身节奏问题。
- 2026-09-23(深晚):**本地直连实测(用户执行),根因坐实 = VA 时钟锯齿** —— 直连(RTT 11~20ms)下 gapfreeze 仅 2 次、C1 归零(HOST 26→1、VM 4→0),但**卡顿仍在、持续不平滑(用户确认)**。`MultiPlayer ext` 高速段 ageNow 在 0~0.054s 锯齿(avg 0.024、max 0.064,与钳制上限 2×SendIntervalEst 吻合);`chain` 的 tg(目标帧间推进)剧烈跳动 0~13.6m(HOST 看 VM 船 avg 3.13m/max 13.62m)→ 目标位置 = 包位置 + V×ext 随 VA 锯齿摆动 ≈3.3m/帧级。与帧率无关(30fps 复现)、与延迟无关(直连 11ms 复现)。修复方向:ext 的 ageNow 先低通(EMA)再外推,消除锯齿;待用户确认实施。
- 2026-09-23(夜):**实施"realAge"修复(外推包龄改用真实包龄,弃用 VA 自走时钟)**:
  - 改动:`RemoteCraftDriver.cs` ageNow 由 `va`(VirtualAge 积分时钟)改为 `Mathf.Min(age, vaCapFrame)`(`age = now − NewestArrivalTime` 真实包龄);`RemoteCraft.cs` VirtualAge 保留仅作诊断(vaRaw),不再进入 ext;注释/字段同步;构建 0 错误 0 警告。
  - 动机(数学):锁 30fps(1 帧 1 包)时 VA 每帧 +33ms、每包同帧 −33ms → VA 恒 0 → ageNow 恒 0 → ext 恒 latencySec → 目标只在包到达时跳 V×c、包间完全不动 —— 这被判定为"双端锁 30fps 仍复现"的直接机制,而 realAge 包到达天然归零、包间每帧 +dt,理论上包到达帧零跳变。
  - **实测结果(用户第 4 次日志,2026-09-23 夜):部分改善,未治愈** —— VM 高速巡航 clamp 从 55~71f/窗口降到 0~5f、ratio 从超走 1.14 回到 0.96~0.98(硬跳变消除,target 匀速性显著改善);**但残留 jerk 5~16%(VM 看 HOST 方向)**,且**方向性不对称**:
    - HOST 看 VM 船(VM 发包 66Hz > HOST 渲染 32fps):每帧多包 → ageNow 恒 0 → 连续,ratio≈0.98~0.997、jerk 4~17;
    - VM 看 HOST 船(HOST 发包 30Hz < VM 渲染 70fps):每 ~2.3 帧才 1 包 → **包到达帧 target 位移 = 0(位置跳 V×c 与 ageNow 归零恰好抵消),包间帧位移 V×dt → 目标"停-走"交替** → 渲染 lag 骤缩/恢复 → 慢帧(ratio 低至 0.03~0.12)计入 jerk。
  - **失败裁定(用户 2026-09-23 夜)**:观感仍不平滑,本尝试标记失败;不再做增量修补,转入**彻底重构**(新对话)。
  - 经验固化:realAge 消除了"VA 恒 0/顶格/负漂"类时钟病,但**只要 包率 < 渲染帧率,任何"最新包 + 时钟归零"外推都会产生目标停-走帧**;平滑层只能衰减不能消除(alpha≈0.54 时停帧后 lag 骤缩 → 渲染慢帧)。重构方向应直接面对"包率与帧率解耦"(插值缓冲/独立运动模型),而不是继续调外推时钟。

## 一、当前代码状态(2026-09-22 重构后基线)

- 构建标记:`MultiPlayer build r10 2026-09-19`(= r6 baseline:r4 id-remap + r5 stable-anchor;r7 orbit / r8 freeze / r9 SP2 dead-reckon 已移除)。
- **代码已按 2026-09-22 上帝类重构分层**:平滑/外推相关现位于 `Net/Sync/`(`LocalCraftSender` 发送端采样 / `RemoteCraft` 接收端状态 + PushSample / `RemoteCraftDriver` 每帧外推驱动 / `RemoteCraftSmoothing` 平滑算法 / `GhostPoseWriter` 位姿写回),日志前缀已统一为 `MultiPlayer`。**行为与 r10 等价的验证见** [`refactor-mpnetworkmanager-2026-09-22.md`](refactor-mpnetworkmanager-2026-09-22.md) §10.4。
- **2 阶外推仍在生效**:平移 `½·a·ext²`(`EnableSecondOrderExtrap=true`);朝向外推 `SrfRel *= Euler(Flip(ω)·ext·sign)`(`EnableRotationExtrap=true`、`RotationExtrapSign=+1`,ω 符号 2026-09-19 sendDiag 实测定案 F+ = 翻转(-x,y,-z) 正号)。
- **现行诊断**:接收端 `MultiPlayer smoothing`(3s)、`MultiPlayer slowmo`(0.5s)、`MultiPlayer twitch`(1s)、事件 `MultiPlayer freeze`/`MultiPlayer gapfreeze`/`MultiPlayer gap`;发送端 `MultiPlayer sendDiag`(1s)、一次性 `MultiPlayer bodyMap`/`MultiPlayer bodyNames`。全部只进 `Player.log`。
- 与 r14+ 时代的差异(均已回滚,不在代码):`MP net`/`MP smooth` 拆行、`os*` 残差诊断、`MP frame`/`MP chain`/`MP ext`/`MP diag`、`MP hier`、F9 直线目标、帧节奏治理器(`FpsGovernor`/`FramePacingGovernor.cs`)、`ModSettings.SmoothingDiagnostics`/`FrameGovernor`。

## 二、结论:原 bug 不在 mod 位置管线

r30 实测:可见物速度抖动仅 **2.5~29%**,而显示帧时长抖动 **171~880%**(`dt` 中位 3.81,是可见抖动的 26 倍);F9(位置完全脱离网络)隔离后抖动不变 ⇒ **残余观感主要来自接收端帧显示节拍**——帧间隔 16~80ms、<45fps 且极不均、游戏不设 vsync/不限帧(`vSyncCount=0 + targetFrameRate=-1`)。

## 三、已排除项(省未来时间)

| 项 | 结论 | 依据 |
|---|---|---|
| 参考帧(浮动原点) | 排除 | 原点确实每帧在动(表面锁定跟行星自转),但可见幽灵由绝对坐标导出、原点在往返中精确抵消;`rootΔ` 恒为常数(即原点位移) |
| 网络/到达 | 排除 | `gapWin` max <230ms、`gapFreezeF=0`;F9 隔离后抖动完全不变 |
| 写入路径/游戏侧 | 排除 | `writeDrift=0.0000m`(游戏没有回推幽灵);直线目标下可见速度抖动仅 3~4% |
| 部件相对位姿(层②) | 排除 | `part` 与 `vis` 数值几乎相同(0.141 vs 0.146),无独立贡献;`bodyTgt`/`bodyBig` 是旋翼自旋扫过圆直径(转得正常) |
| 位置数据量化 | 排除 | 样本最小间距 0.001m、连续分布 |
| 自造时钟 VA | **确认是坏部件** | `MP ext` 实测 `ageRaw=−0.43~−0.54s`、`ext` 被钳 0 ⇒ 旧法里根本没有外推,全靠贴包位置 |

## 四、方法论教训 + 下次重做的正确顺序(2026-09-21 复盘)

失败根因(按重要性):
1. 没有端到端因果链(包→目标→写入→可见物→显示)就开始改,前 20 轮在修旁边的部件(修掉 4 个真 bug,主症状没动)。
2. 测量指标被污染却当证据:`visJit = 位移÷dt` 在 `dt` 摆 4 倍时自身摆 ±50%。
3. 把相关性当因果("低帧率机器更卡 ⇒ 与帧率相关"是样本混杂)。
4. 只加不减、机制堆叠:六个自持状态机互相耦合,排查复杂度超过原 bug(3222→4486 行)。
5. 验证依赖人工观察,没有自我判定的自动化对照(F9 直到 r25 才有)。
6. 改动粒度太大,无法归因。

正确顺序:
1) 先建"可自动化判定的对照"——隔离开关 + 多层抖动并列,只提交纯观测部分,不改行为;
2) 用对照把范围收到**一段**,再动那一段的代码;
3) 每轮**只改一处**,先删被证伪的机制再加新的(净行数不应单调上涨);
4) 绝不引入"自带状态、自己积分"的第二套运动模型;
5) 目标定义保持**唯一**(要么包位置决定,要么包速度决定);
6) 每次复测给出一个**可二值判定**的指标。

**已确认的 4 个真 bug(回滚了,建议按"独立小改动"重做)**:
1. R8 超界检测器恒 0(记录被消费前清零);
2. VA 下界不对称造成的单向上漂(棘轮);
3. `maxStep` 的 dt 钳(0.06s)砍掉长帧合法位移 ⇒ 乘性误差;
4. 插值端点差分放大包节奏抖动。

## 五、SP2 / LMP 对照 + R1~R8 状态

| 维度 | SP2 | LMP | 我们(现行 r10) |
|---|---|---|---|
| 取包 | 最新包 | 队列双状态插值 | 最新包 |
| 外推 | 1 阶 `pos+v·t` 钳 0.25s | 无外推 | 1+2 阶 `pos+v·ext+½a·ext²`;VA 上界 2×间隔、ext 总上限 1.0s |
| 旋转 | `q·AngleAxis(ω·t)` 右乘 | Slerp(无 ω) | `SrfRel *= Euler(ω·ext)` 已开 |
| 包间连续性 | 物理引擎积分(速度写回刚体) | 回放缓冲 | 每帧指数平滑 + `maxStep=1.5·v·dt` |
| 空缓冲/间隙 | 物理兜底 | 冻结保持 | 冻结(ext→latencySec) |
| 时钟依赖 | 需要(权威钟) | 需要(KSP 时间) | 无(RTT/2 + VA) |

| # | 改进 | 状态 |
|---|---|---|
| R1 | 开启旋转外推 | ✅ 已落地(2026-09-19,r10 在) |
| R2 | 延迟估计 EMA(`LatencyEmaMs`) | ✅ 已落地(r10 在) |
| R3 | ext 总上限收紧 1.0→~0.4s | 📋 待议,低优先 |
| R4 | LMP 式空缓冲保持强化 | 📋 待议(与 R8 同源) |
| R5 | 时钟同步 | ❌ 放弃(2026-09-21,用户"不指望") |
| R6 | 速度写回/物理连续性(需开物理) | ⏸ 暂搁(收益<风险,留最后手段) |
| R7 | 速度分段混合插值/外推 | 📋 候选(与 R8 互补) |
| R8 | 超界分批消化(残差 P 控制) | ⛔ 曾落地(build r14)后随本主题一并回滚(双端割裂嫌疑) |
