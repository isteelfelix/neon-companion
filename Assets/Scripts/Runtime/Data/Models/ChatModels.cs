using System;
using System.Collections.Generic;

namespace NeonCompanion.Runtime.Data.Models
{
    [Serializable]
    public class ChatMessage
    {
        public string role;
        public string content;
        public long unixTimeSeconds;
    }

    [Serializable]
    public class ChatSession
    {
        public string sessionId;
        public string providerId;
        public string title;
        public List<ChatMessage> messages = new List<ChatMessage>();
        public long updatedAtUnix;
    }
}
