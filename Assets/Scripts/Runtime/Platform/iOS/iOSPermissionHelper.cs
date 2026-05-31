using System;
using UnityEngine;

#if UNITY_IOS && !UNITY_EDITOR
using UnityEngine.iOS; // for some iOS specifics if needed
#endif

namespace NeonCompanion.Runtime.Platform.iOS
{
    /// <summary>
    /// Вспомогательный класс для runtime permissions на iOS.
    /// 
    /// На iOS permissions в основном декларативные (через Info.plist):
    /// - NSMicrophoneUsageDescription
    /// - NSPhotoLibraryUsageDescription
    /// - NSCameraUsageDescription (если понадобится)
    /// 
    /// Runtime запрос делается через UnityEngine.Android.Permission? Нет — Unity's Permission API
    /// работает кросс-платформенно для Microphone и Camera на iOS.
    /// 
    /// Для Photos на iOS 14+ рекомендуется PHPicker (уже в file picker).
    /// 
    /// См. docs/17_iOS_Platform_Architecture.md (IOS-03).
    /// </summary>
    public static class iOSPermissionHelper
    {
        public static bool HasPermission(string permission)
        {
#if UNITY_IOS && !UNITY_EDITOR
            // Unity's Permission API works for Microphone on iOS
            if (permission == Microphone)
            {
                return Permission.HasUserAuthorizedPermission(Permission.Microphone);
            }
            // For other (Photos etc.) — assume granted after Info.plist + first use
            return true;
#else
            return true;
#endif
        }

        public static void RequestPermission(string permission, Action onGranted = null, Action onDenied = null)
        {
#if UNITY_IOS && !UNITY_EDITOR
            if (permission == Microphone)
            {
                if (Permission.HasUserAuthorizedPermission(Permission.Microphone))
                {
                    onGranted?.Invoke();
                    return;
                }

                var callbacks = new PermissionCallbacks();
                callbacks.PermissionGranted += _ => onGranted?.Invoke();
                callbacks.PermissionDenied += _ => onDenied?.Invoke();
                callbacks.PermissionDeniedAndDontAskAgain += _ => onDenied?.Invoke();

                Permission.RequestUserPermission(Permission.Microphone, callbacks);
            }
            else
            {
                onGranted?.Invoke(); // для остальных — handled by system sheet after plist
            }
#else
            onGranted?.Invoke();
#endif
        }

        // Кросс-платформенные константы (совместимы с Android helper)
        public const string Microphone = "microphone";
        public const string Photos = "photos";
    }
}