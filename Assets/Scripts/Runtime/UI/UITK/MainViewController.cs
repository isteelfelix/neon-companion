using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Avatar;
using NeonCompanion.Runtime.Avatar3D;
using NeonCompanion.Runtime.Chat;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Donation;
using NeonCompanion.Runtime.Localization;
using NeonCompanion.Runtime.Platform;
using NeonCompanion.Runtime.Plugins;
using NeonCompanion.Runtime.UI.Avatars;
using NeonCompanion.Runtime.Voice;
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
        private Label _navProvidersCount;

        private VisualElement _root;

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
        private Toggle _settingsVoiceIo;
        private Toggle _settingsEncryptKeys;
        private Toggle _settingsMaskLogs;
        private Label _settingsStoragePath;
        private Label _settingsVersion;
        private Label _settingsPluginsSummary;
        private Label _settingsPluginsConfig;
        private VisualElement _settingsPluginsList;
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
            ["neon"] = new BuiltInAvatarMeta("avatar.builtin.neon.name", "Неон", "avatar.builtin.neon.style", "стандартный", "avatar.builtin.neon.persona", "Неон — спокойный и практичный AI-компаньон разработчика. Отвечает кратко, структурно и по делу.", AvatarFilter.Standard),
            ["aurora"] = new BuiltInAvatarMeta("avatar.builtin.aurora.name", "Аврора", "avatar.builtin.aurora.style", "прохладный · градиент", "avatar.builtin.aurora.persona", "Аврора — спокойная и аналитичная. Объясняет ясно и сначала обдумывает ответ.", AvatarFilter.Gradient),
            ["ember"] = new BuiltInAvatarMeta("avatar.builtin.ember.name", "Эмбер", "avatar.builtin.ember.style", "тёплый · градиент", "avatar.builtin.ember.persona", "Эмбер — тёплая и эмпатичная. Улавливает настроение и отвечает бережно.", AvatarFilter.Gradient),
            ["glass"] = new BuiltInAvatarMeta("avatar.builtin.glass.name", "Гласс", "avatar.builtin.glass.style", "минимал · тёмный", "avatar.builtin.glass.persona", "Гласс — энергичная и смелая. Любит сложные задачи и быстрый темп.", AvatarFilter.Minimal),
            ["flora"] = new BuiltInAvatarMeta("avatar.builtin.flora.name", "Флора", "avatar.builtin.flora.style", "природный · градиент", "avatar.builtin.flora.persona", "Флора — вдумчивая и спокойная. Даёт нюансированные и сбалансированные ответы.", AvatarFilter.Gradient),
            ["mono"] = new BuiltInAvatarMeta("avatar.builtin.mono.name", "Моно", "avatar.builtin.mono.style", "минимал · монохром", "avatar.builtin.mono.persona", "Моно — точная и эффективная. Ценит корректность и краткость.", AvatarFilter.Minimal),
            ["cobalt"] = new BuiltInAvatarMeta("avatar.builtin.cobalt.name", "Кобальт", "avatar.builtin.cobalt.style", "смелый · градиент", "avatar.builtin.cobalt.persona", "Кобальт — креативная и изобретательная. Любит исследовать идеи и связи.", AvatarFilter.Gradient),
            ["rose"] = new BuiltInAvatarMeta("avatar.builtin.rose.name", "Роуз", "avatar.builtin.rose.style", "мягкий · градиент", "avatar.builtin.rose.persona", "Роуз — обаятельная и общительная. Делает диалог более личным.", AvatarFilter.Gradient)
        };
        private enum AvatarViewMode { Static, Animated, Volume3D }
        private AvatarViewMode _avatarViewMode = AvatarViewMode.Static;

        private Button _viewModeStaticBtn;
        private Button _viewModeAnimatedBtn;
        private Button _viewMode3DBtn;
        private VisualElement _avatarFilterRow;
        private VisualElement _galleryStatic;
        private VisualElement _galleryAnimated;
        private VisualElement _gallery3D;
        private VisualElement _avtileNeonAnimated;

        private string _activeAvatarId = "neon";
        private AvatarFilter _activeAvatarFilter = AvatarFilter.All;
        private VisualElement _avatarArt;
        private VisualElement _avatarCircle;
        private VisualElement _avatarStageHero;
        private VisualElement _avatarGlow;
        private VisualElement _avatarShade;
        private Label _avatarLetter;
        private VisualElement _previewHero;
        private Label _previewTitle;
        private Label _previewTag;
        private Label _previewPersona;
        private Label _previewAnimationInfo;
        private Label _previewPersonaStateBadge;
        private Label _previewPersonaStateHelp;
        private VisualElement _previewPersonaStateRow;
        private Label _streamingLabel;
        private Button _previewApplyBtn;
        private Button _previewEditPersonaBtn;
        private Button _previewResetPersonaBtn;
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
        private AvatarCustomizationPanel _avatarCustomizationPanel;
        private AvatarCustomizationData _activeCustomizationBaseline;
        private Label _avatarEmojiOverlay;
        private Label _previewEmojiOverlay;

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
        private Label _editorProviderStatus;

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
        private string _sessionSearchQuery = string.Empty;

        private ScrollView _providersList;
        private Button _addProviderButton;
        private Button _saveProviderButton;
        private Button _cancelEditButton;
        private Button _importProviderButton;
        private Button _copyButton;
        private Button _regenerateButton;
        private Button _listenButton;
        private Button _micButton;
        private Button _attachButton;
        private Button _avatarUploadBtn;
        private Button _avatarOpenFolderBtn;
        private VisualElement _avatarUploadTile;
        private TextField _editName;
        private TextField _editBaseUrl;
        private TextField _editApiKey;
        private TextField _editModel;
        private DropdownField _editModelPreset;
        private VisualElement _editModelCustomWrap;
        private TextField _editMaxTokens;
        private Slider _editTemperature;
        private VisualElement _providerEditPanel;
        private readonly Dictionary<string, string> _modelPresetByLabel = new Dictionary<string, string>();
        private bool _syncingModelPresetUi;
        private string _lastCustomModel = string.Empty;
        private bool _editModelUsesCustomMode;

        private CompanionApp _app;
        private ChatService _chatService;
        private ProviderConfig _editingProvider;
        private ProviderConfig _editingProviderSource;
        private bool _cancelPending;
        private bool _clearDataConfirmPending;
        private long _clearDataConfirmExpiresAtMs;
        private string _chatSubtitle = string.Empty;
        private string _currentSessionId = string.Empty;
        private string _currentSessionTitle = string.Empty;
        private bool _isBound;
        private bool _isSending;
        private Image _avatarArtImage;
        private Image _avatar3DImage;
        private SpriteSheetAnimator _avatarAnimator;
        private Avatar3DRenderer _avatar3DRenderer;
        private IAvatar3DService _avatar3DService;
        private IVoiceService _voiceService;
        private VoiceInputManager _voiceInputManager;
        private VoiceOutputManager _voiceOutputManager;
        private IDonationService _donationService;
        private ILocalizationService _localizationService;
        private bool _isRefreshingLocalizedUi;
        private bool _voiceBoundToChat;
        private AvatarMotionState _avatarMotionState = AvatarMotionState.Idle;
        private bool _isVoicePlaying;
        private bool _isVoiceRecording;

        private enum AvatarMotionState
        {
            Idle,
            Thinking,
            Talking,
            Listening
        }

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
            Add("providers.page.subtitle", "Подключи любой OpenAI-совместимый API и переключайся прямо из чата.", "Connect any OpenAI-compatible API and switch right from chat.");
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
            Add("avatars.section.params", "Параметры", "Parameters");
            Add("avatars.param.animation", "Анимация", "Animation");
            Add("avatars.param.halo", "Ореол", "Halo");
            Add("avatars.param.pulse", "Пульс речи", "Speech pulse");
            Add("avatars.param.pulse.auto", "авто", "auto");
            Add("avatars.persona.label", "Инструкции аватара", "Avatar instructions");
            Add("avatars.customization", "Кастомизация", "Customization");
            Add("avatars.color.primary", "Основной цвет", "Primary color");
            Add("avatars.color.accent", "Акцент", "Accent");
            Add("avatars.color.halo", "Ореол", "Halo");
            Add("avatars.emoji", "Эмодзи", "Emoji");
            Add("avatars.frame", "Рамка", "Frame");

            Add("themes.page.title", "Темы", "Themes");
            Add("themes.page.subtitle", "Форма и поведение аватара в интерфейсе. Без смены персоны — только визуальный режим.", "Avatar shape and behavior in UI. Visual mode only, no persona changes.");
            Add("themes.hero.title", "Визуальная подача Neon", "Neon visual style");
            Add("themes.hero.subtitle", "Собери внешний вид аватара: форма, halo и breathing. Изменения применяются сразу и сохраняются локально.", "Tune avatar look: shape, halo, and breathing. Changes apply instantly and are saved locally.");
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
            Add("themes.next.note", "Следующим большим куском сюда можно вынести палитры, реактивные состояния и дополнительные пресеты отображения.", "Next step: move palettes, reactive states, and extra display presets here.");

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

            return map;
        }

        private void OnEnable()
        {
            var document = GetComponent<UIDocument>();
            if (document == null || document.rootVisualElement == null)
                return;

            Bind(document.rootVisualElement);
            RegisterCallbacks();
            _ = BindLocalizationEventsAsync();
            ShowChat();

            _ = RefreshAsync();
        }

        private void OnDisable()
        {
            UnregisterCallbacks();
            UnbindVoiceAnimationEvents();
            _isSending = false;
            _avatarMotionState = AvatarMotionState.Idle;
            if (_voiceBoundToChat && _chatService != null && _voiceOutputManager != null)
                _voiceOutputManager.UnbindChat(_chatService);
            _voiceBoundToChat = false;
            UnbindLocalizationEvents();
            _avatarAnimator?.Stop();
            _avatar3DService?.Unload();
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
            _historyPanel = root.Q<VisualElement>("history-panel");
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
            _editorProviderStatus = root.Q<Label>("editor-provider-status");

            _messageInput = root.Q<TextField>("message-input");
            _sendButton = root.Q<Button>("send-button");
            _summarizeButton = root.Q<Button>("summarize-btn");
            _searchButton = root.Q<Button>("search-btn");
            _moreButton = root.Q<Button>("more-btn");
            _newSessionButton = root.Q<Button>("new-session-btn");
            _messagesList = root.Q<ScrollView>("messages-list");
            _sessionsList = root.Q<ScrollView>("sessions-list");
            _historySessionsList = root.Q<ScrollView>("history-panel-sessions-list");
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

            _providersList = root.Q<ScrollView>("providers-list");
            _addProviderButton    = root.Q<Button>("add-provider-btn");
            _importProviderButton = root.Q<Button>("import-provider-btn");
            _saveProviderButton   = root.Q<Button>("save-provider-btn");
            _cancelEditButton     = root.Q<Button>("cancel-edit-btn");
            _copyButton       = root.Q<Button>("copy-btn");
            _regenerateButton = root.Q<Button>("refresh-btn");
            _listenButton = root.Q<Button>("listen-btn");
            _micButton = root.Q<Button>("mic-button");
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
            _editModelPreset = root.Q<DropdownField>("edit-model-preset");
            _editModelCustomWrap = root.Q<VisualElement>("edit-model-custom-wrap");
            _editMaxTokens = root.Q<TextField>("edit-maxtokens");
            _editTemperature = root.Q<Slider>("edit-temperature");

            ApplyLocalizedStaticTexts();

            // Settings action buttons
            _settingsOpenFolderBtn = root.Q<Button>("settings-open-folder");
            _settingsExportBtn     = root.Q<Button>("settings-export-btn");
            _settingsClearBtn      = root.Q<Button>("settings-clear-btn");
            _settingsClearBtnText  = _settingsClearBtn?.Q<Label>();
            _settingsGithubBtn     = root.Q<Button>("settings-github-btn");
            _settingsDocsBtn       = root.Q<Button>("settings-docs-btn");
            _settingsDonateBtn     = root.Q<Button>("settings-donate-btn");
            _donationService = null;
            if (_app != null)
                _app.Services.TryGet(out _donationService);
            _settingsDonateBtn?.SetEnabled(_donationService?.IsDonationSupported == true);
            _testProviderBtn = root.Q<Button>("test-provider-btn");
            _testRow         = root.Q<VisualElement>("test-row");
            _testRowLabel    = root.Q<Label>("test-row-label");

            // Settings page controls
            _settingsLanguage    = root.Q<DropdownField>("settings-language");
            _settingsHistory     = root.Q<Toggle>("settings-save-history");
            _settingsStreaming    = root.Q<Toggle>("settings-streaming");
            _settingsSystemPrompt = root.Q<Toggle>("settings-system-prompt");
            _settingsVoiceIo = root.Q<Toggle>("settings-voice-io");
            _settingsEncryptKeys = root.Q<Toggle>("settings-encrypt-keys");
            _settingsMaskLogs    = root.Q<Toggle>("settings-mask-logs");
            _settingsStoragePath = root.Q<Label>("settings-storage-path");
            _settingsVersion     = root.Q<Label>("settings-version");
            _settingsPluginsSummary = root.Q<Label>("settings-plugins-summary");
            _settingsPluginsConfig = root.Q<Label>("settings-plugins-config");
            _settingsPluginsList = root.Q<VisualElement>("settings-plugins-list");
            _brandVersion       = root.Q<Label>("brand-version");
            _shapeRound  = root.Q<Button>("shape-round");
            _shapeSquare = root.Q<Button>("shape-square");
            _shapeHex    = root.Q<Button>("shape-hex");
            _settingsShowHalo   = root.Q<Toggle>("settings-show-halo");
            _settingsBreathing  = root.Q<Toggle>("settings-breathing");

            // Avatar elements
            _avatarArt       = root.Q<VisualElement>("avatar-art");
            _avatarCircle    = root.Q<VisualElement>("avatar-circle");
            _avatarStageHero = root.Q<VisualElement>("avatar-stage-hero");
            _avatarGlow      = root.Q<VisualElement>("avatar-glow");
            _avatarShade     = _avatarCircle?.Q<VisualElement>(className: "avatar__shade");
            _avatarLetter    = _avatarCircle?.Q<Label>(className: "avatar__letter");
            _previewHero  = root.Q<VisualElement>("preview-hero");
            _themesPreviewHalo   = root.Q<VisualElement>("themes-preview-halo");
            _themesPreviewAvatar = root.Q<VisualElement>("themes-preview-avatar");
            _previewTitle   = root.Q<Label>("preview-title");
            _previewTag     = root.Q<Label>("preview-tag");
            _previewPersona = root.Q<Label>("preview-persona");
            _previewAnimationInfo = root.Q<Label>("preview-animation-info");
            _previewPersonaStateBadge = root.Q<Label>("preview-persona-state-badge");
            _previewPersonaStateHelp = root.Q<Label>("preview-persona-state-help");
            _previewPersonaStateRow = root.Q<VisualElement>("preview-persona-state-row");
            _previewApplyBtn      = root.Q<Button>("preview-apply-btn");
            _previewEditPersonaBtn = root.Q<Button>("preview-edit-persona-btn");
            _previewResetPersonaBtn = root.Q<Button>("preview-reset-persona-btn");
            _previewDeleteAvatarBtn = root.Q<Button>("preview-delete-avatar-btn");
            _previewPersonaLabel  = root.Q<Label>("preview-persona-label");
            _previewActionsRow    = root.Q<VisualElement>("preview-actions-row");
            _personaEditorPanel   = root.Q<VisualElement>("persona-editor-panel");
            _personaEditField     = root.Q<TextField>("persona-edit-field");
            _personaSaveBtn       = root.Q<Button>("persona-save-btn");
            _personaCancelBtn     = root.Q<Button>("persona-cancel-btn");
            _viewModeStaticBtn   = root.Q<Button>("viewmode-static-btn");
            _viewModeAnimatedBtn = root.Q<Button>("viewmode-animated-btn");
            _viewMode3DBtn       = root.Q<Button>("viewmode-3d-btn");
            _avatarFilterRow     = root.Q<VisualElement>("avatar-filterrow");
            _galleryStatic       = root.Q<VisualElement>("gallery-static");
            _galleryAnimated     = root.Q<VisualElement>("gallery-animated");
            _gallery3D           = root.Q<VisualElement>("gallery-3d");
            _avtileNeonAnimated  = root.Q<VisualElement>("avtile-neon-animated");
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
            EnsureCustomizationOverlayElements();
            if (_avatarCustomizationPanel == null)
            {
                var customizationRoot = root.Q<VisualElement>("avatar-customization-foldout");
                if (customizationRoot != null)
                {
                    _avatarCustomizationPanel = new AvatarCustomizationPanel(customizationRoot);
                    _avatarCustomizationPanel.Changed += OnAvatarCustomizationChanged;
                    _avatarCustomizationPanel.Saved += OnAvatarCustomizationSaved;
                    _avatarCustomizationPanel.Canceled += OnAvatarCustomizationCanceled;
                }
            }
            EnsureAvatarAnimationImage();
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
            RegisterClick(_historyPanelNewSessionButton, OnNewSessionClicked);
            RegisterClick(_historySearchBtn, OnHistorySearchToggled);
            RegisterClick(_historySearchClear, OnHistorySearchCleared);
            RegisterClick(_historyPanelSearchBtn, OnHistorySearchToggled);
            RegisterClick(_historyPanelSearchClear, OnHistorySearchCleared);
            if (_historySearchInput != null)
                _historySearchInput.RegisterCallback<ChangeEvent<string>>(OnHistorySearchChanged);
            if (_historyPanelSearchInput != null)
                _historyPanelSearchInput.RegisterCallback<ChangeEvent<string>>(OnHistorySearchChanged);
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
            if (_editModelPreset != null)
                _editModelPreset.RegisterCallback<ChangeEvent<string>>(OnModelPresetChanged);
            if (_editName != null)
                _editName.RegisterCallback<ChangeEvent<string>>(OnProviderEditorIdentityChanged);
            if (_editBaseUrl != null)
                _editBaseUrl.RegisterCallback<ChangeEvent<string>>(OnProviderEditorIdentityChanged);
            if (_editModel != null)
                _editModel.RegisterCallback<ChangeEvent<string>>(OnManualModelChanged);

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
            RegisterClick(_previewResetPersonaBtn, OnPreviewResetPersonaClicked);
            RegisterClick(_previewDeleteAvatarBtn, OnPreviewDeleteAvatarClicked);
            RegisterClick(_personaSaveBtn, OnPersonaSaveClicked);
            RegisterClick(_personaCancelBtn, OnPersonaCancelClicked);
            RegisterClick(_viewModeStaticBtn,   OnViewModeStaticClicked);
            RegisterClick(_viewModeAnimatedBtn, OnViewModeAnimatedClicked);
            RegisterClick(_viewMode3DBtn,       OnViewMode3DClicked);
            RegisterClick(_avtileNeonAnimated,  OnNeonAnimatedTileClicked);
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
            UnregisterClick(_historyPanelNewSessionButton, OnNewSessionClicked);
            UnregisterClick(_historySearchBtn, OnHistorySearchToggled);
            UnregisterClick(_historySearchClear, OnHistorySearchCleared);
            UnregisterClick(_historyPanelSearchBtn, OnHistorySearchToggled);
            UnregisterClick(_historyPanelSearchClear, OnHistorySearchCleared);
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
            UnregisterClick(_previewResetPersonaBtn, OnPreviewResetPersonaClicked);
            UnregisterClick(_previewDeleteAvatarBtn, OnPreviewDeleteAvatarClicked);
            UnregisterClick(_personaSaveBtn, OnPersonaSaveClicked);
            UnregisterClick(_personaCancelBtn, OnPersonaCancelClicked);
            UnregisterClick(_viewModeStaticBtn,   OnViewModeStaticClicked);
            UnregisterClick(_viewModeAnimatedBtn, OnViewModeAnimatedClicked);
            UnregisterClick(_viewMode3DBtn,       OnViewMode3DClicked);
            UnregisterClick(_avtileNeonAnimated,  OnNeonAnimatedTileClicked);
            UnregisterClick(_avatarFilterAllBtn, OnAvatarFilterAllClicked);
            UnregisterClick(_avatarFilterStandardBtn, OnAvatarFilterStandardClicked);
            UnregisterClick(_avatarFilterGradientBtn, OnAvatarFilterGradientClicked);
            UnregisterClick(_avatarFilterMinimalBtn, OnAvatarFilterMinimalClicked);
            UnregisterClick(_avatarFilterCustomBtn, OnAvatarFilterCustomClicked);
            if (_editModelPreset != null)
                _editModelPreset.UnregisterCallback<ChangeEvent<string>>(OnModelPresetChanged);
            if (_editName != null)
                _editName.UnregisterCallback<ChangeEvent<string>>(OnProviderEditorIdentityChanged);
            if (_editBaseUrl != null)
                _editBaseUrl.UnregisterCallback<ChangeEvent<string>>(OnProviderEditorIdentityChanged);
            if (_editModel != null)
                _editModel.UnregisterCallback<ChangeEvent<string>>(OnManualModelChanged);
            if (_settingsLanguage != null)
                _settingsLanguage.UnregisterCallback<ChangeEvent<string>>(OnSettingsLanguageChanged);

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
            SetTopbar(LocalizationExtensions.Get("topbar.avatars.title", "Аватары"),
                LocalizationExtensions.GetFormat("topbar.avatars.subtitle", "{0} образов · {1}", total, AvatarDisplayName(_activeAvatarId)));
            ShowArea(_avatarsPanel);
        }

        private void ShowProviders()
        {
            if (!CanLeaveProviderEditor())
                return;

            SetActiveNav(_navProviders);
            SetTopbar(LocalizationExtensions.Get("topbar.providers.title", "Провайдеры"), LocalizationExtensions.Get("topbar.providers.subtitle", "OpenAI-совместимые провайдеры"));
            ShowArea(_providersPanel);
            _ = RefreshProvidersListAsync();
        }

        private void ShowHistory()
        {
            if (!CanLeaveProviderEditor())
                return;

            SetActiveNav(_navHistory);
            SetTopbar(LocalizationExtensions.Get("topbar.history.title", "История чатов"),
                string.IsNullOrWhiteSpace(_sessionSearchQuery)
                    ? LocalizationExtensions.Get("topbar.history.subtitle.saved", "Сохранённые сессии")
                    : LocalizationExtensions.GetFormat("topbar.history.subtitle.search", "Поиск: {0}", _sessionSearchQuery));
            ShowArea(_historyPanel);
            _ = RefreshSessionsFromCacheAsync();
        }

        private string GetChatTitle()
        {
            return !string.IsNullOrWhiteSpace(_currentSessionTitle)
                ? _currentSessionTitle
                : LocalizationExtensions.Get("chat.new", "Новый чат");
        }

        private string GetProviderStatusText()
        {
            var provider = _chatService?.CurrentProvider;
            if (provider == null) return LocalizationExtensions.Get("provider.status.none", "нет провайдера");
            if (!string.IsNullOrWhiteSpace(provider.defaultModel))
                return $"{provider.displayName ?? LocalizationExtensions.Get("provider.short.default", "API")} · {provider.defaultModel}";
            return provider.displayName ?? LocalizationExtensions.Get("provider.status.configured", "настроен");
        }

        private void ShowThemes()
        {
            if (!CanLeaveProviderEditor())
                return;

            SetActiveNav(_navThemes);
            SetTopbar(LocalizationExtensions.Get("topbar.themes.title", "Темы"), LocalizationExtensions.Get("topbar.themes.subtitle", "Форма, ореол и дыхание для аватара"));
            ShowArea(_themesPanel);
        }

        private void ShowSettings()
        {
            if (!CanLeaveProviderEditor())
                return;

            SetActiveNav(_navSettings);
            SetTopbar(LocalizationExtensions.Get("topbar.settings.title", "Настройки"), string.Empty);
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
            SetDisplay(_historyPanel, visible == _historyPanel ? DisplayStyle.Flex : DisplayStyle.None);
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
                    AddSystemMessage(LocalizationExtensions.Get("system.app.not_initialized", "Приложение не инициализировано."));
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
                TriggerAvatarSmile();
            }
            catch (Exception ex)
            {
                AddSystemMessage(LocalizationExtensions.Get("system.chat.send_failed", "Не удалось отправить сообщение. Попробуй ещё раз."));
                NeonLogger.LogError(ex.ToString());
                TriggerAvatarConfused();
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
                _connectionStatus.text = isSending
                    ? LocalizationExtensions.Get("provider.status.generating", "генерация…")
                    : GetProviderStatusText();

            SetDisplay(_typingIndicator, isSending ? DisplayStyle.Flex : DisplayStyle.None);
            SetDisplay(_subtitleBody, isSending ? DisplayStyle.None : DisplayStyle.Flex);

            if (isSending) StartTypingAnimation();
            else StopTypingAnimation();

            RefreshAvatarMotionState();
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
                    AddSystemMessage(LocalizationExtensions.Get("system.app.not_initialized", "Приложение не инициализировано."));
                    return;
                }

                string summary = await chat.SummarizeCurrentConversationAsync();
                AddSystemMessage(summary);
            }
            catch (Exception ex)
            {
                AddSystemMessage(LocalizationExtensions.Get("system.chat.summary_failed", "Не удалось получить сводку диалога. Попробуй позже."));
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
                if (_historyPanelSearchInput != null)
                    _historyPanelSearchInput.SetValueWithoutNotify(_sessionSearchQuery);

                var chat = await GetChatServiceAsync();
                if (chat == null)
                    return;

                var allSessions = await chat.GetAllSessionsAsync();
                var app = await GetAppAsync();
                var providers = app != null ? await app.ProviderManager.GetAllProvidersAsync() : new List<ProviderConfig>();
                if (_isBound)
                    RenderSessionList(allSessions, providers);

                ShowHistory();
            }
            catch (Exception ex)
            {
                AddSystemMessage(LocalizationExtensions.Get("system.chat.search_failed", "Не удалось выполнить поиск по чатам. Попробуй ещё раз."));
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
                AddSystemMessage(LocalizationExtensions.Get("system.voice.no_assistant_reply", "Нет ответа ассистента для озвучивания."));
                return;
            }

            for (int i = messages.Count - 1; i >= 0; i--)
            {
                var msg = messages[i];
                if (msg?.role == "assistant" && !string.IsNullOrWhiteSpace(msg.content))
                {
                    _voiceOutputManager?.EnqueueResponse(msg.content);
                    return;
                }
            }

            AddSystemMessage(LocalizationExtensions.Get("system.voice.no_assistant_reply", "Нет ответа ассистента для озвучивания."));
        }

        private async Task EnsureVoicePipelineAsync(ChatService chat)
        {
            if (chat == null)
                return;

            if (_voiceService == null)
            {
                _voiceService = GetComponent<WebSpeechBridge>();
                if (_voiceService == null)
                    _voiceService = gameObject.AddComponent<WebSpeechBridge>();
            }

            if (_voiceOutputManager == null)
            {
                _voiceOutputManager = GetComponent<VoiceOutputManager>();
                if (_voiceOutputManager == null)
                    _voiceOutputManager = gameObject.AddComponent<VoiceOutputManager>();
                _voiceOutputManager.Initialize(_voiceService, IsVoiceEnabledBySettings, () => _voiceInputManager != null && _voiceInputManager.IsRecording);
            }

            if (_voiceInputManager == null)
            {
                _voiceInputManager = GetComponent<VoiceInputManager>();
                if (_voiceInputManager == null)
                    _voiceInputManager = gameObject.AddComponent<VoiceInputManager>();
                _voiceInputManager.Initialize(_voiceService, _micButton, IsVoiceEnabledBySettings, SendVoiceMessageAsync, OnVoiceRecordingStarted);
            }

            BindVoiceAnimationEvents();

            if (!_voiceBoundToChat)
            {
                _voiceOutputManager.BindChat(chat);
                _voiceBoundToChat = true;
            }

            RefreshVoiceControls();
            await Task.CompletedTask;
        }

        private void OnVoiceRecordingStarted()
        {
            _voiceOutputManager?.StopSpeakingAndClear();
        }

        private async Task SendVoiceMessageAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || _isSending)
                return;

            if (_messageInput != null)
                _messageInput.value = text.Trim();

            await SendCurrentMessageAsync();
        }

        private bool IsVoiceEnabledBySettings()
        {
            return _settingsVoiceIo?.value ?? false;
        }

        private void RefreshVoiceControls()
        {
            _voiceInputManager?.RefreshState();
            if (!IsVoiceEnabledBySettings())
            {
                _voiceOutputManager?.StopSpeakingAndClear();
                _isVoicePlaying = false;
                _isVoiceRecording = false;
                RefreshAvatarMotionState();
            }
        }

        private void BindVoiceAnimationEvents()
        {
            if (_voiceOutputManager != null)
            {
                _voiceOutputManager.OnPlaybackStarted -= HandleVoicePlaybackStarted;
                _voiceOutputManager.OnPlaybackCompleted -= HandleVoicePlaybackCompleted;
                _voiceOutputManager.OnPlaybackStarted += HandleVoicePlaybackStarted;
                _voiceOutputManager.OnPlaybackCompleted += HandleVoicePlaybackCompleted;
            }

            if (_voiceInputManager != null)
            {
                _voiceInputManager.OnRecordingStarted -= HandleVoiceRecordingStarted;
                _voiceInputManager.OnRecordingStopped -= HandleVoiceRecordingStopped;
                _voiceInputManager.OnRecordingStarted += HandleVoiceRecordingStarted;
                _voiceInputManager.OnRecordingStopped += HandleVoiceRecordingStopped;
            }
        }

        private void UnbindVoiceAnimationEvents()
        {
            if (_voiceOutputManager != null)
            {
                _voiceOutputManager.OnPlaybackStarted -= HandleVoicePlaybackStarted;
                _voiceOutputManager.OnPlaybackCompleted -= HandleVoicePlaybackCompleted;
            }

            if (_voiceInputManager != null)
            {
                _voiceInputManager.OnRecordingStarted -= HandleVoiceRecordingStarted;
                _voiceInputManager.OnRecordingStopped -= HandleVoiceRecordingStopped;
            }

            _isVoicePlaying = false;
            _isVoiceRecording = false;
        }

        private void HandleVoicePlaybackStarted(string _)
        {
            _isVoicePlaying = true;
            RefreshAvatarMotionState();
        }

        private void HandleVoicePlaybackCompleted()
        {
            _isVoicePlaying = false;
            RefreshAvatarMotionState();
        }

        private void HandleVoiceRecordingStarted()
        {
            _isVoiceRecording = true;
            RefreshAvatarMotionState();
        }

        private void HandleVoiceRecordingStopped()
        {
            _isVoiceRecording = false;
            RefreshAvatarMotionState();
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
                AddSystemMessage(LocalizationExtensions.Get("system.chat.attachment_failed", "Не удалось добавить вложение к сообщению."));
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
                await BindLocalizationEventsAsync();
                if (_donationService == null)
                    app.Services.TryGet(out _donationService);
                _settingsDonateBtn?.SetEnabled(_donationService?.IsDonationSupported == true);

                var providers = await app.ProviderManager.GetAllProvidersAsync();
                if (providers.Count == 0)
                {
                    SetNoProviderState();
                    return;
                }

                var chat = await GetChatServiceAsync();
                if (!_isBound || chat == null) return;
                await EnsureVoicePipelineAsync(chat);

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
                _subtitleBody.text = LocalizationExtensions.Get("provider.not_configured.hint", "Провайдер не настроен. Перейди в Провайдеры и добавь API-ключ.");
            if (_subtitleRole != null)
                _subtitleRole.text = LocalizationExtensions.Get("chat.role.system", "Система");
            if (_sendButton != null)
                _sendButton.SetEnabled(false);
            if (_connectionStatus != null)
                _connectionStatus.text = LocalizationExtensions.Get("provider.status.none", "нет провайдера");
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

            await _chatService.GetOrCreateChatAsync(settingsSnap?.activeProviderId);
            if (string.IsNullOrEmpty(_currentSessionId))
                _currentSessionId = _chatService.CurrentSessionId ?? string.Empty;
            return _chatService;
        }

        private async Task LoadSessionsAsync(ChatService chat)
        {
            if (_sessionsList == null && _historySessionsList == null)
                return;

            ShowHistoryState(LocalizationExtensions.Get("history.loading", "Загрузка истории…"), isError: false);
            var allSessions = await chat.GetAllSessionsAsync();
            var providers = new List<ProviderConfig>();
            var app = await GetAppAsync();
            if (app != null)
                providers = await app.ProviderManager.GetAllProvidersAsync();
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

            if (_topbarTitle != null && _chatPanel != null && _chatPanel.style.display != DisplayStyle.None)
                _topbarTitle.text = GetChatTitle();

            RenderSessionList(allSessions, providers);
        }

        private void RenderSessionList(List<ChatSession> allSessions, List<ProviderConfig> providers)
        {
            if (_sessionsList == null && _historySessionsList == null) return;

            _sessionsList?.Clear();
            _historySessionsList?.Clear();
            _sessionItems.Clear();

            var sessions = string.IsNullOrWhiteSpace(_sessionSearchQuery)
                ? allSessions
                : allSessions.FindAll(s =>
                    (s.title ?? string.Empty).IndexOf(_sessionSearchQuery, StringComparison.OrdinalIgnoreCase) >= 0);

            AddSessionHeader(_sessionsList, sessions.Count);
            AddSessionHeader(_historySessionsList, sessions.Count);

            if (sessions.Count == 0)
            {
                string emptyText = string.IsNullOrWhiteSpace(_sessionSearchQuery)
                    ? LocalizationExtensions.Get("history.empty.saved_sessions", "Сохранённых сессий пока нет.")
                    : LocalizationExtensions.Get("history.empty.search", "По этому запросу ничего не найдено.");
                var railEmpty = new Label(emptyText);
                railEmpty.AddToClassList("history__meta");
                _sessionsList?.Add(railEmpty);

                var historyEmpty = new Label(emptyText);
                historyEmpty.AddToClassList("history__meta");
                _historySessionsList?.Add(historyEmpty);
                ShowHistoryState(string.IsNullOrWhiteSpace(_sessionSearchQuery)
                    ? LocalizationExtensions.Get("history.empty.first_session", "История пуста. Начните чат, чтобы появилась первая сессия.")
                    : LocalizationExtensions.Get("history.search.try_another", "Попробуйте изменить поисковый запрос."), isError: false);
                return;
            }

            ShowHistoryState(string.Empty, isError: false);
            for (int i = 0; i < sessions.Count; i++)
            {
                bool isActive = IsActiveSession(sessions[i], i);
                var railItem = CreateSessionItem(sessions[i], isActive, providers);
                var historyItem = CreateSessionItem(sessions[i], isActive, providers);
                _sessionsList?.Add(railItem);
                _historySessionsList?.Add(historyItem);
                _sessionItems.Add(railItem);
                _sessionItems.Add(historyItem);
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
            bool railVisible = _historySearchBar != null && _historySearchBar.style.display == DisplayStyle.Flex;
            bool panelVisible = _historyPanelSearchBar != null && _historyPanelSearchBar.style.display == DisplayStyle.Flex;
            bool isVisible = railVisible || panelVisible;
            SetDisplay(_historySearchBar, isVisible ? DisplayStyle.None : DisplayStyle.Flex);
            SetDisplay(_historyPanelSearchBar, isVisible ? DisplayStyle.None : DisplayStyle.Flex);
            if (!isVisible)
                (_historyPanelSearchInput ?? _historySearchInput)?.Focus();
            if (isVisible)
                OnHistorySearchCleared();
        }

        private void OnHistorySearchCleared()
        {
            _sessionSearchQuery = string.Empty;
            if (_historySearchInput != null)
                _historySearchInput.SetValueWithoutNotify(string.Empty);
            if (_historyPanelSearchInput != null)
                _historyPanelSearchInput.SetValueWithoutNotify(string.Empty);
            SetDisplay(_historySearchBar, DisplayStyle.None);
            SetDisplay(_historyPanelSearchBar, DisplayStyle.None);
            _ = RefreshSessionsFromCacheAsync();
        }

        private void OnHistorySearchChanged(ChangeEvent<string> evt)
        {
            _sessionSearchQuery = evt.newValue ?? string.Empty;
            if (_historySearchInput != null && _historySearchInput != evt.target)
                _historySearchInput.SetValueWithoutNotify(_sessionSearchQuery);
            if (_historyPanelSearchInput != null && _historyPanelSearchInput != evt.target)
                _historyPanelSearchInput.SetValueWithoutNotify(_sessionSearchQuery);
            _ = RefreshSessionsFromCacheAsync();
        }

        private async Task RefreshSessionsFromCacheAsync()
        {
            var chat = await GetChatServiceAsync();
            if (chat == null) return;
            try
            {
                ShowHistoryState(LocalizationExtensions.Get("history.loading", "Загрузка истории…"), isError: false);
                var allSessions = await chat.GetAllSessionsAsync();
                var app = await GetAppAsync();
                var providers = app != null ? await app.ProviderManager.GetAllProvidersAsync() : new List<ProviderConfig>();
                if (_isBound) RenderSessionList(allSessions, providers);
            }
            catch (Exception ex)
            {
                ShowHistoryState(LocalizationExtensions.Get("history.load_failed", "Не удалось загрузить историю чатов."), isError: true);
                NeonLogger.LogError(ex.ToString());
            }
        }

        private void AddSessionHeader(ScrollView target, int sessionsCount)
        {
            if (target == null) return;
            var groupLabel = new Label(string.IsNullOrWhiteSpace(_sessionSearchQuery)
                ? LocalizationExtensions.Get("history.group.recent", "Недавние")
                : LocalizationExtensions.GetFormat("history.group.results", "Результаты: {0}", sessionsCount));
            groupLabel.AddToClassList("history__group");
            target.Add(groupLabel);
        }

        private void ShowHistoryState(string message, bool isError)
        {
            if (_historyState == null)
                return;

            _historyState.text = message ?? string.Empty;
            bool hasMessage = !string.IsNullOrWhiteSpace(_historyState.text);
            SetDisplay(_historyState, hasMessage ? DisplayStyle.Flex : DisplayStyle.None);
            _historyState.EnableInClassList("history-panel__state--error", hasMessage && isError);
        }

        private VisualElement CreateSessionItem(ChatSession session, bool isActive, List<ProviderConfig> providers)
        {
            var container = new VisualElement();
            container.AddToClassList("history__item");
            container.EnableInClassList(ActiveSessionClass, isActive);

            var headerRow = new VisualElement();
            headerRow.AddToClassList("history__row");

            var titleLabel = new Label(string.IsNullOrWhiteSpace(session.title) || session.title == "New chat"
                ? LocalizationExtensions.Get("chat.new", "Новый чат")
                : session.title);
            titleLabel.AddToClassList("history__title");

            var providerLabel = new Label(BuildSessionProviderLabel(session, providers));
            providerLabel.AddToClassList("history__provider");

            int count = session.messages?.Count ?? 0;
            var metaLabel = new Label(MessageCountText(count));
            metaLabel.AddToClassList("history__meta");

            var deleteBtn = new Button { text = "\u00d7" };
            deleteBtn.AddToClassList("history__delete-btn");
            bool deletePending = false;
            deleteBtn.RegisterCallback<ClickEvent>(evt =>
            {
                evt.StopPropagation();
                if (!deletePending)
                {
                    deletePending = true;
                    deleteBtn.text = "\u2713";
                    deleteBtn.AddToClassList("history__delete-btn--confirm");
                    return;
                }
                _ = DeleteSessionAndRefreshAsync(session.sessionId);
            });

            headerRow.Add(titleLabel);
            headerRow.Add(providerLabel);
            headerRow.Add(deleteBtn);

            container.Add(headerRow);
            container.Add(metaLabel);
            container.RegisterCallback<ClickEvent>(evt => { _ = SwitchSessionAsync(session, container); });

            return container;
        }

        private static string BuildSessionProviderLabel(ChatSession session, List<ProviderConfig> providers)
        {
            if (session == null)
                return LocalizationExtensions.Get("history.provider.none", "Провайдер: —");

            if (string.IsNullOrWhiteSpace(session.providerId))
                return LocalizationExtensions.Get("history.provider.default", "Провайдер: default");

            var provider = providers?.Find(p => p != null && p.id == session.providerId);
            if (provider != null && !string.IsNullOrWhiteSpace(provider.displayName))
                return LocalizationExtensions.GetFormat("history.provider.named", "Провайдер: {0}", provider.displayName);

            return LocalizationExtensions.GetFormat("history.provider.named", "Провайдер: {0}", ShortProviderId(session.providerId));
        }

        private static string ShortProviderId(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId))
                return "—";

            return providerId.Length <= 8 ? providerId : providerId.Substring(0, 8);
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
                RenderMessages(chat.CurrentChatViewModel?.Messages);
                await LoadSessionsAsync(chat);
                ShowChat();
            }
            catch (Exception ex)
            {
                NeonLogger.LogError(ex.ToString());
            }
        }

        private async Task DeleteSessionAndRefreshAsync(string sessionId)
        {
            try
            {
                var chat = await GetChatServiceAsync();
                if (chat == null) return;

                await chat.DeleteSessionAsync(sessionId);

                if (_currentSessionId == sessionId)
                {
                    _currentSessionId = chat.CurrentSessionId ?? string.Empty;
                    _currentSessionTitle = string.Empty;
                    RenderMessages(chat.CurrentChatViewModel?.Messages);
                }

                await LoadSessionsAsync(chat);
            }
            catch (Exception ex)
            {
                NeonLogger.LogError(ex.ToString());
            }
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

            string text = LocalizationExtensions.Get("chat.subtitle.ready", "Готова помочь. С чего начнём?");
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

            var title = new Label(LocalizationExtensions.Get("chat.empty.title", "Пока нет сообщений"));
            title.AddToClassList("transcript__empty-title");

            var body = new Label(LocalizationExtensions.Get("chat.empty.body", "Начни диалог ниже, и здесь появится полная история текущей сессии."));
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
                    return LocalizationExtensions.Get("chat.role.you", "Ты");
                case "system":
                    return LocalizationExtensions.Get("chat.role.system", "Система");
                default:
                    return LocalizationExtensions.Get("chat.role.neon", "Neon");
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
                word = LocalizationExtensions.Get("chat.messages.many", "сообщений");
            else if (mod10 == 1)
                word = LocalizationExtensions.Get("chat.messages.one", "сообщение");
            else if (mod10 >= 2 && mod10 <= 4)
                word = LocalizationExtensions.Get("chat.messages.few", "сообщения");
            else
                word = LocalizationExtensions.Get("chat.messages.many", "сообщений");

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
            _editingProvider = ProviderConfig.CreateDefault(LocalizationExtensions.Get("providers.new_provider", "Новый провайдер"), "https://api.openai.com/v1");
            _lastCustomModel = _editingProvider.defaultModel ?? string.Empty;
            _editModelUsesCustomMode = false;
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
            _lastCustomModel = _editingProvider.defaultModel ?? string.Empty;
            _editModelUsesCustomMode = false;
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
            UpdateEditorStatus();
            if (_editName != null)
                _editName.SetValueWithoutNotify(_editingProvider.displayName ?? string.Empty);
            if (_editBaseUrl != null)
                _editBaseUrl.SetValueWithoutNotify(_editingProvider.baseUrl ?? string.Empty);
            if (_editApiKey != null)
                _editApiKey.SetValueWithoutNotify(_editingProvider.apiKey ?? string.Empty);
            if (_editModel != null)
                _editModel.SetValueWithoutNotify(_editingProvider.defaultModel ?? string.Empty);
            SyncModelPresetUi(_editingProvider.defaultModel ?? string.Empty);
            if (_editTemperature != null)
                _editTemperature.SetValueWithoutNotify(_editingProvider.temperature);
            if (_editMaxTokens != null)
                _editMaxTokens.SetValueWithoutNotify(_editingProvider.maxTokens.ToString());

            SetTestRow(null, string.Empty);
            _providerEditPanel.style.display = DisplayStyle.Flex;
            _ = RefreshProvidersListAsync();
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

                UpdateEditorStatus();

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
                SetTestRow(null, LocalizationExtensions.Get("providers.test.checking", "Проверяем соединение…"));

                var draft = BuildProviderDraftFromEditor();
                if (draft == null)
                {
                    SetTestRow(false, LocalizationExtensions.Get("providers.test.build_failed", "Не удалось собрать настройки провайдера."));
                    return;
                }

                var result = await app.AiClient.TestConnectionAsync(draft);
                SetTestRow(result.Success, result.Message);
            }
            catch (Exception ex)
            {
                SetTestRow(false, LocalizationExtensions.Get("providers.test.failed", "Проверка подключения не выполнена. Проверь адрес, модель и параметры доступа."));
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
                SetTestRow(false, LocalizationExtensions.Get("providers.unsaved.press_cancel_again", "Изменения не сохранены. Нажми «Отмена» ещё раз, чтобы сбросить."));
                return;
            }

            _cancelPending = false;
            _editingProvider = null;
            _editingProviderSource = null;
            SetDisplay(_providerEditPanel, DisplayStyle.None);
            _ = RefreshProvidersListAsync();
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
            draft.defaultModel = GetCurrentModelValue();
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
            SetTestRow(false, LocalizationExtensions.Get("providers.unsaved.save_or_cancel", "Есть несохранённые изменения. Сначала сохрани или отмени."));
            return false;
        }

        private bool CanSwitchEditingProvider(ProviderConfig target)
        {
            if (_editingProviderSource?.id == target?.id) return true;
            if (!HasUnsavedChanges()) return true;
            SetTestRow(false, LocalizationExtensions.Get("providers.unsaved.save_or_cancel", "Есть несохранённые изменения. Сначала сохрани или отмени."));
            return false;
        }

        private void OnProviderEditorIdentityChanged(ChangeEvent<string> _)
        {
            if (_syncingModelPresetUi)
                return;

            if (_editorProviderName != null)
                _editorProviderName.text = string.IsNullOrWhiteSpace(_editName?.value) ? "—" : _editName.value;

            SyncModelPresetUi(GetCurrentModelValue());
        }

        private void OnManualModelChanged(ChangeEvent<string> evt)
        {
            if (_syncingModelPresetUi)
                return;

            if (string.Equals(_editModelPreset?.value, CustomModelPresetValue, StringComparison.Ordinal))
                _lastCustomModel = evt?.newValue ?? string.Empty;
        }

        private void OnModelPresetChanged(ChangeEvent<string> evt)
        {
            if (_syncingModelPresetUi)
                return;

            if (evt == null)
                return;

            string selectedLabel = evt.newValue ?? string.Empty;
            bool isCustom = string.Equals(selectedLabel, CustomModelPresetValue, StringComparison.Ordinal);
            SetDisplay(_editModelCustomWrap, isCustom ? DisplayStyle.Flex : DisplayStyle.None);

            if (isCustom)
            {
                if (_editModel != null)
                    _editModel.SetValueWithoutNotify(_lastCustomModel ?? string.Empty);
                _editModelUsesCustomMode = true;
                return;
            }

            _editModelUsesCustomMode = false;
            if (_modelPresetByLabel.TryGetValue(selectedLabel, out string modelId) && _editModel != null)
                _editModel.SetValueWithoutNotify(modelId ?? string.Empty);
        }

        private string GetCurrentModelValue()
        {
            if (string.Equals(_editModelPreset?.value, CustomModelPresetValue, StringComparison.Ordinal))
                return _editModel?.value ?? string.Empty;

            string selected = _editModelPreset?.value ?? string.Empty;
            if (_modelPresetByLabel.TryGetValue(selected, out string presetModel))
                return presetModel ?? string.Empty;

            return _editModel?.value ?? string.Empty;
        }

        private void SyncModelPresetUi(string currentModel)
        {
            if (_editModelPreset == null)
                return;

            string nameHint = _editName?.value ?? _editingProvider?.displayName ?? _editingProviderSource?.displayName ?? string.Empty;
            string baseUrlHint = _editBaseUrl?.value ?? _editingProvider?.baseUrl ?? _editingProviderSource?.baseUrl ?? string.Empty;
            var presets = BuildModelPresets(nameHint, baseUrlHint);
            bool preserveCustomMode = _editModelUsesCustomMode;

            _syncingModelPresetUi = true;
            _modelPresetByLabel.Clear();
            var choices = new List<string>(presets.Count + 1);
            foreach (var preset in presets)
            {
                _modelPresetByLabel[preset.Label] = preset.ModelId;
                choices.Add(preset.Label);
            }
            choices.Add(CustomModelPresetValue);
            _editModelPreset.choices = choices;

            string targetChoice = CustomModelPresetValue;
            if (!preserveCustomMode)
            {
                for (int i = 0; i < presets.Count; i++)
                {
                    if (string.Equals(presets[i].ModelId, currentModel, StringComparison.Ordinal))
                    {
                        targetChoice = presets[i].Label;
                        break;
                    }
                }
            }

            if (targetChoice == CustomModelPresetValue)
                _lastCustomModel = currentModel ?? string.Empty;

            _editModelPreset.SetValueWithoutNotify(targetChoice);
            bool showCustom = string.Equals(targetChoice, CustomModelPresetValue, StringComparison.Ordinal);
            _editModelUsesCustomMode = showCustom;
            SetDisplay(_editModelCustomWrap, showCustom ? DisplayStyle.Flex : DisplayStyle.None);
            if (_editModel != null)
                _editModel.SetValueWithoutNotify(showCustom ? (_lastCustomModel ?? string.Empty) : (currentModel ?? string.Empty));
            _syncingModelPresetUi = false;
        }

        private void UpdateEditorStatus()
        {
            if (_editorProviderStatus == null)
                return;

            if (_editingProviderSource == null)
            {
                _editorProviderStatus.text = LocalizationExtensions.Get("providers.editor.status.new_draft", "Новый черновик");
                _editorProviderStatus.EnableInClassList("editor__status--active", false);
                _editorProviderStatus.EnableInClassList("editor__status--inactive", false);
                _editorProviderStatus.EnableInClassList("editor__status--draft", true);
                return;
            }

            bool isActive = string.Equals(_chatService?.CurrentProvider?.id, _editingProviderSource.id, StringComparison.Ordinal);
            _editorProviderStatus.text = isActive
                ? LocalizationExtensions.Get("providers.editor.status.active", "В редакторе: активный провайдер")
                : LocalizationExtensions.Get("providers.editor.status.inactive", "В редакторе: неактивный провайдер");
            _editorProviderStatus.EnableInClassList("editor__status--active", isActive);
            _editorProviderStatus.EnableInClassList("editor__status--inactive", !isActive);
            _editorProviderStatus.EnableInClassList("editor__status--draft", false);
        }

        private readonly struct ModelPreset
        {
            public readonly string Label;
            public readonly string ModelId;

            public ModelPreset(string label, string modelId)
            {
                Label = label;
                ModelId = modelId;
            }
        }

        private static List<ModelPreset> BuildModelPresets(string nameHint, string baseUrlHint)
        {
            string hint = $"{nameHint} {baseUrlHint}".ToLowerInvariant();
            if (hint.Contains("anthropic"))
                return new List<ModelPreset> { new ModelPreset("Claude Sonnet 4.5", "claude-sonnet-4-5"), new ModelPreset("Claude 3.7 Sonnet", "claude-3-7-sonnet-latest"), new ModelPreset("Claude 3.5 Haiku", "claude-3-5-haiku-latest") };
            if (hint.Contains("gemini") || hint.Contains("googleapis.com"))
                return new List<ModelPreset> { new ModelPreset("Gemini 2.5 Pro", "gemini-2.5-pro"), new ModelPreset("Gemini 2.5 Flash", "gemini-2.5-flash"), new ModelPreset("Gemini 2.0 Flash", "gemini-2.0-flash") };
            if (hint.Contains("x.ai") || hint.Contains("grok") || hint.Contains("xai"))
                return new List<ModelPreset> { new ModelPreset("Grok 3", "grok-3"), new ModelPreset("Grok 3 Mini", "grok-3-mini"), new ModelPreset("Grok 2", "grok-2-latest") };
            if (hint.Contains("openrouter"))
                return new List<ModelPreset> { new ModelPreset("OpenAI GPT-4.1", "openai/gpt-4.1"), new ModelPreset("Anthropic Sonnet 4.5", "anthropic/claude-sonnet-4-5"), new ModelPreset("Google Gemini 2.5 Pro", "google/gemini-2.5-pro") };
            if (hint.Contains("localhost") || hint.Contains("127.0.0.1") || hint.Contains("ollama"))
                return new List<ModelPreset> { new ModelPreset("Llama 3.1 8B (Ollama)", "llama3.1:8b"), new ModelPreset("Qwen 2.5 7B (Ollama)", "qwen2.5:7b"), new ModelPreset("Mistral 7B (Ollama)", "mistral:7b") };
            if (hint.Contains("openai"))
                return new List<ModelPreset> { new ModelPreset("GPT-4.1", "gpt-4.1"), new ModelPreset("GPT-4o", "gpt-4o"), new ModelPreset("GPT-4o mini", "gpt-4o-mini") };
            return new List<ModelPreset> { new ModelPreset("GPT-4.1", "gpt-4.1"), new ModelPreset("Claude Sonnet 4.5", "claude-sonnet-4-5"), new ModelPreset("Gemini 2.5 Flash", "gemini-2.5-flash") };
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

                var deletedCurrent = string.Equals(_chatService?.CurrentProvider?.id, provider.id, StringComparison.Ordinal);
                await app.ProviderManager.DeleteProviderAsync(provider.id);

                if (_editingProvider?.id == provider.id)
                {
                    _cancelPending = false;
                    _editingProvider = null;
                    _editingProviderSource = null;
                    SetDisplay(_providerEditPanel, DisplayStyle.None);
                }

                if (deletedCurrent)
                {
                    // ProviderManager materializes a default provider if the repository became empty.
                    var fallbackProvider = await app.ProviderManager.GetActiveProviderAsync();
                    await SwitchProviderAsync(fallbackProvider);
                    return;
                }

                var settings = app.Settings.Load() ?? new AppSettings();
                if (string.Equals(settings.activeProviderId, provider.id, StringComparison.Ordinal))
                {
                    var fallbackProvider = await app.ProviderManager.GetActiveProviderAsync();
                    settings.activeProviderId = fallbackProvider?.id ?? settings.activeProviderId;
                    app.Settings.Save(settings);
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

                var app = await GetAppAsync();
                if (app != null)
                {
                    var s = app.Settings.Load() ?? new AppSettings();
                    s.activeProviderId = chat.CurrentProvider?.id ?? provider?.id ?? s.activeProviderId;
                    app.Settings.Save(s);
                }
                else
                {
                    await SaveSettingsAsync();
                }

                _currentSessionId = chat.CurrentSessionId ?? string.Empty;
                _currentSessionTitle = string.Empty;
                SetProviderHeader(provider);
                UpdateEditorStatus();
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
                _providersList.Add(new Label(LocalizationExtensions.Get("providers.manager.not_ready", "Менеджер провайдеров не готов.")));
                return;
            }

            var providers = await app.ProviderManager.GetAllProvidersAsync();
            if (_navProvidersCount != null)
                _navProvidersCount.text = providers.Count.ToString();

            if (providers.Count == 0)
            {
                _providersList.Add(new Label(LocalizationExtensions.Get("providers.empty", "Провайдеры не настроены.")));
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
            bool isEditing = _editingProviderSource != null && _editingProviderSource.id == provider.id;
            container.EnableInClassList(EditingProviderClass, isEditing);
            container.RegisterCallback<ClickEvent>(evt => StartEditingProvider(provider));

            var logo = new VisualElement();
            logo.AddToClassList("provider__logo");
            logo.Add(new Label(BuildProviderShort(provider)));

            var body = new VisualElement();
            body.AddToClassList("provider__body");

            var nameRow = new VisualElement();
            nameRow.AddToClassList("provider__name-row");

            var nameLabel = new Label(string.IsNullOrWhiteSpace(provider.displayName) ? LocalizationExtensions.Get("providers.default_name", "Провайдер") : provider.displayName);
            nameLabel.AddToClassList("provider__name");
            nameRow.Add(nameLabel);

            if (isActive)
            {
                var chip = new Label(LocalizationExtensions.Get("providers.chip.active", "активен"));
                chip.AddToClassList("chip");
                chip.AddToClassList("chip--accent");
                nameRow.Add(chip);
            }
            if (isEditing)
            {
                var chip = new Label(LocalizationExtensions.Get("providers.chip.editing", "в редакторе"));
                chip.AddToClassList("chip");
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
            var metaLabel = new Label(LocalizationExtensions.Get("providers.source", "Источник"));
            metaLabel.AddToClassList("provider__meta-label");
            var metaValue = new Label(BuildProviderLocationText(provider));
            metaValue.AddToClassList("provider__meta-value");
            meta.Add(metaLabel);
            meta.Add(metaValue);

            var actions = new VisualElement();
            actions.AddToClassList("provider__actions");
            var useButton = new Button(() => SwitchProvider(provider)) { text = LocalizationExtensions.Get("providers.action.use", "Использовать") };
            var editButton = new Button(() => StartEditingProvider(provider)) { text = LocalizationExtensions.Get("providers.action.edit", "Изменить") };
            var deleteButton = new Button(() => DeleteProvider(provider)) { text = LocalizationExtensions.Get("providers.action.delete", "Удалить") };
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
            string displayName = string.IsNullOrWhiteSpace(provider.displayName) ? LocalizationExtensions.Get("common.dash", "—") : provider.displayName;
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
            return LocalizationExtensions.Get("providers.location.unknown", "неизвестно");

            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            {
                string host = uri.Host;
                if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase))
                {
                    return LocalizationExtensions.Get("providers.location.local", "локально");
                }

                return LocalizationExtensions.Get("providers.location.remote", "удалённо");
            }

            if (baseUrl.IndexOf("localhost", StringComparison.OrdinalIgnoreCase) >= 0
                || baseUrl.IndexOf("127.0.0.1", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return LocalizationExtensions.Get("providers.location.local", "локально");
            }

            return LocalizationExtensions.Get("providers.location.remote", "удалённо");
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
            RegisterToggleChanged(_settingsVoiceIo,      _ => { SaveSettings(); RefreshVoiceControls(); });
            RegisterToggleChanged(_settingsEncryptKeys,  _ => SaveSettings());
            RegisterToggleChanged(_settingsMaskLogs,     _ => SaveSettings());
            RegisterToggleChanged(_settingsShowHalo,    v => { ApplyHaloVisibility(v); SaveSettings(); });
            RegisterToggleChanged(_settingsBreathing,   v => { ApplyBreathingAnimation(v); SaveSettings(); });

            if (_settingsLanguage != null)
                _settingsLanguage.RegisterCallback<ChangeEvent<string>>(OnSettingsLanguageChanged);
        }

        private void OnSettingsLanguageChanged(ChangeEvent<string> evt)
        {
            OnLanguageSettingChanged(evt?.newValue);
        }

        private void ApplyLocalizedStaticTexts()
        {
            _navChatLabel?.Localize("tab.chat");
            _navAvatarsLabel?.Localize("tab.avatar");
            _navProvidersLabel?.Localize("settings.providers");
            _navHistoryLabel?.Localize("chat.history");
            _navThemesLabel?.Localize("settings.themes");
            _navSettingsLabel?.Localize("tab.settings");
            _testRowLabel?.Localize("providers.test.hint");
            ApplyStaticTemplateLocalization();
        }

        private void OnLanguageSettingChanged(string selectedValue)
        {
            string languageCode = ResolveLanguageCode(selectedValue);
            _ = ApplyLanguageRuntimeAsync(languageCode);
            SaveSettings();
        }

        private async Task ApplyLanguageRuntimeAsync(string languageCode)
        {
            var app = await GetAppAsync();
            if (app == null)
                return;

            if (app.Services.TryGet<ILocalizationService>(out var localization) && localization != null)
            {
                localization.SetLanguage(languageCode);
                LocalizationExtensions.SetLocalizationService(localization);
                await RefreshLocalizedUiAsync();
            }
        }

        private async Task BindLocalizationEventsAsync()
        {
            var app = await GetAppAsync();
            if (app == null)
                return;

            if (!app.Services.TryGet<ILocalizationService>(out var localization) || localization == null)
                return;

            if (ReferenceEquals(_localizationService, localization))
                return;

            UnbindLocalizationEvents();
            _localizationService = localization;
            LocalizationExtensions.SetLocalizationService(localization);
            _localizationService.LanguageChanged += OnLocalizationLanguageChanged;
        }

        private void UnbindLocalizationEvents()
        {
            if (_localizationService == null)
                return;

            _localizationService.LanguageChanged -= OnLocalizationLanguageChanged;
            _localizationService = null;
        }

        private void OnLocalizationLanguageChanged()
        {
            if (!isActiveAndEnabled || !_isBound)
                return;

            _ = RefreshLocalizedUiAsync();
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

                if (_settingsLanguage != null)
                {
                    string currentLanguage = _localizationService?.CurrentLanguage ?? ResolveLanguageCode(_settingsLanguage.value);
                    _settingsLanguage.SetValueWithoutNotify(currentLanguage == "en"
                        ? LocalizationExtensions.Get("settings.language.english", "English")
                        : LocalizationExtensions.Get("settings.language.russian", "Русский"));
                }

                if (_previewPersona != null)
                    _previewPersona.text = AvatarPersonaText(_activeAvatarId);
                UpdatePersonaStateUi(_activeAvatarId);
                UpdateAvatarActionButtons(_activeAvatarId);
                RefreshBuiltInAvatarTileLabels();
                UpdateAvatarFilterCounts();
                UpdateClearDataButtonState();

                var app = await GetAppAsync();
                if (!_isBound)
                    return;

                if (app != null)
                    RefreshPluginStatus(app);

                var chat = await GetChatServiceAsync();
                if (!_isBound)
                    return;

                if (chat != null)
                {
                    RenderMessages(chat.CurrentChatViewModel?.Messages);
                    await LoadSessionsAsync(chat);
                }

                await RefreshProvidersListAsync();
                UpdateEditorStatus();
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
                SetTopbar(GetChatTitle(), _chatSubtitle);
                return;
            }

            if (_avatarsPanel != null && _avatarsPanel.style.display != DisplayStyle.None)
            {
                int total = BuiltInAvatarIds.Length + (_cachedCustomProfiles?.Count ?? 0);
                SetTopbar(LocalizationExtensions.Get("topbar.avatars.title", "Аватары"),
                    LocalizationExtensions.GetFormat("topbar.avatars.subtitle", "{0} образов · {1}", total, AvatarDisplayName(_activeAvatarId)));
                return;
            }

            if (_providersPanel != null && _providersPanel.style.display != DisplayStyle.None)
            {
                SetTopbar(LocalizationExtensions.Get("topbar.providers.title", "Провайдеры"), LocalizationExtensions.Get("topbar.providers.subtitle", "OpenAI-совместимые провайдеры"));
                return;
            }

            if (_historyPanel != null && _historyPanel.style.display != DisplayStyle.None)
            {
                SetTopbar(LocalizationExtensions.Get("topbar.history.title", "История чатов"),
                    string.IsNullOrWhiteSpace(_sessionSearchQuery)
                        ? LocalizationExtensions.Get("topbar.history.subtitle.saved", "Сохранённые сессии")
                        : LocalizationExtensions.GetFormat("topbar.history.subtitle.search", "Поиск: {0}", _sessionSearchQuery));
                return;
            }

            if (_themesPanel != null && _themesPanel.style.display != DisplayStyle.None)
            {
                SetTopbar(LocalizationExtensions.Get("topbar.themes.title", "Темы"), LocalizationExtensions.Get("topbar.themes.subtitle", "Форма, ореол и дыхание для аватара"));
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

        private static string ResolveLanguageCode(string languageValue)
        {
            if (string.IsNullOrWhiteSpace(languageValue))
                return "ru";

            return languageValue.Trim().Equals("english", StringComparison.OrdinalIgnoreCase)
                ? "en"
                : "ru";
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
                var availableAvatars = app.Avatars.GetAll();
                string resolvedAvatarId = string.IsNullOrEmpty(s.activeAvatarId) ? "neon" : s.activeAvatarId;
                bool knownAvatar = Array.IndexOf(BuiltInAvatarIds, resolvedAvatarId) >= 0 ||
                                   availableAvatars.Any(a => a != null && a.id == resolvedAvatarId);
                if (!knownAvatar)
                    resolvedAvatarId = "neon";

                _activeAvatarId = resolvedAvatarId;

                _settingsHistory?.SetValueWithoutNotify(s.saveChatHistory);
                _settingsStreaming?.SetValueWithoutNotify(s.streaming);
                _settingsSystemPrompt?.SetValueWithoutNotify(s.useSystemPrompt);
                _settingsVoiceIo?.SetValueWithoutNotify(s.voiceIOEnabled);
                _settingsEncryptKeys?.SetValueWithoutNotify(s.encryptKeys);
                _settingsMaskLogs?.SetValueWithoutNotify(s.maskLogs);
                _settingsShowHalo?.SetValueWithoutNotify(s.showHalo);
                _settingsBreathing?.SetValueWithoutNotify(s.breathingAnimation);

                if (_settingsLanguage != null)
                    _settingsLanguage.SetValueWithoutNotify(s.language == "en"
                        ? LocalizationExtensions.Get("settings.language.english", "English")
                        : LocalizationExtensions.Get("settings.language.russian", "Русский"));

                await ApplyLanguageRuntimeAsync(s.language == "en" ? "en" : "ru");

                if (_settingsStoragePath != null)
                    _settingsStoragePath.text = Application.persistentDataPath;

                if (_settingsVersion != null)
                    _settingsVersion.text = string.IsNullOrEmpty(Application.version) ? "0.1.0" : Application.version;
                if (_brandVersion != null)
                    _brandVersion.text = string.IsNullOrEmpty(Application.version) ? "0.1.0" : Application.version;
                RefreshPluginStatus(app);

                SetAvatarShape(s.avatarShape ?? "round", save: false);
                ApplyHaloVisibility(s.showHalo);
                RefreshCustomAvatarGallery(app);
                RefreshBuiltInAvatarTileLabels();
                ApplyAvatarFilter();
                ApplyAvatarArt(_activeAvatarId);
                SyncGallerySelection(_activeAvatarId);
                ApplyBreathingAnimation(s.breathingAnimation);

                if (!string.Equals(s.activeAvatarId, _activeAvatarId, StringComparison.Ordinal))
                {
                    s.activeAvatarId = _activeAvatarId;
                    app.Settings.Save(s);
                }

                await SyncActiveAvatarSystemPromptAsync(app, s);
                RefreshVoiceControls();
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

        private void RefreshPluginStatus(CompanionApp app)
        {
            if (app == null)
                return;

            if (!app.Services.TryGet<PluginManager>(out var pluginManager) || pluginManager == null)
            {
                if (_settingsPluginsSummary != null) _settingsPluginsSummary.text = LocalizationExtensions.Get("plugins.status.not_initialized", "не инициализирован");
                if (_settingsPluginsConfig != null) _settingsPluginsConfig.text = LocalizationExtensions.Get("plugins.status.no_data", "нет данных");
                _settingsPluginsList?.Clear();
                return;
            }

            var plugins = pluginManager.Plugins;
            int loaded = 0;
            int failed = 0;
            int skipped = 0;

            for (int i = 0; i < plugins.Count; i++)
            {
                switch (plugins[i].Status)
                {
                    case PluginManager.PluginRuntimeStatus.Loaded:
                        loaded++;
                        break;
                    case PluginManager.PluginRuntimeStatus.Failed:
                        failed++;
                        break;
                    default:
                        skipped++;
                        break;
                }
            }

            if (_settingsPluginsSummary != null)
                _settingsPluginsSummary.text = $"loaded={loaded} failed={failed} skipped={skipped}";
            if (_settingsPluginsConfig != null)
                _settingsPluginsConfig.text = pluginManager.HasAnyPluginConfigFiles
                    ? LocalizationExtensions.Get("plugins.config.found", "обнаружены")
                    : LocalizationExtensions.Get("plugins.config.not_found", "не найдены");

            if (_settingsPluginsList == null)
                return;

            _settingsPluginsList.Clear();
            if (plugins.Count == 0)
            {
                var empty = new Label(LocalizationExtensions.Get("plugins.empty", "Плагины не найдены в persistentDataPath/Plugins."));
                empty.AddToClassList("settings-plugin-item");
                _settingsPluginsList.Add(empty);
                return;
            }

            for (int i = 0; i < plugins.Count; i++)
            {
                var p = plugins[i];
                string status = p.Status == PluginManager.PluginRuntimeStatus.Loaded ? "loaded" :
                    (p.Status == PluginManager.PluginRuntimeStatus.Failed ? "failed" : "skipped");
                var label = new Label($"{p.Name} ({p.Version}) [{status}] config={(p.HasConfig ? "yes" : "no")}");
                label.AddToClassList("settings-plugin-item");
                _settingsPluginsList.Add(label);
            }
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
                if (_settingsVoiceIo != null)       s.voiceIOEnabled      = _settingsVoiceIo.value;
                if (_settingsEncryptKeys != null)   s.encryptKeys         = _settingsEncryptKeys.value;
                if (_settingsMaskLogs != null)      s.maskLogs            = _settingsMaskLogs.value;
                if (_settingsShowHalo != null)      s.showHalo            = _settingsShowHalo.value;
                if (_settingsBreathing != null)     s.breathingAnimation  = _settingsBreathing.value;
                if (_settingsLanguage != null)      s.language            = ResolveLanguageCode(_settingsLanguage.value);

                s.avatarShape    = _avatarShape;
                s.activeAvatarId = _activeAvatarId;
                s.activeProviderId = _chatService?.CurrentProvider?.id ?? s.activeProviderId;

                app.Settings.Save(s);

                // Propagate runtime flags to services immediately
                if (_chatService != null)
                    _chatService.SaveChatHistory = s.saveChatHistory;

                await SyncActiveAvatarSystemPromptAsync(app, s);
            }
            catch (Exception ex)
            {
                NeonLogger.LogError(ex.ToString());
            }
        }

        private async Task SyncActiveAvatarSystemPromptAsync(CompanionApp app = null, AppSettings settings = null)
        {
            if (_chatService == null)
                return;

            app ??= await GetAppAsync();
            if (app == null)
                return;

            settings ??= app.Settings.Load() ?? new AppSettings();
            var avatarProfiles = app.Avatars.GetAll();
            string prompt = app.AvatarService.GetSystemPrompt(_activeAvatarId, avatarProfiles);
            _chatService.SystemPrompt = settings.useSystemPrompt ? prompt : null;
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

        private void OnViewModeStaticClicked()                    => SetAvatarViewMode(AvatarViewMode.Static);
        private void OnViewModeAnimatedClicked()                  => SetAvatarViewMode(AvatarViewMode.Animated);
        private void OnViewMode3DClicked()                        => SetAvatarViewMode(AvatarViewMode.Volume3D);
        private void OnNeonAnimatedTileClicked(ClickEvent _)
        {
            // SelectAvatar has an early-return when the same avatar is already active.
            // The animated tile must force-refresh art even if "neon" is already selected
            // (e.g. user was on the Static tab and just switched to Animated).
            if (_activeAvatarId == "neon")
            {
                SyncGallerySelection("neon");
                ApplyAvatarArt("neon");
                SaveSettings();
            }
            else
            {
                SelectAvatar("neon");
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

        private void SetAvatarViewMode(AvatarViewMode mode)
        {
            if (_avatarViewMode == mode) return;
            _avatarViewMode = mode;
            ApplyAvatarViewMode();
            // Re-apply avatar art so the chat display and preview labels update
            // immediately when switching between Static / Animated / 3D tabs.
            ApplyAvatarArt(_activeAvatarId);
        }

        private void ApplyAvatarViewMode()
        {
            bool isStatic   = _avatarViewMode == AvatarViewMode.Static;
            bool isAnimated = _avatarViewMode == AvatarViewMode.Animated;
            bool is3D       = _avatarViewMode == AvatarViewMode.Volume3D;

            _viewModeStaticBtn?.EnableInClassList("viewmode-btn--active", isStatic);
            _viewModeAnimatedBtn?.EnableInClassList("viewmode-btn--active", isAnimated);
            _viewMode3DBtn?.EnableInClassList("viewmode-btn--active", is3D);

            SetDisplay(_avatarFilterRow, isStatic ? DisplayStyle.Flex : DisplayStyle.None);
            SetDisplay(_galleryStatic,   isStatic ? DisplayStyle.Flex : DisplayStyle.None);
            SetDisplay(_galleryAnimated, isAnimated ? DisplayStyle.Flex : DisplayStyle.None);
            SetDisplay(_gallery3D,       is3D ? DisplayStyle.Flex : DisplayStyle.None);

            // Sync animated tile selection badge
            _avtileNeonAnimated?.EnableInClassList("avtile--selected", isAnimated && _activeAvatarId == "neon");
        }

        private void SelectAvatar(string avatarId)
        {
            if (_activeAvatarId == avatarId) return;
            ClosePersonaEditor();
            CancelCustomizationEdits();
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

                await SyncActiveAvatarSystemPromptAsync(app);

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
            string current = PersonaEditorText(_activeAvatarId);
            if (_personaEditField != null) _personaEditField.value = current;
            SetDisplay(_previewPersonaStateRow, DisplayStyle.None);
            SetDisplay(_previewPersonaLabel, DisplayStyle.None);
            SetDisplay(_previewPersona, DisplayStyle.None);
            SetDisplay(_previewActionsRow, DisplayStyle.None);
            SetDisplay(_personaEditorPanel, DisplayStyle.Flex);
        }

        private void ClosePersonaEditor()
        {
            SetDisplay(_personaEditorPanel, DisplayStyle.None);
            SetDisplay(_previewPersonaStateRow, DisplayStyle.Flex);
            SetDisplay(_previewPersonaLabel, DisplayStyle.Flex);
            SetDisplay(_previewPersona, DisplayStyle.Flex);
            SetDisplay(_previewActionsRow, DisplayStyle.Flex);
            UpdateAvatarActionButtons(_activeAvatarId);
        }

        private void OnPersonaCancelClicked() => ClosePersonaEditor();

        private void OnPersonaSaveClicked() => _ = SavePersonaAsync();

        private void OnPreviewResetPersonaClicked() => _ = ResetPersonaOverrideAsync();

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
                UpdatePersonaStateUi(_activeAvatarId);

                await SyncActiveAvatarSystemPromptAsync(app);

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
                AddSystemMessage(LocalizationExtensions.Get("avatar.delete.builtin_forbidden", "Встроенные аватары нельзя удалить."));
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
                string avatarAssetPath = profile.is3D ? profile.modelPath : profile.imagePath;

                profiles.RemoveAll(a => a.id == removedAvatarId);
                app.Avatars.SaveAll(profiles);

                UpdateAvatarProfileCaches(profiles);
                ReleaseCustomTexture(avatarAssetPath);
                DeleteCustomAvatarFileIfUnused(avatarAssetPath, profiles);

                ClosePersonaEditor();
                RefreshCustomAvatarGallery(app);

                _activeAvatarId = string.Empty;
                SelectAvatar("neon");

                await SyncActiveAvatarSystemPromptAsync(app);

                AddSystemMessage(LocalizationExtensions.GetFormat("avatar.delete.success", "Аватар «{0}» удалён.", removedName));
            }
            catch (Exception ex)
            {
                AddSystemMessage(LocalizationExtensions.Get("avatar.delete.failed", "Не удалось удалить аватар."));
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

            // Animated gallery tile
            _avtileNeonAnimated?.EnableInClassList("avtile--selected",
                _avatarViewMode == AvatarViewMode.Animated && avatarId == "neon");
        }

        private void ApplyAvatarArt(string avatarId)
        {
            bool isBuiltIn = Array.IndexOf(BuiltInAvatarIds, avatarId) >= 0;
            var profile = GetStoredProfile(avatarId);
            NeonLogger.Log("[AvatarArt] ApplyAvatarArt id='" + avatarId +
                "' isBuiltIn=" + isBuiltIn +
                " storedProfile=" + (profile != null ? "found (clips=" + (profile.animationClips?.Count ?? 0) + ")" : "null") +
                " _avatarArt=" + (_avatarArt != null ? "ok" : "NULL"));
            // For built-in avatars with no stored profile, create a stub so motion-pack
            // discovery in AvatarMotionPackLoader.ResolveProfileMotion can run.
            if (profile == null && isBuiltIn)
                profile = new AvatarProfile { id = avatarId, isBuiltIn = true };
            bool is3D = profile != null && profile.is3D && !string.IsNullOrWhiteSpace(profile.modelPath);
            bool hasAnimation = !is3D && ConfigureAvatarAnimation(profile);
            NeonLogger.Log("[AvatarArt] hasAnimation=" + hasAnimation + " is3D=" + is3D);

            if (is3D)
                _ = ConfigureAvatar3DAsync(profile);
            else
                Disable3DAvatarRender();

            if (_avatarArt != null)
            {
                // When animation is active, remove the CSS art class so its background-image
                // and background-color don't bleed through the sprite's transparent areas.
                foreach (var id in BuiltInAvatarIds)
                    _avatarArt.EnableInClassList($"avatar__art--{id}", isBuiltIn && id == avatarId && !hasAnimation);

                if (!isBuiltIn && !hasAnimation && !is3D)
                {
                    var tex = GetOrLoadTexture(GetCustomProfile(avatarId)?.imagePath);
                    _avatarArt.style.backgroundImage = tex != null ? new StyleBackground(tex) : StyleKeyword.Null;
                }
                else
                {
                    _avatarArt.style.backgroundImage = StyleKeyword.Null;
                }
            }

            // Hide the letter placeholder and shade ring when animation is running;
            // show them when displaying a static image so the circle doesn't look empty.
            SetDisplay(_avatarShade,  hasAnimation ? DisplayStyle.None : DisplayStyle.Flex);
            SetDisplay(_avatarLetter, hasAnimation ? DisplayStyle.None : DisplayStyle.Flex);

            ApplyAvatarLayout(hasAnimation);

            if (_previewHero != null)
            {
                foreach (var id in BuiltInAvatarIds)
                    _previewHero.EnableInClassList($"preview-hero--{id}", isBuiltIn && id == avatarId);

                if (!isBuiltIn && !is3D)
                {
                    var tex = GetOrLoadTexture(GetCustomProfile(avatarId)?.imagePath);
                    _previewHero.style.backgroundImage = tex != null ? new StyleBackground(tex) : StyleKeyword.Null;
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
            if (_previewAnimationInfo != null)
                _previewAnimationInfo.text = (_avatarViewMode == AvatarViewMode.Animated)
                    ? BuildAnimationInfoText(profile)
                    : LocalizationExtensions.Get("avatar.animation.static", "Статичное изображение");
            UpdatePersonaStateUi(avatarId);
            _activeCustomizationBaseline = CloneCustomization(profile?.customization);
            _avatarCustomizationPanel?.Bind(_activeCustomizationBaseline);
            ApplyAvatarCustomizationVisual(_activeCustomizationBaseline);

            UpdateAvatarActionButtons(avatarId);
        }

        /// <summary>
        /// Switches the avatar container between "fullscreen sprite" layout (animation active)
        /// and the default circular badge layout (static image).
        /// </summary>
        private void ApplyAvatarLayout(bool animated)
        {
            if (_avatarCircle != null)
            {
                if (animated)
                {
                    // Remove the fixed 240×240 circle – let it fill the hero area.
                    _avatarCircle.style.width          = new StyleLength(new Length(100, LengthUnit.Percent));
                    _avatarCircle.style.height         = new StyleLength(new Length(100, LengthUnit.Percent));
                    _avatarCircle.style.alignSelf      = Align.Stretch;
                    _avatarCircle.style.borderTopLeftRadius     = 0;
                    _avatarCircle.style.borderTopRightRadius    = 0;
                    _avatarCircle.style.borderBottomLeftRadius  = 0;
                    _avatarCircle.style.borderBottomRightRadius = 0;
                    _avatarCircle.style.borderTopWidth    = 0;
                    _avatarCircle.style.borderBottomWidth = 0;
                    _avatarCircle.style.borderLeftWidth   = 0;
                    _avatarCircle.style.borderRightWidth  = 0;
                    _avatarCircle.style.backgroundColor  = new StyleColor(Color.clear);
                }
                else
                {
                    // Restore CSS defaults (remove all inline overrides).
                    _avatarCircle.style.width          = StyleKeyword.Null;
                    _avatarCircle.style.height         = StyleKeyword.Null;
                    _avatarCircle.style.alignSelf      = StyleKeyword.Null;
                    _avatarCircle.style.borderTopLeftRadius     = StyleKeyword.Null;
                    _avatarCircle.style.borderTopRightRadius    = StyleKeyword.Null;
                    _avatarCircle.style.borderBottomLeftRadius  = StyleKeyword.Null;
                    _avatarCircle.style.borderBottomRightRadius = StyleKeyword.Null;
                    _avatarCircle.style.borderTopWidth    = StyleKeyword.Null;
                    _avatarCircle.style.borderBottomWidth = StyleKeyword.Null;
                    _avatarCircle.style.borderLeftWidth   = StyleKeyword.Null;
                    _avatarCircle.style.borderRightWidth  = StyleKeyword.Null;
                    _avatarCircle.style.backgroundColor  = StyleKeyword.Null;
                }
            }

            if (_avatarStageHero != null)
            {
                if (animated)
                {
                    _avatarStageHero.style.paddingTop    = 0;
                    _avatarStageHero.style.paddingBottom = 0;
                    _avatarStageHero.style.paddingLeft   = 0;
                    _avatarStageHero.style.paddingRight  = 0;
                }
                else
                {
                    _avatarStageHero.style.paddingTop    = StyleKeyword.Null;
                    _avatarStageHero.style.paddingBottom = StyleKeyword.Null;
                    _avatarStageHero.style.paddingLeft   = StyleKeyword.Null;
                    _avatarStageHero.style.paddingRight  = StyleKeyword.Null;
                }
            }

            // Hide the decorative halo glow in animated mode (it would show behind the sprite).
            SetDisplay(_avatarGlow, animated ? DisplayStyle.None : DisplayStyle.Flex);

            // In animated mode use ScaleToFit so the full character is always visible.
            if (_avatarArtImage != null)
                _avatarArtImage.scaleMode = animated ? ScaleMode.ScaleToFit : ScaleMode.ScaleAndCrop;
        }

        private void EnsureCustomizationOverlayElements()
        {
            if (_avatarArt != null && _avatarEmojiOverlay == null)
            {
                _avatarEmojiOverlay = new Label { name = "avatar-emoji-overlay" };
                _avatarEmojiOverlay.AddToClassList("avatar-emoji-overlay");
                _avatarEmojiOverlay.pickingMode = PickingMode.Ignore;
                _avatarArt.Add(_avatarEmojiOverlay);
            }

            if (_previewHero != null && _previewEmojiOverlay == null)
            {
                _previewEmojiOverlay = new Label { name = "preview-emoji-overlay" };
                _previewEmojiOverlay.AddToClassList("preview-emoji-overlay");
                _previewEmojiOverlay.pickingMode = PickingMode.Ignore;
                _previewHero.Add(_previewEmojiOverlay);
            }
        }

        private void OnAvatarCustomizationChanged(AvatarCustomizationData data)
        {
            ApplyAvatarCustomizationVisual(data);
        }

        private void OnAvatarCustomizationSaved()
        {
            _ = SaveAvatarCustomizationAsync();
        }

        private void OnAvatarCustomizationCanceled()
        {
            CancelCustomizationEdits();
        }

        private void CancelCustomizationEdits()
        {
            _avatarCustomizationPanel?.Bind(_activeCustomizationBaseline);
            ApplyAvatarCustomizationVisual(_activeCustomizationBaseline);
        }

        private async Task SaveAvatarCustomizationAsync()
        {
            try
            {
                var app = await GetAppAsync();
                if (app == null || _avatarCustomizationPanel == null)
                    return;

                var profiles = app.Avatars.GetAll();
                var profile = profiles.FirstOrDefault(a => a.id == _activeAvatarId);
                var data = CloneCustomization(GetPanelCustomizationData());
                bool isBuiltIn = Array.IndexOf(BuiltInAvatarIds, _activeAvatarId) >= 0;
                bool shouldStore = !IsCustomizationEffectivelyDefault(data);

                if (profile == null && !isBuiltIn)
                    return;

                if (profile == null && shouldStore)
                {
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

                if (profile != null)
                {
                    profile.customization = shouldStore ? data : null;
                    if (isBuiltIn && string.IsNullOrWhiteSpace(profile.systemPrompt) && profile.customization == null)
                        profiles.RemoveAll(a => a.id == _activeAvatarId && a.isBuiltIn);
                }

                app.Avatars.SaveAll(profiles);
                UpdateAvatarProfileCaches(profiles);
                _activeCustomizationBaseline = CloneCustomization(shouldStore ? data : null);
                _avatarCustomizationPanel.Bind(_activeCustomizationBaseline);
                ApplyAvatarCustomizationVisual(_activeCustomizationBaseline);
            }
            catch (Exception ex)
            {
                NeonLogger.LogError(ex.ToString());
            }
        }

        private AvatarCustomizationData GetPanelCustomizationData()
        {
            return _avatarCustomizationPanel?.CurrentData ?? _activeCustomizationBaseline;
        }

        private void ApplyAvatarCustomizationVisual(AvatarCustomizationData data)
        {
            var effective = CloneCustomization(data) ?? new AvatarCustomizationData();
            if (_avatarArt != null)
                _avatarArt.style.unityBackgroundImageTintColor = new StyleColor(BuildTintColor(effective.PrimaryColor, effective.Saturation, effective.Brightness));
            if (_previewHero != null)
                _previewHero.style.unityBackgroundImageTintColor = new StyleColor(BuildTintColor(effective.PrimaryColor, effective.Saturation, effective.Brightness));

            if (_avatarCircle != null)
            {
                _avatarCircle.style.borderBottomColor = new StyleColor(ParseHtmlColor(effective.SecondaryColor, new Color(0.486f, 0.478f, 0.929f)));
                _avatarCircle.style.borderTopColor = _avatarCircle.style.borderBottomColor;
                _avatarCircle.style.borderLeftColor = _avatarCircle.style.borderBottomColor;
                _avatarCircle.style.borderRightColor = _avatarCircle.style.borderBottomColor;
            }

            var halo = _root?.Q<VisualElement>("avatar-glow");
            if (halo != null)
            {
                var haloColor = ParseHtmlColor(effective.HaloColor, new Color(0.486f, 0.478f, 0.929f));
                haloColor.a = Mathf.Clamp01(effective.HaloIntensity) * 0.55f;
                halo.style.backgroundColor = new StyleColor(haloColor);
                halo.style.opacity = Mathf.Clamp(0.15f + effective.HaloIntensity, 0f, 1f);
            }

            string emoji = effective.OverlayEmoji ?? string.Empty;
            if (_avatarEmojiOverlay != null)
            {
                _avatarEmojiOverlay.text = emoji;
                SetDisplay(_avatarEmojiOverlay, string.IsNullOrEmpty(emoji) ? DisplayStyle.None : DisplayStyle.Flex);
            }
            if (_previewEmojiOverlay != null)
            {
                _previewEmojiOverlay.text = emoji;
                SetDisplay(_previewEmojiOverlay, string.IsNullOrEmpty(emoji) ? DisplayStyle.None : DisplayStyle.Flex);
            }

            SetFrameClass(_avatarCircle, effective.CustomFrame, "avatar-frame");
            SetFrameClass(_previewHero, effective.CustomFrame, "preview-frame");
        }

        private static void SetFrameClass(VisualElement element, string frame, string prefix)
        {
            if (element == null) return;
            element.EnableInClassList($"{prefix}--none", false);
            element.EnableInClassList($"{prefix}--neon", false);
            element.EnableInClassList($"{prefix}--gold", false);
            element.EnableInClassList($"{prefix}--holographic", false);
            string normalized = string.IsNullOrWhiteSpace(frame) ? "none" : frame.ToLowerInvariant();
            element.EnableInClassList($"{prefix}--{normalized}", true);
        }

        private static Color BuildTintColor(string hex, float saturation, float brightness)
        {
            var baseColor = ParseHtmlColor(hex, Color.white);
            float gray = baseColor.r * 0.299f + baseColor.g * 0.587f + baseColor.b * 0.114f;
            var saturated = new Color(
                Mathf.Clamp01(gray + (baseColor.r - gray) * Mathf.Clamp(saturation, 0f, 2f)),
                Mathf.Clamp01(gray + (baseColor.g - gray) * Mathf.Clamp(saturation, 0f, 2f)),
                Mathf.Clamp01(gray + (baseColor.b - gray) * Mathf.Clamp(saturation, 0f, 2f)),
                1f);
            float b = Mathf.Clamp(brightness, 0f, 2f);
            return new Color(
                Mathf.Clamp01(saturated.r * b),
                Mathf.Clamp01(saturated.g * b),
                Mathf.Clamp01(saturated.b * b),
                1f);
        }

        private static Color ParseHtmlColor(string hex, Color fallback)
        {
            if (!string.IsNullOrWhiteSpace(hex) && ColorUtility.TryParseHtmlString(hex, out var parsed))
                return parsed;
            return fallback;
        }

        private static AvatarCustomizationData CloneCustomization(AvatarCustomizationData source)
        {
            if (source == null) return null;
            return new AvatarCustomizationData
            {
                PrimaryColor = source.PrimaryColor,
                SecondaryColor = source.SecondaryColor,
                HaloColor = source.HaloColor,
                HaloIntensity = source.HaloIntensity,
                Saturation = source.Saturation,
                Brightness = source.Brightness,
                OverlayEmoji = source.OverlayEmoji,
                CustomFrame = source.CustomFrame
            };
        }

        private static bool IsCustomizationEffectivelyDefault(AvatarCustomizationData data)
        {
            if (data == null) return true;
            bool defaultColors = string.Equals((data.PrimaryColor ?? string.Empty).ToUpperInvariant(), "#FFFFFF", StringComparison.Ordinal)
                && string.Equals((data.SecondaryColor ?? string.Empty).ToUpperInvariant(), "#7C7AED", StringComparison.Ordinal)
                && string.Equals((data.HaloColor ?? string.Empty).ToUpperInvariant(), "#7C7AED", StringComparison.Ordinal);
            bool defaultScalars = Mathf.Abs(data.HaloIntensity - 0.6f) < 0.0001f
                && Mathf.Abs(data.Saturation - 1f) < 0.0001f
                && Mathf.Abs(data.Brightness - 1f) < 0.0001f;
            bool defaultOverlay = string.IsNullOrEmpty(data.OverlayEmoji)
                && (string.IsNullOrWhiteSpace(data.CustomFrame) || string.Equals(data.CustomFrame, "none", StringComparison.OrdinalIgnoreCase));
            return defaultColors && defaultScalars && defaultOverlay;
        }

        private void EnsureAvatarAnimationImage()
        {
            if (_avatarArt == null || _avatarArtImage != null)
                return;

            _avatarArtImage = new Image { name = "avatar-art-image" };
            _avatarArtImage.pickingMode = PickingMode.Ignore;
            _avatarArtImage.style.position = Position.Absolute;
            _avatarArtImage.style.left = 0;
            _avatarArtImage.style.right = 0;
            _avatarArtImage.style.top = 0;
            _avatarArtImage.style.bottom = 0;
            _avatarArtImage.scaleMode = ScaleMode.ScaleAndCrop;
            _avatarArt.Add(_avatarArtImage);

            _avatarAnimator = gameObject.GetComponent<SpriteSheetAnimator>();
            if (_avatarAnimator == null)
                _avatarAnimator = gameObject.AddComponent<SpriteSheetAnimator>();
        }

        private void EnsureAvatar3DImage()
        {
            if (_avatarArt == null || _avatar3DImage != null)
                return;

            _avatar3DImage = new Image { name = "avatar-art-3d-image" };
            _avatar3DImage.pickingMode = PickingMode.Position;
            _avatar3DImage.style.position = Position.Absolute;
            _avatar3DImage.style.left = 0;
            _avatar3DImage.style.right = 0;
            _avatar3DImage.style.top = 0;
            _avatar3DImage.style.bottom = 0;
            _avatar3DImage.scaleMode = ScaleMode.ScaleAndCrop;
            _avatarArt.Add(_avatar3DImage);

            _avatar3DRenderer = gameObject.GetComponent<Avatar3DRenderer>();
            if (_avatar3DRenderer == null)
                _avatar3DRenderer = gameObject.AddComponent<Avatar3DRenderer>();
            _avatar3DRenderer.AttachTargetImage(_avatar3DImage);

            if (_avatar3DService == null)
            {
                if (_app != null && _app.Services.TryGet<IAvatar3DService>(out var sharedService))
                    _avatar3DService = sharedService;
                else
                    _avatar3DService = new Avatar3DService();
            }

            SetDisplay(_avatar3DImage, DisplayStyle.None);
        }

        private bool ConfigureAvatarAnimation(AvatarProfile profile)
        {
            EnsureAvatarAnimationImage();
            EnsureAvatar3DImage();

            if (_avatarAnimator == null || _avatarArtImage == null)
            {
                NeonLogger.LogWarning("[AvatarAnim] EnsureAvatarAnimationImage failed: " +
                    "_avatarAnimator=" + (_avatarAnimator == null ? "null" : "ok") +
                    " _avatarArtImage=" + (_avatarArtImage == null ? "null" : "ok") +
                    " _avatarArt=" + (_avatarArt == null ? "null" : "ok"));
                return false;
            }

            // In Static gallery mode the user explicitly wants the still PNG, not animation.
            // Only load sprites when the Animated (or future 3D-anim) tab is active.
            if (_avatarViewMode != AvatarViewMode.Animated)
            {
                _avatarAnimator.Stop();
                _avatarArtImage.sprite = null;
                SetDisplay(_avatarArtImage, DisplayStyle.None);
                return false;
            }

            var resolvedMotion = AvatarMotionPackLoader.ResolveProfileMotion(profile) ?? new AvatarProfileMotionResolution();
            var clips = resolvedMotion.animationClips;
            NeonLogger.Log("[AvatarAnim] ResolveProfileMotion for '" + (profile?.id ?? "null") +
                "' -> clips=" + (clips?.Count.ToString() ?? "null") +
                " manifest=" + (resolvedMotion.sourceManifestPath ?? "none"));
            if (clips == null || clips.Count == 0)
            {
                _avatarAnimator.Stop();
                _avatarArtImage.sprite = null;
                SetDisplay(_avatarArtImage, DisplayStyle.None);
                if (_avatar3DImage != null)
                    SetDisplay(_avatar3DImage, DisplayStyle.None);
                return false;
            }

            _avatarAnimator.Configure(clips, _avatarArtImage);
            if (resolvedMotion.lipsyncClip != null)
                _avatarAnimator.RegisterClip(resolvedMotion.lipsyncClip);
            NeonLogger.Log("[AvatarAnim] After Configure: HasAnyClips=" + _avatarAnimator.HasAnyClips);
            if (!_avatarAnimator.HasAnyClips)
            {
                _avatarArtImage.sprite = null;
                SetDisplay(_avatarArtImage, DisplayStyle.None);
                return false;
            }

            SetDisplay(_avatarArtImage, DisplayStyle.Flex);
            if (_avatar3DImage != null)
                SetDisplay(_avatar3DImage, DisplayStyle.None);
            RefreshAvatarMotionState();

            return true;
        }

        private async Task ConfigureAvatar3DAsync(AvatarProfile profile)
        {
            Disable2DAvatarAnimation();
            EnsureAvatar3DImage();
            if (_avatar3DService == null || _avatar3DRenderer == null || _avatar3DImage == null)
                return;

            if (profile == null || string.IsNullOrWhiteSpace(profile.modelPath))
            {
                Disable3DAvatarRender();
                return;
            }

            bool loaded = await _avatar3DService.LoadAvatar(profile.modelPath);
            if (!loaded)
            {
                Disable3DAvatarRender();
                return;
            }

            var runtimeRoot = _avatar3DService.GetRuntimeRoot();
            if (runtimeRoot == null)
            {
                Disable3DAvatarRender();
                return;
            }

            runtimeRoot.transform.SetParent(transform, false);
            runtimeRoot.transform.localPosition = Vector3.zero;
            runtimeRoot.transform.localRotation = Quaternion.identity;
            runtimeRoot.transform.localScale = Vector3.one;

            _avatar3DRenderer.SetModelRoot(_avatar3DService.GetRuntimeTransform());
            SetDisplay(_avatar3DImage, DisplayStyle.Flex);
            RefreshAvatarMotionState();
        }

        private void Disable2DAvatarAnimation()
        {
            if (_avatarAnimator != null)
                _avatarAnimator.Stop();

            if (_avatarArtImage != null)
            {
                _avatarArtImage.sprite = null;
                SetDisplay(_avatarArtImage, DisplayStyle.None);
            }
        }

        private void Disable3DAvatarRender()
        {
            _avatar3DService?.Unload();
            _avatar3DRenderer?.ClearModel();
            if (_avatar3DImage != null)
                SetDisplay(_avatar3DImage, DisplayStyle.None);
        }

        private void SetAvatarMotionState(AvatarMotionState state)
        {
            _avatarMotionState = state;

            if (_avatar3DService != null && _avatar3DService.IsLoaded)
            {
                string clip3D = StateToClipName(state);
                if (!_avatar3DService.SetAnimation(clip3D))
                    _avatar3DService.SetAnimation("idle");
                return;
            }

            if (_avatarAnimator == null || !_avatarAnimator.HasAnyClips)
                return;

            string clip2D = ResolveAvailableClipForState(state);
            _avatarAnimator.Play(clip2D);
        }

        private void RefreshAvatarMotionState()
        {
            if (_avatarAnimator != null && _avatarAnimator.IsPlayingOneShot)
                return;

            if (_isVoiceRecording)
            {
                SetAvatarMotionState(AvatarMotionState.Listening);
                return;
            }

            if (_isVoicePlaying)
            {
                SetAvatarMotionState(AvatarMotionState.Talking);
                return;
            }

            if (_isSending)
            {
                SetAvatarMotionState(AvatarMotionState.Thinking);
                return;
            }

            SetAvatarMotionState(AvatarMotionState.Idle);
        }

        private string ResolveAvailableClipForState(AvatarMotionState state)
        {
            string preferred = StateToClipName(state);
            if (_avatarAnimator != null && _avatarAnimator.HasClip(preferred))
                return preferred;

            return "idle";
        }

        private static string StateToClipName(AvatarMotionState state)
        {
            switch (state)
            {
                case AvatarMotionState.Thinking:
                    return "thinking";
                case AvatarMotionState.Talking:
                    return "talking";
                case AvatarMotionState.Listening:
                    return "listening";
                default:
                    return "idle";
            }
        }

        private void PlayAvatarReaction(string reactionClipName)
        {
            if (string.IsNullOrWhiteSpace(reactionClipName))
                return;

            if (_avatar3DService != null && _avatar3DService.IsLoaded)
            {
                NeonLogger.LogWarning("3D avatar reaction is not implemented for clip '" + reactionClipName + "'.");
                return;
            }

            if (_avatarAnimator == null || !_avatarAnimator.HasAnyClips)
                return;

            if (!_avatarAnimator.HasClip(reactionClipName))
                return;

            _avatarAnimator.PlayOneShot(reactionClipName, RefreshAvatarMotionState);
        }

        private void TriggerAvatarSmile()
        {
            PlayAvatarReaction("smile");
        }

        private void TriggerAvatarConfused()
        {
            PlayAvatarReaction("confused");
        }

        private static string BuildAnimationInfoText(AvatarProfile profile)
        {
            if (profile != null && profile.is3D)
            {
                if (profile.modelAnimationClips == null || profile.modelAnimationClips.Count == 0)
                    return LocalizationExtensions.Get("avatar.animation.3d_model", "3D модель");

                return LocalizationExtensions.GetFormat("avatar.animation.3d_animations", "3D анимации: {0}", string.Join(", ", profile.modelAnimationClips));
            }

            var motion = AvatarMotionPackLoader.ResolveProfileMotion(profile) ?? new AvatarProfileMotionResolution();
            var clips = motion.animationClips;
            if (clips == null || clips.Count == 0)
                return LocalizationExtensions.Get("avatar.animation.static", "Статичное изображение");

            var parts = new List<string>();
            for (int i = 0; i < clips.Count; i++)
            {
                var clip = clips[i];
                if (clip == null || string.IsNullOrWhiteSpace(clip.clipName))
                    continue;

                float fps = clip.frameRate > 0f ? clip.frameRate : 1f;
                parts.Add($"{clip.clipName} · {fps:0.#}fps");
            }

            return parts.Count > 0 ? string.Join(", ", parts) : LocalizationExtensions.Get("avatar.animation.static", "Статичное изображение");
        }

        private void UpdateAvatarActionButtons(string avatarId)
        {
            bool isCustom = GetCustomProfile(avatarId) != null;
            bool hasOverride = HasPersonaOverride(avatarId);
            SetDisplay(_previewResetPersonaBtn, hasOverride ? DisplayStyle.Flex : DisplayStyle.None);
            SetDisplay(_previewDeleteAvatarBtn, isCustom ? DisplayStyle.Flex : DisplayStyle.None);
        }

        private bool HasPersonaOverride(string avatarId)
        {
            var stored = GetStoredProfile(avatarId);
            return stored != null && !string.IsNullOrWhiteSpace(stored.systemPrompt);
        }

        private string PersonaEditorText(string avatarId)
        {
            var stored = GetStoredProfile(avatarId);
            if (stored != null && !string.IsNullOrWhiteSpace(stored.systemPrompt))
                return stored.systemPrompt;

            if (BuiltInAvatarMetaById.TryGetValue(avatarId, out var meta))
                return LocalizationExtensions.Get(meta.PersonaKey, meta.PersonaFallback);

            return string.Empty;
        }

        private async Task ResetPersonaOverrideAsync()
        {
            try
            {
                var app = await GetAppAsync();
                if (app == null) return;

                var profiles = app.Avatars.GetAll();
                var profile = profiles.FirstOrDefault(a => a.id == _activeAvatarId);
                if (profile == null || string.IsNullOrWhiteSpace(profile.systemPrompt))
                {
                    UpdateAvatarActionButtons(_activeAvatarId);
                    return;
                }

                bool isBuiltIn = Array.IndexOf(BuiltInAvatarIds, _activeAvatarId) >= 0;
                if (isBuiltIn)
                {
                    profiles.RemoveAll(a => a.id == _activeAvatarId && a.isBuiltIn);
                }
                else
                {
                    profile.systemPrompt = string.Empty;
                }

                app.Avatars.SaveAll(profiles);
                UpdateAvatarProfileCaches(profiles);

                if (_previewPersona != null)
                    _previewPersona.text = AvatarPersonaText(_activeAvatarId);
                UpdatePersonaStateUi(_activeAvatarId);
                UpdateAvatarActionButtons(_activeAvatarId);

                await SyncActiveAvatarSystemPromptAsync(app);
            }
            catch (Exception ex)
            {
                NeonLogger.LogError(ex.ToString());
            }
        }

        private string AvatarStyleTag(string avatarId)
        {
            if (BuiltInAvatarMetaById.TryGetValue(avatarId, out var meta))
                return LocalizationExtensions.Get(meta.StyleTagKey, meta.StyleTagFallback);

            return LocalizationExtensions.Get("avatar.style.custom", "пользовательский");
        }

        private void UpdatePersonaStateUi(string avatarId)
        {
            if (_previewPersonaStateBadge == null || _previewPersonaStateHelp == null)
                return;

            bool isCustom = GetCustomProfile(avatarId) != null;
            bool hasOverride = HasPersonaOverride(avatarId);

            if (hasOverride)
            {
                _previewPersonaStateBadge.text = LocalizationExtensions.Get("avatar.persona.state.local.badge", "Локальные инструкции");
                _previewPersonaStateHelp.text = LocalizationExtensions.Get("avatar.persona.state.local.help", "Сейчас используется сохранённый локально system prompt для этого аватара.");
                return;
            }

            if (isCustom)
            {
                _previewPersonaStateBadge.text = LocalizationExtensions.Get("avatar.persona.state.missing.badge", "Инструкции не заданы");
                _previewPersonaStateHelp.text = LocalizationExtensions.Get("avatar.persona.state.missing.help", "Для этого пользовательского аватара system prompt сейчас не применяется.");
                return;
            }

            _previewPersonaStateBadge.text = LocalizationExtensions.Get("avatar.persona.state.builtin.badge", "Встроенные инструкции");
            _previewPersonaStateHelp.text = LocalizationExtensions.Get("avatar.persona.state.builtin.help", "Сейчас используются встроенные инструкции по умолчанию.");
        }

        private string AvatarPersonaText(string avatarId)
        {
            var stored = GetStoredProfile(avatarId);
            if (stored != null && !string.IsNullOrWhiteSpace(stored.systemPrompt))
                return stored.systemPrompt;

            if (BuiltInAvatarMetaById.TryGetValue(avatarId, out var meta))
                return LocalizationExtensions.Get(meta.PersonaKey, meta.PersonaFallback);

            if (GetCustomProfile(avatarId) != null)
                return LocalizationExtensions.Get("avatar.persona.custom.missing", "Инструкции не заданы. Нажми «Изменить», чтобы добавить текст для system prompt.");

            var fallbackMeta = BuiltInAvatarMetaById["neon"];
            return LocalizationExtensions.Get(fallbackMeta.PersonaKey, fallbackMeta.PersonaFallback);
        }

        private string AvatarDisplayName(string avatarId)
        {
            var custom = GetCustomProfile(avatarId);
            if (custom != null && !string.IsNullOrWhiteSpace(custom.name))
                return custom.name;

            if (BuiltInAvatarMetaById.TryGetValue(avatarId, out var meta))
                return LocalizationExtensions.Get(meta.DisplayNameKey, meta.DisplayNameFallback);
            var fallbackMeta = BuiltInAvatarMetaById["neon"];
            return LocalizationExtensions.Get(fallbackMeta.DisplayNameKey, fallbackMeta.DisplayNameFallback);
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

            bool isStillReferenced = profiles.Any(a =>
                a != null &&
                !a.isBuiltIn &&
                (string.Equals(a.imagePath, path, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(a.modelPath, path, StringComparison.OrdinalIgnoreCase)));
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
            public BuiltInAvatarMeta(
                string displayNameKey,
                string displayNameFallback,
                string styleTagKey,
                string styleTagFallback,
                string personaKey,
                string personaFallback,
                AvatarFilter filter)
            {
                DisplayNameKey = displayNameKey;
                DisplayNameFallback = displayNameFallback;
                StyleTagKey = styleTagKey;
                StyleTagFallback = styleTagFallback;
                PersonaKey = personaKey;
                PersonaFallback = personaFallback;
                Filter = filter;
            }

            public string DisplayNameKey { get; }
            public string DisplayNameFallback { get; }
            public string StyleTagKey { get; }
            public string StyleTagFallback { get; }
            public string PersonaKey { get; }
            public string PersonaFallback { get; }
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
            AddSystemMessage(LocalizationExtensions.Get("system.open.github", "Открыт GitHub репозитория."));
        }

        private void OnSettingsDocsClicked()
        {
            OpenExternalUrl("https://github.com/isteelfelix/neon-companion/tree/main/docs");
            AddSystemMessage(LocalizationExtensions.Get("system.open.docs", "Открыта папка docs."));
        }

        private void OnSettingsDonateClicked()
        {
            if (_donationService?.IsDonationSupported == true)
            {
                _donationService.OpenDonationPage();
                AddSystemMessage(LocalizationExtensions.Get("system.open.donate", "Открыта страница поддержки проекта."));
                return;
            }

            AddSystemMessage(LocalizationExtensions.Get("system.donate.unavailable", "Поддержка проекта пока недоступна."));
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
                string fileName = System.IO.Path.GetFileName(path);

                if (_subtitleBody != null)
                    _subtitleBody.text = LocalizationExtensions.GetFormat("system.export.subtitle", "Экспортировано: {0}", fileName);

                AddSystemMessage(LocalizationExtensions.GetFormat("system.export.chats", "Чаты экспортированы в {0}.", fileName));
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
                AddSystemMessage(LocalizationExtensions.Get("system.clear.confirm_hint", "Нажми «Подтвердить» ещё раз в течение 7 секунд, чтобы удалить все данные."));
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
                _settingsClearBtnText.text = armed
                    ? LocalizationExtensions.Get("settings.clear.confirm", "Подтвердить")
                    : LocalizationExtensions.Get("chat.clear", "Очистить");

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
                if (_historySessionsList != null) _historySessionsList.Clear();
                ShowHistoryState(LocalizationExtensions.Get("history.empty.first_session", "История пуста. Начните чат, чтобы появилась первая сессия."), isError: false);
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
                    AddSystemMessage(LocalizationExtensions.Get("system.copy.success", "Скопировано в буфер обмена."));
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
                    TriggerAvatarSmile();
                }
                finally
                {
                    SetSending(false);
                }
            }
            catch (Exception ex)
            {
                AddSystemMessage(LocalizationExtensions.Get("system.regenerate.failed", "Не удалось пересоздать последний ответ."));
                NeonLogger.LogError(ex.ToString());
                TriggerAvatarConfused();
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
                    AddSystemMessage(LocalizationExtensions.Get("providers.import.empty", "Файл не содержит провайдеров."));
                    return;
                }

                foreach (var p in imported.items)
                {
                    if (!string.IsNullOrEmpty(p?.id))
                        await app.ProviderManager.SaveProviderAsync(p);
                }

                await RefreshProvidersListAsync();
                AddSystemMessage(LocalizationExtensions.GetFormat("providers.import.success", "Импортировано: {0} провайдер(ов).", imported.items.Count));
            }
            catch (Exception ex)
            {
                AddSystemMessage(LocalizationExtensions.Get("providers.import.failed", "Не удалось импортировать провайдеров из файла."));
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
                string path = await filePicker.PickFileAsync("png,glb,gltf");
                if (string.IsNullOrEmpty(path)) return;

                string fileName = System.IO.Path.GetFileNameWithoutExtension(path);
                string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
                bool is3D = extension == ".glb" || extension == ".gltf";
                string destDir  = System.IO.Path.Combine(Application.persistentDataPath, "Avatars");
                System.IO.Directory.CreateDirectory(destDir);
                string destPath = System.IO.Path.Combine(destDir, System.IO.Path.GetFileName(path));
                System.IO.File.Copy(path, destPath, overwrite: true);

                var all = app.Avatars.GetAll();
                string profileId = ResolveCustomAvatarId(fileName, destPath, all);
                var modelAnimations = new List<string>();

                if (is3D)
                {
                    var loadResult = await Avatar3DLoader.LoadAsync(destPath);
                    if (loadResult.Success && loadResult.Instance != null)
                    {
                        modelAnimations.AddRange(loadResult.AnimationNames);
                        Destroy(loadResult.Instance);
                    }
                }

                var profile = new NeonCompanion.Runtime.Data.Models.AvatarProfile
                {
                    id        = profileId,
                    name      = fileName,
                    imagePath = is3D ? string.Empty : destPath,
                    modelPath = is3D ? destPath : string.Empty,
                    isBuiltIn = false,
                    is3D = is3D,
                    modelAnimationClips = modelAnimations
                };

                int existing = all.FindIndex(a => a != null && a.id == profile.id);
                if (existing >= 0) all[existing] = profile;
                else all.Add(profile);
                app.Avatars.SaveAll(all);

                RefreshCustomAvatarGallery(app);

                // Auto-select the uploaded avatar (force re-selection even if same ID)
                if (_activeAvatarId == profile.id) _activeAvatarId = string.Empty;
                SelectAvatar(profile.id);

                AddSystemMessage(is3D
                    ? LocalizationExtensions.GetFormat("avatar.upload.success.3d", "3D аватар «{0}» загружен.", fileName)
                    : LocalizationExtensions.GetFormat("avatar.upload.success", "Аватар «{0}» загружен.", fileName));
            }
            catch (Exception ex)
            {
                AddSystemMessage(LocalizationExtensions.Get("avatar.upload.failed", "Не удалось загрузить аватар."));
                NeonLogger.LogError(ex.ToString());
            }
        }

        private string ResolveCustomAvatarId(string fileName, string imagePath, List<AvatarProfile> allProfiles)
        {
            string existingByPath = allProfiles?
                .FirstOrDefault(a =>
                    a != null &&
                    !a.isBuiltIn &&
                    (string.Equals(a.imagePath, imagePath, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a.modelPath, imagePath, StringComparison.OrdinalIgnoreCase)))
                ?.id;
            if (!string.IsNullOrWhiteSpace(existingByPath))
                return existingByPath;

            string baseId = BuildCustomAvatarBaseId(fileName);
            string candidate = baseId;
            int suffix = 2;

            while (ContainsAvatarId(candidate, allProfiles))
            {
                candidate = $"{baseId}_{suffix}";
                suffix++;
            }

            return candidate;
        }

        private static bool ContainsAvatarId(string candidateId, List<AvatarProfile> allProfiles)
        {
            if (string.IsNullOrWhiteSpace(candidateId))
                return false;

            if (Array.IndexOf(BuiltInAvatarIds, candidateId) >= 0)
                return true;

            return allProfiles != null && allProfiles.Any(a => a != null && string.Equals(a.id, candidateId, StringComparison.Ordinal));
        }

        private static string BuildCustomAvatarBaseId(string fileName)
        {
            string source = (fileName ?? string.Empty).Trim().ToLowerInvariant();
            var sb = new StringBuilder(source.Length);
            bool previousWasSeparator = false;

            foreach (char ch in source)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(ch);
                    previousWasSeparator = false;
                    continue;
                }

                if (!previousWasSeparator)
                {
                    sb.Append('_');
                    previousWasSeparator = true;
                }
            }

            string normalized = sb.ToString().Trim('_');
            if (string.IsNullOrWhiteSpace(normalized))
                normalized = "avatar";

            return $"custom_{normalized}";
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
