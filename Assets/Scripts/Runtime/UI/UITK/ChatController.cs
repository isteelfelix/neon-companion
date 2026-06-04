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
        private ChatNotificationManager _notifications;
        private ChatInputManager _inputManager;
        private ChatAttachmentManager _attachmentManager;
        private ChatService _currentChatService;
        private ChatStreamingCoordinator _streamingCoordinator;
        private ToolCallApprovalController _approvalController;
        private string _chatSubtitle = string.Empty;
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

        // Message list rendering (U-29) — delegated to ChatMessageListRenderer
        private ChatMessageListRenderer _messageListRenderer;

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
            _attachmentManager = new ChatAttachmentManager(
                _d.Composer,
                _d.MessageInput,
                _d.GetAppAsync,
                _d.ShowSystemMessage,
                GetOverlayRoot);
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
                m => ChatMessageListRenderer.CreateMessageElement(m),
                ApplyTextCursor,
                bubble => _approvalController?.SetBubble(bubble),
                () => { _d.GetAvatarAnimationController?.Invoke()?.TriggerStreamStart(); _d.RefreshAvatarMotionState?.Invoke(); });

            _messageListRenderer = new ChatMessageListRenderer(
                _d.MessagesList,
                _contextMenu,
                _d.GetAvatarDisplayName,
                _d.TopbarSubtitle,
                _d.NavChatCount,
                s => { _chatSubtitle = s; },
                ShowImageLightbox,
                ScrollTranscriptToBottom,
                () => _selectionManager != null && _selectionManager.IsSelecting,
                i => _selectionManager != null && _selectionManager.IsIndexSelected(i),
                i => _selectionManager?.ToggleSelection(i),
                () => { _ = StartNewSessionAsync(); });
        }

        public void SetVoiceRecording(bool value) { _inputManager?.SetVoiceRecording(value); }
        public void SetChatSubtitle(string value) { _chatSubtitle = value ?? string.Empty; }
        public void SetSessionSearchQuery(string value) { _searchController?.SetSessionSearchQuery(value); }
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

            // Context menu triggers on transcript (right-click + long-press) — delegated to renderer
            _messageListRenderer?.RegisterCallbacks();

            if (_d.MessageInput != null)
            {
                _inputManager.RegisterCallbacks();
                _d.MessageInput.RegisterValueChangedCallback(OnComposerTextChangedForAvatar);
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
                () => _messageListRenderer?.Render(_d.GetChatServiceAsync().Result?.CurrentChatViewModel?.Messages));
            _selectionManager.OnBulkDelete += OnSelectionBulkDelete;
            _selectionManager.OnBulkForward += OnSelectionBulkForward;

            var chatMain = _d.Composer?.parent;
            _attachmentManager?.RegisterCallbacks(chatMain);

            // Application.focusChanged is managed by ChatNotificationManager
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

            if (_d.MessageInput != null)
            {
                _d.MessageInput.UnregisterValueChangedCallback(OnComposerTextChangedForAvatar);
            }

            _inputManager?.UnregisterCallbacks();

            _messageListRenderer?.UnregisterCallbacks();

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

            var chatMain = _d.Composer?.parent;
            _attachmentManager?.UnregisterCallbacks(chatMain);

            // Application.focusChanged managed by ChatNotificationManager (Dispose in future)
        }

        public void InitState()
        {
            SetSending(false);
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

            bool hasPendingAttachments = _attachmentManager != null && _attachmentManager.CurrentAttachments.Count > 0;
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
                var qAttach = _attachmentManager.CloneCurrent();
                string qMsg = StripAttachmentTokens(composerText, qAttach);
                _messageQueue.Enqueue(new QueuedMessage { Message = qMsg, Attachments = qAttach });
                _d.MessageInput.value = string.Empty;
                _inputManager.QueueComposerHeightUpdate();
                ClearPendingComposerAttachments();
                RenderQueueIndicator();
                return;
            }

            var pendingAttachments = _attachmentManager.CloneCurrent();
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
                                        if (last != null && string.Equals(ChatMessageListRenderer.NormalizeRole(last.role), "assistant", StringComparison.OrdinalIgnoreCase))
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
                    _attachmentManager.Restore(nextQueuedMessage.Attachments);
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

        private void OnAttachClicked() { _attachmentManager?.OpenFilePicker(); }

        public void ClearPendingComposerAttachments() { _attachmentManager?.Clear(); }

        private void RestorePendingComposerAttachments(IReadOnlyList<ChatAttachment> attachments)
        {
            _attachmentManager?.Restore(attachments);
        }

        private void RestoreComposerDraft(string message, IReadOnlyList<ChatAttachment> attachments)
        {
            _attachmentManager?.RestoreDraft(message, attachments, _inputManager.QueueComposerHeightUpdate);
        }

        private void RenderQueueIndicator()
        {
            ChatAttachmentManager.RenderQueueIndicator(_messageQueue, _queueIndicator);
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
                string role = ChatMessageListRenderer.NormalizeRole(msg.role);
                sb.AppendLine($"[{ChatMessageListRenderer.DisplayRole(role)}]");
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
                                            if (last != null && string.Equals(ChatMessageListRenderer.NormalizeRole(last.role), "assistant", StringComparison.OrdinalIgnoreCase))
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

        // ===== Message Rendering (delegated to ChatMessageListRenderer) =====

        public void RenderMessages(IReadOnlyList<ChatMessage> messages)
        {
            bool hasSession = messages != null;
            SetDisplay(_d.Composer, hasSession ? DisplayStyle.Flex : DisplayStyle.None);
            _editController?.CancelEdit();
            if (_searchController != null && _searchController.IsVisible)
                _searchController.Hide();

            _messageListRenderer?.Render(messages);
            UpdateContextBar();
        }

        private void OnScrollBottomClicked() { _messageListRenderer?.ScrollToBottom(); }

        public void ScrollTranscriptToBottom()
        {
            _messageListRenderer?.ScrollToBottom();
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

        internal static string GetAttachmentDisplayName(ChatAttachment attachment)
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

            if (_messageListRenderer != null && _messageListRenderer._transcriptContextRoot != null && _messageListRenderer._transcriptContextRoot.panel != null)
                return _messageListRenderer._transcriptContextRoot.panel.visualTree;

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

        internal static bool IsImageFile(string path)
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

        // ===== Message context menu (U-29/U-30) delegated to ChatMessageListRenderer =====

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
            string role = ChatMessageListRenderer.NormalizeRole(msg.role);
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

        internal static VisualElement GetDocumentRoot(VisualElement el)
        {
            if (el == null)
                return null;
            var panelVisualTree = el.panel != null ? el.panel.visualTree : null;
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
                    string role = ChatMessageListRenderer.NormalizeRole(chat.CurrentChatViewModel.Messages[index].role);
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

        internal static void OnCopyClickedStatic() => CopyRequested?.Invoke();
        internal static void OnRegenerateClickedStatic() => RegenerateRequested?.Invoke();
        internal static void OnListenClickedStatic() => ListenRequested?.Invoke();

        internal static void RegisterClickStatic(Button button, Action handler)
        {
            if (button != null)
                button.clicked += handler;
        }
    }
}
