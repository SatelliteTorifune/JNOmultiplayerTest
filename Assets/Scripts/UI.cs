using System.Linq;
using System.Xml.Linq;
using ModApi.Ui;
using UI.Xml;

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

        private void OnHostLobbyClick()
        {
            global::ModApi.Ui.InputDialogScript portDialog = Game.Instance.UserInterface.CreateInputDialog(null);
            portDialog.MessageText = "Enter Port";
            portDialog.InputText = "";
            portDialog.OkayClicked += delegate(global::ModApi.Ui.InputDialogScript d)
            {
                d.Close();
                HostLobby(int.Parse(portDialog.InputText));
            };
        }

        private void OnJoinLobbyClick()
        {
            global::ModApi.Ui.InputDialogScript ipDialog = Game.Instance.UserInterface.CreateInputDialog(null);
            ipDialog.MessageText = "Enter Ip";
            ipDialog.InputText = "127.0.0.1";
            ipDialog.OkayClicked += delegate(global::ModApi.Ui.InputDialogScript d)
            {
                string ipString = ipDialog.InputText;
                d.Close();

                global::ModApi.Ui.InputDialogScript portDialog = Game.Instance.UserInterface.CreateInputDialog(null);
                portDialog.MessageText = "Enter Port";
                portDialog.InputText = "25555";
                portDialog.OkayClicked += delegate(global::ModApi.Ui.InputDialogScript d1)
                {
                    int portNumber = int.Parse(portDialog.InputText);
                    d1.Close();
                    JoinLobby(ipString, portNumber);
                };
            };
        }
    }
    
}