using System;
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
using UnityEngine.SceneManagement;

namespace Assets.Scripts
{
    public class MultiPlayerUI:MonoBehaviourBase
    {
        #region 字段

        public static MultiPlayerUI Instance;
        public const string MpUiBottomId = "toggle-multiplayer-ui-bottom";
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

        #region 检查器面板

        public void CreateInspectorPanel()
        {
            // 大家好啊,我是从隔壁Droodism偷来的分割线
            inspectorModel = new InspectorModel("MPUI",
                "<color=yellow>" + Locale.GetString("MultiPlayer.MultiPlayerUI.MPinspector"));
            inspectorModel.Add(new TextButtonModel(Locale.GetString("MultiPlayer.MultiPlayerUI.HostLobbyButton"), (b) => OnSteamHostLobbyClick()));
            inspectorModel.Add(new TextButtonModel(Locale.GetString("MultiPlayer.MultiPlayerUI.JoinLobbyButton"), (b) => OnSteamJoinLobbyClick()));
            inspectorModel.Add(new TextButtonModel(Locale.GetString("MultiPlayer.MultiPlayerUI.DisconnectButton"), (b) => OnDisconnectClick()));
           
            inspectorPanel = Game.Instance.UserInterface.CreateInspectorPanel(inspectorModel,
                new InspectorPanelCreationInfo()
                {
                    PanelWidth = 400,
                    Resizable = true,
                });
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
            // 端口已无意义（Steam 无真实端口/端口转发），直接开房
            bool ok = LobbyManager.Instance.HostLobby(0);
            if (ok)
            {
                ulong steamId = 0UL;
                var mgr =  MpNetworkManager.Instance;
                if (mgr != null && mgr.Transport is Net.SteamTransport st) steamId = st.LocalSteamId;
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
                    LobbyManager.Instance.JoinLobby(steamId, 0);
                }
                else
                {
                    global::ModApi.Ui.MessageDialogScript msg = Game.Instance.UserInterface.CreateMessageDialog(global::ModApi.Ui.MessageDialogType.Okay, null, true);
                    msg.MessageText = Locale.GetString("MultiPlayer.MultiPlayerUI.InvalidSteamId", steamId);
                }
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

        private void OnSceneLoaded(Object Sender, SceneEventArgs e)
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
