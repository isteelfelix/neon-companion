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

    [Serializable]
    public class AiChatRequest
    {
        public string model;
        public float temperature = 0.7f;
        public int maxTokens = 512;
        public string systemPrompt;
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