using System;
using System.Linq;
using System.Text;
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
            // 在线玩家数与玩家名列表（含自己；加入/离开/状态变化时自动刷新）
            inspectorModel.Add(new TextModel(
                Locale.GetString("MultiPlayer.MultiPlayerUI.Players"),
                GetPlayersText, null, null, null));
            // 当前发包频率（所有人可见；房主调整后广播同步）
            inspectorModel.Add(new TextModel(
                Locale.GetString("MultiPlayer.MultiPlayerUI.TickRateLabel"),
                GetTickRateText, null, null, null));

            inspectorModel.Add(new TextButtonModel(Locale.GetString("MultiPlayer.MultiPlayerUI.HostLobbyButton"), (b) => OnSteamHostLobbyClick()));
            inspectorModel.Add(new TextButtonModel(Locale.GetString("MultiPlayer.MultiPlayerUI.JoinLobbyButton"), (b) => OnSteamJoinLobbyClick()));
            inspectorModel.Add(new TextButtonModel(Locale.GetString("MultiPlayer.MultiPlayerUI.DisconnectButton"), (b) => OnDisconnectClick()));

            // --- 房主设置（仅房主可见，非房主自动隐藏）---
            GroupModel hostGroup = new GroupModel(Locale.GetString("MultiPlayer.MultiPlayerUI.HostSettings"), null);
            hostGroup.DetermineVisibility = () => MpNetworkManager.Instance != null && MpNetworkManager.Instance.IsServer;
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

        /// <summary>在线玩家列表文本（实时）：连接人数 + 每个玩家名（含自己）。</summary>
        private static string GetPlayersText()
        {
            MpNetworkManager m = MpNetworkManager.Instance;
            if (m == null || !m.IsConnected)
                return Locale.GetString("MultiPlayer.MultiPlayerUI.NoPlayers");

            // GetPlayers() 只含远端玩家（房主端=客户端们；客户端端=房主+其他客户端），自己单独加上。
            var peers = m.GetPlayers().OrderBy(p => p.PlayerId).ToList();
            var sb = new StringBuilder();
            sb.AppendLine(Locale.GetString("MultiPlayer.MultiPlayerUI.PlayersConnected", peers.Count + 1));
            string selfName = string.IsNullOrEmpty(m.PlayerName) ? ("Player " + m.PlayerId) : m.PlayerName;
            sb.Append("  • ").AppendLine(m.IsServer
                ? selfName + " " + Locale.GetString("MultiPlayer.MultiPlayerUI.HostTag")
                : selfName);
            foreach (MpPeer p in peers)
            {
                string name = string.IsNullOrEmpty(p.PlayerName) ? ("Player " + p.PlayerId) : p.PlayerName;
                sb.Append("  • ").AppendLine(name);
            }
            return sb.ToString().TrimEnd('\r', '\n');
        }

        /// <summary>当前发包频率文本（实时）。</summary>
        private static string GetTickRateText()
        {
            MpNetworkManager m = MpNetworkManager.Instance;
            if (m == null) return string.Empty;
            return Locale.GetString("MultiPlayer.MultiPlayerUI.TickRate", m.TickRate);
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

        /// <summary>TCP debug：切到 TcpTransport 并开房（房主监听 IPAddress.Any:DefaultTcpPort）。</summary>
        private void OnTcpHostLobbyClick()
        {
            MpNetworkManager mgr = LobbyManager.Instance.EnsureMpManager();
            if (mgr != null) mgr.SetTransport(new Net.TcpTransport());
            bool ok = LobbyManager.Instance.HostLobby(DefaultTcpPort);
            if (ok)
            {
                global::ModApi.Ui.MessageDialogScript msg = Game.Instance.UserInterface.CreateMessageDialog(global::ModApi.Ui.MessageDialogType.Okay, null, true);
                msg.MessageText = Locale.GetString("MultiPlayer.MultiPlayerUI.TcpHostStarted", DefaultTcpPort);
            }
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
