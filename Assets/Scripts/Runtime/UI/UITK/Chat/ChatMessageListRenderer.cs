using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Chat;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Localization;
using NeonCompanion.Runtime.UI.UITK;
using NeonCompanion.Runtime.Voice;
using UnityEngine;
using UnityEngine.Networking;
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
        private readonly ChatMessageEditController _messageEditController;
        private readonly Func<string> _getAvatarDisplayName;
        private readonly Label _topbarSubtitle;
        private readonly Label _navChatCount;
        private readonly Action<string> _setChatSubtitle;
        private readonly Action _scrollToBottomCallback;
        private readonly Func<bool> _isSelecting;
        private readonly Func<int, bool> _isIndexSelected;
        private readonly Action<int> _toggleSelection;
        private readonly Action _onNewSessionRequested;
        private readonly Action<string> _toggleAudioFile;
        private readonly Action<string, float> _seekAudioFile;
        private readonly Func<string, VoicePlaybackState> _getAudioPlaybackState;

        internal VisualElement _transcriptContextRoot;
        private readonly Dictionary<string, VisualElement> _messageRowCache = new Dictionary<string, VisualElement>();
        private VisualElement _lightbox;

        private bool _pinBottomQueued;

        // Long-press state for mobile context menu
        private VisualElement _longPressTarget;
        private int _longPressIndex;
        private bool _longPressIsUser;
        private Vector2 _longPressPos;
        private IVisualElementScheduledItem _longPressSchedule;

        internal ChatMessageListRenderer(
            ScrollView messagesList,
            ChatMessageEditController messageEditController,
            Func<string> getAvatarDisplayName,
            Label topbarSubtitle,
            Label navChatCount,
            Action<string> setChatSubtitle,
            Action scrollToBottomCallback,
            Func<bool> isSelecting,
            Func<int, bool> isIndexSelected,
            Action<int> toggleSelection,
            Action onNewSessionRequested,
            Action<string> toggleAudioFile = null,
            Action<string, float> seekAudioFile = null,
            Func<string, VoicePlaybackState> getAudioPlaybackState = null)
        {
            _messagesList = messagesList;
            _messageEditController = messageEditController;
            _getAvatarDisplayName = getAvatarDisplayName;
            _topbarSubtitle = topbarSubtitle;
            _navChatCount = navChatCount;
            _setChatSubtitle = setChatSubtitle;
            _scrollToBottomCallback = scrollToBottomCallback;
            _isSelecting = isSelecting;
            _isIndexSelected = isIndexSelected;
            _toggleSelection = toggleSelection;
            _onNewSessionRequested = onNewSessionRequested;
            _toggleAudioFile = toggleAudioFile;
            _seekAudioFile = seekAudioFile;
            _getAudioPlaybackState = getAudioPlaybackState;
        }

        internal void Render(IReadOnlyList<ChatMessage> messages)
        {
            int count = messages?.Count ?? 0;
            string avatarName = _getAvatarDisplayName?.Invoke() ?? string.Empty;
            string subtitle = string.IsNullOrEmpty(avatarName)
                ? ChatAttachmentManager.MessageCountText(count)
                : $"{ChatAttachmentManager.MessageCountText(count)} · {avatarName}";

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

        internal void ShowImageLightbox(string imagePath)
        {
            if (string.IsNullOrEmpty(imagePath))
                return;

            VisualElement root = GetOverlayRoot();
            if (root == null)
                return;

            HideLightbox();

            _lightbox = new VisualElement();
            _lightbox.name = "image-lightbox";
            _lightbox.AddToClassList("lightbox");
            _lightbox.focusable = true;
            _lightbox.pickingMode = PickingMode.Position;
            ApplyFullscreenOverlayLayout(_lightbox);

            _lightbox.RegisterCallback<ClickEvent>(evt =>
            {
                if (evt.target == _lightbox)
                {
                    HideLightbox();
                    evt.StopPropagation();
                }
            });

            _lightbox.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Escape)
                {
                    HideLightbox();
                    evt.StopPropagation();
                }
            });

            var image = new Image();
            image.AddToClassList("lightbox__image");
            image.scaleMode = ScaleMode.ScaleToFit;
            ApplyLightboxImageLayout(image);
            image.RegisterCallback<ClickEvent>(evt => evt.StopPropagation());
            LoadImageAsync(image, imagePath);
            _lightbox.Add(image);

            var closeButton = new Button(HideLightbox);
            closeButton.text = "\u00d7";
            closeButton.AddToClassList("lightbox__close");
            ApplyLightboxCloseLayout(closeButton);
            _lightbox.Add(closeButton);

            root.Add(_lightbox);
            _lightbox.BringToFront();
            _lightbox.schedule.Execute(() => _lightbox?.Focus()).StartingIn(50);
        }

        internal void HideLightbox()
        {
            if (_lightbox == null)
                return;
            _lightbox.RemoveFromHierarchy();
            _lightbox = null;
        }

        private VisualElement GetOverlayRoot()
        {
            if (_messagesList != null && _messagesList.panel != null)
                return _messagesList.panel.visualTree;
            if (_transcriptContextRoot != null && _transcriptContextRoot.panel != null)
                return _transcriptContextRoot.panel.visualTree;
            return null;
        }

        internal static async void LoadImageAsync(Image imageElement, string path, Action onLoaded = null)
        {
            if (imageElement == null || string.IsNullOrEmpty(path))
                return;

            try
            {
                string url = "file://" + path;
                using (var request = UnityWebRequestTexture.GetTexture(url))
                {
                    var operation = request.SendWebRequest();
                    while (!operation.isDone)
                        await Task.Yield();

                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        var download = request.downloadHandler as DownloadHandlerTexture;
                        if (download != null)
                        {
                            imageElement.image = download.texture;
                            onLoaded?.Invoke();
                        }
                    }
                }
            }
            catch
            {
                // Silent fail: image simply does not render.
            }
        }

        internal static void RegisterClick(Button button, Action handler)
        {
            if (button != null)
                button.clicked += handler;
        }

        internal static void UnregisterClick(Button button, Action handler)
        {
            if (button != null)
                button.clicked -= handler;
        }

        private static void ApplyFullscreenOverlayLayout(VisualElement overlay)
        {
            if (overlay == null)
                return;

            overlay.style.position = Position.Absolute;
            overlay.style.left = 0;
            overlay.style.right = 0;
            overlay.style.top = 0;
            overlay.style.bottom = 0;
            overlay.style.backgroundColor = new StyleColor(new Color(0f, 0f, 0f, 0.87f));
            overlay.style.alignItems = Align.Center;
            overlay.style.justifyContent = Justify.Center;
        }

        private static void ApplyLightboxImageLayout(Image image)
        {
            if (image == null)
                return;

            image.style.width = Length.Percent(80f);
            image.style.height = Length.Percent(80f);
            image.style.borderTopLeftRadius = 8f;
            image.style.borderTopRightRadius = 8f;
            image.style.borderBottomLeftRadius = 8f;
            image.style.borderBottomRightRadius = 8f;
            image.style.borderTopWidth = 1f;
            image.style.borderRightWidth = 1f;
            image.style.borderBottomWidth = 1f;
            image.style.borderLeftWidth = 1f;
            image.style.borderTopColor = new StyleColor(new Color(1f, 1f, 1f, 0.12f));
            image.style.borderRightColor = new StyleColor(new Color(1f, 1f, 1f, 0.12f));
            image.style.borderBottomColor = new StyleColor(new Color(1f, 1f, 1f, 0.12f));
            image.style.borderLeftColor = new StyleColor(new Color(1f, 1f, 1f, 0.12f));
        }

        private static void ApplyLightboxCloseLayout(Button closeButton)
        {
            if (closeButton == null)
                return;

            closeButton.style.position = Position.Absolute;
            closeButton.style.top = 16f;
            closeButton.style.right = 16f;
            closeButton.style.width = 36f;
            closeButton.style.height = 36f;
            closeButton.style.minWidth = 36f;
            closeButton.style.minHeight = 36f;
            closeButton.style.borderTopLeftRadius = 18f;
            closeButton.style.borderTopRightRadius = 18f;
            closeButton.style.borderBottomLeftRadius = 18f;
            closeButton.style.borderBottomRightRadius = 18f;
            closeButton.style.paddingLeft = 0f;
            closeButton.style.paddingRight = 0f;
            closeButton.style.paddingTop = 0f;
            closeButton.style.paddingBottom = 0f;
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
                    row = CreateMessageElement(
                        message,
                        ShowImageLightbox,
                        _scrollToBottomCallback,
                        _toggleAudioFile,
                        _seekAudioFile,
                        _getAudioPlaybackState);

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
                    hash = AppendHash(hash, message.audioPath);
                    hash = AppendHash(hash, message.voiceOutputBusy ? 1 : 0);

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
                        hash = AppendHash(hash, segment.details);
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

        internal static VisualElement CreateMessageElement(
            ChatMessage message,
            Action<string> onImageClick = null,
            Action onImageLoaded = null,
            Action<string> onAudioToggle = null,
            Action<string, float> onAudioSeek = null,
            Func<string, VoicePlaybackState> getAudioPlaybackState = null)
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
            string renderableContent = StripDataUrlFromContent(message.content);
            if (!hasTextSegment && !string.IsNullOrWhiteSpace(renderableContent))
            {
                bool isAssistant = string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase);
                bubble.Add(CreateTranscriptBody(renderableContent, isAssistant));
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
                        (attachment.kind == "image" || ChatAttachmentManager.IsImageFile(attachment.path)))
                    {
                        var imageElement = new Image();
                        imageElement.AddToClassList("transcript__image");
                        imageElement.scaleMode = ScaleMode.ScaleToFit;
                        LoadImageAsync(imageElement, attachment.path, onImageLoaded);
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
                        var attachmentLabel = new Label($"[file] {ChatAttachmentManager.GetAttachmentDisplayName(attachment)}");
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
                actions.RegisterCallback<PointerDownEvent>(StopBubbleActionPointerDown);
                actions.RegisterCallback<PointerUpEvent>(StopBubbleActionPointerUp);

                var copyBtn = new Button();
                copyBtn.AddToClassList("iconbtn");
                copyBtn.AddToClassList("icon");
                copyBtn.AddToClassList("icon--copy");
                copyBtn.tooltip = LocalizationExtensions.Get("tooltip.copy", "Copy");
                string messageCopyText = BuildMessageCopyText(message);
                RegisterClick(copyBtn, () => ChatController.OnCopyMessageClickedStatic(messageCopyText));

                var refreshBtn = new Button();
                refreshBtn.AddToClassList("iconbtn");
                refreshBtn.AddToClassList("icon");
                refreshBtn.AddToClassList("icon--refresh");
                refreshBtn.tooltip = LocalizationExtensions.Get("tooltip.regenerate", "Regenerate");
                RegisterClick(refreshBtn, () => ChatController.OnRegenerateClickedStatic());

                var listenBtn = new Button();
                listenBtn.AddToClassList("iconbtn");
                listenBtn.AddToClassList("icon");
                if (message.voiceOutputBusy)
                {
                    actions.AddToClassList("transcript__bubble-actions--busy");
                    listenBtn.AddToClassList("voice-output-button--busy");
                    listenBtn.tooltip = LocalizationExtensions.Get(
                        "voice.output.processing",
                        "Preparing audio...");
                    int loadingFrame = 0;
                    listenBtn.text = ".";
                    listenBtn.schedule.Execute(() =>
                    {
                        loadingFrame = (loadingFrame % 3) + 1;
                        listenBtn.text = new string('.', loadingFrame);
                    }).Every(350);
                }
                else
                {
                    listenBtn.AddToClassList("icon--headphones");
                    listenBtn.tooltip = LocalizationExtensions.Get("tooltip.listen", "Speak last response");
                }
                RegisterClick(listenBtn, () => ChatController.OnListenMessageClickedStatic(message));

                actions.Add(copyBtn);
                actions.Add(refreshBtn);
                actions.Add(listenBtn);
            }

            if (role == "assistant")
            {
                var statsFooter = new VisualElement();
                statsFooter.AddToClassList("transcript__stats");
                var statsLabel = new Label();
                statsLabel.AddToClassList("transcript__stats-label");
                statsFooter.Add(statsLabel);
                bubble.Add(statsFooter);

                if (message.tokenCount > 0 || message.responseTimeSeconds > 0)
                {
                    statsFooter.style.display = DisplayStyle.Flex;
                    double t = message.responseTimeSeconds > 0 ? message.responseTimeSeconds : 0.0;
                    string template = LocalizationExtensions.Get("chat.stats.footer", "~{0} tok · {1:F1}s");
                    if (message.tokenCount > 0)
                    {
                        string exact = template.Replace("~", string.Empty);
                        statsLabel.text = string.Format(exact, message.tokenCount, t);
                    }
                    else
                    {
                        statsLabel.text = string.Format("{0:F1}s", t);
                    }
                }
                else
                {
                    statsFooter.style.display = DisplayStyle.None;
                }
            }

            // Voice bubble — user messages with a recorded WAV, or assistant messages with a
            // synthesized TTS clip. Both use the same seekable cached-file player.
            if ((role == "user" || role == "assistant") && !string.IsNullOrEmpty(message.audioPath)
                && System.IO.File.Exists(message.audioPath))
            {
                string capturedPath = message.audioPath;
                float  capturedDuration = message.audioDurationSecs;

                var voiceBubble = new VisualElement();
                voiceBubble.AddToClassList("voice-bubble");
                // Don't let a tap on the play button bubble up into message selection.
                voiceBubble.RegisterCallback<PointerDownEvent>(StopBubbleActionPointerDown);
                voiceBubble.RegisterCallback<PointerUpEvent>(StopBubbleActionPointerUp);

                var playBtn = new Button();
                playBtn.AddToClassList("voice-bubble__play");
                playBtn.text = "▶";
                playBtn.tooltip = LocalizationExtensions.Get("voice.preview.play", "Play");
                RegisterClick(playBtn, () =>
                {
                    if (onAudioToggle != null)
                        onAudioToggle(capturedPath);
                });
                voiceBubble.Add(playBtn);

                var timeline = new VisualElement();
                timeline.AddToClassList("voice-bubble__timeline");

                var track = new VisualElement();
                track.AddToClassList("voice-bubble__track");
                track.tooltip = LocalizationExtensions.Get("voice.playback.seek", "Seek audio");
                var trackLine = new VisualElement();
                trackLine.AddToClassList("voice-bubble__track-line");
                var progressFill = new VisualElement();
                progressFill.AddToClassList("voice-bubble__progress");
                trackLine.Add(progressFill);
                track.Add(trackLine);
                timeline.Add(track);

                var timeRow = new VisualElement();
                timeRow.AddToClassList("voice-bubble__time-row");
                var elapsedLabel = new Label("0:00");
                elapsedLabel.AddToClassList("voice-bubble__time");
                var durationLabel = new Label(FormatAudioTime(capturedDuration));
                durationLabel.AddToClassList("voice-bubble__time");
                timeRow.Add(elapsedLabel);
                timeRow.Add(durationLabel);
                timeline.Add(timeRow);
                voiceBubble.Add(timeline);

                int seekPointerId = -1;
                Action<Vector2> seekToPointer = panelPosition =>
                {
                    if (onAudioSeek == null || track.contentRect.width <= 0f)
                        return;
                    Vector2 local = track.WorldToLocal(panelPosition);
                    onAudioSeek(capturedPath, Mathf.Clamp01(local.x / track.contentRect.width));
                };
                track.RegisterCallback<PointerDownEvent>(evt =>
                {
                    seekPointerId = evt.pointerId;
                    track.CapturePointer(evt.pointerId);
                    seekToPointer(evt.position);
                    evt.StopPropagation();
                });
                track.RegisterCallback<PointerMoveEvent>(evt =>
                {
                    if (seekPointerId != evt.pointerId || !track.HasPointerCapture(evt.pointerId))
                        return;
                    seekToPointer(evt.position);
                    evt.StopPropagation();
                });
                track.RegisterCallback<PointerUpEvent>(evt =>
                {
                    if (seekPointerId != evt.pointerId)
                        return;
                    seekToPointer(evt.position);
                    if (track.HasPointerCapture(evt.pointerId))
                        track.ReleasePointer(evt.pointerId);
                    seekPointerId = -1;
                    evt.StopPropagation();
                });
                track.RegisterCallback<PointerCaptureOutEvent>(_ => seekPointerId = -1);

                voiceBubble.schedule.Execute(() =>
                {
                    VoicePlaybackState state = getAudioPlaybackState != null
                        ? getAudioPlaybackState(capturedPath)
                        : new VoicePlaybackState();
                    float duration = state.IsCurrent && state.DurationSecs > 0f
                        ? state.DurationSecs
                        : capturedDuration;
                    float position = state.IsCurrent ? state.PositionSecs : 0f;
                    float progress = duration > 0f ? Mathf.Clamp01(position / duration) : 0f;

                    progressFill.style.width = Length.Percent(progress * 100f);
                    elapsedLabel.text = FormatAudioTime(position);
                    durationLabel.text = FormatAudioTime(duration);
                    playBtn.text = state.IsLoading ? "…" : (state.IsPlaying ? "⏸" : "▶");
                    playBtn.tooltip = state.IsPlaying
                        ? LocalizationExtensions.Get("voice.preview.pause", "Pause")
                        : LocalizationExtensions.Get("voice.preview.play", "Play");
                    voiceBubble.EnableInClassList("voice-bubble--playing", state.IsPlaying);
                    voiceBubble.EnableInClassList("voice-bubble--paused", state.IsPaused);
                }).Every(50);

                bubble.Add(voiceBubble);
            }

            if (actions != null)
                bubble.Add(actions);

            row.Add(bubble);

            return row;
        }

        private static string FormatAudioTime(float seconds)
        {
            float safeSeconds = Mathf.Max(0f, seconds);
            int totalSeconds = Mathf.FloorToInt(safeSeconds);
            return (totalSeconds / 60) + ":" + (totalSeconds % 60).ToString("D2");
        }

        internal static string BuildMessageCopyText(ChatMessage message)
        {
            if (message == null)
                return string.Empty;

            if (message.segments != null && message.segments.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < message.segments.Count; i++)
                {
                    var segment = message.segments[i];
                    if (segment == null)
                        continue;

                    if (string.Equals(segment.kind, ChatMessageSegment.TextKind, StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(segment.text))
                    {
                        sb.Append(segment.text);
                    }
                }

                string segmentedText = sb.ToString().Trim();
                if (!string.IsNullOrEmpty(segmentedText))
                    return segmentedText;
            }

            return message.content ?? string.Empty;
        }

        private static bool AddMessageSegments(VisualElement bubble, ChatMessage message)
        {
            if (bubble == null || message == null || message.segments == null || message.segments.Count == 0)
                return false;

            var segments = message.segments;
            bool hasText = false;
            int i = 0;
            while (i < segments.Count)
            {
                var segment = segments[i];
                if (segment == null)
                {
                    i++;
                    continue;
                }

                if (string.Equals(segment.kind, ChatMessageSegment.TextKind, StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(segment.text))
                    {
                        bubble.Add(CreateTranscriptBody(segment.text, true));
                        hasText = true;
                    }
                    i++;
                    continue;
                }

                if (string.Equals(segment.kind, ChatMessageSegment.ToolKind, StringComparison.OrdinalIgnoreCase))
                {
                    // Collect a run of consecutive tool calls with the same tool name and collapse
                    // them into one grouped chip ("tool ×N") so long agent turns don't pile up.
                    var run = new System.Collections.Generic.List<ChatMessageSegment>();
                    run.Add(segment);
                    int j = i + 1;
                    while (j < segments.Count)
                    {
                        var next = segments[j];
                        if (next == null)
                        {
                            j++;
                            continue;
                        }
                        if (!string.Equals(next.kind, ChatMessageSegment.ToolKind, StringComparison.OrdinalIgnoreCase))
                            break;
                        if (!string.Equals(next.tool ?? string.Empty, segment.tool ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                            break;
                        run.Add(next);
                        j++;
                    }

                    if (run.Count == 1)
                        bubble.Add(ToolCallUiHelper.CreateEntryElement(segment.tool, segment.label, segment.emoji, segment.status, segment.inlineDiff, segment.details));
                    else
                        bubble.Add(ToolCallUiHelper.CreateGroupedEntryElement(segment.tool, run));

                    i = j;
                    continue;
                }

                i++;
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
            ApplyTextCursor(bodyElement);
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
                return !string.IsNullOrWhiteSpace(segment.tool) ||
                       !string.IsNullOrWhiteSpace(segment.label) ||
                       !string.IsNullOrWhiteSpace(segment.details) ||
                       !string.IsNullOrWhiteSpace(segment.inlineDiff);

            if (string.Equals(segment.kind, ChatMessageSegment.TextKind, StringComparison.OrdinalIgnoreCase))
                return !string.IsNullOrWhiteSpace(segment.text);

            return false;
        }

        /// <summary>
        /// Strip data:image/...;base64,... URLs from message content so they don't render as raw text.
        /// Preserves any text before/after the data URL.
        /// </summary>
        private static string StripDataUrlFromContent(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return content;

            const string marker = "data:image/";
            int idx = content.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return content;

            // Find the start of the data URL (may be preceded by a space or newline)
            int start = idx;
            while (start > 0 && content[start - 1] == ' ')
                start--;

            // Find the end of the base64 payload (terminated by a space, newline, or end of string)
            int end = content.IndexOf(",base64,", idx, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
                end = idx;
            else
            {
                // Skip past the base64 data until whitespace or end
                end = content.IndexOf(',', end + 8); // skip ",base64,"
                if (end < 0) end = content.Length;
                end++; // include the comma
                while (end < content.Length && content[end] != ' ' && content[end] != '\n' && content[end] != '\r')
                    end++;
            }

            string cleaned = content.Remove(start, end - start).Trim();
            return cleaned;
        }

        internal void RegisterCallbacks()
        {
            if (_messagesList == null)
                return;

            _messagesList.RegisterCallback<PointerDownEvent>(OnTranscriptPointerDown, TrickleDown.TrickleDown);
            _messagesList.RegisterCallback<ContextualMenuPopulateEvent>(OnTranscriptContextMenuPopulate, TrickleDown.TrickleDown);
            // Capture phase (TrickleDown): the ScrollView/tool rows capture the pointer while
            // scrolling, so a bubble-phase move handler may never fire. In capture phase the
            // list sees every move first and can cancel the pending long-press.
            _messagesList.RegisterCallback<PointerMoveEvent>(OnTranscriptPointerMove, TrickleDown.TrickleDown);
            _messagesList.RegisterCallback<PointerUpEvent>(OnTranscriptPointerUp);
            // Any actual scroll cancels a pending long-press — the most reliable signal,
            // independent of pointer-capture quirks during touch scrolling.
            if (_messagesList.verticalScroller != null)
                _messagesList.verticalScroller.valueChanged += OnTranscriptScrolled;
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
                _messagesList.UnregisterCallback<PointerMoveEvent>(OnTranscriptPointerMove, TrickleDown.TrickleDown);
                _messagesList.UnregisterCallback<PointerUpEvent>(OnTranscriptPointerUp);
                if (_messagesList.verticalScroller != null)
                    _messagesList.verticalScroller.valueChanged -= OnTranscriptScrolled;
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

            VisualElement target = evt.target as VisualElement;
            if (IsInsideBubbleActions(target))
                return;

            bool isContextButton = evt.button != 0 || (evt.pressedButtons & 2) != 0;
            if (!isContextButton)
                return;

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

            int? msgIndex = ChatMessageEditController.GetMessageIndexFromElement(bubble);
            if (msgIndex == null)
                return;

            bool isUser = bubble.ClassListContains("transcript__bubble--user");
            _messageEditController.ShowMessageContextMenu(bubble, msgIndex.Value, isUser, pos);
        }

        private void OnTranscriptPointerDown(PointerDownEvent evt)
        {
            if (_messagesList == null)
                return;

            if (IsInsideBubbleActions(evt.target as VisualElement))
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

            int? msgIndex = ChatMessageEditController.GetMessageIndexFromElement(bubble);
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
                            _messageEditController.ShowMessageContextMenu(_longPressTarget, _longPressIndex, _longPressIsUser, _longPressPos);
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
            if (IsInsideBubbleActions(target))
                return;

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

        // Cancel a pending long-press once the finger moves — that's a scroll gesture,
        // not a hold. Without this, dragging to scroll over a bubble for >480ms popped
        // the context menu and blocked scrolling (worse with many tools / long messages).
        private void OnTranscriptPointerMove(PointerMoveEvent evt)
        {
            if (_longPressSchedule == null)
                return;
            if (Vector2.Distance((Vector2)evt.position, _longPressPos) > 12f)
                CancelLongPress();
        }

        private void OnTranscriptScrolled(float _)
        {
            CancelLongPress();
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

        private static bool IsInsideBubbleActions(VisualElement el)
        {
            while (el != null)
            {
                if (el.ClassListContains("transcript__bubble-actions") || el.ClassListContains("voice-bubble"))
                    return true;
                el = el.parent;
            }
            return false;
        }

        private static void StopBubbleActionPointerDown(PointerDownEvent evt)
        {
            StopBubbleActionEvent(evt);
        }

        private static void StopBubbleActionPointerUp(PointerUpEvent evt)
        {
            StopBubbleActionEvent(evt);
        }

        private static void StopBubbleActionEvent(EventBase evt)
        {
            if (evt == null)
                return;

            evt.StopImmediatePropagation();
            evt.StopPropagation();
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

        private static Texture2D s_TextCursorTex;

        internal static void ApplyTextCursor(VisualElement el)
        {
            el.RegisterCallback<MouseEnterEvent>(_ =>
                UnityEngine.Cursor.SetCursor(GetTextCursorTexture(), new Vector2(4, 11), CursorMode.ForceSoftware));
            el.RegisterCallback<MouseLeaveEvent>(_ =>
                UnityEngine.Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto));
        }

        private static Texture2D GetTextCursorTexture()
        {
            if (s_TextCursorTex != null)
                return s_TextCursorTex;

            const int w = 10, h = 22;
            s_TextCursorTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var px = new Color32[w * h];
            var c = new Color32(220, 220, 220, 255);
            var transparent = new Color32(0, 0, 0, 0);
            for (int i = 0; i < px.Length; i++)
                px[i] = transparent;
            for (int x = 2; x <= 7; x++)
            {
                px[(h - 1) * w + x] = c;
                px[(h - 2) * w + x] = c;
                px[x] = c;
                px[w + x] = c;
            }
            for (int y = 2; y <= h - 3; y++)
                px[y * w + 4] = c;
            s_TextCursorTex.SetPixels32(px);
            s_TextCursorTex.Apply(false);
            return s_TextCursorTex;
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
