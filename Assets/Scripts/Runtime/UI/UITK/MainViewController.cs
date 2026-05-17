using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using NeonCompanion.Runtime.Localization;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;

namespace NeonCompanion.Runtime.UI.UITK
{
    [RequireComponent(typeof(UIDocument))]
    public sealed class MainViewController : MonoBehaviour
    {
        private Button _navChat;
        private Button _navAvatars;
        private Button _navProviders;

        private VisualElement _centerArea;
        private VisualElement _avatarStage;
        private VisualElement _chatArea;

        private Button _sendButton;
        private TextField _messageInput;

        private ScrollView _sessionsList;
        private Button _summarizeButton;

        private List<VisualElement> _sessionItems = new();

        private void OnEnable()
        {
            var document = GetComponent<UIDocument>();
            if (document == null || document.rootVisualElement == null) return;

            var root = document.rootVisualElement;

            // Navigation
            _navChat = root.Q<Button>("nav-chat");
            _navAvatars = root.Q<Button>("nav-avatars");
            _navProviders = root.Q<Button>("nav-providers");

            _navChat?.clicked += () => ShowSection("chat");
            _navAvatars?.clicked += () => ShowSection("avatars");
            _navProviders?.clicked += () => ShowSection("providers");

            // Center area elements
            _centerArea = root.Q<VisualElement>("center-area");
            _avatarStage = root.Q<VisualElement>("avatar-stage");
            _chatArea = root.Q<VisualElement>("chat-area");

            _sendButton = root.Q<Button>("send-button");
            _messageInput = root.Q<TextField>("message-input");

            _sendButton?.clicked += OnSendMessage;

            // Right sidebar
            _sessionsList = root.Q<ScrollView>("sessions-list");
            _summarizeButton = root.Q<Button>("summarize-btn");

            _summarizeButton?.clicked += OnSummarize;

            // Load sessions
            LoadSessions();

            // Default state
            ShowSection("chat");
        }

        private void ShowSection(string section)
        {
            // Highlight active nav item
            _navChat?.RemoveFromClassList("nav-item--active");
            _navAvatars?.RemoveFromClassList("nav-item--active");
            _navProviders?.RemoveFromClassList("nav-item--active");

            switch (section)
            {
                case "chat":
                    _navChat?.AddToClassList("nav-item--active");
                    _avatarStage.style.display = DisplayStyle.Flex;
                    _chatArea.style.display = DisplayStyle.Flex;
                    break;

                case "avatars":
                    _navAvatars?.AddToClassList("nav-item--active");
                    _avatarStage.style.display = DisplayStyle.Flex;
                    _chatArea.style.display = DisplayStyle.None;
                    break;

                case "providers":
                    _navProviders?.AddToClassList("nav-item--active");
                    _avatarStage.style.display = DisplayStyle.None;
                    _chatArea.style.display = DisplayStyle.None;
                    break;
            }
        }

        private void OnSendMessage()
        {
            if (_messageInput == null || string.IsNullOrWhiteSpace(_messageInput.value))
                return;

            // TODO: Send message via AppManager
            Debug.Log($"[UI] Send: {_messageInput.value}");
            _messageInput.value = "";
        }

        private void OnSummarize()
        {
            Debug.Log("[UI] Summarize sessions clicked");
            // TODO: Implement summarization
        }

        private void LoadSessions()
        {
            if (_sessionsList == null) return;

            _sessionsList.Clear();
            _sessionItems.Clear();

            if (AppManager.Instance == null || AppManager.Instance.Chat == null)
            {
                Debug.LogWarning("[MainViewController] AppManager not ready, using placeholder sessions");
                CreatePlaceholderSessions();
                return;
            }

            // Real data
            var sessions = AppManager.Instance.GetAllChatSessionsAsync().Result;

            if (sessions == null || sessions.Count == 0)
            {
                CreatePlaceholderSessions();
                return;
            }

            foreach (var session in sessions)
            {
                string title = session.title ?? "Untitled Session";
                string meta = $"neon - {session.messages?.Count ?? 0} msg";

                var item = CreateSessionItem(title, meta, session);
                _sessionsList.Add(item);
                _sessionItems.Add(item);
            }
        }

        private void CreatePlaceholderSessions()
        {
            var placeholders = new List<(string title, string meta)>
            {
                ("Дизайн системы рендеринга 2D", "neon - 14 msg"),
                ("Шейдер аватара — breathing", "neon - 6 msg"),
                ("Парсинг ответа OpenAI compatible", "webxr - 22 msg"),
            };

            foreach (var p in placeholders)
            {
                var item = CreateSessionItem(p.title, p.meta, null);
                _sessionsList.Add(item);
                _sessionItems.Add(item);
            }
        }

        private VisualElement CreateSessionItem(string title, string meta, ChatSession session)
        {
            var container = new VisualElement();
            container.AddToClassList("session-item");

            var titleLabel = new Label(title);
            titleLabel.AddToClassList("session-title");

            var metaLabel = new Label(meta);
            metaLabel.AddToClassList("session-meta");

            container.Add(titleLabel);
            container.Add(metaLabel);

            container.RegisterCallback<ClickEvent>(evt =>
            {
                Debug.Log($"[UI] Session selected: {title}");
                // TODO: Load session via AppManager
            });

            return container;
        }
    }
}