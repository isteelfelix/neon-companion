using System;

namespace NeonCompanion.Runtime.Voice
{
    public interface IVoiceService
    {
        bool IsRecording { get; }
        bool IsSpeaking { get; }
        bool IsAvailable { get; }

        /// <summary>
        /// When true, recording stops automatically after a silence window (VAD).
        /// Set false for hold-to-record, where the user controls start/stop explicitly.
        /// Services without VAD (e.g. WebSpeechBridge) may ignore this.
        /// </summary>
        bool AutoStopOnSilence { get; set; }

        event Action<string> OnSpeechRecognized;
        event Action OnPlaybackComplete;
        /// <summary>
        /// Fires after recording stops but before (or during) transcription.
        /// wavPath — local file path to the captured WAV (empty string when no file is saved, e.g. WebSpeechBridge).
        /// durationSecs — recorded audio length in seconds.
        /// </summary>
        event Action<string, float> OnRecordingComplete;
        /// <summary>
        /// Fires when a TTS clip has been synthesized and saved to disk (path, durationSecs), so the UI
        /// can attach it to the assistant message bubble for cached replay. Empty for backends that
        /// can't produce a file (e.g. WebSpeechBridge / OS TTS).
        /// </summary>
        event Action<string, float> OnSpeechAudioReady;

        void StartRecording();
        byte[] StopRecording();
        void Speak(string text);
        void StopSpeaking();
    }
}
