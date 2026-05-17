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

        public async Task<List<ProviderConfig>> GetAllAsync()
        {
            // Симуляция асинхронности для совместимости с Task API
            return await Task.Run(() => {
                var collection = _storage.Load<ProviderConfigCollection>(AppPaths.ProvidersFile);
                return collection.items ?? new List<ProviderConfig>();
            });
        }

        public async Task SaveAllAsync(List<ProviderConfig> providers)
        {
            await Task.Run(() => {
                _storage.Save(AppPaths.ProvidersFile, new ProviderConfigCollection
                {
                    items = providers ?? new List<ProviderConfig>()
                });
            });
        }
    }
}
