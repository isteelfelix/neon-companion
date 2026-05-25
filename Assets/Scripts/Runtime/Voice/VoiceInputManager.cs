using System;
using System.Threading.Tasks;
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
        private Func<string, Task> _sendRecognizedMessageAsync;
        private Action _onRecordingStarted;
        private bool _pulseGrowing = true;
        private float _pulseOpacity = 1f;

        public bool IsRecording => _voiceService?.IsRecording ?? false;

        public event Action OnRecordingStarted;
        public event Action OnRecordingStopped;

        public void Initialize(
            IVoiceService voiceService,
            Button micButton,
            Func<bool> isVoiceEnabled,
            Func<string, Task> sendRecognizedMessageAsync,
            Action onRecordingStarted)
        {
            _voiceService = voiceService;
            _micButton = micButton;
            _isVoiceEnabled = isVoiceEnabled;
            _sendRecognizedMessageAsync = sendRecognizedMessageAsync;
            _onRecordingStarted = onRecordingStarted;

            if (_voiceService != null)
                _voiceService.OnSpeechRecognized += HandleSpeechRecognized;

            if (_micButton != null)
                _micButton.clicked += ToggleRecording;

            UpdateMicButtonState();
        }

        private void OnDestroy()
        {
            if (_voiceService != null)
                _voiceService.OnSpeechRecognized -= HandleSpeechRecognized;

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
            UpdateMicVisual(true);
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

        private void HandleSpeechRecognized(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || _sendRecognizedMessageAsync == null)
                return;

            _ = _sendRecognizedMessageAsync(text.Trim());
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
