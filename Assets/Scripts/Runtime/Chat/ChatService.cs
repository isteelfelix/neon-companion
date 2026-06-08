using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Api;
using NeonCompanion.Runtime.Api.Hermes;
using NeonCompanion.Runtime.Api.Models;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Data.Repositories;
using NeonCompanion.Runtime.Localization;
using NeonCompanion.Runtime.UI.Chat;
using UnityEngine;

namespace NeonCompanion.Runtime.Chat
{
    public sealed class ChatService
    {
        private readonly IAiClient _aiClient;
        private readonly ProviderManager _providerManager;
        private readonly IChatSessionRepository _sessionRepository;
        private IChatTransport _chatTransport;
        private ChatViewModel _currentChatViewModel;
        private ChatSession _currentSession;
        private ProviderConfig _currentProvider;

        // Hermes streaming state
        private Action<string> _hermesStreamTokenCallback;
        private Action<string, string, string, string> _hermesToolProgressCallback;
        private ChatMessage _hermesStreamingMessage;
        private System.Text.StringBuilder _hermesStreamBuffer;
        private bool _hermesStreamActive;
        private TaskCompletionSource<bool> _hermesGenerationComplete;
        private DateTime _hermesStreamStartTime;
        private int _previousSessionTotal;
        private System.Text.StringBuilder _hermesReasoningBuffer;
        private string _hermesLastError;

        public event Action<string> OnAssistantResponse;

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

            _chatTransport = transport;

            // Wire new transport
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

            SyncFromProvider(_currentProvider);
            _currentChatViewModel = new ChatViewModel(_aiClient, _currentProvider);
            _currentChatViewModel.ProviderSessionId = _currentSession?.providerSessionId;
            _currentChatViewModel.SelectedModel = _currentSession?.selectedModel ?? _currentProvider?.defaultModel;
            ApplyGenerationSettings();

            NeonLogger.Log("Chat session ready.");
            return _currentChatViewModel;
        }

        public async Task<List<ChatSession>> GetAllSessionsAsync()
        {
            return await Task.FromResult(GetSortedSessions());
        }

        public async Task DeleteSessionAsync(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                return;

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

            _currentSession = session;

            var sessionProvider = await TryGetProviderByIdAsync(session.providerId);
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

            SyncFromProvider(_currentProvider);

            _currentChatViewModel = new ChatViewModel(_aiClient, _currentProvider);
            _currentChatViewModel.ProviderSessionId = session.providerSessionId;
            _currentChatViewModel.SelectedModel = string.IsNullOrWhiteSpace(session.selectedModel)
                ? _currentProvider?.defaultModel
                : session.selectedModel;
            ApplyGenerationSettings();

            _currentChatViewModel.Messages.Clear();
            foreach (var msg in session.messages ?? new List<ChatMessage>())
            {
                _currentChatViewModel.Messages.Add(msg);
            }

            NeonLogger.Log($"Switched to session {session.sessionId}");
        }

        public async Task ClearCurrentSessionAsync()
        {
            if (_currentChatViewModel == null) return;

            _currentChatViewModel.Messages.Clear();

            if (_currentSession != null)
            {
                _currentSession.messages.Clear();
                _currentSession.providerSessionId = null;
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
            _currentProvider.isEnabled = updatedProvider.isEnabled;

            if (resetRemoteSession)
                ResetRemoteSessionState();

            SyncFromProvider(_currentProvider);
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

            var response = await selector.SessionManager.CreateSession();
            string persistentSessionId = GetPersistentHermesSessionId(response);

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
                    sessionId = Guid.NewGuid().ToString(),
                    providerId = _currentProvider?.id,
                    providerSessionId = persistentSessionId,
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

            _currentSession.providerId = _currentProvider?.id ?? _currentSession.providerId;
            _currentSession.providerSessionId = persistentSessionId;
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
        /// Resume an existing Hermes session via WebSocket.
        /// </summary>
        public async Task ResumeHermesSessionAsync(string hermesSessionId)
        {
            var selector = GlobalBackendSelector.Instance;
            if (selector?.SessionManager == null)
                return;

            var response = await selector.SessionManager.ResumeSession(hermesSessionId);

            if (_currentProvider == null)
                _currentProvider = await ResolveProviderAsync();

            _currentChatViewModel = new ChatViewModel(_aiClient, _currentProvider);
            _currentChatViewModel.ProviderSessionId = hermesSessionId;
            _currentChatViewModel.SelectedModel = response.info?.model ?? _currentProvider?.defaultModel;
            ApplyGenerationSettings();

            // Load messages from response
            _currentChatViewModel.Messages.Clear();
            if (response.messages != null)
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

            // If we already have the local session for this provider session (e.g. the user opened
            // it from history and we're only reconnecting the server side), update it in place.
            // Minting a new sessionId here spawned a duplicate "new chat" every time an old chat
            // was resumed after restart — which is exactly the reported bug.
            bool reuseExisting = _currentSession != null &&
                string.Equals(_currentSession.providerSessionId, hermesSessionId, StringComparison.Ordinal);

            if (reuseExisting)
            {
                _currentSession.providerId = _currentProvider?.id;
                _currentSession.providerSessionId = hermesSessionId;
                _currentSession.selectedModel = response.info?.model ?? _currentSession.selectedModel ?? _currentProvider?.defaultModel;
                if (string.IsNullOrWhiteSpace(_currentSession.title) || _currentSession.title == "New chat")
                    _currentSession.title = response.info?.title ?? _currentSession.title;

                // Trust server history only when it returned messages; otherwise keep the local copy
                // so reopening a chat never wipes its contents.
                if (_currentChatViewModel.Messages.Count > 0)
                {
                    _currentSession.messages = new List<ChatMessage>(_currentChatViewModel.Messages);
                }
                else if (_currentSession.messages != null)
                {
                    _currentChatViewModel.Messages.Clear();
                    for (int i = 0; i < _currentSession.messages.Count; i++)
                        _currentChatViewModel.Messages.Add(_currentSession.messages[i]);
                }

                _currentSession.updatedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }
            else
            {
                _currentSession = new ChatSession
                {
                    sessionId = Guid.NewGuid().ToString(),
                    providerId = _currentProvider?.id,
                    providerSessionId = hermesSessionId,
                    selectedModel = response.info?.model ?? _currentProvider?.defaultModel,
                    title = response.info?.title ?? "Hermes session",
                    updatedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    messages = new List<ChatMessage>(_currentChatViewModel.Messages),
                    folder = string.Empty
                };
            }

            SaveCurrentSession();
            NeonLogger.Log("Hermes session resumed: " + hermesSessionId);
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
                var result = await selector.RestClient.ListSessions(40);
                if (result?.sessions == null)
                    return new List<HermesSession>();
                return new List<HermesSession>(result.sessions);
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
            if (selector?.RestClient == null)
                return;

            try
            {
                await selector.RestClient.DeleteSession(hermesSessionId);
                NeonLogger.Log("Hermes session deleted: " + hermesSessionId);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ChatService] Failed to delete Hermes session: " + ex.Message);
            }
        }

        public void CancelCurrentGeneration()
        {
            if (_chatTransport != null && _chatTransport.IsConnected)
            {
                _chatTransport.Interrupt();
                return;
            }
            _currentChatViewModel?.CancelGeneration();
        }

        public async Task RegenerateAsync(Action<string> onStreamToken = null, Action<string, string, string, string> onToolProgress = null)
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
            Action<string, string, string, string> onToolProgress = null)
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
        /// Send a message via Hermes WebSocket transport.
        /// </summary>
        private async Task SendViaTransport(string message, IReadOnlyList<ChatAttachment> attachments = null, Action<string> onStreamToken = null, Action<string, string, string, string> onToolProgress = null)
        {
            if (_chatTransport == null)
                return;

            // Store callbacks for streaming events
            _hermesStreamTokenCallback = onStreamToken;
            _hermesToolProgressCallback = onToolProgress;
            _hermesStreamBuffer = new System.Text.StringBuilder();
            _hermesStreamActive = false;
            _hermesLastError = null;

            // Create a TCS so we can wait for message.complete before returning.
            // Without this, SendViaTransport returns after the RPC ack (prompt.submit)
            // while WS streaming events arrive later — causing the streaming bubble
            // to be destroyed before any tokens are received.
            _hermesGenerationComplete = new TaskCompletionSource<bool>();

            ChatMessage localUserMessage = null;
            bool localUserMessageAdded = false;
            bool submitAcknowledged = false;

            try
            {
                await EnsureHermesSessionReadyAsync();

                // Add user message to local history optimistically, but only keep it if
                // prompt.submit succeeds. Hermes can reject the submit with "session busy";
                // in that case the server never saw the message, so keeping a local bubble
                // makes the transcript lie to the user.
                if (_currentChatViewModel == null)
                {
                    await GetOrCreateChatAsync();
                }

                localUserMessage = new ChatMessage
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
                _currentChatViewModel.Messages.Add(localUserMessage);
                localUserMessageAdded = true;
                // Sync session immediately so switching sessions mid-stream
                // doesn't lose the user message (session carries stale data).
                _currentSession.messages = new List<ChatMessage>(_currentChatViewModel.Messages);

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
                            await _chatTransport.AttachImageBytes(b64);
                    }
                }

                // Send via WebSocket — this returns after RPC ack
                await _chatTransport.SendMessage(message);
                submitAcknowledged = true;

                // Wait for generation to complete (message.complete or error/disconnect).
                // Use a generous timeout so long-running generations don't hang forever.
                var completedTask = await Task.WhenAny(
                    _hermesGenerationComplete.Task,
                    Task.Delay(300000)); // 5 min safety timeout

                if (completedTask != _hermesGenerationComplete.Task)
                {
                    NeonLogger.LogWarning("[Hermes] Generation timed out after 5 minutes");
                }
                else
                {
                    bool completed = await _hermesGenerationComplete.Task;
                    if (!completed)
                    {
                        string error = string.IsNullOrWhiteSpace(_hermesLastError)
                            ? "Hermes generation failed."
                            : _hermesLastError;
                        throw new InvalidOperationException(error);
                    }
                }

                _hermesGenerationComplete = null;
                SaveCurrentSession();
            }
            catch
            {
                if (!submitAcknowledged && localUserMessageAdded && _currentChatViewModel?.Messages != null)
                {
                    _currentChatViewModel.Messages.Remove(localUserMessage);
                    SaveCurrentSession();
                }

                ClearHermesPendingGenerationState();
                throw;
            }
        }

        private void ClearHermesPendingGenerationState()
        {
            _hermesGenerationComplete?.TrySetResult(false);
            _hermesGenerationComplete = null;
            _hermesStreamActive = false;
            _hermesStreamingMessage = null;
            _hermesStreamBuffer = null;
            _hermesStreamTokenCallback = null;
            _hermesToolProgressCallback = null;
            _hermesLastError = null;
        }

        /// <summary>
        /// Ensure the Hermes WebSocket transport has an active server-side session before sending.
        /// </summary>
        private async Task EnsureHermesSessionReadyAsync()
        {
            var selector = GlobalBackendSelector.Instance;
            var sessionManager = selector?.SessionManager;
            if (sessionManager == null)
                throw new InvalidOperationException("Hermes session manager is not available.");

            if (!sessionManager.IsConnected)
                await selector.ConnectHermes();

            string desired = _currentSession != null ? _currentSession.providerSessionId : null;

            // The selected chat's server session takes priority over whatever the transport
            // currently has active. After a restart (or after switching chats) the manager may
            // hold a different/fresh ActiveSessionId; sending without re-pointing it would leak
            // the old chat's message into a brand-new session (the reported bug).
            if (!string.IsNullOrWhiteSpace(desired))
            {
                bool alreadyOnSelected =
                    string.Equals(sessionManager.ActiveSessionId, desired, StringComparison.Ordinal) ||
                    string.Equals(sessionManager.StoredSessionId, desired, StringComparison.Ordinal);
                if (alreadyOnSelected)
                    return;

                // Resume can fail if the server-side session was cleaned up (e.g. WebSocket
                // dropped on a previous app kill). Fall back to a fresh session in that case.
                try
                {
                    await ResumeHermesSessionAsync(desired);
                    return;
                }
                catch (Exception ex)
                {
                    NeonLogger.LogWarning("[Hermes] Session resume failed (" + ex.Message + "), binding a new remote session to the current chat.");
                    _currentSession.providerSessionId = null;
                    if (_currentChatViewModel != null)
                        _currentChatViewModel.ProviderSessionId = null;
                }

                await StartHermesSessionAsync(false);
                return;
            }

            // The selected chat has no server session yet (older local history, imported chat,
            // or a previous failed resume). Never adopt whatever ActiveSessionId happens to be
            // in the transport: after restart or switching chats that can point at another chat.
            // Instead create a fresh remote Hermes session and bind it to the current local chat.
            await StartHermesSessionAsync(_currentSession == null);
        }

        // === Hermes Transport Event Handlers ===

        private void HandleHermesStreamStarted()
        {
            BeginHermesStreamingMessage(true);
        }

        private void HandleHermesDelta(string text)
        {
            if (string.IsNullOrEmpty(text) || !BeginHermesStreamingMessage(false))
                return;

            _hermesStreamBuffer.Append(text);
            _hermesStreamingMessage.content = _hermesStreamBuffer.ToString();

            // Also add as text segment for interleaved rendering with tool segments
            if (_hermesStreamingMessage.segments == null)
                _hermesStreamingMessage.segments = new System.Collections.Generic.List<ChatMessageSegment>();
            ChatMessageSegment last = _hermesStreamingMessage.segments.Count > 0
                ? _hermesStreamingMessage.segments[_hermesStreamingMessage.segments.Count - 1]
                : null;
            if (last != null && string.Equals(last.kind, ChatMessageSegment.TextKind, System.StringComparison.OrdinalIgnoreCase))
            {
                last.text = (last.text ?? "") + text;
            }
            else
            {
                _hermesStreamingMessage.segments.Add(new ChatMessageSegment
                {
                    kind = ChatMessageSegment.TextKind,
                    text = text
                });
            }

            // Notify UI
            _hermesStreamTokenCallback?.Invoke(text);
        }

        private void HandleHermesComplete(string finalText)
        {
            bool hadStreamingMessage = _hermesStreamingMessage != null;
            if (!hadStreamingMessage && !string.IsNullOrWhiteSpace(finalText))
                BeginHermesStreamingMessage(false);

            if (_hermesStreamingMessage != null)
            {
                string normalizedFinalText = null;

                // Apply final text if we got one
                if (!string.IsNullOrEmpty(finalText))
                {
                    _hermesStreamingMessage.content = finalText;
                    normalizedFinalText = finalText;
                }
                else if (_hermesStreamBuffer != null && _hermesStreamBuffer.Length > 0)
                {
                    _hermesStreamingMessage.content = _hermesStreamBuffer.ToString();
                    normalizedFinalText = _hermesStreamingMessage.content;
                }

                NormalizeHermesFinalTextSegments(_hermesStreamingMessage, normalizedFinalText);

                if (!hadStreamingMessage && !string.IsNullOrEmpty(normalizedFinalText))
                    _hermesStreamTokenCallback?.Invoke(normalizedFinalText);

                // Store reasoning text for expandable display
                if (_hermesReasoningBuffer != null && _hermesReasoningBuffer.Length > 0)
                {
                    _hermesStreamingMessage.reasoning = _hermesReasoningBuffer.ToString();
                }

                // Persist usage from gateway so stats footer survives re-render
                try
                {
                    var usage = GlobalBackendSelector.Instance?.SessionManager?.RuntimeInfo?.usage;
                    if (usage != null && usage.total > 0)
                    {
                        _hermesStreamingMessage.tokenCount = usage.total - _previousSessionTotal;
                        _previousSessionTotal = usage.total;
                        _hermesStreamingMessage.responseTimeSeconds = (float)(DateTime.UtcNow - _hermesStreamStartTime).TotalSeconds;
                    }
                }
                catch { }
            }

            _hermesStreamActive = false;
            _hermesStreamingMessage = null;
            _hermesStreamBuffer = null;
            _hermesStreamTokenCallback = null;
            _hermesToolProgressCallback = null;
            _hermesLastError = null;

            // Signal SendViaTransport that generation is done
            _hermesGenerationComplete?.TrySetResult(true);

            EmitLatestAssistantResponse();
            SaveCurrentSession();
        }

        private void HandleHermesToolUpdate(ToolCallUpdate update)
        {
            if (update == null || !BeginHermesStreamingMessage(false))
                return;

            // Add tool segment to streaming message
            if (_hermesStreamingMessage.segments == null)
                _hermesStreamingMessage.segments = new System.Collections.Generic.List<ChatMessageSegment>();

            string status = update.status == ToolCallStatus.Running ? "running" : "complete";
            // The TUI/stdio gateway doesn't include an emoji in the payload, so leave it empty there
            // and let the UI derive a per-tool emoji client-side (ToolCallUiHelper.GetToolEmoji).
            // The API SSE gateway does send one — pass it through. Status is shown separately
            // (left stripe + ● / ✓), so we no longer overwrite the icon with a status glyph.
            string emoji = update.emoji ?? string.Empty;
            string key = (update.name ?? "") + "\x01" + (update.toolId ?? "");
            string label = !string.IsNullOrEmpty(update.preview) ? update.preview : (update.name ?? "");

            for (int i = 0; i < _hermesStreamingMessage.segments.Count; i++)
            {
                ChatMessageSegment existing = _hermesStreamingMessage.segments[i];
                if (existing == null ||
                    !string.Equals(existing.kind, ChatMessageSegment.ToolKind, System.StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(existing.key, key, System.StringComparison.Ordinal))
                {
                    continue;
                }

                existing.tool = update.name ?? "";
                if (!string.IsNullOrEmpty(label))
                    existing.label = label;
                if (!string.IsNullOrEmpty(emoji))
                    existing.emoji = emoji;
                existing.status = status;
                if (!string.IsNullOrEmpty(update.inlineDiff))
                    existing.inlineDiff = update.inlineDiff;
                if (!string.IsNullOrEmpty(update.details))
                    existing.details = update.details;

                _hermesToolProgressCallback?.Invoke(update.name, existing.label ?? "", existing.emoji ?? "", status);
                return;
            }

            _hermesStreamingMessage.segments.Add(new ChatMessageSegment
            {
                kind = ChatMessageSegment.ToolKind,
                key = key,
                tool = update.name ?? "",
                label = label,
                emoji = emoji,
                status = status,
                inlineDiff = update.inlineDiff,
                details = update.details
            });

            _hermesToolProgressCallback?.Invoke(update.name, label, emoji, status);
        }

        private bool BeginHermesStreamingMessage(bool reset)
        {
            if (_currentChatViewModel == null)
                return false;

            if (_hermesStreamingMessage != null)
            {
                _hermesStreamActive = true;
                if (_hermesStreamBuffer == null)
                    _hermesStreamBuffer = new System.Text.StringBuilder();
                if (_hermesReasoningBuffer == null)
                    _hermesReasoningBuffer = new System.Text.StringBuilder();
                return true;
            }

            _hermesStreamActive = true;
            _hermesStreamBuffer = new System.Text.StringBuilder();
            _hermesReasoningBuffer = new System.Text.StringBuilder();
            _hermesStreamStartTime = DateTime.UtcNow;

            _hermesStreamingMessage = new ChatMessage
            {
                role = "assistant",
                content = string.Empty,
                model = string.Empty,
                unixTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
            _currentChatViewModel.Messages.Add(_hermesStreamingMessage);
            // Sync so mid-stream session switch doesn't lose the assistant bubble
            if (_currentSession != null)
                _currentSession.messages = new System.Collections.Generic.List<ChatMessage>(_currentChatViewModel.Messages);
            return true;
        }

        private static void NormalizeHermesFinalTextSegments(ChatMessage message, string finalText)
        {
            if (message == null || string.IsNullOrEmpty(finalText) || message.segments == null)
                return;

            bool appliedText = false;
            for (int i = message.segments.Count - 1; i >= 0; i--)
            {
                ChatMessageSegment segment = message.segments[i];
                if (segment == null ||
                    !string.Equals(segment.kind, ChatMessageSegment.TextKind, System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!appliedText)
                {
                    segment.text = finalText;
                    appliedText = true;
                }
                else
                {
                    message.segments.RemoveAt(i);
                }
            }

            if (!appliedText)
            {
                message.segments.Insert(0, new ChatMessageSegment
                {
                    kind = ChatMessageSegment.TextKind,
                    text = finalText
                });
            }
        }

        private void HandleHermesReasoningDelta(string text)
        {
            if (!_hermesStreamActive || _hermesStreamingMessage == null)
                return;
            _hermesReasoningBuffer?.Append(text);
        }

        private void HandleHermesError(string error)
        {
            NeonLogger.Log("[Hermes] Error: " + error);
            _hermesLastError = error;
            _hermesStreamActive = false;
            _hermesStreamingMessage = null;
            _hermesStreamBuffer = null;
            _hermesStreamTokenCallback = null;
            _hermesToolProgressCallback = null;

            // Unblock SendViaTransport so it doesn't hang on error
            _hermesGenerationComplete?.TrySetResult(false);
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
                bool hasText = !string.IsNullOrWhiteSpace(message?.content);
                bool hasAttachments = message?.attachments != null && message.attachments.Count > 0;
                if (message == null || string.IsNullOrWhiteSpace(message.role) || (!hasText && !hasAttachments))
                    continue;

                requestMessages.Add(new AiChatMessage
                {
                    role = message.role,
                    content = hasText ? message.content : "[image]"
                });
            }

            if (requestMessages.Count == 0)
                return LocalizationExtensions.Get("chat.summary.empty", "Пока нет сообщений для краткого пересказа.");

            requestMessages.Add(new AiChatMessage
            {
                role = "user",
                content = LocalizationExtensions.Get("chat.summary.user_prompt", "Сделай короткое резюме диалога на русском языке (2-4 предложения).")
            });

            var request = new AiChatRequest
            {
                model = CurrentSessionModel ?? provider.defaultModel,
                providerSessionId = _currentChatViewModel?.ProviderSessionId,
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

            return _currentProvider != null && _currentProvider.isEnabled ? _currentProvider : null;
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
                _currentSession.providerSessionId = null;

            if (_currentChatViewModel != null)
                _currentChatViewModel.ProviderSessionId = null;
        }

        public void ClearActiveProviderState()
        {
            _currentProvider = null;
            _currentSession = null;
            _currentChatViewModel = null;
        }

        public bool CurrentProviderMatchesBackend(BackendMode mode)
        {
            if (_currentProvider == null)
                return true;

            bool hermesMode = mode == BackendMode.Hermes;
            return IsHermesProvider(_currentProvider) == hermesMode;
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
