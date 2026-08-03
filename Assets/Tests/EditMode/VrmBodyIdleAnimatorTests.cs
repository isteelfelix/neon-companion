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
        public void OneTickMovesTheBreathingChain()
        {
            var idle = new VrmBodyIdleAnimator(new Random(1));
            idle.Tick(0.7f); // a quarter breath in, so the chain is off-rest

            Assert.IsTrue(idle.Pose.ContainsKey(HumanBodyBones.Spine));
            Assert.IsTrue(idle.Pose.ContainsKey(HumanBodyBones.Chest));
            Assert.IsTrue(idle.Pose.ContainsKey(HumanBodyBones.Head));
            Assert.Greater(
                Quaternion.Angle(idle.Pose[HumanBodyBones.Chest], Quaternion.identity),
                0.01f,
                "The chest never left its rest rotation — nothing is breathing.");
        }

        [Test]
        public void ZeroOrNegativeDeltaIsIgnored()
        {
            var idle = new VrmBodyIdleAnimator(new Random(2));
            idle.Tick(Frame);
            Quaternion head = idle.Pose[HumanBodyBones.Head];

            idle.Tick(0f);
            idle.Tick(-0.5f);

            // The pose is left exactly as the last real tick produced it.
            Assert.AreEqual(0f, Quaternion.Angle(head, idle.Pose[HumanBodyBones.Head]), 1e-4f);
        }

        [Test]
        public void TheFeetStayPlanted_NoHipsOrLegAreEverDriven()
        {
            var idle = new VrmBodyIdleAnimator(new Random(4));
            for (int i = 0; i < 60 * 90; i++) // 90 s, crosses many breaks
            {
                idle.Tick(Frame);
                AssertUntouched(idle, HumanBodyBones.Hips);
                AssertUntouched(idle, HumanBodyBones.LeftUpperLeg);
                AssertUntouched(idle, HumanBodyBones.RightUpperLeg);
                AssertUntouched(idle, HumanBodyBones.LeftLowerLeg);
                AssertUntouched(idle, HumanBodyBones.RightLowerLeg);
            }
        }

        [Test]
        public void NoArmBonesAreDriven_OnlyShouldersMove()
        {
            var idle = new VrmBodyIdleAnimator(new Random(6));
            for (int i = 0; i < 60 * 90; i++)
            {
                idle.Tick(Frame);
                AssertUntouched(idle, HumanBodyBones.LeftUpperArm);
                AssertUntouched(idle, HumanBodyBones.RightUpperArm);
                AssertUntouched(idle, HumanBodyBones.LeftLowerArm);
                AssertUntouched(idle, HumanBodyBones.RightLowerArm);
                AssertUntouched(idle, HumanBodyBones.LeftHand);
                AssertUntouched(idle, HumanBodyBones.RightHand);
            }
        }

        [Test]
        public void AnIdleBreakEventuallyFires()
        {
            var idle = new VrmBodyIdleAnimator(new Random(8));
            bool fired = false;
            for (int i = 0; i < 60 * 20; i++)
            {
                idle.Tick(Frame);
                if (idle.ActiveGesture >= 0)
                {
                    fired = true;
                    break;
                }
            }
            Assert.IsTrue(fired, "No idle break fired within 20 s.");
        }

        [Test]
        public void HeadMotionIsContinuousAcrossBreakBoundaries()
        {
            // The port's whole point: gestures accumulate onto the base drift and
            // ease with a C2 envelope, so the head never snaps when a break starts
            // or ends. Overwriting instead of stacking (the old bug) spiked this.
            var idle = new VrmBodyIdleAnimator(new Random(13));
            idle.Tick(Frame);
            Quaternion previous = idle.Pose[HumanBodyBones.Head];
            float worst = 0f;

            for (int i = 0; i < 60 * 120; i++) // 2 min, dozens of breaks
            {
                idle.Tick(Frame);
                Quaternion current = idle.Pose[HumanBodyBones.Head];
                float step = Quaternion.Angle(previous, current);
                if (step > worst)
                    worst = step;
                previous = current;
            }

            // Normal per-frame head motion peaks well under a degree; a snap on a
            // break boundary would be several degrees in a single frame.
            Assert.Less(worst, 1.5f,
                "The head jerked " + worst.ToString("0.00") +
                " deg in one frame — a break is snapping, not easing.");
        }

        [Test]
        public void SameSeedReplaysTheSameMotion()
        {
            var a = new VrmBodyIdleAnimator(new Random(42));
            var b = new VrmBodyIdleAnimator(new Random(42));
            for (int i = 0; i < 60 * 60; i++)
            {
                a.Tick(Frame);
                b.Tick(Frame);
                Assert.AreEqual(a.ActiveGesture, b.ActiveGesture);
                Assert.AreEqual(
                    0f,
                    Quaternion.Angle(a.Pose[HumanBodyBones.Head], b.Pose[HumanBodyBones.Head]),
                    1e-4f);
            }
        }

        private static void AssertUntouched(VrmBodyIdleAnimator idle, HumanBodyBones bone)
        {
            Assert.IsFalse(
                idle.Pose.ContainsKey(bone),
                bone + " was driven — it must stay at its modelled pose.");
        }
    }
}
