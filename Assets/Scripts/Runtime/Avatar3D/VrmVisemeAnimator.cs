using UniVRM10;
using UnityEngine;

namespace NeonCompanion.Runtime.Avatar3D
{
    /// <summary>
    /// Shapes the mouth for speech, writing into <see cref="VrmExpressionLayer.Viseme"/>.
    /// <para>
    /// The lipsync source hands us one viseme per frame; a raw write of that key
    /// at full weight snaps the jaw and, once several phonemes overlap, smears the
    /// mouth into a permanent <c>aa</c>. Three rules fix that. Each viseme eases
    /// in fast and out slower (attack/release), so the mouth moves like flesh, not
    /// a switch. No viseme passes 0.7 — a VRM viseme at 1.0 is a rictus. And only
    /// the two loudest visemes are ever voiced at once (winner-and-runner), so a
    /// third, fading shape can't pile on and leave the mouth hanging open.
    /// </para>
    /// </summary>
    internal sealed class VrmVisemeAnimator
    {
        /// <summary>A viseme at full VRM weight looks like a scream; hold it back.</summary>
        internal const float MaxWeight = 0.7f;

        private const float AttackRate = 18f;
        private const float ReleaseRate = 9f;
        private const float Epsilon = 0.001f;

        private static readonly ExpressionKey[] MouthKeys =
        {
            ExpressionKey.Aa,
            ExpressionKey.Ih,
            ExpressionKey.Ou,
            ExpressionKey.Ee,
            ExpressionKey.Oh
        };

        private readonly VrmExpressionComposer _composer;
        private readonly float[] _weights = new float[MouthKeys.Length];
        private readonly float[] _targets = new float[MouthKeys.Length];

        internal VrmVisemeAnimator(VrmExpressionComposer composer)
        {
            _composer = composer;
        }

        /// <summary>True when no viseme is voiced, so the mouth belongs to the face again.</summary>
        internal bool IsSilent { get; private set; }

        /// <summary>
        /// Aims the mouth at one viseme; every other viseme is told to release. The
        /// caller re-states this each frame with the current phoneme.
        /// </summary>
        internal void SetShape(ExpressionKey key, float weight)
        {
            int active = IndexOf(key);
            float clamped = Mathf.Clamp(weight, 0f, MaxWeight);
            for (int i = 0; i < _targets.Length; i++)
                _targets[i] = i == active ? clamped : 0f;
        }

        /// <summary>Lets every viseme fall back to rest at the release rate.</summary>
        internal void Clear()
        {
            for (int i = 0; i < _targets.Length; i++)
                _targets[i] = 0f;
        }

        /// <summary>
        /// Snaps the mouth shut and releases every key immediately — for teardown,
        /// where there is no next frame to ease into.
        /// </summary>
        internal void ForceSilence()
        {
            for (int i = 0; i < _weights.Length; i++)
            {
                _weights[i] = 0f;
                _targets[i] = 0f;
                _composer.Clear(VrmExpressionLayer.Viseme, MouthKeys[i]);
            }
            IsSilent = true;
        }

        /// <summary>
        /// Advances every viseme toward its target and writes the survivors. Called
        /// once per frame before the composer flushes.
        /// </summary>
        internal void Tick(float deltaTime)
        {
            if (deltaTime > 0f)
            {
                for (int i = 0; i < _weights.Length; i++)
                {
                    float rate = _targets[i] > _weights[i] ? AttackRate : ReleaseRate;
                    _weights[i] = Mathf.MoveTowards(
                        _weights[i], _targets[i], rate * deltaTime);
                }
            }

            ApplyWinnerAndRunner();
        }

        private void ApplyWinnerAndRunner()
        {
            int first = -1;
            int second = -1;
            for (int i = 0; i < _weights.Length; i++)
            {
                if (_weights[i] <= Epsilon)
                    continue;
                if (first < 0 || _weights[i] > _weights[first])
                {
                    second = first;
                    first = i;
                }
                else if (second < 0 || _weights[i] > _weights[second])
                {
                    second = i;
                }
            }

            bool anyVoiced = false;
            for (int i = 0; i < _weights.Length; i++)
            {
                bool kept = (i == first || i == second) && _weights[i] > Epsilon;
                if (kept)
                {
                    _composer.Set(VrmExpressionLayer.Viseme, MouthKeys[i], _weights[i]);
                    anyVoiced = true;
                    continue;
                }

                // A loser is cut on the spot rather than left to fade, so it can
                // never climb back into the pair; a released key hands the mouth
                // back to the emotional face instead of pinning it at zero.
                _weights[i] = 0f;
                _composer.Clear(VrmExpressionLayer.Viseme, MouthKeys[i]);
            }

            IsSilent = !anyVoiced;
        }

        private static int IndexOf(ExpressionKey key)
        {
            for (int i = 0; i < MouthKeys.Length; i++)
            {
                if (MouthKeys[i].Equals(key))
                    return i;
            }
            return -1;
        }
    }
}
