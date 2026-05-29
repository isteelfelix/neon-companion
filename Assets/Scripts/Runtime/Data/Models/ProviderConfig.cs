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
        public bool isEnabled = true;

        /// <summary>
        /// Тип бэкенда: "hermes", null (generic OpenAI-compatible).
        /// Определяет, какой IProviderAdapter используется.
        /// </summary>
        public string backendType; // null = generic

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
