using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Chat;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Localization;
using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK
{
    [RequireComponent(typeof(UIDocument))]
    public sealed class MainViewController : MonoBehaviour
    {
        private const string ActiveNavClass = "nav-item--active";
        private const string ActiveSessionClass = "session-item--active";

        private readonly List<Button> _navButtons = new List<Button>();
        private readonly List<VisualElement> _sessionItems = new List<VisualElement>();

        private Button _navChat;
        private Button _navAvatars;
        private Button _navProviders;
        private Button _navHistory;
        private Button _navThemes;
        private Button _navSettings;
        private Button _sendButton;
        private Button _summarizeButton;

        private TextField _messageInput;
        private VisualElement _avatarStage;
        private VisualElement _chatArea;
        private VisualElement _chatMessages;
        private ScrollView _chatScroll;
        private ScrollView _sessionsList;

        private ChatService _chatService;
        private bool _isBound;
        private bool _isSending;

        private void OnEnable()
        {
            var document = GetComponent<UIDocument>();
            if (document == null || document.rootVisualElement == null)
                return;

            Bind(document.rootVisualElement);
            RegisterCallbacks();
            ShowChat();

            _ = RefreshAsync();
        }

        private void OnDisable()
        {
            UnregisterCallbacks();
            _isBound = false;
        }

        private void Bind(VisualElement root)
        {
            _navButtons.Clear();

            _navChat = root.Q<Button>("nav-chat");
            _navAvatars = root.Q<Button>("nav-avatars");
            _navProviders = root.Q<Button>("nav-providers");
            _navHistory = root.Q<Button>("nav-history");
            _navThemes = root.Q<Button>("nav-themes");
            _navSettings = root.Q<Button>("nav-settings");

            AddNav(_navChat);
            AddNav(_navAvatars);
            AddNav(_navProviders);
            AddNav(_navHistory);
            AddNav(_navThemes);
            AddNav(_navSettings);

            _avatarStage = root.Q<VisualElement>("avatar-stage");
            _chatArea = root.Q<VisualElement>("chat-area");
            _chatScroll = root.Q<ScrollView>("chat-scroll");
            _chatMessages = root.Q<VisualElement>("chat-messages");
            _messageInput = root.Q<TextField>("message-input");
            _sendButton = root.Q<Button>("send-button");
            _sessionsList = root.Q<ScrollView>("sessions-list");
            _summarizeButton = root.Q<Button>("summarize-btn");

            _navChat?.Localize("tab.chat");
            _navAvatars?.Localize("tab.avatar");
            _navProviders?.Localize("settings.providers");
            _navHistory?.Localize("chat.history");
            _navThemes?.Localize("settings.themes");
            _navSettings?.Localize("tab.settings");

            if (_messageInput != null)
                _messageInput.RegisterCallback<KeyDownEvent>(OnInputKeyDown);

            _isBound = true;
        }

        private void AddNav(Button button)
        {
            if (button != null)
                _navButtons.Add(button);
        }

        private void RegisterCallbacks()
        {
            _navChat?.clicked += ShowChat;
            _navAvatars?.clicked += ShowAvatars;
            _navProviders?.clicked += ShowProviders;
            _navHistory?.clicked += ShowHistory;
            _navThemes?.clicked += ShowThemes;
            _navSettings?.clicked += ShowSettings;
            _sendButton?.clicked += OnSendClicked;
            _summarizeButton?.clicked += OnSummarizeClicked;
        }

        private void UnregisterCallbacks()
        {
            _navChat?.clicked -= ShowChat;
            _navAvatars?.clicked -= ShowAvatars;
            _navProviders?.clicked -= ShowProviders;
            _navHistory?.clicked -= ShowHistory;
            _navThemes?.clicked -= ShowThemes;
            _navSettings?.clicked -= ShowSettings;
            _sendButton?.clicked -= OnSendClicked;
            _summarizeButton?.clicked -= OnSummarizeClicked;

            if (_messageInput != null)
                _messageInput.UnregisterCallback<KeyDownEvent>(OnInputKeyDown);
        }

        private void ShowChat()
        {
            SetActiveNav(_navChat);
            SetDisplay(_avatarStage, DisplayStyle.Flex);
            SetDisplay(_chatArea, DisplayStyle.Flex);
        }

        private void ShowAvatars()
        {
            SetActiveNav(_navAvatars);
            SetDisplay(_avatarStage, DisplayStyle.Flex);
            SetDisplay(_chatArea, DisplayStyle.None);
        }

        private void ShowProviders()
        {
            SetActiveNav(_navProviders);
            SetDisplay(_avatarStage, DisplayStyle.None);
            SetDisplay(_chatArea, DisplayStyle.None);
        }

        private void ShowHistory()
        {
            SetActiveNav(_navHistory);
            SetDisplay(_avatarStage, DisplayStyle.None);
            SetDisplay(_chatArea, DisplayStyle.Flex);
        }

        private void ShowThemes()
        {
            SetActiveNav(_navThemes);
            SetDisplay(_avatarStage, DisplayStyle.None);
            SetDisplay(_chatArea, DisplayStyle.None);
        }

        private void ShowSettings()
        {
            SetActiveNav(_navSettings);
            SetDisplay(_avatarStage, DisplayStyle.None);
            SetDisplay(_chatArea, DisplayStyle.None);
        }

        private void SetActiveNav(Button active)
        {
            foreach (var button in _navButtons)
                button.EnableInClassList(ActiveNavClass, button == active);
        }

        private static void SetDisplay(VisualElement element, DisplayStyle display)
        {
            if (element != null)
                element.style.display = display;
        }

        private void OnInputKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode != KeyCode.Return || !evt.ctrlKey)
                return;

            evt.StopPropagation();
            OnSendClicked();
        }

        private void OnSendClicked()
        {
            _ = SendCurrentMessageAsync();
        }

        private async Task SendCurrentMessageAsync()
        {
            if (_isSending || _messageInput == null || string.IsNullOrWhiteSpace(_messageInput.value))
                return;

            string message = _messageInput.value.Trim();
            _messageInput.value = string.Empty;
            SetSending(true);

            try
            {
                var chat = await GetChatServiceAsync();
                if (chat == null)
                {
                    AddSystemMessage("Application is not initialized.");
                    return;
                }

                await chat.SendMessageAsync(message);
                RenderMessages(chat.CurrentChatViewModel?.Messages);
                await LoadSessionsAsync(chat);
            }
            catch (Exception ex)
            {
                AddSystemMessage($"[Error] {ex.Message}");
                NeonLogger.LogError(ex.ToString());
            }
            finally
            {
                SetSending(false);
            }
        }

        private void SetSending(bool isSending)
        {
            _isSending = isSending;
            if (_sendButton != null)
                _sendButton.SetEnabled(!isSending);
        }

        private void OnSummarizeClicked()
        {
            AddSystemMessage("Summarize is not implemented yet.");
        }

        private async Task RefreshAsync()
        {
            try
            {
                var chat = await GetChatServiceAsync();
                if (!_isBound || chat == null)
                    return;

                RenderMessages(chat.CurrentChatViewModel?.Messages);
                await LoadSessionsAsync(chat);
            }
            catch (Exception ex)
            {
                NeonLogger.LogError(ex.ToString());
            }
        }

        private async Task<ChatService> GetChatServiceAsync()
        {
            if (_chatService != null)
                return _chatService;

            for (int i = 0; i < 120 && isActiveAndEnabled; i++)
            {
                var bootstrap = UnityEngine.Object.FindFirstObjectByType<AppBootstrap>();
                var app = bootstrap?.App;
                if (app != null)
                {
                    _chatService = app.ChatService;
                    await _chatService.GetOrCreateChatAsync();
                    return _chatService;
                }

                await Task.Yield();
            }

            return null;
        }

        private async Task LoadSessionsAsync(ChatService chat)
        {
            if (_sessionsList == null)
                return;

            var sessions = await chat.GetAllSessionsAsync();
            if (!_isBound)
                return;

            _sessionsList.Clear();
            _sessionItems.Clear();

            foreach (var session in sessions)
            {
                var item = CreateSessionItem(session);
                _sessionsList.Add(item);
                _sessionItems.Add(item);
            }
        }

        private VisualElement CreateSessionItem(ChatSession session)
        {
            var container = new VisualElement();
            container.AddToClassList("session-item");

            var titleLabel = new Label(string.IsNullOrWhiteSpace(session.title) ? "New chat" : session.title);
            titleLabel.AddToClassList("session-title");

            int count = session.messages?.Count ?? 0;
            var metaLabel = new Label($"{count} msg");
            metaLabel.AddToClassList("session-meta");

            container.Add(titleLabel);
            container.Add(metaLabel);
            container.RegisterCallback<ClickEvent>(_ => _ = SwitchSessionAsync(session, container));

            return container;
        }

        private async Task SwitchSessionAsync(ChatSession session, VisualElement item)
        {
            try
            {
                var chat = await GetChatServiceAsync();
                if (chat == null)
                    return;

                await chat.SwitchToSessionAsync(session);
                SetActiveSession(item);
                RenderMessages(chat.CurrentChatViewModel?.Messages);
            }
            catch (Exception ex)
            {
                NeonLogger.LogError(ex.ToString());
            }
        }

        private void SetActiveSession(VisualElement selected)
        {
            foreach (var item in _sessionItems)
                item.EnableInClassList(ActiveSessionClass, item == selected);
        }

        private void RenderMessages(IReadOnlyList<ChatMessage> messages)
        {
            if (_chatMessages == null)
                return;

            _chatMessages.Clear();
            if (messages != null)
            {
                foreach (var message in messages)
                    _chatMessages.Add(CreateMessageElement(message));
            }

            if (_chatScroll == null)
                return;

            _chatScroll.schedule.Execute(() =>
            {
                if (_chatScroll != null)
                    _chatScroll.scrollOffset = new Vector2(0f, float.MaxValue);
            });
        }

        private static VisualElement CreateMessageElement(ChatMessage message)
        {
            var container = new VisualElement();
            container.AddToClassList("chat-message");
            container.AddToClassList(message.role == "user" ? "chat-message--user" : "chat-message--assistant");

            var label = new Label(message.content ?? string.Empty);
            label.AddToClassList("chat-message__text");
            container.Add(label);

            return container;
        }

        private void AddSystemMessage(string text)
        {
            if (_chatMessages == null)
                return;

            _chatMessages.Add(CreateMessageElement(new ChatMessage
            {
                role = "assistant",
                content = text,
                unixTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            }));
        }
    }
}
