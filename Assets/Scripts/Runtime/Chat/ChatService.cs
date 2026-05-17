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

        public ProviderConfig CurrentProvider => _currentProvider;

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
            _currentChatViewModel = new ChatViewModel(_aiClient, _currentProvider);
            ApplyGenerationSettings();

            await LoadLatestSessionAsync();
            NeonLogger.Log("Chat session ready.");
            return _currentChatViewModel;
        }

        public async Task<List<ChatSession>> GetAllSessionsAsync()
        {
            return _sessionRepository.GetAll();
        }

        public async Task SwitchToSessionAsync(ChatSession session)
        {
            if (session == null) return;

            _currentSession = session;
            _currentChatViewModel = new ChatViewModel(_aiClient, _currentProvider ?? await _providerManager.GetActiveProviderAsync());
            ApplyGenerationSettings();

            _currentChatViewModel.Messages.Clear();
            foreach (var msg in session.messages)
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
        }

        public async Task SwitchProviderAsync(ProviderConfig newProvider)
        {
            _currentProvider = newProvider;
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
                updatedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                messages = new List<ChatMessage>()
            };

            SaveCurrentSession();
            NeonLogger.Log("New chat session started.");
        }

        public async Task SendMessageAsync(string message)
        {
            if (_currentChatViewModel == null)
            {
                await GetOrCreateChatAsync();
            }

            _currentChatViewModel.InputMessage = message;
            await _currentChatViewModel.SendAsync();

            SaveCurrentSession();
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
            var sessions = _sessionRepository.GetAll();
            if (sessions.Count > 0)
            {
                _currentSession = sessions[0];
                foreach (var msg in _currentSession.messages)
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

            _currentSession.messages = new List<ChatMessage>(_currentChatViewModel.Messages);
            _sessionRepository.SaveAll(new List<ChatSession> { _currentSession });
        }
    }
}