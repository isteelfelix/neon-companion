using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Api.Hermes;
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
            if (isActive) toggle.AddToClassList("toggle--on");
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

            // Temperature / max tokens only apply to the OpenAI HTTP path. Hermes drives
            // generation server-side (session.create ignores them), so hide that row.
            var generationRow = _d.EditTemperature?.parent?.parent;
            SetDisplay(generationRow, ChatService.IsHermesProvider(_editingProvider)
                ? DisplayStyle.None
                : DisplayStyle.Flex);

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

                var chat = await _d.GetChatServiceAsync();
                if (chat?.CurrentProvider?.id == draft.id)
                {
                    bool resetRemoteSession = _editingProviderSource != null &&
                        (endpointChanged ||
                         !string.Equals(_editingProviderSource.defaultModel, draft.defaultModel, StringComparison.Ordinal));
                    await chat.ApplyProviderConfigAsync(draft, resetRemoteSession);
                    SetProviderHeader(chat.CurrentProvider, chat.CurrentSessionModel);

                    // Active Hermes provider edited — re-point the transport and reconnect so the
                    // new URL/key take effect right away (otherwise the old socket lingers).
                    if (ChatService.IsHermesProvider(draft) && endpointChanged)
                    {
                        var selector = GlobalBackendSelector.Instance;
                        if (selector != null)
                        {
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

        private void SwitchProvider(ProviderConfig provider)
        {
            _ = SwitchProviderAsync(provider);
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
                bool isActive = string.Equals(savedForMode, provider.id, StringComparison.Ordinal)
                    || string.Equals(activeChat?.CurrentProvider?.id, provider.id, StringComparison.Ordinal);
                bool activateProvider = !provider.isEnabled || !isActive;

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

                var result = await app.AiClient.TestConnectionAsync(draft);
                SetTestRow(result.Success, result.Message);

                if (result.Success && result.DiscoveredModels != null && result.DiscoveredModels.Count > 0)
                    SyncModelPresetFromDiscovery(result.DiscoveredModels, GetCurrentModelValue());
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

        private async Task TestHermesConnectionAsync(ProviderConfig draft)
        {
            if (draft == null || string.IsNullOrWhiteSpace(draft.baseUrl))
            {
                SetTestRow(false, LocalizationExtensions.Get("providers.test.base_url_required", "Укажите базовый URL Hermes backend."));
                if (_d.TriggerAvatarConfused != null) _d.TriggerAvatarConfused();
                return;
            }

            string wsUrl = GlobalBackendSelector.BuildHermesWsUrl(draft.baseUrl);
            if (!string.IsNullOrEmpty(draft.apiKey))
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

                // For Hermes WS backend, use slash.exec via gateway
                var selector = GlobalBackendSelector.Instance;
                var sessionManager = selector?.SessionManager;
                if (sessionManager != null && sessionManager.IsConnected)
                {
                    bool ok = await sessionManager.SwitchModelAsync(modelId, providerSlug);
                    if (!ok)
                    {
                        if (_modelPickerStatus != null)
                            _modelPickerStatus.text = "Не удалось переключить модель.";
                        return;
                    }
                }
                else
                {
                    // Fallback for non-WS backends
                    var chat = await _d.GetChatServiceAsync();
                    if (chat == null) return;
                    var result = await chat.SetCurrentSessionModelAsync(modelId);
                    if (!result.Success)
                    {
                        string failure = string.IsNullOrWhiteSpace(result.Message)
                            ? "Не удалось применить модель."
                            : result.Message;
                        if (_modelPickerStatus != null)
                            _modelPickerStatus.text = failure;
                        return;
                    }
                }

                if (_d.LoadSessionsAsync != null)
                    await _d.LoadSessionsAsync();
                CloseModelPicker();
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
                || draft.maxTokens != _editingProviderSource.maxTokens;
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

            // Backend type is fixed when the provider editor opens/creates the draft.
            // Saving must never migrate a provider between Hermes and OpenAI just because
            // the active chat backend or filter dropdown changed while editing.

            return draft;
        }

        // ============================================================
        // Static helpers
        // ============================================================

        internal static ProviderConfig CloneProvider(ProviderConfig source)
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
                contextWindow = source.contextWindow,
                isEnabled    = source.isEnabled,
                backendType  = source.backendType
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

        private static string GetProviderStatusText(ChatService chatService)
        {
            var provider = chatService?.CurrentProvider;
            var currentModel = chatService?.CurrentSessionModel;
            if (provider == null) return LocalizationExtensions.Get("provider.status.none", "нет провайдера");
            if (!string.IsNullOrWhiteSpace(currentModel))
                return $"{provider.displayName ?? LocalizationExtensions.Get("provider.short.default", "API")} · {currentModel}";
            if (!string.IsNullOrWhiteSpace(provider.defaultModel))
                return $"{provider.displayName ?? LocalizationExtensions.Get("provider.short.default", "API")} · {provider.defaultModel}";
            return provider.displayName ?? LocalizationExtensions.Get("provider.status.configured", "настроен");
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
