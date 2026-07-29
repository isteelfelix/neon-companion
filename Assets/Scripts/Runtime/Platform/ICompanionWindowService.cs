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
        PinnedChanged
    }

    public sealed class CompanionWindowEvent
    {
        public CompanionWindowEventKind Kind;
        public string Message;
        public int X;
        public int Y;
        public bool BoolValue;
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
        void ClearVoicePlayback();
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
        public int x;
        public int y;
        public CompanionDisplaySnapshot snapshot;
        public CompanionWindowPreferences preferences;
    }
}
