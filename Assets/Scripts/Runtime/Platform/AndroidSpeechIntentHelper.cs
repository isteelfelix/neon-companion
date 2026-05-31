using System;
using UnityEngine;

#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace NeonCompanion.Runtime.Platform
{
    /// <summary>
    /// Launches Android speech recognition via custom activity (RecognizerIntent).
    /// Result delivered via UnitySendMessage to OnAndroidSpeechResult on the provided GameObject.
    /// Follows the same proven pattern as NeonFilePickerActivity + AndroidFilePickerBridge.
    /// </summary>
    public static class AndroidSpeechIntentHelper
    {
        public static void StartSpeechRecognition(string gameObjectName)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                AndroidPermissionHelper.RequestPermission(AndroidPermissionHelper.RECORD_AUDIO, () =>
                {
                    var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                    var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");

                    var speechClass = new AndroidJavaClass("com.neoncompanion.speech.NeonSpeechRecognitionActivity");
                    speechClass.CallStatic("startRecognition", activity, gameObjectName);
                });
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NeonCompanion] Android speech recognition launch failed: {ex.Message}");
                // Notify immediately so UI doesn't hang in recording state
                var go = GameObject.Find(gameObjectName);
                go?.SendMessage("OnAndroidSpeechResult", "", SendMessageOptions.DontRequireReceiver);
            }
#else
            Debug.LogWarning("AndroidSpeechIntentHelper only works on real Android device");
#endif
        }
    }
}
