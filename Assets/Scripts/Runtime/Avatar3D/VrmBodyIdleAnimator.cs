using System;
using System.Collections.Generic;
using UnityEngine;

namespace NeonCompanion.Runtime.Avatar3D
{
    /// <summary>
    /// The body's idle life: slow breathing and a planted weight shift under a
    /// stream of small, deliberate head "breaks" (glances, tilts, a nod) — the
    /// base-idle-plus-idle-breaks pattern a game plays so a standing character
    /// never reads as frozen.
    /// <para>
    /// Pure signal generation, like <see cref="VrmIdleAnimator"/>. Each tick it
    /// accumulates a normalized local rotation per humanoid bone and hands the set
    /// to a sink; the driver writes them onto the VRM control rig (identity there =
    /// the modelled arms-down pose, so a plain delta needs no re-basing and works
    /// on any VRM). Randomness is injected so a test can pin the cadence.
    /// </para>
    /// <para>
    /// Rotations ACCUMULATE (breath + sway + a gesture on the same bone multiply
    /// together). The sandbox port hinged on this: writing instead of stacking made
    /// a gesture swallow the base drift and snap it back on release — a visible jerk.
    /// </para>
    /// </summary>
    internal sealed class VrmBodyIdleAnimator
    {
        // Flip to -1f if a nod reads as looking UP on device: this is the one axis
        // with an inherent direction (yaw/roll are symmetric or random-signed), so
        // a single constant reconciles the three.js sandbox with Unity's handedness.
        private const float PitchDir = 1f;

        // Breathing — the chest lifts on a slow sine, the spine takes a share, the
        // shoulders follow.
        private const float BreathHz = 0.24f;
        private const float BreathAmp = 0.033f;
        private const float BreathShoulder = 0.5f;

        // Weight shift — two incommensurate sines so it never visibly loops. Driven
        // from the SPINE, never the hips: rotating hips with no leg IK swings the
        // legs like a pendulum. From the spine up the feet stay planted.
        private const float SwayPeriodA = 9.0f;
        private const float SwayPeriodB = 13.7f;
        private const float SwayAmp = 0.02f;

        // Slow posture drift carried on the neck and head.
        private const float DriftPeriod = 21.0f;
        private const float DriftYaw = 0.045f;
        private const float DriftPitch = 0.02f;

        // Head "looking around" — a continuous slow scan on two incommensurate
        // periods, on TOP of the drift above and the discrete glance breaks below.
        // From the mocap idle the head carried ~30° peak-to-peak of yaw at a ~8 s
        // beat; kept well under that here so the breaks still have room to add.
        private const float LookPeriodA = 7.7f;
        private const float LookPeriodB = 16.3f;
        private const float LookYaw = 0.11f;
        private const float LookPitch = 0.03f;

        // Arm life — the arms must not hang like a mannequin. A gentle in/out swing
        // carried upper arm → forearm → hand, each on its own period so the two
        // sides never mirror and nothing loops. Periods/reach are taken from a mocap
        // idle (a ~6 s upper-body sway, forearms a touch faster). SIGNS/AXES are the
        // first tuning knob if a limb swings the wrong way on the model — same story
        // as PitchDir; a single sign flip reconciles the capture with the rig.
        private const float ArmPeriodL = 5.8f;
        private const float ArmPeriodR = 7.7f;
        private const float ForearmPeriodL = 6.4f;
        private const float ForearmPeriodR = 4.9f;
        private const float HandPeriodL = 11.5f;
        private const float HandPeriodR = 8.3f;
        private const float ArmAmp = 0.09f;      // ~10° peak-to-peak on the upper arm
        private const float ForearmAmp = 0.09f;
        private const float HandAmp = 0.10f;

        // Idle-break scheduling (seconds between breaks).
        private const float BreakMin = 4.0f;
        private const float BreakMax = 9.0f;

        // Gesture library. Parallel arrays keep it allocation-free and C# 9 friendly
        // (no switch expressions). Duration is [easeIn, hold, easeOut]; weight biases
        // the random picker. Index maps to the switch in ApplyGesture.
        private static readonly float[][] GestureDur =
        {
            new float[] { 1.3f, 2.4f, 1.5f }, // 0 WeightShift
            new float[] { 1.3f, 1.6f, 1.5f }, // 1 GlanceDiag
            new float[] { 1.5f, 1.5f, 1.6f }, // 2 NodDown
            new float[] { 1.4f, 2.0f, 1.5f }, // 3 SoftTilt
            new float[] { 1.9f, 2.4f, 1.9f }, // 4 TurnDiag
            new float[] { 1.5f, 1.3f, 1.6f }, // 5 LookUp
        };
        private static readonly float[] GestureWeight =
        {
            1.0f, 1.6f, 1.3f, 1.1f, 1.1f, 0.7f,
        };
        private const int GestureCount = 6;

        private readonly System.Random _random;
        private readonly Dictionary<HumanBodyBones, Quaternion> _pose =
            new Dictionary<HumanBodyBones, Quaternion>();

        private float _time;
        private int _active;       // -1 while resting between breaks
        private float _gestureTime;
        private int _dir;          // yaw side of the active gesture
        private int _dir2;         // vertical (pitch) side of the active gesture
        private float _wait;

        internal VrmBodyIdleAnimator(System.Random random)
        {
            _random = random != null ? random : new System.Random();
            _active = -1;
            _wait = 3.5f;
        }

        /// <summary>The bones the last <see cref="Tick"/> wants to move, and to what.</summary>
        internal IReadOnlyDictionary<HumanBodyBones, Quaternion> Pose => _pose;

        /// <summary>Index of the running idle break, or -1 while resting. For tests.</summary>
        internal int ActiveGesture => _active;

        internal void Tick(float deltaTime)
        {
            if (deltaTime <= 0f)
                return;
            _time += deltaTime;
            _pose.Clear();
            Breathing();
            Sway();
            Drift();
            ArmLife();
            TickGesture(deltaTime);
        }

        internal void Apply(Action<HumanBodyBones, Quaternion> sink)
        {
            if (sink == null)
                return;
            foreach (KeyValuePair<HumanBodyBones, Quaternion> entry in _pose)
                sink(entry.Key, entry.Value);
        }

        // ── base layers ──

        private void Breathing()
        {
            float breath = Mathf.Sin(_time * BreathHz * 2f * Mathf.PI);
            AddRot(HumanBodyBones.Spine, breath * BreathAmp * 0.35f, 0f, 0f);
            AddRot(HumanBodyBones.Chest, breath * BreathAmp * 0.5f, 0f, 0f);
            AddRot(HumanBodyBones.UpperChest, breath * BreathAmp, 0f, 0f);
            float shoulder = breath * BreathAmp * BreathShoulder;
            AddRot(HumanBodyBones.LeftShoulder, 0f, 0f, -shoulder);
            AddRot(HumanBodyBones.RightShoulder, 0f, 0f, shoulder);
        }

        private void Sway()
        {
            float sway = SwaySignal();
            AddRot(HumanBodyBones.Spine, 0f, 0f, sway * SwayAmp);
            AddRot(HumanBodyBones.Chest, 0f, 0f, -sway * SwayAmp * 0.5f);
            float breath = Mathf.Sin(_time * BreathHz * 2f * Mathf.PI);
            AddRot(HumanBodyBones.LeftShoulder, 0f, 0f, sway * SwayAmp * 0.3f + breath * 0.006f);
            AddRot(HumanBodyBones.RightShoulder, 0f, 0f, sway * SwayAmp * 0.3f - breath * 0.006f);
        }

        private void Drift()
        {
            float sway = SwaySignal();
            float drift = Mathf.Sin(_time / DriftPeriod * 2f * Mathf.PI);
            // Continuous "looking around": two incommensurate periods so the head
            // scans without ever settling into a loop. The discrete glance breaks
            // ride on top of this.
            float look =
                Mathf.Sin(_time / LookPeriodA * 2f * Mathf.PI) * 0.6f +
                Mathf.Sin(_time / LookPeriodB * 2f * Mathf.PI + 0.8f) * 0.4f;
            AddRot(HumanBodyBones.Neck,
                look * LookPitch * 0.4f,
                drift * DriftYaw * 0.3f + look * LookYaw * 0.4f,
                -sway * SwayAmp * 0.4f);
            AddRot(HumanBodyBones.Head,
                drift * DriftPitch * Mathf.Sin(_time * 0.13f) + look * LookPitch,
                drift * DriftYaw + look * LookYaw,
                -sway * SwayAmp * 0.3f);
        }

        // The arms breathe with the body instead of hanging: a slow swing on the
        // upper arm, a phase-lagged bend on the forearm, a wrist float on the hand.
        // Left and right run on different periods, so the pose never reads as
        // symmetric and never repeats. Fingers are left at the modelled pose.
        private void ArmLife()
        {
            float upperL = Mathf.Sin(_time / ArmPeriodL * 2f * Mathf.PI);
            float upperR = Mathf.Sin(_time / ArmPeriodR * 2f * Mathf.PI + 1.3f);
            AddRot(HumanBodyBones.LeftUpperArm, ArmAmp * 0.3f * upperL, 0f, ArmAmp * upperL);
            AddRot(HumanBodyBones.RightUpperArm, ArmAmp * 0.3f * upperR, 0f, -ArmAmp * upperR);

            float foreL = Mathf.Sin(_time / ForearmPeriodL * 2f * Mathf.PI + 0.5f);
            float foreR = Mathf.Sin(_time / ForearmPeriodR * 2f * Mathf.PI + 2.1f);
            AddRot(HumanBodyBones.LeftLowerArm, ForearmAmp * foreL, ForearmAmp * 0.3f * foreL, 0f);
            AddRot(HumanBodyBones.RightLowerArm, ForearmAmp * foreR, -ForearmAmp * 0.3f * foreR, 0f);

            float handL = Mathf.Sin(_time / HandPeriodL * 2f * Mathf.PI + 1.7f);
            float handR = Mathf.Sin(_time / HandPeriodR * 2f * Mathf.PI + 0.4f);
            AddRot(HumanBodyBones.LeftHand, HandAmp * handL, HandAmp * 0.4f * handL, 0f);
            AddRot(HumanBodyBones.RightHand, HandAmp * handR, -HandAmp * 0.4f * handR, 0f);
        }

        private float SwaySignal()
        {
            return Mathf.Sin(_time / SwayPeriodA * 2f * Mathf.PI) * 0.6f +
                Mathf.Sin(_time / SwayPeriodB * 2f * Mathf.PI) * 0.4f;
        }

        // ── idle breaks ──

        private void TickGesture(float deltaTime)
        {
            if (_active < 0)
            {
                _wait -= deltaTime;
                if (_wait > 0f)
                    return;
                _active = PickGesture();
                _gestureTime = 0f;
                _dir = NextSign();
                _dir2 = NextSign();
            }

            float[] dur = GestureDur[_active];
            float total = dur[0] + dur[1] + dur[2];
            _gestureTime += deltaTime;
            ApplyGesture(_active, Envelope(_gestureTime, dur[0], dur[1], dur[2]), _dir, _dir2);
            if (_gestureTime >= total)
            {
                _active = -1;
                _wait = BreakMin + (float)_random.NextDouble() * (BreakMax - BreakMin);
            }
        }

        private int PickGesture()
        {
            float total = 0f;
            for (int i = 0; i < GestureCount; i++)
                total += GestureWeight[i];
            float r = (float)_random.NextDouble() * total;
            for (int i = 0; i < GestureCount; i++)
            {
                r -= GestureWeight[i];
                if (r <= 0f)
                    return i;
            }
            return 0;
        }

        // k = eased 0..1 weight; d = yaw side; d2 = vertical side (+down / -up).
        private void ApplyGesture(int index, float k, int d, int d2)
        {
            float df = d;
            float d2f = d2;
            if (index == 0) // WeightShift — spine-driven, feet planted
            {
                AddRot(HumanBodyBones.Spine, 0f, 0f, 0.045f * df * k);
                AddRot(HumanBodyBones.Chest, 0f, 0f, -0.028f * df * k);
                AddRot(HumanBodyBones.Neck, 0f, 0f, 0.015f * df * k);
                AddRot(HumanBodyBones.Head, 0.018f * k, 0.045f * df * k, 0.012f * df * k);
            }
            else if (index == 1) // GlanceDiag — yaw + a little pitch
            {
                AddRot(HumanBodyBones.Neck, 0.025f * d2f * k, 0.075f * df * k, 0f);
                AddRot(HumanBodyBones.Head, 0.045f * d2f * k, 0.10f * df * k, 0.02f * df * k);
            }
            else if (index == 2) // NodDown — gentle look down, slight side
            {
                AddRot(HumanBodyBones.Neck, 0.055f * k, 0.02f * df * k, 0f);
                AddRot(HumanBodyBones.Head, 0.085f * k, 0.03f * df * k, 0.01f * df * k);
            }
            else if (index == 3) // SoftTilt — roll with some pitch → diagonal
            {
                AddRot(HumanBodyBones.Neck, 0.025f * d2f * k, 0.02f * df * k, 0.045f * df * k);
                AddRot(HumanBodyBones.Head, 0.045f * d2f * k, 0.04f * df * k, 0.06f * df * k);
            }
            else if (index == 4) // TurnDiag — deliberate diagonal turn, longer hold
            {
                AddRot(HumanBodyBones.Neck, 0.03f * d2f * k, 0.10f * df * k, 0.008f * df * k);
                AddRot(HumanBodyBones.Head, 0.05f * d2f * k, 0.14f * df * k, 0.02f * df * k);
            }
            else if (index == 5) // LookUp — subtle glance up to a side
            {
                AddRot(HumanBodyBones.Neck, -0.04f * k, 0.03f * df * k, 0f);
                AddRot(HumanBodyBones.Head, -0.06f * k, 0.055f * df * k, 0f);
            }
        }

        // ── helpers ──

        private void AddRot(HumanBodyBones bone, float pitch, float yaw, float roll)
        {
            Quaternion delta = Quaternion.Euler(
                PitchDir * pitch * Mathf.Rad2Deg,
                yaw * Mathf.Rad2Deg,
                roll * Mathf.Rad2Deg);
            Quaternion current;
            if (_pose.TryGetValue(bone, out current))
                _pose[bone] = current * delta;
            else
                _pose[bone] = delta;
        }

        private int NextSign()
        {
            return _random.NextDouble() < 0.5 ? -1 : 1;
        }

        /// <summary>
        /// Quintic smootherstep envelope: zero first AND second derivative at both
        /// ends, so a break eases in and out with no acceleration pop. The base idle
        /// is the shared enter/exit pose (every term scales by the returned weight).
        /// </summary>
        private static float Envelope(float t, float easeIn, float hold, float easeOut)
        {
            if (t < easeIn)
                return Smooth(t / easeIn);
            if (t < easeIn + hold)
                return 1f;
            if (t < easeIn + hold + easeOut)
                return 1f - Smooth((t - easeIn - hold) / easeOut);
            return 0f;
        }

        private static float Smooth(float x)
        {
            if (x <= 0f)
                return 0f;
            if (x >= 1f)
                return 1f;
            return x * x * x * (x * (x * 6f - 15f) + 10f);
        }
    }
}
