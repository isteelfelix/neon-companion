// HermesSessionManager.cs - Session lifecycle + IChatTransport implementation
// Wraps HermesGateway with session management, streaming, tool calls, clarify.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Core;
using Newtonsoft.Json;
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
    public class ErrorPayload
    {
        public string message;
    }

    // === HermesSessionManager ===

    public class HermesSessionManager : IChatTransport
    {
        private readonly HermesGateway _gateway;
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

        // === Constructor ===

        public HermesSessionManager(HermesGateway gateway)
        {
            _gateway = gateway;
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

            await _gateway.Request<object>(
                RpcMethods.PromptSubmit,
                new { session_id = ActiveSessionId, text });
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

        public async Task RespondToApproval(string requestId, bool approved)
        {
            await _gateway.Request<object>(
                RpcMethods.ApprovalRespond,
                new { request_id = requestId, approved });
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

            var payload = evt.Payload.ToObject<MessageDeltaPayload>();
            if (payload != null && payload.text != null)
                OnDelta?.Invoke(payload.text);
        }

        private void HandleMessageComplete(GatewayEvent evt)
        {
            if (!IsActiveEvent(evt)) return;

            string finalText = "";
            if (evt.Payload != null)
            {
                var payload = evt.Payload.ToObject<MessageCompletePayload>();
                finalText = payload?.text ?? payload?.rendered ?? "";
            }

            OnComplete?.Invoke(finalText);
            AwaitingResponse = false;

            if (evt.Payload != null)
            {
                var payload = evt.Payload.ToObject<MessageCompletePayload>();
                if (payload?.usage != null && RuntimeInfo != null)
                    RuntimeInfo.usage = payload.usage;
            }
        }

        private void HandleReasoningDelta(GatewayEvent evt)
        {
            if (!IsActiveEvent(evt)) return;
            if (evt.Payload == null) return;

            var payload = evt.Payload.ToObject<MessageDeltaPayload>();
            if (payload != null && payload.text != null)
                OnReasoningDelta?.Invoke(payload.text);
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
                inlineDiff = payload.inline_diff
            });
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
            _gateway?.Dispose();
        }
    }
}
