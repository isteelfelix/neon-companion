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
        private const float ZoomStep = 1.12f;
        private const float WheelZoomFactor = 0.0012f;

        private readonly VisualElement _root;
        private readonly VisualElement _overlay;
        private readonly VisualElement _cropContainer;
        private readonly VisualElement _clipMask;
        private readonly VisualElement _imageLayer;
        private readonly Button _cancelBtn;
        private readonly Button _confirmBtn;

        private Texture2D _texture;
        private float _coverScale = 1f;
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
            _overlay.focusable = true;

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
            _clipMask.pickingMode = PickingMode.Position;
            _cropContainer.Add(_clipMask);

            // --- Image layer ---
            _imageLayer = new VisualElement();
            _imageLayer.name = "image-layer";
            _imageLayer.style.position = Position.Absolute;
            _imageLayer.pickingMode = PickingMode.Position;
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

            var zoomOutBtn = new Button(() => ApplyZoom(1f / ZoomStep)) { text = "−" };
            zoomOutBtn.style.minWidth = 40;
            var zoomInBtn = new Button(() => ApplyZoom(ZoomStep)) { text = "+" };
            zoomInBtn.style.minWidth = 40;
            zoomInBtn.style.marginLeft = 8;

            _cancelBtn = new Button(OnCancel) { text = "Отмена" };
            _cancelBtn.style.minWidth = 100;
            _cancelBtn.style.marginLeft = 16;

            _confirmBtn = new Button(OnConfirm) { text = "Установить" };
            _confirmBtn.style.minWidth = 120;
            _confirmBtn.style.marginLeft = 16;

            toolbar.Add(zoomOutBtn);
            toolbar.Add(zoomInBtn);
            toolbar.Add(_cancelBtn);
            toolbar.Add(_confirmBtn);
            _overlay.Add(toolbar);

            // --- Events (crop circle + overlay: wheel/pan must not scroll the page behind) ---
            RegisterCropPointerHandlers(_cropContainer);
            RegisterCropPointerHandlers(_clipMask);
            RegisterCropPointerHandlers(_imageLayer);
            _root.RegisterCallback<WheelEvent>(OnWheel, TrickleDown.TrickleDown);
            _overlay.RegisterCallback<WheelEvent>(OnWheel, TrickleDown.TrickleDown);
            _cropContainer.RegisterCallback<WheelEvent>(OnWheel);
            _overlay.RegisterCallback<KeyDownEvent>(OnKeyDown);

            _coverScale = ComputeCoverScale();

            // existingScale is relative zoom (0.5–3.0) stored in AvatarProfile
            if (Mathf.Approximately(existingScale, 1f) && Mathf.Approximately(existingOffsetX, 0f) && Mathf.Approximately(existingOffsetY, 0f))
                AutoFitImage();
            else
                _scale = Mathf.Clamp(existingScale, MinScale, MaxScale) * _coverScale;

            UpdateTransform();
        }

        public void Show()
        {
            _root.Add(_overlay);
            _overlay.BringToFront();
            _overlay.schedule.Execute(() => _overlay?.Focus()).StartingIn(50);
        }

        private void RegisterCropPointerHandlers(VisualElement element)
        {
            element.RegisterCallback<PointerDownEvent>(OnPointerDown);
            element.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            element.RegisterCallback<PointerUpEvent>(OnPointerUp);
            element.RegisterCallback<PointerCancelEvent>(OnPointerCancel);
        }

        public void Hide()
        {
            if (_overlay.parent != null)
                _overlay.RemoveFromHierarchy();

            // Full-screen host must leave the tree or it blocks all UI input.
            if (_root != null && _root.name == "avatar-crop-root" && _root.parent != null)
                _root.RemoveFromHierarchy();
        }

        private float ComputeCoverScale()
        {
            if (_texture == null || _texture.width == 0 || _texture.height == 0)
                return 1f;

            float scaleX = CircleSize / _texture.width;
            float scaleY = CircleSize / _texture.height;
            return Mathf.Max(scaleX, scaleY);
        }

        private float GetRelativeZoom()
        {
            if (_coverScale <= 0f)
                return 1f;

            return Mathf.Clamp(_scale / _coverScale, MinScale, MaxScale);
        }

        // --- Auto-fit: scale image to cover the circle ---
        private void AutoFitImage()
        {
            _coverScale = ComputeCoverScale();
            _scale = _coverScale;
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
            if (evt.button != 0)
                return;

            _cropContainer.CapturePointer(evt.pointerId);
            _dragging = true;
            _dragStart = (Vector2)evt.position;
            _dragStartOffsetX = _offsetX;
            _dragStartOffsetY = _offsetY;
            evt.StopPropagation();
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (!_dragging || !_cropContainer.HasPointerCapture(evt.pointerId))
                return;

            Vector2 delta = (Vector2)evt.deltaPosition;
            if (delta.sqrMagnitude < 0.0001f)
            {
                Vector2 currentPosition = (Vector2)evt.position;
                delta = currentPosition - _dragStart;
                _offsetX = _dragStartOffsetX + delta.x / CircleSize * 100f;
                _offsetY = _dragStartOffsetY + delta.y / CircleSize * 100f;
            }
            else
            {
                _offsetX += delta.x / CircleSize * 100f;
                _offsetY += delta.y / CircleSize * 100f;
            }

            ClampOffsets();
            UpdateTransform();
            evt.StopPropagation();
        }

        private void OnPointerUp(PointerUpEvent evt)
        {
            EndDrag(evt.pointerId);
            evt.StopPropagation();
        }

        private void OnPointerCancel(PointerCancelEvent evt)
        {
            EndDrag(evt.pointerId);
            evt.StopPropagation();
        }

        private void EndDrag(int pointerId)
        {
            if (!_dragging)
                return;

            if (_cropContainer.HasPointerCapture(pointerId))
                _cropContainer.ReleasePointer(pointerId);
            _dragging = false;
        }

        // --- Zoom ---
        private void OnWheel(WheelEvent evt)
        {
            float wheelDelta = evt.delta.y;
            if (Mathf.Approximately(wheelDelta, 0f))
                wheelDelta = evt.delta.x;

            if (Mathf.Approximately(wheelDelta, 0f))
                return;

            evt.StopPropagation();

            float factor = 1f - wheelDelta * WheelZoomFactor;
            ApplyZoom(factor);
        }

        private void ApplyZoom(float factor)
        {
            if (factor <= 0f || Mathf.Approximately(factor, 1f))
                return;

            float relativeZoom = Mathf.Clamp(GetRelativeZoom() * factor, MinScale, MaxScale);
            _scale = relativeZoom * _coverScale;
            ClampOffsets();
            UpdateTransform();
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
            if (_texture == null || _texture.width == 0 || _texture.height == 0)
                return;

            float scaledW = _texture.width * _scale;
            float scaledH = _texture.height * _scale;
            float maxX = Mathf.Max(0f, (scaledW - CircleSize) * 0.5f / CircleSize * 100f);
            float maxY = Mathf.Max(0f, (scaledH - CircleSize) * 0.5f / CircleSize * 100f);
            _offsetX = Mathf.Clamp(_offsetX, -maxX, maxX);
            _offsetY = Mathf.Clamp(_offsetY, -maxY, maxY);
        }

        private void OnConfirm()
        {
            var result = new AvatarCropResult
            {
                scale = GetRelativeZoom(),
                offsetX = _offsetX,
                offsetY = _offsetY
            };

            // Finish on next frame so UITK is not re-entered from the button click.
            _overlay.schedule.Execute(() =>
            {
                Hide();
                Confirmed?.Invoke(result);
            }).StartingIn(0);
        }

        private void OnCancel()
        {
            _overlay.schedule.Execute(() =>
            {
                Hide();
                Cancelled?.Invoke();
            }).StartingIn(0);
        }
    }

    public static class AvatarCropBaker
    {
        public static Texture2D Bake(Texture2D source, AvatarCropResult crop, int outputSize = 512)
        {
            if (source == null || source.width == 0 || source.height == 0 || outputSize <= 0)
                return null;

            float relativeZoom = Mathf.Clamp(crop.scale > 0f ? crop.scale : 1f, 0.5f, 3f);
            float coverScale = Mathf.Max(outputSize / (float)source.width, outputSize / (float)source.height);
            float pixelScale = relativeZoom * coverScale;
            float scaledW = source.width * pixelScale;
            float scaledH = source.height * pixelScale;
            float posX = (outputSize - scaledW) * 0.5f + crop.offsetX * outputSize / 100f;
            float posY = (outputSize - scaledH) * 0.5f + crop.offsetY * outputSize / 100f;

            var output = new Texture2D(outputSize, outputSize, TextureFormat.RGBA32, false);
            var pixels = new Color32[outputSize * outputSize];
            float radiusSq = (outputSize * 0.5f) * (outputSize * 0.5f);
            float center = outputSize * 0.5f;

            for (int y = 0; y < outputSize; y++)
            {
                for (int x = 0; x < outputSize; x++)
                {
                    int index = y * outputSize + x;
                    float dx = x - center + 0.5f;
                    float dy = y - center + 0.5f;
                    if ((dx * dx + dy * dy) > radiusSq)
                    {
                        pixels[index] = new Color32(0, 0, 0, 0);
                        continue;
                    }

                    // UITK/editor Y is from top; Texture2D rows are from bottom.
                    float yFromTop = (outputSize - 1 - y) + 0.5f;
                    float xFromLeft = x + 0.5f;
                    float imgX = xFromLeft - posX;
                    float imgY = yFromTop - posY;
                    float u = imgX / scaledW;
                    float v = 1f - (imgY / scaledH);

                    if (u < 0f || u > 1f || v < 0f || v > 1f)
                    {
                        pixels[index] = new Color32(0, 0, 0, 0);
                        continue;
                    }

                    pixels[index] = source.GetPixelBilinear(u, v);
                }
            }

            output.SetPixels32(pixels);
            output.Apply();
            return output;
        }

        public static bool TryWriteBakedAvatar(string sourceImagePath, AvatarCropResult crop, int outputSize, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(sourceImagePath) || !System.IO.File.Exists(sourceImagePath))
            {
                error = "Source image not found.";
                return false;
            }

            var bytes = System.IO.File.ReadAllBytes(sourceImagePath);
            var source = new Texture2D(2, 2);
            if (!source.LoadImage(bytes))
            {
                UnityEngine.Object.Destroy(source);
                error = "Failed to load source image.";
                return false;
            }

            var baked = Bake(source, crop, outputSize);
            UnityEngine.Object.Destroy(source);
            if (baked == null)
            {
                error = "Failed to bake avatar crop.";
                return false;
            }

            try
            {
                System.IO.File.WriteAllBytes(sourceImagePath, baked.EncodeToPNG());
            }
            catch (System.Exception ex)
            {
                error = ex.Message;
                UnityEngine.Object.Destroy(baked);
                return false;
            }

            UnityEngine.Object.Destroy(baked);
            return true;
        }
    }
}
