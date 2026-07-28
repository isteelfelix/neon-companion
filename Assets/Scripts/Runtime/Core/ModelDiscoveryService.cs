using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Data.Repositories;
using NeonCompanion.Runtime.Api.Adapters;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;

namespace NeonCompanion.Runtime.Core
{
    public sealed class ModelDiscoveryService
    {
        private readonly IProviderConfigRepository _repository;
        private readonly Dictionary<string, IReadOnlyList<string>> _cache = new Dictionary<string, IReadOnlyList<string>>();
        // Raw JSON from successful discovery — used for context window lookup (U-36)
        private readonly Dictionary<string, string> _jsonCache = new Dictionary<string, string>();
        private readonly Dictionary<string, DetectedContextWindow> _contextCache =
            new Dictionary<string, DetectedContextWindow>();

        public ModelDiscoveryService(IProviderConfigRepository repository)
        {
            _repository = repository;
        }

        public async Task<IReadOnlyList<string>> DiscoverModelsAsync(ProviderConfig provider, CancellationToken cancellationToken = default)
        {
            if (provider == null || string.IsNullOrWhiteSpace(provider.baseUrl))
                return null;

            var cacheKey = $"{provider.backendType}|{provider.baseUrl}|{provider.apiKey}";
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

        public bool HasResolvedContextWindow(ProviderConfig provider, string modelId)
        {
            if (provider == null || string.IsNullOrEmpty(modelId))
                return false;

            return _contextCache.ContainsKey(BuildContextCacheKey(provider, modelId));
        }

        public ContextWindowResolution GetContextWindowResolution(ProviderConfig provider, string modelId)
        {
            if (provider == null)
                return BuildResolution(0, ContextWindowSource.Unknown, 0);

            DetectedContextWindow detected = null;
            if (!string.IsNullOrWhiteSpace(modelId))
                _contextCache.TryGetValue(BuildContextCacheKey(provider, modelId), out detected);

            if (detected == null)
            {
                int registryLimit = KnownModelContextRegistry.GetContextWindow(provider.baseUrl, modelId);
                detected = new DetectedContextWindow
                {
                    Limit = registryLimit,
                    Source = registryLimit > 0 ? ContextWindowSource.Registry : ContextWindowSource.Unknown
                };
            }

            return BuildResolution(detected.Limit, detected.Source, provider.contextWindow);
        }

        public async Task<ContextWindowResolution> ResolveContextWindowAsync(
            ProviderConfig provider,
            string modelId,
            CancellationToken cancellationToken = default)
        {
            if (provider == null || string.IsNullOrWhiteSpace(modelId))
                return BuildResolution(0, ContextWindowSource.Unknown, provider != null ? provider.contextWindow : 0);

            string contextCacheKey = BuildContextCacheKey(provider, modelId);
            DetectedContextWindow detected = null;

            if (IsLocalEndpoint(provider.baseUrl))
                detected = await TryResolveLmStudioContextAsync(provider, modelId, cancellationToken);

            if (detected == null || detected.Limit <= 0)
            {
                await DiscoverModelsAsync(provider, cancellationToken);
                if (!cancellationToken.IsCancellationRequested)
                {
                    string providerKey = BuildProviderCacheKey(provider);
                    if (_jsonCache.TryGetValue(providerKey, out string json))
                    {
                        int discoveredLimit = ExtractContextWindowFromModelsJson(json, modelId);
                        if (discoveredLimit > 0)
                        {
                            detected = new DetectedContextWindow
                            {
                                Limit = discoveredLimit,
                                Source = ContextWindowSource.Discovery
                            };
                        }
                    }
                }
            }

            if (detected == null || detected.Limit <= 0)
            {
                int registryLimit = KnownModelContextRegistry.GetContextWindow(provider.baseUrl, modelId);
                detected = new DetectedContextWindow
                {
                    Limit = registryLimit,
                    Source = registryLimit > 0 ? ContextWindowSource.Registry : ContextWindowSource.Unknown
                };
            }

            _contextCache[contextCacheKey] = detected;
            return BuildResolution(detected.Limit, detected.Source, provider.contextWindow);
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
                webRequest.timeout = 8;
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

        private async Task<DetectedContextWindow> TryResolveLmStudioContextAsync(
            ProviderConfig provider,
            string modelId,
            CancellationToken cancellationToken)
        {
            if (!Uri.TryCreate(provider.baseUrl, UriKind.Absolute, out Uri baseUri))
                return null;

            string endpoint = baseUri.GetLeftPart(UriPartial.Authority).TrimEnd('/') + "/api/v1/models";
            string json = await TryFetchJsonAsync(endpoint, provider.apiKey, cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                JObject root = JObject.Parse(json);
                JArray models = root["models"] as JArray;
                if (models == null)
                    return null;

                for (int i = 0; i < models.Count; i++)
                {
                    JObject model = models[i] as JObject;
                    if (model == null)
                        continue;

                    JArray instances = model["loaded_instances"] as JArray;
                    if (instances != null)
                    {
                        for (int j = 0; j < instances.Count; j++)
                        {
                            JObject instance = instances[j] as JObject;
                            string instanceId = ReadString(instance != null ? instance["id"] : null);
                            if (!string.Equals(instanceId, modelId, StringComparison.OrdinalIgnoreCase))
                                continue;

                            int runtimeLimit = ReadPositiveInt(instance["config"]?["context_length"]);
                            if (runtimeLimit > 0)
                            {
                                return new DetectedContextWindow
                                {
                                    Limit = runtimeLimit,
                                    Source = ContextWindowSource.Runtime
                                };
                            }
                        }
                    }

                    string modelKey = ReadString(model["key"]);
                    if (!string.Equals(modelKey, modelId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (instances != null && instances.Count == 1)
                    {
                        int runtimeLimit = ReadPositiveInt(instances[0]?["config"]?["context_length"]);
                        if (runtimeLimit > 0)
                        {
                            return new DetectedContextWindow
                            {
                                Limit = runtimeLimit,
                                Source = ContextWindowSource.Runtime
                            };
                        }
                    }

                    int maximumLimit = ReadPositiveInt(model["max_context_length"]);
                    if (maximumLimit > 0)
                    {
                        return new DetectedContextWindow
                        {
                            Limit = maximumLimit,
                            Source = ContextWindowSource.Discovery
                        };
                    }
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static int ExtractContextWindowFromModelsJson(string json, string modelId)
        {
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(modelId))
                return 0;

            try
            {
                JObject root = JObject.Parse(json);
                JArray models = root["data"] as JArray;
                if (models == null)
                    return 0;

                for (int i = 0; i < models.Count; i++)
                {
                    JObject model = models[i] as JObject;
                    if (model == null ||
                        !string.Equals(ReadString(model["id"]), modelId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    int value = ReadPositiveInt(model["metadata"]?["limits"]?["max_context_length"]);
                    if (value <= 0) value = ReadPositiveInt(model["context_length"]);
                    if (value <= 0) value = ReadPositiveInt(model["context_window"]);
                    if (value <= 0) value = ReadPositiveInt(model["n_ctx"]);
                    if (value <= 0) value = ReadPositiveInt(model["max_context_length"]);
                    if (value <= 0) value = ReadPositiveInt(model["max_model_len"]);
                    return value;
                }
            }
            catch
            {
                return 0;
            }

            return 0;
        }

        private static ContextWindowResolution BuildResolution(
            int knownLimit,
            ContextWindowSource knownSource,
            int manualLimit)
        {
            int normalizedKnown = knownLimit > 0 ? knownLimit : 0;
            int normalizedManual = manualLimit > 0 ? manualLimit : 0;
            int effective = normalizedKnown;
            ContextWindowSource source = normalizedKnown > 0 ? knownSource : ContextWindowSource.Unknown;

            if (normalizedManual > 0)
            {
                effective = normalizedKnown > 0
                    ? Math.Min(normalizedManual, normalizedKnown)
                    : normalizedManual;
                source = ContextWindowSource.Manual;
            }

            return new ContextWindowResolution
            {
                EffectiveContextWindow = effective,
                KnownLimit = normalizedKnown,
                ManualLimit = normalizedManual,
                Source = source,
                KnownSource = normalizedKnown > 0 ? knownSource : ContextWindowSource.Unknown
            };
        }

        private static int ReadPositiveInt(JToken token)
        {
            if (token == null)
                return 0;

            if (token.Type == JTokenType.Integer)
            {
                long value = token.Value<long>();
                return value > 0 && value <= int.MaxValue ? (int)value : 0;
            }

            if (token.Type == JTokenType.String &&
                int.TryParse(token.Value<string>(), out int parsed) &&
                parsed > 0)
            {
                return parsed;
            }

            return 0;
        }

        private static string ReadString(JToken token)
        {
            return token != null && token.Type == JTokenType.String
                ? token.Value<string>()
                : null;
        }

        private async Task<string> TryFetchJsonAsync(
            string endpoint,
            string apiKey,
            CancellationToken cancellationToken)
        {
            using (var webRequest = UnityWebRequest.Get(endpoint))
            {
                webRequest.timeout = 8;
                if (!string.IsNullOrWhiteSpace(apiKey))
                    webRequest.SetRequestHeader("Authorization", "Bearer " + apiKey);

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

                if (webRequest.result != UnityWebRequest.Result.Success)
                    return null;

                return webRequest.downloadHandler != null ? webRequest.downloadHandler.text : null;
            }
        }

        private static bool IsLocalEndpoint(string baseUrl)
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri uri))
                return false;

            if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(uri.Host, "0.0.0.0", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return IPAddress.TryParse(uri.Host, out IPAddress address) && IPAddress.IsLoopback(address);
        }

        private static string BuildProviderCacheKey(ProviderConfig provider)
        {
            return $"{provider.backendType}|{provider.baseUrl}|{provider.apiKey}";
        }

        private static string BuildContextCacheKey(ProviderConfig provider, string modelId)
        {
            return BuildProviderCacheKey(provider) + "|" + (modelId ?? string.Empty).Trim();
        }

        private sealed class DetectedContextWindow
        {
            public int Limit;
            public ContextWindowSource Source;
        }
    }
}
