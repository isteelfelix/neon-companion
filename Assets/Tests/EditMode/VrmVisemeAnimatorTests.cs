using System.Collections.Generic;
using NeonCompanion.Runtime.Avatar3D;
using NUnit.Framework;
using UniVRM10;
using UnityEngine;

namespace NeonCompanion.Tests
{
    public sealed class VrmVisemeAnimatorTests
    {
        private const float Frame = 1f / 60f;

        private static readonly ExpressionKey[] MouthKeys =
        {
            ExpressionKey.Aa,
            ExpressionKey.Ih,
            ExpressionKey.Ou,
            ExpressionKey.Ee,
            ExpressionKey.Oh
        };

        private Dictionary<ExpressionKey, float> _model;
        private VrmExpressionComposer _composer;
        private VrmVisemeAnimator _visemes;

        [SetUp]
        public void SetUp()
        {
            _model = new Dictionary<ExpressionKey, float>(ExpressionKey.Comparer);
            _composer = new VrmExpressionComposer(Write);
            _visemes = new VrmVisemeAnimator(_composer);
        }

        [Test]
        public void AVisemeEasesInAndIsCappedBelowFullWeight()
        {
            _visemes.SetShape(ExpressionKey.Aa, 1f);

            Step();
            float afterOneFrame = Weight(ExpressionKey.Aa);
            Assert.Greater(afterOneFrame, 0f, "The viseme never started opening.");
            Assert.Less(afterOneFrame, VrmVisemeAnimator.MaxWeight,
                "A single frame jumped straight to the cap — no attack.");

            for (int i = 0; i < 20; i++)
                Step();

            Assert.AreEqual(
                VrmVisemeAnimator.MaxWeight,
                Weight(ExpressionKey.Aa),
                0.0001f,
                "A held viseme must settle at the cap, never above it.");
        }

        [Test]
        public void RequestedWeightIsHonouredWhenBelowTheCap()
        {
            _visemes.SetShape(ExpressionKey.Ee, 0.4f);
            for (int i = 0; i < 20; i++)
                Step();

            Assert.AreEqual(0.4f, Weight(ExpressionKey.Ee), 0.0001f);
        }

        [Test]
        public void ClearingReleasesTheMouthBackToTheEmotionUnderneath()
        {
            // An emotion holds aa faintly; speech should override it, then return
            // it when the mouth falls silent.
            _composer.Set(VrmExpressionLayer.Emotion, ExpressionKey.Aa, 0.35f);
            _visemes.SetShape(ExpressionKey.Aa, 1f);
            for (int i = 0; i < 10; i++)
                Step();
            Assert.AreEqual(
                VrmVisemeAnimator.MaxWeight,
                Weight(ExpressionKey.Aa),
                0.0001f,
                "Speech should own the mouth while it is voiced.");

            _visemes.Clear();
            for (int i = 0; i < 20; i++)
                Step();

            Assert.AreEqual(
                0.35f,
                Weight(ExpressionKey.Aa),
                0.0001f,
                "Once silent, the emotional mouth must come back.");
            Assert.IsTrue(_visemes.IsSilent);
        }

        [Test]
        public void NeverMoreThanTwoVisemesAreVoicedAtOnce()
        {
            ExpressionKey[] sequence =
            {
                ExpressionKey.Aa, ExpressionKey.Ih, ExpressionKey.Ou,
                ExpressionKey.Ee, ExpressionKey.Oh, ExpressionKey.Aa,
                ExpressionKey.Oh, ExpressionKey.Ih
            };

            for (int s = 0; s < sequence.Length; s++)
            {
                _visemes.SetShape(sequence[s], 1f);
                // Change phoneme every couple of frames, the way fast speech does.
                for (int f = 0; f < 2; f++)
                {
                    Step();
                    Assert.LessOrEqual(
                        VoicedCount(),
                        2,
                        "A third viseme leaked through winner-and-runner.");
                }
            }
        }

        [Test]
        public void ANewVisemeTakesOverAsTheOldOneReleases()
        {
            _visemes.SetShape(ExpressionKey.Aa, 1f);
            for (int i = 0; i < 10; i++)
                Step();

            _visemes.SetShape(ExpressionKey.Oh, 1f);
            Step();
            Assert.Greater(Weight(ExpressionKey.Oh), 0f, "The new viseme did not open.");
            Assert.Greater(
                Weight(ExpressionKey.Aa),
                0f,
                "The old viseme should still be releasing, not gone.");

            for (int i = 0; i < 20; i++)
                Step();
            Assert.AreEqual(
                VrmVisemeAnimator.MaxWeight,
                Weight(ExpressionKey.Oh),
                0.0001f);
            Assert.AreEqual(
                0f,
                Weight(ExpressionKey.Aa),
                0.0001f,
                "The replaced viseme must reach rest.");
        }

        [Test]
        public void ForceSilenceShutsTheMouthImmediately()
        {
            _visemes.SetShape(ExpressionKey.Ou, 1f);
            for (int i = 0; i < 10; i++)
                Step();
            Assert.Greater(Weight(ExpressionKey.Ou), 0f);

            _visemes.ForceSilence();
            Step();
            Assert.AreEqual(0f, Weight(ExpressionKey.Ou), 0.0001f);
            Assert.IsTrue(_visemes.IsSilent);
        }

        [Test]
        public void SilenceHoldsTheMouthClosedWithNoClaims()
        {
            Step();
            Assert.IsTrue(_visemes.IsSilent);
            Assert.AreEqual(0, VoicedCount());
        }

        private int VoicedCount()
        {
            int count = 0;
            for (int i = 0; i < MouthKeys.Length; i++)
            {
                if (Weight(MouthKeys[i]) > 0.001f)
                    count++;
            }
            return count;
        }

        /// <summary>One frame: reset what the model sees, tick, then flush.</summary>
        private void Step()
        {
            _model.Clear();
            _visemes.Tick(Frame);
            _composer.Apply();
        }

        private float Weight(ExpressionKey key)
        {
            float weight;
            if (_model.TryGetValue(key, out weight))
                return weight;
            return 0f;
        }

        private void Write(ExpressionKey key, float weight)
        {
            if (weight > 0f)
                _model[key] = weight;
            else
                _model.Remove(key);
        }
    }
}
