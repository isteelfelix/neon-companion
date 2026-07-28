using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK
{
    internal sealed class PanelResizeHandler
    {
        private const float MinAvatarWidth = 180f;
        private const float MaxAvatarWidth = 520f;
        private const float MinRailWidth = 160f;
        private const float MaxRailWidth = 400f;

        // Доли от ширины окна — потолок, чтобы панель не «съедала» весь экран
        // на узком окне. Эффективный максимум = min(px-предел, доля·ширина окна).
        private const float MaxRailPct = 0.33f;
        private const float MaxAvatarPct = 0.5f;

        private VisualElement _resizeHandle;
        private VisualElement _avatarPanel;
        private VisualElement _railResizeHandle;
        private VisualElement _railElement;

        private bool _isResizing;
        private float _resizeStartX;
        private float _resizeStartWidth;

        private bool _isRailResizing;
        private float _railResizeStartX;
        private float _railResizeStartWidth;

        internal void Init(
            VisualElement resizeHandle,
            VisualElement avatarPanel,
            VisualElement railResizeHandle,
            VisualElement railElement)
        {
            _resizeHandle = resizeHandle;
            _avatarPanel = avatarPanel;
            _railResizeHandle = railResizeHandle;
            _railElement = railElement;
        }

        internal void RegisterCallbacks()
        {
            if (_resizeHandle != null)
            {
                _resizeHandle.RegisterCallback<PointerDownEvent>(OnResizePointerDown);
                _resizeHandle.RegisterCallback<PointerMoveEvent>(OnResizePointerMove);
                _resizeHandle.RegisterCallback<PointerUpEvent>(OnResizePointerUp);
            }

            if (_railResizeHandle != null)
            {
                _railResizeHandle.RegisterCallback<PointerDownEvent>(OnRailResizePointerDown);
                _railResizeHandle.RegisterCallback<PointerMoveEvent>(OnRailResizePointerMove);
                _railResizeHandle.RegisterCallback<PointerUpEvent>(OnRailResizePointerUp);
            }
        }

        internal void UnregisterCallbacks()
        {
            if (_resizeHandle != null)
            {
                _resizeHandle.UnregisterCallback<PointerDownEvent>(OnResizePointerDown);
                _resizeHandle.UnregisterCallback<PointerMoveEvent>(OnResizePointerMove);
                _resizeHandle.UnregisterCallback<PointerUpEvent>(OnResizePointerUp);
            }

            if (_railResizeHandle != null)
            {
                _railResizeHandle.UnregisterCallback<PointerDownEvent>(OnRailResizePointerDown);
                _railResizeHandle.UnregisterCallback<PointerMoveEvent>(OnRailResizePointerMove);
                _railResizeHandle.UnregisterCallback<PointerUpEvent>(OnRailResizePointerUp);
            }
        }

        private void OnResizePointerDown(PointerDownEvent evt)
        {
            if (evt.button != 0) return;
            _isResizing = true;
            _resizeStartX = evt.position.x;
            _resizeStartWidth = _avatarPanel?.resolvedStyle.width ?? 320f;
            _resizeHandle.CapturePointer(evt.pointerId);
            _resizeHandle?.AddToClassList("resize-handle--active");
            evt.StopPropagation();
        }

        private void OnResizePointerMove(PointerMoveEvent evt)
        {
            if (!_isResizing) return;
            float delta = _resizeStartX - evt.position.x;
            float newWidth = Mathf.Clamp(_resizeStartWidth + delta, MinAvatarWidth, AvatarMaxWidth(GetWindowWidth()));
            if (_avatarPanel != null)
                _avatarPanel.style.width = newWidth;
            evt.StopPropagation();
        }

        private void OnResizePointerUp(PointerUpEvent evt)
        {
            if (!_isResizing) return;
            _isResizing = false;
            _resizeHandle?.RemoveFromClassList("resize-handle--active");
            if (_resizeHandle != null && _resizeHandle.HasPointerCapture(evt.pointerId))
                _resizeHandle.ReleasePointer(evt.pointerId);
            evt.StopPropagation();
        }

        private void OnRailResizePointerDown(PointerDownEvent evt)
        {
            if (evt.button != 0) return;
            _isRailResizing = true;
            _railResizeStartX = evt.position.x;
            _railResizeStartWidth = _railElement?.resolvedStyle.width ?? 212f;
            _railResizeHandle.CapturePointer(evt.pointerId);
            _railResizeHandle?.AddToClassList("resize-handle--active");
            evt.StopPropagation();
        }

        private void OnRailResizePointerMove(PointerMoveEvent evt)
        {
            if (!_isRailResizing) return;
            float delta = evt.position.x - _railResizeStartX;
            float newWidth = Mathf.Clamp(_railResizeStartWidth + delta, MinRailWidth, RailMaxWidth(GetWindowWidth()));
            if (_railElement != null)
                _railElement.style.width = newWidth;
            evt.StopPropagation();
        }

        private void OnRailResizePointerUp(PointerUpEvent evt)
        {
            if (!_isRailResizing) return;
            _isRailResizing = false;
            _railResizeHandle?.RemoveFromClassList("resize-handle--active");
            if (_railResizeHandle != null && _railResizeHandle.HasPointerCapture(evt.pointerId))
                _railResizeHandle.ReleasePointer(evt.pointerId);
            evt.StopPropagation();
        }

        /// <summary>
        /// Поджимает рейл/аватар-панель под текущее окно: если пользователь растянул
        /// панель на большом окне, а потом окно сузили — панель не должна «съедать»
        /// весь экран. Вызывается из LayoutController при ресайзе окна.
        /// </summary>
        internal void ClampToWindow(float windowWidth)
        {
            if (windowWidth <= 0f)
                return;

            if (_railElement != null)
            {
                float max = RailMaxWidth(windowWidth);
                if (_railElement.resolvedStyle.width > max)
                    _railElement.style.width = max;
            }

            if (_avatarPanel != null)
            {
                float max = AvatarMaxWidth(windowWidth);
                if (_avatarPanel.resolvedStyle.width > max)
                    _avatarPanel.style.width = max;
            }
        }

        private float GetWindowWidth()
        {
            VisualElement probe = _railElement ?? _avatarPanel;
            if (probe == null || probe.panel == null)
                return 0f;

            VisualElement root = probe.panel.visualTree;
            return root != null ? root.resolvedStyle.width : 0f;
        }

        private static float RailMaxWidth(float windowWidth)
        {
            if (windowWidth <= 0f)
                return MaxRailWidth;
            return Mathf.Min(MaxRailWidth, windowWidth * MaxRailPct);
        }

        private static float AvatarMaxWidth(float windowWidth)
        {
            if (windowWidth <= 0f)
                return MaxAvatarWidth;
            return Mathf.Min(MaxAvatarWidth, windowWidth * MaxAvatarPct);
        }
    }
}
