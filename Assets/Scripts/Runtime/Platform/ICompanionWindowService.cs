using System;
using System.Collections.Generic;
using NeonCompanion.Runtime.Data.Models;

namespace NeonCompanion.Runtime.Platform
{
    public static class CompanionDisplayStates
    {
        public const string Idle = "idle";
        public const string Listening = "listening";
        public const string Thinking = "thinking";
        public const string Speaking = "speaking";
        public const string Stop = "stop";
    }

    public static class CompanionDockStates
    {
        public const string Docked = "docked";
        public const string DetachedStarting = "detached-starting";
        public const string DetachedReady = "detached-ready";
        public const string DetachedHidden = "detached-hidden";
        public const string Failed = "failed";

        public static string Normalize(string state)
        {
            switch (state)
            {
                case Docked:
                case DetachedStarting:
                case DetachedReady:
                case DetachedHidden:
                case Failed:
                    return state;
                default:
                    return Docked;
            }
        }
    }

    public enum CompanionDockEvent
    {
        Detach,
        Started,
        Hide,
        Show,
        Closed,
        Fail,
        ReturnToColumn
    }

    public sealed class CompanionDockStateMachine
    {
        public CompanionDockStateMachine(string persistedState)
        {
            State = CompanionDockStates.Normalize(persistedState);
        }

        public string State { get; private set; }

        public bool IsDetached
        {
            get
            {
                return State == CompanionDockStates.DetachedStarting ||
                    State == CompanionDockStates.DetachedReady ||
                    State == CompanionDockStates.DetachedHidden;
            }
        }

        public bool NeedsLaunch
        {
            get { return State == CompanionDockStates.DetachedStarting; }
        }

        public string Apply(CompanionDockEvent dockEvent)
        {
            if (dockEvent == CompanionDockEvent.ReturnToColumn)
                return Set(CompanionDockStates.Docked);
            if (dockEvent == CompanionDockEvent.Fail)
                return Set(CompanionDockStates.Failed);

            switch (State)
            {
                case CompanionDockStates.Docked:
                case CompanionDockStates.Failed:
                    if (dockEvent == CompanionDockEvent.Detach ||
                        dockEvent == CompanionDockEvent.Show)
                        return Set(CompanionDockStates.DetachedStarting);
                    break;
                case CompanionDockStates.DetachedStarting:
                    if (dockEvent == CompanionDockEvent.Started)
                        return Set(CompanionDockStates.DetachedReady);
                    if (dockEvent == CompanionDockEvent.Closed ||
                        dockEvent == CompanionDockEvent.Hide)
                        return Set(CompanionDockStates.DetachedHidden);
                    break;
                case CompanionDockStates.DetachedReady:
                    if (dockEvent == CompanionDockEvent.Hide ||
                        dockEvent == CompanionDockEvent.Closed)
                        return Set(CompanionDockStates.DetachedHidden);
                    break;
                case CompanionDockStates.DetachedHidden:
                    if (dockEvent == CompanionDockEvent.Show ||
                        dockEvent == CompanionDockEvent.Detach)
                        return Set(CompanionDockStates.DetachedStarting);
                    break;
            }
            return State;
        }

        private string Set(string state)
        {
            State = state;
            return State;
        }
    }

    [Serializable]
    public sealed class CompanionDisplaySnapshot
    {
        public string avatarId;
        public string displayName;
        public string avatarType;
        public string imagePath;
        public string imagePngBase64;
        public string modelPath;
        public string motionPackManifestPath;
        public List<SpriteSheetAnimation> animationClips = new List<SpriteSheetAnimation>();
        public Avatar3DStateClipMapping stateClipMapping;
        public float avatarScale = 1f;
        public float avatarOffsetX;
        public float avatarOffsetY;

        public static CompanionDisplaySnapshot FromProfile(AvatarProfile profile, string fallbackId, string displayName)
        {
            AvatarProfile source = profile ?? new AvatarProfile
            {
                id = fallbackId,
                name = displayName,
                avatarType = AvatarProfileTypes.SpriteSheet,
                isBuiltIn = true
            };

            return new CompanionDisplaySnapshot
            {
                avatarId = string.IsNullOrWhiteSpace(source.id) ? fallbackId : source.id,
                displayName = string.IsNullOrWhiteSpace(source.name) ? displayName : source.name,
                avatarType = string.IsNullOrWhiteSpace(source.avatarType)
                    ? AvatarProfileTypes.SpriteSheet
                    : source.avatarType,
                imagePath = source.imagePath,
                modelPath = source.modelPath,
                motionPackManifestPath = source.motionPackManifestPath,
                animationClips = source.animationClips != null
                    ? new List<SpriteSheetAnimation>(source.animationClips)
                    : new List<SpriteSheetAnimation>(),
                stateClipMapping = source.stateClipMapping,
                avatarScale = source.avatarScale <= 0f ? 1f : source.avatarScale,
                avatarOffsetX = source.avatarOffsetX,
                avatarOffsetY = source.avatarOffsetY
            };
        }
    }

    [Serializable]
    public sealed class CompanionWindowPreferences
    {
        public bool visible = true;
        public bool pinned = true;
        public bool clickThrough;
        public int monitorIndex;
        public float scale = 1f;
        public string language = "en";
        public int positionX = int.MinValue;
        public int positionY = int.MinValue;
    }

    public enum CompanionWindowEventKind
    {
        Started,
        Closed,
        Failed,
        OpenAvatarSettings,
        ReturnToColumn,
        BoundsChanged,
        ClickThroughChanged,
        VisibilityChanged,
        PinnedChanged,
        ScaleChanged
    }

    public sealed class CompanionWindowEvent
    {
        public CompanionWindowEventKind Kind;
        public string Message;
        public int X;
        public int Y;
        public bool BoolValue;
        public float FloatValue;
    }

    public interface ICompanionWindowService : IDisposable
    {
        bool IsAvailable { get; }
        bool IsRunning { get; }
        IReadOnlyList<string> MonitorNames { get; }
        event Action<CompanionWindowEvent> EventReceived;

        void Launch(CompanionDisplaySnapshot snapshot, CompanionWindowPreferences preferences);
        void SetProfile(CompanionDisplaySnapshot snapshot);
        void SetState(string state);
        void StartVoicePlayback(string text);
        void UpdateVoicePlayback(float positionSecs, float durationSecs, bool isPlaying);
        void ClearVoicePlayback();
        void TriggerReaction(string reaction);
        void UpdatePreferences(CompanionWindowPreferences preferences);
        void Show();
        void Hide();
        void Stop();
        void Tick();
    }

    [Serializable]
    internal sealed class CompanionProcessMessage
    {
        public string type;
        public string text;
        public bool boolValue;
        public float floatValue;
        public float floatValue2;
        public int x;
        public int y;
        public CompanionDisplaySnapshot snapshot;
        public CompanionWindowPreferences preferences;
    }
}
