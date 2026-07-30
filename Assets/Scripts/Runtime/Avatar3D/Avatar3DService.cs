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
            return _scene.CanMutate && _vrmDriver != null &&
                _vrmDriver.SetMouthShape(shape);
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
        private ExpressionKey _activeMouthKey;
        private Action<float> _activeMouthSetter;
        private bool _hasActiveMouth;
        private float _blinkTimer;
        private float _blinkWeight;
        private Transform _head;
        private Transform _gazeTarget;
        private bool _useBuiltInMotionPack;
        private float _gazeHorizontal;
        private float _gazeVertical;

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
            _blinkTimer = 2.5f;
            if (_capabilities.hasGaze &&
                _vrm.TryGetBoneTransform(HumanBodyBones.Head, out _head))
            {
                GameObject gazeObject = new GameObject("AvatarVRM_GazeTarget");
                _gazeTarget = gazeObject.transform;
                _vrm.LookAtTargetType =
                    VRM10ObjectLookAt.LookAtTargetTypes.SpecifiedTransform;
                _vrm.LookAtTarget = _gazeTarget;
            }

            if (!_useBuiltInMotionPack)
                return true;
            if (_runtime == null || _runtime.ControlRig == null)
            {
                Debug.LogWarning(
                    "[NeonCompanion] Built-in VRM has no runtime control rig; " +
                    "continuing without body motion.");
                return true;
            }

            Vrm10AnimationInstance animation =
                await Avatar3DLoader.LoadBuiltInVrmAnimationAsync(IdleState);

            // The scene was torn down while the clip was loading, so this clip
            // has no owner: drop it here, because OnDestroy has already run.
            if (!scene.IsCurrent(generation))
            {
                if (animation != null)
                    DestroyOwnedObject(animation.gameObject);
                return false;
            }

            // A missing or malformed idle must not sink the avatar: the face,
            // gaze and lipsync are what the companion is actually built on.
            if (animation == null ||
                animation.ControlRig.Item1 == null ||
                animation.ControlRig.Item2 == null)
            {
                if (animation != null)
                    DestroyOwnedObject(animation.gameObject);
                Debug.LogWarning(
                    "[NeonCompanion] Built-in idle animation is unusable; " +
                    "continuing without body motion.");
                return true;
            }

            animation.gameObject.name = "AvatarVRMA_" + IdleState;
            animation.transform.SetParent(transform.parent, false);
            if (animation.BoxMan != null)
                animation.BoxMan.enabled = false;
            animation.gameObject.SetActive(false);
            _idleAnimation = animation;
            return true;
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
            BindActiveMouthSetter();

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

        internal void SetGazeNormalized(float horizontal, float vertical)
        {
            _gazeHorizontal = Mathf.Clamp(horizontal, -0.5f, 0.5f);
            _gazeVertical = Mathf.Clamp(vertical, -0.5f, 0.5f);
        }

        internal bool SetMouthShape(string shape)
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
            ClearMouth();
            _activeMouthKey = key;
            _hasActiveMouth = true;
            BindActiveMouthSetter();
            ApplyActiveMouth(1f);
            return true;
        }

        internal void ClearMouth()
        {
            if (_vrm != null && _hasActiveMouth)
                ApplyActiveMouth(0f);
            _hasActiveMouth = false;
            _activeMouthSetter = null;
        }

        internal bool SetExpression(string expressionName, float weight)
        {
            if (_vrm == null || !_capabilities.hasExpressions)
                return false;

            ExpressionKey key;
            if (!TryResolveExpressionKey(expressionName, out key))
                return false;
            ApplyExpressionWeight(key, Mathf.Clamp01(weight));
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

        private void LateUpdate()
        {
            if (_gazeTarget != null && _head != null)
            {
                _gazeTarget.position = _head.position +
                    transform.forward * 3f +
                    transform.right * _gazeHorizontal * 1.2f +
                    transform.up * _gazeVertical * 0.8f;
            }

            if (_vrm == null)
                return;

            if (_hasActiveMouth)
                ApplyActiveMouth(1f);

            if (!_capabilities.hasBlink)
                return;

            _blinkTimer -= Time.unscaledDeltaTime;
            if (_blinkTimer > 0f)
                return;

            _blinkWeight += Time.unscaledDeltaTime * 10f;
            float weight = _blinkWeight <= 1f ? _blinkWeight : 2f - _blinkWeight;
            float clampedWeight = Mathf.Clamp01(weight);
            ApplyExpressionWeight(ExpressionKey.Blink, clampedWeight);
            ApplyExpressionWeight(ExpressionKey.BlinkLeft, clampedWeight);
            ApplyExpressionWeight(ExpressionKey.BlinkRight, clampedWeight);
            if (_blinkWeight >= 2f)
            {
                ApplyExpressionWeight(ExpressionKey.Blink, 0f);
                ApplyExpressionWeight(ExpressionKey.BlinkLeft, 0f);
                ApplyExpressionWeight(ExpressionKey.BlinkRight, 0f);
                _blinkWeight = 0f;
                _blinkTimer = UnityEngine.Random.Range(2.5f, 5.5f);
            }
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
            _activeMouthSetter = null;
        }

        private void ClearAnimationResources()
        {
            if (_idleAnimation != null)
            {
                DestroyOwnedObject(_idleAnimation.gameObject);
                _idleAnimation = null;
            }
        }

        private void BindActiveMouthSetter()
        {
            _activeMouthSetter = null;
            if (!_hasActiveMouth || _activeVrmAnimation == null)
                return;

            Action<float> setter;
            if (_activeVrmAnimation.ExpressionSetterMap.TryGetValue(
                _activeMouthKey,
                out setter))
                _activeMouthSetter = setter;
        }

        private void ApplyActiveMouth(float weight)
        {
            if (_activeMouthSetter != null)
                _activeMouthSetter(weight);
            else
                _runtime.Expression.SetWeight(_activeMouthKey, weight);
        }

        private void ApplyExpressionWeight(ExpressionKey key, float weight)
        {
            Action<float> setter;
            if (_activeVrmAnimation != null &&
                _activeVrmAnimation.ExpressionSetterMap.TryGetValue(key, out setter))
            {
                setter(weight);
                return;
            }
            _runtime.Expression.SetWeight(key, weight);
        }

        private void OnDestroy()
        {
            ClearMouth();
            ClearAnimation();
            if (_gazeTarget != null)
                DestroyOwnedObject(_gazeTarget.gameObject);
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
