using System;
using NeonCompanion.Runtime.Localization;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK
{
    internal sealed class LayoutController
    {
        public struct Deps
        {
            public VisualElement Root;
            public VisualElement RailElement;
            public VisualElement RailResizeHandle;
            public VisualElement AvatarPanel;
            public VisualElement ResizeHandle;
            public VisualElement ChatPanel;
            public VisualElement HistoryPanel;
            public VisualElement ProvidersPanel;
            public VisualElement AvatarsPanel;
            public VisualElement ThemesPanel;
            public VisualElement PlaceholderArea;
            public VisualElement SettingsPanel;
            public PanelResizeHandler PanelResizeHandler;
        }

        private Deps _d;
        private Button _toggleLeftPanelBtn;
        private Button _toggleRightPanelBtn;
        private bool _leftPanelVisible = true;
        private bool _rightPanelVisible = true;

        public bool LeftPanelVisible => _leftPanelVisible;
        public bool RightPanelVisible => _rightPanelVisible;

        public void SetDeps(Deps deps)
        {
            _d = deps;
        }

        public void Init()
        {
            if (_d.Root == null)
                return;

            _toggleLeftPanelBtn = _d.Root.Q<Button>("toggle-left-panel-btn");
            _toggleRightPanelBtn = _d.Root.Q<Button>("toggle-right-panel-btn");

            if (_d.PanelResizeHandler != null)
                _d.PanelResizeHandler.Init(_d.ResizeHandle, _d.AvatarPanel, _d.RailResizeHandle, _d.RailElement);

            UpdatePanelToggleTooltips();
        }

        public void RegisterCallbacks()
        {
            RegisterClick(_toggleLeftPanelBtn, OnToggleLeftPanel);
            RegisterClick(_toggleRightPanelBtn, OnToggleRightPanel);

            if (_d.PanelResizeHandler != null)
                _d.PanelResizeHandler.RegisterCallbacks();
        }

        public void UnregisterCallbacks()
        {
            UnregisterClick(_toggleLeftPanelBtn, OnToggleLeftPanel);
            UnregisterClick(_toggleRightPanelBtn, OnToggleRightPanel);

            if (_d.PanelResizeHandler != null)
                _d.PanelResizeHandler.UnregisterCallbacks();
        }

        public void OnDisable()
        {
        }

        public void OnToggleLeftPanel()
        {
            _leftPanelVisible = !_leftPanelVisible;
            SetDisplay(_d.RailElement, _leftPanelVisible ? DisplayStyle.Flex : DisplayStyle.None);
            SetDisplay(_d.RailResizeHandle, _leftPanelVisible ? DisplayStyle.Flex : DisplayStyle.None);
            UpdatePanelToggleTooltips();
        }

        public void OnToggleRightPanel()
        {
            _rightPanelVisible = !_rightPanelVisible;
            SetDisplay(_d.AvatarPanel, _rightPanelVisible ? DisplayStyle.Flex : DisplayStyle.None);
            SetDisplay(_d.ResizeHandle, _rightPanelVisible ? DisplayStyle.Flex : DisplayStyle.None);
            UpdatePanelToggleTooltips();
        }

        public void UpdatePanelToggleTooltips()
        {
            if (_toggleLeftPanelBtn != null)
            {
                _toggleLeftPanelBtn.tooltip = _leftPanelVisible
                    ? LocalizationExtensions.Get("tooltip.panel.left.hide", "\u0421\u043a\u0440\u044b\u0442\u044c \u043f\u0430\u043d\u0435\u043b\u044c \u0441\u0435\u0441\u0441\u0438\u0439")
                    : LocalizationExtensions.Get("tooltip.panel.left.show", "\u041f\u043e\u043a\u0430\u0437\u0430\u0442\u044c \u043f\u0430\u043d\u0435\u043b\u044c \u0441\u0435\u0441\u0441\u0438\u0439");
            }

            if (_toggleRightPanelBtn != null)
            {
                _toggleRightPanelBtn.tooltip = _rightPanelVisible
                    ? LocalizationExtensions.Get("tooltip.panel.right.hide", "\u0421\u043a\u0440\u044b\u0442\u044c \u043f\u0430\u043d\u0435\u043b\u044c \u043d\u0430\u0441\u0442\u0440\u043e\u0435\u043a")
                    : LocalizationExtensions.Get("tooltip.panel.right.show", "\u041f\u043e\u043a\u0430\u0437\u0430\u0442\u044c \u043f\u0430\u043d\u0435\u043b\u044c \u043d\u0430\u0441\u0442\u0440\u043e\u0435\u043a");
            }
        }

        public void ShowArea(VisualElement visible)
        {
            SetDisplay(_d.ChatPanel, visible == _d.ChatPanel ? DisplayStyle.Flex : DisplayStyle.None);
            SetDisplay(_d.HistoryPanel, visible == _d.HistoryPanel ? DisplayStyle.Flex : DisplayStyle.None);
            SetDisplay(_d.ProvidersPanel, visible == _d.ProvidersPanel ? DisplayStyle.Flex : DisplayStyle.None);
            SetDisplay(_d.AvatarsPanel, visible == _d.AvatarsPanel ? DisplayStyle.Flex : DisplayStyle.None);
            SetDisplay(_d.ThemesPanel, visible == _d.ThemesPanel ? DisplayStyle.Flex : DisplayStyle.None);
            SetDisplay(_d.PlaceholderArea, visible == _d.PlaceholderArea ? DisplayStyle.Flex : DisplayStyle.None);
            SetDisplay(_d.SettingsPanel, visible == _d.SettingsPanel ? DisplayStyle.Flex : DisplayStyle.None);
        }

        private static void SetDisplay(VisualElement element, DisplayStyle display)
        {
            if (element != null)
                element.style.display = display;
        }

        private static void RegisterClick(Button button, Action handler)
        {
            if (button != null)
                button.clicked += handler;
        }

        private static void UnregisterClick(Button button, Action handler)
        {
            if (button != null)
                button.clicked -= handler;
        }
    }
}
