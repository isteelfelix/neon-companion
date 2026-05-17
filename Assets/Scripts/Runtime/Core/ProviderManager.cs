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

        public async Task<ProviderConfig> GetActiveProviderAsync()
        {
            var providers = await _repository.GetAllAsync();

            if (providers.Count > 0)
            {
                return providers[0];
            }

            var defaultProvider = new ProviderConfig
            {
                id = "default",
                name = "OpenAI",
                baseUrl = "https://api.openai.com/v1",
                model = "gpt-4o-mini",
                apiKey = ""
            };

            await _repository.SaveAsync(defaultProvider);
            return defaultProvider;
        }

        public async Task<List<ProviderConfig>> GetAllProvidersAsync()
        {
            return await _repository.GetAllAsync();
        }

        public async Task SaveProviderAsync(ProviderConfig provider)
        {
            await _repository.SaveAsync(provider);
        }

        public async Task DeleteProviderAsync(string providerId)
        {
            await _repository.DeleteAsync(providerId);
        }
    }
}