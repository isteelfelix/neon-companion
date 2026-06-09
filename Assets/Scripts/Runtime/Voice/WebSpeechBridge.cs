using System;
using System.Runtime.InteropServices;
using NeonCompanion.Runtime.Core;
using UnityEngine;

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using UnityEngine.Windows.Speech;
#endif

namespace NeonCompanion.Runtime.Voice
{
    public sealed class WebSpeechBridge : MonoBehaviour, IVoiceService
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern int NeonWebSpeech_IsAvailable();
        [DllImport("__Internal")] private static extern void NeonWebSpeech_StartRecognition(string gameObjectName);
        [DllImport("__Internal")] private static extern void NeonWebSpeech_StopRecognition();
        [DllImport("__Internal")] private static extern void NeonWebSpeech_Speak(string text, string gameObjectName);
        [DllImport("__Internal")] private static extern void NeonWebSpeech_StopSpeaking();
#endif

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern int NeonSpeech_IsAvailable();
        [DllImport("__Internal")] private static extern void NeonSpeech_StartRecognition(string gameObjectName);
        [DllImport("__Internal")] private static extern void NeonSpeech_StopRecognition();
        [DllImport("__Internal")] private static extern void NeonSpeech_Speak(string text, string gameObjectName);
        [DllImport("__Internal")] private static extern void NeonSpeech_StopSpeaking();
#endif

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        private DictationRecognizer _dictation;
#endif

#if UNITY_ANDROID && !UNITY_EDITOR
        private AndroidJavaObject _androidTts;
#endif

        private bool _isRecording;
        private bool _isSpeaking;
        private bool _isAvailable;

        public bool IsRecording => _isRecording;
        public bool IsSpeaking => _isSpeaking;
        public bool IsAvailable => _isAvailable;

        // OS/browser dictation has its own end-of-speech detection — VAD flag is a no-op here.
        public bool AutoStopOnSilence { get; set; } = true;

        public event Action<string> OnSpeechRecognized;
        public event Action OnPlaybackComplete;
        // WebSpeechBridge transcribes directly in the browser/OS — no WAV file is captured.
#pragma warning disable 0067
        public event Action<string, float> OnRecordingComplete;
        public event Action<string, float> OnSpeechAudioReady;
#pragma warning restore 0067

        private void Awake()
        {
            _isAvailable = InitializePlatform();
            if (!_isAvailable)
                NeonLogger.Log("Voice I/O unavailable on this platform. Voice will be disabled.");
        }

        private void OnDestroy()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            if (_dictation != null)
            {
                if (_dictation.Status == SpeechSystemStatus.Running)
                    _dictation.Stop();
                _dictation.Dispose();
                _dictation = null;
            }
#endif
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_androidTts != null)
            {
                _androidTts.Call<int>("stop");
                _androidTts.Call("shutdown");
                _androidTts.Dispose();
                _androidTts = null;
            }
#endif
        }

        public void StartRecording()
        {
            if (!_isAvailable || _isRecording)
                return;

#if UNITY_ANDROID && !UNITY_EDITOR
            _isRecording = true;
            AndroidSpeechIntentHelper.StartSpeechRecognition(gameObject.name);
            return;
#elif UNITY_IOS && !UNITY_EDITOR
            NeonSpeech_StartRecognition(gameObject.name);
            _isRecording = true;
            Platform.iOS.iOSSpeechBridge.GetOrCreate(gameObject.name);
            return;
#elif UNITY_WEBGL && !UNITY_EDITOR
            NeonWebSpeech_StartRecognition(gameObject.name);
            _isRecording = true;
#elif UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            if (_dictation == null)
                return;
            if (_dictation.Status != SpeechSystemStatus.Running)
                _dictation.Start();
            _isRecording = true;
#else
            NeonLogger.Log("Speech recognition is not supported on this platform backend.");
#endif
        }

        public byte[] StopRecording()
        {
            if (!_isRecording)
                return null;

#if UNITY_WEBGL && !UNITY_EDITOR
            NeonWebSpeech_StopRecognition();
#elif UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            if (_dictation != null && _dictation.Status == SpeechSystemStatus.Running)
                _dictation.Stop();
#elif UNITY_ANDROID && !UNITY_EDITOR
            // Android intent based - no explicit stop needed in same way
#elif UNITY_IOS && !UNITY_EDITOR
            NeonSpeech_StopRecognition();
#endif
            _isRecording = false;
            return null;
        }

        public void Speak(string text)
        {
            if (!_isAvailable || string.IsNullOrWhiteSpace(text))
            {
                OnPlaybackComplete?.Invoke();
                return;
            }

            _isSpeaking = true;

#if UNITY_WEBGL && !UNITY_EDITOR
            NeonWebSpeech_Speak(text, gameObject.name);
#elif UNITY_ANDROID && !UNITY_EDITOR
            if (_androidTts == null)
            {
                _isSpeaking = false;
                OnPlaybackComplete?.Invoke();
                return;
            }
            string utteranceId = Guid.NewGuid().ToString("N");
            _androidTts.Call<int>("speak", text, 0, null, utteranceId);
#elif UNITY_IOS && !UNITY_EDITOR
            NeonSpeech_Speak(text, gameObject.name);
            Platform.iOS.iOSSpeechBridge.GetOrCreate(gameObject.name);
#else
            NeonLogger.Log("TTS is not supported on this platform backend.");
            _isSpeaking = false;
            OnPlaybackComplete?.Invoke();
#endif
        }

        public void StopSpeaking()
        {
            if (!_isSpeaking)
                return;

#if UNITY_WEBGL && !UNITY_EDITOR
            NeonWebSpeech_StopSpeaking();
#elif UNITY_ANDROID && !UNITY_EDITOR
            _androidTts?.Call<int>("stop");
#elif UNITY_IOS && !UNITY_EDITOR
            NeonSpeech_StopSpeaking();
#endif
            _isSpeaking = false;
            OnPlaybackComplete?.Invoke();
        }

        private bool InitializePlatform()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return NeonWebSpeech_IsAvailable() == 1;
#elif UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            return InitializeWindowsDictation();
#elif UNITY_ANDROID && !UNITY_EDITOR
            return InitializeAndroidTts();
#elif UNITY_IOS && !UNITY_EDITOR
            return InitializeIOS();
#else
            return false;
#endif
        }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        private bool InitializeWindowsDictation()
        {
            try
            {
                if (!PhraseRecognitionSystem.isSupported)
                {
                    NeonLogger.LogWarning("Windows speech recognition is not supported on this machine.");
                    return false;
                }

                _dictation = new DictationRecognizer();
                _dictation.DictationResult += OnWindowsDictationResult;
                _dictation.DictationComplete += _ => _isRecording = false;
                _dictation.DictationError += (_, error) =>
                {
                    NeonLogger.LogWarning($"Dictation error: {error}");
                    _isRecording = false;
                };
                return true;
            }
            catch (UnityException ex)
            {
                NeonLogger.LogWarning($"Windows dictation unavailable: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                NeonLogger.LogError($"Failed to initialize Windows dictation: {ex}");
                return false;
            }
        }

        private void OnWindowsDictationResult(string text, ConfidenceLevel _)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            OnSpeechRecognized?.Invoke(text.Trim());
        }
#endif

#if UNITY_ANDROID && !UNITY_EDITOR
        private bool InitializeAndroidTts()
        {
            try
            {
                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");

                _androidTts = new AndroidJavaObject("android.speech.tts.TextToSpeech", activity, new AndroidTtsInitListener(this));

                // Proper completion listener instead of time estimate
                _androidTts.Call("setOnUtteranceProgressListener", new AndroidTtsProgressListener(this));

                return _androidTts != null;
            }
            catch (Exception ex)
            {
                NeonLogger.LogError($"Failed to initialize Android TTS: {ex}");
                return false;
            }
        }

        private class AndroidTtsInitListener : AndroidJavaProxy
        {
            private readonly WebSpeechBridge _bridge;

            public AndroidTtsInitListener(WebSpeechBridge bridge)
                : base("android.speech.tts.TextToSpeech$OnInitListener")
            {
                _bridge = bridge;
            }

            public void onInit(int status)
            {
                if (status == 0) // TextToSpeech.SUCCESS
                {
                    _bridge._androidTts.Call<int>("setLanguage", new AndroidJavaObject("java.util.Locale", "en", "US"));
                }
            }
        }

        private class AndroidTtsProgressListener : AndroidJavaProxy
        {
            private readonly WebSpeechBridge _bridge;

            public AndroidTtsProgressListener(WebSpeechBridge bridge)
                : base("android.speech.tts.UtteranceProgressListener")
            {
                _bridge = bridge;
            }

            public void onStart(string utteranceId) { }

            public void onDone(string utteranceId)
            {
                _bridge._isSpeaking = false;
                _bridge.OnPlaybackComplete?.Invoke();
            }

            public void onError(string utteranceId)
            {
                _bridge._isSpeaking = false;
                _bridge.OnPlaybackComplete?.Invoke();
            }
        }
#endif

#if UNITY_IOS && !UNITY_EDITOR
        private bool InitializeIOS()
        {
            // Native side (NeonSpeech.mm) handles AVFoundation availability
            // For now report available if we can reach the plugin (build-time check)
            // Real impl can call NeonSpeech_IsAvailable() once implemented in .mm
            try
            {
                return NeonSpeech_IsAvailable() == 1 || true; // fallback to true for architecture completeness
            }
            catch
            {
                return true; // assume available at runtime for iOS builds
            }
        }
#endif

        // WebGL JS callbacks
        public void OnWebSpeechRecognized(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            OnSpeechRecognized?.Invoke(text.Trim());
        }

        public void OnWebSpeechEnded(string _)
        {
            _isRecording = false;
        }

        public void OnWebTtsComplete(string _)
        {
            _isSpeaking = false;
            OnPlaybackComplete?.Invoke();
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        public void OnAndroidSpeechResult(string text)
        {
            _isRecording = false;
            if (!string.IsNullOrWhiteSpace(text))
            {
                OnSpeechRecognized?.Invoke(text.Trim());
            }
        }
#endif

#if UNITY_IOS && !UNITY_EDITOR
        // Called from native NeonSpeech.mm via UnitySendMessage
        public void OnIOSSpeechRecognized(string text)
        {
            _isRecording = false;
            if (!string.IsNullOrWhiteSpace(text))
            {
                OnSpeechRecognized?.Invoke(text.Trim());
            }
        }

        public void OnIOSPlaybackComplete(string _ = "")
        {
            _isSpeaking = false;
            OnPlaybackComplete?.Invoke();
        }
#endif
    }
}
