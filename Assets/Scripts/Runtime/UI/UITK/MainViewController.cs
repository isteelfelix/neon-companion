using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Chat;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Localization;
using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK
{
    [RequireComponent(typeof(UIDocument))]
    public sealed class MainViewController : MonoBehaviour
    {
        private const string ActiveNavClass = "nav-item--active";
        private const string ActiveSessionClass = "session-item--active";

        private readonly List<Button> _navButtons = new List<Button>();
        private readonly List<VisualElement> _sessionItems = new List<VisualElement>();

        private Button _navChat;
        private Button _navAvatars;
        private Button _navProviders;
        private Button _navHistory;
        private Button _navThemes;
        private Button _navSettings;
        private Button _sendButton;
        private Button _summarizeButton;

        private TextField _messageInput;
        private VisualElement _avatarStage;
        private VisualElement _chatArea;
        private VisualElement _providersArea;
        private VisualElement _chatMessages;
        private ScrollView _chatScroll;
        private ScrollView _sessionsList;

        private ScrollView _providersList;
        private Button _addProviderButton;
        private VisualElement _providerEditPanel;
        private TextField _editName;
        private TextField _editBaseUrl;
        private TextField _editApiKey;
        private TextField _editModel;
        private Slider _editTemperature;
        private Button _saveProviderButton;
        private Button _cancelEditButton;

        private CompanionApp _app;
        private ChatService _chatService;
        private ProviderConfig _editingProvider;
        private bool _isBound;
        private bool _isSending;

        private void OnEnable()
        {
            var document = GetComponent<UIDocument>();
            if (document == null || document.rootVisualElement == null)
                return;

            Bind(document.rootVisualElement);
            RegisterCallbacks();
            ShowChat();

            _ = RefreshAsync();
        }

        private void OnDisable()
        {
            UnregisterCallbacks();
            _isBound = false;
        }

        private void Bind(VisualElement root)
        {
            _navButtons.Clear();

            _navChat = root.Q<Button>("nav-chat");
            _navAvatars = root.Q<Button>("nav-avatars");
            _navProviders = root.Q<Button>("nav-providers");
            _navHistory = root.Q<Button>("nav-history");
            _navThemes = root.Q<Button>("nav-themes");
            _navSettings = root.Q<Button>("nav-settings");

            AddNav(_navChat);
            AddNav(_navAvatars);
            AddNav(_navProviders);
            AddNav(_navHistory);
            AddNav(_navThemes);
            AddNav(_navSettings);

            _avatarStage = root.Q<VisualElement>("avatar-stage");
            _chatArea = root.Q<VisualElement>("chat-area");
            _providersArea = root.Q<VisualElement>("providers-area");
            _chatScroll = root.Q<ScrollView>("chat-scroll");
            _chatMessages = root.Q<VisualElement>("chat-messages");
            _messageInput = root.Q<TextField>("message-input");
            _sendButton = root.Q<Button>("send-button");
            _sessionsList = root.Q<ScrollView>("sessions-list");
            _summarizeButton = root.Q<Button>("summarize-btn");

            _providersList = root.Q<ScrollView>("providers-list");
            _addProviderButton = root.Q<Button>("add-provider-btn");
            _providerEditPanel = root.Q<VisualElement>("provider-edit-panel");
            _editName = root.Q<TextField>("edit-name");
            _editBaseUrl = root.Q<TextField>("edit-baseurl");
            _editApiKey = root.Q<TextField>("edit-apikey");
            _editModel = root.Q<TextField>("edit-model");
            _editTemperature = root.Q<Slider>("edit-temperature");
            _saveProviderButton = root.Q<Button>("save-provider-btn");
            _cancelEditButton = root.Q<Button>("cancel-edit-btn");

            _navChat?.Localize("tab.chat");
            _navAvatars?.Localize("tab.avatar");
            _navProviders?.Localize("settings.providers");
            _navHistory?.Localize("chat.history");
            _navThemes?.Localize("settings.themes");
            _navSettings?.Localize("tab.settings");

            SetDisplay(_providerEditPanel, DisplayStyle.None);
            _isBound = true;
        }

        private void AddNav(Button button)
        {
            if (button != null)
                _navButtons.Add(button);
        }

        private void RegisterCallbacks()
        {
            RegisterClick(_navChat, ShowChat);
            RegisterClick(_navAvatars, ShowAvatars);
            RegisterClick(_navProviders, ShowProviders);
            RegisterClick(_navHistory, ShowHistory);
            RegisterClick(_navThemes, ShowThemes);
            RegisterClick(_navSettings, ShowSettings);
            RegisterClick(_sendButton, OnSendClicked);
            RegisterClick(_summarizeButton, OnSummarizeClicked);
            RegisterClick(_addProviderButton, OnAddProviderClicked);
            RegisterClick(_saveProviderButton, OnSaveProviderClicked);
            RegisterClick(_cancelEditButton, OnCancelEditClicked);

            if (_messageInput != null)
                _messageInput.RegisterCallback<KeyDownEvent>(OnInputKeyDown);
        }

        private void UnregisterCallbacks()
        {
            UnregisterClick(_navChat, ShowChat);
            UnregisterClick(_navAvatars, ShowAvatars);
            UnregisterClick(_navProviders, ShowProviders);
            UnregisterClick(_navHistory, ShowHistory);
            UnregisterClick(_navThemes, ShowThemes);
            UnregisterClick(_navSettings, ShowSettings);
            UnregisterClick(_sendButton, OnSendClicked);
            UnregisterClick(_summarizeButton, OnSummarizeClicked);
            UnregisterClick(_addProviderButton, OnAddProviderClicked);
            UnregisterClick(_saveProviderButton, OnSaveProviderClicked);
            UnregisterClick(_cancelEditButton, OnCancelEditClicked);

            if (_messageInput != null)
                _messageInput.UnregisterCallback<KeyDownEvent>(OnInputKeyDown);
        }

        private static void RegisterClick(Button button, Action handler)
        {
            if (button != null)
                button.clicked += handler;
        }

        private static void UnregisterClick(Button button, Action handler)
        {
            if (button != null)
                button.clicked -= handler;
        }

        private void ShowChat()
        {
            SetActiveNav(_navChat);
            SetDisplay(_avatarStage, DisplayStyle.Flex);
            SetDisplay(_chatArea, DisplayStyle.Flex);
            SetDisplay(_providersArea, DisplayStyle.None);
        }

        private void ShowAvatars()
        {
            SetActiveNav(_navAvatars);
            SetDisplay(_avatarStage, DisplayStyle.Flex);
            SetDisplay(_chatArea, DisplayStyle.None);
            SetDisplay(_providersArea, DisplayStyle.None);
        }

        private void ShowProviders()
        {
            SetActiveNav(_navProviders);
            SetDisplay(_avatarStage, DisplayStyle.None);
            SetDisplay(_chatArea, DisplayStyle.None);
            SetDisplay(_providersArea, DisplayStyle.Flex);
            _ = RefreshProvidersListAsync();
        }

        private void ShowHistory()
        {
            SetActiveNav(_navHistory);
            SetDisplay(_avatarStage, DisplayStyle.None);
            SetDisplay(_chatArea, DisplayStyle.Flex);
            SetDisplay(_providersArea, DisplayStyle.None);
        }

        private void ShowThemes()
        {
            SetActiveNav(_navThemes);
            SetDisplay(_avatarStage, DisplayStyle.None);
            SetDisplay(_chatArea, DisplayStyle.None);
            SetDisplay(_providersArea, DisplayStyle.None);
        }

        private void ShowSettings()
        {
            SetActiveNav(_navSettings);
            SetDisplay(_avatarStage, DisplayStyle.None);
            SetDisplay(_chatArea, DisplayStyle.None);
            SetDisplay(_providersArea, DisplayStyle.None);
        }

        private void SetActiveNav(Button active)
        {
            foreach (var button in _navButtons)
                button.EnableInClassList(ActiveNavClass, button == active);
        }

        private static void SetDisplay(VisualElement element, DisplayStyle display)
        {
            if (element != null)
                element.style.display = display;
        }

        private void OnInputKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter)
                return;

            evt.StopPropagation();
            OnSendClicked();
        }

        private void OnSendClicked()
        {
            _ = SendCurrentMessageAsync();
        }

        private async Task SendCurrentMessageAsync()
        {
            if (_isSending || _messageInput == null || string.IsNullOrWhiteSpace(_messageInput.value))
                return;

            string message = _messageInput.value.Trim();
            _messageInput.value = string.Empty;
            SetSending(true);

            try
            {
                var chat = await GetChatServiceAsync();
                if (chat == null)
                {
                    AddSystemMessage("Application is not initialized.");
                    return;
                }

                await chat.SendMessageAsync(message);
                RenderMessages(chat.CurrentChatViewModel?.Messages);
                await LoadSessionsAsync(chat);
            }
            catch (Exception ex)
            {
                AddSystemMessage($"[Error] {ex.Message}");
                NeonLogger.LogError(ex.ToString());
            }
            finally
            {
                SetSending(false);
            }
        }

        private void SetSending(bool isSending)
        {
            _isSending = isSending;
            if (_sendButton != null)
                _sendButton.SetEnabled(!isSending);
        }

        private void OnSummarizeClicked()
        {
            AddSystemMessage("Summarize is not implemented yet.");
        }

        private async Task RefreshAsync()
        {
            try
            {
                var chat = await GetChatServiceAsync();
                if (!_isBound || chat == null)
                    return;

                RenderMessages(chat.CurrentChatViewModel?.Messages);
                await LoadSessionsAsync(chat);
            }
            catch (Exception ex)
            {
                NeonLogger.LogError(ex.ToString());
            }
        }

        private async Task<CompanionApp> GetAppAsync()
        {
            if (_app != null)
                return _app;

            for (int i = 0; i < 120 && isActiveAndEnabled; i++)
            {
                var bootstrap = UnityEngine.Object.FindAnyObjectByType<AppBootstrap>();
                if (bootstrap?.App != null)
                {
                    _app = bootstrap.App;
                    return _app;
                }

                await Task.Yield();
            }

            return null;
        }

        private async Task<ChatService> GetChatServiceAsync()
        {
            if (_chatService != null)
                return _chatService;

            var app = await GetAppAsync();
            if (app == null)
                return null;

            _chatService = app.ChatService;
            await _chatService.GetOrCreateChatAsync();
            return _chatService;
        }

        private async Task LoadSessionsAsync(ChatService chat)
        {
            if (_sessionsList == null)
                return;

            var sessions = await chat.GetAllSessionsAsync();
            if (!_isBound)
                return;

            _sessionsList.Clear();
            _sessionItems.Clear();

            foreach (var session in sessions)
            {
                var item = CreateSessionItem(session);
                _sessionsList.Add(item);
                _sessionItems.Add(item);
            }
        }

        private VisualElement CreateSessionItem(ChatSession session)
        {
            var container = new VisualElement();
            container.AddToClassList("session-item");

            var titleLabel = new Label(string.IsNullOrWhiteSpace(session.title) ? "New chat" : session.title);
            titleLabel.AddToClassList("session-title");

            int count = session.messages?.Count ?? 0;
            var metaLabel = new Label($"{count} msg");
            metaLabel.AddToClassList("session-meta");

            container.Add(titleLabel);
            container.Add(metaLabel);
            container.RegisterCallback<ClickEvent>(evt => { _ = SwitchSessionAsync(session, container); });

            return container;
        }

        private async Task SwitchSessionAsync(ChatSession session, VisualElement item)
        {
            try
            {
                var chat = await GetChatServiceAsync();
                if (chat == null)
                    return;

                await chat.SwitchToSessionAsync(session);
                SetActiveSession(item);
                RenderMessages(chat.CurrentChatViewModel?.Messages);
            }
            catch (Exception ex)
            {
                NeonLogger.LogError(ex.ToString());
            }
        }

        private void SetActiveSession(VisualElement selected)
        {
            foreach (var item in _sessionItems)
                item.EnableInClassList(ActiveSessionClass, item == selected);
        }

        private void RenderMessages(IReadOnlyList<ChatMessage> messages)
        {
            if (_chatMessages == null)
                return;

            _chatMessages.Clear();
            if (messages != null)
            {
                foreach (var message in messages)
                    _chatMessages.Add(CreateMessageElement(message));
            }

            if (_chatScroll == null)
                return;

            _chatScroll.schedule.Execute(() =>
            {
                if (_chatScroll != null)
                    _chatScroll.scrollOffset = new Vector2(0f, float.MaxValue);
            });
        }

        private static VisualElement CreateMessageElement(ChatMessage message)
        {
            var container = new VisualElement();
            container.AddToClassList("chat-message");
            container.AddToClassList(message.role == "user" ? "chat-message--user" : "chat-message--assistant");

            var label = new Label(message.content ?? string.Empty);
            label.AddToClassList("chat-message__text");
            container.Add(label);

            return container;
        }

        private void AddSystemMessage(string text)
        {
            if (_chatMessages == null)
                return;

            _chatMessages.Add(CreateMessageElement(new ChatMessage
            {
                role = "assistant",
                content = text,
                unixTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            }));
        }

        private void OnAddProviderClicked()
        {
            _editingProvider = ProviderConfig.CreateDefault("New Provider", "https://api.openai.com/v1");
            ShowProviderEditPanel();
        }

        private void StartEditingProvider(ProviderConfig provider)
        {
            _editingProvider = provider;
            ShowProviderEditPanel();
        }

        private void ShowProviderEditPanel()
        {
            if (_providerEditPanel == null || _editingProvider == null)
                return;

            if (_editName != null)
                _editName.value = _editingProvider.displayName ?? string.Empty;
            if (_editBaseUrl != null)
                _editBaseUrl.value = _editingProvider.baseUrl ?? string.Empty;
            if (_editApiKey != null)
                _editApiKey.value = _editingProvider.apiKey ?? string.Empty;
            if (_editModel != null)
                _editModel.value = _editingProvider.defaultModel ?? string.Empty;
            if (_editTemperature != null)
                _editTemperature.value = _editingProvider.temperature;

            _providerEditPanel.style.display = DisplayStyle.Flex;
        }

        private void OnSaveProviderClicked()
        {
            _ = SaveProviderAsync();
        }

        private async Task SaveProviderAsync()
        {
            if (_editingProvider == null)
                return;

            try
            {
                var app = await GetAppAsync();
                if (app == null)
                    return;

                _editingProvider.displayName = _editName?.value ?? _editingProvider.displayName;
                _editingProvider.baseUrl = _editBaseUrl?.value ?? _editingProvider.baseUrl;
                _editingProvider.apiKey = _editApiKey?.value ?? _editingProvider.apiKey;
                _editingProvider.defaultModel = _editModel?.value ?? _editingProvider.defaultModel;
                if (_editTemperature != null)
                    _editingProvider.temperature = _editTemperature.value;

                await app.ProviderManager.SaveProviderAsync(_editingProvider);
                _editingProvider = null;
                SetDisplay(_providerEditPanel, DisplayStyle.None);
                await RefreshProvidersListAsync();
            }
            catch (Exception ex)
            {
                NeonLogger.LogError(ex.ToString());
            }
        }

        private void OnCancelEditClicked()
        {
            _editingProvider = null;
            SetDisplay(_providerEditPanel, DisplayStyle.None);
        }

        private void DeleteProvider(ProviderConfig provider)
        {
            _ = DeleteProviderAsync(provider);
        }

        private async Task DeleteProviderAsync(ProviderConfig provider)
        {
            if (provider == null)
                return;

            try
            {
                var app = await GetAppAsync();
                if (app == null)
                    return;

                await app.ProviderManager.DeleteProviderAsync(provider.id);
                await RefreshProvidersListAsync();
            }
            catch (Exception ex)
            {
                NeonLogger.LogError(ex.ToString());
            }
        }

        private void SwitchProvider(ProviderConfig provider)
        {
            _ = SwitchProviderAsync(provider);
        }

        private async Task SwitchProviderAsync(ProviderConfig provider)
        {
            try
            {
                var chat = await GetChatServiceAsync();
                if (chat == null)
                    return;

                await chat.SwitchProviderAsync(provider);
                RenderMessages(chat.CurrentChatViewModel?.Messages);
                await LoadSessionsAsync(chat);
                ShowChat();
            }
            catch (Exception ex)
            {
                NeonLogger.LogError(ex.ToString());
            }
        }

        private async Task RefreshProvidersListAsync()
        {
            if (_providersList == null)
                return;

            _providersList.Clear();

            var app = await GetAppAsync();
            if (!_isBound || app == null)
            {
                _providersList.Add(new Label("ProviderManager is not ready."));
                return;
            }

            var providers = await app.ProviderManager.GetAllProvidersAsync();
            if (providers.Count == 0)
            {
                _providersList.Add(new Label("No providers configured."));
                return;
            }

            foreach (var provider in providers)
                _providersList.Add(CreateProviderListItem(provider));
        }

        private VisualElement CreateProviderListItem(ProviderConfig provider)
        {
            var container = new VisualElement();
            container.AddToClassList("provider-item");

            var nameLabel = new Label(string.IsNullOrWhiteSpace(provider.displayName) ? "Provider" : provider.displayName);
            nameLabel.AddToClassList("provider-name");

            var urlLabel = new Label(provider.baseUrl ?? string.Empty);
            urlLabel.AddToClassList("provider-url");

            var modelLabel = new Label(provider.defaultModel ?? string.Empty);
            modelLabel.AddToClassList("provider-model");

            var buttons = new VisualElement();
            buttons.AddToClassList("provider-actions");

            buttons.Add(new Button(() => SwitchProvider(provider)) { text = "Use" });
            buttons.Add(new Button(() => StartEditingProvider(provider)) { text = "Edit" });
            buttons.Add(new Button(() => DeleteProvider(provider)) { text = "Delete" });

            container.Add(nameLabel);
            container.Add(urlLabel);
            container.Add(modelLabel);
            container.Add(buttons);

            return container;
        }
    }
}
