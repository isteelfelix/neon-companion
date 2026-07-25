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

        /// <summary>
        /// Write the provider list with the API key stripped (it lives in the secret store).
        /// The key is the ONLY field this may drop — copy every other field. Fields that were
        /// silently missing here were wiped on every save: authMode/authProvider/authUsername,
        /// which cost the Hermes gateway its OAuth flag (so a restart fell back to token mode
        /// with an empty token and could not restore its cookie), and the whole voice block.
        /// </summary>
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
                    contextWindow = provider.contextWindow,
                    backendType = provider.backendType,
                    isEnabled = provider.isEnabled,
                    authMode = provider.authMode,
                    authProvider = provider.authProvider,
                    authUsername = provider.authUsername,
                    sttProvider = provider.sttProvider,
                    ttsProvider = provider.ttsProvider,
                    ttsVoice = provider.ttsVoice,
                    ttsModel = provider.ttsModel,
                    ttsSpeed = provider.ttsSpeed,
                    sttLanguage = provider.sttLanguage
                });
            }

            _storage.Save(AppPaths.ProvidersFile, new ProviderConfigCollection
            {
                items = sanitized
            });
        }
    }
}
