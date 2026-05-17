using System.Collections.Generic;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Data.Secrets;
using NeonCompanion.Runtime.Data.Storage;

namespace NeonCompanion.Runtime.Data.Repositories
{
    public sealed class ProviderConfigRepository : IProviderConfigRepository
    {
        private readonly IJsonStorage _storage;
        private readonly ISecretStore _secretStore;

        public ProviderConfigRepository(IJsonStorage storage, ISecretStore secretStore)
        {
            _storage = storage;
            _secretStore = secretStore;
        }

        public Task<List<ProviderConfig>> GetAllAsync()
        {
            var collection = _storage.Load<ProviderConfigCollection>(AppPaths.ProvidersFile);
            var providers = collection.items ?? new List<ProviderConfig>();
            bool shouldRewriteProviders = false;

            foreach (var provider in providers)
            {
                if (provider == null)
                    continue;

                if (string.IsNullOrWhiteSpace(provider.id))
                {
                    provider.id = System.Guid.NewGuid().ToString("N");
                    shouldRewriteProviders = true;
                }

                if (!string.IsNullOrEmpty(provider.apiKey))
                {
                    _secretStore.SetSecret(provider.id, provider.apiKey);
                    shouldRewriteProviders = true;
                    continue;
                }

                provider.apiKey = _secretStore.GetSecret(provider.id);
            }

            if (shouldRewriteProviders)
                SaveSanitizedProviders(providers);

            return Task.FromResult(providers);
        }

        public Task SaveAllAsync(List<ProviderConfig> providers)
        {
            providers = providers ?? new List<ProviderConfig>();
            var retainedIds = new HashSet<string>();

            foreach (var provider in providers)
            {
                if (provider == null)
                    continue;

                if (string.IsNullOrWhiteSpace(provider.id))
                    provider.id = System.Guid.NewGuid().ToString("N");

                retainedIds.Add(provider.id);
                _secretStore.SetSecret(provider.id, provider.apiKey);
            }

            _secretStore.DeleteSecretsExcept(retainedIds);
            SaveSanitizedProviders(providers);

            return Task.CompletedTask;
        }

        private void SaveSanitizedProviders(List<ProviderConfig> providers)
        {
            var sanitized = new List<ProviderConfig>();
            foreach (var provider in providers ?? new List<ProviderConfig>())
            {
                if (provider == null)
                    continue;

                sanitized.Add(new ProviderConfig
                {
                    id = provider.id,
                    displayName = provider.displayName,
                    baseUrl = provider.baseUrl,
                    apiKey = string.Empty,
                    defaultModel = provider.defaultModel,
                    temperature = provider.temperature,
                    maxTokens = provider.maxTokens,
                    isEnabled = provider.isEnabled
                });
            }

            _storage.Save(AppPaths.ProvidersFile, new ProviderConfigCollection
            {
                items = sanitized
            });
        }
    }
}
