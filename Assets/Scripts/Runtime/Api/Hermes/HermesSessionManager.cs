// HermesSessionManager.cs - Session lifecycle + IChatTransport implementation
// Wraps HermesGateway with session management, streaming, tool calls, clarify.

using System;
using System.Collections.Generic;
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
        public long? ended_at;
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
        public string branch;
        public bool? running;
        public string personality;
        public UsageStats usage;
    }

    [Serializable]
    public class UsageStats
    {
        public int input;
        public int output;
        public int total;
        public int calls;
        public double? cost_usd;
        public int context_max;
        public int context_used;
        public int context_percent;
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

    public class TerminalExecuteRequest
    {
        public string RequestId;
        public string Command;
        public int TimeoutMs;
        public bool Persistent;
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
            ActiveSessionId = DisplaySessionIdFor(sessionId);
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
        public event Action<string, string> OnError;
        public event Action<TransportState> OnStateChanged;

        public event Action<TerminalExecuteRequest> OnTerminalExecute;

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

        public async Task Connect(string url, string token = null)
        {
            string wsUrl = url;
            if (!string.IsNullOrEmpty(token))
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
                await _gateway.Request<object>(
                    RpcMethods.PromptSubmit,
                    new { session_id = runtimeSessionId, text });
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
        /// Switch model for current session via gateway slash command.
        /// </summary>
        public async Task<bool> SwitchModelAsync(string modelId, string providerSlug = null)
        {
            string cmd = $"/model {modelId}";
            if (!string.IsNullOrEmpty(providerSlug))
                cmd += $" --provider {providerSlug}";

            var result = await _gateway.Request<object>(
                "slash.exec",
                new { session_id = RuntimeSessionIdFor(ActiveSessionId), command = cmd }
            );
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

        public async Task RespondToApproval(string sessionId, bool approved)
        {
            string choice = approved ? "once" : "deny";
            string sid = string.IsNullOrEmpty(sessionId) ? ActiveSessionId : sessionId;
            await _gateway.Request<object>(
                RpcMethods.ApprovalRespond,
                new { session_id = RuntimeSessionIdFor(sid), choice });
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
            _gateway.On(GatewayEvents.MessageComplete, HandleMessageComplete);
            _gateway.On(GatewayEvents.ReasoningDelta, HandleReasoningDelta);
            _gateway.On(GatewayEvents.ToolStart, HandleToolStart);
            _gateway.On(GatewayEvents.ToolProgress, HandleToolProgress);
            _gateway.On(GatewayEvents.ToolComplete, HandleToolComplete);
            _gateway.On(GatewayEvents.ClarifyRequest, HandleClarifyRequest);
            _gateway.On(GatewayEvents.ApprovalRequest, HandleApprovalRequest);
            _gateway.On(GatewayEvents.SudoRequest, HandleSudoRequest);
            _gateway.On(GatewayEvents.TerminalExecute, HandleTerminalExecute);
            _gateway.On(GatewayEvents.Error, HandleError);
        }

        private bool IsActiveEvent(GatewayEvent evt)
        {
            if (evt == null)
                return false;
            var sessionId = DisplaySessionIdFor(evt.SessionId ?? ActiveSessionId);
            return sessionId == ActiveSessionId;
        }

        /// <summary>Resolve the session id an event belongs to, falling back to the foreground.</summary>
        private string EventSessionId(GatewayEvent evt)
        {
            if (evt != null && !string.IsNullOrEmpty(evt.SessionId))
                return DisplaySessionIdFor(evt.SessionId);
            return ActiveSessionId;
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

            _runtimeBySession[sid] = info;
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

            OnComplete?.Invoke(sid, finalText);
            SetBusy(sid, false);
            SetAwaiting(sid, false);

            if (evt.Payload != null)
            {
                UsageStats usage = ExtractUsage(evt.Payload);
                SessionRuntimeInfo rt = RuntimeInfoFor(sid);
                if (usage != null && rt != null)
                    rt.usage = usage;
            }
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

        private static UsageStats ExtractUsage(JToken token)
        {
            if (token == null || token.Type != JTokenType.Object)
                return null;

            JToken usageToken = token["usage"];
            if (usageToken == null || usageToken.Type != JTokenType.Object)
                return null;

            try
            {
                return usageToken.ToObject<UsageStats>();
            }
            catch
            {
                return null;
            }
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

            OnToolUpdate?.Invoke(sid, new ToolCallUpdate
            {
                name = payload.name,
                toolId = payload.tool_id,
                status = status,
                preview = payload.context ?? payload.preview,
                inlineDiff = payload.inline_diff,
                details = status == ToolCallStatus.Complete ? ExtractToolDetails(evt.Payload) : null,
                emoji = payload.emoji
            });
        }

        private static string ExtractToolDetails(JToken token)
        {
            if (token == null || token.Type != JTokenType.Object)
                return null;

            string[] keys = { "result", "output", "content", "text", "rendered", "message", "response", "data" };
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

        private void HandleApprovalRequest(GatewayEvent evt)
        {
            if (evt.Payload == null) return;
            string sid = EventSessionId(evt);

            var payload = evt.Payload.ToObject<ClarifyEventPayload>();
            if (payload == null) return;

            OnApprovalRequest?.Invoke(sid, new ApprovalRequest
            {
                requestId = payload.request_id,
                description = payload.question,
                type = "approval"
            });
        }

        private void HandleSudoRequest(GatewayEvent evt)
        {
            if (evt.Payload == null) return;
            string sid = EventSessionId(evt);

            var payload = evt.Payload.ToObject<ClarifyEventPayload>();
            if (payload == null) return;

            OnApprovalRequest?.Invoke(sid, new ApprovalRequest
            {
                requestId = payload.request_id,
                description = payload.question,
                type = "sudo"
            });
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
