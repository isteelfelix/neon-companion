using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Localization;
using NeonCompanion.Runtime.Platform;
using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK
{
    internal sealed class CompanionWindowController
    {
        public struct Deps
        {
            public VisualElement Root;
            public Func<Task<CompanionApp>> GetAppAsync;
            public Func<CompanionApp> GetAppSync;
            public Func<string> GetActiveAvatarId;
            public Func<string, string> GetAvatarDisplayName;
            public Func<string, string> CaptureBuiltInPreview;
            public Action ShowTerminal;
            public Action ShowAvatar;
            public Action OpenAvatarSettings;
        }

        private Deps _d;
        private ICompanionWindowService _service;
        private CompanionWindowPreferences _preferences = new CompanionWindowPreferences();
        private CompanionDockStateMachine _dockState =
            new CompanionDockStateMachine(CompanionDockStates.Docked);
        private bool _loading;
        private bool _registered;
        private int _monitorIndex;

        private VisualElement _card;
        private Label _title;
        private Label _subtitle;
        private Label _status;
        private Label _emergencyHint;
        private Toggle _visibleToggle;
        private Toggle _pinnedToggle;
        private Toggle _clickThroughToggle;
        private Button _monitorButton;
        private Label _scaleLabel;
        private Slider _scaleSlider;
        private Button _showButton;
        private Button _hideButton;
        private Button _returnButton;
        private Button _detachButton;

        public bool IsAvailable => _service != null && _service.IsAvailable;
        public string DockState => _dockState.State;

        public void SetDeps(Deps deps)
        {
            _d = deps;
        }

        public void Init()
        {
            VisualElement root = _d.Root;
            if (root == null)
                return;

            _card = root.Q<VisualElement>("companion-window-card");
            _title = root.Q<Label>("companion-window-title");
            _subtitle = root.Q<Label>("companion-window-subtitle");
            _status = root.Q<Label>("companion-window-status");
            _emergencyHint = root.Q<Label>("companion-window-emergency");
            _visibleToggle = root.Q<Toggle>("companion-visible-toggle");
            _pinnedToggle = root.Q<Toggle>("companion-pinned-toggle");
            _clickThroughToggle = root.Q<Toggle>("companion-click-through-toggle");
            _monitorButton = root.Q<Button>("companion-monitor-button");
            _scaleLabel = root.Q<Label>("companion-scale-label");
            _scaleSlider = root.Q<Slider>("companion-scale-slider");
            _showButton = root.Q<Button>("companion-show-button");
            _hideButton = root.Q<Button>("companion-hide-button");
            _returnButton = root.Q<Button>("companion-return-button");
            _detachButton = root.Q<Button>("avatar-detach-button");

            Localize();
            SetDisplay(_card, DisplayStyle.None);
            SetDisplay(_detachButton, DisplayStyle.None);
            _ = LoadAsync();
        }

        public void RegisterCallbacks()
        {
            _registered = true;
            if (_service != null)
            {
                _service.EventReceived -= OnServiceEvent;
                _service.EventReceived += OnServiceEvent;
            }
            if (_visibleToggle != null)
                _visibleToggle.RegisterValueChangedCallback(OnVisibleChanged);
            if (_pinnedToggle != null)
                _pinnedToggle.RegisterValueChangedCallback(OnPinnedChanged);
            if (_clickThroughToggle != null)
                _clickThroughToggle.RegisterValueChangedCallback(OnClickThroughChanged);
            if (_scaleSlider != null)
                _scaleSlider.RegisterValueChangedCallback(OnScaleChanged);
            RegisterClick(_monitorButton, OnMonitorClicked);
            RegisterClick(_showButton, OnShowClicked);
            RegisterClick(_hideButton, OnHideClicked);
            RegisterClick(_returnButton, OnReturnClicked);
            RegisterClick(_detachButton, OnDetachClicked);
        }

        public void UnregisterCallbacks()
        {
            _registered = false;
            if (_visibleToggle != null)
                _visibleToggle.UnregisterValueChangedCallback(OnVisibleChanged);
            if (_pinnedToggle != null)
                _pinnedToggle.UnregisterValueChangedCallback(OnPinnedChanged);
            if (_clickThroughToggle != null)
                _clickThroughToggle.UnregisterValueChangedCallback(OnClickThroughChanged);
            if (_scaleSlider != null)
                _scaleSlider.UnregisterValueChangedCallback(OnScaleChanged);
            UnregisterClick(_monitorButton, OnMonitorClicked);
            UnregisterClick(_showButton, OnShowClicked);
            UnregisterClick(_hideButton, OnHideClicked);
            UnregisterClick(_returnButton, OnReturnClicked);
            UnregisterClick(_detachButton, OnDetachClicked);
            if (_service != null)
                _service.EventReceived -= OnServiceEvent;
        }

        public void Tick()
        {
            _service?.Tick();
        }

        public void RefreshLocalizedUi()
        {
            Localize();
            RefreshMonitorText();
            RefreshStatus();
            _ = RefreshChildLanguageAsync();
        }

        public void Detach()
        {
            if (!IsAvailable)
                return;
            Transition(CompanionDockEvent.Detach);
            _d.ShowTerminal?.Invoke();
            _ = LaunchAsync();
        }

        public void OnAvatarChanged(string avatarId)
        {
            if (!IsAvailable)
                return;
            _ = PublishProfileAsync(avatarId);
        }

        public void OnAvatarMotionStateChanged(AvatarMotionState state)
        {
            if (!IsAvailable || !_dockState.IsDetached)
                return;

            string displayState;
            switch (state)
            {
                case AvatarMotionState.Listening:
                    displayState = CompanionDisplayStates.Listening;
                    break;
                case AvatarMotionState.Thinking:
                    displayState = CompanionDisplayStates.Thinking;
                    break;
                case AvatarMotionState.Talking:
                    displayState = CompanionDisplayStates.Speaking;
                    break;
                default:
                    displayState = CompanionDisplayStates.Idle;
                    break;
            }
            _service.SetState(displayState);
        }

        public void StopAvatarDisplay()
        {
            if (IsAvailable && _dockState.IsDetached)
            {
                _service.ClearVoicePlayback();
                _service.SetState(CompanionDisplayStates.Stop);
            }
        }

        public void StartVoicePlayback(string text)
        {
            if (IsAvailable && _dockState.IsDetached)
                _service.StartVoicePlayback(text);
        }

        public void ClearVoicePlayback()
        {
            if (IsAvailable && _dockState.IsDetached)
                _service.ClearVoicePlayback();
        }

        private async Task LoadAsync()
        {
            CompanionApp app = _d.GetAppAsync != null ? await _d.GetAppAsync() : null;
            if (app == null)
                return;

            ICompanionWindowService service;
            if (!app.Services.TryGet<ICompanionWindowService>(out service))
                return;

            _service = service;
            if (!service.IsAvailable)
                return;

            if (_registered)
            {
                _service.EventReceived -= OnServiceEvent;
                _service.EventReceived += OnServiceEvent;
            }
            SetDisplay(_card, DisplayStyle.Flex);
            SetDisplay(_detachButton, DisplayStyle.Flex);

            AppSettings settings = app.Settings.Load() ?? new AppSettings();
            string persistedState = settings.companionDockState;
            if ((string.IsNullOrWhiteSpace(persistedState) ||
                persistedState == CompanionDockStates.Docked) &&
                settings.companionModeEnabled)
                persistedState = settings.companionWindowVisible
                    ? CompanionDockStates.DetachedReady
                    : CompanionDockStates.DetachedHidden;
            _dockState = new CompanionDockStateMachine(persistedState);
            _preferences = new CompanionWindowPreferences
            {
                visible = settings.companionWindowVisible,
                pinned = settings.companionWindowPinned,
                clickThrough = settings.companionWindowClickThrough,
                monitorIndex = settings.companionWindowMonitor,
                scale = Mathf.Clamp(settings.companionWindowScale, 0.5f, 2f),
                language = settings.language,
                positionX = settings.companionWindowPositionX,
                positionY = settings.companionWindowPositionY
            };
            _monitorIndex = Mathf.Clamp(
                _preferences.monitorIndex,
                0,
                Mathf.Max(0, _service.MonitorNames.Count - 1));
            _preferences.monitorIndex = _monitorIndex;

            _loading = true;
            _visibleToggle?.SetValueWithoutNotify(_preferences.visible);
            _pinnedToggle?.SetValueWithoutNotify(_preferences.pinned);
            _clickThroughToggle?.SetValueWithoutNotify(_preferences.clickThrough);
            _scaleSlider?.SetValueWithoutNotify(_preferences.scale);
            _loading = false;
            RefreshMonitorText();
            RefreshStatus();

            if (_dockState.State == CompanionDockStates.DetachedHidden)
            {
                _d.ShowTerminal?.Invoke();
            }
            else if (_dockState.State == CompanionDockStates.DetachedReady ||
                _dockState.State == CompanionDockStates.DetachedStarting)
            {
                Transition(CompanionDockEvent.Detach);
                _d.ShowTerminal?.Invoke();
                await LaunchAsync(settings.activeAvatarId);
            }
        }

        private async Task LaunchAsync(string requestedAvatarId = null)
        {
            if (!IsAvailable)
                return;

            string avatarId = !string.IsNullOrWhiteSpace(requestedAvatarId)
                ? requestedAvatarId
                : _d.GetActiveAvatarId != null
                ? _d.GetActiveAvatarId()
                : "neon";
            CompanionDisplaySnapshot snapshot = await BuildSnapshotAsync(avatarId);
            if (snapshot == null)
                return;

            _service.Launch(snapshot, _preferences);
            RefreshStatus(LocalizationExtensions.Get(
                "companion.window.status.starting",
                "Starting Companion player…"));
        }

        private async Task PublishProfileAsync(string avatarId)
        {
            CompanionDisplaySnapshot snapshot = await BuildSnapshotAsync(avatarId);
            if (snapshot != null && _service != null)
                _service.SetProfile(snapshot);
        }

        private async Task RefreshChildLanguageAsync()
        {
            if (!IsAvailable)
                return;
            CompanionApp app = _d.GetAppAsync != null ? await _d.GetAppAsync() : null;
            if (app == null)
                return;
            AppSettings settings = app.Settings.Load() ?? new AppSettings();
            _preferences.language = settings.language;
            _service.UpdatePreferences(_preferences);
        }

        private async Task<CompanionDisplaySnapshot> BuildSnapshotAsync(string avatarId)
        {
            CompanionApp app = _d.GetAppAsync != null ? await _d.GetAppAsync() : null;
            if (app == null)
                return null;

            List<AvatarProfile> profiles = app.Avatars.GetAll();
            AvatarProfile profile = profiles.FirstOrDefault(item =>
                item != null && string.Equals(item.id, avatarId, StringComparison.Ordinal));
            string name = _d.GetAvatarDisplayName != null
                ? _d.GetAvatarDisplayName(avatarId)
                : avatarId;
            CompanionDisplaySnapshot snapshot =
                CompanionDisplaySnapshot.FromProfile(profile, avatarId, name);
            if (profile == null && _d.CaptureBuiltInPreview != null)
            {
                await Task.Yield();
                snapshot.imagePngBase64 = _d.CaptureBuiltInPreview(avatarId);
            }
            return snapshot;
        }

        private void OnVisibleChanged(ChangeEvent<bool> evt)
        {
            if (_loading || !IsAvailable)
                return;
            _preferences.visible = evt.newValue;
            if (evt.newValue)
            {
                Transition(CompanionDockEvent.Show);
                _d.ShowTerminal?.Invoke();
                _ = LaunchAsync();
            }
            else
            {
                _service.Hide();
                Transition(CompanionDockEvent.Hide);
            }
        }

        private void OnPinnedChanged(ChangeEvent<bool> evt)
        {
            if (_loading || !IsAvailable)
                return;
            _preferences.pinned = evt.newValue;
            _service.UpdatePreferences(_preferences);
            SaveSettings();
        }

        private void OnClickThroughChanged(ChangeEvent<bool> evt)
        {
            if (_loading || !IsAvailable)
                return;
            _preferences.clickThrough = evt.newValue;
            _service.UpdatePreferences(_preferences);
            SaveSettings();
        }

        private void OnScaleChanged(ChangeEvent<float> evt)
        {
            if (_loading || !IsAvailable)
                return;
            _preferences.scale = Mathf.Clamp(evt.newValue, 0.5f, 2f);
            _service.UpdatePreferences(_preferences);
            SaveSettings();
        }

        private void OnMonitorClicked(ClickEvent evt)
        {
            if (!IsAvailable || _service.MonitorNames.Count == 0)
                return;
            _monitorIndex = (_monitorIndex + 1) % _service.MonitorNames.Count;
            _preferences.monitorIndex = _monitorIndex;
            _preferences.positionX = int.MinValue;
            _preferences.positionY = int.MinValue;
            _service.UpdatePreferences(_preferences);
            RefreshMonitorText();
            SaveSettings();
        }

        private void OnShowClicked(ClickEvent evt)
        {
            if (!IsAvailable)
                return;
            _preferences.visible = true;
            _visibleToggle?.SetValueWithoutNotify(true);
            Transition(CompanionDockEvent.Show);
            _d.ShowTerminal?.Invoke();
            _ = LaunchAsync();
        }

        private void OnHideClicked(ClickEvent evt)
        {
            if (!IsAvailable)
                return;
            _preferences.visible = false;
            _visibleToggle?.SetValueWithoutNotify(false);
            _service.Hide();
            Transition(CompanionDockEvent.Hide);
        }

        private void OnReturnClicked(ClickEvent evt)
        {
            ReturnToColumn();
        }

        private void OnDetachClicked(ClickEvent evt)
        {
            Detach();
        }

        private void ReturnToColumn()
        {
            if (!IsAvailable)
                return;
            _service.ClearVoicePlayback();
            _service.SetState(CompanionDisplayStates.Stop);
            _service.Stop();
            Transition(CompanionDockEvent.ReturnToColumn);
            _d.ShowAvatar?.Invoke();
        }

        private void OnServiceEvent(CompanionWindowEvent evt)
        {
            switch (evt.Kind)
            {
                case CompanionWindowEventKind.Started:
                    Transition(CompanionDockEvent.Started);
                    RefreshStatus(LocalizationExtensions.Get(
                        "companion.window.status.running",
                        "Companion player is running."));
                    break;
                case CompanionWindowEventKind.Closed:
                    Transition(CompanionDockEvent.Closed);
                    RefreshStatus(LocalizationExtensions.Get(
                        "companion.window.status.closed",
                        "Companion player closed; chat remains active."));
                    break;
                case CompanionWindowEventKind.Failed:
                    Transition(CompanionDockEvent.Fail);
                    RefreshStatus(LocalizationExtensions.Get(
                        "companion.window.status.failed",
                        "Companion player failed to start; chat remains active."));
                    break;
                case CompanionWindowEventKind.OpenAvatarSettings:
                    _d.OpenAvatarSettings?.Invoke();
                    break;
                case CompanionWindowEventKind.ReturnToColumn:
                    ReturnToColumn();
                    break;
                case CompanionWindowEventKind.BoundsChanged:
                    _preferences.positionX = evt.X;
                    _preferences.positionY = evt.Y;
                    SaveSettings();
                    break;
                case CompanionWindowEventKind.ClickThroughChanged:
                    _preferences.clickThrough = evt.BoolValue;
                    _clickThroughToggle?.SetValueWithoutNotify(evt.BoolValue);
                    SaveSettings();
                    break;
                case CompanionWindowEventKind.VisibilityChanged:
                    _preferences.visible = evt.BoolValue;
                    _visibleToggle?.SetValueWithoutNotify(evt.BoolValue);
                    SaveSettings();
                    break;
                case CompanionWindowEventKind.PinnedChanged:
                    _preferences.pinned = evt.BoolValue;
                    _pinnedToggle?.SetValueWithoutNotify(evt.BoolValue);
                    SaveSettings();
                    break;
            }
        }

        private void SaveSettings()
        {
            CompanionApp app = _d.GetAppSync != null ? _d.GetAppSync() : null;
            if (app == null)
                return;
            AppSettings settings = app.Settings.Load() ?? new AppSettings();
            settings.companionDockState = _dockState.State;
            settings.companionModeEnabled = _dockState.IsDetached;
            settings.companionWindowVisible = _preferences.visible;
            settings.companionWindowPinned = _preferences.pinned;
            settings.companionWindowClickThrough = _preferences.clickThrough;
            settings.companionWindowMonitor = _preferences.monitorIndex;
            settings.companionWindowScale = _preferences.scale;
            settings.companionWindowPositionX = _preferences.positionX;
            settings.companionWindowPositionY = _preferences.positionY;
            app.Settings.Save(settings);
        }

        private void Transition(CompanionDockEvent dockEvent)
        {
            _dockState.Apply(dockEvent);
            _preferences.visible =
                _dockState.State == CompanionDockStates.DetachedStarting ||
                _dockState.State == CompanionDockStates.DetachedReady;
            _visibleToggle?.SetValueWithoutNotify(_preferences.visible);
            SaveSettings();
            RefreshStatus();
        }

        private void Localize()
        {
            if (_title != null)
                _title.text = LocalizationExtensions.Get("companion.window.title", "Companion window");
            if (_subtitle != null)
                _subtitle.text = LocalizationExtensions.Get(
                    "companion.window.subtitle",
                    "Render the selected avatar in an isolated Windows player.");
            if (_visibleToggle != null)
                _visibleToggle.label = LocalizationExtensions.Get("companion.window.visible", "Visible");
            if (_pinnedToggle != null)
                _pinnedToggle.label = LocalizationExtensions.Get("companion.window.pinned", "Always on top");
            if (_clickThroughToggle != null)
                _clickThroughToggle.label = LocalizationExtensions.Get(
                    "companion.window.click_through",
                    "Click-through");
            if (_showButton != null)
                _showButton.text = LocalizationExtensions.Get("companion.window.show", "Show");
            if (_scaleLabel != null)
                _scaleLabel.text = LocalizationExtensions.Get("companion.window.scale", "Scale");
            if (_hideButton != null)
                _hideButton.text = LocalizationExtensions.Get("companion.window.hide", "Hide");
            if (_returnButton != null)
                _returnButton.text = LocalizationExtensions.Get(
                    "companion.window.return",
                    "Return to column");
            if (_detachButton != null)
                _detachButton.text = LocalizationExtensions.Get(
                    "companion.window.detach",
                    "Detach");
            if (_emergencyHint != null)
                _emergencyHint.text = LocalizationExtensions.Get(
                    "companion.window.emergency",
                    "Emergency click-through release: Ctrl+Shift+F12");
        }

        private void RefreshMonitorText()
        {
            if (_monitorButton == null || !IsAvailable || _service.MonitorNames.Count == 0)
                return;
            _monitorButton.text = LocalizationExtensions.Get("companion.window.monitor", "Monitor") +
                ": " + _service.MonitorNames[_monitorIndex];
        }

        private void RefreshStatus(string message = null)
        {
            if (_status == null)
                return;
            if (!string.IsNullOrWhiteSpace(message))
                _status.text = message;
            else if (_service != null && _service.IsRunning)
                _status.text = LocalizationExtensions.Get(
                    "companion.window.status.running",
                    "Companion player is running.");
            else
                _status.text = LocalizationExtensions.Get(
                    "companion.window.status.stopped",
                    "Companion player is stopped.");
        }

        private static void RegisterClick(VisualElement element, EventCallback<ClickEvent> handler)
        {
            if (element != null)
                element.RegisterCallback(handler);
        }

        private static void UnregisterClick(VisualElement element, EventCallback<ClickEvent> handler)
        {
            if (element != null)
                element.UnregisterCallback(handler);
        }

        private static void SetDisplay(VisualElement element, DisplayStyle display)
        {
            if (element != null)
                element.style.display = display;
        }
    }
}
