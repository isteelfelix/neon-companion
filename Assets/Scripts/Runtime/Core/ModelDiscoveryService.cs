using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Data.Repositories;

namespace NeonCompanion.Runtime.Core
{
    public sealed class ModelDiscoveryService
    {
        private readonly IProviderConfigRepository _repository;
        private readonly Dictionary<string, IReadOnlyList<string>> _cache = new();

        public ModelDiscoveryService(IProviderConfigRepository repository)
        {
            _repository = repository;
        }

        public async Task<IReadOnlyList<string>> DiscoverModelsAsync(ProviderConfig provider, CancellationToken cancellationToken = default)
        {
            if (provider == null || string.IsNullOrWhiteSpace(provider.baseUrl))
                return null;

            var cacheKey = $"{provider.baseUrl}|{provider.apiKey}";
            if (_cache.TryGetValue(cacheKey, out var cached))
                return cached;

            var result = await DiscoverModelsFromEndpointAsync(provider.baseUrl, provider.apiKey, cancellationToken);
            if (result != null && result.Count > 0)
                _cache[cacheKey] = result;

            return result;
        }

        private async Task<IReadOnlyList<string>> DiscoverModelsFromEndpointAsync(string baseUrl, string apiKey, CancellationToken cancellationToken)
        {
            var normalized = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            if (normalized.EndsWith("/chat/completions", System.StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(0, normalized.Length - "/chat/completions".Length);

            var endpoint = $"{normalized}/models";

            using (var webRequest = UnityEngine.Networking.UnityWebRequest.Get(endpoint))
            {
                if (!string.IsNullOrWhiteSpace(apiKey))
                    webRequest.SetRequestHeader("Authorization", $"Bearer {apiKey}");

                var operation = webRequest.SendWebRequest();

                while (!operation.isDone)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        webRequest.Abort();
                        return null;
                    }

                    await Task.Yield();
                }

                if (webRequest.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
                    return null;

                var modelsPayload = webRequest.downloadHandler?.text;
                if (string.IsNullOrEmpty(modelsPayload))
                    return null;

                return ParseModelIds(modelsPayload);
            }
        }

        private static IReadOnlyList<string> ParseModelIds(string json)
        {
            try
            {
                var response = UnityEngine.JsonUtility.FromJson<OpenAiModelsResponse>(json);
                if (response?.data == null || response.data.Length == 0)
                    return null;

                var ids = new List<string>(response.data.Length);
                foreach (var entry in response.data)
                {
                    if (!string.IsNullOrWhiteSpace(entry?.id))
                        ids.Add(entry.id);
                }

                return ids.Count > 0 ? ids : null;
            }
            catch
            {
                return null;
            }
        }

        [System.Serializable]
        private class OpenAiModelsResponse
        {
            public OpenAiModelEntry[] data;
        }

        [System.Serializable]
        private class OpenAiModelEntry
        {
            public string id;
        }
    }
}
