using UnityEngine;
using UnityEngine.UIElements;
using NeonCompanion.Runtime.Localization;

namespace NeonCompanion.Runtime.UI.UITK
{
    [RequireComponent(typeof(UIDocument))]
    public sealed class MainViewController : MonoBehaviour
    {
        private VisualElement _chatPanel;
        private VisualElement _settingsPanel;
        private VisualElement _avatarPanel;

        private Button _chatTab;
        private Button _settingsTab;
        private Button _avatarTab;

        private void OnEnable()
        {
            var document = GetComponent<UIDocument>();
            if (document == null || document.rootVisualElement == null)
            {
                return;
            }

            var root = document.rootVisualElement;

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
                _avatarTab.clicked -= ShowAvatar;
            }
        }

        private void ShowChat()
        {
            SetPanelState(chat: true, settings: false, avatar: false);
            SetTabState(chatActive: true, settingsActive: false, avatarActive: false);
        }

        private void ShowSettings()
        {
            SetPanelState(chat: false, settings: true, avatar: false);
            SetTabState(chatActive: false, settingsActive: true, avatarActive: false);
        }

        private void ShowAvatar()
        {
            SetPanelState(chat: false, settings: false, avatar: true);
            SetTabState(chatActive: false, settingsActive: false, avatarActive: true);
        }

        private void SetPanelState(bool chat, bool settings, bool avatar)
        {
            SetDisplay(_chatPanel, chat);
            SetDisplay(_settingsPanel, settings);
            SetDisplay(_avatarPanel, avatar);
        }

        private void SetTabState(bool chatActive, bool settingsActive, bool avatarActive)
        {
            SetActiveClass(_chatTab, chatActive);
            SetActiveClass(_settingsTab, settingsActive);
            SetActiveClass(_avatarTab, avatarActive);
        }

        private static void SetDisplay(VisualElement element, bool visible)
        {
            if (element == null)
            {
                return;
            }

            element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private static void SetActiveClass(VisualElement element, bool active)
        {
            if (element == null)
            {
                return;
            }

            const string className = "tab-btn--active";
            if (active)
            {
                element.AddToClassList(className);
            }
            else
            {
                element.RemoveFromClassList(className);
            }
        }
    }
}