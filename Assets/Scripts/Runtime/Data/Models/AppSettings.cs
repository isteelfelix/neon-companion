using System;
using System.Collections.Generic;

namespace NeonCompanion.Runtime.Data.Models
{
    [Serializable]
    public class AppSettings
    {
        public string activeProviderId;
        public string activeOpenAiProviderId;
        public string activeHermesProviderId;
        public string activeAvatarId = "neon";
        public bool saveChatHistory = true;
        public bool streaming = true;
        public bool enterToSend = true;
        public bool useSystemPrompt = true;
        public bool encryptKeys = false;
        public bool maskLogs = true;
        public bool voiceIOEnabled = true;
        public bool voiceAlwaysReply = false;
        public string avatarShape = "round";
        public string avatarViewMode = "static";
        public bool showHalo = true;
        public bool breathingAnimation = true;
        public string language = "ru";
        public string closeHotkey = "Escape";
        public string toolPermissionMode = "manual";
        public List<string> alwaysApprovedTools = new List<string>();

        // Hermes backend
        public string backendMode = "openai"; // "openai" | "hermes"
        public string hermesWsUrl = "";
        public string hermesRestUrl = "";

        // Voice (universal)
        public string inputDeviceName = "";   // microphone device name (empty = system default)
        public string outputDeviceName = "";  // reserved for future platform-specific output device selection
        public float outputVolume = 0.8f;     // 0.0-1.0
    }
}
