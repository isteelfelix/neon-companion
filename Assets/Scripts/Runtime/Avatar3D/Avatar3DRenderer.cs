using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.Avatar3D
{
    public sealed class Avatar3DRenderer : MonoBehaviour
    {
        [Header("Render")]
        [SerializeField] private int _textureWidth = 1024;
        [SerializeField] private int _textureHeight = 1024;
        [SerializeField] private Color _clearColor = new Color(0f, 0f, 0f, 0f);

        [Header("Camera")]
        [SerializeField] private float _orbitDistance = 2f;
        [SerializeField] private float _minDistance = 0.7f;
        [SerializeField] private float _maxDistance = 4.5f;
        [SerializeField] private float _yaw = 180f;
        [SerializeField] private float _pitch = 10f;
        [SerializeField] private float _minPitch = -30f;
        [SerializeField] private float _maxPitch = 60f;
        [SerializeField] private float _orbitSensitivity = 0.2f;
        [SerializeField] private float _pinchSensitivity = 0.01f;

        [Header("Portrait framing")]
        [Tooltip("Height of the framed slice as a fraction of the model's height " +
            "when the eyes can be located. Smaller is a tighter bust.")]
        [SerializeField] private float _portraitHeightFraction = 0.46f;
        [Tooltip("How far below the eyes to centre the frame, as a fraction of " +
            "the model's height, so the head sits in the upper third.")]
        [SerializeField] private float _portraitEyeBias = 0.12f;

        private RenderTexture _renderTexture;
        private Camera _camera;
        private Light _directionalLight;
        private Transform _cameraPivot;
        private Transform _target;
        private Vector3 _targetCenter;
        private float _targetHeight = 1f;
        private float _framedDistance = 2f;
        private float _viewScale = 1f;
        private float _viewOffsetX;
        private float _viewOffsetY;

        private Image _targetImage;
        private bool _dragging;
        private int _activePointerId = -1;
        private Vector2 _lastPointer;
        private Vector2 _pointerDownLocal;
        private bool _pointerMovedFar;
        private float _lastPinchDistance;

        /// <summary>
        /// A tap on the rendered image that landed on a touch zone. The renderer
        /// only classifies the hit; the app decides how the companion reacts.
        /// </summary>
        public event System.Action<AvatarTouchRegion> Touched;

        // A press that slides past this many pixels is an orbit, not a tap.
        private const float TapMoveThreshold = 6f;
        private readonly Dictionary<int, Vector2> _touchPointers =
            new Dictionary<int, Vector2>();

        public Texture OutputTexture
        {
            get
            {
                EnsureRenderScene();
                return _renderTexture;
            }
        }

        public void AttachTargetImage(Image image)
        {
            if (_targetImage == image)
                return;

            UnbindImageEvents();
            _targetImage = image;
            BindImageEvents();

            if (_targetImage != null)
            {
                EnsureRenderScene();
                _targetImage.image = _renderTexture;
                _targetImage.scaleMode = ScaleMode.ScaleAndCrop;
            }
        }

        public void SetModelRoot(Transform modelRoot)
        {
            _target = modelRoot;
            EnsureRenderScene();
            FrameTarget();
            UpdateCameraTransform();
        }

        /// <summary>World position of the render camera — the point Camera-mode gaze holds.</summary>
        public Vector3 CameraWorldPosition
        {
            get
            {
                EnsureRenderScene();
                return _camera != null ? _camera.transform.position : transform.position;
            }
        }

        /// <summary>
        /// Turns a point on the rendered image (viewport UV, origin bottom-left)
        /// into the world point under it, at the depth of the framed model — the
        /// cursor path's "RenderTexture UV → camera ray".
        /// </summary>
        public bool TryGetGazePoint(Vector2 viewportUv, out Vector3 worldPoint)
        {
            EnsureRenderScene();
            if (_camera == null)
            {
                worldPoint = Vector3.zero;
                return false;
            }

            Ray ray = _camera.ViewportPointToRay(new Vector3(
                Mathf.Clamp01(viewportUv.x),
                Mathf.Clamp01(viewportUv.y),
                0f));
            float depth = Vector3.Distance(_camera.transform.position, _targetCenter);
            if (depth < 0.1f)
                depth = _orbitDistance;
            worldPoint = ray.GetPoint(depth);
            return true;
        }

        public void SetView(float scale, float offsetX, float offsetY)
        {
            _viewScale = Mathf.Clamp(scale > 0f ? scale : 1f, 0.5f, 2f);
            _viewOffsetX = Mathf.Clamp(offsetX, -0.35f, 0.35f);
            _viewOffsetY = Mathf.Clamp(offsetY, -0.35f, 0.35f);
            _orbitDistance = Mathf.Clamp(
                _framedDistance / _viewScale,
                _minDistance,
                _maxDistance);
            UpdateCameraTransform();
        }

        public void ClearModel()
        {
            _target = null;
            UpdateCameraTransform();
        }

        private void OnDisable()
        {
            UnbindImageEvents();
            _touchPointers.Clear();
            _lastPinchDistance = 0f;
        }

        private void OnDestroy()
        {
            UnbindImageEvents();
            TearDownRenderScene();
        }

        private void LateUpdate()
        {
            if (_camera == null || _target == null)
                return;

            UpdateCameraTransform();
            _camera.Render();
        }

        private void EnsureRenderScene()
        {
            if (_renderTexture == null)
            {
                _renderTexture = new RenderTexture(_textureWidth, _textureHeight, 24, RenderTextureFormat.ARGB32)
                {
                    name = "Avatar3DRenderTexture"
                };
                _renderTexture.Create();
            }

            if (_cameraPivot == null)
            {
                var pivotGo = new GameObject("Avatar3D_CameraPivot");
                pivotGo.transform.SetParent(transform, false);
                _cameraPivot = pivotGo.transform;
            }

            if (_camera == null)
            {
                var cameraGo = new GameObject("Avatar3D_Camera");
                cameraGo.transform.SetParent(_cameraPivot, false);
                _camera = cameraGo.AddComponent<Camera>();
                _camera.clearFlags = CameraClearFlags.SolidColor;
                _camera.backgroundColor = _clearColor;
                _camera.nearClipPlane = 0.01f;
                _camera.farClipPlane = 30f;
                _camera.fieldOfView = 32f;
                _camera.targetTexture = _renderTexture;
                _camera.enabled = false;
            }

            if (_directionalLight == null)
            {
                var lightGo = new GameObject("Avatar3D_KeyLight");
                lightGo.transform.SetParent(_cameraPivot, false);
                lightGo.transform.localRotation = Quaternion.Euler(40f, -35f, 0f);

                _directionalLight = lightGo.AddComponent<Light>();
                _directionalLight.type = LightType.Directional;
                _directionalLight.intensity = 1.1f;
                _directionalLight.color = Color.white;
            }

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.35f, 0.35f, 0.35f, 1f);
        }

        private void TearDownRenderScene()
        {
            if (_camera != null)
            {
                _camera.targetTexture = null;
                Destroy(_camera.gameObject);
                _camera = null;
            }

            if (_directionalLight != null)
            {
                Destroy(_directionalLight.gameObject);
                _directionalLight = null;
            }

            if (_cameraPivot != null)
            {
                Destroy(_cameraPivot.gameObject);
                _cameraPivot = null;
            }

            if (_renderTexture != null)
            {
                if (_targetImage != null && _targetImage.image == _renderTexture)
                    _targetImage.image = null;

                _renderTexture.Release();
                Destroy(_renderTexture);
                _renderTexture = null;
            }

        }

        private void UpdateCameraTransform()
        {
            if (_cameraPivot == null)
                return;

            Vector3 pivot = _target != null
                ? _targetCenter -
                    Vector3.right * (_viewOffsetX * _targetHeight) -
                    Vector3.up * (_viewOffsetY * _targetHeight)
                : Vector3.zero;
            _cameraPivot.position = pivot;
            _cameraPivot.rotation = Quaternion.Euler(_pitch, _yaw, 0f);

            if (_camera != null)
            {
                _camera.transform.localPosition = new Vector3(0f, 0f, -_orbitDistance);
                _camera.transform.LookAt(pivot);
            }

        }

        private void FrameTarget()
        {
            if (_target == null)
            {
                _targetCenter = Vector3.zero;
                _targetHeight = 1f;
                _framedDistance = 2f;
                return;
            }

            Renderer[] renderers = _target.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                _targetCenter = _target.position;
                _targetHeight = 1f;
                _framedDistance = 2f;
                return;
            }

            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                bounds.Encapsulate(renderers[i].bounds);

            float totalHeight = Mathf.Max(bounds.size.y, 0.5f);
            _targetHeight = totalHeight;

            float focusY;
            float framedHalfHeight;
            float eyeY;
            if (TryGetEyeHeight(out eyeY))
            {
                // The companion is seen bust-up, so frame the face: centre a
                // little below the eyes and show a slice of the body, not all of
                // it. The geometric bounds centre would sit near the hips.
                focusY = eyeY - totalHeight * _portraitEyeBias;
                framedHalfHeight = Mathf.Max(
                    totalHeight * _portraitHeightFraction * 0.5f, 0.15f);
            }
            else
            {
                focusY = bounds.center.y;
                framedHalfHeight = Mathf.Max(bounds.extents.y, 0.25f);
            }

            _targetCenter = new Vector3(bounds.center.x, focusY, bounds.center.z);
            float framedDistance =
                framedHalfHeight / Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            _framedDistance = Mathf.Clamp(
                framedDistance * 1.15f,
                _minDistance,
                _maxDistance);
            _orbitDistance = Mathf.Clamp(
                _framedDistance / _viewScale,
                _minDistance,
                _maxDistance);
        }

        /// <summary>
        /// The eyes' world height, from the humanoid rig. Averages the eye bones
        /// when present, falls back to the head bone, and reports failure for a
        /// non-humanoid model so framing falls back to the bounds centre. Reads
        /// the rig through <see cref="Animator"/>, so it stays free of any VRM
        /// dependency and works for any humanoid.
        /// </summary>
        private bool TryGetEyeHeight(out float worldY)
        {
            worldY = 0f;

            Animator animator = _target.GetComponentInChildren<Animator>();
            if (animator == null || !animator.isHuman)
                return false;

            Transform leftEye = animator.GetBoneTransform(HumanBodyBones.LeftEye);
            Transform rightEye = animator.GetBoneTransform(HumanBodyBones.RightEye);
            if (leftEye != null && rightEye != null)
            {
                worldY = (leftEye.position.y + rightEye.position.y) * 0.5f;
                return true;
            }

            Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
            if (head != null)
            {
                worldY = head.position.y;
                return true;
            }

            return false;
        }

        private void BindImageEvents()
        {
            if (_targetImage == null)
                return;

            _targetImage.RegisterCallback<PointerDownEvent>(OnPointerDown);
            _targetImage.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            _targetImage.RegisterCallback<PointerUpEvent>(OnPointerUp);
            _targetImage.RegisterCallback<PointerCancelEvent>(OnPointerCancel);
        }

        private void UnbindImageEvents()
        {
            if (_targetImage == null)
                return;

            _targetImage.UnregisterCallback<PointerDownEvent>(OnPointerDown);
            _targetImage.UnregisterCallback<PointerMoveEvent>(OnPointerMove);
            _targetImage.UnregisterCallback<PointerUpEvent>(OnPointerUp);
            _targetImage.UnregisterCallback<PointerCancelEvent>(OnPointerCancel);
        }

        private void OnPointerDown(PointerDownEvent evt)
        {
            if (_target == null)
                return;

            if (evt.pointerType == UnityEngine.UIElements.PointerType.touch)
            {
                _touchPointers[evt.pointerId] =
                    new Vector2(evt.position.x, evt.position.y);
                _lastPinchDistance = GetCurrentPinchDistance();
                _targetImage?.CapturePointer(evt.pointerId);
                return;
            }

            _activePointerId = evt.pointerId;
            _lastPointer = new Vector2(evt.position.x, evt.position.y);
            _pointerDownLocal = new Vector2(evt.localPosition.x, evt.localPosition.y);
            _pointerMovedFar = false;
            _dragging = true;
            _targetImage?.CapturePointer(evt.pointerId);
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (_target == null)
                return;

            if (evt.pointerType == UnityEngine.UIElements.PointerType.touch)
            {
                _touchPointers[evt.pointerId] =
                    new Vector2(evt.position.x, evt.position.y);
                float currentPinchDistance = GetCurrentPinchDistance();
                if (_lastPinchDistance > 0f && currentPinchDistance > 0f)
                {
                    float delta = currentPinchDistance - _lastPinchDistance;
                    _orbitDistance = Mathf.Clamp(_orbitDistance - delta * _pinchSensitivity, _minDistance, _maxDistance);
                }

                _lastPinchDistance = currentPinchDistance;
                return;
            }

            if (!_dragging || evt.pointerId != _activePointerId)
                return;

            Vector2 pointerPos = new Vector2(evt.position.x, evt.position.y);
            Vector2 deltaMove = pointerPos - _lastPointer;
            _lastPointer = pointerPos;

            Vector2 local = new Vector2(evt.localPosition.x, evt.localPosition.y);
            if ((local - _pointerDownLocal).sqrMagnitude >
                TapMoveThreshold * TapMoveThreshold)
                _pointerMovedFar = true;

            _yaw += deltaMove.x * _orbitSensitivity;
            _pitch = Mathf.Clamp(_pitch - deltaMove.y * _orbitSensitivity, _minPitch, _maxPitch);
        }

        private void OnPointerUp(PointerUpEvent evt)
        {
            if (evt.pointerType == UnityEngine.UIElements.PointerType.touch)
            {
                _touchPointers.Remove(evt.pointerId);
                _lastPinchDistance = GetCurrentPinchDistance();
                _targetImage?.ReleasePointer(evt.pointerId);
                return;
            }

            if (evt.pointerId != _activePointerId)
                return;

            _dragging = false;
            _activePointerId = -1;
            _targetImage?.ReleasePointer(evt.pointerId);

            // A press that never turned into an orbit is a tap: see what it hit.
            if (!_pointerMovedFar)
                TryTouchAt(new Vector2(evt.localPosition.x, evt.localPosition.y));
        }

        private void OnPointerCancel(PointerCancelEvent evt)
        {
            _touchPointers.Remove(evt.pointerId);
            _dragging = false;
            _activePointerId = -1;
            _lastPinchDistance = GetCurrentPinchDistance();
        }

        /// <summary>
        /// Casts a ray from the tapped point on the image into the scene and, if
        /// it lands on a touch zone, reports the region. The image draws the
        /// square render texture with ScaleAndCrop, so this treats the visible
        /// rect as the viewport directly — precise enough for finger-sized zones,
        /// not pixel-exact when the image aspect is far from square.
        /// </summary>
        private void TryTouchAt(Vector2 localPosition)
        {
            if (Touched == null || _camera == null || _targetImage == null)
                return;

            Rect rect = _targetImage.contentRect;
            if (rect.width <= 0f || rect.height <= 0f)
                return;

            float u = Mathf.Clamp01(localPosition.x / rect.width);
            float v = Mathf.Clamp01(1f - localPosition.y / rect.height);
            Ray ray = _camera.ViewportPointToRay(new Vector3(u, v, 0f));

            RaycastHit[] hits = Physics.RaycastAll(
                ray,
                _camera.farClipPlane,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Collide);

            float nearest = float.MaxValue;
            bool found = false;
            AvatarTouchRegion region = AvatarTouchRegion.Head;
            for (int i = 0; i < hits.Length; i++)
            {
                VrmTouchZone zone =
                    hits[i].collider.GetComponentInParent<VrmTouchZone>();
                if (zone == null || hits[i].distance >= nearest)
                    continue;
                nearest = hits[i].distance;
                region = zone.Region;
                found = true;
            }

            if (found)
                Touched(region);
        }

        private float GetCurrentPinchDistance()
        {
            if (_touchPointers.Count < 2)
                return 0f;

            Dictionary<int, Vector2>.ValueCollection.Enumerator enumerator =
                _touchPointers.Values.GetEnumerator();
            if (!enumerator.MoveNext())
                return 0f;
            Vector2 first = enumerator.Current;
            if (!enumerator.MoveNext())
                return 0f;
            return Vector2.Distance(first, enumerator.Current);
        }
    }
}
