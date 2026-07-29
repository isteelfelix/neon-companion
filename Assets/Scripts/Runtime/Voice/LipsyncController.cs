using System;
using System.Collections.Generic;
using NeonCompanion.Runtime.Avatar;
using NeonCompanion.Runtime.Avatar3D;
using NeonCompanion.Runtime.Data.Models;
using UnityEngine;

namespace NeonCompanion.Runtime.Voice
{
    /// <summary>
    /// Visemes supported for both 2D sprite-frame lipsync and 3D blend-shape lipsync.
    /// The integer value is the frame index in the lipsync sprite sheet.
    /// </summary>
    public enum Viseme { Silence = 0, A = 1, E = 2, I = 3, O = 4, U = 5 }

    /// <summary>
    /// Drives lip animation for 2D (sprite frames) and 3D (blend shapes) avatars.
    ///
    /// 2D usage:
    ///   1. Add a "lipsync" SpriteSheetAnimation to AvatarProfile.lipsyncClip whose
    ///      frames are ordered: Silence(0), A(1), E(2), I(3), O(4), U(5).
    ///   2. Call SetSpriteAnimator() after the avatar's SpriteSheetAnimator is configured.
    ///
    /// 3D usage:
    ///   Call SetBlendShapeTarget() with the SkinnedMeshRenderer that owns the viseme
    ///   blend shapes. Standard names (Ready Player Me / ARKit): viseme_sil, viseme_aa,
    ///   viseme_E, viseme_I, viseme_O, viseme_U.
    /// </summary>
    public sealed class LipsyncController : MonoBehaviour
    {
        // Maps individual characters to their dominant viseme
        private static readonly Dictionary<char, Viseme> CharToViseme = new Dictionary<char, Viseme>
        {
            ['a'] = Viseme.A, ['à'] = Viseme.A, ['á'] = Viseme.A,
            ['ä'] = Viseme.A, ['â'] = Viseme.A,
            ['e'] = Viseme.E, ['è'] = Viseme.E, ['é'] = Viseme.E,
            ['ë'] = Viseme.E, ['ê'] = Viseme.E,
            ['i'] = Viseme.I, ['ì'] = Viseme.I, ['í'] = Viseme.I,
            ['ï'] = Viseme.I, ['y'] = Viseme.I,
            ['o'] = Viseme.O, ['ò'] = Viseme.O, ['ó'] = Viseme.O,
            ['ö'] = Viseme.O, ['ô'] = Viseme.O,
            ['u'] = Viseme.U, ['ù'] = Viseme.U, ['ú'] = Viseme.U,
            ['ü'] = Viseme.U,
            ['а'] = Viseme.A, ['я'] = Viseme.A,
            ['е'] = Viseme.E, ['э'] = Viseme.E,
            ['и'] = Viseme.I, ['ы'] = Viseme.I,
            ['о'] = Viseme.O, ['ё'] = Viseme.O,
            ['у'] = Viseme.U, ['ю'] = Viseme.U,
        };

        public const float TextCharsPerSecond = 12f;
        private const string LipsyncClipName = "lipsync";
        private const string TalkingClipName = "talking";
        private const string IdleClipName    = "idle";

        private VoiceOutputManager  _outputManager;
        private VoiceInputManager   _inputManager;
        private SpriteSheetAnimator _spriteAnimator;
        private SkinnedMeshRenderer _blendShapeTarget;
        private Func<SpriteSheetAnimator> _getSpriteAnimator;
        private Func<IAvatar3DService> _getAvatar3DService;

        // Cached blend-shape indices; -1 means not present on the mesh
        private readonly int[] _shapeIndices = new int[6]; // one slot per Viseme

        private string _activeText;
        private float  _charTimer;
        private int    _charIndex;
        private bool   _isActive;
        private bool   _hasLipsyncClip;
        private bool   _hasTalkingClip;

        // ── Initialisation ──────────────────────────────────────────────────────

        public void Initialize(
            VoiceOutputManager outputManager,
            VoiceInputManager inputManager,
            Func<SpriteSheetAnimator> getSpriteAnimator,
            Func<IAvatar3DService> getAvatar3DService)
        {
            _outputManager = outputManager;
            _inputManager  = inputManager;
            _getSpriteAnimator = getSpriteAnimator;
            _getAvatar3DService = getAvatar3DService;
            RefreshTargets();

            if (_outputManager != null)
            {
                _outputManager.OnPlaybackStarted   += StartLipsync;
                _outputManager.OnPlaybackCompleted += StopLipsync;
            }

            if (_inputManager != null)
            {
                _inputManager.OnRecordingStarted += HandleRecordingStarted;
                _inputManager.OnRecordingStopped += HandleRecordingStopped;
            }
        }

        private void OnDestroy()
        {
            if (_outputManager != null)
            {
                _outputManager.OnPlaybackStarted   -= StartLipsync;
                _outputManager.OnPlaybackCompleted -= StopLipsync;
            }

            if (_inputManager != null)
            {
                _inputManager.OnRecordingStarted -= HandleRecordingStarted;
                _inputManager.OnRecordingStopped -= HandleRecordingStopped;
            }
            Apply3DViseme(Viseme.Silence);
        }

        // ── Per-frame update ────────────────────────────────────────────────────

        private void Update()
        {
            if (!_isActive || string.IsNullOrEmpty(_activeText))
                return;

            // Skip update if neither path can consume the result
            IAvatar3DService avatar3D = _getAvatar3DService != null
                ? _getAvatar3DService()
                : null;
            if (!_hasLipsyncClip && _blendShapeTarget == null &&
                (avatar3D == null || !avatar3D.IsLoaded || !avatar3D.Capabilities.hasLipsync))
                return;

            _charTimer += Time.unscaledDeltaTime;
            int newIndex = Mathf.FloorToInt(_charTimer * TextCharsPerSecond);

            if (newIndex <= _charIndex)
                return;

            _charIndex = newIndex;

            var viseme = _charIndex < _activeText.Length
                ? GetVisemeAt(_activeText, _charIndex)
                : Viseme.Silence;

            ApplyViseme(viseme);
        }

        // ── Event handlers ──────────────────────────────────────────────────────

        private void StartLipsync(string text)
        {
            RefreshTargets();
            _activeText = text ?? string.Empty;
            _charTimer  = 0f;
            _charIndex  = 0;
            _isActive   = true;

            // Immediately open mouth on first syllable
            var firstViseme = _activeText.Length > 0
                ? GetVisemeAt(_activeText, 0)
                : Viseme.A;

            if (_hasLipsyncClip)
            {
                _spriteAnimator.ShowFrame(LipsyncClipName, (int)firstViseme);
            }
            else if (_hasTalkingClip)
            {
                _spriteAnimator.Play(TalkingClipName);
            }

            Apply3DViseme(firstViseme);
        }

        private void StopLipsync()
        {
            _isActive   = false;
            _activeText = null;

            ApplyViseme(Viseme.Silence);

            if (_spriteAnimator != null && _spriteAnimator.HasClip(IdleClipName))
                _spriteAnimator.Play(IdleClipName);
        }

        private void HandleRecordingStarted()
        {
            RefreshTargets();
            if (_spriteAnimator == null)
                return;

            if (_hasTalkingClip)
                _spriteAnimator.Play(TalkingClipName);
            else if (_hasLipsyncClip)
                _spriteAnimator.ShowFrame(LipsyncClipName, (int)Viseme.A);
        }

        private void HandleRecordingStopped()
        {
            if (_spriteAnimator != null && _spriteAnimator.HasClip(IdleClipName))
                _spriteAnimator.Play(IdleClipName);
        }

        // ── Viseme application ──────────────────────────────────────────────────

        private void ApplyViseme(Viseme viseme)
        {
            if (_hasLipsyncClip && _spriteAnimator != null)
                _spriteAnimator.ShowFrame(LipsyncClipName, (int)viseme);

            Apply3DViseme(viseme);
        }

        private void Apply3DViseme(Viseme viseme)
        {
            IAvatar3DService avatar3D = _getAvatar3DService != null
                ? _getAvatar3DService()
                : null;
            if (avatar3D != null && avatar3D.IsLoaded)
            {
                if (viseme == Viseme.Silence)
                    avatar3D.ClearMouth();
                else
                    avatar3D.SetMouthShape(viseme.ToString());
            }

            if (_blendShapeTarget == null)
                return;

            int active = (int)viseme;
            for (int i = 0; i < _shapeIndices.Length; i++)
            {
                int idx = _shapeIndices[i];
                if (idx >= 0)
                    _blendShapeTarget.SetBlendShapeWeight(idx, i == active ? 100f : 0f);
            }
        }

        // ── Phoneme helpers ─────────────────────────────────────────────────────

        public static Viseme GetVisemeAt(string text, int charIndex)
        {
            if (string.IsNullOrEmpty(text) || charIndex < 0 || charIndex >= text.Length)
                return Viseme.Silence;

            char c = char.ToLowerInvariant(text[charIndex]);
            return CharToViseme.TryGetValue(c, out var viseme) ? viseme : Viseme.Silence;
        }

        private void RefreshTargets()
        {
            _spriteAnimator = _getSpriteAnimator != null ? _getSpriteAnimator() : null;
            _hasLipsyncClip = _spriteAnimator != null &&
                _spriteAnimator.HasClip(LipsyncClipName);
            _hasTalkingClip = _spriteAnimator != null &&
                _spriteAnimator.HasClip(TalkingClipName);
        }

    }
}
