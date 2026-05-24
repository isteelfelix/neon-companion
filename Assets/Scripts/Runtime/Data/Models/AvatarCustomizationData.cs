using System;

namespace NeonCompanion.Runtime.Data.Models
{
    [Serializable]
    public class AvatarCustomizationData
    {
        public string PrimaryColor = "#FFFFFF";
        public string SecondaryColor = "#7C7AED";
        public string HaloColor = "#7C7AED";
        public float HaloIntensity = 0.6f;
        public float Saturation = 1f;
        public float Brightness = 1f;
        public string OverlayEmoji = string.Empty;
        public string CustomFrame = "none";
    }
}
