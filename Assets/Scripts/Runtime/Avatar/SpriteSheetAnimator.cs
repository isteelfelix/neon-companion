using System;
using System.Collections.Generic;
using System.Linq;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;
using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.Avatar
{
    public sealed class SpriteSheetAnimator : MonoBehaviour
    {
        private const float PendingTransitionSpeedMultiplier = 1.4f;

        private readonly Dictionary<string, ClipRuntime> _clips = new Dictionary<string, ClipRuntime>(StringComparer.OrdinalIgnoreCase);
        private Image _targetImage;
        private ClipRuntime _activeClip;
        private float _frameTimer;
        private int _frameIndex;
        private bool _isPlaying;
        private bool _isPlayingOneShot;
        private bool _pingPongForward = true;
        private Action _onClipCompleted;
        private string _pendingClipName;
        private Action _pendingClipCompleted;
        private bool _pendingClipIsOneShot;
        private bool _pendingClipPingPong;
        private bool _isOneShotPingPong;
        private bool _isOneShotReturning;

        public void Configure(IReadOnlyList<SpriteSheetAnimation> clips, Image targetImage)
        {
            ReleaseAllLoadedFrames();
            _clips.Clear();
            _targetImage = targetImage;
            _activeClip = null;
            _frameTimer = 0f;
            _frameIndex = 0;
            _isPlaying = false;
            _isPlayingOneShot = false;
            ClearDeferredPlaybackState();

            if (targetImage == null || clips == null)
                return;

            for (int i = 0; i < clips.Count; i++)
            {
                var clip = clips[i];
                if (clip == null || string.IsNullOrWhiteSpace(clip.clipName))
                    continue;

                _clips[clip.clipName] = new ClipRuntime(clip);
            }

            if (_clips.Count == 0)
                return;

            string defaultClip = _clips.ContainsKey("idle") ? "idle" : _clips.Keys.First();
            SetClip(defaultClip);
            Play(defaultClip);
        }

        public void SetClip(string clipName)
        {
            if (string.IsNullOrWhiteSpace(clipName))
                return;

            if (!_clips.TryGetValue(clipName, out var clip))
                return;
            if (!EnsureFrames(clip))
                return;

            ClipRuntime previousClip = _activeClip;
            _onClipCompleted = null;
            _isPlayingOneShot = false;
            ClearDeferredPlaybackState();
            _activeClip = clip;
            _frameTimer = 0f;
            _frameIndex = 0;
            _pingPongForward = true;
            ApplyFrame();
            if (previousClip != null && previousClip != _activeClip)
                ReleaseFrames(previousClip);
        }

        public void Play(string clipName)
        {
            if (!string.IsNullOrWhiteSpace(clipName))
            {
                if (HasPendingOneShot())
                {
                    _isPlaying = true;
                    return;
                }

                if (_activeClip != null &&
                    string.Equals(_activeClip.Config.clipName, clipName, StringComparison.OrdinalIgnoreCase) &&
                    !_isPlayingOneShot)
                {
                    ClearPendingClipRequest();
                    _isPlaying = true;
                    return;
                }

                if (TryQueueSmoothAmbientTransition(clipName))
                    return;

                SetClip(clipName);
            }

            if (_activeClip == null)
                return;

            _isPlaying = true;
        }

        public void Stop()
        {
            _isPlaying = false;
            _isPlayingOneShot = false;
            _onClipCompleted = null;
            ClearDeferredPlaybackState();
            _frameTimer = 0f;
            _frameIndex = 0;
            ApplyFrame();
        }

        public bool HasClip(string clipName)
        {
            return !string.IsNullOrWhiteSpace(clipName) && _clips.ContainsKey(clipName);
        }

        public bool HasAnyClips => _clips.Count > 0;
        public bool IsPlayingOneShot => _isPlayingOneShot || HasPendingOneShot();

        /// <summary>
        /// Pauses playback and pins a specific frame from the named clip.
        /// Used by LipsyncController to drive mouth-shape frames directly.
        /// </summary>
        public void ShowFrame(string clipName, int frameIndex)
        {
            if (string.IsNullOrWhiteSpace(clipName) || !_clips.TryGetValue(clipName, out var clip))
                return;
            if (!EnsureFrames(clip))
                return;

            ClipRuntime previousClip = _activeClip;
            _activeClip = clip;
            _isPlaying = false;
            _isPlayingOneShot = false;
            var callback = _onClipCompleted;
            _onClipCompleted = null;
            ClearDeferredPlaybackState();
            _frameTimer = 0f;
            _frameIndex = Mathf.Clamp(frameIndex, 0, clip.Frames.Length - 1);
            ApplyFrame();
            if (previousClip != null && previousClip != _activeClip)
                ReleaseFrames(previousClip);
            if (callback != null)
                callback();
        }

        /// <summary>
        /// Registers a single clip at runtime without replacing existing clips.
        /// Used by LipsyncController to inject the lipsync clip from AvatarProfile.
        /// </summary>
        public void RegisterClip(SpriteSheetAnimation clip)
        {
            if (clip == null || string.IsNullOrWhiteSpace(clip.clipName) || _targetImage == null)
                return;

            ClipRuntime previous;
            if (_clips.TryGetValue(clip.clipName, out previous) &&
                previous != _activeClip)
                ReleaseFrames(previous);
            _clips[clip.clipName] = new ClipRuntime(clip);
        }

        public bool PlayOneShot(string clipName, Action onComplete, bool pingPongToStart = false)
        {
            if (string.IsNullOrWhiteSpace(clipName))
                return false;

            if (!_clips.TryGetValue(clipName, out var clip))
                return false;

            if (TryQueueSmoothOneShotTransition(clipName, onComplete, pingPongToStart))
                return true;

            return StartOneShotPlayback(clipName, clip, onComplete, pingPongToStart);
        }

        private void Update()
        {
            if (!_isPlaying || _activeClip == null || _targetImage == null)
                return;

            _frameTimer += Time.unscaledDeltaTime;

            while (true)
            {
                float fps = Mathf.Max(0.01f, _activeClip.Config.frameRate);
                if (HasPendingClipRequest())
                    fps *= PendingTransitionSpeedMultiplier;

                float frameDuration = 1f / fps;
                if (_frameTimer < frameDuration)
                    break;

                _frameTimer -= frameDuration;

                if (_isPlayingOneShot && _isOneShotPingPong)
                {
                    AdvanceOneShotPingPong();
                }
                else if (_activeClip.Config.pingPong &&
                         _activeClip.Config.loop &&
                         !_isPlayingOneShot)
                {
                    AdvanceLoopPingPong();
                }
                else
                {
                    AdvanceStandardPlayback();
                }

                ApplyFrame();

                if (TrySwitchToPendingClip())
                    continue;

                if (!_isPlaying)
                    break;
            }
        }

        private void AdvanceLoopPingPong()
        {
            if (_pingPongForward)
            {
                _frameIndex++;
                if (_frameIndex >= _activeClip.Frames.Length)
                {
                    _pingPongForward = false;
                    // Step back from last frame to avoid repeating it.
                    _frameIndex = Mathf.Max(0, _activeClip.Frames.Length - 2);
                }
            }
            else
            {
                _frameIndex--;
                if (_frameIndex < 0)
                {
                    _pingPongForward = true;
                    // Step forward from first frame to avoid repeating it.
                    _frameIndex = Mathf.Min(1, _activeClip.Frames.Length - 1);
                }
            }
        }

        private void AdvanceOneShotPingPong()
        {
            if (!_isOneShotReturning)
            {
                _frameIndex++;
                if (_frameIndex >= _activeClip.Frames.Length)
                {
                    _isOneShotReturning = true;
                    // Step back from last frame to avoid repeating it.
                    _frameIndex = Mathf.Max(0, _activeClip.Frames.Length - 2);
                }
                return;
            }

            _frameIndex--;
            if (_frameIndex <= 0)
            {
                _frameIndex = 0;
                CompleteCurrentClip();
            }
        }

        private void AdvanceStandardPlayback()
        {
            _frameIndex++;
            if (_frameIndex >= _activeClip.Frames.Length)
            {
                bool shouldLoop = _activeClip.Config.loop && !_isPlayingOneShot;
                if (shouldLoop)
                {
                    _frameIndex = 0;
                }
                else
                {
                    _frameIndex = _activeClip.Frames.Length - 1;
                    CompleteCurrentClip();
                }
            }
        }

        private bool TryQueueSmoothAmbientTransition(string clipName)
        {
            if (_activeClip == null || !_isPlaying || _isPlayingOneShot)
                return false;

            if (!_clips.TryGetValue(clipName, out var targetClip))
                return false;

            if (string.Equals(_activeClip.Config.clipName, targetClip.Config.clipName, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!IsSmoothAmbientClip(_activeClip.Config) || !IsSmoothAmbientClip(targetClip.Config))
                return false;

            if (_frameIndex == 0)
                return false;

            ClearPendingClipRequest();
            _pendingClipName = clipName;
            _isPlaying = true;
            return true;
        }

        private bool TrySwitchToPendingClip()
        {
            if (string.IsNullOrWhiteSpace(_pendingClipName) || _activeClip == null)
                return false;

            if (_frameIndex != 0)
                return false;

            if (_activeClip.Frames.Length > 1 && _pingPongForward)
                return false;

            string clipName = _pendingClipName;
            Action onComplete = _pendingClipCompleted;
            bool isOneShot = _pendingClipIsOneShot;
            bool pingPongToStart = _pendingClipPingPong;
            ClearPendingClipRequest();

            if (isOneShot)
            {
                if (!_clips.TryGetValue(clipName, out var clip))
                    return false;

                return StartOneShotPlayback(clipName, clip, onComplete, pingPongToStart);
            }

            SetClip(clipName);
            _isPlaying = true;
            return true;
        }

        private void CompleteCurrentClip()
        {
            _isPlaying = false;
            _isPlayingOneShot = false;
            _isOneShotPingPong = false;
            _isOneShotReturning = false;
            var callback = _onClipCompleted;
            _onClipCompleted = null;
            if (callback != null)
                callback();
        }

        private void ClearDeferredPlaybackState()
        {
            ClearPendingClipRequest();
            _isOneShotPingPong = false;
            _isOneShotReturning = false;
        }

        private bool TryQueueSmoothOneShotTransition(string clipName, Action onComplete, bool pingPongToStart)
        {
            if (_activeClip == null || !_isPlaying || _isPlayingOneShot)
                return false;

            if (!_activeClip.Config.loop || !_activeClip.Config.pingPong)
                return false;

            if (_frameIndex == 0)
                return false;

            _pendingClipName = clipName;
            _pendingClipCompleted = onComplete;
            _pendingClipIsOneShot = true;
            _pendingClipPingPong = pingPongToStart;
            _isPlaying = true;
            return true;
        }

        private bool StartOneShotPlayback(string clipName, ClipRuntime clip, Action onComplete, bool pingPongToStart)
        {
            SetClip(clipName);
            if (_activeClip != clip || clip == null ||
                clip.Frames == null || clip.Frames.Length == 0)
                return false;
            _onClipCompleted = onComplete;
            _isPlayingOneShot = true;
            _isPlaying = true;
            _isOneShotPingPong = pingPongToStart && clip != null && clip.Frames.Length > 1;
            _isOneShotReturning = false;
            return true;
        }

        private bool HasPendingOneShot()
        {
            return _pendingClipIsOneShot && !string.IsNullOrWhiteSpace(_pendingClipName);
        }

        private bool HasPendingClipRequest()
        {
            return !string.IsNullOrWhiteSpace(_pendingClipName);
        }

        private static bool IsSmoothAmbientClip(SpriteSheetAnimation clip)
        {
            return clip != null && clip.loop && clip.pingPong;
        }

        private void ClearPendingClipRequest()
        {
            _pendingClipName = null;
            _pendingClipCompleted = null;
            _pendingClipIsOneShot = false;
            _pendingClipPingPong = false;
        }

        private void ApplyFrame()
        {
            if (_targetImage == null || _activeClip == null || _activeClip.Frames.Length == 0)
                return;

            _frameIndex = Mathf.Clamp(_frameIndex, 0, _activeClip.Frames.Length - 1);
            _targetImage.sprite = _activeClip.Frames[_frameIndex];
        }

        private bool EnsureFrames(ClipRuntime clip)
        {
            if (clip == null)
                return false;
            if (clip.Frames != null && clip.Frames.Length > 0 &&
                clip.Frames[0] != null)
                return true;

            clip.Frames = SpriteSheetAnimationLoader.LoadFrames(
                clip.Config.spriteSheetPath,
                clip.Config.columns,
                clip.Config.rows,
                clip.Config.frameCount);
            NeonLogger.Log("[SpriteSheetAnimator] clip='" + clip.Config.clipName +
                "' path='" + clip.Config.spriteSheetPath +
                "' frames=" + (clip.Frames != null
                    ? clip.Frames.Length.ToString()
                    : "null"));
            return clip.Frames != null && clip.Frames.Length > 0;
        }

        private void ReleaseFrames(ClipRuntime clip)
        {
            if (clip == null || clip.Frames == null)
                return;
            SpriteSheetAnimationLoader.ReleaseFrames(
                clip.Config.spriteSheetPath,
                clip.Config.columns,
                clip.Config.rows,
                clip.Config.frameCount,
                clip.Frames);
            clip.Frames = null;
        }

        private void ReleaseAllLoadedFrames()
        {
            if (_targetImage != null)
                _targetImage.sprite = null;
            foreach (ClipRuntime clip in _clips.Values)
                ReleaseFrames(clip);
            _activeClip = null;
        }

        private void OnDestroy()
        {
            ReleaseAllLoadedFrames();
        }

        private sealed class ClipRuntime
        {
            public ClipRuntime(SpriteSheetAnimation config)
            {
                Config = config;
            }

            public SpriteSheetAnimation Config { get; }
            public Sprite[] Frames { get; set; }
        }
    }
}
