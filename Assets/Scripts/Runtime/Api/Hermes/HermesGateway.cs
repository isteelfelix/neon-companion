// HermesGateway.cs - WebSocket JSON-RPC 2.0 client for Hermes backend
// Based on Desktop app's json-rpc-gateway.ts and C# reference implementation.
// Uses System.Net.WebSockets.ClientWebSocket (built into Unity/NET Standard 2.1).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace NeonCompanion.Runtime.Api.Hermes
{
    // === Protocol Types ===

    [Serializable]
    public class RpcRequest
    {
        [JsonProperty("jsonrpc")] public string JsonRpc = "2.0";
        [JsonProperty("id")] public string Id;
        [JsonProperty("method")] public string Method;
        [JsonProperty("params")] public object Params;
    }

    [Serializable]
    public class RpcFrame
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("method")] public string Method;
        [JsonProperty("result")] public JToken Result;
        [JsonProperty("error")] public RpcError Error;
        [JsonProperty("params")] public JObject Params;
    }

    [Serializable]
    public class RpcError
    {
        [JsonProperty("message")] public string Message;
        [JsonProperty("code")] public int Code;
    }

    /// <summary>
    /// A JSON-RPC error frame surfaced as an exception. Carries the numeric code so callers can
    /// tell "this backend predates the method" (-32601) from a real failure without parsing text —
    /// the message stays exactly what the backend sent, so existing error UI is unchanged.
    /// </summary>
    public class RpcException : Exception
    {
        public int Code { get; private set; }

        public RpcException(int code, string message) : base(message)
        {
            Code = code;
        }
    }

    [Serializable]
    public class GatewayEvent
    {
        [JsonProperty("type")] public string Type;
        [JsonProperty("session_id")] public string SessionId;
        [JsonProperty("payload")] public JToken Payload;
    }

    public enum ConnectionState { Idle, Connecting, Open, Closed, Error }

    // === Pending Request ===

    internal class PendingCall
    {
        public TaskCompletionSource<JToken> Tcs;
        public CancellationTokenSource Cts;
    }

    // === Gateway Event Names ===

    public static class GatewayEvents
    {
        public const string GatewayReady = "gateway.ready";
        public const string SessionInfo = "session.info";
        public const string MessageStart = "message.start";
        public const string MessageDelta = "message.delta";
        public const string MessageInterim = "message.interim";
        public const string MessageComplete = "message.complete";
        public const string ThinkingDelta = "thinking.delta";
        public const string ReasoningDelta = "reasoning.delta";
        public const string ReasoningAvailable = "reasoning.available";
        public const string StatusUpdate = "status.update";
        public const string ToolStart = "tool.start";
        public const string ToolProgress = "tool.progress";
        public const string ToolComplete = "tool.complete";
        public const string ToolGenerating = "tool.generating";
        public const string ClarifyRequest = "clarify.request";
        public const string ApprovalRequest = "approval.request";
        public const string SudoRequest = "sudo.request";
        public const string SecretRequest = "secret.request";
        // The gateway gave up waiting on a sudo/secret prompt (server-side _block timeout, 120s for
        // sudo). The pending request is gone: the capture UI must be torn down WITHOUT answering,
        // since any late sudo.respond/secret.respond now resolves to status="expired".
        public const string SudoExpire = "sudo.expire";
        public const string SecretExpire = "secret.expire";
        public const string BackgroundComplete = "background.complete";
        // Self-improvement background review saved something to memory/skills and emitted a
        // persistent summary. Desktop surfaces it as a permanent system line in the transcript.
        public const string ReviewSummary = "review.summary";
        public const string SessionTitle = "session.title";
        // COMPANION-ONLY EXTENSION. The upstream tui_gateway never emits terminal.execute and has
        // no terminal.respond method (unknown methods answer -32601), so this is a compatibility
        // path for gateways that do speak it: the handler stays wired, and the reply degrades to a
        // no-op when the backend rejects it. The upstream terminal contract is the three events
        // below plus terminal.read.respond.
        public const string TerminalExecute = "terminal.execute";
        // read_terminal tool: backend asks the client to serialize its live terminal buffer.
        // The Python side BLOCKS on the matching terminal.read.respond RPC, so this must always
        // be answered (empty text = no live pane). Desktop gateway-event.ts handles it the same.
        public const string TerminalReadRequest = "terminal.read.request";
        // Live stdout/stderr of a backend `terminal(background=true)` process, pushed chunk by
        // chunk (tui_gateway _wire_agent_terminal_output). session_id names the session that owns
        // the process, payload is {process_id, chunk}. Fire-and-forget: nothing is answered.
        public const string AgentTerminalOutput = "agent.terminal.output";
        // close_terminal tool: drop the read-only view of a background process. The process is NOT
        // killed — only the view/buffer goes away. Payload is {process_id}; session_id may be empty
        // when the process is already gone, so the process id is the authoritative key.
        public const string TerminalClose = "terminal.close";
        public const string ClientPing = "client.ping";
        public const string FileTransferStart = "file.transfer.start";
        public const string FileTransferChunk = "file.transfer.chunk";
        public const string FileTransferFinish = "file.transfer.finish";
        public const string Error = "error";

        // Subagent stream events (Desktop use-message-stream). These carry an explicit
        // session_id when scoped to a background session; when unscoped they are DROPPED
        // by the routing layer (see gateway-events.ts gatewayEventRequiresSessionId),
        // never attributed to the focused chat. Match on the SubagentPrefix.
        public const string SubagentPrefix = "subagent.";
        public const string SubagentSpawnRequested = "subagent.spawn_requested";
        public const string SubagentStart = "subagent.start";
        public const string SubagentProgress = "subagent.progress";
        public const string SubagentComplete = "subagent.complete";
        public const string SubagentThinking = "subagent.thinking";
        public const string SubagentTool = "subagent.tool";
    }

    // === RPC Method Names ===

    public static class RpcMethods
    {
        public const string SessionCreate = "session.create";
        public const string SessionResume = "session.resume";
        public const string SessionClose = "session.close";
        public const string SessionList = "session.list";
        // Re-attach an ALREADY-LIVE session to this socket. The gateway stores one transport per
        // live session, so after a reconnect its events go to the dead socket until something
        // rebinds it — session.activate is that rebind, and unlike session.resume it neither
        // rebuilds the agent nor closes the previously focused session.
        public const string SessionActivate = "session.activate";
        // In-memory snapshot of the sessions this gateway process still has agents for. Live
        // status ONLY (Desktop use-background-sync) — the durable history catalog stays REST
        // /api/sessions?profile=…, which is profile-scoped; active_list is not.
        public const string SessionActiveList = "session.active_list";
        public const string SessionInterrupt = "session.interrupt";
        public const string SessionSteer = "session.steer";
        public const string PromptSubmit = "prompt.submit";
        public const string SlashExec = "slash.exec";
        public const string ModelOptions = "model.options";
        public const string ClarifyRespond = "clarify.respond";
        public const string ApprovalRespond = "approval.respond";
        public const string SudoRespond = "sudo.respond";
        public const string SecretRespond = "secret.respond";
        // Companion-only reply to GatewayEvents.TerminalExecute — see the note there. Upstream
        // answers -32601; callers must treat that as "not supported", not as a failure.
        public const string TerminalRespond = "terminal.respond";
        public const string TerminalReadRespond = "terminal.read.respond";
        public const string ImageAttach = "image.attach";
        public const string ImageDetach = "image.detach";
        public const string ClientRegister = "client.register";
        public const string ClientPong = "client.pong";
        public const string FileTransferAck = "file.transfer.ack";
        public const string FileTransferComplete = "file.transfer.complete";
        public const string FileTransferStart = "file.transfer.start";
        public const string FileTransferChunk = "file.transfer.chunk";
        public const string FileTransferFinish = "file.transfer.finish";
        public const string ImageAttachBytes = "image.attach_bytes";
        public const string SessionUsage = "session.usage";
        public const string SessionContextBreakdown = "session.context_breakdown";
    }

    // === HermesGateway ===

    public class HermesGateway : IDisposable
    {
        private ClientWebSocket _socket;
        private ConnectionState _state = ConnectionState.Idle;
        private int _nextId;
        private readonly ConcurrentDictionary<string, PendingCall> _pending = new ConcurrentDictionary<string, PendingCall>();
        private readonly Dictionary<string, List<Action<GatewayEvent>>> _eventHandlers = new Dictionary<string, List<Action<GatewayEvent>>>();
        private readonly List<Action<ConnectionState>> _stateHandlers = new List<Action<ConnectionState>>();
        private readonly object _lock = new object();
        private readonly SynchronizationContext _syncContext = SynchronizationContext.Current;
        private CancellationTokenSource _receiveCts;
        private Task _receiveLoop;
        private bool _disposed;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

        // Config
        // Default per-request ack timeout. Desktop's shared client uses 120s, but its
        // gateway instance overrides to 30s (DEFAULT_GATEWAY_REQUEST_TIMEOUT_MS); companion
        // keeps 30s to match that instance default.
        public int RequestTimeoutMs { get; set; } = 30000;
        // Open-handshake timeout. Desktop DEFAULT_CONNECT_TIMEOUT_MS = 15s: a dead socket must
        // fail to Error instead of hanging forever in Connecting.
        public int ConnectTimeoutMs { get; set; } = 15000;
        public string RequestIdPrefix { get; set; } = "r";

        // prompt.submit is effectively fire-and-forget: turn completion is signalled by the
        // message.complete/error stream events, NOT the RPC ack. Desktop bounds it at
        // PROMPT_SUBMIT_REQUEST_TIMEOUT_MS = 1_800_000 (matches backend agent.gateway_timeout).
        // Bounding it at the 30s default surfaces a spurious "request timed out" mid-turn.
        public const int PromptSubmitTimeoutMs = 1800000;

        /// <summary>
        /// Default ack timeout for a given RPC method when the caller does not override it.
        /// Only prompt.submit needs the long timeout; everything else uses RequestTimeoutMs.
        /// </summary>
        public int DefaultTimeoutForMethod(string method)
        {
            if (method == RpcMethods.PromptSubmit)
                return PromptSubmitTimeoutMs;
            return RequestTimeoutMs;
        }

        /// <summary>
        /// True when a JSON-RPC call failed because the backend predates the method
        /// (JSON-RPC -32601). Mirrors Desktop gateway-rpc.ts isMissingRpcMethod.
        /// </summary>
        public static bool IsMissingRpcMethod(Exception error)
        {
            if (error == null)
                return false;
            // The code is authoritative when the backend answered with an error frame; the string
            // sniff below stays for transports/proxies that only relay a message.
            var rpcError = error as RpcException;
            if (rpcError != null && rpcError.Code == -32601)
                return true;
            var message = error.Message;
            if (string.IsNullOrEmpty(message))
                return false;
            message = message.ToLowerInvariant();
            return message.Contains("method not found")
                || message.Contains("-32601")
                || message.Contains("unknown method")
                || message.Contains("no such method");
        }

        public ConnectionState State => _state;
        public event Action<GatewayEvent> OnEvent;

        // === Connection ===

        public async Task Connect(string wsUrl)
        {
            if (_state == ConnectionState.Open || _state == ConnectionState.Connecting)
                return;

            SetState(ConnectionState.Connecting);

            // A previous socket that failed/closed WITHOUT Close() (receive-loop error, remote
            // teardown) leaves its loop running. It reads the _socket FIELD, so once this connect
            // installs the replacement both loops would consume the new socket and dispatch every
            // event twice — duplicated deltas/tool rows in the UI. Retire it before swapping.
            _receiveCts?.Cancel();
            _receiveCts = null;
            _receiveLoop = null;

            try
            {
                _socket = new ClientWebSocket();
                _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

                var uri = new Uri(wsUrl);

                // Bound the open handshake so a dead socket fails to Error instead of
                // hanging forever in Connecting (Desktop DEFAULT_CONNECT_TIMEOUT_MS = 15s).
                if (ConnectTimeoutMs > 0)
                {
                    var connectCts = new CancellationTokenSource(ConnectTimeoutMs);
                    try
                    {
                        await _socket.ConnectAsync(uri, connectCts.Token);
                    }
                    catch (OperationCanceledException) when (connectCts.IsCancellationRequested)
                    {
                        throw new TimeoutException("Gateway connect timed out after " + ConnectTimeoutMs + "ms");
                    }
                    finally
                    {
                        connectCts.Dispose();
                    }
                }
                else
                {
                    await _socket.ConnectAsync(uri, CancellationToken.None);
                }

                SetState(ConnectionState.Open);

                _receiveCts = new CancellationTokenSource();
                // Bind the loop to THIS socket instance, not the field: a later reconnect swaps
                // the field, and a loop still reading it would keep pushing events into the UI
                // on behalf of a socket the app already replaced.
                _receiveLoop = ReceiveLoop(_socket, _receiveCts.Token);
            }
            catch (Exception ex)
            {
                SetState(ConnectionState.Error);
                // Logged once at the caller (GlobalBackendSelector). Keep this a warning
                // to avoid a duplicate red error + stack on every reconnect attempt.
                Debug.LogWarning("[HermesGateway] Connection failed: " + ex.Message);
                throw;
            }
        }

        public async Task Close()
        {
            if (_socket == null)
                return;

            _receiveCts?.Cancel();

            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[HermesGateway] Close error: " + ex.Message);
            }
            finally
            {
                _socket?.Dispose();
                _socket = null;
                SetState(ConnectionState.Closed);
                RejectAllPending(new Exception("Gateway closed"));
            }
        }

        // === RPC Request ===

        public async Task<T> Request<T>(string method, object parameters = null, int timeoutMs = -1)
        {
            if (_state != ConnectionState.Open || _socket == null)
                throw new InvalidOperationException("Gateway not connected");

            var id = RequestIdPrefix + (++_nextId);
            var timeout = timeoutMs > 0 ? timeoutMs : DefaultTimeoutForMethod(method);

            var tcs = new TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cts = new CancellationTokenSource();
            var pending = new PendingCall { Tcs = tcs, Cts = cts };
            _pending[id] = pending;

            // Timeout
            _ = Task.Delay(timeout, cts.Token).ContinueWith(_ =>
            {
                if (_pending.TryRemove(id, out var call))
                {
                    call.Tcs.TrySetException(new TimeoutException("Request timed out: " + method));
                }
            }, TaskContinuationOptions.OnlyOnRanToCompletion);

            var request = new RpcRequest
            {
                Id = id,
                Method = method,
                Params = parameters
            };

            var json = JsonConvert.SerializeObject(request);
            var bytes = Encoding.UTF8.GetBytes(json);

            try
            {
                await SendRawAsync(bytes, CancellationToken.None);
            }
            catch (Exception)
            {
                _pending.TryRemove(id, out _);
                throw;
            }

            try
            {
                var result = await tcs.Task;
                return result.ToObject<T>();
            }
            catch (TimeoutException)
            {
                throw;
            }
        }

        /// <summary>
        /// Fire-and-forget JSON-RPC notification (no id). Used for client.pong and similar responses.
        /// </summary>
        public async Task NotifyAsync(string method, object parameters = null)
        {
            if (_state != ConnectionState.Open || _socket == null)
                throw new InvalidOperationException("Gateway not connected");

            var frame = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method
            };
            if (parameters != null)
                frame["params"] = JToken.FromObject(parameters);

            var json = frame.ToString(Formatting.None);
            var bytes = Encoding.UTF8.GetBytes(json);
            await SendRawAsync(bytes, CancellationToken.None);
        }

        // === Event Subscriptions ===

        public void On(string eventType, Action<GatewayEvent> handler)
        {
            lock (_lock)
            {
                if (!_eventHandlers.ContainsKey(eventType))
                    _eventHandlers[eventType] = new List<Action<GatewayEvent>>();
                _eventHandlers[eventType].Add(handler);
            }
        }

        public void OnStateChange(Action<ConnectionState> handler)
        {
            lock (_lock)
            {
                _stateHandlers.Add(handler);
            }
            handler(_state); // Emit current state
        }

        public void Off(string eventType, Action<GatewayEvent> handler)
        {
            lock (_lock)
            {
                if (_eventHandlers.TryGetValue(eventType, out var list))
                    list.Remove(handler);
            }
        }

        // === Receive Loop ===

        private async Task ReceiveLoop(ClientWebSocket socket, CancellationToken ct)
        {
            var buffer = new byte[65536];
            var sb = new StringBuilder();

            try
            {
                while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    sb.Clear();
                    WebSocketReceiveResult result;

                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            if (ReferenceEquals(_socket, socket))
                                SetState(ConnectionState.Closed);
                            return;
                        }
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    }
                    while (!result.EndOfMessage);

                    // A frame that arrived on a socket this gateway has already replaced belongs
                    // to a dead connection: dropping it is what keeps a retired socket from
                    // resolving pending calls or mutating the UI behind the live one.
                    if (!ReferenceEquals(_socket, socket))
                        return;

                    HandleMessage(sb.ToString());
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown
            }
            catch (WebSocketException ex)
            {
                Debug.LogWarning("[HermesGateway] WebSocket error: " + ex.Message);
                if (ReferenceEquals(_socket, socket))
                    SetState(ConnectionState.Error);
            }
            catch (Exception ex)
            {
                Debug.LogError("[HermesGateway] Receive loop error: " + ex.Message);
                if (ReferenceEquals(_socket, socket))
                    SetState(ConnectionState.Error);
            }
        }

        private void HandleMessage(string raw)
        {
            RpcFrame frame;
            try
            {
                frame = JsonConvert.DeserializeObject<RpcFrame>(raw);
            }
            catch
            {
                return;
            }

            if (frame == null)
                return;

            // Response to a request (has id)
            if (!string.IsNullOrEmpty(frame.Id))
            {
                if (_pending.TryRemove(frame.Id, out var call))
                {
                    call.Cts.Cancel();
                    if (frame.Error != null)
                        call.Tcs.TrySetException(new RpcException(frame.Error.Code, frame.Error.Message ?? "RPC error"));
                    else
                        call.Tcs.TrySetResult(frame.Result);
                }
                return;
            }

            // Server-push event (method = "event")
            if (frame.Method == "event" && frame.Params != null)
            {
                var evt = frame.Params.ToObject<GatewayEvent>();
                if (evt != null)
                    DispatchEvent(evt);
            }
        }

        private void DispatchEvent(GatewayEvent evt)
        {
            // Copy the handler list inside the lock: a handler (e.g. the gateway.ready one) may
            // call Off() and mutate the underlying list while we iterate it on the same context,
            // which would throw "Collection was modified" and kill the receive loop.
            List<Action<GatewayEvent>> handlers = null;
            lock (_lock)
            {
                if (_eventHandlers.TryGetValue(evt.Type, out var list) && list != null)
                    handlers = new List<Action<GatewayEvent>>(list);
            }
            if (handlers != null)
            {
                foreach (var h in handlers)
                {
                    var handler = h;
                    InvokeOnContext(() =>
                    {
                        try { handler(evt); }
                        catch (Exception ex) { Debug.LogError("[HermesGateway] Handler error: " + ex.Message); }
                    });
                }
            }

            // Wildcard handlers
            List<Action<GatewayEvent>> anyHandlers = null;
            lock (_lock)
            {
                if (_eventHandlers.TryGetValue("*", out var list) && list != null)
                    anyHandlers = new List<Action<GatewayEvent>>(list);
            }
            if (anyHandlers != null)
            {
                foreach (var h in anyHandlers)
                {
                    var handler = h;
                    InvokeOnContext(() =>
                    {
                        try { handler(evt); }
                        catch (Exception ex) { Debug.LogError("[HermesGateway] Handler error: " + ex.Message); }
                    });
                }
            }

            if (OnEvent != null)
                InvokeOnContext(() => OnEvent?.Invoke(evt));
        }

        // === Internal ===

        private async Task SendRawAsync(byte[] bytes, CancellationToken ct)
        {
            if (_socket == null)
                throw new InvalidOperationException("Gateway socket not available");

            await _sendLock.WaitAsync(ct);
            try
            {
                await _socket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    true,
                    ct);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private void SetState(ConnectionState newState)
        {
            if (_state == newState)
                return;
            _state = newState;

            List<Action<ConnectionState>> handlers;
            lock (_lock)
            {
                handlers = new List<Action<ConnectionState>>(_stateHandlers);
            }
            foreach (var h in handlers)
            {
                var handler = h;
                InvokeOnContext(() =>
                {
                    try { handler(newState); }
                    catch (Exception ex) { Debug.LogError("[HermesGateway] State handler error: " + ex.Message); }
                });
            }
        }

        private void InvokeOnContext(Action action)
        {
            if (action == null)
                return;

            if (_syncContext == null || SynchronizationContext.Current == _syncContext)
            {
                action();
                return;
            }

            _syncContext.Post(_ => action(), null);
        }

        private void RejectAllPending(Exception error)
        {
            foreach (var kv in _pending)
            {
                kv.Value.Cts.Cancel();
                kv.Value.Tcs.TrySetException(error);
            }
            _pending.Clear();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            _receiveCts?.Cancel();
            try { _socket?.Dispose(); }
            catch { }
            _socket = null;
            SetState(ConnectionState.Closed);
            _sendLock?.Dispose();
        }
    }
}
