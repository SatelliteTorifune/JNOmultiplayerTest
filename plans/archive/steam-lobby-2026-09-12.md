# Steam 大厅系统移植分析(房间列表替代手动 SteamId)

> 项目:JNOMultiPlayer(SimpleRockets 2 / JNO 联机 mod MultiPlayer)
> 创建日期:2026-09-12
> 状态:✅ **已归档**(原状态:已落地,2026-09-12 拍板执行;旧决策「Lobby 邀请:不做」翻案,实现见 §4)
> 动机:当前加入房间必须手动输入房主 SteamId(见 [`MultiPlayerUI.cs:522`](../../Assets/Scripts/MultiPlayerUI.cs:522) `OnSteamJoinLobbyClick`),目标是把 SP2 的"房间列表菜单"移植过来,实现"开房可见、点列表加入"。

---

## 〇、一句话结论

**完全可以移植,且比 SP2 更简单**——只需移植 SP2 的 `SteamLobbyManager`(SteamMatchmaking 部分),FishNet / FishySteamworks 那半段**零依赖、用不上**。SP2 的大厅管理器是纯 Steamworks 包装,与本项目现有 `SteamTransport`(Steam Networking Sockets P2P)天然衔接:加入大厅 → `GetLobbyOwner` 拿房主 SteamId → 复用 `SteamTransport.StartClient(hostSteamId, 0, hello)`。传输层、握手、状态同步逻辑**一行不用改**。

---

## 一、背景与现状

- 当前加入流程:`MultiPlayerUI.OnSteamJoinLobbyClick` 弹输入框 → 玩家手动粘贴房主 SteamId → `LobbyManager.JoinLobby(steamId, 0)` → `SteamTransport.StartClient(hostSteamId)`。
- 全项目 grep:`SteamMatchmaking` / `CreateLobby` / `RequestLobbyList` / `SetLobbyData` / `GetLobbyOwner` / `LobbyEnter` / `GameLobbyJoinRequested` / `ActivateGameOverlayInviteDialog` **一处都没有**(2026-09 复核仍为 0 命中;代码里出现的 `JoinLobby(` 是 `LobbyManager` 自己的方法名),SP2 的 `SteamLobbyManager` 未移植。也**不存在**房间元数据、版本过滤、密码房、好友邀请、房间列表、ping 估算。
- 旧决策记录于 [`steam-integration-2026-08-13.md`](steam-integration-2026-08-13.md) §3.4 + Step3:**「Lobby 邀请【决策:不做】,维持手动输入房主 SteamId」**——当时为优先验证 P2P 通道。本文档为重新评估该决策的可行性分析,**待定,不翻案**。

---

## 二、可行性证据(2026-09-12 反射验证,游戏本机 Managed)

| 前提 | 状态 |
|---|---|
| mod 能直调 Steamworks.NET | ✅ 已落地(`SteamSpike.cs` 验证 + `SteamTransport.cs` 在用) |
| 游戏自带 `com.rlabrecque.steamworks.net.dll`(399KB,`SimpleRockets2_Data/Managed/`) | ✅ 反射加载成功 |
| `SteamMatchmaking` 全套 API | ✅ `CreateLobby / JoinLobby / LeaveLobby / RequestLobbyList / GetLobbyByIndex / SetLobbyData / GetLobbyData / GetLobbyOwner / GetNumLobbyMembers / GetLobbyMemberLimit / AddRequestLobbyListStringFilter / NumericalFilter / DistanceFilter / ResultCountFilter / InviteUserToLobby` |
| 大厅回调类型 | ✅ `LobbyCreated_t / LobbyMatchList_t / LobbyEnter_t / LobbyDataUpdate_t / GameLobbyJoinRequested_t` + `Callback<T>`(与 `SteamTransport` 现有 `_connStatusCallback` 同机制) |
| 回调泵 | ⚠️ **2026-09-13 实测推翻原假设**:游戏**没有**初始化 Steamworks.NET 托管 `CallbackDispatcher`(`SteamAPI.Init()` 从未被托管侧调用;游戏用自有 Steam 互操作做 native 初始化)。浏览器 `Update` 每帧调 `SteamAPI.RunCallbacks()` 抛 "Callback dispatcher is not initialized." → **一个会话刷 6645 条日志**。已修:反射调 `CallbackDispatcher.Initialize()`(internal static,幂等)补上托管 dispatcher,再泵 `RunCallbacks()`;初始化失败则放弃泵(大厅回调靠游戏 native 泵分发)并只警告一次 |
| AppID 一致 | ✅ 全部 870200,大厅列表只返回同 AppID 房 |
| 双账号公网测试路径 | ✅ 已实测可行(archive steam §Step4) |

**API 名差异备忘**(本游戏 DLL 版本):人数用 `GetNumLobbyMembers`(非新版 `GetLobbyMemberCount`);结果计数过滤器用 `AddRequestLobbyListResultCountFilter`(非 `Set...`);其余名称与 SP2 所用 Jundroo 包装一一对应。

---

## 三、SP2 → 本项目映射(SteamLobbyManager 逐项)

| SP2(Jundroo.SocialPlatforms 包装) | 本项目(Steamworks.NET 直调) |
|---|---|
| `CreateLobby(LobbyType, max)` + `CreateLobbyResult` 事件 | `SteamMatchmaking.CreateLobby(ELobbyType, max)` + `LobbyCreated_t` 回调 |
| `RequestLobbyList(filters)` + `RequestLobbyListResult` | `SteamMatchmaking.RequestLobbyList()` + filter 方法 + `LobbyMatchList_t` |
| 回调逐个 `GetLobbyData` 读元数据、`GetLobbyMemberLimit / GetNumLobbyMembers`、组 `LobbyData` 列表 | 完全同构:`GetLobbyByIndex(i)` 循环 → 读 KV → 组列表 → 事件抛给 UI |
| `JoinLobby(id)` + `JoinLobbyResult` → `GetLobbyOwner` | `SteamMatchmaking.JoinLobby(id)` + `LobbyEnter_t` → `GetLobbyOwner` → 现有 `SteamTransport.StartClient` |
| `SetLobbyData` 写房间元数据(名字/描述/版本/密码标记) | `SetLobbyData` 同写法 |
| `EstimatePingTimeFromLocalHost(PingLocation)` 估延迟 | 可不做:列表显示 "—",连接后用现有 Ping/Pong 真实 RTT(见 `MpNetworkManager.OnPong`) |
| `OpenInviteFriendsDialog`(overlay 邀请) | `SteamFriends.ActivateGameOverlayInviteDialog` ✅ 存在 |
| 好友点"加入游戏"→ `OnJoinLobbyRequested` | `GameLobbyJoinRequested_t` 回调 ✅ 存在,顺手支持 |

---

## 四、最小改动方案(✅ 已按此落地)

### 4.1 新增:`Assets/Scripts/Net/SteamLobbyBrowser.cs`(✅ 已实现,~370 行)

- **开房**:`CreateLobby(Public, maxPlayers)` → `LobbyCreated_t` 成功后 `SetLobbyData`(房间名 `mp_name` / 描述 / mod 版本 `mp_ver_major|minor|build` / 房主 SteamId `mp_owner`)→ 调现有 `LobbyManager.HostLobby(0)` 起 P2P 监听;
- **列表**:`RequestLobbyList()` + 版本数值过滤(全等)+ `DistanceFilter`(默认 Regional,`SteamLobbyListWorld` 切 WorldWide)→ `LobbyMatchList_t` → `GetLobbyByIndex` 循环组 `LobbyInfo` 列表 → 事件抛给 UI(过滤掉自己的房间);
- **加入**:`JoinLobby(lobbyId)` → `LobbyEnter_t` 成功 → `GetLobbyOwner`(兜底读 `mp_owner`)→ **复用现有 `LobbyManager.JoinLobby(ownerSteamId, 0)`**(内部 `SteamTransport.StartClient(hostSteamId, 0, hello)`,`SteamTransport.cs:105`),传输/握手/状态同步零改动;
- **邀请**:`SteamFriends.ActivateGameOverlayInviteDialog` + `GameLobbyJoinRequested_t` 自动加入(顺手支持);
- **回调泵**:游戏**不**保证托管 `RunCallbacks()` 可用(实测托管 CallbackDispatcher 未初始化,直接调每帧抛异常刷屏)→ `Update` 先反射确保 `CallbackDispatcher.Initialize()`(internal static)已调用,成功才泵 `SteamAPI.RunCallbacks()`;初始化失败只警告一次并放弃泵,依赖游戏 native 泵分发大厅回调(传输回调 `SteamNetworkingSockets.RunCallbacks()` 走独立通道不受影响);回调引用字段持有防 GC 退订(§六-1)。

### 4.2 UI 改造:`Assets/Scripts/MultiPlayerUI.cs`(✅ 已实现,采用方案 3 MVP)

采用 **方案 3(最省事 MVP)**:仿照现有玩家列表动态 `GroupModel` 重建(`RebuildPlayersIfChanged` 同款),在 inspector 面板新增 **"Steam 房间列表"** 分组——刷新按钮 + 邀请按钮(仅房主可见)+ 状态行 + 每房间一个按钮("房间名 (n/max) v版本"→ 点击即加入),不引入新窗口框架。`OnSteamHostLobbyClick` 改为输入房间名后走大厅开房;`OnSteamJoinLobbyClick` 改为触发列表刷新。手动输入 SteamId 路径保留于控制台 `SteamJoinLobby <id>`。

### 4.3 决策文档(✅ 已同步)

- [`README.md`](../README.md) 决策速查表 "Lobby 邀请 | 不做" 一行已修订为 "Steam 房间列表 | ✅ 已实现";
- [`steam-integration-2026-08-13.md`](steam-integration-2026-08-13.md) §0/Step3 的「Lobby 邀请:不做」已修订为「2026-09-12 翻案落地」。

---

## 五、可选进阶项(SP2 有,不搬不影响 MVP)

- **密码房**:`SetLobbyData(PasswordProtected)` + 加入后、P2P 握手前校验密码 hash(SP2 在 `NetworkConnectionAuthenticator` 做;本项目目前无鉴权层,需新增握手后校验,列为可选);
- **好友邀请**:overlay 邀请 + `GameLobbyJoinRequested_t` 自动加入(SP2 `OnJoinLobbyRequested` 同款);
- **举报/封禁**:SP2 用 `ReportCount < 7` 过滤 + 名单文件;MVP 可不做;
- **延迟显示**:列表显示 Steam 估算 ping,或连接后 RTT(见 §三)。

---

## 六、风险与注意

1. **回调引用必须持有**:字段保存 `Callback<T>` 实例,防 GC 自动退订(`SteamTransport._connStatusCallback` 同款模式);
2. **版本过滤**建议做进 lobby data 数值过滤,避免旧版 mod 玩家看到/加入新版房(SP2 `ServerVersionMajor/Minor/Build` 同款);
3. **大厅成员数 = 房间人数**,Steam 自动维护;注意"进了大厅但没连上 P2P"的人也会占名额(可接受);
4. **Public 房全世界可见**,建议保留 FriendsOnly 选项;
5. **测试**:双 Steam 账号,一台开 Public 房,另一台刷列表 + 点加入;同 AppID 默认 Regional 距离过滤,需测 WorldWide 开关;
6. 旧决策翻案需同步 `plans/README.md`(项目约定:有结论写进 plans + 更新决策速查表)。

---

## 七、工作量估计

新增约 300~400 行(`SteamLobbyBrowser` + 列表 UI),改动 `MultiPlayerUI.cs` + `Mod.cs`(注册浏览器命令),**传输层 / 同步层 / `SteamTransport` 完全不动**。风险集中在 Steam 大厅 API 的异步回调时序,而非游戏逻辑。

---

## 八、决策记录

- 2026-09-12:分析定稿,状态 **📋 待定**。旧决策「Lobby 邀请:不做,维持手动输入房主 SteamId」**暂不翻案**;拍板落地时按 §4 执行并同步 README 决策表。
- 2026-09-12(当日拍板):**✅ 翻案落地**——新增 `Assets/Scripts/Net/SteamLobbyBrowser.cs`(CreateLobby/RequestLobbyList 版本过滤/JoinLobby→GetLobbyOwner→复用 `SteamTransport`)+ `MultiPlayerUI` "Steam 房间列表" 分组(方案 3 MVP,动态 GroupModel)+ `Mod.cs` 控制台命令(`SteamLobbyList`/`SteamLobbyListWorld`/`SteamLobbyCreate`/`SteamLobbyJoin`/`SteamLobbyLeave`)+ `StopLobby` 退厅;README 决策表与 archive 文档已同步修订。手动输入 SteamId 路径保留于控制台 `SteamJoinLobby <hostSteamId>`。**待双 Steam 账号公网实测**(开 Public 房 → 另一台刷列表 + 点加入)。
