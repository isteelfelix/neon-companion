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
            if (_currentChatViewModel != null)
                return _currentChatViewModel;

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

            await LoadLatestSessionAsync(preferredProviderId);
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
                if (remaining.Count > 0)
                    await SwitchToSessionAsync(remaining[0]);
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

            _currentProvider = sessionProvider
                ?? preferredProvider
                ?? _currentProvider
                ?? await _providerManager.GetActiveProviderAsync();

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

        /// <summary>True if the provider is configured to use the Hermes backend.</summary>
        internal static bool IsHermesProvider(ProviderConfig provider)
        {
            return provider != null
                && string.Equals(provider.backendType, "hermes", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Create a new Hermes session via WebSocket.
        /// </summary>
        private async Task StartHermesSessionAsync()
        {
            var selector = GlobalBackendSelector.Instance;
            if (selector?.SessionManager == null)
                return;

            var response = await selector.SessionManager.CreateSession();

            // Create local session record
            if (_currentProvider == null)
                _currentProvider = await ResolveProviderAsync();

            _currentChatViewModel = new ChatViewModel(_aiClient, _currentProvider);
            _currentChatViewModel.ProviderSessionId = response.session_id;
            _currentChatViewModel.SelectedModel = response.info?.model ?? _currentProvider?.defaultModel;
            ApplyGenerationSettings();

            _currentSession = new ChatSession
            {
                sessionId = Guid.NewGuid().ToString(),
                providerId = _currentProvider?.id,
                providerSessionId = response.session_id,
                selectedModel = response.info?.model ?? _currentProvider?.defaultModel,
                title = response.info?.title ?? "Hermes session",
                updatedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                messages = new List<ChatMessage>(),
                folder = string.Empty
            };

            SaveCurrentSession();
            NeonLogger.Log("Hermes session created: " + response.session_id);
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

        public async Task SendMessageAsync(
            string message,
            IReadOnlyList<ChatAttachment> attachments,
            Action<string> onStreamToken = null,
            Action<string, string, string, string> onToolProgress = null)
        {
            // Hermes backend: route through WebSocket transport
            if (_chatTransport != null && _chatTransport.IsConnected)
            {
                await SendViaTransport(message, onStreamToken, onToolProgress);
                return;
            }

            // OpenAI backend: existing HTTP path
            if (_currentChatViewModel == null)
            {
                await GetOrCreateChatAsync();
            }

            if (_currentProvider == null || _currentChatViewModel == null)
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
        private async Task SendViaTransport(string message, Action<string> onStreamToken = null, Action<string, string, string, string> onToolProgress = null)
        {
            if (_chatTransport == null)
                return;

            // Store callbacks for streaming events
            _hermesStreamTokenCallback = onStreamToken;
            _hermesToolProgressCallback = onToolProgress;
            _hermesStreamBuffer = new System.Text.StringBuilder();
            _hermesStreamActive = false;

            await EnsureHermesSessionReadyAsync();

            // Add user message to local history
            if (_currentChatViewModel == null)
            {
                await GetOrCreateChatAsync();
            }
            _currentChatViewModel.Messages.Add(new ChatMessage
            {
                role = "user",
                content = message,
                unixTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });

            // Send via WebSocket
            await _chatTransport.SendMessage(message);
            SaveCurrentSession();
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

            if (!string.IsNullOrEmpty(sessionManager.ActiveSessionId))
                return;

            if (_currentSession != null && !string.IsNullOrWhiteSpace(_currentSession.providerSessionId))
                await ResumeHermesSessionAsync(_currentSession.providerSessionId);
            else
                await StartHermesSessionAsync();
        }

        // === Hermes Transport Event Handlers ===

        private void HandleHermesStreamStarted()
        {
            if (_currentChatViewModel == null)
                return;

            _hermesStreamActive = true;
            _hermesStreamBuffer = new System.Text.StringBuilder();

            // Create streaming assistant message
            _hermesStreamingMessage = new ChatMessage
            {
                role = "assistant",
                content = string.Empty,
                model = string.Empty,
                unixTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
            _currentChatViewModel.Messages.Add(_hermesStreamingMessage);
        }

        private void HandleHermesDelta(string text)
        {
            if (!_hermesStreamActive || _hermesStreamingMessage == null)
                return;

            _hermesStreamBuffer.Append(text);
            _hermesStreamingMessage.content = _hermesStreamBuffer.ToString();

            // Notify UI
            _hermesStreamTokenCallback?.Invoke(text);
        }

        private void HandleHermesComplete(string finalText)
        {
            if (_hermesStreamingMessage != null)
            {
                // Apply final text if we got one
                if (!string.IsNullOrEmpty(finalText))
                {
                    _hermesStreamingMessage.content = finalText;
                }
                else if (_hermesStreamBuffer.Length > 0)
                {
                    _hermesStreamingMessage.content = _hermesStreamBuffer.ToString();
                }
            }

            _hermesStreamActive = false;
            _hermesStreamingMessage = null;
            _hermesStreamBuffer = null;
            _hermesStreamTokenCallback = null;
            _hermesToolProgressCallback = null;

            EmitLatestAssistantResponse();
            SaveCurrentSession();
        }

        private void HandleHermesToolUpdate(ToolCallUpdate update)
        {
            if (_hermesStreamingMessage == null || update == null)
                return;

            // Add tool segment to streaming message
            if (_hermesStreamingMessage.segments == null)
                _hermesStreamingMessage.segments = new System.Collections.Generic.List<ChatMessageSegment>();

            string status = update.status == ToolCallStatus.Running ? "running" : "complete";
            string emoji = update.status == ToolCallStatus.Running ? "⏳" : "✅";

            _hermesStreamingMessage.segments.Add(new ChatMessageSegment
            {
                kind = ChatMessageSegment.ToolKind,
                key = (update.name ?? "") + "\x01" + (update.toolId ?? ""),
                tool = update.name ?? "",
                label = update.toolId ?? "",
                emoji = emoji,
                status = status
            });

            _hermesToolProgressCallback?.Invoke(update.name, update.toolId, emoji, status);
        }

        private void HandleHermesError(string error)
        {
            NeonLogger.Log("[Hermes] Error: " + error);
            _hermesStreamActive = false;
            _hermesStreamingMessage = null;
            _hermesStreamBuffer = null;
        }

        public async Task<ModelSwitchResult> SetCurrentSessionModelAsync(string modelId)
        {
            if (string.IsNullOrWhiteSpace(modelId))
                throw new ArgumentException("Model is required.", nameof(modelId));

            if (_currentChatViewModel == null)
                await GetOrCreateChatAsync();

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

            var provider = _currentProvider ?? await _providerManager.GetActiveProviderAsync();
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
            if (sessions.Count > 0)
            {
                await SwitchToSessionAsync(sessions[0], preferredProviderId);
            }
            else
            {
                ClearCurrentSessionWithoutSaving();
            }
        }

        private async Task<ProviderConfig> ResolveProviderAsync(string providerId = null)
        {
            if (!string.IsNullOrWhiteSpace(providerId))
            {
                var provider = await _providerManager.GetProviderByIdAsync(providerId);
                if (provider != null)
                    return provider;
            }

            return await _providerManager.GetActiveProviderAsync();
        }

        private async Task<ProviderConfig> TryGetProviderByIdAsync(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId))
                return null;

            return await _providerManager.GetProviderByIdAsync(providerId);
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

        public void ClearCurrentSessionState()
        {
            ClearCurrentSessionWithoutSaving();
        }

        private void ClearCurrentSessionWithoutSaving()
        {
            _currentSession = null;

            if (_currentProvider == null)
            {
                _currentChatViewModel = null;
                return;
            }

            _currentChatViewModel = new ChatViewModel(_aiClient, _currentProvider);
            _currentChatViewModel.ProviderSessionId = null;
            _currentChatViewModel.SelectedModel = _currentProvider.defaultModel;
            ApplyGenerationSettings();
        }
    }
}
