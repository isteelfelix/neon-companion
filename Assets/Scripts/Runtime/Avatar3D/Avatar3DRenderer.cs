using System;
using System.Collections.Generic;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Rendering;
using UniGLTF.SpringBoneJobs.Blittables;
using UniVRM10;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.Avatar3D
{
    // Render AFTER VrmAvatarDriver (order 10000): the driver's foot IK edits the model
    // bones in its LateUpdate, and the next frame's Vrm10 Process() re-copies the control
    // rig over them — so the render must capture the pose in the same frame, after the IK,
    // or the IK is never seen.
    [DefaultExecutionOrder(11000)]
    public sealed class Avatar3DRenderer : MonoBehaviour
    {
        [Header("Render")]
        [Tooltip("Fallback render size used only until the target image has been laid out.")]
        [SerializeField] private int _fallbackTextureSize = 1024;
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
        [SerializeField] private float _portraitHeightFraction = 0.34f;
        [Tooltip("How far below the eyes to centre the frame, as a fraction of " +
            "the model's height, so the head sits in the upper third.")]
        [SerializeField] private float _portraitEyeBias = 0.06f;

        private RenderTexture _renderTexture;
        private Camera _camera;
        private Light _directionalLight;
        private Light _fillLight;
        private Light _rimLight;
        private Transform _cameraPivot;
        private Transform _target;
        private Vector3 _targetCenter;
        private float _targetHeight = 1f;
        private float _framedDistance = 2f;
        // Wheel/pinch zoom sets this target; _orbitDistance eases toward it each frame
        // so a scroll glides in instead of snapping. Framing (SetView/FrameTarget) sets
        // both together so a re-frame is instant, not animated.
        private float _targetOrbitDistance = 2f;
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
        private bool _panning;
        private int _activeButton = -1;

        // In-app column mouse framing: right/middle-drag pans, wheel zooms. The pet
        // window is framed by its own saved scale/offset instead, so these only
        // ever run on the interactive column image (the preview ignores picking).
        private const float PanSensitivity = 0.0025f;
        // Distance added to the zoom target per wheel notch. Kept small for a short
        // throw; the per-frame easing below spreads it out so it still reads as smooth.
        private const float WheelZoomStep = 0.09f;
        // Fraction of the remaining zoom gap closed per second (exponential ease).
        private const float ZoomSmoothing = 14f;

        // ===== Model spin =====
        //
        // A left drag turns the model itself rather than orbiting the camera around a
        // statue. That distinction is the whole point: VRM spring bones (hair, bust,
        // skirt, sleeves) react to the model's motion through the world, so orbiting a
        // camera around a motionless character produces no secondary motion at all.
        private float _modelYaw;
        private float _appliedModelYaw;
        private float _modelYawVelocity;
        private bool _modelYawDirty;
        private Vrm10Instance _vrmInstance;
        private bool _vrmUsesCenteredSprings;
        private BustSpringAnimator _bustSpringAnimator;

        // Exponential decay of the spin after release, per second, and the ceiling on
        // how fast a flick can throw her.
        private const float ModelSpinDamping = 5f;
        private const float MaxModelSpin = 720f;
        private const float MinModelSpin = 4f;
        private const float CenteredSpringForce = 0.35f;

        // ===== Quality =====

        private AvatarGraphicsSettings _graphics = new AvatarGraphicsSettings();
        private bool _graphicsDirty = true;
        private float _nextRenderAt;
        private int _renderWidth;
        private int _renderHeight;
        private int _manualWidth;
        private int _manualHeight;

        // What the last allocation actually got, which is not always what was asked for —
        // an unsupported HDR format falls back to ARGB32. Comparing against the request
        // instead would see a mismatch every frame and reallocate forever.
        private RenderTextureFormat _createdFormat = RenderTextureFormat.ARGB32;
        private int _createdMsaa = 1;

        // Rig state, applied on every render span rather than held on the Light components,
        // because the lights themselves are toggled off between renders.
        private bool _fillLightActive = true;
        private bool _rimLightActive = true;
        private Color _ambientColor = new Color(0.35f, 0.35f, 0.35f, 1f);

        // Smallest side a render target may have, and the relative size change that is
        // worth a reallocation. Without the threshold every pixel of a panel drag would
        // allocate a new texture.
        private const int MinRenderSize = 128;
        private const float ResizeThreshold = 0.04f;

        /// <summary>Current render target size in device pixels. Zero before the first frame.</summary>
        public Vector2Int RenderSize
        {
            get { return new Vector2Int(_renderWidth, _renderHeight); }
        }

        /// <summary>
        /// Tells the renderer how large its output is drawn, in device pixels, for hosts
        /// that composite the texture themselves instead of attaching a UI Toolkit image.
        /// The render scale is applied on top of this.
        /// </summary>
        public void SetManualRenderSize(int width, int height)
        {
            if (width <= 0 || height <= 0)
                return;
            if (_manualWidth == width && _manualHeight == height)
                return;

            _manualWidth = width;
            _manualHeight = height;
            UpdateRenderTextureSize(true);
        }

        /// <summary>
        /// A tap on the rendered image that landed on a touch zone. The renderer
        /// only classifies the hit; the app decides how the companion reacts.
        /// </summary>
        public event System.Action<AvatarTouchRegion> Touched;

        // A press that slides past this many pixels is a drag, not a tap.
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

            if (_targetImage != null && image == null)
                ClearSpringMotionForce();
            UnbindImageEvents();
            _targetImage = image;
            BindImageEvents();

            if (_targetImage != null)
            {
                EnsureRenderScene();
                // The render target is sized from this image, so a new target means the
                // old texture is the wrong shape.
                UpdateRenderTextureSize(true);
                _targetImage.image = _renderTexture;
                _targetImage.scaleMode = ScaleMode.ScaleAndCrop;
            }
        }

        private bool _fullBodyFraming;

        public void SetModelRoot(Transform modelRoot)
        {
            if (_target != modelRoot)
            {
                ClearSpringMotionForce();
                ResetModelSpin();
            }
            _target = modelRoot;
            FindVrmSpringBones();
            EnsureRenderScene();
            FrameTarget();
            UpdateCameraTransform();
        }

        /// <summary>
        /// Frames the whole model instead of a portrait bust. Used by the library
        /// preview, which should show the avatar head-to-toe regardless of the
        /// per-user pet-window framing.
        /// </summary>
        public void SetFullBodyFraming(bool fullBody)
        {
            if (_fullBodyFraming == fullBody)
                return;
            _fullBodyFraming = fullBody;
            if (_target != null)
            {
                FrameTarget();
                UpdateCameraTransform();
            }
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
            _targetOrbitDistance = _orbitDistance;
            UpdateCameraTransform();
        }

        /// <summary>
        /// Replaces the quality/lighting settings for this renderer alone. Used by
        /// the thumbnail baker, which wants a brighter rig than the live view: a
        /// tile is small and dark stills read as muddy at that size.
        /// </summary>
        public void ApplyGraphicsOverride(AvatarGraphicsSettings settings)
        {
            if (settings == null)
                return;
            _graphics = settings;
            _graphicsDirty = true;
        }

        /// <summary>
        /// Frames a fixed window instead of deriving one from renderer bounds.
        /// A model that has not been posed yet reports bind-pose bounds wide
        /// enough to pull the camera back onto the whole T-pose, so the baker
        /// measures the humanoid rig itself and hands the result over here.
        /// </summary>
        public void SetManualFraming(Vector3 focusWorld, float halfHeight)
        {
            if (_camera == null)
                EnsureRenderScene();
            if (_camera == null || halfHeight <= 0f)
                return;

            _targetCenter = focusWorld;
            _targetHeight = halfHeight * 2f;
            _framedDistance = halfHeight /
                Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            _orbitDistance = _framedDistance;
            _targetOrbitDistance = _framedDistance;
            _viewScale = 1f;
            _viewOffsetX = 0f;
            _viewOffsetY = 0f;
            UpdateCameraTransform();
        }

        /// <summary>
        /// Renders the current target once into a fresh texture, using this
        /// renderer's own framing and light rig. Synchronous — it does not wait
        /// for a frame — so the caller can stage a model, capture it and put it
        /// back before anything else in the scene runs.
        /// </summary>
        public Texture2D CaptureStill(int width, int height)
        {
            if (_target == null || width < 8 || height < 8)
                return null;

            EnsureRenderScene();
            if (_camera == null)
                return null;

            // The framing is whatever SetModelRoot or SetManualFraming last set;
            // re-deriving it here would throw a manual frame away.
            if (_graphicsDirty)
            {
                _graphicsDirty = false;
                ApplyCameraQuality();
                ApplyLightingQuality();
            }
            UpdateCameraTransform();

            RenderTexture target = RenderTexture.GetTemporary(
                width, height, 24, ResolveFormat());
            target.antiAliasing = DesiredMsaa();

            RenderTexture previousTarget = _camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;
            Texture2D still = null;
            try
            {
                _camera.targetTexture = target;
                SetRigEnabled(true);
                try
                {
                    _camera.Render();
                }
                finally
                {
                    SetRigEnabled(false);
                }

                RenderTexture.active = target;
                still = new Texture2D(width, height, TextureFormat.RGBA32, false);
                still.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
                still.Apply();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[NeonCompanion] Avatar still capture failed: " + ex.Message);
                if (still != null)
                {
                    Destroy(still);
                    still = null;
                }
            }
            finally
            {
                RenderTexture.active = previousActive;
                _camera.targetTexture = previousTarget;
                RenderTexture.ReleaseTemporary(target);
            }

            return still;
        }

        public void ClearModel()
        {
            ClearSpringMotionForce();
            ResetModelSpin();
            _target = null;
            _vrmInstance = null;
            _vrmUsesCenteredSprings = false;
            _bustSpringAnimator = null;
            UpdateCameraTransform();
        }

        private void OnEnable()
        {
            GraphicsQualityService.Changed += OnGraphicsChanged;
            if (GraphicsQualityService.HasApplied)
                OnGraphicsChanged(GraphicsQualityService.Current);
        }

        private void OnDisable()
        {
            ClearSpringMotionForce();
            ResetModelSpin();
            GraphicsQualityService.Changed -= OnGraphicsChanged;
            UnbindImageEvents();
            _touchPointers.Clear();
            _lastPinchDistance = 0f;
        }

        private void OnDestroy()
        {
            ClearSpringMotionForce();
            GraphicsQualityService.Changed -= OnGraphicsChanged;
            UnbindImageEvents();
            TearDownRenderScene();
        }

        private void OnGraphicsChanged(AvatarGraphicsSettings settings)
        {
            if (settings == null)
                return;
            _graphics = settings;
            _graphicsDirty = true;
        }

        /// <summary>
        /// The model's own rotation is integrated here rather than in
        /// <see cref="LateUpdate"/>. It has to run on every frame even when rendering
        /// is throttled, and Update lands before UniVRM solves its spring bones in
        /// LateUpdate, so the authored hair/clothing/body springs see the motion.
        /// </summary>
        private void Update()
        {
            // A model that has been parked in the cache (deactivated, kept warm for
            // the next switch) is still referenced here until a new one is mounted.
            if (_target == null || !_target.gameObject.activeInHierarchy)
                return;

            float deltaTime = Time.deltaTime;
            if (deltaTime > 0f && !_dragging && Mathf.Abs(_modelYawVelocity) > MinModelSpin)
            {
                // Coast after release, so a flick keeps turning her and the hair keeps
                // trailing instead of stopping dead with the pointer.
                _modelYaw += _modelYawVelocity * deltaTime;
                _modelYawVelocity *= Mathf.Exp(-ModelSpinDamping * deltaTime);
                _modelYawDirty = true;
            }
            else if (!_dragging)
            {
                _modelYawVelocity = 0f;
            }

            if (_modelYawDirty)
            {
                _modelYawDirty = false;
                float yawDelta = _modelYaw - _appliedModelYaw;
                _appliedModelYaw = _modelYaw;

                // Apply only our delta. The runtime root is shared by the portrait and
                // full-body preview renderers, and other avatar APIs may also set its
                // pose; assigning an absolute rotation here would overwrite them.
                _target.localRotation =
                    _target.localRotation * Quaternion.Euler(0f, yawDelta, 0f);
            }

            // Glide the camera toward the wheel/pinch zoom target. Frame-rate
            // independent exponential ease; snaps the last hair to kill drift.
            if (deltaTime > 0f && _orbitDistance != _targetOrbitDistance)
            {
                float t = 1f - Mathf.Exp(-ZoomSmoothing * deltaTime);
                _orbitDistance = Mathf.Lerp(_orbitDistance, _targetOrbitDistance, t);
                if (Mathf.Abs(_orbitDistance - _targetOrbitDistance) < 0.0005f)
                    _orbitDistance = _targetOrbitDistance;
            }

            ApplySpringMotionForce();
        }

        private void ResetModelSpin()
        {
            if (_targetImage != null && _bustSpringAnimator != null)
                _bustSpringAnimator.SetTurnVelocity(0f);
            _modelYaw = 0f;
            _appliedModelYaw = 0f;
            _modelYawVelocity = 0f;
            _modelYawDirty = false;
        }

        private void FindVrmSpringBones()
        {
            _vrmInstance = _target != null
                ? _target.GetComponentInChildren<Vrm10Instance>(true)
                : null;
            _vrmUsesCenteredSprings = false;
            _bustSpringAnimator = null;
            if (_vrmInstance == null || _vrmInstance.SpringBone == null)
                return;

            _bustSpringAnimator =
                _vrmInstance.GetComponent<BustSpringAnimator>();
            if (_bustSpringAnimator == null)
                _bustSpringAnimator =
                    _vrmInstance.gameObject.AddComponent<BustSpringAnimator>();
            _bustSpringAnimator.Configure(_vrmInstance);

            List<Vrm10InstanceSpringBone.Spring> springs =
                _vrmInstance.SpringBone.Springs;
            if (springs == null)
                return;
            for (int i = 0; i < springs.Count; i++)
            {
                if (springs[i] != null && springs[i].Center != null)
                {
                    _vrmUsesCenteredSprings = true;
                    return;
                }
            }
        }

        private void ApplySpringMotionForce()
        {
            // A centered VRM stores its previous tails in the center's local space.
            // Rotating the whole model rotates that history too, cancelling most of
            // the visible inertia. Feed the same drag into UniVRM as a small lateral
            // force so authored stiffness, drag, gravity and colliders still decide
            // the actual motion. Only the interactive renderer owns this input; the
            // separate read-only preview must not overwrite it with zero every frame.
            if (_targetImage == null || !_vrmUsesCenteredSprings || _vrmInstance == null)
                return;

            float normalizedSpin = Mathf.Clamp(
                _modelYawVelocity / MaxModelSpin, -1f, 1f);
            if (_bustSpringAnimator != null)
                _bustSpringAnimator.SetTurnVelocity(normalizedSpin);
            Vector3 force = -_target.right * normalizedSpin * CenteredSpringForce;
            SetSpringMotionForce(force);
        }

        private void ClearSpringMotionForce()
        {
            if (_vrmInstance != null && _vrmUsesCenteredSprings && _targetImage != null)
                SetSpringMotionForce(Vector3.zero);
        }

        private void SetSpringMotionForce(Vector3 force)
        {
            _vrmInstance.Runtime.SpringBone.SetModelLevel(
                _vrmInstance.transform,
                new BlittableModelLevel(new float3(force.x, force.y, force.z)));
        }

        private void LateUpdate()
        {
            if (_camera == null || _target == null ||
                !_target.gameObject.activeInHierarchy)
                return;

            if (_graphicsDirty)
            {
                _graphicsDirty = false;
                ApplyCameraQuality();
                ApplyLightingQuality();
                UpdateRenderTextureSize(true);
            }

            // Off-screen or collapsed views cost nothing: the last rendered frame stays in
            // the texture, so re-showing the panel is instant. Always on — there is no
            // reason a user would want to burn frames on something they cannot see.
            if (!IsTargetVisible())
                return;

            float now = Time.unscaledTime;
            if (now < _nextRenderAt)
                return;

            int fps = _graphics.avatarFrameRate;
            float interval = fps > 0 ? 1f / fps : 0f;
            // Anchor on "now" rather than accumulating, so a stall cannot build up a
            // backlog of catch-up renders.
            _nextRenderAt = now + interval;

            UpdateRenderTextureSize(false);
            UpdateCameraTransform();

            // Lights are scene-global, and the app runs two renderers at once (the avatar
            // column and the gallery preview). Each rig is switched on only for the span of
            // its own synchronous Render call, so neither camera is lit by the other's rig.
            SetRigEnabled(true);
            try
            {
                _camera.Render();
            }
            finally
            {
                SetRigEnabled(false);
            }
        }

        private void SetRigEnabled(bool enabled)
        {
            if (_directionalLight != null)
                _directionalLight.enabled = enabled;
            if (_fillLight != null)
                _fillLight.enabled = enabled && _fillLightActive;
            if (_rimLight != null)
                _rimLight.enabled = enabled && _rimLightActive;

            if (!enabled)
                return;

            // Pin the main light instead of letting URP pick the brightest directional
            // light — otherwise a strong rim would take over and shadow from behind.
            RenderSettings.sun = _directionalLight;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = _ambientColor;
        }

        /// <summary>
        /// Walks up from the target image: any collapsed ancestor, a detached panel or a
        /// zero-sized rect all mean nothing of this render would be seen.
        ///
        /// Hosts that draw the output texture themselves (the pet window blits it with
        /// IMGUI) never attach an image; they own their own visibility, so this reports
        /// visible and lets them stop the component instead.
        /// </summary>
        private bool IsTargetVisible()
        {
            if (_targetImage == null)
                return true;

            if (_targetImage.panel == null || !_targetImage.visible)
                return false;

            Rect rect = _targetImage.contentRect;
            if (rect.width < 1f || rect.height < 1f || float.IsNaN(rect.width))
                return false;

            VisualElement current = _targetImage;
            while (current != null)
            {
                if (current.resolvedStyle.display == DisplayStyle.None)
                    return false;
                current = current.parent;
            }
            return true;
        }

        private void EnsureRenderScene()
        {
            if (_renderTexture == null)
                CreateRenderTexture(_fallbackTextureSize, _fallbackTextureSize);

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
                ApplyCameraQuality();
            }

            // OutputTexture calls this every frame in the pet window, so only touch the
            // lighting when a light was actually just created.
            if (EnsureLights())
                ApplyLightingQuality();
        }

        /// <summary>Creates any missing light in the rig. Returns true when something was created.</summary>
        private bool EnsureLights()
        {
            bool created = false;
            if (_directionalLight == null)
            {
                _directionalLight = CreateLight("Avatar3D_KeyLight", Quaternion.Euler(40f, -35f, 0f));
                created = true;
            }
            if (_fillLight == null)
            {
                _fillLight = CreateLight("Avatar3D_FillLight", Quaternion.Euler(15f, 40f, 0f));
                created = true;
            }
            if (_rimLight == null)
            {
                _rimLight = CreateLight("Avatar3D_RimLight", Quaternion.Euler(-12f, 165f, 0f));
                created = true;
            }
            return created;
        }

        /// <summary>
        /// Lights hang off the camera pivot, so the rig turns with the view and the
        /// companion keeps the same portrait lighting from every orbit angle.
        /// </summary>
        private Light CreateLight(string lightName, Quaternion localRotation)
        {
            var lightGo = new GameObject(lightName);
            lightGo.transform.SetParent(_cameraPivot, false);
            lightGo.transform.localRotation = localRotation;

            Light light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = Color.white;
            light.shadows = LightShadows.None;
            // Off until this renderer's own render span — see SetRigEnabled.
            light.enabled = false;
            return light;
        }

        // ============================================================
        // Quality
        // ============================================================

        private void ApplyCameraQuality()
        {
            if (_camera == null)
                return;

            _camera.allowHDR = _graphics.hdr;
            _camera.allowMSAA = _graphics.MsaaSamples > 1;

            UniversalAdditionalCameraData data = _camera.GetUniversalAdditionalCameraData();
            if (data == null)
                return;

            // The project's default renderer is the 2D one; the avatar needs the 3D
            // renderer for shadows, post-processing and the post-AA passes.
            int rendererIndex = GraphicsQualityService.AvatarRendererIndex;
            if (rendererIndex >= 0)
                data.SetRenderer(rendererIndex);

            bool fxaa = _graphics.UsesFxaa;
            bool smaa = _graphics.UsesSmaa;

            if (fxaa)
                data.antialiasing = AntialiasingMode.FastApproximateAntialiasing;
            else if (smaa)
                data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            else
                data.antialiasing = AntialiasingMode.None;

            data.antialiasingQuality = AntialiasingQuality.High;

            // URP only runs the post-AA passes as part of post-processing, so FXAA/SMAA
            // force the stack on even when every effect is switched off.
            data.renderPostProcessing = _graphics.postProcessing || fxaa || smaa;
        }

        private void ApplyLightingQuality()
        {
            if (_directionalLight != null)
            {
                _directionalLight.intensity = _graphics.KeyLightIntensity;

                if (!_graphics.ShadowsEnabled)
                    _directionalLight.shadows = LightShadows.None;
                else if (_graphics.SoftShadows)
                    _directionalLight.shadows = LightShadows.Soft;
                else
                    _directionalLight.shadows = LightShadows.Hard;
            }

            _fillLightActive = _graphics.FillLightIntensity > 0.001f;
            if (_fillLight != null)
                _fillLight.intensity = _graphics.FillLightIntensity;

            _rimLightActive = _graphics.RimLightIntensity > 0.001f;
            if (_rimLight != null)
                _rimLight.intensity = _graphics.RimLightIntensity;

            float ambient = _graphics.AmbientIntensity;
            _ambientColor = new Color(ambient, ambient, ambient, 1f);

            // Left off outside the render span — see SetRigEnabled.
            SetRigEnabled(false);
        }

        // ============================================================
        // Render target sizing
        // ============================================================

        /// <summary>
        /// Sizes the render target from the target image's own on-screen size in device
        /// pixels, times the user's render scale. That replaces the old fixed 1024x1024
        /// texture, which was simultaneously blurry (stretched into a tall column) and
        /// wasteful (rendering pixels the crop threw away).
        /// </summary>
        private void UpdateRenderTextureSize(bool force)
        {
            int width;
            int height;
            if (!TryMeasureTarget(out width, out height))
                return;

            if (!force && _renderTexture != null)
            {
                float widthDelta = Mathf.Abs(width - _renderWidth) / (float)Mathf.Max(_renderWidth, 1);
                float heightDelta = Mathf.Abs(height - _renderHeight) / (float)Mathf.Max(_renderHeight, 1);
                if (widthDelta < ResizeThreshold && heightDelta < ResizeThreshold)
                    return;
            }

            if (_renderTexture != null &&
                _renderTexture.width == width &&
                _renderTexture.height == height &&
                _createdFormat == ResolveFormat() &&
                _createdMsaa == DesiredMsaa())
                return;

            CreateRenderTexture(width, height);

            if (_camera != null)
                _camera.targetTexture = _renderTexture;
            if (_targetImage != null)
                _targetImage.image = _renderTexture;
        }

        private bool TryMeasureTarget(out int width, out int height)
        {
            width = 0;
            height = 0;

            float sourceWidth;
            float sourceHeight;
            float pointsToPixels = 1f;

            if (_targetImage != null && _targetImage.panel != null)
            {
                Rect rect = _targetImage.contentRect;
                if (rect.width < 1f || rect.height < 1f ||
                    float.IsNaN(rect.width) || float.IsNaN(rect.height))
                    return false;

                sourceWidth = rect.width;
                sourceHeight = rect.height;

                // UI Toolkit lays out in points; with a scaled panel those are not device
                // pixels. The panel's own root width against the screen gives the exact
                // factor for any PanelSettings scale mode.
                VisualElement panelRoot = _targetImage.panel.visualTree;
                if (panelRoot != null && panelRoot.layout.width > 1f)
                    pointsToPixels = Screen.width / panelRoot.layout.width;
                if (pointsToPixels < 0.1f || pointsToPixels > 8f || float.IsNaN(pointsToPixels))
                    pointsToPixels = 1f;
            }
            else if (_manualWidth > 0 && _manualHeight > 0)
            {
                // Host-driven size — already in device pixels.
                sourceWidth = _manualWidth;
                sourceHeight = _manualHeight;
            }
            else
            {
                return false;
            }

            float scale = pointsToPixels * _graphics.renderScale;
            width = Mathf.RoundToInt(sourceWidth * scale);
            height = Mathf.RoundToInt(sourceHeight * scale);

            int longest = Mathf.Max(width, height);
            if (longest > _graphics.maxRenderSize)
            {
                float clampScale = _graphics.maxRenderSize / (float)longest;
                width = Mathf.RoundToInt(width * clampScale);
                height = Mathf.RoundToInt(height * clampScale);
            }

            width = Mathf.Max(width, MinRenderSize);
            height = Mathf.Max(height, MinRenderSize);
            return true;
        }

        /// <summary>
        /// The format actually used, after checking hardware support. Both candidates carry
        /// alpha on purpose: URP decides whether post-processing may write the alpha channel
        /// from the target's format, and without it the transparent background goes opaque.
        /// </summary>
        private RenderTextureFormat ResolveFormat()
        {
            RenderTextureFormat wanted = _graphics.hdr
                ? RenderTextureFormat.ARGBHalf
                : RenderTextureFormat.ARGB32;
            return SystemInfo.SupportsRenderTextureFormat(wanted)
                ? wanted
                : RenderTextureFormat.ARGB32;
        }

        private int DesiredMsaa()
        {
            return _graphics.MsaaSamples;
        }

        private void CreateRenderTexture(int width, int height)
        {
            RenderTexture previous = _renderTexture;

            RenderTextureFormat format = ResolveFormat();
            int msaa = DesiredMsaa();

            var created = new RenderTexture(width, height, 24, format);
            created.name = "Avatar3DRenderTexture";
            created.antiAliasing = msaa;
            created.filterMode = FilterMode.Bilinear;
            created.wrapMode = TextureWrapMode.Clamp;
            created.useMipMap = false;
            created.Create();

            _renderTexture = created;
            _renderWidth = width;
            _renderHeight = height;
            _createdFormat = format;
            _createdMsaa = msaa;

            if (previous != null)
            {
                if (_camera != null && _camera.targetTexture == previous)
                    _camera.targetTexture = created;
                if (_targetImage != null && _targetImage.image == previous)
                    _targetImage.image = created;
                previous.Release();
                Destroy(previous);
            }
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
                if (RenderSettings.sun == _directionalLight)
                    RenderSettings.sun = null;
                Destroy(_directionalLight.gameObject);
                _directionalLight = null;
            }

            if (_fillLight != null)
            {
                Destroy(_fillLight.gameObject);
                _fillLight = null;
            }

            if (_rimLight != null)
            {
                Destroy(_rimLight.gameObject);
                _rimLight = null;
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
            if (_fullBodyFraming)
            {
                // Head-to-toe with a little headroom, centred on the bounds.
                focusY = bounds.center.y;
                framedHalfHeight = Mathf.Max(bounds.extents.y * 1.08f, 0.25f);
            }
            else if (TryGetEyeHeight(out eyeY))
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
            _targetOrbitDistance = _orbitDistance;
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
            _targetImage.RegisterCallback<WheelEvent>(OnPointerWheel);
        }

        private void UnbindImageEvents()
        {
            if (_targetImage == null)
                return;

            _targetImage.UnregisterCallback<PointerDownEvent>(OnPointerDown);
            _targetImage.UnregisterCallback<PointerMoveEvent>(OnPointerMove);
            _targetImage.UnregisterCallback<PointerUpEvent>(OnPointerUp);
            _targetImage.UnregisterCallback<PointerCancelEvent>(OnPointerCancel);
            _targetImage.UnregisterCallback<WheelEvent>(OnPointerWheel);
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
            _activeButton = evt.button;
            // Left button turns the model; right/middle drag pans the framing.
            _panning = evt.button == 1 || evt.button == 2;
            if (!_panning)
                _modelYawVelocity = 0f;
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
                    _targetOrbitDistance = Mathf.Clamp(_targetOrbitDistance - delta * _pinchSensitivity, _minDistance, _maxDistance);
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

            if (_panning)
            {
                // Grab-style pan: the model follows the cursor. Signs give a natural
                // feel at the default front view; drives the column's own framing
                // (independent of the pet window's saved offset).
                _viewOffsetX += deltaMove.x * PanSensitivity;
                _viewOffsetY -= deltaMove.y * PanSensitivity;
            }
            else
            {
                // Horizontal turns the model, vertical still tilts the camera — a
                // character cannot credibly be tipped over, but she can be spun.
                float yawDelta = deltaMove.x * _orbitSensitivity;
                _modelYaw += yawDelta;
                _modelYawDirty = true;

                float deltaTime = Time.deltaTime;
                if (deltaTime > 0.0001f)
                {
                    // Smoothed, because pointer moves can arrive several times a frame
                    // and a raw delta/dt reads as jitter on release.
                    float instantaneous = Mathf.Clamp(
                        yawDelta / deltaTime, -MaxModelSpin, MaxModelSpin);
                    _modelYawVelocity = Mathf.Lerp(_modelYawVelocity, instantaneous, 0.5f);
                }

                _pitch = Mathf.Clamp(_pitch - deltaMove.y * _orbitSensitivity, _minPitch, _maxPitch);
            }
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

            // A left-button press that never turned into a drag is a tap: see what
            // it hit. Right/middle drags are panning, never a touch.
            bool wasPan = _panning;
            int button = _activeButton;
            _panning = false;
            _activeButton = -1;
            if (!_pointerMovedFar && !wasPan && button == 0)
                TryTouchAt(new Vector2(evt.localPosition.x, evt.localPosition.y));
        }

        private void OnPointerCancel(PointerCancelEvent evt)
        {
            _touchPointers.Remove(evt.pointerId);
            _dragging = false;
            _activePointerId = -1;
            _panning = false;
            _activeButton = -1;
            _modelYawVelocity = 0f;
            _lastPinchDistance = GetCurrentPinchDistance();
        }

        private void OnPointerWheel(WheelEvent evt)
        {
            if (_target == null)
                return;

            // Mouse-wheel zoom for the in-app column. Scrolling up (negative delta)
            // pulls the camera in. Only the target moves here; Update() eases the
            // actual distance toward it so the zoom glides instead of snapping.
            _targetOrbitDistance = Mathf.Clamp(
                _targetOrbitDistance + evt.delta.y * WheelZoomStep,
                _minDistance,
                _maxDistance);
            evt.StopPropagation();
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
