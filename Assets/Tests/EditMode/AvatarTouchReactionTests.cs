using System;
using NeonCompanion.Runtime.Avatar3D;
using NUnit.Framework;

namespace NeonCompanion.Tests
{
    public sealed class AvatarTouchReactionTests
    {
        [Test]
        public void EachRegionMapsToItsReaction()
        {
            Assert.AreEqual("happy", AvatarTouchReactions.ForRegion(AvatarTouchRegion.Head));
            Assert.AreEqual("shy", AvatarTouchReactions.ForRegion(AvatarTouchRegion.Hand));
            Assert.AreEqual(
                "surprised",
                AvatarTouchReactions.ForRegion(AvatarTouchRegion.Forearm));
        }

        [Test]
        public void EveryReactionIsAnEmotionTheBlenderCanPlay()
        {
            foreach (AvatarTouchRegion region in
                Enum.GetValues(typeof(AvatarTouchRegion)))
            {
                string reaction = AvatarTouchReactions.ForRegion(region);
                AvatarEmotion emotion;
                Assert.IsTrue(
                    VrmEmotionPalette.TryParse(reaction, out emotion),
                    region + " maps to \"" + reaction +
                    "\", which SetEmotion would reject.");
            }
        }
    }
}
