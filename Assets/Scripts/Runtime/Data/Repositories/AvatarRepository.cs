using System.Collections.Generic;
using System;
using System.IO;
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
            var items = collection.items ?? new List<AvatarProfile>();
            for (int i = 0; i < items.Count; i++)
            {
                items[i]?.NormalizeContract();
                RestoreVrmRuntimePath(items[i]);
            }
            return items;
        }

        public void SaveAll(List<AvatarProfile> avatars)
        {
            if (avatars != null)
            {
                for (int i = 0; i < avatars.Count; i++)
                    avatars[i]?.NormalizeContract();
            }

            _storage.Save(AppPaths.AvatarsFile, new AvatarProfileCollection
            {
                items = avatars ?? new List<AvatarProfile>()
            });
        }

        private static void RestoreVrmRuntimePath(AvatarProfile profile)
        {
            if (profile == null ||
                profile.avatarType != AvatarProfileTypes.Vrm ||
                !string.IsNullOrWhiteSpace(profile.modelPath) ||
                profile.source == null ||
                string.IsNullOrWhiteSpace(profile.source.relativePath))
                return;

            try
            {
                string root = Path.GetFullPath(AppPaths.RootData);
                string candidate = Path.GetFullPath(
                    Path.Combine(root, profile.source.relativePath));
                string rootPrefix = root.EndsWith(Path.DirectorySeparatorChar.ToString(),
                    StringComparison.Ordinal)
                    ? root
                    : root + Path.DirectorySeparatorChar;
                if (candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(candidate))
                {
                    profile.modelPath = candidate;
                    profile.is3D = true;
                }
            }
            catch
            {
                // The gallery reports a missing source without trusting malformed stored paths.
            }
        }
    }
}
