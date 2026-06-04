using System;
using System.Collections.Generic;
using NeonCompanion.Runtime.Chat;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Localization;
using NeonCompanion.Runtime.UI.UITK;
using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK.Chat
{
    /// <summary>
    /// ScrollView population, bubble construction, context menus.
    /// Extracted from ChatController per docs/18_Refactoring_Oversized_Files.md section 1.9.
    /// </summary>
    internal class ChatMessageListRenderer
    {
        private readonly ScrollView _messagesList;
        private readonly MessageContextMenu _contextMenu;
        private readonly Func<string> _getAvatarDisplayName;
        private readonly Label _topbarSubtitle;
        private readonly Label _navChatCount;
        private readonly Action<string> _setChatSubtitle;
        private readonly Action<string> _onImageClick;
        private readonly Action _scrollToBottomCallback;
        private readonly Func<bool> _isSelecting;
        private readonly Func<int, bool> _isIndexSelected;
        private readonly Action<int> _toggleSelection;
        private readonly Action _onNewSessionRequested;

        internal VisualElement _transcriptContextRoot;
        private readonly Dictionary<string, VisualElement> _messageRowCache = new Dictionary<string, VisualElement>();

        private bool _pinBottomQueued;

        // Long-press state for mobile context menu
        private VisualElement _longPressTarget;
        private int _longPressIndex;
        private bool _longPressIsUser;
        private Vector2 _longPressPos;
        private IVisualElementScheduledItem _longPressSchedule;

        internal ChatMessageListRenderer(
            ScrollView messagesList,
            MessageContextMenu contextMenu,
            Func<string> getAvatarDisplayName,
            Label topbarSubtitle,
            Label navChatCount,
            Action<string> setChatSubtitle,
            Action<string> onImageClick,
            Action scrollToBottomCallback,
            Func<bool> isSelecting,
            Func<int, bool> isIndexSelected,
            Action<int> toggleSelection,
            Action onNewSessionRequested)
        {
            _messagesList = messagesList;
            _contextMenu = contextMenu;
            _getAvatarDisplayName = getAvatarDisplayName;
            _topbarSubtitle = topbarSubtitle;
            _navChatCount = navChatCount;
            _setChatSubtitle = setChatSubtitle;
            _onImageClick = onImageClick;
            _scrollToBottomCallback = scrollToBottomCallback;
            _isSelecting = isSelecting;
            _isIndexSelected = isIndexSelected;
            _toggleSelection = toggleSelection;
            _onNewSessionRequested = onNewSessionRequested;
        }

        internal void Render(IReadOnlyList<ChatMessage> messages)
        {
            int count = messages?.Count ?? 0;
            string avatarName = _getAvatarDisplayName?.Invoke() ?? string.Empty;
            string subtitle = string.IsNullOrEmpty(avatarName)
                ? ChatController.MessageCountText(count)
                : $"{ChatController.MessageCountText(count)} · {avatarName}";

            if (_topbarSubtitle != null)
                _topbarSubtitle.text = subtitle;

            if (_navChatCount != null)
                _navChatCount.text = count.ToString();

            _setChatSubtitle?.Invoke(subtitle);

            RenderTranscript(messages);
        }

        internal void ScrollToBottom()
        {
            var list = _messagesList;
            if (list == null)
                return;

            var content = list.contentContainer;
            if (content == null)
                return;

            list.schedule.Execute(PinTranscriptToBottom);
            list.schedule.Execute(PinTranscriptToBottom).StartingIn(50);
            list.schedule.Execute(PinTranscriptToBottom).StartingIn(150);
            list.schedule.Execute(PinTranscriptToBottom).StartingIn(300);

            if (_pinBottomQueued)
                return;

            _pinBottomQueued = true;
            content.RegisterCallback<GeometryChangedEvent>(OnTranscriptGeometryForScroll);
        }

        internal void ScrollToMessage(int messageIndex)
        {
            if (_messagesList == null || messageIndex < 0)
                return;

            VisualElement targetRow = null;
            foreach (var child in _messagesList.Children())
            {
                if (child.userData is int idx && idx == messageIndex)
                {
                    targetRow = child;
                    break;
                }
            }

            if (targetRow == null)
                return;

            try
            {
                _messagesList.ScrollTo(targetRow);
            }
            catch
            {
                var content = _messagesList.contentContainer;
                if (content != null)
                {
                    float y = targetRow.layout.y - 60f;
                    if (y < 0f) y = 0f;
                    _messagesList.scrollOffset = new Vector2(0f, y);
                }
            }
        }

        private void RenderTranscript(IReadOnlyList<ChatMessage> messages)
        {
            if (_messagesList == null)
                return;

            bool hasSession = messages != null;
            bool selecting = _isSelecting != null && _isSelecting();
            if (selecting)
                _messageRowCache.Clear();

            _messagesList.Clear();

            if (messages == null || messages.Count == 0)
            {
                _messageRowCache.Clear();
                _messagesList.Add(CreateEmptyTranscript(hasSession));
                return;
            }

            bool hasVisibleMessages = false;
            var nextCache = new Dictionary<string, VisualElement>();
            for (int i = 0; i < messages.Count; i++)
            {
                var message = messages[i];
                if (!HasRenderableMessageContent(message))
                    continue;

                string renderKey = BuildMessageRenderKey(i, message);
                VisualElement row = null;
                if (!selecting)
                    _messageRowCache.TryGetValue(renderKey, out row);
                if (row == null)
                    row = CreateMessageElement(message, _onImageClick, _scrollToBottomCallback);

                row.userData = i;
                var bubbleForTag = row.Q<VisualElement>(className: "transcript__bubble");
                if (bubbleForTag != null)
                    bubbleForTag.userData = i;

                int rowIndex = i;

                if (selecting)
                {
                    bool selected = _isIndexSelected != null && _isIndexSelected(rowIndex);
                    if (selected)
                        row.AddToClassList("transcript__row--selected");

                    bool isSystemRow = row.ClassListContains("transcript__row--system");
                    if (!isSystemRow)
                    {
                        row.RegisterCallback<ClickEvent>(_ =>
                        {
                            if (_toggleSelection != null)
                                _toggleSelection(rowIndex);
                        });
                    }
                }

                _messagesList.Add(row);
                if (!selecting)
                    nextCache[renderKey] = row;
                hasVisibleMessages = true;
            }

            _messageRowCache.Clear();
            foreach (var pair in nextCache)
                _messageRowCache[pair.Key] = pair.Value;

            if (!hasVisibleMessages)
            {
                _messageRowCache.Clear();
                _messagesList.Add(CreateEmptyTranscript(true));
                return;
            }

            ScrollToBottom();
        }

        private static string BuildMessageRenderKey(int index, ChatMessage message)
        {
            unchecked
            {
                int hash = 17;
                hash = AppendHash(hash, index);
                if (message != null)
                {
                    hash = AppendHash(hash, message.role);
                    hash = AppendHash(hash, message.content);
                    hash = AppendHash(hash, message.model);
                    hash = AppendHash(hash, message.reasoning);
                    hash = AppendHash(hash, message.unixTimeSeconds.GetHashCode());
                    hash = AppendHash(hash, message.tool_call_id);
                    hash = AppendHash(hash, message.tokenCount);
                    hash = AppendHash(hash, message.responseTimeSeconds.GetHashCode());

                    int attachmentCount = message.attachments != null ? message.attachments.Count : 0;
                    hash = AppendHash(hash, attachmentCount);
                    for (int i = 0; i < attachmentCount; i++)
                    {
                        ChatAttachment attachment = message.attachments[i];
                        if (attachment == null)
                            continue;
                        hash = AppendHash(hash, attachment.kind);
                        hash = AppendHash(hash, attachment.name);
                        hash = AppendHash(hash, attachment.path);
                        hash = AppendHash(hash, attachment.mediaType);
                    }

                    int segmentCount = message.segments != null ? message.segments.Count : 0;
                    hash = AppendHash(hash, segmentCount);
                    for (int i = 0; i < segmentCount; i++)
                    {
                        ChatMessageSegment segment = message.segments[i];
                        if (segment == null)
                            continue;
                        hash = AppendHash(hash, segment.kind);
                        hash = AppendHash(hash, segment.key);
                        hash = AppendHash(hash, segment.text);
                        hash = AppendHash(hash, segment.tool);
                        hash = AppendHash(hash, segment.label);
                        hash = AppendHash(hash, segment.emoji);
                        hash = AppendHash(hash, segment.status);
                        hash = AppendHash(hash, segment.inlineDiff);
                    }
                }
                return index.ToString() + ":" + hash.ToString();
            }
        }

        private static int AppendHash(int hash, string value)
        {
            unchecked
            {
                if (value == null)
                    return hash * 31;
                for (int i = 0; i < value.Length; i++)
                    hash = hash * 31 + value[i];
                return hash;
            }
        }

        private static int AppendHash(int hash, int value)
        {
            unchecked
            {
                return hash * 31 + value;
            }
        }

        private VisualElement CreateEmptyTranscript(bool hasSession)
        {
            var container = new VisualElement();
            container.AddToClassList("transcript__empty");

            string titleText = hasSession
                ? LocalizationExtensions.Get("chat.empty.title", "Пока нет сообщений")
                : LocalizationExtensions.Get("chat.empty.no_session.title", "Чат не создан");
            var title = new Label(titleText);
            title.AddToClassList("transcript__empty-title");

            string bodyText = hasSession
                ? LocalizationExtensions.Get("chat.empty.body", "Начни диалог ниже, и здесь появится полная история текущей сессии.")
                : LocalizationExtensions.Get("chat.empty.no_session.body", "Создай новый чат, чтобы начать диалог с активным провайдером.");
            var body = new Label(bodyText);
            body.AddToClassList("transcript__empty-body");

            container.Add(title);
            container.Add(body);

            if (!hasSession)
            {
                var createButton = new Button(() =>
                {
                    if (_onNewSessionRequested != null)
                        _onNewSessionRequested();
                })
                {
                    text = LocalizationExtensions.Get("chat.empty.create", "Создать новый чат")
                };
                createButton.AddToClassList("btn");
                createButton.AddToClassList("btn--primary");
                createButton.AddToClassList("transcript__empty-action");
                container.Add(createButton);
            }

            return container;
        }

        internal static VisualElement CreateMessageElement(ChatMessage message, Action<string> onImageClick = null, Action onImageLoaded = null)
        {
            if (message == null)
            {
                var dummy = new VisualElement();
                dummy.AddToClassList("transcript__row");
                return dummy;
            }

            string role = NormalizeRole(message.role);

            var row = new VisualElement();
            row.AddToClassList("transcript__row");
            row.AddToClassList($"transcript__row--{role}");

            var bubble = new VisualElement();
            bubble.AddToClassList("transcript__bubble");
            bubble.AddToClassList($"transcript__bubble--{role}");
            if (IsMarkdownHeavyMessage(message))
                bubble.AddToClassList("transcript__bubble--markdown");

            VisualElement actions = null;

            var meta = new VisualElement();
            meta.AddToClassList("transcript__meta");

            var roleLabel = new Label(DisplayRole(role));
            roleLabel.AddToClassList("transcript__role");

            Label modelLabel = null;
            if (role == "assistant" && !string.IsNullOrWhiteSpace(message.model))
            {
                modelLabel = new Label(message.model);
                modelLabel.style.fontSize = 11f;
                modelLabel.style.color = new Color(0.62f, 0.66f, 0.78f, 1f);
                modelLabel.style.marginLeft = 6f;
                modelLabel.style.paddingLeft = 6f;
                modelLabel.style.paddingRight = 6f;
                modelLabel.style.paddingTop = 1f;
                modelLabel.style.paddingBottom = 1f;
                modelLabel.style.backgroundColor = new Color(0.18f, 0.2f, 0.28f, 0.9f);
                modelLabel.style.borderTopLeftRadius = 8f;
                modelLabel.style.borderTopRightRadius = 8f;
                modelLabel.style.borderBottomLeftRadius = 8f;
                modelLabel.style.borderBottomRightRadius = 8f;
            }

            var timeLabel = new Label(FormatMessageTime(message.unixTimeSeconds));
            timeLabel.AddToClassList("transcript__time");

            meta.Add(roleLabel);
            if (modelLabel != null)
                meta.Add(modelLabel);
            meta.Add(timeLabel);
            bubble.Add(meta);

            if (!string.IsNullOrWhiteSpace(message.reasoning))
            {
                var reasoningRoot = new VisualElement();
                reasoningRoot.AddToClassList("tool-entry-root");

                var reasoningHeader = new VisualElement();
                reasoningHeader.AddToClassList("tool-entry");
                reasoningHeader.AddToClassList("tool-entry--header");
                reasoningHeader.AddToClassList("tool-entry--reasoning");

                var toggleLabel = new Label("▶");
                toggleLabel.AddToClassList("tool-entry__toggle");

                var iconLabel = new Label("💭");
                iconLabel.AddToClassList("tool-entry__icon");

                var nameLabel = new Label("Thinking");
                nameLabel.AddToClassList("tool-entry__name");

                reasoningHeader.Add(toggleLabel);
                reasoningHeader.Add(iconLabel);
                reasoningHeader.Add(nameLabel);

                var reasoningDetails = new VisualElement();
                reasoningDetails.AddToClassList("reasoning-entry__details");
                reasoningDetails.style.display = DisplayStyle.None;

                var reasoningText = new TextField();
                reasoningText.isReadOnly = true;
                reasoningText.multiline = true;
                reasoningText.value = message.reasoning;
                reasoningText.AddToClassList("reasoning-entry__text");
                reasoningDetails.Add(reasoningText);

                reasoningRoot.Add(reasoningHeader);
                reasoningRoot.Add(reasoningDetails);

                bool reasoningExpanded = false;
                reasoningHeader.RegisterCallback<ClickEvent>(evt =>
                {
                    reasoningExpanded = !reasoningExpanded;
                    toggleLabel.text = reasoningExpanded ? "▼" : "▶";
                    reasoningDetails.style.display = reasoningExpanded ? DisplayStyle.Flex : DisplayStyle.None;
                    evt.StopPropagation();
                });

                bubble.Add(reasoningRoot);
            }

            bool hasTextSegment = AddMessageSegments(bubble, message);
            if (!hasTextSegment && !string.IsNullOrWhiteSpace(message.content))
            {
                bool isAssistant = string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase);
                bubble.Add(CreateTranscriptBody(message.content, isAssistant));
            }

            if (message.attachments != null && message.attachments.Count > 0)
            {
                var attachmentWrap = new VisualElement();
                attachmentWrap.AddToClassList("transcript__attachments");

                for (int i = 0; i < message.attachments.Count; i++)
                {
                    var attachment = message.attachments[i];
                    if (attachment == null)
                        continue;

                    if (!string.IsNullOrEmpty(attachment.path) &&
                        (attachment.kind == "image" || ChatController.IsImageFile(attachment.path)))
                    {
                        var imageElement = new Image();
                        imageElement.AddToClassList("transcript__image");
                        imageElement.scaleMode = ScaleMode.ScaleToFit;
                        ChatController.LoadImageAsync(imageElement, attachment.path, onImageLoaded);
                        string imgPath = attachment.path;
                        if (onImageClick != null)
                        {
                            imageElement.RegisterCallback<ClickEvent>(evt =>
                            {
                                onImageClick(imgPath);
                                evt.StopPropagation();
                            });
                        }
                        attachmentWrap.Add(imageElement);
                    }
                    else
                    {
                        var attachmentLabel = new Label($"[file] {ChatController.GetAttachmentDisplayName(attachment)}");
                        attachmentLabel.AddToClassList("transcript__body");
                        attachmentLabel.style.fontSize = 11f;
                        attachmentLabel.focusable = true;
                        attachmentWrap.Add(attachmentLabel);
                    }
                }

                if (attachmentWrap.childCount > 0)
                    bubble.Add(attachmentWrap);
            }

            if (role == "assistant")
            {
                actions = new VisualElement();
                actions.AddToClassList("transcript__bubble-actions");

                var copyBtn = new Button();
                copyBtn.AddToClassList("iconbtn");
                copyBtn.AddToClassList("icon");
                copyBtn.AddToClassList("icon--copy");
                copyBtn.tooltip = "Копировать";
                ChatController.RegisterClickStatic(copyBtn, () => ChatController.OnCopyClickedStatic());

                var refreshBtn = new Button();
                refreshBtn.AddToClassList("iconbtn");
                refreshBtn.AddToClassList("icon");
                refreshBtn.AddToClassList("icon--refresh");
                refreshBtn.tooltip = "Пересоздать";
                ChatController.RegisterClickStatic(refreshBtn, () => ChatController.OnRegenerateClickedStatic());

                var listenBtn = new Button();
                listenBtn.AddToClassList("iconbtn");
                listenBtn.AddToClassList("icon");
                listenBtn.AddToClassList("icon--headphones");
                listenBtn.tooltip = "Озвучить";
                ChatController.RegisterClickStatic(listenBtn, () => ChatController.OnListenClickedStatic());

                actions.Add(copyBtn);
                actions.Add(refreshBtn);
                actions.Add(listenBtn);
            }

            if (actions != null)
                bubble.Add(actions);

            if (role == "assistant")
            {
                var statsFooter = new VisualElement();
                statsFooter.AddToClassList("transcript__stats");
                var statsLabel = new Label();
                statsLabel.AddToClassList("transcript__stats-label");
                statsFooter.Add(statsLabel);
                bubble.Add(statsFooter);

                if (message.tokenCount > 0)
                {
                    statsFooter.style.display = DisplayStyle.Flex;
                    double t = message.responseTimeSeconds > 0 ? message.responseTimeSeconds : 0.0;
                    string template = LocalizationExtensions.Get("chat.stats.footer", "~{0} tok · {1:F1}s");
                    string exact = template.Replace("~", string.Empty);
                    statsLabel.text = string.Format(exact, message.tokenCount, t);
                }
                else
                {
                    statsFooter.style.display = DisplayStyle.None;
                }
            }

            row.Add(bubble);

            return row;
        }

        private static bool AddMessageSegments(VisualElement bubble, ChatMessage message)
        {
            if (bubble == null || message == null || message.segments == null || message.segments.Count == 0)
                return false;

            var allText = new System.Text.StringBuilder();
            bool hasText = false;

            for (int i = 0; i < message.segments.Count; i++)
            {
                var segment = message.segments[i];
                if (segment == null)
                    continue;

                if (string.Equals(segment.kind, ChatMessageSegment.TextKind, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(segment.text))
                {
                    allText.Append(segment.text);
                    hasText = true;
                }
            }

            if (hasText)
            {
                bubble.Add(CreateTranscriptBody(allText.ToString(), true));
            }

            for (int i = 0; i < message.segments.Count; i++)
            {
                var segment = message.segments[i];
                if (segment == null) continue;
                if (string.Equals(segment.kind, ChatMessageSegment.ToolKind, StringComparison.OrdinalIgnoreCase))
                {
                    bubble.Add(ToolCallUiHelper.CreateEntryElement(segment.tool, segment.label, segment.emoji, segment.status, segment.inlineDiff));
                }
            }

            return hasText;
        }

        private static VisualElement CreateTranscriptBody(string text, bool isAssistant = false)
        {
            var bodyElement = new SelectableMarkdownElement();
            bodyElement.SetMarkdown(text ?? string.Empty);
            bodyElement.AddToClassList("transcript__body");
            bodyElement.style.minWidth = 0;
            bodyElement.style.width = Length.Percent(100);
            bodyElement.style.minHeight = 20;
            if (!isAssistant)
                bodyElement.AddToClassList("transcript__body--user");
            ChatController.ApplyTextCursor(bodyElement);
            return bodyElement;
        }

        private static bool IsMarkdownHeavyMessage(ChatMessage message)
        {
            if (message == null)
                return false;
            string text = message.content ?? string.Empty;
            return MarkdownRenderer.ContainsMarkdown(text);
        }

        internal static string NormalizeRole(string role)
        {
            if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
                return "user";

            if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase))
                return "system";

            if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
                return "tool";

            return "assistant";
        }

        internal static string DisplayRole(string role)
        {
            switch (role)
            {
                case "user":
                    return LocalizationExtensions.Get("chat.role.you", "Ты");
                case "system":
                    return LocalizationExtensions.Get("chat.role.system", "Система");
                case "tool":
                    return LocalizationExtensions.Get("chat.role.tool", "Инструмент");
                default:
                    return LocalizationExtensions.Get("chat.role.neon", "Neon");
            }
        }

        private static string FormatMessageTime(long unixTimeSeconds)
        {
            if (unixTimeSeconds <= 0)
                return string.Empty;

            return DateTimeOffset
                .FromUnixTimeSeconds(unixTimeSeconds)
                .ToLocalTime()
                .ToString("HH:mm");
        }

        private static bool HasRenderableMessageContent(ChatMessage message)
        {
            if (message == null)
                return false;
            if (!string.IsNullOrWhiteSpace(message.content))
                return true;
            if (message.segments != null)
            {
                for (int i = 0; i < message.segments.Count; i++)
                {
                    if (HasRenderableSegmentContent(message.segments[i]))
                        return true;
                }
            }
            if (message.attachments != null && message.attachments.Count > 0)
                return true;
            return false;
        }

        private static bool HasRenderableSegmentContent(ChatMessageSegment segment)
        {
            if (segment == null)
                return false;

            if (string.Equals(segment.kind, ChatMessageSegment.ToolKind, StringComparison.OrdinalIgnoreCase))
                return !string.IsNullOrWhiteSpace(segment.tool) || !string.IsNullOrWhiteSpace(segment.label);

            if (string.Equals(segment.kind, ChatMessageSegment.TextKind, StringComparison.OrdinalIgnoreCase))
                return !string.IsNullOrWhiteSpace(segment.text);

            return false;
        }

        internal void RegisterCallbacks()
        {
            if (_messagesList == null)
                return;

            _messagesList.RegisterCallback<PointerDownEvent>(OnTranscriptPointerDown, TrickleDown.TrickleDown);
            _messagesList.RegisterCallback<ContextualMenuPopulateEvent>(OnTranscriptContextMenuPopulate, TrickleDown.TrickleDown);
            _messagesList.RegisterCallback<PointerUpEvent>(OnTranscriptPointerUp);
            _messagesList.RegisterCallback<PointerCancelEvent>(OnTranscriptPointerCancel);

            _transcriptContextRoot = ChatController.GetDocumentRoot(_messagesList);
            if (_transcriptContextRoot != null)
            {
                _transcriptContextRoot.RegisterCallback<MouseDownEvent>(OnTranscriptRootMouseDown, TrickleDown.TrickleDown);
                if (_transcriptContextRoot != _messagesList)
                    _transcriptContextRoot.RegisterCallback<ContextualMenuPopulateEvent>(OnTranscriptContextMenuPopulate, TrickleDown.TrickleDown);
            }
        }

        internal void UnregisterCallbacks()
        {
            if (_messagesList != null)
            {
                _messagesList.UnregisterCallback<PointerDownEvent>(OnTranscriptPointerDown, TrickleDown.TrickleDown);
                _messagesList.UnregisterCallback<ContextualMenuPopulateEvent>(OnTranscriptContextMenuPopulate, TrickleDown.TrickleDown);
                _messagesList.UnregisterCallback<PointerUpEvent>(OnTranscriptPointerUp);
                _messagesList.UnregisterCallback<PointerCancelEvent>(OnTranscriptPointerCancel);
            }

            if (_transcriptContextRoot != null && _transcriptContextRoot != _messagesList)
            {
                _transcriptContextRoot.UnregisterCallback<ContextualMenuPopulateEvent>(OnTranscriptContextMenuPopulate, TrickleDown.TrickleDown);
            }
            if (_transcriptContextRoot != null)
            {
                _transcriptContextRoot.UnregisterCallback<MouseDownEvent>(OnTranscriptRootMouseDown, TrickleDown.TrickleDown);
                _transcriptContextRoot = null;
            }

            if (_pinBottomQueued)
            {
                _pinBottomQueued = false;
                _messagesList?.contentContainer?.UnregisterCallback<GeometryChangedEvent>(OnTranscriptGeometryForScroll);
            }

            CancelLongPress();
        }

        private void OnTranscriptRootMouseDown(MouseDownEvent evt)
        {
            if (_messagesList == null || evt == null)
                return;

            bool isContextButton = evt.button != 0 || (evt.pressedButtons & 2) != 0;
            if (!isContextButton)
                return;

            VisualElement target = evt.target as VisualElement;
            Vector2 pos = evt.mousePosition;
            bool insideByTarget = IsInsideMessagesList(target);
            bool insideByPos = _messagesList.worldBound.Contains(pos);
            if (!insideByTarget && !insideByPos)
                return;

            evt.StopImmediatePropagation();
            evt.StopPropagation();
#pragma warning disable CS0618
            evt.PreventDefault();
#pragma warning restore CS0618

            if (_isSelecting != null && _isSelecting())
                return;

            VisualElement bubble = ResolveBubbleFromEvent(target, pos);
            if (bubble == null)
                return;

            if (bubble.ClassListContains("transcript__bubble--system"))
                return;

            int? msgIndex = GetMessageIndexFromElement(bubble);
            if (msgIndex == null)
                return;

            bool isUser = bubble.ClassListContains("transcript__bubble--user");
            ShowMessageContextMenu(bubble, msgIndex.Value, isUser, pos);
        }

        private void OnTranscriptPointerDown(PointerDownEvent evt)
        {
            if (_messagesList == null)
                return;

            Vector2 pos = evt.position;
            bool isContextButton = evt.button != 0 || (evt.pressedButtons & 2) != 0;

            if (isContextButton)
            {
                evt.StopImmediatePropagation();
                evt.StopPropagation();
#pragma warning disable CS0618
                evt.PreventDefault();
#pragma warning restore CS0618

                if (_isSelecting != null && _isSelecting())
                    return;
                return;
            }

            if (evt.button == 0 && IsInsideSelectableTextField(evt.target as VisualElement))
                return;

            VisualElement bubble = ResolveBubbleFromEvent(evt.target as VisualElement, pos);
            if (bubble == null)
                return;

            if (bubble.ClassListContains("transcript__bubble--system"))
                return;

            int? msgIndex = GetMessageIndexFromElement(bubble);
            if (msgIndex == null)
                return;

            bool isUser = bubble.ClassListContains("transcript__bubble--user");

            if (_isSelecting != null && _isSelecting())
            {
                return;
            }

            if (evt.button == 0)
            {
                bool longPressSelect = string.Equals(evt.pointerType, "mouse", StringComparison.OrdinalIgnoreCase);

                _longPressTarget = bubble;
                _longPressIndex = msgIndex.Value;
                _longPressIsUser = isUser;
                _longPressPos = pos;

                if (_longPressSchedule != null)
                {
                    _longPressSchedule.Pause();
                    _longPressSchedule = null;
                }

                _longPressSchedule = bubble.schedule.Execute(() =>
                {
                    _longPressSchedule = null;
                    if (_longPressTarget != null)
                    {
                        if (longPressSelect)
                        {
                            if (_toggleSelection != null)
                                _toggleSelection(_longPressIndex);
                        }
                        else
                        {
                            ShowMessageContextMenu(_longPressTarget, _longPressIndex, _longPressIsUser, _longPressPos);
                        }
                    }
                    _longPressTarget = null;
                }).StartingIn(480);
            }
        }

        private void OnTranscriptContextMenuPopulate(ContextualMenuPopulateEvent evt)
        {
            if (_messagesList == null || evt == null)
                return;

            var target = evt.target as VisualElement;
            Vector2 triggerPos;
            bool hasTriggerPos = TryGetEventPosition(evt.triggerEvent, out triggerPos);
            bool insideByTarget = IsInsideMessagesList(target);
            bool insideByPos = hasTriggerPos && _messagesList.worldBound.Contains(triggerPos);
            if (!insideByTarget && !insideByPos)
                return;

            evt.StopImmediatePropagation();
            evt.StopPropagation();
#pragma warning disable CS0618
            evt.PreventDefault();
#pragma warning restore CS0618
        }

        private void OnTranscriptPointerUp(PointerUpEvent evt)
        {
            CancelLongPress();
        }

        private void OnTranscriptPointerCancel(PointerCancelEvent evt)
        {
            CancelLongPress();
        }

        private void CancelLongPress()
        {
            if (_longPressSchedule != null)
            {
                _longPressSchedule.Pause();
                _longPressSchedule = null;
            }
            _longPressTarget = null;
        }

        private static VisualElement FindBubbleAncestor(VisualElement el)
        {
            while (el != null)
            {
                if (el.ClassListContains("transcript__bubble"))
                    return el;
                el = el.parent;
            }
            return null;
        }

        private bool TryGetEventPosition(EventBase eventBase, out Vector2 position)
        {
            if (eventBase is MouseDownEvent)
            {
                position = ((MouseDownEvent)eventBase).mousePosition;
                return true;
            }

            if (eventBase is MouseUpEvent)
            {
                position = ((MouseUpEvent)eventBase).mousePosition;
                return true;
            }

            if (eventBase is PointerDownEvent)
            {
                position = ((PointerDownEvent)eventBase).position;
                return true;
            }

            if (eventBase is PointerUpEvent)
            {
                position = ((PointerUpEvent)eventBase).position;
                return true;
            }

            position = Vector2.zero;
            return false;
        }

        private bool IsInsideMessagesList(VisualElement element)
        {
            if (_messagesList == null || element == null)
                return false;

            var current = element;
            while (current != null)
            {
                if (current == _messagesList)
                    return true;
                current = current.parent;
            }

            return false;
        }

        private static bool IsInsideSelectableTextField(VisualElement el)
        {
            while (el != null)
            {
                if (el is SelectableMarkdownElement)
                    return true;
                if (el is TextField && el.ClassListContains("transcript__body"))
                    return true;
                el = el.parent;
            }
            return false;
        }

        private VisualElement ResolveBubbleFromEvent(VisualElement target, Vector2 panelPosition)
        {
            var bubble = FindBubbleAncestor(target);
            if (bubble != null)
                return bubble;

            if (_messagesList == null)
                return null;

            foreach (var child in _messagesList.Children())
            {
                var row = child as VisualElement;
                if (row == null)
                    continue;

                var candidate = row.Q<VisualElement>(className: "transcript__bubble");
                if (candidate != null && candidate.worldBound.Contains(panelPosition))
                    return candidate;
            }

            return null;
        }

        private static int? GetMessageIndexFromElement(VisualElement el)
        {
            while (el != null)
            {
                if (el.userData is int)
                    return (int)el.userData;
                el = el.parent;
            }
            return null;
        }

        private void ShowMessageContextMenu(VisualElement target, int messageIndex, bool isUser, Vector2 position)
        {
            if (_contextMenu == null)
                return;

            _contextMenu.Hide();
            _contextMenu.ShowAt(target, messageIndex, isUser, position);
        }

        private void OnTranscriptGeometryForScroll(GeometryChangedEvent evt)
        {
            _pinBottomQueued = false;
            _messagesList?.contentContainer?.UnregisterCallback<GeometryChangedEvent>(OnTranscriptGeometryForScroll);
            PinTranscriptToBottom();
        }

        private void PinTranscriptToBottom()
        {
            var list = _messagesList;
            var content = list?.contentContainer;
            var viewport = list?.contentViewport;
            if (content == null || viewport == null || content.childCount == 0)
                return;

            float viewportHeight = viewport.layout.height;
            float contentHeight = content.layout.height;
            if (viewportHeight <= 0f || contentHeight <= 0f)
                return;

            float maxScroll = Mathf.Max(0f, contentHeight - viewportHeight);
            Vector2 scrollOffset = list.scrollOffset;
            list.scrollOffset = new Vector2(scrollOffset.x, maxScroll);
            // note: hide scroll btn is done in controller via _d
        }
    }
}
