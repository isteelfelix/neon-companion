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
    internal sealed class SessionHistoryController
    {
        public struct Deps
        {
            // UI elements
            public ScrollView SessionsList;
            public ScrollView HistorySessionsList;
            public List<VisualElement> SessionItems;
            public Label HistoryState;
            public VisualElement HistorySearchBar;
            public VisualElement HistoryPanelSearchBar;
            public TextField HistorySearchInput;
            public TextField HistoryPanelSearchInput;
            public Label NavChatCount;
            public Label TopbarTitle;
            public VisualElement ChatPanel;
            // Services
            public Func<Task<ChatService>> GetChatServiceAsync;
            public Func<Task<CompanionApp>> GetAppAsync;
            public Func<bool> IsBound;
            // State getters
            public Func<string> GetCurrentSessionId;
            public Func<string> GetChatTitle;
            public Func<string> GetSessionSearchQuery;
            public Action<string> SetSessionSearchQuery;
            public Action<string, string> SetCurrentSession;
            public Action<string, string> SetTopbar;
            // Rendering
            public Action RenderMessages;
            public Action<string> ShowSystemMessage;
            public Action<string, bool> ShowHistoryState;
            // Navigation
            public Action ShowChat;
            // Chat controller
            public Action ClearPendingComposerAttachments;
            public Func<TextField> GetMessageInput;
            public Action<object> SetProviderHeader;
        }

        private Deps _d;

        public void SetDeps(Deps deps) { _d = deps; }

        public async Task LoadSessionsAsync(ChatService chat)
        {
            if (_d.SessionsList == null && _d.HistorySessionsList == null)
                return;

            _d.ShowHistoryState(LocalizationExtensions.Get("history.loading", "Загрузка истории…"), false);
            var allSessions = await chat.GetAllSessionsAsync();
            var providers = new List<ProviderConfig>();
            var app = await _d.GetAppAsync();
            if (app != null)
                providers = await app.ProviderManager.GetAllProvidersAsync();
            if (!_d.IsBound())
                return;

            string currentSessionId = _d.GetCurrentSessionId();
            string currentSessionTitle = string.Empty;

            // Sync session identity from service if not yet captured
            if (string.IsNullOrEmpty(currentSessionId))
            {
                currentSessionId = chat.CurrentSessionId ?? string.Empty;
            }

            // Sync display title from the actual current session
            if (!string.IsNullOrEmpty(currentSessionId))
            {
                var active = allSessions.Find(s => s.sessionId == currentSessionId);
                if (active != null)
                {
                    currentSessionTitle = string.IsNullOrWhiteSpace(active.title) || active.title == "New chat"
                        ? string.Empty
                        : active.title;
                }
                else
                {
                    currentSessionId = string.Empty;
                    currentSessionTitle = string.Empty;
                }
            }

            _d.SetCurrentSession(currentSessionId, currentSessionTitle);

            if (_d.NavChatCount != null)
                _d.NavChatCount.text = allSessions.Count.ToString();

            if (_d.TopbarTitle != null && _d.ChatPanel != null && _d.ChatPanel.style.display != DisplayStyle.None)
                _d.TopbarTitle.text = _d.GetChatTitle();

            RenderSessionList(allSessions, providers);
        }

        public void RenderSessionList(List<ChatSession> allSessions, List<ProviderConfig> providers)
        {
            if (_d.SessionsList == null && _d.HistorySessionsList == null) return;

            _d.SessionsList?.Clear();
            _d.HistorySessionsList?.Clear();
            _d.SessionItems.Clear();

            string searchQuery = _d.GetSessionSearchQuery();
            var sessions = string.IsNullOrWhiteSpace(searchQuery)
                ? allSessions
                : allSessions.FindAll(s =>
                    (s.title ?? string.Empty).IndexOf(searchQuery, StringComparison.OrdinalIgnoreCase) >= 0);

            AddSessionHeader(_d.SessionsList, sessions.Count);
            AddSessionHeader(_d.HistorySessionsList, sessions.Count);

            if (sessions.Count == 0)
            {
                string emptyText = string.IsNullOrWhiteSpace(searchQuery)
                    ? LocalizationExtensions.Get("history.empty.saved_sessions", "Сохранённых сессий пока нет.")
                    : LocalizationExtensions.Get("history.empty.search", "По этому запросу ничего не найдено.");
                var railEmpty = new Label(emptyText);
                railEmpty.AddToClassList("history__meta");
                _d.SessionsList?.Add(railEmpty);

                var historyEmpty = new Label(emptyText);
                historyEmpty.AddToClassList("history__meta");
                _d.HistorySessionsList?.Add(historyEmpty);
                _d.ShowHistoryState(string.IsNullOrWhiteSpace(searchQuery)
                    ? LocalizationExtensions.Get("history.empty.first_session", "История пуста. Начните чат, чтобы появилась первая сессия.")
                    : LocalizationExtensions.Get("history.search.try_another", "Попробуйте изменить поисковый запрос."), false);
                return;
            }

            _d.ShowHistoryState(string.Empty, false);
            string currentSessionId = _d.GetCurrentSessionId();
            for (int i = 0; i < sessions.Count; i++)
            {
                bool IsActiveSession(ChatSession session, int index)
                {
                    if (!string.IsNullOrEmpty(currentSessionId))
                        return session.sessionId == currentSessionId;
                    return index == 0;
                }

                bool isActive = IsActiveSession(sessions[i], i);
                var railItem = CreateSessionItem(sessions[i], isActive, providers);
                var historyItem = CreateSessionItem(sessions[i], isActive, providers);
                _d.SessionsList?.Add(railItem);
                _d.HistorySessionsList?.Add(historyItem);
                _d.SessionItems.Add(railItem);
                _d.SessionItems.Add(historyItem);
            }
        }

        // ---- History search ----

        public void OnHistorySearchToggled()
        {
            bool railVisible = _d.HistorySearchBar != null && _d.HistorySearchBar.style.display == DisplayStyle.Flex;
            bool panelVisible = _d.HistoryPanelSearchBar != null && _d.HistoryPanelSearchBar.style.display == DisplayStyle.Flex;
            bool isVisible = railVisible || panelVisible;
            SetDisplay(_d.HistorySearchBar, isVisible ? DisplayStyle.None : DisplayStyle.Flex);
            SetDisplay(_d.HistoryPanelSearchBar, isVisible ? DisplayStyle.None : DisplayStyle.Flex);
            if (!isVisible)
                (_d.HistoryPanelSearchInput ?? _d.HistorySearchInput)?.Focus();
            if (isVisible)
                OnHistorySearchCleared();
        }

        public void OnHistorySearchCleared()
        {
            _d.SetSessionSearchQuery(string.Empty);
            if (_d.HistorySearchInput != null)
                _d.HistorySearchInput.SetValueWithoutNotify(string.Empty);
            if (_d.HistoryPanelSearchInput != null)
                _d.HistoryPanelSearchInput.SetValueWithoutNotify(string.Empty);
            SetDisplay(_d.HistorySearchBar, DisplayStyle.None);
            SetDisplay(_d.HistoryPanelSearchBar, DisplayStyle.None);
            _ = RefreshSessionsFromCacheAsync();
        }

        public void OnHistorySearchChanged(ChangeEvent<string> evt)
        {
            _d.SetSessionSearchQuery(evt.newValue ?? string.Empty);
            string query = _d.GetSessionSearchQuery();
            if (_d.HistorySearchInput != null && _d.HistorySearchInput != evt.target)
                _d.HistorySearchInput.SetValueWithoutNotify(query);
            if (_d.HistoryPanelSearchInput != null && _d.HistoryPanelSearchInput != evt.target)
                _d.HistoryPanelSearchInput.SetValueWithoutNotify(query);
            _ = RefreshSessionsFromCacheAsync();
        }

        public async Task RefreshSessionsFromCacheAsync()
        {
            var chat = await _d.GetChatServiceAsync();
            if (chat == null) return;
            try
            {
                _d.ShowHistoryState(LocalizationExtensions.Get("history.loading", "Загрузка истории…"), false);
                var allSessions = await chat.GetAllSessionsAsync();
                var app = await _d.GetAppAsync();
                var providers = app != null ? await app.ProviderManager.GetAllProvidersAsync() : new List<ProviderConfig>();
                if (_d.IsBound()) RenderSessionList(allSessions, providers);
            }
            catch (Exception ex)
            {
                _d.ShowHistoryState(LocalizationExtensions.Get("history.load_failed", "Не удалось загрузить историю чатов."), true);
                NeonLogger.LogError(ex.ToString());
            }
        }

        // ---- Session operations ----

        public async Task SwitchSessionAsync(ChatSession session)
        {
            try
            {
                var chat = await _d.GetChatServiceAsync();
                if (chat == null) return;

                await chat.SwitchToSessionAsync(session);
                _d.SetCurrentSession(
                    session.sessionId,
                    string.IsNullOrWhiteSpace(session.title) || session.title == "New chat"
                        ? string.Empty
                        : session.title);
                _d.ClearPendingComposerAttachments();
                var msgInput = _d.GetMessageInput();
                if (msgInput != null)
                    msgInput.value = string.Empty;
                _d.SetProviderHeader(chat.CurrentProvider, chat.CurrentSessionModel);
                _d.RenderMessages();
                await LoadSessionsAsync(chat);
                _d.ShowChat();
            }
            catch (Exception ex)
            {
                NeonLogger.LogError(ex.ToString());
            }
        }

        public async Task DeleteSessionAndRefreshAsync(string sessionId)
        {
            try
            {
                var chat = await _d.GetChatServiceAsync();
                if (chat == null) return;

                await chat.DeleteSessionAsync(sessionId);

                string currentSessionId = _d.GetCurrentSessionId();
                if (currentSessionId == sessionId)
                {
                    _d.SetCurrentSession(chat.CurrentSessionId ?? string.Empty, string.Empty);
                    _d.SetProviderHeader(chat.CurrentProvider, chat.CurrentSessionModel);
                    _d.RenderMessages();
                }

                await LoadSessionsAsync(chat);
            }
            catch (Exception ex)
            {
                NeonLogger.LogError(ex.ToString());
            }
        }

        // ---- UI helpers ----

        private void AddSessionHeader(ScrollView target, int sessionsCount)
        {
            if (target == null) return;
            string searchQuery = _d.GetSessionSearchQuery();
            var groupLabel = new Label(string.IsNullOrWhiteSpace(searchQuery)
                ? LocalizationExtensions.Get("history.group.recent", "Недавние")
                : LocalizationExtensions.GetFormat("history.group.results", "Результаты: {0}", sessionsCount));
            groupLabel.AddToClassList("history__group");
            target.Add(groupLabel);
        }

        private VisualElement CreateSessionItem(ChatSession session, bool isActive, List<ProviderConfig> providers)
        {
            var container = new VisualElement();
            container.AddToClassList("history__item");
            container.EnableInClassList("history__item--active", isActive);

            var headerRow = new VisualElement();
            headerRow.AddToClassList("history__row");

            var titleLabel = new Label(string.IsNullOrWhiteSpace(session.title) || session.title == "New chat"
                ? LocalizationExtensions.Get("chat.new", "Новый чат")
                : session.title);
            titleLabel.AddToClassList("history__title");

            var providerLabel = new Label(BuildSessionProviderLabel(session, providers));
            providerLabel.AddToClassList("history__provider");

            int count = session.messages?.Count ?? 0;
            var metaLabel = new Label(ChatController.MessageCountText(count));
            metaLabel.AddToClassList("history__meta");

            var deleteBtn = new Button { text = "\u00d7" };
            deleteBtn.AddToClassList("history__delete-btn");
            bool deletePending = false;
            string sid = session.sessionId;
            deleteBtn.RegisterCallback<ClickEvent>(evt =>
            {
                evt.StopPropagation();
                if (!deletePending)
                {
                    deletePending = true;
                    deleteBtn.text = "\u2713";
                    deleteBtn.AddToClassList("history__delete-btn--confirm");
                    return;
                }
                _ = DeleteSessionAndRefreshAsync(sid);
            });

            headerRow.Add(titleLabel);
            headerRow.Add(providerLabel);
            headerRow.Add(deleteBtn);

            container.Add(headerRow);
            container.Add(metaLabel);
            container.RegisterCallback<ClickEvent>(evt => { _ = SwitchSessionAsync(session); });

            return container;
        }

        private static string BuildSessionProviderLabel(ChatSession session, List<ProviderConfig> providers)
        {
            if (session == null)
                return LocalizationExtensions.Get("history.provider.none", "Провайдер: —");

            if (string.IsNullOrWhiteSpace(session.providerId))
                return LocalizationExtensions.Get("history.provider.default", "Провайдер: default");

            var provider = providers?.Find(p => p != null && p.id == session.providerId);
            if (provider != null && !string.IsNullOrWhiteSpace(provider.displayName))
                return LocalizationExtensions.GetFormat("history.provider.named", "Провайдер: {0}", provider.displayName);

            return LocalizationExtensions.GetFormat("history.provider.named", "Провайдер: {0}", ShortProviderId(session.providerId));
        }

        private static string ShortProviderId(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId))
                return "—";

            return providerId.Length <= 8 ? providerId : providerId.Substring(0, 8);
        }

        private static void SetDisplay(VisualElement element, DisplayStyle display)
        {
            if (element != null)
                element.style.display = display;
        }
    }
}
