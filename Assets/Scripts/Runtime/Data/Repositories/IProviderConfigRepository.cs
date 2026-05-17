using System.Collections.Generic;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Data.Models;

namespace NeonCompanion.Runtime.Data.Repositories
{
    public interface IProviderConfigRepository
    {
        Task<List<ProviderConfig>> GetAllAsync();
        Task SaveAllAsync(List<ProviderConfig> providers);
    }
}
