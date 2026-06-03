// IChatTransport.cs - Abstraction for chat transport layer
// Hermes: WebSocket JSON-RPC 2.0
// OpenAI: HTTP REST + SSE

using System;
using System.Threading.Tasks;

namespace NeonCompanion.Runtime.Api
{
    /// <summary>
    /// Transport-agnostic interface for sending/receiving chat messages.
    /// ChatService works through this interface, never knowing about WebSocket or HTTP.
    /// </summary>
    public interface IChatTransport : IDisposable
    {
        /// <summary>Whether the transport is currently connected and ready.</summary>
        bool IsConnected { get; }

        /// <summary>Connect to the backend.</summary>
        Task Connect(string url, string token = null);

        /// <summary>Disconnect from the backend.</summary>
        Task Disconnect();

        /// <summary>Send a user message. Response arrives via events.</summary>
        Task SendMessage(string text);

        /// <summary>Interrupt the current generation.</summary>
        Task Interrupt();

        // --- Events ---

        /// <summary>Generation started (message.start).</summary>
        event Action OnStreamStarted;

        /// <summary>Streaming text delta (message.delta).</summary>
        event Action<string> OnDelta;

        /// <summary>Generation complete (message.complete).</summary>
        event Action<string> OnComplete;

        /// <summary>Reasoning/thinking delta (reasoning.delta).</summary>
        event Action<string> OnReasoningDelta;

        /// <summary>Tool call update (tool.start / tool.progress / tool.complete).</summary>
        event Action<ToolCallUpdate> OnToolUpdate;

        /// <summary>Clarify request from agent (clarify.request).</summary>
        event Action<ClarifyRequest> OnClarifyRequest;

        /// <summary>Approval request from agent (approval.request).</summary>
        event Action<ApprovalRequest> OnApprovalRequest;

        /// <summary>Error from backend.</summary>
        event Action<string> OnError;

        /// <summary>Connection state changed.</summary>
        event Action<TransportState> OnStateChanged;
    }

    public enum TransportState
    {
        Disconnected,
        Connecting,
        Connected,
        Reconnecting,
        Error
    }

    public enum ToolCallStatus
    {
        Running,
        Complete
    }

    [Serializable]
    public class ToolCallUpdate
    {
        public string name;
        public string toolId;
        public ToolCallStatus status;
        public string preview;
        public string inlineDiff;
        public string emoji;
    }

    [Serializable]
    public class ClarifyRequest
    {
        public string requestId;
        public string question;
        public string[] choices;
    }

    [Serializable]
    public class ApprovalRequest
    {
        public string requestId;
        public string description;
        public string type; // "approval" | "sudo" | "secret"
        public string runId;
    }
}
