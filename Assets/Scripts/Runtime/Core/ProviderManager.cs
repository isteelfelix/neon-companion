using System.Collections.Generic;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Data.Repositories;

namespace NeonCompanion.Runtime.Core
{
    public sealed class ProviderManager
    {
        private readonly IProviderConfigRepository _repository;

        public ProviderManager(IProviderConfigRepository repository)
        {
            _repository = repository;
        }

        public async Task<ProviderConfig> GetProviderByIdAsync(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId))
                return null;

            var providers = await _repository.GetAllAsync();
            return providers.Find(provider => provider != null && provider.id == providerId);
        }

        public async Task<ProviderConfig> GetActiveProviderAsync(string preferredProviderId = null)
        {
            if (!string.IsNullOrWhiteSpace(preferredProviderId))
            {
                var preferredProvider = await GetProviderByIdAsync(preferredProviderId);
                if (preferredProvider != null && preferredProvider.isEnabled)
                    return preferredProvider;
            }

            var providers = await _repository.GetAllAsync();

            for (int i = 0; i < providers.Count; i++)
            {
                var provider = providers[i];
                if (provider != null && provider.isEnabled)
                    return provider;
            }

            return null;
        }

        public async Task<ProviderConfig> GetActiveProviderForBackendAsync(BackendMode mode, string preferredProviderId = null, bool fallbackToFirst = true)
        {
            bool hermesMode = mode == BackendMode.Hermes;

            if (!string.IsNullOrWhiteSpace(preferredProviderId))
            {
                var preferredProvider = await GetProviderByIdAsync(preferredProviderId);
                if (IsEnabledProviderForBackend(preferredProvider, hermesMode))
                    return preferredProvider;
            }

            if (!fallbackToFirst)
                return null;

            var providers = await _repository.GetAllAsync();
            for (int i = 0; i < providers.Count; i++)
            {
                var provider = providers[i];
                if (IsEnabledProviderForBackend(provider, hermesMode))
                    return provider;
            }

            return null;
        }

        private static bool IsEnabledProviderForBackend(ProviderConfig provider, bool hermesMode)
        {
            if (provider == null || !provider.isEnabled)
                return false;

            bool providerIsHermes = !string.IsNullOrWhiteSpace(provider.backendType)
                && string.Equals(provider.backendType, "hermes", System.StringComparison.OrdinalIgnoreCase);
            return providerIsHermes == hermesMode;
        }

        public async Task<List<ProviderConfig>> GetAllProvidersAsync()
        {
            return await _repository.GetAllAsync();
        }

        public async Task SaveProviderAsync(ProviderConfig provider)
        {
            var providers = await _repository.GetAllAsync();
            var index = providers.FindIndex(p => p.id == provider.id);
            if (index >= 0)
                providers[index] = provider;
            else
                providers.Add(provider);
            await _repository.SaveAllAsync(providers);
        }

        public async Task DeleteProviderAsync(string providerId)
        {
            var providers = await _repository.GetAllAsync();
            providers.RemoveAll(p => p.id == providerId);
            await _repository.SaveAllAsync(providers);
        }
    }
}
