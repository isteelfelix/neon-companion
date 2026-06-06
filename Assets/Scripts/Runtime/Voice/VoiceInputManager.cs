using System;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Localization;
using UnityEngine;
using UnityEngine.Android;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.Voice
{
    public sealed class VoiceInputManager : MonoBehaviour
    {
        private const string MicRecordingClass = "mic-btn--recording";

        private IVoiceService _voiceService;
        private Button _micButton;
        private Func<bool> _isVoiceEnabled;
        private Action _onRecordingStarted;
        private bool _pulseGrowing = true;
        private float _pulseOpacity = 1f;

        // Set by OnRecordingComplete so HandleSpeechRecognized can attach it to the outgoing message.
        private string _pendingVoicePath = "";

        public bool IsRecording => _voiceService?.IsRecording ?? false;

        public event Action OnRecordingStarted;
        public event Action OnRecordingStopped;

        /// <summary>
        /// Fires when STT is done. (transcribedText, wavFilePath) — wavFilePath may be "" for
        /// WebSpeechBridge / platforms that don't capture a WAV file.
        /// </summary>
        public event Action<string, string> OnVoiceMessage;

        public void Initialize(
            IVoiceService voiceService,
            Button micButton,
            Func<bool> isVoiceEnabled,
            Action onRecordingStarted)
        {
            _voiceService = voiceService;
            _micButton = micButton;
            _isVoiceEnabled = isVoiceEnabled;
            _onRecordingStarted = onRecordingStarted;

            if (_voiceService != null)
            {
                _voiceService.OnSpeechRecognized += HandleSpeechRecognized;
                _voiceService.OnRecordingComplete += HandleRecordingComplete;
            }

            if (_micButton != null)
                _micButton.clicked += ToggleRecording;

            UpdateMicButtonState();
        }

        private void OnDestroy()
        {
            if (_voiceService != null)
            {
                _voiceService.OnSpeechRecognized -= HandleSpeechRecognized;
                _voiceService.OnRecordingComplete -= HandleRecordingComplete;
            }

            if (_micButton != null)
                _micButton.clicked -= ToggleRecording;
        }

        private void Update()
        {
            if (_micButton == null || !IsRecording)
                return;

            float speed = 1.5f * Time.unscaledDeltaTime;
            _pulseOpacity += (_pulseGrowing ? speed : -speed);
            if (_pulseOpacity >= 1f)
            {
                _pulseOpacity = 1f;
                _pulseGrowing = false;
            }
            else if (_pulseOpacity <= 0.45f)
            {
                _pulseOpacity = 0.45f;
                _pulseGrowing = true;
            }

            _micButton.style.opacity = _pulseOpacity;
        }

        public void RefreshState()
        {
            UpdateMicButtonState();
        }

        private void ToggleRecording()
        {
            if (_voiceService == null || _micButton == null)
                return;

            if (!_voiceService.IsAvailable || !(_isVoiceEnabled?.Invoke() ?? false))
            {
                NeonLogger.Log("Voice input is disabled by settings or unavailable backend.");
                return;
            }

            if (!EnsureMicrophonePermission())
                return;

            if (IsRecording)
                StopRecording();
            else
                StartRecording();
        }

        private void StartRecording()
        {
            _onRecordingStarted?.Invoke();
            _voiceService.StartRecording();
            // IsRecording reflects actual state — Microphone.Start can fail silently,
            // so only flip the visual and fire the event if recording actually began.
            bool started = _voiceService.IsRecording;
            UpdateMicVisual(started);
            if (started)
                OnRecordingStarted?.Invoke();
        }

        private void StopRecording()
        {
            _voiceService.StopRecording();
            UpdateMicVisual(false);
            OnRecordingStopped?.Invoke();
        }

        private bool EnsureMicrophonePermission()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (Permission.HasUserAuthorizedPermission(Permission.Microphone))
                return true;

            Permission.RequestUserPermission(Permission.Microphone);
            return false;
#else
            return true;
#endif
        }

        private void HandleRecordingComplete(string wavPath, float durationSecs)
        {
            _pendingVoicePath = wavPath ?? "";
        }

        private void HandleSpeechRecognized(string text)
        {
            // VAD or manual stop has ended the recording — reset button visual.
            // (VAD calls StopRecording() internally without going through us.)
            UpdateMicVisual(false);
            OnRecordingStopped?.Invoke();

            string path = _pendingVoicePath;
            _pendingVoicePath = "";

            // Fire the voice message event. VoiceController decides whether to show
            // a preview or send directly (based on whether path is non-empty).
            if (!string.IsNullOrWhiteSpace(text))
                OnVoiceMessage?.Invoke(text.Trim(), path);
        }

        private void UpdateMicButtonState()
        {
            if (_micButton == null)
                return;

            bool enabled = (_isVoiceEnabled?.Invoke() ?? false) && (_voiceService?.IsAvailable ?? false);
            _micButton.SetEnabled(enabled);
            if (!enabled)
                UpdateMicVisual(false);
        }

        private void UpdateMicVisual(bool isRecording)
        {
            if (_micButton == null)
                return;

            _micButton.EnableInClassList(MicRecordingClass, isRecording);
            _micButton.tooltip = isRecording
                ? LocalizationExtensions.Get("voice.mic.stop", "Остановить запись")
                : LocalizationExtensions.Get("voice.mic.start", "Голосовой ввод");
            _micButton.style.opacity = 1f;
            _pulseOpacity = 1f;
            _pulseGrowing = false;
        }
    }
}
