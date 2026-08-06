using System;
using System.Collections.Generic;
using UnityEngine;

namespace NeonCompanion.Runtime.Avatar3D
{
    /// <summary>
    /// The body's idle life, played back from a real mocap capture (<c>idle.vrma</c>)
    /// rather than hand-authored: breathing, arm motion, a foot-to-foot weight shift,
    /// and — once per loop — a small "adjusts her sleeves" moment.
    /// <para>
    /// The capture is stored as a <b>band-limited Fourier series</b> per bone (see
    /// <see cref="VrmIdleClipData"/>). Each bone's motion is the rotation DELTA from
    /// the clip's mean pose; dropping the high harmonics removes the 30 fps mocap
    /// jitter, and a Fourier series is C-infinity continuous and exactly periodic
    /// over the clip length, so the idle <b>loops with no seam</b>. Because it is a
    /// delta from the mean, any constant rest-pose difference between the capture rig
    /// and this VRM's control rig cancels — the driver applies <c>rest * delta</c>, so
    /// the arms hang at each avatar's own rest and only the captured MOTION rides on
    /// top. The only glTF→Unity conversion baked in is handedness (ReverseZ).
    /// </para>
    /// <para>
    /// Pure signal generation, like <see cref="VrmIdleAnimator"/>: each tick it fills
    /// a per-bone delta set plus a hips position offset and hands them to the driver.
    /// Fingers are left at the modelled pose (the capture's fingers were incidental).
    /// </para>
    /// </summary>
    internal sealed class VrmBodyIdleAnimator
    {
        // Playback speed multiplier. 0.9 plays the capture 10% slower; the loop stays
        // seamless (same periodic function, evaluated slower).
        private const float PlaybackSpeed = 0.9f;

        // Uprightness correction. The capture's neutral stance leans back ("hanging by
        // the shoulders"). We keep the MOTION but pull each posture bone's AVERAGE pose
        // toward vertical by this fraction (0 = the capture's leaned stance untouched,
        // 1 = fully upright). In normalized space "upright" for these bones is identity,
        // so no external reference pose is needed. Arms/hands/head keep their capture
        // posture (their identity is the T-pose, which is NOT what we want there).
        // 0 = off: the current capture is already upright, so no correction is applied.
        // Left in as a knob in case a future capture leans.
        private const float Uprightness = 0f;
        private static readonly HumanBodyBones[] PostureBones =
        {
            HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest,
            HumanBodyBones.UpperChest,
            HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg,
            HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg,
            HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot,
        };

        private readonly float _period;
        private readonly int _harmonics;
        private readonly HumanBodyBones[] _bones;
        private readonly float[][] _rot;     // per bone: 4 components * (1 + 2*harmonics)
        private readonly float[] _hipsDelta; // 3 components * (1 + 2*harmonics)
        private readonly float[] _cos;
        private readonly float[] _sin;
        private readonly Quaternion[] _correction; // constant per bone; identity = none
        private readonly Dictionary<HumanBodyBones, Quaternion> _pose =
            new Dictionary<HumanBodyBones, Quaternion>();

        private float _time;
        private Vector3 _hipsNormalizedDelta;

        // random is kept for call-site/DI compatibility; the motion is now fully
        // determined by the captured clip, so it is unused.
        internal VrmBodyIdleAnimator(System.Random random)
        {
            _period = VrmIdleClipData.Period;
            _harmonics = VrmIdleClipData.Harmonics;
            _cos = new float[_harmonics + 1];
            _sin = new float[_harmonics + 1];

            int stride = 4 * (1 + 2 * _harmonics);
            float[] rot = DecodeFloats(VrmIdleClipData.RotCoeffs);
            string[] names = VrmIdleClipData.Bones;
            _bones = new HumanBodyBones[names.Length];
            _rot = new float[names.Length][];
            for (int i = 0; i < names.Length; i++)
            {
                _bones[i] = (HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), names[i]);
                float[] block = new float[stride];
                Array.Copy(rot, i * stride, block, 0, stride);
                _rot[i] = block;
            }
            _hipsDelta = DecodeFloats(VrmIdleClipData.HipsDeltaCoeffs);
            _correction = BuildCorrections(stride);
        }

        // For each posture bone, Corr = Slerp(mean, identity, Uprightness) * mean^-1.
        // Applied on the left of the reconstructed rotation it swaps the leaned average
        // for an uprighted one while leaving the motion (residual) untouched. Non-posture
        // bones get identity.
        private Quaternion[] BuildCorrections(int stride)
        {
            Quaternion[] corr = new Quaternion[_bones.Length];
            HashSet<HumanBodyBones> posture = new HashSet<HumanBodyBones>(PostureBones);
            int per = 1 + 2 * _harmonics;
            for (int i = 0; i < _bones.Length; i++)
            {
                if (Uprightness <= 0f || !posture.Contains(_bones[i]))
                {
                    corr[i] = Quaternion.identity;
                    continue;
                }
                float[] c = _rot[i];
                Quaternion mean = new Quaternion(c[0 * per], c[1 * per], c[2 * per], c[3 * per]);
                float n = Mathf.Sqrt(mean.x * mean.x + mean.y * mean.y + mean.z * mean.z + mean.w * mean.w);
                if (n < 1e-6f)
                {
                    corr[i] = Quaternion.identity;
                    continue;
                }
                float inv = 1f / n;
                mean = new Quaternion(mean.x * inv, mean.y * inv, mean.z * inv, mean.w * inv);
                Quaternion upright = Quaternion.Slerp(mean, Quaternion.identity, Uprightness);
                corr[i] = upright * Quaternion.Inverse(mean);
            }
            return corr;
        }

        private static float[] DecodeFloats(string base64)
        {
            byte[] bytes = Convert.FromBase64String(base64);
            float[] values = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
            return values;
        }

        /// <summary>Absolute normalized local rotation per captured bone this frame.</summary>
        internal IReadOnlyDictionary<HumanBodyBones, Quaternion> Pose => _pose;

        /// <summary>
        /// Hips weight-shift offset as a NORMALIZED delta (metres per unit hip height).
        /// The driver scales it by the target avatar's own hip height, matching
        /// <c>Vrm10Retarget</c>, so it transfers to any VRM.
        /// </summary>
        internal Vector3 HipsNormalizedDelta => _hipsNormalizedDelta;

        internal void Tick(float deltaTime)
        {
            if (deltaTime <= 0f)
                return;
            _time += deltaTime * PlaybackSpeed;
            if (_time >= _period)
                _time -= _period * Mathf.Floor(_time / _period);

            float w = 2f * Mathf.PI / _period;
            for (int k = 0; k <= _harmonics; k++)
            {
                float a = w * k * _time;
                _cos[k] = Mathf.Cos(a);
                _sin[k] = Mathf.Sin(a);
            }

            _pose.Clear();
            int per = 1 + 2 * _harmonics;
            for (int i = 0; i < _bones.Length; i++)
            {
                float[] c = _rot[i];
                float x = Reconstruct(c, 0 * per);
                float y = Reconstruct(c, 1 * per);
                float z = Reconstruct(c, 2 * per);
                float wq = Reconstruct(c, 3 * per);
                float n = Mathf.Sqrt(x * x + y * y + z * z + wq * wq);
                if (n > 1e-6f)
                {
                    float inv = 1f / n;
                    Quaternion q = new Quaternion(x * inv, y * inv, z * inv, wq * inv);
                    _pose[_bones[i]] = _correction[i] * q;
                }
                else
                {
                    _pose[_bones[i]] = _correction[i];
                }
            }

            _hipsNormalizedDelta = new Vector3(
                Reconstruct(_hipsDelta, 0 * per),
                Reconstruct(_hipsDelta, 1 * per),
                Reconstruct(_hipsDelta, 2 * per));
        }

        // value(t) = mean + Σ Ak*cos(k w t) + Bk*sin(k w t), coefficients laid out as
        // [mean, A1, B1, A2, B2, ...] starting at offset.
        private float Reconstruct(float[] coeffs, int offset)
        {
            float v = coeffs[offset];
            for (int k = 1; k <= _harmonics; k++)
            {
                v += coeffs[offset + 2 * k - 1] * _cos[k] + coeffs[offset + 2 * k] * _sin[k];
            }
            return v;
        }
    }
}
