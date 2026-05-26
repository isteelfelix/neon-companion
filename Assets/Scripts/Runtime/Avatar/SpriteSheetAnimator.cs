using System;
using System.Collections.Generic;
using System.Linq;
using NeonCompanion.Runtime.Data.Models;
using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.Avatar
{
    public sealed class SpriteSheetAnimator : MonoBehaviour
    {
        private readonly Dictionary<string, ClipRuntime> _clips = new Dictionary<string, ClipRuntime>(StringComparer.OrdinalIgnoreCase);
        private Image _targetImage;
        private ClipRuntime _activeClip;
        private float _frameTimer;
        private int _frameIndex;
        private bool _isPlaying;
        private bool _isPlayingOneShot;
        private Action _onClipCompleted;

        public void Configure(IReadOnlyList<SpriteSheetAnimation> clips, Image targetImage)
        {
            _clips.Clear();
            _targetImage = targetImage;
            _activeClip = null;
            _frameTimer = 0f;
            _frameIndex = 0;
            _isPlaying = false;
            _isPlayingOneShot = false;

            if (targetImage == null || clips == null)
                return;

            for (int i = 0; i < clips.Count; i++)
            {
                var clip = clips[i];
                if (clip == null || string.IsNullOrWhiteSpace(clip.clipName))
                    continue;

                var frames = SpriteSheetAnimationLoader.LoadFrames(clip.spriteSheetPath, clip.columns, clip.rows);
                if (frames == null || frames.Length == 0)
                    continue;

                _clips[clip.clipName] = new ClipRuntime(clip, frames);
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

            _onClipCompleted = null;
            _isPlayingOneShot = false;
            _activeClip = clip;
            _frameTimer = 0f;
            _frameIndex = 0;
            ApplyFrame();
        }

        public void Play(string clipName)
        {
            if (!string.IsNullOrWhiteSpace(clipName))
                SetClip(clipName);

            if (_activeClip == null)
                return;

            _isPlaying = true;
        }

        public void Stop()
        {
            _isPlaying = false;
            _isPlayingOneShot = false;
            _onClipCompleted = null;
            _frameTimer = 0f;
            _frameIndex = 0;
            ApplyFrame();
        }

        public bool HasClip(string clipName)
        {
            return !string.IsNullOrWhiteSpace(clipName) && _clips.ContainsKey(clipName);
        }

        public bool HasAnyClips => _clips.Count > 0;
        public bool IsPlayingOneShot => _isPlayingOneShot;
        public string ActiveClipName => _activeClip != null ? _activeClip.Config.clipName : null;

        /// <summary>
        /// Pauses playback and pins a specific frame from the named clip.
        /// Used by LipsyncController to drive mouth-shape frames directly.
        /// </summary>
        public void ShowFrame(string clipName, int frameIndex)
        {
            if (string.IsNullOrWhiteSpace(clipName) || !_clips.TryGetValue(clipName, out var clip))
                return;

            _activeClip = clip;
            _isPlaying = false;
            _isPlayingOneShot = false;
            var callback = _onClipCompleted;
            _onClipCompleted = null;
            _frameTimer = 0f;
            _frameIndex = Mathf.Clamp(frameIndex, 0, clip.Frames.Length - 1);
            ApplyFrame();
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

            var frames = SpriteSheetAnimationLoader.LoadFrames(clip.spriteSheetPath, clip.columns, clip.rows);
            if (frames == null || frames.Length == 0)
                return;

            _clips[clip.clipName] = new ClipRuntime(clip, frames);
        }

        public bool PlayOneShot(string clipName, Action onComplete)
        {
            if (string.IsNullOrWhiteSpace(clipName))
                return false;

            if (!_clips.TryGetValue(clipName, out var clip))
                return false;

            SetClip(clipName);
            _onClipCompleted = onComplete;
            _isPlayingOneShot = true;
            _isPlaying = true;
            return true;
        }

        private void Update()
        {
            if (!_isPlaying || _activeClip == null || _targetImage == null)
                return;

            float fps = Mathf.Max(0.01f, _activeClip.Config.frameRate);
            float frameDuration = 1f / fps;
            _frameTimer += Time.unscaledDeltaTime;

            while (_frameTimer >= frameDuration)
            {
                _frameTimer -= frameDuration;
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
                        _isPlaying = false;
                        _isPlayingOneShot = false;
                        var callback = _onClipCompleted;
                        _onClipCompleted = null;
                        if (callback != null)
                            callback();
                    }
                }

                ApplyFrame();
            }
        }

        private void ApplyFrame()
        {
            if (_targetImage == null || _activeClip == null || _activeClip.Frames.Length == 0)
                return;

            _frameIndex = Mathf.Clamp(_frameIndex, 0, _activeClip.Frames.Length - 1);
            _targetImage.sprite = _activeClip.Frames[_frameIndex];
        }

        private sealed class ClipRuntime
        {
            public ClipRuntime(SpriteSheetAnimation config, Sprite[] frames)
            {
                Config = config;
                Frames = frames;
            }

            public SpriteSheetAnimation Config { get; }
            public Sprite[] Frames { get; }
        }
    }
}
