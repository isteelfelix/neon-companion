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

        private RenderTexture _renderTexture;
        private Camera _camera;
        private Light _directionalLight;
        private Transform _cameraPivot;
        private Transform _target;

        private Image _targetImage;
        private bool _dragging;
        private int _activePointerId = -1;
        private Vector2 _lastPointer;
        private float _lastPinchDistance;

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
                _camera.enabled = true;
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

            Vector3 pivot = _target != null ? _target.position : Vector3.zero;
            _cameraPivot.position = pivot;
            _cameraPivot.rotation = Quaternion.Euler(_pitch, _yaw, 0f);

            if (_camera != null)
            {
                _camera.transform.localPosition = new Vector3(0f, 0f, -_orbitDistance);
                _camera.transform.LookAt(pivot);
            }
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
                _lastPinchDistance = GetCurrentPinchDistance();
                return;
            }

            _activePointerId = evt.pointerId;
            _lastPointer = new Vector2(evt.position.x, evt.position.y);
            _dragging = true;
            _targetImage?.CapturePointer(evt.pointerId);
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (_target == null)
                return;

            if (evt.pointerType == UnityEngine.UIElements.PointerType.touch)
            {
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

            _yaw += deltaMove.x * _orbitSensitivity;
            _pitch = Mathf.Clamp(_pitch - deltaMove.y * _orbitSensitivity, _minPitch, _maxPitch);
        }

        private void OnPointerUp(PointerUpEvent evt)
        {
            if (evt.pointerType == UnityEngine.UIElements.PointerType.touch)
            {
                _lastPinchDistance = 0f;
                return;
            }

            if (evt.pointerId != _activePointerId)
                return;

            _dragging = false;
            _activePointerId = -1;
            _targetImage?.ReleasePointer(evt.pointerId);
        }

        private void OnPointerCancel(PointerCancelEvent evt)
        {
            _dragging = false;
            _activePointerId = -1;
            _lastPinchDistance = 0f;
        }

        private float GetCurrentPinchDistance()
        {
            if (Input.touchCount < 2)
                return 0f;

            var t0 = Input.GetTouch(0);
            var t1 = Input.GetTouch(1);
            return Vector2.Distance(t0.position, t1.position);
        }
    }
}
