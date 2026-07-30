using System;
using System.Collections.Generic;
using UniVRM10;
using UnityEngine;

namespace NeonCompanion.Runtime.Avatar3D
{
    /// <summary>
    /// Sources that contribute expression weights, ordered by rank: a higher
    /// value outranks a lower one when both claim the same key and that key
    /// blends by <see cref="VrmExpressionBlend.Override"/>.
    /// </summary>
    internal enum VrmExpressionLayer
    {
        /// <summary>The emotional face: long-lived, blended over seconds.</summary>
        Emotion = 0,

        /// <summary>Speech: owns the mouth for as long as the companion talks.</summary>
        Viseme = 1,

        /// <summary>Reflexes. Involuntary and brief, so they answer to nobody.</summary>
        Blink = 2
    }

    /// <summary>How contributions to one key from several layers combine.</summary>
    internal enum VrmExpressionBlend
    {
        /// <summary>Highest-ranked contributor wins outright; the rest are ignored.</summary>
        Override = 0,

        /// <summary>Strongest contribution wins, whichever layer it came from.</summary>
        Max = 1,

        /// <summary>Contributions sum, then clamp.</summary>
        Additive = 2
    }

    /// <summary>
    /// The single owner of every VRM expression weight.
    /// <para>
    /// Blinking, lipsync and emotion all want the same small set of blendshape
    /// keys — VRM spends <c>aa</c>/<c>ee</c>/<c>oh</c> on visemes *and* on the
    /// mouth corners of an emotional face, and an emotional squint lands on the
    /// same eyelid as a blink. Left to write straight to
    /// <c>Vrm10Runtime.Expression</c>, they resolve conflicts by accident of
    /// update order: whoever runs last that frame wins, and a key nobody
    /// mentions any more simply keeps its stale value.
    /// </para>
    /// <para>
    /// So nobody writes to the model directly. Layers declare what they want,
    /// this folds the declarations down by rank and blend mode, and
    /// <see cref="Apply"/> pushes exactly one value per key per frame — plus a
    /// single zero for every key that has just been let go.
    /// </para>
    /// </summary>
    internal sealed class VrmExpressionComposer
    {
        private const int LayerCount = 3;

        private readonly Action<ExpressionKey, float> _write;
        private readonly Dictionary<ExpressionKey, float>[] _layers;
        private readonly Dictionary<ExpressionKey, VrmExpressionBlend> _blendOverrides;
        private readonly Dictionary<ExpressionKey, float> _composed;
        private readonly HashSet<ExpressionKey> _claimed;
        private readonly List<ExpressionKey> _released;

        /// <param name="write">
        /// Where a composed weight goes. The driver owns this, because routing
        /// depends on whether a VRM animation is currently intercepting the key.
        /// </param>
        internal VrmExpressionComposer(Action<ExpressionKey, float> write)
        {
            _write = write;
            _layers = new Dictionary<ExpressionKey, float>[LayerCount];
            for (int i = 0; i < LayerCount; i++)
            {
                _layers[i] = new Dictionary<ExpressionKey, float>(
                    ExpressionKey.Comparer);
            }

            _blendOverrides = new Dictionary<ExpressionKey, VrmExpressionBlend>(
                ExpressionKey.Comparer);
            _composed = new Dictionary<ExpressionKey, float>(ExpressionKey.Comparer);
            _claimed = new HashSet<ExpressionKey>(ExpressionKey.Comparer);
            _released = new List<ExpressionKey>();
        }

        /// <summary>
        /// Declares this layer's weight for a key. Repeat calls replace the
        /// layer's own value; other layers are untouched.
        /// </summary>
        internal void Set(VrmExpressionLayer layer, ExpressionKey key, float weight)
        {
            Dictionary<ExpressionKey, float> values = ResolveLayer(layer);
            if (values == null)
                return;
            values[key] = Mathf.Clamp01(weight);
        }

        /// <summary>
        /// Withdraws every claim this layer makes. Keys no other layer wants are
        /// zeroed on the next <see cref="Apply"/>.
        /// </summary>
        internal void ClearLayer(VrmExpressionLayer layer)
        {
            Dictionary<ExpressionKey, float> values = ResolveLayer(layer);
            if (values != null)
                values.Clear();
        }

        /// <summary>
        /// Pins how one key resolves a contest, overriding the default for its
        /// kind. Reserved for the emotion and viseme stages, which need keys
        /// that the defaults would not classify correctly on their own.
        /// </summary>
        internal void SetBlend(ExpressionKey key, VrmExpressionBlend blend)
        {
            _blendOverrides[key] = blend;
        }

        /// <summary>
        /// Folds the layers down and pushes the result to the model. The only
        /// place a weight is written, so a key receives exactly one value per
        /// frame regardless of how many layers had an opinion about it.
        /// </summary>
        internal void Apply()
        {
            if (_write == null)
                return;

            Compose();

            // A dropped key has to be told to go home. The model holds whatever
            // it was handed last, so letting a claim lapse in silence would
            // freeze the face mid-blink or leave a viseme hanging open.
            _released.Clear();
            foreach (ExpressionKey key in _claimed)
            {
                if (!_composed.ContainsKey(key))
                    _released.Add(key);
            }

            for (int i = 0; i < _released.Count; i++)
            {
                _write(_released[i], 0f);
                _claimed.Remove(_released[i]);
            }

            foreach (KeyValuePair<ExpressionKey, float> pair in _composed)
            {
                _write(pair.Key, pair.Value);
                _claimed.Add(pair.Key);
            }
        }

        private void Compose()
        {
            _composed.Clear();

            // Ascending rank, so for an Override key the last writer standing is
            // the highest-ranked layer that asked for it.
            for (int i = 0; i < _layers.Length; i++)
            {
                foreach (KeyValuePair<ExpressionKey, float> pair in _layers[i])
                    Fold(pair.Key, pair.Value);
            }
        }

        private void Fold(ExpressionKey key, float weight)
        {
            float current;
            if (!_composed.TryGetValue(key, out current))
            {
                _composed[key] = weight;
                return;
            }

            float folded;
            switch (ResolveBlend(key))
            {
                case VrmExpressionBlend.Max:
                    folded = current > weight ? current : weight;
                    break;
                case VrmExpressionBlend.Additive:
                    folded = current + weight;
                    break;
                default:
                    folded = weight;
                    break;
            }

            _composed[key] = Mathf.Clamp01(folded);
        }

        private VrmExpressionBlend ResolveBlend(ExpressionKey key)
        {
            VrmExpressionBlend blend;
            if (_blendOverrides.TryGetValue(key, out blend))
                return blend;

            // An eyelid is shared on purpose: a blink and an emotional squint
            // both want it, and whichever is stronger is the one you should see.
            // Overriding would make a squint vanish for the length of a blink,
            // and summing would drive the lid past what the model was rigged for.
            if (key.IsBlink)
                return VrmExpressionBlend.Max;

            // Everything else — mouth keys above all — hands the key to the
            // ranking layer whole. While there is speech to shape, speech owns
            // the mouth; an emotion's idea of `aa` does not get to pile on.
            return VrmExpressionBlend.Override;
        }

        private Dictionary<ExpressionKey, float> ResolveLayer(VrmExpressionLayer layer)
        {
            int index = (int)layer;
            if (index < 0 || index >= _layers.Length)
                return null;
            return _layers[index];
        }
    }
}
