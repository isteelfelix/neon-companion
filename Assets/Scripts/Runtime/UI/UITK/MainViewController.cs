using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Chat;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Localization;
using NeonCompanion.Runtime.Platform;
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

        private VisualElement _root;

        private VisualElement _chatStage;
        private VisualElement _providersArea;
        private VisualElement _avatarsArea;
        private VisualElement _placeholderArea;
        private VisualElement _settingsArea;
        private VisualElement _topbarSep;
        private VisualElement _typingIndicator;

        // ===== Settings page =====
        private DropdownField _settingsLanguage;
        private Toggle _settingsHistory;
        private Toggle _settingsStreaming;
        private Toggle _settingsSystemPrompt;
        private Toggle _settingsEncryptKeys;
        private Toggle _settingsMaskLogs;
        private Label _settingsStoragePath;
        private Label _settingsVersion;
        private Button _shapeRound;
        private Button _shapeSquare;
        private Button _shapeHex;
        private Toggle _settingsShowHalo;
        private Toggle _settingsBreathing;
        private string _avatarShape = "round";

        // ===== Avatar gallery =====
        private static readonly string[] BuiltInAvatarIds =
            { "neon", "aurora", "ember", "glass", "flora", "mono", "cobalt", "rose" };
        private string _activeAvatarId = "neon";
        private VisualElement _avatarArt;
        private VisualElement _avatarCircle;
        private VisualElement _previewHero;
        private Label _previewTitle;
        private Label _previewTag;
        private Label _previewPersona;
        private Label _streamingLabel;
        private Button _previewApplyBtn;
        private Button _previewEditPersonaBtn;

        // ===== Typing animation =====
        private VisualElement _typingDot1;
        private VisualElement _typingDot2;
        private VisualElement _typingDot3;
        private IVisualElementScheduledItem _typingSchedule;
        private int _typingFrame;

        // ===== Breathing animation =====
        private IVisualElementScheduledItem _breathSchedule;
        private long _breathStartMs;

        private Label _topbarTitle;
        private Label _topbarSubtitle;
        private Label _placeholderTitle;
        private Label _placeholderBody;
        private Label _subtitleRole;
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
        private Button _searchButton;
        private Button _moreButton;
        private Button _newSessionButton;
        private Button _settingsOpenFolderBtn;
        private Button _settingsExportBtn;
        private Button _settingsClearBtn;
        private Button _testProviderBtn;
        private VisualElement _testRow;
        private Label _testRowLabel;
        private TextField _messageInput;
        private ScrollView _messagesList;
        private ScrollView _sessionsList;
        private VisualElement _historySearchBar;
        private TextField _historySearchInput;
        private Button _historySearchBtn;
        private Button _historySearchClear;
        private string _sessionSearchQuery = string.Empty;

        private ScrollView _providersList;
        private Button _addProviderButton;
        private Button _saveProviderButton;
        private Button _cancelEditButton;
        private Button _importProviderButton;
        private Button _copyButton;
        private Button _regenerateButton;
        private Button _listenButton;
        private Button _attachButton;
        private Button _avatarUploadBtn;
        private Button _avatarOpenFolderBtn;
        private VisualElement _avatarUploadTile;
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
            _root = root;
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
            _subtitleRole = root.Q<Label>("subtitle-role");
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
            _searchButton = root.Q<Button>("search-btn");
            _moreButton = root.Q<Button>("more-btn");
            _newSessionButton = root.Q<Button>("new-session-btn");
            _messagesList = root.Q<ScrollView>("messages-list");
            _sessionsList = root.Q<ScrollView>("sessions-list");
            _historySearchBar   = root.Q<VisualElement>("history-search-bar");
            _historySearchInput = root.Q<TextField>("history-search-input");
            _historySearchBtn   = root.Q<Button>("history-search-btn");
            _historySearchClear = root.Q<Button>("history-search-clear");

            _providersList = root.Q<ScrollView>("providers-list");
            _addProviderButton    = root.Q<Button>("add-provider-btn");
            _importProviderButton = root.Q<Button>("import-provider-btn");
            _saveProviderButton   = root.Q<Button>("save-provider-btn");
            _cancelEditButton     = root.Q<Button>("cancel-edit-btn");
            _copyButton       = root.Q<Button>("copy-btn");
            _regenerateButton = root.Q<Button>("refresh-btn");
            _listenButton = root.Q<Button>("listen-btn");
            _attachButton = root.Q<Button>("attach-btn");
            _avatarUploadBtn      = root.Q<Button>("avatar-upload-btn");
            _avatarOpenFolderBtn  = root.Q<Button>("avatar-open-folder-btn");
            _avatarUploadTile     = root.Q<VisualElement>("avtile-upload");
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

            // Settings action buttons
            _settingsOpenFolderBtn = root.Q<Button>("settings-open-folder");
            _settingsExportBtn     = root.Q<Button>("settings-export-btn");
            _settingsClearBtn      = root.Q<Button>("settings-clear-btn");
            _testProviderBtn = root.Q<Button>("test-provider-btn");
            _testRow         = root.Q<VisualElement>("test-row");
            _testRowLabel    = root.Q<Label>("test-row-label");

            // Settings page controls
            _settingsLanguage    = root.Q<DropdownField>("settings-language");
            _settingsHistory     = root.Q<Toggle>("settings-save-history");
            _settingsStreaming    = root.Q<Toggle>("settings-streaming");
            _settingsSystemPrompt = root.Q<Toggle>("settings-system-prompt");
            _settingsEncryptKeys = root.Q<Toggle>("settings-encrypt-keys");
            _settingsMaskLogs    = root.Q<Toggle>("settings-mask-logs");
            _settingsStoragePath = root.Q<Label>("settings-storage-path");
            _settingsVersion     = root.Q<Label>("settings-version");
            _shapeRound  = root.Q<Button>("shape-round");
            _shapeSquare = root.Q<Button>("shape-square");
            _shapeHex    = root.Q<Button>("shape-hex");
            _settingsShowHalo   = root.Q<Toggle>("settings-show-halo");
            _settingsBreathing  = root.Q<Toggle>("settings-breathing");

            // Avatar elements
            _avatarArt    = root.Q<VisualElement>("avatar-art");
            _avatarCircle = root.Q<VisualElement>("avatar-circle");
            _previewHero  = root.Q<VisualElement>("preview-hero");
            _previewTitle   = root.Q<Label>("preview-title");
            _previewTag     = root.Q<Label>("preview-tag");
            _previewPersona = root.Q<Label>("preview-persona");
            _previewApplyBtn      = root.Q<Button>("preview-apply-btn");
            _previewEditPersonaBtn = root.Q<Button>("preview-edit-persona-btn");

            // Typing dots
            var typingEl = root.Q<VisualElement>("typing-indicator");
            if (typingEl != null)
            {
                var dots = typingEl.Query<VisualElement>(className: "typing__dot").ToList();
                _typingDot1 = dots.Count > 0 ? dots[0] : null;
                _typingDot2 = dots.Count > 1 ? dots[1] : null;
                _typingDot3 = dots.Count > 2 ? dots[2] : null;
            }

            SetDisplay(_providerEditPanel, DisplayStyle.None);
            SetSending(false);
            _ = LoadSettingsAsync();
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
            RegisterClick(_searchButton, OnSearchClicked);
            RegisterClick(_moreButton, OnMoreClicked);
            RegisterClick(_newSessionButton, OnNewSessionClicked);
            RegisterClick(_historySearchBtn, OnHistorySearchToggled);
            RegisterClick(_historySearchClear, OnHistorySearchCleared);
            if (_historySearchInput != null)
                _historySearchInput.RegisterCallback<ChangeEvent<string>>(OnHistorySearchChanged);
            RegisterClick(_addProviderButton, OnAddProviderClicked);
            RegisterClick(_importProviderButton, OnImportProviderClicked);
            RegisterClick(_saveProviderButton, OnSaveProviderClicked);
            RegisterClick(_cancelEditButton, OnCancelEditClicked);
            RegisterClick(_settingsOpenFolderBtn, OnOpenFolderClicked);
            RegisterClick(_settingsExportBtn, OnExportChatsClicked);
            RegisterClick(_settingsClearBtn, OnClearDataClicked);
            RegisterClick(_testProviderBtn, OnTestProviderClicked);
            RegisterClick(_copyButton, OnCopyLastMessageClicked);
            RegisterClick(_regenerateButton, OnRegenerateClicked);
            RegisterClick(_listenButton, OnListenClicked);
            RegisterClick(_attachButton, OnAttachClicked);
            RegisterClick(_avatarUploadBtn, OnAvatarUploadClicked);
            RegisterClick(_avatarOpenFolderBtn, OnAvatarOpenFolderClicked);
            if (_avatarUploadTile != null)
                _avatarUploadTile.RegisterCallback<ClickEvent>(_ => OnAvatarUploadClicked());

            if (_messageInput != null)
                _messageInput.RegisterCallback<KeyDownEvent>(OnInputKeyDown);

            RegisterSettingsCallbacks();
            RegisterAvatarGalleryCallbacks();
            RegisterClick(_previewApplyBtn, OnPreviewApplyClicked);
            RegisterClick(_previewEditPersonaBtn, OnPreviewEditPersonaClicked);
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
            UnregisterClick(_searchButton, OnSearchClicked);
            UnregisterClick(_moreButton, OnMoreClicked);
            UnregisterClick(_newSessionButton, OnNewSessionClicked);
            UnregisterClick(_addProviderButton, OnAddProviderClicked);
            UnregisterClick(_saveProviderButton, OnSaveProviderClicked);
            UnregisterClick(_cancelEditButton, OnCancelEditClicked);
            UnregisterClick(_listenButton, OnListenClicked);
            UnregisterClick(_attachButton, OnAttachClicked);

            if (_messageInput != null)
                _messageInput.UnregisterCallback<KeyDownEvent>(OnInputKeyDown);

            _typingSchedule?.Pause();
            _breathSchedule?.Pause();
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

                bool streaming = _settingsStreaming?.value ?? false;
                chat.UseStreaming = streaming;

                RenderMessages(BuildPendingMessages(chat.CurrentChatViewModel?.Messages, message));

                if (streaming)
                {
                    AddStreamingBubble();
                    await chat.SendMessageAsync(message, OnStreamToken);
                    _streamingLabel = null;
                }
                else
                {
                    await chat.SendMessageAsync(message);
                }

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

        private void AddStreamingBubble()
        {
            if (_messagesList == null) return;
            var placeholder = CreateMessageElement(new ChatMessage { role = "assistant", content = "" });
            _messagesList.Add(placeholder);
            _streamingLabel = placeholder.Q<Label>(className: "transcript__body");
            ScrollTranscriptToBottom();
        }

        private void OnStreamToken(string token)
        {
            if (_streamingLabel != null)
                _streamingLabel.text += token;
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

            if (isSending) StartTypingAnimation();
            else StopTypingAnimation();
        }

        private void OnSummarizeClicked()
        {
            _ = SummarizeCurrentConversationAsync();
        }

        private async Task SummarizeCurrentConversationAsync()
        {
            try
            {
                var chat = await GetChatServiceAsync();
                if (chat == null)
                {
                    AddSystemMessage("Приложение не инициализировано.");
                    return;
                }

                string summary = await chat.SummarizeCurrentConversationAsync();
                AddSystemMessage(summary);
            }
            catch (Exception ex)
            {
                AddSystemMessage($"[Ошибка] {ex.Message}");
                NeonLogger.LogError(ex.ToString());
            }
        }

        private void OnSearchClicked()
        {
            _ = SearchSessionsFromComposerAsync();
        }

        private async Task SearchSessionsFromComposerAsync()
        {
            try
            {
                _sessionSearchQuery = _messageInput?.value?.Trim() ?? string.Empty;
                if (_historySearchInput != null)
                    _historySearchInput.SetValueWithoutNotify(_sessionSearchQuery);

                var chat = await GetChatServiceAsync();
                if (chat == null)
                    return;

                var allSessions = await chat.GetAllSessionsAsync();
                if (_isBound)
                    RenderSessionList(allSessions);

                ShowHistory();
            }
            catch (Exception ex)
            {
                AddSystemMessage($"[Ошибка поиска] {ex.Message}");
                NeonLogger.LogError(ex.ToString());
            }
        }

        private void OnMoreClicked()
        {
            ShowSettings();
        }

        private void OnListenClicked()
        {
            var messages = _chatService?.CurrentChatViewModel?.Messages;
            if (messages == null || messages.Count == 0)
            {
                AddSystemMessage("Нет ответа ассистента для копирования.");
                return;
            }

            for (int i = messages.Count - 1; i >= 0; i--)
            {
                var msg = messages[i];
                if (msg?.role == "assistant" && !string.IsNullOrWhiteSpace(msg.content))
                {
                    GUIUtility.systemCopyBuffer = msg.content;
                    AddSystemMessage("Последний ответ ассистента скопирован в буфер обмена.");
                    return;
                }
            }

            AddSystemMessage("Нет ответа ассистента для копирования.");
        }

        private void OnAttachClicked()
        {
            _ = AttachImageTokenAsync();
        }

        private async Task AttachImageTokenAsync()
        {
            try
            {
                var app = await GetAppAsync();
                if (app == null || _messageInput == null) return;

                var filePicker = app.Services.GetRequired<IFilePickerService>();
                string path = await filePicker.PickImagePathAsync();
                if (string.IsNullOrEmpty(path)) return;

                string fileName = System.IO.Path.GetFileName(path);
                string token = $"[attachment: {fileName}]";
                string current = _messageInput.value ?? string.Empty;
                _messageInput.value = string.IsNullOrWhiteSpace(current)
                    ? token
                    : $"{current.TrimEnd()} {token}";
                _messageInput.Focus();
            }
            catch (Exception ex)
            {
                AddSystemMessage($"[Ошибка вложения] {ex.Message}");
                NeonLogger.LogError(ex.ToString());
            }
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
                var app = await GetAppAsync();
                if (!_isBound || app == null) return;

                var providers = await app.ProviderManager.GetAllProvidersAsync();
                if (providers.Count == 0)
                {
                    SetNoProviderState();
                    return;
                }

                var chat = await GetChatServiceAsync();
                if (!_isBound || chat == null) return;

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

        private void SetNoProviderState()
        {
            if (_subtitleBody != null)
                _subtitleBody.text = "Провайдер не настроен. Перейди в Провайдеры и добавь API-ключ.";
            if (_subtitleRole != null)
                _subtitleRole.text = "Система";
            if (_sendButton != null)
                _sendButton.SetEnabled(false);
            if (_connectionStatus != null)
                _connectionStatus.text = "no provider";
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

            var settingsSnap = app.Settings.Load();
            if (settingsSnap != null)
                _chatService.SaveChatHistory = settingsSnap.saveChatHistory;

            await _chatService.GetOrCreateChatAsync();
            return _chatService;
        }

        private async Task LoadSessionsAsync(ChatService chat)
        {
            if (_sessionsList == null)
                return;

            var allSessions = await chat.GetAllSessionsAsync();
            if (!_isBound)
                return;

            if (_navChatCount != null)
                _navChatCount.text = allSessions.Count.ToString();

            // Sync topbar title with current (first/most-recent) session title
            if (allSessions.Count > 0 && _topbarTitle != null)
            {
                string sessionTitle = allSessions[0].title;
                if (!string.IsNullOrWhiteSpace(sessionTitle) && sessionTitle != "New chat")
                    _topbarTitle.text = sessionTitle;
            }

            RenderSessionList(allSessions);
        }

        private void RenderSessionList(List<ChatSession> allSessions)
        {
            if (_sessionsList == null) return;

            _sessionsList.Clear();
            _sessionItems.Clear();

            var sessions = string.IsNullOrWhiteSpace(_sessionSearchQuery)
                ? allSessions
                : allSessions.FindAll(s =>
                    (s.title ?? string.Empty).IndexOf(_sessionSearchQuery, StringComparison.OrdinalIgnoreCase) >= 0);

            var groupLabel = new Label(string.IsNullOrWhiteSpace(_sessionSearchQuery)
                ? "Недавние"
                : $"Результаты: {sessions.Count}");
            groupLabel.AddToClassList("history__group");
            _sessionsList.Add(groupLabel);

            if (sessions.Count == 0)
            {
                var empty = new Label(string.IsNullOrWhiteSpace(_sessionSearchQuery)
                    ? "Пока нет сессий"
                    : "Ничего не найдено");
                empty.AddToClassList("history__meta");
                _sessionsList.Add(empty);
                return;
            }

            for (int i = 0; i < sessions.Count; i++)
            {
                var item = CreateSessionItem(sessions[i], i == 0 && string.IsNullOrWhiteSpace(_sessionSearchQuery));
                _sessionsList.Add(item);
                _sessionItems.Add(item);
            }
        }

        // ---- History search ----

        private void OnHistorySearchToggled()
        {
            if (_historySearchBar == null) return;
            bool isVisible = _historySearchBar.style.display == DisplayStyle.Flex;
            SetDisplay(_historySearchBar, isVisible ? DisplayStyle.None : DisplayStyle.Flex);
            if (!isVisible && _historySearchInput != null)
                _historySearchInput.Focus();
            if (isVisible)
                OnHistorySearchCleared();
        }

        private void OnHistorySearchCleared()
        {
            _sessionSearchQuery = string.Empty;
            if (_historySearchInput != null)
                _historySearchInput.SetValueWithoutNotify(string.Empty);
            SetDisplay(_historySearchBar, DisplayStyle.None);
            _ = RefreshSessionsFromCacheAsync();
        }

        private void OnHistorySearchChanged(ChangeEvent<string> evt)
        {
            _sessionSearchQuery = evt.newValue ?? string.Empty;
            _ = RefreshSessionsFromCacheAsync();
        }

        private async Task RefreshSessionsFromCacheAsync()
        {
            var chat = await GetChatServiceAsync();
            if (chat == null) return;
            var allSessions = await chat.GetAllSessionsAsync();
            if (_isBound) RenderSessionList(allSessions);
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
            string avatarName = AvatarDisplayName(_activeAvatarId);
            _chatSubtitle = $"{MessageCountText(count)} · {avatarName}";

            if (_topbarSubtitle != null)
                _topbarSubtitle.text = _chatSubtitle;

            if (_navChatCount != null)
                _navChatCount.text = count.ToString();

            if (_subtitleRole != null)
                _subtitleRole.text = avatarName;

            RenderTranscript(messages);

            if (_subtitleBody == null)
                return;

            string text = $"Готова помочь. С чего начнём?";
            if (messages != null)
            {
                for (int i = messages.Count - 1; i >= 0; i--)
                {
                    var msg = messages[i];
                    if (msg == null || string.IsNullOrWhiteSpace(msg.content)) continue;
                    if (msg.role != "user") { text = msg.content; break; }
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

        private void OnTestProviderClicked()
        {
            _ = TestProviderConnectionAsync();
        }

        private async Task TestProviderConnectionAsync()
        {
            if (_editingProvider == null) return;

            try
            {
                var app = await GetAppAsync();
                if (app == null) return;

                if (_testProviderBtn != null) _testProviderBtn.SetEnabled(false);
                SetTestRow(null, "Проверяем соединение…");

                var result = await app.AiClient.TestConnectionAsync(_editingProvider);
                SetTestRow(result.Success, result.Message);
            }
            catch (Exception ex)
            {
                SetTestRow(false, ex.Message);
            }
            finally
            {
                if (_testProviderBtn != null) _testProviderBtn.SetEnabled(true);
            }
        }

        private void SetTestRow(bool? success, string message)
        {
            if (_testRowLabel != null)
                _testRowLabel.text = message ?? string.Empty;

            if (_testRow == null) return;

            _testRow.EnableInClassList("testrow--ok",    success == true);
            _testRow.EnableInClassList("testrow--error", success == false);
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

        // ============================================================
        // Settings page
        // ============================================================

        private void RegisterSettingsCallbacks()
        {
            RegisterClick(_shapeRound,  () => SetAvatarShape("round"));
            RegisterClick(_shapeSquare, () => SetAvatarShape("square"));
            RegisterClick(_shapeHex,    () => SetAvatarShape("hex"));

            RegisterToggleChanged(_settingsHistory,      _ => SaveSettings());
            RegisterToggleChanged(_settingsStreaming,     _ => SaveSettings());
            RegisterToggleChanged(_settingsSystemPrompt, _ => SaveSettings());
            RegisterToggleChanged(_settingsEncryptKeys,  _ => SaveSettings());
            RegisterToggleChanged(_settingsMaskLogs,     _ => SaveSettings());
            RegisterToggleChanged(_settingsShowHalo,    v => { ApplyHaloVisibility(v); SaveSettings(); });
            RegisterToggleChanged(_settingsBreathing,   v => { ApplyBreathingAnimation(v); SaveSettings(); });

            if (_settingsLanguage != null)
                _settingsLanguage.RegisterCallback<ChangeEvent<string>>(_ => SaveSettings());
        }

        private static void RegisterToggleChanged(Toggle toggle, Action<bool> handler)
        {
            if (toggle != null)
                toggle.RegisterCallback<ChangeEvent<bool>>(evt => handler(evt.newValue));
        }

        private async Task LoadSettingsAsync()
        {
            try
            {
                var app = await GetAppAsync();
                if (!_isBound || app == null) return;

                var s = app.Settings.Load() ?? new AppSettings();
                _activeAvatarId = string.IsNullOrEmpty(s.activeAvatarId) ? "neon" : s.activeAvatarId;

                _settingsHistory?.SetValueWithoutNotify(s.saveChatHistory);
                _settingsStreaming?.SetValueWithoutNotify(s.streaming);
                _settingsSystemPrompt?.SetValueWithoutNotify(s.useSystemPrompt);
                _settingsEncryptKeys?.SetValueWithoutNotify(s.encryptKeys);
                _settingsMaskLogs?.SetValueWithoutNotify(s.maskLogs);
                _settingsShowHalo?.SetValueWithoutNotify(s.showHalo);
                _settingsBreathing?.SetValueWithoutNotify(s.breathingAnimation);

                if (_settingsLanguage != null)
                    _settingsLanguage.SetValueWithoutNotify(s.language == "en" ? "English" : "Русский");

                if (_settingsStoragePath != null)
                    _settingsStoragePath.text = Application.persistentDataPath;

                if (_settingsVersion != null)
                    _settingsVersion.text = string.IsNullOrEmpty(Application.version) ? "0.1.0" : Application.version;

                SetAvatarShape(s.avatarShape ?? "round", save: false);
                ApplyHaloVisibility(s.showHalo);
                ApplyAvatarArt(_activeAvatarId);
                SyncGallerySelection(_activeAvatarId);
                ApplyBreathingAnimation(s.breathingAnimation);
            }
            catch (Exception ex)
            {
                NeonLogger.LogError(ex.ToString());
            }
        }

        private void SaveSettings()
        {
            _ = SaveSettingsAsync();
        }

        private async Task SaveSettingsAsync()
        {
            try
            {
                var app = await GetAppAsync();
                if (app == null) return;

                var s = app.Settings.Load() ?? new AppSettings();

                if (_settingsHistory != null)      s.saveChatHistory     = _settingsHistory.value;
                if (_settingsStreaming != null)     s.streaming           = _settingsStreaming.value;
                if (_settingsSystemPrompt != null)  s.useSystemPrompt     = _settingsSystemPrompt.value;
                if (_settingsEncryptKeys != null)   s.encryptKeys         = _settingsEncryptKeys.value;
                if (_settingsMaskLogs != null)      s.maskLogs            = _settingsMaskLogs.value;
                if (_settingsShowHalo != null)      s.showHalo            = _settingsShowHalo.value;
                if (_settingsBreathing != null)     s.breathingAnimation  = _settingsBreathing.value;
                if (_settingsLanguage != null)      s.language            = _settingsLanguage.value == "English" ? "en" : "ru";

                s.avatarShape    = _avatarShape;
                s.activeAvatarId = _activeAvatarId;

                app.Settings.Save(s);

                // Propagate runtime flags to services immediately
                if (_chatService != null)
                    _chatService.SaveChatHistory = s.saveChatHistory;
            }
            catch (Exception ex)
            {
                NeonLogger.LogError(ex.ToString());
            }
        }

        private void SetAvatarShape(string shape, bool save = true)
        {
            _avatarShape = shape;

            _shapeRound?.EnableInClassList("seg__btn--active",  shape == "round");
            _shapeSquare?.EnableInClassList("seg__btn--active", shape == "square");
            _shapeHex?.EnableInClassList("seg__btn--active",    shape == "hex");

            if (_avatarCircle != null)
            {
                _avatarCircle.EnableInClassList("avatar--square", shape == "square");
                _avatarCircle.EnableInClassList("avatar--hex",    shape == "hex");
            }

            if (save) SaveSettings();
        }

        private void ApplyHaloVisibility(bool visible)
        {
            var halo = _root?.Q<VisualElement>("avatar-glow");
            SetDisplay(halo, visible ? DisplayStyle.Flex : DisplayStyle.None);
        }

        private void ApplyBreathingAnimation(bool enabled)
        {
            if (enabled) StartBreathing();
            else StopBreathing();
        }

        // ============================================================
        // Avatar gallery
        // ============================================================

        private void RegisterAvatarGalleryCallbacks()
        {
            if (_root == null) return;
            foreach (var id in BuiltInAvatarIds)
            {
                string capturedId = id;
                var tile = _root.Q<VisualElement>($"avtile-{id}");
                if (tile != null)
                    tile.RegisterCallback<ClickEvent>(_ => SelectAvatar(capturedId));
            }
        }

        private void SelectAvatar(string avatarId)
        {
            if (_activeAvatarId == avatarId) return;
            _activeAvatarId = avatarId;
            SyncGallerySelection(avatarId);
            ApplyAvatarArt(avatarId);
            string name = AvatarDisplayName(avatarId);
            if (_subtitleRole != null) _subtitleRole.text = name;
            _chatSubtitle = _chatSubtitle.Contains("·")
                ? _chatSubtitle.Substring(0, _chatSubtitle.LastIndexOf('·') + 2) + name
                : _chatSubtitle;
            if (_topbarSubtitle != null) _topbarSubtitle.text = _chatSubtitle;
            SaveSettings();
        }

        private void OnPreviewApplyClicked()
        {
            _ = ApplyAvatarToSessionAsync();
        }

        private async Task ApplyAvatarToSessionAsync()
        {
            try
            {
                var app = await GetAppAsync();
                if (app == null) return;

                // Update system prompt on the running chat service
                if (_chatService != null)
                {
                    var avatarProfiles = app.Avatars.GetAll();
                    string prompt = app.AvatarService.GetSystemPrompt(_activeAvatarId, avatarProfiles);
                    var s = app.Settings.Load() ?? new AppSettings();
                    _chatService.SystemPrompt = s.useSystemPrompt ? prompt : null;
                }

                SaveSettings();
                ShowChat();
            }
            catch (Exception ex)
            {
                NeonLogger.LogError(ex.ToString());
            }
        }

        private void OnPreviewEditPersonaClicked()
        {
            // Placeholder — persona editing UI not implemented yet
            AddSystemMessage("Редактирование персоны будет добавлено в следующем обновлении.");
        }

        private void SyncGallerySelection(string avatarId)
        {
            if (_root == null) return;
            foreach (var id in BuiltInAvatarIds)
            {
                var tile = _root.Q<VisualElement>($"avtile-{id}");
                tile?.EnableInClassList("avtile--selected", id == avatarId);
            }
        }

        private void ApplyAvatarArt(string avatarId)
        {
            if (_avatarArt != null)
            {
                foreach (var id in BuiltInAvatarIds)
                    _avatarArt.EnableInClassList($"avatar__art--{id}", id == avatarId);
            }

            if (_previewHero != null)
            {
                foreach (var id in BuiltInAvatarIds)
                    _previewHero.EnableInClassList($"preview-hero--{id}", id == avatarId);
            }

            string name = AvatarDisplayName(avatarId);
            if (_previewTitle != null)
                _previewTitle.text = name;
            if (_previewTag != null)
                _previewTag.text = AvatarStyleTag(avatarId);
            if (_previewPersona != null)
                _previewPersona.text = AvatarPersonaText(avatarId);
        }

        private static string AvatarStyleTag(string avatarId)
        {
            switch (avatarId)
            {
                case "aurora": return "cool · gradient";
                case "ember":  return "warm · vivid";
                case "glass":  return "minimal · dark";
                case "flora":  return "natural · green";
                case "mono":   return "monochrome";
                case "cobalt": return "bold · blue";
                case "rose":   return "soft · pink";
                default:       return "default";
            }
        }

        private static string AvatarPersonaText(string avatarId)
        {
            switch (avatarId)
            {
                case "aurora": return "Aurora — calm and analytical. Explains clearly and thinks before responding.";
                case "ember":  return "Ember — warm and empathetic. Understands feelings and responds with care.";
                case "glass":  return "Glass — energetic and bold. Always ready to take on any challenge.";
                case "flora":  return "Flora — wise and thoughtful. Gives nuanced, balanced perspectives.";
                case "mono":   return "Mono — precise and efficient. Values accuracy and brevity above all.";
                case "cobalt": return "Cobalt — creative and imaginative. Loves exploring ideas and connections.";
                case "rose":   return "Rose — charming and sociable. Makes every interaction feel personal.";
                default:       return "Neon — helpful and witty. Direct, clever, and a bit playful.";
            }
        }

        private static string AvatarDisplayName(string avatarId)
        {
            switch (avatarId)
            {
                case "aurora": return "Aurora";
                case "ember":  return "Ember";
                case "glass":  return "Glass";
                case "flora":  return "Flora";
                case "mono":   return "Mono";
                case "cobalt": return "Cobalt";
                case "rose":   return "Rose";
                default:       return "Neon";
            }
        }

        // ============================================================
        // Typing dots animation
        // ============================================================

        private void StartTypingAnimation()
        {
            if (_typingDot1 == null) return;
            _typingFrame = 0;
            _typingSchedule?.Pause();
            _typingSchedule = _typingDot1.schedule.Execute(TickTyping).Every(380);
        }

        private void StopTypingAnimation()
        {
            _typingSchedule?.Pause();
            SetDotOpacity(_typingDot1, 1f);
            SetDotOpacity(_typingDot2, 1f);
            SetDotOpacity(_typingDot3, 1f);
        }

        private void TickTyping()
        {
            int step = _typingFrame % 3;
            SetDotOpacity(_typingDot1, step == 0 ? 1f : 0.25f);
            SetDotOpacity(_typingDot2, step == 1 ? 1f : 0.25f);
            SetDotOpacity(_typingDot3, step == 2 ? 1f : 0.25f);
            _typingFrame++;
        }

        private static void SetDotOpacity(VisualElement dot, float opacity)
        {
            if (dot != null) dot.style.opacity = opacity;
        }

        // ============================================================
        // Breathing animation
        // ============================================================

        private void StartBreathing()
        {
            if (_avatarCircle == null) return;
            _breathStartMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _breathSchedule?.Pause();
            _breathSchedule = _avatarCircle.schedule.Execute(TickBreath).Every(33); // ~30 fps
        }

        private void StopBreathing()
        {
            _breathSchedule?.Pause();
            _breathSchedule = null;
            if (_avatarCircle != null)
                _avatarCircle.style.scale = new StyleScale(new Scale(new Vector3(1f, 1f, 1f)));
        }

        private void TickBreath()
        {
            if (_avatarCircle == null) return;
            float elapsed = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _breathStartMs) / 1000f;
            // Slow sine wave: period ~5 s, amplitude ±1.5%
            float s = 1f + 0.015f * (float)Math.Sin(elapsed * (2f * (float)Math.PI / 5f));
            _avatarCircle.style.scale = new StyleScale(new Scale(new Vector3(s, s, 1f)));
        }

        // ============================================================
        // Settings action buttons
        // ============================================================

        private void OnOpenFolderClicked()
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            System.Diagnostics.Process.Start("explorer.exe", Application.persistentDataPath.Replace('/', '\\'));
#elif UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            System.Diagnostics.Process.Start("open", Application.persistentDataPath);
#elif UNITY_EDITOR_LINUX || UNITY_STANDALONE_LINUX
            System.Diagnostics.Process.Start("xdg-open", Application.persistentDataPath);
#else
            Application.OpenURL("file://" + Application.persistentDataPath);
#endif
        }

        private void OnExportChatsClicked()
        {
            _ = ExportChatsAsync();
        }

        private async Task ExportChatsAsync()
        {
            try
            {
                var chat = await GetChatServiceAsync();
                if (chat == null) return;

                var sessions = await chat.GetAllSessionsAsync();
                string json = JsonUtility.ToJson(new ChatSessionCollection { items = sessions }, true);
                string path = System.IO.Path.Combine(Application.persistentDataPath, $"export_{DateTime.Now:yyyyMMdd_HHmmss}.json");
                System.IO.File.WriteAllText(path, json);

                if (_subtitleBody != null)
                    _subtitleBody.text = $"Экспортировано: {System.IO.Path.GetFileName(path)}";
            }
            catch (Exception ex)
            {
                NeonLogger.LogError(ex.ToString());
            }
        }

        private void OnClearDataClicked()
        {
            _ = ClearAllDataAsync();
        }

        private async Task ClearAllDataAsync()
        {
            try
            {
                var app = await GetAppAsync();
                if (app == null) return;

                // Clear all chat sessions
                app.Chats.SaveAll(new System.Collections.Generic.List<ChatSession>());

                // Delete all providers
                var providers = await app.ProviderManager.GetAllProvidersAsync();
                foreach (var p in providers)
                    await app.ProviderManager.DeleteProviderAsync(p.id);

                // Reset settings
                app.Settings.Save(new AppSettings());

                // Reset service cache so next call reinitialises
                _app = null;
                _chatService = null;

                RenderMessages(null);
                SetNoProviderState();
                if (_sessionsList != null) _sessionsList.Clear();
                if (_navChatCount != null) _navChatCount.text = "0";
                if (_navProvidersCount != null) _navProvidersCount.text = "0";
            }
            catch (Exception ex)
            {
                NeonLogger.LogError(ex.ToString());
            }
        }

        // ============================================================
        // Chat action buttons: Copy, Regenerate
        // ============================================================

        private void OnCopyLastMessageClicked()
        {
            var messages = _chatService?.CurrentChatViewModel?.Messages;
            if (messages == null || messages.Count == 0) return;

            for (int i = messages.Count - 1; i >= 0; i--)
            {
                var msg = messages[i];
                if (msg?.role == "assistant" && !string.IsNullOrWhiteSpace(msg.content))
                {
                    GUIUtility.systemCopyBuffer = msg.content;
                    AddSystemMessage("Скопировано в буфер обмена.");
                    return;
                }
            }
        }

        private void OnRegenerateClicked()
        {
            _ = RegenerateLastAsync();
        }

        private async Task RegenerateLastAsync()
        {
            try
            {
                var chat = await GetChatServiceAsync();
                if (chat == null || _isSending) return;

                var messages = chat.CurrentChatViewModel?.Messages;
                if (messages == null || messages.Count == 0) return;

                // Remove the last assistant reply so it gets regenerated
                if (messages[messages.Count - 1].role == "assistant")
                    messages.RemoveAt(messages.Count - 1);

                if (messages.Count == 0) return;

                SetSending(true);
                try
                {
                    bool streaming = _settingsStreaming?.value ?? false;
                    chat.UseStreaming = streaming;

                    RenderMessages(chat.CurrentChatViewModel?.Messages);

                    if (streaming)
                    {
                        AddStreamingBubble();
                        await chat.RegenerateAsync(OnStreamToken);
                        _streamingLabel = null;
                    }
                    else
                    {
                        await chat.RegenerateAsync();
                    }

                    RenderMessages(chat.CurrentChatViewModel?.Messages);
                    await LoadSessionsAsync(chat);
                }
                finally
                {
                    SetSending(false);
                }
            }
            catch (Exception ex)
            {
                AddSystemMessage($"[Error] {ex.Message}");
                NeonLogger.LogError(ex.ToString());
            }
        }

        // ============================================================
        // Provider import
        // ============================================================

        private void OnImportProviderClicked()
        {
            _ = ImportProvidersAsync();
        }

        private async Task ImportProvidersAsync()
        {
            try
            {
                var app = await GetAppAsync();
                if (app == null) return;

                var filePicker = app.Services.GetRequired<IFilePickerService>();
                string path = await filePicker.PickFileAsync("json");
                if (string.IsNullOrEmpty(path)) return;

                string json = System.IO.File.ReadAllText(path);
                var imported = JsonUtility.FromJson<ProviderConfigCollection>(json);
                if (imported?.items == null || imported.items.Count == 0)
                {
                    AddSystemMessage("Файл не содержит провайдеров.");
                    return;
                }

                foreach (var p in imported.items)
                {
                    if (!string.IsNullOrEmpty(p?.id))
                        await app.ProviderManager.SaveProviderAsync(p);
                }

                await RefreshProvidersListAsync();
                AddSystemMessage($"Импортировано: {imported.items.Count} провайдер(ов).");
            }
            catch (Exception ex)
            {
                AddSystemMessage($"[Ошибка импорта] {ex.Message}");
                NeonLogger.LogError(ex.ToString());
            }
        }

        // ============================================================
        // Avatar upload
        // ============================================================

        private void OnAvatarUploadClicked()
        {
            _ = UploadAvatarAsync();
        }

        private async Task UploadAvatarAsync()
        {
            try
            {
                var app = await GetAppAsync();
                if (app == null) return;

                var filePicker = app.Services.GetRequired<IFilePickerService>();
                string path = await filePicker.PickFileAsync("png");
                if (string.IsNullOrEmpty(path)) return;

                string fileName = System.IO.Path.GetFileNameWithoutExtension(path);
                string destDir  = System.IO.Path.Combine(Application.persistentDataPath, "Avatars");
                System.IO.Directory.CreateDirectory(destDir);
                string destPath = System.IO.Path.Combine(destDir, System.IO.Path.GetFileName(path));
                System.IO.File.Copy(path, destPath, overwrite: true);

                var profile = new NeonCompanion.Runtime.Data.Models.AvatarProfile
                {
                    id        = System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant().Replace(" ", "_"),
                    name      = fileName,
                    imagePath = destPath,
                    isBuiltIn = false
                };

                var all = app.Avatars.GetAll();
                int existing = all.FindIndex(a => a.id == profile.id);
                if (existing >= 0) all[existing] = profile;
                else all.Add(profile);
                app.Avatars.SaveAll(all);

                AddSystemMessage($"Аватар «{fileName}» загружен.");
            }
            catch (Exception ex)
            {
                AddSystemMessage($"[Ошибка загрузки] {ex.Message}");
                NeonLogger.LogError(ex.ToString());
            }
        }

        private void OnAvatarOpenFolderClicked()
        {
            string dir = System.IO.Path.Combine(Application.persistentDataPath, "Avatars");
            System.IO.Directory.CreateDirectory(dir);
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            System.Diagnostics.Process.Start("explorer.exe", dir.Replace('/', '\\'));
#elif UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            System.Diagnostics.Process.Start("open", dir);
#else
            Application.OpenURL("file://" + dir);
#endif
        }
    }
}
