// HermesSessionManager.cs - Session lifecycle + IChatTransport implementation
// Wraps HermesGateway with session management, streaming, tool calls, clarify.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace NeonCompanion.Runtime.Api.Hermes
{
    // === Runtime Types ===

    [Serializable]
    public class SessionCreateResponse
    {
        /// <summary>Runtime id — the one prompt.submit/session.* RPCs and stream events use.</summary>
        public string session_id;
        /// <summary>Persisted DB key. session.create is the only response that names it this way.</summary>
        public string stored_session_id;
        /// <summary>
        /// Live runtime state (model/provider/cwd/…) — NOT the stored session row. The gateway
        /// returns the same shape here as on the session.info event.
        /// </summary>
        public SessionRuntimeInfo info;
        public SessionMessage[] messages;
        public int message_count;
    }

    [Serializable]
    public class SessionResumeResponse
    {
        /// <summary>Runtime id bound by this resume/activate.</summary>
        public string session_id;
        /// <summary>
        /// Persisted key the gateway actually bound. session.resume/session.activate report it as
        /// session_key (never stored_session_id); after auto-compression it is the continuation
        /// TIP, which differs from the id that was asked for.
        /// </summary>
        public string session_key;
        /// <summary>Id the gateway resolved the request to (tip of the compression chain).</summary>
        public string resumed;
        /// <summary>Kept for gateways that answer resume with the create-style field name.</summary>
        public string stored_session_id;
        public SessionMessage[] messages;
        public int message_count;
        public SessionRuntimeInfo info;
        /// <summary>True when a turn is still generating on the backend right now.</summary>
        public bool running;
        /// <summary>idle | starting | working | waiting.</summary>
        public string status;
        public long started_at;
        /// <summary>The turn in flight when this payload was built (null when idle).</summary>
        public SessionInflight inflight;
        /// <summary>A prompt accepted but not yet started (arrives while the session is busy).</summary>
        public SessionQueued queued;
    }

    /// <summary>Backend snapshot of the in-flight turn (gateway _inflight_snapshot).</summary>
    [Serializable]
    public class SessionInflight
    {
        public string user;
        public string assistant;
        public bool streaming;
    }

    /// <summary>Backend snapshot of an accepted-but-not-started prompt (gateway _queued_prompt_snapshot).</summary>
    [Serializable]
    public class SessionQueued
    {
        public string user;
    }

    /// <summary>
    /// One row of session.active_list: a session this gateway process still holds an agent for.
    /// Live status only — it is NOT profile-scoped and must never stand in for the REST history.
    /// </summary>
    [Serializable]
    public class LiveSessionStatus
    {
        /// <summary>Runtime id.</summary>
        public string id;
        /// <summary>Persisted key.</summary>
        public string session_key;
        /// <summary>idle | starting | working | waiting.</summary>
        public string status;
        public bool current;
        public string model;
        public string title;
        public string preview;
        public int message_count;
        public double last_active;
        public double started_at;
    }

    [Serializable]
    public class LiveSessionListResponse
    {
        public LiveSessionStatus[] sessions;
    }

    [Serializable]
    public class SessionMessage
    {
        public string role;
        public string text;
        public string name;
        public long? timestamp;
    }

    [Serializable]
    public class SessionRuntimeInfo
    {
        public string model;
        public string provider;
        public string cwd;
        public bool? running;
        public UsageStats usage;
        /// <summary>
        /// The gateway's LIVE session_key for this runtime id. Auto-compression rotates it (the
        /// parent conversation ends and a continuation row takes over) while the runtime id stays
        /// put, so it is the only signal that a chat's persisted key moved.
        /// </summary>
        public string stored_session_id;
        public string title;
        public string reasoning_effort;
        public bool? fast;
    }

    [Serializable]
    public class UsageStats
    {
        public int input;
        public int output;
        public int total;
        public int calls;
        public int context_max;
        public int context_used;
        public float context_percent;
        public float cost_usd;
    }

    [Serializable]
    public class MessageDeltaPayload
    {
        public string text;
    }

    [Serializable]
    public class MessageCompletePayload
    {
        public string text;
        public string rendered;
        public UsageStats usage;
    }

    [Serializable]
    public class ContextUsageCategory
    {
        public string id;
        public string label;
        public string color;
        public int tokens;
    }

    [Serializable]
    public class ContextBreakdown
    {
        public ContextUsageCategory[] categories;
        public int context_max;
        public float context_percent;
        public int context_used;
        public int estimated_total;
        public string model;
    }

    [Serializable]
    public class ToolEventPayload
    {
        public string name;
        public string tool_id;
        public string status;
        public string preview;
        public string context;
        public string inline_diff;
        public string emoji;
        public object args;
        /// <summary>Desktop GatewayEventPayload.error — string or boolean; truthy marks tool as failed.</summary>
        public object error;
    }

    [Serializable]
    public class ClarifyEventPayload
    {
        public string request_id;
        public string question;
        public string[] choices;
    }

    [Serializable]
    public class TerminalExecutePayload
    {
        public string request_id;
        public string command;
        public int timeout_ms;
        // Optional: when true, run on the persistent agent shell (state survives across
        // commands). Absent/false -> one-shot execution. Default keeps old behavior.
        public bool persistent;
    }

    [Serializable]
    public class ErrorPayload
    {
        public string message;
    }

    [Serializable]
    public class SteerResponse
    {
        public string status;
    }

    [Serializable]
    public class SecretEventPayload
    {
        public string request_id;
        public string env_var;
        public string prompt;
    }

    [Serializable]
    public class ApprovalEventPayload
    {
        public string command;
        public string description;
        public string[] choices;
        public bool allow_permanent = true;
        public bool smart_denied;
    }

    [Serializable]
    public class SessionTitlePayload
    {
        public string session_id;
        public string title;
    }

    public class TerminalExecuteRequest
    {
        public string RequestId;
        public string Command;
        public int TimeoutMs;
        public bool Persistent;
    }

    // read_terminal tool -> terminal.read.request. Start/Count are optional paging hints into
    // the terminal buffer; -1 means "unset" (client picks its default window). The client must
    // answer with terminal.read.respond because the backend blocks on it.
    public class TerminalReadRequest
    {
        public string RequestId;
        public int Start;
        public int Count;
    }

    // agent.terminal.output / terminal.close. Both are one-way pushes keyed by the backend
    // background process id; `chunk` is only present on output.
    [Serializable]
    public class AgentTerminalPayload
    {
        public string process_id;
        public string chunk;
    }

    // === HermesSessionManager ===

    public class HermesSessionManager : IChatTransport
    {
        /// <summary>Terminal width shipped on session.create/resume/activate (Desktop sends 96).</summary>
        private const int SessionCols = 96;

        /// <summary>
        /// Surface tag persisted as the session's DB `source` (Desktop sends "desktop"). It must
        /// be a stable explicit value: the gateway only falls back to env-resolved platform
        /// detection when the field is absent, and REST history does not filter on it.
        /// </summary>
        private const string SessionSource = "companion";

        private readonly HermesGateway _gateway;
        private readonly HermesClientBridge _clientBridge;
        private bool _disposed;

        // Foreground/last-resumed session hints. These no longer filter events — the transport
        // multiplexes every session. ActiveSessionId is the session the UI currently views; it
        // only drives RuntimeInfo and the foreground-only handlers (clarify/approval/terminal).
        public string ActiveSessionId { get; private set; }
        public string StoredSessionId { get; private set; }

        /// <summary>
        /// Workspace the foreground session last reported (session.info cwd). A backend path, not
        /// a device path. New chats are created in it so they open where the user was working,
        /// which is what Desktop's resolveNewSessionCwd does; null = let the gateway choose.
        /// </summary>
        public string LastKnownCwd { get; private set; }

        // Per-session generation state, keyed by the display/persisted session id. Runtime ids
        // from Hermes are translated at the transport boundary.
        private readonly Dictionary<string, bool> _busyBySession = new Dictionary<string, bool>();
        private readonly Dictionary<string, bool> _awaitingBySession = new Dictionary<string, bool>();
        private readonly Dictionary<string, SessionRuntimeInfo> _runtimeBySession = new Dictionary<string, SessionRuntimeInfo>();
        private readonly Dictionary<string, string> _runtimeByDisplaySession = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _displayByRuntimeSession = new Dictionary<string, string>();

        // Backlogs for agent.terminal.output, keyed by owning chat + backend process id.
        private readonly AgentTerminalStream _agentTerminals = new AgentTerminalStream();
        // terminal.respond is a companion-only extension; upstream answers -32601. Latch the first
        // rejection so a chatty terminal.execute bridge cannot spam the console every command.
        private bool _terminalRespondUnsupported;

        // Unscoped-stream pin (Desktop gateway-events.ts resolveGatewayEventSessionId). Holds the
        // display session id that last received an unscoped message.start, and is released on that
        // session's message.complete/error. Unscoped stream events (delta/tool/reasoning/prompt)
        // resolve to the pin so a mid-turn chat switch cannot steal live output onto the newly
        // focused chat.
        private string _unscopedStreamSessionId;

        // Stream events that, when they arrive WITHOUT an explicit session_id, belong to the pinned
        // turn rather than whatever chat is focused (Desktop UNSCOPED_STREAM_EVENT_TYPES). Companion
        // has no browser.progress event, so it is intentionally absent.
        private static readonly HashSet<string> UnscopedStreamEventTypes = new HashSet<string>
        {
            GatewayEvents.ApprovalRequest,
            GatewayEvents.ClarifyRequest,
            GatewayEvents.Error,
            GatewayEvents.MessageComplete,
            GatewayEvents.MessageDelta,
            GatewayEvents.MessageInterim,
            GatewayEvents.MessageStart,
            GatewayEvents.ReasoningAvailable,
            GatewayEvents.ReasoningDelta,
            GatewayEvents.SecretRequest,
            GatewayEvents.StatusUpdate,
            GatewayEvents.SudoRequest,
            GatewayEvents.ThinkingDelta,
            GatewayEvents.ToolComplete,
            GatewayEvents.ToolGenerating,
            GatewayEvents.ToolProgress,
            GatewayEvents.ToolStart
        };

        /// <summary>True if the given session currently has a generation in flight.</summary>
        public bool IsSessionBusy(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                return false;
            sessionId = DisplaySessionIdFor(sessionId);
            bool busy;
            if (_busyBySession.TryGetValue(sessionId, out busy) && busy)
                return true;
            bool awaiting;
            if (_awaitingBySession.TryGetValue(sessionId, out awaiting) && awaiting)
                return true;
            return false;
        }

        /// <summary>Runtime info (model/usage/context) for a specific session, or null.</summary>
        public SessionRuntimeInfo RuntimeInfoFor(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                return null;
            sessionId = DisplaySessionIdFor(sessionId);
            SessionRuntimeInfo info;
            return _runtimeBySession.TryGetValue(sessionId, out info) ? info : null;
        }

        /// <summary>Runtime info for the foreground (currently viewed) session.</summary>
        public SessionRuntimeInfo RuntimeInfo => RuntimeInfoFor(ActiveSessionId);

        /// <summary>
        /// Mark which session the UI currently views (drives RuntimeInfo and the foreground-only
        /// clarify/approval/terminal handlers). Does not resume or touch the server.
        /// </summary>
        public void SetForegroundSession(string sessionId)
        {
            string displaySessionId = DisplaySessionIdFor(sessionId);
            ActiveSessionId = displaySessionId;
            OnRuntimeInfoChanged?.Invoke(displaySessionId);
        }

        public string RuntimeSessionIdFor(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                return sessionId;
            string runtime;
            return _runtimeByDisplaySession.TryGetValue(sessionId, out runtime) ? runtime : sessionId;
        }

        public bool HasRuntimeSessionFor(string sessionId)
        {
            return !string.IsNullOrEmpty(sessionId) && _runtimeByDisplaySession.ContainsKey(sessionId);
        }

        public string DisplaySessionIdFor(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                return sessionId;
            string display;
            return _displayByRuntimeSession.TryGetValue(sessionId, out display) ? display : sessionId;
        }

        private void RememberSessionIds(string runtimeSessionId, string displaySessionId)
        {
            if (string.IsNullOrEmpty(runtimeSessionId))
                return;
            if (string.IsNullOrEmpty(displaySessionId))
                displaySessionId = runtimeSessionId;

            _runtimeByDisplaySession[displaySessionId] = runtimeSessionId;
            _displayByRuntimeSession[runtimeSessionId] = displaySessionId;

            // Also make direct lookups harmless when the runtime id is already the display id.
            if (!_runtimeByDisplaySession.ContainsKey(runtimeSessionId))
                _runtimeByDisplaySession[runtimeSessionId] = runtimeSessionId;
        }

        /// <summary>
        /// Point a SECOND persisted key at an existing chat. Auto-compression ends a conversation
        /// and continues it under a new key; the chat keeps its canonical id (its transcript and
        /// sidebar row), and the rotated key is aliased onto it so later events/resumes that name
        /// the tip land in the same chat instead of opening a duplicate.
        /// </summary>
        private void RememberStoredAlias(string storedSessionId, string displaySessionId)
        {
            if (string.IsNullOrEmpty(storedSessionId) || string.IsNullOrEmpty(displaySessionId))
                return;
            if (storedSessionId == displaySessionId)
                return;

            _displayByRuntimeSession[storedSessionId] = displaySessionId;

            string runtime;
            if (_runtimeByDisplaySession.TryGetValue(displaySessionId, out runtime) && !string.IsNullOrEmpty(runtime))
                _runtimeByDisplaySession[storedSessionId] = runtime;
        }

        /// <summary>
        /// Reconcile a payload that carries BOTH ids (session.info, session.activate,
        /// session.active_list). Keeps the chat's canonical display id and only re-points the
        /// runtime id used by prompt.submit — a rotated stored key becomes an alias, never a new
        /// chat. Returns the display id the pair belongs to.
        /// </summary>
        private string ReconcileSessionIds(string runtimeSessionId, string storedSessionId)
        {
            bool hasRuntime = !string.IsNullOrEmpty(runtimeSessionId);
            bool hasStored = !string.IsNullOrEmpty(storedSessionId);
            if (!hasRuntime && !hasStored)
                return null;

            // A known runtime id already names its chat; otherwise the stored key does (that is
            // the reconnect case, where the gateway minted a fresh runtime id while we were away).
            string display = null;
            if (hasRuntime && _displayByRuntimeSession.ContainsKey(runtimeSessionId))
                display = _displayByRuntimeSession[runtimeSessionId];
            else if (hasStored && _displayByRuntimeSession.ContainsKey(storedSessionId))
                display = _displayByRuntimeSession[storedSessionId];
            else if (hasStored && _runtimeByDisplaySession.ContainsKey(storedSessionId))
                display = storedSessionId;
            else if (hasRuntime && _runtimeByDisplaySession.ContainsKey(runtimeSessionId))
                display = runtimeSessionId;
            else
                display = hasStored ? storedSessionId : runtimeSessionId;

            if (hasRuntime)
                RememberSessionIds(runtimeSessionId, display);
            if (hasStored)
                RememberStoredAlias(storedSessionId, display);

            return display;
        }

        private void ForgetSessionIds(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                return;

            string display = DisplaySessionIdFor(sessionId);
            string runtime = RuntimeSessionIdFor(sessionId);
            // The chat is gone, so its background processes have no view to stream into.
            _agentTerminals.ForgetSession(display);
            _runtimeByDisplaySession.Remove(display);
            _runtimeByDisplaySession.Remove(runtime);
            _displayByRuntimeSession.Remove(runtime);
            _displayByRuntimeSession.Remove(display);
        }

        private void SetBusy(string sessionId, bool value)
        {
            if (!string.IsNullOrEmpty(sessionId))
                _busyBySession[sessionId] = value;
        }

        private void SetAwaiting(string sessionId, bool value)
        {
            if (!string.IsNullOrEmpty(sessionId))
                _awaitingBySession[sessionId] = value;
        }

        // === IChatTransport ===

        public bool IsConnected => _gateway.State == ConnectionState.Open;

        public event Action<string> OnStreamStarted;
        public event Action<string, string> OnDelta;
        public event Action<string, string> OnComplete;
        public event Action<string, string> OnReasoningDelta;
        public event Action<string, ToolCallUpdate> OnToolUpdate;
        public event Action<string, ClarifyRequest> OnClarifyRequest;
        public event Action<string, ApprovalRequest> OnApprovalRequest;
        public event Action<string, SecretRequest> OnSecretRequest;
        public event Action<string, string> OnSecretExpire;
        public event Action<string, string> OnSessionTitle;
        // (sid, text) — a background self-improvement review saved to memory/skills. Surfaced as a
        // persistent system line in the transcript (Desktop review.summary parity).
        public event Action<string, string> OnReviewSummary;
        public event Action<string, string> OnError;
        public event Action<TransportState> OnStateChanged;
        public event Action<string> OnRuntimeInfoChanged;

        /// <summary>
        /// (sid, activate payload) — a session that was STILL RUNNING on the gateway has been
        /// re-attached after a reconnect. The payload carries the backend's own view of the turn
        /// in flight (inflight/queued/running), which is authoritative: listeners should reconcile
        /// their partial bubble with it instead of starting a second one.
        /// </summary>
        public event Action<string, SessionResumeResponse> OnSessionRehydrated;

        public event Action<TerminalExecuteRequest> OnTerminalExecute;
        public event Action<TerminalReadRequest> OnTerminalReadRequest;

        /// <summary>
        /// (sid, processId, chunk) — live output of a backend `terminal(background=true)` process,
        /// already routed to the chat that owns it. The same chunk is appended to
        /// <see cref="AgentTerminals"/>, so a view that mounts later can replay the backlog.
        /// </summary>
        public event Action<string, string, string> OnAgentTerminalOutput;

        /// <summary>
        /// (sid, processId) — the agent dropped a background process's read-only view via the
        /// close_terminal tool. The process keeps running; only the view/backlog goes away.
        /// </summary>
        public event Action<string, string> OnAgentTerminalClose;

        /// <summary>Backlogs for the agent terminals streamed by this transport.</summary>
        public AgentTerminalStream AgentTerminals { get { return _agentTerminals; } }

        // === Constructor ===

        public HermesSessionManager(HermesGateway gateway)
        {
            _gateway = gateway;
            var rootResolver = new FilePathRootResolver();
            var fileTransferReceiver = new FileTransferReceiver(gateway, rootResolver);
            var fileTransferSender = new FileTransferSender(gateway, rootResolver);
            _clientBridge = new HermesClientBridge(gateway, fileTransferReceiver, fileTransferSender);
            RegisterEventHandlers();

            _gateway.OnStateChange(HandleGatewayStateChange);
        }

        // === IChatTransport: Connection ===

        public Task Connect(string url, string token = null)
        {
            return Connect(url, token, null);
        }

        /// <summary>
        /// Open the gateway socket. The upgrade carries auth ONLY — no profile. One remote
        /// gateway serves every backend profile (Desktop buildGatewayWsUrlWithTicket appends
        /// nothing but the ticket), so the profile is not a property of the socket: it rides in
        /// the params of <see cref="CreateSession"/> / <see cref="ResumeSession"/>.
        /// </summary>
        public async Task Connect(string url, string token, string ticket)
        {
            string wsUrl = url;
            // OAuth remote mode: a single-use ws-ticket authenticates the upgrade (?ticket=).
            // Legacy token mode is unchanged (?token=). Ticket wins when both are supplied.
            if (!string.IsNullOrEmpty(ticket))
            {
                wsUrl = HermesRemoteAuth.BuildTicketWsUrl(wsUrl, ticket);
            }
            else if (!string.IsNullOrEmpty(token))
            {
                string separator = wsUrl.Contains("?") ? "&" : "?";
                wsUrl = wsUrl + separator + "token=" + Uri.EscapeDataString(token);
            }

            await _gateway.Connect(wsUrl);

            // Wait for gateway.ready event
            var readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<GatewayEvent> readyHandler = null;
            readyHandler = (evt) =>
            {
                readyTcs.TrySetResult(true);
                _gateway.Off(GatewayEvents.GatewayReady, readyHandler);
            };
            _gateway.On(GatewayEvents.GatewayReady, readyHandler);

            // Timeout after 5 seconds
            _ = Task.Delay(5000).ContinueWith(_ => readyTcs.TrySetResult(true));
            await readyTcs.Task;

            NeonLogger.Log("[Hermes] Connected and ready");

            // A reconnect keeps the backend's sessions alive but leaves their event transport
            // bound to the socket that just died. Re-attach the ones this client tracks; failures
            // (older gateway, nothing live) are non-fatal and must never block the connect.
            try
            {
                await RehydrateActiveSessions();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Hermes] Active-session rehydration skipped: " + ex.Message);
            }
        }

        /// <summary>
        /// Tear down the TRANSPORT only. session.close destroys the backend session (the gateway
        /// pops it and finalizes its agent), so sending it because a socket is being replaced —
        /// reconnect, endpoint change, profile switch, mode toggle — would kill a conversation
        /// that is still generating server-side. Desktop closes a session only on an explicit user
        /// close/delete, and reattaches everything else with session.activate after reconnect.
        /// The id maps survive on purpose: they are what <see cref="RehydrateActiveSessions"/>
        /// rebinds against. Use <see cref="CloseSession"/> for a real close.
        /// </summary>
        public async Task Disconnect()
        {
            await _gateway.Close();
        }

        /// <summary>
        /// Forget every local session mapping WITHOUT touching the server: no session.close, no
        /// delete. Used on a Hermes profile switch, where the sessions being left must keep
        /// running (switching back must list and open them again) but their ids must not be
        /// reused against the profile being switched to.
        /// </summary>
        public void DropLocalSessionState()
        {
            _busyBySession.Clear();
            _awaitingBySession.Clear();
            _runtimeBySession.Clear();
            _runtimeByDisplaySession.Clear();
            _displayByRuntimeSession.Clear();
            // Agent-terminal backlogs are keyed by chat id; the ids being dropped must not be
            // reused against the profile being switched to.
            _agentTerminals.Clear();
            _unscopedStreamSessionId = null;
            ActiveSessionId = null;
            StoredSessionId = null;
            // Workspaces are per-profile: carrying one over would create the next chat in a
            // directory that belongs to the profile the user just left.
            LastKnownCwd = null;
        }

        // === IChatTransport: Messaging ===

        public Task SendMessage(string sessionId, string text)
        {
            return SubmitPrompt(sessionId, text, false, 0, false);
        }

        /// <summary>
        /// Rewind: <c>prompt.submit</c> carrying <c>truncate_before_user_ordinal</c>, which makes the
        /// backend drop that user turn plus everything after it before running the new text. Desktop
        /// <c>runRewindSubmit</c> interrupts a live turn first (a submit into a running agent comes
        /// back as "session busy"); an idle session is submitted into directly, because interrupting
        /// an idle agent can leave a stale interrupt flag that cancels the fresh turn.
        /// </summary>
        public async Task RewindAndSubmit(string sessionId, string text, int truncateBeforeUserOrdinal)
        {
            if (string.IsNullOrEmpty(sessionId))
                throw new InvalidOperationException("No session id");
            if (truncateBeforeUserOrdinal < 0)
                throw new ArgumentOutOfRangeException("truncateBeforeUserOrdinal");

            string displaySessionId = DisplaySessionIdFor(sessionId);
            if (IsSessionBusy(displaySessionId))
                await Interrupt(sessionId);

            await SubmitPrompt(sessionId, text, true, truncateBeforeUserOrdinal, true);
        }

        private async Task SubmitPrompt(
            string sessionId,
            string text,
            bool truncate,
            int truncateBeforeUserOrdinal,
            bool allowBusy)
        {
            if (string.IsNullOrEmpty(sessionId))
                throw new InvalidOperationException("No session id");

            string displaySessionId = DisplaySessionIdFor(sessionId);
            string runtimeSessionId = RuntimeSessionIdFor(sessionId);

            if (!allowBusy && IsSessionBusy(displaySessionId))
                throw new InvalidOperationException("Session is busy. Wait for the current response to finish.");

            // The ordinal key is omitted entirely for a normal submit: the gateway treats a present
            // truncate_before_user_ordinal as an explicit rewind, so a placeholder would drop turns.
            object payload = truncate
                ? (object)new { session_id = runtimeSessionId, text, truncate_before_user_ordinal = truncateBeforeUserOrdinal }
                : (object)new { session_id = runtimeSessionId, text };

            try
            {
                // prompt.submit is effectively fire-and-forget: turn completion is signalled by
                // message.complete/error stream events, not this ack. Pass the long timeout so a
                // long-running turn does not surface a spurious "request timed out"
                // (Desktop PROMPT_SUBMIT_REQUEST_TIMEOUT_MS = 1_800_000).
                await _gateway.Request<object>(
                    RpcMethods.PromptSubmit,
                    payload,
                    HermesGateway.PromptSubmitTimeoutMs);
            }
            catch
            {
                SetBusy(displaySessionId, false);
                SetAwaiting(displaySessionId, false);
                throw;
            }

            SetBusy(displaySessionId, true);
            SetAwaiting(displaySessionId, true);
        }

        public async Task AttachImageBytes(string sessionId, string contentBase64)
        {
            if (string.IsNullOrEmpty(sessionId))
                throw new InvalidOperationException("No session id");

            string runtimeSessionId = RuntimeSessionIdFor(sessionId);
            if (IsSessionBusy(DisplaySessionIdFor(sessionId)))
                throw new InvalidOperationException("Session is busy. Wait for the current response to finish.");

            if (string.IsNullOrEmpty(contentBase64))
                return;

            await _gateway.Request<object>(
                RpcMethods.ImageAttachBytes,
                new { session_id = runtimeSessionId, content_base64 = contentBase64 });
        }

        public async Task Interrupt(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                return;

            string displaySessionId = DisplaySessionIdFor(sessionId);
            try
            {
                await _gateway.Request<object>(
                    RpcMethods.SessionInterrupt,
                    new { session_id = RuntimeSessionIdFor(sessionId) });
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Hermes] Interrupt failed: " + ex.Message);
            }
            finally
            {
                SetBusy(displaySessionId, false);
                SetAwaiting(displaySessionId, false);
            }
        }

        public async Task<bool> Steer(string sessionId, string text)
        {
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(text))
                return false;

            string runtimeSid = RuntimeSessionIdFor(sessionId);
            try
            {
                var result = await _gateway.Request<SteerResponse>(
                    RpcMethods.SessionSteer,
                    new { session_id = runtimeSid, text });
                return result != null && string.Equals(result.status, "queued", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // Swallow — caller falls back to queuing the text for the next turn.
                return false;
            }
        }

        // === Session Lifecycle ===

        /// <summary>
        /// Blank/whitespace profile means "the gateway's own default" — send null rather than an
        /// empty string so the param reads as absent on the gateway side.
        /// </summary>
        private static string NormalizeProfile(string profile)
        {
            return string.IsNullOrWhiteSpace(profile) ? null : profile.Trim();
        }

        /// <summary>
        /// Create a session in <paramref name="profile"/> (null = the gateway's own default).
        /// Passing the profile is load-bearing: one remote gateway serves every profile, so an
        /// omitted profile silently lands the new chat on the launch (default) profile no matter
        /// which one the UI shows. Desktop desktopSessionCreateParams sends it the same way.
        /// </summary>
        public async Task<SessionCreateResponse> CreateSession(
            string cwd = null,
            string title = null,
            string profile = null,
            string model = null,
            string provider = null,
            string reasoningEffort = null,
            bool? fast = null)
        {
            // Desktop desktopSessionCreateParams. Every optional field is OMITTED rather than sent
            // empty: the gateway treats presence as intent (a "fast" key pins the service tier,
            // an absent one inherits the profile; an empty cwd would not be an explicit workspace
            // choice), so a blanket payload would silently override profile defaults.
            var payload = new Dictionary<string, object>
            {
                { "cols", SessionCols },
                { "source", SessionSource }
            };
            if (!string.IsNullOrWhiteSpace(cwd))
                payload["cwd"] = cwd.Trim();
            if (!string.IsNullOrWhiteSpace(title))
                payload["title"] = title.Trim();
            string normalizedProfile = NormalizeProfile(profile);
            if (!string.IsNullOrEmpty(normalizedProfile))
                payload["profile"] = normalizedProfile;
            if (!string.IsNullOrWhiteSpace(model))
            {
                payload["model"] = model.Trim();
                // provider is only meaningful alongside a model (the gateway resolves it at build).
                if (!string.IsNullOrWhiteSpace(provider))
                    payload["provider"] = provider.Trim();
            }
            if (!string.IsNullOrWhiteSpace(reasoningEffort))
                payload["reasoning_effort"] = reasoningEffort.Trim();
            if (fast.HasValue)
                payload["fast"] = fast.Value;

            var result = await _gateway.Request<SessionCreateResponse>(RpcMethods.SessionCreate, payload);

            string displaySessionId = !string.IsNullOrEmpty(result.stored_session_id)
                ? result.stored_session_id
                : result.session_id;
            RememberSessionIds(result.session_id, displaySessionId);

            ActiveSessionId = displaySessionId;
            StoredSessionId = displaySessionId;

            ApplyRuntimeInfo(displaySessionId, result.info, null);

            NeonLogger.Log("[Hermes] Session created: " + result.session_id);
            return result;
        }

        /// <summary>
        /// Resume a session that lives in <paramref name="profile"/> (null = the gateway's own
        /// default). The lookup runs against that profile's state.db, so resuming a non-default
        /// session without its profile fails with "session not found".
        /// </summary>
        public async Task<SessionResumeResponse> ResumeSession(string sessionId, string profile = null)
        {
            var payload = new Dictionary<string, object>
            {
                { "session_id", sessionId },
                { "cols", SessionCols },
                { "source", SessionSource }
            };
            string normalizedProfile = NormalizeProfile(profile);
            if (!string.IsNullOrEmpty(normalizedProfile))
                payload["profile"] = normalizedProfile;

            var result = await _gateway.Request<SessionResumeResponse>(RpcMethods.SessionResume, payload);

            string displaySessionId = AdoptResumePayload(sessionId, result);

            ActiveSessionId = displaySessionId;
            StoredSessionId = displaySessionId;

            NeonLogger.Log("[Hermes] Session resumed: " + sessionId
                + (displaySessionId == sessionId ? "" : " (chat " + displaySessionId + ")"));
            return result;
        }

        /// <summary>
        /// Bind a session.resume / session.activate payload to a chat and return its display id.
        ///
        /// The canonical key is the one the caller asked for (the sidebar row / persisted chat) —
        /// or, when that id is itself an alias of an already-open chat, that chat. The key the
        /// gateway reports back (session_key, else resumed) may be the post-compression tip; it is
        /// aliased onto the same chat so events naming it route correctly, while the runtime id
        /// used by prompt.submit is re-pointed to the freshly bound one.
        /// </summary>
        private string AdoptResumePayload(string requestedSessionId, SessionResumeResponse result)
        {
            if (result == null)
                return DisplaySessionIdFor(requestedSessionId);

            string displaySessionId = DisplaySessionIdFor(requestedSessionId);
            if (string.IsNullOrEmpty(displaySessionId))
            {
                displaySessionId = FirstNonEmpty(result.session_key, result.stored_session_id, result.resumed, result.session_id);
            }

            RememberSessionIds(result.session_id, displaySessionId);
            RememberStoredAlias(result.session_key, displaySessionId);
            RememberStoredAlias(result.stored_session_id, displaySessionId);
            RememberStoredAlias(result.resumed, displaySessionId);

            ApplyRuntimeInfo(displaySessionId, result.info, null);

            // The gateway reports whether a turn is still generating on this session. Trust it:
            // a resume/activate during a live turn must keep the busy state instead of presenting
            // an idle chat whose Send button then races the running generation.
            SetBusy(displaySessionId, result.running);
            SetAwaiting(displaySessionId, result.running);

            return displaySessionId;
        }

        /// <summary>
        /// Re-attach an already-live session to the current socket. Unlike session.resume this
        /// neither rebuilds the agent nor reloads the transcript — it rebinds the backend's
        /// per-session event transport to this connection and reports the live turn — and unlike
        /// session.close it leaves every other session running. Returns null when the gateway
        /// predates the method or the session is no longer live there.
        /// </summary>
        public async Task<SessionResumeResponse> ActivateSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                return null;

            string runtimeSid = RuntimeSessionIdFor(sessionId);
            try
            {
                // Bounded well under the 30s default: activate never builds an agent, and it runs
                // on the connect path, where a hung gateway must not stall the whole reconnect.
                var result = await _gateway.Request<SessionResumeResponse>(
                    RpcMethods.SessionActivate,
                    new { session_id = runtimeSid, cols = SessionCols },
                    timeoutMs: 10000);
                if (result == null)
                    return null;

                AdoptResumePayload(sessionId, result);
                return result;
            }
            catch (Exception ex)
            {
                // Missing method (older gateway) and "session not found" (the runtime id died with
                // the previous backend) are both non-fatal: the caller falls back to a full resume.
                Debug.LogWarning("[Hermes] session.activate failed for " + runtimeSid + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// The gateway's in-memory snapshot of sessions it still holds agents for. This is LIVE
        /// STATUS ONLY and is not profile-scoped — the durable, profile-scoped history catalog
        /// stays REST /api/sessions?profile=…. Returns null when the gateway does not expose the
        /// method, so callers can leave their current state untouched.
        /// </summary>
        public async Task<List<LiveSessionStatus>> ListActiveSessions()
        {
            try
            {
                // Short timeout: this is an opportunistic read on the connect path (see
                // ActivateSession) — falling back to "unknown" beats delaying the reconnect.
                var response = await _gateway.Request<LiveSessionListResponse>(
                    RpcMethods.SessionActiveList,
                    new { },
                    timeoutMs: 5000);
                if (response == null || response.sessions == null)
                    return null;
                return new List<LiveSessionStatus>(response.sessions);
            }
            catch (Exception ex)
            {
                if (!HermesGateway.IsMissingRpcMethod(ex))
                    Debug.LogWarning("[Hermes] session.active_list failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// After a reconnect, restore the live state of sessions this client already knows and
        /// re-point the backend's event transport at the new socket.
        ///
        /// Events emitted while the socket was down cannot be replayed, so a running turn would
        /// otherwise stay dark until the user clicked the chat. Only sessions already mapped
        /// locally are touched: session.active_list also reports sessions belonging to other
        /// clients and other profiles, and adopting those would fabricate chats. No transcript is
        /// synthesized — session.activate reports the in-flight turn, and the caller decides how
        /// to reconcile it with what is already on screen.
        /// </summary>
        public async Task<List<SessionResumeResponse>> RehydrateActiveSessions()
        {
            var rehydrated = new List<SessionResumeResponse>();
            List<LiveSessionStatus> live = await ListActiveSessions();
            // null = the gateway does not expose the method. Leave every mapping as it is; the
            // stale-runtime-id retry on prompt.submit remains the fallback, exactly as before.
            if (live == null)
                return rehydrated;

            var stillLive = new HashSet<string>();

            for (int i = 0; i < live.Count; i++)
            {
                LiveSessionStatus item = live[i];
                if (item == null)
                    continue;

                // "Known" means this client has a chat bound to that persisted key or runtime id.
                // A fresh session belonging to somebody else's window is skipped entirely.
                bool known = (!string.IsNullOrEmpty(item.session_key)
                        && (_runtimeByDisplaySession.ContainsKey(item.session_key)
                            || _displayByRuntimeSession.ContainsKey(item.session_key)))
                    || (!string.IsNullOrEmpty(item.id) && _displayByRuntimeSession.ContainsKey(item.id));
                if (!known)
                    continue;

                string sid = ReconcileSessionIds(item.id, item.session_key);
                if (string.IsNullOrEmpty(sid))
                    continue;
                stillLive.Add(sid);

                // Rebind the backend's transport for this session to the new socket. Without it
                // the gateway keeps publishing the live turn into the dead connection.
                SessionResumeResponse activated = await ActivateSession(sid);

                // Applied AFTER the activate: its own `running` flag cannot express a session
                // parked on an approval/clarify prompt, which the live status reports as
                // "waiting" — and that still has to read as busy so the composer stays gated.
                bool waiting = string.Equals(item.status, "waiting", StringComparison.OrdinalIgnoreCase);
                bool working = waiting || string.Equals(item.status, "working", StringComparison.OrdinalIgnoreCase);
                SetBusy(sid, working);
                SetAwaiting(sid, working);

                SessionRuntimeInfo runtime;
                if (_runtimeBySession.TryGetValue(sid, out runtime) && runtime != null)
                    runtime.running = working;
                OnRuntimeInfoChanged?.Invoke(sid);

                if (activated != null)
                {
                    rehydrated.Add(activated);
                    OnSessionRehydrated?.Invoke(sid, activated);
                }
            }

            PruneDeadRuntimeBindings(stillLive);
            return rehydrated;
        }

        /// <summary>
        /// Drop the runtime-id bindings of chats the gateway no longer has a live session for
        /// (it reports idle ones too, so absence means the session really is gone from this
        /// process). This is bookkeeping ONLY — no session.close, no delete, and the chat's
        /// runtime info stays: the next send simply resumes the conversation from its persisted
        /// key instead of submitting against an id that would answer "session not found".
        /// </summary>
        private void PruneDeadRuntimeBindings(HashSet<string> stillLive)
        {
            var dead = new List<string>();
            foreach (var pair in _runtimeByDisplaySession)
            {
                if (!stillLive.Contains(DisplaySessionIdFor(pair.Key)))
                    dead.Add(pair.Key);
            }

            for (int i = 0; i < dead.Count; i++)
                ForgetSessionIds(dead[i]);
        }

        public async Task CloseSession(string sessionId = null)
        {
            var sid = sessionId ?? ActiveSessionId;
            if (string.IsNullOrEmpty(sid))
                return;

            string displaySid = DisplaySessionIdFor(sid);
            string runtimeSid = RuntimeSessionIdFor(sid);

            try
            {
                await _gateway.Request<object>(RpcMethods.SessionClose, new { session_id = runtimeSid });
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Hermes] Close session failed: " + ex.Message);
            }

            _busyBySession.Remove(displaySid);
            _awaitingBySession.Remove(displaySid);
            _runtimeBySession.Remove(displaySid);
            ForgetSessionIds(displaySid);

            if (displaySid == ActiveSessionId)
            {
                ActiveSessionId = null;
                StoredSessionId = null;
            }
        }

        /// <summary>
        /// List all sessions known to the gateway (server is the source of truth in Hermes mode).
        /// </summary>
        public async Task<List<HermesSession>> ListSessions(int limit = 40)
        {
            var token = await _gateway.Request<JToken>(RpcMethods.SessionList, new { limit, offset = 0 });
            var list = ParseSessionList(token);
            return list;
        }

        private static List<HermesSession> ParseSessionList(JToken token)
        {
            var list = new List<HermesSession>();
            if (token == null)
                return list;

            JToken arr = token;
            if (token.Type == JTokenType.Object)
            {
                JToken nested = token["sessions"] ?? token["items"] ?? token["data"];
                if (nested != null)
                    arr = nested;
            }

            if (arr.Type == JTokenType.Array)
            {
                foreach (JToken child in arr.Children())
                {
                    try
                    {
                        var hs = child.ToObject<HermesSession>();
                        if (hs != null && !string.IsNullOrEmpty(hs.id))
                            list.Add(hs);
                    }
                    catch { }
                }
            }

            return list;
        }

        /// <summary>
        /// Switch model for the CURRENT session only via gateway slash command.
        /// The trailing --session flag is mandatory for parity with Desktop
        /// (use-model-controls.ts selectModel → config.set "model" =
        /// "<model> --provider <provider> --session"): it forces the gateway's
        /// resolve_persist_behavior to session scope so the pick can NEVER write
        /// the profile default in config.yaml. Without it a bare "/model <name>"
        /// (no provider) defers to model.persist_switch_by_default, which — when
        /// enabled — would leak the switch into every future chat.
        /// </summary>
        public async Task<bool> SwitchModelAsync(string modelId, string providerSlug = null)
        {
            string sessionId = ActiveSessionId;
            string cmd = $"/model {modelId}";
            if (!string.IsNullOrEmpty(providerSlug))
                cmd += $" --provider {providerSlug}";
            cmd += " --session";

            var result = await _gateway.Request<object>(
                "slash.exec",
                new { session_id = RuntimeSessionIdFor(sessionId), command = cmd }
            );
            OnRuntimeInfoChanged?.Invoke(sessionId);
            return result != null;
        }

        /// <summary>
        /// Fetch model options grouped by provider from the gateway.
        /// </summary>
        public async Task<ModelOptionsResponse> GetModelOptionsAsync()
        {
            var result = await _gateway.Request<ModelOptionsResponse>(
                "model.options",
                new { session_id = RuntimeSessionIdFor(ActiveSessionId) }
            );
            return result;
        }

        // === Clarify / Approval ===

        public async Task RespondToClarify(string requestId, string answer)
        {
            await _gateway.Request<object>(
                RpcMethods.ClarifyRespond,
                new { request_id = requestId, answer });
        }

        /// <summary>
        /// Answer a companion-only terminal.execute. The upstream gateway has no terminal.respond
        /// method and rejects the call with -32601; that is a protocol mismatch, not a failure, so
        /// it is swallowed (once, loudly) instead of surfacing as a bridge error. Every other
        /// failure still propagates to the caller.
        /// </summary>
        public async Task RespondToTerminal(string requestId, ProcessResult result, long? durationMs = null)
        {
            if (_terminalRespondUnsupported)
                return;

            var payload = new Dictionary<string, object>
            {
                { "request_id", requestId },
                { "stdout", result.stdout ?? string.Empty },
                { "stderr", result.stderr ?? string.Empty },
                { "exit_code", result.exitCode },
                { "timed_out", result.timedOut }
            };
            if (durationMs.HasValue)
                payload["duration_ms"] = durationMs.Value;

            try
            {
                await _gateway.Request<object>(RpcMethods.TerminalRespond, payload);
            }
            catch (Exception ex)
            {
                if (!HermesGateway.IsMissingRpcMethod(ex))
                    throw;

                _terminalRespondUnsupported = true;
                Debug.LogWarning(
                    "[Hermes] terminal.respond is not supported by this backend; the terminal.execute "
                    + "bridge is disabled for this connection (upstream uses read_terminal instead).");
            }
        }

        /// <summary>
        /// Answer a terminal.read.request. <paramref name="text"/> is the JSON-serialized
        /// terminal buffer view (empty string = no live pane), matching Desktop
        /// gateway-event.ts which sends { request_id, text }.
        /// </summary>
        public async Task RespondToTerminalRead(string requestId, string text)
        {
            await _gateway.Request<object>(
                RpcMethods.TerminalReadRespond,
                new { request_id = requestId, text = text ?? string.Empty });
        }

        public async Task RespondToApproval(string sessionId, bool approved)
        {
            string choice = approved ? "once" : "deny";
            string sid = string.IsNullOrEmpty(sessionId) ? ActiveSessionId : sessionId;
            await _gateway.Request<object>(
                RpcMethods.ApprovalRespond,
                new { session_id = RuntimeSessionIdFor(sid), choice });
        }

        /// <summary>
        /// Respond to an approval.request with an explicit choice. Desktop supports:
        /// "once" (run this time), "session" (allow for this session), "always" (permanent),
        /// "deny" (reject).
        /// </summary>
        public async Task RespondToApproval(string sessionId, string choice)
        {
            string sid = string.IsNullOrEmpty(sessionId) ? ActiveSessionId : sessionId;
            await _gateway.Request<object>(
                RpcMethods.ApprovalRespond,
                new { session_id = RuntimeSessionIdFor(sid), choice });
        }

        /// <summary>Answer a secret.request (skill credential capture) with the captured value.</summary>
        public async Task RespondToSecret(string requestId, string value)
        {
            await _gateway.Request<object>(
                RpcMethods.SecretRespond,
                new { request_id = requestId, value });
        }

        /// <summary>Answer a sudo.request with the captured password.</summary>
        public async Task RespondToSudo(string requestId, string password)
        {
            await _gateway.Request<object>(
                RpcMethods.SudoRespond,
                new { request_id = requestId, password });
        }

        // === Context / Usage RPC ===

        /// <summary>
        /// Fetch detailed context breakdown from the gateway (categories, exact used/max).
        /// Returns null if the RPC fails or the session has no context data.
        /// </summary>
        public async Task<ContextBreakdown> RequestContextBreakdown(string sessionId = null)
        {
            string sid = string.IsNullOrEmpty(sessionId) ? ActiveSessionId : sessionId;
            if (string.IsNullOrEmpty(sid))
                return null;

            string displaySid = DisplaySessionIdFor(sid);
            string runtimeSid = RuntimeSessionIdFor(sid);
            try
            {
                ContextBreakdown breakdown = await _gateway.Request<ContextBreakdown>(
                    RpcMethods.SessionContextBreakdown,
                    new { session_id = runtimeSid },
                    timeoutMs: 5000);
                if (breakdown != null)
                    ApplyContextBreakdown(displaySid, breakdown);
                return breakdown;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Fetch cumulative session usage from the gateway. Returns null on failure.
        /// Desktop uses this as a compatibility fallback when session.activate fails.
        /// </summary>
        public async Task<UsageStats> RequestSessionUsage(string sessionId = null)
        {
            string sid = string.IsNullOrEmpty(sessionId) ? ActiveSessionId : sessionId;
            if (string.IsNullOrEmpty(sid))
                return null;

            string displaySid = DisplaySessionIdFor(sid);
            string runtimeSid = RuntimeSessionIdFor(sid);
            try
            {
                UsageStats usage = await _gateway.Request<UsageStats>(
                    RpcMethods.SessionUsage,
                    new { session_id = runtimeSid },
                    timeoutMs: 5000);
                if (usage != null)
                    ApplyUsage(displaySid, usage);
                return usage;
            }
            catch
            {
                return null;
            }
        }

        // === Event Handlers ===

        private void RegisterEventHandlers()
        {
            _gateway.On(GatewayEvents.GatewayReady, evt =>
            {
                NeonLogger.Log("[Hermes] Gateway ready");
            });

            _gateway.On(GatewayEvents.SessionInfo, HandleSessionInfo);
            _gateway.On(GatewayEvents.MessageStart, HandleMessageStart);
            _gateway.On(GatewayEvents.MessageDelta, HandleMessageDelta);
            _gateway.On(GatewayEvents.MessageInterim, HandleMessageInterim);
            _gateway.On(GatewayEvents.MessageComplete, HandleMessageComplete);
            _gateway.On(GatewayEvents.ReasoningDelta, HandleReasoningDelta);
            // reasoning.available is a whole-block reasoning push; companion reasoning is
            // append-only, so route it through the same path as reasoning.delta.
            _gateway.On(GatewayEvents.ReasoningAvailable, HandleReasoningDelta);
            // thinking.delta is a reasoning stream too (Desktop pins it like reasoning.delta).
            // Without this it is in the unscoped-pin set but has no handler, so it is dropped.
            _gateway.On(GatewayEvents.ThinkingDelta, HandleReasoningDelta);
            _gateway.On(GatewayEvents.StatusUpdate, HandleStatusUpdate);
            _gateway.On(GatewayEvents.ToolStart, HandleToolStart);
            _gateway.On(GatewayEvents.ToolProgress, HandleToolProgress);
            // tool.generating is the pre-run "assembling arguments" phase — a running tool update.
            _gateway.On(GatewayEvents.ToolGenerating, HandleToolStart);
            _gateway.On(GatewayEvents.ToolComplete, HandleToolComplete);
            _gateway.On(GatewayEvents.ClarifyRequest, HandleClarifyRequest);
            _gateway.On(GatewayEvents.ApprovalRequest, HandleApprovalRequest);
            _gateway.On(GatewayEvents.SudoRequest, HandleSudoRequest);
            _gateway.On(GatewayEvents.SecretRequest, HandleSecretRequest);
            _gateway.On(GatewayEvents.SudoExpire, HandleSecretExpire);
            _gateway.On(GatewayEvents.SecretExpire, HandleSecretExpire);
            _gateway.On(GatewayEvents.SessionTitle, HandleSessionTitle);
            _gateway.On(GatewayEvents.BackgroundComplete, HandleBackgroundComplete);
            _gateway.On(GatewayEvents.ReviewSummary, HandleReviewSummary);
            _gateway.On(GatewayEvents.TerminalExecute, HandleTerminalExecute);
            _gateway.On(GatewayEvents.TerminalReadRequest, HandleTerminalReadRequest);
            _gateway.On(GatewayEvents.AgentTerminalOutput, HandleAgentTerminalOutput);
            _gateway.On(GatewayEvents.TerminalClose, HandleTerminalClose);
            _gateway.On(GatewayEvents.Error, HandleError);
            // subagent.* has no dedicated per-type registration; a wildcard lets any subagent
            // subtype be handled by prefix (Desktop matches on the SubagentPrefix) so none is
            // dropped silently, while unscoped ones are still refused inside the handler.
            _gateway.On("*", HandleWildcardEvent);
        }

        private bool IsActiveEvent(GatewayEvent evt)
        {
            if (evt == null)
                return false;
            var sessionId = DisplaySessionIdFor(evt.SessionId ?? ActiveSessionId);
            return sessionId == ActiveSessionId;
        }

        /// <summary>
        /// Resolve the display session id an event belongs to, porting Desktop
        /// resolveGatewayEventSessionId (gateway-events.ts). Explicit session_id always wins;
        /// unscoped stream events pin to the session that last received message.start so a
        /// mid-turn chat switch cannot steal live deltas/tool output; unscoped subagent.* is
        /// refused (returns null) rather than attributed to the focused chat. This mutates the
        /// unscoped-stream pin and must be called exactly once per event (each handler calls it
        /// once at the top; there is one handler per event type).
        /// </summary>
        private string EventSessionId(GatewayEvent evt)
        {
            if (evt == null)
                return ActiveSessionId;

            string type = evt.Type;
            string explicitSid = string.IsNullOrEmpty(evt.SessionId) ? null : DisplaySessionIdFor(evt.SessionId);
            bool isEnd = type == GatewayEvents.MessageComplete || type == GatewayEvents.Error;

            // Explicit session_id always wins. Release the pin only when this session's own turn ends.
            if (!string.IsNullOrEmpty(explicitSid))
            {
                if (isEnd && explicitSid == _unscopedStreamSessionId)
                    _unscopedStreamSessionId = null;
                return explicitSid;
            }

            // Unscoped subagent.* must never attach to the focused chat (Desktop drop rule).
            if (!string.IsNullOrEmpty(type) && type.StartsWith(GatewayEvents.SubagentPrefix))
                return null;

            bool streamEvent = !string.IsNullOrEmpty(type) && UnscopedStreamEventTypes.Contains(type);

            string sid;
            if (type == GatewayEvents.MessageStart)
                sid = ActiveSessionId;
            else if (streamEvent)
                sid = !string.IsNullOrEmpty(_unscopedStreamSessionId) ? _unscopedStreamSessionId : ActiveSessionId;
            else
                sid = ActiveSessionId;

            // message.start pins the live stream to the focused session; end events release it.
            if (type == GatewayEvents.MessageStart && !string.IsNullOrEmpty(ActiveSessionId))
                _unscopedStreamSessionId = ActiveSessionId;
            else if (isEnd)
                _unscopedStreamSessionId = null;

            return sid;
        }

        /// <summary>
        /// Merge a runtime-info payload into a session's live state. session.info is emitted as a
        /// PARTIAL patch (a heartbeat may carry only running, or only usage), so every field is
        /// applied only when the payload actually names it — assigning the deserialized object
        /// wholesale would blank the model/provider/cwd and zero the token counters on the next
        /// bare heartbeat. Stored session metadata (title/preview/counters from REST) is a
        /// separate concern and is never written here.
        /// </summary>
        private void ApplyRuntimeInfo(string sessionId, SessionRuntimeInfo info, JToken raw)
        {
            if (string.IsNullOrEmpty(sessionId) || info == null)
                return;

            string sid = DisplaySessionIdFor(sessionId);
            SessionRuntimeInfo current;
            if (!_runtimeBySession.TryGetValue(sid, out current) || current == null)
            {
                current = new SessionRuntimeInfo();
                _runtimeBySession[sid] = current;
            }

            if (!string.IsNullOrEmpty(info.model))
                current.model = info.model;
            if (!string.IsNullOrEmpty(info.provider))
                current.provider = info.provider;
            if (!string.IsNullOrEmpty(info.cwd))
                current.cwd = info.cwd;
            if (!string.IsNullOrEmpty(info.title))
                current.title = info.title;
            if (!string.IsNullOrEmpty(info.reasoning_effort))
                current.reasoning_effort = info.reasoning_effort;
            if (info.fast.HasValue)
                current.fast = info.fast;
            if (info.running.HasValue)
                current.running = info.running;

            JToken usageToken = raw != null && raw.Type == JTokenType.Object ? raw["usage"] : null;
            if (usageToken != null && usageToken.Type == JTokenType.Object)
                current.usage = MergeUsage(usageToken, current.usage);
            else if (info.usage != null)
                current.usage = MergeUsage(info.usage, current.usage);

            OnRuntimeInfoChanged?.Invoke(sid);
        }

        /// <summary>
        /// Merge a deserialized usage snapshot, treating zero as "not reported". The gateway ships
        /// a usage object on every runtime-info payload but leaves it empty for a session whose
        /// agent has not been built yet, and it omits the context fields unless a turn has run —
        /// both deserialize to zeros here, which would otherwise wipe counters and the context
        /// gauge that session.context_breakdown just filled in. Counters only ever grow, so
        /// ignoring zeros cannot hide a real value.
        /// </summary>
        private static UsageStats MergeUsage(UsageStats incoming, UsageStats current)
        {
            if (incoming == null)
                return current;
            if (current == null)
                return incoming;

            if (incoming.input != 0) current.input = incoming.input;
            if (incoming.output != 0) current.output = incoming.output;
            if (incoming.total != 0) current.total = incoming.total;
            if (incoming.calls != 0) current.calls = incoming.calls;
            if (incoming.context_max != 0) current.context_max = incoming.context_max;
            if (incoming.context_used != 0) current.context_used = incoming.context_used;
            if (incoming.context_percent != 0f) current.context_percent = incoming.context_percent;
            if (incoming.cost_usd != 0f) current.cost_usd = incoming.cost_usd;
            return current;
        }

        private void HandleSessionInfo(GatewayEvent evt)
        {
            if (evt.Payload == null) return;
            string sid = EventSessionId(evt);
            if (string.IsNullOrEmpty(sid)) return;

            var info = evt.Payload.ToObject<SessionRuntimeInfo>();
            if (info == null) return;

            // stored_session_id is the gateway's live session_key. When auto-compression rotates
            // it the runtime id is unchanged, so this is the only notice that the chat's persisted
            // key moved — alias it onto the same chat rather than letting the tip look like a new
            // conversation (and keep the runtime id prompt.submit uses in sync).
            // Only a SCOPED event can reconcile: an unscoped one was routed by the stream pin, so
            // its stored key must not be allowed to re-key whichever chat happens to be focused.
            if (!string.IsNullOrEmpty(evt.SessionId) && !string.IsNullOrEmpty(info.stored_session_id))
            {
                string reconciled = ReconcileSessionIds(evt.SessionId, info.stored_session_id);
                if (!string.IsNullOrEmpty(reconciled))
                    sid = reconciled;
            }

            ApplyRuntimeInfo(sid, info, evt.Payload);

            // cwd is a foreground/workspace concern — only the viewed session drives it.
            if (sid == ActiveSessionId && !string.IsNullOrEmpty(info.cwd))
            {
                _clientBridge.SetWorkspace(info.cwd);
                LastKnownCwd = info.cwd;
            }
            if (info.running.HasValue)
            {
                SetBusy(sid, info.running.Value);
                if (!info.running.Value)
                    SetAwaiting(sid, false);
            }
        }

        private void HandleMessageStart(GatewayEvent evt)
        {
            string sid = EventSessionId(evt);
            if (string.IsNullOrEmpty(sid)) return;
            SetBusy(sid, true);
            SetAwaiting(sid, true);
            OnStreamStarted?.Invoke(sid);
        }

        private void HandleMessageDelta(GatewayEvent evt)
        {
            if (evt.Payload == null) return;
            string sid = EventSessionId(evt);
            if (string.IsNullOrEmpty(sid)) return;

            string text = ExtractText(evt.Payload);
            if (!string.IsNullOrEmpty(text))
                OnDelta?.Invoke(sid, text);
        }

        private void HandleMessageComplete(GatewayEvent evt)
        {
            string sid = EventSessionId(evt);
            if (string.IsNullOrEmpty(sid)) return;

            string finalText = "";
            if (evt.Payload != null)
            {
                finalText = ExtractText(evt.Payload) ?? "";
            }

            if (string.IsNullOrWhiteSpace(finalText) && evt.Payload != null)
                NeonLogger.LogWarning("[Hermes] message.complete had no text payload: " + evt.Payload.ToString(Formatting.None));

            if (evt.Payload != null)
            {
                ApplyUsage(sid, evt.Payload);
            }

            OnComplete?.Invoke(sid, finalText);
            SetBusy(sid, false);
            SetAwaiting(sid, false);

            // message.complete carries the cumulative counters but not the post-turn prompt size,
            // so the context gauge would keep drifting on its local estimate until the chat is
            // reopened. session.context_breakdown is the exact source; ask for it once the turn has
            // settled, and only for the session the gauge actually renders. Fire-and-forget —
            // RequestContextBreakdown swallows its own failures and leaves the last good numbers.
            if (string.Equals(sid, ActiveSessionId, StringComparison.Ordinal))
                _ = RequestContextBreakdown(sid);
        }

        private void HandleReasoningDelta(GatewayEvent evt)
        {
            if (evt.Payload == null) return;
            string sid = EventSessionId(evt);
            if (string.IsNullOrEmpty(sid)) return;

            string text = ExtractText(evt.Payload);
            if (!string.IsNullOrEmpty(text))
                OnReasoningDelta?.Invoke(sid, text);
        }

        private void HandleMessageInterim(GatewayEvent evt)
        {
            // Interim assistant commentary (text alongside tool calls, or an attempted final
            // answer before a verify-on-stop nudge). Its text already streamed into the live
            // bubble via message.delta (Desktop gateway-event.ts), and companion finalizes that
            // single bubble on message.complete — so there is nothing extra to render. Resolve
            // the sid so the unscoped-stream pin stays consistent; the bookkeeping is the point.
            EventSessionId(evt);
        }

        private void HandleStatusUpdate(GatewayEvent evt)
        {
            // status.update surfaces phase changes (e.g. compacting) and background-process
            // notifications. Companion has no compaction UI; at least keep the pin consistent and
            // let listeners re-read runtime info for the owning session.
            string sid = EventSessionId(evt);
            if (string.IsNullOrEmpty(sid)) return;
            OnRuntimeInfoChanged?.Invoke(sid);
        }

        private void ApplyUsage(string sessionId, JToken payload)
        {
            if (string.IsNullOrEmpty(sessionId) || payload == null || payload.Type != JTokenType.Object)
                return;

            JToken usageToken = payload["usage"];
            if (usageToken == null || usageToken.Type != JTokenType.Object)
                return;

            string sid = DisplaySessionIdFor(sessionId);
            SessionRuntimeInfo rt;
            if (!_runtimeBySession.TryGetValue(sid, out rt) || rt == null)
            {
                rt = new SessionRuntimeInfo();
                _runtimeBySession[sid] = rt;
            }

            rt.usage = MergeUsage(usageToken, rt.usage);
            OnRuntimeInfoChanged?.Invoke(sid);
        }

        private void ApplyUsage(string sessionId, UsageStats usage)
        {
            if (string.IsNullOrEmpty(sessionId) || usage == null)
                return;

            string sid = DisplaySessionIdFor(sessionId);
            SessionRuntimeInfo rt;
            if (!_runtimeBySession.TryGetValue(sid, out rt) || rt == null)
            {
                rt = new SessionRuntimeInfo();
                _runtimeBySession[sid] = rt;
            }

            // session.usage answers with the cumulative counters only — merging keeps the context
            // gauge that session.context_breakdown filled in instead of zeroing it.
            rt.usage = MergeUsage(usage, rt.usage);
            OnRuntimeInfoChanged?.Invoke(sid);
        }

        private void ApplyContextBreakdown(string sessionId, ContextBreakdown breakdown)
        {
            if (string.IsNullOrEmpty(sessionId) || breakdown == null)
                return;

            string sid = DisplaySessionIdFor(sessionId);
            SessionRuntimeInfo rt;
            if (!_runtimeBySession.TryGetValue(sid, out rt) || rt == null)
            {
                rt = new SessionRuntimeInfo();
                _runtimeBySession[sid] = rt;
            }

            UsageStats usage = rt.usage ?? new UsageStats();

            // Zero means "not reported", exactly as in MergeUsage. A session whose agent has not
            // been built yet answers session.context_breakdown with an all-zero snapshot
            // (tui_gateway falls back to the usage mirror, which has no context fields until a turn
            // has run) — writing those through would blank a gauge that session.info or
            // message.complete had already filled in.
            if (breakdown.context_max > 0)
                usage.context_max = breakdown.context_max;

            // The backend derives context_used from the compressor's measured prompt size and only
            // falls back to the summed category estimate when it has none; mirror that ordering so
            // a pre-first-turn breakdown still shows its estimate instead of nothing.
            int contextUsed = breakdown.context_used > 0 ? breakdown.context_used : breakdown.estimated_total;
            if (contextUsed > 0)
                usage.context_used = contextUsed;

            if (breakdown.context_percent > 0f)
                usage.context_percent = breakdown.context_percent;

            rt.usage = usage;
            OnRuntimeInfoChanged?.Invoke(sid);
        }

        private static UsageStats MergeUsage(JToken usageToken, UsageStats current)
        {
            UsageStats merged = current != null
                ? new UsageStats
                {
                    input = current.input,
                    output = current.output,
                    total = current.total,
                    calls = current.calls,
                    context_max = current.context_max,
                    context_used = current.context_used,
                    context_percent = current.context_percent,
                    cost_usd = current.cost_usd
                }
                : new UsageStats();

            SetUsageInt(usageToken, "input", value => merged.input = value);
            SetUsageInt(usageToken, "output", value => merged.output = value);
            SetUsageInt(usageToken, "total", value => merged.total = value);
            SetUsageInt(usageToken, "calls", value => merged.calls = value);
            SetUsageInt(usageToken, "context_max", value => merged.context_max = value);
            SetUsageInt(usageToken, "context_used", value => merged.context_used = value);
            SetUsageFloat(usageToken, "context_percent", value => merged.context_percent = value);
            SetUsageFloat(usageToken, "cost_usd", value => merged.cost_usd = value);
            return merged;
        }

        private static void SetUsageInt(JToken token, string key, Action<int> set)
        {
            if (token == null || token.Type != JTokenType.Object)
                return;

            JToken valueToken = token[key];
            if (valueToken == null || valueToken.Type == JTokenType.Null)
                return;

            int value;
            if (int.TryParse(valueToken.ToString(), out value))
                set(value);
        }

        private static void SetUsageFloat(JToken token, string key, Action<float> set)
        {
            if (token == null || token.Type != JTokenType.Object)
                return;

            JToken valueToken = token[key];
            if (valueToken == null || valueToken.Type == JTokenType.Null)
                return;

            float value;
            if (float.TryParse(valueToken.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                set(value);
        }

        private static string ExtractText(JToken token)
        {
            if (token == null)
                return null;

            if (token.Type == JTokenType.String)
                return token.Value<string>();

            string direct = FirstNonEmpty(
                TokenString(token, "text"),
                TokenString(token, "rendered"),
                TokenString(token, "content"),
                TokenString(token, "delta"),
                TokenString(token, "message"),
                TokenString(token, "output"),
                TokenString(token, "result"),
                TokenString(token, "final"),
                TokenString(token, "response"),
                TokenString(token, "body"));
            if (!string.IsNullOrEmpty(direct))
                return direct;

            if (token.Type == JTokenType.Array)
            {
                foreach (JToken child in token.Children())
                {
                    string nested = ExtractText(child);
                    if (!string.IsNullOrEmpty(nested))
                        return nested;
                }
            }

            if (token.Type != JTokenType.Object)
                return null;

            string[] keys = { "message", "messages", "output", "result", "final", "response", "content", "body", "data" };
            for (int i = 0; i < keys.Length; i++)
            {
                JToken child = token[keys[i]];
                if (child == null)
                    continue;

                string nested = ExtractText(child);
                if (!string.IsNullOrEmpty(nested))
                    return nested;
            }

            return null;
        }

        private static string TokenString(JToken token, string key)
        {
            if (token == null || string.IsNullOrEmpty(key))
                return null;

            if (token.Type != JTokenType.Object)
                return null;

            JToken child = token[key];
            if (child == null || child.Type != JTokenType.String)
                return null;

            return child.Value<string>();
        }

        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null)
                return null;

            for (int i = 0; i < values.Length; i++)
            {
                if (!string.IsNullOrEmpty(values[i]))
                    return values[i];
            }

            return null;
        }

        private void HandleToolStart(GatewayEvent evt)
        {
            HandleToolEvent(evt, ToolCallStatus.Running);
        }

        private void HandleToolProgress(GatewayEvent evt)
        {
            HandleToolEvent(evt, ToolCallStatus.Running);
        }

        private void HandleToolComplete(GatewayEvent evt)
        {
            HandleToolEvent(evt, ToolCallStatus.Complete);
        }

        private void HandleToolEvent(GatewayEvent evt, ToolCallStatus status)
        {
            if (evt.Payload == null) return;
            string sid = EventSessionId(evt);
            if (string.IsNullOrEmpty(sid)) return;

            var payload = evt.Payload.ToObject<ToolEventPayload>();
            if (payload == null) return;

            // Desktop toolId(payload): tool_id || tool_call_id || id
            string toolId = FirstNonEmpty(
                payload.tool_id,
                TokenString(evt.Payload, "tool_call_id"),
                TokenString(evt.Payload, "id"));

            // Desktop upsertToolPart: on complete, isError = Boolean(payload.error); result.error
            // is also carried into the tool result for the expanded row.
            string errorText = status == ToolCallStatus.Complete
                ? ExtractToolError(evt.Payload, payload.error)
                : null;
            string details = null;
            if (status == ToolCallStatus.Complete)
            {
                details = ExtractToolDetails(evt.Payload);
                if (string.IsNullOrWhiteSpace(details) && !string.IsNullOrWhiteSpace(errorText))
                    details = errorText;
            }
            // Progress may carry a short preview/context only — still surface as details when no
            // prior result body exists so the expanded row is not empty mid-run.
            else if (status == ToolCallStatus.Running)
            {
                string progressPreview = payload.context ?? payload.preview;
                if (!string.IsNullOrWhiteSpace(progressPreview))
                    details = LimitToolDetails(progressPreview);
            }

            OnToolUpdate?.Invoke(sid, new ToolCallUpdate
            {
                name = payload.name,
                toolId = toolId,
                status = status,
                preview = payload.context ?? payload.preview,
                inlineDiff = payload.inline_diff,
                details = details,
                emoji = payload.emoji,
                error = errorText
            });
        }

        private static string ExtractToolError(JToken token, object payloadError)
        {
            string fromPayload = FormatToolError(payloadError);
            if (!string.IsNullOrWhiteSpace(fromPayload))
                return fromPayload;

            if (token == null || token.Type != JTokenType.Object)
                return null;

            JToken errorToken = token["error"];
            if (errorToken == null || errorToken.Type == JTokenType.Null)
                return null;

            return FormatToolErrorToken(errorToken);
        }

        private static string FormatToolError(object error)
        {
            if (error == null)
                return null;

            if (error is bool)
            {
                bool flag = (bool)error;
                return flag ? "error" : null;
            }

            if (error is string)
            {
                string text = (string)error;
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            }

            string asText = error.ToString();
            if (string.IsNullOrWhiteSpace(asText) || string.Equals(asText, "False", StringComparison.OrdinalIgnoreCase))
                return null;
            if (string.Equals(asText, "True", StringComparison.OrdinalIgnoreCase))
                return "error";
            return asText.Trim();
        }

        private static string FormatToolErrorToken(JToken errorToken)
        {
            if (errorToken == null || errorToken.Type == JTokenType.Null)
                return null;

            if (errorToken.Type == JTokenType.Boolean)
                return errorToken.Value<bool>() ? "error" : null;

            if (errorToken.Type == JTokenType.String)
            {
                string text = errorToken.Value<string>();
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            }

            string extracted = ExtractText(errorToken);
            if (!string.IsNullOrWhiteSpace(extracted))
                return extracted.Trim();

            string raw = errorToken.ToString(Formatting.None);
            return string.IsNullOrWhiteSpace(raw) ? "error" : LimitToolDetails(raw);
        }

        private static string ExtractToolDetails(JToken token)
        {
            if (token == null || token.Type != JTokenType.Object)
                return null;

            // Prefer human-readable result fields; error is handled separately for failed status
            // but may also surface as the expanded body when no result is present.
            string[] keys = { "result", "output", "content", "text", "rendered", "message", "response", "data", "summary" };
            for (int i = 0; i < keys.Length; i++)
            {
                JToken child = token[keys[i]];
                if (child == null)
                    continue;

                string text = ExtractText(child);
                if (!string.IsNullOrWhiteSpace(text))
                    return LimitToolDetails(text);
            }

            return null;
        }

        private static string LimitToolDetails(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= 30000)
                return text;

            return text.Substring(0, 30000) + "\n... [tool output truncated]";
        }

        private void HandleClarifyRequest(GatewayEvent evt)
        {
            if (evt.Payload == null) return;
            string sid = EventSessionId(evt);

            var payload = evt.Payload.ToObject<ClarifyEventPayload>();
            if (payload == null) return;

            OnClarifyRequest?.Invoke(sid, new ClarifyRequest
            {
                requestId = payload.request_id,
                question = payload.question,
                choices = payload.choices
            });
        }

        private void HandleTerminalExecute(GatewayEvent evt)
        {
            // Companion-only extension. Once the backend has rejected terminal.respond there is
            // nowhere to deliver the result, so don't run the command at all.
            if (_terminalRespondUnsupported) return;
            if (!IsActiveEvent(evt)) return;
            if (evt.Payload == null) return;
            var payload = evt.Payload.ToObject<TerminalExecutePayload>();
            if (payload == null) return;
            
            OnTerminalExecute?.Invoke(new TerminalExecuteRequest
            {
                RequestId = payload.request_id,
                Command = payload.command ?? string.Empty,
                TimeoutMs = payload.timeout_ms > 0 ? payload.timeout_ms : 30000,
                Persistent = payload.persistent
            });
        }

        // read_terminal tool. Unlike terminal.execute this is NOT gated on the active session:
        // the backend blocks on terminal.read.respond regardless of which chat is focused, and
        // Companion has a single terminal, so we always answer (Desktop gateway-event.ts does the
        // same — it reads request_id/start/count and responds immediately). start/count are
        // optional; absent -> -1 so the UI layer applies its default window (visible screen).
        private void HandleTerminalReadRequest(GatewayEvent evt)
        {
            if (evt.Payload == null) return;
            var payload = evt.Payload as JObject;
            if (payload == null) return;

            var requestIdToken = payload["request_id"];
            string requestId = requestIdToken != null ? requestIdToken.ToObject<string>() : null;
            if (string.IsNullOrEmpty(requestId))
                return;

            int start = -1;
            var startToken = payload["start"];
            if (startToken != null && startToken.Type == JTokenType.Integer)
                start = startToken.ToObject<int>();

            int count = -1;
            var countToken = payload["count"];
            if (countToken != null && countToken.Type == JTokenType.Integer)
                count = countToken.ToObject<int>();

            OnTerminalReadRequest?.Invoke(new TerminalReadRequest
            {
                RequestId = requestId,
                Start = start,
                Count = count
            });
        }

        // Live chunk from a backend `terminal(background=true)` process. Unlike terminal.execute
        // this is NOT gated on the focused chat: the backend routes it to the session that owns the
        // process, and Companion multiplexes every session over one socket, so gating on the
        // foreground would silently drop a background chat's output. Desktop keys purely on
        // process_id (it has one global tab list); Companion keeps the owning session too so the
        // backlog can be scoped per chat.
        private void HandleAgentTerminalOutput(GatewayEvent evt)
        {
            if (evt == null || evt.Payload == null) return;

            var payload = ReadAgentTerminalPayload(evt);
            if (payload == null || string.IsNullOrEmpty(payload.process_id)) return;
            if (string.IsNullOrEmpty(payload.chunk)) return;

            string sid = ResolveAgentTerminalSession(evt, payload.process_id);
            if (string.IsNullOrEmpty(sid)) return;

            _agentTerminals.Append(sid, payload.process_id, payload.chunk);
            OnAgentTerminalOutput?.Invoke(sid, payload.process_id, payload.chunk);
        }

        // close_terminal tool: drop the read-only view of a background process WITHOUT killing it.
        // The backend emits an empty session_id when the process is already gone, so the owner is
        // resolved from the process id we bound on the first chunk.
        private void HandleTerminalClose(GatewayEvent evt)
        {
            if (evt == null || evt.Payload == null) return;

            var payload = ReadAgentTerminalPayload(evt);
            if (payload == null || string.IsNullOrEmpty(payload.process_id)) return;

            string sid = ResolveAgentTerminalSession(evt, payload.process_id);
            bool hadBuffer = _agentTerminals.Close(sid, payload.process_id);
            NeonLogger.Log("[Hermes] terminal.close for process " + payload.process_id
                + " (session " + (string.IsNullOrEmpty(sid) ? "<none>" : sid)
                + (hadBuffer ? ", buffer dropped)" : ", nothing buffered)"));
            OnAgentTerminalClose?.Invoke(sid, payload.process_id);
        }

        private AgentTerminalPayload ReadAgentTerminalPayload(GatewayEvent evt)
        {
            try
            {
                return evt.Payload.ToObject<AgentTerminalPayload>();
            }
            catch (Exception ex)
            {
                // A malformed payload must never kill the dispatch loop — every other event on the
                // socket would go with it.
                Debug.LogWarning("[Hermes] Invalid " + evt.Type + " payload: " + ex.Message);
                return null;
            }
        }

        // An explicit session_id (translated runtime -> display) always wins and re-binds the
        // process, which is what makes a reconnect's freshly minted runtime id follow the same
        // chat. Unscoped events fall back to the process's remembered owner, and only a
        // never-before-seen process falls through to the focused chat.
        private string ResolveAgentTerminalSession(GatewayEvent evt, string processId)
        {
            string scoped = string.IsNullOrEmpty(evt.SessionId) ? null : DisplaySessionIdFor(evt.SessionId);
            return _agentTerminals.ResolveOwner(processId, scoped, ActiveSessionId);
        }

        private void HandleApprovalRequest(GatewayEvent evt)
        {
            if (evt.Payload == null) return;
            string sid = EventSessionId(evt);

            var payload = evt.Payload.ToObject<ApprovalEventPayload>();
            if (payload == null) return;

            OnApprovalRequest?.Invoke(sid, new ApprovalRequest
            {
                description = payload.description ?? "dangerous command",
                command = payload.command,
                type = "approval",
                choices = payload.choices,
                allowPermanent = payload.allow_permanent,
                smartDenied = payload.smart_denied
            });
        }

        private void HandleSudoRequest(GatewayEvent evt)
        {
            if (evt.Payload == null) return;
            string sid = EventSessionId(evt);

            var payload = evt.Payload.ToObject<ClarifyEventPayload>();
            if (payload == null || string.IsNullOrEmpty(payload.request_id)) return;

            // Sudo needs a captured PASSWORD, not an approve/deny choice: the agent is blocked on
            // sudo.respond {request_id, password}. Route it through the masked secret-input surface
            // (isSudo flags the responder to answer via sudo.respond, not secret.respond). Answering
            // through the generic approval path would leave the agent blocked forever.
            OnSecretRequest?.Invoke(sid, new SecretRequest
            {
                requestId = payload.request_id,
                prompt = payload.question,
                isSudo = true
            });
        }

        // sudo.expire / secret.expire — the server stopped waiting (tui_gateway _block timeout) and
        // dropped the pending request. Both carry only {request_id}; listeners match on it and tear
        // down the matching capture UI without answering (TUI createGatewayEventHandler parity).
        private void HandleSecretExpire(GatewayEvent evt)
        {
            if (evt.Payload == null) return;
            string sid = EventSessionId(evt);

            var payload = evt.Payload.ToObject<ClarifyEventPayload>();
            if (payload == null || string.IsNullOrEmpty(payload.request_id)) return;

            OnSecretExpire?.Invoke(sid, payload.request_id);
        }

        private void HandleSecretRequest(GatewayEvent evt)
        {
            if (evt.Payload == null) return;
            string sid = EventSessionId(evt);

            var payload = evt.Payload.ToObject<SecretEventPayload>();
            if (payload == null || string.IsNullOrEmpty(payload.request_id)) return;

            // Surfaced on its own event (not OnApprovalRequest): the agent is blocked on
            // secret.respond {request_id, value} — a text value, not an approve/deny choice.
            OnSecretRequest?.Invoke(sid, new SecretRequest
            {
                requestId = payload.request_id,
                envVar = payload.env_var,
                prompt = payload.prompt
            });
        }

        private void HandleSessionTitle(GatewayEvent evt)
        {
            if (evt.Payload == null) return;

            var payload = evt.Payload.ToObject<SessionTitlePayload>();
            if (payload == null) return;

            string title = payload.title != null ? payload.title.Trim() : null;
            if (string.IsNullOrEmpty(title)) return;

            // session.title carries the STORED/display id in its payload (titler runs async after
            // the turn). Fall back to the event's own routing when it is absent.
            string sid = !string.IsNullOrEmpty(payload.session_id)
                ? DisplaySessionIdFor(payload.session_id)
                : EventSessionId(evt);
            if (string.IsNullOrEmpty(sid)) return;

            OnSessionTitle?.Invoke(sid, title);
        }

        private void HandleBackgroundComplete(GatewayEvent evt)
        {
            // Optional event: a background session finished. Companion has no background-session
            // panel yet — resolve the sid (keeps pin bookkeeping consistent) and log so it is not
            // dropped silently. Must not crash on an unexpected/absent payload.
            string sid = EventSessionId(evt);
            NeonLogger.Log("[Hermes] background.complete for session " + (string.IsNullOrEmpty(sid) ? "<none>" : sid));
        }

        private void HandleReviewSummary(GatewayEvent evt)
        {
            // A background self-improvement review persisted a change to memory/skills and emitted a
            // pre-formatted summary line ("💾 Self-improvement review: …"). The CLI/TUI print it as a
            // persistent system line; without a consumer the change would happen silently. Python
            // always scopes this to the reviewed session's sid and sends a plain-string {"text": …}.
            // Surface it via OnReviewSummary so the UI can pin it into that session's transcript.
            if (evt == null || evt.Payload == null) return;
            string sid = EventSessionId(evt);
            if (string.IsNullOrEmpty(sid)) return;

            string text = ExtractText(evt.Payload);
            if (string.IsNullOrEmpty(text)) return;

            OnReviewSummary?.Invoke(sid, text.Trim());
        }

        private void HandleWildcardEvent(GatewayEvent evt)
        {
            if (evt == null || string.IsNullOrEmpty(evt.Type))
                return;
            // Only subagent.* is routed here; every other type has a dedicated handler.
            if (evt.Type.StartsWith(GatewayEvents.SubagentPrefix))
                HandleSubagentEvent(evt);
        }

        private void HandleSubagentEvent(GatewayEvent evt)
        {
            // subagent.* describes background/async work. An unscoped event (no session_id) must
            // NEVER attach to the focused chat (Desktop gatewayEventRequiresSessionId → drop). A
            // scoped event carries its owning session's id; companion has no subagent panel yet, so
            // log a structured line under the CORRECT session rather than dropping it silently or
            // misattributing it to whichever chat is focused.
            if (string.IsNullOrEmpty(evt.SessionId))
                return; // unscoped subagent.* → drop

            string sid = DisplaySessionIdFor(evt.SessionId);
            string preview = evt.Payload != null ? evt.Payload.ToString(Formatting.None) : "";
            if (preview.Length > 500)
                preview = preview.Substring(0, 500) + " …";
            NeonLogger.Log("[Hermes] " + evt.Type + " (session " + sid + "): " + preview);
        }

        private void HandleError(GatewayEvent evt)
        {
            if (evt.Payload == null) return;
            string sid = EventSessionId(evt);

            var payload = evt.Payload.ToObject<ErrorPayload>();
            string message = payload?.message ?? "Unknown error";
            if (!string.IsNullOrEmpty(sid))
            {
                SetBusy(sid, false);
                SetAwaiting(sid, false);
            }
            OnError?.Invoke(sid, message);
        }

        private void HandleGatewayStateChange(ConnectionState state)
        {
            TransportState ts;
            switch (state)
            {
                case ConnectionState.Open:
                    ts = TransportState.Connected;
                    break;
                case ConnectionState.Connecting:
                    ts = TransportState.Connecting;
                    break;
                case ConnectionState.Closed:
                    ts = TransportState.Disconnected;
                    break;
                case ConnectionState.Error:
                    ts = TransportState.Error;
                    break;
                default:
                    ts = TransportState.Disconnected;
                    break;
            }
            // A fresh socket may be a different gateway (profile switch, upgraded backend), so the
            // terminal.respond capability latch has to be re-probed instead of staying disabled.
            // Agent-terminal backlogs are deliberately KEPT: the backend processes outlive the
            // socket, and their chunks resume against the same chat once the ids are rebound.
            if (ts == TransportState.Connected)
                _terminalRespondUnsupported = false;

            OnStateChanged?.Invoke(ts);

            // When the WebSocket drops mid-generation, ChatService is blocked on
            // _hermesGenerationComplete (a TaskCompletionSource). Without firing OnError
            // here the TCS never resolves and the UI hangs on "Выполнение..." until the
            // 5-minute safety timeout. TrySetResult is safe even when no generation is active.
            if (ts == TransportState.Disconnected || ts == TransportState.Error)
            {
                // Connection-level error: null session id signals "fail every in-flight stream"
                // so ChatService unblocks all pending generations, not just the foreground one.
                // Busy/awaiting are re-established from the gateway by the reconnect's
                // rehydration; the id maps are deliberately KEPT, because that is what the
                // rehydration rebinds against.
                _busyBySession.Clear();
                _awaitingBySession.Clear();
                // The pin belongs to the turn that was streaming on the dead socket. Releasing it
                // stops a post-reconnect unscoped event from being attributed to that turn.
                _unscopedStreamSessionId = null;
                OnError?.Invoke(null, "Hermes connection lost (" + state + ")");
            }
        }

        // === Dispose ===

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _agentTerminals.Clear();
            _clientBridge?.Dispose();
            _gateway?.Dispose();
        }
    }
}
