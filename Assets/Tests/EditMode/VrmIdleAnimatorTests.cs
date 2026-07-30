using NeonCompanion.Runtime.Avatar3D;
using NUnit.Framework;
using UnityEngine;
using Random = System.Random;

namespace NeonCompanion.Tests
{
    public sealed class VrmIdleAnimatorTests
    {
        private const float Frame = 1f / 60f;

        [Test]
        public void EyesStartOpenAndStayOpenUntilTheFirstBlink()
        {
            var idle = new VrmIdleAnimator(new Random(1));
            Assert.AreEqual(0f, idle.BlinkWeight);

            // Nothing can fire before the shortest interval has elapsed.
            float safe = VrmIdleAnimator.BlinkIntervalMin - 0.1f;
            RunFor(idle, safe);
            Assert.AreEqual(
                0f,
                idle.BlinkWeight,
                "A blink fired before the minimum interval.");
        }

        [Test]
        public void ABlinkFiresWithinTheIntervalAndFullyCloses()
        {
            var idle = new VrmIdleAnimator(new Random(7));

            float elapsed = 0f;
            float peak = 0f;
            bool opened = false;
            while (elapsed < VrmIdleAnimator.BlinkIntervalMax + 0.5f)
            {
                idle.Tick(Frame);
                elapsed += Frame;
                if (idle.BlinkWeight > peak)
                    peak = idle.BlinkWeight;
                if (peak > 0.9f && idle.BlinkWeight <= 0f)
                {
                    opened = true;
                    break;
                }
            }

            Assert.Greater(peak, 0.98f, "The lid never fully closed.");
            Assert.IsTrue(opened, "The eye closed but never reopened.");
        }

        [Test]
        public void BlinkClosesThenOpensWithoutReversingDirection()
        {
            var idle = new VrmIdleAnimator(new Random(3));

            // Walk up to the blink.
            while (idle.BlinkWeight <= 0f)
                idle.Tick(Frame);

            // Closing half: weight must not decrease until it has peaked.
            float previous = idle.BlinkWeight;
            bool peaked = false;
            for (int i = 0; i < 60; i++)
            {
                idle.Tick(Frame);
                float current = idle.BlinkWeight;
                if (!peaked)
                {
                    if (current + 0.0001f < previous)
                        peaked = true;
                    else
                        Assert.GreaterOrEqual(
                            current + 0.0001f,
                            previous,
                            "The lid reversed while still closing.");
                }
                else
                {
                    Assert.LessOrEqual(
                        current,
                        previous + 0.0001f,
                        "The lid reversed while opening.");
                    if (current <= 0f)
                        break;
                }
                previous = current;
            }

            Assert.IsTrue(peaked, "The blink never reached its peak.");
        }

        [Test]
        public void SaccadeOffsetsNeverLeaveTheAmplitudeBox()
        {
            var idle = new VrmIdleAnimator(new Random(11));
            const float bound = VrmIdleAnimator.SaccadeAmplitude + 0.0005f;

            for (int i = 0; i < 6000; i++)
            {
                idle.Tick(Frame);
                Assert.LessOrEqual(Mathf.Abs(idle.GazeOffsetHorizontal), bound);
                Assert.LessOrEqual(
                    Mathf.Abs(idle.GazeOffsetVertical),
                    VrmIdleAnimator.SaccadeAmplitude * 0.5f + 0.0005f);
            }
        }

        [Test]
        public void TheEyeActuallyMovesOverTime()
        {
            var idle = new VrmIdleAnimator(new Random(5));
            RunFor(idle, 0.5f);
            float first = idle.GazeOffsetHorizontal;

            bool moved = false;
            for (int i = 0; i < 600; i++)
            {
                idle.Tick(Frame);
                if (Mathf.Abs(idle.GazeOffsetHorizontal - first) > 0.02f)
                {
                    moved = true;
                    break;
                }
            }

            Assert.IsTrue(moved, "The gaze offset never changed — the eye is dead.");
        }

        [Test]
        public void ASaccadeEasesTowardItsTargetRatherThanJumping()
        {
            var idle = new VrmIdleAnimator(new Random(9));

            float before = idle.GazeOffsetHorizontal;
            idle.Tick(Frame);
            float afterOneFrame = idle.GazeOffsetHorizontal;

            // One 1/60 s step must not span the whole amplitude range.
            Assert.Less(
                Mathf.Abs(afterOneFrame - before),
                VrmIdleAnimator.SaccadeAmplitude,
                "The eye teleported to its fixation instead of flicking to it.");
        }

        [Test]
        public void ZeroOrNegativeDeltaDoesNothing()
        {
            var idle = new VrmIdleAnimator(new Random(2));
            RunFor(idle, 1f);
            float blink = idle.BlinkWeight;
            float h = idle.GazeOffsetHorizontal;

            idle.Tick(0f);
            idle.Tick(-0.5f);

            Assert.AreEqual(blink, idle.BlinkWeight);
            Assert.AreEqual(h, idle.GazeOffsetHorizontal);
        }

        [Test]
        public void SameSeedReplaysTheSameMotion()
        {
            var a = new VrmIdleAnimator(new Random(42));
            var b = new VrmIdleAnimator(new Random(42));

            for (int i = 0; i < 2000; i++)
            {
                a.Tick(Frame);
                b.Tick(Frame);
                Assert.AreEqual(a.BlinkWeight, b.BlinkWeight, 1e-6f);
                Assert.AreEqual(
                    a.GazeOffsetHorizontal, b.GazeOffsetHorizontal, 1e-6f);
                Assert.AreEqual(
                    a.GazeOffsetVertical, b.GazeOffsetVertical, 1e-6f);
            }
        }

        private static void RunFor(VrmIdleAnimator idle, float seconds)
        {
            float elapsed = 0f;
            while (elapsed < seconds)
            {
                idle.Tick(Frame);
                elapsed += Frame;
            }
        }
    }
}
