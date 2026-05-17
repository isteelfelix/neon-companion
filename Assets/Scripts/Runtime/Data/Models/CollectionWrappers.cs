using System;
using System.Collections.Generic;

namespace NeonCompanion.Runtime.Data.Models
{
    [Serializable]
    public class ProviderConfigCollection
    {
        public List<ProviderConfig> items = new List<ProviderConfig>();
    }

    [Serializable]
    public class AvatarProfileCollection
    {
        public List<AvatarProfile> items = new List<AvatarProfile>();
    }

    [Serializable]
    public class ChatSessionCollection
    {
        public List<ChatSession> items = new List<ChatSession>();
    }
}
