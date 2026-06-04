using System;
using System.Text;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Localization;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK.Chat
{
    internal sealed class ChatStreamingCoordinator
    {
        private readonly ScrollView _messagesList;
        private readonly Action _scrollToBottom;
        private readonly Func<ChatMessage, VisualElement> _createMessageElement;
        private readonly Action<VisualElement> _applyTextCursor;
        private readonly Action<VisualElement> _onBubbleReady;
        private readonly Action _onFirstToken;

        private VisualElement _bubble;
        private NeonCompanion.Runtime.UI.UITK.SelectableMarkdownElement _label;
        private VisualElement _typingDots;
        private readonly StringBuilder _textBuffer = new StringBuilder();
        private readonly StringBuilder _segmentBuffer = new StringBuilder();
        private IVisualElementScheduledItem _typingSchedule;
        private int _typingFrame;
        private DateTime _startTime;
        private int _estimatedTokens;
        private VisualElement _statsFooter;
        private Label _statsLabel;
        private IVisualElementScheduledItem _statsSchedule;

        internal bool IsStreaming { get; private set; }
        internal DateTime StartTime { get { return _startTime; } }
        internal int EstimatedTokens { get { return _estimatedTokens; } }

        internal ChatStreamingCoordinator(
            ScrollView messagesList,
            Action scrollToBottom,
            Func<ChatMessage, VisualElement> createMessageElement,
            Action<VisualElement> applyTextCursor,
            Action<VisualElement> onBubbleReady,
            Action onFirstToken)
        {
            _messagesList = messagesList;
            _scrollToBottom = scrollToBottom;
            _createMessageElement = createMessageElement;
            _applyTextCursor = applyTextCursor;
            _onBubbleReady = onBubbleReady;
            _onFirstToken = onFirstToken;
        }

        internal void Begin()
        {
            if (_messagesList == null) return;
            var placeholder = _createMessageElement(new ChatMessage { role = "assistant", content = "" });
            _messagesList.Add(placeholder);

            var bubble = placeholder.Q<VisualElement>(className: "transcript__bubble");

            _bubble = bubble;
            _label = null;
            IsStreaming = false;
            _textBuffer.Length = 0;
            _segmentBuffer.Length = 0;

            _typingDots = new VisualElement();
            _typingDots.AddToClassList("typing--inline");
            for (int i = 0; i < 3; i++)
            {
                var dot = new VisualElement();
                dot.AddToClassList("typing__dot");
                if (i == 1) dot.AddToClassList("typing__dot--delay-1");
                if (i == 2) dot.AddToClassList("typing__dot--delay-2");
                _typingDots.Add(dot);
            }
            if (bubble != null)
                bubble.Insert(1, _typingDots);

            _onBubbleReady?.Invoke(bubble);

            if (bubble != null)
            {
                _statsFooter = bubble.Q<VisualElement>(className: "transcript__stats");
                _statsLabel = _statsFooter != null ? _statsFooter.Q<Label>(className: "transcript__stats-label") : null;
                if (_statsFooter != null)
                    _statsFooter.style.display = DisplayStyle.Flex;
            }
            StartStatsUpdate();
            StartTypingAnimation();
            _scrollToBottom?.Invoke();
        }

        internal void OnToken(string token)
        {
            if (_typingDots != null)
            {
                StopTypingAnimation();
                _typingDots.RemoveFromHierarchy();
                _typingDots = null;
                IsStreaming = true;
                _onFirstToken?.Invoke();
            }

            EnsureLabel();
            if (_label != null)
            {
                _textBuffer.Append(token);
                _segmentBuffer.Append(token);
                _label.SetMarkdown(_segmentBuffer.ToString());
            }

            if (!string.IsNullOrEmpty(token))
                _estimatedTokens += Math.Max(1, token.Length / 4);

            UpdateStats();
            _scrollToBottom?.Invoke();
        }

        internal void ResetStreamingSegment()
        {
            _label = null;
            _segmentBuffer.Length = 0;
        }

        internal Label PauseStatsSchedule()
        {
            if (_statsSchedule != null)
            {
                _statsSchedule.Pause();
                _statsSchedule = null;
            }
            return _statsLabel;
        }

        internal void SetFinalStats(int tokenCount, double elapsedSeconds)
        {
            _estimatedTokens = tokenCount;
            if (_statsLabel != null)
            {
                string template = LocalizationExtensions.Get("chat.stats.footer", "~{0} tok · {1:F1}s");
                string exactTemplate = template.Replace("~", string.Empty);
                _statsLabel.text = string.Format(exactTemplate, tokenCount, elapsedSeconds);
            }
        }

        internal void Abort()
        {
            StopTypingAnimation();
            StopStatsUpdate();
            if (_typingDots != null)
            {
                _typingDots.RemoveFromHierarchy();
                _typingDots = null;
            }
            _bubble = null;
            _label = null;
            IsStreaming = false;
        }

        private void EnsureLabel()
        {
            if (_label != null || _bubble == null)
                return;

            _label = new NeonCompanion.Runtime.UI.UITK.SelectableMarkdownElement();
            _label.SetMarkdown(string.Empty);
            _label.AddToClassList("transcript__body");
            _label.style.minWidth = 0;
            _label.style.width = Length.Percent(100);
            _label.style.minHeight = 20;
            _applyTextCursor?.Invoke(_label);

            var statsFooter = _bubble.Q<VisualElement>(className: "transcript__stats");
            if (statsFooter != null && statsFooter.parent == _bubble)
            {
                int idx = _bubble.IndexOf(statsFooter);
                _bubble.Insert(idx, _label);
            }
            else
            {
                _bubble.Add(_label);
            }
        }

        private void StartStatsUpdate()
        {
            if (_statsSchedule != null)
            {
                _statsSchedule.Pause();
                _statsSchedule = null;
            }
            _startTime = DateTime.UtcNow;
            _estimatedTokens = 0;
            if (_statsLabel == null)
                return;
            UpdateStats();
            _statsSchedule = _statsLabel.schedule.Execute(() =>
            {
                if (_statsLabel == null)
                {
                    if (_statsSchedule != null)
                    {
                        _statsSchedule.Pause();
                        _statsSchedule = null;
                    }
                    return;
                }
                UpdateStats();
            }).Every(500);
        }

        private void StopStatsUpdate()
        {
            if (_statsSchedule != null)
            {
                _statsSchedule.Pause();
                _statsSchedule = null;
            }
            _statsLabel = null;
            _statsFooter = null;
        }

        private void UpdateStats()
        {
            if (_statsLabel == null)
                return;
            double elapsed = (DateTime.UtcNow - _startTime).TotalSeconds;
            if (elapsed < 0)
                elapsed = 0;
            string stats = LocalizationExtensions.GetFormat("chat.stats.footer", "~{0} tok · {1:F1}s", _estimatedTokens, elapsed);
            _statsLabel.text = stats;
        }

        private void StartTypingAnimation()
        {
            _typingFrame = 0;
            _typingSchedule?.Pause();
            _typingSchedule = _messagesList?.schedule.Execute(() =>
            {
                if (_typingDots == null)
                {
                    _typingSchedule?.Pause();
                    return;
                }
                var dots = _typingDots.Query<VisualElement>(className: "typing__dot").ToList();
                for (int i = 0; i < dots.Count; i++)
                    dots[i].style.opacity = i == (_typingFrame % 3) ? 1f : 0.25f;
                _typingFrame++;
            }).Every(200);
        }

        private void StopTypingAnimation()
        {
            _typingSchedule?.Pause();
            _typingSchedule = null;
        }
    }
}
