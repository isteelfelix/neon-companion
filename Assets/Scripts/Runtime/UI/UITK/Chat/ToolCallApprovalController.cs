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
        private NeonCompanion.Runtime.UI.UITK.ApprovalPrompt _currentApprovalPrompt;
        private VisualElement _currentApprovalElement;
        private TaskCompletionSource<bool> _pendingApprovalTcs;
        private IChatTransport _hermesTransport;

        // A background session's approval/clarify/secret request, deferred until the user opens it.
        private sealed class PendingRequest
        {
            public string sessionId;
            public bool isClarify;
            public ClarifyRequest clarify;
            public ApprovalRequest approval;
            public SecretRequest secret;
        }
        private readonly Dictionary<string, PendingRequest> _pendingBySession = new Dictionary<string, PendingRequest>();

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

        // Defer a background session's secret request (distinct: needs a text value, not approve/deny).
        private void StorePendingSecret(string sessionId, SecretRequest secret)
        {
            if (string.IsNullOrEmpty(sessionId) || secret == null)
                return;
            _pendingBySession[sessionId] = new PendingRequest
            {
                sessionId = sessionId,
                secret = secret
            };
            var chat = _getCurrentChatService != null ? _getCurrentChatService() : null;
            chat?.MarkSessionAttention(sessionId);
            try { _playAttentionSound?.Invoke(); } catch { }
        }

        /// <summary>
        /// Show any deferred approval/clarify for the session the user just opened. Called when the
        /// foreground session changes.
        /// </summary>
        public void ShowPendingForSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                return;
            PendingRequest p;
            if (!_pendingBySession.TryGetValue(sessionId, out p))
                return;
            _pendingBySession.Remove(sessionId);
            var chat = _getCurrentChatService != null ? _getCurrentChatService() : null;
            chat?.ClearSessionAttention(sessionId);

            if (p.secret != null)
                ShowSecretNow(p.secret);
            else if (p.isClarify)
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
                _pendingBySession.Clear();
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

            bool hasChoices = request.choices != null && request.choices.Length > 0;
            // A clarify with no choices is auto-answered — no user input needed, so handle it
            // regardless of which session it belongs to.
            if (!hasChoices)
            {
                _ = AutoRespondToClarify(request, "ok");
                return;
            }

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
                _ = AutoRespondToClarify(request, "ok");
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

        private void OnClarifyChoiceSelected(ClarifyRequest request, string choice)
        {
            DisableClarifyButtons();
            _showSystemMessage?.Invoke("[You] " + choice);
            _ = SendClarifyResponse(request, choice);
        }

        private async Task SendClarifyResponse(ClarifyRequest request, string answer)
        {
            var selector = GlobalBackendSelector.Instance;
            if (selector?.SessionManager == null)
                return;
            await selector.SessionManager.RespondToClarify(request.requestId, answer);
        }

        private async Task AutoRespondToClarify(ClarifyRequest request, string defaultAnswer)
        {
            await Task.Delay(100);
            var selector = GlobalBackendSelector.Instance;
            if (selector?.SessionManager == null)
                return;
            string answer = defaultAnswer ?? "ok";
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

            bool approved = await RequestToolApprovalAsync(toolReq);

            try
            {
                var selector = GlobalBackendSelector.Instance;
                if (selector?.SessionManager != null)
                    await selector.SessionManager.RespondToApproval(sessionId, approved);
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

        // secret.request is NOT an approve/deny choice: the agent is blocked on
        // secret.respond {request_id, value} (a captured text value, e.g. an API key). It must
        // never go through RespondToApproval — it gets its own masked text-input prompt.
        private void OnHermesSecretRequest(string sessionId, SecretRequest request)
        {
            if (request == null || string.IsNullOrEmpty(request.requestId))
                return;

            if (IsForeground(sessionId))
                ShowSecretNow(request);
            else
                StorePendingSecret(sessionId, request);
        }

        private void ShowSecretNow(SecretRequest request)
        {
            if (request == null || _messagesList == null)
                return;

            string label = !string.IsNullOrWhiteSpace(request.prompt)
                ? request.prompt
                : (!string.IsNullOrWhiteSpace(request.envVar)
                    ? LocalizationExtensions.Get("secret.enter_value_for", "Enter a value for ") + request.envVar
                    : LocalizationExtensions.Get("secret.enter_value", "Enter secret value"));
            _showSystemMessage?.Invoke("[Hermes] " + label);
            ShowSecretInput(request);
        }

        private void ShowSecretInput(SecretRequest request)
        {
            var container = new VisualElement();
            container.AddToClassList("secret-input");
            container.style.marginTop = 4;
            container.style.marginLeft = 40;
            container.style.flexDirection = FlexDirection.Row;
            container.style.flexWrap = Wrap.Wrap;

            var field = new TextField();
            field.isPasswordField = true; // mask the captured secret in the UI
            field.AddToClassList("secret-input__field");
            field.style.minWidth = 200;

            var submit = new Button(() => OnSecretSubmit(request, container, field));
            submit.text = LocalizationExtensions.Get("secret.submit", "Submit");
            submit.AddToClassList("secret-input__btn");

            container.Add(field);
            container.Add(submit);
            _messagesList.Add(container);
            _scrollToBottom?.Invoke();
        }

        private void OnSecretSubmit(SecretRequest request, VisualElement container, TextField field)
        {
            string value = field != null ? field.value : null;
            if (container != null)
                container.SetEnabled(false);
            // Never echo the secret back into the transcript.
            _showSystemMessage?.Invoke("[You] " + LocalizationExtensions.Get("secret.submitted", "(secret submitted)"));
            _ = SendSecretResponse(request, value ?? string.Empty);
        }

        private async Task SendSecretResponse(SecretRequest request, string value)
        {
            var selector = GlobalBackendSelector.Instance;
            if (selector?.SessionManager == null)
                return;
            try
            {
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
