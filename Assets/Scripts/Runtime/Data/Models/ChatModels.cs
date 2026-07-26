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
        public string toolId;
        public string label;
        public string emoji;
        public string status;
        public string inlineDiff;
        public string details;
    }

    /// <summary>
    /// Serializable Responses API item kept with a transcript message. The API layer maps its
    /// wire objects to this small, stable DTO so JSON history does not depend on polymorphic SDK
    /// types or a provider-specific response schema.
    /// </summary>
    [Serializable]
    public class ChatResponseContentPart
    {
        public string type;
        public string text;
        public string refusal;
    }

    [Serializable]
    public class ChatResponseItem
    {
        public string id;
        public string type;
        public string role;
        public string status;
        public string content;
        public string callId;
        public string name;
        public string arguments;
        public string encryptedContent;
        public string output;
        public string summary;
        public List<ChatResponseContentPart> contentParts = new List<ChatResponseContentPart>();
        public List<ChatResponseContentPart> summaryParts = new List<ChatResponseContentPart>();
    }

    /// <summary>Per-response token accounting returned by Responses; zero means not supplied.</summary>
    [Serializable]
    public class ChatResponseUsage
    {
        public int inputTokens;
        public int outputTokens;
        public int reasoningTokens;
        public int cachedInputTokens;
        public int totalTokens;
    }

    /// <summary>
    /// Live tool-card update (tool.start / progress / generating / complete).
    /// Desktop keys rows by tool_id and merges progress/result in place; Companion mirrors that.
    /// </summary>
    public sealed class ToolProgressInfo
    {
        public string tool;
        public string toolId;
        public string label;
        public string emoji;
        public string status;
        public string inlineDiff;
        public string details;

        public static ToolProgressInfo Create(string tool, string label, string emoji, string status)
        {
            ToolProgressInfo info = new ToolProgressInfo();
            info.tool = tool;
            info.label = label;
            info.emoji = emoji;
            info.status = status;
            return info;
        }
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
        // Responses API identity and canonical output, retained for continuation/reload.
        public string responseId;
        public string previousResponseId;
        public List<ChatResponseItem> responseItems = new List<ChatResponseItem>();
        public ChatResponseUsage responseUsage;
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
        // Last completed Responses id. Kept separately to continue a loaded session without
        // recovering it from presentation-only message text.
        public string lastResponseId;
        public ChatResponseUsage usage;
        public string title;
        public List<ChatMessage> messages = new List<ChatMessage>();
        public long updatedAtUnix;
        public string folder;
        // Server-provided message count (Hermes server-truth mode), where `messages` is not
        // populated for list display. 0 means "use messages.Count instead".
        public int messageCount;
    }
}
