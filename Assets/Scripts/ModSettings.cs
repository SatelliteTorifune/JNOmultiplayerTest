using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ModApi.Common;
using ModApi.Settings.Core;
namespace Assets.Scripts
{

    /// <summary>
    /// The settings for the mod.
    /// </summary>
    /// <seealso cref="ModApi.Settings.Core.SettingsCategory{Assets.Scripts.ModSettings}" />
    public class ModSettings : SettingsCategory<ModSettings>
    {

        private static ModSettings _instance;

        public ModSettings() : base("Maybe it's MP")
        {
        }
        public static ModSettings Instance => _instance ?? (_instance = Game.Instance.Settings.ModSettings.GetCategory<ModSettings>());

        public BoolSetting DebugMode { get; set; }
        
        public StringSetting PlayerName { get; set; }

        protected override void InitializeSettings()
        {
            PlayerName=CreateString("Player Name")
                .SetDescription("The name of the player in multiplayer")
                .SetDefault("LazyNullName");
            DebugMode=CreateBool("Debug Mode")
                .SetDescription("Enable debug mode, which will print debug information to the console")
                .SetDefault(false);
        }
    }
}
