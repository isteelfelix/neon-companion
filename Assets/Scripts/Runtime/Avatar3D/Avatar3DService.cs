using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Data.Models;
using UniVRM10;
using UnityEngine;

namespace NeonCompanion.Runtime.Avatar3D
{
    public sealed class Avatar3DService : IAvatar3DService
    {
        private GameObject _runtimeRoot;
        private Animator _animator;
        private Animation _legacyAnimation;
        private VrmAvatarDriver _vrmDriver;
        private AvatarCapabilities _capabilities = new AvatarCapabilities();
        private readonly List<string> _availableAnimations = new List<string>();
        private readonly AvatarSceneState _scene = new AvatarSceneState();
        private AvatarGazeMode _gazeMode = AvatarGazeMode.Cursor;

        public bool IsLoaded => _runtimeRoot != null && _scene.CanMutate;
        public IReadOnlyList<string> AvailableAnimations => _availableAnimations;
        public AvatarCapabilities Capabilities => _capabilities;

        public async Task<bool> LoadAvatar(string modelPath)
        {
            int generation = _scene.BeginLoad();

            var loadResult = await Avatar3DLoader.LoadAsync(modelPath);

            // Unload() or a newer LoadAvatar() ran while we were loading: this
            // result is orphaned, so destroy it rather than binding it.
            if (!_scene.IsCurrent(generation))
            {
                DestroyOwnedObject(loadResult != null ? loadResult.Instance : null);
                return false;
            }

            if (!loadResult.Success || loadResult.Instance == null)
            {
                _scene.MarkFailed();
                return false;
            }

            DisposeCurrentModel();
            _scene.BeginBinding();
            _runtimeRoot = loadResult.Instance;
            _animator = _runtimeRoot.GetComponentInChildren<Animator>(true);
            _legacyAnimation = _runtimeRoot.GetComponentInChildren<Animation>(true);
            _capabilities = loadResult.Capabilities ?? new AvatarCapabilities();
            if (loadResult.VrmInstance != null)
            {
                _vrmDriver = _runtimeRoot.AddComponent<VrmAvatarDriver>();
                bool driverReady = await _vrmDriver.InitializeAsync(
                    loadResult.VrmInstance,
                    _capabilities,
                    BuiltInAvatarProfiles.IsResourcePath(modelPath),
                    generation,
                    _scene);

                // Unload() ran during driver init and already tore the model
                // down; the driver cleaned up whatever it had loaded.
                if (!_scene.IsCurrent(generation))
                    return false;

                if (!driverReady)
                {
                    DisposeCurrentModel();
                    _scene.MarkFailed();
                    return false;
                }
            }

            _availableAnimations.Clear();
            _availableAnimations.AddRange(loadResult.AnimationNames);

            _scene.MarkMounted();

            // Carry over a gaze mode chosen before the model finished loading.
            if (_vrmDriver != null)
                _vrmDriver.SetGazeMode(_gazeMode);

            if (_availableAnimations.Contains("idle"))
                SetAnimation("idle");
            return true;
        }

        public bool SetAnimation(string clipName)
        {
            if (!IsLoaded || string.IsNullOrWhiteSpace(clipName))
                return false;

            if (_vrmDriver != null && _vrmDriver.SetAnimation(clipName))
                return true;

            bool played = false;

            if (_animator != null)
            {
                _animator.Play(clipName, 0, 0f);
                played = true;
            }

            if (_legacyAnimation != null && _legacyAnimation.GetClip(clipName) != null)
            {
                _legacyAnimation.clip = _legacyAnimation.GetClip(clipName);
                _legacyAnimation.Play();
                played = true;
            }

            return played;
        }

        public bool SetMouthShape(string shape)
        {
            return SetMouthShape(shape, 1f);
        }

        public bool SetMouthShape(string shape, float weight)
        {
            return _scene.CanMutate && _vrmDriver != null &&
                _vrmDriver.SetMouthShape(shape, weight);
        }

        public void ClearMouth()
        {
            if (_scene.CanMutate && _vrmDriver != null)
                _vrmDriver.ClearMouth();
        }

        public bool SetExpression(string expressionName, float weight)
        {
            return _scene.CanMutate && _vrmDriver != null &&
                _vrmDriver.SetExpression(expressionName, weight);
        }

        public bool SetEmotion(string emotionName)
        {
            return _scene.CanMutate && _vrmDriver != null &&
                _vrmDriver.SetEmotion(emotionName);
        }

        public bool SetPose(string poseName)
        {
            if (!IsLoaded)
                return false;

            if (string.IsNullOrWhiteSpace(poseName))
            {
                _runtimeRoot.transform.localRotation = Quaternion.identity;
                return true;
            }

            switch (poseName.Trim().ToLowerInvariant())
            {
                case "front":
                    _runtimeRoot.transform.localRotation = Quaternion.identity;
                    return true;
                case "left":
                    _runtimeRoot.transform.localRotation = Quaternion.Euler(0f, 45f, 0f);
                    return true;
                case "right":
                    _runtimeRoot.transform.localRotation = Quaternion.Euler(0f, -45f, 0f);
                    return true;
                case "back":
                    _runtimeRoot.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
                    return true;
                default:
                    return false;
            }
        }

        public AvatarGazeMode GazeMode
        {
            get { return _gazeMode; }
        }

        public void SetGazeMode(AvatarGazeMode mode)
        {
            _gazeMode = mode;
            if (_scene.CanMutate && _vrmDriver != null)
                _vrmDriver.SetGazeMode(mode);
        }

        public void SetGazeTarget(Vector3 worldPoint)
        {
            if (_scene.CanMutate && _vrmDriver != null)
                _vrmDriver.SetGazeTarget(worldPoint);
        }

        public void SetGazeNormalized(float horizontal, float vertical)
        {
            if (_scene.CanMutate && _vrmDriver != null)
                _vrmDriver.SetGazeNormalized(horizontal, vertical);
        }

        public Transform GetRuntimeTransform()
        {
            return _runtimeRoot != null ? _runtimeRoot.transform : null;
        }

        public GameObject GetRuntimeRoot()
        {
            return _runtimeRoot;
        }

        public void Unload()
        {
            // Bumping the generation first is what lets a load that is still
            // awaiting notice that it no longer owns the scene.
            _scene.Reset();
            DisposeCurrentModel();
        }

        /// <summary>
        /// Releases the live model without invalidating the current load stamp.
        /// Used when the owning load is replacing its own model.
        /// </summary>
        private void DisposeCurrentModel()
        {
            _availableAnimations.Clear();
            _animator = null;
            _legacyAnimation = null;
            _vrmDriver = null;
            _capabilities = new AvatarCapabilities();

            if (_runtimeRoot != null)
            {
                DestroyOwnedObject(_runtimeRoot);
                _runtimeRoot = null;
            }
        }

        private static void DestroyOwnedObject(UnityEngine.Object value)
        {
            if (value == null)
                return;
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(value);
            else
                UnityEngine.Object.DestroyImmediate(value);
        }
    }

    [DefaultExecutionOrder(10000)]
    internal sealed class VrmAvatarDriver : MonoBehaviour
    {
        /// <summary>
        /// The built-in motion pack is a single looping idle. Emotion and speech
        /// are carried by expressions and visemes rather than by body clips, so
        /// there is nothing else to bake or to keep in sync with the face.
        /// </summary>
        private const string IdleState = "idle";

        private Vrm10Instance _vrm;
        private Vrm10Runtime _runtime;
        private AvatarCapabilities _capabilities;
        private Vrm10AnimationInstance _idleAnimation;
        private Animation _activeAnimation;
        private Vrm10AnimationInstance _activeVrmAnimation;
        private string _activeState;
        private VrmExpressionComposer _composer;
        private VrmEmotionBlender _emotions;
        private VrmIdleAnimator _idle;
        private VrmBodyIdleAnimator _bodyIdle;
        private VrmVisemeAnimator _visemes;

        // Foot IK — the captured animation does NOT keep the feet planted (they drift
        // ~12% of leg length), so we lock each foot to where it rests and solve the knee
        // so the pelvis/body can move over planted feet. 1 = fully locked, 0 = off.
        private const float FootIkWeight = 1f;
        private Transform _lUpperLeg, _lLowerLeg, _lFoot;
        private Transform _rUpperLeg, _rLowerLeg, _rFoot;
        private bool _legsCached;
        private bool _footRestCaptured;
        private Vector3 _lFootRestPos, _rFootRestPos;
        private Quaternion _lFootRestRot, _rFootRestRot;

        // The neutral "arms down" seed pose, shared by every avatar (normalized, so
        // model-independent) and loaded once from the built-in idle clip.
        private static Dictionary<HumanBodyBones, Quaternion> _sharedRestPose;
        private static bool _restPoseLoaded;
        private Transform _head;
        private Transform _gazeTarget;
        private bool _useBuiltInMotionPack;
        private float _gazeHorizontal;
        private float _gazeVertical;
        private AvatarGazeMode _gazeMode = AvatarGazeMode.Cursor;
        private Vector3 _gazeTargetWorld;
        private bool _hasGazeTargetWorld;
        private readonly List<GameObject> _touchZones = new List<GameObject>();

        /// <summary>
        /// Built on first use rather than in Awake: the driver is added to the
        /// model and driven straight away, and Awake does not run for a
        /// component added outside play mode.
        /// </summary>
        private VrmExpressionComposer Composer
        {
            get
            {
                if (_composer == null)
                    _composer = new VrmExpressionComposer(WriteExpressionWeight);
                return _composer;
            }
        }

        private VrmEmotionBlender Emotions
        {
            get
            {
                if (_emotions == null)
                    _emotions = new VrmEmotionBlender(Composer);
                return _emotions;
            }
        }

        private VrmIdleAnimator Idle
        {
            get
            {
                if (_idle == null)
                    _idle = new VrmIdleAnimator(null);
                return _idle;
            }
        }

        private VrmBodyIdleAnimator BodyIdle
        {
            get
            {
                if (_bodyIdle == null)
                    _bodyIdle = new VrmBodyIdleAnimator(null);
                return _bodyIdle;
            }
        }

        private VrmVisemeAnimator Visemes
        {
            get
            {
                if (_visemes == null)
                    _visemes = new VrmVisemeAnimator(Composer);
                return _visemes;
            }
        }

        internal async Task<bool> InitializeAsync(
            Vrm10Instance vrm,
            AvatarCapabilities capabilities,
            bool useBuiltInMotionPack,
            int generation,
            AvatarSceneState scene)
        {
            _vrm = vrm;
            _runtime = _vrm != null ? _vrm.Runtime : null;
            _capabilities = capabilities ?? new AvatarCapabilities();
            _useBuiltInMotionPack = useBuiltInMotionPack;
            if (_capabilities.hasGaze &&
                _vrm.TryGetBoneTransform(HumanBodyBones.Head, out _head))
            {
                GameObject gazeObject = new GameObject("AvatarVRM_GazeTarget");
                _gazeTarget = gazeObject.transform;
                _vrm.LookAtTargetType =
                    VRM10ObjectLookAt.LookAtTargetTypes.SpecifiedTransform;
                _vrm.LookAtTarget = _gazeTarget;
            }

            // Touchable spheres on the reachable bones — every VRM is pettable.
            VrmTouchColliders.Build(_vrm.GetComponentInChildren<Animator>(), _touchZones);

            // Body idle is procedural (breathing + a planted weight shift + head
            // breaks), driven onto the control rig in Update() so EVERY VRM gets it
            // with no per-model rig. The idle authors only DELTAS from a neutral
            // standing pose — but a VRM control-rig's identity is the T-POSE (arms
            // out), so we seed that neutral "arms down" pose once from the built-in
            // idle clip's first frame (a normalized pose that retargets to any VRM)
            // and layer the deltas onto it. SetAnimation stays a no-op (the clip is
            // never played), so VrmAnimation is null and Process skips its retarget.
            await EnsureRestPoseAsync();
            return true;
        }

        /// <summary>
        /// The neutral standing pose (arms down) as normalized control-rig rotations,
        /// loaded once from the built-in idle clip's first frame and shared by every
        /// avatar. A VRM's control rig sits at the T-pose when untouched, so without
        /// this seed the arms splay out; the pose is normalized, so the built-in
        /// clip's values apply to any humanoid VRM.
        /// </summary>
        private async Task EnsureRestPoseAsync()
        {
            if (_restPoseLoaded)
                return;
            _restPoseLoaded = true;

            Vrm10AnimationInstance clip =
                await Avatar3DLoader.LoadBuiltInVrmAnimationAsync(IdleState);
            if (clip == null)
                return;

            try
            {
                // Sample the first frame so the provider reports the standing pose
                // rather than the T-pose the clip skeleton was built at.
                Animation clipAnimation = clip.GetComponentInChildren<Animation>(true);
                if (clipAnimation != null)
                {
                    foreach (AnimationState state in clipAnimation)
                    {
                        state.enabled = true;
                        state.weight = 1f;
                        state.time = 0f;
                        break;
                    }
                    clipAnimation.Sample();
                }

                INormalizedPoseProvider provider = clip.ControlRig.Item1;
                ITPoseProvider tpose = clip.ControlRig.Item2;
                if (provider != null && tpose != null)
                {
                    Dictionary<HumanBodyBones, Quaternion> pose =
                        new Dictionary<HumanBodyBones, Quaternion>();
                    foreach (var pair in tpose.EnumerateBoneParentPairs())
                        pose[pair.Bone] =
                            provider.GetNormalizedLocalRotation(pair.Bone, pair.Parent);
                    _sharedRestPose = pose;
                }
            }
            finally
            {
                DestroyOwnedObject(clip.gameObject);
            }
        }

        internal bool SetAnimation(string state)
        {
            if (_vrm == null || !_capabilities.canAnimate ||
                _idleAnimation == null || string.IsNullOrWhiteSpace(state))
                return false;

            string normalizedState = state.Trim().ToLowerInvariant();
            if (normalizedState != IdleState)
                return false;

            if (normalizedState == _activeState &&
                _activeAnimation != null &&
                _activeAnimation.isPlaying)
                return true;

            StopActiveAnimation();
            _idleAnimation.gameObject.SetActive(true);
            _activeVrmAnimation = _idleAnimation;
            _runtime.VrmAnimation = _idleAnimation;

            Animation animation =
                _idleAnimation.GetComponentInChildren<Animation>(true);
            if (animation == null)
            {
                StopActiveAnimation();
                return false;
            }

            foreach (AnimationState animationState in animation)
            {
                if (animationState == null || animationState.clip == null)
                    continue;
                animationState.wrapMode = WrapMode.Loop;
                animation.clip = animationState.clip;
                animation.Play();
                _activeAnimation = animation;
                _activeState = normalizedState;
                return true;
            }

            StopActiveAnimation();
            return false;
        }

        internal void SetGazeMode(AvatarGazeMode mode)
        {
            _gazeMode = mode;
            // A world point from the previous mode is stale the moment the mode
            // changes; drop it so None rests and a new source has to re-supply it.
            _hasGazeTargetWorld = false;
        }

        internal void SetGazeTarget(Vector3 worldPoint)
        {
            _gazeTargetWorld = worldPoint;
            _hasGazeTargetWorld = true;
        }

        internal void SetGazeNormalized(float horizontal, float vertical)
        {
            _gazeHorizontal = Mathf.Clamp(horizontal, -0.5f, 0.5f);
            _gazeVertical = Mathf.Clamp(vertical, -0.5f, 0.5f);
        }

        /// <summary>
        /// Where the look-at target sits this frame. The mode picks the base
        /// point — a rest point ahead of the face, a world point a source fed us,
        /// or the head-basis fallback for a source that only speaks normalized —
        /// and the idle saccades ride on top of all three.
        /// </summary>
        private Vector3 ResolveGazePosition()
        {
            Vector3 rest = _head.position + transform.forward * 3f;

            Vector3 basePoint;
            if (_gazeMode == AvatarGazeMode.None)
            {
                basePoint = rest;
            }
            else if (_hasGazeTargetWorld)
            {
                basePoint = _gazeTargetWorld;
            }
            else
            {
                basePoint = rest +
                    transform.right * _gazeHorizontal * 1.2f +
                    transform.up * _gazeVertical * 0.8f;
            }

            return basePoint +
                transform.right * Idle.GazeOffsetHorizontal * 1.2f +
                transform.up * Idle.GazeOffsetVertical * 0.8f;
        }

        internal bool SetMouthShape(string shape, float weight)
        {
            if (_vrm == null || !_capabilities.hasLipsync)
                return false;

            string normalized = (shape ?? string.Empty).Trim().ToUpperInvariant();
            if (normalized == "SILENCE" || normalized.Length == 0)
            {
                ClearMouth();
                return true;
            }

            ExpressionKey key;
            if (!TryResolveMouthKey(normalized, out key))
                return false;

            // The animator owns the mouth from here: it eases the new viseme in,
            // releases the last one, and keeps at most two voiced at once.
            Visemes.SetShape(key, weight);
            return true;
        }

        internal void ClearMouth()
        {
            Visemes.Clear();
        }

        internal bool SetExpression(string expressionName, float weight)
        {
            if (_vrm == null || !_capabilities.hasExpressions)
                return false;

            ExpressionKey key;
            if (!TryResolveExpressionKey(expressionName, out key))
                return false;

            // A zero stays on the layer rather than dropping off it: callers
            // wind a reaction back down with SetExpression(name, 0f) and expect
            // that to hold, not to hand the key back to something else.
            Composer.Set(VrmExpressionLayer.Manual, key, Mathf.Clamp01(weight));
            return true;
        }

        internal bool SetEmotion(string emotionName)
        {
            if (_vrm == null || !_capabilities.hasExpressions)
                return false;

            AvatarEmotion emotion;
            if (!VrmEmotionPalette.TryParse(emotionName, out emotion))
                return false;

            Emotions.SetEmotion(emotion);
            return true;
        }

        private bool TryResolveMouthKey(string shape, out ExpressionKey key)
        {
            VRM10ObjectExpression expressions = _vrm.Vrm.Expression;
            if (shape == "A" && expressions.Aa != null)
            {
                key = ExpressionKey.Aa;
                return true;
            }
            if (shape == "E" && expressions.Ee != null)
            {
                key = ExpressionKey.Ee;
                return true;
            }
            if (shape == "I" && expressions.Ih != null)
            {
                key = ExpressionKey.Ih;
                return true;
            }
            if (shape == "O" && expressions.Oh != null)
            {
                key = ExpressionKey.Oh;
                return true;
            }
            if (shape == "U" && expressions.Ou != null)
            {
                key = ExpressionKey.Ou;
                return true;
            }

            if (expressions.Aa != null) key = ExpressionKey.Aa;
            else if (expressions.Ee != null) key = ExpressionKey.Ee;
            else if (expressions.Ih != null) key = ExpressionKey.Ih;
            else if (expressions.Oh != null) key = ExpressionKey.Oh;
            else if (expressions.Ou != null) key = ExpressionKey.Ou;
            else
            {
                key = ExpressionKey.Aa;
                return false;
            }
            return true;
        }

        private bool TryResolveExpressionKey(
            string expressionName,
            out ExpressionKey key)
        {
            string normalized = (expressionName ?? string.Empty).Trim().ToLowerInvariant();
            VRM10ObjectExpression expressions = _vrm.Vrm.Expression;
            if ((normalized == "happy" || normalized == "smile") &&
                expressions.Happy != null)
            {
                key = ExpressionKey.Happy;
                return true;
            }
            if ((normalized == "happy" || normalized == "smile") &&
                expressions.Relaxed != null)
            {
                key = ExpressionKey.Relaxed;
                return true;
            }
            if (normalized == "angry" && expressions.Angry != null)
            {
                key = ExpressionKey.Angry;
                return true;
            }
            if (normalized == "sad" && expressions.Sad != null)
            {
                key = ExpressionKey.Sad;
                return true;
            }
            if (normalized == "relaxed" && expressions.Relaxed != null)
            {
                key = ExpressionKey.Relaxed;
                return true;
            }
            if ((normalized == "surprised" || normalized == "confused") &&
                expressions.Surprised != null)
            {
                key = ExpressionKey.Surprised;
                return true;
            }
            if (expressions.CustomClips != null)
            {
                for (int i = 0; i < expressions.CustomClips.Count; i++)
                {
                    VRM10Expression custom = expressions.CustomClips[i];
                    if (custom != null &&
                        string.Equals(custom.name, normalized, StringComparison.OrdinalIgnoreCase))
                    {
                        key = ExpressionKey.CreateCustom(custom.name);
                        return true;
                    }
                }
            }

            key = ExpressionKey.Happy;
            return false;
        }

        private void Update()
        {
            // Body idle writes the control rig here, in the Update phase, so it lands
            // before Vrm10Instance.Process() runs in LateUpdate and springBone (hair,
            // cloth) follows the same frame. VrmAnimation stays null, so Process skips
            // its retarget step and only maps what we set here.
            if (_vrm == null || _runtime == null || _runtime.ControlRig == null)
                return;

            BodyIdle.Tick(Time.unscaledDeltaTime);
            IReadOnlyDictionary<HumanBodyBones, Quaternion> pose = BodyIdle.Pose;

            // Drive every captured bone at its absolute normalized local rotation —
            // exactly what Vrm10Retarget does from a VRM animation, but fed from the
            // smoothed baked capture. Normalized rotations apply to any VRM as-is.
            foreach (KeyValuePair<HumanBodyBones, Quaternion> entry in pose)
            {
                Transform controlBone = _runtime.ControlRig.GetBoneTransform(entry.Key);
                if (controlBone != null)
                    controlBone.localRotation = entry.Value;
            }

            // Seat any humanoid bone the capture doesn't cover at the neutral rest.
            if (_sharedRestPose != null)
            {
                foreach (KeyValuePair<HumanBodyBones, Quaternion> rest in _sharedRestPose)
                {
                    if (pose.ContainsKey(rest.Key))
                        continue;
                    Transform controlBone = _runtime.ControlRig.GetBoneTransform(rest.Key);
                    if (controlBone != null)
                        controlBone.localRotation = rest.Value;
                }
            }

            // The avatar root is left exactly where it was placed — a standard VRM's
            // armature root already sits on the floor, so posing the standard bones is
            // all that's needed. (Earlier grounding code moved the root and was itself
            // what lifted her off the floor.)
        }

        private void LateUpdate()
        {
            float deltaTime = Time.unscaledDeltaTime;

            // Advance the involuntary layer first: the gaze target built just
            // below wants this frame's saccade offset folded in.
            Idle.Tick(deltaTime);

            if (_gazeTarget != null && _head != null)
                _gazeTarget.position = ResolveGazePosition();

            if (_vrm == null)
                return;

            UpdateBlink();
            if (_emotions != null)
                _emotions.Tick(deltaTime);
            if (_visemes != null)
                _visemes.Tick(deltaTime);

            // Everything above only declared what it wants. This is the one
            // place a frame's worth of intent reaches the model.
            Composer.Apply();

            // Foot IK runs last, on the rendered model bones, after Process() has posed
            // them — it plants the feet the captured rotations would otherwise drift.
            ApplyFootIk();
        }

        private void ApplyFootIk()
        {
            if (FootIkWeight <= 0f || _vrm == null)
                return;
            if (!_legsCached)
            {
                _legsCached = true;
                // The mesh is skinned to the Animator's humanoid bones (what Process
                // writes to), NOT the control-rig proxy that _vrm.TryGetBoneTransform
                // returns — editing the proxy after Process never reaches the render.
                Animator animator = GetComponentInChildren<Animator>(true);
                if (animator != null && animator.isHuman)
                {
                    _lUpperLeg = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
                    _lLowerLeg = animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
                    _lFoot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
                    _rUpperLeg = animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
                    _rLowerLeg = animator.GetBoneTransform(HumanBodyBones.RightLowerLeg);
                    _rFoot = animator.GetBoneTransform(HumanBodyBones.RightFoot);
                }
            }
            if (_lFoot == null || _rFoot == null)
                return;

            // First pass: remember where the feet naturally rest, stored in the avatar
            // root's LOCAL space so the lock turns WITH the model when it is spun (a
            // world-space lock left the feet planted while the body twisted around them).
            if (!_footRestCaptured)
            {
                _footRestCaptured = true;
                _lFootRestPos = transform.InverseTransformPoint(_lFoot.position);
                _lFootRestRot = Quaternion.Inverse(transform.rotation) * _lFoot.rotation;
                _rFootRestPos = transform.InverseTransformPoint(_rFoot.position);
                _rFootRestRot = Quaternion.Inverse(transform.rotation) * _rFoot.rotation;
                return;
            }

            SolveLeg(_lUpperLeg, _lLowerLeg, _lFoot, _lFootRestPos, _lFootRestRot);
            SolveLeg(_rUpperLeg, _rLowerLeg, _rFoot, _rFootRestPos, _rFootRestRot);
        }

        private void SolveLeg(Transform upper, Transform lower, Transform foot,
            Vector3 restLocalPos, Quaternion restLocalRot)
        {
            if (upper == null || lower == null || foot == null)
                return;
            // Convert the root-relative lock back to world for this frame's orientation.
            Vector3 restPos = transform.TransformPoint(restLocalPos);
            Quaternion restRot = transform.rotation * restLocalRot;
            Vector3 goal = Vector3.Lerp(foot.position, restPos, FootIkWeight);
            // Pole a step in front of the hip so the knee always bends the way the
            // avatar faces, even when the leg is nearly straight.
            SolveTwoBoneIk(upper, lower, foot, goal, upper.position + transform.forward);
            foot.rotation = Quaternion.Slerp(foot.rotation, restRot, FootIkWeight);
        }

        // Analytic two-bone IK: places the knee by law of cosines with a pole for the
        // bend direction, then aligns the two bones. Operates in world space on the
        // rendered bones.
        private static void SolveTwoBoneIk(Transform a, Transform b, Transform c,
            Vector3 target, Vector3 pole)
        {
            Vector3 aPos = a.position;
            float lab = Vector3.Distance(aPos, b.position);
            float lcb = Vector3.Distance(b.position, c.position);
            Vector3 toTarget = target - aPos;
            float dist = toTarget.magnitude;
            if (dist < 1e-5f || lab < 1e-5f || lcb < 1e-5f)
                return;
            float lat = Mathf.Clamp(dist, Mathf.Abs(lab - lcb) + 1e-3f, lab + lcb - 1e-3f);
            Vector3 dir = toTarget / dist;

            float cosA = Mathf.Clamp((lab * lab + lat * lat - lcb * lcb) / (2f * lab * lat), -1f, 1f);
            float along = lab * cosA;
            float perpLen = lab * Mathf.Sqrt(Mathf.Max(0f, 1f - cosA * cosA));

            Vector3 poleDir = pole - aPos;
            Vector3 perp = poleDir - Vector3.Dot(poleDir, dir) * dir;
            if (perp.sqrMagnitude < 1e-8f)
            {
                perp = Vector3.Cross(dir, Vector3.up);
                if (perp.sqrMagnitude < 1e-8f)
                    perp = Vector3.Cross(dir, Vector3.forward);
            }
            perp.Normalize();

            Vector3 kneePos = aPos + dir * along + perp * perpLen;

            // Upper bone: aim the hip so the knee reaches kneePos.
            Vector3 curUpper = b.position - aPos;
            Vector3 newUpper = kneePos - aPos;
            if (curUpper.sqrMagnitude > 1e-8f && newUpper.sqrMagnitude > 1e-8f)
                a.rotation = Quaternion.FromToRotation(curUpper, newUpper) * a.rotation;

            // Lower bone: aim the knee so the ankle reaches the target.
            Vector3 curLower = c.position - b.position;
            Vector3 newLower = target - b.position;
            if (curLower.sqrMagnitude > 1e-8f && newLower.sqrMagnitude > 1e-8f)
                b.rotation = Quaternion.FromToRotation(curLower, newLower) * b.rotation;
        }

        private void UpdateBlink()
        {
            if (!_capabilities.hasBlink)
                return;

            float weight = Idle.BlinkWeight;
            if (weight <= 0f)
            {
                // Eyes open. Release the layer rather than pin the lids at zero,
                // so an emotion squinting underneath gets the eyelid back.
                Composer.ClearLayer(VrmExpressionLayer.Blink);
                return;
            }

            Composer.Set(VrmExpressionLayer.Blink, ExpressionKey.Blink, weight);
            Composer.Set(VrmExpressionLayer.Blink, ExpressionKey.BlinkLeft, weight);
            Composer.Set(VrmExpressionLayer.Blink, ExpressionKey.BlinkRight, weight);
        }

        private void ClearAnimation()
        {
            StopActiveAnimation();
            ClearAnimationResources();
        }

        private void StopActiveAnimation()
        {
            if (_runtime != null && _useBuiltInMotionPack)
                _runtime.VrmAnimation = null;
            if (_activeAnimation != null)
                _activeAnimation.Stop();
            if (_activeVrmAnimation != null)
                _activeVrmAnimation.gameObject.SetActive(false);
            _activeAnimation = null;
            _activeVrmAnimation = null;
            _activeState = null;
        }

        private void ClearAnimationResources()
        {
            if (_idleAnimation != null)
            {
                DestroyOwnedObject(_idleAnimation.gameObject);
                _idleAnimation = null;
            }
        }

        /// <summary>
        /// The composer's outlet to the model. A playing VRM animation routes
        /// expression tracks through its own setter map, so a weight written
        /// past it would be overwritten; resolving the route per write keeps it
        /// correct across animation changes.
        /// </summary>
        private void WriteExpressionWeight(ExpressionKey key, float weight)
        {
            Action<float> setter;
            if (_activeVrmAnimation != null &&
                _activeVrmAnimation.ExpressionSetterMap.TryGetValue(key, out setter))
            {
                setter(weight);
                return;
            }

            if (_runtime != null)
                _runtime.Expression.SetWeight(key, weight);
        }

        private void OnDestroy()
        {
            // No further frame will flush, so shut the mouth outright rather than
            // starting a release that never completes.
            if (_visemes != null)
                _visemes.ForceSilence();
            if (_composer != null)
                _composer.Apply();
            ClearAnimation();
            if (_gazeTarget != null)
                DestroyOwnedObject(_gazeTarget.gameObject);
            for (int i = 0; i < _touchZones.Count; i++)
                DestroyOwnedObject(_touchZones[i]);
            _touchZones.Clear();
        }

        private static void DestroyOwnedObject(UnityEngine.Object value)
        {
            if (value == null)
                return;
            if (Application.isPlaying)
                Destroy(value);
            else
                DestroyImmediate(value);
        }
    }
}
