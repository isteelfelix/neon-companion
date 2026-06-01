using System;
using UnityEngine;

#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace NeonCompanion.Runtime.Platform
{
    /// <summary>
    /// Helper for listening to soft keyboard visibility on Android.
    /// Can be used to adjust UI insets dynamically (e.g. composer padding).
    /// </summary>
    public static class AndroidKeyboardInset
    {
        public static bool IsKeyboardVisible { get; private set; }
        public static float KeyboardHeight { get; private set; }

#pragma warning disable 0067
        public static event Action<bool, float> OnKeyboardVisibilityChanged;
#pragma warning restore 0067

        /// <summary>
        /// Call this periodically or from a MonoBehaviour Update on Android.
        /// </summary>
        public static void PollKeyboardState()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                using var view = activity.Call<AndroidJavaObject>("getWindow").Call<AndroidJavaObject>("getDecorView");
                using var rect = new AndroidJavaObject("android.graphics.Rect");

                view.Call("getWindowVisibleDisplayFrame", rect);

                int screenHeight = view.Call<int>("getHeight");
                int visibleBottom = rect.Get<int>("bottom");

                float keyboardHeight = screenHeight - visibleBottom;

                bool wasVisible = IsKeyboardVisible;
                IsKeyboardVisible = keyboardHeight > 100; // threshold
                KeyboardHeight = Mathf.Max(0, keyboardHeight);

                if (wasVisible != IsKeyboardVisible)
                {
                    OnKeyboardVisibilityChanged?.Invoke(IsKeyboardVisible, KeyboardHeight);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NeonCompanion] Keyboard inset poll failed: {ex.Message}");
            }
#endif
        }
    }
}
