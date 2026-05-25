using System;

namespace NeonCompanion.Runtime.Voice
{
    public interface IVoiceService
    {
        bool IsRecording { get; }
        bool IsSpeaking { get; }
        bool IsAvailable { get; }

        event Action<string> OnSpeechRecognized;
        event Action OnPlaybackComplete;

        void StartRecording();
        byte[] StopRecording();
        void Speak(string text);
        void StopSpeaking();
    }
}
