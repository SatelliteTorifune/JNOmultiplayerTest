# MpNetworkManager 上帝类重构方案(refactor-mpnetworkmanager-2026-09-22.md)

> **状态:✅ 已完成归档(2026-09-22)**:上帝类已消失——`MpNetworkManager.cs` 3717 行 → **391 行瘦门面**;首轮拆出 15 个职责类,同日二次整理**合并到 11 个类 + 按 4 层目录分类**(`Net/` 传输协议 / `Net/Session/` 会话房间 / `Net/Sync/` 同步管线 / `Net/CraftVisual/` 远程船呈现,共 25 个文件)。`dotnet build MultiPlayer.csproj -c Debug` **0 错误 0 警告**(Unity 侧重编译亦通过);字符串字面量 / `Mod.Log*` 调用点逐项对账通过(见 §十「实施记录」)。**P3 诊断收拢、§八 #5/#12/#13 缺陷修复、双端实测回归未做**(前者属可选优化,中者属行为变更,后者需用户环境)。
> **日期:2026-09-22**(分析 + 实施 + 二次整理同日完成)
> **关联**:[README.md](../README.md) §二(架构表,已按最终目录重写)/§三(Vizzy 契约)/§八(已知问题);[latency-smoothing-2026-08-22.md](latency-smoothing-2026-08-22.md)(现行平滑实现事实);[acceleration-smoothing-2026-09-14.md](acceleration-smoothing-2026-09-14.md)(r10 基线 + 回滚教训,已归档)
> **一句话定位**:`Assets/Scripts/Net/MpNetworkManager.cs` 原为 3717 行、74 方法、12 项职责的上帝类——已按"**瘦门面(MonoBehaviour)+ 职责组件(普通类)**"拆解并分目录归类;协议、平滑算法、日志口径、热路径零分配、注释全部保持不变(纯搬移)。

---

## 〇、经验教训(归档时补,给下一次大重构用)

1. **"行为不变"要靠文本级对账证明,不能只靠"构建通过"。** 构建只保证类型正确,不保证日志字段、数值常量、调用顺序没被搬丢。本次实际有效的四道检查,按性价比排序:
   - **字符串字面量多重集对账**(剥离注释后,原文件 vs 新文件集合的逐串计数差):一行脚本就能证明"日志文本零改动",且把缺失项精确归因到被删死代码(本次 18 处 = `Q()×2` 的全部字面量)。**这是最划算的一道检查。**
   - **日志/副作用调用点计数**(如 `Mod.Log*` 84 → 84):一次 grep 就能发现"整块代码搬丢"。
   - **逐字节重建比对**:把搬移配方写成脚本,从 HEAD 重建一份再与磁盘文件比,差异必然只有预期的标识符改写——`TrySampleLocalCraft`(275 行)因此证明为完全一致。
   - **行级账目**(原文件每行在重构后是否有对应行,未匹配的逐条归类):能把 355 处未匹配压缩成"死代码 18 / 标识符改写 115 / 改名与访问级别 99 / 其余 123 已解释"。
2. **按行号区间提取 + 脚本拼接,比手抄 3700 行安全得多**;但要注意两个真实踩过的坑:
   - **PowerShell 变量插值会吞关键字**:`"$t2internal void X"` 里的 `$t2internal` 被当成变量名 → 生成 ` void X`(合法但变 private),`$tforeach` 更会直接吃掉 `foreach` 导致语法错误。**必须写成 `${t2}internal` 或把前缀与文本分开拼接。**
   - **here-string 终止符 `'@` 必须独占行首**,写成 `''` 会让整段脚本被吞进字符串、静默生成垃圾文件(本次真的写出过一个错乱文件)。
3. **搬移漏行比想象中容易**,但"漏"往往表现为**字段没跟着走**(本次 pacer 的 5 个字段因 anchor 空格不符没插进合并后的类)——编译器会报 `CS0103 名称不存在`,是很好的安全网;**不要在搬移的同时做逻辑优化**,否则编译器报错时说不清是哪一类问题。
4. **注释是资产,必须随代码走**:这个项目的行内注释记录了 30 轮双端实测的因果(哪次修了什么、为什么),重构时一字未改,新文件头只**追加**一句来源与职责说明。
5. **诊断代码是"隐形的最大成分"**:上帝类里约 1/3 是诊断(≈50 个字段 + 84 处日志)。本次先把诊断**连同其所有者**一起搬(不合并、不重命名),把"诊断收拢"留作独立的 P3——若在搬移时同时清理诊断,风险会成倍上升。
6. **`csproj` 由 Unity 生成**:Unity 开着的时候,新增 `.cs` 会被它自动写进 `Compile` 列表;手动补的条目会和它重复(本项目实测出现过 14 条重复 → 需去重,否则 `CS2002` 警告破坏"0 警告")。**搬移文件后先看 Unity 有没有跟,再决定要不要手动改。**
7. **目录分类会暴露反向依赖**:把类分到 `Session/Sync/CraftVisual` 后,"传输层反向 `using` 会话层"这种伪依赖一目了然(本次清掉 10 处,全部是**注释里提到类型名**被脚本误判);**判断 using 是否真需要,要看剥离注释后的代码文本**,而不是全文匹配。
8. **"太散"和"太胖"是同一种病**:首轮拆出 15 个平铺文件,每文件 40~580 行不等,用户反馈"太零散";合并的判据是**数据与算法的归属**(采样与发包共用一份诊断状态 → 合成一个类;body 重排只操作 `RemoteCraft` 自己的缓冲 → 并进去),而不是"行数凑够就合"。

---

## 〇、结论先行

1. **问题定性:确实是上帝类,但病根不在"不够 OOP",而在"职责没有边界"。** 一个类同时是:连接管理器、协议路由器、玩家注册表、XML 下载器、发送端采样器、发包节拍器、幽灵船生命周期管理器、外推/平滑算法库、Transform 写回器、UI 通知器、RTT 测量器、诊断日志机。其中**发送端采样**与**接收端渲染**两条管线几乎不共享任何状态,却挤在同一个类里互享字段。
2. **诊断代码是最大的单一成分(估算占 1/3 强,≈1300 行)**:RemoteCraft 148 个字段里约 50 个是纯诊断;`UpdateRemoteCrafts`(372 行)、`PushSample`(190 行)、`ProcessOutgoing`(225 行)里大段是日志拼装,核心算法被淹没。
3. **重构策略:绞杀者模式,不是重写。** r10 基线是 30 轮双端实测换来的稳定态,行内注释就是决策记录——**只搬代码不改逻辑,注释随代码走**,每阶段 `dotnet build MultiPlayer.csproj -c Debug` 0 错误 0 警告 + 日志逐字段比对,最后双端回归。
4. **门面不动,调用方零感知**:外部 6 个消费方(MultiPlayerUI / LobbyManager / Mod / 两个 Harmony patch / 三个 VisualSync)只依赖 `MpNetworkManager.Instance` 的公开成员和 `MpNetworkManager.RemoteCraft` 类型;拆分期间门面成员签名全部保留,唯一需要改引用的是 RemoteCraft 移出嵌套(3 个文件,编译器兜底)。
5. **顺手核实了 3 个既有问题**(已登记 README §八 #12/#13):TCP 路径 `OnPeerTimeout` 在**读线程**触发回调 → 管理器在非主线程改 Dictionary / 销毁 Unity 对象(潜在线程安全 bug);`Stop()` 会话复位不完整(`_nextPlayerId`/`TickRate`/`ClientPingMs`);另有已知的 #5/#6/#7 相邻问题一并纳入 P0 清理清单。

---

## 一、动机

- **可读性/可维护性**:3717 行单文件,任何修改都要先在脑子里装下 12 个子系统;`UpdateRemoteCrafts` 一个方法 372 行,新读者无法定位"哪段是算法、哪段是日志"。
- **改动风险面**:发送端节奏(F5 帧率解耦)与接收端平滑(VirtualAge 时钟)是完全独立的问题域,现在改任何一处都在同一个文件同一批字段旁边动手,回归范围无法收窄。
- **后续路线图的直接需求**:multi-craft-sync(§六.2)要重写身份层(`playerId → craftGuid`)、eva-sync 要加 `Eva*` 字段、physics-sync P0 要改速度字段——这三项**全部落在本文件的不同区域**。不先拆,每个主题都要在 3700 行里做手术,互相踩踏(acceleration-smoothing 的 r4~r30 已经演示过一次)。
- **测试性**:现为 `MonoBehaviour` + 静态单例 + 游戏内部 API 直接调用,平滑/外推/节拍逻辑**完全不可单测**。拆成普通类后,`RemoteCraftSmoother`/`DeadReckoningState`/`StateSendPacer` 至少可以离线喂样本验证(哪怕不上正式测试框架,加一个 DevConsole 自测命令也行)。

## 二、现状体检(2026-09-22 实测数据)

### 2.1 体量

| 指标 | 数值 | 口径 |
|---|---|---|
| 文件总行数 | 3717 | `Assets/Scripts/Net/MpNetworkManager.cs` |
| 方法数 | 74(+嵌套类 3) | 正则统计方法声明 |
| 管理器级字段/事件声明 | 45 | 含 3 个 event、11 个 Dictionary/HashSet 集合 |
| 嵌套类 `RemoteCraft` 字段数 | **148** | 其中约 **50 个纯诊断**(Diag*/Win*/Last*Log*) |
| 常量/调参区 | ≈130 行 | L44–175(外推开关、EMA 系数、钳制值……) |
| `Mod.Log*` 调用点 | 84 处 | 大半在周期诊断行拼装里 |
| `try` 块 | 37 个 | 防御式风格贯穿全文件 |
| 诊断代码占比(估) | **≈1/3 强(≈1300 行)** | 诊断字段 + 各周期日志块 + 事件日志(sendDiag/smoothing/slowmo/twitch/gap/gapfreeze/bodyMap/spawnDiag/visualDiag) |

### 2.2 方法长度 TOP(重构的"病灶"清单)

| 方法 | 行区间 | 行数 | 实际混装的职责 |
|---|---|---|---|
| `UpdateRemoteCrafts` | 2459–2831 | **372** | 外推时钟 + 冻结检测 + 2 阶外推 + 平滑调度 + 3 类周期日志 + 可见性诊断 |
| `TrySampleLocalCraft` | 3427–3700 | **274** | 坐标系换算 + body 采样 + ω/v 差分 + EMA + 控制输入采集 + 发送端诊断 + 一次性 dump |
| `ProcessOutgoing` | 765–989 | **225** | 发送节拍(F4/F5/F6b)+ 同帧去重 + sendDiag(≈150 行日志)+ ω 符号自校验 |
| `ApplyRemoteSmoothing` | 2929–3122 | **194** | 位置/朝向/每 body 平滑 + 旋转 body 逐帧积分 + 部件抖动诊断 |
| `RemoteCraft.PushSample` | 1778–1967 | **190** | 环形缓冲 + gap/jitter EMA + 时钟扣减 + mRate 估计 + 停顿判定 + 突发日志 |
| `ApplyRemoteState`(static) | 3179–3328 | **150** | GroundedSurface 反射 + SetStateVectors + 朝向公式 + body 写回 + FlightData 反射刷新 + 抽搐诊断 |
| `ReorderRemoteBodiesByGhost` | 592–677 | 86 | body id 重排 + 映射缓存重建 + 一次性诊断 |
| `SpawnRemoteCraftAtPosition` | 1997–2082 | 86 | 生成 + 登记 + 表面锁定 + spawnDiag |
| `SpawnRemoteCraftCoroutine` | 2233–2312 | 80 | 异步预加载编排 + 有效性校验 |
| `TryGetInterpolatedState` | 2843–2921 | 79 | **死代码**(README §八 #6 已登记) |

### 2.3 重复模式(机械去重即可消除)

| # | 模式 | 出现位置 | 次数 |
|---|---|---|---|
| D1 | `if (IsServer) Transport.Broadcast(...) else foreach(peer.IsServer) SendTo(...)` | L975–986 / L1002–1012 / L418–429 | 3 |
| D2 | 移除玩家序列(lock→Remove→`_pendingXmlRequests.Remove`→广播 PlayerLeave→`OnPlayerLeft?.Invoke`) | `KickPlayer` L1437–1466 / `HandlePeerTimeout` L1482–1510 / `OnPlayerLeave` L1333–1348 | 3 |
| D3 | 玩家名回退 `IsNullOrEmpty(name) ? "Player "+id : name` | L248 / L258 / L1215 | 3 |
| D4 | 参考系回退 `frame==null → ViewManager.GameView.ReferenceFrame` | L527–531 / L3209–3213 / L3461–3466 | 3 |
| D5 | 渲染器统计/启用遍历 `GetComponentsInCraft` | L2065–2075 / L2810–2819 / L2163–2170 | 3 |
| D6 | `lock (_playersByPlayerId)` 样板 | 全文 | 11 |

## 三、职责清单:一个类戴 12 顶帽子

| # | 职责 | 代表成员(行号) | 备注 |
|---|---|---|---|
| R1 | 连接/会话生命周期 | `Host`/`Join`/`Stop`/`SetTransport`(265–378);`IsServer/IsConnected/PlayerId/PlayerName/LocalNodeId` | 角色(房主/客户端)是隐式布尔,不是显式状态机 |
| R2 | 协议分发 | `HandlePacket` switch(1037–1092)+ 15 个 `On*` 处理器 | 消息编解码已良好隔离在 `MpMessage.cs` ✓ |
| R3 | 玩家注册表 | `_playersByPlayerId`/`RegisterPlayer`/`NextPlayerId`/`HandlePeerTimeout`/`KickPlayer`/`GetPlayers`(104,1470–1515) | 唯一加了 lock 的集合(见 §四.7) |
| R4 | 飞船 XML 分发(SP2 按需下载) | `_xmlCache`/`_pendingXmlRequests`/`_hostCraftResend` + 双向重发计时器(146–152,463–493,1098–1331) | 一个"内容分发子系统"藏在消息处理器里 |
| R5 | 发送端采样 | `TrySampleLocalCraft` + `_lastBody*`/`_accelEma` 等 14 个采样缓冲字段(3427–3700,104–141) | **纯发送端**,与 R8 零共享 |
| R6 | 发包节拍/心跳 | `ProcessOutgoing`(F4/F5/F6b 节奏)、`SendKeepAlive`、`SetTickRate`(765–1033) | 节奏参数本身有历史教训(帧率耦合) |
| R7 | 幽灵船生命周期 | `_remoteCrafts` + 生成协程 + 预加载 + 进度框 + `EnforceRemoteCraftVisuals` + `OnFlightSceneLoaded`(105–107,1997–2456) | 与 `VizzyIsolationPatch` 有**契约**:命名约定「玩家名\|船名」+ `ClearGhostNodeCache` 调用时机(README §三) |
| R8 | 接收端外推/平滑 | `RemoteCraft` 嵌套类算法部分 + `UpdateRemoteCrafts` + `ApplyRemoteSmoothing`(1519–1985,2459–3122) | **算法心脏**,全部静态方法 + 公有字段 |
| R9 | Transform 写回 | `ApplyRemoteState`(static)/`ForceRemoteHeading`/`ApplyRemoteBodyPoses`/`TryGetLogicalComPose`/`ApplyRemoteGroundedSurface` + 5 个反射 PropertyInfo(522–763,3179–3367) | 坐标系注释是全项目最贵的知识 |
| R10 | UI 通知 | `ShowFlightMessage`/加入离开提示/去重宽限/Kick 弹窗(165–260,1416–1432) | 与 R2 的消息处理器耦合 |
| R11 | RTT 测量 | `SendKeepAlive`/`OnPong` + `ClientPingMs` + peer.PingMs → rc.LatencyMs 同步(995–1013,1392–1414) | 小而独立 |
| R12 | 诊断 | 84 个日志点 + ≈50 个诊断字段 + `ExtraDiagEnabled` + FrameRing(1597–1768 等) | 最大单一成分,横切所有其它职责 |

## 四、具体设计问题(逐条 + 证据)

1. **上帝类 / SRP 崩坏**:R1~R12 全部塞在一个 `MonoBehaviour`。修改任何一项职责都要触碰同一个文件;字段区 45 个声明混装 12 个子系统 的状态。
2. **发送端与接收端同班**:R5/R6(我的船怎么发)与 R7/R8/R9(别人的船怎么画)是两条独立管线,只在 `Transport` 和 `PlayerId` 上有一丁点交集。现在 `_lastBodyPos`(发送端 body 缓冲)与 `rc.SmoothedBodyPos`(接收端平滑)竟然住在同一个类的作用域里,靠命名前缀区分。
3. **`RemoteCraft` 嵌套类是第二个上帝对象**:148 个公有字段 = 状态缓冲 + 平滑状态 + 外推时钟 + 冻结检测 + body 重排缓存 + 引擎视觉缓存(`EngineDrivers`/`SyncedThrottles`/`LastInjected*`)+ ≈50 个诊断字段。它同时被 3 个外部静态类(`EngineVisualSync`/`PartVisualSync`/`ControlVisualSync`)读写——本质上是**全局共享可变数据袋**,类型嵌套只是语法,没有边界。
4. **诊断与核心逻辑织死**:诊断没有统一开关(只有 `RemoteCraft.ExtraDiagEnabled` 一个 const),删一行日志要在 372 行的方法里找;诊断字段污染 `RemoteCraft` 字段区(诊断:算法 ≈ 1:2);日志行拼装(`MP smoothing` 60+ 字段)直接写在热路径方法体里。
5. **"自订阅事件"的隐藏控制流**:`Awake` 里 `OnPlayerLeft += HandlePlayerLeft; OnPlayerJoined += ShowPlayerJoinedNotice; OnRemoteState += ApplyRemoteState;`(L181–186)——类自己订阅自己的事件再回调自己,读代码时要绕一圈才能建立调用链;事件本应只用于**对外广播**(MultiPlayerUI 正是这么用的),内部流程应是显式直接调用。
6. **配置即公有可变状态**:`SendIntervalMs`/`RenderDelayMs`/`TimeoutMs`/`TickRate` 是 public 字段,UI/控制台/房主广播三处写入;`SetTickRate` 与字段默认值互相矛盾(§八 #5:`TickRate=30` vs `SendIntervalMs=50`)。
7. **线程模型是隐式的**(本次核实):Steam 路径所有收包/超时都在主线程(`DrainIncoming`/`CheckTimeouts` 由 `Update` 调用);**但 TCP 路径 `OnPeerTimeout` 会在读线程触发**(`TcpTransport.cs:343`,read loop 末尾)→ 管理器 `HandlePeerTimeout` 在非主线程执行 `Dictionary.Remove`(`_hostCraftResend`/`_pendingXmlRequests`/`_remoteCrafts` 均无锁)并经 `OnPlayerLeft` → `RemoveRemoteCraft` → `DestroyCraft()` **在后台线程调 Unity API**。`MultiPlayerUI.cs:315` 的注释("事件可能来自网络线程,仅置标志")说明 UI 侧已意识到,但管理器侧未防护 → 已登记 §八 #12。
8. **死代码**:插值时代残留(§八 #6):`TryGetInterpolatedState`(79 行)/`ApplyRemoteTransformDirect`/`ClearBuffer`/`ReuseInterpBody*`/`UnderrunFrames`/`SnapFrames`/`RenderDelayMs`;`OnPause` 整体注释(L1364–1374);`Q()` 两个格式化助手已无调用者(朝向诊断日志删除后残留)。
9. **巨型方法的"混合抽象层"**:`UpdateRemoteCrafts` 里一行写 `rc.VirtualAge += Time.unscaledDeltaTime`(时钟推进),三行后拼 60 字段日志字符串(表现层),再调 `ApplyRemoteState`(游戏 API 层)——三个抽象层在同一方法体交错 6 次。
10. **魔数参数化良好但散落**:平滑/外推参数(`0.1/0.6/50/1.5/100/2.5/10/0.01`)全在方法体内,常量区(44–175)与使用点分离,改动要跨 300 行对照注释。
11. **`ApplyRemoteState` 同名异义**:实例版(L2183,按 playerId 分发/触发生成)与静态版(L3179,把包写进 Transform)名字相同职责完全不同,阅读时极易混淆;`ApplyRemoteTransformDirect` 只是它的转发壳(死代码)。
12. **会话状态复位不完整**:`Stop()`(321–355)不清 `_nextPlayerId`(L1470)——同一进程反复"开房→踢光→再开房",PlayerId 单调递增不复用;`TickRate`/`SendIntervalMs`/`ClientPingMs` 也跨会话残留(manager 是 `DontDestroyOnLoad` 复用实例,`LobbyManager.EnsureMpManager` 只在 `Instance==null` 时新建)→ 已登记 §八 #13。

## 五、目标架构

### 5.1 原则(先说清楚"不追求什么")

- **目标是职责边界,不是类数量最大化**:`RemoteDataPack` 保持 struct(值语义、零分配是对的);三个 `*VisualSync` 静态类继续存在(它们已经是良好拆分);**不引入** DI 容器、不搞接口爆炸、不拆程序集、不改 async/Task——这是游戏 mod 运行时,实用主义优先。
- **MonoBehaviour 只做三件事**:Unity 生命周期、协程宿主、对外门面。所有逻辑进普通 C# 类,由门面在 `Update`/`LateUpdate` 里显式驱动(顺序即契约,见 §五.4)。
- **热路径零分配红线不破**:所有复用缓冲(`Reuse*`/`_lastBody*`/`FrameRing`)随所属状态对象整体搬移,不进静态、不改生命周期。
- **注释是资产**:搬迁时注释随代码走(含那些"2026-09-XX 实测"记录);本方案不重写任何注释内容。

### 5.2 组件划分(目标目录 `Assets/Scripts/Net/`)

> **阅读提示**:本节是**动手前的布局草案**——里面列出的 `MpSessionState`/`MpTransportHub`/`MpDiag`/`RemoteStateBuffer`/`DeadReckoningState`/`RemoteCraftSmoother` 等最终**没有建**(有意简化,理由见 §10.2);`LocalCraftSampler`/`StateSendPacer`/`MpMath`/`MpSyncTuning`/`GhostBodyRemapper`/`MpFlightNotices` 后来**被合并**(见 §10.6)。**最终落地的目录结构与类清单见 §10.1。**

| 新文件 | 职责(唯一) | 吸收的现有代码(MpNetworkManager.cs 行号) | 类型 |
|---|---|---|---|
| `MpNetworkManager.cs`(瘦身后,目标 <500 行) | Unity 生命周期 + 组合根 + 对外门面 + Update 调度 | `Awake/OnDestroy/Update/LateUpdate`(178–205,459–520)+ Host/Join/Stop/SetTransport 外壳 | MonoBehaviour |
| `MpSessionState.cs` | 会话身份与角色(IsServer/IsConnected/PlayerId/PlayerName/LocalNodeId/TickRate)+ 统一复位(修 §八 #13) | 38–53 状态属性 + Stop 的复位清单(321–355) | 普通类 |
| `MpTransportHub.cs` | 传输持有/换绑/事件接线 + `SendOrBroadcastToNet()`(消 D1)+ Drain/超时驱动 + **回调主线程化契约**(修 §八 #12) | 36,178–205,361–378 + D1 三处 | 普通类 |
| `MpPlayerRegistry.cs` | 玩家表 + PlayerId 分配 + 统一移除出口 `RemovePlayerInternal()`(消 D2)+ PlayerLeave 广播 | 104,1470–1515,1333–1348(部分),1482–1510 | 普通类 |
| `MpMessageRouter.cs` | 消息类型 → 处理器分发 | 1037–1092 | 普通类 |
| `MpCraftCatalog.cs` | XML 缓存/按需下载/客户端与房主双向重发状态机 | 146–152,1098–1128,1179–1201(登记部分),1222–1331,463–493 | 普通类 |
| `LocalCraftSampler.cs` | **发送端**采样:坐标系换算/body 缓冲/EMA/控制输入 | 3427–3700 + 104–141 采样字段 + `GetLocalCraftXml/NodeId`(3371–3424) | 普通类 |
| `StateSendPacer.cs` | **发送端**节奏(F4/F5/F6b)/暂停降频/心跳/SetTickRate | 765–989(除 sendDiag),995–1033,108–109 | 普通类 |
| `RemoteCraftManager.cs` | 幽灵注册表/生成协程/预加载/进度框/移除/可见性强制/场景清理 | 105–107,156–176,1997–2456,444–455 | 普通类(协程仍由门面 StartCoroutine) |
| `RemoteCraft.cs`(移出嵌套,聚合根) | 每船标识 + Node 引用 + 生命周期标志 + **视觉同步缓存**(EngineDrivers/SyncedThrottles/LastInjected*)+ LastApplied | 1519–1985 的非诊断非算法字段 | 普通类 |
| `RemoteStateBuffer.cs` | 环形缓冲 + 到达间隔/抖动 EMA + TryGetNewest | 1543–1546,1770–1776,1792–1816(统计部分) | 普通类 |
| `DeadReckoningState.cs` | VirtualAge/RealAge/SendIntervalEst/SenderTimeRate/SenderMotionRate/停顿判定 | 1659–1704,1778–1967(时钟/倍率/停顿部分) | 普通类 |
| `RemoteCraftSmoother.cs` | 平滑状态 + `ApplyRemoteSmoothing` 算法 + `SnapSmoothedBodies` | 1571–1596,2929–3167 | 普通类 |
| `GhostPoseWriter.cs` | 写回 Transform/GroundedSurface(反射)/逻辑 comRot/朝向公式/FlightData 刷新 | 522–581(ForceRemoteHeading),699–763,3179–3367 | 静态类(纯函数,入参 rc+data) |
| `GhostBodyRemapper.cs` | body id → 幽灵装配序重排 + 映射缓存 | 592–677 | 静态类 |
| `MpFlightNotices.cs` | FlightUI 提示/去重宽限/Kick 弹窗 + `ShowFlightMessage` | 165–176,222–260,1416–1432 | 普通类 |
| `MpDiag.cs` | 发送端诊断(sendDiag/ω 自校验/fps EMA)+ `ExtraDiag` 总开关 | 110–141,814–965 | 静态类/开关 |
| (并入 `RemoteCraft.cs` 或独立)`RemoteCraftDiag` | 接收端全部诊断字段与周期日志(smoothing/slowmo/twitch/gap/gapfreeze/frame) | 1597–1768 诊断字段 + UpdateRemoteCrafts 内日志块(2652–2822) | 普通类(每船一份) |

> 拆分后 `MpNetworkManager` 门面持有上述组件的实例并按序驱动;`RemoteCraft` 聚合持有 Buffer/Clocks/Smoother/Diag 四个子对象(保持 `rc.X.Y` 的访问习惯,外部 VisualSync 只碰聚合根上的视觉缓存字段,签名兼容)。

### 5.3 `RemoteCraft` 拆分明细(148 字段的归宿)

| 字段组 | 数量(约) | 去向 |
|---|---|---|
| 标识/生命周期(PlayerId/Node/HasState/IsInitialized/HasApplied/LastApplied) | 10 | `RemoteCraft`(聚合根) |
| 视觉同步缓存(SyncedThrottles/EngineDrivers/LastInjected*/ReuseRenderers) | 8 | `RemoteCraft`(聚合根;EngineVisualSync 等继续读写) |
| 状态缓冲(Buffer/BufferCount/BufferHead/NewestArrivalTime/StateSample) | 6 | `RemoteStateBuffer` |
| 外推时钟/倍率/停顿(VirtualAge/RealAgeSec/SendIntervalEst/MArrivalEma/SenderTimeRate/SenderMotionRate/PktStall*/RemotePaused*/LatencyEmaMs/GapEmaMs) | 20 | `DeadReckoningState` |
| 平滑状态(SmoothedPos/SrfRel/BodyPos/BodyRot/HasSmoothed/ReuseSmooth*/ReuseReorder*) | 14 | `RemoteCraftSmoother` |
| body 重排缓存(BodyIdMap*/ReuseReorder*/BodyRemapCount) | 10 | `RemoteCraft`(或 GhostBodyRemapper 持有,rc 引用) |
| **纯诊断**(Diag*/Win*/Last*Log*/FrameRing/慢放/抽搐/跳动/帧级统计) | **≈50** | `RemoteCraftDiag` |
| 引擎/部件同步消耗字段(SyncedThrottles 已列) | — | 聚合根 |

### 5.4 对外 API 面(迁移期间必须保持不变)

| 消费方 | 依赖成员 | 迁移策略 |
|---|---|---|
| `MultiPlayerUI.cs` | Instance/IsConnected/IsServer/PlayerName/PlayerId/TickRate/SetTickRate/GetPlayers/Transport/OnPlayerJoined/OnPlayerLeft/ClientPingMs/GetPlayerLoadProgress/KickPlayer/SetTransport | 门面全保留(转发到组件) |
| `LobbyManager.cs` | Instance/Host/Join/Stop/OnFlightSceneLoaded/RefreshLocalCraft/SetTickRate/Transport | 同上 |
| `Mod.cs` | Instance/Transport(NetSim 诊断) | 同上 |
| `VizzyIsolationPatch.cs` | Instance/IsConnected/Transport.GetPeers/IsRemoteCraftNode;**反向契约**:`OnFlightSceneLoaded` 必须调 `ClearGhostNodeCache()`(README §三) | `IsRemoteCraftNode` 转发 RemoteCraftManager;契约在门面 `OnFlightSceneLoaded` 里保序 |
| `JetEngineGhostPatch.cs` | `IsRemoteCraftNode`(静态) | 同上 |
| `EngineVisualSync`/`PartVisualSync`/`ControlVisualSync` | `MpNetworkManager.RemoteCraft` 类型 + 其公有字段 | RemoteCraft 移出嵌套为顶层 `Assets.Scripts.Net.RemoteCraft`,**3 个文件改 using/引用**(编译器兜底);字段名不动 |

**Update 阶段顺序是行为的一部分,固定为契约**(写入门面注释):`Transport.DrainIncoming` → 重发计时(CraftData/PlayerJoin)→ `ProcessOutgoing` → `UpdateRemoteCrafts` → `EnforceRemoteCraftVisuals` → `SendKeepAlive` → `Transport.CheckTimeouts`;`LateUpdate` 只做朝向/body 写回(依赖 `[DefaultExecutionOrder(1000)]` 不变)。

### 5.5 顺带的结构性改进(随拆分落地,零行为变化)

- D1→`SendOrBroadcastToNet(byte[])`;D2→`RemovePlayerInternal(playerId, broadcastLeave)`;D3→`DisplayName(peer)`;D4→`ResolveReferenceFrame(CraftNode)`;D5→`RefreshRenderers(rc, list)`。
- `Awake` 的自订阅事件改为显式调用链(事件只留对外广播);`ApplyRemoteState` 实例版改名 `HandleRemoteStatePacket`,静态版改名 `WritePoseToGhost`(消 §四.11)。
- 平滑/外推参数集中为一个 `static class SmoothingParams`(带原注释),方法体里只引用具名常量。

## 六、分阶段迁移计划(每阶段一次提交,独立可回滚)

| 阶段 | 内容 | 行为变化 | 验证 |
|---|---|---|---|
| **P0 清障** | ① 删死代码(§四.8 清单,联动 README §八 #6/#7 改状态);② 修 #5(TickRate 默认值统一为 20Hz 或 30Hz 二选一)+ #13(Stop 复位清单集中到 `MpSessionState.Reset()`);③ `Q()` 等孤儿助手删除 | 日志少 snap/interpPct/posErr 三个死字段(在 §八 #6 预告过);#5/#13 是有意的缺陷修复 | build 0/0;双机快速回归(开房/加入/移动/踢人);日志 diff 确认仅预期差异 |
| **P1 机制去重** | D1–D6 helper 化;自订阅事件改直调;`ApplyRemoteState` 改名;参数集中 `SmoothingParams` | 无(纯等价变换) | build 0/0;代码 review 对照"每个 helper 的 3 处调用点语义一致";双机冒烟 |
| **P2 类提取** | 按 §5.2 逐组件搬移(建议顺序:纯静态先走——GhostPoseWriter/GhostBodyRemapper/MpDiag;再 MpPlayerRegistry/MpCraftCatalog/MpTransportHub;再 LocalCraftSampler/StateSendPacer;最后 RemoteCraft 四件套 + RemoteCraftManager);**门面签名不动**;RemoteCraft 移出嵌套时同步改 3 个 VisualSync 引用 | 无 | 每搬一个类 build 0/0;全部完成后 **Steam 双账号公网 + VM TCP/NetSim 双路径全回归**(§九) |
| **P3 诊断收拢** | `RemoteCraftDiag` 组件化;`ExtraDiagEnabled` 升级为全局总开关 + 子开关(sendDiag/smoothing/slowmo/twitch/frame);日志字段口径保持与 archive/latency-smoothing §9.6 文档一致 | 关开关时零开销;开时输出逐字段不变 | build 0/0;开关两态各跑一轮双机,对比 `MP smoothing` 行字段齐全 |
| **P4(可选,远期)** | RemoteDataPack 从 Mod.cs 归位 Net 命名空间;`IsServer` 分支收敛为 Host/Client 角色策略;发送端 LocalCraftSampler 加 DevConsole 自测命令(喂录制样本回放) | 有(组织性) | 单独评估,**且仅在 multi-craft/EVA 主题开工前做**——那两个主题会重写身份层,现在过度设计会白做 |

## 七、风险与禁区

- **最大风险:搬移时"顺手优化"**。r10 是 30 轮实测换来的(acceleration-smoothing §四的教训之一);**逻辑改动与搬移严禁同批提交**——P0 的 #5/#13 是登记过的缺陷修复,单独成 commit。
- **热路径零 GC**:缓冲随对象走,禁止搬移过程中把复用 List 变成方法内 new(现存唯一例外:周期 visualDiag 的 `new List<Renderer>()` 每 3s 一次,P1 统一时顺手消掉)。
- **时序契约**:§5.4 的 Update 顺序、LateUpdate 写回、`[DefaultExecutionOrder(1000)]`、协程"两帧等待"语义(`SpawnRemoteCraftCoroutine` 开头的 `yield return null` ×2)都不能变。
- **隐式对外契约**:幽灵命名「`对方玩家名|船名`」是 VizzyIsolationPatch 第③层判定依据——**命名是 API**;`OnFlightSceneLoaded` ↔ `ClearGhostNodeCache` 的调用时机同理。
- **csproj 是 Unity 生成的**:每新增 .cs 需让 Unity 刷新补 `<Compile>` 条目后才能 `dotnet build` 验证(README §一)。
- **单人游戏零开销路径**(`Instance==null` 早退的 Harmony patch 调用)不能因拆分引入额外初始化成本。
- **不做的**:DI 容器/接口层/async 改造/程序集拆分/协议改动/行为调优——任何一项都属于别的主题,不在本方案范围。

## 八、顺手核实的既有问题(已登记 README §八)

| 新编号 | 问题 | 证据 | 与本方案的关系 |
|---|---|---|---|
| §八 #12 | **TCP 路径 `OnPeerTimeout` 在读线程触发**,管理器在非主线程改无锁 Dictionary 并经 `OnPlayerLeft`→`RemoveRemoteCraft`→`DestroyCraft()` 调 Unity API | `TcpTransport.cs:343`(read loop 末尾 Invoke)vs `MpNetworkManager.cs:1482–1510`/`2084–2138`;Steam 路径无此问题(全部经主线程 DrainIncoming) | P2 `MpTransportHub` 立契约"回调一律主线程泵送"(TCP 侧把超时通知并入 `_incoming` 队列) |
| §八 #13 | **`Stop()` 会话复位不完整**:`_nextPlayerId`/`TickRate`/`SendIntervalMs`/`ClientPingMs` 跨会话残留;manager 是 DontDestroyOnLoad 复用实例 | `MpNetworkManager.cs:321–355`(无上述字段)vs `LobbyManager.cs:138–151`(复用实例) | P0 修:复位清单集中进 `MpSessionState.Reset()` |

(另:已知的 #5 TickRate 默认矛盾、#6 死代码、#7 OnPause 空壳一并纳入 P0;#10 速度缺自转项属 physics-sync 主题,不在本方案动。)

## 九、回归判据(全部保留现行行为)

1. `dotnet build MultiPlayer.csproj -c Debug`:**0 错误 0 警告**(每阶段)。
2. **Steam 双账号公网**:开房→列表加入→幽灵生成(进度框出现、百分比走满)→ 双向移动/旋转(旋翼船)→ 暂停/恢复(对端冻结无抽搐)→ `SetTickRate 60` 广播生效 → 踢人/离开(PlayerLeave 广播、幽灵销毁、无残留)→ Stop 后场景无残影。
3. **VM TCP + NetSim**(延迟/抖动/丢包注入):同上冒烟 + `MP gap`/`gapfreeze` 事件日志仍按预期出现。
4. **日志口径**:`MP smoothing`/`MP sendDiag`/`MP twitch` 行的字段名与顺序与重构前逐字段一致(允许 P0 预告的三个死字段移除);诊断开关(P3)关闭时这些行消失且帧率无可测变化。
5. **Vizzy 隔离回归**:对端船不执行本机 Vizzy、本机船不被误杀(三层判定依赖的命名约定与登记表行为未变)。
6. **单人游戏**(未开联机):Harmony patch 路径开销与重构前一致(肉眼无感知差异)。

## 十、实施记录(2026-09-22)

### 10.1 结果

`MpNetworkManager.cs`:**3717 行 → 391 行**(仅剩会话身份 + 对外 API + FlightUI 提示 + Awake 组建 + Update/LateUpdate 调度 + Raise/SendOrBroadcast 转发助手)。

最终目录结构(**25 个文件 / 4 层**,namespace 与文件夹严格对应):

```
Net/                      Assets.Scripts.Net            传输与协议基础(10):IMpTransport, MpMessage, MpPeer,
                                                        SteamTransport, TcpTransport, LiteNetLibTransport,
                                                        LagSimTransport, SteamLobbyBrowser, SteamSpike, FishNetSpike
Net/Session/              Assets.Scripts.Net.Session     会话与房间(4):MpNetworkManager 391, MpMessageRouter 295,
                                                        MpCraftCatalog 259, MpPlayerRegistry 105
Net/Sync/                 Assets.Scripts.Net.Sync        状态同步管线(7):LocalCraftSender 616, RemoteCraftManager 581,
                                                        RemoteCraft 568, RemoteCraftDriver 421, GhostPoseWriter 372,
                                                        RemoteCraftSmoothing 231, MpSyncUtil 81
Net/CraftVisual/          Assets.Scripts.Net.CraftVisual 远程船呈现(4):EngineVisualSync 574, MpCraftPreloader 247,
                                                        PartVisualSync 161, ControlVisualSync 75
```

| 职责(原 R1~R12) | 新归属(2026-09-22 二次整理后) |
|---|---|
| 连接/会话生命周期、角色状态、FlightUI 提示 | `MpNetworkManager`(门面,`Session/`) |
| 协议分发 + 房间流程(Hello/Welcome/PlayerJoin/PlayerLeave/State/Pong/Kick/TickRate) | `MpMessageRouter`(`Session/`) |
| 飞船内容分发(XML 按需下载 + 双向重发确认) | `MpCraftCatalog`(`Session/`) |
| 玩家注册表(PlayerId 分配/超时/踢出/离开) | `MpPlayerRegistry`(`Session/`) |
| 发送端采样 + 发包节拍/心跳/sendDiag | `LocalCraftSender`(`Sync/`,二次整理合并为一个类) |
| 幽灵生命周期(生成协程/进度框/幻影模式/可见性) | `RemoteCraftManager`(`Sync/`) |
| 接收端每帧外推驱动 + LateUpdate 写回 | `RemoteCraftDriver`(`Sync/`) |
| 平滑算法 | `RemoteCraftSmoothing`(`Sync/`) |
| 位姿写回(反射/坐标系公式) | `GhostPoseWriter`(`Sync/`) |
| body 重排 | 并入 `RemoteCraft`(`Sync/`,二次整理) |
| FlightUI 提示 | 并入门面(二次整理) |
| 共享常量 + 数学工具 | `MpSyncUtil`(`Sync/`,二次整理合并) |
| 每船状态(缓冲/时钟/平滑/诊断) | `RemoteCraft`(移出嵌套为顶层类型,`Sync/`) |

### 10.2 与方案(§五/§六)的偏离(均为有意简化)

- **未建 `MpSessionState` / `MpTransportHub`**:会话身份状态留在门面(它本就是"会话对象"的对外视图,`IsServer`/`TickRate` 等 UI 直接读);传输持有仍在门面,发送去重改为门面 `SendOrBroadcastToNet()`。少一层转发、字段引用更短。
- **未建 `MpDiag`(P3 未做)**:诊断字段留在各自所有者(`_sampler._diag*`、pacer 的 `_sendGapEmaMs/_fpsEma`、`RemoteCraft` 的 Diag/Win 字段),日志拼装随方法整体搬移。诊断收拢留待 P3。
- **`RemoteCraft` 未做四件套拆分**(Buffer/Clocks/Smoother/Diag):整体搬为顶层类,算法与其状态同居一处(与重构前等价);进一步拆分收益低于改动风险,列为 P3 可选项。
- **`ApplyRemoteState` 静态版保留原名**(供 `using static` 逐字搬移调用点);实例版(按 playerId 分发)改名 `HandleRemoteState` 以消歧义。
- **`Awake` 自订阅事件 → 显式 `RaisePlayerJoined/Left/RemoteState`**:调用顺序与原订阅顺序严格一致(内部清理 → UI 提示 → 对外事件)。

### 10.3 已删死代码(全部先核实无调用者)

`TryGetInterpolatedState`(79 行)、`ApplyRemoteTransformDirect`、`ClearBuffer`、`UnderrunFrames` + `ReuseInterpBodyPos/Rot` 字段、`Q(Quaterniond)`/`Q(Quaternion)`(仅被注释代码引用)。**`SnapFrames`/`InterpPct`/`RenderDelayMs` 有意保留**——三者被 `MP smoothing`/`MP.SetTickRate` 日志行读取,删除会改变日志字段(留待 P3 与口径变更一起处理)。

### 10.4 行为保持验证(4 项,全部通过)

1. **构建**:`dotnet build MultiPlayer.csproj -c Debug` → **0 错误 0 警告**(FishNet.Runtime 的 3 条警告为第三方既有)。
2. **字符串字面量多重集对账**(重构范围 vs 原文件,剥离注释):新增 **0** 个;缺失恰好 18 处 = 死代码 `Q()×2` 的全部字面量(2×`(`、2×`)`、6×`","`、8×`"F3"`)。**日志文本零改动**。
3. **`Mod.Log*` 调用点计数**:原 84 → 新 84(死代码内无日志调用,故应相等)。
4. **逐字节重建比对**(用同一配方从 HEAD 重建后再与磁盘文件比对):`TrySampleLocalCraft`(275 行)**完全一致**;`ProcessOutgoing`/`UpdateRemoteCrafts`(374 行)/`RemoteCraft` 类体(455 行)差异仅剩预期的标识符改写与切片边界(逐条核对无意外差异)。

> 二次整理(§10.6)后上述 1~3 项**重跑通过**;第 2 项口径改为"Session+Sync+CraftVisual 全部文件 vs 原文件 + 3 个 VisualSync":缺失仍是 `Q()` 那 4 组;`Mod.Log*` 为新范围 95 = 原 84 + `MpCraftPreloader` 11(该既有文件随重组进入范围)。

### 10.6 二次整理:目录分类 + 合并过碎的类(2026-09-22,同日)

首轮拆分把 15 个类平铺在 `Net/` 一个文件夹里(每文件 40~580 行不等),粒度偏碎、层级不清。二次整理:

- **目录分层**(namespace 与文件夹严格对应):`Net/`(传输与协议基础,既有 10 文件不动)、`Net/Session/`(会话与房间)、`Net/Sync/`(状态同步管线)、`Net/CraftVisual/`(远程船呈现)。**29 → 25 个文件**。
  - 同层内互相引用无需 using(C# 父命名空间自动查找:`...Net.Session` 可见 `...Net` 的类型);跨层引用只加了 8 处 `using`(如 Session↔Sync、Sync↔CraftVisual),并清掉了 10 处"仅注释提及"的伪依赖(如 Transport 层反向引用 Session)。
  - 根层 `Mod.cs` / `LobbyManager.cs` / `MultiPlayerUI.cs` / 两个 Harmony patch 各加一行 `using Assets.Scripts.Net.Session;`;`Net.X` 形式的限定引用(NetSim 等)因传输层未移动而**保持不变**。
- **合并 5 个过碎的类 → 4 处**:
  | 合并 | 理由 |
  |---|---|
  | `LocalCraftSampler` + `StateSendPacer` → **`LocalCraftSender`**(616 行) | 同一管线两段(采样→发包),pacer 每次发包都要读 sampler 的诊断状态,原为跨类 `_sampler._diag*` 访问;合并后为自有字段,去掉一层转发与一个构造依赖 |
  | `MpMath`(40 行)+ `MpSyncTuning`(52 行)→ **`MpSyncUtil`**(81 行) | 都是"共享调参/工具",各文件同时需要两者 → 两个 `using static` 并为一个 |
  | `GhostBodyRemapper`(112 行)→ 并入 **`RemoteCraft`** | 唯一调用者是 `RemoteCraft.PushSample`,方法体只操作该类的复用缓冲(纯数据变换),`internal static` 原样保留(零调用点改动以外的编辑) |
  | `MpFlightNotices`(70 行)→ 并入**门面** | 加入/离开提示只由门面的 `Raise*` 触发,属会话级 UX;`ShowFlightMessage` 本就在门面(原为转发壳,现为实体实现) |
- **合并后的类大小**:门面 391 / LocalCraftSender 616 / RemoteCraftManager 581 / RemoteCraft 568 / RemoteCraftDriver 421 / GhostPoseWriter 372 / MpMessageRouter 295 / MpCraftCatalog 259 / RemoteCraftSmoothing 231 / MpPlayerRegistry 105 / MpSyncUtil 81 —— 都在"一个职责可通读"的区间内。
- **未合并**(刻意保留):`MpPlayerRegistry`(带锁的数据结构,独立生命周期)、`GhostPoseWriter`(被 driver 与 manager 共用,合任何一边都会形成反向依赖)、`RemoteCraftSmoothing`(纯算法,独立便于单独调试平滑问题——这正是本轮重构的初衷)。

### 10.5 尚未完成(留待后续,均属行为变更或可选优化)

- **P3 诊断收拢**(`RemoteCraftDiag` + 全局开关)——不改日志口径的前提下收益有限。
- **§八 #5**(TickRate 默认矛盾)、**#12**(TCP 超时回调在线程外)、**#13**(`Stop()` 复位不完整):本次严格"行为不变",未动;修复方案已就绪(#12 → `MpTransportHub` 主线程泵送契约;#13 → 会话复位清单集中)。
- **双端实测回归**(§九.2~§九.6)需用户在 Steam 双账号 / VM TCP 环境执行——代码侧无法替代;本次仅完成编译期与文本级验证。
- **`RemoteCraft` 四件套拆分 / `MpDiag` / `MpSessionState`**:按需再做。

## 剩余项(归档时状态:以下均**不是**当前待办,按需再取用)

- **待拍板(后续可选)**:① §八 #5 修复方向(TickRate 默认 20 还是 30);② 是否继续 P3 诊断收拢;③ P4(`RemoteDataPack` 归位 Net 命名空间 / 角色策略化)是否与 multi-craft 主题合并执行。
- **待用户复跑**:§九 的双端实测回归(Steam 双账号 / VM TCP)——代码侧无法替代,本次只做到"编译期 + 文本级"验证。
- **已确认与本次无关**:§八 #12 / #13 的修复(线程契约、会话复位)属**行为变更**,代码已就绪但未动,单独开工时按 README §八 取用。
