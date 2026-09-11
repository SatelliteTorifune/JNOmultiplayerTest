# Volken 冲突排查:JNOmultiplayerTest 的 NRE 中断 SceneLoaded 事件链

> 日期:2026-08-27
> 现象:Volken 云完全看不到;**「使用游戏自带云分布」开关无法开启**(提示"该星球没有自带云数据")。此组合此前出现过一次,本次定位到根因。

## 1. 现象

- 进入飞行场景(Droo)后,**云完全不可见**(程序化云也不显示)。
- UI 里 **「使用游戏自带云分布」Toggle 无法打开** → 代码里 `VolkenUserInterface.cs` 在 `StockCloudMap.Current == null` 时拒绝开启并提示 "该星球没有自带云数据"。
- 两个症状同源(见 §3),且与 **JNOmultiplayerTest 联机 mod 同装**时出现。

## 2. 日志证据

`Player.log`(会话 5:18:11):

```
Mod Loaded: Volken, Version 0.51 - 8/26/2026 5:18:11 PM
...
Loaded Scene on Flight-True Subscribed-False and Quality-Ultra
OnSceneLoaded更新Drood数量
OnSceneLoaded执行doShit
[Mptest][Lobby] MP.OnFlightSceneLoaded: cleared stale remote crafts (count=0)
NullReferenceException: Object reference not set to an instance of an object
  at Assets.Scripts.MultiPlayerUI.OnSceneLoaded (System.Object Sender, ModApi.Scenes.Events.SceneEventArgs e) [0x00016]
  at (wrapper delegate-invoke) System.EventHandler`1[ModApi.Scenes.Events.SceneEventArgs].invoke_void_object_TEventArgs(object,ModApi.Scenes.Events.SceneEventArgs)
  at Assets.Scripts.Scenes.SceneManager.OnSceneLoaded (System.String sceneName) [0x00023]
```

该会话**没有任何 Volken 初始化日志**(无 "Refreshing config list"、无 StockCloudMap 加载行),
而 `ModSettings.xml` 里 `Volken ShowDevLog="true"`(日志开关是开的) → 结论:Volken 的 `OnSceneLoaded` 根本没执行。

## 3. 根因(冲突链)

`JNOmultiplayerTest/Assets/Scripts/MultiPlayerUI.cs`:

```csharp
private void Awake()
{
    Instance = this;
    Game.Instance.SceneManager.SceneLoaded += OnSceneLoaded;   // L47:常驻组件注册到场景事件链
}

private void OnSceneLoaded(object Sender, SceneEventArgs e)
{
    if (e.Scene == "Flight")
    {
        inspectorPanel.Visible = false;            // L622:inspectorPanel 为 null → NRE!
        inspectorPanel.CloseButtonClicked += OnCloseButtonClicked;
        Game.Instance.FlightScene.FlightEnded += FlightSceneEnded;
    }
}
```

机制:

1. JNO 的 `MultiPlayerUI` 是 `DontDestroyOnLoad` 常驻组件,在 `Mod.OnModInitialized` 里创建,`Awake` 中订阅 `SceneManager.SceneLoaded`。JNO 的 mod 加载早于 Volken → 在事件链中**靠前**。
2. 用户**从未打开过联机面板**时 `inspectorPanel == null` → `inspectorPanel.Visible = false` 抛 **NullReferenceException**(IL 偏移 0x16,即该行)。
3. .NET 多播委托中**一个处理器抛异常会中断其后所有处理器** → 排在 JNO 后面的 Volken `OnSceneLoaded`(`Volken.cs` L69 注册)与 `VolkenUserInterface.OnSceneLoaded` **都被跳过**。
4. 结果:
   - `OnSceneLoaded` 未执行 → **未创建 `CloudRenderer`** → 看不到云。
   - 未调用 `StockCloudMap.LoadFor` → `StockCloudMap.Current == null` → **自带云开关锁死**。

> 间歇性:若当时联机面板恰好已创建(panel 非空),则不抛异常,事件链正常,Volken 正常初始化。
> 这就是"以前出现过一次、这次又出现"的原因——JNO 面板状态 / 加载时序不同导致 NRE 时有时无。

## 4. 修复

### 4.1 JNO 侧(根因,建议修复)`MultiPlayerUI.OnSceneLoaded` 加 null 保护

文件:`C:\renko\unityProjects\JNOmultiplayerTest\Assets\Scripts\MultiPlayerUI.cs`(L618-626)

```csharp
private void OnSceneLoaded(object Sender, SceneEventArgs e)
{
    if (e.Scene == "Flight")
    {
        // 修复:inspectorPanel 在从未打开过联机面板时为 null,
        // 直接解引用会 NRE;且该异常会中断 SceneLoaded 事件链,使链中其后
        // 注册的其它 mod(如 Volken)的 OnSceneLoaded 不执行(看不到云等)。
        if (inspectorPanel != null)
        {
            inspectorPanel.Visible = false;
            inspectorPanel.CloseButtonClicked += OnCloseButtonClicked;
        }
        Game.Instance.FlightScene.FlightEnded += FlightSceneEnded;
    }
}
```

(已尝试由本工作区直接打补丁,因跨项目被沙箱拦截 → 请手动应用此补丁。)

### 4.2 Volken 侧(加固,已实施):自愈初始化,不再依赖事件链

即使其他 mod/游戏的事件处理器再抛异常,Volken 也会在飞行场景中周期性自检并补初始化。

`Volken.cs` 新增(已改):

```csharp
public void EnsureCloudInitIfNeeded()
{
    try
    {
        if (Game.Instance?.FlightScene == null) return;
        var gameCam = Game.Instance.FlightScene.ViewManager.GameView.GameCamera;
        if (gameCam == null || gameCam.NearCamera == null) return;
        if (gameCam.NearCamera.gameObject.GetComponent<CloudRenderer>() != null) return;
        Mod.LOG("Volken: self-heal — CloudRenderer missing, re-running Flight init");
        OnSceneLoaded(new object(), new SceneEventArgs("Flight"));
    }
    catch (Exception ex) { Mod.LOG("Volken: EnsureCloudInitIfNeeded ERROR: " + ex); }
}
```

`VolkenUserInterface.cs` 新增 `Update()` 驱动(每 0.5s,幂等):

```csharp
private float _cloudInitCheckTimer;
private void Update()
{
    if (Game.Instance?.FlightScene == null) return;
    _cloudInitCheckTimer -= Time.unscaledDeltaTime;
    if (_cloudInitCheckTimer > 0f) return;
    _cloudInitCheckTimer = 0.5f;
    Volken.Instance?.EnsureCloudInitIfNeeded();
}
```

> 显式传 `SceneEventArgs("Flight")` 也覆盖"OnSceneLoaded 跑了但 e.Scene!="Flight" 提前 return" 的情况。

## 5. 额外发现

- Mods 目录同时存在 `Volken.sr2-mod` 与 `Volken-R.sr2-mod` 两个同名程序集 mod,Player.log 只加载了 `Volken 0.51`。建议**只保留一个**,避免程序集/类型名冲突。

## 6. 验证

1. 手动给 JNO 的 `MultiPlayerUI.OnSceneLoaded` 打 §4.1 补丁(或至少确认该 NRE 不再出现)。
2. 重新构建 Volken(含 §4.2 自愈)。
3. 进 Droo 飞行:
   - 云应出现;「使用游戏自带云分布」应能开启。
   - Player.log 若出现 `Volken: self-heal — CloudRenderer missing` 说明自愈触发过(正常)。
4. 若仍看不到云且无 self-heal 日志 → 走另一条路排查(OnRenderImage 每帧抛异常被 catch 吞掉)。
