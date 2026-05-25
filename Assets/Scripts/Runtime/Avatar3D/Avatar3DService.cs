using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace NeonCompanion.Runtime.Avatar3D
{
    public sealed class Avatar3DService : IAvatar3DService
    {
        private GameObject _runtimeRoot;
        private Animator _animator;
        private Animation _legacyAnimation;
        private readonly List<string> _availableAnimations = new List<string>();

        public bool IsLoaded => _runtimeRoot != null;
        public IReadOnlyList<string> AvailableAnimations => _availableAnimations;

        public async Task<bool> LoadAvatar(string modelPath)
        {
            Unload();

            var loadResult = await Avatar3DLoader.LoadAsync(modelPath);
            if (!loadResult.Success || loadResult.Instance == null)
                return false;

            _runtimeRoot = loadResult.Instance;
            _animator = _runtimeRoot.GetComponentInChildren<Animator>(true);
            _legacyAnimation = _runtimeRoot.GetComponentInChildren<Animation>(true);

            _availableAnimations.Clear();
            _availableAnimations.AddRange(loadResult.AnimationNames);

            return true;
        }

        public bool SetAnimation(string clipName)
        {
            if (!IsLoaded || string.IsNullOrWhiteSpace(clipName))
                return false;

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

            if (_runtimeRoot != null)
            {
                UnityEngine.Object.Destroy(_runtimeRoot);
                _runtimeRoot = null;
            }
        }
    }
}
