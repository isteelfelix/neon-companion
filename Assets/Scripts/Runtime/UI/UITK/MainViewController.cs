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
        private VisualElement _providersArea;

        private Button _sendButton;
        private TextField _messageInput;

        private ScrollView _sessionsList;
        private Button _summarizeButton;

        // Providers UI
        private ScrollView _providersList;
        private Button _addProviderButton;
        private VisualElement _providerEditPanel;
        private TextField _editName;
        private TextField _editBaseUrl;
        private TextField _editApiKey;
        private TextField _editModel;
        private Slider _editTemperature;
        private Button _saveProviderButton;
        private Button _cancelEditButton;

        private ProviderConfig _editingProvider;
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
            _providersArea = root.Q<VisualElement>("providers-area");

            _sendButton = root.Q<Button>("send-button");
            _messageInput = root.Q<TextField>("message-input");

            _sendButton?.clicked += OnSendMessage;

            // Right sidebar
            _sessionsList = root.Q<ScrollView>("sessions-list");
            _summarizeButton = root.Q<Button>("summarize-btn");
            _summarizeButton?.clicked += OnSummarize;

            // Providers section
            _providersList = root.Q<ScrollView>("providers-list");
            _addProviderButton = root.Q<Button>("add-provider-btn");
            _addProviderButton?.clicked += OnAddProviderClicked;

            _providerEditPanel = root.Q<VisualElement>("provider-edit-panel");
            _editName = root.Q<TextField>("edit-name");
            _editBaseUrl = root.Q<TextField>("edit-baseurl");
            _editApiKey = root.Q<TextField>("edit-apikey");
            _editModel = root.Q<TextField>("edit-model");
            _editTemperature = root.Q<Slider>("edit-temperature");
            _saveProviderButton = root.Q<Button>("save-provider-btn");
            _cancelEditButton = root.Q<Button>("cancel-edit-btn");

            _saveProviderButton?.clicked += OnSaveProviderClicked;
            _cancelEditButton?.clicked += OnCancelEditClicked;

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

            _providersArea.style.display = DisplayStyle.None;
            _avatarStage.style.display = DisplayStyle.None;
            _chatArea.style.display = DisplayStyle.None;

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
                    break;

                case "providers":
                    _navProviders?.AddToClassList("nav-item--active");
                    _providersArea.style.display = DisplayStyle.Flex;
                    RefreshProvidersList();
                    break;
            }
        }

        #region Providers

        private async void RefreshProvidersList()
        {
            if (_providersList == null) return;
            _providersList.Clear();

            if (AppManager.Instance?.ProviderManager == null)
            {
                var label = new Label("ProviderManager not ready");
                _providersList.Add(label);
                return;
            }

            var providers = await AppManager.Instance.ProviderManager.GetAllProvidersAsync();

            foreach (var provider in providers)
            {
                var item = CreateProviderListItem(provider);
                _providersList.Add(item);
            }
        }

        private VisualElement CreateProviderListItem(ProviderConfig provider)
        {
            var container = new VisualElement();
            container.AddToClassList("provider-item");

            var nameLabel = new Label(provider.displayName);
            nameLabel.AddToClassList("provider-name");

            var urlLabel = new Label(provider.baseUrl);
            urlLabel.AddToClassList("provider-url");

            var editBtn = new Button(() => StartEditingProvider(provider)) { text = "Edit" };
            var deleteBtn = new Button(() => DeleteProvider(provider)) { text = "Delete" };

            var buttons = new VisualElement();
            buttons.style.flexDirection = FlexDirection.Row;
            buttons.Add(editBtn);
            buttons.Add(deleteBtn);

            container.Add(nameLabel);
            container.Add(urlLabel);
            container.Add(buttons);

            return container;
        }

        private void OnAddProviderClicked()
        {
            _editingProvider = ProviderConfig.CreateDefault("New Provider", "https://api.openai.com/v1");
            ShowEditPanel();
        }

        private void StartEditingProvider(ProviderConfig provider)
        {
            _editingProvider = provider;
            ShowEditPanel();
        }

        private void ShowEditPanel()
        {
            if (_providerEditPanel == null || _editingProvider == null) return;

            _editName.value = _editingProvider.displayName ?? "";
            _editBaseUrl.value = _editingProvider.baseUrl ?? "";
            _editApiKey.value = _editingProvider.apiKey ?? "";
            _editModel.value = _editingProvider.defaultModel ?? "";
            _editTemperature.value = _editingProvider.temperature;

            _providerEditPanel.style.display = DisplayStyle.Flex;
        }

        private async void OnSaveProviderClicked()
        {
            if (_editingProvider == null) return;

            _editingProvider.displayName = _editName.value;
            _editingProvider.baseUrl = _editBaseUrl.value;
            _editingProvider.apiKey = _editApiKey.value;
            _editingProvider.defaultModel = _editModel.value;
            _editingProvider.temperature = _editTemperature.value;

            await AppManager.Instance.ProviderManager.SaveProviderAsync(_editingProvider);

            _providerEditPanel.style.display = DisplayStyle.None;
            RefreshProvidersList();
        }

        private void OnCancelEditClicked()
        {
            _providerEditPanel.style.display = DisplayStyle.None;
            _editingProvider = null;
        }

        private async void DeleteProvider(ProviderConfig provider)
        {
            await AppManager.Instance.ProviderManager.DeleteProviderAsync(provider.id);
            RefreshProvidersList();
        }

        #endregion

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