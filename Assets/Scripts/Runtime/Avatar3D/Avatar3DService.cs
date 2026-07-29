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

        public bool IsLoaded => _runtimeRoot != null;
        public IReadOnlyList<string> AvailableAnimations => _availableAnimations;
        public AvatarCapabilities Capabilities => _capabilities;

        public async Task<bool> LoadAvatar(string modelPath)
        {
            var loadResult = await Avatar3DLoader.LoadAsync(modelPath);
            if (!loadResult.Success || loadResult.Instance == null)
                return false;

            Unload();
            _runtimeRoot = loadResult.Instance;
            _animator = _runtimeRoot.GetComponentInChildren<Animator>(true);
            _legacyAnimation = _runtimeRoot.GetComponentInChildren<Animation>(true);
            _capabilities = loadResult.Capabilities ?? new AvatarCapabilities();
            if (loadResult.VrmInstance != null)
            {
                _vrmDriver = _runtimeRoot.AddComponent<VrmAvatarDriver>();
                _vrmDriver.Initialize(
                    loadResult.VrmInstance,
                    _capabilities,
                    BuiltInAvatarProfiles.IsResourcePath(modelPath));
            }

            _availableAnimations.Clear();
            _availableAnimations.AddRange(loadResult.AnimationNames);

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
            return _vrmDriver != null && _vrmDriver.SetMouthShape(shape);
        }

        public void ClearMouth()
        {
            if (_vrmDriver != null)
                _vrmDriver.ClearMouth();
        }

        public bool SetExpression(string expressionName, float weight)
        {
            return _vrmDriver != null && _vrmDriver.SetExpression(expressionName, weight);
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
            _availableAnimations.Clear();
            _animator = null;
            _legacyAnimation = null;
            _vrmDriver = null;
            _capabilities = new AvatarCapabilities();

            if (_runtimeRoot != null)
            {
                UnityEngine.Object.Destroy(_runtimeRoot);
                _runtimeRoot = null;
            }
        }
    }

    [DefaultExecutionOrder(10000)]
    internal sealed class VrmAvatarDriver : MonoBehaviour
    {
        private Vrm10Instance _vrm;
        private AvatarCapabilities _capabilities;
        private GameObject _animationRoot;
        private Animation _activeAnimation;
        private Vrm10AnimationInstance _activeVrmAnimation;
        private string _activeState;
        private bool _reactionPlaying;
        private ExpressionKey _activeMouthKey;
        private Action<float> _activeMouthSetter;
        private bool _hasActiveMouth;
        private float _blinkTimer;
        private float _blinkWeight;
        private Transform _head;
        private Transform _gazeTarget;
        private bool _useBuiltInMotionPack;

        internal void Initialize(
            Vrm10Instance vrm,
            AvatarCapabilities capabilities,
            bool useBuiltInMotionPack)
        {
            _vrm = vrm;
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
        }

        internal bool SetAnimation(string state)
        {
            if (_vrm == null || !_capabilities.canAnimate ||
                !_useBuiltInMotionPack || string.IsNullOrWhiteSpace(state))
                return false;

            string normalizedState = state.Trim().ToLowerInvariant();
            bool requestedReaction =
                normalizedState == "smile" || normalizedState == "confused";
            if (!requestedReaction &&
                normalizedState == _activeState &&
                _activeAnimation != null &&
                _activeAnimation.isPlaying)
                return true;

            GameObject prefab = Resources.Load<GameObject>(
                "Avatars/neon/Neon_" + normalizedState);
            if (prefab == null)
                return false;

            ClearAnimation();
            _animationRoot = Instantiate(prefab);
            _animationRoot.name = "AvatarVRMA_" + state;
            _animationRoot.transform.SetParent(transform.parent, false);

            Vrm10AnimationInstance animationInstance =
                _animationRoot.GetComponentInChildren<Vrm10AnimationInstance>(true);
            if (animationInstance == null)
            {
                ClearAnimation();
                return false;
            }

            if (animationInstance.BoxMan != null)
                animationInstance.BoxMan.enabled = false;
            _activeVrmAnimation = animationInstance;
            _vrm.Runtime.VrmAnimation = animationInstance;
            BindActiveMouthSetter();

            Animation animation = _animationRoot.GetComponentInChildren<Animation>(true);
            if (animation == null)
            {
                ClearAnimation();
                return false;
            }

            foreach (AnimationState animationState in animation)
            {
                if (animationState == null || animationState.clip == null)
                    continue;
                animationState.wrapMode =
                    requestedReaction ? WrapMode.Once : WrapMode.Loop;
                animation.clip = animationState.clip;
                animation.Play();
                _activeAnimation = animation;
                _activeState = normalizedState;
                _reactionPlaying = requestedReaction;
                return true;
            }

            ClearAnimation();
            return false;
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
            if (_reactionPlaying && _activeAnimation != null &&
                !_activeAnimation.isPlaying)
            {
                _reactionPlaying = false;
                if (!SetAnimation("idle"))
                    ClearAnimation();
            }

            if (_gazeTarget != null && _head != null)
            {
                float normalizedX = Screen.width > 0
                    ? (Input.mousePosition.x / Screen.width) - 0.5f
                    : 0f;
                float normalizedY = Screen.height > 0
                    ? (Input.mousePosition.y / Screen.height) - 0.5f
                    : 0f;
                _gazeTarget.position = _head.position +
                    transform.forward * 3f +
                    transform.right * normalizedX * 1.2f +
                    transform.up * normalizedY * 0.8f;
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
            _activeAnimation = null;
            _activeVrmAnimation = null;
            _activeState = null;
            _activeMouthSetter = null;
            _reactionPlaying = false;
            if (_vrm != null && _useBuiltInMotionPack)
                _vrm.Runtime.VrmAnimation = null;
            if (_animationRoot != null)
            {
                Destroy(_animationRoot);
                _animationRoot = null;
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
                _vrm.Runtime.Expression.SetWeight(_activeMouthKey, weight);
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
            _vrm.Runtime.Expression.SetWeight(key, weight);
        }

        private void OnDestroy()
        {
            ClearMouth();
            ClearAnimation();
            if (_gazeTarget != null)
                Destroy(_gazeTarget.gameObject);
        }
    }
}
