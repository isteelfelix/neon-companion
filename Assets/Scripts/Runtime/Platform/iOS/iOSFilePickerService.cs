using System;
using System.Threading.Tasks;
using UnityEngine;

namespace NeonCompanion.Runtime.Platform.iOS
{
    /// <summary>
    /// iOS-реализация IFilePickerService.
    /// 
    /// Полная реализация с нативным плагином:
    /// - NeonFilePicker.mm (UIDocumentPickerViewController / PHPicker)
    /// - iOSFilePickerBridge для UnitySendMessage коллбэков
    /// 
    /// См. docs/17_iOS_Platform_Architecture.md (IOS-02) и docs/16_Platform_Architecture.md.
    /// </summary>
    public sealed class iOSFilePickerService : IFilePickerService
    {
        public Task<string> PickImagePathAsync()
        {
            var tcs = new TaskCompletionSource<string>();

#if UNITY_IOS && !UNITY_EDITOR
            Debug.Log("[NeonCompanion][iOS] Native image picker requested.");
            var bridge = iOSFilePickerBridge.GetOrCreate(path =>
            {
                tcs.TrySetResult(string.IsNullOrEmpty(path) ? null : path);
            });

            // Call native
            NeonFilePicker_PickImage(bridge.gameObject.name);
#else
            Debug.Log("[NeonCompanion][iOS] File picker (image) - not on device, using fallback.");
            tcs.SetResult(null);
#endif

            return tcs.Task;
        }

        public Task<string> PickFileAsync(string extension)
        {
            var tcs = new TaskCompletionSource<string>();

#if UNITY_IOS && !UNITY_EDITOR
            Debug.Log($"[NeonCompanion][iOS] Native file picker requested (ext: {extension}).");
            var bridge = iOSFilePickerBridge.GetOrCreate(path =>
            {
                tcs.TrySetResult(string.IsNullOrEmpty(path) ? null : path);
            });

            NeonFilePicker_PickFile(bridge.gameObject.name, extension ?? "");
#else
            Debug.Log($"[NeonCompanion][iOS] File picker (ext: {extension}) - not on device.");
            tcs.SetResult(null);
#endif

            return tcs.Task;
        }

#if UNITY_IOS && !UNITY_EDITOR
        [System.Runtime.InteropServices.DllImport("__Internal")]
        private static extern void NeonFilePicker_PickImage(string gameObjectName);

        [System.Runtime.InteropServices.DllImport("__Internal")]
        private static extern void NeonFilePicker_PickFile(string gameObjectName, string extension);
#endif
    }
}