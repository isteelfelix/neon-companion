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

        // Active session state
        public string ActiveSessionId { get; private set; }
        public string StoredSessionId { get; private set; }
        public bool Busy { get; private set; }
        public bool AwaitingResponse { get; private set; }
        public SessionRuntimeInfo RuntimeInfo { get; private set; }

        // === IChatTransport ===

        public bool IsConnected => _gateway.State == ConnectionState.Open;

        public event Action OnStreamStarted;
        public event Action<string> OnDelta;
        public event Action<string> OnComplete;
        public event Action<string> OnReasoningDelta;
        public event Action<ToolCallUpdate> OnToolUpdate;
        public event Action<ClarifyRequest> OnClarifyRequest;
        public event Action<ApprovalRequest> OnApprovalRequest;
        public event Action<string> OnError;
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

        public async Task SendMessage(string text)
        {
            if (string.IsNullOrEmpty(ActiveSessionId))
                throw new InvalidOperationException("No active session");

            if (Busy || AwaitingResponse)
                throw new InvalidOperationException("Session is busy. Wait for the current response to finish.");

            try
            {
                await _gateway.Request<object>(
                    RpcMethods.PromptSubmit,
                    new { session_id = ActiveSessionId, text });
            }
            catch
            {
                Busy = false;
                AwaitingResponse = false;
                throw;
            }

            Busy = true;
            AwaitingResponse = true;
        }

        public async Task SendMessage(string text, System.Collections.Generic.List<ImageData> images)
        {
            if (string.IsNullOrEmpty(ActiveSessionId))
                throw new InvalidOperationException("No active session");

            if (Busy || AwaitingResponse)
                throw new InvalidOperationException("Session is busy. Wait for the current response to finish.");

            try
            {
                // Build images array for gateway: [{data: base64, media_type: "image/png"}]
                var imagePayloads = new System.Collections.Generic.List<object>();
                if (images != null)
                {
                    foreach (var img in images)
                    {
                        if (!string.IsNullOrEmpty(img.data))
                        {
                            imagePayloads.Add(new { data = img.data, media_type = img.mediaType ?? "image/png" });
                        }
                    }
                }

                if (imagePayloads.Count > 0)
                {
                    await _gateway.Request<object>(
                        RpcMethods.PromptSubmit,
                        new { session_id = ActiveSessionId, text, images = imagePayloads });
                }
                else
                {
                    await _gateway.Request<object>(
                        RpcMethods.PromptSubmit,
                        new { session_id = ActiveSessionId, text });
                }
            }
            catch
            {
                Busy = false;
                AwaitingResponse = false;
                throw;
            }

            Busy = true;
            AwaitingResponse = true;
        }

        public async Task Interrupt()
        {
            if (string.IsNullOrEmpty(ActiveSessionId))
                return;

            try
            {
                await _gateway.Request<object>(
                    RpcMethods.SessionInterrupt,
                    new { session_id = ActiveSessionId });
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

            ActiveSessionId = result.session_id;
            StoredSessionId = result.stored_session_id;

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

            ActiveSessionId = result.session_id;
            StoredSessionId = sessionId;

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

            try
            {
                await _gateway.Request<object>(RpcMethods.SessionClose, new { session_id = sid });
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Hermes] Close session failed: " + ex.Message);
            }

            if (sid == ActiveSessionId)
            {
                ActiveSessionId = null;
                StoredSessionId = null;
                RuntimeInfo = null;
            }
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
                new { session_id = ActiveSessionId, command = cmd }
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
                new { session_id = ActiveSessionId }
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

        public async Task RespondToApproval(bool approved)
        {
            string choice = approved ? "once" : "deny";
            await _gateway.Request<object>(
                RpcMethods.ApprovalRespond,
                new { session_id = ActiveSessionId, choice });
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
            var sessionId = evt.SessionId ?? ActiveSessionId;
            return sessionId == ActiveSessionId;
        }

        private void ApplySessionInfo(SessionInfo info)
        {
            if (info == null)
                return;

            RuntimeInfo = new SessionRuntimeInfo
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
            if (!IsActiveEvent(evt)) return;
            if (evt.Payload == null) return;

            var info = evt.Payload.ToObject<SessionRuntimeInfo>();
            if (info == null) return;

            RuntimeInfo = info;
            if (!string.IsNullOrEmpty(info.cwd))
                _clientBridge.SetWorkspace(info.cwd);
            if (info.running.HasValue)
            {
                Busy = info.running.Value;
                if (!Busy && AwaitingResponse)
                    AwaitingResponse = false;
            }
        }

        private void HandleMessageStart(GatewayEvent evt)
        {
            if (!IsActiveEvent(evt)) return;
            Busy = true;
            AwaitingResponse = true;
            OnStreamStarted?.Invoke();
        }

        private void HandleMessageDelta(GatewayEvent evt)
        {
            if (!IsActiveEvent(evt)) return;
            if (evt.Payload == null) return;

            string text = ExtractText(evt.Payload);
            if (!string.IsNullOrEmpty(text))
                OnDelta?.Invoke(text);
        }

        private void HandleMessageComplete(GatewayEvent evt)
        {
            if (!IsActiveEvent(evt)) return;

            string finalText = "";
            if (evt.Payload != null)
            {
                finalText = ExtractText(evt.Payload) ?? "";
            }

            if (string.IsNullOrWhiteSpace(finalText) && evt.Payload != null)
                NeonLogger.LogWarning("[Hermes] message.complete had no text payload: " + evt.Payload.ToString(Formatting.None));

            OnComplete?.Invoke(finalText);
            Busy = false;
            AwaitingResponse = false;

            if (evt.Payload != null)
            {
                UsageStats usage = ExtractUsage(evt.Payload);
                if (usage != null && RuntimeInfo != null)
                    RuntimeInfo.usage = usage;
            }
        }

        private void HandleReasoningDelta(GatewayEvent evt)
        {
            if (!IsActiveEvent(evt)) return;
            if (evt.Payload == null) return;

            string text = ExtractText(evt.Payload);
            if (!string.IsNullOrEmpty(text))
                OnReasoningDelta?.Invoke(text);
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
            if (!IsActiveEvent(evt)) return;
            HandleToolEvent(evt, ToolCallStatus.Running);
        }

        private void HandleToolProgress(GatewayEvent evt)
        {
            if (!IsActiveEvent(evt)) return;
            HandleToolEvent(evt, ToolCallStatus.Running);
        }

        private void HandleToolComplete(GatewayEvent evt)
        {
            if (!IsActiveEvent(evt)) return;
            HandleToolEvent(evt, ToolCallStatus.Complete);
        }

        private void HandleToolEvent(GatewayEvent evt, ToolCallStatus status)
        {
            if (evt.Payload == null) return;

            var payload = evt.Payload.ToObject<ToolEventPayload>();
            if (payload == null) return;

            OnToolUpdate?.Invoke(new ToolCallUpdate
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
            if (!IsActiveEvent(evt)) return;
            if (evt.Payload == null) return;

            var payload = evt.Payload.ToObject<ClarifyEventPayload>();
            if (payload == null) return;

            OnClarifyRequest?.Invoke(new ClarifyRequest
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
            if (!IsActiveEvent(evt)) return;
            if (evt.Payload == null) return;

            var payload = evt.Payload.ToObject<ClarifyEventPayload>();
            if (payload == null) return;

            OnApprovalRequest?.Invoke(new ApprovalRequest
            {
                requestId = payload.request_id,
                description = payload.question,
                type = "approval"
            });
        }

        private void HandleSudoRequest(GatewayEvent evt)
        {
            if (!IsActiveEvent(evt)) return;
            if (evt.Payload == null) return;

            var payload = evt.Payload.ToObject<ClarifyEventPayload>();
            if (payload == null) return;

            OnApprovalRequest?.Invoke(new ApprovalRequest
            {
                requestId = payload.request_id,
                description = payload.question,
                type = "sudo"
            });
        }

        private void HandleError(GatewayEvent evt)
        {
            if (!IsActiveEvent(evt)) return;
            if (evt.Payload == null) return;

            var payload = evt.Payload.ToObject<ErrorPayload>();
            string message = payload?.message ?? "Unknown error";
            Busy = false;
            AwaitingResponse = false;
            OnError?.Invoke(message);
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
                OnError?.Invoke("Hermes connection lost (" + state + ")");
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
