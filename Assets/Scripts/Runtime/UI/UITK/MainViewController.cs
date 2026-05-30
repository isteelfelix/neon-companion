using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Avatar;
using NeonCompanion.Runtime.Avatar3D;
using NeonCompanion.Runtime.Chat;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Donation;
using NeonCompanion.Runtime.Localization;
using NeonCompanion.Runtime.Platform;
using NeonCompanion.Runtime.UI.Avatars;
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
        private Button _newSessionButton;
        private Button _exportButton;
        private Button _scrollBottomBtn;
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

        // copy-btn, refresh-btn, listen-btn removed — now in bubble hover
        private Button _micButton;
        private Button _attachButton;
        private Button _stopButton;
        private Button _avatarUploadBtn;
        private Button _avatarOpenFolderBtn;
        private VisualElement _avatarUploadTile;

        private CompanionApp _app;
        private ChatService _chatService;
        private string _currentSessionId = string.Empty;
        private string _currentSessionTitle = string.Empty;
        private bool _isBound;
        private Image _avatarArtImage;
        private Image _avatar3DImage;
        private SpriteSheetAnimator _avatarAnimator;
        private AvatarAnimationController _avatarAnimationController;
        private AudioSource _notifySource; // U-40 notification sounds
        private AudioClip _notifyClip;
        private Avatar3DRenderer _avatar3DRenderer;
        private IAvatar3DService _avatar3DService;
        private bool _isRefreshingLocalizedUi;
        private AvatarMotionState _avatarMotionState = AvatarMotionState.Idle;
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
            var document = GetComponent<UIDocument>();
            if (document == null || document.rootVisualElement == null)
                return;

            Bind(document.rootVisualElement);
            RegisterCallbacks();
            _ = _settingsController.BindLocalizationEventsAsync();
            _navigationController.ShowChat();

            _ = RefreshAsync();
        }

        private void OnDisable()
        {
            UnregisterCallbacks();
            _voiceController.OnDisable();
            _avatarMotionState = AvatarMotionState.Idle;
            _settingsController.UnbindLocalizationEvents();
            _avatarGalleryController.OnDisable();
            _layoutController.OnDisable();
            _avatarAnimator?.Stop();
            _avatar3DService?.Unload();
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
            _railResizeHandle = root.Q<VisualElement>("rail-resize-handle");
            _topbarSep = root.Q<VisualElement>("topbar-sep");
            _typingIndicator = root.Q<VisualElement>("typing-indicator");

            _topbarTitle = root.Q<Label>("topbar-title");
            _topbarSubtitle = root.Q<Label>("topbar-subtitle");
            _placeholderTitle = root.Q<Label>("placeholder-title");
            _placeholderBody = root.Q<Label>("placeholder-body");

            _messageInput = root.Q<TextField>("message-input");
            if (_messageInput != null)
            {
                _messageInput.multiline = true;
                _messageInput.verticalScrollerVisibility = ScrollerVisibility.Auto;
            }
            _sendButton = root.Q<Button>("send-button");
            _summarizeButton = root.Q<Button>("summarize-btn");
            _searchButton = root.Q<Button>("search-btn");
            _moreButton = root.Q<Button>("more-btn");
            _newSessionButton = root.Q<Button>("new-session-btn");
            _exportButton = root.Q<Button>("export-btn");
            _messagesList = root.Q<ScrollView>("messages-list");
            _scrollBottomBtn = root.Q<Button>("scroll-bottom-btn");
            _sessionsList = root.Q<ScrollView>("sessions-list");
            _historySessionsList = root.Q<ScrollView>("history-panel-sessions-list");

            // U-10: add visible app brand icon in sidebar header (dynamic, no UXML change)
            var railHead = root.Q(className: "rail__sessions-head");
            if (railHead != null)
            {
                var brand = new Label("N");
                brand.AddToClassList("rail__brand-icon");
                brand.style.fontSize = 11;
                brand.style.unityFontStyleAndWeight = FontStyle.Bold;
                brand.style.color = new Color(0.49f, 0.48f, 0.93f, 1f); // accent indigo
                brand.style.marginRight = 6;
                brand.style.marginLeft = 4;
                brand.style.alignSelf = Align.Center;
                // Insert as first child so it sits left of "Сессии" label
                railHead.Insert(0, brand);
            }
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
            _stopButton = root.Q<Button>("stop-button");
            _avatarUploadBtn      = root.Q<Button>("avatar-upload-btn");
            _avatarOpenFolderBtn  = root.Q<Button>("avatar-open-folder-btn");
            _avatarUploadTile     = root.Q<VisualElement>("avtile-upload");
            _galleryContainer     = _avatarsPanel?.Q<VisualElement>(className: "gallery");
            _navAvatarsCount      = _navAvatars?.Q<Label>(className: "nav__count");

            ApplyLocalizedStaticTexts();

            // Avatar elements
            _avatarArt       = root.Q<VisualElement>("avatar-art");
            _avatarCircle    = root.Q<VisualElement>("avatar-circle");
            _avatarStageHero = root.Q<VisualElement>("avatar-stage-hero");
            _avatarGlow      = root.Q<VisualElement>("avatar-glow");
            _thinkingBubble  = root.Q<VisualElement>("thinking-bubble");
            _thinkingText    = root.Q<Label>("thinking-text");
            _avatarShade     = _avatarCircle?.Q<VisualElement>(className: "avatar__shade");
            _avatarLetter    = _avatarCircle?.Q<Label>(className: "avatar__letter");
            _previewHero  = root.Q<VisualElement>("preview-hero");
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

            _chatController.InitState();
            _isBound = true;
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
                    _activeAvatarId = id;
                },
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
                MessageInput = _messageInput,
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
                TriggerAvatarSmile = _avatarGalleryController.TriggerAvatarSmile,
                TriggerAvatarConfused = _avatarGalleryController.TriggerAvatarConfused,
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
                PlayNotificationSound = PlayNotificationBeep
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
                RenderMessages = () => RenderMessages(null),
                ShowSystemMessage = AddSystemMessage,
                ShowHistoryState = ShowHistoryState,
                ShowChat = _navigationController.ShowChat,
                ClearPendingComposerAttachments = () => _chatController.ClearPendingComposerAttachments(),
                GetMessageInput = () => _messageInput,
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
                EditMaxTokens        = _root.Q<TextField>("edit-maxtokens"),
                EditTemperature      = _root.Q<Slider>("edit-temperature"),
                EditBackendType      = _root.Q<NeonDropdown>("edit-backend-type"),
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
                Root                 = _root,
                GetAppAsync          = GetAppAsync,
                GetChatServiceAsync  = GetChatServiceAsync,
                GetChatServiceSync   = () => _chatService,
                IsBound              = () => _isBound,
                SaveSettings         = () => _settingsController.SaveSettings(),
                LoadSessionsAsync    = () => LoadSessionsAsync(_chatService),
                RenderMessages       = () => RenderMessages(null),
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
                GetAppAsync = GetAppAsync,
                GetAppSync = () => _app,
                IsBound = () => _isBound,
                GetOrCreateAnimator = () =>
                {
                    if (_avatarAnimator == null)
                    {
                        _avatarAnimator = gameObject.GetComponent<SpriteSheetAnimator>();
                        if (_avatarAnimator == null)
                            _avatarAnimator = gameObject.AddComponent<SpriteSheetAnimator>();
                    }
                    if (_avatarAnimationController == null)
                    {
                        _avatarAnimationController = gameObject.GetComponent<AvatarAnimationController>();
                        if (_avatarAnimationController == null)
                            _avatarAnimationController = gameObject.AddComponent<AvatarAnimationController>();
                    }
                    return _avatarAnimator;
                },
                GetOrCreateAvatar3DRenderer = () =>
                {
                    if (_avatar3DRenderer == null)
                    {
                        _avatar3DRenderer = gameObject.GetComponent<Avatar3DRenderer>();
                        if (_avatar3DRenderer == null)
                            _avatar3DRenderer = gameObject.AddComponent<Avatar3DRenderer>();
                    }
                    return _avatar3DRenderer;
                },
                ModelParent = transform
            };
        }

        private VoiceController.Deps BuildVoiceControllerDeps()
        {
            return new VoiceController.Deps
            {
                gameObject = gameObject,
                MicButton = _micButton,
                IsVoiceEnabledBySettings = IsVoiceEnabledBySettings,
                SendVoiceMessageAsync = SendVoiceMessageAsync,
                OnVoiceRecordingStarted = OnVoiceRecordingStarted,
                RefreshAvatarMotionState = _avatarGalleryController.RefreshAvatarMotionState,
                GetChatServiceAsync = GetChatServiceAsync,
                GetChatServiceSync = () => _chatService,
                IsBound = () => _isBound
            };
        }

        private LayoutController.Deps BuildLayoutControllerDeps()
        {
            return new LayoutController.Deps
            {
                Root = _root,
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
            RegisterClick(_historyPanelNewSessionButton, OnHistoryPanelNewSessionClicked);
            RegisterClick(_historySearchBtn, OnHistorySearchToggled);
            RegisterClick(_historySearchClear, OnHistorySearchCleared);
            RegisterClick(_historyPanelSearchBtn, OnHistorySearchToggled);
            RegisterClick(_historyPanelSearchClear, OnHistorySearchCleared);
            if (_historySearchInput != null)
                _historySearchInput.RegisterCallback<ChangeEvent<string>>(OnHistorySearchChanged);
            if (_historyPanelSearchInput != null)
                _historyPanelSearchInput.RegisterCallback<ChangeEvent<string>>(OnHistorySearchChanged);
            ChatController.ListenRequested += OnListenClicked;

            if (_messagesList != null)
            {
                _scrollBottomButtonSchedule?.Pause();
                _scrollBottomButtonSchedule = _messagesList.schedule.Execute(UpdateScrollBottomButton).Every(200);
            }

            _settingsController.RegisterCallbacks();
        }

        private void UnregisterCallbacks()
        {
            _chatController.UnregisterCallbacks();
            _avatarGalleryController.UnregisterCallbacks();

            UnregisterClick(_moreButton, OnMoreClicked);
            UnregisterClick(_historyPanelNewSessionButton, OnHistoryPanelNewSessionClicked);
            UnregisterClick(_historySearchBtn, OnHistorySearchToggled);
            UnregisterClick(_historySearchClear, OnHistorySearchCleared);
            UnregisterClick(_historyPanelSearchBtn, OnHistorySearchToggled);
            UnregisterClick(_historyPanelSearchClear, OnHistorySearchCleared);
            ChatController.ListenRequested -= OnListenClicked;

            _settingsController.UnregisterCallbacks();
            _voiceController.UnregisterCallbacks();
            _providersController.UnregisterCallbacks();

            _typingSchedule?.Pause();
            _scrollBottomButtonSchedule?.Pause();
            _scrollBottomButtonSchedule = null;
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

        private void SetPlaceholder(string title, string body)
        {
            SetTopbar(title, string.Empty);
            if (_placeholderTitle != null)
                _placeholderTitle.text = title;
            if (_placeholderBody != null)
                _placeholderBody.text = body;
            ShowArea(_placeholderArea);
        }

        private void ShowArea(VisualElement visible) => _layoutController.ShowArea(visible);

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

        private static void SetDisplay(VisualElement element, DisplayStyle display)
        {
            if (element != null)
                element.style.display = display;
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
                _messagesList.Add(ChatController.CreateMessageElement(msg));
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
                    _voiceController.EnqueueVoiceResponse(msg.content);
                    return;
                }
            }

            AddSystemMessage(LocalizationExtensions.Get("system.voice.no_assistant_reply", "Нет ответа ассистента для озвучивания."));
        }

        private async Task EnsureVoicePipelineAsync(ChatService chat) => await _voiceController.EnsureVoicePipelineAsync(chat);

        private void OnVoiceRecordingStarted() => _voiceController.OnVoiceRecordingStarted();

        private async Task SendVoiceMessageAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || _chatController.IsSending)
                return;

            if (_messageInput != null)
                _messageInput.value = text.Trim();

            await SendCurrentMessageAsync();
        }

        private bool IsVoiceEnabledBySettings()
        {
            return _settingsController.VoiceIoEnabled;
        }

        private void RefreshVoiceControls() => _voiceController.RefreshVoiceControls();

        private void BindVoiceAnimationEvents() => _voiceController.BindVoiceAnimationEvents();

        private void UnbindVoiceAnimationEvents() => _voiceController.UnbindVoiceAnimationEvents();

        private async Task RefreshAsync()
        {
            try
            {
                var app = await GetAppAsync();
                if (!_isBound || app == null) return;
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

                if (_previewPersona != null)
                    _previewPersona.text = _avatarGalleryController.AvatarPersonaText(activeAvatarId);
                _avatarGalleryController.UpdatePersonaStateUi(activeAvatarId);
                _avatarGalleryController.UpdateAvatarActionButtons(activeAvatarId);
                _avatarGalleryController.RefreshBuiltInAvatarTileLabels();
                _avatarGalleryController.UpdateAvatarFilterCounts();
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
                SetTopbar(LocalizationExtensions.Get("topbar.providers.title", "Провайдеры"), LocalizationExtensions.Get("topbar.providers.subtitle", "OpenAI-совместимые провайдеры"));
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
                _settingsController.SaveSettings();
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
            string updatedSubtitle = _chatController.ChatSubtitle.Contains("·")
                ? _chatController.ChatSubtitle.Substring(0, _chatController.ChatSubtitle.LastIndexOf('·') + 2) + name
                : _chatController.ChatSubtitle;
            _chatController.SetChatSubtitle(updatedSubtitle);
            if (_topbarSubtitle != null) _topbarSubtitle.text = _chatController.ChatSubtitle;
            _settingsController.SaveSettings();
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

                await _settingsController.SyncActiveAvatarSystemPromptAsync(app);

                _settingsController.SaveSettings();
                _navigationController.ShowChat();
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

                await _settingsController.SyncActiveAvatarSystemPromptAsync(app);

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

                await _settingsController.SyncActiveAvatarSystemPromptAsync(app);

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

            _avatarAnimationController = gameObject.GetComponent<AvatarAnimationController>();
            if (_avatarAnimationController == null)
                _avatarAnimationController = gameObject.AddComponent<AvatarAnimationController>();

            // U-40: ensure notification sound source (runtime tone, no asset required)
            _notifySource = gameObject.GetComponent<AudioSource>();
            if (_notifySource == null)
                _notifySource = gameObject.AddComponent<AudioSource>();
            _notifySource.playOnAwake = false;
            _notifySource.volume = 0.15f;
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
            if (_avatarAnimationController != null)
                _avatarAnimationController.SetAnimator(_avatarAnimator);
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

            if (_voiceController.IsVoiceRecording)
            {
                SetAvatarMotionState(AvatarMotionState.Listening);
                return;
            }

            if (_voiceController.IsVoicePlaying)
            {
                SetAvatarMotionState(AvatarMotionState.Talking);
                return;
            }

            if (_chatController.IsSending)
            {
                SetAvatarMotionState(_chatController.IsStreamingResponse ? AvatarMotionState.Talking : AvatarMotionState.Thinking);
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

            _avatarAnimator.PlayOneShot(reactionClipName, RefreshAvatarMotionState, true);
        }

        private void TriggerAvatarSmile()
        {
            PlayAvatarReaction("smile");
        }

        private void TriggerAvatarConfused()
        {
            PlayAvatarReaction("confused");
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

                await _settingsController.SyncActiveAvatarSystemPromptAsync(app);
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

    }
}
