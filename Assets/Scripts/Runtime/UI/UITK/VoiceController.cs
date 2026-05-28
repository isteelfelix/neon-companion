using System;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Chat;
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
            public Func<bool> IsVoiceEnabledBySettings;
            public Func<string, Task> SendVoiceMessageAsync;
            public Action OnVoiceRecordingStarted;
            public Action RefreshAvatarMotionState;
            public Func<Task<ChatService>> GetChatServiceAsync;
            public Func<ChatService> GetChatServiceSync;
            public Func<bool> IsBound;
        }

        private Deps _d;
        private IVoiceService _voiceService;
        private VoiceInputManager _voiceInputManager;
        private VoiceOutputManager _voiceOutputManager;
        private bool _voiceBoundToChat;
        private bool _isVoicePlaying;
        private bool _isVoiceRecording;

        public bool IsVoicePlaying => _isVoicePlaying;
        public bool IsVoiceRecording => _isVoiceRecording;

        // ============================================================
        // Lifecycle
        // ============================================================

        internal void SetDeps(Deps deps)
        {
            _d = deps;
        }

        internal void Init()
        {
            // No UI queries needed; _micButton comes from Deps.
        }

        internal void RegisterCallbacks()
        {
        }

        internal void UnregisterCallbacks()
        {
        }

        internal void OnDisable()
        {
            UnbindVoiceAnimationEvents();
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

            if (_voiceService == null)
            {
                _voiceService = _d.gameObject.GetComponent<WebSpeechBridge>();
                if (_voiceService == null)
                    _voiceService = _d.gameObject.AddComponent<WebSpeechBridge>();
            }

            if (_voiceOutputManager == null)
            {
                _voiceOutputManager = _d.gameObject.GetComponent<VoiceOutputManager>();
                if (_voiceOutputManager == null)
                    _voiceOutputManager = _d.gameObject.AddComponent<VoiceOutputManager>();
                _voiceOutputManager.Initialize(_voiceService, _d.IsVoiceEnabledBySettings, () => _voiceInputManager != null && _voiceInputManager.IsRecording);
            }

            if (_voiceInputManager == null)
            {
                _voiceInputManager = _d.gameObject.GetComponent<VoiceInputManager>();
                if (_voiceInputManager == null)
                    _voiceInputManager = _d.gameObject.AddComponent<VoiceInputManager>();
                _voiceInputManager.Initialize(_voiceService, _d.MicButton, _d.IsVoiceEnabledBySettings, _d.SendVoiceMessageAsync, _d.OnVoiceRecordingStarted);
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

        internal void OnVoiceRecordingStarted()
        {
            _voiceOutputManager?.StopSpeakingAndClear();
        }

        internal void EnqueueVoiceResponse(string text)
        {
            _voiceOutputManager?.EnqueueResponse(text);
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
                _isVoicePlaying = false;
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

        internal void UnbindVoiceAnimationEvents()
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
            _d.RefreshAvatarMotionState?.Invoke();
        }

        private void HandleVoicePlaybackCompleted()
        {
            _isVoicePlaying = false;
            _d.RefreshAvatarMotionState?.Invoke();
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
