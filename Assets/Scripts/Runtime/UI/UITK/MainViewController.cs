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
        private const string ActiveNavClass = "nav__item--active";
        private const string ActiveSessionClass = "history__item--active";
        private const string ActiveProviderClass = "provider--active";

        private readonly List<VisualElement> _navItems = new List<VisualElement>();
        private readonly List<VisualElement> _sessionItems = new List<VisualElement>();

        private VisualElement _navChat;
        private VisualElement _navAvatars;
        private VisualElement _navProviders;
        private VisualElement _navHistory;
        private VisualElement _navThemes;
        private VisualElement _navSettings;
        private VisualElement _providerTag;

        private Label _navChatLabel;
        private Label _navAvatarsLabel;
        private Label _navProvidersLabel;
        private Label _navHistoryLabel;
        private Label _navThemesLabel;
        private Label _navSettingsLabel;
        private Label _navChatCount;
        private Label _navProvidersCount;

        private VisualElement _chatStage;
        private VisualElement _providersArea;
        private VisualElement _avatarsArea;
        private VisualElement _placeholderArea;
        private VisualElement _settingsArea;
        private VisualElement _topbarSep;
        private VisualElement _typingIndicator;

        private Label _topbarTitle;
        private Label _topbarSubtitle;
        private Label _placeholderTitle;
        private Label _placeholderBody;
        private Label _subtitleBody;
        private Label _connectionStatus;
        private Label _providerShort;
        private Label _providerName;
        private Label _providerModel;
        private Label _railProviderName;
        private Label _railProviderModel;
        private Label _editorProviderShort;
        private Label _editorProviderName;

        private Button _sendButton;
        private Button _summarizeButton;
        private Button _newSessionButton;
        private TextField _messageInput;
        private ScrollView _messagesList;
        private ScrollView _sessionsList;

        private ScrollView _providersList;
        private Button _addProviderButton;
        private Button _saveProviderButton;
        private Button _cancelEditButton;
        private TextField _editName;
        private TextField _editBaseUrl;
        private TextField _editApiKey;
        private TextField _editModel;
        private TextField _editMaxTokens;
        private Slider _editTemperature;
        private VisualElement _providerEditPanel;

        private CompanionApp _app;
        private ChatService _chatService;
        private ProviderConfig _editingProvider;
        private string _chatSubtitle = "0 сообщений · Neon";
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
            _navItems.Clear();

            _navChat = root.Q<VisualElement>("nav-chat");
            _navAvatars = root.Q<VisualElement>("nav-avatars");
            _navProviders = root.Q<VisualElement>("nav-providers");
            _navHistory = root.Q<VisualElement>("nav-history");
            _navThemes = root.Q<VisualElement>("nav-themes");
            _navSettings = root.Q<VisualElement>("nav-settings");
            _providerTag = root.Q<VisualElement>("provider-tag");

            AddNav(_navChat);
            AddNav(_navAvatars);
            AddNav(_navProviders);
            AddNav(_navHistory);
            AddNav(_navThemes);
            AddNav(_navSettings);

            _navChatLabel = root.Q<Label>("nav-chat-label");
            _navAvatarsLabel = root.Q<Label>("nav-avatars-label");
            _navProvidersLabel = root.Q<Label>("nav-providers-label");
            _navHistoryLabel = root.Q<Label>("nav-history-label");
            _navThemesLabel = root.Q<Label>("nav-themes-label");
            _navSettingsLabel = root.Q<Label>("nav-settings-label");
            _navChatCount = root.Q<Label>("nav-chat-count");
            _navProvidersCount = root.Q<Label>("nav-providers-count");

            _chatStage = root.Q<VisualElement>("chat-stage");
            _providersArea = root.Q<VisualElement>("providers-area");
            _avatarsArea = root.Q<VisualElement>("avatars-area");
            _placeholderArea = root.Q<VisualElement>("placeholder-area");
            _settingsArea = root.Q<VisualElement>("settings-area");
            _topbarSep = root.Q<VisualElement>("topbar-sep");
            _typingIndicator = root.Q<VisualElement>("typing-indicator");

            _topbarTitle = root.Q<Label>("topbar-title");
            _topbarSubtitle = root.Q<Label>("topbar-subtitle");
            _placeholderTitle = root.Q<Label>("placeholder-title");
            _placeholderBody = root.Q<Label>("placeholder-body");
            _subtitleBody = root.Q<Label>("subtitle-body");
            _connectionStatus = root.Q<Label>("connection-status");
            _providerShort = root.Q<Label>("provider-short");
            _providerName = root.Q<Label>("provider-name");
            _providerModel = root.Q<Label>("provider-model");
            _railProviderName = root.Q<Label>("rail-provider-name");
            _railProviderModel = root.Q<Label>("rail-provider-model");
            _editorProviderShort = root.Q<Label>("editor-provider-short");
            _editorProviderName = root.Q<Label>("editor-provider-name");

            _messageInput = root.Q<TextField>("message-input");
            _sendButton = root.Q<Button>("send-button");
            _summarizeButton = root.Q<Button>("summarize-btn");
            _newSessionButton = root.Q<Button>("new-session-btn");
            _messagesList = root.Q<ScrollView>("messages-list");
            _sessionsList = root.Q<ScrollView>("sessions-list");

            _providersList = root.Q<ScrollView>("providers-list");
            _addProviderButton = root.Q<Button>("add-provider-btn");
            _saveProviderButton = root.Q<Button>("save-provider-btn");
            _cancelEditButton = root.Q<Button>("cancel-edit-btn");
            _providerEditPanel = root.Q<VisualElement>("provider-edit-panel");
            _editName = root.Q<TextField>("edit-name");
            _editBaseUrl = root.Q<TextField>("edit-baseurl");
            _editApiKey = root.Q<TextField>("edit-apikey");
            _editModel = root.Q<TextField>("edit-model");
            _editMaxTokens = root.Q<TextField>("edit-maxtokens");
            _editTemperature = root.Q<Slider>("edit-temperature");

            _navChatLabel?.Localize("tab.chat");
            _navAvatarsLabel?.Localize("tab.avatar");
            _navProvidersLabel?.Localize("settings.providers");
            _navHistoryLabel?.Localize("chat.history");
            _navThemesLabel?.Localize("settings.themes");
            _navSettingsLabel?.Localize("tab.settings");

            SetDisplay(_providerEditPanel, DisplayStyle.None);
            SetSending(false);
            _isBound = true;
        }

        private void AddNav(VisualElement navItem)
        {
            if (navItem != null)
                _navItems.Add(navItem);
        }

        private void RegisterCallbacks()
        {
            RegisterClick(_navChat, OnNavChatClicked);
            RegisterClick(_navAvatars, OnNavAvatarsClicked);
            RegisterClick(_navProviders, OnNavProvidersClicked);
            RegisterClick(_navHistory, OnNavHistoryClicked);
            RegisterClick(_navThemes, OnNavThemesClicked);
            RegisterClick(_navSettings, OnNavSettingsClicked);
            RegisterClick(_providerTag, OnProviderTagClicked);

            RegisterClick(_sendButton, OnSendClicked);
            RegisterClick(_summarizeButton, OnSummarizeClicked);
            RegisterClick(_newSessionButton, OnNewSessionClicked);
            RegisterClick(_addProviderButton, OnAddProviderClicked);
            RegisterClick(_saveProviderButton, OnSaveProviderClicked);
            RegisterClick(_cancelEditButton, OnCancelEditClicked);

            if (_messageInput != null)
                _messageInput.RegisterCallback<KeyDownEvent>(OnInputKeyDown);
        }

        private void UnregisterCallbacks()
        {
            UnregisterClick(_navChat, OnNavChatClicked);
            UnregisterClick(_navAvatars, OnNavAvatarsClicked);
            UnregisterClick(_navProviders, OnNavProvidersClicked);
            UnregisterClick(_navHistory, OnNavHistoryClicked);
            UnregisterClick(_navThemes, OnNavThemesClicked);
            UnregisterClick(_navSettings, OnNavSettingsClicked);
            UnregisterClick(_providerTag, OnProviderTagClicked);

            UnregisterClick(_sendButton, OnSendClicked);
            UnregisterClick(_summarizeButton, OnSummarizeClicked);
            UnregisterClick(_newSessionButton, OnNewSessionClicked);
            UnregisterClick(_addProviderButton, OnAddProviderClicked);
            UnregisterClick(_saveProviderButton, OnSaveProviderClicked);
            UnregisterClick(_cancelEditButton, OnCancelEditClicked);

            if (_messageInput != null)
                _messageInput.UnregisterCallback<KeyDownEvent>(OnInputKeyDown);
        }

        private static void RegisterClick(VisualElement element, EventCallback<ClickEvent> handler)
        {
            if (element != null)
                element.RegisterCallback<ClickEvent>(handler);
        }

        private static void UnregisterClick(VisualElement element, EventCallback<ClickEvent> handler)
        {
            if (element != null)
                element.UnregisterCallback<ClickEvent>(handler);
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

        private void OnNavChatClicked(ClickEvent evt) => ShowChat();
        private void OnNavAvatarsClicked(ClickEvent evt) => ShowAvatars();
        private void OnNavProvidersClicked(ClickEvent evt) => ShowProviders();
        private void OnNavHistoryClicked(ClickEvent evt) => ShowHistory();
        private void OnNavThemesClicked(ClickEvent evt) => ShowThemes();
        private void OnNavSettingsClicked(ClickEvent evt) => ShowSettings();
        private void OnProviderTagClicked(ClickEvent evt) => ShowProviders();

        private void ShowChat()
        {
            SetActiveNav(_navChat);
            SetTopbar("Дизайн системы рендеринга 2D", _chatSubtitle);
            ShowArea(_chatStage);
        }

        private void ShowAvatars()
        {
            SetActiveNav(_navAvatars);
            SetTopbar("Аватары", "8 образов · Neon");
            ShowArea(_avatarsArea);
        }

        private void ShowProviders()
        {
            SetActiveNav(_navProviders);
            SetTopbar("Провайдеры", "OpenAI-compatible endpoints");
            ShowArea(_providersArea);
            _ = RefreshProvidersListAsync();
        }

        private void ShowHistory()
        {
            SetActiveNav(_navHistory);
            SetTopbar("История", _chatSubtitle);
            ShowArea(_chatStage);
        }

        private void ShowThemes()
        {
            SetActiveNav(_navThemes);
            SetPlaceholder("Темы", "Палитры и формы аватара будут вынесены сюда следующим этапом.");
        }

        private void ShowSettings()
        {
            SetActiveNav(_navSettings);
            SetTopbar("Настройки", string.Empty);
            ShowArea(_settingsArea);
        }

        private void SetPlaceholder(string title, string body)
        {
            SetTopbar(title, string.Empty);
            if (_placeholderTitle != null)
                _placeholderTitle.text = title;
            if (_placeholderBody != null)
                _placeholderBody.text = body;
            ShowArea(_placeholderArea);
        }

        private void ShowArea(VisualElement visible)
        {
            SetDisplay(_chatStage, visible == _chatStage ? DisplayStyle.Flex : DisplayStyle.None);
            SetDisplay(_providersArea, visible == _providersArea ? DisplayStyle.Flex : DisplayStyle.None);
            SetDisplay(_avatarsArea, visible == _avatarsArea ? DisplayStyle.Flex : DisplayStyle.None);
            SetDisplay(_placeholderArea, visible == _placeholderArea ? DisplayStyle.Flex : DisplayStyle.None);
            SetDisplay(_settingsArea, visible == _settingsArea ? DisplayStyle.Flex : DisplayStyle.None);
        }

        private void SetTopbar(string title, string subtitle)
        {
            if (_topbarTitle != null)
                _topbarTitle.text = title;

            bool hasSubtitle = !string.IsNullOrWhiteSpace(subtitle);
            if (_topbarSubtitle != null)
            {
                _topbarSubtitle.text = subtitle ?? string.Empty;
                _topbarSubtitle.style.display = hasSubtitle ? DisplayStyle.Flex : DisplayStyle.None;
            }

            SetDisplay(_topbarSep, hasSubtitle ? DisplayStyle.Flex : DisplayStyle.None);
        }

        private void SetActiveNav(VisualElement active)
        {
            foreach (var navItem in _navItems)
                navItem.EnableInClassList(ActiveNavClass, navItem == active);
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

                RenderMessages(BuildPendingMessages(chat.CurrentChatViewModel?.Messages, message));
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

            if (_connectionStatus != null)
                _connectionStatus.text = isSending ? "generating · live" : "connected · 14:23";

            SetDisplay(_typingIndicator, isSending ? DisplayStyle.Flex : DisplayStyle.None);
            SetDisplay(_subtitleBody, isSending ? DisplayStyle.None : DisplayStyle.Flex);
        }

        private void OnSummarizeClicked()
        {
            AddSystemMessage("Summarize is not implemented yet.");
        }

        private void OnNewSessionClicked()
        {
            _ = StartNewSessionAsync();
        }

        private async Task StartNewSessionAsync()
        {
            try
            {
                var chat = await GetChatServiceAsync();
                if (chat == null)
                    return;

                await chat.StartNewSessionAsync();
                RenderMessages(chat.CurrentChatViewModel?.Messages);
                await LoadSessionsAsync(chat);
                ShowChat();
            }
            catch (Exception ex)
            {
                NeonLogger.LogError(ex.ToString());
            }
        }

        private async Task RefreshAsync()
        {
            try
            {
                var chat = await GetChatServiceAsync();
                if (!_isBound || chat == null)
                    return;

                SetProviderHeader(chat.CurrentProvider);
                RenderMessages(chat.CurrentChatViewModel?.Messages);
                await LoadSessionsAsync(chat);
                await RefreshProvidersListAsync();
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

            if (_navChatCount != null)
                _navChatCount.text = sessions.Count.ToString();

            var groupLabel = new Label("Недавние");
            groupLabel.AddToClassList("history__group");
            _sessionsList.Add(groupLabel);

            if (sessions.Count == 0)
            {
                var empty = new Label("Пока нет сессий");
                empty.AddToClassList("history__meta");
                _sessionsList.Add(empty);
                return;
            }

            for (int i = 0; i < sessions.Count; i++)
            {
                var item = CreateSessionItem(sessions[i], i == 0);
                _sessionsList.Add(item);
                _sessionItems.Add(item);
            }
        }

        private VisualElement CreateSessionItem(ChatSession session, bool isActive)
        {
            var container = new VisualElement();
            container.AddToClassList("history__item");
            container.EnableInClassList(ActiveSessionClass, isActive);

            var titleLabel = new Label(string.IsNullOrWhiteSpace(session.title) ? "New chat" : session.title);
            titleLabel.AddToClassList("history__title");

            int count = session.messages?.Count ?? 0;
            var metaLabel = new Label($"neon · {count} msg");
            metaLabel.AddToClassList("history__meta");

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
                ShowChat();
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
            int count = messages?.Count ?? 0;
            _chatSubtitle = $"{MessageCountText(count)} · Neon";

            if (_topbarSubtitle != null)
                _topbarSubtitle.text = _chatSubtitle;

            if (_navChatCount != null)
                _navChatCount.text = count.ToString();

            RenderTranscript(messages);

            if (_subtitleBody == null)
                return;

            string text = "Готова помочь. С чего сегодня начнём: настройка провайдера, аватары или прошлый разговор о шейдерах?";

            if (messages != null)
            {
                for (int i = messages.Count - 1; i >= 0; i--)
                {
                    var message = messages[i];
                    if (message == null || string.IsNullOrWhiteSpace(message.content))
                        continue;

                    if (message.role != "user")
                    {
                        text = message.content;
                        break;
                    }
                }
            }

            _subtitleBody.text = TrimForSubtitle(text);
        }

        private void RenderTranscript(IReadOnlyList<ChatMessage> messages)
        {
            if (_messagesList == null)
                return;

            _messagesList.Clear();

            if (messages == null || messages.Count == 0)
            {
                _messagesList.Add(CreateEmptyTranscript());
                return;
            }

            bool hasVisibleMessages = false;
            for (int i = 0; i < messages.Count; i++)
            {
                var message = messages[i];
                if (message == null || string.IsNullOrWhiteSpace(message.content))
                    continue;

                _messagesList.Add(CreateMessageElement(message));
                hasVisibleMessages = true;
            }

            if (!hasVisibleMessages)
            {
                _messagesList.Add(CreateEmptyTranscript());
                return;
            }

            ScrollTranscriptToBottom();
        }

        private static VisualElement CreateEmptyTranscript()
        {
            var container = new VisualElement();
            container.AddToClassList("transcript__empty");

            var title = new Label("Пока нет сообщений");
            title.AddToClassList("transcript__empty-title");

            var body = new Label("Начни диалог ниже, и здесь появится полная история текущей сессии.");
            body.AddToClassList("transcript__empty-body");

            container.Add(title);
            container.Add(body);
            return container;
        }

        private static VisualElement CreateMessageElement(ChatMessage message)
        {
            string role = NormalizeRole(message.role);

            var row = new VisualElement();
            row.AddToClassList("transcript__row");
            row.AddToClassList($"transcript__row--{role}");

            var bubble = new VisualElement();
            bubble.AddToClassList("transcript__bubble");
            bubble.AddToClassList($"transcript__bubble--{role}");

            var meta = new VisualElement();
            meta.AddToClassList("transcript__meta");

            var roleLabel = new Label(DisplayRole(role));
            roleLabel.AddToClassList("transcript__role");

            var timeLabel = new Label(FormatMessageTime(message.unixTimeSeconds));
            timeLabel.AddToClassList("transcript__time");

            var body = new Label(message.content);
            body.AddToClassList("transcript__body");

            meta.Add(roleLabel);
            meta.Add(timeLabel);
            bubble.Add(meta);
            bubble.Add(body);
            row.Add(bubble);

            return row;
        }

        private void ScrollTranscriptToBottom()
        {
            if (_messagesList == null)
                return;

            _messagesList.schedule.Execute(() =>
            {
                var content = _messagesList?.contentContainer;
                if (content == null || content.childCount == 0)
                    return;

                _messagesList.ScrollTo(content[content.childCount - 1]);
            });
        }

        private static IReadOnlyList<ChatMessage> BuildPendingMessages(IReadOnlyList<ChatMessage> currentMessages, string pendingText)
        {
            var messages = new List<ChatMessage>();

            if (currentMessages != null)
            {
                for (int i = 0; i < currentMessages.Count; i++)
                {
                    if (currentMessages[i] != null)
                        messages.Add(currentMessages[i]);
                }
            }

            messages.Add(new ChatMessage
            {
                role = "user",
                content = pendingText,
                unixTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });

            return messages;
        }

        private static string NormalizeRole(string role)
        {
            if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
                return "user";

            if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase))
                return "system";

            return "assistant";
        }

        private static string DisplayRole(string role)
        {
            switch (role)
            {
                case "user":
                    return "Ты";
                case "system":
                    return "Система";
                default:
                    return "Neon";
            }
        }

        private static string FormatMessageTime(long unixTimeSeconds)
        {
            if (unixTimeSeconds <= 0)
                return string.Empty;

            return DateTimeOffset
                .FromUnixTimeSeconds(unixTimeSeconds)
                .ToLocalTime()
                .ToString("HH:mm");
        }

        private static string MessageCountText(int count)
        {
            int mod100 = count % 100;
            int mod10 = count % 10;
            string word;

            if (mod100 >= 11 && mod100 <= 14)
                word = "сообщений";
            else if (mod10 == 1)
                word = "сообщение";
            else if (mod10 >= 2 && mod10 <= 4)
                word = "сообщения";
            else
                word = "сообщений";

            return $"{count} {word}";
        }

        private static string TrimForSubtitle(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            string normalized = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return normalized.Length <= 320 ? normalized : normalized.Substring(0, 317) + "...";
        }

        private void AddSystemMessage(string text)
        {
            SetSending(false);
            if (_subtitleBody != null)
                _subtitleBody.text = text;
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

            if (_editorProviderShort != null)
                _editorProviderShort.text = BuildProviderShort(_editingProvider);
            if (_editorProviderName != null)
                _editorProviderName.text = string.IsNullOrWhiteSpace(_editingProvider.displayName) ? "Provider" : _editingProvider.displayName;
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
            if (_editMaxTokens != null)
                _editMaxTokens.value = _editingProvider.maxTokens.ToString();

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

                if (_editMaxTokens != null && int.TryParse(_editMaxTokens.value, out int maxTokens))
                    _editingProvider.maxTokens = maxTokens;

                await app.ProviderManager.SaveProviderAsync(_editingProvider);
                SetProviderHeader(_editingProvider);
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
                if (_editingProvider?.id == provider.id)
                {
                    _editingProvider = null;
                    SetDisplay(_providerEditPanel, DisplayStyle.None);
                }

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
                SetProviderHeader(provider);
                RenderMessages(chat.CurrentChatViewModel?.Messages);
                await LoadSessionsAsync(chat);
                await RefreshProvidersListAsync();
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
            if (_navProvidersCount != null)
                _navProvidersCount.text = providers.Count.ToString();

            if (providers.Count == 0)
            {
                _providersList.Add(new Label("No providers configured."));
                return;
            }

            var chat = await GetChatServiceAsync();
            string activeProviderId = chat?.CurrentProvider?.id;

            for (int i = 0; i < providers.Count; i++)
            {
                var provider = providers[i];
                bool isActive = string.IsNullOrEmpty(activeProviderId) ? i == 0 : provider.id == activeProviderId;
                _providersList.Add(CreateProviderListItem(provider, isActive));

                if (_editingProvider == null && isActive)
                    StartEditingProvider(provider);
            }

            if (_editingProvider == null)
                StartEditingProvider(providers[0]);
        }

        private VisualElement CreateProviderListItem(ProviderConfig provider, bool isActive)
        {
            var container = new VisualElement();
            container.AddToClassList("provider");
            container.EnableInClassList(ActiveProviderClass, isActive);
            container.RegisterCallback<ClickEvent>(evt => StartEditingProvider(provider));

            var logo = new VisualElement();
            logo.AddToClassList("provider__logo");
            logo.Add(new Label(BuildProviderShort(provider)));

            var body = new VisualElement();
            body.AddToClassList("provider__body");

            var nameRow = new VisualElement();
            nameRow.AddToClassList("provider__name-row");

            var nameLabel = new Label(string.IsNullOrWhiteSpace(provider.displayName) ? "Provider" : provider.displayName);
            nameLabel.AddToClassList("provider__name");
            nameRow.Add(nameLabel);

            if (isActive)
            {
                var chip = new Label("active");
                chip.AddToClassList("chip");
                chip.AddToClassList("chip--accent");
                nameRow.Add(chip);
            }

            var urlLabel = new Label(provider.baseUrl ?? string.Empty);
            urlLabel.AddToClassList("provider__url");

            body.Add(nameRow);
            body.Add(urlLabel);

            var modelLabel = new Label(provider.defaultModel ?? string.Empty);
            modelLabel.AddToClassList("chip");
            modelLabel.AddToClassList("provider__model");

            var meta = new VisualElement();
            meta.AddToClassList("provider__meta");
            var metaLabel = new Label("Latency");
            metaLabel.AddToClassList("provider__meta-label");
            var metaValue = new Label(provider.baseUrl != null && provider.baseUrl.Contains("localhost") ? "52 ms" : "local");
            metaValue.AddToClassList("provider__meta-value");
            meta.Add(metaLabel);
            meta.Add(metaValue);

            var actions = new VisualElement();
            actions.AddToClassList("provider__actions");
            var useButton = new Button(() => SwitchProvider(provider)) { text = "Use" };
            var editButton = new Button(() => StartEditingProvider(provider)) { text = "Edit" };
            var deleteButton = new Button(() => DeleteProvider(provider)) { text = "Delete" };
            useButton.AddToClassList("btn");
            editButton.AddToClassList("btn");
            deleteButton.AddToClassList("btn");
            actions.Add(useButton);
            actions.Add(editButton);
            actions.Add(deleteButton);

            container.Add(logo);
            container.Add(body);
            container.Add(modelLabel);
            container.Add(meta);
            container.Add(actions);

            return container;
        }

        private void SetProviderHeader(ProviderConfig provider)
        {
            if (provider == null)
                return;

            string shortName = BuildProviderShort(provider);
            string displayName = string.IsNullOrWhiteSpace(provider.displayName) ? "Provider" : provider.displayName;
            string model = string.IsNullOrWhiteSpace(provider.defaultModel) ? "model" : provider.defaultModel;

            if (_providerShort != null)
                _providerShort.text = shortName;
            if (_providerName != null)
                _providerName.text = displayName;
            if (_providerModel != null)
                _providerModel.text = model;
            if (_railProviderName != null)
                _railProviderName.text = displayName;
            if (_railProviderModel != null)
                _railProviderModel.text = model;
        }

        private static string BuildProviderShort(ProviderConfig provider)
        {
            string name = provider?.displayName;
            if (string.IsNullOrWhiteSpace(name))
                return "API";

            string lower = name.ToLowerInvariant();
            if (lower.Contains("openai"))
                return "OAI";
            if (lower.Contains("grok"))
                return "GRK";
            if (lower.Contains("openrouter"))
                return "OR";
            if (lower.Contains("ollama"))
                return "OL";

            string compact = string.Empty;
            for (int i = 0; i < name.Length && compact.Length < 3; i++)
            {
                if (char.IsLetterOrDigit(name[i]))
                    compact += char.ToUpperInvariant(name[i]);
            }

            return string.IsNullOrEmpty(compact) ? "API" : compact;
        }
    }
}
