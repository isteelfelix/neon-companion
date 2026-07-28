using System;
using System.Collections.Generic;

namespace NeonCompanion.Runtime.Core
{
    /// <summary>
    /// Context limits for common OpenAI models whose /v1/models objects expose identity only.
    /// Keep this table aligned with https://developers.openai.com/api/docs/models.
    /// Provider/runtime metadata remains authoritative when it is available.
    /// </summary>
    internal static class KnownModelContextRegistry
    {
        private static readonly Dictionary<string, int> ExactLimits =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "gpt-4o", 128000 },
                { "gpt-4o-mini", 128000 },
                { "gpt-4.1", 1047576 },
                { "gpt-4.1-mini", 1047576 },
                { "gpt-4.1-nano", 1047576 },
                { "o3", 200000 },
                { "o3-mini", 200000 },
                { "o4-mini", 200000 },
                { "gpt-5", 400000 },
                { "gpt-5-mini", 400000 },
                { "gpt-5-nano", 400000 },
                { "gpt-5-chat-latest", 128000 },
                { "gpt-5.4", 1050000 },
                { "gpt-5.4-mini", 400000 },
                { "gpt-5.4-nano", 400000 },
                { "gpt-5.5", 1050000 },
                { "gpt-5.5-pro", 1050000 },
                { "gpt-5.6", 1050000 },
                { "gpt-5.6-sol", 1050000 },
                { "gpt-5.6-terra", 1050000 },
                { "gpt-5.6-luna", 1050000 }
            };

        private static readonly KeyValuePair<string, int>[] PrefixLimits =
        {
            new KeyValuePair<string, int>("gpt-5.6-terra", 1050000),
            new KeyValuePair<string, int>("gpt-5.6-luna", 1050000),
            new KeyValuePair<string, int>("gpt-5.6-sol", 1050000),
            new KeyValuePair<string, int>("gpt-5.6", 1050000),
            new KeyValuePair<string, int>("gpt-5.5", 1050000),
            new KeyValuePair<string, int>("gpt-5.4-mini", 400000),
            new KeyValuePair<string, int>("gpt-5.4-nano", 400000),
            new KeyValuePair<string, int>("gpt-5.4", 1050000),
            new KeyValuePair<string, int>("gpt-5-chat-latest", 128000),
            new KeyValuePair<string, int>("gpt-5-mini", 400000),
            new KeyValuePair<string, int>("gpt-5-nano", 400000),
            new KeyValuePair<string, int>("gpt-5", 400000),
            new KeyValuePair<string, int>("gpt-4.1-mini", 1047576),
            new KeyValuePair<string, int>("gpt-4.1-nano", 1047576),
            new KeyValuePair<string, int>("gpt-4.1", 1047576),
            new KeyValuePair<string, int>("gpt-4o-mini", 128000),
            new KeyValuePair<string, int>("gpt-4o", 128000),
            new KeyValuePair<string, int>("o4-mini", 200000),
            new KeyValuePair<string, int>("o3-mini", 200000),
            new KeyValuePair<string, int>("o3", 200000)
        };

        public static int GetContextWindow(string baseUrl, string modelId)
        {
            if (!IsOfficialOpenAiEndpoint(baseUrl) || string.IsNullOrWhiteSpace(modelId))
                return 0;

            string normalized = modelId.Trim();
            if (ExactLimits.TryGetValue(normalized, out int exact))
                return exact;

            for (int i = 0; i < PrefixLimits.Length; i++)
            {
                var pair = PrefixLimits[i];
                if (normalized.StartsWith(pair.Key + "-", StringComparison.OrdinalIgnoreCase))
                    return pair.Value;
            }

            return 0;
        }

        private static bool IsOfficialOpenAiEndpoint(string baseUrl)
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri uri))
                return false;

            return string.Equals(uri.Host, "api.openai.com", StringComparison.OrdinalIgnoreCase);
        }
    }
}
