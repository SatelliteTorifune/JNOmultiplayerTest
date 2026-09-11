using System;
using System.Collections;
using System.Text.RegularExpressions;
using ModApi;
using ModApi.Ui;
using UnityEngine;
using UnityEngine.Networking;

namespace Assets.Scripts // ★⑤ 与目标 Mod 一致(本工程全部脚本都在 Assets.Scripts,程序集隔离,与 Volken 同名类不冲突)
{
    /// <summary>
    /// 通用 Mod 更新检查 + 提醒弹窗系统。
    /// 移植自 Volken2 的 Assets/Scripts/ModUpdater.cs(2026-09-10 版本,含防卡死机制),
    /// 适配到 aMptest(SimpleRockets 2 / JNO 联机 Mod)。
    ///
    /// 【它做什么】
    ///   1. 读取本地版本(ModInfo.Version,类型 System.Version,如 0.6);
    ///   2. 双通道获取"网站最新版本":
    ///       通道1:GitHub Releases API 的 tag_name(主);
    ///       通道2:raw 直链 version.txt 兜底(API 限流/断网/还没建过 release 时自动启用);
    ///   3. 网站最新 &gt; 本地版本 且玩家没点过"不再提醒" → 等进主菜单弹三按钮弹窗
    ///      (下载更新 / 稍后再说 / 不再提醒);
    ///   4. "不再提醒"用 PlayerPrefs 记住跳过的版本,以后只有出现更新版本才再次提醒。
    ///
    /// ==================================================================
    /// 【防卡死机制(Volken 2026-09-10 新增,本次一并移植)】
    /// ==================================================================
    ///  ① 15s 总看门狗:FetchRoutine 开始时算 deadline = Time.realtimeSinceStartup + 15,
    ///     整个"等最新版本号"的过程必须在 deadline 前结束。UnityWebRequest.timeout
    ///     只管单个请求,这里管整体等待——双保险。Time.realtimeSinceStartup 不受暂停/
    ///     卡顿影响。
    ///  ② 逐帧轮询 + 主动 Abort:TryFetchVersion 不写 `yield return request.SendWebRequest()`,
    ///     而是 `var operation = request.SendWebRequest();` 后逐帧检查 operation.isDone,
    ///     每帧顺带比对 deadline,超时就 request.Abort() + 回调失败 + yield break。
    ///     把"等版本号"变成必然有上限、必然结束的过程。
    ///  ③ 通道2 期限守卫:只有 `!got && Time.realtimeSinceStartup &lt; deadline` 时才发起
    ///     第二次请求(通道1 超时后不再无谓地再等一轮)。
    ///  ④ 宿主销毁兜底:ModUpdaterHost.OnDestroy 里 StopAllCoroutines(),
    ///     万一宿主对象被销毁(异常场景切换/重载)也不留下悬挂的网络等待。
    ///  ⑤ 全程异步:本组件没有任何同步阻塞主线程的调用,等待期间主线程完全空闲。
    ///
    /// 每次游戏会话只检查一次(static _startedThisSession 防抖)。
    ///
    /// ==================================================================
    /// 【本文件已对 aMptest 完成的适配点(对照 Volken 原版的★耦合点)】
    /// ==================================================================
    ///  ★① 三个 URL 常量 → SatelliteTorifune/JNOmultiplayerTest 仓库;
    ///  ★② 本地版本来源 → Mod.Instance.ModVersion(在 Mod.cs 新增的属性,= ModInfo.Version);
    ///  ★③ 日志 → Mod.LogUpdate(...)(本工程 Mod.Log 无格式化重载,故统一用 LogFmt 包装;
    ///         LogUpdate 常打印,便于诊断超时/中断);
    ///  ★④ 弹窗文案 → Locale.GetString("MultiPlayer.MultiPlayerUI.Update*")
    ///         (见 Assets/Content/Languages/{EN-US,ZH-CN}.xml 的 6 个 key);
    ///  ★⑤ 命名空间 → 保持 Assets.Scripts(与本工程其它脚本一致);
    ///  ★⑥ 主菜单判定 → Game.Instance.SceneManager.InMenuScene(未改);
    ///  ★⑦ PlayerPrefs key → "aMptest.UpdateReminder.SkippedVersion";
    ///  ★⑧ 触发点 → Mod.cs 的 OnModInitialized() 末尾(在 ModVersion 赋值之后)。
    ///
    /// 【依赖】全部是游戏 ModApi / Unity 自带,无需额外包:
    ///   - ModApi.Ui:IUserInterface.CreateMessageDialog / MessageDialogScript / MessageDialogType
    ///   - UnityEngine:UnityWebRequest / PlayerPrefs / Application.OpenURL / MonoBehaviour
    ///   - System.Version(版本比较)
    ///
    /// 【发版配套】仓库根目录需有 version.txt(内容就是版本号,如 "0.1")并推送到 main 分支;
    /// GitHub 上 Create a new release 打 tag 如 0.1 并上传 .sr2-mod 资产。
    /// 没建过 release 时通道1 返回 404,会自动走 version.txt 兜底。
    /// </summary>
    public class ModUpdater
    {
        // ==================================================================
        // ★① 网站最新版本来源
        // ==================================================================
        // 通道1:GitHub Releases API——返回 JSON,取 "tag_name" 即最新版本号
        //        (如 "0.5" / "v0.6.1",前导 v 会被忽略)。
        public const string LatestVersionUrl =
            "https://api.github.com/repos/SatelliteTorifune/JNOmultiplayerTest/releases/latest";

        // 点"下载更新"时打开的页面:Release 列表页(或改成具体某个 release 的页面)。
        public const string DownloadUrl =
            "https://github.com/SatelliteTorifune/JNOmultiplayerTest/releases/latest";

        // 通道2(兜底):GitHub raw 直链的 version.txt(仓库根目录,内容就是版本号,如 "0.6")。
        // 用途:通道1 失败(403 限流 / 网络异常 / 还没建过 release)时自动启用;无 API 限流。
        // 注意:指向 main 分支——发版时记得把 version.txt 同步到 main。
        // 留空("")则禁用兜底通道。
        public const string VersionFileUrl =
            "https://raw.githubusercontent.com/SatelliteTorifune/JNOmultiplayerTest/main/version.txt";

        // ★⑦ 玩家"不再提醒"记住的版本存哪。带本 Mod 名前缀,避免与 Volken 等其它 Mod 互相覆盖。
        private const string SkippedVersionPrefKey = "aMptest.UpdateReminder.SkippedVersion";

        // 防抖:一次游戏会话只检查一次(static 跨实例共享)。
        private static bool _startedThisSession;

        private Version _localVersion;
        private ModUpdaterHost _host;

        /// <summary>
        /// ★③ 日志:本工程 Mod.Log(object) 没有格式化重载,统一在这里 string.Format 后走
        /// Mod.LogUpdate(不受 DebugMode 限制,始终输出——超时/中断诊断需要始终可见)。
        /// </summary>
        private static void LogFmt(string format, params object[] args)
        {
            Mod.LogUpdate(string.Format(format, args));
        }

        /// <summary>
        /// 启动一次更新检查(每次游戏会话最多执行一次,重复调用会被忽略)。
        /// 在 Mod.cs 的 OnModInitialized() 末尾调用(★⑧),必须在本 ModVersion 赋值之后。
        ///
        /// 【不阻塞主线程的保证】本方法本身不发起也不等待任何网络请求——
        /// 只注册协程宿主后立即返回。真正的 HTTP 请求在协程里异步执行
        /// (UnityWebRequest + 逐帧轮询 + 总看门狗),等待最新版本号期间
        /// 主线程完全空闲;即便网络异常/断网,最多等 15s 也会主动放弃。
        /// </summary>
        public void CheckForUpdate()
        {
            try
            {
                if (_startedThisSession) return;
                _startedThisSession = true;

                // ★② 本地版本来源:Mod.Instance.ModVersion(= ModInfo.Version,System.Version 类型)
                _localVersion = Mod.Instance.ModVersion;
                if (_localVersion == null)
                {
                    LogFmt("更新检查跳过——ModVersion 为空"); // ★③
                    return;
                }

                if (string.IsNullOrWhiteSpace(LatestVersionUrl))
                {
                    // 兜底:URL 为空(如调试时临时清空)则只打日志,不弹窗
                    LogFmt("更新检查——LatestVersionUrl 未配置,当前版本 {0}", _localVersion); // ★③
                    return;
                }

                // 协程宿主:非 MonoBehaviour 类用隐藏 GameObject 跑 UnityWebRequest 协程。
                if (_host == null)
                {
                    var go = new GameObject("aMptestUpdateReminder"); // 名字随意,仅便于排查
                    GameObject.DontDestroyOnLoad(go);
                    _host = go.AddComponent<ModUpdaterHost>();
                    _host.Owner = this;
                }
            }
            catch (Exception ex)
            {
                LogFmt("更新检查初始化失败: {0}", ex); // ★③
            }
        }

        /// <summary>
        /// 协程主体:双通道获取网站最新版本 → 与本地版本比较 → 需要时等主菜单并弹窗。
        /// 通道1:GitHub Releases API(取 tag_name);通道2:raw 直链 version.txt(API 失败时兜底)。
        /// 【防卡死】整个过程受 15s 总看门狗约束,见类注释。
        /// </summary>
        public IEnumerator FetchRoutine()
        {
            // 总看门狗:无论网络多慢/多坏,整个"等最新版本号"的过程必须在
            // deadline 前结束(Time.realtimeSinceStartup 不受暂停/卡顿影响)。
            // UnityWebRequest.timeout 只管单个请求,这里管整体等待——双保险,
            // 保证等待永远有上限,绝不无限挂起(也不存在任何同步阻塞主线程的调用)。
            const float totalTimeoutSeconds = 15f;
            var deadline = Time.realtimeSinceStartup + totalTimeoutSeconds;

            Version latest = null;
            var got = false;

            // 通道1:GitHub Releases API
            yield return TryFetchVersion(LatestVersionUrl, deadline, v => { latest = v; got = true; }, () => { });

            // 通道2:API 失败(限流/断网/无 release)时回退到 version.txt;
            // 前提是还没到总看门狗期限(否则直接放弃,不再发起第二次请求)。
            if (!got && Time.realtimeSinceStartup < deadline)
            {
                LogFmt("更新检查——API 通道不可用,回退到 version.txt"); // ★③
                yield return TryFetchVersion(VersionFileUrl, deadline, v => { latest = v; got = true; }, () => { });
            }

            if (!got || latest == null)
            {
                if (Time.realtimeSinceStartup >= deadline)
                    LogFmt("更新检查——等待版本号超时(>{0}s),本次跳过提醒", totalTimeoutSeconds); // ★③
                else
                    LogFmt("更新检查——所有通道均失败,本次跳过提醒"); // ★③
                yield break;
            }

            LogFmt("更新检查——当前 {0},网站最新 {1}", _localVersion, latest); // ★③
            if (latest <= _localVersion) yield break; // 已是最新,不打扰

            // 用户点过"不再提醒"且该版本已跳过?
            if (Version.TryParse(PlayerPrefs.GetString(SkippedVersionPrefKey, ""), out var skipped)
                && latest <= skipped)
            {
                LogFmt("更新检查——版本 {0} 已被用户跳过提醒", latest); // ★③
                yield break;
            }

            // ★⑥ 主菜单判定:等进入主菜单再弹(避免在飞行/设计/联机场景打断玩家)。
            //     想在其他场景弹,把 InMenuScene 换成对应的判定即可。
            while (Game.Instance == null || !Game.Instance.SceneManager.InMenuScene)
            {
                yield return null;
            }

            ShowUpdateDialog(latest);
        }

        /// <summary>
        /// 抓取指定 URL 并解析版本号。成功时回调 onSuccess(version),失败时回调 onFail。
        /// deadline = 总看门狗期限(Time.realtimeSinceStartup),超过则主动 Abort 放弃。
        /// 两个通道共用的下载+解析原语。
        /// </summary>
        private IEnumerator TryFetchVersion(string url, float deadline, Action<Version> onSuccess, Action onFail)
        {
            using (var request = UnityWebRequest.Get(url))
            {
                request.timeout = 10;
                // GitHub 的 URL(API 与 raw)都要求非空 User-Agent,否则返回 403
                request.SetRequestHeader("User-Agent", "aMptestModUpdater/1.0");

                // 关键点:这里是纯异步等待,主线程完全空闲,不会阻塞游戏。
                // 逐帧轮询 isDone 而不是直接 `yield return request.SendWebRequest()`,
                // 是为了每帧顺带检查总看门狗 deadline——超时就主动 Abort 中断,
                // 把"等版本号"变成一个必然有上限、必然结束的过程。
                var operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    if (Time.realtimeSinceStartup >= deadline)
                    {
                        request.Abort(); // 主动中断并释放连接,随后随 using 一起释放
                        LogFmt("更新检查——总等待超时,已中断请求 {0}", url); // ★③
                        onFail?.Invoke();
                        yield break;
                    }
                    yield return null; // 每帧只做一次布尔比较,开销可忽略
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    LogFmt("更新检查——请求失败 {0}: {1}", url, request.error); // ★③
                    onFail?.Invoke();
                    yield break;
                }

                if (TryParseLatestVersion(request.downloadHandler.text, out var version))
                {
                    onSuccess?.Invoke(version);
                }
                else
                {
                    LogFmt("更新检查——无法解析 {0} 的版本,原文: {1}", url, request.downloadHandler.text); // ★③
                    onFail?.Invoke();
                }
            }
        }

        /// <summary>
        /// 从网站响应里解析版本号。兼容:
        /// 纯文本("0.7" / "v0.7.1")、GitHub Releases JSON("tag_name":"v0.7.1")、{"version":"0.7"}。
        /// 通用,无需改动。
        /// </summary>
        private static bool TryParseLatestVersion(string text, out Version version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var s = text.Trim();

            // JSON:优先取 "tag_name"(GitHub Releases)或 "version" 字段的值
            if (s.StartsWith("{") || s.StartsWith("["))
            {
                var match = Regex.Match(s, "\"(?:tag_name|version)\"\\s*:\\s*\"([^\"]+)\"");
                if (match.Success) s = match.Groups[1].Value.Trim();
            }

            // 去掉前导 v/V,再取第一段形如 "数字.数字..." 的内容(抗 HTML/换行干扰)
            s = Regex.Replace(s, "^[vV]", "");
            s = Regex.Match(s, @"\d+(?:\.\d+){1,3}").Value;

            return Version.TryParse(s, out version);
        }

        /// <summary>
        /// 弹三按钮弹窗:下载更新 / 稍后再说 / 不再提醒。
        /// ★④ 弹窗文案来自本地化 key(MultiPlayer.MultiPlayerUI.Update*)。
        /// </summary>
        private void ShowUpdateDialog(Version latest)
        {
            try
            {
                if (Game.Instance?.UserInterface == null) return;

                var dialog = Game.Instance.UserInterface.CreateMessageDialog(MessageDialogType.ThreeButtons, null, true);
                if (dialog == null) return;

                // ★④ 下面 6 个文案 key 对应 EN-US.xml / ZH-CN.xml 里的 MultiPlayer.MultiPlayerUI.Update*
                dialog.MessageText = string.Format(
                    "{0}\n\n{1}\n{2}",
                    Locale.GetString("MultiPlayer.MultiPlayerUI.UpdateAvailable"),
                    string.Format(Locale.GetString("MultiPlayer.MultiPlayerUI.UpdateNewVersion"), latest),
                    string.Format(Locale.GetString("MultiPlayer.MultiPlayerUI.UpdateCurrentVersion"), _localVersion));
                dialog.OkayButtonText = Locale.GetString("MultiPlayer.MultiPlayerUI.UpdateDownload");
                dialog.MiddleButtonText = Locale.GetString("MultiPlayer.MultiPlayerUI.UpdateLater");
                dialog.CancelButtonText = Locale.GetString("MultiPlayer.MultiPlayerUI.UpdateDismiss");

                // 下载更新
                dialog.OkayClicked += d =>
                {
                    d.Close();
                    if (!string.IsNullOrEmpty(DownloadUrl))
                        Application.OpenURL(DownloadUrl);
                    else
                        LogFmt("更新下载页 DownloadUrl 未配置"); // ★③
                };

                // 稍后再说:仅关闭,下次启动仍会提醒
                dialog.MiddleClicked += d => d.Close();

                // 不再提醒:记住跳过的版本,出现更新版本前不再弹
                dialog.CancelClicked += d =>
                {
                    PlayerPrefs.SetString(SkippedVersionPrefKey, latest.ToString());
                    PlayerPrefs.Save();
                    d.Close();
                };
            }
            catch (Exception ex)
            {
                LogFmt("更新提醒弹窗失败: {0}", ex); // ★③
            }
        }

        /// <summary>
        /// 协程宿主:给非 MonoBehaviour 的 ModUpdater 提供跑 UnityWebRequest 协程的载体。
        /// 纯通用,无需改动。
        /// </summary>
        public class ModUpdaterHost : MonoBehaviour
        {
            public ModUpdater Owner;

            private void Start()
            {
                if (Owner != null)
                {
                    StartCoroutine(Owner.FetchRoutine());
                }
            }

            // 【防卡死】保险:万一宿主对象被销毁(异常场景切换/重载等),立刻停掉协程,
            // 确保不会有悬挂的网络等待残留。
            private void OnDestroy()
            {
                StopAllCoroutines();
            }
        }
    }
}
