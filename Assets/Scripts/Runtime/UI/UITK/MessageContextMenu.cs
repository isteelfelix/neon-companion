using System;
using UnityEngine;
using UnityEngine.UIElements;
using NeonCompanion.Runtime.Localization;

namespace NeonCompanion.Runtime.UI.UITK
{
    internal sealed class MessageContextMenu
    {
        public event Action<string> OnEditRequested;
        public event Action<string> OnDeleteRequested;
        public event Action<string> OnCopyRequested;
        public event Action<string> OnSelectRequested;

        private VisualElement _menuElement;
        private VisualElement _panelRoot;
        private EventCallback<PointerDownEvent> _outsideHandler;
        private IVisualElementScheduledItem _outsideRegistrationSchedule;
        private float _outsideIgnoreUntil;
        private string _currentIndexStr;

        public VisualElement Create(string messageIndex, bool isUser)
        {
            _menuElement = new VisualElement();
            _menuElement.AddToClassList("message-context-menu");
            // Inline fallback so menu remains visible even if stylesheet scope changes.
            _menuElement.style.position = Position.Absolute;
            _menuElement.style.backgroundColor = new Color(0.137f, 0.153f, 0.196f, 0.98f); // --bg-4
            var menuBorderColor = new Color(0.227f, 0.251f, 0.322f, 1f); // --line-3
            _menuElement.style.borderTopWidth = 1f;
            _menuElement.style.borderBottomWidth = 1f;
            _menuElement.style.borderLeftWidth = 1f;
            _menuElement.style.borderRightWidth = 1f;
            _menuElement.style.borderTopColor = menuBorderColor;
            _menuElement.style.borderBottomColor = menuBorderColor;
            _menuElement.style.borderLeftColor = menuBorderColor;
            _menuElement.style.borderRightColor = menuBorderColor;
            _menuElement.style.borderTopLeftRadius = 8f;
            _menuElement.style.borderTopRightRadius = 8f;
            _menuElement.style.borderBottomLeftRadius = 8f;
            _menuElement.style.borderBottomRightRadius = 8f;
            _menuElement.style.paddingTop = 4f;
            _menuElement.style.paddingBottom = 4f;
            _menuElement.style.paddingLeft = 4f;
            _menuElement.style.paddingRight = 4f;
            _menuElement.style.minWidth = 150f;

            if (isUser)
            {
                var editItem = CreateMenuItem("\u270F\uFE0F", LocalizationExtensions.Get("msg.context.edit", "Edit"), () =>
                {
                    string captured = _currentIndexStr;
                    Hide();
                    if (OnEditRequested != null)
                        OnEditRequested.Invoke(captured);
                });
                _menuElement.Add(editItem);
            }

            var deleteItem = CreateMenuItem("\uD83D\uDDD1\uFE0F", LocalizationExtensions.Get("msg.context.delete", "Delete"), () =>
            {
                string captured = _currentIndexStr;
                Hide();
                if (OnDeleteRequested != null)
                    OnDeleteRequested.Invoke(captured);
            });
            _menuElement.Add(deleteItem);

            var copyItem = CreateMenuItem("\uD83D\uDCCB", LocalizationExtensions.Get("msg.context.copy", "Copy"), () =>
            {
                string captured = _currentIndexStr;
                Hide();
                if (OnCopyRequested != null)
                    OnCopyRequested.Invoke(captured);
            });
            _menuElement.Add(copyItem);

            var selectItem = CreateMenuItem("\u2611\uFE0F", LocalizationExtensions.Get("msg.context.select", "Select"), () =>
            {
                string captured = _currentIndexStr;
                Hide();
                if (OnSelectRequested != null)
                    OnSelectRequested.Invoke(captured);
            });
            _menuElement.Add(selectItem);

            return _menuElement;
        }

        private VisualElement CreateMenuItem(string icon, string labelText, Action onClick)
        {
            var item = new VisualElement();
            item.AddToClassList("message-context-menu__item");
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
            textLabel.style.color = new Color(0.847f, 0.863f, 0.902f, 1f); // --text-1

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
                item.style.backgroundColor = ThemeColors.AccentSoft;
            });
            item.RegisterCallback<PointerLeaveEvent>(_ =>
            {
                item.RemoveFromClassList("message-context-menu__item--hover");
                item.style.backgroundColor = Color.clear;
            });

            return item;
        }

        public void ShowAt(VisualElement target, int messageIndex, bool isUser, Vector2 clickPosition)
        {
            if (target == null || target.panel == null)
                return;

            Hide();

            _currentIndexStr = messageIndex.ToString();
            Create(_currentIndexStr, isUser);

            if (_menuElement == null)
                return;

            var root = GetDocumentRoot(target);
            if (root == null)
                return;

            _panelRoot = root;

            // Position near the actual click (pointer) position for reliable near-message overlay.
            // Convert world/panel coordinates to root-local coordinates before assigning style.left/top.
            Vector2 localPos = root.WorldToLocal(clickPosition);
            float left = localPos.x + 8f;
            float top = localPos.y + 4f;

            // Simple clamp to avoid extreme offscreen
            if (left < 0f) left = 0f;
            if (top < 0f) top = 0f;

            float maxLeft = root.layout.width - 180f;
            float maxTop = root.layout.height - 160f;
            if (maxLeft < 0f) maxLeft = 0f;
            if (maxTop < 0f) maxTop = 0f;
            if (left > maxLeft) left = maxLeft;
            if (top > maxTop) top = maxTop;

            _menuElement.style.left = left;
            _menuElement.style.top = top;
            _menuElement.style.minWidth = 150f;
            _menuElement.style.display = DisplayStyle.Flex;
            _menuElement.style.visibility = Visibility.Visible;

            root.Add(_menuElement);
            _menuElement.BringToFront();

            // Ignore outside-pointer close right after open to avoid self-close
            // from the same physical right-click sequence.
            _outsideIgnoreUntil = Time.realtimeSinceStartup + 0.15f;

            _outsideHandler = OnOutsidePointerDown;
            if (_outsideRegistrationSchedule != null)
            {
                _outsideRegistrationSchedule.Pause();
                _outsideRegistrationSchedule = null;
            }

            // Delay outside-handler binding by one UI tick so the opening click
            // cannot be interpreted as an immediate outside click.
            _outsideRegistrationSchedule = root.schedule.Execute(() =>
            {
                if (_panelRoot != null && _outsideHandler != null)
                    _panelRoot.RegisterCallback(_outsideHandler, TrickleDown.TrickleDown);
                _outsideRegistrationSchedule = null;
            }).StartingIn(16);
        }

        private void OnOutsidePointerDown(PointerDownEvent evt)
        {
            if (Time.realtimeSinceStartup < _outsideIgnoreUntil)
                return;

            if (_menuElement != null && _menuElement.worldBound.Contains(evt.position))
                return;
            Hide();
        }

        private VisualElement GetDocumentRoot(VisualElement start)
        {
            if (start == null)
                return null;

            var panelVisualTree = start.panel != null ? start.panel.visualTree : null;
            if (panelVisualTree != null)
                return panelVisualTree;

            var el = start;
            while (el.parent != null && el.parent != panelVisualTree)
                el = el.parent;
            return el;
        }

        public void Hide()
        {
            if (_menuElement != null && _menuElement.parent != null)
                _menuElement.RemoveFromHierarchy();

            CleanupHandlers();
            _menuElement = null;
            _currentIndexStr = null;
        }

        private void CleanupHandlers()
        {
            if (_outsideRegistrationSchedule != null)
            {
                _outsideRegistrationSchedule.Pause();
                _outsideRegistrationSchedule = null;
            }

            if (_panelRoot != null && _outsideHandler != null)
                _panelRoot.UnregisterCallback(_outsideHandler, TrickleDown.TrickleDown);

            _outsideHandler = null;
            _panelRoot = null;
            _outsideIgnoreUntil = 0f;
        }
    }
}
