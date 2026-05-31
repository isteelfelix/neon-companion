using UnityEngine;

namespace NeonCompanion.Runtime.Platform.iOS
{
    /// <summary>
    /// Bridge for iOS native speech (TTS + Recognition).
    /// Called from native NeonSpeech.mm via UnitySendMessage.
    /// 
    /// See docs/17_iOS_Platform_Architecture.md (IOS-05).
    /// </summary>
    public class iOSSpeechBridge : MonoBehaviour
    {
        private static iOSSpeechBridge _instance;

        public static iOSSpeechBridge GetOrCreate(string gameObjectName)
        {
#if UNITY_IOS && !UNITY_EDITOR
            if (_instance == null)
            {
                GameObject go = GameObject.Find(gameObjectName) ?? new GameObject(gameObjectName);
                _instance = go.AddComponent<iOSSpeechBridge>();
                DontDestroyOnLoad(go);
            }
            return _instance;
#else
            return null;
#endif
        }

        // Called from native code
        public void OnSpeechRecognized(string text)
        {
            // Forward to WebSpeechBridge or voice service
            Debug.Log($"[iOSSpeechBridge] Recognized: {text}");
        }

        public void OnPlaybackComplete()
        {
            Debug.Log("[iOSSpeechBridge] Playback complete");
        }
    }
}