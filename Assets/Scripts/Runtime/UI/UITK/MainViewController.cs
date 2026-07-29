using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Api;
using NeonCompanion.Runtime.Avatar;
using NeonCompanion.Runtime.Avatar3D;
using NeonCompanion.Runtime.Chat;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Donation;
using NeonCompanion.Runtime.Localization;
using NeonCompanion.Runtime.Platform;
using NeonCompanion.Runtime.UI.UITK.Chat;
using NeonCompanion.Runtime.UI.Avatars;
using NeonCompanion.Runtime.UI.UITK.Terminal;
using NeonCompanion.Runtime.Api.Hermes;
using NeonCompanion.Runtime.Voice;
using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK
{
    internal enum AvatarMotionState
    {
        Idle,
        Thinking,
        Talking,
        Listening
    }

    [RequireComponent(typeof(UIDocument))]
    public sealed class MainViewController : MonoBehaviour
    {
        private const string ActiveNavClass = "nav__item--active";
        private const string ActiveProviderClass = "provider--active";
        private const string EditingProviderClass = "provider--editing";
        private const string ActiveAvatarFilterClass = "filterchip--active";
        private const string CustomModelPresetValue = "Custom / manual";
        private static readonly Dictionary<string, string> StaticTemplateTextToKey = BuildStaticTemplateTextMap();

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

        private VisualElement _root;
        private VisualElement _appRoot;

        private VisualElement _chatPanel;
        private VisualElement _historyPanel;
        private VisualElement _providersPanel;
        private VisualElement _avatarsPanel;
        private VisualElement _themesPanel;
        private VisualElement _placeholderArea;
        private VisualElement _settingsPanel;
        private VisualElement _composer;
        private VisualElement _resizeHandle;
        private VisualElement _avatarPanel;
        private VisualElement _railResizeHandle;
        private VisualElement _railElement;
        private readonly PanelResizeHandler _panelResizeHandler = new PanelResizeHandler();
        private VisualElement _topbarSep;
        private VisualElement _typingIndicator;

        // ===== Quit / close =====
        private Label _navCloseLabel;

        private readonly SettingsController _settingsController = new SettingsController();
        private readonly NavigationController _navigationController = new NavigationController();
        private readonly ChatController _chatController = new ChatController();
        private readonly SessionHistoryController _sessionHistoryController = new SessionHistoryController();
        private readonly ProvidersController _providersController = new ProvidersController();
        private readonly AvatarGalleryController _avatarGalleryController = new AvatarGalleryController();
        private readonly VoiceController _voiceController = new VoiceController();
        private readonly LayoutController _layoutController = new LayoutController();
        private readonly CompanionWindowController _companionWindowController = new CompanionWindowController();

        // ===== Terminal (right panel tab) =====
        private TerminalController _terminalController;
        private VisualElement _rightTabBar;
        private Button _avatarTabBtn;
        private Button _terminalTabBtn;
        private VisualElement _avatarContentHost;
        private VisualElement _terminalHost;
        private bool _rightPanelIsTerminal;

        private HermesSessionManager _terminalHermesManager;
        private ClientTerminalExecutionService _clientTerminalService;

        private VisualElement _avatarCircle;

        // ===== Inline streaming typing dots =====

        // ===== Tool progress UI =====
        private VisualElement _thinkingBubble;
        private Label _thinkingText;

        private Label _topbarTitle;
        private Label _topbarSubtitle;
        private Label _placeholderTitle;
        private Label _placeholderBody;

        private Button _sendButton;
        private Button _summarizeButton;
        private Button _searchButton;
        private Button _moreButton;
        private Button _mobileMenuButton;
        private Button _newSessionButton;
        private Button _exportButton;
        private Button _scrollBottomBtn;
        private TextField _composerInput;
        private ScrollView _messagesList;
        private ScrollView _sessionsList;
        private ScrollView _historySessionsList;
        private Label _historyState;
        private VisualElement _historySearchBar;
        private TextField _historySearchInput;
        private Button _historySearchBtn;
        private Button _historySearchClear;
        private VisualElement _historyPanelSearchBar;
        private TextField _historyPanelSearchInput;
        private Button _historyPanelSearchBtn;
        private Button _historyPanelSearchClear;
        private Button _historyPanelNewSessionButton;

        // copy-btn, refresh-btn, listen-btn removed — now in bubble hover
        private Button _micButton;
        private Button _attachButton;
        private VisualElement _composerPreviews;
        private Button _stopButton;

        private CompanionApp _app;
        private ChatService _chatService;
        private string _currentSessionId = string.Empty;
        private string _currentSessionTitle = string.Empty;
        private bool _isBound;
        private AvatarAnimationController _avatarAnimationController;
        private AudioSource _notifySource; // U-40 notification sounds
        private AudioClip _notifyClip;
        private bool _isRefreshingLocalizedUi;
        private IVisualElementScheduledItem _scrollBottomButtonSchedule;

        private static Dictionary<string, string> BuildStaticTemplateTextMap()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);

            void Add(string key, string ru, string en)
            {
                map[ru] = key;
                map[en] = key;
            }

            Add("settings.page.title", "Настройки", "Settings");
            Add("settings.page.subtitle", "Общие параметры приложения, данные и информация.", "General app parameters, data, and info.");
            Add("settings.section.general", "Общие", "General");
            Add("settings.language.row.title", "Язык", "Language");
            Add("settings.language.row.subtitle", "Язык интерфейса. Применяется сразу.", "Interface language. Applied immediately.");
            Add("settings.history.row.title", "Сохранять историю чатов", "Save chat history");
            Add("settings.history.row.subtitle", "Локально, в JSON-файлах.", "Stored locally in JSON files.");
            Add("settings.streaming.row.title", "Streaming ответов", "Streaming responses");
            Add("settings.streaming.row.subtitle", "Показывать ответ по мере поступления токенов.", "Show response as tokens arrive.");
            Add("settings.enter_to_send.row.title", "Enter → отправить", "Enter → send");
            Add("settings.systemprompt.row.title", "System prompt персонажа", "Avatar system prompt");
            Add("settings.systemprompt.row.subtitle", "Использовать персону выбранного аватара.", "Use selected avatar persona.");
            Add("settings.voice.row.subtitle", "Голосовой ввод и озвучивание ответов.", "Voice input and response playback.");
            Add("settings.section.security", "Безопасность", "Security");
            Add("settings.security.encrypt.title", "Шифровать API-ключи", "Encrypt API keys");
            Add("settings.security.encrypt.subtitle", "Хранить ключи в защищённом виде на устройстве.", "Store keys securely on this device.");
            Add("settings.security.mask.title", "Скрывать ключи в логах", "Mask keys in logs");
            Add("settings.security.mask.subtitle", "Маскировать значения при отладке.", "Mask values in debug output.");
            Add("settings.section.data", "Данные", "Data");
            Add("settings.data.storage", "Папка хранения", "Storage folder");
            Add("settings.data.export.title", "Экспорт чатов", "Export chats");
            Add("settings.data.export.subtitle", "Сохранить всю историю в JSON.", "Save all history to JSON.");
            Add("settings.data.clear_chats.title", "Очистить историю чатов", "Clear chat history");
            Add("settings.data.clear_chats.subtitle", "Удалить все сессии. Провайдеры и настройки сохранятся.", "Delete all sessions. Providers and settings are kept.");
            Add("settings.data.clear.title", "Очистить все данные", "Clear all data");
            Add("settings.data.clear.subtitle", "Удалить сессии, провайдеров и настройки. Действие необратимо.", "Delete sessions, providers, and settings. This action is irreversible.");
            Add("settings.section.plugins", "Плагины", "Plugins");
            Add("settings.plugins.load_status", "Статус загрузки", "Load status");
            Add("settings.plugins.configs", "Конфиги плагинов", "Plugin configs");
            Add("settings.section.about", "О приложении", "About");
            Add("settings.about.version", "Версия", "Version");
            Add("settings.about.license", "Лицензия", "License");
            Add("settings.about.fonts", "Шрифты", "Fonts");
            Add("settings.docs", "Документация", "Documentation");
            Add("settings.support", "Поддержать", "Support");

            Add("providers.page.title", "Провайдеры", "Providers");
            Add("providers.page.subtitle", "Подключи OpenAI-совместимый API или Hermes backend и переключайся прямо из чата.", "Connect an OpenAI-compatible API or Hermes backend and switch right from chat.");
            Add("providers.connection.config", "Конфигурация подключения", "Connection configuration");
            Add("providers.field.name", "Название", "Name");
            Add("providers.field.baseurl", "Базовый URL", "Base URL");
            Add("providers.field.apikey", "API-ключ", "API key");
            Add("providers.field.model", "Модель по умолчанию", "Default model");
            Add("providers.field.model.manual", "ID модели (вручную)", "Model ID (manual)");
            Add("providers.field.temperature", "Температура", "Temperature");
            Add("providers.field.max_tokens", "Макс. токенов", "Max tokens");
            Add("providers.test.hint", "Нажми Тест для проверки соединения", "Press Test to check connection");

            Add("avatars.page.title", "Аватары", "Avatars");
            Add("avatars.page.subtitle", "Визуальное представление агента. Можно выбрать готовый образ или загрузить свой PNG.", "Agent visual identity. Choose a preset or upload your own PNG.");
            Add("avatars.filter.all", "Все", "All");
            Add("avatars.filter.standard", "Стандартные", "Standard");
            Add("avatars.filter.gradient", "Градиентные", "Gradient");
            Add("avatars.filter.minimal", "Минимализм", "Minimal");
            Add("avatars.filter.custom", "Свои", "Custom");
            Add("avatars.drag_png", "перетащи PNG", "drop PNG");
            Add("avatars.persona", "Персона", "Persona");
            Add("avatars.persona.label", "Инструкции аватара", "Avatar instructions");
            Add("avatars.customization", "Кастомизация", "Customization");
            Add("avatars.emoji", "Эмодзи", "Emoji");

            Add("themes.page.title", "Темы", "Themes");
            Add("themes.page.subtitle", "Палитра акцента и поведение аватара в интерфейсе.", "Accent palette and avatar behavior in UI.");
            Add("themes.hero.title", "Визуальная подача Neon", "Neon visual style");
            Add("themes.hero.subtitle", "Выбери палитру акцента и собери внешний вид аватара: форма, halo и breathing. Изменения применяются сразу и сохраняются локально.", "Pick an accent palette and tune avatar look: shape, halo, and breathing. Changes apply instantly and are saved locally.");
            Add("themes.section.palette", "Палитра", "Palette");
            Add("themes.palette.title", "Палитра акцента", "Accent palette");
            Add("themes.palette.indigo", "Индиго", "Indigo");
            Add("themes.palette.rose", "Роуз", "Rose");
            Add("themes.palette.cyan", "Циан", "Cyan");
            Add("themes.palette.ember", "Эмбер", "Ember");
            Add("themes.palette.mono", "Моно", "Mono");
            Add("themes.palette.subtitle", "Меняет цвет подсветки кнопок, ссылок и активных элементов во всём интерфейсе.", "Changes the highlight color of buttons, links, and active elements across the UI.");
            Add("themes.section.shape", "Форма и анимация", "Shape and animation");
            Add("themes.shape.title", "Форма аватара", "Avatar shape");
            Add("themes.shape.subtitle", "Выбери базовую геометрию для портрета справа в чате.", "Choose base geometry for the portrait on the right.");
            Add("themes.shape.round", "Круг", "Round");
            Add("themes.shape.square", "Квадрат", "Square");
            Add("themes.halo.title", "Halo вокруг аватара", "Halo around avatar");
            Add("themes.halo.subtitle", "Неоновое свечение за портретом. Добавляет глубину, но не перегружает сцену.", "Neon glow behind portrait. Adds depth without clutter.");
            Add("themes.breathing.title", "Анимация breathing", "Breathing animation");
            Add("themes.breathing.subtitle", "Лёгкая пульсация аватара в idle-состоянии.", "Subtle avatar pulsing while idle.");
            Add("themes.next", "Что дальше", "What next");
            Add("themes.next.note", "Дальше: реактивные состояния и дополнительные пресеты отображения.", "Next: reactive states and extra display presets.");

            Add("tooltip.history.search", "Поиск сессий", "Search sessions");
            Add("tooltip.chat.new", "Новый чат", "New chat");
            Add("tooltip.clear", "Очистить", "Clear");
            Add("tooltip.search", "Поиск", "Search");
            Add("tooltip.more", "Ещё", "More");
            Add("tooltip.copy", "Копировать", "Copy");
            Add("tooltip.regenerate", "Пересоздать", "Regenerate");
            Add("tooltip.listen", "Озвучить последний ответ", "Speak last response");
            Add("tooltip.attach", "Добавить токен вложения", "Insert attachment token");
            Add("tooltip.voice.input", "Голосовой ввод", "Voice input");
            Add("chat.export", "Экспорт", "Export");

            Add("quit.dialog.title", "Закрыть приложение?", "Close application?");
            Add("quit.dialog.body",  "Активные соединения будут прерваны.", "Active connections will be interrupted.");
            Add("nav.close",         "Закрыть", "Close");
            Add("common.cancel",     "Отмена", "Cancel");
            Add("settings.close.hotkey.title",    "Горячая клавиша закрытия", "Close hotkey");
            Add("settings.close.hotkey.subtitle", "Нажми кнопку, затем клавишу или комбинацию.", "Click the button, then press a key or combination.");

            return map;
        }

        private void OnEnable()
        {
            if (CompanionProcessMode.IsPlayerProcess)
            {
                var playerDocument = GetComponent<UIDocument>();
                if (playerDocument != null)
                    playerDocument.enabled = false;
                enabled = false;
                return;
            }

            var document = GetComponent<UIDocument>();
            if (document == null || document.rootVisualElement == null)
                return;

            Bind(document.rootVisualElement);
            RegisterCallbacks();
            _ = _settingsController.BindLocalizationEventsAsync();
            _navigationController.ShowChat();

            // Subscribe to backend mode changes for nav visibility
            var backendSelector = Core.GlobalBackendSelector.Instance;
            if (backendSelector != null)
            {
                backendSelector.OnModeChanged += OnBackendModeChangedForNav;
                // Apply current mode
                _navigationController.ApplyBackendModeVisibility(
                    backendSelector.CurrentMode == Core.BackendMode.Hermes ? "hermes" : "openai");
                if (backendSelector.CurrentMode == Core.BackendMode.Hermes)
                    SetupTerminalRemoteBridge();
            }

            _ = RefreshAsync();
        }

        private void OnDisable()
        {
            UnregisterCallbacks();
            _voiceController.OnDisable();
            _settingsController.UnbindLocalizationEvents();
            _avatarGalleryController.OnDisable();
            _layoutController.OnDisable();
            _isBound = false;

            // Unsubscribe from backend mode changes
            var backendSelector = Core.GlobalBackendSelector.Instance;
            if (backendSelector != null)
                backendSelector.OnModeChanged -= OnBackendModeChangedForNav;

            if (_chatService != null && _sessionStatesSubscribed)
            {
                _chatService.OnSessionStatesChanged -= OnSessionStatesChanged;
                _chatService.OnSessionTitleChanged -= OnSessionTitleChanged;
                _sessionStatesSubscribed = false;
            }

            TeardownTerminalRemoteBridge();
        }

        private void Update()
        {
            _companionWindowController.Tick();
            _voiceController.Tick();
        }

        private bool _sessionStatesSubscribed;

        private void OnSessionStatesChanged()
        {
            if (!_isBound) return;
            _sessionHistoryController.RerenderStatus();
        }

        /// <summary>
        /// The gateway auto-titled a Hermes chat (session.title). Patch the sidebar in place instead
        /// of waiting for the next history reload — the push is the only notice the title changed.
        /// </summary>
        private void OnSessionTitleChanged(string sessionId, string title)
        {
            if (!_isBound) return;
            _sessionHistoryController.ApplySessionTitle(sessionId, title);
        }

        private void OnBackendModeChangedForNav(Core.BackendMode mode)
        {
            string modeStr = mode == Core.BackendMode.Hermes ? "hermes" : "openai";
            _navigationController.ApplyBackendModeVisibility(modeStr);
            _ = _sessionHistoryController.RefreshSessionsFromCacheAsync();

            if (mode == Core.BackendMode.Hermes)
            {
                SetupTerminalRemoteBridge();
            }
            else
            {
                TeardownTerminalRemoteBridge();
            }
        }

        private void Bind(VisualElement root)
        {
            _root = root;
            // app-root несёт класс .app — на нём живут форм-фактор/платформенные классы.
            _appRoot = root.Q<VisualElement>("app-root") ?? root;
            _navItems.Clear();

            _navChat = root.Q<VisualElement>("nav-chat");
            _navAvatars = root.Q<VisualElement>("nav-avatars");
            _navProviders = root.Q<VisualElement>("nav-providers");
            _navHistory = root.Q<VisualElement>("nav-history");
            _navThemes = root.Q<VisualElement>("nav-themes");
            _navSettings = root.Q<VisualElement>("nav-settings");
            _providerTag = root.Q<VisualElement>("provider-tag");

            _navChatLabel = root.Q<Label>("nav-chat-label");
            _navAvatarsLabel = root.Q<Label>("nav-avatars-label");
            _navProvidersLabel = root.Q<Label>("nav-providers-label");
            _navHistoryLabel = root.Q<Label>("nav-history-label");
            _navThemesLabel = root.Q<Label>("nav-themes-label");
            _navSettingsLabel = root.Q<Label>("nav-settings-label");
            _navChatCount = root.Q<Label>("nav-chat-count");

            _chatPanel = root.Q<VisualElement>("chat-panel");
            _historyPanel = root.Q<VisualElement>("history-panel");
            _providersPanel = root.Q<VisualElement>("providers-panel");
            _avatarsPanel = root.Q<VisualElement>("avatars-panel");
            _themesPanel = root.Q<VisualElement>("themes-panel");
            _placeholderArea = root.Q<VisualElement>("placeholder-area");
            _settingsPanel = root.Q<VisualElement>("settings-panel");
            _composer = root.Q<VisualElement>("composer");
            _resizeHandle = root.Q<VisualElement>("resize-handle");
            _avatarPanel  = root.Q<VisualElement>("avatar-panel");
            _railElement = root.Q<VisualElement>("rail");

            // Setup terminal toggle tabs inside right avatar panel (no UXML change)
            SetupRightPanelTabs();
            _railResizeHandle = root.Q<VisualElement>("rail-resize-handle");
            _topbarSep = root.Q<VisualElement>("topbar-sep");
            _typingIndicator = root.Q<VisualElement>("typing-indicator");

            _topbarTitle = root.Q<Label>("topbar-title");
            _topbarSubtitle = root.Q<Label>("topbar-subtitle");
            _placeholderTitle = root.Q<Label>("placeholder-title");
            _placeholderBody = root.Q<Label>("placeholder-body");

            _composerInput = root.Q<TextField>("message-input");

            if (_composerInput != null)
            {
                _composerInput.multiline = true;
            }
            _sendButton = root.Q<Button>("send-button");
            _summarizeButton = root.Q<Button>("summarize-btn");
            _searchButton = root.Q<Button>("search-btn");
            _moreButton = root.Q<Button>("more-btn");
            _mobileMenuButton = root.Q<Button>("mobile-menu-btn");
            _newSessionButton = root.Q<Button>("new-session-btn");
            _exportButton = root.Q<Button>("export-btn");
            _messagesList = root.Q<ScrollView>("messages-list");
            _scrollBottomBtn = root.Q<Button>("scroll-bottom-btn");
            _sessionsList = root.Q<ScrollView>("sessions-list");
            _historySessionsList = root.Q<ScrollView>("history-panel-sessions-list");

            // Transcript scrolls vertically only — never show a horizontal scrollbar
            // (long code lines wrap; on phone a stray h-scroll was appearing).
            if (_messagesList != null)
                _messagesList.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            if (_sessionsList != null)
                _sessionsList.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            if (_historySessionsList != null)
                _historySessionsList.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            _historyState = root.Q<Label>("history-panel-state");
            _historySearchBar   = root.Q<VisualElement>("history-search-bar");
            _historySearchInput = root.Q<TextField>("history-search-input");
            _historySearchBtn   = root.Q<Button>("history-search-btn");
            _historySearchClear = root.Q<Button>("history-search-clear");
            _historyPanelSearchBar = root.Q<VisualElement>("history-panel-search-bar");
            _historyPanelSearchInput = root.Q<TextField>("history-panel-search-input");
            _historyPanelSearchBtn = root.Q<Button>("history-panel-search-btn");
            _historyPanelSearchClear = root.Q<Button>("history-panel-search-clear");
            _historyPanelNewSessionButton = root.Q<Button>("history-panel-new-session-btn");

            // copy-btn, refresh-btn, listen-btn removed from UXML — now in bubble hover
            _micButton = root.Q<Button>("mic-button");
            _attachButton = root.Q<Button>("attach-btn");
            _composerPreviews = root.Q<VisualElement>("composer-previews");
            _stopButton = root.Q<Button>("stop-button");

            ApplyLocalizedStaticTexts();

            _avatarCircle    = root.Q<VisualElement>("avatar-circle");
            _thinkingBubble  = root.Q<VisualElement>("thinking-bubble");
            _thinkingText    = root.Q<Label>("thinking-text");

            _navCloseLabel = root.Q<Label>("nav-close-label");

            _settingsController.Init(root, BuildSettingsControllerDeps(), _avatarCircle);
            _navigationController.Init(root, BuildNavigationControllerDeps());
            _chatController.SetDeps(BuildChatControllerDeps());
            _chatController.RegisterCallbacks();
            _sessionHistoryController.SetDeps(BuildSessionHistoryControllerDeps());
            _providersController.SetDeps(BuildProvidersControllerDeps());
            _providersController.Init();
            _providersController.RegisterCallbacks();
            _avatarGalleryController.SetDeps(BuildAvatarGalleryControllerDeps());
            _avatarGalleryController.Init();
            _avatarGalleryController.RegisterCallbacks();
            _voiceController.SetDeps(BuildVoiceControllerDeps());
            _voiceController.Init();
            _voiceController.RegisterCallbacks();
            _layoutController.SetDeps(BuildLayoutControllerDeps());
            _layoutController.Init();
            _layoutController.RegisterCallbacks();
            _companionWindowController.SetDeps(BuildCompanionWindowControllerDeps());
            _companionWindowController.Init();
            _companionWindowController.RegisterCallbacks();

            _chatController.InitState();
            _isBound = true;
            _ = _settingsController.LoadSettingsAsync();
        }

        private SettingsControllerDeps BuildSettingsControllerDeps()
        {
            return new SettingsControllerDeps
            {
                GetApp            = GetAppAsync,
                GetChatService    = GetChatServiceAsync,
                GetChatServiceSync = () => _chatService,
                IsActiveAndEnabled = () => isActiveAndEnabled,
                IsBound           = () => _isBound,
                GetActiveAvatarId = () => _avatarGalleryController.ActiveAvatarId,
                SetActiveAvatarId = id =>
                {
                    _avatarGalleryController.ActiveAvatarId = id;
                },
                GetAvatarViewMode = () => _avatarGalleryController.AvatarViewModeSetting,
                SetAvatarViewMode = mode => _avatarGalleryController.SetAvatarViewModeFromSetting(mode),
                RefreshVoiceControls  = RefreshVoiceControls,
                RequestRefreshLocalizedUi = RefreshLocalizedUiAsync,
                RefreshCustomAvatarGallery   = _avatarGalleryController.RefreshCustomAvatarGallery,
                RefreshBuiltInAvatarTileLabels = _avatarGalleryController.RefreshBuiltInAvatarTileLabels,
                ApplyAvatarFilter    = _avatarGalleryController.ApplyAvatarFilter,
                ApplyAvatarArt       = _avatarGalleryController.ApplyAvatarArt,
                SyncGallerySelection = _avatarGalleryController.SyncGallerySelection,
                ShowHistoryState     = ShowHistoryState,
                RequestRenderMessages = () => RenderMessages(null),
                AddSystemMessage    = AddSystemMessage,
                SetNoProviderState  = SetNoProviderState,
                ResetServiceCache   = () => { _app = null; _chatService = null; },
                ClearSessionsListUi = () =>
                {
                    _sessionsList?.Clear();
                    _historySessionsList?.Clear();
                    if (_navChatCount != null) _navChatCount.text = "0";
                },
                ResetProvidersCountUi = () => _providersController.ResetNavProvidersCount(),
                SetCurrentSessionId   = id => _currentSessionId = id,
                SetCurrentSessionTitle = title => _currentSessionTitle = title,
            };
        }

        private NavigationControllerDeps BuildNavigationControllerDeps()
        {
            return new NavigationControllerDeps
            {
                CanLeaveProviderEditor = _providersController.CanLeaveProviderEditor,
                SetTopbar = (title, sub) => SetTopbar(title, sub),
                SetChatModelPickerVisible = SetChatModelPickerVisible,
                ShowArea = ShowArea,
                RefreshProvidersListAsync = () => { _ = _providersController.RefreshProvidersListAsync(); },
                RefreshSessionsFromCacheAsync = () => RefreshSessionsFromCacheAsync(),
                GetChatTitle = GetChatTitle,
                GetChatSubtitle = () => _chatController.ChatSubtitle,
                AvatarDisplayName = _avatarGalleryController.AvatarDisplayName,
                GetAvatarTotalCount = _avatarGalleryController.GetAvatarTotalCount,
                GetActiveAvatarId = () => _avatarGalleryController.ActiveAvatarId,
                GetSessionSearchQuery = () => _chatController.SessionSearchQuery,
                ChatPanel = _chatPanel,
                HistoryPanel = _historyPanel,
                ProvidersPanel = _providersPanel,
                AvatarsPanel = _avatarsPanel,
                ThemesPanel = _themesPanel,
                SettingsPanel = _settingsPanel
            };
        }

        private ChatController.Deps BuildChatControllerDeps()
        {
            return new ChatController.Deps
            {
                TrySendVoicePreview = text => _voiceController != null && _voiceController.TrySendActivePreview(text),
                MessageInput = _composerInput,
                SendButton = _sendButton,
                StopButton = _stopButton,
                SummarizeButton = _summarizeButton,
                SearchButton = _searchButton,
                AttachButton = _attachButton,
                NewSessionButton = _newSessionButton,
                ExportButton = _exportButton,
                MessagesList = _messagesList,
                ScrollBottomBtn = _scrollBottomBtn,
                Composer = _composer,
                ThinkingBubble = _thinkingBubble,
                ThinkingText = _thinkingText,
                TopbarSubtitle = _topbarSubtitle,
                NavChatCount = _navChatCount,
                GetAvatarAnimator = _avatarGalleryController.GetAvatarAnimatorInstance,
                SetAvatarMotionState = _avatarGalleryController.SetAvatarMotionState,
                RefreshAvatarMotionState = _avatarGalleryController.RefreshAvatarMotionState,
                StopAvatarDisplay = _companionWindowController.StopAvatarDisplay,
                TriggerAvatarSmile = TriggerAvatarSmile,
                TriggerAvatarConfused = TriggerAvatarConfused,
                GetAvatarAnimationController = () => _avatarAnimationController,
                GetChatServiceAsync = GetChatServiceAsync,
                GetAppAsync = GetAppAsync,
                LoadSessionsAsync = () => LoadSessionsAsync(_chatService),
                ShowSystemMessage = AddSystemMessage,
                ShowHistory = _navigationController.ShowHistory,
                ShowChat = _navigationController.ShowChat,
                EnterToSend = () => _settingsController.EnterToSend,
                UseStreaming = () => _settingsController.UseStreaming,
                RenderSessionList = RenderSessionList,
                RenderMessages = RenderMessages,
                ApplyModelSelectionAsync = (id, close) => _providersController.ApplyModelSelectionAsync(id, close),
                OpenModelPickerAsync = () => _providersController.OpenModelPickerAsync(),
                GetAvatarDisplayName = () => _avatarGalleryController.AvatarDisplayName(_avatarGalleryController.ActiveAvatarId),
                PlayNotificationSound = PlayNotificationBeep,
                ToggleAudioFile = path => _voiceController.ToggleMessageAudio(path),
                SeekAudioFile = (path, normalized) => _voiceController.SeekMessageAudio(path, normalized),
                StopVoiceOutput = _voiceController.StopVoiceOutput,
                GetAudioPlaybackState = path => _voiceController.GetMessageAudioState(path),
                SetCurrentSession = (id, title) => { _currentSessionId = id; _currentSessionTitle = title; }
            };
        }


        private SessionHistoryController.Deps BuildSessionHistoryControllerDeps()
        {
            return new SessionHistoryController.Deps
            {
                SessionsList = _sessionsList,
                HistorySessionsList = _historySessionsList,
                SessionItems = _sessionItems,
                HistoryState = _historyState,
                HistorySearchBar = _historySearchBar,
                HistoryPanelSearchBar = _historyPanelSearchBar,
                HistorySearchInput = _historySearchInput,
                HistoryPanelSearchInput = _historyPanelSearchInput,
                NavChatCount = _navChatCount,
                TopbarTitle = _topbarTitle,
                ChatPanel = _chatPanel,
                GetChatServiceAsync = GetChatServiceAsync,
                GetAppAsync = GetAppAsync,
                IsBound = () => _isBound,
                GetCurrentSessionId = () => _currentSessionId,
                GetChatTitle = GetChatTitle,
                GetSessionSearchQuery = () => _chatController.SessionSearchQuery,
                SetSessionSearchQuery = value => _chatController.SetSessionSearchQuery(value),
                SetCurrentSession = (id, title) => { _currentSessionId = id; _currentSessionTitle = title; },
                SetTopbar = (title, sub) => SetTopbar(title, sub),
                RenderMessages = () => RenderMessages(_chatService?.CurrentChatViewModel?.Messages),
                OnSessionSwitched = () => _chatController.OnForegroundSessionChanged(),
                ShowSystemMessage = AddSystemMessage,
                ShowHistoryState = ShowHistoryState,
                ShowChat = _navigationController.ShowChat,
                ClearPendingComposerAttachments = () => _chatController.ClearPendingComposerAttachments(),
                GetMessageInput = () => _composerInput,
                SetProviderHeader = (provider, model) => _providersController.SetProviderHeader(provider as NeonCompanion.Runtime.Data.Models.ProviderConfig, model as string)
            };
        }

        private ProvidersController.Deps BuildProvidersControllerDeps()
        {
            return new ProvidersController.Deps
            {
                ProvidersList        = _root.Q<ScrollView>("providers-list"),
                ProviderEditPanel    = _root.Q<VisualElement>("provider-edit-panel"),
                AddProviderButton    = _root.Q<Button>("add-provider-btn"),
                SaveProviderButton   = _root.Q<Button>("save-provider-btn"),
                CancelEditButton     = _root.Q<Button>("cancel-edit-btn"),
                ImportProviderButton = _root.Q<Button>("import-provider-btn"),
                TestProviderBtn      = _root.Q<Button>("test-provider-btn"),
                TestRow              = _root.Q<VisualElement>("test-row"),
                TestRowLabel         = _root.Q<Label>("test-row-label"),
                EditName             = _root.Q<TextField>("edit-name"),
                EditBaseUrl          = _root.Q<TextField>("edit-baseurl"),
                EditApiKey           = _root.Q<TextField>("edit-apikey"),
                EditApiKeyToggle     = _root.Q<Button>("edit-apikey-toggle"),
                EditModel            = _root.Q<TextField>("edit-model"),
                EditModelPreset      = _root.Q<NeonDropdown>("edit-model-preset"),
                EditModelCustomWrap  = _root.Q<VisualElement>("edit-model-custom-wrap"),
                EditContextField     = _root.Q<VisualElement>("edit-context-field"),
                EditContextLabel     = _root.Q<Label>("edit-context-label"),
                EditContextWindow    = _root.Q<TextField>("edit-context-window"),
                EditContextHint      = _root.Q<Label>("edit-context-hint"),
                EditMaxTokensLabel   = _root.Q<Label>("edit-maxtokens-label"),
                EditMaxTokens        = _root.Q<TextField>("edit-maxtokens"),
                EditTemperature      = _root.Q<Slider>("edit-temperature"),
                GlobalBackendMode    = _root.Q<NeonDropdown>("global-backend-mode"),
                BackendModeHint      = _root.Q<Label>("backend-mode-hint"),
                EditorProviderShort  = _root.Q<Label>("editor-provider-short"),
                EditorProviderName   = _root.Q<Label>("editor-provider-name"),
                EditorProviderStatus = _root.Q<Label>("editor-provider-status"),
                NavProvidersCount    = _root.Q<Label>("nav-providers-count"),
                TopbarModelPicker    = _root.Q<NeonDropdown>("topbar-model-picker"),
                ProviderShort        = _root.Q<Label>("provider-short"),
                ProviderName         = _root.Q<Label>("provider-name"),
                ProviderModel        = _root.Q<Label>("provider-model"),
                RailProviderName     = _root.Q<Label>("rail-provider-name"),
                RailProviderModel    = _root.Q<Label>("rail-provider-model"),
                RailFooter           = _root.Q<VisualElement>(className: "rail__footer"),
                Root                 = _root,
                GetAppAsync          = GetAppAsync,
                GetChatServiceAsync  = GetChatServiceAsync,
                GetChatServiceSync   = () => _chatService,
                IsBound              = () => _isBound,
                SaveSettings         = () => _settingsController.SaveSettings(),
                LoadSessionsAsync    = () => LoadSessionsAsync(_chatService),
                RenderMessages       = () => RenderMessages(_chatService?.CurrentChatViewModel?.Messages),
                AddSystemMessage     = AddSystemMessage,
                TriggerAvatarConfused = TriggerAvatarConfused,
                ShowChat             = _navigationController.ShowChat,
                SetCurrentSessionId  = id => _currentSessionId = id,
                SetCurrentSessionTitle = title => _currentSessionTitle = title
            };
        }

        private AvatarGalleryController.Deps BuildAvatarGalleryControllerDeps()
        {
            return new AvatarGalleryController.Deps
            {
                Root = _root,
                SaveSettings = () => _settingsController.SaveSettings(),
                SyncActiveAvatarSystemPromptAsync = (app) => _settingsController.SyncActiveAvatarSystemPromptAsync(app),
                ShowChat = _navigationController.ShowChat,
                GetChatSubtitle = () => _chatController.ChatSubtitle,
                SetChatSubtitle = (text) => { _chatController.SetChatSubtitle(text); },
                SetTopbarSubtitle = (text) => { if (_topbarSubtitle != null) _topbarSubtitle.text = text; },
                AddSystemMessage = AddSystemMessage,
                IsChatSending = () => _chatController.IsSending,
                IsChatStreamingResponse = () => _chatController.IsStreamingResponse,
                GetIsVoicePlaying = () => _voiceController.IsVoicePlaying,
                GetIsVoiceRecording = () => _voiceController.IsVoiceRecording,
                AvatarChanged = _companionWindowController.OnAvatarChanged,
                AvatarMotionStateChanged = _companionWindowController.OnAvatarMotionStateChanged,
                GetAppAsync = GetAppAsync,
                GetAppSync = () => _app,
                IsBound = () => _isBound,
                GetOrCreateAnimator = () =>
                {
                    var anim = gameObject.GetComponent<SpriteSheetAnimator>();
                    if (anim == null)
                        anim = gameObject.AddComponent<SpriteSheetAnimator>();
                    if (_avatarAnimationController == null)
                    {
                        _avatarAnimationController = gameObject.GetComponent<AvatarAnimationController>();
                        if (_avatarAnimationController == null)
                            _avatarAnimationController = gameObject.AddComponent<AvatarAnimationController>();
                    }
                    return anim;
                },
                GetOrCreateAvatar3DRenderer = () =>
                {
                    var r = gameObject.GetComponent<Avatar3DRenderer>();
                    if (r == null)
                        r = gameObject.AddComponent<Avatar3DRenderer>();
                    return r;
                },
                ModelParent = transform
            };
        }

        private CompanionWindowController.Deps BuildCompanionWindowControllerDeps()
        {
            return new CompanionWindowController.Deps
            {
                Root = _root,
                GetAppAsync = GetAppAsync,
                GetAppSync = () => _app,
                GetActiveAvatarId = () => _avatarGalleryController.ActiveAvatarId,
                GetAvatarDisplayName = _avatarGalleryController.AvatarDisplayName,
                CaptureBuiltInPreview = _avatarGalleryController.CaptureBuiltInPreview,
                ShowTerminal = () => SwitchRightPanelTab(true),
                ShowAvatar = () => SwitchRightPanelTab(false),
                OpenAvatarSettings = _navigationController.ShowAvatars
            };
        }

        private VoiceController.Deps BuildVoiceControllerDeps()
        {
            return new VoiceController.Deps
            {
                gameObject = gameObject,
                MicButton = _micButton,
                ComposerPreviews = _composerPreviews,
                IsVoiceEnabledBySettings = IsVoiceEnabledBySettings,
                ShouldAutoVoiceResponse = ShouldAutoVoiceResponse,
                SendVoiceMessageAsync = SendVoiceMessageAsync,
                OnVoiceRecordingStarted = OnVoiceRecordingStarted,
                OnVoicePlaybackStarted = OnTtsPlaybackStarted,
                OnVoicePlaybackProgress = _companionWindowController.UpdateVoicePlayback,
                RefreshAvatarMotionState = _avatarGalleryController.RefreshAvatarMotionState,
                AttachAssistantAudio = AttachAssistantAudio,
                OnVoicePlaybackCompleted = OnTtsPlaybackCompleted,
                GetAvatarAnimator = _avatarGalleryController.GetAvatarAnimatorInstance,
                GetAvatar3DService = _avatarGalleryController.GetAvatar3DServiceInstance,
                GetChatServiceAsync = GetChatServiceAsync,
                GetChatServiceSync = () => _chatService,
                IsBound = () => _isBound,
                GetAppSettings = () => _app != null && _app.Settings != null ? _app.Settings.Load() : new NeonCompanion.Runtime.Data.Models.AppSettings()
            };
        }

        private LayoutController.Deps BuildLayoutControllerDeps()
        {
            return new LayoutController.Deps
            {
                Root = _root,
                AppRoot = _appRoot,
                RailElement = _railElement,
                RailResizeHandle = _railResizeHandle,
                AvatarPanel = _avatarPanel,
                ResizeHandle = _resizeHandle,
                ChatPanel = _chatPanel,
                HistoryPanel = _historyPanel,
                ProvidersPanel = _providersPanel,
                AvatarsPanel = _avatarsPanel,
                ThemesPanel = _themesPanel,
                PlaceholderArea = _placeholderArea,
                SettingsPanel = _settingsPanel,
                PanelResizeHandler = _panelResizeHandler
            };
        }

        private void RegisterCallbacks()
        {
            RegisterClick(_moreButton, OnMoreClicked);
            RegisterClick(_mobileMenuButton, OnMobileMenuClicked);
            RegisterClick(_historyPanelNewSessionButton, OnHistoryPanelNewSessionClicked);
            RegisterClick(_historySearchBtn, OnHistorySearchToggled);
            RegisterClick(_historySearchClear, OnHistorySearchCleared);
            RegisterClick(_historyPanelSearchBtn, OnHistorySearchToggled);
            RegisterClick(_historyPanelSearchClear, OnHistorySearchCleared);
            if (_historySearchInput != null)
                _historySearchInput.RegisterCallback<ChangeEvent<string>>(OnHistorySearchChanged);
            if (_historyPanelSearchInput != null)
                _historyPanelSearchInput.RegisterCallback<ChangeEvent<string>>(OnHistorySearchChanged);
            ChatController.ListenMessageRequested += OnListenMessage;

            if (_messagesList != null)
            {
                _scrollBottomButtonSchedule?.Pause();
                _scrollBottomButtonSchedule = _messagesList.schedule.Execute(UpdateScrollBottomButton).Every(200);
            }

            _settingsController.RegisterCallbacks();

            // Terminal tabs (defensive re-register)
            RegisterClick(_avatarTabBtn, OnAvatarTabClicked);
            RegisterClick(_terminalTabBtn, OnTerminalTabClicked);
        }

        private void UnregisterCallbacks()
        {
            _chatController.UnregisterCallbacks();
            _avatarGalleryController.UnregisterCallbacks();
            _companionWindowController.UnregisterCallbacks();

            UnregisterClick(_moreButton, OnMoreClicked);
            UnregisterClick(_mobileMenuButton, OnMobileMenuClicked);
            UnregisterClick(_historyPanelNewSessionButton, OnHistoryPanelNewSessionClicked);
            UnregisterClick(_historySearchBtn, OnHistorySearchToggled);
            UnregisterClick(_historySearchClear, OnHistorySearchCleared);
            UnregisterClick(_historyPanelSearchBtn, OnHistorySearchToggled);
            UnregisterClick(_historyPanelSearchClear, OnHistorySearchCleared);
            ChatController.ListenMessageRequested -= OnListenMessage;

            _settingsController.UnregisterCallbacks();
            _voiceController.UnregisterCallbacks();
            _providersController.UnregisterCallbacks();

            // Terminal tabs
            UnregisterClick(_avatarTabBtn, OnAvatarTabClicked);
            UnregisterClick(_terminalTabBtn, OnTerminalTabClicked);

            _scrollBottomButtonSchedule?.Pause();
            _scrollBottomButtonSchedule = null;
        }

        // ============================================================
        // Right panel tabs: Avatar <-> Terminal (MVP terminal phase 1)
        // ============================================================

        private void SetupRightPanelTabs()
        {
            if (_avatarPanel == null)
                return;

            // Create tab bar (small header with two buttons)
            _rightTabBar = new VisualElement();
            _rightTabBar.name = "right-panel-tabs";
            _rightTabBar.AddToClassList("right-panel-tabs");

            _avatarTabBtn = new Button();
            _avatarTabBtn.text = LocalizationExtensions.Get("tab.avatar", "Avatar");
            _avatarTabBtn.AddToClassList("right-panel-tab");

            _terminalTabBtn = new Button();
            _terminalTabBtn.text = LocalizationExtensions.Get("terminal.title", "Terminal");
            _terminalTabBtn.AddToClassList("right-panel-tab");

            _rightTabBar.Add(_avatarTabBtn);
            _rightTabBar.Add(_terminalTabBtn);

            // Insert tab bar as first child
            _avatarPanel.Insert(0, _rightTabBar);

            // Create/reparent avatar content host (move existing children except tab bar)
            _avatarContentHost = new VisualElement();
            _avatarContentHost.name = "avatar-content-host";
            _avatarContentHost.AddToClassList("right-panel-content");

            // Move current children (thinking + avatar hero) into the content host
            // Note: children[0] is now our tab bar, so start from 1
            var toMove = new System.Collections.Generic.List<VisualElement>();
            for (int i = 1; i < _avatarPanel.childCount; i++)
            {
                toMove.Add(_avatarPanel[i]);
            }
            for (int i = 0; i < toMove.Count; i++)
            {
                _avatarPanel.Remove(toMove[i]);
                _avatarContentHost.Add(toMove[i]);
            }
            _avatarPanel.Add(_avatarContentHost);

            // Create terminal host (hidden initially)
            _terminalHost = new VisualElement();
            _terminalHost.name = "terminal-host";
            _terminalHost.AddToClassList("terminal-host");
            _terminalHost.style.display = DisplayStyle.None;
            _avatarPanel.Add(_terminalHost);

            // Tabs are registered in RegisterCallbacks() / UnregisterCallbacks()

            // Start in avatar mode
            _rightPanelIsTerminal = false;
            ApplyRightTabVisuals();
        }

        private void SwitchRightPanelTab(bool showTerminal)
        {
            if (_rightPanelIsTerminal == showTerminal)
                return;

            _rightPanelIsTerminal = showTerminal;
            ApplyRightTabVisuals();

            if (_avatarContentHost != null)
                _avatarContentHost.style.display = showTerminal ? DisplayStyle.None : DisplayStyle.Flex;

            if (_terminalHost != null)
                _terminalHost.style.display = showTerminal ? DisplayStyle.Flex : DisplayStyle.None;

            if (showTerminal)
            {
                // Hide avatar-specific overlays while terminal is active
                if (_thinkingBubble != null)
                    _thinkingBubble.style.display = DisplayStyle.None;

                EnsureTerminalController();
            }
            else
            {
                // Restore thinking bubble visibility to controller logic (it manages its own display)
                // No forced show here; ChatController/animation will control it.
            }
        }

        private void ApplyRightTabVisuals()
        {
            if (_avatarTabBtn != null)
                _avatarTabBtn.EnableInClassList("right-panel-tab--active", !_rightPanelIsTerminal);
            if (_terminalTabBtn != null)
                _terminalTabBtn.EnableInClassList("right-panel-tab--active", _rightPanelIsTerminal);
        }

        private void OnAvatarTabClicked()
        {
            SwitchRightPanelTab(false);
        }

        private void OnTerminalTabClicked()
        {
            SwitchRightPanelTab(true);
        }


        private void EnsureTerminalController()
        {
            if (_terminalController != null)
            {
                _terminalController.SetVisible(true);
                return;
            }

            if (_terminalHost == null)
                return;

            _terminalController = gameObject.AddComponent<TerminalController>();
            _terminalController.Initialize(_terminalHost);

            // Wire remote terminal execution bridge (for hermes terminal.execute) after controller ready
            SetupTerminalRemoteBridge();
            ReplayAgentTerminalBacklogs();
        }

        private void ReplayAgentTerminalBacklogs()
        {
            if (_terminalController == null || _terminalHermesManager == null)
                return;

            string sessionId = _terminalHermesManager.ActiveSessionId;
            var processIds = _terminalHermesManager.AgentTerminals.ProcessIds(sessionId);
            for (int i = 0; i < processIds.Count; i++)
            {
                string processId = processIds[i];
                string backlog = _terminalHermesManager.AgentTerminals.Read(sessionId, processId);
                _terminalController.AppendAgentOutput(processId, string.Empty, backlog);
            }
        }

        private void SetupTerminalRemoteBridge()
        {
            var selector = Core.GlobalBackendSelector.Instance;
            if (selector == null || selector.SessionManager == null)
                return;

            bool managerChanged = _terminalHermesManager != null &&
                _terminalHermesManager != selector.SessionManager;
            if (_terminalHermesManager != null)
            {
                _terminalHermesManager.OnTerminalExecute -= HandleRemoteTerminalExecute;
                _terminalHermesManager.OnTerminalReadRequest -= HandleRemoteTerminalRead;
                _terminalHermesManager.OnAgentTerminalOutput -= HandleAgentTerminalOutput;
                _terminalHermesManager.OnAgentTerminalClose -= HandleAgentTerminalClose;
                _terminalHermesManager.OnReviewSummary -= HandleReviewSummary;
                _terminalHermesManager.OnStateChanged -= HandleTerminalTransportStateChanged;
            }
            if (managerChanged && _clientTerminalService != null)
                _clientTerminalService.ResetConnection();

            _terminalHermesManager = selector.SessionManager;
            _terminalHermesManager.OnTerminalExecute += HandleRemoteTerminalExecute;
            _terminalHermesManager.OnTerminalReadRequest += HandleRemoteTerminalRead;
            _terminalHermesManager.OnAgentTerminalOutput += HandleAgentTerminalOutput;
            _terminalHermesManager.OnAgentTerminalClose += HandleAgentTerminalClose;
            _terminalHermesManager.OnReviewSummary += HandleReviewSummary;
            _terminalHermesManager.OnStateChanged += HandleTerminalTransportStateChanged;
            ResolveClientTerminalService();
        }

        private void TeardownTerminalRemoteBridge()
        {
            if (_terminalHermesManager != null)
            {
                _terminalHermesManager.OnTerminalExecute -= HandleRemoteTerminalExecute;
                _terminalHermesManager.OnTerminalReadRequest -= HandleRemoteTerminalRead;
                _terminalHermesManager.OnAgentTerminalOutput -= HandleAgentTerminalOutput;
                _terminalHermesManager.OnAgentTerminalClose -= HandleAgentTerminalClose;
                _terminalHermesManager.OnReviewSummary -= HandleReviewSummary;
                _terminalHermesManager.OnStateChanged -= HandleTerminalTransportStateChanged;
                _terminalHermesManager = null;
            }
            if (_clientTerminalService != null)
                _clientTerminalService.ResetConnection();
        }

        private void HandleTerminalTransportStateChanged(TransportState state)
        {
            if (state != TransportState.Connected && _clientTerminalService != null)
                _clientTerminalService.ResetConnection();
        }

        private ClientTerminalExecutionService ResolveClientTerminalService()
        {
            if (_clientTerminalService != null)
                return _clientTerminalService;

            var bootstrap = FindAnyObjectByType<AppBootstrap>();
            if (bootstrap == null || bootstrap.App == null || bootstrap.App.Services == null)
                return null;

            try
            {
                _clientTerminalService =
                    bootstrap.App.Services.GetRequired<ClientTerminalExecutionService>();
            }
            catch (Exception)
            {
                _clientTerminalService = null;
            }
            return _clientTerminalService;
        }

        // Background self-improvement review saved to memory/skills (Desktop review.summary parity):
        // pin it as a persistent system line in the transcript. AddSystemMessage appends to the live
        // list, so only render it when the summary's own session is the one on screen — a background
        // session's summary must not be misattributed to the focused chat.
        private void HandleReviewSummary(string sessionId, string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;
            if (_terminalHermesManager != null && sessionId != _terminalHermesManager.ActiveSessionId)
                return;
            AddSystemMessage(text);
        }

        private void HandleAgentTerminalOutput(string sessionId, string processId, string chunk)
        {
            bool createdController = false;
            if (_terminalController == null)
            {
                EnsureTerminalController();
                createdController = _terminalController != null;
            }
            if (_terminalController == null)
                return;
            if (_terminalHermesManager == null)
                return;

            string backlog = _terminalHermesManager.AgentTerminals.Read(sessionId, processId);
            _terminalController.AppendAgentOutput(
                processId,
                createdController ? string.Empty : chunk,
                backlog);
        }

        private void HandleAgentTerminalClose(string sessionId, string processId)
        {
            if (_terminalController != null)
                _terminalController.CloseAgentOutput(processId);
        }

        private async void HandleRemoteTerminalExecute(TerminalExecuteRequest request)
        {
            if (request == null)
                return;

            HermesSessionManager manager = _terminalHermesManager;
            if (manager == null)
                return;

            ClientTerminalExecutionService service = ResolveClientTerminalService();
            if (service == null)
            {
                await RespondToClientTerminal(
                    manager,
                    request,
                    new Core.ProcessResult { exitCode = -1, stderr = "Client terminal service is unavailable." },
                    0,
                    "error",
                    "service_unavailable");
                return;
            }

            if (string.IsNullOrEmpty(request.SessionId) ||
                !string.Equals(request.SessionId, manager.ActiveSessionId, StringComparison.Ordinal))
            {
                await RespondToClientTerminal(
                    manager,
                    request,
                    new Core.ProcessResult { exitCode = -1, stderr = "Client session is not active." },
                    0,
                    "denied",
                    "inactive_session");
                return;
            }

            if (!service.HasSessionGrant(request.SessionId))
            {
                string choice = await _chatController.RequestClientTerminalApprovalAsync(request);
                if (string.Equals(choice, "session", StringComparison.OrdinalIgnoreCase))
                {
                    service.GrantSession(request.SessionId);
                }
                else if (!string.Equals(choice, "once", StringComparison.OrdinalIgnoreCase))
                {
                    await RespondToClientTerminal(
                        manager,
                        request,
                        new Core.ProcessResult { exitCode = -1, stderr = "Local execution was denied by the user." },
                        0,
                        "denied",
                        "user_denied");
                    return;
                }
            }

            // The user may switch chats or the socket may drop while the approval is visible.
            // In either case the original authority is stale and the command must not run.
            if (manager != _terminalHermesManager || !manager.IsConnected)
                return;
            if (!string.Equals(request.SessionId, manager.ActiveSessionId, StringComparison.Ordinal))
            {
                await RespondToClientTerminal(
                    manager,
                    request,
                    new Core.ProcessResult { exitCode = -1, stderr = "Client session is no longer active." },
                    0,
                    "denied",
                    "inactive_session");
                return;
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            Core.ProcessResult result;
            try
            {
                result = await service.ExecuteAsync(
                    request.SessionId,
                    request.Command,
                    request.TimeoutMs,
                    request.Persistent);
            }
            catch (Exception ex)
            {
                result = new Core.ProcessResult
                {
                    exitCode = -1,
                    stderr = "Bridge error: " + ex.Message
                };
            }
            stopwatch.Stop();

            await RespondToClientTerminal(
                manager,
                request,
                result,
                stopwatch.ElapsedMilliseconds,
                result != null && result.timedOut ? "timed_out" : "completed",
                result != null && result.timedOut ? "timeout" : null);
        }

        private static async Task RespondToClientTerminal(
            HermesSessionManager manager,
            TerminalExecuteRequest request,
            Core.ProcessResult result,
            long durationMs,
            string status,
            string errorCode)
        {
            if (manager == null || request == null)
                return;

            try
            {
                await manager.RespondToTerminal(
                    request.RequestId,
                    result ?? new Core.ProcessResult { exitCode = -1, stderr = "No execution result." },
                    durationMs,
                    status,
                    errorCode);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Terminal] Failed to respond to terminal RPC: " + ex.Message);
            }
        }

        // Answer a terminal.read.request (read_terminal tool). The backend blocks on the response,
        // so we always reply. We deliberately DON'T spin up the terminal controller here: if the
        // user never opened the terminal there is no live pane, so we answer with empty text
        // (Desktop returns '' the same way) rather than launching a PTY the user didn't ask for.
        private async void HandleRemoteTerminalRead(TerminalReadRequest request)
        {
            if (request == null)
                return;

            string text = string.Empty;
            if (_terminalController != null)
            {
                try
                {
                    text = _terminalController.ReadScreenJson(request.Start, request.Count) ?? string.Empty;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[Terminal] read_terminal serialize failed: " + ex.Message);
                    text = string.Empty;
                }
            }

            var selector = Core.GlobalBackendSelector.Instance;
            if (selector != null && selector.SessionManager != null)
            {
                try
                {
                    await selector.SessionManager.RespondToTerminalRead(request.RequestId, text);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[Terminal] Failed to respond to terminal.read RPC: " + ex.Message);
                }
            }
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

        // Navigation callbacks moved to NavigationController

        // ShowChat, ShowAvatars, ShowProviders, ShowHistory, ShowThemes, ShowSettings
        // moved to NavigationController

        private string GetChatTitle()
        {
            return !string.IsNullOrWhiteSpace(_currentSessionTitle)
                ? _currentSessionTitle
                : LocalizationExtensions.Get("chat.new", "Новый чат");
        }


        private void ShowArea(VisualElement visible) => _layoutController.ShowArea(visible);

        private void SetTopbar(string title, string subtitle)
        {
            if (_topbarTitle != null)
                _topbarTitle.text = title;

            // On phone the subtitle + separator are dropped to keep the topbar uncluttered
            // (handled here, not in USS, because the display is set inline which beats USS).
            bool hasSubtitle = !string.IsNullOrWhiteSpace(subtitle) && !_layoutController.IsPhone;
            if (_topbarSubtitle != null)
            {
                _topbarSubtitle.text = subtitle ?? string.Empty;
                _topbarSubtitle.style.display = hasSubtitle ? DisplayStyle.Flex : DisplayStyle.None;
            }

            SetDisplay(_topbarSep, hasSubtitle ? DisplayStyle.Flex : DisplayStyle.None);
        }

        private void SetChatModelPickerVisible(bool visible)
        {
            if (_providersController == null)
                return;

            if (visible)
                _providersController.ShowTopbarModelPicker();
            else
                _providersController.HideTopbarModelPicker();
        }

        private static void SetDisplay(VisualElement element, DisplayStyle display)
        {
            if (element != null)
                element.style.display = display;
        }

        private void OnMobileMenuClicked()
        {
            _layoutController.ToggleDrawer();
        }


        private void OnMoreClicked()
        {
            _navigationController.ShowSettings();
        }

        private void OnHistoryPanelNewSessionClicked()
        {
            _ = _chatController.StartNewSessionAsync();
        }

        // ===== ChatController wrappers (thin delegates) =====

        private void RenderMessages(IReadOnlyList<ChatMessage> messages)
        {
            _chatController.RenderMessages(messages);
        }

        private void AddSystemMessage(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            // Add as a system-role message in the chat transcript
            if (_messagesList != null)
            {
                var msg = new ChatMessage { role = "system", content = text };
                _messagesList.Add(ChatMessageListRenderer.CreateMessageElement(msg));
                // Scroll to bottom
                var content = _messagesList.contentContainer;
                if (content != null && content.childCount > 0)
                    _messagesList.ScrollTo(content[content.childCount - 1]);
            }
        }

        private void SetSending(bool isSending)
        {
            // Delegated to ChatController; keep wrapper for rare external calls
        }

        private async System.Threading.Tasks.Task SendCurrentMessageAsync()
        {
            await _chatController.SendCurrentMessageAsync();
        }


        // ===== SessionHistoryController wrappers =====

        private void ShowHistoryState(string message, bool isError)
        {
            if (_historyState == null)
                return;
            _historyState.text = message ?? string.Empty;
            bool hasMessage = !string.IsNullOrWhiteSpace(_historyState.text);
            SetDisplay(_historyState, hasMessage ? DisplayStyle.Flex : DisplayStyle.None);
            _historyState.EnableInClassList("history-panel__state--error", hasMessage && isError);
        }

        private void OnHistorySearchToggled() => _sessionHistoryController.OnHistorySearchToggled();
        private void OnHistorySearchCleared() => _sessionHistoryController.OnHistorySearchCleared();
        private void OnHistorySearchChanged(ChangeEvent<string> evt) => _sessionHistoryController.OnHistorySearchChanged(evt);
        private System.Threading.Tasks.Task RefreshSessionsFromCacheAsync() => _sessionHistoryController.RefreshSessionsFromCacheAsync();
        private System.Threading.Tasks.Task LoadSessionsAsync(ChatService chat) => _sessionHistoryController.LoadSessionsAsync(chat);
        private void RenderSessionList(System.Collections.Generic.IReadOnlyList<NeonCompanion.Runtime.Data.Models.ChatSession> allSessions, System.Collections.Generic.List<NeonCompanion.Runtime.Data.Models.ProviderConfig> providers) => _sessionHistoryController.RenderSessionList(new System.Collections.Generic.List<NeonCompanion.Runtime.Data.Models.ChatSession>(allSessions), providers);

        private void OnToggleLeftPanel() => _layoutController.OnToggleLeftPanel();

        private void OnToggleRightPanel() => _layoutController.OnToggleRightPanel();

        private void UpdatePanelToggleTooltips() => _layoutController.UpdatePanelToggleTooltips();

        // The clicked assistant message to attach the next synthesized clip to (headphones button).
        // Null means "the latest assistant message" (auto-TTS of a fresh response).
        private ChatMessage _ttsTargetMessage;
        private ChatMessage _ttsBusyMessage;

        private void OnListenMessage(ChatMessage message)
        {
            if (message == null)
                return;

            // Already have a cached clip for this message → just replay it, no re-synthesis.
            if (!string.IsNullOrEmpty(message.audioPath) && System.IO.File.Exists(message.audioPath))
            {
                _voiceController.ToggleMessageAudio(message.audioPath);
                return;
            }

            if (_ttsBusyMessage != null || _voiceController.IsVoicePlaying)
                return;

            _ttsBusyMessage = message;

            string text = ChatMessageListRenderer.BuildMessageCopyText(message);
            if (string.IsNullOrWhiteSpace(text))
            {
                ClearTtsBusyState();
                return;
            }

            _ttsTargetMessage = message;
            message.voiceOutputBusy = true;
            RenderMessages(_chatService?.CurrentChatViewModel?.Messages);
            if (!_voiceController.EnqueueVoiceResponse(text))
            {
                _ttsTargetMessage = null;
                ClearTtsBusyState();
            }
        }

        private void ClearTtsBusyState()
        {
            if (_ttsBusyMessage == null)
                return;

            _ttsBusyMessage.voiceOutputBusy = false;
            _ttsBusyMessage = null;
            _ttsTargetMessage = null;
            RenderMessages(_chatService?.CurrentChatViewModel?.Messages);
        }

        private void OnTtsPlaybackStarted(string text)
        {
            _companionWindowController.StartVoicePlayback(text);
            if (_ttsBusyMessage == null || !_ttsBusyMessage.voiceOutputBusy)
                return;

            _ttsBusyMessage.voiceOutputBusy = false;
            RenderMessages(_chatService?.CurrentChatViewModel?.Messages);
        }

        private void TriggerAvatarSmile()
        {
            _avatarGalleryController.TriggerAvatarSmile();
            _companionWindowController.TriggerReaction("smile");
        }

        private void TriggerAvatarConfused()
        {
            _avatarGalleryController.TriggerAvatarConfused();
            _companionWindowController.TriggerReaction("confused");
        }

        private void OnTtsPlaybackCompleted()
        {
            _companionWindowController.ClearVoicePlayback();
            ClearTtsBusyState();
        }

        private async Task EnsureVoicePipelineAsync(ChatService chat) => await _voiceController.EnsureVoicePipelineAsync(chat);

        // Attach a synthesized TTS clip to the most recent assistant message so it shows a
        // replayable voice bubble (cached — replay doesn't re-run TTS).
        private void AttachAssistantAudio(string audioPath, float durationSecs)
        {
            if (string.IsNullOrEmpty(audioPath))
                return;

            var messages = _chatService?.CurrentChatViewModel?.Messages;
            if (messages == null)
                return;

            // Headphones on a specific (possibly older) message → attach to that one.
            ChatMessage target = _ttsTargetMessage;
            _ttsTargetMessage = null;
            if (target != null)
            {
                target.audioPath = audioPath;
                target.audioDurationSecs = durationSecs;
                target.voiceOutputBusy = false;
                RenderMessages(messages);
                return;
            }

            // Auto-TTS of a fresh response → attach to the latest assistant message.
            for (int i = messages.Count - 1; i >= 0; i--)
            {
                ChatMessage m = messages[i];
                if (m != null && string.Equals(m.role, "assistant", StringComparison.OrdinalIgnoreCase))
                {
                    m.audioPath = audioPath;
                    m.audioDurationSecs = durationSecs;
                    RenderMessages(messages);
                    return;
                }
            }
        }

        private void OnVoiceRecordingStarted() => _voiceController.OnVoiceRecordingStarted();

        private async Task<bool> SendVoiceMessageAsync(string text, string audioPath)
        {
            if (string.IsNullOrWhiteSpace(text) || _chatController.IsSending)
                return false;

            await _chatController.SendDirectVoiceMessageAsync(text.Trim(), audioPath ?? "");
            return true;
        }

        private bool IsVoiceEnabledBySettings()
        {
            return _settingsController.VoiceIoEnabled;
        }

        // Auto-voice a response only when voice is enabled AND (always-voice mode is on OR the
        // user's last message was itself a voice message — reply in kind, Telegram-style).
        private bool ShouldAutoVoiceResponse()
        {
            if (!_settingsController.VoiceIoEnabled)
                return false;
            if (_settingsController.VoiceAlwaysReply)
                return true;

            var messages = _chatService?.CurrentChatViewModel?.Messages;
            if (messages == null)
                return false;

            for (int i = messages.Count - 1; i >= 0; i--)
            {
                ChatMessage m = messages[i];
                if (m == null)
                    continue;
                if (string.Equals(m.role, "user", StringComparison.OrdinalIgnoreCase))
                    return !string.IsNullOrEmpty(m.audioPath);
            }
            return false;
        }

        private void RefreshVoiceControls()
        {
            _ = RefreshVoiceControlsAsync();
        }

        private async Task RefreshVoiceControlsAsync()
        {
            ChatService chat = _chatService;
            if (chat == null)
                chat = await GetChatServiceAsync();

            if (chat != null)
                await _voiceController.EnsureVoicePipelineAsync(chat);

            _voiceController.RefreshVoiceControls();
        }

        private void BindVoiceAnimationEvents() => _voiceController.BindVoiceAnimationEvents();

        private void UnbindVoiceAnimationEvents() => _voiceController.UnbindVoiceAnimationEvents();

        private async Task RefreshAsync()
        {
            try
            {
                var app = await GetAppAsync();
                if (!_isBound || app == null) return;

                _avatarGalleryController.RefreshCustomAvatarGallery(app);

                // Применяем Safe Area и платформенные классы к app-root (PL-04).
                _layoutController.ApplyPlatformLayout(app.Services.GetRequired<IPlatformInfoService>());
                await _settingsController.BindLocalizationEventsAsync();
                IDonationService donationService = null;
                app.Services.TryGet(out donationService);
                _settingsController.SetDonationService(donationService);

                var providers = await app.ProviderManager.GetAllProvidersAsync();
                if (providers.Count == 0)
                {
                    SetNoProviderState();
                    return;
                }

                var chat = await GetChatServiceAsync();
                if (!_isBound || chat == null) return;
                await chat.GetOrCreateChatAsync();
                await EnsureVoicePipelineAsync(chat);

                _providersController.SetProviderHeader(chat.CurrentProvider, chat.CurrentSessionModel);
                RenderMessages(chat.CurrentChatViewModel?.Messages);
                await _sessionHistoryController.LoadSessionsAsync(chat);
                await _providersController.RefreshProvidersListAsync();
            }
            catch (Exception ex)
            {
                NeonLogger.LogError(ex.ToString());
            }
        }

        private void SetNoProviderState()
        {
            AddSystemMessage(LocalizationExtensions.Get("provider.not_configured.hint", "Провайдер не настроен. Перейди в Провайдеры и добавь API-ключ."));
            _providersController.ClearProviderHeader();
            if (_sendButton != null)
                _sendButton.SetEnabled(false);
        }

        private async Task<CompanionApp> GetAppAsync()
        {
            if (_app != null)
                return _app;

            for (int i = 0; i < 120 && isActiveAndEnabled; i++)
            {
                var bootstrap = UnityEngine.Object.FindAnyObjectByType<AppBootstrap>();
                if (bootstrap != null)
                {
                    await bootstrap.InitializationTask;
                    if (bootstrap.App != null)
                    {
                        _app = bootstrap.App;
                        return _app;
                    }
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

            if (!_sessionStatesSubscribed)
            {
                _chatService.OnSessionStatesChanged += OnSessionStatesChanged;
                _chatService.OnSessionTitleChanged += OnSessionTitleChanged;
                _sessionStatesSubscribed = true;
            }

            var settingsSnap = app.Settings.Load();
            if (settingsSnap != null)
                _chatService.SaveChatHistory = settingsSnap.saveChatHistory;

            if (string.IsNullOrEmpty(_currentSessionId))
                _currentSessionId = _chatService.CurrentSessionId ?? string.Empty;
            return _chatService;
        }

        private void UpdateScrollBottomButton()
        {
            if (_scrollBottomBtn == null || _messagesList == null)
                return;
            if (_chatPanel == null || _chatPanel.style.display == DisplayStyle.None)
            {
                SetDisplay(_scrollBottomBtn, DisplayStyle.None);
                return;
            }

            var content = _messagesList.contentContainer;
            var viewport = _messagesList.contentViewport;
            if (content == null || viewport == null)
                return;

            float viewportHeight = viewport.worldBound.height;
            float contentHeight = content.worldBound.height;
            if (viewportHeight <= 0f || contentHeight <= 0f)
            {
                SetDisplay(_scrollBottomBtn, DisplayStyle.None);
                return;
            }

            float maxScroll = Mathf.Max(0f, contentHeight - viewportHeight);
            float scrollY = _messagesList.scrollOffset.y;
            bool isAtBottom = maxScroll <= 1f || scrollY >= maxScroll - 1f;
            SetDisplay(_scrollBottomBtn, isAtBottom ? DisplayStyle.None : DisplayStyle.Flex);
        }


        private void ApplyLocalizedStaticTexts()
        {
            _navChatLabel?.Localize("tab.chat");
            _navAvatarsLabel?.Localize("tab.avatar");
            _navProvidersLabel?.Localize("settings.providers");
            _navHistoryLabel?.Localize("chat.history");
            _navThemesLabel?.Localize("settings.themes");
            _navSettingsLabel?.Localize("tab.settings");
            _navCloseLabel?.Localize("nav.close");
            UpdatePanelToggleTooltips();
            ApplyStaticTemplateLocalization();
        }

        private async Task RefreshLocalizedUiAsync()
        {
            if (!_isBound || _isRefreshingLocalizedUi)
                return;

            _isRefreshingLocalizedUi = true;
            try
            {
                ApplyLocalizedStaticTexts();
                ApplyStaticTemplateLocalization();

                _settingsController.RefreshLanguageDropdown();

                string activeAvatarId = _avatarGalleryController.ActiveAvatarId;

                _avatarGalleryController.RefreshPreviewPersonaText(activeAvatarId);
                _avatarGalleryController.UpdatePersonaStateUi(activeAvatarId);
                _avatarGalleryController.UpdateAvatarActionButtons(activeAvatarId);
                _avatarGalleryController.RefreshBuiltInAvatarTileLabels();
                _avatarGalleryController.UpdateAvatarFilterCounts();
                _companionWindowController.RefreshLocalizedUi();
                _settingsController.UpdateClearDataButtonState();

                var app = await GetAppAsync();
                if (!_isBound)
                    return;

                if (app != null)
                    _settingsController.RefreshPluginStatus(app);

                var chat = await GetChatServiceAsync();
                if (!_isBound)
                    return;

                if (chat != null)
                {
                    RenderMessages(chat.CurrentChatViewModel?.Messages);
                    await LoadSessionsAsync(chat);
                }

                await _providersController.RefreshProvidersListAsync();
                _providersController.UpdateEditorStatus();
                UpdateCurrentTopbarTexts();
            }
            finally
            {
                _isRefreshingLocalizedUi = false;
            }
        }

        private void UpdateCurrentTopbarTexts()
        {
            if (_chatPanel != null && _chatPanel.style.display != DisplayStyle.None)
            {
                SetTopbar(GetChatTitle(), _chatController.ChatSubtitle);
                return;
            }

            if (_avatarsPanel != null && _avatarsPanel.style.display != DisplayStyle.None)
            {
                int total = _avatarGalleryController.GetAvatarTotalCount();
                string activeAvatarId = _avatarGalleryController.ActiveAvatarId;
                SetTopbar(LocalizationExtensions.Get("topbar.avatars.title", "Аватары"),
                    LocalizationExtensions.GetFormat("topbar.avatars.subtitle", "{0} образов · {1}", total, _avatarGalleryController.AvatarDisplayName(activeAvatarId)));
                return;
            }

            if (_providersPanel != null && _providersPanel.style.display != DisplayStyle.None)
            {
                SetTopbar(LocalizationExtensions.Get("topbar.providers.title", "Провайдеры"), LocalizationExtensions.Get("topbar.providers.subtitle", "Провайдеры приложения"));
                return;
            }

            if (_historyPanel != null && _historyPanel.style.display != DisplayStyle.None)
            {
                SetTopbar(LocalizationExtensions.Get("topbar.history.title", "История чатов"),
                    string.IsNullOrWhiteSpace(_chatController.SessionSearchQuery)
                        ? LocalizationExtensions.Get("topbar.history.subtitle.saved", "Сохранённые сессии")
                        : LocalizationExtensions.GetFormat("topbar.history.subtitle.search", "Поиск: {0}", _chatController.SessionSearchQuery));
                return;
            }

            if (_themesPanel != null && _themesPanel.style.display != DisplayStyle.None)
            {
                SetTopbar(LocalizationExtensions.Get("topbar.themes.title", "Темы"), LocalizationExtensions.Get("topbar.themes.subtitle", "Палитра акцента, форма, ореол и дыхание"));
                return;
            }

            if (_settingsPanel != null && _settingsPanel.style.display != DisplayStyle.None)
                SetTopbar(LocalizationExtensions.Get("topbar.settings.title", "Настройки"), string.Empty);
        }

        private void ApplyStaticTemplateLocalization()
        {
            if (_root == null)
                return;

            var labels = _root.Query<Label>().ToList();
            for (int i = 0; i < labels.Count; i++)
            {
                string localized = ResolveStaticTemplateText(labels[i].text);
                if (!string.Equals(localized, labels[i].text, StringComparison.Ordinal))
                    labels[i].text = localized;
            }

            var buttons = _root.Query<Button>().ToList();
            for (int i = 0; i < buttons.Count; i++)
            {
                string localizedText = ResolveStaticTemplateText(buttons[i].text);
                if (!string.Equals(localizedText, buttons[i].text, StringComparison.Ordinal))
                    buttons[i].text = localizedText;

                string localizedTooltip = ResolveStaticTemplateText(buttons[i].tooltip);
                if (!string.Equals(localizedTooltip, buttons[i].tooltip, StringComparison.Ordinal))
                    buttons[i].tooltip = localizedTooltip;
            }

            var foldouts = _root.Query<Foldout>().ToList();
            for (int i = 0; i < foldouts.Count; i++)
            {
                string localized = ResolveStaticTemplateText(foldouts[i].text);
                if (!string.Equals(localized, foldouts[i].text, StringComparison.Ordinal))
                    foldouts[i].text = localized;
            }
        }

        private static string ResolveStaticTemplateText(string currentText)
        {
            if (string.IsNullOrWhiteSpace(currentText))
                return currentText;

            string candidate = currentText.Trim();
            if (!StaticTemplateTextToKey.TryGetValue(candidate, out var key))
                return currentText;

            return LocalizationExtensions.Get(key, currentText);
        }

        // U-40: runtime-generated notification beep (no audio assets; real tone via PCM)
        private void PlayNotificationBeep()
        {
            try
            {
                if (_notifySource == null)
                {
                    _notifySource = gameObject.GetComponent<AudioSource>();
                    if (_notifySource == null)
                        _notifySource = gameObject.AddComponent<AudioSource>();
                    _notifySource.playOnAwake = false;
                    _notifySource.volume = 0.15f;
                }

                if (_notifyClip == null)
                    _notifyClip = CreateNotificationClip();

                if (_notifyClip != null)
                    _notifySource.PlayOneShot(_notifyClip, 0.2f);
            }
            catch { /* silent fail on unsupported platforms */ }
        }

        private static AudioClip CreateNotificationClip()
        {
            int sampleRate = 44100;
            float duration = 0.08f; // short ding
            int samples = (int)(sampleRate * duration);
            float[] data = new float[samples];
            float freq = 880f; // A5
            for (int i = 0; i < samples; i++)
            {
                float t = (float)i / sampleRate;
                float env = 1f - (t / duration); // linear decay
                data[i] = Mathf.Sin(2f * Mathf.PI * freq * t) * 0.6f * env;
            }
            var clip = AudioClip.Create("notif_beep", samples, 1, sampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

    }
}
