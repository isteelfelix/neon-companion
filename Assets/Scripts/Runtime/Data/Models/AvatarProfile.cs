using System;

namespace NeonCompanion.Runtime.Data.Models
{
    [Serializable]
    public class AvatarProfile
    {
        public string id;
        public string name;
        public string imagePath;
        public bool isBuiltIn;
    }
}
