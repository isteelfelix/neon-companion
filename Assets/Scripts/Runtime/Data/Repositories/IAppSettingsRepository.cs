using NeonCompanion.Runtime.Data.Models;

namespace NeonCompanion.Runtime.Data.Repositories
{
    public interface IAppSettingsRepository
    {
        AppSettings Load();
        void Save(AppSettings settings);
    }
}
