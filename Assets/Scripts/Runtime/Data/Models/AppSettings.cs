using System;

namespace NeonCompanion.Runtime.Data.Models
{
    [Serializable]
    public class AppSettings
    {
        public string activeProviderId;
        public string activeAvatarId = "neon";
        public bool saveChatHistory = true;
        public bool streaming = true;
        public bool enterToSend = true;
        public bool useSystemPrompt = true;
        public bool encryptKeys = false;
        public bool maskLogs = true;
        public bool voiceIOEnabled = true;
        public string avatarShape = "round";
        public bool showHalo = true;
        public bool breathingAnimation = true;
        public string language = "ru";
        public string closeHotkey = "Escape";
    }
}
