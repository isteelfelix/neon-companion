namespace NeonCompanion.Runtime.Core
{
    public enum ContextWindowSource
    {
        Unknown,
        Runtime,
        Discovery,
        Registry,
        Manual
    }

    public sealed class ContextWindowResolution
    {
        public int EffectiveContextWindow { get; set; }
        public int KnownLimit { get; set; }
        public int ManualLimit { get; set; }
        public ContextWindowSource Source { get; set; }
        public ContextWindowSource KnownSource { get; set; }

        public bool IsKnown
        {
            get { return EffectiveContextWindow > 0; }
        }

        public bool IsManual
        {
            get { return ManualLimit > 0; }
        }
    }
}
