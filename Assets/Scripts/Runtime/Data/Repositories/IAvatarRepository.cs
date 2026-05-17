using System.Collections.Generic;
using NeonCompanion.Runtime.Data.Models;

namespace NeonCompanion.Runtime.Data.Repositories
{
    public interface IAvatarRepository
    {
        List<AvatarProfile> GetAll();
        void SaveAll(List<AvatarProfile> avatars);
    }
}
