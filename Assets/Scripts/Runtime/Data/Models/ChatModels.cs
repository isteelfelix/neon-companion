using System;
using System.Collections.Generic;

namespace NeonCompanion.Runtime.Data.Models
{
    [Serializable]
    public class ChatAttachment
    {
        public string kind = "image";
        public string name;
        public string path;
        public string mediaType;
    }

    [Serializable]
    public class ChatMessage
    {
        public string role;
        public string content;
        public string model;
        public List<ChatAttachment> attachments = new List<ChatAttachment>();
        public long unixTimeSeconds;
    }

    [Serializable]
    public class ChatSession
    {
        public string sessionId;
        public string providerId;
        public string providerSessionId;
        public string selectedModel;
        public string title;
        public List<ChatMessage> messages = new List<ChatMessage>();
        public long updatedAtUnix;
    }
}
