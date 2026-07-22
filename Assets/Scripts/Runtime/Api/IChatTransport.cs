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

        /// <summary>Send a user message to a specific session. Response arrives via events.</summary>
        Task SendMessage(string sessionId, string text);

        /// <summary>Attach an image to the given session before submitting a prompt.</summary>
        Task AttachImageBytes(string sessionId, string contentBase64);

        /// <summary>Interrupt the generation of a specific session.</summary>
        Task Interrupt(string sessionId);

        // --- Events ---
        // All streaming events carry the originating session_id so a single transport
        // can multiplex several concurrent sessions (Hermes parallel sessions).

        /// <summary>Generation started (message.start). Arg: sessionId.</summary>
        event Action<string> OnStreamStarted;

        /// <summary>Streaming text delta (message.delta). Args: sessionId, text.</summary>
        event Action<string, string> OnDelta;

        /// <summary>Generation complete (message.complete). Args: sessionId, finalText.</summary>
        event Action<string, string> OnComplete;

        /// <summary>Reasoning/thinking delta (reasoning.delta). Args: sessionId, text.</summary>
        event Action<string, string> OnReasoningDelta;

        /// <summary>Tool call update (tool.start / tool.progress / tool.complete). Args: sessionId, update.</summary>
        event Action<string, ToolCallUpdate> OnToolUpdate;

        /// <summary>Clarify request from agent (clarify.request). Args: sessionId, request.</summary>
        event Action<string, ClarifyRequest> OnClarifyRequest;

        /// <summary>Approval request from agent (approval.request). Args: sessionId, request.</summary>
        event Action<string, ApprovalRequest> OnApprovalRequest;

        /// <summary>
        /// Secret / credential capture request (secret.request). Distinct from approval: the
        /// agent is blocked on <c>secret.respond {request_id, value}</c> (a text value, not an
        /// approve/deny choice), so it must not be routed through the approval responder. Answer
        /// via <see cref="Hermes.HermesSessionManager.RespondToSecret"/>. Args: sessionId, request.
        /// </summary>
        event Action<string, SecretRequest> OnSecretRequest;

        /// <summary>Live auto-title push for a session (session.title). Args: sessionId, title.</summary>
        event Action<string, string> OnSessionTitle;

        /// <summary>Error from backend. Args: sessionId (may be null for connection-level), message.</summary>
        event Action<string, string> OnError;

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
        public string details;
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
    }

    [Serializable]
    public class SecretRequest
    {
        public string requestId;
        public string envVar;
        public string prompt;
    }
}
