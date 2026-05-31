using UnityEngine;

namespace NeonCompanion.Runtime.Platform.iOS
{
    /// <summary>
    /// iOS-specific keyboard inset handling.
    /// 
    /// On iOS the system keyboard handling is stronger than Android.
    /// This can extend or replace AndroidKeyboardInset for iOS builds.
    /// 
    /// See docs/17_iOS_Platform_Architecture.md (IOS-06) and references/android-keyboard-inset-polling.md.
    /// 
    /// Placeholder for now — can poll UIKeyboardDidShowNotification via native later.
    /// </summary>
    public static class iOSKeyboardInset
    {
        public static bool IsKeyboardVisible { get; private set; }

        public static void StartPolling()
        {
#if UNITY_IOS && !UNITY_EDITOR
            // TODO: register for UIKeyboard notifications via native plugin
            Debug.Log("[NeonCompanion][iOS] Keyboard inset polling started (placeholder).");
#else
            // No-op on other platforms
#endif
        }

        public static void StopPolling()
        {
#if UNITY_IOS && !UNITY_EDITOR
            Debug.Log("[NeonCompanion][iOS] Keyboard inset polling stopped.");
#endif
        }
    }
}