using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Api;
using NeonCompanion.Runtime.Api.Hermes;
using NeonCompanion.Runtime.Api.Models;
using NeonCompanion.Runtime.Chat;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Localization;
using NeonCompanion.Runtime.Platform;
using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK
{
    internal sealed class ProvidersController
    {
        public struct Deps
        {
            // UI — provider list
            public ScrollView ProvidersList;
            // UI — edit panel
            public VisualElement ProviderEditPanel;
            public Button AddProviderButton;
            public Button SaveProviderButton;
            public Button CancelEditButton;
            public Button ImportProviderButton;
            public Button TestProviderBtn;
            public VisualElement TestRow;
            public Label TestRowLabel;
            public TextField EditName;
            public TextField EditBaseUrl;
            public TextField EditApiKey;
            public Button EditApiKeyToggle;
            public TextField EditModel;
            public NeonDropdown EditModelPreset;
            public VisualElement EditModelCustomWrap;
            public TextField EditMaxTokens;
            public Slider EditTemperature;
            // UI — global backend mode selector
            public NeonDropdown GlobalBackendMode;
            public Label BackendModeHint;
            // UI — editor header
            public Label EditorProviderShort;
            public Label EditorProviderName;
            public Label EditorProviderStatus;
            // UI — nav count
            public Label NavProvidersCount;
            // UI — topbar model picker
            public NeonDropdown TopbarModelPicker;
            // UI — provider header strip
            public Label ProviderShort;
            public Label ProviderName;
            public Label ProviderModel;
            public Label RailProviderName;
            public Label RailProviderModel;
            // Rail footer row (lower-left active-provider indicator) — host of the Hermes
            // backend-profile selector, which is created in C# next to it.
            public VisualElement RailFooter;
            // Root element for modal overlay
            public VisualElement Root;
            // Services
            public Func<Task<CompanionApp>> GetAppAsync;
            public Func<Task<ChatService>> GetChatServiceAsync;
            public Func<ChatService> GetChatServiceSync;
            // State
            public Func<bool> IsBound;
            // Callbacks
            public Action SaveSettings;
            public Func<Task> LoadSessionsAsync;
            public Action RenderMessages;
            public Action<string> AddSystemMessage;
            public Action TriggerAvatarConfused;
            public Action ShowChat;
            public Action<string> SetCurrentSessionId;
            public Action<string> SetCurrentSessionTitle;
        }

        private const string ActiveProviderClass  = "provider--active";
        private const string EditingProviderClass = "provider--editing";
        private const string CustomModelPresetValue = "Custom / manual";
        private const string OpenAiBackendModeValue = "OpenAI (HTTP REST)";
        private const string HermesBackendModeValue = "Hermes (WebSocket)";
        private BackendMode _providersBackendMode = BackendMode.OpenAI;

        private bool SelectedBackendIsHermes()
        {
            return _providersBackendMode == BackendMode.Hermes;
        }

        private Deps _d;

        // Provider edit state
        private ProviderConfig _editingProvider;
        private ProviderConfig _editingProviderSource;
        private bool _cancelPending;

        // API key visibility toggle (U-16)
        private bool _apiKeyVisible;

        // Model picker state
        private bool _isApplyingModelSwitch;
        private bool _editModelUsesCustomMode;
        private bool _syncingModelPresetUi;
        private bool _syncingGlobalBackendModeUi;
        private bool _refreshingProvidersList;
        private bool _providerListRefreshPending;
        private string _lastCustomModel = string.Empty;
        private IReadOnlyList<string> _discoveredModels;
        private IVisualElementScheduledItem _autoDiscoverSchedule;
        private CancellationTokenSource _autoDiscoverCts;
        private readonly Dictionary<string, string> _modelPresetByLabel = new Dictionary<string, string>();

        // Voice editor fields (queried lazily from ProviderEditPanel)
        private VisualElement _editVoiceSection;
        private NeonDropdown _editSttProvider;
        private NeonDropdown _editTtsProvider;
        private TextField _editTtsVoice;
        private Slider _editTtsSpeed;
        private TextField _editSttLanguage;
        private bool _voiceFieldsQueried;

        // Remote Hermes gateway section (Desktop-style: URL + Connect; token under Advanced).
        // Built dynamically in C# (CLAUDE.md: prefer conditional UI in code over UXML).
        private VisualElement _gatewaySection;
        private Label _gatewayBaseUrlLabel;
        private VisualElement _apiKeyFieldRoot;
        // Model / sampling field roots — hidden for Hermes, where the gateway profile owns them.
        private VisualElement _modelFieldRoot;
        private VisualElement _samplingRowRoot;
        private Label _apiKeyFieldLabel;
        private string _apiKeyLabelOpenAi;
        private VisualElement _gatewayStatusRow;
        private Label _gatewayStatus;
        private Button _gatewayConnectBtn;
        private Button _gatewaySignOutBtn;
        private Button _gatewayAdvancedToggle;
        private VisualElement _gatewayAdvancedWrap;
        private TextField _gatewayAdvancedToken;
        private bool _gatewayFieldsBuilt;
        private bool _gatewayAdvancedOpen;
        private bool _gatewayBusy;
        private bool _gatewayEventsHooked;
        private HermesAuthProbeResult _lastProbe;
        private string _probedAuthProvider; // auto-detected; never shown as a user field
        // Last transport state seen, so a Connected edge triggers exactly one reload.
        private TransportState _lastTransportState = TransportState.Disconnected;

        // Hermes backend-profile strip in the rail footer (created lazily in C#).
        // Hermes-only: profiles are a backend concept, other providers never show this row.
        private VisualElement _profileRow;
        private ScrollView _profileStrip;
        private Label _profileStatus;
        private Label _profileMeta;
        private bool _profileFieldsBuilt;
        private bool _profileSwitchBusy;
        private string _profilesLoadedForEndpoint;
        private string _hoveredProfileName;
        private readonly List<HermesProfile> _hermesProfiles = new List<HermesProfile>();
        private readonly Dictionary<string, VisualElement> _profileChipByName =
            new Dictionary<string, VisualElement>(StringComparer.Ordinal);

        // Drag-to-scroll state for the profile strip: the rail is far too narrow to ever
        // fit every chip, so overflow is reached by dragging the strip with LMB.
        private int _stripPointerId = -1;
        private float _stripPointerStartX;
        private float _stripStartOffsetX;
        private bool _stripDragged;
        private string _stripPressedProfile;

        // Model picker overlay (created lazily)
        private VisualElement _modelPickerOverlay;
        private VisualElement _modelPickerDialog;
        private ScrollView _modelPickerScroll;
        private Label _modelPickerStatus;

        public void SetDeps(Deps deps)
        {
            _d = deps;
        }

        public void Init()
        {
            SetDisplay(_d.ProviderEditPanel, DisplayStyle.None);
        }

        public void ResetNavProvidersCount()
        {
            if (_d.NavProvidersCount != null)
                _d.NavProvidersCount.text = "0";
        }

        public void RegisterCallbacks()
        {
            RegisterClick(_d.AddProviderButton, OnAddProviderClicked);
            RegisterClick(_d.ImportProviderButton, OnImportProviderClicked);
            RegisterClick(_d.SaveProviderButton, OnSaveProviderClicked);
            RegisterClick(_d.CancelEditButton, OnCancelEditClicked);
            RegisterClick(_d.TestProviderBtn, OnTestProviderClicked);

            if (_d.EditModelPreset != null)
                _d.EditModelPreset.RegisterCallback<ChangeEvent<string>>(OnModelPresetChanged);
            if (_d.TopbarModelPicker != null)
                _d.TopbarModelPicker.TriggerClicked += OnTopbarModelPickerTriggered;
            if (_d.EditName != null)
                _d.EditName.RegisterCallback<ChangeEvent<string>>(OnProviderNameChanged);
            if (_d.EditBaseUrl != null)
                _d.EditBaseUrl.RegisterCallback<ChangeEvent<string>>(OnBaseUrlChanged);
            if (_d.EditApiKey != null)
                _d.EditApiKey.RegisterCallback<ChangeEvent<string>>(OnProviderEndpointChanged);
            if (_d.EditApiKeyToggle != null)
                _d.EditApiKeyToggle.RegisterCallback<ClickEvent>(OnApiKeyToggleClicked);
            if (_d.EditModel != null)
                _d.EditModel.RegisterCallback<ChangeEvent<string>>(OnManualModelChanged);

            // Global backend mode selector
            if (_d.GlobalBackendMode != null)
            {
                _d.GlobalBackendMode.choices = new List<string> { OpenAiBackendModeValue, HermesBackendModeValue };
                var selector = GlobalBackendSelector.Instance;
                BackendMode initialMode = selector != null ? selector.CurrentMode : _providersBackendMode;
                SyncGlobalBackendModeUi(initialMode);
                _d.GlobalBackendMode.RegisterCallback<ChangeEvent<string>>(OnGlobalBackendModeChanged);
            }
        }

        public void UnregisterCallbacks()
        {
            UnregisterClick(_d.AddProviderButton, OnAddProviderClicked);
            UnregisterClick(_d.ImportProviderButton, OnImportProviderClicked);
            UnregisterClick(_d.SaveProviderButton, OnSaveProviderClicked);
            UnregisterClick(_d.CancelEditButton, OnCancelEditClicked);
            UnregisterClick(_d.TestProviderBtn, OnTestProviderClicked);

            if (_d.EditModelPreset != null)
                _d.EditModelPreset.UnregisterCallback<ChangeEvent<string>>(OnModelPresetChanged);
            if (_d.TopbarModelPicker != null)
                _d.TopbarModelPicker.TriggerClicked -= OnTopbarModelPickerTriggered;
            if (_d.EditName != null)
                _d.EditName.UnregisterCallback<ChangeEvent<string>>(OnProviderNameChanged);
            if (_d.EditBaseUrl != null)
                _d.EditBaseUrl.UnregisterCallback<ChangeEvent<string>>(OnBaseUrlChanged);
            if (_d.EditApiKey != null)
                _d.EditApiKey.UnregisterCallback<ChangeEvent<string>>(OnProviderEndpointChanged);
            if (_d.EditApiKeyToggle != null)
                _d.EditApiKeyToggle.UnregisterCallback<ClickEvent>(OnApiKeyToggleClicked);
            if (_d.EditModel != null)
                _d.EditModel.UnregisterCallback<ChangeEvent<string>>(OnManualModelChanged);

            if (_d.GlobalBackendMode != null)
                _d.GlobalBackendMode.UnregisterCallback<ChangeEvent<string>>(OnGlobalBackendModeChanged);

            if (_profileStrip != null)
            {
                _profileStrip.UnregisterCallback<PointerDownEvent>(OnProfileStripPointerDown, TrickleDown.TrickleDown);
                _profileStrip.UnregisterCallback<PointerMoveEvent>(OnProfileStripPointerMove);
                _profileStrip.UnregisterCallback<PointerUpEvent>(OnProfileStripPointerUp);
                _profileStrip.UnregisterCallback<PointerCaptureOutEvent>(OnProfileStripPointerCaptureOut);
                _profileStrip.UnregisterCallback<WheelEvent>(OnProfileStripWheel);
            }

            UnhookGatewayAuthEvents();
        }

        // ============================================================
        // Provider list
        // ============================================================

        public async Task RefreshProvidersListAsync()
        {
            if (_refreshingProvidersList)
            {
                _providerListRefreshPending = true;
                return;
            }

            _refreshingProvidersList = true;
            try
            {
                do
                {
                    _providerListRefreshPending = false;
                    await RefreshProvidersListCoreAsync();
                }
                while (_providerListRefreshPending);
            }
            finally
            {
                _refreshingProvidersList = false;
            }
        }

        private async Task RefreshProvidersListCoreAsync()
        {
            if (_d.ProvidersList == null)
                return;

            _d.ProvidersList.Clear();

            var app = await _d.GetAppAsync();
            if (!_d.IsBound() || app == null)
            {
                _d.ProvidersList.Add(new Label(LocalizationExtensions.Get("providers.manager.not_ready", "Менеджер провайдеров не готов.")));
                return;
            }

            var allProviders = await app.ProviderManager.GetAllProvidersAsync();
            var chat = await _d.GetChatServiceAsync();

            // Providers are isolated per backend: Hermes mode shows only Hermes providers,
            // OpenAI mode shows only OpenAI-compatible ones.
            bool hermesMode = SelectedBackendIsHermes();
            var providers = allProviders
                .Where(p => ChatService.IsHermesProvider(p) == hermesMode)
                .ToList();

            if (_d.NavProvidersCount != null)
                _d.NavProvidersCount.text = providers.Count.ToString();

            if (providers.Count == 0)
            {
                _d.ProvidersList.Add(new Label(LocalizationExtensions.Get("providers.empty", "Провайдеры не настроены.")));
                return;
            }

            string activeProviderId = chat?.CurrentProvider?.id;

            for (int i = 0; i < providers.Count; i++)
            {
                var provider = providers[i];
                bool isActive = !string.IsNullOrEmpty(activeProviderId) && provider.id == activeProviderId;
                _d.ProvidersList.Add(CreateProviderListItem(provider, isActive));
            }

            // The editor is a modal overlay (.provider-edit-overlay) that covers the list.
            // Do NOT auto-open it on refresh — that hid the list behind the modal on entry.
            // It opens only on explicit user action (card click / "Изменить" / "Добавить").
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

            var nameLabel = new Label(string.IsNullOrWhiteSpace(provider.displayName)
                ? LocalizationExtensions.Get("providers.default_name", "Провайдер")
                : provider.displayName);
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

            if (!string.IsNullOrWhiteSpace(provider.defaultModel))
            {
                var modelLabel = new Label(provider.defaultModel);
                modelLabel.AddToClassList("provider__model");
                body.Add(modelLabel);
            }

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
            var editButton = new Button(() => StartEditingProvider(provider))
            {
                text = LocalizationExtensions.Get("providers.action.edit", "Изменить")
            };
            var deleteButton = new Button(() => DeleteProvider(provider))
            {
                text = LocalizationExtensions.Get("providers.action.delete", "Удалить")
            };
            editButton.AddToClassList("btn");
            deleteButton.AddToClassList("btn");
            actions.Add(editButton);
            actions.Add(deleteButton);
            // Action clicks must not bubble to the card's click handler (which opens the editor).
            actions.RegisterCallback<ClickEvent>(evt => evt.StopPropagation());

            var toggle = new VisualElement();
            toggle.AddToClassList("toggle");
            // Toggle reflects the PERSISTED enabled flag, not the live chat-current-provider.
            // Otherwise, right after restart (before CurrentProvider is resolved) an enabled
            // provider shows OFF and needs two taps to flip. The "активен" chip above still
            // marks which one is the current chat provider.
            if (provider.isEnabled) toggle.AddToClassList("toggle--on");
            var knob = new VisualElement();
            knob.AddToClassList("toggle__knob");
            toggle.Add(knob);
            toggle.RegisterCallback<ClickEvent>(evt =>
            {
                evt.StopPropagation();
                _ = ToggleProviderEnabledAsync(provider);
            });

            container.Add(logo);
            container.Add(body);
            container.Add(meta);
            container.Add(actions);
            container.Add(toggle);

            return container;
        }

        // ============================================================
        // Provider CRUD
        // ============================================================

        private void OnAddProviderClicked()
        {
            if (!CanLeaveProviderEditor())
                return;

            _cancelPending = false;
            _editingProviderSource = null;
            _editingProvider = ProviderConfig.CreateDefault(
                LocalizationExtensions.Get("providers.new_provider", "Новый провайдер"),
                "https://api.openai.com/v1");

            // New providers inherit the active backend mode (see BuildProviderDraftFromEditor).
            if (SelectedBackendIsHermes())
            {
                _editingProvider.backendType = "hermes";
                _editingProvider.baseUrl = string.Empty;
                _editingProvider.defaultModel = string.Empty;
            }
            else
            {
                _editingProvider.backendType = null;
            }

            _lastCustomModel = _editingProvider.defaultModel ?? string.Empty;
            _editModelUsesCustomMode = false;
            _discoveredModels = null;

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
            SyncGlobalBackendModeUi(ChatService.IsHermesProvider(_editingProvider)
                ? BackendMode.Hermes
                : BackendMode.OpenAI);
            _lastCustomModel = _editingProvider.defaultModel ?? string.Empty;
            _editModelUsesCustomMode = false;
            _discoveredModels = null;
            ShowProviderEditPanel();
        }

        private void ShowProviderEditPanel()
        {
            if (_d.ProviderEditPanel == null || _editingProvider == null)
                return;

            // Reset API key visibility to hidden when opening/switching editor (U-16)
            _apiKeyVisible = false;
            if (_d.EditApiKey != null)
                _d.EditApiKey.isPasswordField = true;
            if (_d.EditApiKeyToggle != null)
            {
                _d.EditApiKeyToggle.RemoveFromClassList("icon--eye-off");
                _d.EditApiKeyToggle.AddToClassList("icon--eye");
            }

            if (_d.EditorProviderShort != null)
                _d.EditorProviderShort.text = BuildProviderShort(_editingProvider);
            if (_d.EditorProviderName != null)
                _d.EditorProviderName.text = string.IsNullOrWhiteSpace(_editingProvider.displayName) ? "—" : _editingProvider.displayName;
            UpdateEditorStatus();
            if (_d.EditName != null)
                _d.EditName.SetValueWithoutNotify(_editingProvider.displayName ?? string.Empty);
            if (_d.EditBaseUrl != null)
                _d.EditBaseUrl.SetValueWithoutNotify(_editingProvider.baseUrl ?? string.Empty);
            if (_d.EditApiKey != null)
                _d.EditApiKey.SetValueWithoutNotify(_editingProvider.apiKey ?? string.Empty);
            if (_d.EditModel != null)
                _d.EditModel.SetValueWithoutNotify(_editingProvider.defaultModel ?? string.Empty);
            SyncModelPresetUi(_editingProvider.defaultModel ?? string.Empty);
            if (_d.EditTemperature != null)
                _d.EditTemperature.SetValueWithoutNotify(_editingProvider.temperature);
            if (_d.EditMaxTokens != null)
                _d.EditMaxTokens.SetValueWithoutNotify(_editingProvider.maxTokens.ToString());

            bool isHermes = ChatService.IsHermesProvider(_editingProvider);

            // Temperature / max tokens only apply to the OpenAI HTTP path. Hermes drives
            // generation server-side (session.create ignores them), so hide that row.
            var generationRow = _d.EditTemperature?.parent?.parent;
            SetDisplay(generationRow, isHermes ? DisplayStyle.None : DisplayStyle.Flex);

            // Voice section: shown for OpenAI-compatible providers only.
            EnsureVoiceEditorFields();
            SetDisplay(_editVoiceSection, isHermes ? DisplayStyle.None : DisplayStyle.Flex);
            if (!isHermes)
                SyncVoiceFieldsToUi(_editingProvider);

            // Desktop-style Remote Hermes gateway section (URL + Connect; token under Advanced).
            EnsureGatewayEditorSection();
            ApplyHermesGatewayLayout(isHermes);
            if (isHermes)
                SyncGatewayFieldsToUi(_editingProvider);

            SetTestRow(null, string.Empty);
            _d.ProviderEditPanel.style.display = DisplayStyle.Flex;
            _ = RefreshProvidersListAsync();

            if (_editingProviderSource != null && !string.IsNullOrWhiteSpace(_editingProvider.baseUrl))
                _ = AutoDiscoverModelsAsync();
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
                var app = await _d.GetAppAsync();
                if (app == null)
                    return;

                var draft = BuildProviderDraftFromEditor();
                if (draft == null)
                    return;

                SyncGlobalBackendModeUi(ChatService.IsHermesProvider(draft)
                    ? BackendMode.Hermes
                    : BackendMode.OpenAI);

                await app.ProviderManager.SaveProviderAsync(draft);

                bool endpointChanged = _editingProviderSource == null ||
                    !string.Equals(_editingProviderSource.baseUrl, draft.baseUrl, StringComparison.Ordinal) ||
                    !string.Equals(_editingProviderSource.apiKey, draft.apiKey, StringComparison.Ordinal);
                bool authModeChanged = _editingProviderSource != null &&
                    !string.Equals(_editingProviderSource.authMode ?? string.Empty,
                        draft.authMode ?? string.Empty, StringComparison.OrdinalIgnoreCase);

                var chat = await _d.GetChatServiceAsync();
                if (chat?.CurrentProvider?.id == draft.id)
                {
                    bool resetRemoteSession = _editingProviderSource != null &&
                        (endpointChanged ||
                         !string.Equals(_editingProviderSource.defaultModel, draft.defaultModel, StringComparison.Ordinal));
                    await chat.ApplyProviderConfigAsync(draft, resetRemoteSession);
                    SetProviderHeader(chat.CurrentProvider, chat.CurrentSessionModel);

                    // Active Hermes provider edited — re-point the transport and reconnect so the
                    // new URL/key/auth-mode take effect right away (otherwise the old socket lingers).
                    if (ChatService.IsHermesProvider(draft) && (endpointChanged || authModeChanged))
                    {
                        var selector = GlobalBackendSelector.Instance;
                        if (selector != null)
                        {
                            // Leaving cookie mode: drop in-memory session so token mode does not
                            // keep sending a stale Cookie header.
                            bool nowOAuth = string.Equals(draft.authMode, "oauth", StringComparison.OrdinalIgnoreCase);
                            if (!nowOAuth && selector.HasRemoteSession)
                                await selector.ClearHermesRemoteSession();

                            selector.ConfigureHermesEndpoint(draft.baseUrl, draft.apiKey);
                            await selector.ReconnectHermes();
                        }
                    }
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
            SetDisplay(_d.ProviderEditPanel, DisplayStyle.None);
            _ = RefreshProvidersListAsync();
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
                var app = await _d.GetAppAsync();
                if (app == null)
                    return;

                var chat = _d.GetChatServiceSync != null ? _d.GetChatServiceSync() : null;
                bool deletedCurrent = string.Equals(chat?.CurrentProvider?.id, provider.id, StringComparison.Ordinal);
                await app.ProviderManager.DeleteProviderAsync(provider.id);

                if (_editingProvider?.id == provider.id)
                {
                    _cancelPending = false;
                    _editingProvider = null;
                    _editingProviderSource = null;
                    SetDisplay(_d.ProviderEditPanel, DisplayStyle.None);
                }

                // Pick a fallback among the REMAINING providers of the active backend.
                // Do not call GetActiveProviderAsync — it recreates a default provider when the
                // list empties, which would make the deletion look like it never happened.
                var remaining = await app.ProviderManager.GetAllProvidersAsync();
                bool hermesMode = SelectedBackendIsHermes();
                var fallbackProvider = remaining.FirstOrDefault(p => p != null && p.isEnabled && ChatService.IsHermesProvider(p) == hermesMode);

                if (deletedCurrent && fallbackProvider != null)
                {
                    // Restore an active provider in place — stay on the Providers page and do
                    // not create a chat session.
                    if (chat != null)
                    {
                        chat.SetActiveProviderWithoutSession(fallbackProvider);
                        SetProviderHeader(fallbackProvider);
                        SetCurrentSessionUi(string.Empty, string.Empty);
                        if (_d.RenderMessages != null)
                            _d.RenderMessages();
                    }

                    var fallbackSettings = app.Settings.Load() ?? new AppSettings();
                    fallbackSettings.activeProviderId = fallbackProvider.id;
                    SetActiveProviderIdForMode(fallbackSettings, _providersBackendMode, fallbackProvider.id);
                    app.Settings.Save(fallbackSettings);

                    await RefreshProvidersListAsync();
                    if (_d.LoadSessionsAsync != null)
                        await _d.LoadSessionsAsync();
                    return;
                }

                var settings = app.Settings.Load() ?? new AppSettings();
                if (string.Equals(settings.activeProviderId, provider.id, StringComparison.Ordinal))
                    settings.activeProviderId = fallbackProvider?.id;
                string savedForMode = GetActiveProviderIdForMode(settings, _providersBackendMode);
                if (string.Equals(savedForMode, provider.id, StringComparison.Ordinal))
                    SetActiveProviderIdForMode(settings, _providersBackendMode, fallbackProvider?.id);
                app.Settings.Save(settings);

                if (deletedCurrent && fallbackProvider == null)
                {
                    if (chat != null)
                        chat.ClearActiveProviderState();
                    ClearProviderHeader();
                }

                await RefreshProvidersListAsync();
            }
            catch (Exception ex)
            {
                NeonLogger.LogError(ex.ToString());
            }
        }

        private async Task ToggleProviderEnabledAsync(ProviderConfig provider)
        {
            if (provider == null)
                return;

            try
            {
                var app = await _d.GetAppAsync();
                if (app == null)
                    return;

                var updated = CloneProvider(provider);
                BackendMode providerMode = ChatService.IsHermesProvider(updated) ? BackendMode.Hermes : BackendMode.OpenAI;
                var settings = app.Settings.Load() ?? new AppSettings();
                var activeChat = _d.GetChatServiceSync != null ? _d.GetChatServiceSync() : null;
                string savedForMode = GetActiveProviderIdForMode(settings, providerMode);
                // Deterministic single-tap: just flip the persisted enabled flag. (Was
                // "!isEnabled || !isActive", which on a restored-but-not-yet-active provider
                // evaluated to "disable" on the first tap — hence the two-tap behaviour.)
                bool activateProvider = !provider.isEnabled;

                updated.isEnabled = activateProvider;
                await app.ProviderManager.SaveProviderAsync(updated);

                if (activateProvider)
                {
                    var allProviders = await app.ProviderManager.GetAllProvidersAsync();
                    bool hermesMode = providerMode == BackendMode.Hermes;
                    for (int i = 0; i < allProviders.Count; i++)
                    {
                        var other = allProviders[i];
                        if (other == null || string.Equals(other.id, updated.id, StringComparison.Ordinal))
                            continue;
                        if (ChatService.IsHermesProvider(other) != hermesMode)
                            continue;
                        if (!other.isEnabled)
                            continue;

                        var disabledOther = CloneProvider(other);
                        disabledOther.isEnabled = false;
                        await app.ProviderManager.SaveProviderAsync(disabledOther);
                    }

                    var selector = GlobalBackendSelector.Instance;
                    if (selector != null)
                    {
                        if (selector.CurrentMode != providerMode)
                            await selector.SetMode(providerMode);
                        SyncGlobalBackendModeUi(providerMode);
                    }

                    activeChat = await _d.GetChatServiceAsync();
                    if (activeChat != null)
                    {
                        activeChat.SetActiveProviderWithoutSession(updated);
                        SetProviderHeader(updated);
                        SetCurrentSessionUi(string.Empty, string.Empty);
                    }

                    settings.activeProviderId = updated.id;
                    SetActiveProviderIdForMode(settings, providerMode, updated.id);
                    app.Settings.Save(settings);

                    if (_d.RenderMessages != null)
                        _d.RenderMessages();
                }
                else
                {
                    if (string.Equals(settings.activeProviderId, updated.id, StringComparison.Ordinal))
                        settings.activeProviderId = null;
                    if (string.Equals(savedForMode, updated.id, StringComparison.Ordinal))
                        SetActiveProviderIdForMode(settings, providerMode, null);
                    app.Settings.Save(settings);
                }

                if (_editingProvider != null && string.Equals(_editingProvider.id, updated.id, StringComparison.Ordinal))
                    _editingProvider.isEnabled = updated.isEnabled;
                if (_editingProviderSource != null && string.Equals(_editingProviderSource.id, updated.id, StringComparison.Ordinal))
                    _editingProviderSource = updated;

                var chat = _d.GetChatServiceSync != null ? _d.GetChatServiceSync() : null;
                if (chat?.CurrentProvider != null && string.Equals(chat.CurrentProvider.id, updated.id, StringComparison.Ordinal))
                {
                    if (updated.isEnabled)
                    {
                        await chat.ApplyProviderConfigAsync(updated);
                        SetProviderHeader(chat.CurrentProvider, chat.CurrentSessionModel);
                    }
                    else
                    {
                        chat.ClearActiveProviderState();
                        if (_d.SetCurrentSessionId != null)
                            _d.SetCurrentSessionId(string.Empty);
                        if (_d.SetCurrentSessionTitle != null)
                            _d.SetCurrentSessionTitle(string.Empty);
                        ClearProviderHeader();
                        if (_d.RenderMessages != null)
                            _d.RenderMessages();
                    }
                }

                await RefreshProvidersListAsync();
                if (_d.LoadSessionsAsync != null)
                    await _d.LoadSessionsAsync();
                UpdateEditorStatus();
            }
            catch (Exception ex)
            {
                NeonLogger.LogError(ex.ToString());
            }
        }

        private async Task SwitchProviderAsync(ProviderConfig provider, bool navigateToChat = true)
        {
            if (provider == null || !provider.isEnabled)
                return;

            try
            {
                BackendMode providerMode = ChatService.IsHermesProvider(provider)
                    ? BackendMode.Hermes
                    : BackendMode.OpenAI;
                var selector = GlobalBackendSelector.Instance;
                if (selector != null)
                {
                    if (selector.CurrentMode != providerMode)
                        await selector.SetMode(providerMode);
                    SyncGlobalBackendModeUi(providerMode);
                }

                var chat = await _d.GetChatServiceAsync();
                if (chat == null)
                    return;

                await chat.SwitchProviderAsync(provider);

                var app = await _d.GetAppAsync();
                if (app != null)
                {
                    var s = app.Settings.Load() ?? new AppSettings();
                    string activeId = chat.CurrentProvider?.id ?? provider?.id ?? s.activeProviderId;
                    s.activeProviderId = activeId;
                    SetActiveProviderIdForMode(s, providerMode, activeId);
                    app.Settings.Save(s);
                }
                else
                {
                    if (_d.SaveSettings != null)
                        _d.SaveSettings();
                }

                if (_d.SetCurrentSessionId != null)
                    _d.SetCurrentSessionId(chat.CurrentSessionId ?? string.Empty);
                if (_d.SetCurrentSessionTitle != null)
                    _d.SetCurrentSessionTitle(string.Empty);

                SetProviderHeader(provider, chat.CurrentSessionModel);
                UpdateEditorStatus();
                if (_d.RenderMessages != null)
                    _d.RenderMessages();
                if (_d.LoadSessionsAsync != null)
                    await _d.LoadSessionsAsync();
                await RefreshProvidersListAsync();
                if (navigateToChat && _d.ShowChat != null)
                    _d.ShowChat();
            }
            catch (Exception ex)
            {
                NeonLogger.LogError(ex.ToString());
            }
        }

        // ============================================================
        // Provider test
        // ============================================================

        private void OnTestProviderClicked()
        {
            _ = TestProviderConnectionAsync();
        }

        private async Task TestProviderConnectionAsync()
        {
            if (_editingProvider == null) return;

            try
            {
                var app = await _d.GetAppAsync();
                if (app == null) return;

                if (_d.TestProviderBtn != null) _d.TestProviderBtn.SetEnabled(false);
                SetTestRow(null, LocalizationExtensions.Get("providers.test.checking", "Проверяем соединение…"));

                var draft = BuildProviderDraftFromEditor();
                if (draft == null)
                {
                    SetTestRow(false, LocalizationExtensions.Get("providers.test.build_failed", "Не удалось собрать настройки провайдера."));
                    if (_d.TriggerAvatarConfused != null) _d.TriggerAvatarConfused();
                    return;
                }

                // Hermes providers speak WebSocket JSON-RPC, not HTTP REST — test the actual transport.
                if (ChatService.IsHermesProvider(draft))
                {
                    await TestHermesConnectionAsync(draft);
                    return;
                }

                var startedAt = DateTimeOffset.UtcNow;
                var result = await app.AiClient.TestConnectionAsync(draft);
                if (!result.Success)
                {
                    SetTestRow(false, result.Message);
                    return;
                }

                if (result.DiscoveredModels != null && result.DiscoveredModels.Count > 0)
                    SyncModelPresetFromDiscovery(result.DiscoveredModels, GetCurrentModelValue());

                string model = SelectResponsesProbeModel(draft, result.DiscoveredModels);
                if (string.IsNullOrWhiteSpace(model))
                {
                    SetTestRow(false, LocalizationExtensions.Get(
                        "providers.test.model_required",
                        "The server returned no model for a Responses API test."));
                    return;
                }

                // A reachable /models endpoint is not enough. The configured model must produce
                // a real completed Responses object before the provider is marked connected.
                AiChatResponse probe;
                try
                {
                    probe = await app.AiClient.SendMessageAsync(draft, new AiChatRequest
                    {
                        model = model,
                        temperature = 0f,
                        maxTokens = 128,
                        messages = new List<AiChatMessage>
                        {
                            new AiChatMessage { role = "user", content = "Reply with OK." }
                        }
                    }, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    bool missingResponses = ex.Message != null &&
                                            (ex.Message.IndexOf("404", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                             ex.Message.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0);
                    SetTestRow(false, missingResponses
                        ? LocalizationExtensions.Get(
                            "providers.test.responses_not_supported",
                            "The server does not support the Responses API. Update the server.")
                        : ex.Message);
                    return;
                }

                bool hasOutput = probe != null &&
                                 ((!string.IsNullOrWhiteSpace(probe.content)) ||
                                  (probe.responseOutput != null && probe.responseOutput.Count > 0));
                bool completed = probe != null &&
                                 string.Equals(probe.status, "completed", StringComparison.OrdinalIgnoreCase) &&
                                 !string.IsNullOrWhiteSpace(probe.id) &&
                                 hasOutput;
                if (!completed)
                {
                    SetTestRow(false, LocalizationExtensions.Get(
                        "providers.test.responses_incomplete",
                        "The server did not complete a Responses API request."));
                    return;
                }

                long elapsedMs = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
                SetTestRow(true, LocalizationExtensions.Get(
                    "providers.test.responses_ok",
                    "OK · Responses API · {0} ms").Replace("{0}", elapsedMs.ToString()));
            }
            catch (Exception ex)
            {
                SetTestRow(false, LocalizationExtensions.Get("providers.test.failed", "Проверка подключения не выполнена. Проверь адрес, модель и параметры доступа."));
                if (_d.TriggerAvatarConfused != null) _d.TriggerAvatarConfused();
                NeonLogger.LogError(ex.ToString());
            }
            finally
            {
                if (_d.TestProviderBtn != null) _d.TestProviderBtn.SetEnabled(true);
            }
        }

        private static string SelectResponsesProbeModel(ProviderConfig draft, IReadOnlyList<string> discoveredModels)
        {
            if (draft != null && !string.IsNullOrWhiteSpace(draft.defaultModel))
                return draft.defaultModel.Trim();

            if (discoveredModels == null)
                return null;

            for (int i = 0; i < discoveredModels.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(discoveredModels[i]))
                    return discoveredModels[i].Trim();
            }

            return null;
        }

        private async Task TestHermesConnectionAsync(ProviderConfig draft)
        {
            if (draft == null || string.IsNullOrWhiteSpace(draft.baseUrl))
            {
                SetTestRow(false, LocalizationExtensions.Get("providers.test.base_url_required", "Укажите базовый URL Hermes backend."));
                if (_d.TriggerAvatarConfused != null) _d.TriggerAvatarConfused();
                return;
            }

            // Probe the public endpoints first. This is what separates "unreachable" from
            // "gated": the old code always opened a bare WebSocket, and a gated gateway rejects
            // that at the upgrade — so a perfectly healthy server reported "WebSocket: Unable to
            // connect to the remote server" even while the app was talking to it happily.
            HermesAuthProbeResult probe = await HermesRemoteAuth.ProbeAsync(draft.baseUrl);
            if (!_d.IsBound())
                return;

            if (!probe.Reachable)
            {
                SetTestRow(false, LocalizationExtensions.GetFormat(
                    "providers.test.ws_unreachable",
                    "Шлюз недоступен: {0}",
                    probe.Error ?? "unreachable"));
                if (_d.TriggerAvatarConfused != null) _d.TriggerAvatarConfused();
                return;
            }

            bool gated = string.Equals(probe.AuthMode, "oauth", StringComparison.OrdinalIgnoreCase);
            string ticket = null;
            if (gated)
            {
                var selector = GlobalBackendSelector.Instance;
                if (selector != null)
                    ticket = await selector.MintHermesWsTicketAsync(draft.baseUrl);
                if (!_d.IsBound())
                    return;

                if (string.IsNullOrEmpty(ticket))
                {
                    // Reachable but this client holds no session for it. That is a sign-in
                    // state, not a transport fault, and saying so is the actionable message.
                    SetTestRow(false, LocalizationExtensions.Get(
                        "providers.test.ws_needs_signin",
                        "Шлюз доступен, но требует входа — нажми «Подключить / Войти»."));
                    return;
                }
            }

            string wsUrl = GlobalBackendSelector.BuildHermesWsUrl(draft.baseUrl);
            if (gated)
                wsUrl = HermesRemoteAuth.BuildTicketWsUrl(wsUrl, ticket);
            else if (!string.IsNullOrEmpty(draft.apiKey))
                wsUrl += (wsUrl.Contains("?") ? "&" : "?") + "token=" + Uri.EscapeDataString(draft.apiKey);

            var gateway = new HermesGateway { RequestTimeoutMs = 8000 };
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var connectTask = gateway.Connect(wsUrl);
                var done = await Task.WhenAny(connectTask, Task.Delay(8000));
                if (done != connectTask)
                {
                    SetTestRow(false, LocalizationExtensions.Get("providers.test.ws_timeout", "WebSocket: таймаут подключения."));
                    if (_d.TriggerAvatarConfused != null) _d.TriggerAvatarConfused();
                    // Observe the eventual fault so it is not surfaced as an unhandled exception.
                    _ = connectTask.ContinueWith(t => { var _ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    return;
                }

                await connectTask; // surface any connection exception
                stopwatch.Stop();

                if (gateway.State == ConnectionState.Open)
                {
                    SetTestRow(true, LocalizationExtensions.GetFormat("providers.test.ws_ok", "OK · WebSocket · {0} ms", stopwatch.ElapsedMilliseconds));
                    await DiscoverModelsForDraftAsync(draft, CancellationToken.None);
                }
                else
                {
                    SetTestRow(false, LocalizationExtensions.Get("providers.test.ws_failed", "WebSocket: не удалось установить соединение."));
                    if (_d.TriggerAvatarConfused != null) _d.TriggerAvatarConfused();
                }
            }
            catch (Exception ex)
            {
                SetTestRow(false, "WebSocket: " + ex.Message);
                if (_d.TriggerAvatarConfused != null) _d.TriggerAvatarConfused();
            }
            finally
            {
                try { await gateway.Close(); }
                catch { }
                gateway.Dispose();
            }
        }

        private void SetTestRow(bool? success, string message)
        {
            if (_d.TestRowLabel != null)
                _d.TestRowLabel.text = message ?? string.Empty;

            if (_d.TestRow == null) return;

            _d.TestRow.EnableInClassList("testrow--ok",    success == true);
            _d.TestRow.EnableInClassList("testrow--error", success == false);
        }

        // ============================================================
        // API key visibility toggle (U-16)
        // ============================================================

        private void OnApiKeyToggleClicked(ClickEvent evt)
        {
            if (_d.EditApiKey == null)
                return;

            _apiKeyVisible = !_apiKeyVisible;
            _d.EditApiKey.isPasswordField = !_apiKeyVisible;

            if (_d.EditApiKeyToggle != null)
            {
                _d.EditApiKeyToggle.RemoveFromClassList("icon--eye");
                _d.EditApiKeyToggle.AddToClassList(_apiKeyVisible ? "icon--eye-off" : "icon--eye");
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
                var app = await _d.GetAppAsync();
                if (app == null) return;

                var filePicker = app.Services.GetRequired<IFilePickerService>();
                string path = await filePicker.PickFileAsync("json");
                if (string.IsNullOrEmpty(path)) return;

                string json = System.IO.File.ReadAllText(path);
                var imported = JsonUtility.FromJson<ProviderConfigCollection>(json);
                if (imported?.items == null || imported.items.Count == 0)
                {
                    if (_d.AddSystemMessage != null)
                        _d.AddSystemMessage(LocalizationExtensions.Get("providers.import.empty", "Файл не содержит провайдеров."));
                    return;
                }

                foreach (var p in imported.items)
                {
                    if (!string.IsNullOrEmpty(p?.id))
                        await app.ProviderManager.SaveProviderAsync(p);
                }

                await RefreshProvidersListAsync();
                if (_d.AddSystemMessage != null)
                    _d.AddSystemMessage(LocalizationExtensions.GetFormat("providers.import.success", "Импортировано: {0} провайдер(ов).", imported.items.Count));
            }
            catch (Exception ex)
            {
                if (_d.AddSystemMessage != null)
                    _d.AddSystemMessage(LocalizationExtensions.Get("providers.import.failed", "Не удалось импортировать провайдеров из файла."));
                NeonLogger.LogError(ex.ToString());
            }
        }

        // ============================================================
        // Model picker
        // ============================================================

        public async Task OpenModelPickerAsync()
        {
            try
            {
                var app = await _d.GetAppAsync();
                var chat = await _d.GetChatServiceAsync();
                var provider = chat?.CurrentProvider;
                if (app == null || chat == null || provider == null || string.IsNullOrWhiteSpace(provider.id))
                {
                    ClearProviderHeader();
                    if (_d.AddSystemMessage != null)
                        _d.AddSystemMessage(LocalizationExtensions.Get("provider.not_configured.hint", "Провайдер не настроен. Перейди в Провайдеры и добавь API-ключ."));
                    return;
                }

                EnsureModelPickerOverlay();
                if (_modelPickerScroll == null || _modelPickerStatus == null || _modelPickerOverlay == null)
                    return;

                _modelPickerScroll.Clear();
                _modelPickerStatus.text = LocalizationExtensions.Get("providers.test.checking", "Проверяем соединение…");
                if (_modelPickerOverlay.parent == null && _d.Root != null)
                    _d.Root.Add(_modelPickerOverlay);

                // Try model.options RPC for Hermes WS backend (grouped by provider)
                var selector = GlobalBackendSelector.Instance;
                var sessionManager = selector?.SessionManager;
                if (sessionManager != null && sessionManager.IsConnected)
                {
                    try
                    {
                        var options = await sessionManager.GetModelOptionsAsync();
                        if (options?.providers != null && options.providers.Length > 0)
                        {
                            PopulateModelPickerFromOptions(options, options.model ?? chat.CurrentSessionModel);
                            _modelPickerStatus.text = LocalizationExtensions.Get("providers.models.pick_hint", "Выбери модель.");
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("[ModelPicker] model.options failed, falling back: " + ex.Message);
                    }
                }

                // Fallback: flat list from TestConnectionAsync
                var result = await app.AiClient.TestConnectionAsync(provider);
                var models = new List<string>();
                if (result?.DiscoveredModels != null)
                {
                    foreach (var model in result.DiscoveredModels)
                    {
                        if (!string.IsNullOrWhiteSpace(model) && !models.Contains(model))
                            models.Add(model);
                    }
                }

                string currentModel = chat.CurrentSessionModel;
                if (!string.IsNullOrWhiteSpace(currentModel) && !models.Contains(currentModel))
                    models.Add(currentModel);

                if (models.Count == 0 && !string.IsNullOrWhiteSpace(currentModel))
                    models.Add(currentModel);

                if (models.Count == 0)
                {
                    _modelPickerStatus.text = LocalizationExtensions.Get(
                        "providers.models.empty",
                        "Сервер не вернул список моделей.");
                    return;
                }

                PopulateModelPicker(models, currentModel);
                _modelPickerStatus.text = result?.Success == false
                    ? result.Message ?? LocalizationExtensions.Get("providers.test.failed", "Проверка подключения не выполнена. Проверь адрес, модель и параметры доступа.")
                    : LocalizationExtensions.Get("providers.models.pick_hint", "Выбери модель. Для Hermes дождёмся подтверждения переключения перед отправкой.");
            }
            catch (Exception ex)
            {
                if (_modelPickerStatus != null)
                    _modelPickerStatus.text = ex.Message;
                NeonLogger.LogError(ex.ToString());
            }
        }

        public async Task ApplyModelSelectionAsync(string modelId, bool closePickerOnSuccess)
        {
            if (_isApplyingModelSwitch || string.IsNullOrWhiteSpace(modelId))
                return;

            _isApplyingModelSwitch = true;
            if (_modelPickerDialog != null)
                _modelPickerDialog.SetEnabled(false);

            try
            {
                var chat = await _d.GetChatServiceAsync();
                if (chat == null)
                    return;

                if (_modelPickerStatus != null)
                    _modelPickerStatus.text = $"Применяем модель: {modelId}";

                var result = await chat.SetCurrentSessionModelAsync(modelId);
                if (!result.Success)
                {
                    string failure = string.IsNullOrWhiteSpace(result.Message)
                        ? LocalizationExtensions.Get("providers.model_switch_failed", "Не удалось применить модель.")
                        : result.Message;
                    if (_modelPickerStatus != null)
                        _modelPickerStatus.text = failure;
                    if (_d.AddSystemMessage != null)
                        _d.AddSystemMessage(failure);
                    if (_d.TriggerAvatarConfused != null)
                        _d.TriggerAvatarConfused();
                    return;
                }

                SetProviderHeader(chat.CurrentProvider, chat.CurrentSessionModel);
                if (_d.LoadSessionsAsync != null)
                    await _d.LoadSessionsAsync();

                if (closePickerOnSuccess)
                    CloseModelPicker();
            }
            catch (Exception ex)
            {
                if (_modelPickerStatus != null)
                    _modelPickerStatus.text = ex.Message;
                if (_d.AddSystemMessage != null)
                    _d.AddSystemMessage(ex.Message);
                if (_d.TriggerAvatarConfused != null)
                    _d.TriggerAvatarConfused();
                NeonLogger.LogError(ex.ToString());
            }
            finally
            {
                _isApplyingModelSwitch = false;
                if (_modelPickerDialog != null)
                    _modelPickerDialog.SetEnabled(true);
            }
        }

        private void EnsureModelPickerOverlay()
        {
            if (_modelPickerOverlay != null)
                return;

            _modelPickerOverlay = new VisualElement();
            _modelPickerOverlay.AddToClassList("modal-overlay");
            _modelPickerOverlay.pickingMode = PickingMode.Position;
            _modelPickerOverlay.RegisterCallback<ClickEvent>(evt =>
            {
                if (evt.target == _modelPickerOverlay)
                    CloseModelPicker();
            });

            _modelPickerDialog = new VisualElement();
            _modelPickerDialog.AddToClassList("modal");
            _modelPickerDialog.AddToClassList("model-picker");

            var headerRow = new VisualElement();
            headerRow.AddToClassList("model-picker__header");

            var titleWrap = new VisualElement();
            titleWrap.AddToClassList("model-picker__title-wrap");

            var title = new Label(LocalizationExtensions.Get("model_picker.title", "Выбор модели"));
            title.AddToClassList("model-picker__title");

            var subtitle = new Label(LocalizationExtensions.Get("model_picker.subtitle", "Выберите модель для текущей сессии. Применяется сразу."));
            subtitle.AddToClassList("model-picker__subtitle");

            var closeButton = new Button(CloseModelPicker)
            {
                text = LocalizationExtensions.Get("common.close", "Закрыть")
            };
            closeButton.AddToClassList("btn");

            titleWrap.Add(title);
            titleWrap.Add(subtitle);
            headerRow.Add(titleWrap);
            headerRow.Add(closeButton);

            _modelPickerStatus = new Label(string.Empty);
            _modelPickerStatus.AddToClassList("model-picker__status");

            _modelPickerScroll = new ScrollView(ScrollViewMode.Vertical);
            _modelPickerScroll.AddToClassList("model-picker__scroll");

            _modelPickerDialog.Add(headerRow);
            _modelPickerDialog.Add(_modelPickerStatus);
            _modelPickerDialog.Add(_modelPickerScroll);
            _modelPickerOverlay.Add(_modelPickerDialog);
        }

        private void PopulateModelPicker(IReadOnlyList<string> models, string currentModel)
        {
            if (_modelPickerScroll == null)
                return;

            _modelPickerScroll.Clear();

            var grouped = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var model in models)
            {
                string providerKey = GetModelProviderKey(model);
                if (!grouped.TryGetValue(providerKey, out var providerModels))
                {
                    providerModels = new List<string>();
                    grouped[providerKey] = providerModels;
                }
                if (!providerModels.Contains(model))
                    providerModels.Add(model);
            }

            foreach (var providerName in grouped.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                var providerModels = grouped[providerName];
                providerModels.Sort(StringComparer.OrdinalIgnoreCase);

                bool hasActive = providerModels.Exists(m => string.Equals(m, currentModel, StringComparison.Ordinal));
                bool expanded = hasActive;

                var itemsWrap = new VisualElement();
                itemsWrap.AddToClassList("model-picker__items");
                if (!expanded)
                    itemsWrap.AddToClassList("is-hidden");

                var groupBtn = new Button();
                groupBtn.AddToClassList("model-picker__group");

                var arrow = new Label(expanded ? "▼" : "▶");
                arrow.AddToClassList("model-picker__group-arrow");

                var groupLabel = new Label($"{providerName.ToUpperInvariant()}  ({providerModels.Count})");
                groupLabel.AddToClassList("model-picker__group-label");

                groupBtn.Add(arrow);
                groupBtn.Add(groupLabel);
                groupBtn.RegisterCallback<ClickEvent>(_ =>
                {
                    bool isExpanded = !itemsWrap.ClassListContains("is-hidden");
                    if (isExpanded)
                    {
                        itemsWrap.AddToClassList("is-hidden");
                        arrow.text = "▶";
                    }
                    else
                    {
                        itemsWrap.RemoveFromClassList("is-hidden");
                        arrow.text = "▼";
                    }
                });

                foreach (var model in providerModels)
                {
                    bool isSelected = string.Equals(model, currentModel, StringComparison.Ordinal);
                    var captured = model;
                    var modelBtn = new Button(() => _ = ApplyModelSelectionAsync(captured, closePickerOnSuccess: true))
                    {
                        text = model
                    };
                    modelBtn.AddToClassList("model-picker__item");
                    if (isSelected)
                        modelBtn.AddToClassList("model-picker__item--selected");
                    itemsWrap.Add(modelBtn);
                }

                _modelPickerScroll.Add(groupBtn);
                _modelPickerScroll.Add(itemsWrap);
            }
        }

        /// <summary>
        /// Populate model picker from model.options RPC response (grouped by provider).
        /// </summary>
        private void PopulateModelPickerFromOptions(ModelOptionsResponse options, string currentModel)
        {
            if (_modelPickerScroll == null || options?.providers == null)
                return;

            _modelPickerScroll.Clear();

            foreach (var provider in options.providers)
            {
                var models = provider.models;
                if (models == null || models.Length == 0)
                    continue;

                bool isCurrentProvider = (provider.is_current == true) ||
                    string.Equals(provider.slug, options.provider, StringComparison.OrdinalIgnoreCase);
                bool hasActive = Array.Exists(models, m => string.Equals(m, currentModel, StringComparison.Ordinal));
                bool expanded = isCurrentProvider || hasActive;

                // Provider group header
                var groupBtn = new Button();
                groupBtn.AddToClassList("model-picker__group");
                if (isCurrentProvider)
                    groupBtn.AddToClassList("model-picker__group--active");

                var arrow = new Label(expanded ? "▼" : "▶");
                arrow.AddToClassList("model-picker__group-arrow");

                string providerDisplayName = !string.IsNullOrEmpty(provider.name) ? provider.name : provider.slug;
                var groupLabel = new Label($"{providerDisplayName.ToUpperInvariant()}  ({models.Length})");
                groupLabel.AddToClassList("model-picker__group-label");

                groupBtn.Add(arrow);
                groupBtn.Add(groupLabel);

                // Items container
                var itemsWrap = new VisualElement();
                itemsWrap.AddToClassList("model-picker__items");
                if (!expanded)
                    itemsWrap.AddToClassList("is-hidden");

                groupBtn.RegisterCallback<ClickEvent>(_ =>
                {
                    bool isExpanded = !itemsWrap.ClassListContains("is-hidden");
                    if (isExpanded)
                    {
                        itemsWrap.AddToClassList("is-hidden");
                        arrow.text = "▶";
                    }
                    else
                    {
                        itemsWrap.RemoveFromClassList("is-hidden");
                        arrow.text = "▼";
                    }
                });

                // Warning banner
                if (!string.IsNullOrEmpty(provider.warning))
                {
                    var warningLabel = new Label(provider.warning);
                    warningLabel.AddToClassList("model-picker__warning");
                    itemsWrap.Add(warningLabel);
                }

                // Model items
                var unavailable = new HashSet<string>(provider.unavailable_models ?? new string[0]);
                foreach (var model in models)
                {
                    bool isSelected = string.Equals(model, currentModel, StringComparison.Ordinal);
                    bool locked = unavailable.Contains(model);
                    var captured = model;
                    var capturedProvider = provider.slug;

                    var modelBtn = new Button(() =>
                    {
                        if (!locked)
                            _ = ApplyModelFromOptionsAsync(capturedProvider, captured);
                    })
                    {
                        text = model
                    };
                    modelBtn.AddToClassList("model-picker__item");
                    if (isSelected)
                        modelBtn.AddToClassList("model-picker__item--selected");
                    if (locked)
                    {
                        modelBtn.AddToClassList("model-picker__item--locked");
                        modelBtn.SetEnabled(false);
                    }
                    itemsWrap.Add(modelBtn);
                }

                _modelPickerScroll.Add(groupBtn);
                _modelPickerScroll.Add(itemsWrap);
            }
        }

        /// <summary>
        /// Apply model selection from model.options picker (includes provider switch).
        /// </summary>
        private async Task ApplyModelFromOptionsAsync(string providerSlug, string modelId)
        {
            if (_isApplyingModelSwitch || string.IsNullOrWhiteSpace(modelId))
                return;

            _isApplyingModelSwitch = true;
            if (_modelPickerDialog != null)
                _modelPickerDialog.SetEnabled(false);

            try
            {
                if (_modelPickerStatus != null)
                    _modelPickerStatus.text = $"Применяем модель: {modelId}";

                // Update local state immediately (like Desktop)
                var chat = await _d.GetChatServiceAsync();
                if (chat?.CurrentChatViewModel != null)
                    chat.CurrentChatViewModel.SelectedModel = modelId;

                // Update topbar dropdown
                SetProviderHeader(chat?.CurrentProvider, modelId);

                // Close picker immediately
                CloseModelPicker();

                // Fire gateway command async (don't block UI)
                var selector = GlobalBackendSelector.Instance;
                var sessionManager = selector?.SessionManager;
                if (sessionManager != null && sessionManager.IsConnected)
                {
                    _ = sessionManager.SwitchModelAsync(modelId, providerSlug);
                }

                if (_d.LoadSessionsAsync != null)
                    await _d.LoadSessionsAsync();
            }
            catch (Exception ex)
            {
                if (_modelPickerStatus != null)
                    _modelPickerStatus.text = ex.Message;
                NeonLogger.LogError(ex.ToString());
            }
            finally
            {
                _isApplyingModelSwitch = false;
                if (_modelPickerDialog != null)
                    _modelPickerDialog.SetEnabled(true);
            }
        }

        private void CloseModelPicker()
        {
            _modelPickerOverlay?.RemoveFromHierarchy();
        }

        // ============================================================
        // Model preset / topbar picker
        // ============================================================

        private void OnTopbarModelPickerTriggered()
        {
            _ = OpenModelPickerAsync();
        }

        private void OnModelPresetChanged(ChangeEvent<string> evt)
        {
            if (_syncingModelPresetUi)
                return;

            if (evt == null)
                return;

            string selectedLabel = evt.newValue ?? string.Empty;
            bool isCustom = string.Equals(selectedLabel, CustomModelPresetValue, StringComparison.Ordinal);
            SetDisplay(_d.EditModelCustomWrap, isCustom ? DisplayStyle.Flex : DisplayStyle.None);

            if (isCustom)
            {
                if (_d.EditModel != null)
                    _d.EditModel.SetValueWithoutNotify(_lastCustomModel ?? string.Empty);
                _editModelUsesCustomMode = true;
                return;
            }

            _editModelUsesCustomMode = false;
            if (_modelPresetByLabel.TryGetValue(selectedLabel, out string modelId) && _d.EditModel != null)
                _d.EditModel.SetValueWithoutNotify(modelId ?? string.Empty);
        }

        private void OnManualModelChanged(ChangeEvent<string> evt)
        {
            if (_syncingModelPresetUi)
                return;

            if (string.Equals(_d.EditModelPreset?.value, CustomModelPresetValue, StringComparison.Ordinal))
                _lastCustomModel = evt?.newValue ?? string.Empty;
        }

        private void OnGlobalBackendModeChanged(ChangeEvent<string> evt)
        {
            if (_syncingGlobalBackendModeUi)
                return;

            string selected = evt?.newValue;
            if (string.IsNullOrEmpty(selected))
                return;

            BackendMode mode = string.Equals(selected, HermesBackendModeValue, StringComparison.Ordinal)
                ? BackendMode.Hermes
                : BackendMode.OpenAI;

            _ = ApplyGlobalBackendModeChangeAsync(mode);
        }

        private async Task ApplyGlobalBackendModeChangeAsync(BackendMode mode)
        {
            var selector = GlobalBackendSelector.Instance;
            BackendMode previousMode = selector != null ? selector.CurrentMode : _providersBackendMode;
            SyncGlobalBackendModeUi(mode);

            try
            {
                await SaveCurrentActiveProviderForModeAsync(previousMode);

                if (selector != null && selector.CurrentMode != mode)
                    await selector.SetMode(mode);
            }
            catch (Exception ex)
            {
                NeonLogger.LogError(ex.ToString());
            }

            await RestoreActiveProviderForModeAsync(mode);

            // Reset the editor: a provider from the previous backend must not stay open in the
            // editor (saving it there could otherwise look like it belongs to the new backend).
            _cancelPending = false;
            _editingProvider = null;
            _editingProviderSource = null;
            SetDisplay(_d.ProviderEditPanel, DisplayStyle.None);

            // Switching backend changes the visible provider/session bucket, but it must not
            // auto-connect Hermes or create chat sessions. Connection happens when a provider is
            // explicitly used or a Hermes message needs transport.
            await RefreshProvidersListAsync();
            if (_d.LoadSessionsAsync != null)
                await _d.LoadSessionsAsync();
        }

        private async Task SaveCurrentActiveProviderForModeAsync(BackendMode mode)
        {
            var app = await _d.GetAppAsync();
            if (app == null)
                return;

            var chat = _d.GetChatServiceSync != null ? _d.GetChatServiceSync() : null;
            var provider = chat?.CurrentProvider;
            if (provider == null || ChatService.IsHermesProvider(provider) != (mode == BackendMode.Hermes))
                return;

            var settings = app.Settings.Load() ?? new AppSettings();
            SetActiveProviderIdForMode(settings, mode, provider.id);
            settings.activeProviderId = provider.id;
            app.Settings.Save(settings);
        }

        private async Task RestoreActiveProviderForModeAsync(BackendMode mode)
        {
            var app = await _d.GetAppAsync();
            var chat = await _d.GetChatServiceAsync();
            if (app == null || chat == null)
                return;

            var settings = app.Settings.Load() ?? new AppSettings();
            string preferredId = GetActiveProviderIdForMode(settings, mode);
            var provider = await app.ProviderManager.GetActiveProviderForBackendAsync(mode, preferredId, true);

            if (provider == null)
            {
                chat.ClearActiveProviderState();
                ClearProviderHeader();
                SetCurrentSessionUi(string.Empty, string.Empty);
                if (_d.RenderMessages != null)
                    _d.RenderMessages();
                return;
            }

            chat.SetActiveProviderWithoutSession(provider);
            SetProviderHeader(provider);
            SetCurrentSessionUi(string.Empty, string.Empty);
            if (_d.RenderMessages != null)
                _d.RenderMessages();

            settings.activeProviderId = provider.id;
            SetActiveProviderIdForMode(settings, mode, provider.id);
            app.Settings.Save(settings);
        }

        private void SetCurrentSessionUi(string sessionId, string title)
        {
            if (_d.SetCurrentSessionId != null)
                _d.SetCurrentSessionId(sessionId ?? string.Empty);
            if (_d.SetCurrentSessionTitle != null)
                _d.SetCurrentSessionTitle(title ?? string.Empty);
        }

        private static string GetActiveProviderIdForMode(AppSettings settings, BackendMode mode)
        {
            if (settings == null)
                return null;

            string modeSpecific = mode == BackendMode.Hermes
                ? settings.activeHermesProviderId
                : settings.activeOpenAiProviderId;
            return string.IsNullOrWhiteSpace(modeSpecific) ? settings.activeProviderId : modeSpecific;
        }

        private static void SetActiveProviderIdForMode(AppSettings settings, BackendMode mode, string providerId)
        {
            if (settings == null)
                return;

            if (mode == BackendMode.Hermes)
                settings.activeHermesProviderId = providerId;
            else
                settings.activeOpenAiProviderId = providerId;
        }

        private void UpdateBackendModeHint(string selected)
        {
            if (_d.BackendModeHint == null)
                return;

            if (string.Equals(selected, HermesBackendModeValue, StringComparison.Ordinal))
                _d.BackendModeHint.text = "WebSocket транспорт, сессии, tools, крон, канбан";
            else
                _d.BackendModeHint.text = "HTTP REST, чистый чат";
        }

        private void SyncGlobalBackendModeUi(BackendMode mode)
        {
            _providersBackendMode = mode;
            string value = mode == BackendMode.Hermes
                ? HermesBackendModeValue
                : OpenAiBackendModeValue;

            _syncingGlobalBackendModeUi = true;
            if (_d.GlobalBackendMode != null)
            {
                _d.GlobalBackendMode.SetValueWithoutNotify(value);
            }
            UpdateBackendModeHint(value);
            _syncingGlobalBackendModeUi = false;
        }

        private void SyncModelPresetUi(string currentModel)
        {
            if (_d.EditModelPreset == null)
                return;

            if (_discoveredModels != null && _discoveredModels.Count > 0)
            {
                SyncModelPresetFromDiscovery(_discoveredModels, currentModel);
                return;
            }

            _syncingModelPresetUi = true;
            _modelPresetByLabel.Clear();
            _d.EditModelPreset.choices = new List<string> { CustomModelPresetValue };
            _lastCustomModel = currentModel ?? string.Empty;
            _d.EditModelPreset.SetValueWithoutNotify(CustomModelPresetValue);
            _editModelUsesCustomMode = true;
            SetDisplay(_d.EditModelCustomWrap, DisplayStyle.Flex);
            if (_d.EditModel != null)
                _d.EditModel.SetValueWithoutNotify(_lastCustomModel);
            _syncingModelPresetUi = false;
        }

        private void SyncModelPresetFromDiscovery(IReadOnlyList<string> discoveredModels, string currentModel)
        {
            if (_d.EditModelPreset == null || discoveredModels == null || discoveredModels.Count == 0)
                return;

            _discoveredModels = discoveredModels;
            _syncingModelPresetUi = true;
            _modelPresetByLabel.Clear();
            var choices = new List<string>(discoveredModels.Count + 1);
            foreach (var modelId in discoveredModels)
            {
                if (string.IsNullOrWhiteSpace(modelId)) continue;
                _modelPresetByLabel[modelId] = modelId;
                choices.Add(modelId);
            }
            choices.Add(CustomModelPresetValue);
            _d.EditModelPreset.choices = choices;

            string targetChoice = CustomModelPresetValue;
            if (!string.IsNullOrWhiteSpace(currentModel) && _modelPresetByLabel.ContainsKey(currentModel))
                targetChoice = currentModel;
            else if (string.IsNullOrWhiteSpace(currentModel) && discoveredModels.Count > 0 && !string.IsNullOrWhiteSpace(discoveredModels[0]))
                targetChoice = discoveredModels[0];

            bool showCustom = string.Equals(targetChoice, CustomModelPresetValue, StringComparison.Ordinal);
            if (showCustom)
                _lastCustomModel = currentModel ?? string.Empty;
            _editModelUsesCustomMode = showCustom;

            _d.EditModelPreset.SetValueWithoutNotify(targetChoice);
            SetDisplay(_d.EditModelCustomWrap, showCustom ? DisplayStyle.Flex : DisplayStyle.None);
            if (_d.EditModel != null)
                _d.EditModel.SetValueWithoutNotify(showCustom ? (_lastCustomModel ?? string.Empty) : (targetChoice ?? string.Empty));

            _syncingModelPresetUi = false;
        }

        private string GetCurrentModelValue()
        {
            if (string.Equals(_d.EditModelPreset?.value, CustomModelPresetValue, StringComparison.Ordinal))
                return _d.EditModel?.value ?? string.Empty;

            string selected = _d.EditModelPreset?.value ?? string.Empty;
            if (_modelPresetByLabel.TryGetValue(selected, out string presetModel))
                return presetModel ?? string.Empty;

            return _d.EditModel?.value ?? string.Empty;
        }

        private void SyncTopbarModelPicker(string currentModel)
        {
            if (_d.TopbarModelPicker == null) return;

            string target = string.IsNullOrWhiteSpace(currentModel)
                ? LocalizationExtensions.Get("providers.models.choose", "Выбрать модель")
                : currentModel;

            _d.TopbarModelPicker.choices = new List<string> { target };
            _d.TopbarModelPicker.SetValueWithoutNotify(target);
        }

        public void ShowTopbarModelPicker()
        {
            SetDisplay(_d.TopbarModelPicker, DisplayStyle.Flex);
        }

        public void HideTopbarModelPicker()
        {
            SetDisplay(_d.TopbarModelPicker, DisplayStyle.None);
        }

        // ============================================================
        // Provider field change handlers
        // ============================================================

        private void OnProviderNameChanged(ChangeEvent<string> _)
        {
            if (_syncingModelPresetUi) return;
            if (_d.EditorProviderName != null)
                _d.EditorProviderName.text = string.IsNullOrWhiteSpace(_d.EditName?.value) ? "—" : _d.EditName.value;
        }

        private void OnBaseUrlChanged(ChangeEvent<string> _)
        {
            if (_syncingModelPresetUi) return;
            _discoveredModels = null;
            SyncModelPresetUi(GetCurrentModelValue());
        }

        private void OnProviderEndpointChanged(ChangeEvent<string> _)
        {
            if (_syncingModelPresetUi)
                return;

            SyncModelPresetUi(GetCurrentModelValue());

            _autoDiscoverSchedule?.Pause();
            _autoDiscoverSchedule = _d.ProviderEditPanel?.schedule.Execute(StartAutoDiscoverModels).StartingIn(800);
        }

        // ============================================================
        // Auto-discover models
        // ============================================================

        private void StartAutoDiscoverModels()
        {
            _ = AutoDiscoverModelsAsync();
        }

        private async Task AutoDiscoverModelsAsync()
        {
            _autoDiscoverCts?.Cancel();
            _autoDiscoverCts = new CancellationTokenSource();
            var ct = _autoDiscoverCts.Token;

            var currentDraft = BuildProviderDraftFromEditor();
            if (currentDraft == null || string.IsNullOrWhiteSpace(currentDraft.baseUrl))
                return;

            try
            {
                var app = await _d.GetAppAsync();
                if (app == null || ct.IsCancellationRequested) return;

                if (ChatService.IsHermesProvider(currentDraft))
                {
                    await DiscoverModelsForDraftAsync(currentDraft, ct);
                    return;
                }

                var result = await app.AiClient.TestConnectionAsync(currentDraft, ct);
                if (ct.IsCancellationRequested) return;

                if (result.Success && result.DiscoveredModels != null && result.DiscoveredModels.Count > 0)
                {
                    SyncModelPresetFromDiscovery(result.DiscoveredModels, GetCurrentModelValue());
                    SyncTopbarModelPicker(GetCurrentModelValue());
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                NeonLogger.LogWarning($"Auto-discover models failed: {ex.Message}");
            }
        }

        private async Task DiscoverModelsForDraftAsync(ProviderConfig draft, CancellationToken cancellationToken)
        {
            var app = await _d.GetAppAsync();
            if (app == null || cancellationToken.IsCancellationRequested)
                return;

            ModelDiscoveryService discovery = null;
            app.Services.TryGet<ModelDiscoveryService>(out discovery);
            if (discovery == null)
                return;

            var models = await discovery.DiscoverModelsAsync(draft, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                return;

            if (models != null && models.Count > 0)
            {
                SyncModelPresetFromDiscovery(models, GetCurrentModelValue());
                SyncTopbarModelPicker(GetCurrentModelValue());
            }
        }

        // ============================================================
        // Editor status
        // ============================================================

        public void UpdateEditorStatus()
        {
            if (_d.EditorProviderStatus == null)
                return;

            if (_editingProviderSource == null)
            {
                _d.EditorProviderStatus.text = LocalizationExtensions.Get("providers.editor.status.new_draft", "Новый черновик");
                _d.EditorProviderStatus.EnableInClassList("editor__status--active", false);
                _d.EditorProviderStatus.EnableInClassList("editor__status--inactive", false);
                _d.EditorProviderStatus.EnableInClassList("editor__status--draft", true);
                return;
            }

            var chat = _d.GetChatServiceSync != null ? _d.GetChatServiceSync() : null;
            bool isActive = string.Equals(chat?.CurrentProvider?.id, _editingProviderSource.id, StringComparison.Ordinal);
            _d.EditorProviderStatus.text = isActive
                ? LocalizationExtensions.Get("providers.editor.status.active", "В редакторе: активный провайдер")
                : LocalizationExtensions.Get("providers.editor.status.inactive", "В редакторе: неактивный провайдер");
            _d.EditorProviderStatus.EnableInClassList("editor__status--active", isActive);
            _d.EditorProviderStatus.EnableInClassList("editor__status--inactive", !isActive);
            _d.EditorProviderStatus.EnableInClassList("editor__status--draft", false);
        }

        // ============================================================
        // Provider header
        // ============================================================

        public void SetProviderHeader(ProviderConfig provider, string currentModel = null)
        {
            if (provider == null)
            {
                ClearProviderHeader();
                return;
            }

            string shortName = BuildProviderShort(provider);
            string displayName = string.IsNullOrWhiteSpace(provider.displayName)
                ? LocalizationExtensions.Get("common.dash", "—")
                : provider.displayName;
            string model = string.IsNullOrWhiteSpace(currentModel)
                ? (string.IsNullOrWhiteSpace(provider.defaultModel) ? string.Empty : provider.defaultModel)
                : currentModel;

            if (_d.ProviderShort != null)
                _d.ProviderShort.text = shortName;
            if (_d.ProviderName != null)
                _d.ProviderName.text = displayName;
            if (_d.ProviderModel != null)
                _d.ProviderModel.text = model;
            if (_d.RailProviderName != null)
                _d.RailProviderName.text = displayName;
            if (_d.RailProviderModel != null)
                _d.RailProviderModel.text = model;
            SyncTopbarModelPicker(model);
            RefreshHermesProfileSelector();
        }

        public void ClearProviderHeader()
        {
            string empty = string.Empty;
            if (_d.ProviderShort != null)
                _d.ProviderShort.text = LocalizationExtensions.Get("provider.short.default", "API");
            if (_d.ProviderName != null)
                _d.ProviderName.text = LocalizationExtensions.Get("provider.status.none", "нет провайдера");
            if (_d.ProviderModel != null)
                _d.ProviderModel.text = empty;
            if (_d.RailProviderName != null)
                _d.RailProviderName.text = LocalizationExtensions.Get("provider.status.none", "нет провайдера");
            if (_d.RailProviderModel != null)
                _d.RailProviderModel.text = empty;
            if (_d.TopbarModelPicker != null)
            {
                _d.TopbarModelPicker.choices = new List<string>();
                _d.TopbarModelPicker.SetValueWithoutNotify(empty);
            }
            RefreshHermesProfileSelector();
        }

        // ============================================================
        // Hermes backend profile selector (rail footer)
        // ============================================================
        // Hermes profiles are BACKEND profiles: one gateway hosts several, each with its own
        // sessions/model/skills. Selecting one is a full context switch (see
        // ChatService.SwitchHermesProfileAsync). Non-Hermes providers never see this row.

        private void EnsureProfileSelector()
        {
            if (_profileFieldsBuilt)
                return;

            VisualElement footer = _d.RailFooter;
            if (footer == null || footer.parent == null)
                return;

            _profileFieldsBuilt = true;

            _profileRow = new VisualElement();
            _profileRow.name = "rail-hermes-profile";
            _profileRow.AddToClassList("rail__profile");
            _profileRow.style.display = DisplayStyle.None;

            // Icon strip. Scrollbars stay hidden: the rail is ~208px wide, so overflow is
            // reached by dragging with LMB (OnProfileStripPointer*) or the wheel, not by a
            // scrollbar that would eat a third of the row height.
            _profileStrip = new ScrollView(ScrollViewMode.Horizontal);
            _profileStrip.name = "rail-hermes-profile-strip";
            _profileStrip.AddToClassList("rail__profile-strip");
            _profileStrip.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            _profileStrip.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            _profileStrip.contentContainer.style.flexDirection = FlexDirection.Row;
            _profileStrip.RegisterCallback<PointerDownEvent>(OnProfileStripPointerDown, TrickleDown.TrickleDown);
            _profileStrip.RegisterCallback<PointerMoveEvent>(OnProfileStripPointerMove);
            _profileStrip.RegisterCallback<PointerUpEvent>(OnProfileStripPointerUp);
            _profileStrip.RegisterCallback<PointerCaptureOutEvent>(OnProfileStripPointerCaptureOut);
            _profileStrip.RegisterCallback<WheelEvent>(OnProfileStripWheel);
            _profileRow.Add(_profileStrip);

            // One caption line under the strip: icons alone cannot say which profile is which.
            // The "Профиль" tag, the active name and its model all share this row — a separate
            // header row above the chips cost a whole line to say one word.
            var caption = new VisualElement();
            caption.AddToClassList("rail__profile-caption");

            var label = new Label(LocalizationExtensions.Get("providers.profile.label", "Профиль"));
            label.AddToClassList("rail__profile-label");
            caption.Add(label);

            _profileStatus = new Label(string.Empty);
            _profileStatus.AddToClassList("rail__profile-status");
            caption.Add(_profileStatus);

            _profileMeta = new Label(string.Empty);
            _profileMeta.AddToClassList("rail__profile-meta");
            _profileMeta.style.display = DisplayStyle.None;
            caption.Add(_profileMeta);
            _profileRow.Add(caption);

            VisualElement parent = footer.parent;
            int idx = parent.IndexOf(footer);
            if (idx >= 0)
                parent.Insert(idx + 1, _profileRow);
            else
                parent.Add(_profileRow);
        }

        /// <summary>
        /// Show and refresh the backend-profile selector next to the active-provider indicator.
        /// The list is fetched once per gateway endpoint; pass <paramref name="force"/> to refetch.
        /// </summary>
        public void RefreshHermesProfileSelector(bool force = false)
        {
            var selector = GlobalBackendSelector.Instance;
            var chat = _d.GetChatServiceSync != null ? _d.GetChatServiceSync() : null;
            bool hermes = selector != null
                && selector.CurrentMode == BackendMode.Hermes
                && ChatService.IsHermesProvider(chat != null ? chat.CurrentProvider : null);

            // Transport events used to be hooked only when the provider editor was built, so a
            // start that never opened Providers never learned the socket came up. This runs on
            // every provider-header update and the selector exists by then.
            HookGatewayAuthEvents();

            EnsureProfileSelector();
            if (_profileRow == null)
                return;

            SetDisplay(_profileRow, hermes ? DisplayStyle.Flex : DisplayStyle.None);
            // For Hermes the rail's model line is folded into the profile caption below the
            // chips — two model lines under one provider was the bulk of the clutter.
            SetDisplay(_d.RailProviderModel, hermes ? DisplayStyle.None : DisplayStyle.Flex);
            if (!hermes)
            {
                _profilesLoadedForEndpoint = null;
                return;
            }

            // Covers the other half of the startup race: the socket may already be up by the
            // time this UI binds, so the Connected event has been and gone.
            if (selector.SessionManager != null && selector.SessionManager.IsConnected)
                NoteTransportConnected();

            string endpoint = selector.HermesRestUrl ?? string.Empty;
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                // No gateway configured yet — retry once the provider supplies its URL.
                _profilesLoadedForEndpoint = null;
                SetProfileStatus(LocalizationExtensions.Get(
                    "providers.profile.unavailable", "Гейтвей недоступен."));
                return;
            }

            if (!force && string.Equals(_profilesLoadedForEndpoint, endpoint, StringComparison.Ordinal))
            {
                SyncProfileSelection();
                return;
            }

            _profilesLoadedForEndpoint = endpoint;
            _ = LoadHermesProfilesAsync();
        }

        private async Task LoadHermesProfilesAsync()
        {
            var selector = GlobalBackendSelector.Instance;
            if (selector == null || selector.RestClient == null)
            {
                SetProfileStatus(LocalizationExtensions.Get(
                    "providers.profile.unavailable", "Гейтвей недоступен."));
                return;
            }

            SetProfileStatus(LocalizationExtensions.Get("providers.profile.loading", "Загрузка профилей…"));

            HermesProfilesResponse response;
            try
            {
                response = await selector.RestClient.GetProfiles();
            }
            catch (Exception ex)
            {
                // Refetch on the next refresh — the gateway may just not be reachable yet.
                _profilesLoadedForEndpoint = null;
                if (!_d.IsBound())
                    return;
                SetProfileStatus(LocalizationExtensions.Get(
                    "providers.profile.failed", "Не удалось загрузить профили."));
                NeonLogger.LogWarning("[Providers] Hermes profiles fetch failed: " + ex.Message);
                return;
            }

            if (!_d.IsBound())
                return;

            _hermesProfiles.Clear();
            if (response != null && response.profiles != null)
            {
                for (int i = 0; i < response.profiles.Length; i++)
                {
                    HermesProfile profile = response.profiles[i];
                    if (profile != null && !string.IsNullOrWhiteSpace(profile.name))
                        _hermesProfiles.Add(profile);
                }
            }

            RebuildProfileChips();

            if (_hermesProfiles.Count == 0)
            {
                SetProfileStatus(LocalizationExtensions.Get("providers.profile.empty", "Профилей нет."));
                return;
            }

            SyncProfileSelection();
        }

        // ── Chip strip ──────────────────────────────────────────────

        private void RebuildProfileChips()
        {
            if (_profileStrip == null)
                return;

            _profileChipByName.Clear();
            _profileStrip.Clear();
            _hoveredProfileName = null;

            for (int i = 0; i < _hermesProfiles.Count; i++)
            {
                HermesProfile profile = _hermesProfiles[i];
                string profileName = profile.name;

                var chip = new VisualElement();
                chip.AddToClassList("rail__profile-chip");
                chip.userData = profileName;

                var glyph = new Label(BuildProfileGlyph(profileName));
                glyph.AddToClassList("rail__profile-chip-glyph");
                // The chip itself must stay the pointer target so press/drag bookkeeping
                // does not have to walk out of the label.
                glyph.pickingMode = PickingMode.Ignore;
                chip.Add(glyph);

                if (profile.is_default)
                {
                    var mark = new VisualElement();
                    mark.AddToClassList("rail__profile-chip-default");
                    mark.pickingMode = PickingMode.Ignore;
                    chip.Add(mark);
                }

                string hoverName = profileName;
                chip.RegisterCallback<PointerEnterEvent>(_ => OnProfileChipHover(hoverName));
                chip.RegisterCallback<PointerLeaveEvent>(_ => OnProfileChipHover(null));

                _profileChipByName[profileName] = chip;
                _profileStrip.Add(chip);
            }
        }

        /// <summary>
        /// Reflect the active profile on the strip. With no explicit selection the gateway uses
        /// its default profile, so that one is shown (display only — no switch is triggered).
        /// </summary>
        private void SyncProfileSelection()
        {
            var selector = GlobalBackendSelector.Instance;
            string active = selector != null ? selector.ActiveHermesProfile : null;
            if (string.IsNullOrEmpty(active))
                active = DefaultHermesProfileName();

            VisualElement activeChip = null;
            foreach (var pair in _profileChipByName)
            {
                bool isActive = string.Equals(pair.Key, active, StringComparison.Ordinal);
                ApplyProfileChipStyle(pair.Value, pair.Key, isActive);
                if (isActive)
                    activeChip = pair.Value;
            }

            // A pointer resting on a chip keeps that chip described — a background refresh
            // must not yank the caption back to the active profile under the user's cursor.
            ShowProfileCaption(string.IsNullOrEmpty(_hoveredProfileName) ? active : _hoveredProfileName);

            // A freshly switched profile may sit past the right edge of the strip.
            if (activeChip != null && _profileStrip != null)
            {
                VisualElement target = activeChip;
                _profileStrip.schedule.Execute(() =>
                {
                    if (_profileStrip != null && target.panel != null)
                        _profileStrip.ScrollTo(target);
                }).ExecuteLater(0);
            }
        }

        private void ApplyProfileChipStyle(VisualElement chip, string profileName, bool isActive)
        {
            if (chip == null)
                return;

            chip.EnableInClassList("rail__profile-chip--active", isActive);

            // Hue is per-profile data, so it has to be inline; the ring, size and radius stay in
            // USS. Active is a filled chip with dark text against dimmed, washed-out siblings —
            // a tinted 1px border alone was invisible and nobody could tell what was selected.
            Color hue = ProfileChipColor(profileName);
            if (isActive)
            {
                chip.style.backgroundColor = new StyleColor(hue);
                chip.style.color = new StyleColor(ReadableOn(hue));
            }
            else
            {
                chip.style.backgroundColor = new StyleColor(new Color(hue.r, hue.g, hue.b, 0.14f));
                chip.style.color = new StyleColor(new Color(hue.r, hue.g, hue.b, 0.75f));
            }
        }

        // Text colour with enough contrast on a filled chip. Perceptual weights, not a flat
        // average — the yellows/greens in the palette need dark text, the blues/violets light.
        private static Color ReadableOn(Color background)
        {
            float luminance = 0.299f * background.r + 0.587f * background.g + 0.114f * background.b;
            return luminance > 0.62f ? new Color(0.05f, 0.05f, 0.07f) : Color.white;
        }

        /// <summary>
        /// Paint a chip as active before the switch has actually happened. The round trip drops
        /// the chat and reloads sessions, so without this the click looks ignored for a second.
        /// A failure path calls <see cref="SyncProfileSelection"/> and the truth comes back.
        /// </summary>
        private void MarkProfilePending(string profileName)
        {
            foreach (var pair in _profileChipByName)
                ApplyProfileChipStyle(pair.Value, pair.Key, string.Equals(pair.Key, profileName, StringComparison.Ordinal));
        }

        private void OnProfileChipHover(string profileName)
        {
            _hoveredProfileName = profileName;
            if (_profileSwitchBusy)
                return;

            string shown = profileName;
            if (string.IsNullOrEmpty(shown))
            {
                var selector = GlobalBackendSelector.Instance;
                shown = selector != null ? selector.ActiveHermesProfile : null;
                if (string.IsNullOrEmpty(shown))
                    shown = DefaultHermesProfileName();
            }
            ShowProfileCaption(shown);
        }

        /// <summary>
        /// Spell out a profile under the strip as "name · model". The default profile is marked
        /// by the dot on its chip, not by a word here — the row has no space for one.
        /// </summary>
        private void ShowProfileCaption(string profileName)
        {
            if (string.IsNullOrEmpty(profileName))
            {
                SetProfileStatus(string.Empty);
                return;
            }

            SetProfileStatus(profileName);

            // For the profile in use, the session's model is the truth; the profile's own model
            // field is only its default and may have been overridden. A hovered other profile
            // has no session, so its configured model is all there is to preview.
            HermesProfile profile = FindProfile(profileName);
            string model = profile != null ? profile.model : null;
            if (IsActiveProfile(profileName))
            {
                var chat = _d.GetChatServiceSync != null ? _d.GetChatServiceSync() : null;
                string sessionModel = chat != null ? chat.CurrentSessionModel : null;
                if (!string.IsNullOrWhiteSpace(sessionModel))
                    model = sessionModel;
            }

            SetProfileMeta(model);
        }

        /// <summary>
        /// What to hand the backend for a chip. The gateway's own default profile is addressed by
        /// sending NO profile at all — that is the state the app boots in (ActiveHermesProfile is
        /// null until something switches it). Sending the literal name instead meant "switch away
        /// and back" landed in a different scope than a fresh start, and the sessions listed there
        /// stopped resuming ("session not found").
        /// </summary>
        private string ProfileSwitchTarget(string profileName)
        {
            HermesProfile profile = FindProfile(profileName);
            return profile != null && profile.is_default ? null : profileName;
        }

        private bool IsActiveProfile(string profileName)
        {
            var selector = GlobalBackendSelector.Instance;
            string active = selector != null ? selector.ActiveHermesProfile : null;
            if (string.IsNullOrEmpty(active))
                active = DefaultHermesProfileName();
            return string.Equals(profileName, active, StringComparison.Ordinal);
        }

        private HermesProfile FindProfile(string profileName)
        {
            for (int i = 0; i < _hermesProfiles.Count; i++)
            {
                if (string.Equals(_hermesProfiles[i].name, profileName, StringComparison.Ordinal))
                    return _hermesProfiles[i];
            }
            return null;
        }

        private string DefaultHermesProfileName()
        {
            for (int i = 0; i < _hermesProfiles.Count; i++)
            {
                if (_hermesProfiles[i].is_default)
                    return _hermesProfiles[i].name;
            }
            return _hermesProfiles.Count > 0 ? _hermesProfiles[0].name : null;
        }

        private void SetProfileStatus(string text)
        {
            // The model line belongs to whatever the caption line currently says, so every
            // status message (loading / failed / switching) drops it; ShowProfileCaption
            // puts it back right after.
            SetProfileMeta(null);

            if (_profileStatus == null)
                return;
            _profileStatus.text = text ?? string.Empty;
            SetDisplay(_profileStatus, string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex);
        }

        private void SetProfileMeta(string text)
        {
            if (_profileMeta == null)
                return;
            _profileMeta.text = text ?? string.Empty;
            SetDisplay(_profileMeta, string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex);
        }

        private static string BuildProfileGlyph(string profileName)
        {
            if (string.IsNullOrEmpty(profileName))
                return "?";

            for (int i = 0; i < profileName.Length; i++)
            {
                if (char.IsLetterOrDigit(profileName[i]))
                    return char.ToUpperInvariant(profileName[i]).ToString();
            }
            return "?";
        }

        // Deterministic per-name hue so a profile keeps the same colour between sessions.
        // string.GetHashCode() is not stable across runs, hence the explicit hash.
        private static readonly Color[] ProfileChipPalette =
        {
            new Color(0.98f, 0.55f, 0.24f), // orange
            new Color(0.45f, 0.62f, 0.98f), // blue
            new Color(0.70f, 0.52f, 0.98f), // violet
            new Color(0.36f, 0.82f, 0.62f), // green
            new Color(0.98f, 0.44f, 0.58f), // pink
            new Color(0.40f, 0.80f, 0.92f), // cyan
            new Color(0.93f, 0.78f, 0.36f), // amber
            new Color(0.62f, 0.72f, 0.44f)  // olive
        };

        private static Color ProfileChipColor(string profileName)
        {
            if (string.IsNullOrEmpty(profileName))
                return ProfileChipPalette[0];

            int hash = 23;
            for (int i = 0; i < profileName.Length; i++)
                hash = unchecked(hash * 31 + profileName[i]);

            int index = (hash & 0x7fffffff) % ProfileChipPalette.Length;
            return ProfileChipPalette[index];
        }

        // ── Drag-to-scroll ──────────────────────────────────────────
        // Chips are plain VisualElements, not Buttons: the strip captures the pointer on
        // press so a drag scrolls instead of clicking, and only a press that never moved
        // past the threshold counts as a selection.

        private const float StripDragThreshold = 4f;

        private void OnProfileStripPointerDown(PointerDownEvent evt)
        {
            if (evt.button != 0 || _profileStrip == null || _profileSwitchBusy)
                return;

            _stripPointerId = evt.pointerId;
            _stripPointerStartX = evt.position.x;
            _stripStartOffsetX = _profileStrip.scrollOffset.x;
            _stripDragged = false;
            _stripPressedProfile = ProfileNameFromTarget(evt.target as VisualElement);

            _profileStrip.CapturePointer(evt.pointerId);
        }

        private void OnProfileStripPointerMove(PointerMoveEvent evt)
        {
            if (_profileStrip == null || evt.pointerId != _stripPointerId)
                return;
            if (!_profileStrip.HasPointerCapture(evt.pointerId))
                return;

            float delta = evt.position.x - _stripPointerStartX;
            if (!_stripDragged && Mathf.Abs(delta) >= StripDragThreshold)
                _stripDragged = true;
            if (!_stripDragged)
                return;

            Vector2 offset = _profileStrip.scrollOffset;
            offset.x = Mathf.Clamp(_stripStartOffsetX - delta, 0f, MaxStripScrollX());
            _profileStrip.scrollOffset = offset;
            evt.StopPropagation();
        }

        private void OnProfileStripPointerUp(PointerUpEvent evt)
        {
            if (_profileStrip == null || evt.pointerId != _stripPointerId)
                return;

            bool wasDrag = _stripDragged;
            string pressed = _stripPressedProfile;

            if (_profileStrip.HasPointerCapture(evt.pointerId))
                _profileStrip.ReleasePointer(evt.pointerId);
            ResetStripDrag();

            if (wasDrag)
            {
                // Enter/leave never reached the chips while the strip held the capture, so
                // the hover caption is stale by now — fall back to the active profile.
                OnProfileChipHover(null);
                return;
            }

            if (string.IsNullOrEmpty(pressed) || _profileSwitchBusy)
                return;

            if (IsActiveProfile(pressed))
                return;

            MarkProfilePending(pressed);
            _ = SwitchHermesProfileAsync(pressed);
        }

        private void OnProfileStripPointerCaptureOut(PointerCaptureOutEvent evt)
        {
            ResetStripDrag();
        }

        private void OnProfileStripWheel(WheelEvent evt)
        {
            if (_profileStrip == null)
                return;

            float max = MaxStripScrollX();
            if (max <= 0f)
                return;

            Vector2 offset = _profileStrip.scrollOffset;
            offset.x = Mathf.Clamp(offset.x + evt.delta.y * 18f, 0f, max);
            _profileStrip.scrollOffset = offset;
            evt.StopPropagation();
        }

        private void ResetStripDrag()
        {
            _stripPointerId = -1;
            _stripDragged = false;
            _stripPressedProfile = null;
        }

        private float MaxStripScrollX()
        {
            if (_profileStrip == null)
                return 0f;
            float content = _profileStrip.contentContainer.layout.width;
            float viewport = _profileStrip.contentViewport.layout.width;
            if (float.IsNaN(content) || float.IsNaN(viewport))
                return 0f;
            return Mathf.Max(0f, content - viewport);
        }

        private string ProfileNameFromTarget(VisualElement target)
        {
            VisualElement el = target;
            while (el != null && el != _profileStrip)
            {
                if (el.ClassListContains("rail__profile-chip"))
                    return el.userData as string;
                el = el.parent;
            }
            return null;
        }

        private async Task SwitchHermesProfileAsync(string profileName)
        {
            _profileSwitchBusy = true;
            // The strip greys out while the switch is in flight — dropping the chat context
            // twice in a row because of an impatient second click is not recoverable.
            if (_profileStrip != null)
                _profileStrip.SetEnabled(false);
            try
            {
                var chat = await _d.GetChatServiceAsync();
                if (chat == null || !_d.IsBound())
                    return;

                SetProfileStatus(LocalizationExtensions.GetFormat(
                    "providers.profile.switching_to", "Переключаем на {0}…", profileName));

                await chat.SwitchHermesProfileAsync(ProfileSwitchTarget(profileName));
                if (!_d.IsBound())
                    return;

                // Full context switch: the chat that belonged to the old profile was dropped
                // locally (never deleted, never recreated) — fall back to the empty/new-chat
                // state and list only the selected profile's sessions.
                if (_d.SetCurrentSessionId != null)
                    _d.SetCurrentSessionId(chat.CurrentSessionId ?? string.Empty);
                if (_d.SetCurrentSessionTitle != null)
                    _d.SetCurrentSessionTitle(string.Empty);
                if (_d.RenderMessages != null)
                    _d.RenderMessages();
                if (_d.LoadSessionsAsync != null)
                    await _d.LoadSessionsAsync();
                if (!_d.IsBound())
                    return;

                SyncProfileSelection();
            }
            catch (Exception ex)
            {
                if (_d.IsBound())
                {
                    // Undo the optimistic chip so the strip stops claiming a profile we are not on.
                    SyncProfileSelection();
                    SetProfileStatus(LocalizationExtensions.Get(
                        "providers.profile.switch_failed", "Не удалось переключить профиль."));
                }
                NeonLogger.LogError(ex.ToString());
            }
            finally
            {
                _profileSwitchBusy = false;
                if (_profileStrip != null)
                    _profileStrip.SetEnabled(true);
            }
        }

        // ============================================================
        // CanLeave / unsaved-changes guard
        // ============================================================

        public bool CanLeaveProviderEditor()
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

        private bool HasUnsavedChanges()
        {
            if (_d.ProviderEditPanel?.style.display != DisplayStyle.Flex) return false;
            if (_editingProvider == null) return false;

            if (_editingProviderSource == null) return true;

            var draft = BuildProviderDraftFromEditor();
            if (draft == null) return false;

            return !SameText(draft.displayName, _editingProviderSource.displayName)
                || !SameText(draft.baseUrl,      _editingProviderSource.baseUrl)
                || !SameText(draft.apiKey,        _editingProviderSource.apiKey)
                || !SameText(draft.defaultModel,  _editingProviderSource.defaultModel)
                || Math.Abs(draft.temperature - _editingProviderSource.temperature) > 0.001f
                || draft.maxTokens != _editingProviderSource.maxTokens
                || !SameText(draft.authMode,     _editingProviderSource.authMode)
                || !SameText(draft.authProvider, _editingProviderSource.authProvider)
                || !SameText(draft.authUsername, _editingProviderSource.authUsername);
        }

        private static bool SameText(string left, string right)
        {
            return string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal);
        }

        private ProviderConfig BuildProviderDraftFromEditor()
        {
            if (_editingProvider == null) return null;

            var draft = CloneProvider(_editingProvider);
            if (_d.EditName != null)        draft.displayName  = _d.EditName.value;
            if (_d.EditBaseUrl != null)     draft.baseUrl      = _d.EditBaseUrl.value;
            if (_d.EditApiKey != null)      draft.apiKey       = _d.EditApiKey.value;
            draft.defaultModel = GetCurrentModelValue();
            if (_d.EditTemperature != null) draft.temperature  = _d.EditTemperature.value;
            if (_d.EditMaxTokens != null && int.TryParse(_d.EditMaxTokens.value, out int tokens))
                draft.maxTokens = tokens;

            if (!ChatService.IsHermesProvider(draft))
            {
                EnsureVoiceEditorFields();
                if (_editSttProvider != null) draft.sttProvider = MapSttLabelToCode(_editSttProvider.value);
                if (_editTtsProvider != null) draft.ttsProvider = MapTtsLabelToCode(_editTtsProvider.value);
                if (_editTtsVoice != null)    draft.ttsVoice    = _editTtsVoice.value;
                if (_editTtsSpeed != null)    draft.ttsSpeed    = _editTtsSpeed.value;
                if (_editSttLanguage != null) draft.sttLanguage = _editSttLanguage.value;
            }
            else
            {
                // Hermes: advanced token field is the API-key / Bearer source when present.
                // authMode / authProvider are set by Connect (probe), not by primary form fields.
                EnsureGatewayEditorSection();
                if (_gatewayAdvancedToken != null
                    && !string.IsNullOrWhiteSpace(_gatewayAdvancedToken.value))
                {
                    draft.apiKey = _gatewayAdvancedToken.value.Trim();
                }
                if (!string.IsNullOrEmpty(_probedAuthProvider))
                    draft.authProvider = _probedAuthProvider;
            }

            // Backend type is fixed when the provider editor opens/creates the draft.
            // Saving must never migrate a provider between Hermes and OpenAI just because
            // the active chat backend or filter dropdown changed while editing.

            return draft;
        }

        private void EnsureVoiceEditorFields()
        {
            if (_voiceFieldsQueried) return;
            _voiceFieldsQueried = true;
            if (_d.ProviderEditPanel == null) return;
            _editVoiceSection = _d.ProviderEditPanel.Q<VisualElement>("edit-voice-section");
            _editSttProvider  = _d.ProviderEditPanel.Q<NeonDropdown>("edit-stt-provider");
            _editTtsProvider  = _d.ProviderEditPanel.Q<NeonDropdown>("edit-tts-provider");
            _editTtsVoice     = _d.ProviderEditPanel.Q<TextField>("edit-tts-voice");
            _editTtsSpeed     = _d.ProviderEditPanel.Q<Slider>("edit-tts-speed");
            _editSttLanguage  = _d.ProviderEditPanel.Q<TextField>("edit-stt-language");
        }

        private void SyncVoiceFieldsToUi(ProviderConfig provider)
        {
            EnsureVoiceEditorFields();

            if (_editSttProvider != null)
                _editSttProvider.SetValueWithoutNotify(MapSttCodeToLabel(provider.sttProvider));

            if (_editTtsProvider != null)
                _editTtsProvider.SetValueWithoutNotify(MapTtsCodeToLabel(provider.ttsProvider));

            if (_editTtsVoice != null)
                _editTtsVoice.SetValueWithoutNotify(provider.ttsVoice ?? string.Empty);

            if (_editTtsSpeed != null)
                _editTtsSpeed.SetValueWithoutNotify(provider.ttsSpeed > 0f ? provider.ttsSpeed : 1f);

            if (_editSttLanguage != null)
                _editSttLanguage.SetValueWithoutNotify(provider.sttLanguage ?? string.Empty);
        }

        // ============================================================
        // Remote Hermes gateway (Desktop-style URL + Connect)
        // ============================================================
        // Primary path: Gateway URL + Connect / Sign in + status + Sign out.
        // Gated gateways always open the browser login window (CDP cookie capture);
        // username/password and cookie paste are never Companion UI.
        // Token/Bearer lives under Advanced only (open / auth_required=false gateways).

        private void EnsureGatewayEditorSection()
        {
            if (_gatewayFieldsBuilt)
                return;

            // Locate the Base URL / API key field roots in the UXML editor.
            _apiKeyFieldRoot = _d.EditApiKey != null && _d.EditApiKey.parent != null
                ? _d.EditApiKey.parent.parent
                : null;
            var baseUrlField = _d.EditBaseUrl != null ? _d.EditBaseUrl.parent : null;
            var content = baseUrlField != null ? baseUrlField.parent : null;
            if (content == null || _apiKeyFieldRoot == null)
                return;

            if (baseUrlField != null)
            {
                _gatewayBaseUrlLabel = baseUrlField.Q<Label>(className: "label");
                if (_gatewayBaseUrlLabel == null)
                {
                    foreach (var child in baseUrlField.Children())
                    {
                        var lbl = child as Label;
                        if (lbl != null)
                        {
                            _gatewayBaseUrlLabel = lbl;
                            break;
                        }
                    }
                }
            }

            if (_apiKeyFieldRoot != null)
            {
                _apiKeyFieldLabel = _apiKeyFieldRoot.Q<Label>(className: "label");
                if (_apiKeyFieldLabel == null)
                {
                    foreach (var child in _apiKeyFieldRoot.Children())
                    {
                        var lbl = child as Label;
                        if (lbl != null)
                        {
                            _apiKeyFieldLabel = lbl;
                            break;
                        }
                    }
                }
                if (_apiKeyFieldLabel != null)
                    _apiKeyLabelOpenAi = _apiKeyFieldLabel.text;
            }

            // Field roots that are meaningless over the Hermes WS transport (see
            // ApplyHermesGatewayLayout). Resolved here because the UXML shape is stable.
            _modelFieldRoot = _d.EditModelPreset != null ? _d.EditModelPreset.parent : null;
            _samplingRowRoot = _d.EditTemperature != null && _d.EditTemperature.parent != null
                ? _d.EditTemperature.parent.parent
                : null;

            _gatewayFieldsBuilt = true;
            _gatewaySection = new VisualElement();
            _gatewaySection.name = "edit-gateway-section";

            // Status
            _gatewayStatusRow = new VisualElement();
            _gatewayStatusRow.AddToClassList("testrow");
            _gatewayStatus = new Label(string.Empty);
            _gatewayStatus.AddToClassList("testrow__label");
            _gatewayStatusRow.Add(_gatewayStatus);
            _gatewaySection.Add(_gatewayStatusRow);

            // Connect + Sign out
            var actions = new VisualElement();
            actions.AddToClassList("provider-edit-actions");
            _gatewayConnectBtn = new Button(() => { if (!_gatewayBusy) _ = OnGatewayConnectClickedAsync(); });
            _gatewayConnectBtn.AddToClassList("btn");
            _gatewayConnectBtn.AddToClassList("btn--primary");
            _gatewaySignOutBtn = new Button(() => { if (!_gatewayBusy) _ = OnGatewaySignOutClickedAsync(); });
            _gatewaySignOutBtn.AddToClassList("btn");
            _gatewaySignOutBtn.AddToClassList("btn--ghost");
            actions.Add(_gatewayConnectBtn);
            actions.Add(_gatewaySignOutBtn);
            _gatewaySection.Add(actions);

            // Desktop parity: username/password and cookie paste are NOT Companion UI.
            // Password providers render their form on the gateway /login page inside the
            // automatic browser window (openOauthLoginWindow equivalent). Advanced holds
            // only legacy Bearer token for open (auth_required=false) gateways.
            _gatewayAdvancedToggle = new Button(() => ToggleGatewayAdvanced())
            {
                text = LocalizationExtensions.Get("providers.gateway.advanced.show", "Advanced / Token mode")
            };
            _gatewayAdvancedToggle.AddToClassList("btn");
            _gatewayAdvancedToggle.AddToClassList("btn--ghost");
            _gatewaySection.Add(_gatewayAdvancedToggle);

            _gatewayAdvancedWrap = new VisualElement();
            _gatewayAdvancedWrap.name = "edit-gateway-advanced";
            _gatewayAdvancedWrap.style.display = DisplayStyle.None;

            var advHint = new Label(LocalizationExtensions.Get(
                "providers.gateway.advanced.hint",
                "Legacy Bearer token for open (auth_required=false) gateways only. OAuth/password gateways use automatic browser sign-in."));
            advHint.AddToClassList("label");
            _gatewayAdvancedWrap.Add(advHint);

            _gatewayAdvancedToken = BuildGatewayField(
                _gatewayAdvancedWrap,
                LocalizationExtensions.Get("providers.gateway.token", "Bearer token (legacy)"),
                true, true);

            _gatewaySection.Add(_gatewayAdvancedWrap);

            // Insert after the base URL field (primary path stays URL then Connect).
            int idx = content.IndexOf(baseUrlField);
            if (idx >= 0)
                content.Insert(idx + 1, _gatewaySection);
            else
                content.Add(_gatewaySection);

            RefreshGatewayActionLabels();
            HookGatewayAuthEvents();
        }

        private static TextField BuildGatewayField(VisualElement parent, string labelText, bool password, bool mono)
        {
            var field = new VisualElement();
            field.AddToClassList("field");
            var label = new Label(labelText);
            label.AddToClassList("label");
            field.Add(label);
            var tf = new TextField();
            tf.AddToClassList("input");
            if (mono)
                tf.AddToClassList("input--mono");
            tf.isPasswordField = password;
            field.Add(tf);
            parent.Add(field);
            return tf;
        }

        private void ApplyHermesGatewayLayout(bool isHermes)
        {
            if (!_gatewayFieldsBuilt)
                return;

            SetDisplay(_gatewaySection, isHermes ? DisplayStyle.Flex : DisplayStyle.None);

            // Hermes primary path hides the API-key field; token lives under Advanced.
            SetDisplay(_apiKeyFieldRoot, isHermes ? DisplayStyle.None : DisplayStyle.Flex);

            // Default model, temperature and max-tokens are dead over Hermes: HermesSessionManager
            // sends none of them — the gateway profile owns the model and the sampling. Showing
            // them invited people to configure settings that do nothing.
            SetDisplay(_modelFieldRoot, isHermes ? DisplayStyle.None : DisplayStyle.Flex);
            SetDisplay(_samplingRowRoot, isHermes ? DisplayStyle.None : DisplayStyle.Flex);
            // Only force it shut for Hermes — for OpenAI the preset logic owns this wrap.
            if (isHermes)
                SetDisplay(_d.EditModelCustomWrap, DisplayStyle.None);

            if (_gatewayBaseUrlLabel != null)
            {
                _gatewayBaseUrlLabel.text = isHermes
                    ? LocalizationExtensions.Get("providers.gateway.url", "Gateway URL")
                    : LocalizationExtensions.Get("providers.field.base_url", "Base URL");
            }

            if (!isHermes && _apiKeyFieldLabel != null && !string.IsNullOrEmpty(_apiKeyLabelOpenAi))
                _apiKeyFieldLabel.text = _apiKeyLabelOpenAi;
        }

        private void SyncGatewayFieldsToUi(ProviderConfig provider)
        {
            if (!_gatewayFieldsBuilt)
                return;

            _probedAuthProvider = provider != null ? provider.authProvider : null;

            if (_gatewayAdvancedToken != null)
            {
                _gatewayAdvancedToken.SetValueWithoutNotify(
                    provider != null ? (provider.apiKey ?? string.Empty) : string.Empty);
            }

            RefreshGatewayActionLabels();
            RefreshGatewayStatus();
        }

        private void ToggleGatewayAdvanced()
        {
            _gatewayAdvancedOpen = !_gatewayAdvancedOpen;
            SetDisplay(_gatewayAdvancedWrap, _gatewayAdvancedOpen ? DisplayStyle.Flex : DisplayStyle.None);
            if (_gatewayAdvancedToggle != null)
            {
                _gatewayAdvancedToggle.text = _gatewayAdvancedOpen
                    ? LocalizationExtensions.Get("providers.gateway.advanced.hide", "Hide advanced")
                    : LocalizationExtensions.Get("providers.gateway.advanced.show", "Advanced / Token mode");
            }
        }

        private void RefreshGatewayActionLabels()
        {
            if (_gatewayConnectBtn != null)
            {
                _gatewayConnectBtn.text = LocalizationExtensions.Get(
                    "providers.gateway.connect", "Connect / Sign in");
            }
            if (_gatewaySignOutBtn != null)
            {
                _gatewaySignOutBtn.text = LocalizationExtensions.Get(
                    "providers.gateway.sign_out", "Sign out");
            }
        }

        private void HookGatewayAuthEvents()
        {
            if (_gatewayEventsHooked)
                return;
            var selector = GlobalBackendSelector.Instance;
            if (selector == null)
                return;
            selector.OnConnectionStateChanged += OnGatewayConnectionStateChanged;
            selector.OnError += OnGatewaySelectorError;
            _gatewayEventsHooked = true;
        }

        private void UnhookGatewayAuthEvents()
        {
            if (!_gatewayEventsHooked)
                return;
            var selector = GlobalBackendSelector.Instance;
            if (selector != null)
            {
                selector.OnConnectionStateChanged -= OnGatewayConnectionStateChanged;
                selector.OnError -= OnGatewaySelectorError;
            }
            _gatewayEventsHooked = false;
        }

        /// <summary>
        /// The transport reaching Connected is the only signal that profile-scoped REST calls
        /// and the session list will actually answer. Startup connects in the background, so
        /// without reloading here the rail stays empty until the user pokes something.
        /// </summary>
        private void OnGatewayConnectionStateChanged(TransportState state)
        {
            RefreshGatewayStatus();

            if (state != TransportState.Connected)
            {
                _lastTransportState = state;
                return;
            }
            if (_d.IsBound == null || !_d.IsBound())
                return;

            if (NoteTransportConnected())
                RefreshHermesProfileSelector(true);
        }

        /// <summary>
        /// Record that the transport is up and, on the rising edge, reload the session list.
        /// Called both from the state event and from <see cref="RefreshHermesProfileSelector"/>,
        /// because a startup connect can finish before the UI ever subscribes — in that case no
        /// event is coming and the rail would sit empty forever. Returns true on the edge only.
        /// </summary>
        private bool NoteTransportConnected()
        {
            if (_lastTransportState == TransportState.Connected)
                return false;

            _lastTransportState = TransportState.Connected;
            // A profile switch reconnects the socket and reloads the list itself, once the new
            // scope has settled — loading here too would race it with the old scope's request.
            if (_d.LoadSessionsAsync != null && !_profileSwitchBusy)
                _ = _d.LoadSessionsAsync();
            return true;
        }

        private void OnGatewaySelectorError(string message)
        {
            RefreshGatewayStatus();
        }

        private void RefreshGatewayStatus()
        {
            if (_gatewayStatus == null)
                return;

            var selector = GlobalBackendSelector.Instance;
            HermesAuthState state = selector != null ? selector.RemoteAuthState : HermesAuthState.NoSession;
            string reason = selector != null ? selector.RemoteAuthError : null;
            string lastError = selector != null ? selector.LastConnectionError : null;
            bool connected = selector != null
                && selector.SessionManager != null
                && selector.SessionManager.IsConnected;

            bool isOAuth = _editingProvider != null
                && string.Equals(_editingProvider.authMode, "oauth", StringComparison.OrdinalIgnoreCase);

            string text;
            bool errorStyle = false;
            bool okStyle = false;

            if (_gatewayBusy)
            {
                text = LocalizationExtensions.Get("providers.gateway.status.busy", "Connecting…");
            }
            else if (isOAuth && state == HermesAuthState.Authenticated)
            {
                okStyle = true;
                text = connected
                    ? LocalizationExtensions.Get("providers.gateway.status.connected", "Signed in · connected")
                    : LocalizationExtensions.Get("providers.gateway.status.signed_in", "Signed in");
            }
            else if (isOAuth && state == HermesAuthState.ReauthRequired)
            {
                errorStyle = true;
                text = LocalizationExtensions.GetFormat(
                    "providers.gateway.status.needs_signin",
                    "Needs sign-in ({0})",
                    ReasonText(reason));
                if (!string.IsNullOrEmpty(lastError)
                    && lastError.IndexOf("sign-in", StringComparison.OrdinalIgnoreCase) >= 0)
                    text = lastError;
            }
            else if (!isOAuth && connected)
            {
                okStyle = true;
                text = LocalizationExtensions.Get(
                    "providers.gateway.status.token_connected", "Connected (token mode)");
            }
            else if (!string.IsNullOrEmpty(lastError))
            {
                errorStyle = true;
                text = lastError;
            }
            else if (isOAuth)
            {
                text = LocalizationExtensions.Get(
                    "providers.gateway.status.none", "Not signed in. Enter the Gateway URL and Connect.");
            }
            else
            {
                text = LocalizationExtensions.Get(
                    "providers.gateway.status.idle", "Enter the Gateway URL and Connect.");
            }

            _gatewayStatus.text = text;
            if (_gatewayStatusRow != null)
            {
                _gatewayStatusRow.EnableInClassList("testrow--ok", !_gatewayBusy && okStyle);
                _gatewayStatusRow.EnableInClassList("testrow--error", !_gatewayBusy && errorStyle);
            }
        }

        private static string ReasonText(string reason)
        {
            if (string.Equals(reason, "no_cookie", StringComparison.Ordinal))
                return LocalizationExtensions.Get("providers.gateway.reason.no_cookie", "no session");
            if (string.Equals(reason, "expired", StringComparison.Ordinal))
                return LocalizationExtensions.Get("providers.gateway.reason.expired", "session expired");
            if (string.Equals(reason, "invalid_credentials", StringComparison.Ordinal))
                return LocalizationExtensions.Get("providers.gateway.reason.invalid", "invalid credentials");
            return reason ?? string.Empty;
        }

        private void SetGatewayActionsEnabled(bool enabled)
        {
            if (_gatewayConnectBtn != null) _gatewayConnectBtn.SetEnabled(enabled);
            if (_gatewaySignOutBtn != null) _gatewaySignOutBtn.SetEnabled(enabled);
        }

        private void SetGatewayStatusMessage(string text, bool error)
        {
            if (_gatewayStatus != null)
                _gatewayStatus.text = text ?? string.Empty;
            if (_gatewayStatusRow != null)
            {
                // Temporary messages during connect are neutral (neither ok nor error) unless
                // explicitly marked as an error; final success/failure is painted by RefreshGatewayStatus.
                _gatewayStatusRow.EnableInClassList("testrow--ok", false);
                _gatewayStatusRow.EnableInClassList("testrow--error", error);
            }
        }

        /// <summary>
        /// Persist draft, activate as Hermes provider, return it. Shared by Connect paths.
        /// </summary>
        private async Task<ProviderConfig> PrepareActiveGatewayProviderAsync(string authMode)
        {
            var app = await _d.GetAppAsync();
            if (app == null)
                return null;

            var draft = BuildProviderDraftFromEditor();
            if (draft == null || !ChatService.IsHermesProvider(draft))
                return null;

            draft.authMode = authMode; // "oauth" or null (token)
            if (string.Equals(authMode, "oauth", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(_probedAuthProvider))
            {
                draft.authProvider = _probedAuthProvider;
            }

            await app.ProviderManager.SaveProviderAsync(draft);
            _editingProviderSource = draft;
            _editingProvider = CloneProvider(draft);

            var selector = GlobalBackendSelector.Instance;
            if (selector != null && selector.CurrentMode != BackendMode.Hermes)
                await selector.SetMode(BackendMode.Hermes);
            SyncGlobalBackendModeUi(BackendMode.Hermes);

            var chat = await _d.GetChatServiceAsync();
            if (chat != null)
                chat.SetActiveProviderWithoutSession(draft);

            return draft;
        }

        private async Task OnGatewayConnectClickedAsync()
        {
            if (_editingProvider == null)
                return;

            string baseUrl = _d.EditBaseUrl != null ? (_d.EditBaseUrl.value ?? string.Empty).Trim() : string.Empty;
            if (string.IsNullOrEmpty(baseUrl))
            {
                SetGatewayStatusMessage(
                    LocalizationExtensions.Get(
                        "providers.gateway.url.required",
                        "Enter the Remote Hermes Gateway URL first."),
                    true);
                return;
            }

            _gatewayBusy = true;
            SetGatewayActionsEnabled(false);
            RefreshGatewayStatus();

            try
            {
                // 1) Probe — same public endpoints Desktop uses (no credentials).
                SetGatewayStatusMessage(
                    LocalizationExtensions.Get("providers.gateway.status.probing", "Checking gateway…"),
                    false);
                HermesAuthProbeResult probe = await HermesRemoteAuth.ProbeAsync(baseUrl);
                _lastProbe = probe;

                if (!probe.Reachable)
                {
                    SetGatewayStatusMessage(
                        LocalizationExtensions.GetFormat(
                            "providers.gateway.status.unreachable",
                            "Could not reach gateway: {0}",
                            probe.Error ?? "unreachable"),
                        true);
                    return;
                }

                bool oauth = string.Equals(probe.AuthMode, "oauth", StringComparison.OrdinalIgnoreCase);

                if (oauth)
                {
                    await ConnectOAuthGatewayAsync(probe, baseUrl);
                }
                else
                {
                    await ConnectTokenGatewayAsync();
                }
            }
            catch (Exception ex)
            {
                NeonLogger.LogError("[Providers] Gateway connect failed: " + ex.GetType().Name + ": " + ex.Message);
                SetGatewayStatusMessage(ex.Message, true);
            }
            finally
            {
                _gatewayBusy = false;
                SetGatewayActionsEnabled(true);
                RefreshGatewayStatus();
                await RefreshProvidersListAsync();
            }
        }

        private async Task ConnectOAuthGatewayAsync(HermesAuthProbeResult probe, string baseUrl)
        {
            // Desktop parity (gateway-settings.tsx signIn + openOauthLoginWindow):
            // ALL gated gateways — password and OAuth IDP — complete login in the browser
            // window on {base}/login. Password form lives on the gateway page (POST
            // /auth/password-login); Companion never collects username/password itself.
            string providerName = probe != null ? probe.FirstPasswordProviderName : null;
            if (string.IsNullOrEmpty(providerName) && probe != null && probe.IsPasswordProvider)
                providerName = "basic";
            if (!string.IsNullOrEmpty(providerName))
                _probedAuthProvider = providerName;

            var draft = await PrepareActiveGatewayProviderAsync("oauth");
            if (draft == null)
                return;

            var selector = GlobalBackendSelector.Instance;
            if (selector == null)
                return;

            selector.ConfigureHermesEndpoint(draft.baseUrl, draft.apiKey);

            // Already signed in? Just reconnect.
            if (selector.HasRemoteSession)
            {
                await selector.ReconnectHermes();
                return;
            }

            // Automatic session capture: dedicated Chromium/Edge profile + CDP cookie poll
            // (HermesBrowserOAuthLogin), then ws-ticket mint via ReconnectHermes.
            SetGatewayStatusMessage(
                LocalizationExtensions.Get(
                    "providers.gateway.browser.waiting",
                    "Complete sign-in in the browser window…"),
                false);

            bool ok = await selector.HermesBrowserLoginAsync(baseUrl);
            if (ok)
            {
                var chat = _d.GetChatServiceSync != null ? _d.GetChatServiceSync() : null;
                SetProviderHeader(
                    chat != null && chat.CurrentProvider != null ? chat.CurrentProvider : draft,
                    chat != null ? chat.CurrentSessionModel : null);
                SetGatewayStatusMessage(
                    LocalizationExtensions.Get(
                        "providers.gateway.status.connected",
                        "Signed in · connected"),
                    false);
                return;
            }

            string err = selector.LastConnectionError;
            if (string.IsNullOrEmpty(err))
            {
                err = LocalizationExtensions.Get(
                    "providers.gateway.browser.failed",
                    "Browser sign-in did not complete. Try Connect again.");
            }
            SetGatewayStatusMessage(err, true);
        }

        private async Task ConnectTokenGatewayAsync()
        {
            // Token / open gateway: use advanced Bearer token (or saved apiKey).
            string token = null;
            if (_gatewayAdvancedToken != null && !string.IsNullOrWhiteSpace(_gatewayAdvancedToken.value))
                token = _gatewayAdvancedToken.value.Trim();
            else if (_d.EditApiKey != null && !string.IsNullOrWhiteSpace(_d.EditApiKey.value))
                token = _d.EditApiKey.value.Trim();
            else if (_editingProvider != null)
                token = _editingProvider.apiKey;

            if (string.IsNullOrEmpty(token))
            {
                if (!_gatewayAdvancedOpen)
                    ToggleGatewayAdvanced();
                SetGatewayStatusMessage(
                    LocalizationExtensions.Get(
                        "providers.gateway.token.required",
                        "This gateway uses token mode. Enter a Bearer token under Advanced, then Connect."),
                    true);
                return;
            }

            // Mirror into the draft apiKey and clear oauth mode.
            if (_d.EditApiKey != null)
                _d.EditApiKey.SetValueWithoutNotify(token);
            if (_gatewayAdvancedToken != null)
                _gatewayAdvancedToken.SetValueWithoutNotify(token);

            var draft = await PrepareActiveGatewayProviderAsync(null);
            if (draft == null)
                return;

            draft.apiKey = token;
            draft.authMode = null;
            var app = await _d.GetAppAsync();
            if (app != null)
                await app.ProviderManager.SaveProviderAsync(draft);
            _editingProviderSource = draft;
            _editingProvider = CloneProvider(draft);

            var selector = GlobalBackendSelector.Instance;
            if (selector == null)
                return;

            if (selector.HasRemoteSession)
                await selector.ClearHermesRemoteSession();

            selector.ConfigureHermesEndpoint(draft.baseUrl, draft.apiKey);
            await selector.ReconnectHermes();

            var chat = _d.GetChatServiceSync != null ? _d.GetChatServiceSync() : null;
            SetProviderHeader(
                chat != null && chat.CurrentProvider != null ? chat.CurrentProvider : draft,
                chat != null ? chat.CurrentSessionModel : null);
        }

        private async Task OnGatewaySignOutClickedAsync()
        {
            var selector = GlobalBackendSelector.Instance;
            if (selector == null)
                return;

            _gatewayBusy = true;
            SetGatewayActionsEnabled(false);
            RefreshGatewayStatus();
            try
            {
                await selector.ClearHermesRemoteSession();
            }
            catch (Exception ex)
            {
                NeonLogger.LogError("[Providers] Gateway sign-out failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                _gatewayBusy = false;
                SetGatewayActionsEnabled(true);
                RefreshGatewayStatus();
                await RefreshProvidersListAsync();
            }
        }

        private static string MapSttCodeToLabel(string code)
        {
            if (string.IsNullOrEmpty(code)) return "OpenAI Whisper";
            if (string.Equals(code, "groq",  StringComparison.OrdinalIgnoreCase)) return "Groq Whisper";
            if (string.Equals(code, "local", StringComparison.OrdinalIgnoreCase)) return "Local (faster-whisper)";
            return "OpenAI Whisper";
        }

        private static string MapSttLabelToCode(string label)
        {
            if (string.IsNullOrEmpty(label)) return "openai";
            if (label.IndexOf("Groq",  StringComparison.OrdinalIgnoreCase) >= 0) return "groq";
            if (label.IndexOf("Local", StringComparison.OrdinalIgnoreCase) >= 0) return "local";
            return "openai";
        }

        private static string MapTtsCodeToLabel(string code)
        {
            if (string.IsNullOrEmpty(code)) return "Edge (free)";
            if (string.Equals(code, "openai",     StringComparison.OrdinalIgnoreCase)) return "OpenAI TTS";
            if (string.Equals(code, "elevenlabs", StringComparison.OrdinalIgnoreCase)) return "ElevenLabs";
            if (string.Equals(code, "minimax",    StringComparison.OrdinalIgnoreCase)) return "MiniMax";
            if (string.Equals(code, "mistral",    StringComparison.OrdinalIgnoreCase)) return "Mistral";
            return "Edge (free)";
        }

        private static string MapTtsLabelToCode(string label)
        {
            if (string.IsNullOrEmpty(label)) return "edge";
            if (label.IndexOf("OpenAI",     StringComparison.OrdinalIgnoreCase) >= 0) return "openai";
            if (label.IndexOf("ElevenLabs", StringComparison.OrdinalIgnoreCase) >= 0) return "elevenlabs";
            if (label.IndexOf("MiniMax",    StringComparison.OrdinalIgnoreCase) >= 0) return "minimax";
            if (label.IndexOf("Mistral",    StringComparison.OrdinalIgnoreCase) >= 0) return "mistral";
            return "edge";
        }

        // ============================================================
        // Static helpers
        // ============================================================

        internal static ProviderConfig CloneProvider(ProviderConfig source)
        {
            if (source == null) return null;
            return new ProviderConfig
            {
                id            = source.id,
                displayName   = source.displayName,
                baseUrl       = source.baseUrl,
                apiKey        = source.apiKey,
                defaultModel  = source.defaultModel,
                temperature   = source.temperature,
                maxTokens     = source.maxTokens,
                contextWindow = source.contextWindow,
                isEnabled     = source.isEnabled,
                backendType   = source.backendType,
                authMode      = source.authMode,
                authProvider  = source.authProvider,
                authUsername  = source.authUsername,
                sttProvider   = source.sttProvider,
                ttsProvider   = source.ttsProvider,
                ttsVoice      = source.ttsVoice,
                ttsModel      = source.ttsModel,
                ttsSpeed      = source.ttsSpeed,
                sttLanguage   = source.sttLanguage
            };
        }

        internal static string BuildProviderShort(ProviderConfig provider)
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

        private static string GetModelProviderKey(string modelId)
        {
            if (string.IsNullOrWhiteSpace(modelId))
                return "Other";

            int slashIndex = modelId.IndexOf('/');
            if (slashIndex <= 0)
                return "General";

            return modelId.Substring(0, slashIndex);
        }

        // ============================================================
        // Utility
        // ============================================================

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

        private static void SetDisplay(VisualElement element, DisplayStyle display)
        {
            if (element != null)
                element.style.display = display;
        }
    }
}
