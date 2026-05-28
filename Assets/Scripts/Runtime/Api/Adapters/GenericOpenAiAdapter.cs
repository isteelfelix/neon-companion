using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace NeonCompanion.Runtime.Api.Adapters
{
    public sealed class GenericOpenAiAdapter : IProviderAdapter
    {
        public ProviderCapabilities GetCapabilities()
        {
            return new ProviderCapabilities
            {
                SupportsModelSwitch = false,
                SupportsInventory = false,
                SupportsToolProgress = false,
                UsesMaxCompletionTokens = false,
                RequiresTemperatureOmission = false,
                ForceNonStreaming = false,
                IgnoresStreamFlag = false
            };
        }

        public void ApplyRequestHeaders(UnityWebRequest request, string providerSessionId)
        {
            // Generic — ничего специфичного
        }

        public string ExtractSessionId(UnityWebRequest response, string fallback)
        {
            return fallback;
        }

        public string[] BuildDiscoveryEndpoints(string baseUrl)
        {
            var normalized = NormalizeBaseUrl(baseUrl);
            if (normalized.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(0, normalized.Length - "/chat/completions".Length);
            }
            return new string[] { normalized + "/models" };
        }

        public IReadOnlyList<string> ParseDiscoveryResponse(string json)
        {
            return ParseModelIds(json);
        }

        public ModelSwitchPayload BuildModelSwitchRequest(string model, string providerSessionId)
        {
            return null;
        }

        public string ParseModelSwitchResponse(string responseContent)
        {
            return null;
        }

        private static string NormalizeBaseUrl(string baseUrl)
        {
            return (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        }

        private static IReadOnlyList<string> ParseModelIds(string json)
        {
            if (string.IsNullOrEmpty(json))
                return null;

            try
            {
                var response = JsonUtility.FromJson<OpenAiModelsResponse>(json);
                if (response == null || response.data == null || response.data.Length == 0)
                    return null;

                var ids = new List<string>(response.data.Length);
                for (int i = 0; i < response.data.Length; i++)
                {
                    var entry = response.data[i];
                    if (entry != null && !string.IsNullOrWhiteSpace(entry.id))
                        ids.Add(entry.id);
                }

                return ids.Count > 0 ? ids : null;
            }
            catch
            {
                return null;
            }
        }

        [Serializable]
        private class OpenAiModelsResponse
        {
            public OpenAiModelEntry[] data;
        }

        [Serializable]
        private class OpenAiModelEntry
        {
            public string id;
        }
    }
}