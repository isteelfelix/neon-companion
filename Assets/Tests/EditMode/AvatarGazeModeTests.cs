using NeonCompanion.Runtime.Avatar3D;
using NUnit.Framework;

namespace NeonCompanion.Tests
{
    public sealed class AvatarGazeModeTests
    {
        [Test]
        public void LooseNamesResolveToTheRightMode()
        {
            AvatarGazeMode mode;

            Assert.IsTrue(AvatarGazeModes.TryParse("none", out mode));
            Assert.AreEqual(AvatarGazeMode.None, mode);
            Assert.IsTrue(AvatarGazeModes.TryParse("OFF", out mode));
            Assert.AreEqual(AvatarGazeMode.None, mode);

            Assert.IsTrue(AvatarGazeModes.TryParse(" Camera ", out mode));
            Assert.AreEqual(AvatarGazeMode.Camera, mode);
            Assert.IsTrue(AvatarGazeModes.TryParse("viewer", out mode));
            Assert.AreEqual(AvatarGazeMode.Camera, mode);

            Assert.IsTrue(AvatarGazeModes.TryParse("mouse", out mode));
            Assert.AreEqual(AvatarGazeMode.Cursor, mode);
        }

        [Test]
        public void UnknownAndEmptyNamesAreRejected()
        {
            AvatarGazeMode mode;
            Assert.IsFalse(AvatarGazeModes.TryParse("elsewhere", out mode));
            Assert.IsFalse(AvatarGazeModes.TryParse("", out mode));
            Assert.IsFalse(AvatarGazeModes.TryParse(null, out mode));
        }

        [Test]
        public void ServiceDefaultsToCursorAndRemembersTheChosenModeBeforeLoad()
        {
            var service = new Avatar3DService();
            Assert.AreEqual(
                AvatarGazeMode.Cursor,
                service.GazeMode,
                "Cursor is the historical default and must stay so.");

            service.SetGazeMode(AvatarGazeMode.None);
            Assert.AreEqual(AvatarGazeMode.None, service.GazeMode);

            service.SetGazeMode(AvatarGazeMode.Camera);
            Assert.AreEqual(AvatarGazeMode.Camera, service.GazeMode);
        }
    }
}
