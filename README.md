[English](README.en.md) | 中文

# MultiPlayer — JNO 联机模组

给 [Juno: New Origins](https://store.steampowered.com/app/870200/) 加联机的 Unity 模组:多名玩家进入同一个场景,各自操作自己的飞船,并**实时看到对方的飞船**。基于官方 ModTools 载入,网络层用 FishNet 走 Steam P2P(公网零端口转发)。

> **当前版本 1.51 · 原型阶段**:目前是「每名玩家一艘船、互相同步成<幽灵船>」的形态,完整联机(多船 / 燃料 / 碰撞)仍在开发。开发与决策记录见 [`plans/README.md`](plans/README.md)。

---

## 能做什么 / 暂不能做什么

**已实现**

- 房主 + 多玩家同场景,所有数据经房主中继转发
- 实时同步对方飞船:位置、速度、朝向、部件开关、引擎尾焰、油门/刹车/滑块/平移、激活组(Activation Group)、分级、各部件姿势
- 高延迟平滑与连续外推(瞬移阈值、延迟 EMA 等),网络抖动下有缓冲
- **Vizzy 隔离**:自己的可视化脚本只作用于自己的船,不会读到/控制别人的船,别人的脚本也不会在这边执行
- Steam 大厅:开房可见、点列表加入、Steam 好友邀请(无需端口转发)
- Windows / 局域网 / 虚拟机调试用 TCP 直连

**尚未实现(已知限制)**

- 每名玩家当前**一艘船**(无多船)
- **不同步**燃料/资源/部件损伤
- **EVA 出舱 / 回舱不同步**(出舱即错位:出舱=节点分裂、回舱=节点合并,需多 craft 身份层;分析见 [`plans/proposals/eva-sync-2026-09-18.md`](plans/proposals/eva-sync-2026-09-18.md))
- **暂停 / 时间缩放不跟随**,需双方都在 1× 实时
- 无双向碰撞物理(靠 kinetic 视觉)
- 需**相同游戏版本** + **同一行星系统**(以房主为准)
- 幽灵船**不可被接管**(按 `[ ]` / 地图 "Take control" 切到对方飞船)——防劫持的 Harmony 拦截**仍待落地**;EVA 上线后风险放大(见 [`plans/README.md`](plans/README.md) §八 #11)

---

## 安装

> 1.安装Juno Harmony
> 2.把MultiPlayer.sr2-mod放入对应文件夹,启用即可
---

## 快速上手

模组加载后,游戏界面里会出现 **MultiPlayer 面板**(工具栏里的 MP 按钮可随时重新打开)。面板顶部显示连接状态与在线人数,下面按功能分组。

### 方式一:Steam 大厅(推荐,公网可用)

**开房**

1. 点面板「开房」按钮 → 输入房间名
2. 房间为**公开**;创建成功后弹窗显示房间名 + 你的 SteamId
3. 等好友加入,或点「邀请好友」直接发 Steam 邀请

**加入**

1. 点「房间列表」→「刷新」
2. 在列表中点目标房间「加入」;或直接接受 Steam 邀请

**断开**:点「断开」(房主 = 关闭房间,客户端 = 退出)。

### 方式二:局域网 / 虚拟机调试(TCP)

- 房主:调试组 →「TCP 开房」,端口默认 `25555`
- 加入方:调试组 →「TCP 加入」,输入房主地址(格式:`主机IP[:端口]`)

> TCP 需要房主放行对应防火墙端口;Steam 路径无需任何端口设置。

### 方式三:开发控制台命令(DevConsole)

游戏内 DevConsole 里可直接输入:

| 命令 | 参数 | 作用 |
|---|---|---|
| `SteamLobbyCreate` | 房间名 | 创建公开 Steam 房间(推荐) |
| `SteamLobbyList` | — | 列出房间 |
| `SteamLobbyListWorld` | — | 列出房间(跨区) |
| `SteamLobbyJoin` | 大厅 id | 按大厅 id 加入 |
| `SteamLobbyLeave` | — | 离开 / 关闭房间 |
| `SteamHostLobby` | 端口 | 旧式:按端口开 Steam 房 |
| `SteamJoinLobby` | 房主 SteamId | 旧式:按房主 SteamId 加入 |
| `TcpHostLobby` | 端口 | TCP 开房(调试) |
| `TcpJoinLobby` | 地址 端口 | TCP 加入(调试) |
| `StopLobby` | — | 停止联机 |
| `SetTickRate` | Hz(默认 20) | 设置状态发送频率 |

调试用网络模拟(`NetSimOn/Off/Reset`、`NetSimDelay/Jitter/Loss/Duplicate`):在本地人为注入延迟/抖动/丢包/重复包,用于复现或自测网络表现,正常游玩无需使用。

---

## 联机前须知

- 双方使用**相同游戏版本**,并且模组版本一致
- 双方进入**同一个行星系统**(以房主为准),且都已加载进飞行场景
- 都在 **1× 实时**(不暂停、不用时间缩放)
- 每名玩家当前一艘飞船

---

## 常见问题

- **看到别人的船偶尔“卡一下 / 跳一下”**:高延迟下的外推与平滑,属预期;可用 `NetSimDelay/Jitter` 本地复现。停→冲顿挫问题正在持续优化中。
- **开房 / 加入无反应**:确认双方 Steam 均在线、游戏已连上 Steam(Steam 路径),或 TCP 路径下端口可达。
- **对方的 Vizzy 输入框等串到本地**:联机中 Vizzy 已被隔离;若仍复现,请把 `Player.log` 里的 `VizzyIsolation` 日志反馈给作者。

---

## 开发 / 构建

本仓库是模组的完整源码。分析、架构、决策与任务清单集中维护在 [`plans/README.md`](plans/README.md)(内部文档,经 Git 管理;涉及本机私密路径的值只在本地、不进仓库)。

- 构建:`dotnet build MultiPlayer.csproj`(0 错误 / 0 警告)
- 依赖:官方 ModTools、FishNet.Runtime / GameKit.Dependencies(见各 `.csproj`)

## 许可

[MIT](LICENSE) · © 2026 Maeriberry Hearn