using System.Linq;
using System.Xml.Linq;
using ModApi.Ui;
using UI.Xml;
using Assets.Scripts.Net;

namespace Assets.Scripts
{
    public partial class  Mod
    {
        private void BuildUi()
        {
            Game.Instance.UserInterface.AddBuildUserInterfaceXmlAction("Ui/Xml/Flight/ViewPanel", OnBuildViewPanel); 
            Game.Instance.SceneManager.SceneLoaded += (sender, e) => 
            {
                if (Game.Instance.SceneManager.InFlightScene)
                {
                    Game.Instance.FlightScene.GameObject.GetComponentsInChildren<XmlElement>().ToList().ForEach(x =>
                    {
                        if (x.id == "host-lobby")
                        {
                            x.AddOnClickEvent(OnHostLobbyClick);
                        }
                        if (x.id == "join-lobby")
                        {
                            x.AddOnClickEvent(OnJoinLobbyClick);
                        }
                    });
                }
            };
        }
        private static readonly XNamespace xNamespace = XmlLayoutConstants.XmlNamespace;
        private static void OnBuildViewPanel(BuildUserInterfaceXmlRequest request)
        {
            var cameraPanelButton =
                request.XmlDocument.Descendants(xNamespace + "ContentButton")
                    .FirstOrDefault(n => n.Attribute("id")?.Value == "toggle-camera-panel-button");

            if (cameraPanelButton != null)
            {
                cameraPanelButton.AddAfterSelf(
                    XElement.Parse(
                        $"<ContentButton name=\"Join-LobbyButton\" id=\"host-lobby\" class=\"view-button audio-btn-click\" tooltip=\"host lobby\" xmlns=\"{xNamespace}\">" +
                        "    <Image sprite=\"aMptest/Sprites/HostLobby\" />" +
                        "</ContentButton>"
                    )
                );
                cameraPanelButton.AddAfterSelf(
                    XElement.Parse(
                        $"<ContentButton name=\"Join-LobbyyButton\" id=\"join-lobby\" class=\"view-button audio-btn-click\" tooltip=\"Join-Lobby\" xmlns=\"{xNamespace}\">" +
                        "    <Image sprite=\"aMptest/Sprites/JoinLobby\" />" +
                        "</ContentButton>"
                    )
                );
            }
        }

        /// <summary>
        /// Host：Steam P2P 开房（无需端口，port 忽略），开房后显示本机 SteamId 供好友加入。
        /// </summary>
        private void OnHostLobbyClick()
        {
            // 端口已无意义（Steam 无真实端口/端口转发），直接开房
            bool ok = HostLobby(0);
            if (ok)
            {
                ulong steamId = 0UL;
                var mgr = MpNetworkManager.Instance;
                if (mgr != null && mgr.Transport is Net.SteamTransport st) steamId = st.LocalSteamId;
                global::ModApi.Ui.MessageDialogScript msg = Game.Instance.UserInterface.CreateMessageDialog(global::ModApi.Ui.MessageDialogType.Okay, null, true);
                msg.MessageText = "Lobby started!\nYour SteamId:\n" + steamId + "\n\n";
            }
        }

        /// <summary>
        /// Join：Steam P2P 按房主 SteamId 加入（替代 IP:port）。
        /// </summary>
        private void OnJoinLobbyClick()
        {
            global::ModApi.Ui.InputDialogScript idDialog = Game.Instance.UserInterface.CreateInputDialog(null);
            idDialog.MessageText = "Enter Host SteamId";
            idDialog.InputText = "";
            idDialog.OkayClicked += delegate(global::ModApi.Ui.InputDialogScript d)
            {
                string steamId = idDialog.InputText.Trim();
                d.Close();
                if (ulong.TryParse(steamId, out ulong _) && steamId.Length > 0)
                {
                    JoinLobby(steamId, 0);
                }
                else
                {
                    global::ModApi.Ui.MessageDialogScript msg = Game.Instance.UserInterface.CreateMessageDialog(global::ModApi.Ui.MessageDialogType.Okay, null, true);
                    msg.MessageText = "Invalid SteamId: '" + steamId + "'\n应为 17 位数字（如 76561199127915239）";
                }
            };
        }
    }
    
}