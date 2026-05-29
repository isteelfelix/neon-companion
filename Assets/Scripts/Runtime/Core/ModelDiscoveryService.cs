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
                var result = await TryFetchModelsAsync(
                    endpoint, provider.apiKey, adapter, cancellationToken);
                if (result != null && result.Count > 0)
                {
                    _cache[cacheKey] = result;
                    return result;
                }
            }

            return null;
        }

        private async Task<IReadOnlyList<string>> TryFetchModelsAsync(
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
                return parsed;
            }
        }
    }
}
