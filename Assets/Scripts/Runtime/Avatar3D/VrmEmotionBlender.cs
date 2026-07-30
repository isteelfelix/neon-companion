using System.Collections.Generic;
using UniVRM10;
using UnityEngine;

namespace NeonCompanion.Runtime.Avatar3D
{
    /// <summary>
    /// Drives the emotional face into <see cref="VrmExpressionLayer.Emotion"/>.
    /// <para>
    /// Two things make an emotion look like a mood rather than a state change.
    /// It eases rather than snaps — cubic in and out, so the face leans into the
    /// expression and settles instead of arriving at constant speed. And it
    /// starts from wherever the face actually is, not from neutral: an emotion
    /// that interrupts another has to grow out of it, or the companion visibly
    /// resets between every sentence.
    /// </para>
    /// <para>
    /// It also lets go by itself. An emotion holds for its palette's lease and
    /// then fades back to neutral, because nothing else in the system knows when
    /// a mood is over, and a grin nobody cancelled is worse than no grin.
    /// </para>
    /// </summary>
    internal sealed class VrmEmotionBlender
    {
        private readonly VrmExpressionComposer _composer;

        /// <summary>Where the running blend started, per key.</summary>
        private readonly Dictionary<ExpressionKey, float> _from;

        /// <summary>Where it is going.</summary>
        private readonly Dictionary<ExpressionKey, float> _target;

        /// <summary>What the layer is currently holding, so a blend can start from it.</summary>
        private readonly Dictionary<ExpressionKey, float> _live;

        private readonly List<ExpressionKey> _keys;

        private AvatarEmotion _emotion;
        private float _blendElapsed;
        private float _blendSeconds;
        private float _holdRemaining;
        private bool _blending;

        internal VrmEmotionBlender(VrmExpressionComposer composer)
        {
            _composer = composer;
            _from = new Dictionary<ExpressionKey, float>(ExpressionKey.Comparer);
            _target = new Dictionary<ExpressionKey, float>(ExpressionKey.Comparer);
            _live = new Dictionary<ExpressionKey, float>(ExpressionKey.Comparer);
            _keys = new List<ExpressionKey>();
            _emotion = AvatarEmotion.Neutral;
        }

        internal AvatarEmotion Emotion
        {
            get { return _emotion; }
        }

        /// <summary>True while the face is still travelling toward its emotion.</summary>
        internal bool IsBlending
        {
            get { return _blending; }
        }

        internal void SetEmotion(AvatarEmotion emotion)
        {
            if (emotion == _emotion)
            {
                // Re-asserting the emotion the face already wears renews its
                // lease. Restarting the blend would make a stream of identical
                // markers pulse the expression instead of sustaining it.
                _holdRemaining = VrmEmotionPalette.Resolve(emotion).HoldSeconds;
                return;
            }

            VrmEmotionPalette palette = VrmEmotionPalette.Resolve(emotion);

            _from.Clear();
            foreach (KeyValuePair<ExpressionKey, float> pair in _live)
                _from[pair.Key] = pair.Value;

            _target.Clear();
            for (int i = 0; i < palette.Accents.Length; i++)
                _target[palette.Accents[i].Key] = palette.Accents[i].Weight;

            _emotion = emotion;
            _blendSeconds = palette.BlendSeconds;
            _blendElapsed = 0f;
            _holdRemaining = palette.HoldSeconds;
            _blending = true;
        }

        /// <summary>
        /// Advances the blend and the hold. Called once per frame before the
        /// composer flushes, so the weights it declares land the same frame.
        /// </summary>
        internal void Tick(float deltaTime)
        {
            if (_blending)
            {
                AdvanceBlend(deltaTime);
                return;
            }

            if (_emotion == AvatarEmotion.Neutral || _holdRemaining <= 0f)
                return;

            _holdRemaining -= deltaTime;
            if (_holdRemaining <= 0f)
                SetEmotion(AvatarEmotion.Neutral);
        }

        private void AdvanceBlend(float deltaTime)
        {
            _blendElapsed += deltaTime;
            float progress = _blendSeconds > 0f
                ? Mathf.Clamp01(_blendElapsed / _blendSeconds)
                : 1f;
            bool settled = progress >= 1f;
            float eased = EaseInOutCubic(progress);

            CollectBlendKeys();

            _live.Clear();
            for (int i = 0; i < _keys.Count; i++)
            {
                ExpressionKey key = _keys[i];

                float fromWeight;
                if (!_from.TryGetValue(key, out fromWeight))
                    fromWeight = 0f;
                float targetWeight;
                if (!_target.TryGetValue(key, out targetWeight))
                    targetWeight = 0f;

                if (settled && targetWeight <= 0f)
                {
                    // Faded out for good. Release rather than pin it at zero, so
                    // a blink or a viseme underneath can have the key back.
                    _composer.Clear(VrmExpressionLayer.Emotion, key);
                    continue;
                }

                float weight = Mathf.Lerp(fromWeight, targetWeight, eased);
                _composer.Set(VrmExpressionLayer.Emotion, key, weight);
                _live[key] = weight;
            }

            if (settled)
                _blending = false;
        }

        /// <summary>
        /// Every key either side of the blend cares about. Keys the target drops
        /// still have to be walked down to zero rather than abandoned.
        /// </summary>
        private void CollectBlendKeys()
        {
            _keys.Clear();
            foreach (KeyValuePair<ExpressionKey, float> pair in _from)
                _keys.Add(pair.Key);
            foreach (KeyValuePair<ExpressionKey, float> pair in _target)
            {
                if (!_from.ContainsKey(pair.Key))
                    _keys.Add(pair.Key);
            }
        }

        private static float EaseInOutCubic(float progress)
        {
            if (progress < 0.5f)
                return 4f * progress * progress * progress;

            float remaining = -2f * progress + 2f;
            return 1f - remaining * remaining * remaining * 0.5f;
        }
    }
}
