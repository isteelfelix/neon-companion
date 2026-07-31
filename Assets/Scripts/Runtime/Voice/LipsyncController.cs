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

        // Amplitude-driven 3D mouth. The jaw opening tracks the real audio output
        // level, so it follows actual speech, freezes shut on pause (no samples),
        // and works on every replay — none of which the text timer did.
        private float _mouthOpen;
        private static readonly float[] _outputSamples = new float[256];
        private const float MouthSilenceLevel = 0.006f; // RMS below this = closed
        private const float MouthLoudLevel    = 0.12f;   // RMS at/above this = fully open
        private const float MouthOpenSpeed     = 11f;
        private const float MouthCloseSpeed    = 7f;

        // Streaming-text mouth imitation, used only when no audio is playing. Fed the
        // visible streaming characters; each informative char sets a viseme target and
        // refreshes an activity window so the jaw closes shortly after text stops.
        private Func<bool> _isStreamingMouthEnabled;
        private bool _streamMouthEnabled;
        private float _streamEnabledReadAt = -999f;
        private float _streamActiveUntil;
        private float _streamTargetOpen;
        private Viseme _streamViseme = Viseme.A;
        private const float StreamActivityHold = 0.22f;
        // Imitation moves the jaw more languidly than the audio-driven path so it reads
        // calmer, especially at slow streaming speeds.
        private const float StreamMouthOpenSpeed  = 5f;
        private const float StreamMouthCloseSpeed = 3f;

        // ── Initialisation ──────────────────────────────────────────────────────

        public void Initialize(
            VoiceOutputManager outputManager,
            VoiceInputManager inputManager,
            Func<SpriteSheetAnimator> getSpriteAnimator,
            Func<IAvatar3DService> getAvatar3DService,
            Func<bool> isStreamingMouthEnabled = null)
        {
            _outputManager = outputManager;
            _inputManager  = inputManager;
            _getSpriteAnimator = getSpriteAnimator;
            _getAvatar3DService = getAvatar3DService;
            _isStreamingMouthEnabled = isStreamingMouthEnabled;
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
            // 3D VRM mouth is amplitude-driven from the real audio output.
            UpdateMouth3D();

            // Legacy 2D sprite / blend-shape-target lipsync keeps the text-timer scan.
            if (!_isActive || string.IsNullOrEmpty(_activeText))
                return;
            if (!_hasLipsyncClip && _blendShapeTarget == null)
                return;

            _charTimer += Time.unscaledDeltaTime;
            int newIndex = Mathf.FloorToInt(_charTimer * TextCharsPerSecond);
            if (newIndex <= _charIndex)
                return;

            _charIndex = newIndex;
            Viseme viseme = _charIndex < _activeText.Length
                ? GetVisemeAt(_activeText, _charIndex)
                : Viseme.Silence;

            ApplyViseme(viseme);
        }

        // Drives the 3D mouth from the current audio output loudness, but only while
        // a voice clip is actually playing (live TTS or a chat-bubble replay — both
        // report through the playback clock; a paused clip reads as not playing, so
        // the jaw closes instead of flapping on).
        private void UpdateMouth3D()
        {
            IAvatar3DService avatar3D = _getAvatar3DService != null
                ? _getAvatar3DService()
                : null;
            if (avatar3D == null || !avatar3D.IsLoaded || !avatar3D.Capabilities.hasLipsync)
                return;

            bool speaking = _outputManager != null &&
                _outputManager.GetCurrentPlaybackState().IsPlaying;

            if (speaking)
            {
                // Real audio always wins: the jaw tracks the actual output loudness.
                float level = SampleOutputLevel();
                float target = level <= MouthSilenceLevel
                    ? 0f
                    : Mathf.Clamp01(
                        (level - MouthSilenceLevel) /
                        (MouthLoudLevel - MouthSilenceLevel));
                _streamActiveUntil = 0f;
                ApplyMouth(avatar3D, "A", target, MouthOpenSpeed, MouthCloseSpeed);
                return;
            }

            // No audio: imitate speech while streaming text is still flowing. The
            // enabled flag was already applied when the activity window was set in
            // FeedStreamingText, so this stays a cheap per-frame check.
            bool imitate = Time.unscaledTime < _streamActiveUntil;
            if (imitate)
            {
                string shape = _streamViseme == Viseme.Silence ? "A" : _streamViseme.ToString();
                ApplyMouth(avatar3D, shape, _streamTargetOpen, StreamMouthOpenSpeed, StreamMouthCloseSpeed);
                return;
            }

            ApplyMouth(avatar3D, "A", 0f, MouthOpenSpeed, MouthCloseSpeed);
        }

        // Eases the jaw toward a target opening for the given viseme and applies it,
        // releasing the mouth entirely when effectively closed.
        private void ApplyMouth(IAvatar3DService avatar3D, string shape, float target,
            float openSpeed, float closeSpeed)
        {
            float speed =
                (target > _mouthOpen ? openSpeed : closeSpeed) *
                Time.unscaledDeltaTime;
            _mouthOpen = Mathf.MoveTowards(_mouthOpen, target, speed);

            if (_mouthOpen <= 0.02f)
                avatar3D.ClearMouth();
            else
                avatar3D.SetMouthShape(shape, _mouthOpen);
        }

        // Feeds the just-revealed streaming text so the mouth can imitate talking when
        // no audio is playing. Sets the viseme/opening from the last informative
        // character and refreshes the activity window; real audio overrides all of it.
        public void FeedStreamingText(string revealed)
        {
            if (string.IsNullOrEmpty(revealed))
                return;

            // Cache the enabled flag (settings read hits disk); refresh at most twice a
            // second. When off, never open the activity window so the mouth stays shut.
            float nowEnabledCheck = Time.unscaledTime;
            if (nowEnabledCheck - _streamEnabledReadAt > 0.5f)
            {
                _streamEnabledReadAt = nowEnabledCheck;
                _streamMouthEnabled = _isStreamingMouthEnabled != null && _isStreamingMouthEnabled();
            }
            if (!_streamMouthEnabled)
                return;

            for (int i = revealed.Length - 1; i >= 0; i--)
            {
                char c = char.ToLowerInvariant(revealed[i]);
                if (CharToViseme.TryGetValue(c, out var v))
                {
                    _streamViseme = v;
                    _streamTargetOpen = UnityEngine.Random.Range(0.6f, 0.95f);
                    _streamActiveUntil = Time.unscaledTime + StreamActivityHold;
                    return;
                }
                if (char.IsLetterOrDigit(c))
                {
                    _streamTargetOpen = UnityEngine.Random.Range(0.22f, 0.4f);
                    _streamActiveUntil = Time.unscaledTime + StreamActivityHold;
                    return;
                }
                if (c == ' ' || c == '\n' || c == '\t' || char.IsPunctuation(c))
                {
                    _streamTargetOpen = 0f;
                    _streamActiveUntil = Time.unscaledTime + StreamActivityHold;
                    return;
                }
            }
        }

        // RMS of the final audio mix this frame. Zero when nothing is audible, so a
        // paused or finished clip closes the mouth on its own.
        private static float SampleOutputLevel()
        {
            AudioListener.GetOutputData(_outputSamples, 0);
            float sum = 0f;
            for (int i = 0; i < _outputSamples.Length; i++)
                sum += _outputSamples[i] * _outputSamples[i];
            return Mathf.Sqrt(sum / _outputSamples.Length);
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

            // 3D mouth is driven per-frame from the audio level in UpdateMouth3D();
            // nothing to prime here.
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
