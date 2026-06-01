using System;
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
        // ── Windows file dialogs via comdlg32.dll (no System.Windows.Forms dependency) ──

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private struct OPENFILENAME
        {
            public int    lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public string lpstrFilter;
            public IntPtr lpstrCustomFilter;
            public int    nMaxCustFilter;
            public int    nFilterIndex;
            public IntPtr lpstrFile;
            public int    nMaxFile;
            public IntPtr lpstrFileTitle;
            public int    nMaxFileTitle;
            public string lpstrInitialDir;
            public string lpstrTitle;
            public int    Flags;
            public short  nFileOffset;
            public short  nFileExtension;
            public string lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public string lpTemplateName;
            public IntPtr pvReserved;
            public int    dwReserved;
            public int    FlagsEx;
        }

        [System.Runtime.InteropServices.DllImport("comdlg32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern bool GetOpenFileNameW(ref OPENFILENAME ofn);

        [System.Runtime.InteropServices.DllImport("comdlg32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern bool GetSaveFileNameW(ref OPENFILENAME ofn);

        private const int OFN_FILEMUSTEXIST   = 0x00001000;
        private const int OFN_PATHMUSTEXIST   = 0x00000800;
        private const int OFN_NOCHANGEDIR     = 0x00000008;
        private const int OFN_OVERWRITEPROMPT = 0x00000002;
        private const int OFN_EXPLORER        = 0x00080000;
        private const int OFN_HIDEREADONLY    = 0x00000004;

        private static Task<string> PickWindowsFilePathAsync(string extension)
        {
            var tcs = new TaskCompletionSource<string>();
            var t = new Thread(() => tcs.TrySetResult(ShowComdlgDialog(extension, isSave: false, defaultName: null)));
            try { t.SetApartmentState(ApartmentState.STA); t.IsBackground = true; t.Start(); }
            catch (Exception ex) { Debug.LogWarning("[NeonCompanion] File picker thread: " + ex.Message); tcs.TrySetResult(null); }
            return tcs.Task;
        }

        private static Task<string> PickWindowsSavePathAsync(string defaultName, string extension)
        {
            var tcs = new TaskCompletionSource<string>();
            var t = new Thread(() => tcs.TrySetResult(ShowComdlgDialog(extension, isSave: true, defaultName: defaultName)));
            try { t.SetApartmentState(ApartmentState.STA); t.IsBackground = true; t.Start(); }
            catch (Exception ex) { Debug.LogWarning("[NeonCompanion] Save dialog thread: " + ex.Message); tcs.TrySetResult(null); }
            return tcs.Task;
        }

        private static string ShowComdlgDialog(string extension, bool isSave, string defaultName)
        {
            try
            {
                const int bufLen = 32768;
                IntPtr buf = System.Runtime.InteropServices.Marshal.AllocHGlobal(bufLen * 2);
                try
                {
                    // Zero the buffer
                    for (int i = 0; i < bufLen * 2; i++)
                        System.Runtime.InteropServices.Marshal.WriteByte(buf, i, 0);

                    // Write default filename if provided
                    if (!string.IsNullOrEmpty(defaultName))
                    {
                        byte[] nameBytes = System.Text.Encoding.Unicode.GetBytes(defaultName);
                        int copyLen = Math.Min(nameBytes.Length, (bufLen - 2) * 2);
                        System.Runtime.InteropServices.Marshal.Copy(nameBytes, 0, buf, copyLen);
                    }

                    string ext = string.IsNullOrEmpty(extension)
                        ? "txt"
                        : extension.Split(',')[0].Trim().TrimStart('.');

                    string filter = isSave
                        ? "Markdown (*." + ext + ")\0*." + ext + "\0All files (*.*)\0*.*\0\0"
                        : BuildOpenFilter(extension);

                    var ofn = new OPENFILENAME();
                    ofn.lStructSize  = System.Runtime.InteropServices.Marshal.SizeOf(typeof(OPENFILENAME));
                    ofn.hwndOwner    = IntPtr.Zero;
                    ofn.lpstrFilter  = filter;
                    ofn.nFilterIndex = 1;
                    ofn.lpstrFile    = buf;
                    ofn.nMaxFile     = bufLen;
                    ofn.lpstrTitle   = isSave ? "Save Chat Export" : "Select Image";
                    ofn.lpstrDefExt  = ext;
                    ofn.Flags        = OFN_NOCHANGEDIR | OFN_EXPLORER | OFN_HIDEREADONLY
                        | (isSave
                            ? OFN_OVERWRITEPROMPT
                            : OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST);

                    bool ok = isSave ? GetSaveFileNameW(ref ofn) : GetOpenFileNameW(ref ofn);
                    if (!ok) return null;

                    string result = System.Runtime.InteropServices.Marshal.PtrToStringUni(buf);
                    return string.IsNullOrWhiteSpace(result) ? null : result;
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.FreeHGlobal(buf);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[NeonCompanion] comdlg32 dialog failed: " + ex.Message);
                return null;
            }
        }

        private static string BuildOpenFilter(string extension)
        {
            bool isImage = extension.Contains("png") || extension.Contains("jpg");
            string label = isImage ? "Images" : "Files";
            string parts = string.Join(";", Array.ConvertAll(
                extension.Split(new char[]{','}, StringSplitOptions.RemoveEmptyEntries),
                e => "*." + e.Trim().TrimStart('.')));
            return label + " (" + parts + ")\0" + parts + "\0All files (*.*)\0*.*\0\0";
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