using System.Collections.Generic;
using NeonCompanion.Runtime.Avatar;
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
        // Blend-shape name convention (Ready Player Me / ARKit)
        public static readonly IReadOnlyDictionary<Viseme, string> BlendShapeNames =
            new Dictionary<Viseme, string>
            {
                [Viseme.Silence] = "viseme_sil",
                [Viseme.A]       = "viseme_aa",
                [Viseme.E]       = "viseme_E",
                [Viseme.I]       = "viseme_I",
                [Viseme.O]       = "viseme_O",
                [Viseme.U]       = "viseme_U",
            };

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
        };

        private const float CharsPerSecond = 12f;
        private const string LipsyncClipName = "lipsync";
        private const string TalkingClipName = "talking";
        private const string IdleClipName    = "idle";

        private VoiceOutputManager  _outputManager;
        private VoiceInputManager   _inputManager;
        private SpriteSheetAnimator _spriteAnimator;
        private SkinnedMeshRenderer _blendShapeTarget;

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
            VoiceInputManager  inputManager)
        {
            _outputManager = outputManager;
            _inputManager  = inputManager;

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

        /// <summary>
        /// Binds the 2D animator. If the AvatarProfile has a lipsyncClip it is
        /// registered with the animator so ShowFrame() can address it by name.
        /// </summary>
        public void SetSpriteAnimator(SpriteSheetAnimator animator, SpriteSheetAnimation lipsyncClip = null)
        {
            _spriteAnimator = animator;

            if (animator != null && lipsyncClip != null)
                animator.RegisterClip(lipsyncClip);

            _hasLipsyncClip = animator != null && animator.HasClip(LipsyncClipName);
            _hasTalkingClip = animator != null && animator.HasClip(TalkingClipName);
        }

        /// <summary>
        /// Binds the 3D blend-shape target. Call this after the GLB model is loaded.
        /// Caches blend-shape indices so Update() pays no allocation or string-lookup cost.
        /// </summary>
        public void SetBlendShapeTarget(SkinnedMeshRenderer target)
        {
            _blendShapeTarget = target;
            for (int i = 0; i < _shapeIndices.Length; i++)
                _shapeIndices[i] = -1;

            if (target == null || target.sharedMesh == null)
                return;

            foreach (var kvp in BlendShapeNames)
            {
                int slot = (int)kvp.Key;
                if (slot >= 0 && slot < _shapeIndices.Length)
                    _shapeIndices[slot] = target.sharedMesh.GetBlendShapeIndex(kvp.Value);
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
        }

        // ── Per-frame update ────────────────────────────────────────────────────

        private void Update()
        {
            if (!_isActive || string.IsNullOrEmpty(_activeText))
                return;

            // Skip update if neither path can consume the result
            if (!_hasLipsyncClip && _blendShapeTarget == null)
                return;

            _charTimer += Time.unscaledDeltaTime;
            int newIndex = Mathf.FloorToInt(_charTimer * CharsPerSecond);

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

        /// <summary>
        /// Returns normalised blend-shape weights (0–1) for all six visemes.
        /// Useful for driving custom rigs that don't use the standard shape names.
        /// </summary>
        public static float[] GetBlendWeights(Viseme viseme)
        {
            var weights = new float[6]; // all zero
            weights[(int)viseme] = 1f;
            return weights;
        }
    }
}
