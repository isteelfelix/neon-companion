using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Api;
using NeonCompanion.Runtime.Api.Models;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Data.Repositories;
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

        public float Temperature { get; set; } = 0.7f;
        public int MaxTokens { get; set; } = 512;
        public string SystemPrompt { get; set; }
        public bool UseStreaming { get; set; }
        public bool SaveChatHistory { get; set; } = true;

        public ProviderConfig CurrentProvider => _currentProvider;
        public ChatViewModel CurrentChatViewModel => _currentChatViewModel;

        public ChatService(
            IAiClient aiClient,
            ProviderManager providerManager,
            IChatSessionRepository sessionRepository)
        {
            _aiClient = aiClient;
            _providerManager = providerManager;
            _sessionRepository = sessionRepository;
        }

        public async Task<ChatViewModel> GetOrCreateChatAsync()
        {
            if (_currentChatViewModel != null)
                return _currentChatViewModel;

            _currentProvider = await _providerManager.GetActiveProviderAsync();
            SyncFromProvider(_currentProvider);
            _currentChatViewModel = new ChatViewModel(_aiClient, _currentProvider);
            ApplyGenerationSettings();

            await LoadLatestSessionAsync();
            NeonLogger.Log("Chat session ready.");
            return _currentChatViewModel;
        }

        public async Task<List<ChatSession>> GetAllSessionsAsync()
        {
            return await Task.FromResult(GetSortedSessions());
        }

        public async Task SwitchToSessionAsync(ChatSession session)
        {
            if (session == null) return;

            _currentSession = session;
            _currentChatViewModel = new ChatViewModel(_aiClient, _currentProvider ?? await _providerManager.GetActiveProviderAsync());
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
            ApplyGenerationSettings();

            await StartNewSessionAsync();
            NeonLogger.Log($"Switched to provider: {newProvider.displayName}");
        }

        public async Task StartNewSessionAsync()
        {
            if (_currentProvider == null)
                _currentProvider = await _providerManager.GetActiveProviderAsync();

            _currentChatViewModel = new ChatViewModel(_aiClient, _currentProvider);
            ApplyGenerationSettings();

            _currentSession = new ChatSession
            {
                sessionId = Guid.NewGuid().ToString(),
                providerId = _currentProvider?.id,
                title = "New chat",
                updatedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                messages = new List<ChatMessage>()
            };

            SaveCurrentSession();
            NeonLogger.Log("New chat session started.");
        }

        public async Task RegenerateAsync(Action<string> onStreamToken = null)
        {
            if (_currentChatViewModel == null)
                await GetOrCreateChatAsync();

            _currentChatViewModel.UseStreaming = UseStreaming;
            await _currentChatViewModel.RegenerateAsync(UseStreaming ? onStreamToken : null);

            SaveCurrentSession();
        }

        public async Task SendMessageAsync(string message, Action<string> onStreamToken = null)
        {
            if (_currentChatViewModel == null)
            {
                await GetOrCreateChatAsync();
            }

            _currentChatViewModel.UseStreaming = UseStreaming;
            _currentChatViewModel.InputMessage = message;
            await _currentChatViewModel.SendAsync(UseStreaming ? onStreamToken : null);

            SaveCurrentSession();
        }

        public async Task<string> SummarizeCurrentConversationAsync(int maxMessages = 12)
        {
            if (_currentChatViewModel == null)
                await GetOrCreateChatAsync();

            var sourceMessages = _currentChatViewModel?.Messages;
            if (sourceMessages == null || sourceMessages.Count == 0)
                return "Пока нет сообщений для краткого пересказа.";

            var provider = _currentProvider ?? await _providerManager.GetActiveProviderAsync();
            if (provider == null)
                return "Провайдер не настроен.";

            var requestMessages = new List<AiChatMessage>();
            int start = Math.Max(0, sourceMessages.Count - Math.Max(1, maxMessages));
            for (int i = start; i < sourceMessages.Count; i++)
            {
                var message = sourceMessages[i];
                if (message == null || string.IsNullOrWhiteSpace(message.role) || string.IsNullOrWhiteSpace(message.content))
                    continue;

                requestMessages.Add(new AiChatMessage
                {
                    role = message.role,
                    content = message.content
                });
            }

            if (requestMessages.Count == 0)
                return "Пока нет сообщений для краткого пересказа.";

            requestMessages.Add(new AiChatMessage
            {
                role = "user",
                content = "Сделай короткое резюме диалога на русском языке (2-4 предложения)."
            });

            var request = new AiChatRequest
            {
                model = provider.defaultModel,
                temperature = 0.2f,
                maxTokens = 140,
                systemPrompt = "Ты помощник, который кратко и точно суммирует переписку на русском языке.",
                messages = requestMessages
            };

            var response = await _aiClient.SendMessageAsync(provider, request);
            var summary = response?.content?.Trim();
            return string.IsNullOrWhiteSpace(summary)
                ? "Не удалось получить резюме."
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

        private async Task LoadLatestSessionAsync()
        {
            var sessions = GetSortedSessions();
            if (sessions.Count > 0)
            {
                _currentSession = sessions[0];
                foreach (var msg in _currentSession.messages ?? new List<ChatMessage>())
                {
                    _currentChatViewModel.Messages.Add(msg);
                }
            }
            else
            {
                await StartNewSessionAsync();
            }
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
    }
}
