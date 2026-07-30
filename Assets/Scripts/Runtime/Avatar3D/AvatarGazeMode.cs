using System;
using System.Collections.Generic;

namespace NeonCompanion.Runtime.Avatar3D
{
    /// <summary>Where the companion's eyes are told to look.</summary>
    public enum AvatarGazeMode
    {
        /// <summary>Eyes rest forward; only the idle saccades move them.</summary>
        None = 0,

        /// <summary>Eyes hold the viewer — they track the render camera as it orbits.</summary>
        Camera = 1,

        /// <summary>Eyes follow the cursor, resolved through the render camera's ray.</summary>
        Cursor = 2
    }

    /// <summary>Loose-string parsing for the gaze mode, matching the emotion path.</summary>
    public static class AvatarGazeModes
    {
        private static readonly Dictionary<string, AvatarGazeMode> Aliases =
            BuildAliases();

        public static bool TryParse(string name, out AvatarGazeMode mode)
        {
            mode = AvatarGazeMode.Cursor;
            if (string.IsNullOrWhiteSpace(name))
                return false;
            return Aliases.TryGetValue(name.Trim(), out mode);
        }

        private static Dictionary<string, AvatarGazeMode> BuildAliases()
        {
            var aliases = new Dictionary<string, AvatarGazeMode>(
                StringComparer.OrdinalIgnoreCase);
            aliases["none"] = AvatarGazeMode.None;
            aliases["off"] = AvatarGazeMode.None;
            aliases["fixed"] = AvatarGazeMode.None;
            aliases["camera"] = AvatarGazeMode.Camera;
            aliases["viewer"] = AvatarGazeMode.Camera;
            aliases["face"] = AvatarGazeMode.Camera;
            aliases["cursor"] = AvatarGazeMode.Cursor;
            aliases["mouse"] = AvatarGazeMode.Cursor;
            aliases["pointer"] = AvatarGazeMode.Cursor;
            return aliases;
        }
    }
}
