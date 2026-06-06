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
        /// <summary>
        /// Fires after recording stops but before (or during) transcription.
        /// wavPath — local file path to the captured WAV (empty string when no file is saved, e.g. WebSpeechBridge).
        /// durationSecs — recorded audio length in seconds.
        /// </summary>
        event Action<string, float> OnRecordingComplete;

        void StartRecording();
        byte[] StopRecording();
        void Speak(string text);
        void StopSpeaking();
    }
}
