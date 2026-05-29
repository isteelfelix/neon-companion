using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK
{
    internal sealed class MessageContextMenu
    {
        public event Action<string> OnEditRequested;
        public event Action<string> OnDeleteRequested;
        public event Action<string> OnCopyRequested;

        private VisualElement _menuElement;
        private VisualElement _panelRoot;
        private EventCallback<PointerDownEvent> _outsideHandler;
        private string _currentIndexStr;

        public VisualElement Create(string messageIndex, bool isUser)
        {
            _menuElement = new VisualElement();
            _menuElement.AddToClassList("message-context-menu");
            _menuElement.style.position = Position.Absolute;
            _menuElement.style.zIndex = 100;

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

            return _menuElement;
        }

        private VisualElement CreateMenuItem(string icon, string labelText, Action onClick)
        {
            var item = new VisualElement();
            item.AddToClassList("message-context-menu__item");

            var iconLabel = new Label(icon);
            iconLabel.AddToClassList("message-context-menu__icon");

            var textLabel = new Label(labelText);
            textLabel.AddToClassList("message-context-menu__label");

            item.Add(iconLabel);
            item.Add(textLabel);

            item.RegisterCallback<ClickEvent>(evt =>
            {
                evt.StopPropagation();
                if (onClick != null)
                    onClick.Invoke();
            });

            item.RegisterCallback<PointerEnterEvent>(_ => item.AddToClassList("message-context-menu__item--hover"));
            item.RegisterCallback<PointerLeaveEvent>(_ => item.RemoveFromClassList("message-context-menu__item--hover"));

            return item;
        }

        public void ShowAt(VisualElement target, int messageIndex, bool isUser)
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

            var bounds = target.worldBound;
            float left = bounds.xMax + 6f;
            float top = bounds.yMin;

            // Simple clamp to avoid extreme offscreen (no screen size query here)
            if (left < 0f) left = 0f;
            if (top < 0f) top = 0f;

            _menuElement.style.left = left;
            _menuElement.style.top = top;
            _menuElement.style.minWidth = 150f;

            root.Add(_menuElement);

            _outsideHandler = OnOutsidePointerDown;
            root.RegisterCallback(_outsideHandler, TrickleDown.TrickleDown);
        }

        private void OnOutsidePointerDown(PointerDownEvent evt)
        {
            if (_menuElement != null && _menuElement.worldBound.Contains(evt.position))
                return;
            Hide();
        }

        private VisualElement GetDocumentRoot(VisualElement start)
        {
            if (start == null)
                return null;

            var el = start;
            var panelVisualTree = start.panel != null ? start.panel.visualTree : null;
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
            if (_panelRoot != null && _outsideHandler != null)
                _panelRoot.UnregisterCallback(_outsideHandler, TrickleDown.TrickleDown);

            _outsideHandler = null;
            _panelRoot = null;
        }
    }
}
