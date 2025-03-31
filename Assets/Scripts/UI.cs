using System;
using System.Linq;
using System.Xml.Linq;
using ModApi.Mods;
using ModApi.Ui;
using UI.Xml;


namespace Assets.Scripts
{
    public partial class Mod : GameMod
    {


    }
    public static class ReplayUI
    {
        public static void Initialize()
        {
            Game.Instance.UserInterface.AddBuildUserInterfaceXmlAction("Ui/Xml/Flight/ViewPanel", OnBuildViewPanel); 
            
            Game.Instance.SceneManager.SceneLoaded += (sender, e) => {
                if (Game.Instance.SceneManager.InFlightScene)
                {
                    Game.Instance.FlightScene.GameObject.GetComponentsInChildren<XmlElement>().ToList().ForEach(x =>
                    {
                        if (x.id == "start-record-button")
                        {
                            x.AddOnClickEvent(OnRecordButtonClicked);
                        }
                        else if (x.id == "start-replay-button")
                        {
                            x.AddOnClickEvent(OnReplayButtonClicked);
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
                        $"<ContentButton name=\"ReplayButton\" id=\"start-replay-button\" class=\"view-button audio-btn-click\" tooltip=\"Replay\" xmlns=\"{xNamespace}\">" +
                        "    <Image sprite=\"Ui/Sprites/Flight/IconTimePlay\" />" +
                        "</ContentButton>"
                    )
                );
                cameraPanelButton.AddAfterSelf(
                    XElement.Parse(
                        $"<ContentButton name=\"RecordButton\" id=\"start-record-button\" class=\"view-button audio-btn-click\" tooltip=\"Record\" xmlns=\"{xNamespace}\">" +
                        "    <Image sprite=\"ReplayTools/Sprites/RecordIcon\" />" +
                        "</ContentButton>"
                    )
                );
            }
        }
        static void OnRecordButtonClicked()
        {
            Mod.Instance.StartRecord();
        }
        static void OnReplayButtonClicked()
        {
            Mod.Instance.ReplayQuickLoad();
        }
    }

}



