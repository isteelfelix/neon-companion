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

        public void Initialize(IVoiceService voiceService, Func<bool> isVoiceEnabled, Func<bool> isUserRecording)
        {
            _voiceService = voiceService;
            _isVoiceEnabled = isVoiceEnabled;
            _isUserRecording = isUserRecording;
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

            foreach (var sentence in SplitSentences(response))
            {
                if (!string.IsNullOrWhiteSpace(sentence))
                    _queue.Enqueue(sentence.Trim());
            }

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
