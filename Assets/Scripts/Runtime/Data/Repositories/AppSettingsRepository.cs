using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Data.Storage;

namespace NeonCompanion.Runtime.Data.Repositories
{
    public sealed class AppSettingsRepository : IAppSettingsRepository
    {
        private readonly IJsonStorage _storage;

        public AppSettingsRepository(IJsonStorage storage)
        {
            _storage = storage;
        }

        public AppSettings Load()
        {
            return _storage.Load<AppSettings>(AppPaths.SettingsFile);
        }

        public void Save(AppSettings settings)
        {
            _storage.Save(AppPaths.SettingsFile, settings ?? new AppSettings());
        }
    }
}
