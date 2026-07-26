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

        /// <summary>
        /// Rewind and re-run a user turn (<c>prompt.submit</c> with
        /// <c>truncate_before_user_ordinal</c>): the backend drops that user turn and everything
        /// after it, then runs <paramref name="text"/> in its place. The ordinal is the zero-based
        /// position of the turn among the session's user messages (Desktop <c>visibleUserOrdinal</c>),
        /// NOT a transcript index. A live turn is interrupted first — the backend rejects a submit
        /// into a running agent as "session busy".
        /// </summary>
        Task RewindAndSubmit(string sessionId, string text, int truncateBeforeUserOrdinal);

        /// <summary>Attach an image to the given session before submitting a prompt.</summary>
        Task AttachImageBytes(string sessionId, string contentBase64);

        /// <summary>Interrupt the generation of a specific session.</summary>
        Task Interrupt(string sessionId);

        /// <summary>
        /// Nudge the live turn without interrupting (session.steer). The gateway appends
        /// the text to the next tool result so the model reads it on its next iteration.
        /// Returns true if the steer was accepted (status="queued"), false if there is no
        /// live tool window to steer into.
        /// </summary>
        Task<bool> Steer(string sessionId, string text);

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

        /// <summary>
        /// A pending sudo/secret capture expired server-side (sudo.expire / secret.expire): the
        /// gateway stopped blocking on it, so the masked input must be torn down WITHOUT sending a
        /// respond (a late one only resolves to status="expired"). Args: sessionId, requestId.
        /// </summary>
        event Action<string, string> OnSecretExpire;

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
        /// <summary>tool.complete error field (string or truthy). When set, UI shows failed status.</summary>
        public string error;
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
        public string command;
        public string type; // "approval" | "sudo" | "secret"
        /// <summary>Allowed choices from the backend (e.g. ["once","deny"] when smart_denied).
        /// Null means all defaults are allowed.</summary>
        public string[] choices;
        /// <summary>False when the backend won't honor a permanent allow (tirith warning).</summary>
        public bool allowPermanent = true;
        /// <summary>True when the backend's smart-deny heuristic flagged this command.</summary>
        public bool smartDenied;
    }

    [Serializable]
    public class SecretRequest
    {
        public string requestId;
        public string envVar;
        public string prompt;
        /// <summary>
        /// True when this is a sudo password capture (sudo.request), not a skill secret. Both need
        /// a masked text value, but sudo answers via <c>sudo.respond {request_id, password}</c> and a
        /// skill secret via <c>secret.respond {request_id, value}</c> — the responder branches on this.
        /// </summary>
        public bool isSudo;
    }
}
