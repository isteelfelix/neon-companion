using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace NeonCompanion.Runtime.Platform
{
    public sealed class WindowsFileDropService : MonoBehaviour, IFileDropService
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        public event Action<IReadOnlyList<string>> FilesDropped;
#else
#pragma warning disable 0067
        public event Action<IReadOnlyList<string>> FilesDropped;
#pragma warning restore 0067
#endif

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        private const int GwlWndProc = -4;
        private const uint WmDropFiles = 0x0233;
        private const uint DragQueryAllFiles = 0xFFFFFFFF;

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private readonly Queue<List<string>> _pendingDrops = new Queue<List<string>>();
        private WndProcDelegate _wndProcDelegate;
        private IntPtr _hwnd = IntPtr.Zero;
        private IntPtr _oldWndProc = IntPtr.Zero;
        private bool _enabled;

        public bool IsAvailable => true;
        internal static WindowsFileDropService Instance { get; private set; }

        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        private static extern void DragAcceptFiles(IntPtr hWnd, bool fAccept);

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern uint DragQueryFileW(IntPtr hDrop, uint iFile, StringBuilder lpszFile, uint cch);

        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        private static extern void DragFinish(IntPtr hDrop);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        private void Awake()
        {
            if (Instance == null)
                Instance = this;
        }

        public void Start()
        {
            if (_enabled)
                return;

            _hwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            if (_hwnd == IntPtr.Zero)
                _hwnd = GetActiveWindow();

            if (_hwnd == IntPtr.Zero)
            {
                Debug.LogWarning("[NeonCompanion] File drop service could not find the application window.");
                return;
            }

            _wndProcDelegate = WndProc;
            IntPtr newWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
            _oldWndProc = SetWindowLongPtr(_hwnd, GwlWndProc, newWndProc);
            if (_oldWndProc == IntPtr.Zero)
            {
                Debug.LogWarning("[NeonCompanion] File drop service failed to subclass the application window.");
                _wndProcDelegate = null;
                return;
            }

            DragAcceptFiles(_hwnd, true);
            _enabled = true;
            Debug.Log("[NeonCompanion] Windows file drop service enabled.");
        }

        public void Stop()
        {
            if (!_enabled)
                return;

            DragAcceptFiles(_hwnd, false);
            if (_hwnd != IntPtr.Zero && _oldWndProc != IntPtr.Zero)
                SetWindowLongPtr(_hwnd, GwlWndProc, _oldWndProc);

            _enabled = false;
            _hwnd = IntPtr.Zero;
            _oldWndProc = IntPtr.Zero;
            _wndProcDelegate = null;

            lock (_pendingDrops)
                _pendingDrops.Clear();
        }

        private void Update()
        {
            while (true)
            {
                List<string> paths = null;
                lock (_pendingDrops)
                {
                    if (_pendingDrops.Count > 0)
                        paths = _pendingDrops.Dequeue();
                }

                if (paths == null)
                    break;

                if (paths.Count > 0)
                    FilesDropped?.Invoke(paths);
            }
        }

        private void OnDestroy()
        {
            Stop();
            if (ReferenceEquals(Instance, this))
                Instance = null;
        }

        private void OnApplicationQuit()
        {
            // This service is the top link in the WndProc chain. It must be removed
            // before WindowsWindowChromeService restores Unity's original WndProc.
            Stop();
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WmDropFiles)
            {
                QueueDroppedFiles(wParam);
                return IntPtr.Zero;
            }

            return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
        }

        private void QueueDroppedFiles(IntPtr hDrop)
        {
            try
            {
                uint count = DragQueryFileW(hDrop, DragQueryAllFiles, null, 0);
                var paths = new List<string>((int)count);

                for (uint i = 0; i < count; i++)
                {
                    uint length = DragQueryFileW(hDrop, i, null, 0);
                    if (length == 0)
                        continue;

                    var buffer = new StringBuilder((int)length + 1);
                    DragQueryFileW(hDrop, i, buffer, (uint)buffer.Capacity);
                    string path = buffer.ToString();
                    if (!string.IsNullOrWhiteSpace(path))
                        paths.Add(path);
                }

                if (paths.Count > 0)
                {
                    lock (_pendingDrops)
                        _pendingDrops.Enqueue(paths);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[NeonCompanion] File drop failed: " + ex.Message);
            }
            finally
            {
                DragFinish(hDrop);
            }
        }

        private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            if (IntPtr.Size == 8)
                return SetWindowLongPtr64(hWnd, nIndex, dwNewLong);

            return new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
        }
#else
        public bool IsAvailable => false;

        public void Start() { }
        public void Stop() { }
#endif
    }
}
