using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Api;
using NeonCompanion.Runtime.Api.Tools;
using NeonCompanion.Runtime.Avatar;
using NeonCompanion.Runtime.Chat;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Localization;
using NeonCompanion.Runtime.Platform;
using NeonCompanion.Runtime.Voice;
using UnityEngine;
using UnityEngine.Networking;
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
            public Action ShowNotificationBadge;
            public Action HideNotificationBadge;
        }

        private class QueuedMessage
        {
            public string Message;
            public List<ChatAttachment> Attachments;
        }

        private Deps _d;
        private bool _isSending;
        private bool _isStreamingResponse;
        private bool _isVoiceRecording;
        private bool _isDragOver;
        private bool _hasUnreadNotification;
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
        private VisualElement _composerPreviews;

        // Message queue (U-45)
        private readonly Queue<QueuedMessage> _messageQueue = new Queue<QueuedMessage>();
        private Label _queueIndicator;

        // Context window indicator (U-36)
        private VisualElement _contextBar;
        private VisualElement _contextBarFill;
        private Label _contextBarLabel;

        // Message context menu (U-29/U-30)
        private MessageContextMenu _contextMenu;

        // Long-press state for mobile context menu
        private VisualElement _longPressTarget;
        private int _longPressIndex;
        private bool _longPressIsUser;
        private Vector2 _longPressPos;
        private IVisualElementScheduledItem _longPressSchedule;

        // Inline edit state
        private int? _editingMessageIndex;
        private VisualElement _editingBubble;
        private VisualElement _editingContainer;
        private TextField _editingTextField;
        private Button _editingSaveBtn;
        private Button _editingCancelBtn;

        // Chat search (U-38)
        private string _searchQuery = string.Empty;
        private int _currentMatchIndex = -1;
        private List<int> _matchingMessageIndices = new List<int>();
        private VisualElement _searchBar;
        private TextField _searchInput;
        private Label _searchCountLabel;
        private Button _searchUpBtn;
        private Button _searchDownBtn;
        private Button _searchCloseBtn;

        // Message selection mode (U-31/U-32)
        private bool _isSelectionMode;
        private readonly HashSet<int> _selectedMessages = new HashSet<int>();
        private VisualElement _selectionBar;
        private Label _selectionCountLabel;

        private const float ComposerInputMinHeight = 36f;
        private const float ComposerInputMaxHeight = 140f;
        private const float ComposerInputVerticalPadding = 12f;

        private const int MaxToolIterations = 10;

        public bool IsSending => _isSending;
        public bool IsStreamingResponse => _isStreamingResponse;
        public string ChatSubtitle => _chatSubtitle;
        public string SessionSearchQuery => _sessionSearchQuery;

        public void SetDeps(Deps deps)
        {
            _d = deps;
            if (_contextMenu == null)
                _contextMenu = new MessageContextMenu();

            _d.ShowNotificationBadge = ShowNotificationBadge;
            _d.HideNotificationBadge = HideNotificationBadge;
        }

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

            if (_contextMenu != null)
            {
                _contextMenu.OnEditRequested += OnEditMessageRequested;
                _contextMenu.OnDeleteRequested += OnDeleteMessageRequested;
                _contextMenu.OnCopyRequested += OnCopyMessageRequested;
                _contextMenu.OnSelectRequested += OnSelectMessageRequested;
            }

            // Context menu triggers on transcript (right-click + long-press)
            if (_d.MessagesList != null)
            {
                _d.MessagesList.RegisterCallback<PointerDownEvent>(OnTranscriptPointerDown, TrickleDown.TrickleDown);
                _d.MessagesList.RegisterCallback<PointerUpEvent>(OnTranscriptPointerUp);
                _d.MessagesList.RegisterCallback<PointerCancelEvent>(OnTranscriptPointerCancel);
            }

            if (_d.MessageInput != null)
            {
                _d.MessageInput.RegisterCallback<KeyDownEvent>(OnInputKeyDown, TrickleDown.TrickleDown);
                _d.MessageInput.RegisterCallback<FocusEvent>(_ => _d.Composer?.AddToClassList("composer--focused"));
                _d.MessageInput.RegisterCallback<BlurEvent>(_ => _d.Composer?.RemoveFromClassList("composer--focused"));
                _d.MessageInput.RegisterCallback<ChangeEvent<string>>(OnComposerTextChanged);
                _d.MessageInput.RegisterCallback<GeometryChangedEvent>(OnComposerGeometryChanged);
                QueueComposerHeightUpdate();
            }

            // Create preview strip dynamically — inserted BEFORE composer in parent column
            if (_d.Composer?.parent != null)
            {
                _composerPreviews = new VisualElement();
                _composerPreviews.name = "composer-previews";
                _composerPreviews.AddToClassList("composer__previews");
                _composerPreviews.style.display = DisplayStyle.None;
                int composerIndex = _d.Composer.parent.IndexOf(_d.Composer);
                _d.Composer.parent.Insert(composerIndex, _composerPreviews);
            }

            // Context window indicator
            _contextBar = new VisualElement();
            _contextBar.name = "context-bar";
            _contextBar.AddToClassList("context-bar");
            _contextBar.style.display = DisplayStyle.None;

            _contextBarFill = new VisualElement();
            _contextBarFill.name = "context-bar__fill";
            _contextBarFill.AddToClassList("context-bar__fill");
            _contextBar.Add(_contextBarFill);

            _contextBarLabel = new Label();
            _contextBarLabel.AddToClassList("context-bar__label");
            _contextBar.Add(_contextBarLabel);

            // Insert AFTER composer in chat-main (parent is column)
            if (_d.Composer?.parent != null)
            {
                _d.Composer.parent.Add(_contextBar);
            }

            // Message queue indicator (U-45) — created dynamically, sibling after composer
            _queueIndicator = new Label();
            _queueIndicator.name = "queue-indicator";
            _queueIndicator.AddToClassList("queue-indicator");
            _queueIndicator.style.display = DisplayStyle.None;
            if (_d.Composer?.parent != null)
                _d.Composer.parent.Add(_queueIndicator);

            // Selection action bar (U-31/U-32) — created dynamically, sibling after composer
            _selectionBar = new VisualElement();
            _selectionBar.name = "selection-bar";
            _selectionBar.AddToClassList("selection-bar");
            _selectionBar.style.display = DisplayStyle.None;

            _selectionCountLabel = new Label();
            _selectionCountLabel.AddToClassList("selection-bar__count");
            _selectionBar.Add(_selectionCountLabel);

            var deleteBtn = new Button(OnDeleteSelected) { text = LocalizationExtensions.Get("chat.selection.delete", "Delete") };
            deleteBtn.AddToClassList("selection-bar__btn selection-bar__btn--danger");
            _selectionBar.Add(deleteBtn);

            var cancelBtn = new Button(ExitSelectionMode) { text = LocalizationExtensions.Get("chat.selection.cancel", "Cancel") };
            cancelBtn.AddToClassList("selection-bar__btn");
            _selectionBar.Add(cancelBtn);

            if (_d.Composer?.parent != null)
                _d.Composer.parent.Add(_selectionBar);

            // Drag-and-drop file support (U-44)
            var chatMain = _d.Composer?.parent;
            if (chatMain != null)
            {
                chatMain.RegisterCallback<DragUpdatedEvent>(OnDragUpdated);
                chatMain.RegisterCallback<DragPerformEvent>(OnDragPerform);
                chatMain.RegisterCallback<DragLeaveEvent>(OnDragLeave);
            }

            Application.focusChanged += OnApplicationFocusChanged;
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

            if (_contextMenu != null)
            {
                _contextMenu.OnEditRequested -= OnEditMessageRequested;
                _contextMenu.OnDeleteRequested -= OnDeleteMessageRequested;
                _contextMenu.OnCopyRequested -= OnCopyMessageRequested;
                _contextMenu.OnSelectRequested -= OnSelectMessageRequested;
            }

            if (_d.MessagesList != null)
            {
                _d.MessagesList.UnregisterCallback<PointerDownEvent>(OnTranscriptPointerDown, TrickleDown.TrickleDown);
                _d.MessagesList.UnregisterCallback<PointerUpEvent>(OnTranscriptPointerUp);
                _d.MessagesList.UnregisterCallback<PointerCancelEvent>(OnTranscriptPointerCancel);
            }

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
            if (_contextMenu != null)
                _contextMenu.Hide();
            CancelInlineEdit();
            CloseSearch();
            if (_searchBar != null)
            {
                _searchBar.RemoveFromHierarchy();
                _searchBar = null;
                _searchInput = null;
                _searchCountLabel = null;
                _searchUpBtn = null;
                _searchDownBtn = null;
                _searchCloseBtn = null;
            }
            _streamingBubble = null;
            _streamingLabel = null;
            StopInlineTypingAnimation();
            StopStreamingStatsUpdate();
            if (_streamingTypingDots != null)
            {
                _streamingTypingDots.RemoveFromHierarchy();
                _streamingTypingDots = null;
            }

            _messageQueue.Clear();
            if (_queueIndicator != null)
            {
                _queueIndicator.RemoveFromHierarchy();
                _queueIndicator = null;
            }

            if (_selectionBar != null)
            {
                _selectionBar.RemoveFromHierarchy();
                _selectionBar = null;
                _selectionCountLabel = null;
            }

            // Drag-and-drop file support (U-44)
            var chatMain = _d.Composer?.parent;
            if (chatMain != null)
            {
                chatMain.UnregisterCallback<DragUpdatedEvent>(OnDragUpdated);
                chatMain.UnregisterCallback<DragPerformEvent>(OnDragPerform);
                chatMain.UnregisterCallback<DragLeaveEvent>(OnDragLeave);
            }

            Application.focusChanged -= OnApplicationFocusChanged;
        }

        public void InitState()
        {
            SetSending(false);
        }

        // ===== Input =====

        private void OnInputKeyDown(KeyDownEvent evt)
        {
            bool hasCtrl = evt.ctrlKey || evt.commandKey;

            // Ctrl+V paste image
            if (hasCtrl && evt.keyCode == KeyCode.V)
            {
                evt.StopPropagation();
                _ = PasteImageFromClipboardAsync();
                return;
            }

            if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter)
                return;

            bool enterToSend = _d.EnterToSend();

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
            if (_contextMenu != null)
                _contextMenu.Hide();
            CancelInlineEdit();
            _currentChatService?.CancelCurrentGeneration();
        }

        public async Task SendCurrentMessageAsync()
        {
            HideNotificationBadge();
            DismissCurrentApprovalPrompt();
            if (_contextMenu != null)
                _contextMenu.Hide();
            CancelInlineEdit();

            bool hasPendingAttachments = _pendingComposerAttachments.Count > 0;
            if (_d.MessageInput == null)
                return;

            string composerText = (_d.MessageInput.value ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(composerText) && !hasPendingAttachments)
                return;

            if (await TryHandleCommandAsync(composerText))
            {
                _d.MessageInput.value = string.Empty;
                QueueComposerHeightUpdate();
                return;
            }

            // If currently sending, queue the message instead (commands already handled above and execute immediately)
            if (_isSending)
            {
                var qAttach = CloneAttachments(_pendingComposerAttachments);
                string qMsg = StripAttachmentTokens(composerText, qAttach);
                _messageQueue.Enqueue(new QueuedMessage { Message = qMsg, Attachments = qAttach });
                _d.MessageInput.value = string.Empty;
                QueueComposerHeightUpdate();
                ClearPendingComposerAttachments();
                RenderQueueIndicator();
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

                    // After streaming completes, get real usage from client (adapted from suggested chat.CurrentProvider.GetClient pattern)
                    // Use exact token count if provided via stream_options; set final stats without ~ estimate
                    Label statsLabelForFinal = _streamingStatsLabel;
                    if (_statsUpdateSchedule != null)
                    {
                        _statsUpdateSchedule.Pause();
                        _statsUpdateSchedule = null;
                    }
                    try
                    {
                        var app = _d.GetAppAsync().Result;
                        var client = app?.AiClient as OpenAiCompatibleClient;
                        if (client != null)
                        {
                            var usage = client.LastStreamUsage;
                            if (usage.total_tokens > 0)
                            {
                                _estimatedTokens = usage.total_tokens;
                                if (statsLabelForFinal != null)
                                {
                                    double elapsed = (DateTime.UtcNow - _streamingStartTime).TotalSeconds;
                                    if (elapsed < 0)
                                        elapsed = 0;
                                    string template = LocalizationExtensions.Get("chat.stats.footer", "~{0} tok · {1:F1}s");
                                    string exactTemplate = template.Replace("~", string.Empty);
                                    statsLabelForFinal.text = string.Format(exactTemplate, _estimatedTokens, elapsed);
                                }
                            }
                        }
                    }
                    catch
                    {
                        // keep estimate if client not available or no usage chunk
                    }

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

                // Show notification badge if window not focused
                if (!Application.isFocused)
                {
                    _hasUnreadNotification = true;
                    _d.ShowNotificationBadge?.Invoke();
                }

                // Process queued messages
                if (_messageQueue.Count > 0)
                {
                    var next = _messageQueue.Dequeue();
                    RenderQueueIndicator();
                    // Set composer text and attachments, then send
                    _d.MessageInput.value = next.Message;
                    if (next.Attachments != null)
                    {
                        for (int i = 0; i < next.Attachments.Count; i++)
                            _pendingComposerAttachments.Add(next.Attachments[i]);
                    }
                    QueueComposerHeightUpdate();
                    // Trigger send
                    _ = SendCurrentMessageAsync();
                    return;
                }
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
                _messageQueue.Clear();
                RenderQueueIndicator();
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
            _streamingLabel.focusable = true;

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

        private void ShowNotificationBadge()
        {
            if (_d.NavChatCount != null)
            {
                _d.NavChatCount.AddToClassList("nav__count--notify");
            }
        }

        private void HideNotificationBadge()
        {
            if (_d.NavChatCount != null)
            {
                _d.NavChatCount.RemoveFromClassList("nav__count--notify");
            }
        }

        private void OnApplicationFocusChanged(bool hasFocus)
        {
            if (hasFocus && _hasUnreadNotification)
            {
                _hasUnreadNotification = false;
                HideNotificationBadge();
            }
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

        // ===== Search (U-38: in-chat transcript search) =====

        private void OnSearchClicked()
        {
            if (_searchBar != null && _searchBar.style.display != DisplayStyle.None)
            {
                CloseSearch();
                return;
            }
            ShowSearchBar();
        }

        private void ShowSearchBar()
        {
            EnsureSearchBarCreated();
            if (_searchBar == null)
                return;

            var list = _d.MessagesList;
            if (list != null)
            {
                var parent = list.parent as VisualElement;
                if (parent != null && _searchBar.parent != parent)
                {
                    parent.Insert(0, _searchBar);
                }
            }

            _searchBar.style.display = DisplayStyle.Flex;

            if (_searchInput != null)
            {
                _searchInput.value = _searchQuery ?? string.Empty;
                _searchInput.Focus();
                _searchInput.schedule.Execute(() =>
                {
                    if (_searchInput != null && _searchInput.panel != null)
                        _searchInput.SelectAll();
                }).StartingIn(60);
            }

            if (!string.IsNullOrEmpty(_searchQuery))
            {
                FindMatches();
                HighlightMatches();
                ScrollToCurrentMatch();
            }
            else
            {
                UpdateSearchCountLabel();
            }
        }

        private void EnsureSearchBarCreated()
        {
            if (_searchBar != null)
                return;

            _searchBar = new VisualElement();
            _searchBar.AddToClassList("search-bar");
            _searchBar.style.display = DisplayStyle.None;

            var icon = new VisualElement();
            icon.AddToClassList("icon");
            icon.AddToClassList("icon--search");
            icon.style.width = 14;
            icon.style.height = 14;
            icon.style.marginLeft = 4;
            icon.style.marginRight = 4;
            icon.style.flexShrink = 0;
            _searchBar.Add(icon);

            _searchInput = new TextField();
            _searchInput.AddToClassList("search-bar__input");
            _searchInput.RegisterValueChangedCallback(evt => OnSearchQueryChanged(evt.newValue));
            _searchInput.RegisterCallback<KeyDownEvent>(OnSearchInputKeyDown, TrickleDown.TrickleDown);
            _searchBar.Add(_searchInput);

            _searchCountLabel = new Label("0/0");
            _searchCountLabel.AddToClassList("search-bar__count");
            _searchBar.Add(_searchCountLabel);

            _searchUpBtn = new Button(GoToPrevMatch);
            _searchUpBtn.text = "\u2191";
            _searchUpBtn.AddToClassList("iconbtn");
            _searchUpBtn.style.width = 22;
            _searchUpBtn.style.height = 22;
            _searchUpBtn.style.fontSize = 11;
            _searchUpBtn.tooltip = LocalizationExtensions.Get("chat.search.previous", "Previous match");
            _searchBar.Add(_searchUpBtn);

            _searchDownBtn = new Button(GoToNextMatch);
            _searchDownBtn.text = "\u2193";
            _searchDownBtn.AddToClassList("iconbtn");
            _searchDownBtn.style.width = 22;
            _searchDownBtn.style.height = 22;
            _searchDownBtn.style.fontSize = 11;
            _searchDownBtn.tooltip = LocalizationExtensions.Get("chat.search.next", "Next match");
            _searchBar.Add(_searchDownBtn);

            _searchCloseBtn = new Button(CloseSearch);
            _searchCloseBtn.text = "\u2715";
            _searchCloseBtn.AddToClassList("iconbtn");
            _searchCloseBtn.style.width = 22;
            _searchCloseBtn.style.height = 22;
            _searchCloseBtn.style.fontSize = 11;
            _searchCloseBtn.tooltip = LocalizationExtensions.Get("chat.search.close", "Close search");
            _searchBar.Add(_searchCloseBtn);
        }

        private void OnSearchInputKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode == KeyCode.Escape)
            {
                CloseSearch();
                evt.StopPropagation();
            }
            else if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
            {
                GoToNextMatch();
                evt.StopPropagation();
            }
        }

        private bool IsSearchBarVisible()
        {
            return _searchBar != null && _searchBar.style.display != DisplayStyle.None;
        }

        private void CloseSearch()
        {
            _searchQuery = string.Empty;
            _currentMatchIndex = -1;
            _matchingMessageIndices.Clear();

            if (_searchBar != null)
            {
                _searchBar.style.display = DisplayStyle.None;
            }
            if (_searchInput != null)
            {
                _searchInput.value = string.Empty;
            }

            ClearSearchHighlights();
            UpdateSearchCountLabel();
        }

        private void ClearSearchHighlights()
        {
            if (_d.MessagesList == null)
                return;

            foreach (var child in _d.MessagesList.Children())
            {
                var ve = child as VisualElement;
                if (ve != null)
                {
                    ve.RemoveFromClassList("transcript__row--search-match");
                    ve.RemoveFromClassList("transcript__row--search-current");
                }
            }
        }

        private void OnSearchQueryChanged(string query)
        {
            _searchQuery = query ?? string.Empty;
            FindMatches();
            HighlightMatches();
            ScrollToCurrentMatch();
        }

        private void FindMatches()
        {
            _matchingMessageIndices.Clear();

            if (string.IsNullOrEmpty(_searchQuery))
            {
                _currentMatchIndex = -1;
                return;
            }

            var messages = GetCurrentMessages();
            if (messages == null)
            {
                _currentMatchIndex = -1;
                return;
            }

            for (int i = 0; i < messages.Count; i++)
            {
                var m = messages[i];
                if (m != null &&
                    !string.IsNullOrEmpty(m.content) &&
                    m.content.IndexOf(_searchQuery, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _matchingMessageIndices.Add(i);
                }
            }

            _currentMatchIndex = _matchingMessageIndices.Count > 0 ? 0 : -1;
        }

        private IReadOnlyList<ChatMessage> GetCurrentMessages()
        {
            try
            {
                var chatTask = _d.GetChatServiceAsync();
                if (chatTask == null)
                    return null;
                // Safe .Result pattern used elsewhere in this controller for UI sync paths
                var chat = chatTask.Result;
                return chat != null ? chat.CurrentChatViewModel?.Messages : null;
            }
            catch
            {
                return null;
            }
        }

        private void HighlightMatches()
        {
            if (_d.MessagesList == null)
                return;

            ClearSearchHighlights();

            if (string.IsNullOrEmpty(_searchQuery) || _matchingMessageIndices.Count == 0)
            {
                UpdateSearchCountLabel();
                return;
            }

            int currentMsgIdx = -1;
            if (_currentMatchIndex >= 0 && _currentMatchIndex < _matchingMessageIndices.Count)
            {
                currentMsgIdx = _matchingMessageIndices[_currentMatchIndex];
            }

            foreach (var child in _d.MessagesList.Children())
            {
                var ve = child as VisualElement;
                if (ve != null && ve.userData is int msgIdx)
                {
                    bool isMatch = false;
                    for (int k = 0; k < _matchingMessageIndices.Count; k++)
                    {
                        if (_matchingMessageIndices[k] == msgIdx)
                        {
                            isMatch = true;
                            break;
                        }
                    }
                    if (isMatch)
                    {
                        ve.AddToClassList("transcript__row--search-match");
                        if (msgIdx == currentMsgIdx)
                        {
                            ve.AddToClassList("transcript__row--search-current");
                        }
                    }
                }
            }

            UpdateSearchCountLabel();
        }

        private void UpdateSearchCountLabel()
        {
            if (_searchCountLabel == null)
                return;

            int total = _matchingMessageIndices != null ? _matchingMessageIndices.Count : 0;
            int cur = (total > 0 && _currentMatchIndex >= 0) ? (_currentMatchIndex + 1) : 0;
            _searchCountLabel.text = total > 0 ? (cur + "/" + total) : "0/0";
        }

        private void GoToNextMatch()
        {
            if (_matchingMessageIndices == null || _matchingMessageIndices.Count == 0)
                return;

            int count = _matchingMessageIndices.Count;
            if (_currentMatchIndex < 0)
                _currentMatchIndex = 0;
            else
                _currentMatchIndex = (_currentMatchIndex + 1) % count;

            HighlightMatches();
            ScrollToCurrentMatch();
        }

        private void GoToPrevMatch()
        {
            if (_matchingMessageIndices == null || _matchingMessageIndices.Count == 0)
                return;

            int count = _matchingMessageIndices.Count;
            if (_currentMatchIndex < 0)
                _currentMatchIndex = count - 1;
            else
                _currentMatchIndex = (_currentMatchIndex - 1 + count) % count;

            HighlightMatches();
            ScrollToCurrentMatch();
        }

        private void ScrollToCurrentMatch()
        {
            if (_d.MessagesList == null || _currentMatchIndex < 0 || _matchingMessageIndices == null || _currentMatchIndex >= _matchingMessageIndices.Count)
                return;

            int targetMsgIdx = _matchingMessageIndices[_currentMatchIndex];
            VisualElement targetRow = null;

            foreach (var child in _d.MessagesList.Children())
            {
                if (child.userData is int idx && idx == targetMsgIdx)
                {
                    targetRow = child;
                    break;
                }
            }

            if (targetRow == null)
                return;

            try
            {
                _d.MessagesList.ScrollTo(targetRow);
            }
            catch
            {
                // Fallback for older UITK: approximate offset
                var content = _d.MessagesList.contentContainer;
                if (content != null)
                {
                    float y = targetRow.layout.y - 60f;
                    if (y < 0f) y = 0f;
                    _d.MessagesList.scrollOffset = new Vector2(0f, y);
                }
            }
        }

        // ===== Attachments =====

        private Task PasteImageFromClipboardAsync()
        {
            try
            {
                string clipboard = GUIUtility.systemCopyBuffer;
                if (string.IsNullOrEmpty(clipboard))
                    return Task.CompletedTask;

                // Check if clipboard contains a file path to an image
                string[] imageExts = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };
                string ext = System.IO.Path.GetExtension(clipboard)?.ToLowerInvariant();
                if (string.IsNullOrEmpty(ext))
                    return Task.CompletedTask;

                bool isImage = false;
                for (int i = 0; i < imageExts.Length; i++)
                {
                    if (ext == imageExts[i]) { isImage = true; break; }
                }
                if (!isImage)
                    return Task.CompletedTask;

                // Check file exists
                if (!System.IO.File.Exists(clipboard))
                    return Task.CompletedTask;

                string fileName = System.IO.Path.GetFileName(clipboard);
                _pendingComposerAttachments.Add(new ChatAttachment
                {
                    kind = "image",
                    name = fileName,
                    path = clipboard,
                    mediaType = GuessImageMediaType(clipboard)
                });

                RenderComposerPreviews();
                _d.MessageInput?.Focus();
            }
            catch (Exception ex)
            {
                NeonLogger.LogError("Paste image failed: " + ex.ToString());
            }
            return Task.CompletedTask;
        }

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

                RenderComposerPreviews();

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
            RenderComposerPreviews();
        }

        private void RestorePendingComposerAttachments(IReadOnlyList<ChatAttachment> attachments)
        {
            _pendingComposerAttachments.Clear();
            var restored = CloneAttachments(attachments);
            for (int i = 0; i < restored.Count; i++)
                _pendingComposerAttachments.Add(restored[i]);
        }

        private void RenderComposerPreviews()
        {
            if (_composerPreviews == null) return;
            _composerPreviews.Clear();

            if (_pendingComposerAttachments.Count == 0)
            {
                _composerPreviews.style.display = DisplayStyle.None;
                return;
            }

            _composerPreviews.style.display = DisplayStyle.Flex;

            for (int i = 0; i < _pendingComposerAttachments.Count; i++)
            {
                var attachment = _pendingComposerAttachments[i];
                if (attachment == null) continue;

                int index = i; // capture for closure
                var thumb = new VisualElement();
                thumb.AddToClassList("composer__preview-thumb");

                if (!string.IsNullOrEmpty(attachment.path) && System.IO.File.Exists(attachment.path))
                {
                    var img = new Image();
                    img.AddToClassList("composer__preview-img");
                    img.scaleMode = ScaleMode.ScaleToFit;
                    img.schedule.Execute(() => LoadImageAsync(img, attachment.path));
                    thumb.Add(img);
                }

                var removeBtn = new Button(() => RemovePendingAttachment(index));
                removeBtn.text = "×";
                removeBtn.AddToClassList("composer__preview-remove");
                removeBtn.tooltip = LocalizationExtensions.Get("chat.preview.remove", "Убрать");
                thumb.Add(removeBtn);

                _composerPreviews.Add(thumb);
            }
        }

        private void RenderQueueIndicator()
        {
            if (_queueIndicator == null) return;
            if (_messageQueue.Count > 0)
            {
                _queueIndicator.style.display = DisplayStyle.Flex;
                _queueIndicator.text = LocalizationExtensions.Get("chat.queue.pending", "Очередь: {0}")
                    .Replace("{0}", _messageQueue.Count.ToString());
            }
            else
            {
                _queueIndicator.style.display = DisplayStyle.None;
            }
        }

        private void RemovePendingAttachment(int index)
        {
            if (index < 0 || index >= _pendingComposerAttachments.Count) return;
            _pendingComposerAttachments.RemoveAt(index);

            // Also remove the [attachment: ...] token from the text input
            // Simple approach: rebuild tokens from remaining attachments
            string text = _d.MessageInput?.value ?? string.Empty;
            string rebuilt = BuildComposerTextWithAttachments(text, _pendingComposerAttachments);
            if (_d.MessageInput != null)
                _d.MessageInput.value = rebuilt;

            RenderComposerPreviews();
        }

        private static string BuildComposerTextWithAttachments(string composerText, IReadOnlyList<ChatAttachment> attachments)
        {
            string text = StripAllAttachmentTokens(composerText ?? string.Empty).Trim();
            if (attachments != null)
            {
                for (int i = 0; i < attachments.Count; i++)
                {
                    var a = attachments[i];
                    if (a == null || string.IsNullOrWhiteSpace(a.name))
                        continue;
                    string token = $"[attachment: {a.name}]";
                    if (text.IndexOf(token, StringComparison.Ordinal) < 0)
                    {
                        if (!string.IsNullOrEmpty(text))
                            text += " ";
                        text += token;
                    }
                }
            }
            return text;
        }

        private static string StripAllAttachmentTokens(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            int idx;
            while ((idx = text.IndexOf("[attachment: ", StringComparison.Ordinal)) >= 0)
            {
                int end = text.IndexOf(']', idx + 13);
                if (end < 0)
                    break;
                text = text.Remove(idx, end - idx + 1);
            }
            return text;
        }

        // ===== Drag and Drop (U-44) =====

        private void OnDragUpdated(DragUpdatedEvent evt)
        {
            if (!HasValidDragFiles(evt))
                return;

            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (!_isDragOver)
            {
                _isDragOver = true;
                _d.Composer?.parent?.AddToClassList("chat-main--drag-over");
            }
            evt.StopPropagation();
        }

        private void OnDragPerform(DragPerformEvent evt)
        {
            _isDragOver = false;
            _d.Composer?.parent?.RemoveFromClassList("chat-main--drag-over");

            if (!HasValidDragFiles(evt))
                return;

            string[] paths = DragAndDrop.paths;
            if (paths == null || paths.Length == 0)
                return;

            for (int i = 0; i < paths.Length; i++)
            {
                string path = paths[i];
                if (string.IsNullOrEmpty(path))
                    continue;

                // Skip non-file entries (folders, etc.)
                if (!System.IO.File.Exists(path))
                    continue;

                string ext = System.IO.Path.GetExtension(path)?.ToLowerInvariant() ?? string.Empty;
                if (IsSupportedFile(ext))
                {
                    if (IsImageFile(ext) && !IsFileSizeOk(path))
                        continue;

                    _pendingComposerAttachments.Add(new ChatAttachment
                    {
                        kind = IsImageFile(ext) ? "image" : "file",
                        name = System.IO.Path.GetFileName(path),
                        path = path,
                        mediaType = GuessImageMediaType(path)
                    });
                }
            }

            RenderComposerPreviews();
            evt.StopPropagation();
        }

        private void OnDragLeave(DragLeaveEvent evt)
        {
            _isDragOver = false;
            _d.Composer?.parent?.RemoveFromClassList("chat-main--drag-over");
            evt.StopPropagation();
        }

        private static bool HasValidDragFiles(DragUpdatedEvent evt)
        {
            return DragAndDrop.paths != null && DragAndDrop.paths.Length > 0;
        }

        private static bool HasValidDragFiles(DragPerformEvent evt)
        {
            return DragAndDrop.paths != null && DragAndDrop.paths.Length > 0;
        }

        private static bool IsSupportedFile(string ext)
        {
            // Images
            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".bmp" || ext == ".webp")
                return true;
            // Documents
            if (ext == ".pdf" || ext == ".txt" || ext == ".md" || ext == ".json" || ext == ".xml" || ext == ".csv")
                return true;
            // Code
            if (ext == ".cs" || ext == ".py" || ext == ".js" || ext == ".ts" || ext == ".java" || ext == ".cpp" || ext == ".h" || ext == ".csproj")
                return true;
            return false;
        }

        private static bool IsImageFile(string ext)
        {
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".bmp" || ext == ".webp";
        }

        private static bool IsFileSizeOk(string path, long maxSizeBytes = 10 * 1024 * 1024)
        {
            try
            {
                var info = new System.IO.FileInfo(path);
                return info.Length <= maxSizeBytes;
            }
            catch
            {
                return false;
            }
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
                if (_contextMenu != null)
                    _contextMenu.Hide();
                CancelInlineEdit();

                var chat = await _d.GetChatServiceAsync();
                if (chat == null)
                    return;

                await chat.StartNewSessionAsync();
                ClearPendingComposerAttachments();
                _messageQueue.Clear();
                RenderQueueIndicator();
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

                        // After streaming completes (regenerate path), get real usage from client
                        Label statsLabelForFinal = _streamingStatsLabel;
                        if (_statsUpdateSchedule != null)
                        {
                            _statsUpdateSchedule.Pause();
                            _statsUpdateSchedule = null;
                        }
                        try
                        {
                            var app = _d.GetAppAsync().Result;
                            var client = app?.AiClient as OpenAiCompatibleClient;
                            if (client != null)
                            {
                                var usage = client.LastStreamUsage;
                                if (usage.total_tokens > 0)
                                {
                                    _estimatedTokens = usage.total_tokens;
                                    if (statsLabelForFinal != null)
                                    {
                                        double elapsed = (DateTime.UtcNow - _streamingStartTime).TotalSeconds;
                                        if (elapsed < 0)
                                            elapsed = 0;
                                        string template = LocalizationExtensions.Get("chat.stats.footer", "~{0} tok · {1:F1}s");
                                        string exactTemplate = template.Replace("~", string.Empty);
                                        statsLabelForFinal.text = string.Format(exactTemplate, _estimatedTokens, elapsed);
                                    }
                                }
                            }
                        }
                        catch
                        {
                            // keep estimate if no real usage
                        }

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

        // ===== Context Window Indicator (U-36) =====

        private int EstimateSessionTokens()
        {
            // Prefer real usage data (exact prompt_tokens from stream_options) when available
            try
            {
                var app = _d.GetAppAsync().Result;
                var client = app?.AiClient as OpenAiCompatibleClient;
                if (client != null)
                {
                    var usage = client.LastStreamUsage;
                    if (usage.prompt_tokens > 0)
                        return usage.prompt_tokens;
                }
            }
            catch
            {
                // fall back to character-based estimate below
            }

            var chat = _d.GetChatServiceAsync().Result;
            var vm = chat?.CurrentChatViewModel;
            if (vm == null || vm.Messages.Count == 0)
                return 0;

            int totalChars = 0;
            for (int i = 0; i < vm.Messages.Count; i++)
            {
                var msg = vm.Messages[i];
                if (msg == null) continue;
                if (!string.IsNullOrEmpty(msg.content))
                    totalChars += msg.content.Length;
                // Count attachment paths as ~100 tokens each (image tokens)
                if (msg.attachments != null)
                    totalChars += msg.attachments.Count * 400;
            }
            // Rough estimate: 1 token ≈ 4 chars for English, ~2 chars for CJK
            // Use 3 as middle ground
            return totalChars / 3;
        }

        private void UpdateContextBar()
        {
            if (_contextBar == null) return;

            var chat = _d.GetChatServiceAsync().Result;
            var provider = chat?.CurrentProvider;
            int contextWindow = provider?.contextWindow ?? 0;

            if (contextWindow <= 0)
            {
                _contextBar.style.display = DisplayStyle.None;
                return;
            }

            int used = EstimateSessionTokens();
            float ratio = (float)used / contextWindow;
            ratio = Mathf.Clamp01(ratio);

            _contextBar.style.display = DisplayStyle.Flex;
            _contextBarFill.style.width = Length.Percent(ratio * 100f);

            // Color: green < 60%, yellow 60-85%, red > 85%
            if (ratio < 0.6f)
                _contextBarFill.style.backgroundColor = new Color(0.3f, 0.7f, 0.4f, 0.8f);
            else if (ratio < 0.85f)
                _contextBarFill.style.backgroundColor = new Color(0.9f, 0.7f, 0.2f, 0.8f);
            else
                _contextBarFill.style.backgroundColor = new Color(0.9f, 0.3f, 0.3f, 0.8f);

            _contextBarLabel.text = LocalizationExtensions.Get("chat.context.usage", "~{0} / {1} tokens")
                .Replace("{0}", used.ToString("N0"))
                .Replace("{1}", contextWindow.ToString("N0"));
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
            UpdateContextBar();
        }

        private void RenderTranscript(IReadOnlyList<ChatMessage> messages)
        {
            if (_d.MessagesList == null)
                return;

            CancelInlineEdit();
            if (IsSearchBarVisible())
                CloseSearch();
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

                var row = CreateMessageElement(message);
                // Tag with model index so context menu / edit can identify which message it represents
                row.userData = i;
                var bubbleForTag = row.Q<VisualElement>(className: "transcript__bubble");
                if (bubbleForTag != null)
                    bubbleForTag.userData = i;

                // Capture index outside any inner block for safe closure (C# 9 rule)
                int rowIndex = i;

                // Apply selection state (U-31) after creating the row (adapted for static CreateMessageElement + current pointer handling)
                if (_isSelectionMode)
                {
                    bool selected = _selectedMessages.Contains(rowIndex);
                    if (selected)
                        row.AddToClassList("transcript__row--selected");

                    bool isSystemRow = row.ClassListContains("transcript__row--system");
                    if (!isSystemRow)
                    {
                        // Click/tap toggles selection (replaces normal right-click/long-press behavior in selection mode)
                        row.RegisterCallback<ClickEvent>(_ => ToggleSelection(rowIndex));
                    }
                }

                _d.MessagesList.Add(row);
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
                attachmentWrap.AddToClassList("transcript__attachments");

                for (int i = 0; i < message.attachments.Count; i++)
                {
                    var attachment = message.attachments[i];
                    if (attachment == null)
                        continue;

                    if (!string.IsNullOrEmpty(attachment.path) &&
                        (attachment.kind == "image" || IsImageFile(attachment.path)))
                    {
                        var imageElement = new Image();
                        imageElement.AddToClassList("transcript__image");
                        imageElement.scaleMode = ScaleMode.ScaleToFit;
                        LoadImageAsync(imageElement, attachment.path);
                        attachmentWrap.Add(imageElement);
                    }
                    else
                    {
                        var attachmentLabel = new Label($"[file] {GetAttachmentDisplayName(attachment)}");
                        attachmentLabel.AddToClassList("transcript__body");
                        attachmentLabel.style.fontSize = 11f;
                        attachmentLabel.focusable = true;
                        attachmentWrap.Add(attachmentLabel);
                    }
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
            VisualElement bodyElement;
            if (isAssistant && !string.IsNullOrWhiteSpace(text) && MarkdownRenderer.ContainsMarkdown(text))
            {
                bodyElement = MarkdownRenderer.Render(text);
            }
            else
            {
                bodyElement = new Label(text);
                bodyElement.AddToClassList("transcript__body");
            }
            MakeTranscriptLabelsFocusable(bodyElement);
            return bodyElement;
        }

        private static void MakeTranscriptLabelsFocusable(VisualElement root)
        {
            if (root == null)
                return;
            var labels = root.Query<Label>().ToList();
            for (int i = 0; i < labels.Count; i++)
            {
                labels[i].focusable = true;
            }
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

        private static async void LoadImageAsync(Image imageElement, string path)
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
                        var dh = request.downloadHandler as DownloadHandlerTexture;
                        if (dh != null)
                        {
                            imageElement.image = dh.texture;
                        }
                    }
                }
            }
            catch
            {
                // Silent fail — image simply won't render
            }
        }

        private static bool IsImageFile(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;
            string ext = System.IO.Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext))
                return false;
            return string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(ext, ".jpg", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(ext, ".jpeg", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(ext, ".gif", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(ext, ".webp", StringComparison.OrdinalIgnoreCase);
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

        // ===== Message context menu (U-29/U-30) =====

        private void OnTranscriptPointerDown(PointerDownEvent evt)
        {
            if (_d.MessagesList == null)
                return;

            VisualElement bubble = FindBubbleAncestor(evt.target as VisualElement);
            if (bubble == null)
                return;

            // Skip system bubbles for context menu
            if (bubble.ClassListContains("transcript__bubble--system"))
                return;

            int? msgIndex = GetMessageIndexFromElement(bubble);
            if (msgIndex == null)
                return;

            bool isUser = bubble.ClassListContains("transcript__bubble--user");

            Vector2 pos = evt.position;

            if (_isSelectionMode)
            {
                // Selection mode: per-row ClickEvent handles toggles for selectable messages.
                // Suppress only the context menu and long-press timer (no menu in selection mode).
                if (evt.button == 1)
                    evt.StopImmediatePropagation();
                // Do not start long-press schedule; do not call ShowMessageContextMenu
                return;
            }

            if (evt.button == 1) // right-click (UITK: 0=left, 1=right, 2=middle)
            {
                evt.StopImmediatePropagation();
                ShowMessageContextMenu(bubble, msgIndex.Value, isUser, pos);
            }
            else if (evt.button == 0)
            {
                // Start long-press timer for mobile
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
                        ShowMessageContextMenu(_longPressTarget, _longPressIndex, _longPressIsUser, _longPressPos);
                    }
                    _longPressTarget = null;
                }).StartingIn(480);
            }
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
                _contextMenu = new MessageContextMenu();

            // Hide any previous
            _contextMenu.Hide();

            // Use target for positioning (ShowAt uses worldBound); position param available for future tweak
            _contextMenu.ShowAt(target, messageIndex, isUser);
        }

        private void OnEditMessageRequested(string messageIndexStr)
        {
            if (_contextMenu != null)
                _contextMenu.Hide();

            int index;
            if (!int.TryParse(messageIndexStr, out index))
                return;

            var chat = _d.GetChatServiceAsync().Result;
            if (chat == null || chat.CurrentChatViewModel == null || chat.CurrentChatViewModel.Messages == null)
                return;

            var messages = chat.CurrentChatViewModel.Messages;
            if (index < 0 || index >= messages.Count)
                return;

            var msg = messages[index];
            string role = NormalizeRole(msg.role);
            if (!string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
                return; // edit only for user

            // Find matching visual element by tagged index
            if (_d.MessagesList == null)
                return;

            VisualElement targetRow = null;
            foreach (var child in _d.MessagesList.Children())
            {
                if (child.userData is int)
                {
                    int idx = (int)child.userData;
                    if (idx == index)
                    {
                        targetRow = child;
                        break;
                    }
                }
            }
            if (targetRow == null)
                return;

            var bubble = targetRow.Q<VisualElement>(className: "transcript__bubble");
            if (bubble == null)
                return;

            StartInlineEdit(index, bubble, msg.content ?? string.Empty);
        }

        private void OnDeleteMessageRequested(string messageIndexStr)
        {
            _ = DeleteMessageAsync(messageIndexStr);
        }

        private async Task DeleteMessageAsync(string messageIndexStr)
        {
            if (_contextMenu != null)
                _contextMenu.Hide();

            int index;
            if (!int.TryParse(messageIndexStr, out index))
                return;

            var chat = await _d.GetChatServiceAsync();
            if (chat == null || chat.CurrentChatViewModel == null || chat.CurrentChatViewModel.Messages == null)
                return;

            var messages = chat.CurrentChatViewModel.Messages;
            if (index >= 0 && index < messages.Count)
            {
                messages.RemoveAt(index);
                _d.RenderMessages(messages);
                await chat.SaveCurrentSessionAsync();
                await _d.LoadSessionsAsync();
                _d.ShowSystemMessage(LocalizationExtensions.Get("msg.deleted", "Message deleted"));
            }
        }

        private void OnCopyMessageRequested(string messageIndexStr)
        {
            if (_contextMenu != null)
                _contextMenu.Hide();

            int index;
            if (!int.TryParse(messageIndexStr, out index))
                return;

            var chat = _d.GetChatServiceAsync().Result;
            if (chat == null || chat.CurrentChatViewModel == null || chat.CurrentChatViewModel.Messages == null)
                return;

            var messages = chat.CurrentChatViewModel.Messages;
            if (index >= 0 && index < messages.Count)
            {
                string content = messages[index].content ?? string.Empty;
                GUIUtility.systemCopyBuffer = content;
                _d.ShowSystemMessage(LocalizationExtensions.Get("msg.copied", "Copied"));
            }
        }

        private void StartInlineEdit(int index, VisualElement bubble, string currentContent)
        {
            if (_editingMessageIndex != null)
                CancelInlineEdit();

            _editingMessageIndex = index;
            _editingBubble = bubble;

            // Hide existing body labels (user messages use plain Labels)
            var bodies = bubble.Query<Label>(className: "transcript__body").ToList();
            for (int i = 0; i < bodies.Count; i++)
            {
                bodies[i].style.display = DisplayStyle.None;
            }

            // Build edit UI
            var container = new VisualElement();
            container.AddToClassList("message-edit-container");

            var tf = new TextField();
            tf.AddToClassList("message-edit-field");
            tf.multiline = true;
            tf.value = currentContent;
            container.Add(tf);

            var btnRow = new VisualElement();
            btnRow.AddToClassList("message-edit-buttons");
            btnRow.style.flexDirection = FlexDirection.Row;

            string saveLabel = LocalizationExtensions.Get("msg.edit.save", "Save");
            var saveBtn = new Button(() => CommitInlineEdit(true, false));
            saveBtn.text = saveLabel;
            saveBtn.AddToClassList("message-edit-btn");
            saveBtn.AddToClassList("message-edit-btn--save");
            btnRow.Add(saveBtn);

            string cancelLabel = LocalizationExtensions.Get("msg.edit.cancel", "Cancel");
            var cancelBtn = new Button(() => CommitInlineEdit(false, false));
            cancelBtn.text = cancelLabel;
            cancelBtn.AddToClassList("message-edit-btn");
            cancelBtn.AddToClassList("message-edit-btn--cancel");
            btnRow.Add(cancelBtn);

            // Offer regenerate if this user message is followed by an assistant response
            var chat = _d.GetChatServiceAsync().Result;
            var msgs = (chat != null && chat.CurrentChatViewModel != null) ? chat.CurrentChatViewModel.Messages : null;
            bool hasFollowingAssistant = msgs != null &&
                                         index + 1 < msgs.Count &&
                                         msgs[index + 1] != null &&
                                         string.Equals(NormalizeRole(msgs[index + 1].role), "assistant", StringComparison.OrdinalIgnoreCase);
            if (hasFollowingAssistant)
            {
                string regenLabel = LocalizationExtensions.Get("msg.edit.save_regen", "Save & Regenerate");
                var regenBtn = new Button(() => CommitInlineEdit(true, true));
                regenBtn.text = regenLabel;
                regenBtn.AddToClassList("message-edit-btn");
                regenBtn.AddToClassList("message-edit-btn--regen");
                btnRow.Add(regenBtn);
            }

            container.Add(btnRow);

            // Insert after meta if present
            var meta = bubble.Q<VisualElement>(className: "transcript__meta");
            if (meta != null)
            {
                int metaIdx = bubble.IndexOf(meta);
                if (metaIdx >= 0)
                    bubble.Insert(metaIdx + 1, container);
                else
                    bubble.Add(container);
            }
            else
            {
                bubble.Add(container);
            }

            _editingContainer = container;
            _editingTextField = tf;
            _editingSaveBtn = saveBtn;
            _editingCancelBtn = cancelBtn;

            // Focus for immediate typing
            tf.Focus();
        }

        private void CommitInlineEdit(bool doSave, bool regenerateAfter)
        {
            if (_editingMessageIndex == null || _editingTextField == null || _editingBubble == null)
            {
                CancelInlineEdit();
                return;
            }

            int index = _editingMessageIndex.Value;
            var chat = _d.GetChatServiceAsync().Result;
            if (chat == null || chat.CurrentChatViewModel == null || chat.CurrentChatViewModel.Messages == null)
            {
                CancelInlineEdit();
                return;
            }

            var messages = chat.CurrentChatViewModel.Messages;
            if (index < 0 || index >= messages.Count)
            {
                CancelInlineEdit();
                return;
            }

            if (doSave)
            {
                string newContent = (_editingTextField.value ?? string.Empty).Trim();
                messages[index].content = newContent;

                // Clear segments for user message (they are plain text)
                if (messages[index].segments != null)
                    messages[index].segments.Clear();

                if (regenerateAfter)
                {
                    // Truncate everything after the edited message
                    while (messages.Count > index + 1)
                        messages.RemoveAt(messages.Count - 1);

                    _d.RenderMessages(messages);
                    _ = chat.SaveCurrentSessionAsync();
                    _ = _d.LoadSessionsAsync();

                    // Reuse existing regenerate flow (it will remove any trailing assistant if present and re-send)
                    _ = RegenerateLastAsync();
                    CancelInlineEdit();
                    return;
                }

                // Normal save: re-render to get clean bubble
                _d.RenderMessages(messages);
                _ = chat.SaveCurrentSessionAsync();
                _ = _d.LoadSessionsAsync();
            }

            CancelInlineEdit();
        }

        private void CancelInlineEdit()
        {
            if (_editingContainer != null && _editingContainer.parent != null)
                _editingContainer.RemoveFromHierarchy();

            if (_editingBubble != null)
            {
                var bodies = _editingBubble.Query<Label>(className: "transcript__body").ToList();
                for (int i = 0; i < bodies.Count; i++)
                {
                    bodies[i].style.display = DisplayStyle.Flex;
                }
            }

            _editingMessageIndex = null;
            _editingBubble = null;
            _editingContainer = null;
            _editingTextField = null;
            _editingSaveBtn = null;
            _editingCancelBtn = null;
        }

        // ===== Message selection (U-31/U-32) =====

        private void EnterSelectionMode(int initialIndex)
        {
            _isSelectionMode = true;
            _selectedMessages.Clear();
            _selectedMessages.Add(initialIndex);
            RenderSelectionUI();
            RenderTranscript(_d.GetChatServiceAsync().Result?.CurrentChatViewModel?.Messages);
        }

        private void ExitSelectionMode()
        {
            _isSelectionMode = false;
            _selectedMessages.Clear();
            RenderSelectionUI();
            RenderTranscript(_d.GetChatServiceAsync().Result?.CurrentChatViewModel?.Messages);
        }

        private void ToggleSelection(int index)
        {
            if (_selectedMessages.Contains(index))
                _selectedMessages.Remove(index);
            else
                _selectedMessages.Add(index);

            if (_selectedMessages.Count == 0)
                ExitSelectionMode();
            else
                RenderSelectionUI();
        }

        private void RenderSelectionUI()
        {
            if (_selectionBar == null) return;
            if (_isSelectionMode)
            {
                _selectionBar.style.display = DisplayStyle.Flex;
                _selectionCountLabel.text = LocalizationExtensions.Get("chat.selection.count", "Selected: {0}")
                    .Replace("{0}", _selectedMessages.Count.ToString());
            }
            else
            {
                _selectionBar.style.display = DisplayStyle.None;
            }
        }

        private void OnDeleteSelected()
        {
            if (_selectedMessages.Count == 0) return;

            var chat = _d.GetChatServiceAsync().Result;
            if (chat?.CurrentChatViewModel == null) return;

            // Sort indices descending to remove from end first
            var indices = new List<int>(_selectedMessages);
            indices.Sort((a, b) => b.CompareTo(a));

            for (int i = 0; i < indices.Count; i++)
            {
                int idx = indices[i];
                if (idx >= 0 && idx < chat.CurrentChatViewModel.Messages.Count)
                    chat.CurrentChatViewModel.Messages.RemoveAt(idx);
            }

            _ = chat.SaveCurrentSessionAsync();
            ExitSelectionMode();
            _d.RenderMessages(chat.CurrentChatViewModel.Messages);
        }

        private void OnSelectMessageRequested(string messageIndexStr)
        {
            if (string.IsNullOrEmpty(messageIndexStr))
                return;

            int index;
            if (!int.TryParse(messageIndexStr, out index))
                return;

            // Enforce: do not allow selection of system messages
            var chat = _d.GetChatServiceAsync().Result;
            if (chat != null && chat.CurrentChatViewModel != null && chat.CurrentChatViewModel.Messages != null)
            {
                if (index >= 0 && index < chat.CurrentChatViewModel.Messages.Count)
                {
                    string role = NormalizeRole(chat.CurrentChatViewModel.Messages[index].role);
                    if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase))
                        return;
                }
            }

            EnterSelectionMode(index);
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
