using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Chat;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Localization;
using NeonCompanion.Runtime.Platform;
using NeonCompanion.Runtime.Voice;
using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK
{
    internal sealed class ChatController
    {
        public struct Deps
        {
            // UI elements
            public TextField MessageInput;
            public Button SendButton;
            public Button SummarizeButton;
            public Button SearchButton;
            public Button AttachButton;
            public Button CopyButton;
            public Button RegenerateButton;
            public Button NewSessionButton;
            public ScrollView MessagesList;
            public Button ScrollBottomBtn;
            public VisualElement Composer;
            public VisualElement ThinkingBubble;
            public Label ThinkingText;
            public Label TopbarSubtitle;
            public Label SubtitleBody;
            public Label SubtitleRole;
            public Label NavChatCount;
            // Avatar
            public Func<SpriteSheetAnimator> GetAvatarAnimator;
            public Action<AvatarMotionState> SetAvatarMotionState;
            public Action RefreshAvatarMotionState;
            public Action TriggerAvatarSmile;
            public Action TriggerAvatarConfused;
            // Services
            public Func<Task<ChatService>> GetChatServiceAsync;
            public Func<Task<CompanionApp>> GetAppAsync;
            public Func<Task> LoadSessionsAsync;
            public Action<string> ShowSystemMessage;
            // Navigation
            public Action ShowHistory;
            public Action ShowChat;
            // Settings
            public Func<bool> EnterToSend;
            public Func<bool> UseStreaming;
            // History
            public Action<IReadOnlyList<ChatSession>, List<ProviderConfig>> RenderSessionList;
            // Rendering
            public Action<IReadOnlyList<ChatMessage>> RenderMessages;
            // Model picker
            public Func<string, bool, Task> ApplyModelSelectionAsync;
            public Func<Task> OpenModelPickerAsync;
            // Avatar name for subtitle
            public Func<string> GetAvatarDisplayName;
        }

        private Deps _d;
        private bool _isSending;
        private bool _isStreamingResponse;
        private bool _isVoiceRecording;
        private Label _streamingLabel;
        private VisualElement _streamingTypingDots;
        private IVisualElementScheduledItem _inlineTypingSchedule;
        private int _inlineTypingFrame;
        private readonly ToolCallUiHelper _toolCallUiHelper = new ToolCallUiHelper();
        private readonly List<ChatAttachment> _pendingComposerAttachments = new List<ChatAttachment>();
        private string _chatSubtitle = string.Empty;
        private string _sessionSearchQuery = string.Empty;

        public bool IsSending => _isSending;
        public bool IsStreamingResponse => _isStreamingResponse;
        public string ChatSubtitle => _chatSubtitle;
        public string SessionSearchQuery => _sessionSearchQuery;

        public void SetDeps(Deps deps) { _d = deps; }

        public void SetVoiceRecording(bool value) { _isVoiceRecording = value; }
        public void SetChatSubtitle(string value) { _chatSubtitle = value ?? string.Empty; }
        public void SetSessionSearchQuery(string value) { _sessionSearchQuery = value ?? string.Empty; }

        public void RegisterCallbacks()
        {
            RegisterClick(_d.SendButton, OnSendClicked);
            RegisterClick(_d.SummarizeButton, OnSummarizeClicked);
            RegisterClick(_d.SearchButton, OnSearchClicked);
            RegisterClick(_d.AttachButton, OnAttachClicked);
            RegisterClick(_d.CopyButton, OnCopyClicked);
            RegisterClick(_d.RegenerateButton, OnRegenerateClicked);
            RegisterClick(_d.NewSessionButton, OnNewSessionClicked);
            RegisterClick(_d.ScrollBottomBtn, OnScrollBottomClicked);

            if (_d.MessageInput != null)
            {
                _d.MessageInput.RegisterCallback<KeyDownEvent>(OnInputKeyDown);
                _d.MessageInput.RegisterCallback<FocusEvent>(_ => _d.Composer?.AddToClassList("composer--focused"));
                _d.MessageInput.RegisterCallback<BlurEvent>(_ => _d.Composer?.RemoveFromClassList("composer--focused"));
                _d.MessageInput.RegisterCallback<ChangeEvent<string>>(OnComposerTextChanged);
            }
        }

        public void UnregisterCallbacks()
        {
            UnregisterClick(_d.SendButton, OnSendClicked);
            UnregisterClick(_d.SummarizeButton, OnSummarizeClicked);
            UnregisterClick(_d.SearchButton, OnSearchClicked);
            UnregisterClick(_d.AttachButton, OnAttachClicked);
            UnregisterClick(_d.CopyButton, OnCopyClicked);
            UnregisterClick(_d.RegenerateButton, OnRegenerateClicked);
            UnregisterClick(_d.NewSessionButton, OnNewSessionClicked);
            UnregisterClick(_d.ScrollBottomBtn, OnScrollBottomClicked);

            if (_d.MessageInput != null)
            {
                _d.MessageInput.UnregisterCallback<KeyDownEvent>(OnInputKeyDown);
                _d.MessageInput.UnregisterCallback<ChangeEvent<string>>(OnComposerTextChanged);
            }

            _isSending = false;
            _isStreamingResponse = false;
            _toolCallUiHelper.Clear();
            _streamingLabel = null;
            StopInlineTypingAnimation();
            if (_streamingTypingDots != null)
            {
                _streamingTypingDots.RemoveFromHierarchy();
                _streamingTypingDots = null;
            }
        }

        public void InitState()
        {
            SetSending(false);
        }

        // ===== Input =====

        private void OnInputKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter)
                return;

            bool enterToSend = _d.EnterToSend();
            bool hasCtrl = evt.ctrlKey || evt.commandKey;

            if (enterToSend)
            {
                evt.StopPropagation();
                OnSendClicked();
                return;
            }

            if (hasCtrl)
            {
                evt.StopPropagation();
                OnSendClicked();
            }
        }

        private void OnComposerTextChanged(ChangeEvent<string> evt)
        {
            if (_isSending || _isVoiceRecording)
                return;

            bool hasText = !string.IsNullOrWhiteSpace(evt.newValue);
            if (hasText)
            {
                var animator = _d.GetAvatarAnimator?.Invoke();
                if (animator != null && animator.HasAnyClips)
                    _d.SetAvatarMotionState(AvatarMotionState.Listening);
            }
        }

        // ===== Send =====

        private void OnSendClicked()
        {
            _ = SendCurrentMessageAsync();
        }

        public async Task SendCurrentMessageAsync()
        {
            bool hasPendingAttachments = _pendingComposerAttachments.Count > 0;
            if (_isSending || _d.MessageInput == null || (string.IsNullOrWhiteSpace(_d.MessageInput.value) && !hasPendingAttachments))
                return;

            string composerText = (_d.MessageInput.value ?? string.Empty).Trim();
            if (await TryHandleModelCommandAsync(composerText))
            {
                _d.MessageInput.value = string.Empty;
                return;
            }

            var pendingAttachments = CloneAttachments(_pendingComposerAttachments);
            string message = StripAttachmentTokens(composerText, pendingAttachments);
            _d.MessageInput.value = string.Empty;
            SetSending(true);

            ChatService chat = null;
            try
            {
                chat = await _d.GetChatServiceAsync();
                if (chat == null)
                {
                    _d.ShowSystemMessage(LocalizationExtensions.Get("system.app.not_initialized", "Приложение не инициализировано."));
                    return;
                }

                bool streaming = _d.UseStreaming();
                chat.UseStreaming = streaming;

                _d.RenderMessages(BuildPendingMessages(chat.CurrentChatViewModel?.Messages, message, pendingAttachments));

                if (streaming)
                {
                    AddStreamingBubble();
                    await chat.SendMessageAsync(message, pendingAttachments, OnStreamToken, OnToolProgress);
                    ClearThinkingBubble();
                    _toolCallUiHelper.Clear();
                    _streamingLabel = null;
                    StopInlineTypingAnimation();
                    if (_streamingTypingDots != null)
                    {
                        _streamingTypingDots.RemoveFromHierarchy();
                        _streamingTypingDots = null;
                    }
                }
                else
                {
                    await chat.SendMessageAsync(message, pendingAttachments);
                }

                ClearPendingComposerAttachments();
                _d.RenderMessages(chat.CurrentChatViewModel?.Messages);
                await _d.LoadSessionsAsync();
                _d.TriggerAvatarSmile();
            }
            catch (Exception ex)
            {
                _d.MessageInput.value = composerText;
                RestorePendingComposerAttachments(pendingAttachments);
                _d.RenderMessages(chat?.CurrentChatViewModel?.Messages);
                _d.ShowSystemMessage(ex.Message);
                NeonLogger.LogError(ex.ToString());
                _d.TriggerAvatarConfused();
            }
            finally
            {
                SetSending(false);
            }
        }

        private async Task<bool> TryHandleModelCommandAsync(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;

            string trimmed = message.Trim();
            if (string.Equals(trimmed, "/model", StringComparison.OrdinalIgnoreCase))
            {
                await _d.OpenModelPickerAsync();
                return true;
            }

            if (!trimmed.StartsWith("/model ", StringComparison.OrdinalIgnoreCase))
                return false;

            string requestedModel = trimmed.Substring("/model ".Length).Trim();
            if (string.IsNullOrWhiteSpace(requestedModel))
            {
                await _d.OpenModelPickerAsync();
                return true;
            }

            await _d.ApplyModelSelectionAsync(requestedModel, false);
            return true;
        }

        // ===== Streaming =====

        private void AddStreamingBubble()
        {
            if (_d.MessagesList == null) return;
            var placeholder = CreateMessageElement(new ChatMessage { role = "assistant", content = "" });
            _d.MessagesList.Add(placeholder);

            var bubble = placeholder.Q<VisualElement>(className: "transcript__bubble");

            _streamingLabel = placeholder.Q<Label>(className: "transcript__body");
            if (_streamingLabel == null && bubble != null)
            {
                _streamingLabel = new Label("");
                _streamingLabel.AddToClassList("transcript__body");
                bubble.Add(_streamingLabel);
            }

            _streamingTypingDots = new VisualElement();
            _streamingTypingDots.AddToClassList("typing--inline");
            for (int i = 0; i < 3; i++)
            {
                var dot = new VisualElement();
                dot.AddToClassList("typing__dot");
                if (i == 1) dot.AddToClassList("typing__dot--delay-1");
                if (i == 2) dot.AddToClassList("typing__dot--delay-2");
                _streamingTypingDots.Add(dot);
            }
            if (bubble != null)
                bubble.Insert(1, _streamingTypingDots);

            _toolCallUiHelper.SetBubble(bubble);
            StartInlineTypingAnimation();
            ScrollTranscriptToBottom();
        }

        private void OnStreamToken(string token)
        {
            if (_streamingTypingDots != null)
            {
                StopInlineTypingAnimation();
                _streamingTypingDots.RemoveFromHierarchy();
                _streamingTypingDots = null;
                _isStreamingResponse = true;
                _d.RefreshAvatarMotionState();
            }
            if (_streamingLabel != null)
                _streamingLabel.text += token;
            ScrollTranscriptToBottom();
        }

        private void OnToolProgress(string tool, string label, string emoji, string status)
        {
            if (_d.ThinkingBubble != null && _d.ThinkingText != null)
            {
                string displayText = string.IsNullOrEmpty(label) ? GetThinkingText(tool) : label;
                if (displayText.Length > 40)
                    displayText = displayText.Substring(0, 40) + "...";
                _d.ThinkingText.text = displayText;
                SetDisplay(_d.ThinkingBubble, DisplayStyle.Flex);
            }

            _toolCallUiHelper.OnToolProgress(tool, label, emoji, status);
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

        private void ClearThinkingBubble()
        {
            if (_d.ThinkingBubble != null)
                SetDisplay(_d.ThinkingBubble, DisplayStyle.None);
            if (_d.ThinkingText != null)
                _d.ThinkingText.text = string.Empty;
        }

        private void SetSending(bool isSending)
        {
            _isSending = isSending;
            if (!isSending)
                _isStreamingResponse = false;
            if (_d.SendButton != null)
                _d.SendButton.SetEnabled(!isSending);
            SetDisplay(_d.SubtitleBody, isSending ? DisplayStyle.None : DisplayStyle.Flex);
            _d.RefreshAvatarMotionState();
        }

        // ===== Summarize =====

        private void OnSummarizeClicked()
        {
            _ = SummarizeCurrentConversationAsync();
        }

        private async Task SummarizeCurrentConversationAsync()
        {
            try
            {
                var chat = await _d.GetChatServiceAsync();
                if (chat == null)
                {
                    _d.ShowSystemMessage(LocalizationExtensions.Get("system.app.not_initialized", "Приложение не инициализировано."));
                    return;
                }

                string summary = await chat.SummarizeCurrentConversationAsync();
                _d.ShowSystemMessage(summary);
            }
            catch (Exception ex)
            {
                _d.ShowSystemMessage(LocalizationExtensions.Get("system.chat.summary_failed", "Не удалось получить сводку диалога. Попробуй позже."));
                NeonLogger.LogError(ex.ToString());
            }
        }

        // ===== Search =====

        private void OnSearchClicked()
        {
            _ = SearchSessionsFromComposerAsync();
        }

        private async Task SearchSessionsFromComposerAsync()
        {
            try
            {
                _sessionSearchQuery = _d.MessageInput?.value?.Trim() ?? string.Empty;

                var chat = await _d.GetChatServiceAsync();
                if (chat == null)
                    return;

                var allSessions = await chat.GetAllSessionsAsync();
                var app = await _d.GetAppAsync();
                var providers = app != null ? await app.ProviderManager.GetAllProvidersAsync() : new List<ProviderConfig>();
                _d.RenderSessionList(allSessions, providers);

                _d.ShowHistory();
            }
            catch (Exception ex)
            {
                _d.ShowSystemMessage(LocalizationExtensions.Get("system.chat.search_failed", "Не удалось выполнить поиск по чатам. Попробуй ещё раз."));
                NeonLogger.LogError(ex.ToString());
            }
        }

        // ===== Attachments =====

        private void OnAttachClicked()
        {
            _ = AttachImageTokenAsync();
        }

        private async Task AttachImageTokenAsync()
        {
            try
            {
                var app = await _d.GetAppAsync();
                if (app == null || _d.MessageInput == null) return;

                var filePicker = app.Services.GetRequired<IFilePickerService>();
                string path = await filePicker.PickImagePathAsync();
                if (string.IsNullOrEmpty(path)) return;

                string fileName = System.IO.Path.GetFileName(path);
                _pendingComposerAttachments.Add(new ChatAttachment
                {
                    kind = "image",
                    name = fileName,
                    path = path,
                    mediaType = GuessImageMediaType(path)
                });

                string token = $"[attachment: {fileName}]";
                string current = _d.MessageInput.value ?? string.Empty;
                _d.MessageInput.value = string.IsNullOrWhiteSpace(current)
                    ? token
                    : $"{current.TrimEnd()} {token}";
                _d.MessageInput.Focus();
            }
            catch (Exception ex)
            {
                _d.ShowSystemMessage(LocalizationExtensions.Get("system.chat.attachment_failed", "Не удалось добавить вложение к сообщению."));
                NeonLogger.LogError(ex.ToString());
            }
        }

        public void ClearPendingComposerAttachments()
        {
            _pendingComposerAttachments.Clear();
        }

        private void RestorePendingComposerAttachments(IReadOnlyList<ChatAttachment> attachments)
        {
            _pendingComposerAttachments.Clear();
            var restored = CloneAttachments(attachments);
            for (int i = 0; i < restored.Count; i++)
                _pendingComposerAttachments.Add(restored[i]);
        }

        // ===== Copy =====

        private void OnCopyClicked()
        {
            var chat = _d.GetChatServiceAsync().Result;
            if (chat == null) return;
            var messages = chat.CurrentChatViewModel?.Messages;
            if (messages == null || messages.Count == 0) return;

            var sb = new StringBuilder();
            for (int i = 0; i < messages.Count; i++)
            {
                var msg = messages[i];
                if (msg == null || string.IsNullOrWhiteSpace(msg.content)) continue;
                string role = NormalizeRole(msg.role);
                sb.AppendLine($"[{DisplayRole(role)}]");
                sb.AppendLine(msg.content);
                sb.AppendLine();
            }

            GUIUtility.systemCopyBuffer = sb.ToString().TrimEnd();
            _d.ShowSystemMessage(LocalizationExtensions.Get("chat.copied", "Диалог скопирован в буфер обмена."));
        }

        // ===== New Session =====

        private void OnNewSessionClicked()
        {
            _ = StartNewSessionAsync();
        }

        public async Task StartNewSessionAsync()
        {
            try
            {
                var chat = await _d.GetChatServiceAsync();
                if (chat == null)
                    return;

                await chat.StartNewSessionAsync();
                ClearPendingComposerAttachments();
                if (_d.MessageInput != null)
                    _d.MessageInput.value = string.Empty;
                _d.RenderMessages(chat.CurrentChatViewModel?.Messages);
                await _d.LoadSessionsAsync();
                _d.ShowChat();
            }
            catch (Exception ex)
            {
                NeonLogger.LogError(ex.ToString());
            }
        }

        // ===== Regenerate =====

        private void OnRegenerateClicked()
        {
            _ = RegenerateLastAsync();
        }

        private async Task RegenerateLastAsync()
        {
            try
            {
                var chat = await _d.GetChatServiceAsync();
                if (chat == null || _isSending) return;

                var messages = chat.CurrentChatViewModel?.Messages;
                if (messages == null || messages.Count == 0) return;

                if (messages[messages.Count - 1].role == "assistant")
                    messages.RemoveAt(messages.Count - 1);

                if (messages.Count == 0) return;

                SetSending(true);
                try
                {
                    bool streaming = _d.UseStreaming();
                    chat.UseStreaming = streaming;

                    _d.RenderMessages(chat.CurrentChatViewModel?.Messages);

                    if (streaming)
                    {
                        AddStreamingBubble();
                        await chat.RegenerateAsync(OnStreamToken, OnToolProgress);
                        ClearThinkingBubble();
                        _toolCallUiHelper.Clear();
                        _streamingLabel = null;
                        StopInlineTypingAnimation();
                        if (_streamingTypingDots != null)
                        {
                            _streamingTypingDots.RemoveFromHierarchy();
                            _streamingTypingDots = null;
                        }
                    }
                    else
                    {
                        await chat.RegenerateAsync();
                    }

                    _d.RenderMessages(chat.CurrentChatViewModel?.Messages);
                    await _d.LoadSessionsAsync();
                    _d.TriggerAvatarSmile();
                }
                catch (Exception ex)
                {
                    _d.ShowSystemMessage(ex.Message);
                    NeonLogger.LogError(ex.ToString());
                    _d.TriggerAvatarConfused();
                }
                finally
                {
                    SetSending(false);
                }
            }
            catch (Exception ex)
            {
                NeonLogger.LogError(ex.ToString());
            }
        }

        // ===== Message Rendering =====

        public void RenderMessages(IReadOnlyList<ChatMessage> messages)
        {
            int count = messages?.Count ?? 0;
            string avatarName = _d.GetAvatarDisplayName?.Invoke() ?? string.Empty;
            _chatSubtitle = string.IsNullOrEmpty(avatarName)
                ? MessageCountText(count)
                : $"{MessageCountText(count)} · {avatarName}";

            if (_d.TopbarSubtitle != null)
                _d.TopbarSubtitle.text = _chatSubtitle;

            if (_d.NavChatCount != null)
                _d.NavChatCount.text = count.ToString();

            if (_d.SubtitleRole != null)
                _d.SubtitleRole.text = avatarName;

            RenderTranscript(messages);

            if (_d.SubtitleBody == null)
                return;

            string text = LocalizationExtensions.Get("chat.subtitle.ready", "Готова помочь. С чего начнём?");
            if (messages != null)
            {
                for (int i = messages.Count - 1; i >= 0; i--)
                {
                    var msg = messages[i];
                    if (!HasRenderableMessageContent(msg)) continue;
                    if (msg.role != "user")
                    {
                        text = !string.IsNullOrWhiteSpace(msg.content)
                            ? msg.content
                            : $"[image] {GetAttachmentDisplayName(msg.attachments != null && msg.attachments.Count > 0 ? msg.attachments[0] : null)}";
                        break;
                    }
                }
            }

            _d.SubtitleBody.text = TrimForSubtitle(text);
        }

        private void RenderTranscript(IReadOnlyList<ChatMessage> messages)
        {
            if (_d.MessagesList == null)
                return;

            _d.MessagesList.Clear();

            if (messages == null || messages.Count == 0)
            {
                _d.MessagesList.Add(CreateEmptyTranscript());
                return;
            }

            bool hasVisibleMessages = false;
            for (int i = 0; i < messages.Count; i++)
            {
                var message = messages[i];
                if (!HasRenderableMessageContent(message))
                    continue;

                _d.MessagesList.Add(CreateMessageElement(message));
                hasVisibleMessages = true;
            }

            if (!hasVisibleMessages)
            {
                _d.MessagesList.Add(CreateEmptyTranscript());
                return;
            }

            ScrollTranscriptToBottom();
        }

        private static VisualElement CreateEmptyTranscript()
        {
            var container = new VisualElement();
            container.AddToClassList("transcript__empty");

            var title = new Label(LocalizationExtensions.Get("chat.empty.title", "Пока нет сообщений"));
            title.AddToClassList("transcript__empty-title");

            var body = new Label(LocalizationExtensions.Get("chat.empty.body", "Начни диалог ниже, и здесь появится полная история текущей сессии."));
            body.AddToClassList("transcript__empty-body");

            container.Add(title);
            container.Add(body);
            return container;
        }

        internal static VisualElement CreateMessageElement(ChatMessage message)
        {
            string role = NormalizeRole(message.role);

            var row = new VisualElement();
            row.AddToClassList("transcript__row");
            row.AddToClassList($"transcript__row--{role}");

            var bubble = new VisualElement();
            bubble.AddToClassList("transcript__bubble");
            bubble.AddToClassList($"transcript__bubble--{role}");

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

            if (!string.IsNullOrWhiteSpace(message.content))
            {
                var body = new Label(message.content);
                body.AddToClassList("transcript__body");
                bubble.Add(body);
            }

            if (message.attachments != null && message.attachments.Count > 0)
            {
                var attachmentWrap = new VisualElement();
                attachmentWrap.style.flexDirection = FlexDirection.Column;
                attachmentWrap.style.marginTop = 6f;

                for (int i = 0; i < message.attachments.Count; i++)
                {
                    var attachment = message.attachments[i];
                    if (attachment == null)
                        continue;

                    var attachmentLabel = new Label($"[image] {GetAttachmentDisplayName(attachment)}");
                    attachmentLabel.AddToClassList("transcript__body");
                    attachmentLabel.style.fontSize = 11f;
                    attachmentLabel.style.color = new Color(0.76f, 0.8f, 0.92f, 0.92f);
                    attachmentWrap.Add(attachmentLabel);
                }

                if (attachmentWrap.childCount > 0)
                    bubble.Add(attachmentWrap);
            }
            row.Add(bubble);

            return row;
        }

        public void ScrollTranscriptToBottom()
        {
            if (_d.MessagesList == null)
                return;

            _d.MessagesList.schedule.Execute(() =>
            {
                var content = _d.MessagesList?.contentContainer;
                if (content == null || content.childCount == 0)
                    return;

                _d.MessagesList.ScrollTo(content[content.childCount - 1]);
                SetDisplay(_d.ScrollBottomBtn, DisplayStyle.None);
            });
        }

        // ===== Static Helpers =====

        private static string StripAttachmentTokens(string composerText, IReadOnlyList<ChatAttachment> attachments)
        {
            string text = composerText ?? string.Empty;
            if (attachments != null)
            {
                for (int i = 0; i < attachments.Count; i++)
                {
                    string name = attachments[i]?.name;
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    string token = $"[attachment: {name}]";
                    text = text.Replace(token, string.Empty);
                }
            }

            return CollapseWhitespace(text).Trim();
        }

        private static string CollapseWhitespace(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            var sb = new StringBuilder(value.Length);
            bool previousWasWhitespace = false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsWhiteSpace(c))
                {
                    if (!previousWasWhitespace)
                        sb.Append(' ');
                    previousWasWhitespace = true;
                }
                else
                {
                    sb.Append(c);
                    previousWasWhitespace = false;
                }
            }

            return sb.ToString();
        }

        private static string GuessImageMediaType(string path)
        {
            string extension = System.IO.Path.GetExtension(path)?.ToLowerInvariant();
            switch (extension)
            {
                case ".png": return "image/png";
                case ".jpg": return "image/jpeg";
                case ".jpeg": return "image/jpeg";
                case ".webp": return "image/webp";
                case ".gif": return "image/gif";
                case ".bmp": return "image/bmp";
                default: return "application/octet-stream";
            }
        }

        private static string NormalizeRole(string role)
        {
            if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
                return "user";

            if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase))
                return "system";

            return "assistant";
        }

        private static string DisplayRole(string role)
        {
            switch (role)
            {
                case "user":
                    return LocalizationExtensions.Get("chat.role.you", "Ты");
                case "system":
                    return LocalizationExtensions.Get("chat.role.system", "Система");
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

        internal static string MessageCountText(int count)
        {
            int mod100 = count % 100;
            int mod10 = count % 10;
            string word;

            if (mod100 >= 11 && mod100 <= 14)
                word = LocalizationExtensions.Get("chat.messages.many", "сообщений");
            else if (mod10 == 1)
                word = LocalizationExtensions.Get("chat.messages.one", "сообщение");
            else if (mod10 >= 2 && mod10 <= 4)
                word = LocalizationExtensions.Get("chat.messages.few", "сообщения");
            else
                word = LocalizationExtensions.Get("chat.messages.many", "сообщений");

            return $"{count} {word}";
        }

        private static string TrimForSubtitle(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            string normalized = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return normalized.Length <= 320 ? normalized : normalized.Substring(0, 317) + "...";
        }

        private static bool HasRenderableMessageContent(ChatMessage message)
        {
            if (message == null)
                return false;
            if (!string.IsNullOrWhiteSpace(message.content))
                return true;
            if (message.attachments != null && message.attachments.Count > 0)
                return true;
            return false;
        }

        private static string GetAttachmentDisplayName(ChatAttachment attachment)
        {
            if (attachment == null)
                return string.Empty;
            return !string.IsNullOrWhiteSpace(attachment.name) ? attachment.name : "image";
        }

        private static List<ChatAttachment> CloneAttachments(IReadOnlyList<ChatAttachment> attachments)
        {
            if (attachments == null || attachments.Count == 0)
                return new List<ChatAttachment>();

            var clone = new List<ChatAttachment>(attachments.Count);
            for (int i = 0; i < attachments.Count; i++)
            {
                var src = attachments[i];
                if (src == null) { clone.Add(null); continue; }
                clone.Add(new ChatAttachment
                {
                    kind = src.kind,
                    name = src.name,
                    path = src.path,
                    mediaType = src.mediaType
                });
            }
            return clone;
        }

        private static IReadOnlyList<ChatMessage> BuildPendingMessages(IReadOnlyList<ChatMessage> existing, string userMessage, IReadOnlyList<ChatAttachment> attachments)
        {
            var list = new List<ChatMessage>();
            if (existing != null)
            {
                for (int i = 0; i < existing.Count; i++)
                    list.Add(existing[i]);
            }
            list.Add(new ChatMessage
            {
                role = "user",
                content = userMessage,
                attachments = attachments != null && attachments.Count > 0
                    ? new List<ChatAttachment>(attachments)
                    : null,
                unixTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
            return list;
        }

        // ===== Inline Typing Animation =====

        private void StartInlineTypingAnimation()
        {
            _inlineTypingFrame = 0;
            _inlineTypingSchedule?.Pause();
            _inlineTypingSchedule = _d.MessagesList?.schedule.Execute(() =>
            {
                _inlineTypingFrame++;
                if (_streamingTypingDots == null)
                {
                    _inlineTypingSchedule?.Pause();
                    return;
                }
                var dots = _streamingTypingDots.Query<VisualElement>(className: "typing__dot").ToList();
                for (int i = 0; i < dots.Count; i++)
                {
                    bool active = (i == _inlineTypingFrame % 3);
                    dots[i].EnableInClassList("typing__dot--active", active);
                }
            }).Every(200);
        }

        private void StopInlineTypingAnimation()
        {
            _inlineTypingSchedule?.Pause();
            _inlineTypingSchedule = null;
        }

        // ===== Utility =====

        private static void RegisterClick(Button button, Action handler)
        {
            if (button != null)
                button.clicked += handler;
        }

        private static void UnregisterClick(Button button, Action handler)
        {
            if (button != null)
                button.clicked -= handler;
        }

        private static void SetDisplay(VisualElement element, DisplayStyle display)
        {
            if (element != null)
                element.style.display = display;
        }
    }
}
