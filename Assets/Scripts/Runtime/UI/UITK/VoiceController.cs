using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Chat;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Localization;
using NeonCompanion.Runtime.Voice;
using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK
{
    internal sealed class VoiceController
    {
        public struct Deps
        {
            public GameObject gameObject;
            public Button MicButton;
            public VisualElement ComposerPreviews;
            public Func<bool> IsVoiceEnabledBySettings;
            /// <summary>True if the next assistant response should be auto-voiced (always-mode or reply-in-kind).</summary>
            public Func<bool> ShouldAutoVoiceResponse;
            /// <summary>(transcribedText, wavFilePath) — sends the voice message to the chat. Returns true if accepted.</summary>
            public Func<string, string, Task<bool>> SendVoiceMessageAsync;
            public Action OnVoiceRecordingStarted;
            public Action OnVoicePlaybackStarted;
            public Action RefreshAvatarMotionState;
            /// <summary>(ttsAudioPath, durationSecs) — attach a synthesized clip to the latest assistant message.</summary>
            public Action<string, float> AttachAssistantAudio;
            public Action OnVoicePlaybackCompleted;
            public Func<Task<ChatService>> GetChatServiceAsync;
            public Func<ChatService> GetChatServiceSync;
            public Func<bool> IsBound;
            public Func<AppSettings> GetAppSettings;
        }

        private Deps _d;
        private IVoiceService _voiceService;
        private VoiceInputManager _voiceInputManager;
        private VoiceOutputManager _voiceOutputManager;
        private VoicePreviewPlayer _previewPlayer;
        private VoicePreviewPlayer _messageAudioPlayer;
        private LipsyncController _lipsyncController;
        private ChatService _providerEventsChat;
        private bool _voiceBoundToChat;
        private bool _isVoicePlaying;
        private bool _isVoiceRecording;
        private string _lastConfigHash;

        // Composer preview state
        private VisualElement _previewBar;
        private Label _previewDurationLabel;
        private Label _previewTextLabel;
        private Button _previewPlayBtn;
        private string _previewWavPath;
        private string _previewText;
        private float _previewDurationSecs;
        private bool _previewPlaying;
        private bool _previewTranscribing;
        private bool _previewTranscriptionFailed;
        private int _previewLoadingFrame;
        private IVisualElementScheduledItem _previewLoadingSchedule;
        private readonly HashSet<string> _discardedPreviewPaths = new HashSet<string>();

        public bool IsVoicePlaying => _isVoicePlaying;
        public bool IsVoiceRecording => _isVoiceRecording;

        // ============================================================
        // Lifecycle
        // ============================================================

        internal void SetDeps(Deps deps)
        {
            _d = deps;
        }

        internal void Init() { }

        internal void RegisterCallbacks() { }

        internal void UnregisterCallbacks() { }

        internal void OnDisable()
        {
            UnbindVoiceAnimationEvents();
            UnbindProviderChangeEvents();
            ChatService chat = _d.GetChatServiceSync != null ? _d.GetChatServiceSync() : null;
            if (_voiceBoundToChat && chat != null && _voiceOutputManager != null)
                _voiceOutputManager.UnbindChat(chat);
            _voiceBoundToChat = false;
        }

        // ============================================================
        // Voice pipeline
        // ============================================================

        internal async Task EnsureVoicePipelineAsync(ChatService chat)
        {
            if (chat == null)
                return;

            BindProviderChangeEvents(chat);

            if (_d.IsVoiceEnabledBySettings != null && !_d.IsVoiceEnabledBySettings())
                return;

            ProviderConfig provider = chat.CurrentProvider;
            AppSettings settings = _d.GetAppSettings != null ? _d.GetAppSettings() : new AppSettings();
            string configHash = ComputeConfigHash(provider, settings);

            if (_lastConfigHash != configHash)
            {
                ReinitializeVoiceService(provider, settings, chat);
                _lastConfigHash = configHash;
            }

            if (_voiceService == null)
            {
                IVoiceService created = VoiceServiceFactory.Create(provider, settings);
                if (created != null)
                    _voiceService = created;
                else
                    _voiceService = _d.gameObject.AddComponent<WebSpeechBridge>();
            }

            if (_voiceOutputManager == null)
            {
                // Always AddComponent — never GetComponent. ReinitializeVoiceService destroys
                // old components with Object.Destroy (deferred), so GetComponent would still
                // return the pending-destroy instance and wire a dead manager to the pipeline.
                _voiceOutputManager = _d.gameObject.AddComponent<VoiceOutputManager>();
                _voiceOutputManager.Initialize(_voiceService, _d.IsVoiceEnabledBySettings,
                    () => _voiceInputManager != null && _voiceInputManager.IsRecording,
                    _d.ShouldAutoVoiceResponse);
                _voiceOutputManager.OnResponseAudioReady += HandleResponseAudioReady;
            }

            if (_voiceInputManager == null)
            {
                _voiceInputManager = _d.gameObject.AddComponent<VoiceInputManager>();
                _voiceInputManager.Initialize(_voiceService, _d.MicButton,
                    _d.IsVoiceEnabledBySettings, CanStartVoiceRecording, _d.OnVoiceRecordingStarted);
                _voiceInputManager.OnVoiceMessage += HandleVoiceMessage;
                _voiceInputManager.OnVoicePreviewReady += HandleVoicePreviewReady;
                _voiceInputManager.OnTranscriptionFailed += HandleTranscriptionFailed;
            }

            if (_previewPlayer == null)
                _previewPlayer = _d.gameObject.AddComponent<VoicePreviewPlayer>();
            if (_messageAudioPlayer == null)
                _messageAudioPlayer = _d.gameObject.AddComponent<VoicePreviewPlayer>();

            if (_lipsyncController == null)
            {
                _lipsyncController = _d.gameObject.AddComponent<LipsyncController>();
                _lipsyncController.Initialize(_voiceOutputManager, _voiceInputManager);
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

        private void BindProviderChangeEvents(ChatService chat)
        {
            if (chat == null || ReferenceEquals(_providerEventsChat, chat))
                return;

            UnbindProviderChangeEvents();
            _providerEventsChat = chat;
            _providerEventsChat.OnCurrentProviderChanged += HandleCurrentProviderChanged;
        }

        private void UnbindProviderChangeEvents()
        {
            if (_providerEventsChat == null)
                return;

            _providerEventsChat.OnCurrentProviderChanged -= HandleCurrentProviderChanged;
            _providerEventsChat = null;
        }

        private void HandleCurrentProviderChanged(ProviderConfig provider)
        {
            ChatService chat = _d.GetChatServiceSync != null ? _d.GetChatServiceSync() : null;
            if (chat != null)
                _ = EnsureVoicePipelineAsync(chat);
        }

        private string ComputeConfigHash(ProviderConfig provider, AppSettings settings)
        {
            if (provider == null || settings == null)
                return "";
            return (provider.id ?? "") + "|" + (provider.baseUrl ?? "") + "|"
                + (provider.apiKey ?? "").GetHashCode().ToString() + "|"
                + (provider.authMode ?? "") + "|"
                + (provider.ttsVoice ?? "") + "|" + (provider.ttsModel ?? "") + "|"
                + provider.ttsSpeed.ToString() + "|" + (provider.sttLanguage ?? "") + "|"
                + (settings.hermesRestUrl ?? "") + "|" + (settings.inputDeviceName ?? "") + "|"
                + settings.outputVolume.ToString();
        }

        private void ReinitializeVoiceService(ProviderConfig provider, AppSettings settings, ChatService chat)
        {
            UnbindVoiceAnimationEvents();
            HideVoicePreview();

            if (_voiceOutputManager != null)
            {
                _voiceOutputManager.OnResponseAudioReady -= HandleResponseAudioReady;
                if (_voiceBoundToChat && chat != null)
                    _voiceOutputManager.UnbindChat(chat);
                UnityEngine.Object.Destroy(_voiceOutputManager);
                _voiceOutputManager = null;
                _voiceBoundToChat = false;
            }

            if (_voiceInputManager != null)
            {
                _voiceInputManager.OnVoiceMessage -= HandleVoiceMessage;
                _voiceInputManager.OnVoicePreviewReady -= HandleVoicePreviewReady;
                _voiceInputManager.OnTranscriptionFailed -= HandleTranscriptionFailed;
                UnityEngine.Object.Destroy(_voiceInputManager);
                _voiceInputManager = null;
            }

            if (_lipsyncController != null)
            {
                UnityEngine.Object.Destroy(_lipsyncController);
                _lipsyncController = null;
            }

            if (_voiceService != null)
            {
                var mb = _voiceService as MonoBehaviour;
                if (mb != null)
                {
                    if (mb.gameObject == _d.gameObject)
                        UnityEngine.Object.Destroy(mb);
                    else
                        UnityEngine.Object.Destroy(mb.gameObject);
                }
                _voiceService = null;
            }

            IVoiceService created = VoiceServiceFactory.Create(provider, settings);
            if (created != null)
                _voiceService = created;
            else
                _voiceService = _d.gameObject.AddComponent<WebSpeechBridge>();
        }

        internal void OnVoiceRecordingStarted()
        {
            _voiceOutputManager?.StopSpeakingAndClear();
        }

        internal bool EnqueueVoiceResponse(string text)
        {
            if (_voiceOutputManager == null || string.IsNullOrWhiteSpace(text))
                return false;

            _voiceOutputManager.EnqueueResponse(text);
            return true;
        }

        private void HandleResponseAudioReady(string path, float durationSecs)
        {
            _d.AttachAssistantAudio?.Invoke(path, durationSecs);
        }

        internal void ToggleMessageAudio(string audioPath)
        {
            if (_messageAudioPlayer == null || string.IsNullOrEmpty(audioPath))
                return;

            if (_voiceOutputManager != null && _voiceOutputManager.TogglePlayback(audioPath))
                return;

            _messageAudioPlayer.Toggle(audioPath);
        }

        internal void SeekMessageAudio(string audioPath, float normalized)
        {
            if (_messageAudioPlayer == null || string.IsNullOrEmpty(audioPath))
                return;

            if (_voiceOutputManager != null && _voiceOutputManager.SeekPlayback(audioPath, normalized))
                return;

            _messageAudioPlayer.SeekNormalized(audioPath, normalized);
        }

        internal VoicePlaybackState GetMessageAudioState(string audioPath)
        {
            if (_voiceOutputManager != null)
            {
                VoicePlaybackState outputState = _voiceOutputManager.GetPlaybackState(audioPath);
                if (outputState.IsCurrent)
                    return outputState;
            }
            if (_messageAudioPlayer == null)
                return new VoicePlaybackState();
            return _messageAudioPlayer.GetState(audioPath);
        }

        // ============================================================
        // Voice preview (composer)
        // ============================================================

        private void HandleVoicePreviewReady(string wavPath, float durationSecs)
        {
            if (string.IsNullOrEmpty(wavPath))
                return;

            if (_previewBar != null &&
                !string.IsNullOrEmpty(_previewWavPath) &&
                !string.Equals(_previewWavPath, wavPath, StringComparison.Ordinal))
            {
                // The composer supports one audio clip per message. Keep the existing preview
                // and discard any late/programmatic second recording defensively.
                _discardedPreviewPaths.Add(wavPath);
                return;
            }

            _previewWavPath = wavPath;
            _previewDurationSecs = durationSecs;
            _previewText = "";
            _previewTranscribing = true;
            _previewTranscriptionFailed = false;
            ShowVoicePreview();
            _voiceInputManager?.RefreshState();
        }

        private void HandleVoiceMessage(string text, string wavPath)
        {
            if (string.IsNullOrEmpty(wavPath))
            {
                // WebSpeechBridge path — no WAV file, send directly.
                if (_d.SendVoiceMessageAsync != null)
                    _ = _d.SendVoiceMessageAsync(text, "");
                return;
            }

            if (_discardedPreviewPaths.Remove(wavPath))
            {
                DeletePreviewFile(wavPath);
                return;
            }

            if (_previewBar == null ||
                !string.Equals(_previewWavPath, wavPath, StringComparison.Ordinal))
            {
                if (_d.ComposerPreviews == null && _d.SendVoiceMessageAsync != null)
                    _ = _d.SendVoiceMessageAsync(text, wavPath);
                return;
            }

            _previewText = text;
            _previewTranscribing = false;
            _previewTranscriptionFailed = false;
            RefreshPreviewTextState();
            _voiceInputManager?.RefreshState();
        }

        private void HandleTranscriptionFailed(string wavPath)
        {
            if (_discardedPreviewPaths.Remove(wavPath))
            {
                DeletePreviewFile(wavPath);
                return;
            }

            if (_previewBar == null ||
                !string.Equals(_previewWavPath, wavPath, StringComparison.Ordinal))
            {
                return;
            }

            _previewText = "";
            _previewTranscribing = false;
            _previewTranscriptionFailed = true;
            RefreshPreviewTextState();
            _voiceInputManager?.RefreshState();
        }

        private void ShowVoicePreview()
        {
            if (_d.ComposerPreviews == null)
            {
                // No UI parent — wait for STT, then fall back to direct send.
                if (!_previewTranscribing &&
                    !_previewTranscriptionFailed &&
                    _d.SendVoiceMessageAsync != null)
                {
                    _ = _d.SendVoiceMessageAsync(_previewText, _previewWavPath);
                }
                return;
            }

            // HideVoicePreview() clears the preview state fields, so stash the values this
            // preview was built for and restore them after tearing down any previous bar.
            string text = _previewText;
            string path = _previewWavPath;
            float durationSecs = _previewDurationSecs;
            bool transcribing = _previewTranscribing;
            bool transcriptionFailed = _previewTranscriptionFailed;

            HideVoicePreview();

            _previewText = text;
            _previewWavPath = path;
            _previewDurationSecs = durationSecs;
            _previewTranscribing = transcribing;
            _previewTranscriptionFailed = transcriptionFailed;

            _previewBar = new VisualElement();
            _previewBar.name = "voice-preview-bar";
            _previewBar.AddToClassList("voice-preview");

            // ── Play / Pause toggle ──────────────────────────────────
            _previewPlayBtn = new Button();
            _previewPlayBtn.name = "voice-preview-play";
            _previewPlayBtn.AddToClassList("voice-preview__play");
            _previewPlaying = false;
            UpdatePreviewPlayIcon();
            _previewPlayBtn.clicked += OnPreviewPlayClicked;
            _previewBar.Add(_previewPlayBtn);

            // ── Mic icon ─────────────────────────────────────────────
            var micIcon = new VisualElement();
            micIcon.AddToClassList("icon");
            micIcon.AddToClassList("icon--mic");
            micIcon.AddToClassList("voice-preview__mic-icon");
            _previewBar.Add(micIcon);

            // ── Duration ─────────────────────────────────────────────
            _previewDurationLabel = new Label(FormatDuration(_previewDurationSecs));
            _previewDurationLabel.AddToClassList("voice-preview__duration");
            _previewBar.Add(_previewDurationLabel);

            // ── Transcribed text ─────────────────────────────────────
            _previewTextLabel = new Label(_previewText);
            _previewTextLabel.AddToClassList("voice-preview__text");
            _previewBar.Add(_previewTextLabel);
            RefreshPreviewTextState();

            // ── Spacer ───────────────────────────────────────────────
            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            _previewBar.Add(spacer);

            // ── Cancel ───────────────────────────────────────────────
            // No dedicated send button here — the composer's standard send button sends this
            // preview (see TrySendActivePreview). Cancel (✕) discards the recording.
            var cancelBtn = new Button();
            cancelBtn.name = "voice-preview-cancel";
            cancelBtn.AddToClassList("voice-preview__cancel");
            cancelBtn.text = "✕";
            cancelBtn.clicked += OnPreviewCancelClicked;
            _previewBar.Add(cancelBtn);

            _d.ComposerPreviews.Add(_previewBar);
            _d.ComposerPreviews.style.display = DisplayStyle.Flex;

            if (_previewPlayer != null)
                _previewPlayer.OnPlaybackComplete += OnPreviewPlaybackComplete;
        }

        private void HideVoicePreview()
        {
            StopPreviewLoadingAnimation();

            if (_previewPlayer != null)
            {
                _previewPlayer.Stop();
                _previewPlayer.OnPlaybackComplete -= OnPreviewPlaybackComplete;
            }

            if (_previewBar != null)
            {
                _previewBar.RemoveFromHierarchy();
                _previewBar = null;
            }

            if (_d.ComposerPreviews != null &&
                _d.ComposerPreviews.childCount == 0)
                _d.ComposerPreviews.style.display = DisplayStyle.None;

            _previewPlaying      = false;
            _previewWavPath      = "";
            _previewText         = "";
            _previewDurationSecs = 0f;
            _previewTranscribing = false;
            _previewTranscriptionFailed = false;
            _voiceInputManager?.RefreshState();
        }

        private void OnPreviewPlayClicked()
        {
            if (_previewPlayer == null || string.IsNullOrEmpty(_previewWavPath))
                return;

            if (_previewPlaying)
            {
                _previewPlayer.Stop();
                _previewPlaying = false;
            }
            else
            {
                _previewPlayer.Play(_previewWavPath);
                _previewPlaying = true;
            }
            UpdatePreviewPlayIcon();
        }

        private void OnPreviewPlaybackComplete()
        {
            _previewPlaying = false;
            UpdatePreviewPlayIcon();
        }

        private void OnPreviewCancelClicked()
        {
            string pathToDelete = _previewWavPath;
            if (_previewTranscribing && !string.IsNullOrEmpty(pathToDelete))
                _discardedPreviewPaths.Add(pathToDelete);
            HideVoicePreview();
            DeletePreviewFile(pathToDelete);
        }

        /// <summary>
        /// Called by the composer's standard send. If a voice preview is active, ships it and
        /// returns true. Hides the preview first to avoid re-entry when the send re-runs the
        /// composer flow.
        /// </summary>
        internal bool TrySendActivePreview(string composerText)
        {
            if (_previewBar == null)
                return false;

            string typedText = (composerText ?? string.Empty).Trim();
            if (_previewTranscribing)
                return true;

            if (_previewTranscriptionFailed && string.IsNullOrWhiteSpace(typedText))
                return true;

            string text = CombineVoiceMessageText(typedText, _previewText);
            string path = _previewWavPath;
            HideVoicePreview();

            if (_d.SendVoiceMessageAsync != null)
                _ = _d.SendVoiceMessageAsync(text, path);
            return true;
        }

        private bool CanStartVoiceRecording()
        {
            return _previewBar == null && string.IsNullOrEmpty(_previewWavPath);
        }

        private static string CombineVoiceMessageText(string typedText, string transcription)
        {
            string typed = (typedText ?? string.Empty).Trim();
            string voice = (transcription ?? string.Empty).Trim();

            if (string.IsNullOrEmpty(typed))
                return voice;
            if (string.IsNullOrEmpty(voice))
                return typed;
            return typed + "\n\n" + voice;
        }

        private void RefreshPreviewTextState()
        {
            if (_previewTextLabel == null)
                return;

            StopPreviewLoadingAnimation();
            _previewTextLabel.EnableInClassList("voice-preview__text--loading", _previewTranscribing);
            _previewTextLabel.EnableInClassList("voice-preview__text--error", _previewTranscriptionFailed);

            if (_previewTranscribing)
            {
                _previewLoadingFrame = 0;
                AdvancePreviewLoadingText();
                _previewLoadingSchedule = _previewTextLabel.schedule
                    .Execute(AdvancePreviewLoadingText)
                    .Every(350);
            }
            else if (_previewTranscriptionFailed)
            {
                _previewTextLabel.text = LocalizationExtensions.Get(
                    "voice.transcription.failed",
                    "Не удалось распознать речь");
            }
            else
            {
                _previewTextLabel.text = _previewText;
            }
        }

        private void AdvancePreviewLoadingText()
        {
            if (_previewTextLabel == null || !_previewTranscribing)
                return;

            _previewLoadingFrame = (_previewLoadingFrame % 3) + 1;
            string label = LocalizationExtensions.Get("voice.transcription.loading", "Распознаём речь");
            _previewTextLabel.text = label + new string('.', _previewLoadingFrame);
        }

        private void StopPreviewLoadingAnimation()
        {
            if (_previewLoadingSchedule != null)
            {
                _previewLoadingSchedule.Pause();
                _previewLoadingSchedule = null;
            }
        }

        private static void DeletePreviewFile(string path)
        {
            if (string.IsNullOrEmpty(path))
                return;

            try { System.IO.File.Delete(path); }
            catch { }
        }

        private void UpdatePreviewPlayIcon()
        {
            if (_previewPlayBtn == null)
                return;
            _previewPlayBtn.text = _previewPlaying ? "⏸" : "▶";
        }

        private static string FormatDuration(float secs)
        {
            int m = (int)(secs / 60f);
            int s = (int)(secs % 60f);
            return m + ":" + s.ToString("D2");
        }

        // ============================================================
        // Voice controls refresh
        // ============================================================

        internal void RefreshVoiceControls()
        {
            _voiceInputManager?.RefreshState();
            if (!(_d.IsVoiceEnabledBySettings != null && _d.IsVoiceEnabledBySettings()))
            {
                _voiceOutputManager?.StopSpeakingAndClear();
                _isVoicePlaying   = false;
                _isVoiceRecording = false;
                _d.RefreshAvatarMotionState?.Invoke();
            }
        }

        // ============================================================
        // Voice animation events
        // ============================================================

        internal void BindVoiceAnimationEvents()
        {
            if (_voiceOutputManager != null)
            {
                _voiceOutputManager.OnPlaybackStarted   -= HandleVoicePlaybackStarted;
                _voiceOutputManager.OnPlaybackCompleted -= HandleVoicePlaybackCompleted;
                _voiceOutputManager.OnPlaybackStarted   += HandleVoicePlaybackStarted;
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

        internal void UnbindVoiceAnimationEvents()
        {
            if (_voiceOutputManager != null)
            {
                _voiceOutputManager.OnPlaybackStarted   -= HandleVoicePlaybackStarted;
                _voiceOutputManager.OnPlaybackCompleted -= HandleVoicePlaybackCompleted;
            }

            if (_voiceInputManager != null)
            {
                _voiceInputManager.OnRecordingStarted -= HandleVoiceRecordingStarted;
                _voiceInputManager.OnRecordingStopped -= HandleVoiceRecordingStopped;
            }

            _isVoicePlaying   = false;
            _isVoiceRecording = false;
        }

        private void HandleVoicePlaybackStarted(string _)
        {
            _isVoicePlaying = true;
            _d.OnVoicePlaybackStarted?.Invoke();
            _d.RefreshAvatarMotionState?.Invoke();
        }

        private void HandleVoicePlaybackCompleted()
        {
            _isVoicePlaying = false;
            _d.RefreshAvatarMotionState?.Invoke();
            _d.OnVoicePlaybackCompleted?.Invoke();
        }

        private void HandleVoiceRecordingStarted()
        {
            _isVoiceRecording = true;
            _d.RefreshAvatarMotionState?.Invoke();
        }

        private void HandleVoiceRecordingStopped()
        {
            _isVoiceRecording = false;
            _d.RefreshAvatarMotionState?.Invoke();
        }
    }
}
