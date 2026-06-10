using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Chat;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Donation;
using NeonCompanion.Runtime.Localization;
using NeonCompanion.Runtime.Plugins;
using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK
{
    internal sealed class SettingsControllerDeps
    {
        public Func<Task<CompanionApp>> GetApp;
        public Func<Task<ChatService>> GetChatService;
        public Func<ChatService> GetChatServiceSync;
        public Func<bool> IsActiveAndEnabled;
        public Func<bool> IsBound;
        public Func<string> GetActiveAvatarId;
        public Action<string> SetActiveAvatarId;
        public Func<string> GetAvatarViewMode;
        public Action<string> SetAvatarViewMode;
        public Action RefreshVoiceControls;
        public Func<Task> RequestRefreshLocalizedUi;
        public Action<CompanionApp> RefreshCustomAvatarGallery;
        public Action RefreshBuiltInAvatarTileLabels;
        public Action ApplyAvatarFilter;
        public Action<string> ApplyAvatarArt;
        public Action<string> SyncGallerySelection;
        public Action<string, bool> ShowHistoryState;
        public Action RequestRenderMessages;
        public Action<string> AddSystemMessage;
        public Action SetNoProviderState;
        public Action ResetServiceCache;
        public Action ClearSessionsListUi;
        public Action ResetProvidersCountUi;
        public Action<string> SetCurrentSessionId;
        public Action<string> SetCurrentSessionTitle;
    }

    internal sealed class SettingsController
    {
        // ===== Deps =====
        private SettingsControllerDeps _deps;
        private VisualElement _root;
        private VisualElement _avatarCircle;

        // ===== Settings UI =====
        private NeonDropdown _settingsLanguage;
        private NeonDropdown _settingsToolPermission;
        private Toggle _settingsHistory;
        private Toggle _settingsStreaming;
        private Toggle _settingsEnterToSend;
        private Toggle _settingsSystemPrompt;
        private Toggle _settingsVoiceIo;
        private Toggle _settingsVoiceAlways;
        private Toggle _settingsEncryptKeys;
        private Toggle _settingsMaskLogs;
        private Toggle _settingsShowHalo;
        private Toggle _settingsBreathing;
        private Label _settingsStoragePath;
        private Label _settingsVersion;
        private Label _settingsPluginsSummary;
        private Label _settingsPluginsConfig;
        private VisualElement _settingsPluginsList;
        private Label _brandVersion;
        private bool _settingsLoaded;
        private bool _settingsLoading;
        private Button _shapeRound;
        private Button _shapeSquare;
        private Button _shapeHex;

        // ===== Voice device UI =====
        private VisualElement _settingsVoiceSection;
        private NeonDropdown _settingsInputDevice;
        private Slider _settingsOutputVolume;

        // ===== Settings action buttons =====
        private Button _settingsOpenFolderBtn;
        private Button _settingsExportBtn;
        private Button _settingsClearChatsBtn;
        private Button _settingsClearBtn;
        private Label _settingsClearBtnText;
        private Button _settingsGithubBtn;
        private Button _settingsDocsBtn;
        private Button _settingsDonateBtn;
        private Button _settingsExitBtn;

        // ===== Hotkey / quit dialog =====
        private Button _navCloseButton;
        private VisualElement _quitOverlay;
        private Label _quitDialogTitle;
        private Label _quitDialogBody;
        private Button _quitConfirmBtn;
        private Button _quitCancelBtn;
        private Button _settingsHotkeyCaptureBtn;

        // ===== Themes preview =====
        private VisualElement _themesPreviewHalo;
        private VisualElement _themesPreviewAvatar;
        private long _themesBreathStartMs;
        private IVisualElementScheduledItem _themesBreathSchedule;

        // ===== Breathing animation (avatar circle) =====
        private IVisualElementScheduledItem _breathSchedule;
        private long _breathStartMs;

        // ===== Clear confirm =====
        private bool _clearDataConfirmPending;
        private long _clearDataConfirmExpiresAtMs;
        private IVisualElementScheduledItem _clearDataConfirmResetSchedule;

        // ===== Localization =====
        private ILocalizationService _localizationService;

        // ===== Donation =====
        private IDonationService _donationService;

        // ===== Owned state =====
        private string _avatarShape = "round";
        private string _closeHotkey = "Escape";
        private bool _isCapturingHotkey;
        private bool _quitDialogVisible;

        // ===== Exposed read-only properties =====
        internal bool UseStreaming => _settingsStreaming != null && _settingsStreaming.value;
        internal bool EnterToSend => _settingsEnterToSend == null || _settingsEnterToSend.value;
        internal bool VoiceIoEnabled => _settingsVoiceIo != null && _settingsVoiceIo.value;
        internal bool VoiceAlwaysReply => _settingsVoiceAlways != null && _settingsVoiceAlways.value;
        internal string CurrentLanguage => _localizationService?.CurrentLanguage;

        // ============================================================
        // Init
        // ============================================================

        internal void Init(VisualElement root, SettingsControllerDeps deps, VisualElement avatarCircle)
        {
            _root = root;
            _deps = deps;
            _avatarCircle = avatarCircle;

            // Settings page controls
            _settingsLanguage     = root.Q<NeonDropdown>("settings-language");
            _settingsToolPermission = root.Q<NeonDropdown>("settings-tool-permission");
            _settingsHistory      = root.Q<Toggle>("settings-save-history");
            _settingsStreaming     = root.Q<Toggle>("settings-streaming");
            _settingsEnterToSend  = root.Q<Toggle>("settings-enter-to-send");
            _settingsSystemPrompt = root.Q<Toggle>("settings-system-prompt");
            _settingsVoiceIo      = root.Q<Toggle>("settings-voice-io");
            _settingsVoiceAlways  = root.Q<Toggle>("settings-voice-always");
            _settingsEncryptKeys  = root.Q<Toggle>("settings-encrypt-keys");
            _settingsMaskLogs     = root.Q<Toggle>("settings-mask-logs");
            _settingsShowHalo     = root.Q<Toggle>("settings-show-halo");
            _settingsBreathing    = root.Q<Toggle>("settings-breathing");
            _settingsStoragePath  = root.Q<Label>("settings-storage-path");
            _settingsVersion      = root.Q<Label>("settings-version");
            _settingsPluginsSummary = root.Q<Label>("settings-plugins-summary");
            _settingsPluginsConfig  = root.Q<Label>("settings-plugins-config");
            _settingsPluginsList    = root.Q<VisualElement>("settings-plugins-list");
            _brandVersion           = root.Q<Label>("brand-version");
            _shapeRound  = root.Q<Button>("shape-round");
            _shapeSquare = root.Q<Button>("shape-square");
            _shapeHex    = root.Q<Button>("shape-hex");

            // Voice device controls
            _settingsVoiceSection = root.Q<VisualElement>("settings-voice-section");
            _settingsInputDevice  = root.Q<NeonDropdown>("settings-input-device");
            _settingsOutputVolume = root.Q<Slider>("settings-output-volume");

            // Settings action buttons
            _settingsOpenFolderBtn = root.Q<Button>("settings-open-folder");
            _settingsExportBtn     = root.Q<Button>("settings-export-btn");
            _settingsClearChatsBtn = root.Q<Button>("settings-clear-chats-btn");
            _settingsClearBtn      = root.Q<Button>("settings-clear-btn");
            _settingsClearBtnText  = _settingsClearBtn?.Q<Label>();
            _settingsGithubBtn     = root.Q<Button>("settings-github-btn");
            _settingsDocsBtn       = root.Q<Button>("settings-docs-btn");
            _settingsDonateBtn     = root.Q<Button>("settings-donate-btn");
            _settingsDonateBtn?.SetEnabled(false);
            _settingsExitBtn       = root.Q<Button>("settings-exit-btn");

            // Hotkey / quit dialog
            _navCloseButton   = root.Q<Button>("nav-close");
            _quitOverlay      = root.Q<VisualElement>("quit-overlay");
            _quitDialogTitle  = root.Q<Label>("quit-dialog-title");
            _quitDialogBody   = root.Q<Label>("quit-dialog-body");
            _quitConfirmBtn   = root.Q<Button>("quit-confirm-btn");
            _quitCancelBtn    = root.Q<Button>("quit-cancel-btn");
            _settingsHotkeyCaptureBtn = root.Q<Button>("settings-hotkey-capture");
            SetDisplay(_quitOverlay, DisplayStyle.None);

            // Themes preview
            _themesPreviewHalo   = root.Q<VisualElement>("themes-preview-halo");
            _themesPreviewAvatar = root.Q<VisualElement>("themes-preview-avatar");

            UpdateClearDataButtonState();
        }

        // ============================================================
        // External-service injection (called when app resolves)
        // ============================================================

        internal void SetDonationService(IDonationService service)
        {
            _donationService = service;
            _settingsDonateBtn?.SetEnabled(service?.IsDonationSupported == true);
        }

        // ============================================================
        // Methods exposed to MainViewController
        // ============================================================

        internal void SaveSettings()
        {
            if (!_settingsLoaded || _settingsLoading)
                return;

            _ = SaveSettingsAsync();
        }

        internal Task SyncActiveAvatarSystemPromptAsync(CompanionApp app = null, AppSettings settings = null)
        {
            return SyncAvatarSystemPromptInternalAsync(app, settings);
        }

        internal void RefreshLanguageDropdown()
        {
            if (_settingsLanguage == null) return;
            string currentLanguage = _localizationService?.CurrentLanguage
                ?? ResolveLanguageCode(_settingsLanguage.value);
            _settingsLanguage.SetValueWithoutNotify(currentLanguage == "en"
                ? LocalizationExtensions.Get("settings.language.english", "English")
                : LocalizationExtensions.Get("settings.language.russian", "Русский"));

            // Also refresh permission mode dropdown labels/choices on language change (no MainViewController edit)
            RefreshPermissionModeDropdown();
        }

        internal void RefreshPluginStatus(CompanionApp app)
        {
            if (app == null) return;

            if (!app.Services.TryGet<PluginManager>(out var pluginManager) || pluginManager == null)
            {
                if (_settingsPluginsSummary != null)
                    _settingsPluginsSummary.text = LocalizationExtensions.Get("plugins.status.not_initialized", "не инициализирован");
                if (_settingsPluginsConfig != null)
                    _settingsPluginsConfig.text = LocalizationExtensions.Get("plugins.status.no_data", "нет данных");
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

            if (_settingsPluginsList == null) return;
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

        internal void UpdateClearDataButtonState()
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

        // ============================================================
        // Callbacks registration
        // ============================================================

        internal void RegisterCallbacks()
        {
            RegisterClick(_shapeRound,  _ => SetAvatarShape("round"));
            RegisterClick(_shapeSquare, _ => SetAvatarShape("square"));
            RegisterClick(_shapeHex,    _ => SetAvatarShape("hex"));

            RegisterToggleChanged(_settingsHistory,      _ => SaveSettings());
            RegisterToggleChanged(_settingsStreaming,     _ => SaveSettings());
            RegisterToggleChanged(_settingsEnterToSend,   _ => SaveSettings());
            RegisterToggleChanged(_settingsSystemPrompt, _ => SaveSettings());
            RegisterToggleChanged(_settingsVoiceIo,      _ => { SaveSettings(); _deps.RefreshVoiceControls?.Invoke(); RefreshVoiceSection(); });
            RegisterToggleChanged(_settingsVoiceAlways,  _ => SaveSettings());
            RegisterToggleChanged(_settingsEncryptKeys,  _ => SaveSettings());
            RegisterToggleChanged(_settingsMaskLogs,     _ => SaveSettings());
            RegisterToggleChanged(_settingsShowHalo,    v => { ApplyHaloVisibility(v); SaveSettings(); });
            RegisterToggleChanged(_settingsBreathing,   v => { ApplyBreathingAnimation(v); SaveSettings(); });

            if (_settingsLanguage != null)
                _settingsLanguage.RegisterCallback<ChangeEvent<string>>(OnSettingsLanguageChanged);

            if (_settingsToolPermission != null)
                _settingsToolPermission.RegisterCallback<ChangeEvent<string>>(OnSettingsToolPermissionChanged);

            if (_settingsInputDevice != null)
                _settingsInputDevice.RegisterCallback<ChangeEvent<string>>(OnVoiceInputDeviceChanged);

            if (_settingsOutputVolume != null)
                _settingsOutputVolume.RegisterCallback<ChangeEvent<float>>(OnVoiceOutputVolumeChanged);

            RegisterClick(_settingsOpenFolderBtn,  OnOpenFolderClicked);
            RegisterClick(_settingsExportBtn,      OnExportChatsClicked);
            RegisterClick(_settingsClearChatsBtn,  OnClearChatsClicked);
            RegisterClick(_settingsClearBtn,       OnClearDataClicked);
            RegisterClick(_settingsGithubBtn,      OnSettingsGitHubClicked);
            RegisterClick(_settingsDocsBtn,        OnSettingsDocsClicked);
            RegisterClick(_settingsDonateBtn,      OnSettingsDonateClicked);
            RegisterClick(_settingsExitBtn,         OnSettingsExitClicked);

            RegisterClick(_navCloseButton,         OnNavCloseClicked);
            RegisterClick(_quitConfirmBtn,         OnQuitConfirmed);
            RegisterClick(_quitCancelBtn,          OnQuitCanceled);
            RegisterClick(_settingsHotkeyCaptureBtn, OnHotkeyCaptureClicked);

            if (_root != null)
                _root.RegisterCallback<KeyDownEvent>(OnGlobalKeyDown, TrickleDown.TrickleDown);
        }

        internal void UnregisterCallbacks()
        {
            if (_settingsLanguage != null)
                _settingsLanguage.UnregisterCallback<ChangeEvent<string>>(OnSettingsLanguageChanged);

            if (_settingsToolPermission != null)
                _settingsToolPermission.UnregisterCallback<ChangeEvent<string>>(OnSettingsToolPermissionChanged);

            if (_settingsInputDevice != null)
                _settingsInputDevice.UnregisterCallback<ChangeEvent<string>>(OnVoiceInputDeviceChanged);

            if (_settingsOutputVolume != null)
                _settingsOutputVolume.UnregisterCallback<ChangeEvent<float>>(OnVoiceOutputVolumeChanged);

            UnregisterClick(_settingsOpenFolderBtn,  OnOpenFolderClicked);
            UnregisterClick(_settingsExportBtn,      OnExportChatsClicked);
            UnregisterClick(_settingsClearChatsBtn,  OnClearChatsClicked);
            UnregisterClick(_settingsClearBtn,       OnClearDataClicked);
            UnregisterClick(_settingsGithubBtn,      OnSettingsGitHubClicked);
            UnregisterClick(_settingsDocsBtn,        OnSettingsDocsClicked);
            UnregisterClick(_settingsDonateBtn,      OnSettingsDonateClicked);
            UnregisterClick(_settingsExitBtn,         OnSettingsExitClicked);

            UnregisterClick(_navCloseButton,         OnNavCloseClicked);
            UnregisterClick(_quitConfirmBtn,         OnQuitConfirmed);
            UnregisterClick(_quitCancelBtn,          OnQuitCanceled);

            if (_root != null)
                _root.UnregisterCallback<KeyDownEvent>(OnGlobalKeyDown, TrickleDown.TrickleDown);

            _breathSchedule?.Pause();
            _breathSchedule = null;
            _clearDataConfirmResetSchedule?.Pause();
            _clearDataConfirmResetSchedule = null;
            _themesBreathSchedule?.Pause();
            _themesBreathSchedule = null;
        }

        // ============================================================
        // Localization binding
        // ============================================================

        internal async Task BindLocalizationEventsAsync()
        {
            var app = await _deps.GetApp();
            if (app == null) return;

            if (!app.Services.TryGet<ILocalizationService>(out var localization) || localization == null)
                return;

            if (ReferenceEquals(_localizationService, localization))
                return;

            UnbindLocalizationEvents();
            _localizationService = localization;
            LocalizationExtensions.SetLocalizationService(localization);
            _localizationService.LanguageChanged += OnLocalizationLanguageChanged;
        }

        internal void UnbindLocalizationEvents()
        {
            if (_localizationService == null) return;
            _localizationService.LanguageChanged -= OnLocalizationLanguageChanged;
            _localizationService = null;
        }

        private void OnLocalizationLanguageChanged()
        {
            if (!(_deps.IsActiveAndEnabled?.Invoke() ?? false) || !(_deps.IsBound?.Invoke() ?? false))
                return;

            _ = _deps.RequestRefreshLocalizedUi?.Invoke();
        }

        // ============================================================
        // Settings language
        // ============================================================

        private void OnSettingsLanguageChanged(ChangeEvent<string> evt)
        {
            OnLanguageSettingChanged(evt?.newValue);
        }

        private void OnSettingsToolPermissionChanged(ChangeEvent<string> evt)
        {
            SaveSettings();
        }

        private void OnLanguageSettingChanged(string selectedValue)
        {
            string languageCode = ResolveLanguageCode(selectedValue);
            _ = ApplyLanguageRuntimeAsync(languageCode);
            SaveSettings();
        }

        private async Task ApplyLanguageRuntimeAsync(string languageCode)
        {
            var app = await _deps.GetApp();
            if (app == null) return;

            if (app.Services.TryGet<ILocalizationService>(out var localization) && localization != null)
            {
                localization.SetLanguage(languageCode);
                LocalizationExtensions.SetLocalizationService(localization);
                if (_deps.RequestRefreshLocalizedUi != null)
                    await _deps.RequestRefreshLocalizedUi();
            }
        }

        // ============================================================
        // Tool permission mode helpers (manual / auto)
        // ============================================================

        private void SetPermissionModeChoices()
        {
            if (_settingsToolPermission == null) return;
            string manual = LocalizationExtensions.Get("approval.manual_mode", "Manual (ask every time)");
            string auto = LocalizationExtensions.Get("approval.auto_mode", "Auto (always allow)");
            _settingsToolPermission.choices = new List<string> { manual, auto };
        }

        private string GetPermissionDisplay(string mode)
        {
            if (string.Equals(mode, "auto", StringComparison.OrdinalIgnoreCase))
                return LocalizationExtensions.Get("approval.auto_mode", "Auto (always allow)");
            return LocalizationExtensions.Get("approval.manual_mode", "Manual (ask every time)");
        }

        private static string ResolvePermissionMode(string displayValue)
        {
            if (displayValue == null) return "manual";
            string autoDisplay = LocalizationExtensions.Get("approval.auto_mode", "Auto (always allow)");
            if (string.Equals(displayValue, autoDisplay, StringComparison.Ordinal))
                return "auto";
            return "manual";
        }

        internal void RefreshPermissionModeDropdown()
        {
            if (_root != null)
            {
                var permLabel = _root.Q<Label>("settings-permission-label");
                if (permLabel != null)
                    permLabel.text = LocalizationExtensions.Get("approval.permission_mode", "Tool permission");
            }

            if (_settingsToolPermission == null) return;
            string currentMode = ResolvePermissionMode(_settingsToolPermission.value);
            SetPermissionModeChoices();
            _settingsToolPermission.SetValueWithoutNotify(GetPermissionDisplay(currentMode));
        }

        // ============================================================
        // Load / Save settings
        // ============================================================

        internal async Task LoadSettingsAsync()
        {
            _settingsLoading = true;
            bool loaded = false;
            try
            {
                var app = await _deps.GetApp();
                if (!(_deps.IsBound?.Invoke() ?? false) || app == null) return;

                var s = app.Settings.Load() ?? new AppSettings();
                var availableAvatars = app.Avatars.GetAll();
                string resolvedAvatarId = string.IsNullOrEmpty(s.activeAvatarId) ? "neon" : s.activeAvatarId;
                bool knownAvatar = System.Array.IndexOf(BuiltInAvatarIds, resolvedAvatarId) >= 0 ||
                                   System.Linq.Enumerable.Any(availableAvatars, a => a != null && a.id == resolvedAvatarId);
                if (!knownAvatar) resolvedAvatarId = "neon";

                _deps.SetActiveAvatarId?.Invoke(resolvedAvatarId);
                _deps.SetAvatarViewMode?.Invoke(string.IsNullOrWhiteSpace(s.avatarViewMode) ? "static" : s.avatarViewMode);

                _settingsHistory?.SetValueWithoutNotify(s.saveChatHistory);
                _settingsStreaming?.SetValueWithoutNotify(s.streaming);
                _settingsEnterToSend?.SetValueWithoutNotify(s.enterToSend);
                _settingsSystemPrompt?.SetValueWithoutNotify(s.useSystemPrompt);
                _settingsVoiceIo?.SetValueWithoutNotify(s.voiceIOEnabled);
                _settingsVoiceAlways?.SetValueWithoutNotify(s.voiceAlwaysReply);
                _settingsEncryptKeys?.SetValueWithoutNotify(s.encryptKeys);
                _settingsMaskLogs?.SetValueWithoutNotify(s.maskLogs);
                NeonLogger.MaskSecrets = s.maskLogs;
                _settingsShowHalo?.SetValueWithoutNotify(s.showHalo);
                _settingsBreathing?.SetValueWithoutNotify(s.breathingAnimation);

                if (_settingsLanguage != null)
                    _settingsLanguage.SetValueWithoutNotify(s.language == "en"
                        ? LocalizationExtensions.Get("settings.language.english", "English")
                        : LocalizationExtensions.Get("settings.language.russian", "Русский"));

                await ApplyLanguageRuntimeAsync(s.language == "en" ? "en" : "ru");

                if (_settingsToolPermission != null)
                {
                    SetPermissionModeChoices();
                    string mode = string.IsNullOrEmpty(s.toolPermissionMode) ? "manual" : s.toolPermissionMode;
                    _settingsToolPermission.SetValueWithoutNotify(GetPermissionDisplay(mode));
                }

                var permLabel = _root != null ? _root.Q<Label>("settings-permission-label") : null;
                if (permLabel != null)
                    permLabel.text = LocalizationExtensions.Get("approval.permission_mode", "Tool permission");

                if (_settingsStoragePath != null)
                    _settingsStoragePath.text = Application.persistentDataPath;

                // Single source of truth: Player Settings → bundleVersion (Application.version).
                if (_settingsVersion != null)
                    _settingsVersion.text = Application.version;
                if (_brandVersion != null)
                    _brandVersion.text = Application.version;

                RefreshPluginStatus(app);
                SetAvatarShape(s.avatarShape ?? "round", save: false);
                ApplyHaloVisibility(s.showHalo);
                _deps.RefreshCustomAvatarGallery?.Invoke(app);
                _deps.RefreshBuiltInAvatarTileLabels?.Invoke();
                _deps.ApplyAvatarFilter?.Invoke();

                string activeId = _deps.GetActiveAvatarId?.Invoke() ?? "neon";
                _deps.ApplyAvatarArt?.Invoke(activeId);
                _deps.SyncGallerySelection?.Invoke(activeId);
                ApplyBreathingAnimation(s.breathingAnimation);

                if (!string.Equals(s.activeAvatarId, activeId, StringComparison.Ordinal))
                {
                    s.activeAvatarId = activeId;
                    app.Settings.Save(s);
                }

                await SyncAvatarSystemPromptInternalAsync(app, s);
                _closeHotkey = s.closeHotkey ?? "Escape";
                if (_settingsHotkeyCaptureBtn != null)
                    _settingsHotkeyCaptureBtn.text = _closeHotkey;

                _deps.RefreshVoiceControls?.Invoke();
                PopulateVoiceDeviceDropdown(s.inputDeviceName);
                if (_settingsOutputVolume != null)
                    _settingsOutputVolume.SetValueWithoutNotify(s.outputVolume);
                RefreshVoiceSection();
                loaded = true;
            }
            catch (Exception ex)
            {
                NeonLogger.LogError(ex.ToString());
            }
            finally
            {
                if (loaded)
                    _settingsLoaded = true;
                _settingsLoading = false;
            }
        }

        private async Task SaveSettingsAsync()
        {
            if (!_settingsLoaded || _settingsLoading)
                return;

            try
            {
                var app = await _deps.GetApp();
                if (app == null) return;

                var s = app.Settings.Load() ?? new AppSettings();

                if (_settingsHistory != null)      s.saveChatHistory    = _settingsHistory.value;
                if (_settingsStreaming != null)     s.streaming          = _settingsStreaming.value;
                if (_settingsEnterToSend != null)   s.enterToSend       = _settingsEnterToSend.value;
                if (_settingsSystemPrompt != null)  s.useSystemPrompt   = _settingsSystemPrompt.value;
                if (_settingsVoiceIo != null)       s.voiceIOEnabled     = _settingsVoiceIo.value;
                if (_settingsVoiceAlways != null)   s.voiceAlwaysReply   = _settingsVoiceAlways.value;
                if (_settingsEncryptKeys != null)   s.encryptKeys        = _settingsEncryptKeys.value;
                if (_settingsMaskLogs != null)    { s.maskLogs           = _settingsMaskLogs.value; NeonLogger.MaskSecrets = s.maskLogs; }
                if (_settingsShowHalo != null)      s.showHalo           = _settingsShowHalo.value;
                if (_settingsBreathing != null)     s.breathingAnimation = _settingsBreathing.value;
                if (_settingsLanguage != null)      s.language           = ResolveLanguageCode(_settingsLanguage.value);
                if (_settingsToolPermission != null) s.toolPermissionMode = ResolvePermissionMode(_settingsToolPermission.value);
                if (_settingsInputDevice != null)   s.inputDeviceName     = _settingsInputDevice.value ?? string.Empty;
                if (_settingsOutputVolume != null)  s.outputVolume        = _settingsOutputVolume.value;

                s.avatarShape      = _avatarShape;
                s.activeAvatarId   = _deps.GetActiveAvatarId?.Invoke() ?? "neon";
                s.avatarViewMode   = _deps.GetAvatarViewMode?.Invoke() ?? "static";
                s.closeHotkey      = _closeHotkey;

                var chatService = _deps.GetChatServiceSync?.Invoke();
                if (chatService?.CurrentProvider != null)
                {
                    string providerId = chatService.CurrentProvider.id;
                    s.activeProviderId = providerId;
                    var selector = GlobalBackendSelector.Instance;
                    BackendMode mode = selector != null ? selector.CurrentMode : BackendMode.OpenAI;
                    if (mode == BackendMode.Hermes)
                        s.activeHermesProviderId = providerId;
                    else
                        s.activeOpenAiProviderId = providerId;
                }

                app.Settings.Save(s);

                if (chatService != null)
                    chatService.SaveChatHistory = s.saveChatHistory;

                await SyncAvatarSystemPromptInternalAsync(app, s);
            }
            catch (Exception ex)
            {
                NeonLogger.LogError(ex.ToString());
            }
        }

        private async Task SyncAvatarSystemPromptInternalAsync(CompanionApp app = null, AppSettings settings = null)
        {
            var chatService = _deps.GetChatServiceSync?.Invoke();
            if (chatService == null) return;

            if (app == null) app = await _deps.GetApp();
            if (app == null) return;

            if (settings == null) settings = app.Settings.Load() ?? new AppSettings();
            var avatarProfiles = app.Avatars.GetAll();
            string activeId = _deps.GetActiveAvatarId?.Invoke() ?? "neon";
            string prompt = app.AvatarService.GetSystemPrompt(activeId, avatarProfiles);
            chatService.SystemPrompt = settings.useSystemPrompt ? prompt : null;
        }

        // ============================================================
        // Avatar shape / halo / breathing
        // ============================================================

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

        private void StartBreathing()
        {
            if (_avatarCircle == null) return;
            _breathStartMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _breathSchedule?.Pause();
            _breathSchedule = _avatarCircle.schedule.Execute(TickBreath).Every(33);
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
            float s = 1f + 0.015f * (float)Math.Sin(elapsed * (2f * (float)Math.PI / 5f));
            _avatarCircle.style.scale = new StyleScale(new Scale(new Vector3(s, s, 1f)));
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
        // Hotkey capture / quit dialog
        // ============================================================

        private void OnNavCloseClicked(ClickEvent _) { ShowQuitDialog(); }

        private void ShowQuitDialog()
        {
            if (_quitDialogVisible || _quitOverlay == null) return;
            _quitDialogVisible = true;
            if (_quitDialogTitle != null)
                _quitDialogTitle.text = LocalizationExtensions.Get("quit.dialog.title", "Закрыть приложение?");
            if (_quitDialogBody != null)
                _quitDialogBody.text = LocalizationExtensions.Get("quit.dialog.body", "Активные соединения будут прерваны.");
            if (_quitConfirmBtn != null)
                _quitConfirmBtn.Q<Label>()?.Localize("nav.close");
            _quitCancelBtn?.Localize("common.cancel");
            SetDisplay(_quitOverlay, DisplayStyle.Flex);
        }

        private void HideQuitDialog()
        {
            if (!_quitDialogVisible || _quitOverlay == null) return;
            _quitDialogVisible = false;
            SetDisplay(_quitOverlay, DisplayStyle.None);
        }

        private void OnQuitConfirmed(ClickEvent _ = null)
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        private void OnQuitCanceled(ClickEvent _) { HideQuitDialog(); }

        private void OnGlobalKeyDown(KeyDownEvent evt)
        {
            if (_isCapturingHotkey)
            {
                if (IsPureModifier(evt.keyCode)) return;
                string captured = BuildHotkeyString(evt);
                _closeHotkey = captured;
                ExitCaptureMode(captured);
                SaveSettings();
                evt.StopPropagation();
                return;
            }

            if (_quitDialogVisible)
            {
                bool isEnter = evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter;
                if (isEnter) OnQuitConfirmed();
                else HideQuitDialog();
                evt.StopPropagation();
                return;
            }

            if (MatchesHotkey(evt, _closeHotkey))
            {
                ShowQuitDialog();
                evt.StopPropagation();
            }
        }

        private void OnHotkeyCaptureClicked(ClickEvent _)
        {
            if (_isCapturingHotkey) { ExitCaptureMode(_closeHotkey); return; }
            EnterCaptureMode();
        }

        private void EnterCaptureMode()
        {
            _isCapturingHotkey = true;
            if (_settingsHotkeyCaptureBtn != null)
            {
                _settingsHotkeyCaptureBtn.text = LocalizationExtensions.Get("settings.hotkey.capturing", "Нажми клавишу…");
                _settingsHotkeyCaptureBtn.AddToClassList("btn--hotkey-capture--active");
            }
        }

        private void ExitCaptureMode(string hotkey)
        {
            _isCapturingHotkey = false;
            if (_settingsHotkeyCaptureBtn != null)
            {
                _settingsHotkeyCaptureBtn.text = hotkey;
                _settingsHotkeyCaptureBtn.RemoveFromClassList("btn--hotkey-capture--active");
            }
        }

        private static bool IsPureModifier(KeyCode key)
        {
            return key == KeyCode.LeftControl  || key == KeyCode.RightControl
                || key == KeyCode.LeftAlt      || key == KeyCode.RightAlt
                || key == KeyCode.LeftShift    || key == KeyCode.RightShift
                || key == KeyCode.LeftCommand  || key == KeyCode.RightCommand
                || key == KeyCode.LeftWindows  || key == KeyCode.RightWindows;
        }

        private static string BuildHotkeyString(KeyDownEvent evt)
        {
            var parts = new List<string>(4);
            if ((evt.modifiers & EventModifiers.Control) != 0) parts.Add("Control");
            if ((evt.modifiers & EventModifiers.Alt)     != 0) parts.Add("Alt");
            if ((evt.modifiers & EventModifiers.Shift)   != 0) parts.Add("Shift");
            parts.Add(evt.keyCode.ToString());
            return string.Join("+", parts);
        }

        private static bool MatchesHotkey(KeyDownEvent evt, string hotkey)
        {
            if (string.IsNullOrWhiteSpace(hotkey)) return false;
            var parts = hotkey.Split('+');
            string keyName = parts[parts.Length - 1];
            if (!Enum.TryParse<KeyCode>(keyName, out KeyCode keyCode) || evt.keyCode != keyCode)
                return false;
            bool needsCtrl  = Array.IndexOf(parts, "Control") >= 0;
            bool needsAlt   = Array.IndexOf(parts, "Alt")     >= 0;
            bool needsShift = Array.IndexOf(parts, "Shift")   >= 0;
            return needsCtrl  == ((evt.modifiers & EventModifiers.Control) != 0)
                && needsAlt   == ((evt.modifiers & EventModifiers.Alt)     != 0)
                && needsShift == ((evt.modifiers & EventModifiers.Shift)   != 0);
        }

        // ============================================================
        // Settings action buttons
        // ============================================================

        private void OnOpenFolderClicked(ClickEvent _)
        {
            OpenPath(Application.persistentDataPath);
        }

        private static void OpenPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
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

        private void OnExportChatsClicked(ClickEvent e) { _ = ExportChatsAsync(); }

        private async Task ExportChatsAsync()
        {
            try
            {
                var chat = await _deps.GetChatService();
                if (chat == null) return;

                var sessions = await chat.GetAllSessionsAsync();
                string json = UnityEngine.JsonUtility.ToJson(new ChatSessionCollection { items = sessions }, true);
                string path = System.IO.Path.Combine(Application.persistentDataPath,
                    $"export_{DateTime.Now:yyyyMMdd_HHmmss}.json");
                System.IO.File.WriteAllText(path, json);
                string fileName = System.IO.Path.GetFileName(path);

                _deps.AddSystemMessage?.Invoke(
                    LocalizationExtensions.GetFormat("system.export.chats", "Чаты экспортированы в {0}.", fileName));
                OpenPath(path);
            }
            catch (Exception ex)
            {
                NeonLogger.LogError(ex.ToString());
            }
        }

        private void OnSettingsGitHubClicked(ClickEvent _)
        {
            OpenExternalUrl("https://github.com/isteelfelix/neon-companion");
            _deps.AddSystemMessage?.Invoke(LocalizationExtensions.Get("system.open.github", "Открыт GitHub репозитория."));
        }

        private void OnSettingsDocsClicked(ClickEvent _)
        {
            OpenExternalUrl("https://github.com/isteelfelix/neon-companion/tree/main/docs");
            _deps.AddSystemMessage?.Invoke(LocalizationExtensions.Get("system.open.docs", "Открыта папка docs."));
        }

        private void OnSettingsDonateClicked(ClickEvent _)
        {
            if (_donationService?.IsDonationSupported == true)
            {
                _donationService.OpenDonationPage();
                _deps.AddSystemMessage?.Invoke(LocalizationExtensions.Get("system.open.donate", "Открыта страница поддержки проекта."));
                return;
            }
            _deps.AddSystemMessage?.Invoke(LocalizationExtensions.Get("system.donate.unavailable", "Поддержка проекта пока недоступна."));
        }

        private void OnSettingsExitClicked(ClickEvent _) { ShowQuitDialog(); }

        private static void OpenExternalUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            Application.OpenURL(url);
        }

        // ============================================================
        // Clear data
        // ============================================================

        private void OnClearDataClicked(ClickEvent e)
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (!_clearDataConfirmPending || nowMs > _clearDataConfirmExpiresAtMs)
            {
                _clearDataConfirmPending = true;
                _clearDataConfirmExpiresAtMs = nowMs + 7000;
                ArmClearDataConfirmationReset();
                UpdateClearDataButtonState();
                _deps.AddSystemMessage?.Invoke(LocalizationExtensions.Get("system.clear.confirm_hint", "Нажми «Подтвердить» ещё раз в течение 7 секунд, чтобы удалить все данные."));
                return;
            }

            ResetClearDataConfirmation();
            _ = ClearAllDataAsync();
        }

        private void OnClearChatsClicked(ClickEvent e) { _ = ClearChatsOnlyAsync(); }

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
            if (_settingsClearBtn == null) return;

            _clearDataConfirmResetSchedule = _settingsClearBtn.schedule
                .Execute(() =>
                {
                    _clearDataConfirmResetSchedule = null;
                    ResetClearDataConfirmation();
                })
                .StartingIn(7000);
        }

        private async Task ClearAllDataAsync()
        {
            try
            {
                ResetClearDataConfirmation();
                var app = await _deps.GetApp();
                if (app == null) return;

                app.Chats.SaveAll(new List<ChatSession>());

                var providers = await app.ProviderManager.GetAllProvidersAsync();
                foreach (var p in providers)
                    await app.ProviderManager.DeleteProviderAsync(p.id);

                var chat = await _deps.GetChatService();
                if (chat != null)
                    chat.ClearActiveProviderState();

                app.Settings.Save(new AppSettings());

                _deps.ResetServiceCache?.Invoke();
                _deps.SetCurrentSessionId?.Invoke(string.Empty);
                _deps.SetCurrentSessionTitle?.Invoke(string.Empty);
                _deps.RequestRenderMessages?.Invoke();
                _deps.SetNoProviderState?.Invoke();
                _deps.ClearSessionsListUi?.Invoke();
                _deps.ResetProvidersCountUi?.Invoke();
                _deps.ShowHistoryState?.Invoke(
                    LocalizationExtensions.Get("history.empty.first_session", "История пуста. Начните чат, чтобы появилась первая сессия."),
                    false);
            }
            catch (Exception ex)
            {
                ResetClearDataConfirmation();
                NeonLogger.LogError(ex.ToString());
            }
        }

        private async Task ClearChatsOnlyAsync()
        {
            try
            {
                var app = await _deps.GetApp();
                if (app == null) return;

                // Wipe the on-disk repository first
                app.Chats.SaveAll(new List<ChatSession>());

                // Reset the in-memory ChatService state so it doesn't write the
                // old session back on the next send.
                var chat = await _deps.GetChatService();
                if (chat != null)
                    chat.ClearCurrentSessionState();

                _deps.SetCurrentSessionId?.Invoke(string.Empty);
                _deps.SetCurrentSessionTitle?.Invoke(string.Empty);
                _deps.RequestRenderMessages?.Invoke();
                _deps.ShowHistoryState?.Invoke(
                    LocalizationExtensions.Get("history.empty.first_session", "История пуста. Начните чат, чтобы появилась первая сессия."),
                    false);
                _deps.ClearSessionsListUi?.Invoke();
            }
            catch (Exception ex)
            {
                NeonLogger.LogError(ex.ToString());
            }
        }

        // ============================================================
        // Voice device helpers
        // ============================================================

        private void PopulateVoiceDeviceDropdown(string savedDeviceName)
        {
            if (_settingsInputDevice == null)
                return;

            string[] devices = UnityEngine.Microphone.devices;
            var choices = new List<string>();

            if (devices == null || devices.Length == 0)
            {
                choices.Add(LocalizationExtensions.Get("settings.voice.input_device.none", "No devices"));
                _settingsInputDevice.choices = choices;
                _settingsInputDevice.SetValueWithoutNotify(choices[0]);
                return;
            }

            foreach (string d in devices)
                choices.Add(d);

            _settingsInputDevice.choices = choices;

            string selected = string.Empty;
            if (!string.IsNullOrEmpty(savedDeviceName))
            {
                for (int i = 0; i < choices.Count; i++)
                {
                    if (string.Equals(choices[i], savedDeviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        selected = choices[i];
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(selected) && choices.Count > 0)
                selected = choices[0];

            _settingsInputDevice.SetValueWithoutNotify(selected);
        }

        private void RefreshVoiceSection()
        {
            bool enabled = _settingsVoiceIo != null && _settingsVoiceIo.value;
            SetDisplay(_settingsVoiceSection, enabled ? DisplayStyle.Flex : DisplayStyle.None);
        }

        private void OnVoiceInputDeviceChanged(ChangeEvent<string> evt)
        {
            SaveSettings();
        }

        private void OnVoiceOutputVolumeChanged(ChangeEvent<float> evt)
        {
            SaveSettings();
        }

        // ============================================================
        // Utilities (static)
        // ============================================================

        private static readonly string[] BuiltInAvatarIds =
            { "neon", "aurora", "ember", "glass", "flora", "mono", "cobalt", "rose" };

        internal static string ResolveLanguageCode(string languageValue)
        {
            if (languageValue == null) return "ru";
            return languageValue.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en" : "ru";
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

        private static void RegisterToggleChanged(Toggle toggle, Action<bool> handler)
        {
            if (toggle != null)
                toggle.RegisterValueChangedCallback(evt => handler(evt.newValue));
        }

        private static void SetDisplay(VisualElement el, DisplayStyle style)
        {
            if (el != null) el.style.display = style;
        }
    }
}
