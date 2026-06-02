// AvatarCropEditor.cs — Telegram-style image crop editor for custom avatars
// Shows a circular crop mask over the uploaded image with drag-to-pan and scroll-to-zoom.

using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.Avatars
{
    public struct AvatarCropResult
    {
        public float scale;   // 0.5–3.0, default 1.0
        public float offsetX; // percentage of container width (-50..+50)
        public float offsetY; // percentage of container height (-50..+50)
    }

    public class AvatarCropEditor
    {
        private const float CircleSize = 300f;
        private const float MinScale = 0.5f;
        private const float MaxScale = 3.0f;
        private const float ZoomSpeed = 0.003f;

        private readonly VisualElement _root;
        private readonly VisualElement _overlay;
        private readonly VisualElement _cropContainer;
        private readonly VisualElement _clipMask;
        private readonly VisualElement _imageLayer;
        private readonly Button _cancelBtn;
        private readonly Button _confirmBtn;

        private Texture2D _texture;
        private float _scale = 1f;
        private float _offsetX;
        private float _offsetY;
        private bool _dragging;
        private Vector2 _dragStart;
        private float _dragStartOffsetX;
        private float _dragStartOffsetY;

        public event Action<AvatarCropResult> Confirmed;
        public event Action Cancelled;

        public AvatarCropEditor(VisualElement root, Texture2D texture, float existingScale = 1f, float existingOffsetX = 0f, float existingOffsetY = 0f)
        {
            _root = root;
            _texture = texture;
            _scale = Mathf.Clamp(existingScale, MinScale, MaxScale);
            _offsetX = existingOffsetX;
            _offsetY = existingOffsetY;

            // --- Overlay ---
            _overlay = new VisualElement();
            _overlay.name = "avatar-crop-overlay";
            _overlay.style.position = Position.Absolute;
            _overlay.style.left = 0;
            _overlay.style.top = 0;
            _overlay.style.right = 0;
            _overlay.style.bottom = 0;
            _overlay.style.backgroundColor = new Color(0f, 0f, 0f, 0.75f);
            _overlay.style.alignItems = Align.Center;
            _overlay.style.justifyContent = Justify.Center;
            _overlay.style.flexDirection = FlexDirection.Column;
            _overlay.pickingMode = PickingMode.Position;

            // Click outside circle = cancel
            _overlay.RegisterCallback<ClickEvent>(evt =>
            {
                if (evt.target == _overlay)
                    OnCancel();
            });

            // --- Crop container ---
            _cropContainer = new VisualElement();
            _cropContainer.name = "crop-container";
            _cropContainer.style.width = CircleSize;
            _cropContainer.style.height = CircleSize;
            _cropContainer.style.position = Position.Relative;
            _cropContainer.style.overflow = Overflow.Hidden;
            _cropContainer.style.borderTopLeftRadius = CircleSize / 2f;
            _cropContainer.style.borderTopRightRadius = CircleSize / 2f;
            _cropContainer.style.borderBottomLeftRadius = CircleSize / 2f;
            _cropContainer.style.borderBottomRightRadius = CircleSize / 2f;
            _cropContainer.style.borderTopWidth = 2f;
            _cropContainer.style.borderBottomWidth = 2f;
            _cropContainer.style.borderLeftWidth = 2f;
            _cropContainer.style.borderRightWidth = 2f;
            _cropContainer.style.borderTopColor = new Color(1f, 1f, 1f, 0.8f);
            _cropContainer.style.borderBottomColor = new Color(1f, 1f, 1f, 0.8f);
            _cropContainer.style.borderLeftColor = new Color(1f, 1f, 1f, 0.8f);
            _cropContainer.style.borderRightColor = new Color(1f, 1f, 1f, 0.8f);
            _cropContainer.pickingMode = PickingMode.Position;

            // --- Clip mask (same size, overflow hidden, circular) ---
            _clipMask = new VisualElement();
            _clipMask.name = "clip-mask";
            _clipMask.style.position = Position.Absolute;
            _clipMask.style.left = 0;
            _clipMask.style.top = 0;
            _clipMask.style.width = CircleSize;
            _clipMask.style.height = CircleSize;
            _clipMask.style.overflow = Overflow.Hidden;
            _clipMask.style.borderTopLeftRadius = CircleSize / 2f;
            _clipMask.style.borderTopRightRadius = CircleSize / 2f;
            _clipMask.style.borderBottomLeftRadius = CircleSize / 2f;
            _clipMask.style.borderBottomRightRadius = CircleSize / 2f;
            _cropContainer.Add(_clipMask);

            // --- Image layer ---
            _imageLayer = new VisualElement();
            _imageLayer.name = "image-layer";
            _imageLayer.style.position = Position.Absolute;
            // Start centered; size/position updated in UpdateTransform
            _clipMask.Add(_imageLayer);

            _overlay.Add(_cropContainer);

            // --- Toolbar ---
            var toolbar = new VisualElement();
            toolbar.name = "crop-toolbar";
            toolbar.style.flexDirection = FlexDirection.Row;
            toolbar.style.alignItems = Align.Center;
            toolbar.style.justifyContent = Justify.Center;
            toolbar.style.marginTop = 16;
            toolbar.style.columnGap = 16;

            _cancelBtn = new Button(OnCancel) { text = "Отмена" };
            _cancelBtn.style.minWidth = 100;

            _confirmBtn = new Button(OnConfirm) { text = "Установить" };
            _confirmBtn.style.minWidth = 120;

            toolbar.Add(_cancelBtn);
            toolbar.Add(_confirmBtn);
            _overlay.Add(toolbar);

            // --- Events ---
            _cropContainer.RegisterCallback<PointerDownEvent>(OnPointerDown);
            _cropContainer.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            _cropContainer.RegisterCallback<PointerUpEvent>(OnPointerUp);
            _cropContainer.RegisterCallback<WheelEvent>(OnWheel);
            _overlay.RegisterCallback<KeyDownEvent>(OnKeyDown);

            // Auto-fit: scale image to cover the circle
            if (Mathf.Approximately(_scale, 1f) && Mathf.Approximately(_offsetX, 0f) && Mathf.Approximately(_offsetY, 0f))
                AutoFitImage();

            UpdateTransform();
        }

        public void Show()
        {
            _root.Add(_overlay);
            _overlay.Focus();
        }

        public void Hide()
        {
            _overlay.RemoveFromHierarchy();
        }

        // --- Auto-fit: scale to cover circle ---
        private void AutoFitImage()
        {
            if (_texture == null || _texture.width == 0 || _texture.height == 0)
                return;

            float imgAspect = (float)_texture.width / _texture.height;
            // Cover: fill the circle completely
            _scale = imgAspect >= 1f
                ? CircleSize / _texture.width   // landscape or square
                : CircleSize / _texture.height; // portrait
            _scale = Mathf.Clamp(_scale, MinScale, MaxScale);
        }

        // --- Transform ---
        private void UpdateTransform()
        {
            if (_texture == null || _texture.width == 0 || _texture.height == 0)
                return;

            float scaledW = _texture.width * _scale;
            float scaledH = _texture.height * _scale;

            _imageLayer.style.width = scaledW;
            _imageLayer.style.height = scaledH;
            _imageLayer.style.backgroundImage = new StyleBackground(_texture);

            // Center image, then apply offset (percentage of circle size)
            float posX = (CircleSize - scaledW) / 2f + _offsetX * CircleSize / 100f;
            float posY = (CircleSize - scaledH) / 2f + _offsetY * CircleSize / 100f;
            _imageLayer.style.left = posX;
            _imageLayer.style.top = posY;
        }

        // --- Pan ---
        private void OnPointerDown(PointerDownEvent evt)
        {
            _cropContainer.CapturePointer(evt.pointerId);
            _dragging = true;
            _dragStart = evt.position;
            _dragStartOffsetX = _offsetX;
            _dragStartOffsetY = _offsetY;
            evt.StopPropagation();
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (!_dragging)
                return;

            Vector2 delta = evt.position - _dragStart;
            _offsetX = _dragStartOffsetX + delta.x / CircleSize * 100f;
            _offsetY = _dragStartOffsetY + delta.y / CircleSize * 100f;
            ClampOffsets();
            UpdateTransform();
            evt.StopPropagation();
        }

        private void OnPointerUp(PointerUpEvent evt)
        {
            if (_dragging)
            {
                _cropContainer.ReleasePointer(evt.pointerId);
                _dragging = false;
            }
            evt.StopPropagation();
        }

        // --- Zoom ---
        private void OnWheel(WheelEvent evt)
        {
            float delta = -evt.delta.y * ZoomSpeed;
            _scale = Mathf.Clamp(_scale + _scale * delta, MinScale, MaxScale);
            ClampOffsets();
            UpdateTransform();
            evt.StopPropagation();
        }

        // --- Keyboard ---
        private void OnKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode == KeyCode.Escape)
                OnCancel();
            else if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                OnConfirm();
        }

        // --- Helpers ---
        private void ClampOffsets()
        {
            float maxOffset = 50f * (_scale - 1f);
            _offsetX = Mathf.Clamp(_offsetX, -maxOffset, maxOffset);
            _offsetY = Mathf.Clamp(_offsetY, -maxOffset, maxOffset);
        }

        private void OnConfirm()
        {
            Confirmed?.Invoke(new AvatarCropResult
            {
                scale = _scale,
                offsetX = _offsetX,
                offsetY = _offsetY
            });
            Hide();
        }

        private void OnCancel()
        {
            Cancelled?.Invoke();
            Hide();
        }
    }
}
