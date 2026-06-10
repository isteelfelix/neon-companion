using System;
using UnityEngine;

#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace NeonCompanion.Runtime.Platform
{
    /// <summary>
    /// Вспомогательный класс для запроса runtime permissions на Android.
    /// Использовать перед mic / file operations.
    /// </summary>
    public static class AndroidPermissionHelper
    {
        public static void RequestPermission(string permission, Action onGranted = null, Action onDenied = null)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (Permission.HasUserAuthorizedPermission(permission))
            {
                onGranted?.Invoke();
                return;
            }

            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += _ => onGranted?.Invoke();
            callbacks.PermissionDenied += _ => onDenied?.Invoke();
            callbacks.PermissionDeniedAndDontAskAgain += _ => onDenied?.Invoke();

            Permission.RequestUserPermission(permission, callbacks);
#else
            onGranted?.Invoke();
#endif
        }

        // Удобные константы
        public const string RECORD_AUDIO = "android.permission.RECORD_AUDIO";
        public const string READ_EXTERNAL_STORAGE = "android.permission.READ_EXTERNAL_STORAGE";
        public const string WRITE_EXTERNAL_STORAGE = "android.permission.WRITE_EXTERNAL_STORAGE";
    }

    public static class AndroidHapticFeedback
    {
        public static void Pulse()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                AndroidJavaObject activity;
                using (AndroidJavaClass unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                    activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");

                activity.Call("runOnUiThread", new AndroidJavaRunnable(() =>
                {
                    try
                    {
                        using (AndroidJavaObject window = activity.Call<AndroidJavaObject>("getWindow"))
                        using (AndroidJavaObject decorView = window.Call<AndroidJavaObject>("getDecorView"))
                        {
                            decorView.Call<bool>("performHapticFeedback", 1);
                        }
                    }
                    finally
                    {
                        activity.Dispose();
                    }
                }));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[NeonCompanion] Android haptic feedback failed: " + ex.Message);
            }
#endif
        }
    }
}
