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
        public string inlineDiff;
        public string details;
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
        // Precise usage for U-28 (persisted for history; 0 = unknown)
        public int tokenCount;
        public float responseTimeSeconds;
        // Model reasoning/thinking text (expandable in UI)
        public string reasoning;
        // Voice: local file path to recorded/synthesised audio (null = text-only message)
        public string audioPath;
        public float audioDurationSecs;
        [NonSerialized] public bool voiceOutputBusy;
    }

    [Serializable]
    public class ChatSession
    {
        public string sessionId;
        public string providerId;
        public string providerSessionId;
        public string providerRuntimeSessionId;
        public string selectedModel;
        public string title;
        public List<ChatMessage> messages = new List<ChatMessage>();
        public long updatedAtUnix;
        public string folder;
        // Server-provided message count (Hermes server-truth mode), where `messages` is not
        // populated for list display. 0 means "use messages.Count instead".
        public int messageCount;
    }
}
