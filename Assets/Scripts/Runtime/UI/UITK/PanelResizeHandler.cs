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
            float newWidth = Mathf.Clamp(_resizeStartWidth + delta, MinAvatarWidth, MaxAvatarWidth);
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
            _railResizeStartWidth = _railElement?.resolvedStyle.width ?? 232f;
            _railResizeHandle.CapturePointer(evt.pointerId);
            _railResizeHandle?.AddToClassList("resize-handle--active");
            evt.StopPropagation();
        }

        private void OnRailResizePointerMove(PointerMoveEvent evt)
        {
            if (!_isRailResizing) return;
            float delta = evt.position.x - _railResizeStartX;
            float newWidth = Mathf.Clamp(_railResizeStartWidth + delta, MinRailWidth, MaxRailWidth);
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
    }
}
