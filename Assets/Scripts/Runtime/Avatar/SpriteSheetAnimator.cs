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

        public void Configure(IReadOnlyList<SpriteSheetAnimation> clips, Image targetImage)
        {
            _clips.Clear();
            _targetImage = targetImage;
            _activeClip = null;
            _frameTimer = 0f;
            _frameIndex = 0;
            _isPlaying = false;

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
            _frameTimer = 0f;
            _frameIndex = 0;
            ApplyFrame();
        }

        public bool HasClip(string clipName)
        {
            return !string.IsNullOrWhiteSpace(clipName) && _clips.ContainsKey(clipName);
        }

        public bool HasAnyClips => _clips.Count > 0;

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
                    if (_activeClip.Config.loop)
                    {
                        _frameIndex = 0;
                    }
                    else
                    {
                        _frameIndex = _activeClip.Frames.Length - 1;
                        _isPlaying = false;
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
