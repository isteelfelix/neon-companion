using System;
using System.Collections.Generic;

namespace NeonCompanion.Runtime.Api.Models
{
    [Serializable]
    public class AiChatRequest
    {
        public string model;
        public float temperature = 0.7f;
        public int maxTokens = 512;
        public List<AiChatMessage> messages = new List<AiChatMessage>();
    }

    [Serializable]
    public class AiChatMessage
    {
        public string role;
        public string content;
    }

    [Serializable]
    public class AiChatResponse
    {
        public string id;
        public string model;
        public string content;
        public DateTime receivedAtUtc = DateTime.UtcNow;
    }
}
