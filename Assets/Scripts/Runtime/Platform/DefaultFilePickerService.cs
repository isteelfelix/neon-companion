using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace NeonCompanion.Runtime.Platform
{
    public sealed class DefaultFilePickerService : IFilePickerService
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        private TaskCompletionSource<string> _androidPickCompletion;
#endif

        public Task<string> PickImagePathAsync()
        {
#if UNITY_EDITOR
            return Task.FromResult(UnityEditor.EditorUtility.OpenFilePanel("Select avatar image", "", "png,jpg,jpeg"));
#elif UNITY_STANDALONE_WIN
            return PickWindowsImagePathAsync();
#elif UNITY_ANDROID
            return PickAndroidImagePathAsync();
#else
            return Task.FromResult<string>(null);
#endif
        }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        private static Task<string> PickWindowsImagePathAsync()
        {
            var completion = new TaskCompletionSource<string>();
            var thread = new Thread(() => completion.TrySetResult(PickWindowsImagePath()));

            try
            {
                thread.SetApartmentState(ApartmentState.STA);
                thread.IsBackground = true;
                thread.Start();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NeonCompanion] Windows file picker thread failed: {ex.Message}");
                completion.TrySetResult(null);
            }

            return completion.Task;
        }

        private static string PickWindowsImagePath()
        {
            try
            {
                var dialogType = FindType("System.Windows.Forms.OpenFileDialog");
                var resultType = FindType("System.Windows.Forms.DialogResult");
                if (dialogType == null || resultType == null)
                    return null;

                var dialog = Activator.CreateInstance(dialogType);
                SetProperty(dialogType, dialog, "Title", "Select avatar image");
                SetProperty(dialogType, dialog, "Filter", "Image files (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg|All files (*.*)|*.*");
                SetProperty(dialogType, dialog, "CheckFileExists", true);
                SetProperty(dialogType, dialog, "Multiselect", false);

                var result = dialogType.GetMethod("ShowDialog", Type.EmptyTypes)?.Invoke(dialog, null);
                var ok = Enum.Parse(resultType, "OK");
                string fileName = Equals(result, ok)
                    ? dialogType.GetProperty("FileName")?.GetValue(dialog) as string
                    : null;

                (dialog as IDisposable)?.Dispose();
                return string.IsNullOrWhiteSpace(fileName) ? null : fileName;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NeonCompanion] Windows file picker failed: {ex.Message}");
                return null;
            }
        }

        private static void SetProperty(Type type, object instance, string propertyName, object value)
        {
            type.GetProperty(propertyName)?.SetValue(instance, value);
        }

        private static Type FindType(string typeName)
        {
            var type = Type.GetType(typeName);
            if (type != null)
                return type;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType(typeName);
                if (type != null)
                    return type;
            }

            try
            {
                var assembly = Assembly.Load("System.Windows.Forms");
                return assembly.GetType(typeName);
            }
            catch
            {
                return null;
            }
        }
#endif

#if UNITY_ANDROID && !UNITY_EDITOR
        private Task<string> PickAndroidImagePathAsync()
        {
            if (_androidPickCompletion != null && !_androidPickCompletion.Task.IsCompleted)
                return _androidPickCompletion.Task;

            _androidPickCompletion = new TaskCompletionSource<string>();
            var bridge = AndroidFilePickerBridge.GetOrCreate(OnAndroidImagePicked);

            try
            {
                var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                var picker = new AndroidJavaClass("com.neoncompanion.filepicker.NeonFilePickerActivity");
                picker.CallStatic("pick", activity, bridge.gameObject.name);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NeonCompanion] Android file picker failed: {ex.Message}");
                _androidPickCompletion.TrySetResult(null);
            }

            return _androidPickCompletion.Task;
        }

        private void OnAndroidImagePicked(string path)
        {
            var completion = _androidPickCompletion;
            _androidPickCompletion = null;
            completion?.TrySetResult(string.IsNullOrWhiteSpace(path) ? null : path);
        }
#endif
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    public sealed class AndroidFilePickerBridge : MonoBehaviour
    {
        private static AndroidFilePickerBridge _instance;
        private Action<string> _resultHandler;

        public static AndroidFilePickerBridge GetOrCreate(Action<string> resultHandler)
        {
            if (_instance == null)
            {
                var gameObject = new GameObject("NeonAndroidFilePickerBridge");
                DontDestroyOnLoad(gameObject);
                _instance = gameObject.AddComponent<AndroidFilePickerBridge>();
            }

            _instance._resultHandler = resultHandler;
            return _instance;
        }

        public void OnAndroidImagePicked(string path)
        {
            _resultHandler?.Invoke(path);
        }
    }
#endif
}
