namespace NeonCompanion.Runtime.Avatar3D
{
    public enum AvatarScenePhase
    {
        Empty = 0,
        Loading = 1,
        Binding = 2,
        Mounted = 3,
        Failed = 4
    }

    /// <summary>
    /// Tracks the avatar scene lifecycle and invalidates in-flight loads.
    /// Loading a model is a long chain of awaits, and the scene can be torn
    /// down halfway through it. Every load stamps itself with a generation;
    /// anything that invalidates the scene bumps the generation, so a load
    /// that lost ownership can notice and clean up after itself instead of
    /// binding into a scene that no longer exists.
    /// </summary>
    internal sealed class AvatarSceneState
    {
        private int _generation;

        public AvatarScenePhase Phase { get; private set; }

        /// <summary>Driving expressions, gaze or animation is only safe once mounted.</summary>
        public bool CanMutate
        {
            get { return Phase == AvatarScenePhase.Mounted; }
        }

        /// <summary>Invalidates any in-flight load and returns the new load's stamp.</summary>
        public int BeginLoad()
        {
            _generation++;
            Phase = AvatarScenePhase.Loading;
            return _generation;
        }

        public void BeginBinding()
        {
            Phase = AvatarScenePhase.Binding;
        }

        public void MarkMounted()
        {
            Phase = AvatarScenePhase.Mounted;
        }

        public void MarkFailed()
        {
            Phase = AvatarScenePhase.Failed;
        }

        /// <summary>Invalidates any in-flight load and returns to the empty scene.</summary>
        public void Reset()
        {
            _generation++;
            Phase = AvatarScenePhase.Empty;
        }

        public bool IsCurrent(int generation)
        {
            return generation == _generation;
        }
    }
}
