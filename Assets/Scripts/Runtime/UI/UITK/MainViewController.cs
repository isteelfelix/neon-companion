using UnityEngine;
using UnityEngine.UIElements;
using NeonCompanion.Runtime.Localization;

namespace NeonCompanion.Runtime.UI.UITK
{
    [RequireComponent(typeof(UIDocument))]
    public sealed class MainViewController : MonoBehaviour
    {
        private static readonly string[] NavItems = { "nav-chat", "nav-avatar", "nav-providers", "nav-history", "nav-themes", "nav-settings" };

        private VisualElement _root;
        private string _activeNav = "nav-chat";

        private void OnEnable()
        {
            var document = GetComponent<UIDocument>();
            if (document == null || document.rootVisualElement == null) return;
            _root = document.rootVisualElement;

            _chatPanel = root.Q<VisualElement>("chat-panel");
            _settingsPanel = root.Q<VisualElement>("settings-panel");
            _avatarPanel = root.Q<VisualElement>("avatar-panel");

            _chatTab = root.Q<Button>("tab-chat");
            _settingsTab = root.Q<Button>("tab-settings");
            _avatarTab = root.Q<Button>("tab-avatar");

            // Apply localization
            _chatTab?.Localize("tab.chat");
            _settingsTab?.Localize("tab.settings");
            _avatarTab?.Localize("tab.avatar");

            // Localize panel titles
            LocalizePanelTitle(root, "chat-panel", "panel.chat.title");
            LocalizePanelTitle(root, "settings-panel", "panel.settings.title");
            LocalizePanelTitle(root, "avatar-panel", "panel.avatar.title");

            if (_chatTab != null)
            {
                _chatTab.clicked += ShowChat;
            }

            if (_settingsTab != null)
            {
                _settingsTab.clicked += ShowSettings;
            }

            if (_avatarTab != null)
            {
                _avatarTab.clicked += ShowAvatar;
            }

            ShowChat();
        }

        private void LocalizePanelTitle(VisualElement root, string panelName, string key)
        {
            var panel = root.Q<VisualElement>(panelName);
            var titleLabel = panel?.Q<Label>(className: "panel-title");
            titleLabel?.Localize(key);
        }

        private void OnDisable()
        {
            if (_chatTab != null)
            {
                _chatTab.clicked -= ShowChat;
            }

            if (_settingsTab != null)
            {
                _settingsTab.clicked -= ShowSettings;
            }

            if (_avatarTab != null)
            {
                foreach (var item in historyList.Query<VisualElement>(className: "history__item").ToList())
                {
                    var captured = item;
                    item.RegisterCallback<ClickEvent>(_ => SetActiveHistory(historyList, captured));
                }
            }

            SetActiveNav(_activeNav);
        }

        private void SetActiveNav(string id)
        {
            _activeNav = id;
            foreach (var n in NavItems)
            {
                var el = _root.Q<VisualElement>(n);
                if (el == null) continue;
                el.EnableInClassList("nav__item--active", n == id);
            }
        }

        private static void SetActiveHistory(ScrollView list, VisualElement selected)
        {
            foreach (var item in list.Query<VisualElement>(className: "history__item").ToList())
            {
                item.EnableInClassList("history__item--active", item == selected);
            }
        }
    }
}