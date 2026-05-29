using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Api.Tools;
using NeonCompanion.Runtime.Avatar;
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
            public Button StopButton;
            public Button SummarizeButton;
            public Button SearchButton;
            public Button AttachButton;
            public Button NewSessionButton;
            public Button ExportButton;
            public ScrollView MessagesList;
            public Button ScrollBottomBtn;
            public VisualElement Composer;
            public VisualElement ThinkingBubble;
            public Label ThinkingText;
            public Label TopbarSubtitle;
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
        private ChatService _currentChatService;
        private VisualElement _streamingBubble;
        private Label _streamingLabel;
        private VisualElement _streamingTypingDots;
        private IVisualElementScheduledItem _inlineTypingSchedule;
        private int _inlineTypingFrame;
        private DateTime _streamingStartTime;
        private int _estimatedTokens;
        private VisualElement _streamingStatsFooter;
        private Label _streamingStatsLabel;
        private IVisualElementScheduledItem _statsUpdateSchedule;
        private readonly ToolCallUiHelper _toolCallUiHelper = new ToolCallUiHelper();
        private ApprovalPrompt _currentApprovalPrompt;
        private VisualElement _currentApprovalElement;
        private readonly List<ChatAttachment> _pendingComposerAttachments = new List<ChatAttachment>();
        private bool _pendingEnterSend;
        private string _chatSubtitle = string.Empty;
        private string _sessionSearchQuery = string.Empty;
        private TextElement _composerTextElement;
        private float _composerInputHeight = -1f;

        private const float ComposerInputMinHeight = 36f;
        private const float ComposerInputMaxHeight = 140f;
        private const float ComposerInputVerticalPadding = 12f;

        private const int MaxToolIterations = 10;

        public bool IsSending => _isSending;
        public bool IsStreamingResponse => _isStreamingResponse;
        public string ChatSubtitle => _chatSubtitle;
        public string SessionSearchQuery => _sessionSearchQuery;

        public void SetDeps(Deps deps) { _d = deps; }

        public void SetVoiceRecording(bool value) { _isVoiceRecording = value; }
        public void SetChatSubtitle(string value) { _chatSubtitle = value ?? string.Empty; }
        public void SetSessionSearchQuery(string value) { _sessionSearchQuery = value ?? string.Empty; }
        public void ShowSystemMessage(string text) { _d.ShowSystemMessage?.Invoke(text); }

        public void RegisterCallbacks()
        {
            RegisterClick(_d.SendButton, OnSendClicked);
            RegisterClick(_d.StopButton, OnStopClicked);
            RegisterClick(_d.SummarizeButton, OnSummarizeClicked);
            RegisterClick(_d.SearchButton, OnSearchClicked);
            RegisterClick(_d.AttachButton, OnAttachClicked);
            RegisterClick(_d.NewSessionButton, OnNewSessionClicked);
            RegisterClick(_d.ExportButton, OnExportClicked);
            RegisterClick(_d.ScrollBottomBtn, OnScrollBottomClicked);

            // Wire up static bubble action events
            CopyRequested += OnCopyClicked;
            RegenerateRequested += OnRegenerateClicked;

            if (_d.MessageInput != null)
            {
                _d.MessageInput.RegisterCallback<KeyDownEvent>(OnInputKeyDown, TrickleDown.TrickleDown);
                _d.MessageInput.RegisterCallback<FocusEvent>(_ => _d.Composer?.AddToClassList("composer--focused"));
                _d.MessageInput.RegisterCallback<BlurEvent>(_ => _d.Composer?.RemoveFromClassList("composer--focused"));
                _d.MessageInput.RegisterCallback<ChangeEvent<string>>(OnComposerTextChanged);
                _d.MessageInput.RegisterCallback<GeometryChangedEvent>(OnComposerGeometryChanged);
                QueueComposerHeightUpdate();
            }
        }

        public void UnregisterCallbacks()
        {
            UnregisterClick(_d.SendButton, OnSendClicked);
            UnregisterClick(_d.StopButton, OnStopClicked);
            UnregisterClick(_d.SummarizeButton, OnSummarizeClicked);
            UnregisterClick(_d.SearchButton, OnSearchClicked);
            UnregisterClick(_d.AttachButton, OnAttachClicked);
            UnregisterClick(_d.NewSessionButton, OnNewSessionClicked);
            UnregisterClick(_d.ExportButton, OnExportClicked);
            UnregisterClick(_d.ScrollBottomBtn, OnScrollBottomClicked);

            CopyRequested -= OnCopyClicked;
            RegenerateRequested -= OnRegenerateClicked;

            if (_d.MessageInput != null)
            {
                _d.MessageInput.UnregisterCallback<KeyDownEvent>(OnInputKeyDown, TrickleDown.TrickleDown);
                _d.MessageInput.UnregisterCallback<ChangeEvent<string>>(OnComposerTextChanged);
                _d.MessageInput.UnregisterCallback<GeometryChangedEvent>(OnComposerGeometryChanged);
            }

            if (_pinBottomQueued)
            {
                _pinBottomQueued = false;
                _d.MessagesList?.contentContainer?.UnregisterCallback<GeometryChangedEvent>(OnTranscriptGeometryForScroll);
            }

            _isSending = false;
            _isStreamingResponse = false;
            _composerTextElement = null;
            _composerInputHeight = -1f;
            _toolCallUiHelper.Clear();
            DismissCurrentApprovalPrompt();
            _streamingBubble = null;
            _streamingLabel = null;
            StopInlineTypingAnimation();
            StopStreamingStatsUpdate();
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

            // Set flag here (TrickleDown — before TextField inserts '\n').
            // OnComposerTextChanged fires after the insertion and consumes it.
            if ((enterToSend && !hasCtrl) || hasCtrl)
                _pendingEnterSend = true;
        }

        private void OnComposerGeometryChanged(GeometryChangedEvent evt)
        {
            QueueComposerHeightUpdate();
        }

        // Auto-grow the multiline composer to fit its content, clamped to the
        // CSS min/max-height. UITK does not auto-size a multiline TextField on its
        // own, so we measure the text and drive the field height explicitly.
        private void QueueComposerHeightUpdate()
        {
            var field = _d.MessageInput;
            if (field == null)
                return;

            field.schedule.Execute(UpdateComposerHeight);
        }

        private void UpdateComposerHeight()
        {
            var field = _d.MessageInput;
            if (field == null || field.panel == null)
                return;

            TextElement textEl = GetComposerTextElement(field);
            if (textEl == null)
                return;

            float width = textEl.contentRect.width;
            if (width <= 1f)
                width = field.contentRect.width;
            if (width <= 1f)
                return;

            string text = string.IsNullOrEmpty(field.value) ? " " : field.value;
            if (text.EndsWith("\n", StringComparison.Ordinal) || text.EndsWith("\r", StringComparison.Ordinal))
                text += " ";

            Vector2 size = textEl.MeasureTextSize(text, width, VisualElement.MeasureMode.Exactly, 0f, VisualElement.MeasureMode.Undefined);
            if (float.IsNaN(size.y) || size.y <= 0f)
                size.y = textEl.resolvedStyle.fontSize * 1.35f;

            float target = Mathf.Clamp(size.y + ComposerInputVerticalPadding, ComposerInputMinHeight, ComposerInputMaxHeight);
            if (_composerInputHeight > 0f && Mathf.Abs(_composerInputHeight - target) < 0.5f)
                return;

            _composerInputHeight = target;
            field.style.height = target;
            textEl.style.minHeight = target;
        }

        private TextElement GetComposerTextElement(TextField field)
        {
            if (_composerTextElement != null && _composerTextElement.panel != null)
                return _composerTextElement;

            TextElement textEl = field.Q<TextElement>(className: "unity-text-field__input");
            if (textEl == null)
                textEl = field.Q<TextElement>(className: "unity-base-text-field__input");
            if (textEl == null)
                textEl = field.Q<TextElement>();

            _composerTextElement = textEl;
            return _composerTextElement;
        }

        private void OnComposerTextChanged(ChangeEvent<string> evt)
        {
            QueueComposerHeightUpdate();

            if (_pendingEnterSend)
            {
                _pendingEnterSend = false;
                string trimmed = (evt.newValue ?? string.Empty).TrimEnd('\n', '\r');
                if (_d.MessageInput != null)
                {
                    _d.MessageInput.SetValueWithoutNotify(trimmed);
                    QueueComposerHeightUpdate();
                }
                OnSendClicked();
                return;
            }

            if (_isSending || _isVoiceRecording)
                return;

            _d.RefreshAvatarMotionState?.Invoke();
        }

        // ===== Send =====

        private void OnSendClicked()
        {
            _ = SendCurrentMessageAsync();
        }

        private void OnStopClicked()
        {
            DismissCurrentApprovalPrompt();
            _currentChatService?.CancelCurrentGeneration();
        }

        public async Task SendCurrentMessageAsync()
        {
            DismissCurrentApprovalPrompt();

            bool hasPendingAttachments = _pendingComposerAttachments.Count > 0;
            if (_isSending || _d.MessageInput == null || (string.IsNullOrWhiteSpace(_d.MessageInput.value) && !hasPendingAttachments))
                return;

            string composerText = (_d.MessageInput.value ?? string.Empty).Trim();
            if (await TryHandleCommandAsync(composerText))
            {
                _d.MessageInput.value = string.Empty;
                QueueComposerHeightUpdate();
                return;
            }

            var pendingAttachments = CloneAttachments(_pendingComposerAttachments);
            string message = StripAttachmentTokens(composerText, pendingAttachments);
            _d.MessageInput.value = string.Empty;
            QueueComposerHeightUpdate();
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

                _currentChatService = chat;

                bool streaming = _d.UseStreaming();
                chat.UseStreaming = streaming;

                _d.RenderMessages(BuildPendingMessages(chat.CurrentChatViewModel?.Messages, message, pendingAttachments));

                if (streaming)
                {
                    AddStreamingBubble();
                    await chat.SendMessageAsync(message, pendingAttachments, OnStreamToken, OnToolProgress);
                    ClearThinkingBubble();
                    _toolCallUiHelper.Clear();
                    DismissCurrentApprovalPrompt();
                    _streamingBubble = null;
                    _streamingLabel = null;
                    StopInlineTypingAnimation();
                    StopStreamingStatsUpdate();
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

                // Agent tool execution loop: handles tool_calls returned by model, shows approvals, executes locally, continues until text response
                await ProcessAgentToolLoopAsync(chat, streaming);
            }
            catch (OperationCanceledException)
            {
                // User clicked stop — keep partial response (critical for local LLMs)
                StopInlineTypingAnimation();
                StopStreamingStatsUpdate();
                if (_streamingTypingDots != null)
                {
                    _streamingTypingDots.RemoveFromHierarchy();
                    _streamingTypingDots = null;
                }
                _streamingBubble = null;
                _streamingLabel = null;
                _toolCallUiHelper.Clear();
                DismissCurrentApprovalPrompt();
                _d.RenderMessages(chat?.CurrentChatViewModel?.Messages);
            }
            catch (Exception ex)
            {
                StopStreamingStatsUpdate();
                StopInlineTypingAnimation();
                if (_streamingTypingDots != null)
                {
                    _streamingTypingDots.RemoveFromHierarchy();
                    _streamingTypingDots = null;
                }
                _streamingBubble = null;
                _streamingLabel = null;
                DismissCurrentApprovalPrompt();
                _d.MessageInput.value = composerText;
                QueueComposerHeightUpdate();
                RestorePendingComposerAttachments(pendingAttachments);
                _d.RenderMessages(chat?.CurrentChatViewModel?.Messages);
                _d.ShowSystemMessage(ex.Message);
                NeonLogger.LogError(ex.ToString());
                _d.TriggerAvatarConfused();
            }
            finally
            {
                _currentChatService = null;
                SetSending(false);
            }
        }

        private async Task<bool> TryHandleCommandAsync(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;

            string trimmed = message.Trim();
            if (!trimmed.StartsWith("/", StringComparison.Ordinal))
                return false;

            // Split into command and args
            string command;
            string args = null;
            int spaceIdx = trimmed.IndexOf(' ');
            if (spaceIdx > 0)
            {
                command = trimmed.Substring(0, spaceIdx).ToLowerInvariant();
                args = trimmed.Substring(spaceIdx + 1).Trim();
            }
            else
            {
                command = trimmed.ToLowerInvariant();
            }

            switch (command)
            {
                case "/model":
                    return await HandleModelCommandAsync(args);
                case "/help":
                    return HandleHelpCommand();
                case "/clear":
                    return await HandleClearCommandAsync();
                case "/new":
                    return await HandleNewCommandAsync();
                case "/system":
                    return HandleSystemCommand(args);
                case "/temp":
                    return HandleTempCommand(args);
                case "/tokens":
                    return HandleTokensCommand(args);
                default:
                    _d.ShowSystemMessage($"Неизвестная команда: {command}. Введите /help для списка.");
                    return true;
            }
        }

        private async Task<bool> HandleModelCommandAsync(string args)
        {
            if (string.IsNullOrWhiteSpace(args))
            {
                await _d.OpenModelPickerAsync();
                return true;
            }

            await _d.ApplyModelSelectionAsync(args, false);
            return true;
        }

        private bool HandleHelpCommand()
        {
            _d.ShowSystemMessage(
                "Доступные команды:\n" +
                "/model — выбрать модель\n" +
                "/model <id> — установить модель\n" +
                "/system <текст> — установить системный промпт\n" +
                "/system — очистить системный промпт\n" +
                "/temp <0-2> — установить температуру\n" +
                "/tokens <число> — установить макс. токенов\n" +
                "/clear — очистить историю чата\n" +
                "/new — новая сессия\n" +
                "/help — показать эту справку");
            return true;
        }

        private async Task<bool> HandleClearCommandAsync()
        {
            var chat = await _d.GetChatServiceAsync();
            if (chat != null)
            {
                await chat.ClearCurrentSessionAsync();
                _d.RenderMessages(chat.CurrentChatViewModel?.Messages);
                _d.ShowSystemMessage("История очищена.");
            }
            return true;
        }

        private async Task<bool> HandleNewCommandAsync()
        {
            await StartNewSessionAsync();
            _d.ShowSystemMessage("Новая сессия начата.");
            return true;
        }

        private bool HandleSystemCommand(string args)
        {
            // Need to get ChatService and set system prompt on current ViewModel
            // This is async because GetChatServiceAsync is async
            // Store for async execution
            _ = SetSystemPromptAsync(args);
            return true;
        }

        private async Task SetSystemPromptAsync(string args)
        {
            var chat = await _d.GetChatServiceAsync();
            if (chat?.CurrentChatViewModel == null) return;

            if (string.IsNullOrWhiteSpace(args))
            {
                chat.CurrentChatViewModel.SystemPrompt = null;
                _d.ShowSystemMessage("Системный промпт очищен.");
            }
            else
            {
                chat.CurrentChatViewModel.SystemPrompt = args;
                _d.ShowSystemMessage($"Системный промпт установлен: {args}");
            }
        }

        private bool HandleTempCommand(string args)
        {
            if (string.IsNullOrWhiteSpace(args))
            {
                _d.ShowSystemMessage("Использование: /temp <0-2>");
                return true;
            }

            if (float.TryParse(args, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float temp))
            {
                if (temp < 0f || temp > 2f)
                {
                    _d.ShowSystemMessage("Температура должна быть от 0 до 2.");
                    return true;
                }
                // Need async to get ChatService
                _ = SetTempAsync(temp);
                return true;
            }

            _d.ShowSystemMessage("Неверное число. Использование: /temp <0-2>");
            return true;
        }

        private async Task SetTempAsync(float temp)
        {
            var chat = await _d.GetChatServiceAsync();
            if (chat?.CurrentChatViewModel != null)
            {
                chat.CurrentChatViewModel.Temperature = temp;
                _d.ShowSystemMessage($"Температура: {temp}");
            }
        }

        private bool HandleTokensCommand(string args)
        {
            if (string.IsNullOrWhiteSpace(args))
            {
                _d.ShowSystemMessage("Использование: /tokens <число>");
                return true;
            }

            if (int.TryParse(args, out int tokens) && tokens > 0)
            {
                _ = SetTokensAsync(tokens);
                return true;
            }

            _d.ShowSystemMessage("Неверное число. Использование: /tokens <число>");
            return true;
        }

        private async Task SetTokensAsync(int tokens)
        {
            var chat = await _d.GetChatServiceAsync();
            if (chat?.CurrentChatViewModel != null)
            {
                chat.CurrentChatViewModel.MaxTokens = tokens;
                _d.ShowSystemMessage($"Макс. токенов: {tokens}");
            }
        }

        // ===== Streaming =====

        private void AddStreamingBubble()
        {
            if (_d.MessagesList == null) return;
            var placeholder = CreateMessageElement(new ChatMessage { role = "assistant", content = "" });
            _d.MessagesList.Add(placeholder);

            var bubble = placeholder.Q<VisualElement>(className: "transcript__bubble");

            _streamingBubble = bubble;
            _streamingLabel = null;

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

            // Activate stats footer for this streaming assistant bubble (created hidden in CreateMessageElement)
            if (bubble != null)
            {
                _streamingStatsFooter = bubble.Q<VisualElement>(className: "transcript__stats");
                _streamingStatsLabel = _streamingStatsFooter != null ? _streamingStatsFooter.Q<Label>(className: "transcript__stats-label") : null;
                if (_streamingStatsFooter != null)
                    _streamingStatsFooter.style.display = DisplayStyle.Flex;
            }
            StartStreamingStatsUpdate();

            StartInlineTypingAnimation();
            ScrollTranscriptToBottom();
        }

        private void EnsureStreamingLabel()
        {
            if (_streamingLabel != null || _streamingBubble == null)
                return;

            _streamingLabel = new Label(string.Empty);
            _streamingLabel.AddToClassList("transcript__body");

            // Insert body before stats footer (if present) so stats appears after content in layout.
            // (Stats is appended at end of CreateMessageElement; dynamic adds would otherwise bury it.)
            var statsFooter = _streamingBubble.Q<VisualElement>(className: "transcript__stats");
            if (statsFooter != null && statsFooter.parent == _streamingBubble)
            {
                int idx = _streamingBubble.IndexOf(statsFooter);
                _streamingBubble.Insert(idx, _streamingLabel);
            }
            else
            {
                _streamingBubble.Add(_streamingLabel);
            }
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

            EnsureStreamingLabel();
            if (_streamingLabel != null)
                _streamingLabel.text += token;

            if (!string.IsNullOrEmpty(token))
            {
                _estimatedTokens += Math.Max(1, token.Length / 4);
            }

            UpdateStreamingStats();
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

            bool insertedNewEntry = _toolCallUiHelper.OnToolProgress(tool, label, emoji, status);
            if (insertedNewEntry)
                _streamingLabel = null;
            ScrollTranscriptToBottom();

            // Wire tool call detection to approval flow (Part B)
            if (IsApprovalRequestStatus(status))
            {
                var req = new ToolCallRequest
                {
                    id = Guid.NewGuid().ToString("N"),
                    toolName = tool ?? string.Empty,
                    description = !string.IsNullOrEmpty(label) ? label : tool,
                    parameters = new Dictionary<string, string>()
                };
                _ = HandleApprovalRequestAsync(req);
            }
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

            // Show stop button during generation (critical for local LLMs that may loop), hide send button
            if (_d.StopButton != null)
            {
                _d.StopButton.style.display = isSending ? DisplayStyle.Flex : DisplayStyle.None;
            }
            if (_d.SendButton != null)
            {
                _d.SendButton.style.display = isSending ? DisplayStyle.None : DisplayStyle.Flex;
            }

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

        // ===== Export =====

        private async Task ExportChatAsync()
        {
            try
            {
                var chat = await _d.GetChatServiceAsync();
                if (chat == null)
                {
                    _d.ShowSystemMessage(LocalizationExtensions.Get("system.app.not_initialized", "Приложение не инициализировано."));
                    return;
                }

                if (chat.CurrentChatViewModel?.Messages == null || chat.CurrentChatViewModel.Messages.Count == 0)
                {
                    _d.ShowSystemMessage(LocalizationExtensions.Get("chat.export.empty", "No messages to export."));
                    return;
                }

                var messages = chat.CurrentChatViewModel.Messages;
                var providerName = (chat.CurrentProvider != null && !string.IsNullOrWhiteSpace(chat.CurrentProvider.displayName))
                    ? chat.CurrentProvider.displayName
                    : (chat.CurrentProvider != null ? chat.CurrentProvider.id : "Unknown");
                var modelName = !string.IsNullOrWhiteSpace(chat.CurrentSessionModel) ? chat.CurrentSessionModel : "Unknown";

                var now = DateTime.Now;
                var sb = new StringBuilder();
                sb.AppendLine("# Chat Export");
                sb.AppendLine();
                sb.AppendLine("**Provider:** " + providerName);
                sb.AppendLine("**Model:** " + modelName);
                sb.AppendLine("**Date:** " + now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();

                for (int i = 0; i < messages.Count; i++)
                {
                    var msg = messages[i];
                    if (msg == null || string.IsNullOrWhiteSpace(msg.content))
                        continue;

                    string role = string.Equals(msg.role, "assistant", StringComparison.OrdinalIgnoreCase)
                        ? "Assistant"
                        : (string.Equals(msg.role, "system", StringComparison.OrdinalIgnoreCase) ? "System" : "User");
                    string model = !string.IsNullOrWhiteSpace(msg.model) ? " (" + msg.model + ")" : "";
                    sb.AppendLine("**" + role + model + ":**");
                    sb.AppendLine(msg.content);
                    sb.AppendLine();
                }

                string fileName = "chat-export-" + now.ToString("yyyy-MM-dd-HHmmss") + ".md";
                string content = sb.ToString();
                string path = System.IO.Path.Combine(Application.persistentDataPath, fileName);
                System.IO.File.WriteAllText(path, content);
                string exportedFile = System.IO.Path.GetFileName(path);

                _d.ShowSystemMessage(LocalizationExtensions.GetFormat("chat.export.success", "Chat exported to {0}.", exportedFile));
            }
            catch (Exception ex)
            {
                _d.ShowSystemMessage(LocalizationExtensions.GetFormat("chat.export.error", "Export error: {0}", ex.Message));
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

        // ===== Export =====

        private void OnExportClicked()
        {
            _ = ExportChatAsync();
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
                {
                    _d.MessageInput.value = string.Empty;
                    QueueComposerHeightUpdate();
                }
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
            DismissCurrentApprovalPrompt();
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
                        DismissCurrentApprovalPrompt();
                        _streamingBubble = null;
                        _streamingLabel = null;
                        StopInlineTypingAnimation();
                        StopStreamingStatsUpdate();
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
                    StopStreamingStatsUpdate();
                    StopInlineTypingAnimation();
                    if (_streamingTypingDots != null)
                    {
                        _streamingTypingDots.RemoveFromHierarchy();
                        _streamingTypingDots = null;
                    }
                    _streamingBubble = null;
                    _streamingLabel = null;
                    DismissCurrentApprovalPrompt();
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

            RenderTranscript(messages);
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

            bool hasSegmentContent = AddMessageSegments(bubble, message);
            if (!hasSegmentContent && !string.IsNullOrWhiteSpace(message.content))
            {
                bool isAssistant = string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase);
                bubble.Add(CreateTranscriptBody(message.content, isAssistant));
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

            // Add hover action buttons for assistant messages
            if (role == "assistant")
            {
                actions = new VisualElement();
                actions.AddToClassList("transcript__bubble-actions");

                var copyBtn = new Button();
                copyBtn.AddToClassList("iconbtn");
                copyBtn.AddToClassList("icon");
                copyBtn.AddToClassList("icon--copy");
                copyBtn.tooltip = "Копировать";
                RegisterClickStatic(copyBtn, () => OnCopyClickedStatic());

                var refreshBtn = new Button();
                refreshBtn.AddToClassList("iconbtn");
                refreshBtn.AddToClassList("icon");
                refreshBtn.AddToClassList("icon--refresh");
                refreshBtn.tooltip = "Пересоздать";
                RegisterClickStatic(refreshBtn, () => OnRegenerateClickedStatic());

                var listenBtn = new Button();
                listenBtn.AddToClassList("iconbtn");
                listenBtn.AddToClassList("icon");
                listenBtn.AddToClassList("icon--headphones");
                listenBtn.tooltip = "Озвучить";
                RegisterClickStatic(listenBtn, () => OnListenClickedStatic());

                actions.Add(copyBtn);
                actions.Add(refreshBtn);
                actions.Add(listenBtn);
            }

            // Actions live INSIDE the bubble (absolutely positioned) so that only
            // hovering the bubble itself reveals them — not the empty space in the row.
            if (actions != null)
                bubble.Add(actions);

            // Stats footer for assistant (after body/attachments in flow; actions are absolute so order ok).
            // Hidden by default; streaming path makes visible + updates it.
            if (role == "assistant")
            {
                var statsFooter = new VisualElement();
                statsFooter.AddToClassList("transcript__stats");
                statsFooter.style.display = DisplayStyle.None;
                var statsLabel = new Label();
                statsLabel.AddToClassList("transcript__stats-label");
                statsFooter.Add(statsLabel);
                bubble.Add(statsFooter);
            }

            row.Add(bubble);

            return row;
        }

        private static bool AddMessageSegments(VisualElement bubble, ChatMessage message)
        {
            if (bubble == null || message == null || message.segments == null || message.segments.Count == 0)
                return false;

            bool added = false;
            for (int i = 0; i < message.segments.Count; i++)
            {
                var segment = message.segments[i];
                if (segment == null)
                    continue;

                if (string.Equals(segment.kind, ChatMessageSegment.ToolKind, StringComparison.OrdinalIgnoreCase))
                {
                    bubble.Add(ToolCallUiHelper.CreateEntryElement(segment.tool, segment.label, segment.emoji, segment.status));
                    added = true;
                    continue;
                }

                if (string.Equals(segment.kind, ChatMessageSegment.TextKind, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(segment.text))
                {
                    bubble.Add(CreateTranscriptBody(segment.text, true));
                    added = true;
                }
            }

            return added;
        }

        private static VisualElement CreateTranscriptBody(string text, bool isAssistant = false)
        {
            if (isAssistant && !string.IsNullOrWhiteSpace(text) && MarkdownRenderer.ContainsMarkdown(text))
            {
                return MarkdownRenderer.Render(text);
            }
            var body = new Label(text);
            body.AddToClassList("transcript__body");
            return body;
        }

        private void OnScrollBottomClicked() { ScrollTranscriptToBottom(); }

        private bool _pinBottomQueued;

        public void ScrollTranscriptToBottom()
        {
            var list = _d.MessagesList;
            if (list == null)
                return;

            var content = list.contentContainer;
            if (content == null)
                return;

            // Try immediately (covers cases where layout is already up to date),
            // then re-pin once the newly added/grown content has actually been
            // laid out — otherwise we measure a stale height and stop short of
            // the just-added message.
            list.schedule.Execute(PinTranscriptToBottom);

            if (_pinBottomQueued)
                return;
            _pinBottomQueued = true;
            content.RegisterCallback<GeometryChangedEvent>(OnTranscriptGeometryForScroll);
        }

        private void OnTranscriptGeometryForScroll(GeometryChangedEvent evt)
        {
            _pinBottomQueued = false;
            _d.MessagesList?.contentContainer?.UnregisterCallback<GeometryChangedEvent>(OnTranscriptGeometryForScroll);
            PinTranscriptToBottom();
        }

        private void PinTranscriptToBottom()
        {
            var list = _d.MessagesList;
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
            SetDisplay(_d.ScrollBottomBtn, DisplayStyle.None);
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

            if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
                return "tool";

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

        // ===== Streaming Stats (token count + elapsed time footer) =====

        private void StartStreamingStatsUpdate()
        {
            // Kill prior schedule without clearing the labels we just assigned for this stream
            if (_statsUpdateSchedule != null)
            {
                _statsUpdateSchedule.Pause();
                _statsUpdateSchedule = null;
            }
            _streamingStartTime = DateTime.UtcNow;
            _estimatedTokens = 0;
            if (_streamingStatsLabel == null)
                return;
            UpdateStreamingStats();
            _statsUpdateSchedule = _streamingStatsLabel.schedule.Execute(() =>
            {
                if (_streamingStatsLabel == null)
                {
                    if (_statsUpdateSchedule != null)
                    {
                        _statsUpdateSchedule.Pause();
                        _statsUpdateSchedule = null;
                    }
                    return;
                }
                UpdateStreamingStats();
            }).Every(500);
        }

        private void StopStreamingStatsUpdate()
        {
            if (_statsUpdateSchedule != null)
            {
                _statsUpdateSchedule.Pause();
                _statsUpdateSchedule = null;
            }
            _streamingStatsLabel = null;
            _streamingStatsFooter = null;
        }

        private void UpdateStreamingStats()
        {
            if (_streamingStatsLabel == null)
                return;
            double elapsed = (DateTime.UtcNow - _streamingStartTime).TotalSeconds;
            if (elapsed < 0)
                elapsed = 0;
            string stats = LocalizationExtensions.GetFormat("chat.stats.footer", "~{0} tok · {1:F1}s", _estimatedTokens, elapsed);
            _streamingStatsLabel.text = stats;
        }

        // ===== Inline Typing Animation =====

        private void StartInlineTypingAnimation()
        {
            _inlineTypingFrame = 0;
            _inlineTypingSchedule?.Pause();
            _inlineTypingSchedule = _d.MessagesList?.schedule.Execute(() =>
            {
                if (_streamingTypingDots == null)
                {
                    _inlineTypingSchedule?.Pause();
                    return;
                }

                var dots = _streamingTypingDots.Query<VisualElement>(className: "typing__dot").ToList();
                for (int i = 0; i < dots.Count; i++)
                {
                    dots[i].style.opacity = i == (_inlineTypingFrame % 3) ? 1f : 0.25f;
                }

                _inlineTypingFrame++;
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

        // ===== Agent Tool Approval (Part B) =====

        private async Task<AppSettings> GetSettingsAsync()
        {
            try
            {
                var app = await _d.GetAppAsync();
                if (app != null && app.Settings != null)
                    return app.Settings.Load();
            }
            catch (Exception ex)
            {
                NeonLogger.LogError("Failed to load settings for tool approval: " + ex);
            }
            return null;
        }

        private async Task SaveSettingsAsync(AppSettings settings)
        {
            try
            {
                var app = await _d.GetAppAsync();
                if (app != null && app.Settings != null && settings != null)
                    app.Settings.Save(settings);
            }
            catch (Exception ex)
            {
                NeonLogger.LogError("Failed to save settings for tool approval: " + ex);
            }
        }

        private async Task<bool> IsToolAlwaysApprovedAsync(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName))
                return false;
            var settings = await GetSettingsAsync();
            if (settings == null || settings.alwaysApprovedTools == null)
                return false;
            for (int i = 0; i < settings.alwaysApprovedTools.Count; i++)
            {
                if (string.Equals(settings.alwaysApprovedTools[i], toolName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private async Task SaveAlwaysApprovedToolAsync(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName))
                return;
            var settings = await GetSettingsAsync();
            if (settings == null)
                return;
            if (settings.alwaysApprovedTools == null)
                settings.alwaysApprovedTools = new List<string>();
            bool exists = false;
            for (int i = 0; i < settings.alwaysApprovedTools.Count; i++)
            {
                if (string.Equals(settings.alwaysApprovedTools[i], toolName, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
            if (!exists)
            {
                settings.alwaysApprovedTools.Add(toolName);
                await SaveSettingsAsync(settings);
            }
        }

        private void DismissCurrentApprovalPrompt()
        {
            if (_currentApprovalElement != null)
            {
                if (_currentApprovalElement.parent != null)
                    _currentApprovalElement.RemoveFromHierarchy();
                _currentApprovalElement = null;
            }
            _currentApprovalPrompt = null;
        }

        private bool IsApprovalRequestStatus(string status)
        {
            if (string.IsNullOrWhiteSpace(status))
                return false;
            if (string.Equals(status, "requesting", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(status, "approval_required", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(status, "pending_approval", StringComparison.OrdinalIgnoreCase))
                return true;
            if (status.IndexOf("request", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return false;
        }

        private Dictionary<string, string> ParseToolArguments(string argumentsJson)
        {
            var result = new Dictionary<string, string>();
            if (string.IsNullOrWhiteSpace(argumentsJson))
                return result;

            try
            {
                int start = argumentsJson.IndexOf('{');
                int end = argumentsJson.LastIndexOf('}');
                if (start < 0 || end <= start)
                    return result;

                string obj = argumentsJson.Substring(start, end - start + 1);
                int pos = 0;
                while (pos < obj.Length)
                {
                    int keyStart = obj.IndexOf('"', pos);
                    if (keyStart < 0)
                        break;
                    int keyEnd = obj.IndexOf('"', keyStart + 1);
                    if (keyEnd < 0)
                        break;
                    string key = obj.Substring(keyStart + 1, keyEnd - keyStart - 1);
                    pos = keyEnd + 1;

                    int colon = obj.IndexOf(':', pos);
                    if (colon < 0)
                        break;
                    pos = colon + 1;

                    while (pos < obj.Length && char.IsWhiteSpace(obj[pos]))
                        pos++;

                    if (pos >= obj.Length || obj[pos] != '"')
                    {
                        pos++;
                        continue;
                    }

                    int valStart = pos + 1;
                    int valEnd = obj.IndexOf('"', valStart);
                    if (valEnd < 0)
                        break;

                    string val = obj.Substring(valStart, valEnd - valStart);
                    // basic unescape
                    val = val.Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\\"", "\"").Replace("\\\\", "\\");
                    result[key] = val;
                    pos = valEnd + 1;
                }
            }
            catch { /* ignore parse errors, return what we have */ }

            return result;
        }

        private async Task HandleApprovalRequestAsync(ToolCallRequest request)
        {
            if (request == null)
                return;
            if (_currentApprovalPrompt != null)
                return; // one at a time

            bool approved = await RequestToolApproval(request);
            if (!approved)
            {
                // Reject: stop generation to pause tool execution (server-side protocol is future work)
                try
                {
                    if (_isSending || _isStreamingResponse)
                    {
                        OnStopClicked();
                    }
                }
                catch (Exception ex)
                {
                    NeonLogger.LogError("Error stopping on tool reject: " + ex);
                }
            }
        }

        private async Task<bool> RequestToolApproval(ToolCallRequest request)
        {
            if (request == null)
                return true;

            var settings = await GetSettingsAsync();
            if (settings != null && string.Equals(settings.toolPermissionMode, "auto", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (await IsToolAlwaysApprovedAsync(request.toolName))
                return true;

            var prompt = new ApprovalPrompt();
            var approvalElement = prompt.Create(request);
            _currentApprovalPrompt = prompt;
            _currentApprovalElement = approvalElement;
            _d.MessagesList.Add(approvalElement);
            ScrollTranscriptToBottom();

            bool approved = false;
            bool always = false;
            var completionSource = new TaskCompletionSource<bool>();

            prompt.OnDecision += (a, alwaysApprove) =>
            {
                approved = a;
                always = alwaysApprove;
                completionSource.TrySetResult(true);
            };

            await completionSource.Task;

            if (approvalElement != null && approvalElement.parent != null)
                approvalElement.RemoveFromHierarchy();

            _currentApprovalPrompt = null;
            _currentApprovalElement = null;

            if (always && approved)
            {
                await SaveAlwaysApprovedToolAsync(request.toolName);
            }

            return approved;
        }

        private async Task ProcessAgentToolLoopAsync(ChatService chat, bool originalStreaming)
        {
            if (chat == null || chat.CurrentChatViewModel == null)
                return;

            int iterations = 0;
            bool streamingMode = originalStreaming;

            // Force non-streaming for tool resolution turns to keep approval UX simple and avoid nested streams
            chat.UseStreaming = false;

            try
            {
                while (iterations < MaxToolIterations)
                {
                    var vm = chat.CurrentChatViewModel;
                    if (vm == null || vm.Messages.Count == 0)
                        break;

                    var last = vm.Messages[vm.Messages.Count - 1];
                    if (last == null ||
                        !string.Equals(last.role, "assistant", StringComparison.OrdinalIgnoreCase) ||
                        last.tool_calls == null ||
                        last.tool_calls.Count == 0)
                    {
                        break;
                    }

                    iterations++;

                    bool executedAny = false;
                    for (int i = 0; i < last.tool_calls.Count; i++)
                    {
                        var tc = last.tool_calls[i];
                        if (tc == null || tc.function == null)
                            continue;

                        var parameters = ParseToolArguments(tc.function.arguments);

                        var request = new ToolCallRequest
                        {
                            id = !string.IsNullOrEmpty(tc.id) ? tc.id : Guid.NewGuid().ToString("N"),
                            toolName = tc.function.name ?? string.Empty,
                            description = tc.function.name ?? LocalizationExtensions.Get("tool.default", "tool"),
                            parameters = parameters
                        };

                        bool approved = await RequestToolApproval(request);
                        if (!approved)
                        {
                            _d.ShowSystemMessage(LocalizationExtensions.Get("tool.approval.denied", "Tool execution denied."));
                            return;
                        }

                        string result = ToolExecutor.Execute(tc.function.name, parameters);
                        if (result != null && result.Length > 10000)
                            result = result.Substring(0, 10000) + "\n... [truncated]";

                        vm.Messages.Add(new ChatMessage
                        {
                            role = "tool",
                            content = result ?? string.Empty,
                            tool_call_id = tc.id ?? string.Empty,
                            unixTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        });

                        executedAny = true;
                    }

                    if (!executedAny)
                        break;

                    // Continue generation with tool results in history (no new user message)
                    try
                    {
                        DismissCurrentApprovalPrompt();
                        _toolCallUiHelper.Clear();

                        await chat.RegenerateAsync(null, null);

                        _d.RenderMessages(chat.CurrentChatViewModel?.Messages);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _d.ShowSystemMessage(LocalizationExtensions.Get("tool.error.loop", "Tool loop error: ") + ex.Message);
                        break;
                    }
                }

                if (iterations >= MaxToolIterations)
                {
                    _d.ShowSystemMessage(LocalizationExtensions.Get("tool.loop.max", "Max tool iterations reached."));
                }
            }
            finally
            {
                chat.UseStreaming = originalStreaming;
            }
        }

        // ===== Static events for bubble action buttons =====

        internal static event Action CopyRequested;
        internal static event Action RegenerateRequested;
        internal static event Action ListenRequested;

        private static void OnCopyClickedStatic() => CopyRequested?.Invoke();
        private static void OnRegenerateClickedStatic() => RegenerateRequested?.Invoke();
        private static void OnListenClickedStatic() => ListenRequested?.Invoke();

        private static void RegisterClickStatic(Button button, Action handler)
        {
            if (button != null)
                button.clicked += handler;
        }
    }
}
