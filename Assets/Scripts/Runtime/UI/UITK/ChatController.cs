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
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK
{
    internal sealed class ChatController
    {
        public struct Deps
        {
            // If a voice preview is active in the composer, this sends it and returns true,
            // so the standard send button doubles as the voice-message send.
            public Func<bool> TrySendVoicePreview;

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
            // Voice bubble replay
            public Action<string> PlayAudioFile;
        }

        // QueuedMessage extracted to Models/Chat/QueuedMessage.cs

        private Deps _d;
        private ChatNotificationManager _notifications;
        private ChatInputManager _inputManager;
        private ChatAttachmentManager _attachmentManager;
        private ChatService _currentChatService;
        private ChatStreamingCoordinator _streamingCoordinator;
        private ToolCallApprovalController _approvalController;
        private string _chatSubtitle = string.Empty;

        // Voice: set before SendCurrentMessageAsync() to attach audio to the outgoing user message.
        private string _pendingVoiceAudioPath = "";
        private float _pendingVoiceDurationSecs;

        // Message queue (U-45)
        private readonly Queue<QueuedMessage> _messageQueue = new Queue<QueuedMessage>();
        private Label _queueIndicator;

        // Context window indicator (U-36)
        private VisualElement _contextBar;
        private VisualElement _contextBarFill;
        private Label _contextBarLabel;

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

        public bool IsSending => _streamingCoordinator != null && _streamingCoordinator.IsSending;
        public bool IsStreamingResponse => _streamingCoordinator != null && _streamingCoordinator.IsStreaming;
        public string ChatSubtitle => _chatSubtitle;
        public string SessionSearchQuery => _searchController != null ? _searchController.SessionSearchQuery : string.Empty;

        public void SetDeps(Deps deps)
        {
            _d = deps;
            _notifications = new ChatNotificationManager(_d.NavChatCount);
            _inputManager = new ChatInputManager(
                _d.MessageInput,
                _d.Composer,
                _d.EnterToSend,
                _d.GetChatServiceAsync,
                _d.ShowSystemMessage,
                _d.OpenModelPickerAsync,
                _d.ApplyModelSelectionAsync,
                _d.RenderMessages,
                () =>
                {
                    _messageQueue.Clear();
                    RenderQueueIndicator();
                },
                StartNewSessionAsync);
            _inputManager.OnSubmit += _ => OnSendClicked();
            _attachmentManager = new ChatAttachmentManager(
                _d.Composer,
                _d.MessageInput,
                _d.GetAppAsync,
                _d.ShowSystemMessage,
                path => _messageListRenderer?.ShowImageLightbox(path));
            _searchController = new ChatSearchController(_d.MessagesList, _d.GetChatServiceAsync);
            _editController = new ChatMessageEditController(
                _d.GetChatServiceAsync,
                _d.RenderMessages,
                _d.LoadSessionsAsync,
                () => RegenerateLastAsync(),
                _d);

            _approvalController = new ToolCallApprovalController(
                _d.MessagesList,
                ScrollTranscriptToBottom,
                _d.GetAppAsync,
                _d.ShowSystemMessage,
                () => IsSending,
                () => _streamingCoordinator != null && _streamingCoordinator.IsStreaming,
                () => _currentChatService);

            _streamingCoordinator = new ChatStreamingCoordinator(
                _d.MessagesList,
                ScrollTranscriptToBottom,
                m => ChatMessageListRenderer.CreateMessageElement(m),
                ChatMessageListRenderer.ApplyTextCursor,
                bubble => _approvalController?.SetBubble(bubble),
                () => { _d.GetAvatarAnimationController?.Invoke()?.TriggerStreamStart(); _d.RefreshAvatarMotionState?.Invoke(); },
                _d.ThinkingBubble,
                _d.ThinkingText,
                _d.SendButton,
                _d.StopButton,
                _approvalController,
                () => _d.GetAvatarAnimationController?.Invoke()?.TriggerStreamEnd(),
                _d.RefreshAvatarMotionState);

            _messageListRenderer = new ChatMessageListRenderer(
                _d.MessagesList,
                _editController,
                _d.GetAvatarDisplayName,
                _d.TopbarSubtitle,
                _d.NavChatCount,
                s => { _chatSubtitle = s; },
                ScrollTranscriptToBottom,
                () => _selectionManager != null && _selectionManager.IsSelecting,
                i => _selectionManager != null && _selectionManager.IsIndexSelected(i),
                i => _selectionManager?.ToggleSelection(i),
                () => { _ = StartNewSessionAsync(); },
                _d.PlayAudioFile);
            _editController.SetMessageListRenderer(_messageListRenderer);
        }

        public void SetVoiceRecording(bool value) { _inputManager?.SetVoiceRecording(value); }
        public void SetChatSubtitle(string value) { _chatSubtitle = value ?? string.Empty; }
        public void SetSessionSearchQuery(string value) { _searchController?.SetSessionSearchQuery(value); }
        public void ShowSystemMessage(string text) { _d.ShowSystemMessage?.Invoke(text); }

        public void RegisterCallbacks()
        {
            ChatMessageListRenderer.RegisterClick(_d.SendButton, OnSendClicked);
            ChatMessageListRenderer.RegisterClick(_d.StopButton, OnStopClicked);
            ChatMessageListRenderer.RegisterClick(_d.SummarizeButton, OnSummarizeClicked);
            ChatMessageListRenderer.RegisterClick(_d.SearchButton, OnSearchClicked);
            ChatMessageListRenderer.RegisterClick(_d.AttachButton, OnAttachClicked);
            ChatMessageListRenderer.RegisterClick(_d.NewSessionButton, OnNewSessionClicked);
            ChatMessageListRenderer.RegisterClick(_d.ExportButton, OnExportClicked);
            ChatMessageListRenderer.RegisterClick(_d.ScrollBottomBtn, OnScrollBottomClicked);

            // Wire up static bubble action events
            CopyRequested += OnCopyClicked;
            CopyMessageRequested += OnCopyMessageClicked;
            RegenerateRequested += OnRegenerateClicked;

            _approvalController?.Subscribe();
            if (_approvalController != null)
                _approvalController.OnStopRequested += OnStopClicked;

            _editController?.RegisterCallbacks(OnSelectMessageRequested);

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
                () => _messageListRenderer?.Render(_d.GetChatServiceAsync().Result?.CurrentChatViewModel?.Messages),
                _d,
                _messageListRenderer,
                ShowSessionPickerAsync);
            _selectionManager.OnBulkDelete += OnSelectionBulkDelete;
            _selectionManager.OnBulkForward += OnSelectionBulkForward;

            var chatMain = _d.Composer?.parent;
            _attachmentManager?.RegisterCallbacks(chatMain);

            // Application.focusChanged is managed by ChatNotificationManager
        }

        public void UnregisterCallbacks()
        {
            ChatMessageListRenderer.UnregisterClick(_d.SendButton, OnSendClicked);
            ChatMessageListRenderer.UnregisterClick(_d.StopButton, OnStopClicked);
            ChatMessageListRenderer.UnregisterClick(_d.SummarizeButton, OnSummarizeClicked);
            ChatMessageListRenderer.UnregisterClick(_d.SearchButton, OnSearchClicked);
            ChatMessageListRenderer.UnregisterClick(_d.AttachButton, OnAttachClicked);
            ChatMessageListRenderer.UnregisterClick(_d.NewSessionButton, OnNewSessionClicked);
            ChatMessageListRenderer.UnregisterClick(_d.ExportButton, OnExportClicked);
            ChatMessageListRenderer.UnregisterClick(_d.ScrollBottomBtn, OnScrollBottomClicked);

            CopyRequested -= OnCopyClicked;
            CopyMessageRequested -= OnCopyMessageClicked;
            RegenerateRequested -= OnRegenerateClicked;
            if (_approvalController != null)
                _approvalController.OnStopRequested -= OnStopClicked;
            _approvalController?.Unsubscribe();

            _editController?.UnregisterCallbacks();

            if (_d.MessageInput != null)
            {
                _d.MessageInput.UnregisterValueChangedCallback(OnComposerTextChangedForAvatar);
            }

            _inputManager?.UnregisterCallbacks();

            _messageListRenderer?.UnregisterCallbacks();

            _streamingCoordinator?.SetSending(false);
            _streamingCoordinator?.Abort();
            _approvalController?.ClearToolProgress();
            _approvalController?.Dismiss();
            _editController?.Hide();
            _searchController?.Hide();
            DismissSessionPicker();
            _messageListRenderer?.HideLightbox();
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
            _streamingCoordinator?.SetSending(false);
        }

        private void OnComposerTextChangedForAvatar(ChangeEvent<string> evt)
        {
            if (IsSending || _inputManager == null)
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
            _editController?.Hide();
            _currentChatService?.CancelCurrentGeneration();
        }

        public async Task SendCurrentMessageAsync()
        {
            // The standard send button also sends an active voice preview (no separate button).
            if (_d.TrySendVoicePreview != null && _d.TrySendVoicePreview())
                return;

            _notifications.MarkRead();
            _approvalController?.Dismiss();
            DismissSessionPicker();
            _editController?.Hide();

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
            if (IsSending)
            {
                var qAttach = _attachmentManager.CloneCurrent();
                string qMsg = ChatAttachmentManager.StripAttachmentTokens(composerText, qAttach);
                _messageQueue.Enqueue(new QueuedMessage { Message = qMsg, Attachments = qAttach });
                _d.MessageInput.value = string.Empty;
                _inputManager.QueueComposerHeightUpdate();
                ClearPendingComposerAttachments();
                RenderQueueIndicator();
                return;
            }

            var pendingAttachments = _attachmentManager.CloneCurrent();
            string message = ChatAttachmentManager.StripAttachmentTokens(composerText, pendingAttachments);
            _d.MessageInput.value = string.Empty;
            ClearPendingComposerAttachments();
            _inputManager.QueueComposerHeightUpdate();
            _streamingCoordinator?.SetSending(true);
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

                if (chat.CurrentProvider == null || chat.CurrentChatViewModel == null)
                    await chat.GetOrCreateChatAsync();

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
                    await chat.SendMessageAsync(message, pendingAttachments, _streamingCoordinator.OnToken, _streamingCoordinator.OnToolProgress);
                    _streamingCoordinator.ClearThinkingBubble();
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
                _d.ShowSystemMessage(GetSendFailureMessage(ex));
                NeonLogger.LogError(ex.ToString());
                _d.TriggerAvatarConfused();
            }
            finally
            {
                _currentChatService = null;
                _streamingCoordinator?.SetSending(false);

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

        /// <summary>
        /// Send a voice-originated message bypassing the composer input field.
        /// The <paramref name="audioPath"/> is stored in the resulting ChatMessage so the
        /// chat bubble can render a playback button.
        /// </summary>
        public async Task SendDirectVoiceMessageAsync(string text, string audioPath)
        {
            if (string.IsNullOrWhiteSpace(text) || IsSending)
                return;

            _pendingVoiceAudioPath    = audioPath ?? "";
            _pendingVoiceDurationSecs = 0f;
            if (!string.IsNullOrEmpty(audioPath))
            {
                try
                {
                    long fileSize = new System.IO.FileInfo(audioPath).Length;
                    _pendingVoiceDurationSecs = (fileSize - 44) / 2f / 16000f;
                }
                catch { }
            }

            // Also hand the audio to the chat service so it sticks on the persisted user message
            // (the optimistic copy from BuildPendingMessages is dropped on re-render after the reply).
            ChatService chat = await _d.GetChatServiceAsync();
            chat?.SetPendingVoiceAudio(_pendingVoiceAudioPath, _pendingVoiceDurationSecs);

            if (_d.MessageInput != null)
                _d.MessageInput.value = text;

            await SendCurrentMessageAsync();
        }

        private Task<bool> TryHandleCommandAsync(string message)
        {
            return _inputManager.TryHandleCommandAsync(message);
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

        private void OnCopyMessageClicked(string text)
        {
            GUIUtility.systemCopyBuffer = text ?? string.Empty;
            _d.ShowSystemMessage(LocalizationExtensions.Get("msg.copied", "Copied"));
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
            ChatService chat = null;
            try
            {
                _editController?.Hide();

                chat = await _d.GetChatServiceAsync();
                if (chat == null)
                {
                    _d.ShowSystemMessage(LocalizationExtensions.Get("system.app.not_initialized", "Приложение не инициализировано."));
                    return false;
                }

                await chat.StartNewSessionAsync();
                if (string.IsNullOrEmpty(chat.CurrentSessionId) || chat.CurrentChatViewModel == null)
                {
                    _d.RenderMessages(null);
                    _d.ShowSystemMessage(GetCreateChatFailureMessage(chat, null));
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
                _d.ShowSystemMessage(GetCreateChatFailureMessage(chat, ex));
                NeonLogger.LogError(ex.ToString());
                return false;
            }
        }

        private static string GetCreateChatFailureMessage(ChatService chat, Exception exception)
        {
            if (chat == null)
                return LocalizationExtensions.Get("system.app.not_initialized", "Приложение не инициализировано.");

            if (chat.CurrentProvider == null || !chat.CurrentProvider.isEnabled)
            {
                return LocalizationExtensions.Get(
                    "chat.create.no_provider",
                    "Провайдер не выбран. Настройте провайдера и активируйте его, чтобы создать чат.");
            }

            if (ChatService.IsHermesProvider(chat.CurrentProvider))
            {
                var selector = GlobalBackendSelector.Instance;
                string details = selector != null ? selector.LastConnectionError : null;
                if (chat.ChatTransport == null || !chat.ChatTransport.IsConnected)
                {
                    if (!string.IsNullOrWhiteSpace(details))
                    {
                        return LocalizationExtensions.GetFormat(
                            "chat.create.hermes_ws_error",
                            "Hermes WebSocket is not connected: {0}",
                            details);
                    }

                    return LocalizationExtensions.Get(
                        "chat.create.hermes_ws_disconnected",
                        "Hermes provider is active, but WebSocket is not connected. Check the provider URL/key or server availability.");
                }
            }

            if (exception != null && !string.IsNullOrWhiteSpace(exception.Message))
            {
                return LocalizationExtensions.GetFormat(
                    "chat.create.failed_with_error",
                    "Could not create chat: {0}",
                    exception.Message);
            }

            return LocalizationExtensions.Get(
                "chat.create.failed",
                "Could not create chat. Check the provider connection and try again.");
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
                if (chat == null || IsSending) return;

                var messages = chat.CurrentChatViewModel?.Messages;
                if (messages == null || messages.Count == 0) return;

                if (messages[messages.Count - 1].role == "assistant")
                    messages.RemoveAt(messages.Count - 1);

                if (messages.Count == 0) return;

                _streamingCoordinator?.SetSending(true);
                try
                {
                    bool streaming = _d.UseStreaming();
                    chat.UseStreaming = streaming;

                    _d.RenderMessages(chat.CurrentChatViewModel?.Messages);

                    if (streaming)
                    {
                        _streamingCoordinator.Begin();
                        await chat.RegenerateAsync(_streamingCoordinator.OnToken, _streamingCoordinator.OnToolProgress);
                        _streamingCoordinator.ClearThinkingBubble();
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
                    _streamingCoordinator?.SetSending(false);
                }
            }
            catch (Exception ex)
            {
                NeonLogger.LogError(ex.ToString());
            }
        }

        // ===== Context Window Indicator (U-36) =====

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
                    contextWindow = ChatContextWindowEstimator.GuessContextWindow(provider, chat.CurrentSessionModel);
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
                used = ChatContextWindowEstimator.EstimateSessionTokens(_d.GetAppAsync, _d.GetChatServiceAsync);
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

        // ===== Message Rendering (delegated to ChatMessageListRenderer) =====

        private static string GetSendFailureMessage(Exception ex)
        {
            string message = ex != null ? ex.Message : null;
            if (!string.IsNullOrWhiteSpace(message) &&
                message.IndexOf("session busy", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return LocalizationExtensions.Get(
                    "chat.hermes.session_busy",
                    "Hermes session is still busy. Wait for the current response to finish or stop it before sending another message.");
            }

            return string.IsNullOrWhiteSpace(message)
                ? LocalizationExtensions.Get("chat.send.failed", "Could not send message.")
                : message;
        }

        public void RenderMessages(IReadOnlyList<ChatMessage> messages)
        {
            bool hasSession = messages != null;
            ChatAttachmentManager.SetDisplay(_d.Composer, hasSession ? DisplayStyle.Flex : DisplayStyle.None);
            _editController?.Hide();
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

        private IReadOnlyList<ChatMessage> BuildPendingMessages(IReadOnlyList<ChatMessage> existing, string userMessage, IReadOnlyList<ChatAttachment> attachments)
        {
            var list = new List<ChatMessage>();
            if (existing != null)
            {
                for (int i = 0; i < existing.Count; i++)
                    list.Add(existing[i]);
            }
            string pendingAudio    = _pendingVoiceAudioPath;
            float  pendingDuration = _pendingVoiceDurationSecs;
            _pendingVoiceAudioPath    = "";
            _pendingVoiceDurationSecs = 0f;

            list.Add(new ChatMessage
            {
                role = "user",
                content = userMessage,
                attachments = attachments != null && attachments.Count > 0
                    ? new List<ChatAttachment>(attachments)
                    : null,
                unixTimeSeconds  = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                audioPath        = string.IsNullOrEmpty(pendingAudio) ? null : pendingAudio,
                audioDurationSecs = pendingDuration
            });
            return list;
        }

        // ===== Utility =====

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

                        var parameters = ToolExecutorHelper.ParseToolArguments(tc.function.arguments);

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

        // ===== Message selection (U-31/U-32) — event handlers for ChatSelectionManager =====

        private void OnSelectionBulkDelete(IReadOnlyList<string> ids)
        {
            _selectionManager?.OnSelectionBulkDelete(ids);
        }

        private void OnSelectionBulkForward(IReadOnlyList<string> ids)
        {
            _selectionManager?.OnSelectionBulkForward(ids);
        }

        private async Task<ChatSession> ShowSessionPickerAsync(List<ChatSession> candidates)
        {
            var tcs = new TaskCompletionSource<ChatSession>();

            var overlay = new VisualElement();
            overlay.AddToClassList("session-picker-overlay");
            // Inline fallback: the overlay attaches to the document root, which is outside this view's
            // stylesheet scope — without these the picker renders unstyled and full-width.
            overlay.style.position = Position.Absolute;
            overlay.style.left = 0;
            overlay.style.right = 0;
            overlay.style.top = 0;
            overlay.style.bottom = 0;
            overlay.style.backgroundColor = new Color(0f, 0f, 0f, 0.55f);
            overlay.style.flexDirection = FlexDirection.Column;
            overlay.style.justifyContent = Justify.Center;
            overlay.style.alignItems = Align.Center;

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
            // Inline fallback (stylesheet may be out of scope at the document root).
            var pickerBorder = new Color(0.227f, 0.251f, 0.322f, 1f); // --line-3
            picker.style.flexDirection = FlexDirection.Column;
            picker.style.backgroundColor = new Color(0.137f, 0.153f, 0.196f, 1f); // --bg-4
            picker.style.borderTopWidth = 1f;
            picker.style.borderBottomWidth = 1f;
            picker.style.borderLeftWidth = 1f;
            picker.style.borderRightWidth = 1f;
            picker.style.borderTopColor = pickerBorder;
            picker.style.borderBottomColor = pickerBorder;
            picker.style.borderLeftColor = pickerBorder;
            picker.style.borderRightColor = pickerBorder;
            picker.style.borderTopLeftRadius = 10f;
            picker.style.borderTopRightRadius = 10f;
            picker.style.borderBottomLeftRadius = 10f;
            picker.style.borderBottomRightRadius = 10f;
            picker.style.minWidth = 280f;
            picker.style.maxWidth = 360f;
            picker.style.maxHeight = 420f;
            picker.style.paddingTop = 8f;
            picker.style.paddingBottom = 8f;
            picker.style.paddingLeft = 8f;
            picker.style.paddingRight = 8f;

            var headerLabel = new Label(LocalizationExtensions.Get("chat.selection.pick_session", "Pick a chat to forward to"));
            headerLabel.AddToClassList("session-picker__header");
            headerLabel.style.color = new Color(0.604f, 0.635f, 0.702f, 1f); // --text-2
            headerLabel.style.fontSize = 13f;
            headerLabel.style.paddingLeft = 6f;
            headerLabel.style.paddingBottom = 8f;
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
                item.style.flexDirection = FlexDirection.Row;
                item.style.alignItems = Align.Center;
                item.style.paddingTop = 8f;
                item.style.paddingBottom = 8f;
                item.style.paddingLeft = 10f;
                item.style.paddingRight = 10f;
                item.style.borderTopLeftRadius = 6f;
                item.style.borderTopRightRadius = 6f;
                item.style.borderBottomLeftRadius = 6f;
                item.style.borderBottomRightRadius = 6f;

                string titleText = string.IsNullOrWhiteSpace(s.title) ? LocalizationExtensions.Get("chat.new", "New chat") : s.title;
                var titleLabel = new Label(titleText);
                titleLabel.AddToClassList("session-picker__title");
                titleLabel.style.flexGrow = 1f;
                titleLabel.style.color = new Color(0.847f, 0.863f, 0.902f, 1f); // --text-1
                titleLabel.style.fontSize = 13f;

                var timeLabel = new Label(FormatSessionTimestamp(s.updatedAtUnix));
                timeLabel.AddToClassList("session-picker__time");
                timeLabel.style.color = new Color(0.42f, 0.45f, 0.53f, 1f); // --text-3
                timeLabel.style.fontSize = 11f;
                timeLabel.style.marginLeft = 8f;

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
            cancelBtn.style.backgroundColor = new Color(0.137f, 0.153f, 0.196f, 1f); // --bg-4
            cancelBtn.style.color = new Color(0.847f, 0.863f, 0.902f, 1f); // --text-1
            cancelBtn.style.borderTopWidth = 1f;
            cancelBtn.style.borderBottomWidth = 1f;
            cancelBtn.style.borderLeftWidth = 1f;
            cancelBtn.style.borderRightWidth = 1f;
            var cancelBorder = new Color(0.176f, 0.196f, 0.251f, 1f); // --line-2
            cancelBtn.style.borderTopColor = cancelBorder;
            cancelBtn.style.borderBottomColor = cancelBorder;
            cancelBtn.style.borderLeftColor = cancelBorder;
            cancelBtn.style.borderRightColor = cancelBorder;
            cancelBtn.style.borderTopLeftRadius = 8f;
            cancelBtn.style.borderTopRightRadius = 8f;
            cancelBtn.style.borderBottomLeftRadius = 8f;
            cancelBtn.style.borderBottomRightRadius = 8f;
            cancelBtn.style.paddingLeft = 16f;
            cancelBtn.style.paddingRight = 16f;
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
            item.RegisterCallback<PointerEnterEvent>(_ =>
            {
                item.AddToClassList("session-picker__item--hover");
                item.style.backgroundColor = new Color(0.486f, 0.478f, 0.929f, 0.16f); // --accent-soft
            });
            item.RegisterCallback<PointerLeaveEvent>(_ =>
            {
                item.RemoveFromClassList("session-picker__item--hover");
                item.style.backgroundColor = Color.clear;
            });
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
        internal static event Action<string> CopyMessageRequested;
        internal static event Action RegenerateRequested;
        internal static event Action<ChatMessage> ListenMessageRequested;

        internal static void OnCopyClickedStatic() => CopyRequested?.Invoke();
        internal static void OnCopyMessageClickedStatic(string text) => CopyMessageRequested?.Invoke(text);
        internal static void OnRegenerateClickedStatic() => RegenerateRequested?.Invoke();
        internal static void OnListenMessageClickedStatic(ChatMessage message) => ListenMessageRequested?.Invoke(message);
    }
}
