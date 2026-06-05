using System;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Chat;
using NeonCompanion.Runtime.Data.Models;
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
            public Func<AppSettings> GetAppSettings;
        }

        private Deps _d;
        private IVoiceService _voiceService;
        private VoiceInputManager _voiceInputManager;
        private VoiceOutputManager _voiceOutputManager;
        private LipsyncController _lipsyncController; // V-02 wiring
        private bool _voiceBoundToChat;
        private bool _isVoicePlaying;
        private bool _isVoiceRecording;
        private string _lastConfigHash;

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
                {
                    _voiceService = created;
                }
                else
                {
                    _voiceService = _d.gameObject.AddComponent<WebSpeechBridge>();
                }
            }

            if (_voiceOutputManager == null)
            {
                // Always AddComponent — never GetComponent. ReinitializeVoiceService destroys
                // old components with Object.Destroy (deferred), so GetComponent would still
                // return the pending-destroy instance and wire a dead manager to the pipeline.
                _voiceOutputManager = _d.gameObject.AddComponent<VoiceOutputManager>();
                _voiceOutputManager.Initialize(_voiceService, _d.IsVoiceEnabledBySettings, () => _voiceInputManager != null && _voiceInputManager.IsRecording);
            }

            if (_voiceInputManager == null)
            {
                _voiceInputManager = _d.gameObject.AddComponent<VoiceInputManager>();
                _voiceInputManager.Initialize(_voiceService, _d.MicButton, _d.IsVoiceEnabledBySettings, _d.SendVoiceMessageAsync, _d.OnVoiceRecordingStarted);
            }

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

        private string ComputeConfigHash(ProviderConfig provider, AppSettings settings)
        {
            if (provider == null || settings == null)
                return "";
            return (provider.id ?? "") + "|" + (provider.baseUrl ?? "") + "|"
                + (provider.ttsVoice ?? "") + "|" + (provider.ttsModel ?? "") + "|"
                + provider.ttsSpeed.ToString() + "|"
                + (settings.inputDeviceName ?? "") + "|" + settings.outputVolume.ToString();
        }

        private void ReinitializeVoiceService(ProviderConfig provider, AppSettings settings, ChatService chat)
        {
            UnbindVoiceAnimationEvents();

            if (_voiceOutputManager != null)
            {
                if (_voiceBoundToChat && chat != null)
                    _voiceOutputManager.UnbindChat(chat);
                UnityEngine.Object.Destroy(_voiceOutputManager);
                _voiceOutputManager = null;
                _voiceBoundToChat = false;
            }

            if (_voiceInputManager != null)
            {
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
            {
                _voiceService = created;
            }
            else
            {
                // Old bridge was destroyed above (deferred). AddComponent always — GetComponent
                // would return the pending-destroy instance during the same frame.
                _voiceService = _d.gameObject.AddComponent<WebSpeechBridge>();
            }
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
