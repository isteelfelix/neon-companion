using System.Collections.Generic;
using NeonCompanion.Runtime.Avatar3D;
using NUnit.Framework;
using UniVRM10;

namespace NeonCompanion.Tests
{
    public sealed class VrmExpressionComposerTests
    {
        private List<KeyValuePair<ExpressionKey, float>> _writes;
        private VrmExpressionComposer _composer;

        [SetUp]
        public void SetUp()
        {
            _writes = new List<KeyValuePair<ExpressionKey, float>>();
            _composer = new VrmExpressionComposer(Record);
        }

        [Test]
        public void FlushWritesEachClaimedKeyExactlyOnce()
        {
            _composer.Set(VrmExpressionLayer.Emotion, ExpressionKey.Happy, 0.8f);
            _composer.Set(VrmExpressionLayer.Viseme, ExpressionKey.Aa, 1f);
            _composer.Apply();

            Assert.AreEqual(2, _writes.Count);
            AssertWritten(ExpressionKey.Happy, 0.8f);
            AssertWritten(ExpressionKey.Aa, 1f);
        }

        [Test]
        public void SpeechOutranksAnEmotionalMouthShapeInsteadOfStackingWithIt()
        {
            _composer.Set(VrmExpressionLayer.Emotion, ExpressionKey.Aa, 0.6f);
            _composer.Set(VrmExpressionLayer.Viseme, ExpressionKey.Aa, 1f);
            _composer.Apply();

            Assert.AreEqual(1, _writes.Count, "aa must resolve to a single value.");
            AssertWritten(ExpressionKey.Aa, 1f);
        }

        [Test]
        public void EndingSpeechHandsTheMouthBackToTheEmotionUnderneath()
        {
            _composer.Set(VrmExpressionLayer.Emotion, ExpressionKey.Aa, 0.4f);
            _composer.Set(VrmExpressionLayer.Viseme, ExpressionKey.Aa, 1f);
            _composer.Apply();

            _writes.Clear();
            _composer.ClearLayer(VrmExpressionLayer.Viseme);
            _composer.Apply();

            AssertWritten(ExpressionKey.Aa, 0.4f);
            Assert.AreEqual(
                1,
                _writes.Count,
                "The emotion still claims aa, so it must not be zeroed.");
        }

        [Test]
        public void EyelidTakesTheStrongerOfBlinkAndSquintEitherWayAround()
        {
            _composer.Set(VrmExpressionLayer.Emotion, ExpressionKey.Blink, 0.3f);
            _composer.Set(VrmExpressionLayer.Blink, ExpressionKey.Blink, 0.9f);
            _composer.Apply();
            AssertWritten(ExpressionKey.Blink, 0.9f);

            // Mid-ramp the reflex is weaker than the squint. Ranking alone would
            // drop the lid back to 0.1 here; Max keeps the squint visible.
            _writes.Clear();
            _composer.Set(VrmExpressionLayer.Blink, ExpressionKey.Blink, 0.1f);
            _composer.Apply();
            AssertWritten(ExpressionKey.Blink, 0.3f);
        }

        [Test]
        public void ReleasedKeyIsZeroedOnceAndThenLeftAlone()
        {
            _composer.Set(VrmExpressionLayer.Viseme, ExpressionKey.Oh, 1f);
            _composer.Apply();

            _writes.Clear();
            _composer.ClearLayer(VrmExpressionLayer.Viseme);
            _composer.Apply();
            AssertWritten(ExpressionKey.Oh, 0f);
            Assert.AreEqual(1, _writes.Count);

            _writes.Clear();
            _composer.Apply();
            Assert.AreEqual(
                0,
                _writes.Count,
                "A key already returned to neutral must not be written again.");
        }

        [Test]
        public void ZeroWeightHoldsTheKeyRatherThanReleasingIt()
        {
            _composer.Set(VrmExpressionLayer.Emotion, ExpressionKey.Happy, 0.8f);
            _composer.Apply();

            _writes.Clear();
            _composer.Set(VrmExpressionLayer.Emotion, ExpressionKey.Happy, 0f);
            _composer.Apply();
            AssertWritten(ExpressionKey.Happy, 0f);

            _writes.Clear();
            _composer.Apply();
            AssertWritten(
                ExpressionKey.Happy,
                0f,
                "An explicit zero is still a claim and keeps being written.");
        }

        [Test]
        public void AdditiveBlendSumsContributionsAndClampsAtOne()
        {
            _composer.SetBlend(ExpressionKey.Surprised, VrmExpressionBlend.Additive);
            _composer.Set(VrmExpressionLayer.Emotion, ExpressionKey.Surprised, 0.7f);
            _composer.Set(VrmExpressionLayer.Viseme, ExpressionKey.Surprised, 0.6f);
            _composer.Apply();

            AssertWritten(ExpressionKey.Surprised, 1f);
        }

        [Test]
        public void WeightsAreClampedWhenDeclared()
        {
            _composer.Set(VrmExpressionLayer.Emotion, ExpressionKey.Sad, 3.5f);
            _composer.Set(VrmExpressionLayer.Emotion, ExpressionKey.Angry, -2f);
            _composer.Apply();

            AssertWritten(ExpressionKey.Sad, 1f);
            AssertWritten(ExpressionKey.Angry, 0f);
        }

        [Test]
        public void CustomExpressionKeysSurviveTheRoundTrip()
        {
            ExpressionKey custom = ExpressionKey.CreateCustom("wink_neon");
            _composer.Set(VrmExpressionLayer.Emotion, custom, 0.5f);
            _composer.Apply();

            AssertWritten(custom, 0.5f);
        }

        private void Record(ExpressionKey key, float weight)
        {
            _writes.Add(new KeyValuePair<ExpressionKey, float>(key, weight));
        }

        private void AssertWritten(ExpressionKey key, float expected)
        {
            AssertWritten(key, expected, null);
        }

        private void AssertWritten(ExpressionKey key, float expected, string message)
        {
            for (int i = 0; i < _writes.Count; i++)
            {
                if (!_writes[i].Key.Equals(key))
                    continue;
                Assert.AreEqual(expected, _writes[i].Value, 0.0001f, message);
                return;
            }

            Assert.Fail(
                "Expected a write to " + key + " but saw " + DescribeWrites());
        }

        private string DescribeWrites()
        {
            if (_writes.Count == 0)
                return "no writes at all";

            var description = new System.Text.StringBuilder();
            for (int i = 0; i < _writes.Count; i++)
            {
                if (i > 0)
                    description.Append(", ");
                description.Append(_writes[i].Key).Append('=').Append(_writes[i].Value);
            }
            return description.ToString();
        }
    }
}
