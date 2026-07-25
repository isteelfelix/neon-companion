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
        public string session_id;
        public string stored_session_id;
        public SessionInfo info;
        public int message_count;
    }

    [Serializable]
    public class SessionResumeResponse
    {
        public string session_id;
        public string stored_session_id;
        public string resumed;
        public SessionMessage[] messages;
        public int message_count;
        public SessionInfo info;
    }

    [Serializable]
    public class SessionInfo
    {
        public string id;
        public string title;
        public string model;
        public bool is_active;
        public int message_count;
        public int input_tokens;
        public int output_tokens;
        public int tool_call_count;
        public long started_at;
        public long last_active;
        public string preview;
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

    // === HermesSessionManager ===

    public class HermesSessionManager : IChatTransport
    {
        private readonly HermesGateway _gateway;
        private readonly HermesClientBridge _clientBridge;
        private bool _disposed;

        // Foreground/last-resumed session hints. These no longer filter events — the transport
        // multiplexes every session. ActiveSessionId is the session the UI currently views; it
        // only drives RuntimeInfo and the foreground-only handlers (clarify/approval/terminal).
        public string ActiveSessionId { get; private set; }
        public string StoredSessionId { get; private set; }

        // Per-session generation state, keyed by the display/persisted session id. Runtime ids
        // from Hermes are translated at the transport boundary.
        private readonly Dictionary<string, bool> _busyBySession = new Dictionary<string, bool>();
        private readonly Dictionary<string, bool> _awaitingBySession = new Dictionary<string, bool>();
        private readonly Dictionary<string, SessionRuntimeInfo> _runtimeBySession = new Dictionary<string, SessionRuntimeInfo>();
        private readonly Dictionary<string, string> _runtimeByDisplaySession = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _displayByRuntimeSession = new Dictionary<string, string>();

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

        private void ForgetSessionIds(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                return;

            string display = DisplaySessionIdFor(sessionId);
            string runtime = RuntimeSessionIdFor(sessionId);
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
        public event Action<string, string> OnSessionTitle;
        // (sid, text) — a background self-improvement review saved to memory/skills. Surfaced as a
        // persistent system line in the transcript (Desktop review.summary parity).
        public event Action<string, string> OnReviewSummary;
        public event Action<string, string> OnError;
        public event Action<TransportState> OnStateChanged;
        public event Action<string> OnRuntimeInfoChanged;

        public event Action<TerminalExecuteRequest> OnTerminalExecute;
        public event Action<TerminalReadRequest> OnTerminalReadRequest;

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
            return Connect(url, token, null, null);
        }

        /// <summary>
        /// Open the gateway socket. <paramref name="profile"/> is the Hermes backend profile the
        /// whole connection is scoped to: the gateway reads ?profile=&lt;name&gt; off the URL, so
        /// session.create and every later RPC on this socket run inside that profile.
        /// </summary>
        public async Task Connect(string url, string token, string ticket, string profile = null)
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

            // Profile rides alongside the auth parameter (both modes) — never replaces it.
            if (!string.IsNullOrEmpty(profile))
            {
                string profileSeparator = wsUrl.Contains("?") ? "&" : "?";
                wsUrl = wsUrl + profileSeparator + "profile=" + Uri.EscapeDataString(profile);
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
        }

        public async Task Disconnect()
        {
            if (ActiveSessionId != null)
            {
                try { await CloseSession(); }
                catch (Exception ex) { Debug.LogWarning("[Hermes] Close session on disconnect: " + ex.Message); }
            }
            await _gateway.Close();
        }

        // === IChatTransport: Messaging ===

        public async Task SendMessage(string sessionId, string text)
        {
            if (string.IsNullOrEmpty(sessionId))
                throw new InvalidOperationException("No session id");

            string displaySessionId = DisplaySessionIdFor(sessionId);
            string runtimeSessionId = RuntimeSessionIdFor(sessionId);

            if (IsSessionBusy(displaySessionId))
                throw new InvalidOperationException("Session is busy. Wait for the current response to finish.");

            try
            {
                // prompt.submit is effectively fire-and-forget: turn completion is signalled by
                // message.complete/error stream events, not this ack. Pass the long timeout so a
                // long-running turn does not surface a spurious "request timed out"
                // (Desktop PROMPT_SUBMIT_REQUEST_TIMEOUT_MS = 1_800_000).
                await _gateway.Request<object>(
                    RpcMethods.PromptSubmit,
                    new { session_id = runtimeSessionId, text },
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

        public async Task<SessionCreateResponse> CreateSession(string cwd = null, string title = null)
        {
            var result = await _gateway.Request<SessionCreateResponse>(
                RpcMethods.SessionCreate,
                new { cols = 96, cwd, title });

            string displaySessionId = !string.IsNullOrEmpty(result.stored_session_id)
                ? result.stored_session_id
                : result.session_id;
            RememberSessionIds(result.session_id, displaySessionId);

            ActiveSessionId = displaySessionId;
            StoredSessionId = displaySessionId;

            if (result.info != null)
                ApplySessionInfo(result.info);

            NeonLogger.Log("[Hermes] Session created: " + result.session_id);
            return result;
        }

        public async Task<SessionResumeResponse> ResumeSession(string sessionId)
        {
            var result = await _gateway.Request<SessionResumeResponse>(
                RpcMethods.SessionResume,
                new { session_id = sessionId });

            string displaySessionId = !string.IsNullOrEmpty(result.stored_session_id)
                ? result.stored_session_id
                : sessionId;
            RememberSessionIds(result.session_id, displaySessionId);

            ActiveSessionId = displaySessionId;
            StoredSessionId = displaySessionId;

            if (result.info != null)
                ApplySessionInfo(result.info);

            NeonLogger.Log("[Hermes] Session resumed: " + sessionId);
            return result;
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

        public async Task RespondToTerminal(string requestId, ProcessResult result, long? durationMs = null)
        {
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

            await _gateway.Request<object>(RpcMethods.TerminalRespond, payload);
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
            _gateway.On(GatewayEvents.SessionTitle, HandleSessionTitle);
            _gateway.On(GatewayEvents.BackgroundComplete, HandleBackgroundComplete);
            _gateway.On(GatewayEvents.ReviewSummary, HandleReviewSummary);
            _gateway.On(GatewayEvents.TerminalExecute, HandleTerminalExecute);
            _gateway.On(GatewayEvents.TerminalReadRequest, HandleTerminalReadRequest);
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

        private void ApplySessionInfo(SessionInfo info)
        {
            if (info == null || string.IsNullOrEmpty(ActiveSessionId))
                return;

            _runtimeBySession[ActiveSessionId] = new SessionRuntimeInfo
            {
                model = info.model,
                running = info.is_active,
                usage = new UsageStats
                {
                    input = info.input_tokens,
                    output = info.output_tokens,
                    total = info.input_tokens + info.output_tokens,
                    calls = info.tool_call_count
                }
            };
        }

        private void HandleSessionInfo(GatewayEvent evt)
        {
            if (evt.Payload == null) return;
            string sid = EventSessionId(evt);
            if (string.IsNullOrEmpty(sid)) return;

            var info = evt.Payload.ToObject<SessionRuntimeInfo>();
            if (info == null) return;

            SessionRuntimeInfo previous;
            _runtimeBySession.TryGetValue(sid, out previous);
            JToken usageToken = evt.Payload.Type == JTokenType.Object ? evt.Payload["usage"] : null;
            if (usageToken != null && usageToken.Type == JTokenType.Object)
                info.usage = MergeUsage(usageToken, previous != null ? previous.usage : null);
            else if (info.usage == null && previous != null)
                info.usage = previous.usage;

            _runtimeBySession[sid] = info;
            OnRuntimeInfoChanged?.Invoke(sid);
            // cwd is a foreground/workspace concern — only the viewed session drives it.
            if (sid == ActiveSessionId && !string.IsNullOrEmpty(info.cwd))
                _clientBridge.SetWorkspace(info.cwd);
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

            rt.usage = usage;
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
            if (breakdown.context_max > 0)
                usage.context_max = breakdown.context_max;
            if (breakdown.context_used >= 0)
                usage.context_used = breakdown.context_used;
            if (breakdown.context_percent >= 0)
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
            OnStateChanged?.Invoke(ts);

            // When the WebSocket drops mid-generation, ChatService is blocked on
            // _hermesGenerationComplete (a TaskCompletionSource). Without firing OnError
            // here the TCS never resolves and the UI hangs on "Выполнение..." until the
            // 5-minute safety timeout. TrySetResult is safe even when no generation is active.
            if (ts == TransportState.Disconnected || ts == TransportState.Error)
            {
                // Connection-level error: null session id signals "fail every in-flight stream"
                // so ChatService unblocks all pending generations, not just the foreground one.
                _busyBySession.Clear();
                _awaitingBySession.Clear();
                OnError?.Invoke(null, "Hermes connection lost (" + state + ")");
            }
        }

        // === Dispose ===

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _clientBridge?.Dispose();
            _gateway?.Dispose();
        }
    }
}
