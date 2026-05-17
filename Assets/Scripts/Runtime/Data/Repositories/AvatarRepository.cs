using System.Collections.Generic;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Data.Storage;

namespace NeonCompanion.Runtime.Data.Repositories
{
    public sealed class AvatarRepository : IAvatarRepository
    {
        private readonly IJsonStorage _storage;

        public AvatarRepository(IJsonStorage storage)
        {
            _storage = storage;
        }

        public List<AvatarProfile> GetAll()
        {
            var collection = _storage.Load<AvatarProfileCollection>(AppPaths.AvatarsFile);
            return collection.items ?? new List<AvatarProfile>();
        }

        public void SaveAll(List<AvatarProfile> avatars)
        {
            _storage.Save(AppPaths.AvatarsFile, new AvatarProfileCollection
            {
                items = avatars ?? new List<AvatarProfile>()
            });
        }
    }
}
