using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Chat;
using UnityEngine;

namespace NeonCompanion.Runtime.Voice
{
    public sealed class VoiceOutputManager : MonoBehaviour
    {
        private readonly Queue<string> _queue = new Queue<string>();
        private IVoiceService _voiceService;
        private Func<bool> _isVoiceEnabled;
        private Func<bool> _isUserRecording;
        private Func<bool> _shouldAutoVoice;
        private bool _isConsuming;
        private string _activeResponse;
        private int _playbackGeneration;
        private bool _playbackAnnounced;
        private TaskCompletionSource<bool> _activePlaybackCompletion;

        public event Action<string> OnPlaybackStarted;
        public event Action OnPlaybackCompleted;
        /// <summary>(ttsAudioFilePath, durationSecs) — a synthesized response clip was saved to disk.</summary>
        public event Action<string, float> OnResponseAudioReady;

        public void Initialize(IVoiceService voiceService, Func<bool> isVoiceEnabled, Func<bool> isUserRecording,
            Func<bool> shouldAutoVoice = null)
        {
            _voiceService = voiceService;
            _isVoiceEnabled = isVoiceEnabled;
            _isUserRecording = isUserRecording;
            _shouldAutoVoice = shouldAutoVoice;

            if (_voiceService != null)
            {
                _voiceService.OnPlaybackStarted += HandlePlaybackStarted;
                _voiceService.OnSpeechAudioReady += HandleSpeechAudioReady;
            }
        }

        private void OnDestroy()
        {
            _playbackGeneration++;
            _queue.Clear();
            if (_activePlaybackCompletion != null)
            {
                _activePlaybackCompletion.TrySetResult(true);
                _activePlaybackCompletion = null;
            }
            if (_voiceService != null)
            {
                _voiceService.OnPlaybackStarted -= HandlePlaybackStarted;
                _voiceService.OnSpeechAudioReady -= HandleSpeechAudioReady;
            }
        }

        private void HandlePlaybackStarted()
        {
            _playbackAnnounced = true;
            OnPlaybackStarted?.Invoke(_activeResponse ?? string.Empty);
        }

        private void HandleSpeechAudioReady(string path, float durationSecs)
        {
            OnResponseAudioReady?.Invoke(path, durationSecs);
        }

        public VoicePlaybackState GetPlaybackState(string audioPath)
        {
            ISeekableVoicePlayback seekable = _voiceService as ISeekableVoicePlayback;
            return seekable != null
                ? seekable.GetPlaybackState(audioPath)
                : new VoicePlaybackState();
        }

        public VoicePlaybackState GetCurrentPlaybackState()
        {
            IVoicePlaybackClock clock = _voiceService as IVoicePlaybackClock;
            return clock != null
                ? clock.GetCurrentPlaybackState()
                : new VoicePlaybackState();
        }

        public bool TogglePlayback(string audioPath)
        {
            ISeekableVoicePlayback seekable = _voiceService as ISeekableVoicePlayback;
            return seekable != null && seekable.TogglePlayback(audioPath);
        }

        public bool SeekPlayback(string audioPath, float normalized)
        {
            ISeekableVoicePlayback seekable = _voiceService as ISeekableVoicePlayback;
            return seekable != null && seekable.SeekPlayback(audioPath, normalized);
        }

        public void BindChat(ChatService chatService)
        {
            if (chatService != null)
                chatService.OnAssistantResponse += HandleAssistantResponse;
        }

        public void UnbindChat(ChatService chatService)
        {
            if (chatService != null)
                chatService.OnAssistantResponse -= HandleAssistantResponse;
        }

        // Auto-TTS gate: only voice a response if always-on mode is set OR the user's last
        // message was itself voice (reply in kind). Manual "listen" calls EnqueueResponse directly
        // and is never gated.
        private void HandleAssistantResponse(string response)
        {
            if (_shouldAutoVoice != null && !_shouldAutoVoice())
                return;
            EnqueueResponse(response);
        }

        public void StopSpeakingAndClear()
        {
            bool notifyStopped = _isConsuming || _playbackAnnounced ||
                !string.IsNullOrEmpty(_activeResponse);
            _playbackGeneration++;
            _queue.Clear();
            _activeResponse = null;
            _playbackAnnounced = false;
            if (_activePlaybackCompletion != null)
            {
                _activePlaybackCompletion.TrySetResult(true);
                _activePlaybackCompletion = null;
            }
            _voiceService?.StopSpeaking();
            if (notifyStopped)
                OnPlaybackCompleted?.Invoke();
        }

        public void EnqueueResponse(string response)
        {
            if (string.IsNullOrWhiteSpace(response) || _voiceService == null)
                return;

            // Enqueue the whole response as one item so it synthesizes to a single audio file
            // (one cached clip per assistant message, not one per sentence).
            _queue.Enqueue(response.Trim());

            if (!_isConsuming)
                _ = ConsumeQueueAsync();
        }

        private async Task ConsumeQueueAsync()
        {
            _isConsuming = true;
            try
            {
                while (_queue.Count > 0)
                {
                    if (!ServiceUsable())
                        break;

                    if (!(_isVoiceEnabled?.Invoke() ?? false) || (_isUserRecording?.Invoke() ?? false))
                    {
                        _queue.Clear();
                        _voiceService.StopSpeaking();
                        break;
                    }

                    var next = _queue.Dequeue();
                    _activeResponse = next;
                    int generation = _playbackGeneration;

                    var tcs = new TaskCompletionSource<bool>();
                    _activePlaybackCompletion = tcs;

                    void Complete() => tcs.TrySetResult(true);
                    _voiceService.OnPlaybackComplete += Complete;
                    Task timeoutTask = Task.Delay(30 * 60 * 1000);
                    Task doneTask;
                    try
                    {
                        _voiceService.Speak(next);

                        // Covers synthesis plus full playback, including user pause. Backend HTTP
                        // requests keep their own shorter timeout; this is only a final stuck guard.
                        doneTask = await Task.WhenAny(tcs.Task, timeoutTask);
                    }
                    finally
                    {
                        _voiceService.OnPlaybackComplete -= Complete;
                        if (ReferenceEquals(_activePlaybackCompletion, tcs))
                            _activePlaybackCompletion = null;
                    }

                    if (generation != _playbackGeneration)
                        break;

                    // The service (and this manager) may have been destroyed while we awaited —
                    // e.g. the voice pipeline was reinitialized. Stop before touching dead objects;
                    // continuing would call Speak/StartCoroutine on an inactive GameObject.
                    if (!ServiceUsable())
                        break;

                    if (doneTask == timeoutTask)
                        _voiceService.StopSpeaking();

                    OnPlaybackCompleted?.Invoke();
                    _playbackAnnounced = false;
                    _activeResponse = null;
                }
            }
            finally
            {
                _activeResponse = null;
                _playbackAnnounced = false;
                _isConsuming = false;
                if (_queue.Count > 0 && ServiceUsable())
                    _ = ConsumeQueueAsync();
            }
        }

        // True only if this manager and its voice service are both still alive. Uses Unity's
        // overloaded == so a destroyed MonoBehaviour (native object gone) reads as not usable.
        private bool ServiceUsable()
        {
            if (this == null)
                return false;

            UnityEngine.Object svc = _voiceService as UnityEngine.Object;
            if (!ReferenceEquals(svc, null) && svc == null)
                return false; // a UnityEngine.Object service that has been destroyed

            return _voiceService != null;
        }
    }
}
