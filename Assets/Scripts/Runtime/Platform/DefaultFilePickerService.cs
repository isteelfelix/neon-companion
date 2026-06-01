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
            return PickFileAsync("png,jpg,jpeg");
        }

        public Task<string> PickFileAsync(string extension)
        {
#if UNITY_EDITOR
            string title = extension.Contains("png") ? "Select image" : "Select file";
            return Task.FromResult(UnityEditor.EditorUtility.OpenFilePanel(title, "", extension));
#elif UNITY_STANDALONE_WIN
            return PickWindowsFilePathAsync(extension);
#elif UNITY_ANDROID
            return PickAndroidImagePathAsync(); // Android path re-used; filter not enforced
#else
            return Task.FromResult<string>(null);
#endif
        }

        public Task<string> PickSavePathAsync(string defaultFileName, string extension)
        {
#if UNITY_EDITOR
            string ext = (extension ?? "md").TrimStart('.');
            string path = UnityEditor.EditorUtility.SaveFilePanel("Save Chat Export", "", defaultFileName, ext);
            return Task.FromResult(string.IsNullOrEmpty(path) ? null : path);
#elif UNITY_STANDALONE_WIN
            return PickWindowsSavePathAsync(defaultFileName, extension);
#else
            return Task.FromResult<string>(null);
#endif
        }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        private static Task<string> PickWindowsFilePathAsync(string extension)
        {
            var completion = new TaskCompletionSource<string>();
            var thread = new Thread(() => completion.TrySetResult(PickWindowsFilePath(extension)));

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

        private static Task<string> PickWindowsSavePathAsync(string defaultName, string extension)
        {
            var completion = new TaskCompletionSource<string>();
            var thread = new Thread(() => completion.TrySetResult(PickWindowsSavePath(defaultName, extension)));
            try
            {
                thread.SetApartmentState(ApartmentState.STA);
                thread.IsBackground = true;
                thread.Start();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NeonCompanion] Windows save dialog thread failed: {ex.Message}");
                completion.TrySetResult(null);
            }
            return completion.Task;
        }

        private static string PickWindowsSavePath(string defaultName, string extension)
        {
            try
            {
                var dialogType = FindType("System.Windows.Forms.SaveFileDialog");
                var resultType = FindType("System.Windows.Forms.DialogResult");
                if (dialogType == null || resultType == null)
                    return null;

                string ext = (extension ?? "md").TrimStart('.');
                string filter = $"Markdown (*.{ext})|*.{ext}|All files (*.*)|*.*";

                var dialog = Activator.CreateInstance(dialogType);
                SetProperty(dialogType, dialog, "Title", "Save Chat Export");
                SetProperty(dialogType, dialog, "Filter", filter);
                SetProperty(dialogType, dialog, "FileName", defaultName ?? ("chat-export." + ext));
                SetProperty(dialogType, dialog, "DefaultExt", ext);
                SetProperty(dialogType, dialog, "OverwritePrompt", true);

                var result = dialogType.GetMethod("ShowDialog", Type.EmptyTypes)?.Invoke(dialog, null);
                var ok = Enum.Parse(resultType, "OK");
                string path = Equals(result, ok)
                    ? dialogType.GetProperty("FileName")?.GetValue(dialog) as string
                    : null;

                (dialog as IDisposable)?.Dispose();
                return string.IsNullOrWhiteSpace(path) ? null : path;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NeonCompanion] Windows save dialog failed: {ex.Message}");
                return null;
            }
        }

        private static string PickWindowsFilePath(string extension)
        {
            try
            {
                var dialogType = FindType("System.Windows.Forms.OpenFileDialog");
                var resultType = FindType("System.Windows.Forms.DialogResult");
                if (dialogType == null || resultType == null)
                    return null;

                bool isImage = extension.Contains("png") || extension.Contains("jpg");
                string exts = string.Join(";", Array.ConvertAll(extension.Split(','), e => $"*.{e.Trim()}"));
                string filter = isImage
                    ? $"Image files ({exts})|{exts}|All files (*.*)|*.*"
                    : $"Files ({exts})|{exts}|All files (*.*)|*.*";

                var dialog = Activator.CreateInstance(dialogType);
                SetProperty(dialogType, dialog, "Title", isImage ? "Select image" : "Select file");
                SetProperty(dialogType, dialog, "Filter", filter);
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

            AndroidPermissionHelper.RequestPermission(
                AndroidPermissionHelper.READ_EXTERNAL_STORAGE,
                onGranted: () =>
                {
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
                },
                onDenied: () =>
                {
                    Debug.LogWarning("[NeonCompanion] Storage permission denied for file picker");
                    _androidPickCompletion.TrySetResult(null);
                });

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