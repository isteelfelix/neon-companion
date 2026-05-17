using System.Collections.Generic;
using NeonCompanion.Runtime.Data.Models;

namespace NeonCompanion.Runtime.Avatar
{
    public interface IAvatarService
    {
        AvatarProfile GetActiveAvatar(string avatarId, List<AvatarProfile> availableAvatars);
    }
}
