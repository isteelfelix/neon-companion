using System;
using System.Collections.Generic;
using System.Text;
using NeonCompanion.Runtime.Api.Tools;
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
        private readonly VisualElement _thinkingBubble;
        private readonly Label _thinkingText;
        private readonly Button _sendButton;
        private readonly Button _stopButton;
        private readonly ToolCallApprovalController _approvalController;
        private readonly Action _onStreamEnd;
        private readonly Action _refreshAvatarMotionState;

        private VisualElement _bubble;
        // Outline beam of the bubble being streamed into; null once the stream ends.
        private BubbleBeamElement _beam;
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
        internal bool IsSending { get; private set; }
        internal DateTime StartTime { get { return _startTime; } }
        internal int EstimatedTokens { get { return _estimatedTokens; } }

        internal ChatStreamingCoordinator(
            ScrollView messagesList,
            Action scrollToBottom,
            Func<ChatMessage, VisualElement> createMessageElement,
            Action<VisualElement> applyTextCursor,
            Action<VisualElement> onBubbleReady,
            Action onFirstToken,
            VisualElement thinkingBubble,
            Label thinkingText,
            Button sendButton,
            Button stopButton,
            ToolCallApprovalController approvalController,
            Action onStreamEnd,
            Action refreshAvatarMotionState)
        {
            _messagesList = messagesList;
            _scrollToBottom = scrollToBottom;
            _createMessageElement = createMessageElement;
            _applyTextCursor = applyTextCursor;
            _onBubbleReady = onBubbleReady;
            _onFirstToken = onFirstToken;
            _thinkingBubble = thinkingBubble;
            _thinkingText = thinkingText;
            _sendButton = sendButton;
            _stopButton = stopButton;
            _approvalController = approvalController;
            _onStreamEnd = onStreamEnd;
            _refreshAvatarMotionState = refreshAvatarMotionState;
        }

        internal void OnToolProgress(string tool, string label, string emoji, string status)
        {
            OnToolProgress(ToolProgressInfo.Create(tool, label, emoji, status));
        }

        internal void OnToolProgress(ToolProgressInfo info)
        {
            if (info == null)
                return;

            string tool = info.tool;
            string label = info.label;
            string status = info.status;

            if (_thinkingBubble != null && _thinkingText != null)
            {
                string displayText = string.IsNullOrEmpty(label) ? GetThinkingText(tool) : label;
                if (displayText.Length > 40)
                    displayText = displayText.Substring(0, 40) + "...";
                _thinkingText.text = displayText;
                _thinkingBubble.style.display = DisplayStyle.Flex;
            }

            // Insert after the current text segment so tool cards stay ordered between
            // preceding assistant text and subsequent tokens (Desktop flushes deltas first).
            bool insertedNewEntry = _approvalController != null && _approvalController.OnToolProgress(info, _label);
            if (insertedNewEntry)
                ResetStreamingSegment();
            _scrollToBottom?.Invoke();

            if (_approvalController != null && _approvalController.ShouldPromptForStreamingApproval(status))
            {
                var request = new ToolCallRequest
                {
                    id = !string.IsNullOrEmpty(info.toolId) ? info.toolId : Guid.NewGuid().ToString("N"),
                    toolName = tool ?? string.Empty,
                    description = !string.IsNullOrEmpty(label) ? label : tool,
                    parameters = new Dictionary<string, string>()
                };
                _ = _approvalController.HandleStreamingApprovalAsync(request);
            }
        }

        internal void ClearThinkingBubble()
        {
            if (_thinkingBubble != null)
                _thinkingBubble.style.display = DisplayStyle.None;
            if (_thinkingText != null)
                _thinkingText.text = string.Empty;
        }

        internal void SetSending(bool isSending)
        {
            IsSending = isSending;
            if (!isSending)
                _onStreamEnd?.Invoke();

            _sendButton?.SetEnabled(!isSending);
            if (_stopButton != null)
                _stopButton.style.display = isSending ? DisplayStyle.Flex : DisplayStyle.None;
            if (_sendButton != null)
                _sendButton.style.display = isSending ? DisplayStyle.None : DisplayStyle.Flex;

            _refreshAvatarMotionState?.Invoke();
        }

        private static string GetThinkingText(string tool)
        {
            if (string.IsNullOrWhiteSpace(tool))
                return LocalizationExtensions.Get("thinking.default", "Thinking...");

            string lower = tool.ToLowerInvariant();
            if (lower.Contains("terminal") || lower.Contains("bash") || lower.Contains("shell"))
                return LocalizationExtensions.Get("thinking.parsing", "Running...");
            if (lower.Contains("search") || lower.Contains("grep"))
                return LocalizationExtensions.Get("thinking.searching", "Searching...");
            if (lower.Contains("read"))
                return LocalizationExtensions.Get("thinking.reading", "Reading...");
            if (lower.Contains("write") || lower.Contains("edit"))
                return LocalizationExtensions.Get("thinking.writing", "Writing...");
            return LocalizationExtensions.Get("thinking.default", "Thinking...");
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

                _beam = bubble.Q<BubbleBeamElement>(className: "transcript__bubble-beam");
                if (_beam != null)
                    _beam.Play();
            }
            StartStatsUpdate();
            StartTypingAnimation();
            _scrollToBottom?.Invoke();
        }

        /// <summary>
        /// Seed a freshly-Begun streaming bubble with already-accumulated partial text, so that
        /// returning to a session still generating in the background shows its current text and then
        /// continues live (subsequent OnToken appends). Switches the bubble from typing dots to text.
        /// </summary>
        internal void Seed(string partialText)
        {
            if (_typingDots != null)
            {
                StopTypingAnimation();
                _typingDots.RemoveFromHierarchy();
                _typingDots = null;
                IsStreaming = true;
            }

            EnsureLabel();
            if (_label != null)
            {
                string text = partialText ?? string.Empty;
                _textBuffer.Length = 0;
                _textBuffer.Append(text);
                _segmentBuffer.Length = 0;
                _segmentBuffer.Append(text);
                _label.SetMarkdown(_segmentBuffer.ToString());
            }

            UpdateStats();
            _scrollToBottom?.Invoke();
        }

        /// <summary>
        /// Replay already-accumulated segments (text + tools, in order) through the live append path
        /// so re-attaching to a still-streaming session shows the earlier tool chips too — not just
        /// new ones. Routing through OnToken/OnToolProgress also registers tool entries so subsequent
        /// live updates for the same tool dedupe instead of duplicating.
        /// </summary>
        internal void Replay(IReadOnlyList<ChatMessageSegment> segments)
        {
            if (segments == null)
                return;
            for (int i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                if (seg == null)
                    continue;
                if (string.Equals(seg.kind, ChatMessageSegment.TextKind, StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(seg.text))
                        OnToken(seg.text);
                }
                else if (string.Equals(seg.kind, ChatMessageSegment.ToolKind, StringComparison.OrdinalIgnoreCase))
                {
                    ToolProgressInfo info = new ToolProgressInfo();
                    info.tool = seg.tool;
                    info.toolId = seg.toolId;
                    info.label = seg.label;
                    info.emoji = seg.emoji;
                    info.status = seg.status;
                    info.inlineDiff = seg.inlineDiff;
                    info.details = seg.details;
                    OnToolProgress(info);
                }
            }
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
            if (_beam != null)
            {
                _beam.Stop();
                _beam = null;
            }
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
            if (_beam != null)
            {
                _beam.Stop();
                _beam = null;
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

            // Insert label before the stats footer. The footer may be a direct
            // child or nested inside another container — walk direct children to
            // find the first element bearing the stats class.
            int insertIdx = -1;
            for (int i = 0; i < _bubble.childCount; i++)
            {
                if (_bubble[i].ClassListContains("transcript__stats"))
                {
                    insertIdx = i;
                    break;
                }
            }
            if (insertIdx >= 0)
            {
                _bubble.Insert(insertIdx, _label);
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
