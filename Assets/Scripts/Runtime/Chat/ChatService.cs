using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Api;
using NeonCompanion.Runtime.Api.Hermes;
using NeonCompanion.Runtime.Api.Models;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Data.Repositories;
using NeonCompanion.Runtime.Localization;
using NeonCompanion.Runtime.UI.Chat;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace NeonCompanion.Runtime.Chat
{
    public sealed class HermesGenerationStalledException : TimeoutException
    {
        public HermesGenerationStalledException(string message) : base(message) { }
    }

    public sealed class ChatService
    {
        private const int RichHermesHistoryMessageThreshold = 120;
        private const int HermesCompletionPollMs = 2000;
        private const int HermesInactivityTimeoutMs = 5 * 60 * 1000;
        private const int HermesCompletionMaxWaitMs = 30 * 60 * 1000;

        private readonly IAiClient _aiClient;
        private readonly ProviderManager _providerManager;
        private readonly IChatSessionRepository _sessionRepository;
        private IChatTransport _chatTransport;
        private ChatViewModel _currentChatViewModel;
        private ChatSession _currentSession;
        private ProviderConfig _currentProvider;
        private string _lastProviderChangeHash;

        // Hermes backend profile the open session belongs to (backend identity only — never an
        // avatar profile or a provider id). Captured when the session is created/opened so a
        // profile switch can tell whether the open chat still belongs to the selected profile.
        private string _currentSessionHermesProfile;

        // Hermes streaming state — multiplexed per display/persisted session id so several
        // sessions can stream in parallel. Each session owns its ChatViewModel/messages and
        // streaming buffers, independent of which session the UI currently views (the foreground).
        private sealed class HermesStream
        {
            public string serverSessionId;
            public ChatSession session;                 // in-memory foreground record (not persisted)
            public ChatViewModel viewModel;             // owns Messages for this session
            public ChatMessage streamingMessage;
            public System.Text.StringBuilder buffer;
            public System.Text.StringBuilder reasoning;
            public bool active;
            public DateTime startTime;
            public DateTime lastActivityTime;
            public bool usageBaselineKnown;
            public int baselineOutput;
            public int baselineTotal;
            public TaskCompletionSource<bool> complete;
            public Action<string> tokenCb;                              // UI; set only while foreground
            public Action<ToolProgressInfo> toolCb;                     // UI; set only while foreground
            public string lastError;
            public string pendingUserContent;
            public bool interrupted;
        }

        private readonly Dictionary<string, HermesStream> _hermesStreams =
            new Dictionary<string, HermesStream>();

        // Sessions with a pending approval/clarify request that hasn't been shown yet (because the
        // session was in the background). Drives the sidebar "needs attention" badge.
        private readonly HashSet<string> _attentionSessions = new HashSet<string>();

        /// <summary>
        /// Raised when a session's display state changes (generation started/finished, or a
        /// pending approval/clarify appeared/cleared) so the sidebar can refresh its indicators.
        /// </summary>
        public event Action OnSessionStatesChanged;

        private void RaiseSessionStatesChanged()
        {
            try { OnSessionStatesChanged?.Invoke(); } catch { }
        }

        /// <summary>True if the session has a pending approval/clarify awaiting the user.</summary>
        public bool SessionNeedsAttention(string sessionId)
        {
            return !string.IsNullOrEmpty(sessionId) && _attentionSessions.Contains(sessionId);
        }

        /// <summary>Flag a background session as awaiting user input (approval/clarify).</summary>
        public void MarkSessionAttention(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            if (_attentionSessions.Add(sessionId))
                RaiseSessionStatesChanged();
        }

        /// <summary>Clear the awaiting-input flag for a session (request shown or answered).</summary>
        public void ClearSessionAttention(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            if (_attentionSessions.Remove(sessionId))
                RaiseSessionStatesChanged();
        }

        public event Action<string> OnAssistantResponse;
        public event Action<ProviderConfig> OnCurrentProviderChanged;

        /// <summary>Active chat transport for current backend mode (Hermes or null for OpenAI).</summary>
        public IChatTransport ChatTransport => _chatTransport;

        /// <summary>Set or clear the active chat transport (called by GlobalBackendSelector on mode change).</summary>
        public void SetTransport(IChatTransport transport)
        {
            // Unwire old transport
            if (_chatTransport != null)
            {
                _chatTransport.OnStreamStarted -= HandleHermesStreamStarted;
                _chatTransport.OnDelta -= HandleHermesDelta;
                _chatTransport.OnComplete -= HandleHermesComplete;
                _chatTransport.OnToolUpdate -= HandleHermesToolUpdate;
                _chatTransport.OnReasoningDelta -= HandleHermesReasoningDelta;
                _chatTransport.OnError -= HandleHermesError;
            }

            // Per-session streams belong to the previous transport/connection — drop them so a
            // mode change (or reconnect) starts clean.
            foreach (var kv in _hermesStreams)
                ClearStreamPendingState(kv.Value);
            _hermesStreams.Clear();

            _chatTransport = transport;

            // Wire new transport (session-aware multiplexed events)
            if (_chatTransport != null)
            {
                _chatTransport.OnStreamStarted += HandleHermesStreamStarted;
                _chatTransport.OnDelta += HandleHermesDelta;
                _chatTransport.OnComplete += HandleHermesComplete;
                _chatTransport.OnToolUpdate += HandleHermesToolUpdate;
                _chatTransport.OnReasoningDelta += HandleHermesReasoningDelta;
                _chatTransport.OnError += HandleHermesError;
            }
        }

        public float Temperature { get; set; } = 0.7f;
        public int MaxTokens { get; set; } = 512;
        public string SystemPrompt { get; set; }
        public bool UseStreaming { get; set; }
        public bool SaveChatHistory { get; set; } = true;

        public ProviderConfig CurrentProvider => _currentProvider;
        public ChatViewModel CurrentChatViewModel => _currentChatViewModel;
        public string CurrentSessionId => _currentSession?.sessionId;
        public string CurrentSessionModel => _currentChatViewModel?.SelectedModel
            ?? _currentSession?.selectedModel
            ?? _currentProvider?.defaultModel;

        public ChatService(
            IAiClient aiClient,
            ProviderManager providerManager,
            IChatSessionRepository sessionRepository)
        {
            _aiClient = aiClient;
            _providerManager = providerManager;
            _sessionRepository = sessionRepository;
        }

        public async Task<ChatViewModel> GetOrCreateChatAsync(string preferredProviderId = null)
        {
            if (_currentProvider != null && !_currentProvider.isEnabled)
                ClearActiveProviderState();

            if (_currentChatViewModel != null)
                return _currentChatViewModel;

            if (_currentSession == null)
            {
                await LoadLatestSessionAsync(preferredProviderId);
                if (_currentChatViewModel != null)
                    return _currentChatViewModel;
            }

            _currentProvider = await ResolveProviderAsync(preferredProviderId);
            if (_currentProvider == null)
            {
                NeonLogger.LogWarning("[ChatService] No provider configured — chat view model not created.");
                return null;
            }
            RaiseCurrentProviderChanged();

            SyncFromProvider(_currentProvider);
            _currentChatViewModel = new ChatViewModel(_aiClient, _currentProvider);
            _currentChatViewModel.ProviderSessionId = _currentSession?.providerSessionId;
            _currentChatViewModel.SelectedModel = _currentSession?.selectedModel ?? _currentProvider?.defaultModel;
            ApplyGenerationSettings();

            NeonLogger.Log("Chat session ready.");
            return _currentChatViewModel;
        }

        /// <summary>True when the Hermes multiplexed transport is the active backend.</summary>
        public bool IsHermesActive => _chatTransport != null;

        /// <summary>
        /// True if the given session currently has a generation in flight (Hermes parallel
        /// sessions). Used by the UI to gate the send button / message queue per session.
        /// </summary>
        public bool IsSessionGenerating(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                return false;
            var mgr = GlobalBackendSelector.Instance?.SessionManager;
            if (mgr != null && mgr.IsSessionBusy(sessionId))
                return true;
            HermesStream s = GetStream(sessionId);
            return s != null && (s.active || s.complete != null);
        }

        /// <summary>
        /// Ensure the foreground chat has a stable session id before streaming begins, and return
        /// it. For Hermes this creates the server session up-front so the send is pinned to a known
        /// id (and the view model is not swapped mid-send). Returns null if no session could be made.
        /// </summary>
        public async Task<string> EnsureForegroundSessionIdAsync()
        {
            if (_chatTransport != null)
            {
                await EnsureHermesSessionReadyAsync();
                return _currentSession?.providerSessionId;
            }
            if (_currentSession == null)
                await StartNewSessionAsync();
            return _currentSession?.sessionId;
        }

        /// <summary>
        /// Re-point the foreground session's live stream callbacks at the UI (used when switching
        /// back to a session that is still generating). No-op if the session has no live stream.
        /// Returns the partial assistant text accumulated so far, or null.
        /// </summary>
        public string AttachForegroundStreamCallbacks(Action<string> tokenCb, Action<ToolProgressInfo> toolCb)
        {
            string sid = _currentSession?.providerSessionId;
            HermesStream s = GetStream(sid);
            if (s == null || !s.active || s.streamingMessage == null)
                return null;
            s.tokenCb = tokenCb;
            s.toolCb = toolCb;
            return s.streamingMessage.content;
        }

        /// <summary>
        /// The in-progress assistant message of the foreground session's live stream, or null.
        /// Used by the UI to exclude it from a snapshot render and re-attach the streaming bubble.
        /// </summary>
        public ChatMessage GetForegroundStreamingMessage()
        {
            string sid = _currentSession?.providerSessionId;
            HermesStream s = GetStream(sid);
            return (s != null && s.active && s.streamingMessage != null) ? s.streamingMessage : null;
        }

        public async Task<List<ChatSession>> GetAllSessionsAsync()
        {
            // Hermes: the gateway DB is the source of truth — list server sessions.
            if (_chatTransport != null)
                return await GetHermesSessionsAsDisplayAsync();

            return await Task.FromResult(GetSortedSessions());
        }

        /// <summary>Map Hermes REST sessions into ChatSession display items (Hermes mode).</summary>
        private async Task<List<ChatSession>> GetHermesSessionsAsDisplayAsync()
        {
            var list = new List<ChatSession>();
            List<HermesSession> server;
            try
            {
                server = await GetHermesSessionsAsync();
            }
            catch (Exception ex)
            {
                NeonLogger.LogWarning("[ChatService] Hermes sessions fetch failed: " + ex.Message);
                return list;
            }

            string providerId = _currentProvider?.id;
            for (int i = 0; i < server.Count; i++)
            {
                HermesSession hs = server[i];
                if (hs == null || string.IsNullOrEmpty(hs.id))
                    continue;

                string title = !string.IsNullOrWhiteSpace(hs.title)
                    ? hs.title
                    : (!string.IsNullOrWhiteSpace(hs.preview) ? hs.preview : "Hermes session");

                list.Add(new ChatSession
                {
                    sessionId = hs.id,
                    providerId = providerId,
                    providerSessionId = hs.id,
                    providerRuntimeSessionId = null,
                    selectedModel = hs.model,
                    title = title,
                    updatedAtUnix = hs.last_active > 0 ? hs.last_active : hs.started_at,
                    messages = new List<ChatMessage>(),
                    messageCount = hs.message_count,
                    folder = string.Empty
                });
            }

            return list;
        }

        public async Task DeleteSessionAsync(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                return;

            // Hermes: delete on the server (source of truth) and drop the in-memory stream.
            if (_chatTransport != null)
            {
                await DeleteHermesSessionAsync(sessionId);
                _hermesStreams.Remove(sessionId);
                if (_currentSession != null &&
                    string.Equals(_currentSession.providerSessionId, sessionId, StringComparison.Ordinal))
                {
                    ClearCurrentSessionWithoutSaving();
                }
                return;
            }

            var sessions = _sessionRepository.GetAll();
            var index = sessions.FindIndex(s => s.sessionId == sessionId);
            if (index < 0)
                return;

            sessions.RemoveAt(index);
            _sessionRepository.SaveAll(sessions);

            if (_currentSession?.sessionId == sessionId)
            {
                var remaining = GetSortedSessions();
                var fallback = await FindFallbackSessionAsync(remaining);
                if (fallback != null)
                    await SwitchToSessionAsync(fallback);
                else
                    ClearCurrentSessionWithoutSaving();
            }
        }

        public async Task SetSessionFolderAsync(string sessionId, string folder)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                return;

            var sessions = _sessionRepository.GetAll();
            var index = sessions.FindIndex(s => s.sessionId == sessionId);
            if (index < 0)
                return;

            string normalized = string.IsNullOrWhiteSpace(folder) ? string.Empty : folder.Trim();
            sessions[index].folder = normalized;

            if (_currentSession != null && _currentSession.sessionId == sessionId)
            {
                _currentSession.folder = normalized;
            }

            _sessionRepository.SaveAll(sessions);
            await Task.CompletedTask;
        }

        public async Task SwitchToSessionAsync(ChatSession session, string preferredProviderId = null)
        {
            if (session == null) return;

            // Hermes: server is the source of truth and sessions multiplex — switch foreground
            // without disturbing any in-flight background stream.
            if (_chatTransport != null)
            {
                await SwitchToHermesSessionAsync(session, preferredProviderId);
                return;
            }

            // Persist the current session before switching so mid-stream messages are not lost.
            SaveCurrentSession();

            // Re-read the target session from storage to get the latest messages
            // (the UI passes a potentially stale snapshot from the session list).
            var freshSessions = _sessionRepository.GetAll();
            var fresh = freshSessions.Find(s => s.sessionId == session.sessionId);
            _currentSession = fresh ?? session;

            var sessionProvider = await TryGetProviderByIdAsync(_currentSession.providerId);
            var preferredProvider = sessionProvider == null
                ? await TryGetProviderByIdAsync(preferredProviderId)
                : null;
            var currentProvider = _currentProvider != null && _currentProvider.isEnabled
                ? _currentProvider
                : null;

            _currentProvider = sessionProvider
                ?? preferredProvider
                ?? currentProvider
                ?? await GetActiveProviderForCurrentBackendAsync();

            if (_currentProvider == null || !_currentProvider.isEnabled)
            {
                ClearCurrentSessionWithoutSaving();
                NeonLogger.LogWarning("[ChatService] Session provider is not available — session not opened.");
                return;
            }

            RaiseCurrentProviderChanged();
            SyncFromProvider(_currentProvider);

            _currentChatViewModel = new ChatViewModel(_aiClient, _currentProvider);
            _currentChatViewModel.ProviderSessionId = _currentSession.providerSessionId;
            _currentChatViewModel.SelectedModel = string.IsNullOrWhiteSpace(_currentSession.selectedModel)
                ? _currentProvider?.defaultModel
                : _currentSession.selectedModel;
            ApplyGenerationSettings();

            _currentChatViewModel.Messages.Clear();
            foreach (var msg in _currentSession.messages ?? new List<ChatMessage>())
            {
                _currentChatViewModel.Messages.Add(msg);
            }

            NeonLogger.Log($"Switched to session {session.sessionId}");
        }

        /// <summary>
        /// Switch the foreground Hermes session. The server holds history; an in-flight background
        /// stream for the target is re-attached in place (no resume) so its partial reply is kept.
        /// </summary>
        private async Task SwitchToHermesSessionAsync(ChatSession session, string preferredProviderId)
        {
            string serverId = !string.IsNullOrWhiteSpace(session.providerSessionId)
                ? session.providerSessionId
                : session.sessionId;

            // Ensure the WS transport is connected — switching sessions is the first
            // user action after selecting a provider, and SetMode(Hermes) only creates
            // the transport without connecting it. Without this, ResumeSession silently
            // fails on a closed socket (the user can delete but not open sessions).
            var selector = GlobalBackendSelector.Instance;
            if (selector != null && _chatTransport != null && !_chatTransport.IsConnected)
            {
                var provider = _currentProvider;
                if (provider != null && IsHermesProvider(provider))
                    selector.ConfigureHermesEndpoint(provider.baseUrl, provider.apiKey);
                if (!selector.SessionManager.IsConnected)
                    await selector.ConnectHermes();
                if (!_chatTransport.IsConnected)
                {
                    NeonLogger.LogWarning("[ChatService] Hermes backend not connected — session not resumed.");
                    return;
                }
            }

            // Leave the previous foreground stream running silently in the background.
            DetachForegroundCallbacks();

            var sessionProvider = await TryGetProviderByIdAsync(session.providerId);
            var fallbackCurrent = _currentProvider != null && _currentProvider.isEnabled ? _currentProvider : null;
            _currentProvider = sessionProvider
                ?? await TryGetProviderByIdAsync(preferredProviderId)
                ?? fallbackCurrent
                ?? await GetActiveProviderForCurrentBackendAsync();

            if (_currentProvider == null || !_currentProvider.isEnabled)
            {
                ClearCurrentSessionWithoutSaving();
                NeonLogger.LogWarning("[ChatService] Hermes provider is not available — session not opened.");
                return;
            }
            RaiseCurrentProviderChanged();
            SyncFromProvider(_currentProvider);

            HermesStream existing = GetStream(serverId);
            if (existing != null && existing.viewModel != null && (existing.active || existing.complete != null))
            {
                // Live re-attach: reuse the in-memory stream (it may still be generating).
                _currentChatViewModel = existing.viewModel;
                _currentSession = existing.session;
                _currentSessionHermesProfile = selector != null ? selector.ActiveHermesProfile : null;
                ApplyGenerationSettings();
            }
            else
            {
                // Load history from the server (source of truth) and refresh the display->runtime
                // mapping. Idle in-memory snapshots may point at a runtime id that the gateway has
                // already closed; using them directly causes "session not found" on prompt.submit.
                try
                {
                    await ResumeHermesSessionAsync(serverId);
                }
                catch (Exception ex)
                {
                    // session.resume runs in the active backend profile, so a session belonging to
                    // a different one resolves to "session not found". Name the profile: without
                    // it the bare stack trace says nothing about which scope the lookup ran in.
                    string scope = selector != null && !string.IsNullOrEmpty(selector.ActiveHermesProfile)
                        ? selector.ActiveHermesProfile
                        : "<gateway default>";
                    NeonLogger.LogWarning("[ChatService] Hermes resume failed for session " + serverId
                        + " in profile " + scope + ": " + ex.Message);
                    ClearCurrentSessionWithoutSaving();
                    throw;
                }
                HermesStream s = GetOrCreateStream(serverId);
                s.viewModel = _currentChatViewModel;
                s.session = _currentSession;
            }

            // Foreground hint drives RuntimeInfo (context bar) and foreground-only handlers.
            var mgr = GlobalBackendSelector.Instance?.SessionManager;
            if (mgr != null)
            {
                mgr.SetForegroundSession(serverId);
                UsageStats usage = await mgr.RequestSessionUsage(serverId);
                if (usage == null || usage.context_max <= 0 || usage.context_used <= 0)
                    await mgr.RequestContextBreakdown(serverId);
            }

            NeonLogger.Log($"Switched to Hermes session {serverId}");
        }

        public async Task ClearCurrentSessionAsync()
        {
            if (_currentChatViewModel == null) return;

            _currentChatViewModel.Messages.Clear();

            if (_currentSession != null)
            {
                _currentSession.messages.Clear();
                _currentSession.providerSessionId = null;
                _currentSession.providerRuntimeSessionId = null;
                if (_currentChatViewModel != null)
                    _currentChatViewModel.ProviderSessionId = null;
                SaveCurrentSession();
            }

            NeonLogger.Log("Current session cleared.");
            await Task.CompletedTask;
        }

        public async Task SwitchProviderAsync(ProviderConfig newProvider)
        {
            if (newProvider == null)
                return;

            _currentProvider = newProvider;
            RaiseCurrentProviderChanged();
            SyncFromProvider(_currentProvider);
            _currentChatViewModel = new ChatViewModel(_aiClient, _currentProvider);
            _currentChatViewModel.SelectedModel = _currentProvider?.defaultModel;
            ApplyGenerationSettings();

            await StartNewSessionAsync();
            NeonLogger.Log($"Switched to provider: {newProvider.displayName}");
        }

        public void SetActiveProviderWithoutSession(ProviderConfig provider)
        {
            if (provider == null || !provider.isEnabled)
            {
                ClearActiveProviderState();
                return;
            }

            _currentProvider = provider;
            RaiseCurrentProviderChanged();
            SyncFromProvider(_currentProvider);
            _currentSession = null;
            _currentChatViewModel = null;
            NeonLogger.Log($"Active provider restored: {provider.displayName}");
        }

        public Task ApplyProviderConfigAsync(ProviderConfig updatedProvider, bool resetRemoteSession = false)
        {
            if (updatedProvider == null || _currentProvider == null || _currentProvider.id != updatedProvider.id)
                return Task.CompletedTask;

            string previousDefaultModel = _currentProvider.defaultModel;
            _currentProvider.displayName = updatedProvider.displayName;
            _currentProvider.baseUrl = updatedProvider.baseUrl;
            _currentProvider.apiKey = updatedProvider.apiKey;
            _currentProvider.defaultModel = updatedProvider.defaultModel;
            _currentProvider.temperature = updatedProvider.temperature;
            _currentProvider.maxTokens = updatedProvider.maxTokens;
            _currentProvider.contextWindow = updatedProvider.contextWindow;
            _currentProvider.isEnabled = updatedProvider.isEnabled;
            _currentProvider.backendType = updatedProvider.backendType;
            // Hermes remote-auth fields (token vs cookie/ws-ticket). Required so an active
            // provider's auth mode change takes effect without restarting the app.
            _currentProvider.authMode = updatedProvider.authMode;
            _currentProvider.authProvider = updatedProvider.authProvider;
            _currentProvider.authUsername = updatedProvider.authUsername;
            _currentProvider.sttProvider = updatedProvider.sttProvider;
            _currentProvider.ttsProvider = updatedProvider.ttsProvider;
            _currentProvider.ttsVoice = updatedProvider.ttsVoice;
            _currentProvider.ttsModel = updatedProvider.ttsModel;
            _currentProvider.ttsSpeed = updatedProvider.ttsSpeed;
            _currentProvider.sttLanguage = updatedProvider.sttLanguage;

            if (resetRemoteSession)
                ResetRemoteSessionState();

            SyncFromProvider(_currentProvider);
            RaiseCurrentProviderChanged();
            if (_currentChatViewModel != null)
            {
                if (string.IsNullOrWhiteSpace(_currentChatViewModel.SelectedModel) ||
                    string.Equals(_currentChatViewModel.SelectedModel, previousDefaultModel, StringComparison.Ordinal))
                {
                    _currentChatViewModel.SelectedModel = updatedProvider.defaultModel;
                }
            }

            if (_currentSession != null)
            {
                if (string.IsNullOrWhiteSpace(_currentSession.selectedModel) ||
                    string.Equals(_currentSession.selectedModel, previousDefaultModel, StringComparison.Ordinal))
                {
                    _currentSession.selectedModel = updatedProvider.defaultModel;
                }
            }
            ApplyGenerationSettings();
            SaveCurrentSession();
            return Task.CompletedTask;
        }

        public async Task StartNewSessionAsync()
        {
            // Hermes mode: create server-side session. Do not silently fall back to OpenAI/local mode.
            if (_chatTransport != null)
            {
                if (_currentProvider == null)
                    _currentProvider = await ResolveProviderAsync();
                if (_currentProvider == null)
                {
                    NeonLogger.LogWarning("[ChatService] No provider configured — Hermes session not created.");
                    return;
                }
                RaiseCurrentProviderChanged();

                var selector = GlobalBackendSelector.Instance;
                if (selector != null)
                {
                    // Point the transport at the active Hermes provider's URL/key before connecting.
                    if (IsHermesProvider(_currentProvider))
                        selector.ConfigureHermesEndpoint(_currentProvider.baseUrl, _currentProvider.apiKey);

                    if (!_chatTransport.IsConnected)
                        await selector.ConnectHermes();
                }

                if (!_chatTransport.IsConnected)
                {
                    // Connect failed (already logged once by GlobalBackendSelector). Abort here
                    // instead of calling CreateSession on a dead gateway, which would throw a
                    // noisy "Gateway not connected" stack trace.
                    NeonLogger.LogWarning("[ChatService] Hermes backend not connected — session not created. Check provider URL/key.");
                    return;
                }

                await StartHermesSessionAsync();
                return;
            }

            // OpenAI mode: local session
            if (_currentProvider == null)
                _currentProvider = await ResolveProviderAsync();
            if (_currentProvider == null)
            {
                NeonLogger.LogWarning("[ChatService] No provider configured — session not created.");
                return;
            }
            RaiseCurrentProviderChanged();

            _currentChatViewModel = new ChatViewModel(_aiClient, _currentProvider);
            _currentChatViewModel.ProviderSessionId = null;
            _currentChatViewModel.SelectedModel = _currentProvider?.defaultModel;
            ApplyGenerationSettings();

            _currentSession = new ChatSession
            {
                sessionId = Guid.NewGuid().ToString(),
                providerId = _currentProvider?.id,
                providerSessionId = null,
                selectedModel = _currentProvider?.defaultModel,
                title = "New chat",
                updatedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                messages = new List<ChatMessage>(),
                folder = string.Empty
            };

            SaveCurrentSession();
            NeonLogger.Log("New chat session started.");
        }

        /// <summary>
        /// Ensure the Hermes WebSocket transport is wired into this service. Switching the global
        /// backend mode to Hermes fires OnModeChanged, which calls SetTransport on this service
        /// (see AppBootstrap). Used to recover when a Hermes provider is active but the transport
        /// was never set up, so messages don't fall through to the OpenAI HTTP path (HTTP 405).
        /// </summary>
        private async Task EnsureHermesTransportAsync()
        {
            if (_chatTransport != null)
                return;

            var selector = GlobalBackendSelector.Instance;
            if (selector == null)
                return;

            if (IsHermesProvider(_currentProvider))
                selector.ConfigureHermesEndpoint(_currentProvider.baseUrl, _currentProvider.apiKey);

            if (selector.CurrentMode != BackendMode.Hermes)
                await selector.SetMode(BackendMode.Hermes);

            if (_chatTransport != null && !_chatTransport.IsConnected)
                await selector.ConnectHermes();
        }

        /// <summary>True if the provider is configured to use the Hermes backend.</summary>
        internal static bool IsHermesProvider(ProviderConfig provider)
        {
            return provider != null
                && string.Equals(provider.backendType, "hermes", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Create a new Hermes session via WebSocket.
        /// </summary>
        private async Task StartHermesSessionAsync(bool createLocalSession = true)
        {
            var selector = GlobalBackendSelector.Instance;
            if (selector?.SessionManager == null)
                return;

            // Read the active profile fresh (never a value captured before an await): it decides
            // which profile the session is created in, and omitting it lands the chat in the
            // gateway default no matter what the UI shows.
            string profile = selector.ActiveHermesProfile;
            var response = await selector.SessionManager.CreateSession(profile: profile);
            string persistentSessionId = GetPersistentHermesSessionId(response);
            _currentSessionHermesProfile = profile;

            // Create local session record
            if (_currentProvider == null)
                _currentProvider = await ResolveProviderAsync();

            if (createLocalSession || _currentSession == null)
            {
                _currentChatViewModel = new ChatViewModel(_aiClient, _currentProvider);
                _currentChatViewModel.ProviderSessionId = persistentSessionId;
                _currentChatViewModel.SelectedModel = response.info?.model ?? _currentProvider?.defaultModel;
                ApplyGenerationSettings();

                _currentSession = new ChatSession
                {
                    // In Hermes mode the display id is the persisted DB id when Hermes provides
                    // one; runtime RPC calls are routed through providerRuntimeSessionId mapping.
                    sessionId = persistentSessionId,
                    providerId = _currentProvider?.id,
                    providerSessionId = persistentSessionId,
                    providerRuntimeSessionId = response.session_id,
                    selectedModel = response.info?.model ?? _currentProvider?.defaultModel,
                    title = response.info?.title ?? "Hermes session",
                    updatedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    messages = new List<ChatMessage>(),
                    folder = string.Empty
                };

                SaveCurrentSession();
                NeonLogger.Log("Hermes session created: " + persistentSessionId);
                return;
            }

            if (_currentChatViewModel == null)
            {
                _currentChatViewModel = new ChatViewModel(_aiClient, _currentProvider);
                if (_currentSession.messages != null)
                {
                    for (int i = 0; i < _currentSession.messages.Count; i++)
                        _currentChatViewModel.Messages.Add(_currentSession.messages[i]);
                }
            }

            _currentChatViewModel.ProviderSessionId = persistentSessionId;
            _currentChatViewModel.SelectedModel = response.info?.model ?? _currentProvider?.defaultModel;
            ApplyGenerationSettings();

            _currentSession.sessionId = persistentSessionId;
            _currentSession.providerId = _currentProvider?.id ?? _currentSession.providerId;
            _currentSession.providerSessionId = persistentSessionId;
            _currentSession.providerRuntimeSessionId = response.session_id;
            _currentSession.selectedModel = _currentChatViewModel.SelectedModel;
            if (string.IsNullOrWhiteSpace(_currentSession.title) || _currentSession.title == "New chat")
                _currentSession.title = response.info?.title ?? _currentSession.title;

            SaveCurrentSession();
            NeonLogger.Log("Hermes session bound to current chat: " + persistentSessionId);
        }

        private static string GetPersistentHermesSessionId(SessionCreateResponse response)
        {
            if (response == null)
                return null;

            return !string.IsNullOrWhiteSpace(response.stored_session_id)
                ? response.stored_session_id
                : response.session_id;
        }

        /// <summary>
        /// Resume an existing Hermes session via WebSocket and load its history from the server.
        /// In Hermes mode the server is the source of truth, so the local view model is rebuilt
        /// entirely from the resume response. The display id remains the persisted DB id, while
        /// providerRuntimeSessionId stores the live id used for prompt.submit.
        ///
        /// The session is looked up in the active backend profile: the history list is scoped by
        /// the same profile (REST /api/sessions?profile=), so a listed session belongs to it.
        /// Without the profile the gateway searches its default profile's state.db and answers
        /// "session not found".
        /// </summary>
        public async Task ResumeHermesSessionAsync(string hermesSessionId)
        {
            var selector = GlobalBackendSelector.Instance;
            if (selector?.SessionManager == null)
                return;
            if (!selector.SessionManager.IsConnected)
            {
                NeonLogger.LogWarning("[ChatService] Cannot resume Hermes session — WS not connected.");
                return;
            }

            string profile = selector.ActiveHermesProfile;
            var response = await selector.SessionManager.ResumeSession(hermesSessionId, profile);
            string displaySessionId = !string.IsNullOrWhiteSpace(response.stored_session_id)
                ? response.stored_session_id
                : hermesSessionId;

            if (_currentProvider == null)
                _currentProvider = await ResolveProviderAsync();

            RaiseCurrentProviderChanged();
            _currentChatViewModel = new ChatViewModel(_aiClient, _currentProvider);
            _currentChatViewModel.ProviderSessionId = displaySessionId;
            _currentChatViewModel.SelectedModel = response.info?.model ?? _currentProvider?.defaultModel;
            ApplyGenerationSettings();

            // Load messages from the server (source of truth). Prefer the REST history endpoint,
            // which returns structured messages incl. tool_calls, so tool blocks survive a reload
            // (the WS resume payload is text-only). Fall back to the WS text messages on failure.
            _currentChatViewModel.Messages.Clear();
            List<ChatMessage> richHistory = null;
            if (ShouldFetchRichHermesHistory(response))
            {
                try
                {
                    var rest = selector.RestClient;
                    if (rest != null)
                    {
                        JToken historyJson = await rest.GetSessionMessages(displaySessionId);
                        richHistory = BuildMessagesFromServerHistory(historyJson);
                    }
                }
                catch (Exception ex)
                {
                    NeonLogger.LogWarning("[ChatService] Rich Hermes history fetch failed: " + ex.Message);
                }
            }

            if (richHistory != null && richHistory.Count > 0)
            {
                for (int i = 0; i < richHistory.Count; i++)
                    _currentChatViewModel.Messages.Add(richHistory[i]);
            }
            else if (response.messages != null)
            {
                for (int i = 0; i < response.messages.Length; i++)
                {
                    var msg = response.messages[i];
                    if (msg == null) continue;
                    _currentChatViewModel.Messages.Add(new Data.Models.ChatMessage
                    {
                        role = msg.role ?? "assistant",
                        content = msg.text ?? "",
                        unixTimeSeconds = msg.timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    });
                }
            }

            _currentSession = new ChatSession
            {
                sessionId = displaySessionId,
                providerId = _currentProvider?.id,
                providerSessionId = displaySessionId,
                providerRuntimeSessionId = response.session_id,
                selectedModel = response.info?.model ?? _currentProvider?.defaultModel,
                title = response.info?.title ?? "Hermes session",
                updatedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                messages = new List<ChatMessage>(_currentChatViewModel.Messages),
                folder = string.Empty
            };
            // session.resume ran in this profile, so the chat belongs to it.
            _currentSessionHermesProfile = profile;

            NeonLogger.Log("Hermes session resumed: " + displaySessionId);
        }

        private static bool ShouldFetchRichHermesHistory(SessionResumeResponse response)
        {
            if (response == null || response.messages == null || response.messages.Length == 0)
                return true;

            return response.messages.Length <= RichHermesHistoryMessageThreshold;
        }

        /// <summary>
        /// Reconstruct the transcript from the gateway's structured message history. Tool calls are
        /// stored server-side as separate assistant messages (empty content + tool_calls[]); we group
        /// consecutive non-user messages into one assistant bubble with interleaved text + tool
        /// segments, matching how a streamed turn looks. Tool results (role="tool") fill each tool's
        /// expandable details. Returns null if the payload isn't a recognizable message array.
        /// </summary>
        private static List<ChatMessage> BuildMessagesFromServerHistory(JToken json)
        {
            if (json == null)
                return null;

            JToken arr = json;
            if (json.Type == JTokenType.Object)
            {
                JToken nested = json["messages"] ?? json["items"] ?? json["data"];
                if (nested != null)
                    arr = nested;
            }
            if (arr == null || arr.Type != JTokenType.Array)
                return null;

            var result = new List<ChatMessage>();
            ChatMessage turn = null;
            var toolByCallId = new Dictionary<string, ChatMessageSegment>();

            foreach (JToken m in arr.Children())
            {
                if (m == null || m.Type != JTokenType.Object)
                    continue;

                string role = (string)m["role"];
                if (string.IsNullOrEmpty(role))
                    role = "assistant";
                string content = (string)m["content"] ?? string.Empty;
                long ts = ReadUnixSeconds(m["timestamp"]);

                if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
                {
                    turn = null;
                    toolByCallId.Clear();
                    result.Add(new ChatMessage { role = "user", content = content, unixTimeSeconds = ts });
                    continue;
                }
                if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase))
                    continue; // system prompts aren't shown in the transcript
                if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
                {
                    string tcId = (string)m["tool_call_id"];
                    ChatMessageSegment seg;
                    if (!string.IsNullOrEmpty(tcId) && toolByCallId.TryGetValue(tcId, out seg) &&
                        !string.IsNullOrWhiteSpace(content))
                    {
                        seg.details = TruncateToolDetails(content);
                    }
                    continue;
                }

                // assistant turn (accumulate until the next user message)
                if (turn == null)
                {
                    turn = new ChatMessage
                    {
                        role = "assistant",
                        content = string.Empty,
                        unixTimeSeconds = ts,
                        segments = new List<ChatMessageSegment>()
                    };
                    result.Add(turn);
                }
                if (turn.segments == null)
                    turn.segments = new List<ChatMessageSegment>();

                JToken toolCalls = m["tool_calls"];
                if (toolCalls != null && toolCalls.Type == JTokenType.Array)
                {
                    foreach (JToken tc in toolCalls.Children())
                    {
                        if (tc == null || tc.Type != JTokenType.Object)
                            continue;
                        JToken fn = tc["function"];
                        string name = (fn != null ? (string)fn["name"] : null) ?? (string)tc["name"] ?? "tool";
                        string args = fn != null ? (string)fn["arguments"] : null;
                        string callId = (string)tc["call_id"] ?? (string)tc["id"];
                        if (string.IsNullOrEmpty(callId))
                            callId = Guid.NewGuid().ToString("N");

                        var seg = new ChatMessageSegment
                        {
                            kind = ChatMessageSegment.ToolKind,
                            key = name + "\x01" + callId,
                            tool = name,
                            label = BuildToolLabel(name, args),
                            emoji = string.Empty,
                            status = "complete"
                        };
                        turn.segments.Add(seg);
                        toolByCallId[callId] = seg;
                    }
                }

                if (!string.IsNullOrWhiteSpace(content))
                {
                    turn.segments.Add(new ChatMessageSegment { kind = ChatMessageSegment.TextKind, text = content });
                    turn.content = string.IsNullOrEmpty(turn.content) ? content : (turn.content + "\n" + content);
                    turn.unixTimeSeconds = ts;
                }
            }

            return result;
        }

        private static long ReadUnixSeconds(JToken token)
        {
            if (token != null)
            {
                try
                {
                    if (token.Type == JTokenType.Integer)
                        return (long)token;
                    if (token.Type == JTokenType.Float)
                        return (long)(double)token;
                    if (token.Type == JTokenType.String)
                    {
                        double d;
                        if (double.TryParse((string)token, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out d))
                            return (long)d;
                    }
                }
                catch { }
            }
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        private static string TruncateToolDetails(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= 10000)
                return text;
            return text.Substring(0, 10000) + "\n... [truncated]";
        }

        /// <summary>Derive a short tool label from its JSON arguments (e.g. the command/query).</summary>
        private static string BuildToolLabel(string name, string argsJson)
        {
            if (string.IsNullOrWhiteSpace(argsJson))
                return name ?? string.Empty;
            try
            {
                JToken parsed = JToken.Parse(argsJson);
                if (parsed != null && parsed.Type == JTokenType.Object)
                {
                    JObject obj = (JObject)parsed;
                    string[] preferred = { "command", "cmd", "query", "q", "pattern", "path", "file", "filename", "url", "text", "name" };
                    for (int i = 0; i < preferred.Length; i++)
                    {
                        JToken v = obj[preferred[i]];
                        if (v != null && v.Type == JTokenType.String)
                        {
                            string s = (string)v;
                            if (!string.IsNullOrWhiteSpace(s))
                                return ShortenLabel(s);
                        }
                    }
                    foreach (JProperty p in obj.Properties())
                    {
                        if (p.Value != null && p.Value.Type == JTokenType.String)
                        {
                            string s = (string)p.Value;
                            if (!string.IsNullOrWhiteSpace(s))
                                return ShortenLabel(s);
                        }
                    }
                }
            }
            catch { }
            return name ?? string.Empty;
        }

        private static string ShortenLabel(string s)
        {
            if (string.IsNullOrEmpty(s))
                return string.Empty;
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > 80 ? s.Substring(0, 80) + "..." : s;
        }

        /// <summary>
        /// Fetch list of Hermes sessions from REST API.
        /// </summary>
        public async Task<List<HermesSession>> GetHermesSessionsAsync()
        {
            var selector = GlobalBackendSelector.Instance;
            if (selector?.RestClient == null)
                return new List<HermesSession>();

            try
            {
                var all = new List<HermesSession>();
                int offset = 0;
                const int pageSize = 100;

                while (true)
                {
                    var result = await selector.RestClient.ListSessions(pageSize, 0, offset);
                    if (result?.sessions == null || result.sessions.Length == 0)
                        break;

                    all.AddRange(result.sessions);

                    int total = result.total > 0 ? result.total : all.Count;
                    offset += result.sessions.Length;
                    if (offset >= total || result.sessions.Length < pageSize)
                        break;
                }

                return all;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ChatService] Failed to fetch Hermes sessions: " + ex.Message);
                return new List<HermesSession>();
            }
        }

        /// <summary>
        /// Delete a Hermes session via REST API.
        /// </summary>
        public async Task DeleteHermesSessionAsync(string hermesSessionId)
        {
            var selector = GlobalBackendSelector.Instance;
            if (selector == null || selector.RestClient == null)
                return;

            try
            {
                if (selector.SessionManager != null && selector.SessionManager.IsConnected)
                    await selector.SessionManager.CloseSession(hermesSessionId);

                await selector.RestClient.DeleteSession(hermesSessionId);
                NeonLogger.Log("Hermes session deleted: " + hermesSessionId);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ChatService] Failed to delete Hermes session: " + ex.Message);
            }
        }

        /// <summary>Hermes backend profile currently selected, or null for the gateway default.</summary>
        public string ActiveHermesProfile
        {
            get
            {
                var selector = GlobalBackendSelector.Instance;
                return selector != null ? selector.ActiveHermesProfile : null;
            }
        }

        /// <summary>
        /// Switch the Hermes backend profile — a FULL context switch. All profile-scoped REST
        /// traffic and the WebSocket are retargeted at <paramref name="profileName"/>, and the
        /// open chat is dropped locally when it belongs to another profile (server sessions are
        /// left untouched: switching back lists and opens them again). No session is created —
        /// the caller refreshes the session list and shows the existing empty/new-chat state.
        /// </summary>
        public async Task SwitchHermesProfileAsync(string profileName)
        {
            var selector = GlobalBackendSelector.Instance;
            if (selector == null)
                return;

            string normalized = string.IsNullOrWhiteSpace(profileName) ? null : profileName.Trim();
            if (string.Equals(selector.ActiveHermesProfile, normalized, StringComparison.Ordinal))
                return;

            // Every in-memory stream belongs to the old profile's socket, which is about to be
            // replaced — drop them (and the open chat) instead of leaving orphaned generations.
            if (!string.Equals(_currentSessionHermesProfile, normalized, StringComparison.Ordinal))
                DropHermesSessionStateLocally();

            await selector.SwitchHermesProfileAsync(normalized);
        }

        /// <summary>
        /// Forget the open Hermes chat and every per-session stream WITHOUT touching the server:
        /// no session.close, no delete, no session.create. Used on a profile switch.
        /// </summary>
        private void DropHermesSessionStateLocally()
        {
            foreach (var kv in _hermesStreams)
            {
                ClearStreamPendingState(kv.Value);
                ClearSessionAttention(kv.Key);
            }
            _hermesStreams.Clear();

            ClearCurrentSessionWithoutSaving();
            _currentSessionHermesProfile = null;
        }

        public bool CancelCurrentGeneration()
        {
            if (_chatTransport != null && _chatTransport.IsConnected)
            {
                string sid = _currentSession?.providerSessionId;
                if (!string.IsNullOrEmpty(sid))
                {
                    _ = _chatTransport.Interrupt(sid);
                    HermesStream stream = GetStream(sid);
                    if (stream != null)
                    {
                        CompleteInterruptedHermesStream(stream);
                        RaiseSessionStatesChanged();
                        return true;
                    }
                }
                return false;
            }
            _currentChatViewModel?.CancelGeneration();
            return true;
        }

        public async Task RegenerateAsync(Action<string> onStreamToken = null, Action<ToolProgressInfo> onToolProgress = null)
        {
            if (_currentChatViewModel == null)
                await GetOrCreateChatAsync();

            _currentChatViewModel.UseStreaming = UseStreaming;
            await _currentChatViewModel.RegenerateAsync(UseStreaming ? onStreamToken : null, UseStreaming ? onToolProgress : null);
            EmitLatestAssistantResponse();

            SaveCurrentSession();
        }

        public Task SendMessageAsync(string message, Action<string> onStreamToken = null)
        {
            return SendMessageAsync(message, null, onStreamToken, null);
        }

        // Voice audio to attach to the next outgoing user message, so the chat bubble keeps its
        // playback button after re-renders (the optimistic copy alone is lost on re-render).
        private string _pendingVoiceAudioPath;
        private float _pendingVoiceDurationSecs;

        /// <summary>Attach a recorded WAV to the next user message sent. Cleared once consumed.</summary>
        public void SetPendingVoiceAudio(string audioPath, float durationSecs)
        {
            _pendingVoiceAudioPath    = audioPath;
            _pendingVoiceDurationSecs = durationSecs;
        }

        public async Task SendMessageAsync(
            string message,
            IReadOnlyList<ChatAttachment> attachments,
            Action<string> onStreamToken = null,
            Action<ToolProgressInfo> onToolProgress = null)
        {
            // Hermes backend: route through WebSocket transport
            if (_chatTransport != null)
            {
                await SendViaTransport(message, attachments, onStreamToken, onToolProgress);
                return;
            }

            // A Hermes provider must never use the OpenAI HTTP path: that POSTs to
            // {baseUrl}/chat/completions, which the WebSocket-only Hermes server rejects with
            // HTTP 405. If the transport is missing (e.g. backend mode was never switched to
            // Hermes), bring it up on demand and route through it instead of POSTing.
            if (_currentProvider == null)
                _currentProvider = await ResolveProviderAsync();
            if (_currentProvider == null || !_currentProvider.isEnabled)
                throw new InvalidOperationException("Provider is not configured.");
            if (IsHermesProvider(_currentProvider))
            {
                await EnsureHermesTransportAsync();
                if (_chatTransport != null)
                {
                    if (_currentSession == null)
                        await StartNewSessionAsync();
                    await SendViaTransport(message, attachments, onStreamToken, onToolProgress);
                    return;
                }

                throw new InvalidOperationException(
                    "Hermes provider selected but the WebSocket transport is unavailable. " +
                    "Switch the backend to Hermes and verify the provider URL/key.");
            }

            // OpenAI backend: existing HTTP path
            if (_currentChatViewModel == null)
            {
                await GetOrCreateChatAsync();
            }

            if (_currentProvider == null || !_currentProvider.isEnabled || _currentChatViewModel == null)
                throw new InvalidOperationException("Provider is not configured.");

            if (_currentSession == null)
                await StartNewSessionAsync();
            if (_currentSession == null)
                throw new InvalidOperationException("Chat session is not ready.");

            _currentChatViewModel.UseStreaming = UseStreaming;
            _currentChatViewModel.InputMessage = message;
            _currentChatViewModel.PendingAttachments.Clear();
            if (attachments != null)
            {
                for (int i = 0; i < attachments.Count; i++)
                {
                    var attachment = attachments[i];
                    if (attachment == null)
                        continue;

                    _currentChatViewModel.PendingAttachments.Add(new ChatAttachment
                    {
                        kind = string.IsNullOrWhiteSpace(attachment.kind) ? "image" : attachment.kind,
                        name = attachment.name,
                        path = attachment.path,
                        mediaType = attachment.mediaType
                    });
                }
            }
            await _currentChatViewModel.SendAsync(UseStreaming ? onStreamToken : null, UseStreaming ? onToolProgress : null);
            EmitLatestAssistantResponse();

            SaveCurrentSession();
        }

        /// <summary>
        /// Send a message via Hermes WebSocket transport. The send is pinned to the foreground
        /// session's id and streams into that session's own context — switching the UI to another
        /// session does not disturb or misroute this generation.
        /// </summary>
        private async Task SendViaTransport(string message, IReadOnlyList<ChatAttachment> attachments = null, Action<string> onStreamToken = null, Action<ToolProgressInfo> onToolProgress = null)
        {
            if (_chatTransport == null)
                return;

            // Ensure the foreground chat has a server session id (create if brand-new).
            await EnsureHermesSessionReadyAsync();
            if (_currentChatViewModel == null)
                await GetOrCreateChatAsync();

            string sid = _currentSession != null ? _currentSession.providerSessionId : null;
            if (string.IsNullOrWhiteSpace(sid))
                throw new InvalidOperationException("Hermes session id is missing.");

            // Pin this generation to the session's stream context. The transport multiplexes by
            // session_id, so events route here regardless of which session the UI later views.
            HermesStream stream = GetOrCreateStream(sid);
            stream.viewModel = _currentChatViewModel;
            stream.session = _currentSession;
            stream.tokenCb = onStreamToken;
            stream.toolCb = onToolProgress;
            stream.lastError = null;
            stream.pendingUserContent = message;
            stream.interrupted = false;
            stream.lastActivityTime = DateTime.UtcNow;
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            stream.complete = completion;

            // Optimistic user bubble — removed only if the submit itself fails (e.g. session busy),
            // because then the server never saw the message.
            ChatMessage localUserMessage = new ChatMessage
            {
                role = "user",
                content = message,
                attachments = CloneChatAttachments(attachments),
                unixTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
            if (!string.IsNullOrEmpty(_pendingVoiceAudioPath))
            {
                localUserMessage.audioPath         = _pendingVoiceAudioPath;
                localUserMessage.audioDurationSecs = _pendingVoiceDurationSecs;
                _pendingVoiceAudioPath    = null;
                _pendingVoiceDurationSecs = 0f;
            }
            stream.viewModel.Messages.Add(localUserMessage);

            bool submitAcknowledged = false;
            try
            {
                // Attach images to the Hermes session first, then submit text normally.
                if (attachments != null && attachments.Count > 0)
                {
                    foreach (var att in attachments)
                    {
                        if (att == null || string.IsNullOrEmpty(att.path))
                            continue;
                        string b64 = null;
                        try
                        {
                            byte[] fileBytes = System.IO.File.ReadAllBytes(att.path);
                            b64 = System.Convert.ToBase64String(fileBytes);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning("[ChatService] Failed to read attachment: " + ex.Message);
                        }

                        if (!string.IsNullOrEmpty(b64))
                            await _chatTransport.AttachImageBytes(sid, b64);
                    }
                }

                // Send via WebSocket — this returns after RPC ack.
                try
                {
                    await _chatTransport.SendMessage(sid, message);
                }
                catch (Exception ex)
                {
                    if (!IsSessionNotFoundError(ex))
                        throw;

                    NeonLogger.LogWarning("[Hermes] Session id was stale; resuming and retrying prompt.submit once.");
                    await EnsureHermesSessionReadyAsync(true);
                    sid = _currentSession != null ? _currentSession.providerSessionId : sid;
                    if (string.IsNullOrWhiteSpace(sid))
                        throw;

                    stream = GetOrCreateStream(sid);
                    stream.viewModel = _currentChatViewModel;
                    stream.session = _currentSession;
                    stream.tokenCb = onStreamToken;
                    stream.toolCb = onToolProgress;
                    stream.lastError = null;
                    stream.pendingUserContent = message;
                    stream.interrupted = false;
                    stream.lastActivityTime = DateTime.UtcNow;
                    stream.complete = completion;

                    if (stream.viewModel != null && stream.viewModel.Messages != null &&
                        !ContainsMessageReference(stream.viewModel.Messages, localUserMessage))
                    {
                        stream.viewModel.Messages.Add(localUserMessage);
                    }

                    await _chatTransport.SendMessage(sid, message);
                }
                submitAcknowledged = true;

                await WaitForHermesCompletionAsync(sid, stream, completion);
            }
            catch
            {
                if (!submitAcknowledged && stream.viewModel != null && stream.viewModel.Messages != null)
                    stream.viewModel.Messages.Remove(localUserMessage);
                ClearStreamPendingState(stream);
                throw;
            }
            finally
            {
                if (stream.complete == completion)
                    stream.complete = null;
            }
        }

        private void RaiseCurrentProviderChanged()
        {
            string hash = GetProviderChangeHash(_currentProvider);
            if (string.Equals(hash, _lastProviderChangeHash, StringComparison.Ordinal))
                return;

            _lastProviderChangeHash = hash;
            try { OnCurrentProviderChanged?.Invoke(_currentProvider); }
            catch { }
        }

        private static string GetProviderChangeHash(ProviderConfig provider)
        {
            if (provider == null)
                return "";

            return (provider.id ?? "") + "|"
                + (provider.backendType ?? "") + "|"
                + (provider.baseUrl ?? "") + "|"
                + (provider.apiKey ?? "") + "|"
                + (provider.ttsVoice ?? "") + "|"
                + (provider.ttsModel ?? "") + "|"
                + provider.ttsSpeed.ToString() + "|"
                + (provider.sttLanguage ?? "");
        }

        private async Task WaitForHermesCompletionAsync(string sessionId, HermesStream stream, TaskCompletionSource<bool> completion)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(HermesCompletionMaxWaitMs);

            while (true)
            {
                DateTime now = DateTime.UtcNow;
                if (stream != null && stream.lastActivityTime != default(DateTime) &&
                    (now - stream.lastActivityTime).TotalMilliseconds >= HermesInactivityTimeoutMs)
                {
                    NeonLogger.LogWarning("[Hermes] No generation activity for " +
                        (HermesInactivityTimeoutMs / 60000) + " minutes; reconciling and interrupting stale session " + sessionId);

                    if (await TryReconcileCompletedHermesSessionAsync(sessionId, stream))
                        return;

                    try { await _chatTransport.Interrupt(sessionId); }
                    catch (Exception ex) { NeonLogger.LogWarning("[Hermes] Stale session interrupt failed: " + ex.Message); }

                    FinalizeTimedOutHermesStream(stream);
                    throw new HermesGenerationStalledException(LocalizationExtensions.Get(
                        "chat.hermes.inactivity_timeout",
                        "Hermes stopped responding. The stale generation was cancelled."));
                }

                int delayMs = HermesCompletionPollMs;
                double remainingMs = (deadline - now).TotalMilliseconds;
                if (remainingMs <= 0)
                {
                    NeonLogger.LogWarning("[Hermes] Generation timed out after " + (HermesCompletionMaxWaitMs / 60000) + " minutes");
                    if (await TryReconcileCompletedHermesSessionAsync(sessionId, stream))
                        return;
                    try { await _chatTransport.Interrupt(sessionId); }
                    catch (Exception ex) { NeonLogger.LogWarning("[Hermes] Maximum-time interrupt failed: " + ex.Message); }
                    FinalizeTimedOutHermesStream(stream);
                    throw new HermesGenerationStalledException(LocalizationExtensions.Get(
                        "chat.hermes.max_timeout",
                        "Hermes generation exceeded the maximum allowed time and was cancelled."));
                }
                if (remainingMs < delayMs)
                    delayMs = (int)remainingMs;

                Task completedTask = await Task.WhenAny(completion.Task, Task.Delay(delayMs));
                if (completedTask == completion.Task)
                {
                    bool completed = await completion.Task;
                    if (!completed)
                    {
                        string error = stream == null || string.IsNullOrWhiteSpace(stream.lastError)
                            ? "Hermes generation failed."
                            : stream.lastError;
                        throw new InvalidOperationException(error);
                    }
                    if (stream != null && stream.interrupted)
                        throw new OperationCanceledException();
                    return;
                }

                if (IsHermesRuntimeFinished(sessionId) &&
                    await TryReconcileCompletedHermesSessionAsync(sessionId, stream))
                {
                    return;
                }
            }
        }

        private static bool IsHermesRuntimeFinished(string sessionId)
        {
            var manager = GlobalBackendSelector.Instance?.SessionManager;
            if (manager != null && manager.IsSessionBusy(sessionId))
                return false;
            var runtime = manager != null ? manager.RuntimeInfoFor(sessionId) : null;
            return runtime != null && runtime.running.HasValue && !runtime.running.Value;
        }

        private async Task<bool> TryReconcileCompletedHermesSessionAsync(string sessionId, HermesStream stream)
        {
            if (stream == null || string.IsNullOrEmpty(sessionId))
                return false;

            var selector = GlobalBackendSelector.Instance;
            var rest = selector != null ? selector.RestClient : null;
            if (rest == null)
                return false;

            try
            {
                JToken historyJson = await rest.GetSessionMessages(sessionId);
                List<ChatMessage> history = BuildMessagesFromServerHistory(historyJson);
                if (history == null || history.Count == 0)
                    return false;
                if (!HistoryContainsCompletedPendingTurn(history, stream.pendingUserContent))
                    return false;

                if (stream.viewModel != null)
                {
                    stream.viewModel.Messages.Clear();
                    for (int i = 0; i < history.Count; i++)
                        stream.viewModel.Messages.Add(history[i]);
                }

                if (stream.session != null)
                {
                    stream.session.messages = new List<ChatMessage>(history);
                    stream.session.updatedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                }

                bool isForeground = stream.viewModel == _currentChatViewModel;
                stream.active = false;
                stream.streamingMessage = null;
                stream.buffer = null;
                stream.reasoning = null;
                stream.lastError = null;
                stream.pendingUserContent = null;
                stream.interrupted = false;
                stream.complete?.TrySetResult(true);

                if (isForeground)
                    EmitLatestAssistantResponse();

                RaiseSessionStatesChanged();
                NeonLogger.Log("[Hermes] Reconciled completed session from REST history: " + sessionId);
                return true;
            }
            catch (Exception ex)
            {
                NeonLogger.LogWarning("[Hermes] Completion reconcile failed: " + ex.Message);
                return false;
            }
        }

        private static bool HistoryContainsCompletedPendingTurn(List<ChatMessage> history, string pendingUserContent)
        {
            if (history == null || history.Count == 0)
                return false;
            if (string.IsNullOrWhiteSpace(pendingUserContent))
                return true;

            string pending = NormalizeForComparison(pendingUserContent);
            int userIndex = -1;
            for (int i = history.Count - 1; i >= 0; i--)
            {
                ChatMessage message = history[i];
                if (message == null || !string.Equals(message.role, "user", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.Equals(NormalizeForComparison(message.content), pending, StringComparison.Ordinal))
                {
                    userIndex = i;
                    break;
                }
            }

            if (userIndex < 0)
                return false;

            for (int i = userIndex + 1; i < history.Count; i++)
            {
                ChatMessage message = history[i];
                if (message != null &&
                    string.Equals(message.role, "assistant", StringComparison.OrdinalIgnoreCase) &&
                    MessageHasAssistantOutput(message))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool MessageHasAssistantOutput(ChatMessage message)
        {
            if (message == null)
                return false;
            if (!string.IsNullOrWhiteSpace(message.content))
                return true;
            if (message.segments == null)
                return false;

            for (int i = 0; i < message.segments.Count; i++)
            {
                ChatMessageSegment segment = message.segments[i];
                if (segment != null && !string.IsNullOrWhiteSpace(segment.text))
                    return true;
            }

            return false;
        }

        private static string NormalizeForComparison(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;
            return text.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
        }

        private HermesStream GetStream(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                return null;
            HermesStream s;
            return _hermesStreams.TryGetValue(sessionId, out s) ? s : null;
        }

        private HermesStream GetOrCreateStream(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                return null;
            HermesStream s;
            if (_hermesStreams.TryGetValue(sessionId, out s))
                return s;

            s = new HermesStream();
            s.serverSessionId = sessionId;
            bool isForeground = _currentSession != null &&
                string.Equals(_currentSession.providerSessionId, sessionId, StringComparison.Ordinal) &&
                _currentChatViewModel != null;
            if (isForeground)
            {
                s.viewModel = _currentChatViewModel;
                s.session = _currentSession;
            }
            else
            {
                s.viewModel = new ChatViewModel(_aiClient, _currentProvider);
                s.viewModel.ProviderSessionId = sessionId;
                s.session = new ChatSession
                {
                    sessionId = sessionId,
                    providerId = _currentProvider != null ? _currentProvider.id : null,
                    providerSessionId = sessionId,
                    providerRuntimeSessionId = null,
                    messages = new List<ChatMessage>(),
                    folder = string.Empty
                };
            }
            _hermesStreams[sessionId] = s;
            return s;
        }

        /// <summary>Begin (or reuse) the streaming assistant message for a session's stream.</summary>
        private bool EnsureStreamingMessage(string sessionId)
        {
            HermesStream s = GetOrCreateStream(sessionId);
            if (s == null || s.viewModel == null)
                return false;

            if (s.streamingMessage != null)
            {
                s.active = true;
                if (s.buffer == null) s.buffer = new System.Text.StringBuilder();
                if (s.reasoning == null) s.reasoning = new System.Text.StringBuilder();
                return true;
            }

            s.active = true;
            s.buffer = new System.Text.StringBuilder();
            s.reasoning = new System.Text.StringBuilder();
            s.startTime = DateTime.UtcNow;
            s.lastActivityTime = s.startTime;
            CaptureHermesUsageBaseline(sessionId, s);
            s.streamingMessage = new ChatMessage
            {
                role = "assistant",
                content = string.Empty,
                model = string.Empty,
                unixTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
            s.viewModel.Messages.Add(s.streamingMessage);
            return true;
        }

        private static void CaptureHermesUsageBaseline(string sessionId, HermesStream stream)
        {
            if (stream == null)
                return;

            stream.usageBaselineKnown = false;
            stream.baselineOutput = 0;
            stream.baselineTotal = 0;

            var usage = GlobalBackendSelector.Instance?.SessionManager?.RuntimeInfoFor(sessionId)?.usage;
            if (usage == null)
                return;

            stream.usageBaselineKnown = true;
            stream.baselineOutput = usage.output;
            stream.baselineTotal = usage.total;
        }

        private static void ClearStreamPendingState(HermesStream stream)
        {
            if (stream == null)
                return;
            stream.complete?.TrySetResult(false);
            stream.complete = null;
            stream.active = false;
            stream.streamingMessage = null;
            stream.buffer = null;
            stream.reasoning = null;
            stream.usageBaselineKnown = false;
            stream.baselineOutput = 0;
            stream.baselineTotal = 0;
            stream.tokenCb = null;
            stream.toolCb = null;
            stream.lastError = null;
            stream.pendingUserContent = null;
            stream.interrupted = false;
            stream.lastActivityTime = default(DateTime);
        }

        private static void FinalizeTimedOutHermesStream(HermesStream stream)
        {
            if (stream == null)
                return;

            if (stream.streamingMessage != null)
            {
                stream.streamingMessage.responseTimeSeconds =
                    (float)Math.Max(0, (DateTime.UtcNow - stream.startTime).TotalSeconds);

                if (stream.streamingMessage.segments != null)
                {
                    for (int i = 0; i < stream.streamingMessage.segments.Count; i++)
                    {
                        ChatMessageSegment segment = stream.streamingMessage.segments[i];
                        if (segment == null ||
                            !string.Equals(segment.kind, ChatMessageSegment.ToolKind, StringComparison.OrdinalIgnoreCase) ||
                            !string.Equals(segment.status, "running", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        segment.status = "failed";
                        if (string.IsNullOrEmpty(segment.details))
                        {
                            segment.details = LocalizationExtensions.Get(
                                "chat.hermes.tool_timeout",
                                "No completion event was received before the inactivity timeout.");
                        }
                    }
                }
            }
        }

        private static void CompleteInterruptedHermesStream(HermesStream stream)
        {
            if (stream == null)
                return;

            if (stream.streamingMessage != null)
            {
                string text = null;
                if (stream.buffer != null && stream.buffer.Length > 0)
                    text = stream.buffer.ToString();
                if (!string.IsNullOrEmpty(text))
                {
                    stream.streamingMessage.content = text;
                    NormalizeHermesFinalTextSegments(stream.streamingMessage, text);
                }

                stream.streamingMessage.responseTimeSeconds = (float)(DateTime.UtcNow - stream.startTime).TotalSeconds;
            }

            stream.active = false;
            stream.streamingMessage = null;
            stream.buffer = null;
            stream.reasoning = null;
            stream.lastError = null;
            stream.pendingUserContent = null;
            stream.interrupted = true;
            stream.complete?.TrySetResult(true);
        }

        /// <summary>Detach the foreground UI callbacks so a stream keeps running silently in background.</summary>
        private void DetachForegroundCallbacks()
        {
            string sid = _currentSession != null ? _currentSession.providerSessionId : null;
            HermesStream s = GetStream(sid);
            if (s != null)
            {
                s.tokenCb = null;
                s.toolCb = null;
            }
        }

        /// <summary>
        /// Ensure the Hermes transport is connected and the foreground chat has a display id that
        /// is mapped to a live runtime session id. After reconnects the mapping is gone, so resume
        /// the persisted session before sending.
        /// </summary>
        private async Task EnsureHermesSessionReadyAsync(bool forceResume = false)
        {
            var selector = GlobalBackendSelector.Instance;
            var sessionManager = selector?.SessionManager;
            if (sessionManager == null)
                throw new InvalidOperationException("Hermes session manager is not available.");

            if (!sessionManager.IsConnected)
                await selector.ConnectHermes();

            string desired = _currentSession != null ? _currentSession.providerSessionId : null;
            if (!string.IsNullOrWhiteSpace(desired))
            {
                if (forceResume || !sessionManager.HasRuntimeSessionFor(desired))
                {
                    try
                    {
                        await ResumeHermesSessionAsync(desired);
                        return;
                    }
                    catch (Exception ex)
                    {
                        NeonLogger.LogWarning("[Hermes] Session resume failed (" + ex.Message + "), binding a new remote session to the current chat.");
                        _currentSession.providerSessionId = null;
                        _currentSession.providerRuntimeSessionId = null;
                        if (_currentChatViewModel != null)
                            _currentChatViewModel.ProviderSessionId = null;
                    }
                }
                else
                {
                    sessionManager.SetForegroundSession(desired);
                    return;
                }
            }

            await StartHermesSessionAsync(_currentSession == null);
        }

        private static bool IsSessionNotFoundError(Exception ex)
        {
            string message = ex != null ? ex.Message : null;
            return !string.IsNullOrWhiteSpace(message) &&
                message.IndexOf("session not found", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool ContainsMessageReference(List<ChatMessage> messages, ChatMessage target)
        {
            if (messages == null || target == null)
                return false;

            for (int i = 0; i < messages.Count; i++)
            {
                if (ReferenceEquals(messages[i], target))
                    return true;
            }

            return false;
        }

        // === Hermes Transport Event Handlers (multiplexed by display/persisted session id) ===

        private void HandleHermesStreamStarted(string sessionId)
        {
            EnsureStreamingMessage(sessionId);
            TouchHermesStream(sessionId);
            // A session began producing output — refresh the sidebar "working" indicator.
            RaiseSessionStatesChanged();
        }

        private void HandleHermesDelta(string sessionId, string text)
        {
            if (string.IsNullOrEmpty(text) || !EnsureStreamingMessage(sessionId))
                return;

            HermesStream s = GetStream(sessionId);
            if (s == null || s.streamingMessage == null)
                return;

            s.buffer.Append(text);
            s.lastActivityTime = DateTime.UtcNow;
            s.streamingMessage.content = s.buffer.ToString();

            // Also add as text segment for interleaved rendering with tool segments
            if (s.streamingMessage.segments == null)
                s.streamingMessage.segments = new System.Collections.Generic.List<ChatMessageSegment>();
            ChatMessageSegment last = s.streamingMessage.segments.Count > 0
                ? s.streamingMessage.segments[s.streamingMessage.segments.Count - 1]
                : null;
            if (last != null && string.Equals(last.kind, ChatMessageSegment.TextKind, System.StringComparison.OrdinalIgnoreCase))
            {
                last.text = (last.text ?? "") + text;
            }
            else
            {
                s.streamingMessage.segments.Add(new ChatMessageSegment
                {
                    kind = ChatMessageSegment.TextKind,
                    text = text
                });
            }

            // Notify UI only if this session is the foreground (its callback is set).
            s.tokenCb?.Invoke(text);
        }

        private void HandleHermesComplete(string sessionId, string finalText)
        {
            HermesStream s = GetStream(sessionId);
            bool hadStreamingMessage = s != null && s.streamingMessage != null;
            if (!hadStreamingMessage && !string.IsNullOrWhiteSpace(finalText))
            {
                EnsureStreamingMessage(sessionId);
                s = GetStream(sessionId);
            }

            if (s != null && s.streamingMessage != null)
            {
                string normalizedFinalText = null;

                // Apply final text if we got one
                if (!string.IsNullOrEmpty(finalText))
                {
                    s.streamingMessage.content = finalText;
                    normalizedFinalText = finalText;
                }
                else if (s.buffer != null && s.buffer.Length > 0)
                {
                    s.streamingMessage.content = s.buffer.ToString();
                    normalizedFinalText = s.streamingMessage.content;
                }

                NormalizeHermesFinalTextSegments(s.streamingMessage, normalizedFinalText);

                if (!hadStreamingMessage && !string.IsNullOrEmpty(normalizedFinalText))
                    s.tokenCb?.Invoke(normalizedFinalText);

                // Store reasoning text for expandable display
                if (s.reasoning != null && s.reasoning.Length > 0)
                {
                    s.streamingMessage.reasoning = s.reasoning.ToString();
                }

                // Persist usage from gateway (per session) so stats footer survives re-render
                try
                {
                    s.streamingMessage.responseTimeSeconds = (float)(DateTime.UtcNow - s.startTime).TotalSeconds;
                    var usage = GlobalBackendSelector.Instance?.SessionManager?.RuntimeInfoFor(sessionId)?.usage;
                    int tokenCount = MessageOutputTokenCount(usage, s, normalizedFinalText);
                    if (tokenCount > 0)
                        s.streamingMessage.tokenCount = tokenCount;
                }
                catch { }
            }

            if (s != null)
            {
                bool isForeground = s.viewModel == _currentChatViewModel;
                s.active = false;
                s.streamingMessage = null;
                s.buffer = null;
                s.reasoning = null;
                s.lastError = null;
                s.pendingUserContent = null;

                // Signal the waiting SendViaTransport that generation is done.
                s.complete?.TrySetResult(true);

                // Emit only for the foreground session (drives avatar/TTS for what the user sees).
                if (isForeground)
                    EmitLatestAssistantResponse();
            }
            // Generation finished — refresh the sidebar "working" indicator.
            RaiseSessionStatesChanged();
            // Server is the source of truth in Hermes mode — no local persistence.
        }

        private static int MessageOutputTokenCount(UsageStats usage, HermesStream stream, string text)
        {
            if (usage != null && stream != null && stream.usageBaselineKnown)
            {
                if (usage.output > stream.baselineOutput)
                    return usage.output - stream.baselineOutput;
                if (usage.total > stream.baselineTotal)
                    return usage.total - stream.baselineTotal;
            }

            return EstimateTokenCount(text);
        }

        private static int EstimateTokenCount(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            return Math.Max(1, (text.Length + 3) / 4);
        }

        private void HandleHermesToolUpdate(string sessionId, ToolCallUpdate update)
        {
            if (update == null || !EnsureStreamingMessage(sessionId))
                return;

            HermesStream s = GetStream(sessionId);
            if (s == null || s.streamingMessage == null)
                return;

            // Add tool segment to streaming message
            if (s.streamingMessage.segments == null)
                s.streamingMessage.segments = new System.Collections.Generic.List<ChatMessageSegment>();

            // Desktop: complete + payload.error → isError; Companion uses status "error".
            string status = "running";
            if (update.status == ToolCallStatus.Complete)
                status = !string.IsNullOrEmpty(update.error) ? "error" : "complete";
            s.lastActivityTime = DateTime.UtcNow;
            // The TUI/stdio gateway doesn't include an emoji in the payload, so leave it empty there
            // and let the UI derive a per-tool emoji client-side (ToolCallUiHelper.GetToolEmoji).
            // The API SSE gateway does send one — pass it through. Status is shown separately
            // (left stripe + ● / ✓), so we no longer overwrite the icon with a status glyph.
            string emoji = update.emoji ?? string.Empty;
            string toolId = update.toolId ?? string.Empty;
            // Prefer stable tool_id (Desktop toolCallId). Fall back to name-only so progress
            // label changes still merge into the same card.
            string key = !string.IsNullOrEmpty(toolId)
                ? "id\x01" + toolId
                : (update.name ?? "") + "\x01";
            string label = !string.IsNullOrEmpty(update.preview) ? update.preview : (update.name ?? "");
            string details = update.details;
            if (string.IsNullOrEmpty(details) && !string.IsNullOrEmpty(update.error))
                details = update.error;

            for (int i = 0; i < s.streamingMessage.segments.Count; i++)
            {
                ChatMessageSegment existing = s.streamingMessage.segments[i];
                if (existing == null ||
                    !string.Equals(existing.kind, ChatMessageSegment.ToolKind, System.StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(existing.key, key, System.StringComparison.Ordinal))
                {
                    continue;
                }

                existing.tool = update.name ?? "";
                existing.toolId = toolId;
                if (!string.IsNullOrEmpty(label))
                    existing.label = label;
                if (!string.IsNullOrEmpty(emoji))
                    existing.emoji = emoji;
                existing.status = status;
                if (!string.IsNullOrEmpty(update.inlineDiff))
                    existing.inlineDiff = update.inlineDiff;
                if (!string.IsNullOrEmpty(details))
                    existing.details = details;

                EmitToolProgress(s, existing);
                return;
            }

            ChatMessageSegment created = new ChatMessageSegment
            {
                kind = ChatMessageSegment.ToolKind,
                key = key,
                tool = update.name ?? "",
                toolId = toolId,
                label = label,
                emoji = emoji,
                status = status,
                inlineDiff = update.inlineDiff,
                details = details
            };
            s.streamingMessage.segments.Add(created);
            EmitToolProgress(s, created);
        }

        private static void EmitToolProgress(HermesStream stream, ChatMessageSegment segment)
        {
            if (stream == null || stream.toolCb == null || segment == null)
                return;

            ToolProgressInfo info = new ToolProgressInfo();
            info.tool = segment.tool;
            info.toolId = segment.toolId;
            info.label = segment.label ?? string.Empty;
            info.emoji = segment.emoji ?? string.Empty;
            info.status = segment.status ?? string.Empty;
            info.inlineDiff = segment.inlineDiff;
            info.details = segment.details;
            stream.toolCb.Invoke(info);
        }

        private static void NormalizeHermesFinalTextSegments(ChatMessage message, string finalText)
        {
            if (message == null || string.IsNullOrEmpty(finalText) || message.segments == null)
                return;

            int lastTextIndex = -1;
            for (int i = message.segments.Count - 1; i >= 0; i--)
            {
                ChatMessageSegment segment = message.segments[i];
                if (segment != null &&
                    string.Equals(segment.kind, ChatMessageSegment.TextKind, System.StringComparison.OrdinalIgnoreCase))
                {
                    lastTextIndex = i;
                    break;
                }
            }

            if (lastTextIndex >= 0)
            {
                message.segments[lastTextIndex].text = finalText;
            }
            else
            {
                message.segments.Insert(0, new ChatMessageSegment
                {
                    kind = ChatMessageSegment.TextKind,
                    text = finalText
                });
            }
        }

        private void HandleHermesReasoningDelta(string sessionId, string text)
        {
            HermesStream s = GetStream(sessionId);
            if (s == null || !s.active || s.streamingMessage == null)
                return;
            s.lastActivityTime = DateTime.UtcNow;
            s.reasoning?.Append(text);
        }

        private void TouchHermesStream(string sessionId)
        {
            HermesStream stream = GetStream(sessionId);
            if (stream != null)
                stream.lastActivityTime = DateTime.UtcNow;
        }

        private void HandleHermesError(string sessionId, string error)
        {
            NeonLogger.Log("[Hermes] Error: " + error);

            // Connection-level error (null session id): fail every in-flight stream so no
            // SendViaTransport hangs on its completion TCS.
            if (string.IsNullOrEmpty(sessionId))
            {
                foreach (var kv in _hermesStreams)
                {
                    HermesStream st = kv.Value;
                    if (st == null)
                        continue;
                    st.lastError = error;
                    st.active = false;
                    st.streamingMessage = null;
                    st.buffer = null;
                    st.pendingUserContent = null;
                    st.interrupted = false;
                    st.complete?.TrySetResult(false);
                }
                RaiseSessionStatesChanged();
                return;
            }

            HermesStream s = GetStream(sessionId);
            if (s == null)
                return;
            s.lastError = error;
            s.active = false;
            s.streamingMessage = null;
            s.buffer = null;
            s.pendingUserContent = null;
            s.interrupted = false;
            s.complete?.TrySetResult(false);
            RaiseSessionStatesChanged();
        }

        public async Task<ModelSwitchResult> SetCurrentSessionModelAsync(string modelId)
        {
            if (string.IsNullOrWhiteSpace(modelId))
                throw new ArgumentException("Model is required.", nameof(modelId));

            if (_currentChatViewModel == null)
                await GetOrCreateChatAsync();
            if (_currentSession == null)
                await StartNewSessionAsync();

            if (_currentProvider == null || _currentChatViewModel == null || _currentSession == null)
                throw new InvalidOperationException("Chat session is not ready.");

            if (_chatTransport != null)
                await EnsureHermesSessionReadyAsync(true);

            string requestedModel = modelId.Trim();
            string previousModel = CurrentSessionModel;
            string previousProviderSessionId = _currentChatViewModel.ProviderSessionId;

            ModelSwitchResult result = await _aiClient.ApplySessionModelAsync(
                _currentProvider,
                requestedModel,
                previousProviderSessionId);

            if (!result.Success)
                return result;

            _currentChatViewModel.SelectedModel = requestedModel;
            _currentChatViewModel.ProviderSessionId = result.IsHermes
                ? result.ProviderSessionId
                : null;

            _currentSession.selectedModel = requestedModel;
            _currentSession.providerSessionId = _currentChatViewModel.ProviderSessionId;
            SaveCurrentSession();

            NeonLogger.Log(
                $"Session model applied: provider={_currentProvider.id}, old={previousModel}, new={requestedModel}, providerSessionId={_currentChatViewModel.ProviderSessionId ?? "<null>"}");

            return result;
        }

        private void EmitLatestAssistantResponse()
        {
            var messages = _currentChatViewModel?.Messages;
            if (messages == null || messages.Count == 0)
                return;

            for (int i = messages.Count - 1; i >= 0; i--)
            {
                var message = messages[i];
                if (message?.role == "assistant" && !string.IsNullOrWhiteSpace(message.content))
                {
                    OnAssistantResponse?.Invoke(message.content);
                    return;
                }
            }
        }

        public async Task<string> SummarizeCurrentConversationAsync(int maxMessages = 12)
        {
            if (_currentChatViewModel == null)
                await GetOrCreateChatAsync();

            var sourceMessages = _currentChatViewModel?.Messages;
            if (sourceMessages == null || sourceMessages.Count == 0)
                return LocalizationExtensions.Get("chat.summary.empty", "Пока нет сообщений для краткого пересказа.");

            var provider = _currentProvider != null && _currentProvider.isEnabled
                ? _currentProvider
                : await GetActiveProviderForCurrentBackendAsync();
            if (provider == null)
                return LocalizationExtensions.Get("chat.summary.provider_not_configured", "Провайдер не настроен.");

            var requestMessages = new List<AiChatMessage>();
            int start = Math.Max(0, sourceMessages.Count - Math.Max(1, maxMessages));
            for (int i = start; i < sourceMessages.Count; i++)
            {
                var message = sourceMessages[i];
                string content = BuildSummaryMessageContent(message);
                bool hasText = !string.IsNullOrWhiteSpace(content);
                bool hasAttachments = message?.attachments != null && message.attachments.Count > 0;
                if (message == null || string.IsNullOrWhiteSpace(message.role) || (!hasText && !hasAttachments))
                    continue;

                requestMessages.Add(new AiChatMessage
                {
                    role = NormalizeSummaryRole(message.role),
                    content = hasText ? content : "[image]"
                });
            }

            if (requestMessages.Count == 0)
                return LocalizationExtensions.Get("chat.summary.empty", "Пока нет сообщений для краткого пересказа.");

            requestMessages.Add(new AiChatMessage
            {
                role = "user",
                content = LocalizationExtensions.Get("chat.summary.user_prompt", "Сделай короткое резюме диалога на русском языке (2-4 предложения).")
            });

            string requestModel = !string.IsNullOrWhiteSpace(CurrentSessionModel)
                ? CurrentSessionModel
                : provider.defaultModel;
            if (string.IsNullOrWhiteSpace(requestModel))
                return LocalizationExtensions.Get("chat.summary.provider_not_configured", "Провайдер не настроен.");

            var request = new AiChatRequest
            {
                model = requestModel,
                providerSessionId = null,
                temperature = 0.2f,
                maxTokens = 140,
                systemPrompt = LocalizationExtensions.Get("chat.summary.system_prompt", "Ты помощник, который кратко и точно суммирует переписку на русском языке."),
                messages = requestMessages
            };

            var response = await _aiClient.SendMessageAsync(provider, request);
            var summary = response?.content?.Trim();
            return string.IsNullOrWhiteSpace(summary)
                ? LocalizationExtensions.Get("chat.summary.failed", "Не удалось получить резюме.")
                : summary;
        }

        private static string NormalizeSummaryRole(string role)
        {
            if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
                return "user";
            if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase))
                return "system";
            return "assistant";
        }

        private static string BuildSummaryMessageContent(ChatMessage message)
        {
            if (message == null)
                return null;
            if (!string.IsNullOrWhiteSpace(message.content))
                return message.content;
            if (message.segments == null || message.segments.Count == 0)
                return null;

            var sb = new StringBuilder();
            for (int i = 0; i < message.segments.Count; i++)
            {
                ChatMessageSegment segment = message.segments[i];
                if (segment == null ||
                    !string.Equals(segment.kind, ChatMessageSegment.TextKind, StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(segment.text))
                {
                    continue;
                }

                if (sb.Length > 0)
                    sb.AppendLine();
                sb.Append(segment.text.Trim());
            }

            return sb.Length == 0 ? null : sb.ToString();
        }

        private void SyncFromProvider(ProviderConfig provider)
        {
            if (provider == null) return;
            Temperature = provider.temperature;
            MaxTokens = provider.maxTokens;
        }

        private void ApplyGenerationSettings()
        {
            if (_currentChatViewModel == null) return;

            _currentChatViewModel.Temperature = Temperature;
            _currentChatViewModel.MaxTokens = MaxTokens;
            _currentChatViewModel.SystemPrompt = SystemPrompt;
        }


        private async Task LoadLatestSessionAsync(string preferredProviderId = null)
        {
            var sessions = GetSortedSessions();
            var fallback = await FindFallbackSessionAsync(sessions, preferredProviderId);
            if (fallback != null)
            {
                await SwitchToSessionAsync(fallback, preferredProviderId);
                return;
            }

            ClearCurrentSessionWithoutSaving();
        }

        private async Task<ChatSession> FindFallbackSessionAsync(List<ChatSession> sessions, string preferredProviderId = null)
        {
            if (sessions == null)
                return null;

            var selector = GlobalBackendSelector.Instance;
            bool hasBackendMode = selector != null;
            bool hermesMode = hasBackendMode && selector.CurrentMode == BackendMode.Hermes;

            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                if (session == null)
                    continue;

                ProviderConfig provider = null;
                if (!string.IsNullOrWhiteSpace(session.providerId))
                    provider = await TryGetProviderByIdAsync(session.providerId);
                else if (!string.IsNullOrWhiteSpace(preferredProviderId))
                    provider = await TryGetProviderByIdAsync(preferredProviderId);
                else if (_currentProvider != null && _currentProvider.isEnabled)
                    provider = _currentProvider;
                else
                    provider = await GetActiveProviderForCurrentBackendAsync();

                if (provider == null)
                    continue;

                if (hasBackendMode && IsHermesProvider(provider) != hermesMode)
                    continue;

                return session;
            }

            return null;
        }

        private async Task<ProviderConfig> ResolveProviderAsync(string providerId = null)
        {
            if (!string.IsNullOrWhiteSpace(providerId))
            {
                var provider = await _providerManager.GetProviderByIdAsync(providerId);
                if (provider != null && provider.isEnabled)
                    return provider;
            }

            if (_currentProvider != null && _currentProvider.isEnabled)
                return _currentProvider;

            return await GetActiveProviderForCurrentBackendAsync();
        }

        private async Task<ProviderConfig> GetActiveProviderForCurrentBackendAsync(string preferredProviderId = null, bool fallbackToFirst = true)
        {
            var selector = GlobalBackendSelector.Instance;
            BackendMode mode = selector != null ? selector.CurrentMode : BackendMode.OpenAI;
            return await _providerManager.GetActiveProviderForBackendAsync(mode, preferredProviderId, fallbackToFirst);
        }

        private async Task<ProviderConfig> TryGetProviderByIdAsync(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId))
                return null;

            var provider = await _providerManager.GetProviderByIdAsync(providerId);
            return provider != null && provider.isEnabled ? provider : null;
        }

        private void SaveCurrentSession()
        {
            if (_currentSession == null || _currentChatViewModel == null)
                return;

            if (!SaveChatHistory)
                return;

            // Hermes mode: the gateway DB is the source of truth — never write local JSON.
            if (IsHermesProvider(_currentProvider))
                return;

            _currentSession.messages = new List<ChatMessage>(_currentChatViewModel.Messages);
            _currentSession.updatedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _currentSession.providerId = _currentProvider?.id ?? _currentSession.providerId;
            _currentSession.providerSessionId = _currentChatViewModel.ProviderSessionId;
            _currentSession.selectedModel = _currentChatViewModel.SelectedModel;
            _currentSession.title = BuildSessionTitle(_currentSession);

            var sessions = _sessionRepository.GetAll();
            var index = sessions.FindIndex(session => session.sessionId == _currentSession.sessionId);
            if (index >= 0)
            {
                sessions[index] = _currentSession;
            }
            else
            {
                sessions.Add(_currentSession);
            }

            sessions = sessions
                .OrderByDescending(session => session.updatedAtUnix)
                .ToList();

            _sessionRepository.SaveAll(sessions);
        }

        /// <summary>
        /// Persists current session after external/manual changes to messages (edit/delete).
        /// Called from ChatController after mutating CurrentChatViewModel.Messages directly.
        /// </summary>
        public async Task SaveCurrentSessionAsync()
        {
            SaveCurrentSession();
            await Task.CompletedTask;
        }

        /// <summary>
        /// Appends deep-copied messages to another (target) session and persists the change.
        /// Used by forward-selected (U-33). Returns number of messages appended.
        /// </summary>
        public async Task<int> AppendMessagesToSessionAsync(string targetSessionId, List<ChatMessage> messages)
        {
            if (string.IsNullOrWhiteSpace(targetSessionId) || messages == null || messages.Count == 0)
                return 0;

            var sessions = _sessionRepository.GetAll();
            int idx = -1;
            for (int i = 0; i < sessions.Count; i++)
            {
                if (sessions[i].sessionId == targetSessionId)
                {
                    idx = i;
                    break;
                }
            }
            if (idx < 0)
                return 0;

            var target = sessions[idx];
            if (target.messages == null)
                target.messages = new List<ChatMessage>();

            int added = 0;
            for (int m = 0; m < messages.Count; m++)
            {
                var original = messages[m];
                if (original == null)
                    continue;

                // Snapshot via JsonUtility (matches storage serialization, handles segments/tool_calls/attachments)
                string json = JsonUtility.ToJson(original);
                var copy = JsonUtility.FromJson<ChatMessage>(json);
                if (copy != null)
                {
                    target.messages.Add(copy);
                    added++;
                }
            }

            if (added > 0)
            {
                target.updatedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                target.title = BuildSessionTitle(target);
                sessions[idx] = target;
                _sessionRepository.SaveAll(sessions);
            }

            await Task.CompletedTask;
            return added;
        }

        private List<ChatSession> GetSortedSessions()
        {
            if (!SaveChatHistory)
                return new List<ChatSession>();

            return _sessionRepository.GetAll()
                .OrderByDescending(session => session.updatedAtUnix)
                .ToList();
        }

        private static string BuildSessionTitle(ChatSession session)
        {
            var firstUserMessage = session.messages?
                .FirstOrDefault(message => message.role == "user" && !string.IsNullOrWhiteSpace(message.content));

            if (firstUserMessage == null)
                return string.IsNullOrWhiteSpace(session.title) ? "New chat" : session.title;

            var title = firstUserMessage.content.Trim();
            return title.Length <= 48 ? title : title.Substring(0, 48) + "...";
        }

        private static List<ChatAttachment> CloneChatAttachments(IReadOnlyList<ChatAttachment> attachments)
        {
            if (attachments == null || attachments.Count == 0)
                return null;

            var clone = new List<ChatAttachment>(attachments.Count);
            for (int i = 0; i < attachments.Count; i++)
            {
                ChatAttachment attachment = attachments[i];
                if (attachment == null)
                    continue;

                clone.Add(new ChatAttachment
                {
                    kind = attachment.kind,
                    name = attachment.name,
                    path = attachment.path,
                    mediaType = attachment.mediaType
                });
            }

            return clone.Count > 0 ? clone : null;
        }

        private void ResetRemoteSessionState()
        {
            if (_currentSession != null)
            {
                _currentSession.providerSessionId = null;
                _currentSession.providerRuntimeSessionId = null;
            }

            if (_currentChatViewModel != null)
                _currentChatViewModel.ProviderSessionId = null;
        }

        public void ClearActiveProviderState()
        {
            bool hadProvider = _currentProvider != null;
            _currentProvider = null;
            _currentSession = null;
            _currentChatViewModel = null;
            if (hadProvider)
                RaiseCurrentProviderChanged();
        }

        public void ClearCurrentSessionState()
        {
            ClearCurrentSessionWithoutSaving();
        }

        private void ClearCurrentSessionWithoutSaving()
        {
            _currentSession = null;
            _currentChatViewModel = null;
        }
    }
}
