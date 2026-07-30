namespace NeonCompanion.Runtime.Avatar3D
{
    /// <summary>
    /// The parts of the companion that answer to a touch. Only bones that are
    /// actually on screen in the bust-up portrait — the feet are never framed,
    /// so they carry no colliders and no region.
    /// </summary>
    public enum AvatarTouchRegion
    {
        Head = 0,
        Hand = 1,
        Forearm = 2
    }

    /// <summary>How a touch on each region reads as an emotion.</summary>
    public static class AvatarTouchReactions
    {
        /// <summary>
        /// The emotion a touch on <paramref name="region"/> triggers, as a name
        /// <c>SetEmotion</c> understands. A pat on the head pleases; a poke at the
        /// hand flusters; a touch on the arm catches attention.
        /// </summary>
        public static string ForRegion(AvatarTouchRegion region)
        {
            switch (region)
            {
                case AvatarTouchRegion.Head:
                    return "happy";
                case AvatarTouchRegion.Hand:
                    return "shy";
                case AvatarTouchRegion.Forearm:
                    return "surprised";
                default:
                    return "surprised";
            }
        }
    }
}
