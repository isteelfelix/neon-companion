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
        // 100 = render tokens as the provider delivers them; below 100 paces the
        // reveal from a buffer for a smoother typewriter feel.
        public int chatStreamingSpeedPercent = 100;
        // Move the avatar's mouth to the streaming text when no voice is playing.
        // Real audio always takes priority over this imitation.
        public bool streamingMouthImitation = true;
        // React with facial emotions to emojis in the assistant's replies (read from
        // the visible stream in real time). Master switch for the emotion reactions.
        public bool avatarEmotionReactions = true;
        public bool enterToSend = true;
        public bool useSystemPrompt = true;
        public bool encryptKeys = false;
        public bool maskLogs = true;
        public bool voiceIOEnabled = true;
        public bool voiceAlwaysReply = false;
        public string avatarShape = "round";
        public string avatarViewMode = "static";
        public string uiTheme = "indigo";
        public bool showHalo = true;
        public bool breathingAnimation = true;
        public string language = "ru";
        public string closeHotkey = "Escape";
        public string toolPermissionMode = "manual";
        public List<string> alwaysApprovedTools = new List<string>();

        // Windows Companion display process (display-only; no provider/session data).
        public string companionDockState = "docked";
        public bool companionModeEnabled = false;
        public bool companionWindowVisible = true;
        public bool companionWindowPinned = true;
        public bool companionWindowClickThrough = false;
        public int companionWindowMonitor = 0;
        public float companionWindowScale = 1f;
        public int companionWindowPositionX = int.MinValue;
        public int companionWindowPositionY = int.MinValue;

        // Hermes backend
        public string backendMode = "openai"; // "openai" | "hermes"
        public string hermesWsUrl = "";
        public string hermesRestUrl = "";

        // Voice (universal)
        public string inputDeviceName = "";   // microphone device name (empty = system default)
        public float outputVolume = 0.8f;     // 0.0-1.0

        // Avatar render quality. Shared with the pet-window process, which reads the same
        // settings file. Never null after Load — see NormalizeGraphics.
        public AvatarGraphicsSettings graphics = new AvatarGraphicsSettings();

        /// <summary>
        /// Repairs the graphics block after deserialization. A settings file written before
        /// this feature existed has no "graphics" key at all, and JsonUtility leaves the
        /// field at its initialized default; a hand-edited one may hold out-of-range values.
        /// </summary>
        public AvatarGraphicsSettings NormalizeGraphics()
        {
            if (graphics == null)
                graphics = new AvatarGraphicsSettings();
            graphics.Normalize();
            return graphics;
        }
    }
}
