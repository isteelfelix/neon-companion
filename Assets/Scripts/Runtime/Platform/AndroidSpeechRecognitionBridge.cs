using System;
using UnityEngine;

#if UNITY_ANDROID && !UNITY_EDITOR
namespace NeonCompanion.Runtime.Platform
{
    /// <summary>
    /// Bridge MonoBehaviour for receiving speech recognition results via UnitySendMessage from Java.
    /// Follows the exact pattern of AndroidFilePickerBridge.
    /// </summary>
    public sealed class AndroidSpeechRecognitionBridge : MonoBehaviour
    {
        private static AndroidSpeechRecognitionBridge _instance;
        private Action<string> _resultHandler;

        public static AndroidSpeechRecognitionBridge GetOrCreate(Action<string> resultHandler)
        {
            if (_instance == null)
            {
                var go = new GameObject("NeonAndroidSpeechBridge");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<AndroidSpeechRecognitionBridge>();
            }

            _instance._resultHandler = resultHandler;
            return _instance;
        }

        public void OnAndroidSpeechResult(string text)
        {
            _resultHandler?.Invoke(text != null ? text.Trim() : null);
        }
    }
}
#endif
