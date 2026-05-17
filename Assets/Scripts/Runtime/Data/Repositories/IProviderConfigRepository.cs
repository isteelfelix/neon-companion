using System.Collections.Generic;
using NeonCompanion.Runtime.Data.Models;

namespace NeonCompanion.Runtime.Data.Repositories
{
    public interface IProviderConfigRepository
    {
        List<ProviderConfig> GetAll();
        void SaveAll(List<ProviderConfig> providers);
    }
}
