using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Api;
using NeonCompanion.Runtime.Chat;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Localization;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK.Chat
{
    internal sealed class ToolCallApprovalController
    {
        private readonly ScrollView _messagesList;
        private readonly Action _scrollToBottom;
        private readonly Func<Task<CompanionApp>> _getAppAsync;
        private readonly Action<string> _showSystemMessage;
        private readonly Func<bool> _getIsSending;
        private readonly Func<bool> _getIsStreamingResponse;
        private readonly Func<ChatService> _getCurrentChatService;
        private readonly Action _playAttentionSound;

        private readonly NeonCompanion.Runtime.UI.UITK.ToolCallUiHelper _toolCallUiHelper = new NeonCompanion.Runtime.UI.UITK.ToolCallUiHelper();
        private static readonly string[] DefaultHermesApprovalChoices = { "once", "session", "always", "deny" };
        private static readonly string[] SmartDeniedApprovalChoices = { "once", "deny" };
        private NeonCompanion.Runtime.UI.UITK.ApprovalPrompt _currentApprovalPrompt;
        private VisualElement _currentApprovalElement;
        private TaskCompletionSource<bool> _pendingApprovalTcs;
        private IChatTransport _hermesTransport;

        // A background session's approval/clarify request, deferred until the user opens it.
        private sealed class PendingRequest
        {
            public string sessionId;
            public bool isClarify;
            public ClarifyRequest clarify;
            public ApprovalRequest approval;
        }
        private readonly Dictionary<string, PendingRequest> _pendingBySession = new Dictionary<string, PendingRequest>();

        // Deferred sudo/secret captures live in their own map: a sudo.request must not evict an
        // approval that is still blocking the same background session (Desktop keeps one prompt
        // store per kind), and vice versa.
        private readonly Dictionary<string, SecretRequest> _pendingSecretBySession = new Dictionary<string, SecretRequest>();

        // The masked capture currently on screen (sudo password or skill secret), its row, and the
        // session that raised it. Kept so an expire event can match it by request id, a foreground
        // switch can park it, and every answer path can fire exactly once.
        private SecretRequest _activeSecretRequest;
        private string _activeSecretSessionId;
        private VisualElement _activeSecretElement;
        private TextField _activeSecretField;

        internal event Action OnStopRequested;
        internal event Action<string> OnApproved;
        internal event Action<string> OnRejected;

        internal ToolCallApprovalController(
            ScrollView messagesList,
            Action scrollToBottom,
            Func<Task<CompanionApp>> getAppAsync,
            Action<string> showSystemMessage,
            Func<bool> getIsSending,
            Func<bool> getIsStreamingResponse,
            Func<ChatService> getCurrentChatService,
            Action playAttentionSound)
        {
            _messagesList = messagesList;
            _scrollToBottom = scrollToBottom;
            _getAppAsync = getAppAsync;
            _showSystemMessage = showSystemMessage;
            _getIsSending = getIsSending;
            _getIsStreamingResponse = getIsStreamingResponse;
            _getCurrentChatService = getCurrentChatService;
            _playAttentionSound = playAttentionSound;
        }

        private string ForegroundSessionId()
        {
            var chat = _getCurrentChatService != null ? _getCurrentChatService() : null;
            return chat != null ? chat.CurrentSessionId : null;
        }

        private bool IsForeground(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                return true; // unknown origin — treat as foreground
            string fg = ForegroundSessionId();
            return string.IsNullOrEmpty(fg) || string.Equals(fg, sessionId, StringComparison.Ordinal);
        }

        // Defer a background session's request: badge it in the sidebar + play a sound.
        private void StorePending(string sessionId, ClarifyRequest clarify, ApprovalRequest approval)
        {
            if (string.IsNullOrEmpty(sessionId))
                return;
            _pendingBySession[sessionId] = new PendingRequest
            {
                sessionId = sessionId,
                isClarify = clarify != null,
                clarify = clarify,
                approval = approval
            };
            var chat = _getCurrentChatService != null ? _getCurrentChatService() : null;
            chat?.MarkSessionAttention(sessionId);
            try { _playAttentionSound?.Invoke(); } catch { }
        }

        // Defer a background session's sudo/secret capture (distinct: needs a masked text value, not
        // approve/deny). `notify` is false when merely parking the foreground capture on a session
        // switch — that badges the sidebar without re-playing the attention sound.
        private void StorePendingSecret(string sessionId, SecretRequest secret, bool notify = true)
        {
            if (string.IsNullOrEmpty(sessionId) || secret == null)
                return;
            _pendingSecretBySession[sessionId] = secret;
            var chat = _getCurrentChatService != null ? _getCurrentChatService() : null;
            chat?.MarkSessionAttention(sessionId);
            if (notify)
            {
                try { _playAttentionSound?.Invoke(); } catch { }
            }
        }

        /// <summary>
        /// Show any deferred approval/clarify/sudo/secret for the session the user just opened.
        /// Called when the foreground session changes.
        /// </summary>
        public void ShowPendingForSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                return;

            // The transcript is re-rendered on a foreground switch, which wipes the capture row of
            // the session being left. Park that request back in the pending map so it reappears when
            // the user returns — the agent stays blocked on it either way, and the password must
            // never be carried over to (or answered from) another chat.
            if (_activeSecretRequest != null &&
                !string.IsNullOrEmpty(_activeSecretSessionId) &&
                !string.Equals(_activeSecretSessionId, sessionId, StringComparison.Ordinal))
            {
                StorePendingSecret(_activeSecretSessionId, _activeSecretRequest, false);
                RemoveSecretInput();
            }

            SecretRequest secret;
            bool hasSecret = _pendingSecretBySession.TryGetValue(sessionId, out secret);
            if (hasSecret)
                _pendingSecretBySession.Remove(sessionId);

            PendingRequest p;
            bool hasPrompt = _pendingBySession.TryGetValue(sessionId, out p);
            if (hasPrompt)
                _pendingBySession.Remove(sessionId);

            if (!hasSecret && !hasPrompt)
                return;

            var chat = _getCurrentChatService != null ? _getCurrentChatService() : null;
            chat?.ClearSessionAttention(sessionId);

            if (hasSecret)
                ShowSecretNow(sessionId, secret);

            if (!hasPrompt)
                return;
            if (p.isClarify)
                ShowClarifyNow(p.clarify);
            else
                _ = HandleHermesApprovalRequestAsync(p.sessionId, p.approval);
        }

        internal void SetBubble(VisualElement bubble)
        {
            _toolCallUiHelper.SetBubble(bubble);
        }

        internal bool OnToolProgress(string tool, string label, string emoji, string status, VisualElement insertAfterElement = null)
        {
            return _toolCallUiHelper.OnToolProgress(tool, label, emoji, status, insertAfterElement);
        }

        internal bool OnToolProgress(ToolProgressInfo info, VisualElement insertAfterElement = null)
        {
            if (info == null)
                return false;
            return _toolCallUiHelper.OnToolProgress(
                info.tool,
                info.toolId,
                info.label,
                info.emoji,
                info.status,
                info.inlineDiff,
                info.details,
                insertAfterElement);
        }

        internal void ClearToolProgress()
        {
            _toolCallUiHelper.Clear();
        }

        internal bool ShouldPromptForStreamingApproval(string status)
        {
            if (!IsApprovalRequestStatus(status))
                return false;
            if (string.Equals(status, "requesting", StringComparison.OrdinalIgnoreCase))
                return false;
            if (IsCurrentProviderHermes())
                return false;
            return true;
        }

        internal void Dismiss()
        {
            if (_currentApprovalElement != null)
            {
                if (_currentApprovalElement.parent != null)
                    _currentApprovalElement.RemoveFromHierarchy();
                _currentApprovalElement = null;
            }
            _currentApprovalPrompt = null;

            if (_pendingApprovalTcs != null)
            {
                _pendingApprovalTcs.TrySetResult(false);
                _pendingApprovalTcs = null;
            }
        }

        internal void ClearPromptUiForSession(string sessionId)
        {
            Dismiss();

            // No respond is sent: this runs on interrupt/stop, and the gateway releases its own
            // pending sudo/secret prompts for that session (_clear_pending) when the turn is cut.
            RemoveSecretInput();

            if (!string.IsNullOrEmpty(sessionId))
            {
                _pendingBySession.Remove(sessionId);
                _pendingSecretBySession.Remove(sessionId);
            }

            RemovePromptElements("clarify-choices");
            RemovePromptElements("clarify-input");
            RemovePromptElements("secret-input");
        }

        internal async Task<bool> RequestToolApprovalAsync(ToolCallRequest request)
        {
            if (request == null)
                return true;

            // Dangerous tools (code execution, shell, file writes) can never be bypassed: neither
            // "auto" permission mode nor a saved "always" entry applies. They always prompt.
            bool dangerous = NeonCompanion.Runtime.Api.Tools.ToolExecutor.IsDangerousTool(request.toolName);

            var settings = await GetSettingsAsync();
            if (!dangerous && settings != null && string.Equals(settings.toolPermissionMode, "auto", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!dangerous && await IsToolAlwaysApprovedAsync(request.toolName))
                return true;

            var prompt = new NeonCompanion.Runtime.UI.UITK.ApprovalPrompt();
            var approvalElement = prompt.Create(request);
            _currentApprovalPrompt = prompt;
            _currentApprovalElement = approvalElement;
            _messagesList.Add(approvalElement);
            _scrollToBottom?.Invoke();

            bool approved = false;
            bool always = false;
            var completionSource = new TaskCompletionSource<bool>();
            _pendingApprovalTcs = completionSource;

            prompt.OnDecision += (a, alwaysApprove) =>
            {
                approved = a;
                always = alwaysApprove;
                _pendingApprovalTcs = null;
                completionSource.TrySetResult(true);
            };

            await completionSource.Task;

            if (approvalElement != null && approvalElement.parent != null)
                approvalElement.RemoveFromHierarchy();

            _currentApprovalPrompt = null;
            _currentApprovalElement = null;
            _pendingApprovalTcs = null;

            if (always && approved && !dangerous)
                await SaveAlwaysApprovedToolAsync(request.toolName);

            string toolId = request.id ?? string.Empty;
            if (approved)
                OnApproved?.Invoke(toolId);
            else
                OnRejected?.Invoke(toolId);

            return approved;
        }

        internal async Task HandleStreamingApprovalAsync(ToolCallRequest request)
        {
            if (request == null)
                return;
            if (_currentApprovalPrompt != null)
                return;

            bool approved = await RequestToolApprovalAsync(request);
            if (!approved)
            {
                try
                {
                    if (_getIsSending() || _getIsStreamingResponse())
                        OnStopRequested?.Invoke();
                }
                catch (Exception ex)
                {
                    NeonLogger.LogError("Error stopping on tool reject: " + ex);
                }
            }
        }

        internal void Subscribe()
        {
            var selector = GlobalBackendSelector.Instance;
            if (selector == null)
                return;
            selector.OnModeChanged += OnBackendModeChanged;
            OnBackendModeChanged(selector.CurrentMode);
        }

        internal void Unsubscribe()
        {
            var selector = GlobalBackendSelector.Instance;
            if (selector != null)
                selector.OnModeChanged -= OnBackendModeChanged;

            if (_hermesTransport != null)
            {
                _hermesTransport.OnClarifyRequest -= OnHermesClarifyRequest;
                _hermesTransport.OnApprovalRequest -= OnHermesApprovalRequest;
                _hermesTransport.OnSecretRequest -= OnHermesSecretRequest;
                _hermesTransport.OnSecretExpire -= OnHermesSecretExpire;
                _hermesTransport = null;
            }
        }

        private void OnBackendModeChanged(BackendMode mode)
        {
            if (_hermesTransport != null)
            {
                _hermesTransport.OnClarifyRequest -= OnHermesClarifyRequest;
                _hermesTransport.OnApprovalRequest -= OnHermesApprovalRequest;
                _hermesTransport.OnSecretRequest -= OnHermesSecretRequest;
                _hermesTransport.OnSecretExpire -= OnHermesSecretExpire;
                _pendingBySession.Clear();
                _pendingSecretBySession.Clear();
                RemoveSecretInput();
            }

            if (mode == BackendMode.Hermes)
            {
                var selector = GlobalBackendSelector.Instance;
                if (selector != null && selector.SessionManager != null)
                {
                    _hermesTransport = selector.SessionManager;
                    _hermesTransport.OnClarifyRequest += OnHermesClarifyRequest;
                    _hermesTransport.OnApprovalRequest += OnHermesApprovalRequest;
                    _hermesTransport.OnSecretRequest += OnHermesSecretRequest;
                    _hermesTransport.OnSecretExpire += OnHermesSecretExpire;
                }
            }
            else
            {
                _hermesTransport = null;
            }
        }

        private void OnHermesClarifyRequest(string sessionId, ClarifyRequest request)
        {
            if (request == null)
                return;

            if (IsForeground(sessionId))
                ShowClarifyNow(request);
            else
                StorePending(sessionId, request, null);
        }

        private void ShowClarifyNow(ClarifyRequest request)
        {
            if (request == null)
                return;
            _showSystemMessage?.Invoke("[Hermes] " + (request.question ?? "Clarify?"));
            if (request.choices != null && request.choices.Length > 0)
                ShowClarifyChoices(request);
            else
                ShowClarifyInput(request);
        }

        private void ShowClarifyChoices(ClarifyRequest request)
        {
            if (_messagesList == null || request.choices == null)
                return;

            var container = new VisualElement();
            container.AddToClassList("clarify-choices");
            container.style.marginTop = 4;
            container.style.marginLeft = 40;
            container.style.flexDirection = FlexDirection.Row;
            container.style.flexWrap = Wrap.Wrap;

            for (int i = 0; i < request.choices.Length; i++)
            {
                string choice = request.choices[i];
                var btn = new Button(() => OnClarifyChoiceSelected(request, choice))
                {
                    text = choice
                };
                btn.AddToClassList("clarify-choices__btn");
                container.Add(btn);
            }

            _messagesList.Add(container);
            _scrollToBottom?.Invoke();
        }

        private void ShowClarifyInput(ClarifyRequest request)
        {
            if (_messagesList == null)
                return;

            var container = new VisualElement();
            container.AddToClassList("clarify-input");
            container.style.marginTop = 4;
            container.style.marginLeft = 40;
            container.style.flexDirection = FlexDirection.Row;
            container.style.flexWrap = Wrap.Wrap;

            var field = new TextField();
            field.AddToClassList("clarify-input__field");
            field.style.minWidth = 240;

            var submit = new Button(() => OnClarifyInputSubmit(request, container, field));
            submit.text = LocalizationExtensions.Get("clarify.continue", "Continue");
            submit.AddToClassList("clarify-input__btn");

            container.Add(field);
            container.Add(submit);
            _messagesList.Add(container);
            _scrollToBottom?.Invoke();
        }

        private void OnClarifyChoiceSelected(ClarifyRequest request, string choice)
        {
            DisableClarifyButtons();
            _showSystemMessage?.Invoke("[You] " + choice);
            _ = SendClarifyResponse(request, choice);
        }

        private void OnClarifyInputSubmit(ClarifyRequest request, VisualElement container, TextField field)
        {
            string answer = field != null ? field.value : string.Empty;
            if (container != null)
                container.SetEnabled(false);
            _showSystemMessage?.Invoke("[You] " + (answer ?? string.Empty));
            _ = SendClarifyResponse(request, answer ?? string.Empty);
        }

        private async Task SendClarifyResponse(ClarifyRequest request, string answer)
        {
            var selector = GlobalBackendSelector.Instance;
            if (selector?.SessionManager == null)
                return;
            await selector.SessionManager.RespondToClarify(request.requestId, answer);
        }

        private void DisableClarifyButtons()
        {
            if (_messagesList == null)
                return;

            var buttons = _messagesList.Query<Button>().ToList();
            for (int i = 0; i < buttons.Count; i++)
            {
                if (buttons[i].parent != null &&
                    buttons[i].parent.ClassListContains("clarify-choices"))
                {
                    buttons[i].SetEnabled(false);
                }
            }
        }

        private void OnHermesApprovalRequest(string sessionId, ApprovalRequest request)
        {
            if (request == null)
                return;
            if (IsForeground(sessionId))
                _ = HandleHermesApprovalRequestAsync(sessionId, request);
            else
                StorePending(sessionId, null, request);
        }

        private async Task HandleHermesApprovalRequestAsync(string sessionId, ApprovalRequest request)
        {
            if (request == null || _currentApprovalPrompt != null)
                return;

            var toolReq = new ToolCallRequest
            {
                id = request.requestId,
                toolName = request.type ?? "approval",
                description = request.description ?? "Approval needed",
                parameters = new Dictionary<string, string>()
            };

            // Use the dedicated Hermes approval flow that tracks the full choice.
            var approvalDecision = await RequestHermesApprovalAsync(toolReq, request);
            bool approved = approvalDecision.approved;
            string choice = approvalDecision.choice;

            try
            {
                var selector = GlobalBackendSelector.Instance;
                if (selector?.SessionManager != null)
                    await selector.SessionManager.RespondToApproval(sessionId, choice);
            }
            catch (Exception ex)
            {
                NeonLogger.LogError("[Hermes] Failed to send approval response: " + ex.Message);
            }

            if (!approved)
            {
                try
                {
                    if (_getIsSending() || _getIsStreamingResponse())
                        OnStopRequested?.Invoke();
                }
                catch (Exception ex)
                {
                    NeonLogger.LogError("Error stopping on Hermes approval reject: " + ex);
                }
            }
        }

        /// <summary>
        /// Hermes-specific approval flow that returns the full gateway choice string.
        /// Desktop choices: "once" (run this time), "session" (allow for session),
        /// "always" (permanent), "deny" (reject).
        /// When the backend supplies its own choices (e.g. smart_denied, allow_permanent=false),
        /// each server-sent choice becomes a button. Otherwise the Desktop default choices are used.
        /// </summary>
        private async Task<(bool approved, string choice)> RequestHermesApprovalAsync(
            ToolCallRequest request, ApprovalRequest hermesRequest)
        {
            if (request == null)
                return (true, "once");

            var prompt = new NeonCompanion.Runtime.UI.UITK.ApprovalPrompt();
            string[] choices = EffectiveHermesApprovalChoices(hermesRequest);

            VisualElement approvalElement = prompt.Create(request, choices,
                hermesRequest == null || hermesRequest.allowPermanent,
                hermesRequest != null && hermesRequest.smartDenied);

            _currentApprovalPrompt = prompt;
            _currentApprovalElement = approvalElement;
            _messagesList.Add(approvalElement);
            _scrollToBottom?.Invoke();

            var completionSource = new TaskCompletionSource<bool>();
            _pendingApprovalTcs = completionSource;

            string selectedChoice = null;

            prompt.OnChoiceSelected += (choice) =>
            {
                selectedChoice = choice;
                _pendingApprovalTcs = null;
                completionSource.TrySetResult(true);
            };

            await completionSource.Task;

            if (approvalElement != null && approvalElement.parent != null)
                approvalElement.RemoveFromHierarchy();

            _currentApprovalPrompt = null;
            _currentApprovalElement = null;
            _pendingApprovalTcs = null;

            string choice = selectedChoice ?? "deny";
            bool approved = !string.Equals(choice, "deny", StringComparison.OrdinalIgnoreCase);
            return (approved, choice);
        }

        private static string[] EffectiveHermesApprovalChoices(ApprovalRequest request)
        {
            if (request != null && request.choices != null && request.choices.Length > 0)
                return request.choices;
            if (request != null && request.smartDenied)
                return SmartDeniedApprovalChoices;
            return DefaultHermesApprovalChoices;
        }

        private void RemovePromptElements(string className)
        {
            if (_messagesList == null || string.IsNullOrEmpty(className))
                return;

            var elements = _messagesList.Query<VisualElement>().ToList();
            for (int i = 0; i < elements.Count; i++)
            {
                if (elements[i] != null &&
                    elements[i].ClassListContains(className) &&
                    elements[i].parent != null)
                {
                    elements[i].RemoveFromHierarchy();
                }
            }
        }

        // secret.request is NOT an approve/deny choice: the agent is blocked on
        // secret.respond {request_id, value} (a captured text value, e.g. an API key). It must
        // never go through RespondToApproval — it gets its own masked text-input prompt.
        private void OnHermesSecretRequest(string sessionId, SecretRequest request)
        {
            if (request == null || string.IsNullOrEmpty(request.requestId))
                return;

            if (IsForeground(sessionId))
                ShowSecretNow(sessionId, request);
            else
                StorePendingSecret(sessionId, request);
        }

        // sudo.expire / secret.expire: the gateway stopped waiting (120s for sudo) and dropped the
        // pending request. Tear the capture down WITHOUT responding — a late sudo.respond only
        // resolves to status="expired" — so the user never types a password into a dead prompt.
        private void OnHermesSecretExpire(string sessionId, string requestId)
        {
            if (string.IsNullOrEmpty(requestId))
                return;

            var chat = _getCurrentChatService != null ? _getCurrentChatService() : null;
            var expired = new List<string>();
            foreach (var entry in _pendingSecretBySession)
            {
                if (entry.Value != null &&
                    string.Equals(entry.Value.requestId, requestId, StringComparison.Ordinal))
                    expired.Add(entry.Key);
            }
            for (int i = 0; i < expired.Count; i++)
            {
                _pendingSecretBySession.Remove(expired[i]);
                chat?.ClearSessionAttention(expired[i]);
            }

            if (_activeSecretRequest == null ||
                !string.Equals(_activeSecretRequest.requestId, requestId, StringComparison.Ordinal))
                return;

            bool isSudo = _activeSecretRequest.isSudo;
            RemoveSecretInput();
            _showSystemMessage?.Invoke("[Hermes] " + (isSudo
                ? LocalizationExtensions.Get("sudo.expired", "Password request expired.")
                : LocalizationExtensions.Get("secret.expired", "Secret request expired.")));
        }

        private void ShowSecretNow(string sessionId, SecretRequest request)
        {
            if (request == null || _messagesList == null)
                return;

            // One capture at a time: a newer request supersedes the row still on screen (Desktop
            // keeps a single sudo/secret slot per session). The superseded one is left unanswered —
            // the gateway expires it on its own and emits sudo.expire/secret.expire.
            RemoveSecretInput();

            string label = !string.IsNullOrWhiteSpace(request.prompt)
                ? request.prompt
                : (request.isSudo
                    ? LocalizationExtensions.Get("sudo.enter_password", "Administrator password")
                    : (!string.IsNullOrWhiteSpace(request.envVar)
                    ? LocalizationExtensions.Get("secret.enter_value_for", "Enter a value for ") + request.envVar
                    : LocalizationExtensions.Get("secret.enter_value", "Enter secret value")));
            _showSystemMessage?.Invoke("[Hermes] " + label);
            ShowSecretInput(sessionId, request);
        }

        private void ShowSecretInput(string sessionId, SecretRequest request)
        {
            var container = new VisualElement();
            container.AddToClassList("secret-input");
            container.style.marginTop = 4;
            container.style.marginLeft = 40;
            container.style.flexDirection = FlexDirection.Row;
            container.style.flexWrap = Wrap.Wrap;

            var field = new TextField();
            field.isPasswordField = true; // mask the captured password/secret in the UI
            field.AddToClassList("secret-input__field");
            field.style.minWidth = 200;

            // Enter sends, Escape refuses — Desktop submits the dialog's form on Enter and maps
            // every close path (Esc included) to the empty-value refusal; the TUI binds Esc the same.
            field.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == UnityEngine.KeyCode.Return || evt.keyCode == UnityEngine.KeyCode.KeypadEnter)
                {
                    evt.StopPropagation();
                    AnswerSecret(request, field.value, false);
                }
                else if (evt.keyCode == UnityEngine.KeyCode.Escape)
                {
                    evt.StopPropagation();
                    AnswerSecret(request, string.Empty, true);
                }
            }, TrickleDown.TrickleDown);

            var submit = new Button(() => AnswerSecret(request, field.value, false));
            submit.text = request.isSudo
                ? LocalizationExtensions.Get("sudo.submit", "Send")
                : LocalizationExtensions.Get("secret.submit", "Submit");
            submit.AddToClassList("secret-input__btn");

            // Cancel answers with an EMPTY value, which the backend treats as a failed sudo (no
            // command runs) / skipped secret. Without it the only way out is to leave the agent
            // blocked until its timeout (Desktop maps every dialog close to the same refusal).
            var cancel = new Button(() => AnswerSecret(request, string.Empty, true));
            cancel.text = LocalizationExtensions.Get("secret.cancel", "Cancel");
            cancel.AddToClassList("secret-input__btn");

            container.Add(field);
            container.Add(submit);
            container.Add(cancel);
            _messagesList.Add(container);

            _activeSecretRequest = request;
            _activeSecretSessionId = sessionId;
            _activeSecretElement = container;
            _activeSecretField = field;

            field.Focus();
            _scrollToBottom?.Invoke();
        }

        /// <summary>
        /// Answer the on-screen capture exactly once (a request id mismatch means it was already
        /// answered, superseded or expired — sending twice would answer a stranger's prompt).
        /// `cancelled` sends an empty value: a refusal, never mistaken for consent.
        /// </summary>
        private void AnswerSecret(SecretRequest request, string value, bool cancelled)
        {
            if (request == null || _activeSecretRequest == null ||
                !string.Equals(_activeSecretRequest.requestId, request.requestId, StringComparison.Ordinal))
                return;

            bool isSudo = request.isSudo;
            // Drop the row (and with it the captured value) before the await: the password is never
            // held past the send, and no second click can reach this request.
            RemoveSecretInput();

            // Never echo the password/secret back into the transcript — it is persisted.
            string line;
            if (cancelled)
                line = isSudo
                    ? LocalizationExtensions.Get("sudo.cancelled", "(password request cancelled)")
                    : LocalizationExtensions.Get("secret.cancelled", "(secret request cancelled)");
            else
                line = isSudo
                    ? LocalizationExtensions.Get("sudo.submitted", "(password submitted)")
                    : LocalizationExtensions.Get("secret.submitted", "(secret submitted)");
            _showSystemMessage?.Invoke("[You] " + line);

            _ = SendSecretResponse(request, value ?? string.Empty);
        }

        // Tear down the capture row and wipe the typed value out of the field. Never responds.
        private void RemoveSecretInput()
        {
            if (_activeSecretField != null)
                _activeSecretField.value = string.Empty;
            if (_activeSecretElement != null && _activeSecretElement.parent != null)
                _activeSecretElement.RemoveFromHierarchy();

            _activeSecretField = null;
            _activeSecretElement = null;
            _activeSecretRequest = null;
            _activeSecretSessionId = null;
        }

        private async Task SendSecretResponse(SecretRequest request, string value)
        {
            var selector = GlobalBackendSelector.Instance;
            if (selector?.SessionManager == null)
                return;
            try
            {
                // Branch on isSudo: sudo.request expects sudo.respond {request_id, password},
                // while secret.request expects secret.respond {request_id, value}. Mixing them
                // leaves the agent blocked forever on the wrong respond call.
                if (request.isSudo)
                    await selector.SessionManager.RespondToSudo(request.requestId, value);
                else
                    await selector.SessionManager.RespondToSecret(request.requestId, value);
            }
            catch (Exception ex)
            {
                NeonLogger.LogError("[Hermes] Failed to send secret response: " + ex.Message);
            }
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
            if (status.IndexOf("approve", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (status.IndexOf("confirm", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (status.IndexOf("permission", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (status.IndexOf("waiting", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (status.IndexOf("request", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return false;
        }

        private bool IsCurrentProviderHermes()
        {
            var selector = GlobalBackendSelector.Instance;
            if (selector != null && selector.CurrentMode == BackendMode.Hermes)
                return true;

            var chatService = _getCurrentChatService != null ? _getCurrentChatService() : null;
            var provider = chatService != null ? chatService.CurrentProvider : null;
            if (provider == null)
                return false;

            if (!string.IsNullOrWhiteSpace(provider.backendType) &&
                string.Equals(provider.backendType, "hermes", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!string.IsNullOrWhiteSpace(provider.defaultModel) &&
                provider.defaultModel.IndexOf("hermes", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            return false;
        }

        private async Task<AppSettings> GetSettingsAsync()
        {
            try
            {
                var app = await _getAppAsync();
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
                var app = await _getAppAsync();
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
    }
}
