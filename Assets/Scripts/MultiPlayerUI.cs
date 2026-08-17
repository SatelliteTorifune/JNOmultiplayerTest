using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using System.Xml.Linq;
using Assets.Scripts.Net;
using ModApi;
using ModApi.Flight.Events;
using ModApi.GameLoop;
using ModApi.Scenes.Events;
using ModApi.Ui;
using ModApi.Ui.Inspector;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Assets.Scripts
{
    public class MultiPlayerUI:MonoBehaviourBase
    {
        #region 字段

        public static MultiPlayerUI Instance;
        public const string MpUiBottomId = "toggle-multiplayer-ui-bottom";
        /// <summary>TCP debug 默认端口（与 LobbyManager.HostLobby 默认一致）。</summary>
        private const int DefaultTcpPort = 25555;
        private IInspectorPanel inspectorPanel;
        private InspectorModel inspectorModel;
        /// <summary>玩家列表分组（按玩家集合变化时 ReplaceGroup 重建）。</summary>
        private GroupModel playersGroup;
        /// <summary>玩家集合签名缓存（playerId 列表），变化时重建列表分组。</summary>
        private string playersKey = "";
        /// <summary>兜底轮询定时器（覆盖本机开房/连接/断开等无事件变化）。</summary>
        private float playersRebuildTimer;
        /// <summary>当前已订阅事件的 MpNetworkManager（惰性订阅，manager 创建后首次 Update 才绑定）。</summary>
        private MpNetworkManager trackedManager;
        /// <summary>事件驱动的脏标记：OnPlayerJoined/OnPlayerLeft 置位，主线程 Update 立即重建。</summary>
        private volatile bool playersDirty;
        private bool playersWasVisible;

        #endregion

        #region Unity 生命周期

        private void Awake()
        { 
            Instance = this;
            Game.Instance.SceneManager.SceneLoaded += OnSceneLoaded;
        }

        private void Start()
        {
            Game.Instance.UserInterface.AddBuildUserInterfaceXmlAction(UserInterfaceIds.Flight.NavPanel,
                (BuildUserInterfaceXmlRequest request) =>
                {
                    var ns = XmlLayoutConstants.XmlNamespace;
                    var inspectButton = request.XmlDocument
                        .Descendants(ns + "ContentButton")
                        .First(x => (string)x.Attribute("id") == "toggle-flight-inspector");
                    if (inspectButton != null && inspectButton.Parent != null)
                    {
                        inspectButton.Parent.Add(
                            new XElement(
                                ns + "ContentButton",
                                new XAttribute("id", MpUiBottomId),
                                new XAttribute("class", "panel-button audio-btn-click"),
                                new XAttribute("tooltip", Locale.GetString("MultiPlayer.MultiPlayerUI.MpButtonTooltip")),
                                new XAttribute("name", "NavPanel.OnToggleMPInspectorPanelState"),
                                new XElement(
                                    ns + "Image",
                                    new XAttribute("class", "panel-button-icon"),
                                    new XAttribute("sprite", "aMptest/Sprites/UIIcon"))));
                    }
                });
        }

        private void OnDestroy()
        {
            Game.Instance.SceneManager.SceneLoaded -= OnSceneLoaded;
            if (trackedManager != null)
            {
                trackedManager.OnPlayerJoined -= OnPlayersChanged;
                trackedManager.OnPlayerLeft -= OnPlayersChanged;
                trackedManager = null;
            }
        }

        #endregion

        #region 面板

        public void CreateInspectorPanel()
        {
            // 大家好啊,我是从隔壁Droodism偷来的分割线
            inspectorModel = new InspectorModel("MPUI",
                "<color=yellow>" + Locale.GetString("MultiPlayer.MultiPlayerUI.MPinspector"));

            // --- 联机状态（TextModel 的 valueGetter 每帧轮询，实时刷新）---
            // 连接状态：未连接 / 开房中 / 已连接房主
            inspectorModel.Add(new TextModel(
                Locale.GetString("MultiPlayer.MultiPlayerUI.ConnectionStatus"),
                GetConnectionStatusText, null, null, null));
            // 在线玩家人数（实时；仅数字，避免玩家名单溢出）
            inspectorModel.Add(new TextModel(
                Locale.GetString("MultiPlayer.MultiPlayerUI.Players"),
                GetPlayerCountText, null, null, null));

            inspectorModel.Add(new TextButtonModel(Locale.GetString("MultiPlayer.MultiPlayerUI.HostLobbyButton"), (b) => OnSteamHostLobbyClick()));
            inspectorModel.Add(new TextButtonModel(Locale.GetString("MultiPlayer.MultiPlayerUI.JoinLobbyButton"), (b) => OnSteamJoinLobbyClick()));
            inspectorModel.Add(new TextButtonModel(Locale.GetString("MultiPlayer.MultiPlayerUI.DisconnectButton"), (b) => OnDisconnectClick()));

            // --- 玩家列表（SP2 风格：每玩家一行 = 名字 + 延迟；房主额外每行踢人按钮）---
            // 初始为空分组占位，Update 中检测到玩家集合变化时用 ReplaceGroup 在原位置重建。
            playersGroup = new GroupModel(Locale.GetString("MultiPlayer.MultiPlayerUI.PlayerList"), null);
            playersGroup.Add(new TextModel(Locale.GetString("MultiPlayer.MultiPlayerUI.NoPlayers"), null, null, null, null));
            inspectorModel.AddGroup(playersGroup);

            // --- 房主设置（仅房主可见，非房主自动隐藏）---
            GroupModel hostGroup = new GroupModel(Locale.GetString("MultiPlayer.MultiPlayerUI.HostSettings"), null);
            // 仅房主可见（client/未连接时隐藏）。注意 ItemElement.Collapsed 跟随 Group.Visible，
            // 分组隐藏时其内子项必然一起隐藏；这里给子项再加一层 DetermineVisibility 双重保险。
            bool hostOnly() => MpNetworkManager.Instance != null && MpNetworkManager.Instance.IsServer;
            hostGroup.DetermineVisibility = hostOnly;
            // 滑条（参考游戏 jnoCode 检查器 SliderModel 用法：wholeNumbers 整数步进 + ValueFormatter 显示 Hz）
            SliderModel tickRateSlider = new SliderModel(
                Locale.GetString("MultiPlayer.MultiPlayerUI.TickRateSlider"),
                () => MpNetworkManager.Instance != null ? (float)MpNetworkManager.Instance.TickRate : 20f,
                v =>
                {
                    // 滑块为整数步进，取整后交给管理器（内部 Clamp 1~120；房主会广播给所有客户端）
                    if (MpNetworkManager.Instance != null) MpNetworkManager.Instance.SetTickRate(Mathf.RoundToInt(v));
                },
                20f, 120f, true, true);
            tickRateSlider.DetermineVisibility = hostOnly;
            tickRateSlider.ValueFormatter = (float x) => Mathf.RoundToInt(x) + " Hz";
            tickRateSlider.Tooltip = Locale.GetString("MultiPlayer.MultiPlayerUI.TickRateHint");
            tickRateSlider.ElementName = "Mp.TickRateSlider";
            hostGroup.Add(tickRateSlider);
            inspectorModel.AddGroup(hostGroup);

            // --- 调试（TCP，仅 Debug 模式显示；用于本机/虚拟机联机调试）---
            GroupModel debugGroup = new GroupModel(Locale.GetString("MultiPlayer.MultiPlayerUI.DebugGroup"), null);
            debugGroup.DetermineVisibility = () =>
            {
                try { return ModSettings.Instance != null && ModSettings.Instance.DebugMode.Value; }
                catch { return false; }
            };
            debugGroup.Add(new TextButtonModel(Locale.GetString("MultiPlayer.MultiPlayerUI.TcpHostLobbyButton"), (b) => OnTcpHostLobbyClick()));
            debugGroup.Add(new TextButtonModel(Locale.GetString("MultiPlayer.MultiPlayerUI.TcpJoinLobbyButton"), (b) => OnTcpJoinLobbyClick()));
            inspectorModel.AddGroup(debugGroup);

            inspectorPanel = Game.Instance.UserInterface.CreateInspectorPanel(inspectorModel,
                new InspectorPanelCreationInfo()
                {
                    PanelWidth = 400,
                    Resizable = true,
                });
        }

        /// <summary>连接状态文本（实时）：未连接 / 开房中 / 已连接房主。</summary>
        private static string GetConnectionStatusText()
        {
            MpNetworkManager m = MpNetworkManager.Instance;
            if (m == null || !m.IsConnected)
                return Locale.GetString("MultiPlayer.MultiPlayerUI.NotConnected");
            string name = string.IsNullOrEmpty(m.PlayerName) ? ("Player " + m.PlayerId) : m.PlayerName;
            return m.IsServer
                ? Locale.GetString("MultiPlayer.MultiPlayerUI.HostingStatus", name)
                : Locale.GetString("MultiPlayer.MultiPlayerUI.ConnectedToHost", name, m.PlayerId);
        }

        /// <summary>在线玩家人数（实时）：远端玩家数 + 自己。</summary>
        private static string GetPlayerCountText()
        {
            MpNetworkManager m = MpNetworkManager.Instance;
            if (m == null || !m.IsConnected) return "0";
            return (m.GetPlayers().Count + 1).ToString();
        }
        

        /// <summary>
        /// 玩家列表刷新（SP2 PlayerListScript 同款思路）：
        /// - 事件驱动：OnPlayerJoined/OnPlayerLeft 置 dirty，下一帧立即重建（加入/离开即时生效）；
        /// - 面板打开时核对一次（SP2 Flyout.Opened 同款）；
        /// - 低频兜底轮询（1s）：覆盖"本机开房/连接/断开"这类没有对应事件的变化。
        /// 重建只在主线程 Update 里执行（事件可能来自网络线程，仅置标志，不直接改 UI）。
        /// </summary>
        private void Update()
        {
            if (inspectorModel == null || inspectorPanel == null) return;
            EnsurePlayersSubscribed();

            bool visible = inspectorPanel.Visible;
            if (visible && !playersWasVisible)
            {
                RebuildPlayersIfChanged(); // 面板刚打开：立即核对一次
            }
            playersWasVisible = visible;
            if (!visible) return;          // 面板未打开：不重建（SP2 同款守卫）

            if (playersDirty)
            {
                playersDirty = false;
                RebuildPlayersIfChanged();
                return;
            }

            playersRebuildTimer -= Time.unscaledDeltaTime;
            if (playersRebuildTimer > 0f) return;
            playersRebuildTimer = 1f;
            RebuildPlayersIfChanged();
        }

        /// <summary>订阅/换绑玩家加入、离开事件。manager 是惰性创建的（开房/加入时才存在），首次用到时才绑定。</summary>
        private void EnsurePlayersSubscribed()
        {
            MpNetworkManager m = MpNetworkManager.Instance;
            if (m == null || m == trackedManager) return;
            if (trackedManager != null)
            {
                trackedManager.OnPlayerJoined -= OnPlayersChanged;
                trackedManager.OnPlayerLeft -= OnPlayersChanged;
            }
            trackedManager = m;
            m.OnPlayerJoined += OnPlayersChanged;
            m.OnPlayerLeft += OnPlayersChanged;
            playersDirty = true; // 新 manager：立即重建一次
        }

        /// <summary>玩家加入/离开事件回调。可能来自网络线程，只置标志，重建在 Update 主线程执行。</summary>
        private void OnPlayersChanged(MpPeer peer)
        {
            playersDirty = true;
        }

        /// <summary>玩家集合签名变化时重建"玩家列表"分组（每玩家一行）。签名没变则跳过。</summary>
        private void RebuildPlayersIfChanged()
        {
            MpNetworkManager m = MpNetworkManager.Instance;
            string key;
            if (m == null || !m.IsConnected)
            {
                key = "off";
            }
            else
            {
                List<string> ids = m.GetPlayers().Select(p => p.PlayerId.ToString()).OrderBy(x => x).ToList();
                ids.Insert(0, m.PlayerId.ToString());
                key = (m.IsServer ? "S:" : "C:") + string.Join(",", ids);
            }
            if (key == playersKey) return;
            playersKey = key;

            GroupModel newGroup = BuildPlayersGroup();
            try
            {
                inspectorPanel.ReplaceGroup(playersGroup, newGroup);
            }
            catch (Exception e)
            {
                Mod.LogLobby("MultiPlayerUI: ReplaceGroup players failed: " + e.Message);
            }
            playersGroup = newGroup;
        }

        /// <summary>构建玩家列表分组：每玩家一行（名字 + 延迟），房主额外每行一个踢人按钮。</summary>
        private static GroupModel BuildPlayersGroup()
        {
            MpNetworkManager m = MpNetworkManager.Instance;
            GroupModel g = new GroupModel(Locale.GetString("MultiPlayer.MultiPlayerUI.PlayerList"), null);
            if (m == null || !m.IsConnected)
            {
                g.Add(new TextModel(Locale.GetString("MultiPlayer.MultiPlayerUI.NoPlayers"), null, null, null, null));
                return g;
            }

            string hostTag = " " + Locale.GetString("MultiPlayer.MultiPlayerUI.HostTag");
            // 自己（SP2 风格：房主行显示 HOST；客户端行显示自己到房主的延迟）
            string selfName = string.IsNullOrEmpty(m.PlayerName) ? ("Player " + m.PlayerId) : m.PlayerName;
            if (m.IsServer)
            {
                g.Add(new TextModel(selfName + hostTag, () => "HOST", null, null, null));
            }
            else
            {
                g.Add(new TextModel(selfName + Locale.GetString("MultiPlayer.MultiPlayerUI.YouTag"), () => m.ClientPingMs < 0 ? "—" : m.ClientPingMs + " ms", null, null, null));
            }

            // 远端玩家（房主：GetPlayers = 客户端们；客户端：GetPlayers = 房主 + 其他客户端）
            foreach (MpPeer p in m.GetPlayers().OrderBy(x => x.PlayerId))
            {
                string name = string.IsNullOrEmpty(p.PlayerName) ? ("Player " + p.PlayerId) : p.PlayerName;
                bool isHostPeer = p.PlayerId == 0;
                string label = name + (isHostPeer ? hostTag : "");
                if (m.IsServer)
                {
                    // 房主：每行显示该客户端的实时延迟（预加载期间显示 "⏳ N%"）
                    MpPeer peer = p;
                    g.Add(new TextModel(label, () =>
                    {
                        MpNetworkManager mm = MpNetworkManager.Instance;
                        if (mm != null)
                        {
                            float? lp = mm.GetPlayerLoadProgress(p.PlayerId);
                            if (lp.HasValue) return "⏳ " + Mathf.RoundToInt(lp.Value * 100f) + "%";
                        }
                        return peer.PingMs < 0 ? "—" : peer.PingMs + " ms";
                    }, null, null, null));
                    // 房主：每行一个踢人按钮（不能踢自己）
                    int pid = p.PlayerId;
                    g.Add(new TextButtonModel(Locale.GetString("MultiPlayer.MultiPlayerUI.KickButton", name), (b) => OnKickPlayerClick(pid)));
                }
                else
                {
                    // 客户端：中继拓扑下只能测自己到房主的延迟，其他人的延迟未知（预加载期间显示 "⏳ N%"）
                    g.Add(new TextModel(label, () =>
                    {
                        MpNetworkManager mm = MpNetworkManager.Instance;
                        if (mm != null)
                        {
                            float? lp = mm.GetPlayerLoadProgress(p.PlayerId);
                            if (lp.HasValue) return "⏳ " + Mathf.RoundToInt(lp.Value * 100f) + "%";
                        }
                        return isHostPeer ? "HOST" : "—";
                    }, null, null, null));
                }
            }
            return g;
        }

        /// <summary>房主踢人按钮：弹出确认框后调用 KickPlayer。</summary>
        private static void OnKickPlayerClick(int playerId)
        {
            MpNetworkManager m = MpNetworkManager.Instance;
            if (m == null) return;
            string name = "Player " + playerId;
            foreach (MpPeer p in m.GetPlayers())
            {
                if (p.PlayerId == playerId)
                {
                    if (!string.IsNullOrEmpty(p.PlayerName)) name = p.PlayerName;
                    break;
                }
            }
            global::ModApi.Ui.MessageDialogScript dlg = Game.Instance.UserInterface.CreateMessageDialog(global::ModApi.Ui.MessageDialogType.OkayCancel, null, true);
            dlg.MessageText = Locale.GetString("MultiPlayer.MultiPlayerUI.KickConfirm", name);
            dlg.OkayButtonText = Locale.GetString("MultiPlayer.MultiPlayerUI.Kick");
            dlg.CancelButtonText = Locale.GetString("MultiPlayer.MultiPlayerUI.Cancel");
            dlg.UseDangerButtonStyle = true;
            dlg.OkayClicked += delegate(global::ModApi.Ui.MessageDialogScript d)
            {
                d.Close();
                MpNetworkManager mm = MpNetworkManager.Instance;
                if (mm != null) mm.KickPlayer(playerId);
            };
            dlg.CancelClicked += delegate(global::ModApi.Ui.MessageDialogScript d) { d.Close(); };
        }

        public void OnToggleMPInspectorPanelState()
        {
           
            try
            {
                inspectorPanel.Visible =  !inspectorPanel.Visible;
            }
            catch (Exception)
            {
                CreateInspectorPanel();
                inspectorPanel.Visible =  !inspectorPanel.Visible;
            }
        }

        private void OnCloseButtonClicked(IInspectorPanel inspectorPanel)
        {
            inspectorPanel.Visible = false;
        }

        #endregion

        #region 联机操作

        private void OnSteamHostLobbyClick()
        {
            // 确保走 Steam 传输（若之前切到过 TCP debug，先切回，避免"Steam 按钮实际走 TCP"）
            MpNetworkManager mgr = LobbyManager.Instance.EnsureMpManager();
            if (mgr != null && !(mgr.Transport is Net.SteamTransport)) mgr.SetTransport(new Net.SteamTransport());
            // 端口已无意义（Steam 无真实端口/端口转发），直接开房
            bool ok = LobbyManager.Instance.HostLobby(0);
            if (ok)
            {
                ulong steamId = 0UL;
                var m =  MpNetworkManager.Instance;
                if (m != null && m.Transport is Net.SteamTransport st) steamId = st.LocalSteamId;
                global::ModApi.Ui.MessageDialogScript msg = Game.Instance.UserInterface.CreateMessageDialog(global::ModApi.Ui.MessageDialogType.Okay, null, true);
                msg.MessageText = Locale.GetString("MultiPlayer.MultiPlayerUI.LobbyStarted", steamId);
            }
        }

        private void OnSteamJoinLobbyClick()
        {
            global::ModApi.Ui.InputDialogScript idDialog = Game.Instance.UserInterface.CreateInputDialog(null);
            idDialog.MessageText = Locale.GetString("MultiPlayer.MultiPlayerUI.EnterHostSteamId");
            idDialog.InputText = "";
            idDialog.OkayClicked += delegate(global::ModApi.Ui.InputDialogScript d)
            {
                string steamId = idDialog.InputText.Trim();
                d.Close();
                if (ulong.TryParse(steamId, out ulong _) && steamId.Length > 0)
                {
                    // 确保走 Steam 传输（同 Host 端，防止停留在 TCP debug 传输上）
                    MpNetworkManager mgr = LobbyManager.Instance.EnsureMpManager();
                    if (mgr != null && !(mgr.Transport is Net.SteamTransport)) mgr.SetTransport(new Net.SteamTransport());
                    LobbyManager.Instance.JoinLobby(steamId, 0);
                }
                else
                {
                    global::ModApi.Ui.MessageDialogScript msg = Game.Instance.UserInterface.CreateMessageDialog(global::ModApi.Ui.MessageDialogType.Okay, null, true);
                    msg.MessageText = Locale.GetString("MultiPlayer.MultiPlayerUI.InvalidSteamId", steamId);
                }
            };
        }

        /// <summary>TCP debug：输入端口后切到 TcpTransport 并开房（房主监听 IPAddress.Any:port）。</summary>
        private void OnTcpHostLobbyClick()
        {
            global::ModApi.Ui.InputDialogScript idDialog = Game.Instance.UserInterface.CreateInputDialog(null);
            idDialog.MessageText = Locale.GetString("MultiPlayer.MultiPlayerUI.EnterTcpPort");
            idDialog.InputText = DefaultTcpPort.ToString();
            idDialog.OkayClicked += delegate(global::ModApi.Ui.InputDialogScript d)
            {
                string input = idDialog.InputText.Trim();
                d.Close();
                int port;
                if (!int.TryParse(input, out port) || port < 1 || port > 65535)
                {
                    global::ModApi.Ui.MessageDialogScript msg = Game.Instance.UserInterface.CreateMessageDialog(global::ModApi.Ui.MessageDialogType.Okay, null, true);
                    msg.MessageText = Locale.GetString("MultiPlayer.MultiPlayerUI.InvalidTcpPort", input);
                    return;
                }
                MpNetworkManager mgr = LobbyManager.Instance.EnsureMpManager();
                if (mgr != null) mgr.SetTransport(new Net.TcpTransport());
                bool ok = LobbyManager.Instance.HostLobby(port);
                if (ok)
                {
                    global::ModApi.Ui.MessageDialogScript msg = Game.Instance.UserInterface.CreateMessageDialog(global::ModApi.Ui.MessageDialogType.Okay, null, true);
                    msg.MessageText = Locale.GetString("MultiPlayer.MultiPlayerUI.TcpHostStarted", port);
                }
            };
        }

        /// <summary>TCP debug：输入房主 IP（可带 :端口，默认 25555），切到 TcpTransport 后加入。</summary>
        private void OnTcpJoinLobbyClick()
        {
            global::ModApi.Ui.InputDialogScript idDialog = Game.Instance.UserInterface.CreateInputDialog(null);
            idDialog.MessageText = Locale.GetString("MultiPlayer.MultiPlayerUI.EnterTcpHost");
            idDialog.InputText = "";
            idDialog.OkayClicked += delegate(global::ModApi.Ui.InputDialogScript d)
            {
                string input = idDialog.InputText.Trim();
                d.Close();
                string host = input;
                int port = DefaultTcpPort;
                int colon = input.IndexOf(':');
                if (colon >= 0)
                {
                    host = input.Substring(0, colon).Trim();
                    string portStr = input.Substring(colon + 1).Trim();
                    if (!int.TryParse(portStr, out port)) port = DefaultTcpPort;
                }
                if (string.IsNullOrEmpty(host))
                {
                    global::ModApi.Ui.MessageDialogScript msg = Game.Instance.UserInterface.CreateMessageDialog(global::ModApi.Ui.MessageDialogType.Okay, null, true);
                    msg.MessageText = Locale.GetString("MultiPlayer.MultiPlayerUI.InvalidTcpHost", input);
                    return;
                }
                MpNetworkManager mgr = LobbyManager.Instance.EnsureMpManager();
                if (mgr != null) mgr.SetTransport(new Net.TcpTransport());
                LobbyManager.Instance.JoinLobby(host, port);
            };
        }

        private void OnDisconnectClick()
        {
            // 停止联机（房主关闭房间 / 客户端断开连接），并清理远程飞船
            LobbyManager.Instance.StopLobby();

            global::ModApi.Ui.MessageDialogScript msg = Game.Instance.UserInterface.CreateMessageDialog(global::ModApi.Ui.MessageDialogType.Okay, null, true);
            msg.MessageText = Locale.GetString("MultiPlayer.MultiPlayerUI.Disconnected");
        }

        #endregion

        #region 场景事件

        private void OnSceneLoaded(object Sender, SceneEventArgs e)
        {
            if (e.Scene == "Flight")
            {
                inspectorPanel.Visible = false;
                inspectorPanel.CloseButtonClicked += OnCloseButtonClicked;
                Game.Instance.FlightScene.FlightEnded += FlightSceneEnded;
            }
        }

        private void FlightSceneEnded(object sender, FlightEndedEventArgs e)
        {
            Game.Instance.FlightScene.FlightEnded -= FlightSceneEnded;
        }

        #endregion
    }
}
