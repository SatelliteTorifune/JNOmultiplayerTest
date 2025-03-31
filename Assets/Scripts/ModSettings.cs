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

        public EnumSetting<replayModes> ReplayMode { get; set; }
        public BoolSetting ReplayCraftControls { get; set; }
        public BoolSetting ReplayDeactivatePhysics { get; set; }
        public BoolSetting RecordBodiesTransform { get; set; }

        public enum replayModes
        {
            Normal,
            Precise,
            None,
        }
        protected override void InitializeSettings()
        {
            ReplayMode = CreateEnum<replayModes>("Replay Mode")
                .SetDescription("Switch Replay mode\n\nNormal: Only change craft velocity in Replay. Smoother but less precise\n\nPrecise: Change both velocity and position in replay. More precise but sometimes will cause lag \n\nNone: Do not change the craft's transform in replay. Best performance but with least precision")
                .SetDefault(replayModes.Normal);

            ReplayCraftControls = CreateBool("Replicate Craft Controls")
                .SetDescription("Record craft's control (Pitch/Yaw/Roll, throttle, sliders, etc), and use them in replay")
                .SetDefault(true);

            ReplayDeactivatePhysics = CreateBool("Disable Physics Calculation")
                .SetDescription("Deactivate craft's physic simulation (Drag, Collision, etc) on replay start\n\nCan significantly increase frame rate but may cause some issue on your craft(e.g. clipping)")
                .SetDefault(false);

            RecordBodiesTransform = CreateBool("Use RigidBody Transform")
                .SetDescription("Record rigid body transform and use them in replay \n\nMay cause performance issue on larger craft, and sometimes will cause unexpected behavior to occur, use at your own risk")
                .SetDefault(false);
        }
    }
}
