using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Chat;
using UnityEngine;

namespace NeonCompanion.Runtime.Voice
{
    public sealed class VoiceOutputManager : MonoBehaviour
    {
        private static readonly Regex SentenceRegex = new Regex(@"[^.!?]+[.!?]?", RegexOptions.Compiled);

        private readonly Queue<string> _queue = new Queue<string>();
        private IVoiceService _voiceService;
        private Func<bool> _isVoiceEnabled;
        private Func<bool> _isUserRecording;
        private bool _isConsuming;

        public event Action<string> OnPlaybackStarted;
        public event Action OnPlaybackCompleted;
        /// <summary>(ttsAudioFilePath, durationSecs) — a synthesized response clip was saved to disk.</summary>
        public event Action<string, float> OnResponseAudioReady;

        public void Initialize(IVoiceService voiceService, Func<bool> isVoiceEnabled, Func<bool> isUserRecording)
        {
            _voiceService = voiceService;
            _isVoiceEnabled = isVoiceEnabled;
            _isUserRecording = isUserRecording;

            if (_voiceService != null)
                _voiceService.OnSpeechAudioReady += HandleSpeechAudioReady;
        }

        private void OnDestroy()
        {
            if (_voiceService != null)
                _voiceService.OnSpeechAudioReady -= HandleSpeechAudioReady;
        }

        private void HandleSpeechAudioReady(string path, float durationSecs)
        {
            OnResponseAudioReady?.Invoke(path, durationSecs);
        }

        public void BindChat(ChatService chatService)
        {
            if (chatService != null)
                chatService.OnAssistantResponse += EnqueueResponse;
        }

        public void UnbindChat(ChatService chatService)
        {
            if (chatService != null)
                chatService.OnAssistantResponse -= EnqueueResponse;
        }

        public void StopSpeakingAndClear()
        {
            _queue.Clear();
            _voiceService?.StopSpeaking();
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
                    OnPlaybackStarted?.Invoke(next);

                    var tcs = new TaskCompletionSource<bool>();

                    void Complete() => tcs.TrySetResult(true);
                    _voiceService.OnPlaybackComplete += Complete;
                    _voiceService.Speak(next);

                    var timeoutTask = Task.Delay(15000);
                    var doneTask = await Task.WhenAny(tcs.Task, timeoutTask);
                    _voiceService.OnPlaybackComplete -= Complete;

                    // The service (and this manager) may have been destroyed while we awaited —
                    // e.g. the voice pipeline was reinitialized. Stop before touching dead objects;
                    // continuing would call Speak/StartCoroutine on an inactive GameObject.
                    if (!ServiceUsable())
                        break;

                    if (doneTask == timeoutTask)
                        _voiceService.StopSpeaking();

                    OnPlaybackCompleted?.Invoke();
                }
            }
            finally
            {
                _isConsuming = false;
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

        private static IEnumerable<string> SplitSentences(string text)
        {
            foreach (Match match in SentenceRegex.Matches(text))
            {
                var value = match.Value?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                    yield return value;
            }
        }
    }
}
