using System;

namespace NeonCompanion.Runtime.Data.Models
{
    [Serializable]
    public class ProviderConfig
    {
        public string id;
        public string displayName;
        public string baseUrl;
        public string apiKey;
        public string defaultModel;
        public float temperature = 0.7f;
        public int maxTokens = 512;
        public int contextWindow = 0; // 0 = unknown/not set
        public bool isEnabled = true;

        /// <summary>
        /// Тип бэкенда: "hermes", null (generic OpenAI-compatible).
        /// Определяет, какой IProviderAdapter используется.
        /// </summary>
        public string backendType; // null = generic

        // Voice settings (OpenAI-compatible backend)
        public string sttProvider;    // "openai", "groq", "local" — null = auto
        public string ttsProvider;    // "edge", "openai", "elevenlabs", "minimax", "mistral" — null = auto
        public string ttsVoice;       // voice ID/name for TTS
        public string ttsModel;       // TTS model (e.g. "tts-1", "tts-1-hd")
        public float ttsSpeed = 1.0f; // 0.25-4.0
        public string sttLanguage;    // Whisper language (e.g. "ru", "en")

        public static ProviderConfig CreateDefault(string name, string baseUrl)
        {
            return new ProviderConfig
            {
                id = Guid.NewGuid().ToString("N"),
                displayName = name,
                baseUrl = baseUrl,
                apiKey = string.Empty,
                defaultModel = "gpt-4o-mini"
            };
        }
    }
}
