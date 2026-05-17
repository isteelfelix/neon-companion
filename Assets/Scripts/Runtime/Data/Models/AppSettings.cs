using System;

namespace NeonCompanion.Runtime.Data.Models
{
    [Serializable]
    public class AppSettings
    {
        public string activeProviderId;
        public string activeAvatarId;
        public bool saveChatHistory = true;
        public string language = "ru";
    }
}
