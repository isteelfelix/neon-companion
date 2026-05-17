using System.Collections.Generic;
using System.Linq;
using NeonCompanion.Runtime.Data.Models;

namespace NeonCompanion.Runtime.Avatar
{
    public sealed class AvatarService : IAvatarService
    {
        public AvatarProfile GetActiveAvatar(string avatarId, List<AvatarProfile> availableAvatars)
        {
            if (availableAvatars == null || availableAvatars.Count == 0)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(avatarId))
            {
                return availableAvatars[0];
            }

            return availableAvatars.FirstOrDefault(a => a.id == avatarId) ?? availableAvatars[0];
        }
    }
}
