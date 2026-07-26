using System;
using System.Collections.Generic;

namespace NeonCompanion.Runtime.Api.Models
{
    public sealed class ConnectionTestResult
    {
        public bool Success { get; }
        public string Message { get; }
        public long LatencyMs { get; }
        public IReadOnlyList<string> DiscoveredModels { get; }

        public ConnectionTestResult(bool success, string message, long latencyMs = 0, IReadOnlyList<string> discoveredModels = null)
        {
            Success = success;
            Message = message;
            LatencyMs = latencyMs;
            DiscoveredModels = discoveredModels;
        }
    }

    public sealed class ModelSwitchResult
    {
        public bool Success { get; }
        public bool IsHermes { get; }
        public string RequestedModel { get; }
        public string AppliedModel { get; }
        public string ProviderSessionId { get; }
        public string Message { get; }

        public ModelSwitchResult(
            bool success,
            string requestedModel,
            string appliedModel,
            string providerSessionId,
            string message = null,
            bool isHermes = false)
        {
            Success = success;
            RequestedModel = requestedModel;
            AppliedModel = appliedModel;
            ProviderSessionId = providerSessionId;
            Message = message;
            IsHermes = isHermes;
        }
    }

    [Serializable]
    public class AiChatRequest
    {
        public string model;
        public string providerSessionId;
        public float temperature = 0.7f;
        public int maxTokens = 512;
        public string systemPrompt;
        public List<AiChatMessage> messages = new List<AiChatMessage>();
        public List<ToolDefinition> tools;
    }

    [Serializable]
    public class ToolCall
    {
        public string id;
        public string type = "function";
        public ToolCallFunction function;
    }

    [Serializable]
    public class ToolCallFunction
    {
        public string name;
        public string arguments;
    }

    [Serializable]
    public class AiChatAttachment
    {
        public string kind = "image";
        public string name;
        public string path;
        public string mediaType;
    }

    [Serializable]
    public class AiChatMessage
    {
        public string role;
        public string content;
        public List<AiChatAttachment> attachments = new List<AiChatAttachment>();
        public string tool_call_id;
        public List<ToolCall> tool_calls;
        public List<ResponsesOutputItem> responseOutput;
    }

    [Serializable]
    public class AiChatResponse
    {
        public string id;
        public string model;
        public string providerSessionId;
        // Responses API lifecycle status (for example: completed, failed, incomplete).
        public string status;
        public string content;
        public DateTime receivedAtUtc = DateTime.UtcNow;
        public List<ToolCall> tool_calls;
        public List<AiChatAttachment> attachments;
        // Preserve the complete Responses output so a later turn can resend typed items
        // instead of reconstructing context from display text alone.
        public List<ResponsesOutputItem> responseOutput;
        public ResponsesUsage usage;
        public ResponsesApiError error;
        public ResponsesIncompleteDetails incompleteDetails;
    }
}
