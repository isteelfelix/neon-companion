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
using NeonCompanion.Runtime.Models.Chat;
using NeonCompanion.Runtime.UI.UITK.Chat;
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
            public Func<AvatarAnimationController> GetAvatarAnimationController;
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
            // Sounds (U-40)
            public Action PlayNotificationSound;
        }

        // QueuedMessage extracted to Models/Chat/QueuedMessage.cs

        private Deps _d;
        private bool _isSending;
#if UNITY_EDITOR
        // Editor-only drag-overlay state; the only read lives in the #if UNITY_EDITOR drag handlers.
        private bool _isDragOver;
#endif
        private bool _callbacksRegistered;
        private IFileDropService _fileDropService;
        private ChatNotificationManager _notifications;
        private ChatInputManager _inputManager;
        private ChatService _currentChatService;
        private ChatStreamingCoordinator _streamingCoordinator;
        private ToolCallApprovalController _approvalController;
        private readonly List<ChatAttachment> _pendingComposerAttachments = new List<ChatAttachment>();
        private string _chatSubtitle = string.Empty;
        private VisualElement _composerPreviews;
        private VisualElement _lightbox;

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
        private VisualElement _transcriptContextRoot;
        private readonly Dictionary<string, VisualElement> _messageRowCache = new Dictionary<string, VisualElement>();

        // Inline edit state — delegated to ChatMessageEditController
        private ChatMessageEditController _editController;

        // Chat search (U-38) — delegated to ChatSearchController
        private ChatSearchController _searchController;

        // Message selection mode (U-31/U-32) — delegated to ChatSelectionManager
        private ChatSelectionManager _selectionManager;

        // Forward picker (U-33)
        private VisualElement _sessionPickerOverlay;
        private VisualElement _sessionPickerPanel;
        private VisualElement _pickerRoot;
        private EventCallback<PointerDownEvent> _pickerOutsideHandler;

        private const int MaxToolIterations = 10;

        public bool IsSending => _isSending;
        public bool IsStreamingResponse => _streamingCoordinator != null && _streamingCoordinator.IsStreaming;
        public string ChatSubtitle => _chatSubtitle;
        public string SessionSearchQuery => _searchController != null ? _searchController.SessionSearchQuery : string.Empty;

        public void SetDeps(Deps deps)
        {
            _d = deps;
            if (_contextMenu == null)
                _contextMenu = new MessageContextMenu();

            _notifications = new ChatNotificationManager(_d.NavChatCount);
            _inputManager = new ChatInputManager(_d.MessageInput, _d.Composer, _d.EnterToSend);
            _inputManager.OnSubmit += _ => OnSendClicked();
            _searchController = new ChatSearchController(_d.MessagesList, _d.GetChatServiceAsync);
            _editController = new ChatMessageEditController(
                _d.GetChatServiceAsync,
                _d.RenderMessages,
                _d.LoadSessionsAsync,
                () => RegenerateLastAsync());

            _approvalController = new ToolCallApprovalController(
                _d.MessagesList,
                ScrollTranscriptToBottom,
                _d.GetAppAsync,
                _d.ShowSystemMessage,
                () => _isSending,
                () => _streamingCoordinator != null && _streamingCoordinator.IsStreaming,
                () => _currentChatService);

            _streamingCoordinator = new ChatStreamingCoordinator(
                _d.MessagesList,
                ScrollTranscriptToBottom,
                m => CreateMessageElement(m),
                ApplyTextCursor,
                bubble => _approvalController?.SetBubble(bubble),
                () => { _d.GetAvatarAnimationController?.Invoke()?.TriggerStreamStart(); _d.RefreshAvatarMotionState?.Invoke(); });
        }

        public void SetVoiceRecording(bool value) { _inputManager?.SetVoiceRecording(value); }
        public void SetChatSubtitle(string value) { _chatSubtitle = value ?? string.Empty; }
        public void SetSessionSearchQuery(string value) { _searchController?.SetSessionSearchQuery(value); }
        public void ShowSystemMessage(string text) { _d.ShowSystemMessage?.Invoke(text); }

        public void RegisterCallbacks()
        {
            _callbacksRegistered = true;

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

            _approvalController?.Subscribe();
            if (_approvalController != null)
                _approvalController.OnStopRequested += OnStopClicked;

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
                _d.MessagesList.RegisterCallback<ContextualMenuPopulateEvent>(OnTranscriptContextMenuPopulate, TrickleDown.TrickleDown);
                _d.MessagesList.RegisterCallback<PointerUpEvent>(OnTranscriptPointerUp);
                _d.MessagesList.RegisterCallback<PointerCancelEvent>(OnTranscriptPointerCancel);

                _transcriptContextRoot = GetDocumentRoot(_d.MessagesList);
                if (_transcriptContextRoot != null)
                {
                    _transcriptContextRoot.RegisterCallback<MouseDownEvent>(OnTranscriptRootMouseDown, TrickleDown.TrickleDown);
                    if (_transcriptContextRoot != _d.MessagesList)
                        _transcriptContextRoot.RegisterCallback<ContextualMenuPopulateEvent>(OnTranscriptContextMenuPopulate, TrickleDown.TrickleDown);
                }
            }

            if (_d.MessageInput != null)
            {
                _inputManager.RegisterCallbacks();
                _d.MessageInput.RegisterCallback<KeyDownEvent>(OnComposerKeyDownForPaste, TrickleDown.TrickleDown);
                _d.MessageInput.RegisterValueChangedCallback(OnComposerTextChangedForAvatar);
            }

            if (_d.Composer != null)
            {
                _composerPreviews = _d.Composer.Q<VisualElement>("composer-previews");
                if (_composerPreviews == null)
                {
                    _composerPreviews = new VisualElement();
                    _composerPreviews.name = "composer-previews";
                    _composerPreviews.AddToClassList("composer__previews");
                    _d.Composer.Insert(0, _composerPreviews);
                }
                _composerPreviews.style.display = DisplayStyle.None;
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

            // Selection action bar (U-31/U-32) — delegated to ChatSelectionManager
            _selectionManager = new ChatSelectionManager(
                _d.MessagesList,
                _d.Composer,
                () => DismissSessionPicker(),
                () => RenderTranscript(_d.GetChatServiceAsync().Result?.CurrentChatViewModel?.Messages));
            _selectionManager.OnBulkDelete += OnSelectionBulkDelete;
            _selectionManager.OnBulkForward += OnSelectionBulkForward;

            // Drag-and-drop file support (U-44)
            var chatMain = _d.Composer?.parent;
#if UNITY_EDITOR
            if (chatMain != null)
            {
                chatMain.RegisterCallback<DragUpdatedEvent>(OnDragUpdated);
                chatMain.RegisterCallback<DragPerformEvent>(OnDragPerform);
                chatMain.RegisterCallback<DragLeaveEvent>(OnDragLeave);
            }
#endif
            _ = BindRuntimeFileDropAsync();

            // Application.focusChanged is managed by ChatNotificationManager
        }

        public void UnregisterCallbacks()
        {
            _callbacksRegistered = false;

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
            if (_approvalController != null)
                _approvalController.OnStopRequested -= OnStopClicked;
            _approvalController?.Unsubscribe();

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
                _d.MessagesList.UnregisterCallback<ContextualMenuPopulateEvent>(OnTranscriptContextMenuPopulate, TrickleDown.TrickleDown);
                _d.MessagesList.UnregisterCallback<PointerUpEvent>(OnTranscriptPointerUp);
                _d.MessagesList.UnregisterCallback<PointerCancelEvent>(OnTranscriptPointerCancel);
            }

            if (_transcriptContextRoot != null && _transcriptContextRoot != _d.MessagesList)
            {
                _transcriptContextRoot.UnregisterCallback<ContextualMenuPopulateEvent>(OnTranscriptContextMenuPopulate, TrickleDown.TrickleDown);
            }
            if (_transcriptContextRoot != null)
            {
                _transcriptContextRoot.UnregisterCallback<MouseDownEvent>(OnTranscriptRootMouseDown, TrickleDown.TrickleDown);
                _transcriptContextRoot = null;
            }

            if (_d.MessageInput != null)
            {
                _d.MessageInput.UnregisterCallback<KeyDownEvent>(OnComposerKeyDownForPaste, TrickleDown.TrickleDown);
                _d.MessageInput.UnregisterValueChangedCallback(OnComposerTextChangedForAvatar);
            }

            _inputManager?.UnregisterCallbacks();

            if (_pinBottomQueued)
            {
                _pinBottomQueued = false;
                _d.MessagesList?.contentContainer?.UnregisterCallback<GeometryChangedEvent>(OnTranscriptGeometryForScroll);
            }

            _isSending = false;
            _streamingCoordinator?.Abort();
            _approvalController?.ClearToolProgress();
            _approvalController?.Dismiss();
            if (_contextMenu != null)
                _contextMenu.Hide();
            _editController?.CancelEdit();
            _searchController?.Hide();
            DismissSessionPicker();
            HideLightbox();
            _searchController?.Dispose();
            _searchController = null;

            _messageQueue.Clear();
            if (_queueIndicator != null)
            {
                _queueIndicator.RemoveFromHierarchy();
                _queueIndicator = null;
            }

            if (_selectionManager != null)
            {
                _selectionManager.OnBulkDelete -= OnSelectionBulkDelete;
                _selectionManager.OnBulkForward -= OnSelectionBulkForward;
                _selectionManager.Teardown();
                _selectionManager = null;
            }

            // Drag-and-drop file support (U-44)
            var chatMain = _d.Composer?.parent;
#if UNITY_EDITOR
            if (chatMain != null)
            {
                chatMain.UnregisterCallback<DragUpdatedEvent>(OnDragUpdated);
                chatMain.UnregisterCallback<DragPerformEvent>(OnDragPerform);
                chatMain.UnregisterCallback<DragLeaveEvent>(OnDragLeave);
            }
#endif
            UnbindRuntimeFileDrop();

            // Application.focusChanged managed by ChatNotificationManager (Dispose in future)
        }

        public void InitState()
        {
            SetSending(false);
        }

        // ===== Input (Ctrl+V paste — enter handling delegated to ChatInputManager) =====

        private void OnComposerKeyDownForPaste(KeyDownEvent evt)
        {
            if (evt == null)
                return;

            bool hasCtrl = evt.ctrlKey || evt.commandKey;

            // Ctrl+V — three cases (U-42):
            // A) clipboard text is a path to image file → attach as image
            // B) clipboard has bitmap data (CF_DIB) regardless of text → extract image
            // C) clipboard has only text → fall through so UITK TextField handles natively
            if (hasCtrl && evt.keyCode == KeyCode.V)
            {
                string clip = GUIUtility.systemCopyBuffer;
                if (!string.IsNullOrEmpty(clip) && IsImageFilePath(clip) && System.IO.File.Exists(clip))
                {
                    evt.StopPropagation();
                    _ = PasteImageFromClipboardAsync();
                    return;
                }
                if (ClipboardHasBitmapData())
                {
                    evt.StopPropagation();
                    _ = PasteWindowsClipboardImageAsync();
                    return;
                }
                // Text content: do not intercept — UITK handles paste natively.
            }
        }

        private void OnComposerTextChangedForAvatar(ChangeEvent<string> evt)
        {
            if (_isSending || _inputManager == null)
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
            _approvalController?.Dismiss();
            DismissSessionPicker();
            if (_contextMenu != null)
                _contextMenu.Hide();
            _editController?.CancelEdit();
            _currentChatService?.CancelCurrentGeneration();
        }

        public async Task SendCurrentMessageAsync()
        {
            _notifications.MarkRead();
            _approvalController?.Dismiss();
            DismissSessionPicker();
            if (_contextMenu != null)
                _contextMenu.Hide();
            _editController?.CancelEdit();

            bool hasPendingAttachments = _pendingComposerAttachments.Count > 0;
            if (_d.MessageInput == null)
                return;

            string composerText = (_inputManager.CurrentText ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(composerText) && !hasPendingAttachments)
                return;

            if (await TryHandleCommandAsync(composerText))
            {
                _d.MessageInput.value = string.Empty;
                _inputManager.QueueComposerHeightUpdate();
                return;
            }

            // If currently sending, queue the message instead (commands already handled above and execute immediately)
            if (_isSending)
            {
                var qAttach = CloneAttachments(_pendingComposerAttachments);
                string qMsg = StripAttachmentTokens(composerText, qAttach);
                _messageQueue.Enqueue(new QueuedMessage { Message = qMsg, Attachments = qAttach });
                _d.MessageInput.value = string.Empty;
                _inputManager.QueueComposerHeightUpdate();
                ClearPendingComposerAttachments();
                RenderQueueIndicator();
                return;
            }

            var pendingAttachments = CloneAttachments(_pendingComposerAttachments);
            string message = StripAttachmentTokens(composerText, pendingAttachments);
            _d.MessageInput.value = string.Empty;
            ClearPendingComposerAttachments();
            _inputManager.QueueComposerHeightUpdate();
            SetSending(true);
            _d.GetAvatarAnimationController?.Invoke()?.TriggerSend();

            ChatService chat = null;
            QueuedMessage nextQueuedMessage = null;
            try
            {
                chat = await _d.GetChatServiceAsync();
                if (chat == null)
                {
                    _d.ShowSystemMessage(LocalizationExtensions.Get("system.app.not_initialized", "Приложение не инициализировано."));
                    RestoreComposerDraft(message, pendingAttachments);
                    return;
                }

                _currentChatService = chat;

                if (chat.CurrentProvider == null || !chat.CurrentProvider.isEnabled || chat.CurrentChatViewModel == null)
                {
                    _d.ShowSystemMessage(LocalizationExtensions.Get("provider.not_configured.hint", "Провайдер не настроен. Перейди в Провайдеры и добавь API-ключ."));
                    RestoreComposerDraft(message, pendingAttachments);
                    return;
                }

                bool streaming = _d.UseStreaming();
                chat.UseStreaming = streaming;

                _d.RenderMessages(BuildPendingMessages(chat.CurrentChatViewModel?.Messages, message, pendingAttachments));

                if (streaming)
                {
                    _streamingCoordinator.Begin();
                    await chat.SendMessageAsync(message, pendingAttachments, _streamingCoordinator.OnToken, OnToolProgress);
                    ClearThinkingBubble();
                    _approvalController?.ClearToolProgress();
                    _approvalController?.Dismiss();
                    DismissSessionPicker();

                    // Finalize stats with real usage from client
                    _streamingCoordinator.PauseStatsSchedule();
                    try
                    {
                        var app = _d.GetAppAsync().Result;
                        var client = app?.AiClient as OpenAiCompatibleClient;
                        if (client != null)
                        {
                            var usage = client.LastStreamUsage;
                            if (usage.total_tokens > 0)
                            {
                                double elapsed = (DateTime.UtcNow - _streamingCoordinator.StartTime).TotalSeconds;
                                if (elapsed < 0)
                                    elapsed = 0;
                                _streamingCoordinator.SetFinalStats(usage.total_tokens, elapsed);
                                // Persist precise usage to the message model so it survives re-renders and reloads (U-28)
                                try
                                {
                                    var vm = chat.CurrentChatViewModel;
                                    if (vm != null && vm.Messages != null && vm.Messages.Count > 0)
                                    {
                                        var last = vm.Messages[vm.Messages.Count - 1];
                                        if (last != null && string.Equals(NormalizeRole(last.role), "assistant", StringComparison.OrdinalIgnoreCase))
                                        {
                                            last.tokenCount = _streamingCoordinator.EstimatedTokens;
                                            last.responseTimeSeconds = (float)elapsed;
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    catch
                    {
                        // keep estimate if client not available or no usage chunk
                    }

                    _streamingCoordinator.Abort();
                }
                else
                {
                    await chat.SendMessageAsync(message, pendingAttachments);
                }

                _d.PlayNotificationSound?.Invoke();
                _d.RenderMessages(chat.CurrentChatViewModel?.Messages);
                await _d.LoadSessionsAsync();
                _d.TriggerAvatarSmile();

                // Agent tool execution loop: handles tool_calls returned by model, shows approvals, executes locally, continues until text response
                await ProcessAgentToolLoopAsync(chat, streaming);
            }
            catch (OperationCanceledException)
            {
                // User clicked stop — keep partial response (critical for local LLMs)
                _streamingCoordinator?.Abort();
                _approvalController?.ClearToolProgress();
                _approvalController?.Dismiss();
                DismissSessionPicker();
                _d.RenderMessages(chat?.CurrentChatViewModel?.Messages);
            }
            catch (Exception ex)
            {
                _streamingCoordinator?.Abort();
                _approvalController?.Dismiss();
                DismissSessionPicker();
                _d.MessageInput.value = composerText;
                _inputManager.QueueComposerHeightUpdate();
                RestorePendingComposerAttachments(pendingAttachments);
                if (chat == null || chat.CurrentProvider == null || !chat.CurrentProvider.isEnabled)
                    _d.RenderMessages(null);
                else
                    _d.RenderMessages(chat.CurrentChatViewModel?.Messages);
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
                    _notifications.NotifyNewMessage();
                }

                // Process queued messages
                if (_messageQueue.Count > 0)
                {
                    nextQueuedMessage = _messageQueue.Dequeue();
                    RenderQueueIndicator();
                    // Set composer text and attachments, then send
                    _d.MessageInput.value = nextQueuedMessage.Message;
                    if (nextQueuedMessage.Attachments != null)
                    {
                        for (int i = 0; i < nextQueuedMessage.Attachments.Count; i++)
                            _pendingComposerAttachments.Add(nextQueuedMessage.Attachments[i]);
                    }
                    _inputManager.QueueComposerHeightUpdate();
                }
            }

            if (nextQueuedMessage != null)
                _ = SendCurrentMessageAsync();
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
            bool started = await StartNewSessionAsync();
            if (started)
                _d.ShowSystemMessage(LocalizationExtensions.Get("chat.new.started", "Новая сессия начата."));
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

            bool insertedNewEntry = _approvalController != null && _approvalController.OnToolProgress(tool, label, emoji, status);
            if (insertedNewEntry)
                _streamingCoordinator?.ResetStreamingSegment();
            ScrollTranscriptToBottom();

            // Hermes executes tools server-side, so its approval request must be
            // surfaced from streaming progress. Generic OpenAI local tools still
            // block in ProcessAgentToolLoopAsync before ToolExecutor runs.
            if (_approvalController != null && _approvalController.ShouldPromptForStreamingApproval(status))
            {
                var req = new ToolCallRequest
                {
                    id = Guid.NewGuid().ToString("N"),
                    toolName = tool ?? string.Empty,
                    description = !string.IsNullOrEmpty(label) ? label : tool,
                    parameters = new Dictionary<string, string>()
                };
                _ = _approvalController.HandleStreamingApprovalAsync(req);
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
            {
                _d.GetAvatarAnimationController?.Invoke()?.TriggerStreamEnd();
            }

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

        // Notification badge extracted to Chat/ChatNotificationManager.cs

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

                string defaultFileName = "chat-export-" + now.ToString("yyyy-MM-dd-HHmmss") + ".md";
                string content = sb.ToString();

                // Ask user where to save (U-37). Fall back to persistentDataPath if dialog unavailable.
                string path = null;
                try
                {
                    var app = await _d.GetAppAsync();
                    if (app != null)
                    {
                        NeonCompanion.Runtime.Platform.IFilePickerService picker = null;
                        app.Services.TryGet<NeonCompanion.Runtime.Platform.IFilePickerService>(out picker);
                        if (picker != null)
                            path = await picker.PickSavePathAsync(defaultFileName, "md");
                    }
                }
                catch { /* dialog failure — fall through to default path */ }

                if (string.IsNullOrEmpty(path))
                    path = System.IO.Path.Combine(Application.persistentDataPath, defaultFileName);

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
            if (_searchController != null && _searchController.IsVisible)
            {
                _searchController.Hide();
                return;
            }
            _searchController?.Show();
        }

        // Search methods moved to ChatSearchController

        // ===== Attachments =====

        private static bool IsImageFilePath(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > 512)
                return false;
            // A real file path is a single line with no illegal path characters. Guard before
            // Path.GetExtension, which throws ArgumentException ("Illegal characters in path")
            // on control chars such as the newlines/tabs present in pasted multi-line markdown.
            if (text.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0)
                return false;
            string ext;
            try
            {
                ext = System.IO.Path.GetExtension(text)?.ToLowerInvariant();
            }
            catch (ArgumentException)
            {
                return false;
            }
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg"
                || ext == ".gif" || ext == ".webp" || ext == ".bmp";
        }

        // ── Windows clipboard bitmap support (PNG/JFIF/CF_DIB via user32 / kernel32 P/Invoke) ──
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        private const uint ClipboardFormatDib = 8;
        private const uint ClipboardFormatDibV5 = 17;

        private sealed class ClipboardImageData
        {
            public byte[] Bytes;
            public bool IsDib;
            public string Extension;
            public string MediaType;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsClipboardFormatAvailable(uint format);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint RegisterClipboardFormat(string lpszFormat);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool OpenClipboard(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool CloseClipboard();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetClipboardData(uint format);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr GlobalLock(IntPtr hMem);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool GlobalUnlock(IntPtr hMem);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern int GlobalSize(IntPtr hMem);

        private static bool ClipboardHasBitmapData()
        {
            try
            {
                if (IsClipboardFormatAvailable(ClipboardFormatDib) ||
                    IsClipboardFormatAvailable(ClipboardFormatDibV5))
                    return true;

                uint pngFormat = RegisterClipboardFormat("PNG");
                uint jfifFormat = RegisterClipboardFormat("JFIF");
                return (pngFormat != 0 && IsClipboardFormatAvailable(pngFormat)) ||
                       (jfifFormat != 0 && IsClipboardFormatAvailable(jfifFormat));
            }
            catch
            {
                return false;
            }
        }

        // Reads encoded image bytes first, then raw DIB bytes, on an STA thread.
        private static Task<ClipboardImageData> GetClipboardImageDataAsync()
        {
            var tcs = new TaskCompletionSource<ClipboardImageData>();
            var t = new System.Threading.Thread(() =>
            {
                if (!OpenClipboard(IntPtr.Zero)) { tcs.TrySetResult(null); return; }
                try
                {
                    uint pngFormat = RegisterClipboardFormat("PNG");
                    byte[] png = GetClipboardBytes(pngFormat);
                    if (png != null && png.Length > 0)
                    {
                        tcs.TrySetResult(new ClipboardImageData
                        {
                            Bytes = png,
                            Extension = ".png",
                            MediaType = "image/png",
                            IsDib = false
                        });
                        return;
                    }

                    uint jfifFormat = RegisterClipboardFormat("JFIF");
                    byte[] jfif = GetClipboardBytes(jfifFormat);
                    if (jfif != null && jfif.Length > 0)
                    {
                        tcs.TrySetResult(new ClipboardImageData
                        {
                            Bytes = jfif,
                            Extension = ".jpg",
                            MediaType = "image/jpeg",
                            IsDib = false
                        });
                        return;
                    }

                    byte[] dib = GetClipboardBytes(ClipboardFormatDibV5);
                    if (dib == null || dib.Length == 0)
                        dib = GetClipboardBytes(ClipboardFormatDib);

                    if (dib != null && dib.Length > 0)
                    {
                        tcs.TrySetResult(new ClipboardImageData
                        {
                            Bytes = dib,
                            Extension = ".png",
                            MediaType = "image/png",
                            IsDib = true
                        });
                        return;
                    }

                    tcs.TrySetResult(null);
                }
                finally { CloseClipboard(); }
            });
            try { t.SetApartmentState(System.Threading.ApartmentState.STA); t.IsBackground = true; t.Start(); }
            catch { tcs.TrySetResult(null); }
            return tcs.Task;
        }

        private static byte[] GetClipboardBytes(uint format)
        {
            if (format == 0 || !IsClipboardFormatAvailable(format))
                return null;

            IntPtr hData = GetClipboardData(format);
            if (hData == IntPtr.Zero)
                return null;

            int size = GlobalSize(hData);
            if (size <= 0)
                return null;

            IntPtr ptr = GlobalLock(hData);
            if (ptr == IntPtr.Zero)
                return null;

            try
            {
                byte[] bytes = new byte[size];
                System.Runtime.InteropServices.Marshal.Copy(ptr, bytes, 0, size);
                return bytes;
            }
            finally
            {
                GlobalUnlock(hData);
            }
        }

        // Converts raw DIB bytes to a PNG temp file using Unity's Texture2D.
        // Must be called from the main thread (Texture2D API requirement).
        private static string DibToPngFile(byte[] dib)
        {
            if (dib == null || dib.Length < 40) return null;
            try
            {
                int headerSize  = BitConverter.ToInt32(dib, 0);
                int width       = BitConverter.ToInt32(dib, 4);
                int height      = BitConverter.ToInt32(dib, 8);
                short bpp       = BitConverter.ToInt16(dib, 14);
                int compression = BitConverter.ToInt32(dib, 16);
                int clrUsed     = BitConverter.ToInt32(dib, 32);

                if (width <= 0 || height == 0 ||
                    (compression != 0 && compression != 3) ||
                    (bpp != 24 && bpp != 32))
                    return null; // only uncompressed 24/32 bpp supported

                bool bottomUp = height > 0;
                if (height < 0) height = -height;

                int extraHeaderBytes = 0;
                if (compression == 3 && headerSize == 40)
                    extraHeaderBytes = bpp == 32 ? 16 : 12; // BI_BITFIELDS masks after BITMAPINFOHEADER

                int colorTableSize = bpp <= 8 ? (clrUsed > 0 ? clrUsed : (1 << bpp)) * 4 : 0;
                int pixelOffset = headerSize + extraHeaderBytes + colorTableSize;
                if (pixelOffset >= dib.Length) return null;

                int bytesPerPixel = bpp / 8;
                int stride = bpp == 32 ? width * 4 : ((width * 3 + 3) / 4) * 4;

                var colors = new Color32[width * height];
                for (int y = 0; y < height; y++)
                {
                    int srcY = bottomUp ? y : (height - 1 - y);
                    int rowBase = pixelOffset + srcY * stride;
                    for (int x = 0; x < width; x++)
                    {
                        int off = rowBase + x * bytesPerPixel;
                        if (off + 2 >= dib.Length) break;
                        byte b = dib[off];
                        byte g = dib[off + 1];
                        byte r = dib[off + 2];
                        byte a = (bpp == 32 && off + 3 < dib.Length) ? dib[off + 3] : (byte)255;
                        colors[y * width + x] = new Color32(r, g, b, a);
                    }
                }

                var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
                tex.SetPixels32(colors);
                tex.Apply();
                byte[] png = tex.EncodeToPNG();
                UnityEngine.Object.Destroy(tex);

                if (png == null || png.Length == 0) return null;

                string path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "neon-paste-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".png");
                System.IO.File.WriteAllBytes(path, png);
                return path;
            }
            catch (Exception ex)
            {
                NeonLogger.LogWarning("DIB→PNG failed: " + ex.Message);
                return null;
            }
        }
#else
        private static bool ClipboardHasBitmapData() { return false; }
#endif

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

        // Extracts a bitmap image from the Windows clipboard (screenshots, images copied from browser etc.)
        // Uses reflection + STA thread so it works without a direct System.Drawing reference.
        private async Task PasteWindowsClipboardImageAsync()
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            // Read image data on an STA thread (P/Invoke only), then convert DIB on main thread if needed.
            ClipboardImageData imageData = await GetClipboardImageDataAsync();
            if (imageData == null || imageData.Bytes == null || imageData.Bytes.Length == 0)
            {
                _d.ShowSystemMessage(LocalizationExtensions.Get("chat.paste.image_failed", "Не удалось извлечь изображение из буфера."));
                return;
            }

            string tempPath = imageData.IsDib
                ? DibToPngFile(imageData.Bytes)
                : WriteClipboardImageFile(imageData.Bytes, imageData.Extension);
            if (string.IsNullOrEmpty(tempPath))
            {
                _d.ShowSystemMessage(LocalizationExtensions.Get("chat.paste.image_failed", "Не удалось извлечь изображение из буфера."));
                return;
            }

            string fileName = "clipboard-" + DateTime.Now.ToString("HHmmss") + ".png";
            _pendingComposerAttachments.Add(new ChatAttachment
            {
                kind = "image",
                name = fileName,
                path = tempPath,
                mediaType = string.IsNullOrEmpty(imageData.MediaType) ? "image/png" : imageData.MediaType
            });
            RenderComposerPreviews();
            _d.MessageInput?.Focus();
#else
            await Task.CompletedTask;
#endif
        }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        private static string WriteClipboardImageFile(byte[] bytes, string extension)
        {
            if (bytes == null || bytes.Length == 0)
                return null;

            string ext = string.IsNullOrEmpty(extension) ? ".png" : extension;
            if (!ext.StartsWith(".", StringComparison.Ordinal))
                ext = "." + ext;

            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "neon-paste-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ext);
            System.IO.File.WriteAllBytes(path, bytes);
            return path;
        }
#endif

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
            RenderComposerPreviews();
        }

        private void RestoreComposerDraft(string message, IReadOnlyList<ChatAttachment> attachments)
        {
            if (_d.MessageInput != null)
                _d.MessageInput.value = message ?? string.Empty;
            RestorePendingComposerAttachments(attachments);
            _inputManager.QueueComposerHeightUpdate();
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
                    if (attachment.kind == "image" || IsImageFile(attachment.path))
                    {
                        var img = new Image();
                        img.AddToClassList("composer__preview-img");
                        img.scaleMode = ScaleMode.ScaleAndCrop;
                        img.schedule.Execute(() => LoadImageAsync(img, attachment.path));
                        string previewPath = attachment.path; // capture for closure
                        img.RegisterCallback<ClickEvent>(evt =>
                        {
                            ShowImageLightbox(previewPath);
                            evt.StopPropagation();
                        });
                        thumb.Add(img);
                    }
                    else
                    {
                        var fileLabel = new Label(GetAttachmentDisplayName(attachment));
                        fileLabel.AddToClassList("composer__preview-file");
                        thumb.Add(fileLabel);
                    }
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

            // Keep the composer text clean if it still contains legacy attachment tokens.
            string text = _d.MessageInput?.value ?? string.Empty;
            string rebuilt = BuildComposerTextWithAttachments(text, _pendingComposerAttachments);
            if (_d.MessageInput != null)
                _d.MessageInput.value = rebuilt;

            RenderComposerPreviews();
        }

        private static string BuildComposerTextWithAttachments(string composerText, IReadOnlyList<ChatAttachment> attachments)
        {
            return StripAllAttachmentTokens(composerText ?? string.Empty).Trim();
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

        private async Task BindRuntimeFileDropAsync()
        {
            if (_fileDropService != null)
                return;

            try
            {
                var app = await _d.GetAppAsync();
                if (!_callbacksRegistered || app == null)
                    return;

                IFileDropService fileDrop = null;
                if (!app.Services.TryGet<IFileDropService>(out fileDrop) || fileDrop == null || !fileDrop.IsAvailable)
                    return;

                _fileDropService = fileDrop;
                _fileDropService.FilesDropped += OnRuntimeFilesDropped;
                _fileDropService.Start();
            }
            catch (Exception ex)
            {
                NeonLogger.LogWarning("File drop binding failed: " + ex.Message);
            }
        }

        private void UnbindRuntimeFileDrop()
        {
            if (_fileDropService == null)
                return;

            _fileDropService.FilesDropped -= OnRuntimeFilesDropped;
            _fileDropService.Stop();
            _fileDropService = null;
        }

        private void OnRuntimeFilesDropped(IReadOnlyList<string> paths)
        {
#if UNITY_EDITOR
            _isDragOver = false;
#endif
            _d.Composer?.parent?.RemoveFromClassList("chat-main--drag-over");

            int added = AddPendingAttachmentsFromPaths(paths);
            if (added > 0)
            {
                RenderComposerPreviews();
                _d.MessageInput?.Focus();
                return;
            }

            _d.ShowSystemMessage?.Invoke(LocalizationExtensions.Get(
                "chat.drop.no_supported_files",
                "Не удалось добавить файлы: поддерживаются изображения и текстовые документы."));
        }

#if UNITY_EDITOR
        private void OnDragUpdated(DragUpdatedEvent evt)
        {
            if (!HasValidDragFiles(evt))
                return;

            SetDragCopyVisualMode();
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

            string[] paths = GetDraggedPaths();
            if (paths == null || paths.Length == 0)
                return;

            int added = AddPendingAttachmentsFromPaths(paths);
            if (added > 0)
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
            string[] paths = GetDraggedPaths();
            return paths != null && paths.Length > 0;
        }

        private static bool HasValidDragFiles(DragPerformEvent evt)
        {
            string[] paths = GetDraggedPaths();
            return paths != null && paths.Length > 0;
        }
#endif

        private int AddPendingAttachmentsFromPaths(IReadOnlyList<string> paths)
        {
            if (paths == null || paths.Count == 0)
                return 0;

            int added = 0;
            for (int i = 0; i < paths.Count; i++)
            {
                string path = paths[i];
                if (string.IsNullOrEmpty(path))
                    continue;

                if (!System.IO.File.Exists(path))
                    continue;

                string ext = System.IO.Path.GetExtension(path)?.ToLowerInvariant() ?? string.Empty;
                if (!IsSupportedFile(ext))
                    continue;

                if (IsImageExtension(ext) && !IsFileSizeOk(path))
                    continue;

                bool isImage = IsImageExtension(ext);
                _pendingComposerAttachments.Add(new ChatAttachment
                {
                    kind = isImage ? "image" : "file",
                    name = System.IO.Path.GetFileName(path),
                    path = path,
                    mediaType = isImage ? GuessImageMediaType(path) : GuessFileMediaType(path)
                });
                added++;
            }

            return added;
        }

        private static void SetDragCopyVisualMode()
        {
#if UNITY_EDITOR
            UnityEditor.DragAndDrop.visualMode = UnityEditor.DragAndDropVisualMode.Copy;
#endif
        }

        private static string[] GetDraggedPaths()
        {
#if UNITY_EDITOR
            return UnityEditor.DragAndDrop.paths;
#else
            return null;
#endif
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

        private static string GuessFileMediaType(string path)
        {
            string ext = System.IO.Path.GetExtension(path)?.ToLowerInvariant() ?? string.Empty;
            if (ext == ".pdf") return "application/pdf";
            if (ext == ".json") return "application/json";
            if (ext == ".xml") return "application/xml";
            if (ext == ".csv") return "text/csv";
            if (ext == ".md") return "text/markdown";
            if (ext == ".txt") return "text/plain";
            if (ext == ".cs" || ext == ".py" || ext == ".js" || ext == ".ts" || ext == ".java" || ext == ".cpp" || ext == ".h" || ext == ".csproj")
                return "text/plain";
            return "application/octet-stream";
        }

        private static bool IsImageExtension(string ext)
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

        public async Task<bool> StartNewSessionAsync()
        {
            try
            {
                if (_contextMenu != null)
                    _contextMenu.Hide();
                _editController?.CancelEdit();

                var chat = await _d.GetChatServiceAsync();
                if (chat == null)
                {
                    _d.ShowSystemMessage(LocalizationExtensions.Get("system.app.not_initialized", "Приложение не инициализировано."));
                    return false;
                }

                await chat.StartNewSessionAsync();
                if (string.IsNullOrEmpty(chat.CurrentSessionId) || chat.CurrentChatViewModel == null)
                {
                    _d.RenderMessages(null);
                    _d.ShowSystemMessage(LocalizationExtensions.Get(
                        "chat.create.no_provider",
                        "Провайдер не выбран. Настройте провайдера и активируйте его, чтобы создать чат."));
                    return false;
                }

                ClearPendingComposerAttachments();
                _messageQueue.Clear();
                RenderQueueIndicator();
                if (_d.MessageInput != null)
                {
                    _d.MessageInput.value = string.Empty;
                    _inputManager.QueueComposerHeightUpdate();
                }
                _d.RenderMessages(chat.CurrentChatViewModel?.Messages);
                await _d.LoadSessionsAsync();
                _d.ShowChat();
                return true;
            }
            catch (Exception ex)
            {
                _d.ShowSystemMessage(LocalizationExtensions.Get(
                    "chat.create.no_provider",
                    "Провайдер не выбран. Настройте провайдера и активируйте его, чтобы создать чат."));
                NeonLogger.LogError(ex.ToString());
                return false;
            }
        }

        // ===== Regenerate =====

        private void OnRegenerateClicked()
        {
            _ = RegenerateLastAsync();
        }

        private async Task RegenerateLastAsync()
        {
            _approvalController?.Dismiss();
            DismissSessionPicker();
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
                        _streamingCoordinator.Begin();
                        await chat.RegenerateAsync(_streamingCoordinator.OnToken, OnToolProgress);
                        ClearThinkingBubble();
                        _approvalController?.ClearToolProgress();
                        _approvalController?.Dismiss();
                        DismissSessionPicker();

                        // Finalize stats with real usage from client
                        _streamingCoordinator.PauseStatsSchedule();
                        try
                        {
                            var app = _d.GetAppAsync().Result;
                            var client = app?.AiClient as OpenAiCompatibleClient;
                            if (client != null)
                            {
                                var usage = client.LastStreamUsage;
                                if (usage.total_tokens > 0)
                                {
                                    double elapsed = (DateTime.UtcNow - _streamingCoordinator.StartTime).TotalSeconds;
                                    if (elapsed < 0)
                                        elapsed = 0;
                                    _streamingCoordinator.SetFinalStats(usage.total_tokens, elapsed);
                                    // Persist precise usage to the message model so it survives re-renders and reloads (U-28)
                                    try
                                    {
                                        var vm = chat.CurrentChatViewModel;
                                        if (vm != null && vm.Messages != null && vm.Messages.Count > 0)
                                        {
                                            var last = vm.Messages[vm.Messages.Count - 1];
                                            if (last != null && string.Equals(NormalizeRole(last.role), "assistant", StringComparison.OrdinalIgnoreCase))
                                            {
                                                last.tokenCount = _streamingCoordinator.EstimatedTokens;
                                                last.responseTimeSeconds = (float)elapsed;
                                            }
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                        catch
                        {
                            // keep estimate if no real usage
                        }

                        _streamingCoordinator.Abort();
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
                    _streamingCoordinator?.Abort();
                    _approvalController?.Dismiss();
                    DismissSessionPicker();
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

            // Prefer real context_length from gateway runtime info, then discovery API, then saved value, then heuristic guess.
            int contextWindow = 0;

            // 1. Gateway runtime info (most accurate — model's actual context window)
            try
            {
                var rt = GlobalBackendSelector.Instance?.SessionManager?.RuntimeInfo?.usage;
                if (rt != null && rt.context_max > 0)
                    contextWindow = rt.context_max;
            }
            catch { /* non-critical */ }

            if (provider != null)
            {
                string modelId = chat.CurrentSessionModel ?? provider.defaultModel;
                // 2. Discovery API (/v1/models)
                if (contextWindow <= 0)
                {
                    try
                    {
                        var app = _d.GetAppAsync().Result;
                        NeonCompanion.Runtime.Core.ModelDiscoveryService disc = null;
                        if (app?.Services != null)
                            app.Services.TryGet<NeonCompanion.Runtime.Core.ModelDiscoveryService>(out disc);
                        if (disc != null && !string.IsNullOrEmpty(modelId))
                            contextWindow = disc.GetContextWindowForModel(provider, modelId);
                    }
                    catch { /* non-critical */ }
                }

                // 3. Provider config
                if (contextWindow <= 0 && provider.contextWindow > 0)
                    contextWindow = provider.contextWindow;
                // 4. Heuristic guess
                if (contextWindow <= 0)
                    contextWindow = GuessContextWindow(provider, chat.CurrentSessionModel);
            }

            if (contextWindow <= 0)
            {
                _contextBar.style.display = DisplayStyle.None;
                return;
            }

            // Prefer gateway's context_used, fall back to local estimate
            int used = 0;
            try
            {
                var rt = GlobalBackendSelector.Instance?.SessionManager?.RuntimeInfo?.usage;
                if (rt != null && rt.context_used > 0)
                    used = rt.context_used;
            }
            catch { }
            if (used <= 0)
                used = EstimateSessionTokens();
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

        private static int GuessContextWindow(ProviderConfig provider, string selectedModel = null)
        {
            // Use the currently-selected model when available; fall back to the provider default.
            string modelId = !string.IsNullOrEmpty(selectedModel) ? selectedModel
                : (provider != null ? provider.defaultModel : null);

            if (string.IsNullOrEmpty(modelId))
                return 8192;

            string m = modelId.ToLowerInvariant();
            // Large-context flagship models
            if (m.Contains("gpt-4o") || m.Contains("gpt-4-turbo") || m.Contains("claude-3") || m.Contains("gemini-1.5"))
                return 128000;
            // Llama 3 / 3.1 / 3.2 — 128 k by default
            if (m.Contains("llama-3") || m.Contains("llama3") || m.Contains("llama_3"))
                return 131072;
            // Hermes models (Nous-Hermes, hermes-3, etc.) typically 128 k
            if (m.Contains("hermes"))
                return 131072;
            // Mistral / Mixtral
            if (m.Contains("mistral") || m.Contains("mixtral"))
                return 32768;
            // GPT-4 classic
            if (m.Contains("gpt-4") || m.Contains("claude"))
                return 8192;
            // GPT-3.5
            if (m.Contains("gpt-3.5"))
                return 16384;
            // Small models
            if (m.Contains("phi") || m.Contains("gemma"))
                return 4096;
            // Generic local / unknown
            return 8192;
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

            bool hasSession = messages != null;
            SetDisplay(_d.Composer, hasSession ? DisplayStyle.Flex : DisplayStyle.None);
            _editController?.CancelEdit();
            if (_searchController != null && _searchController.IsVisible)
                _searchController.Hide();
            if (_selectionManager != null && _selectionManager.IsSelecting)
                _messageRowCache.Clear();
            _d.MessagesList.Clear();

            if (messages == null || messages.Count == 0)
            {
                _messageRowCache.Clear();
                _d.MessagesList.Add(CreateEmptyTranscript(hasSession));
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
                if (_selectionManager == null || !_selectionManager.IsSelecting)
                    _messageRowCache.TryGetValue(renderKey, out row);
                if (row == null)
                    row = CreateMessageElement(message, ShowImageLightbox, ScrollTranscriptToBottom);
                // Tag with model index so context menu / edit can identify which message it represents
                row.userData = i;
                var bubbleForTag = row.Q<VisualElement>(className: "transcript__bubble");
                if (bubbleForTag != null)
                    bubbleForTag.userData = i;

                // Capture index outside any inner block for safe closure (C# 9 rule)
                int rowIndex = i;

                // Apply selection state (U-31) after creating the row
                if (_selectionManager != null && _selectionManager.IsSelecting)
                {
                    bool selected = _selectionManager.IsIndexSelected(rowIndex);
                    if (selected)
                        row.AddToClassList("transcript__row--selected");

                    bool isSystemRow = row.ClassListContains("transcript__row--system");
                    if (!isSystemRow)
                    {
                        // Click/tap toggles selection (replaces normal right-click/long-press behavior in selection mode)
                        row.RegisterCallback<ClickEvent>(_ => _selectionManager.ToggleSelection(rowIndex));
                    }
                }

                _d.MessagesList.Add(row);
                if (_selectionManager == null || !_selectionManager.IsSelecting)
                    nextCache[renderKey] = row;
                hasVisibleMessages = true;
            }

            _messageRowCache.Clear();
            foreach (var pair in nextCache)
                _messageRowCache[pair.Key] = pair.Value;

            if (!hasVisibleMessages)
            {
                _messageRowCache.Clear();
                _d.MessagesList.Add(CreateEmptyTranscript(true));
                return;
            }

            ScrollTranscriptToBottom();
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
                var createButton = new Button(OnNewSessionClicked)
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

            // Expandable reasoning/thinking section
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
                        (attachment.kind == "image" || IsImageFile(attachment.path)))
                    {
                        var imageElement = new Image();
                        imageElement.AddToClassList("transcript__image");
                        imageElement.scaleMode = ScaleMode.ScaleToFit;
                        LoadImageAsync(imageElement, attachment.path, onImageLoaded);
                        string imgPath = attachment.path; // capture for closure
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
            // For completed messages with persisted usage (U-28), show immediately.
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

            // Collect all segments, merging ALL text into one block for proper markdown rendering.
            // Tool entries stay in their original positions relative to text.
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

            // Render combined text as one block (tables, code blocks, etc. stay intact)
            if (hasText)
            {
                bubble.Add(CreateTranscriptBody(allText.ToString(), true));
            }

            // Render tool entries separately (after text, in order)
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
            list.schedule.Execute(PinTranscriptToBottom).StartingIn(50);
            list.schedule.Execute(PinTranscriptToBottom).StartingIn(150);
            list.schedule.Execute(PinTranscriptToBottom).StartingIn(300);

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
            bool previousWasInlineWhitespace = false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '\r')
                    continue;

                if (c == '\n')
                {
                    sb.Append('\n');
                    previousWasInlineWhitespace = false;
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    if (!previousWasInlineWhitespace)
                        sb.Append(' ');
                    previousWasInlineWhitespace = true;
                }
                else
                {
                    sb.Append(c);
                    previousWasInlineWhitespace = false;
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

        // ── Image Lightbox ──────────────────────────────────────────

        private void ShowImageLightbox(string imagePath)
        {
            if (string.IsNullOrEmpty(imagePath)) return;

            VisualElement root = GetOverlayRoot();
            if (root == null) return;

            HideLightbox();

            _lightbox = new VisualElement();
            _lightbox.name = "image-lightbox";
            _lightbox.AddToClassList("lightbox");
            _lightbox.focusable = true;
            _lightbox.pickingMode = PickingMode.Position;
            ApplyFullscreenOverlayLayout(_lightbox);

            // Click on the dark background closes the overlay
            _lightbox.RegisterCallback<ClickEvent>(evt =>
            {
                if (evt.target == _lightbox)
                {
                    HideLightbox();
                    evt.StopPropagation();
                }
            });

            // ESC closes the overlay (requires focus — set below)
            _lightbox.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Escape)
                {
                    HideLightbox();
                    evt.StopPropagation();
                }
            });

            // Image element — ScaleToFit preserves aspect ratio within the USS-defined bounds
            var imgEl = new Image();
            imgEl.AddToClassList("lightbox__image");
            imgEl.scaleMode = ScaleMode.ScaleToFit;
            ApplyLightboxImageLayout(imgEl);
            // Stop propagation so click on image itself does NOT close the overlay
            imgEl.RegisterCallback<ClickEvent>(evt => evt.StopPropagation());
            LoadImageAsync(imgEl, imagePath);
            _lightbox.Add(imgEl);

            // Close button (×) — top-right corner
            var closeBtn = new Button(HideLightbox);
            closeBtn.text = "×";
            closeBtn.AddToClassList("lightbox__close");
            ApplyLightboxCloseLayout(closeBtn);
            _lightbox.Add(closeBtn);

            root.Add(_lightbox);
            _lightbox.BringToFront();

            // Focus after layout tick so ESC key events are received
            _lightbox.schedule.Execute(() => _lightbox?.Focus()).StartingIn(50);
        }

        private void HideLightbox()
        {
            if (_lightbox == null) return;
            _lightbox.RemoveFromHierarchy();
            _lightbox = null;
        }

        private VisualElement GetOverlayRoot()
        {
            if (_d.MessagesList != null && _d.MessagesList.panel != null)
                return _d.MessagesList.panel.visualTree;

            if (_d.Composer != null && _d.Composer.panel != null)
                return _d.Composer.panel.visualTree;

            if (_transcriptContextRoot != null && _transcriptContextRoot.panel != null)
                return _transcriptContextRoot.panel.visualTree;

            return null;
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

        // ────────────────────────────────────────────────────────────

        private static async void LoadImageAsync(Image imageElement, string path, Action onLoaded = null)
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
                            if (onLoaded != null)
                                onLoaded();
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

        private void DismissSessionPicker()
        {
            if (_sessionPickerOverlay != null && _sessionPickerOverlay.parent != null)
                _sessionPickerOverlay.RemoveFromHierarchy();

            CleanupPickerHandlers();
            _sessionPickerOverlay = null;
            _sessionPickerPanel = null;
        }

        private void CleanupPickerHandlers()
        {
            if (_pickerRoot != null && _pickerOutsideHandler != null)
                _pickerRoot.UnregisterCallback(_pickerOutsideHandler, TrickleDown.TrickleDown);

            _pickerOutsideHandler = null;
            _pickerRoot = null;
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

                        bool approved = await _approvalController.RequestToolApprovalAsync(request);
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
                        _approvalController?.Dismiss();
                        _approvalController?.ClearToolProgress();

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

        private void OnTranscriptRootMouseDown(MouseDownEvent evt)
        {
            if (_d.MessagesList == null || evt == null)
                return;

            bool isContextButton = evt.button != 0 || (evt.pressedButtons & 2) != 0;
            if (!isContextButton)
                return;

            VisualElement target = evt.target as VisualElement;
            Vector2 pos = evt.mousePosition;
            bool insideByTarget = IsInsideMessagesList(target);
            bool insideByPos = _d.MessagesList.worldBound.Contains(pos);
            if (!insideByTarget && !insideByPos)
                return;

            evt.StopImmediatePropagation();
            evt.StopPropagation();
#pragma warning disable CS0618
            evt.PreventDefault();
#pragma warning restore CS0618

            if (_selectionManager != null && _selectionManager.IsSelecting)
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
            if (_d.MessagesList == null)
                return;

            Vector2 pos = evt.position;
            bool isContextButton = evt.button != 0 || (evt.pressedButtons & 2) != 0;

            if (isContextButton)
            {
                // Always suppress default context behavior (blue strip / native fallback).
                evt.StopImmediatePropagation();
                evt.StopPropagation();
#pragma warning disable CS0618
                evt.PreventDefault();
#pragma warning restore CS0618

                if (_selectionManager != null && _selectionManager.IsSelecting)
                    return;
                return;
            }

            // Do not start long-press / selection mode when the left-click landed on a
            // selectable transcript TextField — the user is selecting text, not the message.
            if (evt.button == 0 && IsInsideSelectableTextField(evt.target as VisualElement))
                return;

            VisualElement bubble = ResolveBubbleFromEvent(evt.target as VisualElement, pos);
            if (bubble == null)
                return;

            // Skip system bubbles for context menu
            if (bubble.ClassListContains("transcript__bubble--system"))
                return;

            int? msgIndex = GetMessageIndexFromElement(bubble);
            if (msgIndex == null)
                return;

            bool isUser = bubble.ClassListContains("transcript__bubble--user");

            if (_selectionManager != null && _selectionManager.IsSelecting)
            {
                // Selection mode: per-row ClickEvent handles toggles for selectable messages.
                // No long-press/context menu here; selection toggles are handled by row ClickEvent.
                return;
            }

            if (evt.button == 0)
            {
                bool longPressSelect = string.Equals(evt.pointerType, "mouse", StringComparison.OrdinalIgnoreCase);

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
                        if (longPressSelect)
                        {
                            _selectionManager?.EnterSelectionMode(_longPressIndex);
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
            if (_d.MessagesList == null || evt == null)
                return;

            var target = evt.target as VisualElement;
            Vector2 triggerPos;
            bool hasTriggerPos = TryGetEventPosition(evt.triggerEvent, out triggerPos);
            bool insideByTarget = IsInsideMessagesList(target);
            bool insideByPos = hasTriggerPos && _d.MessagesList.worldBound.Contains(triggerPos);
            if (!insideByTarget && !insideByPos)
                return;

            // Always suppress Unity's default context menu path in transcript.
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

        private Vector2 NormalizeToPanelPosition(Vector2 position)
        {
            if (_d.MessagesList == null)
                return position;

            // If already in panel space, it should lie within or near the transcript bounds.
            if (_d.MessagesList.worldBound.Contains(position))
                return position;

            // Fallback: treat value as local-to-messages-list and convert to panel/world space.
            return _d.MessagesList.LocalToWorld(position);
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
            if (_d.MessagesList == null || element == null)
                return false;

            var current = element;
            while (current != null)
            {
                if (current == _d.MessagesList)
                    return true;
                current = current.parent;
            }

            return false;
        }

        // Returns true if 'el' is inside (or is) a read-only transcript TextField.
        // Used to skip long-press selection when the user is selecting text (U-34).
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

        // Registers mouse-enter/leave callbacks that swap to a text I-beam cursor (U-34).
        private static Texture2D s_TextCursorTex;

        private static void ApplyTextCursor(VisualElement el)
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
            var t = new Color32(0, 0, 0, 0);
            for (int i = 0; i < px.Length; i++) px[i] = t;
            // top crossbar (Unity: y=0 is bottom, top rows = h-1 and h-2)
            for (int x = 2; x <= 7; x++) { px[(h - 1) * w + x] = c; px[(h - 2) * w + x] = c; }
            // bottom crossbar
            for (int x = 2; x <= 7; x++) { px[0 * w + x] = c; px[1 * w + x] = c; }
            // vertical stem (center x = 4 or 5, use 4)
            for (int y = 2; y <= h - 3; y++) px[y * w + 4] = c;
            s_TextCursorTex.SetPixels32(px);
            s_TextCursorTex.Apply(false);
            return s_TextCursorTex;
        }

        private VisualElement ResolveBubbleFromEvent(VisualElement target, Vector2 panelPosition)
        {
            var bubble = FindBubbleAncestor(target);
            if (bubble != null)
                return bubble;

            if (_d.MessagesList == null)
                return null;

            foreach (var child in _d.MessagesList.Children())
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
                _contextMenu = new MessageContextMenu();

            // Hide any previous
            _contextMenu.Hide();

            // Pass click position for reliable placement near the tapped/clicked message bubble.
            _contextMenu.ShowAt(target, messageIndex, isUser, position);
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

            _editController.BeginEditMessage(index, bubble, msg.content ?? string.Empty);
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

        // ===== Message selection (U-31/U-32) — event handlers for ChatSelectionManager =====

        private void OnSelectionBulkDelete(IReadOnlyList<string> ids)
        {
            var chat = _d.GetChatServiceAsync().Result;
            if (chat?.CurrentChatViewModel == null) return;

            var indices = new List<int>(ids.Count);
            for (int i = 0; i < ids.Count; i++)
            {
                int idx;
                if (int.TryParse(ids[i], out idx))
                    indices.Add(idx);
            }
            indices.Sort((a, b) => b.CompareTo(a));

            for (int i = 0; i < indices.Count; i++)
            {
                int idx = indices[i];
                if (idx >= 0 && idx < chat.CurrentChatViewModel.Messages.Count)
                    chat.CurrentChatViewModel.Messages.RemoveAt(idx);
            }

            _ = chat.SaveCurrentSessionAsync();
            _d.RenderMessages(chat.CurrentChatViewModel.Messages);
        }

        private void OnSelectionBulkForward(IReadOnlyList<string> ids)
        {
            _ = ForwardSelectedAsync(ids);
        }

        private async Task ForwardSelectedAsync(IReadOnlyList<string> ids)
        {
            if (ids == null || ids.Count == 0) return;

            var chat = await _d.GetChatServiceAsync();
            if (chat == null || chat.CurrentChatViewModel == null) return;

            var source = chat.CurrentChatViewModel.Messages;
            var toForward = new List<ChatMessage>();
            var indices = new List<int>(ids.Count);
            for (int i = 0; i < ids.Count; i++)
            {
                int idx;
                if (int.TryParse(ids[i], out idx))
                    indices.Add(idx);
            }
            indices.Sort();

            for (int i = 0; i < indices.Count; i++)
            {
                int idx = indices[i];
                if (idx >= 0 && idx < source.Count)
                    toForward.Add(source[idx]);
            }

            if (toForward.Count == 0)
            {
                _selectionManager?.ExitSelectionMode();
                return;
            }

            var all = await chat.GetAllSessionsAsync();
            string currentId = chat.CurrentSessionId;

            var candidates = new List<ChatSession>();
            for (int i = 0; i < all.Count; i++)
            {
                var s = all[i];
                if (s != null && s.sessionId != currentId)
                    candidates.Add(s);
            }

            if (candidates.Count == 0)
            {
                _selectionManager?.ExitSelectionMode();
                return;
            }

            var target = await ShowSessionPickerAsync(candidates);
            if (target == null)
            {
                // Cancel or outside click — leave selection mode active so user can try again or cancel
                return;
            }

            int added = await chat.AppendMessagesToSessionAsync(target.sessionId, toForward);
            _selectionManager?.ExitSelectionMode();

            if (added > 0)
            {
                string done = LocalizationExtensions.Get("chat.selection.forward_done", "Forwarded {0} messages")
                    .Replace("{0}", added.ToString());
                _d.ShowSystemMessage(done);
            }
        }

        private async Task<ChatSession> ShowSessionPickerAsync(List<ChatSession> candidates)
        {
            var tcs = new TaskCompletionSource<ChatSession>();

            var overlay = new VisualElement();
            overlay.AddToClassList("session-picker-overlay");

            // Find root to attach (reuse pattern from context menu)
            VisualElement root = null;
            var selBar = _selectionManager?.SelectionBar;
            if (selBar != null && selBar.panel != null)
                root = GetDocumentRoot(selBar);
            if (root == null && _d.MessagesList != null && _d.MessagesList.panel != null)
                root = GetDocumentRoot(_d.MessagesList);
            if (root == null)
            {
                tcs.SetResult(null);
                return await tcs.Task;
            }

            var picker = new VisualElement();
            picker.AddToClassList("session-picker");
            picker.RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation());

            var headerLabel = new Label(LocalizationExtensions.Get("chat.selection.pick_session", "Pick a chat to forward to"));
            headerLabel.AddToClassList("session-picker__header");
            picker.Add(headerLabel);

            var listScroll = new ScrollView();
            listScroll.AddToClassList("session-picker__list");
            listScroll.style.flexGrow = 1f;
            listScroll.style.minHeight = 60f;

            for (int i = 0; i < candidates.Count; i++)
            {
                var s = candidates[i];
                if (s == null) continue;

                var item = new VisualElement();
                item.AddToClassList("session-picker__item");

                string titleText = string.IsNullOrWhiteSpace(s.title) ? LocalizationExtensions.Get("chat.new", "New chat") : s.title;
                var titleLabel = new Label(titleText);
                titleLabel.AddToClassList("session-picker__title");

                var timeLabel = new Label(FormatSessionTimestamp(s.updatedAtUnix));
                timeLabel.AddToClassList("session-picker__time");

                item.Add(titleLabel);
                item.Add(timeLabel);

                WireSessionPickerItem(item, s, tcs);
                listScroll.Add(item);
            }

            picker.Add(listScroll);

            // Footer cancel
            var footer = new VisualElement();
            footer.AddToClassList("session-picker__footer");
            footer.style.flexDirection = FlexDirection.Row;
            footer.style.justifyContent = Justify.FlexEnd;
            footer.style.marginTop = 6f;

            var cancelBtn = new Button(() =>
            {
                DismissSessionPicker();
                tcs.TrySetResult(null);
            })
            { text = LocalizationExtensions.Get("chat.selection.cancel", "Cancel") };
            cancelBtn.AddToClassList("selection-bar__btn");
            footer.Add(cancelBtn);
            picker.Add(footer);

            overlay.Add(picker);
            root.Add(overlay);

            _sessionPickerOverlay = overlay;
            _sessionPickerPanel = picker;
            _pickerRoot = root;

            _pickerOutsideHandler = (PointerDownEvent evt) =>
            {
                if (_sessionPickerPanel != null && _sessionPickerPanel.worldBound.Contains(evt.position))
                    return;
                DismissSessionPicker();
                tcs.TrySetResult(null);
            };
            root.RegisterCallback(_pickerOutsideHandler, TrickleDown.TrickleDown);

            return await tcs.Task;
        }

        private static string FormatSessionTimestamp(long unixSeconds)
        {
            if (unixSeconds <= 0)
                return "";
            try
            {
                var dt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime();
                return dt.ToString("dd.MM HH:mm");
            }
            catch
            {
                return "";
            }
        }

        private VisualElement GetDocumentRoot(VisualElement start)
        {
            if (start == null)
                return null;
            var el = start;
            var panelVisualTree = start.panel != null ? start.panel.visualTree : null;
            while (el.parent != null && el.parent != panelVisualTree)
                el = el.parent;
            return el;
        }

        private void WireSessionPickerItem(VisualElement item, ChatSession session, TaskCompletionSource<ChatSession> tcs)
        {
            item.RegisterCallback<ClickEvent>(evt =>
            {
                evt.StopPropagation();
                DismissSessionPicker();
                tcs.TrySetResult(session);
            });
            item.RegisterCallback<PointerEnterEvent>(_ => item.AddToClassList("session-picker__item--hover"));
            item.RegisterCallback<PointerLeaveEvent>(_ => item.RemoveFromClassList("session-picker__item--hover"));
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

            _selectionManager?.EnterSelectionMode(index);
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
