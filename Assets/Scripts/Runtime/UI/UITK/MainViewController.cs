using System;
using System.Collections.Generic;
using System.Linq;
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
        private const string ActiveAvatarFilterClass = "filterchip--active";

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

        private VisualElement _chatPanel;
        private VisualElement _providersPanel;
        private VisualElement _avatarsPanel;
        private VisualElement _themesPanel;
        private VisualElement _placeholderArea;
        private VisualElement _settingsPanel;
        private VisualElement _composer;
        private VisualElement _resizeHandle;
        private VisualElement _avatarPanel;
        private bool _isResizing;
        private float _resizeStartX;
        private float _resizeStartWidth;
        private const float MinAvatarWidth = 180f;
        private const float MaxAvatarWidth = 520f;
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
        private Label _brandVersion;
        private Button _shapeRound;
        private Button _shapeSquare;
        private Button _shapeHex;
        private Toggle _settingsShowHalo;
        private Toggle _settingsBreathing;
        private string _avatarShape = "round";

        // ===== Avatar gallery =====
        private static readonly string[] BuiltInAvatarIds =
            { "neon", "aurora", "ember", "glass", "flora", "mono", "cobalt", "rose" };
        private static readonly Dictionary<string, BuiltInAvatarMeta> BuiltInAvatarMetaById = new Dictionary<string, BuiltInAvatarMeta>
        {
            ["neon"] = new BuiltInAvatarMeta("Неон", "стандартный", "Неон — спокойный и практичный AI-компаньон разработчика. Отвечает кратко, структурно и по делу.", AvatarFilter.Standard),
            ["aurora"] = new BuiltInAvatarMeta("Аврора", "прохладный · градиент", "Аврора — спокойная и аналитичная. Объясняет ясно и сначала обдумывает ответ.", AvatarFilter.Gradient),
            ["ember"] = new BuiltInAvatarMeta("Эмбер", "тёплый · градиент", "Эмбер — тёплая и эмпатичная. Улавливает настроение и отвечает бережно.", AvatarFilter.Gradient),
            ["glass"] = new BuiltInAvatarMeta("Гласс", "минимал · тёмный", "Гласс — энергичная и смелая. Любит сложные задачи и быстрый темп.", AvatarFilter.Minimal),
            ["flora"] = new BuiltInAvatarMeta("Флора", "природный · градиент", "Флора — вдумчивая и спокойная. Даёт нюансированные и сбалансированные ответы.", AvatarFilter.Gradient),
            ["mono"] = new BuiltInAvatarMeta("Моно", "минимал · монохром", "Моно — точная и эффективная. Ценит корректность и краткость.", AvatarFilter.Minimal),
            ["cobalt"] = new BuiltInAvatarMeta("Кобальт", "смелый · градиент", "Кобальт — креативная и изобретательная. Любит исследовать идеи и связи.", AvatarFilter.Gradient),
            ["rose"] = new BuiltInAvatarMeta("Роуз", "мягкий · градиент", "Роуз — обаятельная и общительная. Делает диалог более личным.", AvatarFilter.Gradient)
        };
        private string _activeAvatarId = "neon";
        private AvatarFilter _activeAvatarFilter = AvatarFilter.All;
        private VisualElement _avatarArt;
        private VisualElement _avatarCircle;
        private VisualElement _previewHero;
        private Label _previewTitle;
        private Label _previewTag;
        private Label _previewPersona;
        private Label _streamingLabel;
        private Button _previewApplyBtn;
        private Button _previewEditPersonaBtn;
        private Button _previewDeleteAvatarBtn;
        private VisualElement _galleryContainer;
        private Label _navAvatarsCount;
        private Label _previewPersonaLabel;
        private VisualElement _previewActionsRow;
        private VisualElement _personaEditorPanel;
        private TextField _personaEditField;
        private Button _personaSaveBtn;
        private Button _personaCancelBtn;
        private readonly Dictionary<string, VisualElement> _customAvatarTiles = new Dictionary<string, VisualElement>();
        private readonly Dictionary<string, Texture2D> _customTextures = new Dictionary<string, Texture2D>();
        private List<AvatarProfile> _cachedCustomProfiles = new List<AvatarProfile>();
        private readonly Dictionary<string, AvatarProfile> _cachedProfilesById = new Dictionary<string, AvatarProfile>();
        private Button _avatarFilterAllBtn;
        private Button _avatarFilterStandardBtn;
        private Button _avatarFilterGradientBtn;
        private Button _avatarFilterMinimalBtn;
        private Button _avatarFilterCustomBtn;
        private Label _avatarFilterAllCount;
        private Label _avatarFilterStandardCount;
        private Label _avatarFilterGradientCount;
        private Label _avatarFilterMinimalCount;
        private Label _avatarFilterCustomCount;

        // ===== Typing animation =====
        private VisualElement _typingDot1;
        private VisualElement _typingDot2;
        private VisualElement _typingDot3;
        private IVisualElementScheduledItem _typingSchedule;
        private int _typingFrame;

        // ===== Breathing animation =====
        private IVisualElementScheduledItem _breathSchedule;
        private long _breathStartMs;
        private IVisualElementScheduledItem _clearDataConfirmResetSchedule;

        // ===== Themes preview =====
        private VisualElement _themesPreviewHalo;
        private VisualElement _themesPreviewAvatar;
        private long _themesBreathStartMs;
        private IVisualElementScheduledItem _themesBreathSchedule;

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
        private Label _settingsClearBtnText;
        private Button _settingsGithubBtn;
        private Button _settingsDocsBtn;
        private Button _settingsDonateBtn;
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
        private ProviderConfig _editingProviderSource;
        private bool _cancelPending;
        private bool _clearDataConfirmPending;
        private long _clearDataConfirmExpiresAtMs;
        private string _chatSubtitle = "0 сообщений";
        private string _currentSessionId = string.Empty;
        private string _currentSessionTitle = string.Empty;
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
            _clearDataConfirmResetSchedule?.Pause();
            _clearDataConfirmResetSchedule = null;
            _themesBreathSchedule?.Pause();
            _themesBreathSchedule = null;
            foreach (var tex in _customTextures.Values)
                if (tex != null) UnityEngine.Object.Destroy(tex);
            _customTextures.Clear();
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

            _chatPanel = root.Q<VisualElement>("chat-panel");
            _providersPanel = root.Q<VisualElement>("providers-panel");
            _avatarsPanel = root.Q<VisualElement>("avatars-panel");
            _themesPanel = root.Q<VisualElement>("themes-panel");
            _placeholderArea = root.Q<VisualElement>("placeholder-area");
            _settingsPanel = root.Q<VisualElement>("settings-panel");
            _composer = root.Q<VisualElement>("composer");
            _resizeHandle = root.Q<VisualElement>("resize-handle");
            _avatarPanel  = root.Q<VisualElement>("avatar-panel");
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
            _galleryContainer     = _avatarsPanel?.Q<VisualElement>(className: "gallery");
            _navAvatarsCount      = _navAvatars?.Q<Label>(className: "nav__count");
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
            _settingsClearBtnText  = _settingsClearBtn?.Q<Label>();
            _settingsGithubBtn     = root.Q<Button>("settings-github-btn");
            _settingsDocsBtn       = root.Q<Button>("settings-docs-btn");
            _settingsDonateBtn     = root.Q<Button>("settings-donate-btn");
            _settingsDonateBtn?.SetEnabled(false);
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
            _brandVersion       = root.Q<Label>("brand-version");
            _shapeRound  = root.Q<Button>("shape-round");
            _shapeSquare = root.Q<Button>("shape-square");
            _shapeHex    = root.Q<Button>("shape-hex");
            _settingsShowHalo   = root.Q<Toggle>("settings-show-halo");
            _settingsBreathing  = root.Q<Toggle>("settings-breathing");

            // Avatar elements
            _avatarArt    = root.Q<VisualElement>("avatar-art");
            _avatarCircle = root.Q<VisualElement>("avatar-circle");
            _previewHero  = root.Q<VisualElement>("preview-hero");
            _themesPreviewHalo   = root.Q<VisualElement>("themes-preview-halo");
            _themesPreviewAvatar = root.Q<VisualElement>("themes-preview-avatar");
            _previewTitle   = root.Q<Label>("preview-title");
            _previewTag     = root.Q<Label>("preview-tag");
            _previewPersona = root.Q<Label>("preview-persona");
            _previewApplyBtn      = root.Q<Button>("preview-apply-btn");
            _previewEditPersonaBtn = root.Q<Button>("preview-edit-persona-btn");
            _previewDeleteAvatarBtn = root.Q<Button>("preview-delete-avatar-btn");
            _previewPersonaLabel  = root.Q<Label>("preview-persona-label");
            _previewActionsRow    = root.Q<VisualElement>("preview-actions-row");
            _personaEditorPanel   = root.Q<VisualElement>("persona-editor-panel");
            _personaEditField     = root.Q<TextField>("persona-edit-field");
            _personaSaveBtn       = root.Q<Button>("persona-save-btn");
            _personaCancelBtn     = root.Q<Button>("persona-cancel-btn");
            _avatarFilterAllBtn = root.Q<Button>("avatar-filter-all-btn");
            _avatarFilterStandardBtn = root.Q<Button>("avatar-filter-standard-btn");
            _avatarFilterGradientBtn = root.Q<Button>("avatar-filter-gradient-btn");
            _avatarFilterMinimalBtn = root.Q<Button>("avatar-filter-minimal-btn");
            _avatarFilterCustomBtn = root.Q<Button>("avatar-filter-custom-btn");
            _avatarFilterAllCount = root.Q<Label>("avatar-filter-all-count");
            _avatarFilterStandardCount = root.Q<Label>("avatar-filter-standard-count");
            _avatarFilterGradientCount = root.Q<Label>("avatar-filter-gradient-count");
            _avatarFilterMinimalCount = root.Q<Label>("avatar-filter-minimal-count");
            _avatarFilterCustomCount = root.Q<Label>("avatar-filter-custom-count");
            SetDisplay(_personaEditorPanel, DisplayStyle.None);
            SetDisplay(_previewDeleteAvatarBtn, DisplayStyle.None);

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
            UpdateClearDataButtonState();
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
            RegisterClick(_settingsGithubBtn, OnSettingsGitHubClicked);
            RegisterClick(_settingsDocsBtn, OnSettingsDocsClicked);
            RegisterClick(_settingsDonateBtn, OnSettingsDonateClicked);
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
            {
                _messageInput.RegisterCallback<KeyDownEvent>(OnInputKeyDown);
                _messageInput.RegisterCallback<FocusEvent>(_ => _composer?.AddToClassList("composer--focused"));
                _messageInput.RegisterCallback<BlurEvent>(_ => _composer?.RemoveFromClassList("composer--focused"));
            }

            if (_resizeHandle != null)
            {
                _resizeHandle.RegisterCallback<PointerDownEvent>(OnResizePointerDown);
                _resizeHandle.RegisterCallback<PointerMoveEvent>(OnResizePointerMove);
                _resizeHandle.RegisterCallback<PointerUpEvent>(OnResizePointerUp);
            }

            RegisterSettingsCallbacks();
            RegisterAvatarGalleryCallbacks();
            RegisterClick(_previewApplyBtn, OnPreviewApplyClicked);
            RegisterClick(_previewEditPersonaBtn, OnPreviewEditPersonaClicked);
            RegisterClick(_previewDeleteAvatarBtn, OnPreviewDeleteAvatarClicked);
            RegisterClick(_personaSaveBtn, OnPersonaSaveClicked);
            RegisterClick(_personaCancelBtn, OnPersonaCancelClicked);
            RegisterClick(_avatarFilterAllBtn, OnAvatarFilterAllClicked);
            RegisterClick(_avatarFilterStandardBtn, OnAvatarFilterStandardClicked);
            RegisterClick(_avatarFilterGradientBtn, OnAvatarFilterGradientClicked);
            RegisterClick(_avatarFilterMinimalBtn, OnAvatarFilterMinimalClicked);
            RegisterClick(_avatarFilterCustomBtn, OnAvatarFilterCustomClicked);
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
            UnregisterClick(_settingsOpenFolderBtn, OnOpenFolderClicked);
            UnregisterClick(_settingsExportBtn, OnExportChatsClicked);
            UnregisterClick(_settingsClearBtn, OnClearDataClicked);
            UnregisterClick(_settingsGithubBtn, OnSettingsGitHubClicked);
            UnregisterClick(_settingsDocsBtn, OnSettingsDocsClicked);
            UnregisterClick(_settingsDonateBtn, OnSettingsDonateClicked);
            UnregisterClick(_testProviderBtn, OnTestProviderClicked);
            UnregisterClick(_listenButton, OnListenClicked);
            UnregisterClick(_attachButton, OnAttachClicked);
            UnregisterClick(_previewDeleteAvatarBtn, OnPreviewDeleteAvatarClicked);
            UnregisterClick(_personaSaveBtn, OnPersonaSaveClicked);
            UnregisterClick(_personaCancelBtn, OnPersonaCancelClicked);
            UnregisterClick(_avatarFilterAllBtn, OnAvatarFilterAllClicked);
            UnregisterClick(_avatarFilterStandardBtn, OnAvatarFilterStandardClicked);
            UnregisterClick(_avatarFilterGradientBtn, OnAvatarFilterGradientClicked);
            UnregisterClick(_avatarFilterMinimalBtn, OnAvatarFilterMinimalClicked);
            UnregisterClick(_avatarFilterCustomBtn, OnAvatarFilterCustomClicked);

            if (_messageInput != null)
                _messageInput.UnregisterCallback<KeyDownEvent>(OnInputKeyDown);

            if (_resizeHandle != null)
            {
                _resizeHandle.UnregisterCallback<PointerDownEvent>(OnResizePointerDown);
                _resizeHandle.UnregisterCallback<PointerMoveEvent>(OnResizePointerMove);
                _resizeHandle.UnregisterCallback<PointerUpEvent>(OnResizePointerUp);
            }

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
            if (!CanLeaveProviderEditor())
                return;

            SetActiveNav(_navChat);
            SetTopbar(GetChatTitle(), _chatSubtitle);
            ShowArea(_chatPanel);
        }

        private void ShowAvatars()
        {
            if (!CanLeaveProviderEditor())
                return;

            SetActiveNav(_navAvatars);
            int total = BuiltInAvatarIds.Length + (_cachedCustomProfiles?.Count ?? 0);
            SetTopbar("Аватары", $"{total} образов · {AvatarDisplayName(_activeAvatarId)}");
            ShowArea(_avatarsPanel);
        }

        private void ShowProviders()
        {
            if (!CanLeaveProviderEditor())
                return;

            SetActiveNav(_navProviders);
            SetTopbar("Провайдеры", "OpenAI-совместимые провайдеры");
            ShowArea(_providersPanel);
            _ = RefreshProvidersListAsync();
        }

        private void ShowHistory()
        {
            if (!CanLeaveProviderEditor())
                return;

            SetActiveNav(_navHistory);
            SetTopbar(GetChatTitle(), _chatSubtitle);
            ShowArea(_chatPanel);
        }

        private string GetChatTitle()
        {
            return !string.IsNullOrWhiteSpace(_currentSessionTitle) ? _currentSessionTitle : "Новый чат";
        }

        private string GetProviderStatusText()
        {
            var provider = _chatService?.CurrentProvider;
            if (provider == null) return "нет провайдера";
            if (!string.IsNullOrWhiteSpace(provider.defaultModel))
                return $"{provider.displayName ?? "API"} · {provider.defaultModel}";
            return provider.displayName ?? "настроен";
        }

        private void ShowThemes()
        {
            if (!CanLeaveProviderEditor())
                return;

            SetActiveNav(_navThemes);
            SetTopbar("Темы", "Форма, ореол и дыхание для аватара");
            ShowArea(_themesPanel);
        }

        private void ShowSettings()
        {
            if (!CanLeaveProviderEditor())
                return;

            SetActiveNav(_navSettings);
            SetTopbar("Настройки", string.Empty);
            ShowArea(_settingsPanel);
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
            SetDisplay(_chatPanel, visible == _chatPanel ? DisplayStyle.Flex : DisplayStyle.None);
            SetDisplay(_providersPanel, visible == _providersPanel ? DisplayStyle.Flex : DisplayStyle.None);
            SetDisplay(_avatarsPanel, visible == _avatarsPanel ? DisplayStyle.Flex : DisplayStyle.None);
            SetDisplay(_themesPanel, visible == _themesPanel ? DisplayStyle.Flex : DisplayStyle.None);
            SetDisplay(_placeholderArea, visible == _placeholderArea ? DisplayStyle.Flex : DisplayStyle.None);
            SetDisplay(_settingsPanel, visible == _settingsPanel ? DisplayStyle.Flex : DisplayStyle.None);
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
                    AddSystemMessage("Приложение не инициализировано.");
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
                AddSystemMessage("Не удалось отправить сообщение. Попробуй ещё раз.");
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
                _connectionStatus.text = isSending ? "генерация…" : GetProviderStatusText();

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
                AddSystemMessage("Не удалось получить сводку диалога. Попробуй позже.");
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
                AddSystemMessage("Не удалось выполнить поиск по чатам. Попробуй ещё раз.");
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
                    AddSystemMessage("Последний ответ скопирован в буфер обмена.");
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
                AddSystemMessage("Не удалось добавить вложение к сообщению.");
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
                _currentSessionId = chat.CurrentSessionId ?? string.Empty;
                _currentSessionTitle = string.Empty;
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
                _connectionStatus.text = "нет провайдера";
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
            if (string.IsNullOrEmpty(_currentSessionId))
                _currentSessionId = _chatService.CurrentSessionId ?? string.Empty;
            return _chatService;
        }

        private async Task LoadSessionsAsync(ChatService chat)
        {
            if (_sessionsList == null)
                return;

            var allSessions = await chat.GetAllSessionsAsync();
            if (!_isBound)
                return;

            // Sync session identity from service if not yet captured (covers refresh/init)
            if (string.IsNullOrEmpty(_currentSessionId))
                _currentSessionId = chat.CurrentSessionId ?? string.Empty;

            // Sync display title from the actual current session (covers reload/refresh case)
            if (!string.IsNullOrEmpty(_currentSessionId))
            {
                var active = allSessions.Find(s => s.sessionId == _currentSessionId);
                if (active != null)
                {
                    _currentSessionTitle = string.IsNullOrWhiteSpace(active.title) || active.title == "New chat"
                        ? string.Empty
                        : active.title;
                }
                else
                {
                    _currentSessionId = string.Empty;
                    _currentSessionTitle = string.Empty;
                }
            }

            if (_navChatCount != null)
                _navChatCount.text = allSessions.Count.ToString();

            if (_topbarTitle != null)
                _topbarTitle.text = GetChatTitle();

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
                bool isActive = IsActiveSession(sessions[i], i);
                var item = CreateSessionItem(sessions[i], isActive);
                _sessionsList.Add(item);
                _sessionItems.Add(item);
            }
        }

        private bool IsActiveSession(ChatSession session, int index)
        {
            if (!string.IsNullOrEmpty(_currentSessionId))
                return session.sessionId == _currentSessionId;
            return index == 0;
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

            var titleLabel = new Label(string.IsNullOrWhiteSpace(session.title) || session.title == "New chat"
                ? "Новый чат"
                : session.title);
            titleLabel.AddToClassList("history__title");

            int count = session.messages?.Count ?? 0;
            var metaLabel = new Label(MessageCountText(count));
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
                _currentSessionId = session.sessionId;
                _currentSessionTitle = string.IsNullOrWhiteSpace(session.title) || session.title == "New chat"
                    ? string.Empty
                    : session.title;
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
            if (!CanLeaveProviderEditor())
                return;

            _cancelPending = false;
            _editingProviderSource = null;
            _editingProvider = ProviderConfig.CreateDefault("Новый провайдер", "https://api.openai.com/v1");
            ShowProviderEditPanel();
        }

        private void StartEditingProvider(ProviderConfig provider)
        {
            if (provider == null)
                return;

            if (!CanSwitchEditingProvider(provider))
                return;

            _cancelPending = false;
            _editingProviderSource = provider;
            _editingProvider = CloneProvider(provider);
            ShowProviderEditPanel();
        }

        private void ShowProviderEditPanel()
        {
            if (_providerEditPanel == null || _editingProvider == null)
                return;

            if (_editorProviderShort != null)
                _editorProviderShort.text = BuildProviderShort(_editingProvider);
            if (_editorProviderName != null)
                _editorProviderName.text = string.IsNullOrWhiteSpace(_editingProvider.displayName) ? "—" : _editingProvider.displayName;
            if (_editName != null)
                _editName.SetValueWithoutNotify(_editingProvider.displayName ?? string.Empty);
            if (_editBaseUrl != null)
                _editBaseUrl.SetValueWithoutNotify(_editingProvider.baseUrl ?? string.Empty);
            if (_editApiKey != null)
                _editApiKey.SetValueWithoutNotify(_editingProvider.apiKey ?? string.Empty);
            if (_editModel != null)
                _editModel.SetValueWithoutNotify(_editingProvider.defaultModel ?? string.Empty);
            if (_editTemperature != null)
                _editTemperature.SetValueWithoutNotify(_editingProvider.temperature);
            if (_editMaxTokens != null)
                _editMaxTokens.SetValueWithoutNotify(_editingProvider.maxTokens.ToString());

            SetTestRow(null, string.Empty);
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

                var draft = BuildProviderDraftFromEditor();
                if (draft == null)
                    return;

                await app.ProviderManager.SaveProviderAsync(draft);

                var chat = await GetChatServiceAsync();
                if (chat?.CurrentProvider?.id == draft.id)
                {
                    await chat.ApplyProviderConfigAsync(draft);
                    SetProviderHeader(chat.CurrentProvider);
                }
                else if (_editingProviderSource?.id == draft.id)
                {
                    SetProviderHeader(draft);
                }

                _cancelPending = false;
                _editingProviderSource = draft;
                _editingProvider = CloneProvider(draft);
                ShowProviderEditPanel();
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

                var draft = BuildProviderDraftFromEditor();
                if (draft == null)
                {
                    SetTestRow(false, "Не удалось собрать настройки провайдера.");
                    return;
                }

                var result = await app.AiClient.TestConnectionAsync(draft);
                SetTestRow(result.Success, result.Message);
            }
            catch (Exception ex)
            {
                SetTestRow(false, "Проверка подключения не выполнена. Проверь адрес, модель и параметры доступа.");
                NeonLogger.LogError(ex.ToString());
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
            if (HasUnsavedChanges() && !_cancelPending)
            {
                _cancelPending = true;
                SetTestRow(false, "Изменения не сохранены. Нажми «Отмена» ещё раз, чтобы сбросить.");
                return;
            }

            _cancelPending = false;
            _editingProvider = null;
            _editingProviderSource = null;
            SetDisplay(_providerEditPanel, DisplayStyle.None);
        }

        // ---- Draft editing helpers ----

        private static ProviderConfig CloneProvider(ProviderConfig source)
        {
            if (source == null) return null;
            return new ProviderConfig
            {
                id           = source.id,
                displayName  = source.displayName,
                baseUrl      = source.baseUrl,
                apiKey       = source.apiKey,
                defaultModel = source.defaultModel,
                temperature  = source.temperature,
                maxTokens    = source.maxTokens,
                isEnabled    = source.isEnabled
            };
        }

        private ProviderConfig BuildProviderDraftFromEditor()
        {
            if (_editingProvider == null) return null;

            var draft = CloneProvider(_editingProvider);
            if (_editName != null)        draft.displayName  = _editName.value;
            if (_editBaseUrl != null)     draft.baseUrl      = _editBaseUrl.value;
            if (_editApiKey != null)      draft.apiKey       = _editApiKey.value;
            if (_editModel != null)       draft.defaultModel = _editModel.value;
            if (_editTemperature != null) draft.temperature  = _editTemperature.value;
            if (_editMaxTokens != null && int.TryParse(_editMaxTokens.value, out int tokens))
                draft.maxTokens = tokens;
            return draft;
        }

        private bool HasUnsavedChanges()
        {
            if (_providerEditPanel?.style.display != DisplayStyle.Flex) return false;
            if (_editingProvider == null) return false;

            // New provider not yet saved — treat as dirty so navigation is blocked.
            if (_editingProviderSource == null) return true;

            var draft = BuildProviderDraftFromEditor();
            if (draft == null) return false;

            return draft.displayName  != _editingProviderSource.displayName
                || draft.baseUrl      != _editingProviderSource.baseUrl
                || draft.apiKey       != _editingProviderSource.apiKey
                || draft.defaultModel != _editingProviderSource.defaultModel
                || Math.Abs(draft.temperature - _editingProviderSource.temperature) > 0.001f
                || draft.maxTokens    != _editingProviderSource.maxTokens;
        }

        private bool CanLeaveProviderEditor()
        {
            if (!HasUnsavedChanges()) return true;
            SetTestRow(false, "Есть несохранённые изменения. Сначала сохрани или отмени.");
            return false;
        }

        private bool CanSwitchEditingProvider(ProviderConfig target)
        {
            if (_editingProviderSource?.id == target?.id) return true;
            if (!HasUnsavedChanges()) return true;
            SetTestRow(false, "Есть несохранённые изменения. Сначала сохрани или отмени.");
            return false;
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
                    _cancelPending = false;
                    _editingProvider = null;
                    _editingProviderSource = null;
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
                _currentSessionId = chat.CurrentSessionId ?? string.Empty;
                _currentSessionTitle = string.Empty;
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
                _providersList.Add(new Label("Менеджер провайдеров не готов."));
                return;
            }

            var providers = await app.ProviderManager.GetAllProvidersAsync();
            if (_navProvidersCount != null)
                _navProvidersCount.text = providers.Count.ToString();

            if (providers.Count == 0)
            {
                _providersList.Add(new Label("Провайдеры не настроены."));
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

            var nameLabel = new Label(string.IsNullOrWhiteSpace(provider.displayName) ? "Провайдер" : provider.displayName);
            nameLabel.AddToClassList("provider__name");
            nameRow.Add(nameLabel);

            if (isActive)
            {
                var chip = new Label("активен");
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
            var metaLabel = new Label("Источник");
            metaLabel.AddToClassList("provider__meta-label");
            var metaValue = new Label(BuildProviderLocationText(provider));
            metaValue.AddToClassList("provider__meta-value");
            meta.Add(metaLabel);
            meta.Add(metaValue);

            var actions = new VisualElement();
            actions.AddToClassList("provider__actions");
            var useButton = new Button(() => SwitchProvider(provider)) { text = "Использовать" };
            var editButton = new Button(() => StartEditingProvider(provider)) { text = "Изменить" };
            var deleteButton = new Button(() => DeleteProvider(provider)) { text = "Удалить" };
            useButton.AddToClassList("btn");
            editButton.AddToClassList("btn");
            deleteButton.AddToClassList("btn");
            actions.Add(useButton);
            actions.Add(editButton);
            actions.Add(deleteButton);

            var toggle = new VisualElement();
            toggle.AddToClassList("toggle");
            if (isActive) toggle.AddToClassList("toggle--on");
            var knob = new VisualElement();
            knob.AddToClassList("toggle__knob");
            toggle.Add(knob);
            toggle.RegisterCallback<ClickEvent>(evt =>
            {
                evt.StopPropagation();
                SwitchProvider(provider);
            });

            container.Add(logo);
            container.Add(body);
            container.Add(modelLabel);
            container.Add(meta);
            container.Add(actions);
            container.Add(toggle);

            return container;
        }

        private void SetProviderHeader(ProviderConfig provider)
        {
            if (provider == null)
                return;

            string shortName = BuildProviderShort(provider);
            string displayName = string.IsNullOrWhiteSpace(provider.displayName) ? "—" : provider.displayName;
            string model = string.IsNullOrWhiteSpace(provider.defaultModel) ? string.Empty : provider.defaultModel;

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

        private static string BuildProviderLocationText(ProviderConfig provider)
        {
            string baseUrl = provider?.baseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
                return "неизвестно";

            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            {
                string host = uri.Host;
                if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase))
                {
                    return "локально";
                }

                return "удалённо";
            }

            if (baseUrl.IndexOf("localhost", StringComparison.OrdinalIgnoreCase) >= 0
                || baseUrl.IndexOf("127.0.0.1", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "локально";
            }

            return "удалённо";
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
                if (_brandVersion != null)
                    _brandVersion.text = string.IsNullOrEmpty(Application.version) ? "0.1.0" : Application.version;

                SetAvatarShape(s.avatarShape ?? "round", save: false);
                ApplyHaloVisibility(s.showHalo);
                RefreshCustomAvatarGallery(app);
                RefreshBuiltInAvatarTileLabels();
                ApplyAvatarFilter();
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

            if (_themesPreviewAvatar != null)
            {
                _themesPreviewAvatar.EnableInClassList("themes-preview__avatar--square", shape == "square");
                _themesPreviewAvatar.EnableInClassList("themes-preview__avatar--hex",    shape == "hex");
            }

            if (save) SaveSettings();
        }

        private void ApplyHaloVisibility(bool visible)
        {
            var halo = _root?.Q<VisualElement>("avatar-glow");
            SetDisplay(halo, visible ? DisplayStyle.Flex : DisplayStyle.None);
            SetDisplay(_themesPreviewHalo, visible ? DisplayStyle.Flex : DisplayStyle.None);
        }

        private void ApplyBreathingAnimation(bool enabled)
        {
            if (enabled) StartBreathing();
            else StopBreathing();
            ApplyThemesPreviewBreathing(enabled);
        }

        private void ApplyThemesPreviewBreathing(bool enabled)
        {
            _themesBreathSchedule?.Pause();
            _themesBreathSchedule = null;

            if (_themesPreviewAvatar == null) return;

            if (!enabled)
            {
                _themesPreviewAvatar.style.scale = new StyleScale(new Scale(new Vector3(1f, 1f, 1f)));
                return;
            }

            _themesBreathStartMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _themesBreathSchedule = _themesPreviewAvatar.schedule.Execute(TickThemesBreath).Every(33);
        }

        private void TickThemesBreath()
        {
            if (_themesPreviewAvatar == null) return;
            float elapsed = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _themesBreathStartMs) / 1000f;
            float s = 1f + 0.015f * (float)Math.Sin(elapsed * (2f * (float)Math.PI / 5f));
            _themesPreviewAvatar.style.scale = new StyleScale(new Scale(new Vector3(s, s, 1f)));
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

        private void OnAvatarFilterAllClicked() => SetAvatarFilter(AvatarFilter.All);
        private void OnAvatarFilterStandardClicked() => SetAvatarFilter(AvatarFilter.Standard);
        private void OnAvatarFilterGradientClicked() => SetAvatarFilter(AvatarFilter.Gradient);
        private void OnAvatarFilterMinimalClicked() => SetAvatarFilter(AvatarFilter.Minimal);
        private void OnAvatarFilterCustomClicked() => SetAvatarFilter(AvatarFilter.Custom);

        private void SetAvatarFilter(AvatarFilter filter)
        {
            _activeAvatarFilter = filter;
            ApplyAvatarFilter();
        }

        private void SelectAvatar(string avatarId)
        {
            if (_activeAvatarId == avatarId) return;
            ClosePersonaEditor();
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
            OpenPersonaEditor();
        }

        private void OpenPersonaEditor()
        {
            string current = AvatarPersonaText(_activeAvatarId);
            if (_personaEditField != null) _personaEditField.value = current;
            SetDisplay(_previewPersonaLabel, DisplayStyle.None);
            SetDisplay(_previewPersona, DisplayStyle.None);
            SetDisplay(_previewActionsRow, DisplayStyle.None);
            SetDisplay(_personaEditorPanel, DisplayStyle.Flex);
        }

        private void ClosePersonaEditor()
        {
            SetDisplay(_personaEditorPanel, DisplayStyle.None);
            SetDisplay(_previewPersonaLabel, DisplayStyle.Flex);
            SetDisplay(_previewPersona, DisplayStyle.Flex);
            SetDisplay(_previewActionsRow, DisplayStyle.Flex);
            UpdateAvatarActionButtons(_activeAvatarId);
        }

        private void OnPersonaCancelClicked() => ClosePersonaEditor();

        private void OnPersonaSaveClicked() => _ = SavePersonaAsync();

        private void OnPreviewDeleteAvatarClicked() => _ = DeleteSelectedAvatarAsync();

        private async Task SavePersonaAsync()
        {
            try
            {
                var app = await GetAppAsync();
                if (app == null) return;

                string newPrompt = _personaEditField?.value?.Trim() ?? string.Empty;
                var profiles = app.Avatars.GetAll();
                var profile = profiles.FirstOrDefault(a => a.id == _activeAvatarId);
                if (profile == null)
                {
                    bool isBuiltIn = Array.IndexOf(BuiltInAvatarIds, _activeAvatarId) >= 0;
                    if (!isBuiltIn) return;

                    profile = new AvatarProfile
                    {
                        id = _activeAvatarId,
                        isBuiltIn = true,
                        name = string.Empty,
                        imagePath = string.Empty,
                        systemPrompt = string.Empty
                    };
                    profiles.Add(profile);
                }

                profile.systemPrompt = newPrompt;
                app.Avatars.SaveAll(profiles);
                UpdateAvatarProfileCaches(profiles);

                if (_previewPersona != null)
                    _previewPersona.text = AvatarPersonaText(_activeAvatarId);

                if (_chatService != null)
                {
                    var s = app.Settings.Load() ?? new AppSettings();
                    string prompt = app.AvatarService.GetSystemPrompt(_activeAvatarId, profiles);
                    _chatService.SystemPrompt = s.useSystemPrompt ? prompt : null;
                }

                ClosePersonaEditor();
            }
            catch (Exception ex)
            {
                NeonLogger.LogError(ex.ToString());
            }
        }

        private async Task DeleteSelectedAvatarAsync()
        {
            if (Array.IndexOf(BuiltInAvatarIds, _activeAvatarId) >= 0)
            {
                AddSystemMessage("Встроенные аватары нельзя удалить.");
                return;
            }

            try
            {
                var app = await GetAppAsync();
                if (app == null) return;

                var profiles = app.Avatars.GetAll();
                var profile = profiles.FirstOrDefault(a => a.id == _activeAvatarId && !a.isBuiltIn);
                if (profile == null) return;

                string removedAvatarId = profile.id;
                string removedName = string.IsNullOrWhiteSpace(profile.name) ? removedAvatarId : profile.name;
                string imagePath = profile.imagePath;

                profiles.RemoveAll(a => a.id == removedAvatarId);
                app.Avatars.SaveAll(profiles);

                UpdateAvatarProfileCaches(profiles);
                ReleaseCustomTexture(imagePath);
                DeleteCustomAvatarFileIfUnused(imagePath, profiles);

                ClosePersonaEditor();
                RefreshCustomAvatarGallery(app);

                _activeAvatarId = string.Empty;
                SelectAvatar("neon");

                if (_chatService != null)
                {
                    var s = app.Settings.Load() ?? new AppSettings();
                    string prompt = app.AvatarService.GetSystemPrompt(_activeAvatarId, profiles);
                    _chatService.SystemPrompt = s.useSystemPrompt ? prompt : null;
                }

                AddSystemMessage($"Аватар «{removedName}» удалён.");
            }
            catch (Exception ex)
            {
                AddSystemMessage("Не удалось удалить аватар.");
                NeonLogger.LogError(ex.ToString());
            }
        }

        private void SyncGallerySelection(string avatarId)
        {
            if (_root == null) return;
            foreach (var id in BuiltInAvatarIds)
            {
                var tile = _root.Q<VisualElement>($"avtile-{id}");
                tile?.EnableInClassList("avtile--selected", id == avatarId);
            }
            foreach (var kvp in _customAvatarTiles)
                kvp.Value.EnableInClassList("avtile--selected", kvp.Key == avatarId);
        }

        private void ApplyAvatarArt(string avatarId)
        {
            bool isBuiltIn = Array.IndexOf(BuiltInAvatarIds, avatarId) >= 0;

            if (_avatarArt != null)
            {
                foreach (var id in BuiltInAvatarIds)
                    _avatarArt.EnableInClassList($"avatar__art--{id}", isBuiltIn && id == avatarId);

                if (!isBuiltIn)
                {
                    var tex = GetOrLoadTexture(GetCustomProfile(avatarId)?.imagePath);
                    _avatarArt.style.backgroundImage = tex != null ? new StyleBackground(tex) : StyleKeyword.Null;
                    if (tex != null) _avatarArt.style.unityBackgroundScaleMode = ScaleMode.ScaleAndCrop;
                }
                else
                {
                    _avatarArt.style.backgroundImage = StyleKeyword.Null;
                }
            }

            if (_previewHero != null)
            {
                foreach (var id in BuiltInAvatarIds)
                    _previewHero.EnableInClassList($"preview-hero--{id}", isBuiltIn && id == avatarId);

                if (!isBuiltIn)
                {
                    var tex = GetOrLoadTexture(GetCustomProfile(avatarId)?.imagePath);
                    _previewHero.style.backgroundImage = tex != null ? new StyleBackground(tex) : StyleKeyword.Null;
                    if (tex != null) _previewHero.style.unityBackgroundScaleMode = ScaleMode.ScaleAndCrop;
                    _previewHero.style.backgroundColor = new StyleColor(new Color(0.18f, 0.18f, 0.22f));
                }
                else
                {
                    _previewHero.style.backgroundImage = StyleKeyword.Null;
                    _previewHero.style.backgroundColor = StyleKeyword.Null;
                }
            }

            string name = AvatarDisplayName(avatarId);
            if (_previewTitle != null)
                _previewTitle.text = name;
            if (_previewTag != null)
                _previewTag.text = AvatarStyleTag(avatarId);
            if (_previewPersona != null)
                _previewPersona.text = AvatarPersonaText(avatarId);

            UpdateAvatarActionButtons(avatarId);
        }

        private void UpdateAvatarActionButtons(string avatarId)
        {
            bool isCustom = GetCustomProfile(avatarId) != null;
            SetDisplay(_previewDeleteAvatarBtn, isCustom ? DisplayStyle.Flex : DisplayStyle.None);
        }

        private string AvatarStyleTag(string avatarId)
        {
            if (BuiltInAvatarMetaById.TryGetValue(avatarId, out var meta))
                return meta.StyleTagRu;

            return "пользовательский";
        }

        private string AvatarPersonaText(string avatarId)
        {
            var stored = GetStoredProfile(avatarId);
            if (stored != null && !string.IsNullOrWhiteSpace(stored.systemPrompt))
                return stored.systemPrompt;

            if (BuiltInAvatarMetaById.TryGetValue(avatarId, out var meta))
                return meta.PersonaRu;

            return BuiltInAvatarMetaById["neon"].PersonaRu;
        }

        private string AvatarDisplayName(string avatarId)
        {
            var custom = GetCustomProfile(avatarId);
            if (custom != null && !string.IsNullOrWhiteSpace(custom.name))
                return custom.name;

            if (BuiltInAvatarMetaById.TryGetValue(avatarId, out var meta))
                return meta.DisplayNameRu;
            return BuiltInAvatarMetaById["neon"].DisplayNameRu;
        }

        private void RefreshCustomAvatarGallery(CompanionApp app)
        {
            if (_galleryContainer == null || app == null)
                return;

            foreach (var tile in _customAvatarTiles.Values)
                _galleryContainer.Remove(tile);

            _customAvatarTiles.Clear();
            UpdateAvatarProfileCaches(app.Avatars.GetAll());

            foreach (var profile in _cachedCustomProfiles)
            {
                var tile = CreateCustomAvatarTile(profile);
                if (_avatarUploadTile != null)
                {
                    int uploadIndex = _galleryContainer.IndexOf(_avatarUploadTile);
                    if (uploadIndex >= 0)
                        _galleryContainer.Insert(uploadIndex, tile);
                    else
                        _galleryContainer.Add(tile);
                }
                else
                {
                    _galleryContainer.Add(tile);
                }

                _customAvatarTiles[profile.id] = tile;
            }

            int total = BuiltInAvatarIds.Length + _cachedCustomProfiles.Count;
            if (_navAvatarsCount != null)
                _navAvatarsCount.text = total.ToString();

            RefreshBuiltInAvatarTileLabels();
            ApplyAvatarFilter();
        }

        private VisualElement CreateCustomAvatarTile(AvatarProfile profile)
        {
            var tile = new VisualElement();
            tile.name = $"avtile-{profile.id}";
            tile.AddToClassList("avtile");

            var texture = GetOrLoadTexture(profile.imagePath);
            if (texture != null)
            {
                tile.style.backgroundImage = new StyleBackground(texture);
                tile.style.unityBackgroundScaleMode = ScaleMode.ScaleAndCrop;
            }
            else
            {
                tile.style.backgroundColor = new StyleColor(new Color(0.25f, 0.25f, 0.30f));
            }

            var nameLabel = new Label(string.IsNullOrWhiteSpace(profile.name) ? profile.id : profile.name);
            nameLabel.AddToClassList("avtile__name");
            tile.Add(nameLabel);

            var badge = new VisualElement();
            badge.AddToClassList("avtile__badge");
            var checkIcon = new VisualElement();
            checkIcon.AddToClassList("icon");
            checkIcon.AddToClassList("icon--check");
            badge.Add(checkIcon);
            tile.Add(badge);

            string capturedId = profile.id;
            tile.RegisterCallback<ClickEvent>(_ => SelectAvatar(capturedId));
            return tile;
        }

        private AvatarProfile GetCustomProfile(string avatarId)
        {
            return _cachedCustomProfiles?.FirstOrDefault(a => a.id == avatarId);
        }

        private AvatarProfile GetStoredProfile(string avatarId)
        {
            if (string.IsNullOrWhiteSpace(avatarId))
                return null;

            return _cachedProfilesById.TryGetValue(avatarId, out var profile) ? profile : null;
        }

        private void UpdateAvatarProfileCaches(List<AvatarProfile> profiles)
        {
            _cachedProfilesById.Clear();
            if (profiles == null)
            {
                _cachedCustomProfiles = new List<AvatarProfile>();
                return;
            }

            foreach (var profile in profiles)
            {
                if (profile == null || string.IsNullOrWhiteSpace(profile.id))
                    continue;

                _cachedProfilesById[profile.id] = profile;
            }

            _cachedCustomProfiles = profiles.Where(a => a != null && !a.isBuiltIn).ToList();
        }

        private void RefreshBuiltInAvatarTileLabels()
        {
            if (_root == null) return;
            foreach (var id in BuiltInAvatarIds)
            {
                var tile = _root.Q<VisualElement>($"avtile-{id}");
                var nameLabel = tile?.Q<Label>(className: "avtile__name");
                if (nameLabel != null)
                    nameLabel.text = AvatarDisplayName(id);
            }
        }

        private void ApplyAvatarFilter()
        {
            UpdateAvatarFilterChipState();
            UpdateAvatarFilterCounts();

            if (_root == null)
                return;

            foreach (var id in BuiltInAvatarIds)
            {
                var tile = _root.Q<VisualElement>($"avtile-{id}");
                if (tile != null)
                    SetDisplay(tile, MatchesFilter(id) ? DisplayStyle.Flex : DisplayStyle.None);
            }

            foreach (var kvp in _customAvatarTiles)
                SetDisplay(kvp.Value, MatchesFilter(kvp.Key) ? DisplayStyle.Flex : DisplayStyle.None);

            if (_avatarUploadTile != null)
                SetDisplay(_avatarUploadTile, _activeAvatarFilter == AvatarFilter.All || _activeAvatarFilter == AvatarFilter.Custom
                    ? DisplayStyle.Flex
                    : DisplayStyle.None);

            if (!MatchesFilter(_activeAvatarId))
            {
                var fallback = GetFirstAvatarForActiveFilter();
                if (!string.IsNullOrEmpty(fallback))
                    SelectAvatar(fallback);
            }
        }

        private void UpdateAvatarFilterChipState()
        {
            SetFilterChipActive(_avatarFilterAllBtn, _activeAvatarFilter == AvatarFilter.All);
            SetFilterChipActive(_avatarFilterStandardBtn, _activeAvatarFilter == AvatarFilter.Standard);
            SetFilterChipActive(_avatarFilterGradientBtn, _activeAvatarFilter == AvatarFilter.Gradient);
            SetFilterChipActive(_avatarFilterMinimalBtn, _activeAvatarFilter == AvatarFilter.Minimal);
            SetFilterChipActive(_avatarFilterCustomBtn, _activeAvatarFilter == AvatarFilter.Custom);
        }

        private static void SetFilterChipActive(Button button, bool active)
        {
            button?.EnableInClassList(ActiveAvatarFilterClass, active);
        }

        private void UpdateAvatarFilterCounts()
        {
            int totalBuiltIn = BuiltInAvatarIds.Length;
            int customCount = _cachedCustomProfiles?.Count ?? 0;
            int standardCount = BuiltInAvatarIds.Count(id => GetBuiltInFilter(id) == AvatarFilter.Standard);
            int gradientCount = BuiltInAvatarIds.Count(id => GetBuiltInFilter(id) == AvatarFilter.Gradient);
            int minimalCount = BuiltInAvatarIds.Count(id => GetBuiltInFilter(id) == AvatarFilter.Minimal);

            SetCountLabel(_avatarFilterAllCount, totalBuiltIn + customCount);
            SetCountLabel(_avatarFilterStandardCount, standardCount);
            SetCountLabel(_avatarFilterGradientCount, gradientCount);
            SetCountLabel(_avatarFilterMinimalCount, minimalCount);
            SetCountLabel(_avatarFilterCustomCount, customCount);
        }

        private static void SetCountLabel(Label label, int value)
        {
            if (label != null)
                label.text = value.ToString();
        }

        private bool MatchesFilter(string avatarId)
        {
            if (_activeAvatarFilter == AvatarFilter.All)
                return true;

            if (GetCustomProfile(avatarId) != null)
                return _activeAvatarFilter == AvatarFilter.Custom;

            return GetBuiltInFilter(avatarId) == _activeAvatarFilter;
        }

        private static AvatarFilter GetBuiltInFilter(string avatarId)
        {
            if (BuiltInAvatarMetaById.TryGetValue(avatarId, out var meta))
                return meta.Filter;
            return AvatarFilter.Standard;
        }

        private string GetFirstAvatarForActiveFilter()
        {
            foreach (var id in BuiltInAvatarIds)
            {
                if (MatchesFilter(id))
                    return id;
            }

            var custom = _cachedCustomProfiles?.FirstOrDefault();
            return custom?.id;
        }

        private Texture2D GetOrLoadTexture(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            if (_customTextures.TryGetValue(path, out var cached))
                return cached;

            var texture = LoadTextureFromFile(path);
            if (texture != null)
                _customTextures[path] = texture;

            return texture;
        }

        private void ReleaseCustomTexture(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            if (_customTextures.TryGetValue(path, out var texture))
            {
                if (texture != null)
                    UnityEngine.Object.Destroy(texture);
                _customTextures.Remove(path);
            }
        }

        private static void DeleteCustomAvatarFileIfUnused(string path, List<AvatarProfile> profiles)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            bool isStillReferenced = profiles.Any(a => a != null && !a.isBuiltIn && string.Equals(a.imagePath, path, StringComparison.OrdinalIgnoreCase));
            if (isStillReferenced || !System.IO.File.Exists(path))
                return;

            System.IO.File.Delete(path);
        }

        private enum AvatarFilter
        {
            All,
            Standard,
            Gradient,
            Minimal,
            Custom
        }

        private readonly struct BuiltInAvatarMeta
        {
            public BuiltInAvatarMeta(string displayNameRu, string styleTagRu, string personaRu, AvatarFilter filter)
            {
                DisplayNameRu = displayNameRu;
                StyleTagRu = styleTagRu;
                PersonaRu = personaRu;
                Filter = filter;
            }

            public string DisplayNameRu { get; }
            public string StyleTagRu { get; }
            public string PersonaRu { get; }
            public AvatarFilter Filter { get; }
        }

        private static Texture2D LoadTextureFromFile(string path)
        {
            if (!System.IO.File.Exists(path))
                return null;

            var bytes = System.IO.File.ReadAllBytes(path);
            var texture = new Texture2D(2, 2);
            if (texture.LoadImage(bytes))
                return texture;

            UnityEngine.Object.Destroy(texture);
            return null;
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
            OpenPath(Application.persistentDataPath);
        }

        private static void OpenPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            System.Diagnostics.Process.Start("explorer.exe", path.Replace('/', '\\'));
#elif UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            System.Diagnostics.Process.Start("open", path);
#elif UNITY_EDITOR_LINUX || UNITY_STANDALONE_LINUX
            System.Diagnostics.Process.Start("xdg-open", path);
#else
            Application.OpenURL("file://" + path);
#endif
        }

        private void OnExportChatsClicked()
        {
            _ = ExportChatsAsync();
        }

        private void OnSettingsGitHubClicked()
        {
            OpenExternalUrl("https://github.com/isteelfelix/neon-companion");
            AddSystemMessage("Открыт GitHub репозитория.");
        }

        private void OnSettingsDocsClicked()
        {
            OpenExternalUrl("https://github.com/isteelfelix/neon-companion/tree/main/docs");
            AddSystemMessage("Открыта папка docs.");
        }

        private void OnSettingsDonateClicked()
        {
            AddSystemMessage("Ссылка для поддержки пока не настроена.");
        }

        private static void OpenExternalUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return;

            Application.OpenURL(url);
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

                AddSystemMessage($"Чаты экспортированы в {System.IO.Path.GetFileName(path)}.");
                OpenPath(path);
            }
            catch (Exception ex)
            {
                NeonLogger.LogError(ex.ToString());
            }
        }

        private void OnClearDataClicked()
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (!_clearDataConfirmPending || nowMs > _clearDataConfirmExpiresAtMs)
            {
                _clearDataConfirmPending = true;
                _clearDataConfirmExpiresAtMs = nowMs + 7000;
                ArmClearDataConfirmationReset();
                UpdateClearDataButtonState();
                AddSystemMessage("Нажми «Подтвердить» ещё раз в течение 7 секунд, чтобы удалить все данные.");
                return;
            }

            ResetClearDataConfirmation();
            _ = ClearAllDataAsync();
        }

        private void ResetClearDataConfirmation()
        {
            _clearDataConfirmResetSchedule?.Pause();
            _clearDataConfirmResetSchedule = null;
            _clearDataConfirmPending = false;
            _clearDataConfirmExpiresAtMs = 0;
            UpdateClearDataButtonState();
        }

        private void ArmClearDataConfirmationReset()
        {
            _clearDataConfirmResetSchedule?.Pause();
            if (_settingsClearBtn == null)
                return;

            _clearDataConfirmResetSchedule = _settingsClearBtn.schedule
                .Execute(() =>
                {
                    _clearDataConfirmResetSchedule = null;
                    ResetClearDataConfirmation();
                })
                .StartingIn(7000);
        }

        private void UpdateClearDataButtonState()
        {
            if (_clearDataConfirmPending && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > _clearDataConfirmExpiresAtMs)
            {
                _clearDataConfirmPending = false;
                _clearDataConfirmExpiresAtMs = 0;
            }

            bool armed = _clearDataConfirmPending;

            if (_settingsClearBtnText != null)
                _settingsClearBtnText.text = armed ? "Подтвердить" : "Очистить";

            _settingsClearBtn?.EnableInClassList("btn--danger-armed", armed);
        }

        private async Task ClearAllDataAsync()
        {
            try
            {
                ResetClearDataConfirmation();
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

                // Reset service cache and session identity so next call reinitialises
                _app = null;
                _chatService = null;
                _currentSessionId = string.Empty;
                _currentSessionTitle = string.Empty;

                RenderMessages(null);
                SetNoProviderState();
                if (_sessionsList != null) _sessionsList.Clear();
                if (_navChatCount != null) _navChatCount.text = "0";
                if (_navProvidersCount != null) _navProvidersCount.text = "0";
            }
            catch (Exception ex)
            {
                ResetClearDataConfirmation();
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
                AddSystemMessage("Не удалось пересоздать последний ответ.");
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
                AddSystemMessage("Не удалось импортировать провайдеров из файла.");
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

                RefreshCustomAvatarGallery(app);

                // Auto-select the uploaded avatar (force re-selection even if same ID)
                if (_activeAvatarId == profile.id) _activeAvatarId = string.Empty;
                SelectAvatar(profile.id);

                AddSystemMessage($"Аватар «{fileName}» загружен.");
            }
            catch (Exception ex)
            {
                AddSystemMessage("Не удалось загрузить аватар.");
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

        // ============================================================
        // Resize handle — drag left edge of avatar panel to resize
        // ============================================================

        private void OnResizePointerDown(PointerDownEvent evt)
        {
            if (evt.button != 0) return;
            _isResizing = true;
            _resizeStartX = evt.position.x;
            _resizeStartWidth = _avatarPanel?.resolvedStyle.width ?? 320f;
            _resizeHandle.CapturePointer(evt.pointerId);
            _resizeHandle?.AddToClassList("resize-handle--active");
            evt.StopPropagation();
        }

        private void OnResizePointerMove(PointerMoveEvent evt)
        {
            if (!_isResizing) return;
            float delta = _resizeStartX - evt.position.x;
            float newWidth = Mathf.Clamp(_resizeStartWidth + delta, MinAvatarWidth, MaxAvatarWidth);
            if (_avatarPanel != null)
                _avatarPanel.style.width = newWidth;
            evt.StopPropagation();
        }

        private void OnResizePointerUp(PointerUpEvent evt)
        {
            if (!_isResizing) return;
            _isResizing = false;
            _resizeHandle?.RemoveFromClassList("resize-handle--active");
            if (_resizeHandle != null && _resizeHandle.HasPointerCapture(evt.pointerId))
                _resizeHandle.ReleasePointer(evt.pointerId);
            evt.StopPropagation();
        }
    }
}
