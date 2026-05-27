using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Api;
using NeonCompanion.Runtime.Api.Models;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Data.Repositories;
using NeonCompanion.Runtime.Localization;
using NeonCompanion.Runtime.UI.Chat;

namespace NeonCompanion.Runtime.Chat
{
    public sealed class ChatService
    {
        private readonly IAiClient _aiClient;
        private readonly ProviderManager _providerManager;
        private readonly IChatSessionRepository _sessionRepository;
        private ChatViewModel _currentChatViewModel;
        private ChatSession _currentSession;
        private ProviderConfig _currentProvider;
        public event Action<string> OnAssistantResponse;

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
                    await StartNewSessionAsync();
            }
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
            if (_currentProvider == null)
                _currentProvider = await ResolveProviderAsync();

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
                messages = new List<ChatMessage>()
            };

            SaveCurrentSession();
            NeonLogger.Log("New chat session started.");
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
            if (_currentChatViewModel == null)
            {
                await GetOrCreateChatAsync();
            }

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
                await StartNewSessionAsync();
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
    }
}
