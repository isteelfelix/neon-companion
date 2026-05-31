using System;
using UnityEngine;

namespace NeonCompanion.Runtime.Platform.iOS
{
    /// <summary>
    /// iOS-specific file picker bridge.
    /// Called from native NeonFilePicker.mm via UnitySendMessage.
    /// 
    /// Mirrors the pattern of AndroidFilePickerBridge in DefaultFilePickerService.
    /// See docs/17_iOS_Platform_Architecture.md (IOS-02).
    /// </summary>
    public class iOSFilePickerBridge : MonoBehaviour
    {
        private static iOSFilePickerBridge _instance;
        private Action<string> _resultHandler;

        public static iOSFilePickerBridge GetOrCreate(Action<string> resultHandler)
        {
#if UNITY_IOS && !UNITY_EDITOR
            if (_instance == null)
            {
                GameObject go = new GameObject("NeonIOSFilePickerBridge");
                _instance = go.AddComponent<iOSFilePickerBridge>();
                DontDestroyOnLoad(go);
            }
            _instance._resultHandler = resultHandler;
            return _instance;
#else
            return null;
#endif
        }

        // Called from native code (NeonFilePicker.mm)
        public void OnFilePicked(string path)
        {
            _resultHandler?.Invoke(path);
            _resultHandler = null;
        }
    }
}