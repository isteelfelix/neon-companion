using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Data.Repositories;
using NeonCompanion.Runtime.Api.Adapters;

namespace NeonCompanion.Runtime.Core
{
    public sealed class ModelDiscoveryService
    {
        private readonly IProviderConfigRepository _repository;
        private readonly Dictionary<string, IReadOnlyList<string>> _cache = new Dictionary<string, IReadOnlyList<string>>();
        // Raw JSON from successful discovery — used for context window lookup (U-36)
        private readonly Dictionary<string, string> _jsonCache = new Dictionary<string, string>();

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

            var adapter = ProviderAdapterFactory.Create(
                provider != null ? provider.backendType : null);
            var endpoints = adapter.BuildDiscoveryEndpoints(provider.baseUrl);

            foreach (var endpoint in endpoints)
            {
                var fetched = await TryFetchModelsAsync(
                    endpoint, provider.apiKey, adapter, cancellationToken);
                if (fetched != null && fetched.Models != null && fetched.Models.Count > 0)
                {
                    _cache[cacheKey] = fetched.Models;
                    if (!string.IsNullOrEmpty(fetched.Json))
                        _jsonCache[cacheKey] = fetched.Json;
                    return fetched.Models;
                }
            }

            return null;
        }

        /// <summary>
        /// Returns the context window size for a specific model from cached discovery data.
        /// Parses context_length / context_window fields from the raw API response.
        /// Returns 0 if not available (caller should fall back to heuristics).
        /// </summary>
        public int GetContextWindowForModel(ProviderConfig provider, string modelId)
        {
            if (provider == null || string.IsNullOrEmpty(modelId))
                return 0;

            var cacheKey = $"{provider.baseUrl}|{provider.apiKey}";
            if (!_jsonCache.TryGetValue(cacheKey, out string json) || string.IsNullOrEmpty(json))
                return 0;

            return ExtractContextWindowFromJson(json, modelId);
        }

        private static int ExtractContextWindowFromJson(string json, string modelId)
        {
            // Find the model ID occurrence in the JSON document.
            string quotedId = "\"" + modelId + "\"";
            int pos = json.IndexOf(quotedId, System.StringComparison.OrdinalIgnoreCase);
            if (pos < 0)
                return 0;

            // Search a window around the model ID for context size fields.
            int searchStart = System.Math.Max(0, pos - 200);
            int searchEnd = System.Math.Min(json.Length, pos + quotedId.Length + 400);
            string region = json.Substring(searchStart, searchEnd - searchStart);

            string[] fields = { "context_length", "context_window", "n_ctx", "max_context_length", "max_tokens" };
            foreach (string field in fields)
            {
                string key = "\"" + field + "\"";
                int fieldIdx = region.IndexOf(key, System.StringComparison.OrdinalIgnoreCase);
                if (fieldIdx < 0)
                    continue;

                int colonIdx = region.IndexOf(':', fieldIdx + key.Length);
                if (colonIdx < 0)
                    continue;

                int valueIdx = colonIdx + 1;
                while (valueIdx < region.Length && char.IsWhiteSpace(region[valueIdx]))
                    valueIdx++;

                if (valueIdx >= region.Length || !char.IsDigit(region[valueIdx]))
                    continue;

                int end = valueIdx;
                while (end < region.Length && char.IsDigit(region[end]))
                    end++;

                if (int.TryParse(region.Substring(valueIdx, end - valueIdx), out int value) && value > 512)
                    return value;
            }

            return 0;
        }

        private sealed class FetchResult
        {
            public IReadOnlyList<string> Models;
            public string Json;
        }

        private async Task<FetchResult> TryFetchModelsAsync(
            string endpoint, string apiKey, IProviderAdapter adapter, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(endpoint))
                return null;

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

                var payload = webRequest.downloadHandler?.text;
                if (string.IsNullOrEmpty(payload))
                    return null;

                var parsed = adapter.ParseDiscoveryResponse(payload);
                return new FetchResult { Models = parsed, Json = payload };
            }
        }
    }
}
