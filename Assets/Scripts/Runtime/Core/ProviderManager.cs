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
                if (preferredProvider != null)
                    return preferredProvider;
            }

            var providers = await _repository.GetAllAsync();

            if (providers.Count > 0)
            {
                return providers[0];
            }

            var defaultProvider = new ProviderConfig
            {
                id = "default",
                displayName = "OpenAI",
                baseUrl = "https://api.openai.com/v1",
                defaultModel = "gpt-4o-mini",
                apiKey = ""
            };

            await _repository.SaveAllAsync(new List<ProviderConfig> { defaultProvider });
            return defaultProvider;
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