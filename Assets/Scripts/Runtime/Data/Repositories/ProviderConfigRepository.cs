using System.Collections.Generic;
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

        public List<ProviderConfig> GetAll()
        {
            var collection = _storage.Load<ProviderConfigCollection>(AppPaths.ProvidersFile);
            return collection.items ?? new List<ProviderConfig>();
        }

        public void SaveAll(List<ProviderConfig> providers)
        {
            _storage.Save(AppPaths.ProvidersFile, new ProviderConfigCollection
            {
                items = providers ?? new List<ProviderConfig>()
            });
        }
    }
}
