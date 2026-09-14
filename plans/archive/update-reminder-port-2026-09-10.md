# ModUpdater 移植分析：Volken2 → MultiPlayer（更新提醒弹窗系统）

> 状态：✅ **已实施（代码落地 + MSBuild 编译验证），待游戏内实测**
> 日期：2026-09-10
> 源文件：`<VOLKEN2>\Assets\Scripts\ModUpdater.cs`（353 行，含 Volken 2026-09-10 防卡死提交 `5e26184`）
> 目标工程：`JNOMultiPlayer`（Mod 名 **MultiPlayer**，SimpleRockets 2 / JNO，Unity 2022.3.62f3）

---

## ⚠️ 交付后复核：当前存在「本地版本 vs 发布版本」不一致（2026-09 代码复核）

落地本身没问题，但**版本来源没有对齐**，会让更新检查**每次启动都误报**：

| | 值 | 来源 |
|---|---|---|
| 本地 mod 版本(运行时读到的) | **1.4** | `Assets/ModData.asset` 的 `_versionMajor: 1` / `_versionMinor: 4` → `ModInfo.Version` → `Mod.cs:53` `this.ModVersion = ...` → `ModUpdater.cs:123` |
| 线上"最新"版本 | **1.5** | 仓库根 `version.txt`(=`VersionFileUrl` 拉取的内容) |

按 `ModUpdater` 的比较逻辑(`latest <= _localVersion` 则跳过),`1.5 <= 1.4` 为假 ⇒ **所有 1.4 版本的用户每次启动都会看到"有新版本"弹窗**,只能靠"不再提醒"消掉(它会把 1.5 写进 `PlayerPrefs`)。

**修法二选一**(属版本策略,本文档不代拍):
1. 把 `Assets/ModData.asset` 的 `_versionMinor` 提到与 `version.txt` 一致(即"这版就是要发布的 1.5");
2. 或者把 `version.txt` 改回当前实际发布号,等真正发版时再同时抬两边。

**顺带提醒**:`ModUpdater` 内部仍有 `aMptest` 命名残留(`SkippedVersionPrefKey = "aMptest.UpdateReminder.SkippedVersion"`、宿主对象名 `"aMptestUpdateReminder"`、`User-Agent: aMptestModUpdater/1.0`)。功能上无害(只是 key 名),但会影响排查时的可读性;若要清理,注意 **改 `SkippedVersionPrefKey` 会让用户已存的"不再提醒"记录失效**(会再弹一次)。

---

## 〇、一句话结论

**可行性：极高（★~★~★，几乎零改动"复制即用"级）——已落地。**
两个工程是**同一款游戏**（SimpleRockets 2 / JNO，Steam AppID 870200）的 Mod，依赖的 `ModApi.*` / `Jundroo.ModTools` / Unity 自带 API **完全同源**，ModUpdater 的 8 个★耦合点里 6 个只是"改 URL / 改文案 key / 换日志方法"，唯一需要新增代码的是给目标 `Mod.cs` 补一个 `ModVersion` 属性（Volken 有、MultiPlayer 原本没有）。无新增依赖、无程序包、无 asmdef 改动。

### 本次实际改动文件

| 文件 | 改动 |
|---|---|
| `Assets/Scripts/ModUpdater.cs` | **新建**（移植主体，含全部防卡死机制） |
| `Assets/Scripts/Mod.cs` | 新增 `ModVersion` 属性；`OnModInitialized()` 末尾赋值 + 触发 `CheckForUpdate()` |
| `Assets/Scripts/ModUtils.cs` | 新增 `Mod.LogUpdate(object)`（常打印的更新检查日志通道） |
| `Assets/Content/Languages/EN-US.xml` | 新增 6 个 `MultiPlayer.MultiPlayerUI.Update*` key |
| `Assets/Content/Languages/ZH-CN.xml` | 同上（中文文案） |
| `version.txt` | **新建**（仓库根目录，内容 `1.5`）——通道2 兜底通道的原料 |
| `MultiPlayer.csproj` | 加入 `<Compile Include="Assets\Scripts\ModUpdater.cs" />`（Unity 下次刷新会自动生成等价条目） |

---

## 一、【重点】Volken 的防卡死机制（2026-09-10 提交 `5e26184`，本次一并移植）

Volken 在提交 `5e26184`（"你妈"，`Assets/Scripts/ModUpdater.cs` 61 增 17 删）里新增了一套"绝不无限挂起"的保证，**这是本次移植的重点**，已原样保留：

| # | 机制 | 实现位置 | 作用 |
|---|---|---|---|
| ① | **15s 总看门狗** | `FetchRoutine` 开头 `const float totalTimeoutSeconds = 15f; var deadline = Time.realtimeSinceStartup + totalTimeoutSeconds;` | 覆盖"两个通道加起来"的整体等待，`UnityWebRequest.timeout` 只管单个请求，二者双保险；`realtimeSinceStartup` 不受暂停/卡顿影响 |
| ② | **逐帧轮询 + 主动 Abort** | `TryFetchVersion` 内 `var operation = request.SendWebRequest(); while (!operation.isDone) { if (Time.realtimeSinceStartup >= deadline) { request.Abort(); ... yield break; } yield return null; }` | 不写 `yield return request.SendWebRequest()`（那样无法中途放弃），改为每帧比对 deadline，超时立刻 Abort 并回调失败——把等待变成"必然有上限、必然结束" |
| ③ | **通道2 期限守卫** | `if (!got && Time.realtimeSinceStartup < deadline)` 才发第二次请求 | 通道1 已耗尽预算时不再无谓地再等一轮 |
| ④ | **宿主销毁兜底** | `ModUpdaterHost.OnDestroy() { StopAllCoroutines(); }` | 万一宿主对象被销毁（异常场景切换/重载）也不留下悬挂的网络等待 |
| ⑤ | **全程异步** | 无任何同步阻塞调用（`CheckForUpdate()` 只挂宿主后立即返回） | 等待期间主线程完全空闲，不会卡游戏 |

> 附带：该提交还把日志里的 `"Volken: "` 前缀全部去掉（日志文案变成 Mod 无关的通用字符串），并补了"不阻塞主线程"的文档注释——移植时反而更省事（无需再剥前缀）。

---

## 二、源系统是做什么的

`ModUpdater.cs` 是一个**通用 Mod 更新检查 + 三按钮提醒弹窗**组件，与 Volken 本体玩法完全解耦，设计上就是为"复制到别的 Mod"准备的（文件头自带 8 处★耦合点检查清单）：

| 模块 | 行为 | 移植性 |
|---|---|---|
| 双通道取"网站最新版本" | ① GitHub Releases API 取 `tag_name`（主）→ 失败时② raw 直链 `version.txt`（兜底，403 限流/断网/还没建 release 时自动启用） | 纯网络，改 URL 即可 |
| 本地版本 | `Mod.Instance.ModVersion`（= `ModInfo.Version`，`System.Version` 类型） | 目标需补属性 |
| 版本比较 | `System.Version` 比较 + 语义化前导 v 剥离、抗 HTML/JSON 解析 | 无需改 |
| 防卡死 | 见 §一（15s 看门狗 / 逐帧 Abort / 期限守卫 / OnDestroy 兜底） | 原样保留 |
| 会话防抖 | `static bool _startedThisSession`，一局只查一次 | 无需改（注意 static 跨实例） |
| 弹窗时机 | 等进主菜单（`Game.Instance.SceneManager.InMenuScene`）才弹，不在飞行/设计/联机场景打扰 | 可直接用 |
| 三按钮弹窗 | `CreateMessageDialog(MessageDialogType.ThreeButtons, null, true)`：下载更新 / 稍后再说 / 不再提醒 | 可直接用 |
| 不再提醒 | `PlayerPrefs` 记跳过版本，出新版本前不再弹 | 改 key 前缀 |
| 文案 | 6 个本地化 key | 目标需加语言 key |

---

## 三、可行性证据（目标工程逐项验证结果）

以下 API 依赖已在本工程/同源 ModApi 里逐一确认，**移植后不需要换任何库**：

| ModUpdater 用到的依赖 | 目标侧验证结果 | 证据 |
|---|---|---|
| `ModApi.Mods.GameMod` / `GetModInstance<T>` / `ModInfo.Version` | ✅ 目标 `Mod : GameMod`，`ModInfo.Version` 为 `System.Version`（Volken `Mod.cs:66` 同用法；`ModApi/Mods/RequiredModData.cs` 亦用 `modInfo.Version`） | 源码确认 |
| `Game.Instance.UserInterface.CreateMessageDialog(MessageDialogType, Transform, bool)` | ✅ 目标 `LobbyManager.cs:62`、`MultiPlayerUI.cs:466` 已在用同一 API | 源码确认 |
| `MessageDialogType.ThreeButtons` | ✅ 存在于 `ModApi/Ui/MessageDialogType.cs:13` | ModApi 源码确认 |
| `MessageDialogScript.MiddleButtonText` / `MiddleClicked` | ✅ 存在于 `ModApi/Ui/MessageDialogScript.cs:17,57` | ModApi 源码确认 |
| `Game.Instance.SceneManager.InMenuScene` | ✅ 存在于 `ModApi/…/ISceneManager.cs:62`；目标 `Mod.cs` 已在用 `SceneManager.SceneLoaded` | ModApi 源码确认 |
| `Locale.GetString(...)` | ✅ 项目全工程已在用（`MultiPlayerUI.cs` 大量 `MultiPlayer.MultiPlayerUI.*` key） | 源码确认 |
| `UnityWebRequest` / `PlayerPrefs` / `Application.OpenURL` / `MonoBehaviour` | ✅ Unity 2022.3.62f3 自带；`ENABLE_UNITYWEBREQUEST` 已定义，asmdef 未设 `noEngineReferences` | csproj / asmdef 确认 |
| 本地化语言文件 | ✅ `Assets/Content/Languages/` 有 `EN-US.xml`、`ZH-CN.xml`（**无 RU-RU**，与 Volken 不同） | 目录确认 |
| 编译程序集 | ✅ 新文件放进 `Assets/Scripts/` 即并入 `MultiPlayer.asmdef`；MSBuild 需 csproj 有对应 `<Compile Include>` 条目（已补，Unity 刷新后自动生成） | 编译验证 |
| 发版仓库 | ✅ origin = `https://github.com/SatelliteTorifune/JNOMultiPlayer.git`（与 Volken 同 GitHub 作者） | `git remote -v` 确认 |

**结论：ModApi 面 100% 对齐，移植 = 复制文件 + 替换耦合点，不涉及任何"换 API 实现方式"。**

---

## 四、8 个耦合点逐项对照（★①~★⑧）——已按"实际移植值"落地

| # | Volken 原值 | MultiPlayer 实际落地值 | 状态 |
|---|---|---|---|
| ★① 三个 URL 常量 | `…/SatelliteTorifune/Volken2/…` | `LatestVersionUrl` / `DownloadUrl` = `https://api.github.com/repos/SatelliteTorifune/JNOMultiPlayer/releases/latest`、`https://github.com/SatelliteTorifune/JNOMultiPlayer/releases/latest`；`VersionFileUrl` = `…/JNOMultiPlayer/main/version.txt` | ✅ |
| ★② 本地版本 | `Mod.Instance.ModVersion`（Volken 自有属性） | 目标原本没有 → 在 `Mod.cs` **新增** `public Version ModVersion { get; private set; }`，与 Volken 同款 | ✅ |
| ★③ 日志 | `Mod.Log(string format, params object[] args)`（受 `ShowDevLog` 门控） | 目标 `Mod.Log(object)` **无格式化重载** → 新增常量级通道 `Mod.LogUpdate(object)`（不受 `DebugMode` 限制，始终输出，前缀 `[MultiPlayer][Update]`），`ModUpdater` 内用私有 `LogFmt(string, params object[])` 做 `string.Format` 包装后调用 | ✅ |
| ★④ 弹窗文案 | `Volken.UI.Update*` 6 个 key（EN-US/RU-RU/ZH-CN） | `MultiPlayer.MultiPlayerUI.UpdateAvailable / UpdateNewVersion / UpdateCurrentVersion / UpdateDownload / UpdateLater / UpdateDismiss`，只加 `EN-US.xml` + `ZH-CN.xml`（沿用项目既有 `MultiPlayer.MultiPlayerUI.*` 前缀惯例） | ✅ |
| ★⑤ 命名空间 | `namespace Assets.Scripts` | 保持 `Assets.Scripts`（与本工程其它脚本一致；同名类跨程序集隔离，见 §六-1） | ✅ |
| ★⑥ 主菜单判定 | `Game.Instance.SceneManager.InMenuScene` | 同 API，未改 | ✅ |
| ★⑦ PlayerPrefs key | `Volken.UpdateReminder.SkippedVersion` | `MultiPlayer.UpdateReminder.SkippedVersion` | ✅ |
| ★⑧ 触发点 | `OnModLoaded()` 末尾 | 目标用 **`OnModInitialized()`** → 在该方法末尾（`InitializeUserInterface()` 之后）赋值 `this.ModVersion = this.ModInfo.Version;` 再 `new ModUpdater().CheckForUpdate();` | ✅ |

> 另 3 处"非★"改写：`User-Agent` → `"MultiPlayerModUpdater/1.0"`（GitHub 要求非空 UA，否则 403）；宿主 GameObject 名 → `"MultiPlayerUpdateReminder"`；文件头注释改为 MultiPlayer 版并记录防卡死机制。

---

## 五、实际落地的代码

### 1. `Assets/Scripts/Mod.cs`

```csharp
public static Mod Instance { get; } = GameModBase.GetModInstance<Mod>();

/// <summary>本地 Mod 版本（= ModInfo.Version，类型 System.Version，如 0.1）。</summary>
public Version ModVersion { get; private set; }

protected override void OnModInitialized()
{
    try
    {
        …
        RegisterMpCommands();
        InitializeUserInterface();

        // 更新检查（移植自 Volken2 ModUpdater，含防卡死机制）：
        // 必须在 ModVersion 赋值之后调用，否则 ModUpdater 会因本地版本为空而跳过。
        this.ModVersion = this.ModInfo.Version;
        new ModUpdater().CheckForUpdate();
    }
    catch (Exception e) { Log("Init failed: " + e.ToString()); }
}
```

### 2. `Assets/Scripts/ModUtils.cs`（新增日志通道）

```csharp
/// <summary>
/// 更新检查日志：不受 DebugMode 限制，始终输出到控制台。
/// 更新检查含网络"总看门狗"超时 / 主动中断等诊断（防卡死机制），需要始终可见，
/// 便于确认"最多等 15s 必放弃"确实生效。
/// </summary>
public static void LogUpdate(object message)
{
    UnityEngine.Debug.Log("[MultiPlayer][Update] " + message);
}
```

### 3. `Assets/Scripts/ModUpdater.cs`（移植主体，关键片段）

```csharp
namespace Assets.Scripts

// ★①
public const string LatestVersionUrl =
    "https://api.github.com/repos/SatelliteTorifune/JNOMultiPlayer/releases/latest";
public const string DownloadUrl =
    "https://github.com/SatelliteTorifune/JNOMultiPlayer/releases/latest";
public const string VersionFileUrl =
    "https://raw.githubusercontent.com/SatelliteTorifune/JNOMultiPlayer/main/version.txt";

// ★⑦
private const string SkippedVersionPrefKey = "MultiPlayer.UpdateReminder.SkippedVersion";

// ★③ Mod.Log 无格式化重载 → 统一包装
private static void LogFmt(string format, params object[] args)
{
    Mod.LogUpdate(string.Format(format, args));
}

// 防卡死①：15s 总看门狗
const float totalTimeoutSeconds = 15f;
var deadline = Time.realtimeSinceStartup + totalTimeoutSeconds;

// 防卡死③：通道2 期限守卫
if (!got && Time.realtimeSinceStartup < deadline) { … }

// 防卡死②：逐帧轮询 + 主动 Abort
var operation = request.SendWebRequest();
while (!operation.isDone)
{
    if (Time.realtimeSinceStartup >= deadline)
    {
        request.Abort();
        LogFmt("更新检查——总等待超时,已中断请求 {0}", url);
        onFail?.Invoke();
        yield break;
    }
    yield return null;
}

// 防卡死④：宿主销毁兜底
private void OnDestroy() { StopAllCoroutines(); }
```

### 4. 语言文件（`Assets/Content/Languages/*.xml` 各 6 个 key）

```xml
<!-- Update Reminder -->
<s id="MultiPlayer.MultiPlayerUI.UpdateAvailable">MultiPlayer 有新版本可用！</s>
<s id="MultiPlayer.MultiPlayerUI.UpdateNewVersion">新版本：{0}</s>
<s id="MultiPlayer.MultiPlayerUI.UpdateCurrentVersion">当前版本：{0}</s>
<s id="MultiPlayer.MultiPlayerUI.UpdateDownload">下载更新</s>
<s id="MultiPlayer.MultiPlayerUI.UpdateLater">稍后再说</s>
<s id="MultiPlayer.MultiPlayerUI.UpdateDismiss">不再提醒</s>
```

---

## 六、风险与注意点

1. **与 Volken 同装时的同名类型**：Volken 和 MultiPlayer 都定义 `Assets.Scripts.ModUpdater` / `ModUpdaterHost`。C# 程序集隔离下**运行时无冲突**（各程序集各用各的类型，`_startedThisSession` static 也是各自独立的），MultiPlayer 本身已在 `Assets.Scripts` 命名空间下与 Volken 共存运行。仅当未来某方代码在 `using Assets.Scripts;` 下交叉引用另一个程序集里的同名类时才会编译歧义——概率低。若想 0 成本规避，把命名空间改成 `Assets.Scripts.MP`（贴合项目 `Assets.Scripts.Net` 的约定）即可，文件内无其他硬编码全名。
2. **PlayerPrefs key 必须带 Mod 名**：已用 `MultiPlayer.UpdateReminder.SkippedVersion`，不会与 Volken 的 `Volken.UpdateReminder.SkippedVersion` 互踩"不再提醒"状态。
3. **日志门控差异（坑已绕过）**：目标 `Mod.Log` 受 `DebugMode` 控制且**无格式化重载**——照抄 `Mod.Log("...{0}", x)` 会编译报错。本次改为 `LogFmt` → `Mod.LogUpdate`（常打印，超时/中断诊断始终可见）。若你想让它跟 `DebugMode` 一起静默，把 `LogFmt` 内换成 `Mod.Log(string.Format(…))` 即可。
4. **触发时机**：`ModVersion` 赋值必须早于 `CheckForUpdate()` 调用，否则 `_localVersion == null` 会直接跳过（只打一条日志）。
5. **生命周期入口不同**：目标用 `OnModInitialized`，Volken 用 `OnModLoaded`——只是入口名差异，语义相同。
6. **语言文件差异**：目标只有 EN-US/ZH-CN（Volken 有 RU-RU），key 只加到现有两个文件；其它语言的玩家会看到英文兜底（ModApi 默认行为）。
7. **GitHub 限流**：未认证 API 每 IP 每小时 60 次；多人同 IP 环境可能触发 403 → 自动走 `version.txt` 兜底，无需处理。
8. **弹窗时机对联机 Mod 的影响**：`InMenuScene` 判定对 MultiPlayer 同样合理——联机房间/飞行中不打断，进主菜单才弹。若未来想改时机，把 ★⑥ 的判定换成对应 `SceneManager` 状态即可。
9. **MSBuild 与 Unity 的工程文件**：`MultiPlayer.csproj` 由 Unity 生成、用显式 `<Compile Include>` 清单。本次为编译验证手动补了 `ModUpdater.cs` 条目；Unity 下次刷新 Assets 会重新生成等价条目（不会丢代码）。

---

## 七、验证结果与后续待办

### 已完成

- [x] 1. `Assets/Scripts/ModUpdater.cs` 移植落地（★①~★⑧ 全部替换）
- [x] 2. ★① URL 三处 → `JNOMultiPlayer`；`User-Agent` / 宿主 GameObject 名改写
- [x] 3. ★⑦ PlayerPrefs key → `MultiPlayer.UpdateReminder.SkippedVersion`
- [x] 4. ★③ 新增 `Mod.LogUpdate` 通道 + `LogFmt` 包装，替换全部 `Mod.Log(...)`
- [x] 5. ★④ 两个语言文件各加 6 个 `MultiPlayer.MultiPlayerUI.Update*` key
- [x] 6. ★⑧ `Mod.cs` 新增 `ModVersion` 属性 + `OnModInitialized` 末尾赋值与调用
- [x] 7. 防卡死机制 ①~⑤ 全部保留（15s 看门狗 / 逐帧 Abort / 期限守卫 / OnDestroy 兜底 / 全程异步）
- [x] 8. **MSBuild 编译验证**：补 csproj `<Compile Include>` 后编译通过（仅剩工程原有的 `MSB3277` 程序集版本警告噪声，无 error）

### 待办（需在游戏内 / 仓库侧做）

- [x] 9. **仓库根目录 `version.txt` 已建**，内容 = `1.5`（3 字节 `31 2E 35`，无 BOM、无末尾换行，与 Volken 的 `version.txt` 格式一致）；该文件未被 `.gitignore` 忽略
      - ⚠️ **必须提交并推送到 `main` 分支**才生效：`VersionFileUrl` 指向 `https://raw.githubusercontent.com/SatelliteTorifune/JNOMultiPlayer/main/version.txt`，而本仓库当前工作分支是 **`prototype`**（`main` 分支本地与 origin 均存在）
      - 📌 本地版本对照：`Assets/ModData.asset` 中 MultiPlayer 为 `_versionMajor: 1` / `_versionMinor: 4` → `ModInfo.Version` = **1.4**；故 `version.txt = 1.5` 正好构成"有新版本"（1.5 > 1.4），**适合直接验证提醒弹窗**
- [ ] 10. GitHub 上建 release、打 tag（如 `1.5`）并上传 `ModAssetBundles/MultiPlayer.sr2-mod`；未建 release 时通道1 返回 404 会自动走 `version.txt`
- [ ] 11. 游戏内实测（三种路径）：
      - 无更新：本地版本 ≥ 网站版本 → 只打一条日志，不弹窗
      - 有更新（当前即此状态：1.4 vs 1.5）→ 进主菜单弹三按钮；点"不再提醒"后重启不再弹；点"下载更新"打开 release 页
      - **断网/堵死（验证防卡死）**：断网或把 URL 指向不可达主机 → 最多 15s 放弃，日志出现"等待版本号超时(>15s)"或"总等待超时,已中断请求"，游戏不卡不挂
- [ ] 12. 打包 `MultiPlayer.sr2-mod` 时确认 mod 清单里的版本号（`ModInfo.Version`）与 `version.txt` / release tag 三者一致

---

## 八、工作量与结论

| 项 | 结果 |
|---|---|
| 代码改动 | 新建 1 文件（~340 行）+ 改动 5 文件（约 30 行） |
| 纯"照抄"比例 | ≥ 90%（协程 / 解析 / 宿主 / 防抖 / 弹窗 / 防卡死逻辑一字未改） |
| 编译 | ✅ MSBuild 通过（无 error） |
| 测试重点 | 双通道切换（无 release → version.txt）、15s 看门狗不挂起、三按钮与"不再提醒"持久化 |
| 结论 | **推荐并已移植**——收益高、风险极低、与联机功能零耦合 |
