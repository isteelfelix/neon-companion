using NeonCompanion.Runtime.Avatar3D;
using NUnit.Framework;
using UnityEngine;
using Random = System.Random;

namespace NeonCompanion.Tests
{
    public sealed class VrmBodyIdleAnimatorTests
    {
        private const float Frame = 1f / 60f;

        [Test]
        public void PoseIsEmptyBeforeTheFirstTick()
        {
            var idle = new VrmBodyIdleAnimator(new Random(1));
            Assert.AreEqual(0, idle.Pose.Count);
        }

        [Test]
        public void OneTickDrivesTheWholeCapturedBody()
        {
            // Absolute normalized rotations for every captured bone — torso, head,
            // the full arm chain, legs, and (unlike the first attempt) the fingers.
            var idle = new VrmBodyIdleAnimator(new Random(1));
            idle.Tick(0.5f);

            Assert.IsTrue(idle.Pose.ContainsKey(HumanBodyBones.Spine));
            Assert.IsTrue(idle.Pose.ContainsKey(HumanBodyBones.Head));
            Assert.IsTrue(idle.Pose.ContainsKey(HumanBodyBones.LeftUpperArm));
            Assert.IsTrue(idle.Pose.ContainsKey(HumanBodyBones.LeftHand));
            Assert.IsTrue(idle.Pose.ContainsKey(HumanBodyBones.LeftUpperLeg));
            Assert.IsTrue(idle.Pose.ContainsKey(HumanBodyBones.LeftFoot));
        }

        [Test]
        public void TheFingersAreDriven()
        {
            // The whole point of going through UniVRM's retarget: the finger bones
            // come across with correct Unity enum names and actually move.
            var idle = new VrmBodyIdleAnimator(new Random(1));
            idle.Tick(0.5f);
            Assert.IsTrue(idle.Pose.ContainsKey(HumanBodyBones.LeftIndexProximal));
            Assert.IsTrue(idle.Pose.ContainsKey(HumanBodyBones.RightThumbProximal));
            Assert.IsTrue(idle.Pose.ContainsKey(HumanBodyBones.LeftLittleDistal));
        }

        [Test]
        public void TheMotionProgressesOverTime()
        {
            var idle = new VrmBodyIdleAnimator(new Random(1));
            idle.Tick(0.3f);
            Quaternion early = idle.Pose[HumanBodyBones.LeftLowerArm];
            idle.Tick(1.2f);
            Quaternion later = idle.Pose[HumanBodyBones.LeftLowerArm];
            Assert.Greater(Quaternion.Angle(early, later), 0.1f,
                "The forearm did not move between two times — the clip is frozen.");
        }

        [Test]
        public void ZeroOrNegativeDeltaIsIgnored()
        {
            var idle = new VrmBodyIdleAnimator(new Random(2));
            idle.Tick(Frame);
            Quaternion head = idle.Pose[HumanBodyBones.Head];
            Vector3 hips = idle.HipsNormalizedDelta;

            idle.Tick(0f);
            idle.Tick(-0.5f);

            Assert.AreEqual(0f, Quaternion.Angle(head, idle.Pose[HumanBodyBones.Head]), 1e-4f);
            Assert.AreEqual(hips, idle.HipsNormalizedDelta);
        }

        [Test]
        public void TheWeightShiftMovesHipsLegsAndTranslatesTheHips()
        {
            var idle = new VrmBodyIdleAnimator(new Random(3));
            Quaternion firstLeg = Quaternion.identity;
            bool legMoved = false;
            bool hipsTranslated = false;
            idle.Tick(0.1f);
            firstLeg = idle.Pose[HumanBodyBones.LeftUpperLeg];
            for (int i = 0; i < 60 * 30; i++)
            {
                idle.Tick(Frame);
                if (Quaternion.Angle(firstLeg, idle.Pose[HumanBodyBones.LeftUpperLeg]) > 0.5f)
                    legMoved = true;
                if (idle.HipsNormalizedDelta.magnitude > 1e-4f)
                    hipsTranslated = true;
            }
            Assert.IsTrue(idle.Pose.ContainsKey(HumanBodyBones.Hips), "Hips not driven.");
            Assert.IsTrue(legMoved, "The legs never moved — no weight shift.");
            Assert.IsTrue(hipsTranslated, "The hips never translated — weight shift is incomplete.");
        }

        [Test]
        public void HeadMotionIsContinuous_IncludingTheLoopWrap()
        {
            var idle = new VrmBodyIdleAnimator(new Random(13));
            idle.Tick(Frame);
            Quaternion previous = idle.Pose[HumanBodyBones.Head];
            float worst = 0f;

            for (int i = 0; i < 60 * 70; i++) // > 2 loops, crosses the seam twice
            {
                idle.Tick(Frame);
                Quaternion current = idle.Pose[HumanBodyBones.Head];
                float step = Quaternion.Angle(previous, current);
                if (step > worst)
                    worst = step;
                previous = current;
            }

            Assert.Less(worst, 1.5f,
                "The head jerked " + worst.ToString("0.00") +
                " deg in one frame — the loop is snapping, not easing.");
        }

        [Test]
        public void TheLoopIsSeamless()
        {
            var a = new VrmBodyIdleAnimator(new Random(0));
            a.Tick(0.02f);
            Quaternion atStart = a.Pose[HumanBodyBones.Head];

            var b = new VrmBodyIdleAnimator(new Random(0));
            b.Tick(VrmIdleClipData.Period - 0.02f);
            Quaternion beforeWrap = b.Pose[HumanBodyBones.Head];

            Assert.Less(Quaternion.Angle(atStart, beforeWrap), 2.0f,
                "The head pose at the loop seam is discontinuous.");
        }

        [Test]
        public void TheMotionIsDeterministic()
        {
            var a = new VrmBodyIdleAnimator(new Random(42));
            var b = new VrmBodyIdleAnimator(new Random(7));
            for (int i = 0; i < 60 * 60; i++)
            {
                a.Tick(Frame);
                b.Tick(Frame);
                Assert.AreEqual(
                    0f,
                    Quaternion.Angle(a.Pose[HumanBodyBones.Head], b.Pose[HumanBodyBones.Head]),
                    1e-4f);
                Assert.AreEqual(a.HipsNormalizedDelta, b.HipsNormalizedDelta);
            }
        }
    }
}
