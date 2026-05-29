using System;
using System.Collections.Generic;
using NeonCompanion.Runtime.Api.Models;

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
    public class ChatMessageSegment
    {
        public const string TextKind = "text";
        public const string ToolKind = "tool";

        public string kind;
        public string key;
        public string text;
        public string tool;
        public string label;
        public string emoji;
        public string status;
    }

    [Serializable]
    public class ChatMessage
    {
        public string role;
        public string content;
        public string model;
        public List<ChatAttachment> attachments = new List<ChatAttachment>();
        public List<ChatMessageSegment> segments = new List<ChatMessageSegment>();
        public long unixTimeSeconds;
        public string tool_call_id;
        public List<ToolCall> tool_calls;
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
