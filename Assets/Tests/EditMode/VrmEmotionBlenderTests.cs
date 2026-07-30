using System.Collections.Generic;
using NeonCompanion.Runtime.Avatar3D;
using NUnit.Framework;
using UniVRM10;

namespace NeonCompanion.Tests
{
    public sealed class VrmEmotionBlenderTests
    {
        private Dictionary<ExpressionKey, float> _model;
        private VrmExpressionComposer _composer;
        private VrmEmotionBlender _blender;

        [SetUp]
        public void SetUp()
        {
            _model = new Dictionary<ExpressionKey, float>(ExpressionKey.Comparer);
            _composer = new VrmExpressionComposer(Write);
            _blender = new VrmEmotionBlender(_composer);
        }

        [Test]
        public void EmotionArrivesGraduallyRatherThanSnapping()
        {
            _blender.SetEmotion(AvatarEmotion.Happy);

            Step(0.05f);
            float early = Weight(ExpressionKey.Happy);
            Assert.Greater(early, 0f, "The blend must have started.");
            Assert.Less(early, 0.4f, "An eased blend leans in slowly at first.");
            Assert.IsTrue(_blender.IsBlending);

            Settle();
            Assert.AreEqual(0.8f, Weight(ExpressionKey.Happy), 0.001f);
            Assert.IsFalse(_blender.IsBlending);
        }

        [Test]
        public void NoAccentIsEverDrivenToFullWeight()
        {
            AvatarEmotion[] emotions =
            {
                AvatarEmotion.Happy, AvatarEmotion.Sad, AvatarEmotion.Angry,
                AvatarEmotion.Surprised, AvatarEmotion.Confused,
                AvatarEmotion.Relaxed, AvatarEmotion.Shy,
                AvatarEmotion.Excited, AvatarEmotion.Sleepy
            };

            for (int i = 0; i < emotions.Length; i++)
            {
                VrmEmotionPalette palette = VrmEmotionPalette.Resolve(emotions[i]);
                Assert.Greater(
                    palette.Accents.Length,
                    0,
                    emotions[i] + " has no face at all.");

                for (int a = 0; a < palette.Accents.Length; a++)
                {
                    Assert.Less(
                        palette.Accents[a].Weight,
                        1f,
                        emotions[i] + "/" + palette.Accents[a].Key +
                        " is pinned at full weight and will read as a mask.");
                    Assert.Greater(
                        palette.Accents[a].Weight,
                        0f,
                        emotions[i] + "/" + palette.Accents[a].Key + " contributes nothing.");
                }
            }
        }

        [Test]
        public void EveryDominantAccentSitsInTheIntendedBand()
        {
            AvatarEmotion[] emotions =
            {
                AvatarEmotion.Happy, AvatarEmotion.Sad, AvatarEmotion.Angry,
                AvatarEmotion.Surprised, AvatarEmotion.Relaxed,
                AvatarEmotion.Excited, AvatarEmotion.Sleepy
            };

            for (int i = 0; i < emotions.Length; i++)
            {
                VrmEmotionPalette palette = VrmEmotionPalette.Resolve(emotions[i]);
                float strongest = 0f;
                for (int a = 0; a < palette.Accents.Length; a++)
                {
                    if (palette.Accents[a].Weight > strongest)
                        strongest = palette.Accents[a].Weight;
                }

                Assert.That(
                    strongest,
                    Is.InRange(0.7f, 0.8f),
                    emotions[i] + " leads with " + strongest +
                    ", outside the 0.7-0.8 band.");
            }
        }

        [Test]
        public void SwitchingMidBlendGrowsOutOfTheFaceAlreadyOnScreen()
        {
            _blender.SetEmotion(AvatarEmotion.Happy);
            Settle();
            Assert.AreEqual(0.8f, Weight(ExpressionKey.Happy), 0.001f);

            // Angry shares no dominant key with happy, so happy has to walk down
            // from 0.8 rather than vanish on the switch.
            _blender.SetEmotion(AvatarEmotion.Angry);
            Step(0.02f);
            float happyJustAfter = Weight(ExpressionKey.Happy);
            Assert.Greater(
                happyJustAfter,
                0.6f,
                "The previous emotion was dropped instead of faded.");
            Assert.Less(happyJustAfter, 0.8f, "It should already be receding.");

            Settle();
            Assert.AreEqual(0.8f, Weight(ExpressionKey.Angry), 0.001f);
            Assert.AreEqual(
                0f,
                Weight(ExpressionKey.Happy),
                0.001f,
                "The outgoing emotion must reach zero.");
        }

        [Test]
        public void FadedOutKeysAreReleasedSoLowerLayersGetThemBack()
        {
            _blender.SetEmotion(AvatarEmotion.Sleepy);
            Settle();
            Assert.Greater(Weight(ExpressionKey.Blink), 0.5f);

            _blender.SetEmotion(AvatarEmotion.Neutral);
            Settle();

            // A blink reflex claiming the key must win outright, which only works
            // if the emotion layer let go of it rather than holding it at zero.
            _composer.Set(VrmExpressionLayer.Blink, ExpressionKey.Blink, 0.42f);
            _composer.Apply();
            Assert.AreEqual(0.42f, Weight(ExpressionKey.Blink), 0.001f);
        }

        [Test]
        public void EmotionLetsGoOnItsOwnOnceTheLeaseRunsOut()
        {
            _blender.SetEmotion(AvatarEmotion.Surprised);
            Settle();
            Assert.AreEqual(AvatarEmotion.Surprised, _blender.Emotion);

            float hold = VrmEmotionPalette.Resolve(AvatarEmotion.Surprised).HoldSeconds;
            Step(hold * 0.5f);
            Assert.AreEqual(
                AvatarEmotion.Surprised,
                _blender.Emotion,
                "It must not bail out halfway through its lease.");

            Step(hold);
            Assert.AreEqual(AvatarEmotion.Neutral, _blender.Emotion);
            Settle();
            Assert.AreEqual(0f, Weight(ExpressionKey.Surprised), 0.001f);
        }

        [Test]
        public void RepeatingTheSameEmotionRenewsTheLeaseWithoutRestartingTheBlend()
        {
            _blender.SetEmotion(AvatarEmotion.Happy);
            Settle();

            float hold = VrmEmotionPalette.Resolve(AvatarEmotion.Happy).HoldSeconds;
            Step(hold * 0.7f);

            _blender.SetEmotion(AvatarEmotion.Happy);
            Assert.IsFalse(
                _blender.IsBlending,
                "Re-asserting the current emotion must not re-ramp the face.");
            Assert.AreEqual(0.8f, Weight(ExpressionKey.Happy), 0.001f);

            // Without the renewal the lease would have expired by now.
            Step(hold * 0.7f);
            Assert.AreEqual(
                AvatarEmotion.Happy,
                _blender.Emotion,
                "The lease should have been renewed.");
        }

        [Test]
        public void SpeechStillOutranksAnEmotionalMouthShape()
        {
            _blender.SetEmotion(AvatarEmotion.Surprised);
            Settle();
            Assert.AreEqual(0.3f, Weight(ExpressionKey.Aa), 0.001f);

            _composer.Set(VrmExpressionLayer.Viseme, ExpressionKey.Aa, 1f);
            _composer.Apply();
            Assert.AreEqual(
                1f,
                Weight(ExpressionKey.Aa),
                0.001f,
                "A viseme must take the mouth whole, not stack on the emotion.");

            _composer.ClearLayer(VrmExpressionLayer.Viseme);
            _composer.Apply();
            Assert.AreEqual(
                0.3f,
                Weight(ExpressionKey.Aa),
                0.001f,
                "When speech stops the emotional mouth should return.");
        }

        [Test]
        public void SleepyLidSurvivesABlinkPassingOverIt()
        {
            _blender.SetEmotion(AvatarEmotion.Sleepy);
            Settle();
            float lid = Weight(ExpressionKey.Blink);

            _composer.Set(VrmExpressionLayer.Blink, ExpressionKey.Blink, 1f);
            _composer.Apply();
            Assert.AreEqual(1f, Weight(ExpressionKey.Blink), 0.001f);

            _composer.ClearLayer(VrmExpressionLayer.Blink);
            _composer.Apply();
            Assert.AreEqual(
                lid,
                Weight(ExpressionKey.Blink),
                0.001f,
                "The heavy lid must come back after the blink.");
        }

        [Test]
        public void LooseNamesResolveAndUnknownOnesAreRejected()
        {
            AvatarEmotion emotion;

            Assert.IsTrue(VrmEmotionPalette.TryParse("Happy", out emotion));
            Assert.AreEqual(AvatarEmotion.Happy, emotion);
            Assert.IsTrue(VrmEmotionPalette.TryParse("  smile ", out emotion));
            Assert.AreEqual(AvatarEmotion.Happy, emotion);
            Assert.IsTrue(VrmEmotionPalette.TryParse("TIRED", out emotion));
            Assert.AreEqual(AvatarEmotion.Sleepy, emotion);

            Assert.IsFalse(VrmEmotionPalette.TryParse("hopeful", out emotion));
            Assert.IsFalse(VrmEmotionPalette.TryParse("", out emotion));
            Assert.IsFalse(VrmEmotionPalette.TryParse(null, out emotion));
        }

        /// <summary>Advances one frame at a time, as LateUpdate would.</summary>
        private void Step(float seconds)
        {
            const float frame = 1f / 60f;
            float remaining = seconds;
            while (remaining > 0f)
            {
                float delta = remaining < frame ? remaining : frame;
                _blender.Tick(delta);
                _composer.Apply();
                remaining -= delta;
            }
        }

        /// <summary>
        /// Runs exactly until the blend finishes and no further, so the emotion's
        /// hold is left intact for tests that measure it.
        /// </summary>
        private void Settle()
        {
            const float frame = 1f / 60f;
            int frames = 0;
            while (_blender.IsBlending && frames < 600)
            {
                _blender.Tick(frame);
                _composer.Apply();
                frames++;
            }

            Assert.Less(frames, 600, "The blend never settled.");
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
            _model[key] = weight;
        }
    }
}
