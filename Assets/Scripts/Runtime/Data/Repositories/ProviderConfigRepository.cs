using System.Collections.Generic;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Data.Storage;

namespace NeonCompanion.Runtime.Data.Repositories
{
    public sealed class ProviderConfigRepository : IProviderConfigRepository
    {
        private readonly IJsonStorage _storage;

        public ProviderConfigRepository(IJsonStorage storage)
        {
            _storage = storage;
        }

        public Task<List<ProviderConfig>> GetAllAsync()
        {
            var collection = _storage.Load<ProviderConfigCollection>(AppPaths.ProvidersFile);
            return Task.FromResult(collection.items ?? new List<ProviderConfig>());
        }

        public Task SaveAllAsync(List<ProviderConfig> providers)
        {
            _storage.Save(AppPaths.ProvidersFile, new ProviderConfigCollection
            {
                items = providers ?? new List<ProviderConfig>()
            });

            return Task.CompletedTask;
        }
    }
}
