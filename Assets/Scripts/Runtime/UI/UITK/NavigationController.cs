using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Localization;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK
{
    internal sealed class NavigationControllerDeps
    {
        public Func<bool> CanLeaveProviderEditor;
        public Action<string, string> SetTopbar;
        public Action<VisualElement> ShowArea;
        public Action RefreshProvidersListAsync;
        public Func<Task> RefreshSessionsFromCacheAsync;
        public Func<string> GetChatTitle;
        public Func<string> GetChatSubtitle;
        public Func<string, string> AvatarDisplayName;
        public Func<int> GetAvatarTotalCount;
        public Func<string> GetActiveAvatarId;
        public Func<string> GetSessionSearchQuery;
        public VisualElement ChatPanel;
        public VisualElement HistoryPanel;
        public VisualElement ProvidersPanel;
        public VisualElement AvatarsPanel;
        public VisualElement ThemesPanel;
        public VisualElement SettingsPanel;
    }

    internal sealed class NavigationController
    {
        private const string ActiveNavClass = "nav__item--active";

        private readonly List<VisualElement> _navItems = new List<VisualElement>();

        private VisualElement _navChat;
        private VisualElement _navAvatars;
        private VisualElement _navProviders;
        private VisualElement _navHistory;
        private VisualElement _navThemes;
        private VisualElement _navSettings;
        private VisualElement _providerTag;

        private Label _navChatLabel;
        private Label _navAvatarsLabel;
        private Label _navProvidersLabel;
        private Label _navHistoryLabel;
        private Label _navThemesLabel;
        private Label _navSettingsLabel;
        private Label _navChatCount;
        private Label _navProvidersCount;
        private Label _navAvatarsCount;
        private Label _navCloseLabel;

        private NavigationControllerDeps _deps;

        public void Init(VisualElement root, NavigationControllerDeps deps)
        {
            if (root == null)
                return;

            _deps = deps;
            _navItems.Clear();

            _navChat = root.Q<VisualElement>("nav-chat");
            _navAvatars = root.Q<VisualElement>("nav-avatars");
            _navProviders = root.Q<VisualElement>("nav-providers");
            _navHistory = root.Q<VisualElement>("nav-history");
            _navThemes = root.Q<VisualElement>("nav-themes");
            _navSettings = root.Q<VisualElement>("nav-settings");
            _providerTag = root.Q<VisualElement>("provider-tag");

            AddNav(_navChat);
            AddNav(_navAvatars);
            AddNav(_navProviders);
            AddNav(_navHistory);
            AddNav(_navThemes);
            AddNav(_navSettings);

            _navChatLabel = root.Q<Label>("nav-chat-label");
            _navAvatarsLabel = root.Q<Label>("nav-avatars-label");
            _navProvidersLabel = root.Q<Label>("nav-providers-label");
            _navHistoryLabel = root.Q<Label>("nav-history-label");
            _navThemesLabel = root.Q<Label>("nav-themes-label");
            _navSettingsLabel = root.Q<Label>("nav-settings-label");
            _navChatCount = root.Q<Label>("nav-chat-count");
            _navProvidersCount = root.Q<Label>("nav-providers-count");
            _navAvatarsCount = _navAvatars != null ? _navAvatars.Q<Label>(className: "nav__count") : null;
            _navCloseLabel = root.Q<Label>("nav-close-label");

            RegisterNavCallbacks();
        }

        public void UnregisterCallbacks()
        {
            UnregisterClick(_navChat, OnNavChatClicked);
            UnregisterClick(_navAvatars, OnNavAvatarsClicked);
            UnregisterClick(_navProviders, OnNavProvidersClicked);
            UnregisterClick(_navHistory, OnNavHistoryClicked);
            UnregisterClick(_navThemes, OnNavThemesClicked);
            UnregisterClick(_navSettings, OnNavSettingsClicked);
            UnregisterClick(_providerTag, OnProviderTagClicked);
        }

        private void AddNav(VisualElement navItem)
        {
            if (navItem != null)
                _navItems.Add(navItem);
        }

        private void RegisterNavCallbacks()
        {
            RegisterClick(_navChat, OnNavChatClicked);
            RegisterClick(_navAvatars, OnNavAvatarsClicked);
            RegisterClick(_navProviders, OnNavProvidersClicked);
            RegisterClick(_navHistory, OnNavHistoryClicked);
            RegisterClick(_navThemes, OnNavThemesClicked);
            RegisterClick(_navSettings, OnNavSettingsClicked);
            RegisterClick(_providerTag, OnProviderTagClicked);
        }

        private void OnNavChatClicked(ClickEvent evt) => ShowChat();
        private void OnNavAvatarsClicked(ClickEvent evt) => ShowAvatars();
        private void OnNavProvidersClicked(ClickEvent evt) => ShowProviders();
        private void OnNavHistoryClicked(ClickEvent evt) => ShowHistory();
        private void OnNavThemesClicked(ClickEvent evt) => ShowThemes();
        private void OnNavSettingsClicked(ClickEvent evt) => ShowSettings();
        private void OnProviderTagClicked(ClickEvent evt) => ShowProviders();

        public void ShowChat()
        {
            if (_deps != null && _deps.CanLeaveProviderEditor != null && !_deps.CanLeaveProviderEditor())
                return;

            SetActiveNav(_navChat);

            string title = _deps != null && _deps.GetChatTitle != null ? _deps.GetChatTitle() : string.Empty;
            string subtitle = _deps != null && _deps.GetChatSubtitle != null ? _deps.GetChatSubtitle() : string.Empty;

            if (_deps != null && _deps.SetTopbar != null)
                _deps.SetTopbar(title, subtitle);

            if (_deps != null && _deps.ShowArea != null && _deps.ChatPanel != null)
                _deps.ShowArea(_deps.ChatPanel);
        }

        public void ShowAvatars()
        {
            if (_deps != null && _deps.CanLeaveProviderEditor != null && !_deps.CanLeaveProviderEditor())
                return;

            SetActiveNav(_navAvatars);

            int total = _deps != null && _deps.GetAvatarTotalCount != null ? _deps.GetAvatarTotalCount() : 0;
            string activeId = _deps != null && _deps.GetActiveAvatarId != null ? _deps.GetActiveAvatarId() : "neon";
            string displayName = _deps != null && _deps.AvatarDisplayName != null ? _deps.AvatarDisplayName(activeId) : string.Empty;

            if (_deps != null && _deps.SetTopbar != null)
            {
                _deps.SetTopbar(
                    LocalizationExtensions.Get("topbar.avatars.title", "Аватары"),
                    LocalizationExtensions.GetFormat("topbar.avatars.subtitle", "{0} образов · {1}", total, displayName));
            }

            if (_deps != null && _deps.ShowArea != null && _deps.AvatarsPanel != null)
                _deps.ShowArea(_deps.AvatarsPanel);
        }

        public void ShowProviders()
        {
            SetActiveNav(_navProviders);

            if (_deps != null && _deps.SetTopbar != null)
            {
                _deps.SetTopbar(
                    LocalizationExtensions.Get("topbar.providers.title", "Провайдеры"),
                    LocalizationExtensions.Get("topbar.providers.subtitle", "OpenAI-совместимые провайдеры"));
            }

            if (_deps != null && _deps.ShowArea != null && _deps.ProvidersPanel != null)
                _deps.ShowArea(_deps.ProvidersPanel);

            if (_deps != null && _deps.RefreshProvidersListAsync != null)
                _deps.RefreshProvidersListAsync();
        }

        public void ShowHistory()
        {
            if (_deps != null && _deps.CanLeaveProviderEditor != null && !_deps.CanLeaveProviderEditor())
                return;

            SetActiveNav(_navHistory);

            string sub;
            string query = _deps != null && _deps.GetSessionSearchQuery != null ? _deps.GetSessionSearchQuery() : string.Empty;
            if (string.IsNullOrWhiteSpace(query))
            {
                sub = LocalizationExtensions.Get("topbar.history.subtitle.saved", "Сохранённые сессии");
            }
            else
            {
                sub = LocalizationExtensions.GetFormat("topbar.history.subtitle.search", "Поиск: {0}", query);
            }

            if (_deps != null && _deps.SetTopbar != null)
            {
                _deps.SetTopbar(
                    LocalizationExtensions.Get("topbar.history.title", "История чатов"),
                    sub);
            }

            if (_deps != null && _deps.ShowArea != null && _deps.HistoryPanel != null)
                _deps.ShowArea(_deps.HistoryPanel);

            if (_deps != null && _deps.RefreshSessionsFromCacheAsync != null)
                _ = _deps.RefreshSessionsFromCacheAsync();
        }

        public void ShowThemes()
        {
            if (_deps != null && _deps.CanLeaveProviderEditor != null && !_deps.CanLeaveProviderEditor())
                return;

            SetActiveNav(_navThemes);

            if (_deps != null && _deps.SetTopbar != null)
            {
                _deps.SetTopbar(
                    LocalizationExtensions.Get("topbar.themes.title", "Темы"),
                    LocalizationExtensions.Get("topbar.themes.subtitle", "Форма, ореол и дыхание для аватара"));
            }

            if (_deps != null && _deps.ShowArea != null && _deps.ThemesPanel != null)
                _deps.ShowArea(_deps.ThemesPanel);
        }

        public void ShowSettings()
        {
            if (_deps != null && _deps.CanLeaveProviderEditor != null && !_deps.CanLeaveProviderEditor())
                return;

            SetActiveNav(_navSettings);

            if (_deps != null && _deps.SetTopbar != null)
            {
                _deps.SetTopbar(
                    LocalizationExtensions.Get("topbar.settings.title", "Настройки"),
                    string.Empty);
            }

            if (_deps != null && _deps.ShowArea != null && _deps.SettingsPanel != null)
                _deps.ShowArea(_deps.SettingsPanel);
        }

        public void ApplyLocalizedTexts()
        {
            _navChatLabel?.Localize("tab.chat");
            _navAvatarsLabel?.Localize("tab.avatar");
            _navProvidersLabel?.Localize("settings.providers");
            _navHistoryLabel?.Localize("chat.history");
            _navThemesLabel?.Localize("settings.themes");
            _navSettingsLabel?.Localize("tab.settings");
            _navCloseLabel?.Localize("nav.close");
        }

        public void SetChatCount(int count)
        {
            if (_navChatCount != null)
                _navChatCount.text = count.ToString();
        }

        public void SetProvidersCount(int count)
        {
            if (_navProvidersCount != null)
                _navProvidersCount.text = count.ToString();
        }

        public void SetAvatarsCount(int count)
        {
            if (_navAvatarsCount != null)
                _navAvatarsCount.text = count.ToString();
        }

        public void ResetChatCount()
        {
            if (_navChatCount != null)
                _navChatCount.text = "0";
        }

        public void ResetProvidersCount()
        {
            if (_navProvidersCount != null)
                _navProvidersCount.text = "0";
        }

        private void SetActiveNav(VisualElement active)
        {
            foreach (var navItem in _navItems)
            {
                if (navItem != null)
                    navItem.EnableInClassList(ActiveNavClass, navItem == active);
            }
        }

        private static void RegisterClick(VisualElement element, EventCallback<ClickEvent> handler)
        {
            if (element != null)
                element.RegisterCallback<ClickEvent>(handler);
        }

        private static void UnregisterClick(VisualElement element, EventCallback<ClickEvent> handler)
        {
            if (element != null)
                element.UnregisterCallback<ClickEvent>(handler);
        }
    }
}
