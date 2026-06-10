using System;
using System.Collections.Generic;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Localization;
using NeonCompanion.Runtime.Platform;
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
        private bool _isHolding;
#if UNITY_ANDROID && !UNITY_EDITOR
        private bool _permissionRequestPending;
#endif

        // One entry per completed recording, in order, so each transcription result is paired
        // with its own WAV file (a single shared field would be clobbered by back-to-back records).
        private readonly Queue<string> _pendingVoicePaths = new Queue<string>();

        public bool IsRecording => _voiceService?.IsRecording ?? false;

        public event Action OnRecordingStarted;
        public event Action OnRecordingStopped;
        public event Action<string, float> OnVoicePreviewReady;
        public event Action<string> OnTranscriptionFailed;

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
                // Hold-to-record: the user controls start/stop, so disable silence auto-stop.
                _voiceService.AutoStopOnSilence = false;
            }

            if (_micButton != null)
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                // Touch input uses tap-to-toggle. A press/release pair is too short for useful
                // microphone capture and can start and stop within the same rendered frame.
                _micButton.clicked += OnAndroidMicClicked;
#else
                // Register in the TrickleDown (capture) phase: Button's built-in Clickable
                // calls StopImmediatePropagation() in BubbleUp, which would otherwise eat
                // these handlers and break press-and-hold.
                _micButton.RegisterCallback<PointerDownEvent>(OnMicPointerDown, TrickleDown.TrickleDown);
                _micButton.RegisterCallback<PointerUpEvent>(OnMicPointerUp, TrickleDown.TrickleDown);
                _micButton.RegisterCallback<PointerCaptureOutEvent>(OnMicPointerCaptureOut);
#endif
            }

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
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                _micButton.clicked -= OnAndroidMicClicked;
#else
                _micButton.UnregisterCallback<PointerDownEvent>(OnMicPointerDown, TrickleDown.TrickleDown);
                _micButton.UnregisterCallback<PointerUpEvent>(OnMicPointerUp, TrickleDown.TrickleDown);
                _micButton.UnregisterCallback<PointerCaptureOutEvent>(OnMicPointerCaptureOut);
#endif
            }
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

#if UNITY_ANDROID && !UNITY_EDITOR
        private void OnAndroidMicClicked()
        {
            if (_voiceService == null || _micButton == null)
                return;

            if (!_voiceService.IsAvailable || !(_isVoiceEnabled?.Invoke() ?? false))
            {
                NeonLogger.Log("Voice input is disabled by settings or unavailable backend.");
                return;
            }

            if (IsRecording)
            {
                StopRecording();
                return;
            }

            if (Permission.HasUserAuthorizedPermission(Permission.Microphone))
            {
                StartRecording();
                return;
            }

            RequestMicrophonePermissionAndStart();
        }

        private void RequestMicrophonePermissionAndStart()
        {
            if (_permissionRequestPending)
                return;

            _permissionRequestPending = true;
            PermissionCallbacks callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += permissionName =>
            {
                _permissionRequestPending = false;
                if (permissionName == Permission.Microphone &&
                    _voiceService != null &&
                    !IsRecording &&
                    (_isVoiceEnabled?.Invoke() ?? false))
                {
                    StartRecording();
                }
            };
            callbacks.PermissionDenied += permissionName =>
            {
                _permissionRequestPending = false;
                NeonLogger.LogWarning("Microphone permission denied: " + permissionName);
            };
            Permission.RequestUserPermission(Permission.Microphone, callbacks);
        }
#endif

        // Hold-to-record: press starts capture, release stops and ships it.
        private void OnMicPointerDown(PointerDownEvent evt)
        {
            if (_voiceService == null || _micButton == null || IsRecording)
                return;

            if (!_voiceService.IsAvailable || !(_isVoiceEnabled?.Invoke() ?? false))
            {
                NeonLogger.Log("Voice input is disabled by settings or unavailable backend.");
                return;
            }

            if (!EnsureMicrophonePermission())
                return;

            // No manual CapturePointer here — the Button's built-in Clickable already captures
            // the pointer, so PointerUp is delivered even if the cursor leaves the button.
            // Manually capturing on top of that left the capture stuck and froze all UI clicks.
            _isHolding = true;
            StartRecording();
        }

        private void OnMicPointerUp(PointerUpEvent evt)
        {
            if (!_isHolding)
                return;
            _isHolding = false;

            if (IsRecording)
                StopRecording();
        }

        // Safety: if the pointer capture is released without a PointerUp reaching us (e.g. the
        // window loses focus mid-hold), end the recording so the mic can't get stuck on.
        private void OnMicPointerCaptureOut(PointerCaptureOutEvent evt)
        {
            if (!_isHolding)
                return;
            _isHolding = false;
            if (IsRecording)
                StopRecording();
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
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                AndroidHapticFeedback.Pulse();
#endif
                OnRecordingStarted?.Invoke();
            }
        }

        private void StopRecording()
        {
            bool wasRecording = _voiceService.IsRecording;
            _voiceService.StopRecording();
            UpdateMicVisual(false);
#if UNITY_ANDROID && !UNITY_EDITOR
            if (wasRecording)
                AndroidHapticFeedback.Pulse();
#endif
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
            _pendingVoicePaths.Enqueue(wavPath ?? "");
            OnVoicePreviewReady?.Invoke(wavPath ?? "", durationSecs);
        }

        private void HandleSpeechRecognized(string text)
        {
            // VAD or manual stop has ended the recording — reset button visual.
            // (VAD calls StopRecording() internally without going through us.)
            UpdateMicVisual(false);
            OnRecordingStopped?.Invoke();

            // Pair this result with the WAV from the matching recording (FIFO). Empty for
            // backends that don't capture a file (e.g. WebSpeechBridge).
            string path = _pendingVoicePaths.Count > 0 ? _pendingVoicePaths.Dequeue() : "";

            // Fire the voice message event. VoiceController decides whether to show
            // a preview or send directly (based on whether path is non-empty).
            if (!string.IsNullOrWhiteSpace(text))
                OnVoiceMessage?.Invoke(text.Trim(), path);
            else
                OnTranscriptionFailed?.Invoke(path);
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
