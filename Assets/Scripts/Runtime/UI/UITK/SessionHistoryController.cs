using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Chat;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Localization;
using NeonCompanion.Runtime.UI.UITK.Chat;
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
            public Action<object, object> SetProviderHeader;
        }

        private Deps _d;

        private readonly HashSet<string> _collapsedFolders = new HashSet<string>();
        private readonly List<string> _currentAllFolders = new List<string>();
        private VisualElement _sessionContextMenu;
        private EventCallback<PointerDownEvent> _sessionMenuOutsideHandler;
        private VisualElement _sessionMenuRoot;
        private float _sessionMenuIgnoreUntil;
        private IVisualElementScheduledItem _sessionMenuOutsideSchedule;
        private VisualElement _folderInputPopup;
        private EventCallback<PointerDownEvent> _folderInputOutsideHandler;
        private VisualElement _folderInputRoot;

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

            var visibleSessions = FilterSessionsForCurrentBackend(allSessions, providers);
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
                var active = visibleSessions.Find(s => s.sessionId == currentSessionId);
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
                _d.NavChatCount.text = visibleSessions.Count.ToString();

            if (_d.TopbarTitle != null && _d.ChatPanel != null && _d.ChatPanel.style.display != DisplayStyle.None)
                _d.TopbarTitle.text = _d.GetChatTitle();

            RenderSessionList(visibleSessions, providers);
        }

        public void RenderSessionList(List<ChatSession> allSessions, List<ProviderConfig> providers)
        {
            if (_d.SessionsList == null && _d.HistorySessionsList == null) return;

            allSessions = FilterSessionsForCurrentBackend(allSessions, providers);
            if (_d.NavChatCount != null)
                _d.NavChatCount.text = allSessions.Count.ToString();
            HideSessionMenus();
            UpdateKnownFolders(allSessions);

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

            var groups = BuildFolderGroups(sessions);
            var sortedFolders = groups.Keys
                .Where(k => k != "")
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();
            bool hasUncat = groups.ContainsKey("");

            RenderGroupsToList(_d.SessionsList, groups, sortedFolders, hasUncat, providers, currentSessionId);
            RenderGroupsToList(_d.HistorySessionsList, groups, sortedFolders, hasUncat, providers, currentSessionId);
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
                if (_d.IsBound()) RenderSessionList(FilterSessionsForCurrentBackend(allSessions, providers), providers);
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
                else if (string.IsNullOrEmpty(chat.CurrentSessionId))
                {
                    _d.SetCurrentSession(string.Empty, string.Empty);
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
            var metaLabel = new Label(ChatAttachmentManager.MessageCountText(count));
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

            container.RegisterCallback<ClickEvent>(evt =>
            {
                if (evt.button == 0)
                    _ = SwitchSessionAsync(session);
            });
            container.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button == 1)
                {
                    evt.StopImmediatePropagation();
                    ShowSessionContextMenu(container, session, evt.position);
                }
            });

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

        private static List<ChatSession> FilterSessionsForCurrentBackend(List<ChatSession> sessions, List<ProviderConfig> providers)
        {
            if (sessions == null)
                return new List<ChatSession>();

            var selector = GlobalBackendSelector.Instance;
            BackendMode mode = selector != null ? selector.CurrentMode : BackendMode.OpenAI;
            bool hermesMode = mode == BackendMode.Hermes;
            return sessions.FindAll(s => IsSessionForBackend(s, providers, hermesMode));
        }

        private static bool IsSessionForBackend(ChatSession session, List<ProviderConfig> providers, bool hermesMode)
        {
            if (session == null)
                return false;

            ProviderConfig provider = null;
            if (!string.IsNullOrWhiteSpace(session.providerId) && providers != null)
                provider = providers.Find(p => p != null && string.Equals(p.id, session.providerId, StringComparison.Ordinal));

            if (provider != null)
                return provider.isEnabled && ChatService.IsHermesProvider(provider) == hermesMode;

            if (!string.IsNullOrWhiteSpace(session.providerSessionId))
                return hermesMode;

            return !hermesMode;
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

        // ---- Folder grouping helpers ----

        private void UpdateKnownFolders(List<ChatSession> allSessions)
        {
            _currentAllFolders.Clear();
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (allSessions != null)
            {
                foreach (var s in allSessions)
                {
                    if (!string.IsNullOrWhiteSpace(s.folder))
                    {
                        string f = s.folder.Trim();
                        if (set.Add(f))
                            _currentAllFolders.Add(f);
                    }
                }
            }
            _currentAllFolders.Sort(StringComparer.OrdinalIgnoreCase);
        }

        private Dictionary<string, List<ChatSession>> BuildFolderGroups(List<ChatSession> sessions)
        {
            var groups = new Dictionary<string, List<ChatSession>>();
            if (sessions != null)
            {
                foreach (var s in sessions)
                {
                    string key = string.IsNullOrWhiteSpace(s.folder) ? "" : s.folder.Trim();
                    if (!groups.ContainsKey(key))
                        groups[key] = new List<ChatSession>();
                    groups[key].Add(s);
                }
            }
            return groups;
        }

        private void RenderGroupsToList(
            ScrollView target,
            Dictionary<string, List<ChatSession>> groups,
            List<string> sortedFolders,
            bool hasUncat,
            List<ProviderConfig> providers,
            string currentSessionId)
        {
            if (target == null) return;

            foreach (string f in sortedFolders)
            {
                var list = groups[f];
                bool expanded = !_collapsedFolders.Contains(f);
                var header = CreateFolderHeader(f, list.Count, expanded, f);
                target.Add(header);
                if (expanded)
                {
                    foreach (var s in list)
                    {
                        bool isActive = !string.IsNullOrEmpty(currentSessionId) && s.sessionId == currentSessionId;
                        var item = CreateSessionItem(s, isActive, providers);
                        target.Add(item);
                        _d.SessionItems.Add(item);
                    }
                }
            }

            if (hasUncat)
            {
                var list = groups[""];
                bool hasNamedFolders = sortedFolders.Count > 0;
                if (hasNamedFolders)
                {
                    string display = LocalizationExtensions.Get("session.folder.uncategorized", "Uncategorized");
                    bool expanded = !_collapsedFolders.Contains("");
                    var header = CreateFolderHeader(display, list.Count, expanded, "");
                    target.Add(header);
                    if (expanded)
                    {
                        foreach (var s in list)
                        {
                            bool isActive = !string.IsNullOrEmpty(currentSessionId) && s.sessionId == currentSessionId;
                            var item = CreateSessionItem(s, isActive, providers);
                            target.Add(item);
                            _d.SessionItems.Add(item);
                        }
                    }
                }
                else
                {
                    // No named folders yet: render uncategorized items flat (preserve legacy look under "Recent")
                    foreach (var s in list)
                    {
                        bool isActive = !string.IsNullOrEmpty(currentSessionId) && s.sessionId == currentSessionId;
                        var item = CreateSessionItem(s, isActive, providers);
                        target.Add(item);
                        _d.SessionItems.Add(item);
                    }
                }
            }
        }

        private VisualElement CreateFolderHeader(string displayName, int itemCount, bool expanded, string folderKey)
        {
            var header = new VisualElement();
            header.AddToClassList("session-folder-header");

            string chevronChar = expanded ? "\u25bc" : "\u25b6";
            var chevron = new Label(chevronChar);
            chevron.AddToClassList("session-folder-chevron");

            string labelText = displayName;
            if (itemCount > 0)
                labelText += " (" + itemCount + ")";
            var nameLabel = new Label(labelText);
            nameLabel.AddToClassList("session-folder-name");

            header.Add(chevron);
            header.Add(nameLabel);

            string key = folderKey ?? string.Empty;
            header.RegisterCallback<ClickEvent>(evt =>
            {
                evt.StopPropagation();
                if (_collapsedFolders.Contains(key))
                    _collapsedFolders.Remove(key);
                else
                    _collapsedFolders.Add(key);
                _ = RefreshSessionsFromCacheAsync();
            });

            return header;
        }

        // ---- Session folder context menu ----

        private void ShowSessionContextMenu(VisualElement target, ChatSession session, Vector2 position)
        {
            if (target == null || target.panel == null)
                return;

            HideSessionMenus();

            var menu = new VisualElement();
            menu.AddToClassList("session-context-menu");
            // Inline fallback so the menu renders correctly regardless of stylesheet scope
            menu.style.flexDirection = FlexDirection.Column;
            menu.style.backgroundColor = new Color(0.118f, 0.137f, 0.196f, 0.98f);
            menu.style.borderTopWidth = 1f;
            menu.style.borderRightWidth = 1f;
            menu.style.borderBottomWidth = 1f;
            menu.style.borderLeftWidth = 1f;
            menu.style.borderTopColor = new Color(0.392f, 0.471f, 0.784f, 0.3f);
            menu.style.borderRightColor = new Color(0.392f, 0.471f, 0.784f, 0.3f);
            menu.style.borderBottomColor = new Color(0.392f, 0.471f, 0.784f, 0.3f);
            menu.style.borderLeftColor = new Color(0.392f, 0.471f, 0.784f, 0.3f);
            menu.style.borderTopLeftRadius = 8f;
            menu.style.borderTopRightRadius = 8f;
            menu.style.borderBottomLeftRadius = 8f;
            menu.style.borderBottomRightRadius = 8f;
            menu.style.paddingTop = 4f;
            menu.style.paddingBottom = 4f;
            menu.style.paddingLeft = 4f;
            menu.style.paddingRight = 4f;

            string currentF = string.IsNullOrWhiteSpace(session.folder) ? "" : session.folder.Trim();

            // Uncategorized target
            string uncatLabel = LocalizationExtensions.Get("session.folder.uncategorized", "Uncategorized");
            if (currentF == "") uncatLabel += " \u2713";
            menu.Add(CreateFolderMenuItem("\uD83D\uDCC1", uncatLabel, () =>
            {
                HideSessionMenus();
                if (currentF != "") _ = AssignSessionToFolderAsync(session, string.Empty);
            }));

            // Existing folders
            foreach (var f in _currentAllFolders)
            {
                string label = f;
                if (f == currentF) label += " \u2713";
                string ff = f;
                menu.Add(CreateFolderMenuItem("\uD83D\uDCC1", label, () =>
                {
                    HideSessionMenus();
                    if (ff != currentF) _ = AssignSessionToFolderAsync(session, ff);
                }));
            }

            // New folder
            string newLabel = LocalizationExtensions.Get("session.folder.new", "New folder...");
            menu.Add(CreateFolderMenuItem("\u2795", newLabel, () =>
            {
                HideSessionMenus();
                ShowNewFolderNamePopup(target, session, position);
            }));

            var root = GetDocumentRoot(target);
            if (root == null)
                return;

            _sessionContextMenu = menu;
            _sessionMenuRoot = root;

            // Convert pointer position to root-local coordinates (mirrors MessageContextMenu.ShowAt)
            Vector2 localPos = root.WorldToLocal(position);
            float left = localPos.x + 8f;
            float top = localPos.y + 4f;
            if (left < 0f) left = 0f;
            if (top < 0f) top = 0f;
            float maxLeft = root.layout.width - 180f;
            float maxTop = root.layout.height - 220f;
            if (maxLeft < 0f) maxLeft = 0f;
            if (maxTop < 0f) maxTop = 0f;
            if (left > maxLeft) left = maxLeft;
            if (top > maxTop) top = maxTop;

            menu.style.position = Position.Absolute;
            menu.style.left = left;
            menu.style.top = top;
            menu.style.minWidth = 160f;
            menu.style.display = DisplayStyle.Flex;
            menu.style.visibility = Visibility.Visible;

            root.Add(menu);
            menu.BringToFront();

            // Ignore outside-pointer close right after open to avoid self-close from the opening click
            _sessionMenuIgnoreUntil = Time.realtimeSinceStartup + 0.15f;

            _sessionMenuOutsideHandler = OnSessionMenuOutside;
            if (_sessionMenuOutsideSchedule != null)
            {
                _sessionMenuOutsideSchedule.Pause();
                _sessionMenuOutsideSchedule = null;
            }
            _sessionMenuOutsideSchedule = root.schedule.Execute(() =>
            {
                if (_sessionMenuRoot != null && _sessionMenuOutsideHandler != null)
                    _sessionMenuRoot.RegisterCallback(_sessionMenuOutsideHandler, TrickleDown.TrickleDown);
                _sessionMenuOutsideSchedule = null;
            }).StartingIn(16);
        }

        private VisualElement CreateFolderMenuItem(string icon, string labelText, Action onClick)
        {
            var item = new VisualElement();
            item.AddToClassList("message-context-menu__item");
            // Inline fallback styles (CSS may be template-scoped and unavailable here)
            item.style.flexDirection = FlexDirection.Row;
            item.style.alignItems = Align.Center;
            item.style.paddingLeft = 12f;
            item.style.paddingRight = 16f;
            item.style.paddingTop = 6f;
            item.style.paddingBottom = 6f;
            item.style.borderTopLeftRadius = 4f;
            item.style.borderTopRightRadius = 4f;
            item.style.borderBottomLeftRadius = 4f;
            item.style.borderBottomRightRadius = 4f;
            item.style.minHeight = 28f;

            var iconLabel = new Label(icon);
            iconLabel.AddToClassList("message-context-menu__icon");
            iconLabel.style.marginRight = 8f;
            iconLabel.style.fontSize = 14f;
            iconLabel.style.width = 18f;

            var textLabel = new Label(labelText);
            textLabel.AddToClassList("message-context-menu__label");
            textLabel.style.fontSize = 13f;
            textLabel.style.color = new Color(0.878f, 0.878f, 0.878f, 1f); // #e0e0e0

            item.Add(iconLabel);
            item.Add(textLabel);

            item.RegisterCallback<ClickEvent>(evt =>
            {
                evt.StopPropagation();
                if (onClick != null)
                    onClick.Invoke();
            });

            item.RegisterCallback<PointerEnterEvent>(_ =>
            {
                item.AddToClassList("message-context-menu__item--hover");
                item.style.backgroundColor = new Color(1f, 1f, 1f, 0.08f);
            });
            item.RegisterCallback<PointerLeaveEvent>(_ =>
            {
                item.RemoveFromClassList("message-context-menu__item--hover");
                item.style.backgroundColor = Color.clear;
            });

            return item;
        }

        private void OnSessionMenuOutside(PointerDownEvent evt)
        {
            if (Time.realtimeSinceStartup < _sessionMenuIgnoreUntil)
                return;
            if (_sessionContextMenu != null && _sessionContextMenu.worldBound.Contains(evt.position))
                return;
            HideSessionMenus();
        }

        private void HideSessionMenus()
        {
            if (_sessionContextMenu != null && _sessionContextMenu.parent != null)
                _sessionContextMenu.RemoveFromHierarchy();

            if (_sessionMenuOutsideSchedule != null)
            {
                _sessionMenuOutsideSchedule.Pause();
                _sessionMenuOutsideSchedule = null;
            }

            if (_sessionMenuRoot != null && _sessionMenuOutsideHandler != null)
                _sessionMenuRoot.UnregisterCallback(_sessionMenuOutsideHandler, TrickleDown.TrickleDown);

            _sessionContextMenu = null;
            _sessionMenuRoot = null;
            _sessionMenuOutsideHandler = null;
            _sessionMenuIgnoreUntil = 0f;

            HideFolderInputPopup();
        }

        private void ShowNewFolderNamePopup(VisualElement target, ChatSession session, Vector2 position)
        {
            if (target == null || target.panel == null)
                return;

            HideFolderInputPopup();

            var popup = new VisualElement();
            popup.AddToClassList("folder-name-input-popup");
            // Inline fallback styles (CSS may be template-scoped)
            popup.style.flexDirection = FlexDirection.Column;
            popup.style.backgroundColor = new Color(0.118f, 0.137f, 0.196f, 0.98f);
            popup.style.borderTopWidth = 1f;
            popup.style.borderRightWidth = 1f;
            popup.style.borderBottomWidth = 1f;
            popup.style.borderLeftWidth = 1f;
            popup.style.borderTopColor = new Color(0.392f, 0.471f, 0.784f, 0.3f);
            popup.style.borderRightColor = new Color(0.392f, 0.471f, 0.784f, 0.3f);
            popup.style.borderBottomColor = new Color(0.392f, 0.471f, 0.784f, 0.3f);
            popup.style.borderLeftColor = new Color(0.392f, 0.471f, 0.784f, 0.3f);
            popup.style.borderTopLeftRadius = 8f;
            popup.style.borderTopRightRadius = 8f;
            popup.style.borderBottomLeftRadius = 8f;
            popup.style.borderBottomRightRadius = 8f;
            popup.style.paddingTop = 10f;
            popup.style.paddingBottom = 10f;
            popup.style.paddingLeft = 10f;
            popup.style.paddingRight = 10f;
            popup.style.position = Position.Absolute;

            var titleLabel = new Label(LocalizationExtensions.Get("session.folder.input_title", "Название папки"));
            titleLabel.AddToClassList("folder-name-input-popup__title");
            titleLabel.style.fontSize = 12f;
            titleLabel.style.color = new Color(0.62f, 0.66f, 0.78f, 1f);
            titleLabel.style.marginBottom = 6f;

            var input = new TextField();
            input.AddToClassList("folder-name-input-popup__input");
            input.style.marginBottom = 8f;

            var btnRow = new VisualElement();
            btnRow.style.flexDirection = FlexDirection.Row;
            btnRow.style.justifyContent = Justify.FlexEnd;

            var cancelBtn = new Button();
            cancelBtn.text = LocalizationExtensions.Get("session.folder.cancel", "Отмена");
            cancelBtn.style.marginRight = 4f;
            cancelBtn.RegisterCallback<ClickEvent>(evt =>
            {
                evt.StopPropagation();
                HideFolderInputPopup();
            });

            var capturedSession = session;
            var createBtn = new Button();
            createBtn.text = LocalizationExtensions.Get("session.folder.create", "Создать");
            createBtn.RegisterCallback<ClickEvent>(evt =>
            {
                evt.StopPropagation();
                string name = (input.value ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(name))
                    return;
                HideFolderInputPopup();
                _ = AssignSessionToFolderAsync(capturedSession, name);
            });

            btnRow.Add(cancelBtn);
            btnRow.Add(createBtn);

            popup.Add(titleLabel);
            popup.Add(input);
            popup.Add(btnRow);

            var root = GetDocumentRoot(target);
            if (root == null)
                return;

            _folderInputPopup = popup;
            _folderInputRoot = root;

            // Convert position to root-local coordinates
            Vector2 localPos = root.WorldToLocal(position);
            float left = localPos.x + 8f;
            float top = localPos.y + 4f;
            if (left < 0f) left = 0f;
            if (top < 0f) top = 0f;
            float maxLeft = root.layout.width - 220f;
            float maxTop = root.layout.height - 160f;
            if (maxLeft < 0f) maxLeft = 0f;
            if (maxTop < 0f) maxTop = 0f;
            if (left > maxLeft) left = maxLeft;
            if (top > maxTop) top = maxTop;

            popup.style.left = left;
            popup.style.top = top;
            popup.style.minWidth = 200f;
            popup.style.display = DisplayStyle.Flex;
            popup.style.visibility = Visibility.Visible;

            root.Add(popup);
            popup.BringToFront();

            _folderInputOutsideHandler = OnFolderInputOutside;
            root.RegisterCallback(_folderInputOutsideHandler, TrickleDown.TrickleDown);

            popup.schedule.Execute(() =>
            {
                if (input != null)
                    input.Focus();
            }).StartingIn(20);

            input.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                {
                    string name = (input.value ?? string.Empty).Trim();
                    if (!string.IsNullOrEmpty(name))
                    {
                        HideFolderInputPopup();
                        _ = AssignSessionToFolderAsync(capturedSession, name);
                    }
                }
                else if (evt.keyCode == KeyCode.Escape)
                {
                    HideFolderInputPopup();
                }
            });
        }

        private void OnFolderInputOutside(PointerDownEvent evt)
        {
            if (_folderInputPopup != null && _folderInputPopup.worldBound.Contains(evt.position))
                return;
            HideFolderInputPopup();
        }

        private void HideFolderInputPopup()
        {
            if (_folderInputPopup != null && _folderInputPopup.parent != null)
                _folderInputPopup.RemoveFromHierarchy();

            if (_folderInputRoot != null && _folderInputOutsideHandler != null)
                _folderInputRoot.UnregisterCallback(_folderInputOutsideHandler, TrickleDown.TrickleDown);

            _folderInputPopup = null;
            _folderInputRoot = null;
            _folderInputOutsideHandler = null;
        }

        private VisualElement GetDocumentRoot(VisualElement start)
        {
            if (start == null)
                return null;

            // Return the panel visual tree directly (same as MessageContextMenu.GetDocumentRoot)
            // so that overlays added here are siblings of everything else and BringToFront works
            var panelVisualTree = start.panel != null ? start.panel.visualTree : null;
            if (panelVisualTree != null)
                return panelVisualTree;

            var el = start;
            while (el.parent != null)
                el = el.parent;
            return el;
        }

        private async Task AssignSessionToFolderAsync(ChatSession session, string folderName)
        {
            if (session == null)
                return;

            try
            {
                var chat = await _d.GetChatServiceAsync();
                if (chat == null)
                    return;

                await chat.SetSessionFolderAsync(session.sessionId, folderName);
                await RefreshSessionsFromCacheAsync();
            }
            catch (Exception ex)
            {
                NeonLogger.LogError(ex.ToString());
                if (_d.ShowSystemMessage != null)
                    _d.ShowSystemMessage(LocalizationExtensions.Get("session.folder.move_failed", "Failed to move session to folder."));
            }
        }
    }
}
