using System;
using NeonCompanion.Runtime.Localization;
using NeonCompanion.Runtime.Platform;
using UnityEngine;
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

            /// <summary>
            /// Опционально. Если передан — будет применён Safe Area и платформенные классы.
            /// </summary>
            public IPlatformInfoService PlatformInfo;
        }

        // Брейкпоинты адаптива десктоп-окна (физические px ширины root).
        private const float CompactWidth = 1000f;
        private const float NarrowWidth = 820f;
        private const float AvatarHideWidth = 900f;

        private Deps _d;
        private Button _toggleLeftPanelBtn;
        private Button _toggleRightPanelBtn;
        private bool _leftPanelVisible = true;
        private bool _rightPanelVisible = true;
        private bool _avatarAutoHidden;

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

            ApplyPlatformLayout();
        }

        public void RegisterCallbacks()
        {
            RegisterClick(_toggleLeftPanelBtn, OnToggleLeftPanel);
            RegisterClick(_toggleRightPanelBtn, OnToggleRightPanel);

            if (_d.Root != null)
                _d.Root.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);

            if (_d.PanelResizeHandler != null)
                _d.PanelResizeHandler.RegisterCallbacks();
        }

        public void UnregisterCallbacks()
        {
            UnregisterClick(_toggleLeftPanelBtn, OnToggleLeftPanel);
            UnregisterClick(_toggleRightPanelBtn, OnToggleRightPanel);

            if (_d.Root != null)
                _d.Root.UnregisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);

            if (_d.PanelResizeHandler != null)
                _d.PanelResizeHandler.UnregisterCallbacks();
        }

        /// <summary>
        /// Адаптив десктоп-окна: при сужении переключаем CSS-классы брейкпоинтов
        /// и авто-скрываем аватар-панель, освобождая место под чат. Ширину рейла/
        /// панели не трогаем (её пишет PanelResizeHandler inline-стилем) — работаем
        /// через display и классы, чтобы не конфликтовать.
        /// </summary>
        private void OnRootGeometryChanged(GeometryChangedEvent evt)
        {
            if (_d.Root == null)
                return;

            float width = evt.newRect.width;
            _d.Root.EnableInClassList("app--compact", width < CompactWidth);
            _d.Root.EnableInClassList("app--narrow", width < NarrowWidth);

            UpdateAvatarAutoHide(width);

            // Поджать рейл/аватар под новую ширину окна (если их растянули раньше).
            if (_d.PanelResizeHandler != null)
                _d.PanelResizeHandler.ClampToWindow(width);
        }

        private void UpdateAvatarAutoHide(float width)
        {
            bool shouldAutoHide = width < AvatarHideWidth;

            if (shouldAutoHide && !_avatarAutoHidden)
            {
                _avatarAutoHidden = true;
                if (_rightPanelVisible)
                {
                    SetDisplay(_d.AvatarPanel, DisplayStyle.None);
                    SetDisplay(_d.ResizeHandle, DisplayStyle.None);
                }
            }
            else if (!shouldAutoHide && _avatarAutoHidden)
            {
                _avatarAutoHidden = false;
                SetDisplay(_d.AvatarPanel, _rightPanelVisible ? DisplayStyle.Flex : DisplayStyle.None);
                SetDisplay(_d.ResizeHandle, _rightPanelVisible ? DisplayStyle.Flex : DisplayStyle.None);
            }
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
            // Ручное переключение перехватывает контроль у авто-скрытия.
            _avatarAutoHidden = false;
            SetDisplay(_d.AvatarPanel, _rightPanelVisible ? DisplayStyle.Flex : DisplayStyle.None);
            SetDisplay(_d.ResizeHandle, _rightPanelVisible ? DisplayStyle.Flex : DisplayStyle.None);
            UpdatePanelToggleTooltips();
        }

        public void UpdatePanelToggleTooltips()
        {
            if (_toggleLeftPanelBtn != null)
            {
                _toggleLeftPanelBtn.tooltip = _leftPanelVisible
                    ? LocalizationExtensions.Get("tooltip.panel.left.hide", "Скрыть панель сессий")
                    : LocalizationExtensions.Get("tooltip.panel.left.show", "Показать панель сессий");
            }

            if (_toggleRightPanelBtn != null)
            {
                _toggleRightPanelBtn.tooltip = _rightPanelVisible
                    ? LocalizationExtensions.Get("tooltip.panel.right.hide", "Скрыть панель настроек")
                    : LocalizationExtensions.Get("tooltip.panel.right.show", "Показать панель настроек");
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

        /// <summary>
        /// Применяет Safe Area и платформенные классы к root.
        /// Вызывается автоматически в Init(), если PlatformInfo передан в Deps.
        /// </summary>
        public void ApplyPlatformLayout()
        {
            if (_d.Root == null || _d.PlatformInfo == null)
                return;

            var info = _d.PlatformInfo;
            var safeArea = info.SafeArea;

            // Применяем Safe Area как padding к корневому элементу
            // На десктопе SafeArea = полный экран, padding будет 0
            _d.Root.style.paddingLeft = safeArea.x;
            _d.Root.style.paddingRight = Screen.width - safeArea.xMax;
            _d.Root.style.paddingTop = Screen.height - safeArea.yMax;
            _d.Root.style.paddingBottom = safeArea.y;

            // Добавляем полезные классы для USS-адаптации
            if (info.IsMobile)
            {
                _d.Root.AddToClassList("platform-mobile");
            }

            if (Application.platform == RuntimePlatform.Android)
            {
                _d.Root.AddToClassList("platform-android");
            }
            else if (Application.platform == RuntimePlatform.IPhonePlayer)
            {
                _d.Root.AddToClassList("platform-ios");
            }
        }

        /// <summary>
        /// Применяет Safe Area и платформенные классы.
        /// Можно вызывать повторно, если сервис стал доступен позже (после инициализации).
        /// </summary>
        public void ApplyPlatformLayout(IPlatformInfoService info)
        {
            if (_d.Root == null || info == null)
                return;

            _d.PlatformInfo = info;
            ApplyPlatformLayout();
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
