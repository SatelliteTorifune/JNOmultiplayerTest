# 平滑管线归零重建(smoothing-reset)— 彻底移除 + 重新打 log + 重构

> 状态:⛔ **失败,已归档(2026-09-23)**——阶段 A(物理时钟轴)/ 阶段 B(PhysX 接管,SP2 架构)全量实施并 10+ 轮双端实测:接收端渲染台阶 moveMax≈v×50ms(VM)/v×100ms(HOST)**与发包率(17~86Hz)、与所有 mod 侧写入方式无关**(逐帧写/混合写回/插值/钳制/刚体 kinematic/kill-switch patch 均无效,见 §七/§九);结论:台阶由游戏自身渲染管线按固定节拍驱动,mod 侧已达极限。**代码已回滚至上个 commit(d09be5c),任务终止。**
> 日期:2026-09-23(创建)
> 关联:[`archive/acceleration-smoothing-2026-09-14.md`](archive/acceleration-smoothing-2026-09-14.md)(前身主题:三轮重试失败的平滑调整,已归档;本文档取代其"继续打补丁"路线,其结论作历史参考);[`archive/latency-smoothing-2026-08-22.md`](archive/latency-smoothing-2026-08-22.md) §9(被移除管线的实现事实);[`archive/refactor-mpnetworkmanager-2026-09-22.md`](archive/refactor-mpnetworkmanager-2026-09-22.md)(平滑代码所在分层结构)。
> 本文档已**归档(⛔ 失败)**;acceleration-smoothing 已归档至 [`archive/acceleration-smoothing-2026-09-14.md`](archive/acceleration-smoothing-2026-09-14.md)(2026-09-23;其 §〇 VA/realAge 结论随 P1 直写基线一并弃用/验证)。

---

## 〇、结论先行(2026-09-23 拍板)

- 用户已**三轮重试**平滑调整(工作区未提交的 realAge 修复 + git `rotation fixing` / `大粪` / `cca9620`),观感仍"一坨屎" → 停止在旧机制上打补丁。
- **拍板:彻底移除接收端平滑 / 外推 / 时钟 / 聚合诊断 → 以"最新包直写"为第一个可运行基线 → 重打最小化证据链日志定位根因 → 重构。**
- **边界(用户选定:接收端归零、协议字段保留)**:
  - 删:接收端全部平滑 / 外推 / 时钟判定 / 旧聚合诊断(RemoteCraft 里 100+ 诊断字段、RemoteCraftDriver 七种周期日志、RemoteCraftSmoothing 指数收敛)。
  - 留:**协议字段与编解码**(Acceleration / AngularVelocity / BodyAngularVelocities / BodyVelocities 保留,接收端不用即失效,向后兼容);发送端 2 阶采样保留(可后续关停)。
  - 留:**GhostPoseWriter 坐标系公式与写回**、body 重排(`ReorderRemoteBodiesByGhost`)、部件 / 控制 / 尾焰同步、生命周期。
- **基线(用户选定)**:最新包直接快照——零平滑零外推,拿到包就直写 Transform。
- **流程(用户选定)**:先落盘本文档 → 再开始 P1(代码)。
- 可行性:**可行**。归零重建正是 acceleration-smoothing §四 方法论(每轮只改一处、先删被证伪机制、不做第二套运动模型)的自然终点;直写基线是历次排查里从未有过的**干净对照**,能回答"卡顿到底是否来自平滑层"这个一直没回答过的问题。

---

## 一、动机

1. **效果**:三轮重试(VA/realAge 修复等)后观感仍不平滑,用户判定不可再在现有机制上打补丁。
2. **复杂度**:接收端平滑已叠出**六个自持状态机**(VirtualAge / realAge / mRate / SenderTimeRate / stall / RemotePaused),互相耦合;acceleration-smoothing §四 自己已承认"排查复杂度超过原 bug(3222→4486 行)"。
3. **方法论缺口**:历次排查缺"隔离到一段"的自动对照(§四教训 1/5)——没有人回答过"平滑层到底有没有用、有没有害"。归零后直写基线正好补上。

---

## 二、现状盘点:平滑相关代码全景

接收端渲染管线每帧 = 取最新包 → **外推(dead-reckoning)** → **2 阶外推** → **指数平滑** → 写回 Transform。

| 类别 | 文件 | 内容 | 处置 |
|---|---|---|---|
| 外推 / 时钟(纯平滑) | `Net/Sync/RemoteCraftDriver.cs:81-228` | ext 计算(latency EMA + realAge)、暂停冻结 ramp、mRate 换算、2 阶外推、1s 上限 | 删 |
| 指数平滑(纯平滑) | `Net/Sync/RemoteCraftSmoothing.cs:20-216` | 整船位置/朝向指数收敛 + 静止锁定 + maxStep + 100m 瞬移 + per-body 10·dt / 50·dt + 旋转 body 逐帧积分 | ⚠️ 位置/朝向部分删;**per-body 与位姿同步耦合,拆开** |
| 时钟 / 判定状态(纯平滑) | `Net/Sync/RemoteCraft.cs` | VirtualAge、realAge、mRate / SenderTimeRate、stall、GapEma / JitterEma、SendIntervalEst、RemotePaused 全家 | 删 |
| 发送端 2 阶数据(纯平滑) | `Net/Sync/LocalCraftSender.cs:339-365` | 加速度 / 角速度 EMA + 钳制;body 角速度 / 线速度差分 | 协议保留,采样保留(接收端不用) |
| 写回路径(非平滑) | `Net/Sync/GhostPoseWriter.cs` | 坐标系公式、GroundedSurface*、body 摆放、FlightData 刷新 | ⛔ 必须保留 |
| 协议核心(非平滑) | `Net/MultiPlayerMessages.cs` + `Mod.cs` RemoteDataPack | Position / Velocity / SrfRel / Heading / body 位姿 / 部件 / 控制 | ⛔ 必须保留 |
| 聚合诊断日志 | `RemoteCraftDriver.cs:361-606` + RemoteCraft 诊断字段 | smoothing / ext / frame / chain / diag / twitch / slowmo 七种周期行 | 清掉重写 |
| 发送端节拍(F4/F5/F6b) | `LocalCraftSender.cs:405-435` | 帧率解耦、泄洪钳制、同帧重复包抑制 | ⚠️ 非平滑,保留(独立变量) |

**关键耦合点**:`ApplyRemoteSmoothing` 一个函数同时干"整船位置/朝向指数平滑"和"per-body 位姿收敛 + 旋转 body 积分"两件事(RemoteCraftSmoothing.cs:110-210)。"彻底移除"不是删文件,是**先拆开这个耦合体**——这正是 P4 重构的核心。

---

## 三、边界分析:哪些必须保留

- **坐标系公式(GhostPoseWriter)**:地表 / 行星空间互转、GroundedSurface*、comRot 逻辑位姿、body 世界摆放公式——这是"船摆得对不对"的地基,与平滑无关,动它等于重新踩所有坑(速度自转项、comRot 反馈环、1.4.2 层级)。
- **body 重排(ReorderRemoteBodiesByGhost)**:按 BodyData.Id 对齐幽灵装配顺序,是索引错位修复,属于"位姿同步"不属于"平滑"。**P2 删代码时它留在"直写路径"里**。
- **部件 / 控制 / 尾焰同步**(PartVisualSync / ControlVisualSync / EngineVisualSync):独立子系统,全部保留。
- **协议字段**:保留(§〇),避免动编解码和双端版本依赖。
- **发送端节拍 F4/F5/F6b**:保留为独立变量——P1 实测时它不变,排除"发送端节奏"干扰。

**删除清单(P2,待 P1 证据后再删)**:RemoteCraftSmoothing 整文件(拆出直写 per-body 应用)、RemoteCraftDriver 的外推/冻结/平滑调用与全部诊断块、RemoteCraft 的时钟/判定/诊断字段族、MultiPlayerSyncUtil 里 2 阶/body 旋转相关常量与开关。

---

## 四、重新打 log:证据链设计(P3 前提,和现在的本质区别)

现状七种日志全是 **3 秒聚合统计**(moveMax、clamp 命中率、SEG= 自打结论)——聚合窗口把"卡顿那一帧"的输入输出抹平,只能看到"3s 内有一次大跳",看不到"大跳时输入是什么"。新日志理念 = **时间对齐的单帧事件序列**,只留 4 条,全带时间戳 + 帧号,可逐帧对齐读因果链:

| 日志 | 时机 | 内容 | 回答的问题 |
|---|---|---|---|
| `SEND` | 发送端每包 | t_send、位置、速度、Paused | 发送端数据是否干净(位置是否在跳) |
| `ARR` | 接收端每包 | t_arrive、内容间隔、到达间隔 | 网络 / 中继是否攒批(内容间隔小 + 到达间隔大 = 网络;两者都大 = 发送端) |
| `APP` | 接收端每帧 | 帧号、dt、目标位置、写入后实际位置、单帧位移 | 渲染层是否跳;写回前后对比(tfDrift) |
| `GAME` | 接收端每帧(低频) | 写前读 Transform vs 上次写后值 | 是不是游戏自己的 Update 在动幽灵 |

- 频率可控(包级 20Hz + 帧级 60Hz),统一开关,不散进热路径结构体字段。
- **不预设结论、不自打 SEG=**(上一轮最大方法论错误 = 先写死"VA 是根因"再去证明,§四教训 3)。

---

## 五、重构方向(P4,归零之后长什么样)

1. **拆开耦合体**:`PoseTarget`(从包算"目标位姿":最新包 or 外推,可插拔)+ `PoseWriter`(目标→Transform,固定公式)分离,中间允许插一层可选 `Smoother`(identity / 指数 / 其他,接口 ~10 行,整体替换)。
2. **时钟收敛为一个**:若要保留外推,只留 realAge(now − 到达时刻),删掉 VA / mRate / SenderTimeRate / stall 一族自持状态机。
3. **诊断独立**:日志模块独立于状态字段,不散落在热路径类里。
4. **一切由实测驱动**:每加回一项前先问"直写基线对照证明它有必要吗"(即 acceleration-smoothing §五 R1~R8 该有的顺序)。

---

## 六、分阶段执行方案

| 阶段 | 动作 | 验收 |
|---|---|---|
| P0 | 本文档落盘 + 索引同步(已完成本阶段) | 文档 0 错误,代码未动 |
| P1 | ✅ **已实施(2026-09-23)**:新增两个总开关 `EnableExtrapolation` / `EnableSmoothing`(**static readonly**,默认 false = 最新包直写基线),RemoteCraftDriver 门控外推块与平滑调用;内层 2 阶开关保留可单独翻转 | **0 错误 0 警告已达成**;待双端实测基线观感 + 收 `smoothing` 行基线值 |
| P2 | 按 P1 证据删代码 + 落 §四 四类新日志 | 0 错误 0 警告;双端实测收集完整证据链 |
| P3 | 按证据定位根因,决定保留哪些机制 | 每个机制一个可二值判据 |
| P4 | 按 §五 重构 + 全功能回归 | 平滑 / 位姿 / 部件 / 尾焰全回归 |

**P1 A/B 测试矩阵**(每次只改一行常量 → build → 双端实测):

| 配置 | EnableExtrapolation | EnableSmoothing | 内层 2 阶开关 | 行为 | 状态 |
|---|---|---|---|---|---|
| ① 基线 | false | false | (无关) | 最新包直写 | ✅ 已实测(2026-09-23):到达突发→单帧 3-5× 跳,平滑层零参与 |
| ② 外推无平滑 | **true(当前)** | false | 默认 true | 最新包 + 外推(含 2 阶),无指数平滑 | 🔧 **位置误差已清零**(chain r≈1.00、avg ratio 1.01,ageNow 钳制根治生效);残差 = 接收端帧节拍 C2 + 包量化;**干净测试配置(关诊断 + 20Hz)待实测** |
| ③ 1 阶外推 | true | false | false | 最新包 + 纯 1 阶外推 | 待测 |
| ④ 现状 | true | true | true | 完整旧管线 | 待测(对照组) |

实测场景(每配置):高空高速并排、悬停、暂停三场景;记录观感 + `Player.log` 的 `MultiPlayer smoothing` 行(moveMax / gapEMA / fps)。

**回归判据**:直写基线下"卡顿形态"若与现状几乎一致 → 根因 100% 不在平滑层,直奔发送端 / 网络 / 写回 / 帧节拍;若明显更跳 → 平滑层确实在起作用,再逐个加回。

---

## 七、实施记录

- 2026-09-23:可行性分析完成(盘点 8 类代码、边界、证据链、重构方向);用户拍板三个决策点(接收端归零 + 协议保留 / 直写基线 / 落盘后 P1);本文档创建,索引同步;代码未动。
- 2026-09-23(下午):**P1 落地**——`MultiPlayerSyncUtil` 新增 `EnableExtrapolation` / `EnableSmoothing`(static readonly 默认 false,const 会触发 CS0162 不可达代码警告,故用 static readonly);`RemoteCraftDriver` 门控:外推全链(dead-reckoning + 暂停冻结 + 2 阶 + 朝向 + VA/realAge 时钟)包在 `if (EnableExtrapolation)` 内、`ApplyRemoteSmoothing` 调用包在 `if (EnableSmoothing)` 内,直写基线走 else 分支(目标 = 最新包原值);`dotnet build MultiPlayer.csproj -c Debug` **0 错误 0 警告**。
- 2026-09-23(傍晚):**配置①直写基线 = (false,false) 双端实测完成**(TcpTransport 直连 192.168.40.1:25555、**SetTickRate 120Hz**、VM 两次连接=P1/P2)。**结论:卡顿源头 = 到达突发,不在平滑层**——
  - 静止船完美(moveMax 0.01m/3s),tfDrift=0、bodyDelta=0 → 写回路径与 body 同步无罪;
  - 飞行段 HOST 看 97m/s 船 **moveMax 9.7~11.65m(平均 3.2m)**、VM 看 105m/s 船 **moveMax 4.8~7.3m(平均 1.4m)** = 单帧 3-5× 平均;
  - `chain` 逐帧证据:尖峰帧 `[dt=42ms st=12.61 r=3.09]`(13 包≈104ms 量一帧内应用)紧跟干涸帧 `[dt=104ms st=2.91 r=0.29]`;`gap` 事件 258~317ms 到达空档;
  - 发送端均匀(VM gapWin 4-19ms 无 >250ms)+ NoDelay 已开 → 突发来自**传输/轮询的到达相位抖动(±50ms)**,非发送端节流;
  - **机制**:接收端每帧应用最新包 → 到达成簇时整簇距离变单帧跳变。**平滑层零参与时跳变依旧 → 卡顿源头是到达节奏,不是平滑算法**。
  - 推论:配置②(realAge 外推)的目标 = 最新包 + v×(now−到达时刻),是墙钟连续函数、与到达节奏解耦,理论上应平滑;旧管线 5~16% 残留 jerk 可能来自**指数平滑层的滞后追赶**而非外推。
- 2026-09-23(傍晚):**切到配置② = (true,false)**(外推开、平滑关)**双端实测完成。卡顿依旧(moveMax 8~10m),但抓到了真凶 = ageNow 钳制**:
  - ext 行确认配置②生效(ext=latency+ageNow 非零、120Hz),但 **ageNow 稳定段恒 0.000s**(包到达后同帧即处理 → 包龄天然≈0,属正常);真正的问题是 **`ageNow = min(age, 2×SendIntervalEst)` 钳制**(120Hz 下 = 16.6ms),而实测到达突发 ~100ms 远超它:
  - **数学验证**:配置① 跳变 = v×gap ≈ 89.5×0.1 = 8.95m(实测 9.7~11.65m ✓);配置② 跳变 = v×(gap−cap) ≈ 89.5×0.083 = 7.5m(实测 8.07~9.87m ✓)——**两者吻合,钳制就是"干涸期目标停住 + 成簇到达时补跳"的机制**;
  - realAge 连续性要求包间 ageNow **自由增长**(目标每帧推进 v×dt、包到达帧零跳变,P₂=P₁+v·c 与 ageNow:c→0 恰好抵消);有界钳制(2×发包间隔)只在发送端真停时才有意义,却误伤了正常到达节奏。
- 2026-09-23(晚):**根治(用户授权极端手段)—— ageNow 钳制值从 2×SendIntervalEst 改为 gapfreeze 阈(0.25s)**:
  - `RemoteCraftDriver.cs`: `ageNow = min(age, gapFreezeThr)`(gapFreezeThr = max(gapEMA×3, 0.25s),计算顺序前移;暂停冻结 RemotePausedRamp→0 优先逻辑不动;VA 钳制 vaCapFrame 仅剩诊断用途);
  - 0.25s 阈 = 真停/断连兜底(幽灵漂移 ≤ v×0.25s),正常到达突发(实测 gapWin max 100ms)永不触顶 → 连续性完整;
  - **chain 逐帧日志新增 `a=`(ageNowMs)字段**(FrameSample.AgeNowMs):一次实测同时验证"卡顿是否消失"(st/r 尖峰)与"干涸期 ageNow 是否随帧增长"(连续性证据);
  - `dotnet build MultiPlayer.csproj -c Debug` **0 错误 0 警告**。
- 2026-09-23(深夜):**配置②+钳制根治 复测完成。位置误差已清零,残差 = 接收端自身帧节拍(C2)**——
  - **VM 侧 chain `a=` 完美工作**:干涸帧 `[dt=16ms st=1.99 r=1.00 a=16]`、`[dt=12ms st=1.51 r=1.00 a=38]`,包到达帧 `a=0` + r=1.00 —— 外推连续性建立,单帧位置误差消失;
  - `frame` 行铁证:HOST `step=335.6m expect=334.25m avg ratio=1.010`(总量分毫不差,单帧 r 0.24-2.62 = 帧间再分配);VM `avg ratio 1.02-1.04`;
  - `diag` 行 SEG 自判 `C2 frameBeat(3.6x)`(HOST dtJit 27.7-98.9ms;VM 7.4-27.8ms);
  - **残差机制 = 接收端帧节拍**(HOST 25-31fps、dt 抖动 3.6×、99ms 长帧 → 12m/帧) **+ 包量化**(28fps 收 ~80Hz → 每帧 2-4 包不均);
  - **新发现:发送端有效发包率 = 发送端帧率**(F6b 同帧重复抑制 → 每帧 1 包):HOST 28fps → 只发 27Hz,**120Hz tick 在慢发送端根本发不满,反而在慢接收端制造包量化**;VM(80fps)收 HOST 的 27Hz 包 → chain r 全 ≈1.00(慢机器收慢包=平滑);
  - 推论:HOST 的 28fps + 3.6× 帧抖动是当前观感差的直接瓶颈 —— 需验证是否由"诊断日志(每秒 10 行 500 字符)+ 120Hz 处理"拖出来的。
- 2026-09-23(深夜):**干净测试配置(下一轮实测)**——`ExtraDiagEnabled` 改 static readonly=false(顺带修 CS0162),sendDiag/twitch/slowmo 并入该开关,只留 3s smoothing 汇总行 + 稀有事件(freeze/gapfreeze/gap);**用户按默认 20Hz 实测(勿 SetTickRate 120)**。判据:HOST fps 是否回升(诊断是否拖帧)、moveMax 是否回到 ≈v×dt(3.5-4m@28fps / 1.5m@80fps)、观感。
- 2026-09-23(深夜):**SP2 反编译复核(NetworkBodyScript.cs)——修复方向与 SP2 公式逐条对齐**:
  - SP2 发送端(`OnPostTickOwner`→`SerializeWrite`,FishNet 物理 tick 回调):包内带 `_fsn.PhysicsTime`,采样 transform+刚体 `linearVelocity/angularVelocity` → **发送内容与渲染帧率解耦**;
  - SP2 接收端(`SerializeRead`):`num2 = _fsn.PhysicsTime − packetPhysicsTime`(**age = 物理时间差,非自造时钟**);`pos = packetPos + v×num2`、`rot = packetRot × Euler(ω×num2)`(**1 阶外推**);`_lastPhysicsTime <= num` 乱序保护;**velocity 写回真实刚体 → PhysX 固定步长积分兜底包间连续性**;
  - 对照:本次修的两处恰好是"回到 SP2 公式"——realAge(now−到达) = SP2 的 physicsTime 差(差常数单向延迟);ageNow 钳制 0.25s = SP2 文档记载的 `Clamp(now−pktTime,0,0.25)`;无缓冲直取最新包 = SP2 同;无指数平滑 = SP2 位置路径无平滑;
  - 剩余架构差异(不照搬):SP2 远程船是真实刚体(物理积分兜底),mod 是 kinematic 幽灵(每渲染帧手动积分)→ 28fps 显示 = 28 采样/秒,SP2 同机同限制;发送端 Update 采样(F6b 每帧 1 包)→ 有效发包率=渲染帧率,但接收端 dead-reckoning 对任意发包率成立(VM 链 r=1.00 已证),非 bug 源头。
- 2026-09-23(深夜):**用户拍板"不计代价,引入 PhysX 与物理时钟轴"(对齐 SP2 架构)——阶段 A 已实施**:
  - `MultiPlayerSyncUtil`:新增 `EnablePhysicsClockAxis`(static readonly,默认 true)+ `CurrentPhysicsClock()`(读游戏 `FlightState.Time`,固定步长;场景未就绪回退 `Time.unscaledTimeAsDouble`);
  - `RemoteCraft.NewestArrivalFlightTime`:每包到达时记录本地物理时钟读数;
  - `RemoteCraftDriver`:包龄改 `CurrentPhysicsClock() − NewestArrivalFlightTime`(**只取本地时钟差值 → 无需两端时钟同步**;**暂停时 FlightState.Time 冻结 → age 冻结 → 目标自动停住**;**慢放按 timeScale 缩放 → 外推与发送端运动同步**,旧 mRate/SenderTimeRate 补丁天然多余);
  - `NetworkManager`:新增 `FixedUpdate()` —— 发包(`Sender.ProcessOutgoing`)与接收推进(`Driver.UpdateRemoteCrafts`)整条搬到物理 tick,Update 仅在开关关闭时推进(避免同帧双份);`DrainIncoming` 仍留 Update(到达时刻/入缓冲不受 tick 相位影响);Tick 内 `Time.unscaledDeltaTime = Time.fixedDeltaTime` → 定时器/EMA 按固定步长确定性推进;
  - `dotnet build MultiPlayer.csproj -c Debug` **0 错误 0 警告**。
- 2026-09-23(深夜):**阶段 B(PhysX 接管)设计已核实可行——关键发现(反编译 JNO 源码)**:
  - `CraftNode.Update:1226-1233`:craft **物理启用时**,节点状态**从物理读回**(`SetStateVectorsAtDefaultTime(frame.FrameToPlanetPosition(_craftScript.FramePosition), …FrameVelocity)` 并清空 GroundedSurface*)→ **PhysX 即位置权威**;物理禁用时走 `InContactWithPlanet` 分支(现幽灵路径,游戏每帧按 GroundedSurface* 摆位);
  - `CraftScript.IFlightFixedUpdate.FlightFixedUpdate:865-883`:物理启用时游戏逐 body `AddForce(mass × GravityScale × GravityFrame)` 施加重力,**但包在 `if (bodyScript.ApplyStandardForces)` 内** —— **`IBodyScript.ApplyStandardForces` 是游戏内建的"跳过标准受力"开关 = SP2 `RigidBodyRemote` 的对应物**,幽灵可置 false 免打 patch;
  - 接管方案(阶段 B):①`SetPhysicsEnabled(true, Warp)` 让节点状态由物理读回 ②每个 body:`ApplyStandardForces=false`、`isKinematic=false`、`useGravity=false`、drag/angularDrag=0、碰撞仍禁用(`CraftUtils.DisableCraftPhysicCalculation`)、`interpolation=Interpolate` ③物理 tick:新包到达 → 写入外推位姿(传送)+ 写回 `velocity/angularVelocity`(惯性系);包间由 PhysX 按固定步长积分(SP2 模型) ④不再写 GroundedSurface*(由物理分支接管);
  - 风险清单(必须回归):旋翼电机力矩/关节约束(部件自身 FlightFixedUpdate 未被 ApplyStandardForces 覆盖 → 可能需沿用/扩展 JetEngineGhostPatch)、穿地/碰撞、暂停/慢放、抽搐反馈环、地面/轨道分支切换。
- 2026-09-23(深夜):**阶段 A 首次实测(默认 tick 30Hz、慢速地面滑行 8~19m/s)——两个决定性发现**:
  - ✅ **诊断负载就是帧率杀手**:关掉周期诊断后 **双端 fps 28 → 94~100**(HOST 25-31→94-97,VM 76-100→94-100),`env` 显示 165Hz 屏 + vsync;**C2 帧节拍问题的主因 = mod 自身的诊断/120Hz 处理负载**,不是机器上限(用户当时未 SetTickRate,日志 `TickRate(30Hz)` 即默认);
  - ❌ **真凶 = `SenderMotionRate`(mRate)把外推量乘爆**:本轮双端 `mRate=1.7~5.2`(mRateWin max 5.19),`ext *= mRate` → 幽灵以 **3~6× 速度**外推、每包到达又被拉回 → 剧烈锯齿。铁证:双端 `move3s=150~250m/3s ≈ 58~83 m/s`,而包内遥测 `vel` 仅 8~19 m/s;而逐窗口 `newest` 位置推进 **正好 = 遥测速度**(971102→971125→971151…每 3s ≈52m ≈17.5m/s)→ **包数据自洽,是 mRate 乘错**;
  - 根因分析:mRate 的设计前提是"`FlightState.Time` 不缩放,慢放只能靠包位置位移反推"——**物理时钟轴已让 age 活在游戏模拟时间上(暂停冻结/慢放缩放),再乘 mRate 就是重复补偿**;且 mRate 用"单包位置位移 ÷ (速度×包内容时间)"测量,在慢速/地面接触场景被瞬时抖动污染到 2~5;
  - ✅ **修复**:`EnablePhysicsClockAxis=true` 时 `ext` **不再乘 mRate**(`RemoteCraftDriver` 门控;`vEff` 诊断口径同步),`build 0 错误 0 警告`;
  - ⚠️ 残留(待下轮):双端均出现 **1.6s / 2.7s 到达大空档**(jitterEMA 曾达 258ms),0.25s 钳制下 gapfreeze 激活 164~247 帧 → 幽灵滑行 0.25s 后被冻住、恢复时补跳 —— 属传输层长静默,非模型问题。
- 2026-09-23(深夜):**阶段 A 第二次实测(用户中途又 SetTickRate 120,recvHz 103~126)——mRate 门控已生效,但挖出真正的震荡源**:
  - mRate 已归 0/被门控(`mRate=0.000`、`mRateWin=(0.00,1.00)`),**但依旧一坨屎**;
  - 铁证:**VM 的船停在跑道上(vel=0.013m/s),双端 `pkΔ` 恒定 1.5888m、`moveMax=1.59m`、`move3s=20~67m`** → **发送端包位置在 ±1.59m 来回震荡**,幽灵逐包跟随 → 观感抖动;
  - 根因:**发送采样被搬进 FixedUpdate(50Hz),而游戏状态 `craft.Position` 与星球旋转是 Update(97fps)驱动的 → 相位差 → `PlanetVectorToSurfaceVector` 换算注入虚假旋转量 ≈ ω_planet×R×相位差 ≈ 1.6m**(量级吻合);
  - ✅ **修复**:采样/写回**全部回 Update(与游戏状态同相)**;`EnablePhysicsClockAxis` 缩窄为**只选包龄时钟**(age = FlightState.Time 差,暂停冻结/慢放缩放收益保留);mRate 门控保留(物理时钟下重复补偿);`build 0 错误 0 警告`;
  - 结论:物理时钟轴的"推进搬 tick"在 JNO 上行不通(游戏状态不是物理 tick 驱动的,SP2 是 FishNet tick 驱动的),**只保留"包龄用模拟时间"这一半**。
- 2026-09-23(深夜):**阶段 B(PhysX 接管)已实施(build 0/0)——SP2 架构落地**:
  - 开关 `MultiPlayerSyncUtil.EnablePhysXGhost`(static readonly,默认 true;false = 回到"物理禁用+表面锁定+kinematic"干净基线);
  - `InitializeRemoteCraft`(RemoteCraftManager):开关开时 `SetPhysicsEnabled(true, Warp)` + `ConfigurePhysXGhostBodies`(全部刚体 `isKinematic=false`、`useGravity=false`、`drag/angularDrag=0`、`interpolation=Interpolate`、**`ApplyStandardForces=false`**——游戏标准受力(重力)跳过,等效 SP2 `RigidBodyRemote`,免打 patch);
  - `GhostPoseWriter.ApplyRemoteStatePhysX`(新路径):物理保持启用 → 只写刚体 —— 根 body `rb.position=frame.PlanetToFramePosition(planetPos)`、`rb.rotation=headingFrame`,全体 `rb.velocity=frame.PlanetToFrameVelocity(planetVel)`、`rb.angularVelocity=headingFrame×data.AngularVelocity`(craft 局部系→世界系);游戏 CraftNode.Update 物理分支从刚体读回节点状态 → 刚体即位置权威;包间 PhysX 固定步长积分 + interpolation 渲染插值 → 连续;
  - 不写 per-body 旋转/ControlVisualSync(避免物理下与关节/输入驱动部件打架,待回归);旧路径(开关关)一字未动;
  - 已核实(反编译):`IBodyScript.ApplyStandardForces`(ModApi 可写)是 CraftScript.FlightFixedUpdate 施加重力的唯一条件;`IReferenceFrame.PlanetToFramePosition/Velocity` 存在;
  - ⚠️ 回归风险待实测:旋翼电机力矩(部件自身 FlightFixedUpdate 不被 ApplyStandardForces 覆盖 → 可能需扩展 JetEngineGhostPatch)、穿地/碰撞、暂停慢放、地面/轨道分支、关节链跟随。
- 2026-09-23(深夜):**阶段 B 首次实测(高速俯冲 58~79m/s)——仍跳变,但包数据自洽(mRate=1.001、newest 逐窗推进=vel)→ 定位为写回方式错误**:
  - 现象:moveMax 2~4.7m(31~90fps)、pktJump 4~6.6m、posErr=0;包位置逐 3s 窗推进 66 m/s = vel ✓;
  - 根因:**每帧写 rb.position 与 PhysX 积分打架**——物理步推进 v×20ms 后,下帧 Update 又把位置拨回目标(只推进 v×13ms)→ 每帧向后回拨 ≈v×26ms(70m/s≈1.8m)+ 包到达帧目标跳 → 观感跳变;
  - ✅ **修复(混合写回)**:只在 ①新包到达(`!ReferenceEquals(data, rc.LastApplied)`)②漂移>1m ③数据过期(age>0.25s 冻结)④发送端暂停(`data.Paused`)时写根 body 位姿;每帧只写 velocity/angularVelocity(stale/paused 时写零)→ 包间 PhysX 固定步长积分,到达帧 body 已"走到"新包位置 → 修正极小;**SP2 模型落地**;build 0 错误 0 警告。
- 2026-09-23(深夜):**SP2 全文对照复核(NetworkBodyScript.cs 375 行)——逐条比对,补最后一个缺口**:
  - ✅ 已对齐:age=PhysicsTime−pktTime、pos+v×age、rot×Euler(ω×age)(SrfRel 外推,符号已实测)、velocity/angularVelocity 写回真实刚体、包到达 SetPositionAndRotation、乱序保护(缓冲重排)、ApplyStandardForces=false(等效 RigidBodyRemote/IsRemote 力抑制)、FloatingOrigin(帧系统等效)、发送端采样+时间戳;
  - ❌→✅ **补:per-body 角速度**(SP2 每 body 独立网络对象,旋翼自旋靠 per-body angularVelocity;包内 BodyAngularVelocities 是局部系、已按 ghost body 顺序重排)→ PhysX 写回循环改为每 body `rb.angularVelocity = rb.rotation × BodyAngularVelocities[i]`(旋翼 300+RPM 靠这个转;焊接 body 值≈整船翻滚,无害),build 0/0;
  - 🚫 不抄(有意):age 无钳制(SP2 长空档无限外推漂移,我们 0.25s 冻结更稳)、每 body 位置独立同步(关节链已保证刚体组)、每 tick 发包(接收端模型与发包率无关)。

---

## 八、剩余项与回归判据

- **P1**:✅ 开关与门控已实施(build 0/0);**待用户双端实测 A/B 矩阵四配置**(改 `MultiPlayerSyncUtil` 两开关 → build → 装游戏 → 实测,记录观感 + `MultiPlayer smoothing` 行)。
- **P2**:待 P1 证据 → 删除清单(§三)+ §四 新日志。
- **P3 / P4**:待 P2 证据。
- 判据统一:每阶段只改一处、可二值验收;`dotnet build MultiPlayer.csproj -c Debug` 0 错误 0 警告。

---

## 九、任务终止记录(2026-09-23)

**用户判定:任务失败。** 代码已回滚至上个 commit(`d09be5c`),本文档归档(状态 ⛔ 失败)。

阶段 B(PhysX 接管)在"混合写回 + per-body 角速度"之后又追加的 5 轮修改,**全部实测无效**(moveMax 恒 ≈v×50ms(VM)/v×100ms(HOST),不随发包率 17~86Hz 变化):

1. **LateUpdate 逐帧 transform 位写回**(渲染跟随逐帧外推目标)——无效;
2. **ageNow 每帧增长钳制**(长帧目标最多推进 ~33ms,对齐 SP2 观感)——无效;
3. **RigidbodyInterpolation.None**(排除插值在渲染期覆盖 transform)——无效;
4. **GhostCraftNodeUpdatePatch**(Harmony prefix 跳过幽灵船 `CraftNode.FlightUpdate` 全部摆位/积分;双端日志确认绑定成功)——**无效**;
5. **刚体全部 isKinematic=true + patch + LateUpdate 逐帧写 = mod 唯一权威**(此时除 mod 外无任何写者)——用户实测仍"问题还在"(任务终止,未再取数)。

**决定性证据链**(§七):`moveDelta`(单帧)≈v×帧长始终平滑,只有个别帧跳 2-5×;`pkΔ`/`mRate`/`move3s`/`newest` 逐窗推进全部自洽 → 同步模型正确(SP2 全量对齐),台阶源于**接收端游戏渲染侧按固定节拍(~50ms/100ms)步进幽灵位置**,任何 mod 侧写入都被该节拍压制或覆盖。SP2 无此问题是因为其远程刚体走独立渲染管线(每渲染帧插值);JNO 的幽灵渲染受游戏自身管线支配,mod 侧无法绕过。

**遗留事实**:HOST 机 28~32fps(帧长 30-45ms)是显示侧硬约束;诊断日志关掉后曾达 94~100fps,PhysX 模式(8 个非 kinematic body + 关节 50Hz)会额外吃掉接收端 CPU。
