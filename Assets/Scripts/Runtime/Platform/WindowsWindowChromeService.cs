using System;
using UnityEngine;

namespace NeonCompanion.Runtime.Platform
{
    /// <summary>
    /// Безрамочное десктоп-окно с системным ресайзом (Win32 + DWM).
    ///
    /// Подход:
    /// - Убираем WS_CAPTION/WS_BORDER/WS_DLGFRAME, оставляем WS_THICKFRAME — окно
    ///   остаётся системно-ресайзабельным за края.
    /// - WM_NCCALCSIZE возвращаем так, что клиентская область занимает всё окно
    ///   (визуальной рамки нет). В maximized добавляем инсет, чтобы окно не лезло
    ///   за экран и не перекрывало таскбар.
    /// - WM_NCHITTEST сами помечаем края/углы как HTLEFT/HTRIGHT/... — Windows
    ///   тянет окно нативно (8px зона по умолчанию).
    /// - Перетаскивание инициируется из UITK через BeginDrag()
    ///   (ReleaseCapture + WM_NCLBUTTONDOWN/HTCAPTION), чтобы кнопки в топбаре
    ///   оставались кликабельными.
    /// - Скругление и тень — через dwmapi (Win11 для углов; на Win10 тихо игнор).
    ///
    /// Субклассит окно так же, как WindowsFileDropService; обе процедуры сцепляются
    /// в цепочку (необработанные сообщения форвардятся через CallWindowProc),
    /// поэтому порядок старта не важен.
    /// </summary>
    public sealed class WindowsWindowChromeService : MonoBehaviour, IWindowChromeService
    {
        // Поля объявлены безусловно, чтобы layout сериализации совпадал между
        // редактором и плеером (иначе сборка падает с "script class layout is
        // incompatible"). Читаются только в Windows-ветке — в редакторе CS0414
        // глушим, как и в WindowsFileDropService с событием.
#pragma warning disable 0414
        [SerializeField] private bool applyOnStart = true;
        [SerializeField] private int resizeBorderThickness = 8;
        [SerializeField] private bool roundedCorners = true;
        [SerializeField] private bool dropShadow = true;
        [SerializeField] private int minWindowWidth = 760;
        [SerializeField] private int minWindowHeight = 560;
#pragma warning restore 0414

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        // ===== Window styles =====
        private const int GwlStyle = -16;
        private const int GwlWndProc = -4;
        private const uint WsCaption = 0x00C00000;
        private const uint WsThickFrame = 0x00040000;
        private const uint WsBorder = 0x00800000;
        private const uint WsDlgFrame = 0x00400000;
        private const uint WsSysMenu = 0x00080000;
        private const uint WsMinimizeBox = 0x00020000;
        private const uint WsMaximizeBox = 0x00010000;

        // ===== Messages =====
        private const uint WmGetMinMaxInfo = 0x0024;
        private const uint WmNcCalcSize = 0x0083;
        private const uint WmNcHitTest = 0x0084;
        private const uint WmNcLButtonDown = 0x00A1;

        // ===== Hit-test codes =====
        private const int HtClient = 1;
        private const int HtCaption = 2;
        private const int HtLeft = 10;
        private const int HtRight = 11;
        private const int HtTop = 12;
        private const int HtTopLeft = 13;
        private const int HtTopRight = 14;
        private const int HtBottom = 15;
        private const int HtBottomLeft = 16;
        private const int HtBottomRight = 17;

        // ===== SetWindowPos / ShowWindow =====
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpFrameChanged = 0x0020;
        private const int SwMinimize = 6;
        private const int SwMaximize = 3;
        private const int SwRestore = 9;

        // ===== System metrics =====
        private const int SmCxFrame = 32;
        private const int SmCyFrame = 33;
        private const int SmCxPaddedBorder = 92;

        // ===== DWM =====
        private const int DwmwaWindowCornerPreference = 33;
        private const int DwmwcpRound = 2;

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private WndProcDelegate _wndProcDelegate;
        private IntPtr _hwnd = IntPtr.Zero;
        private IntPtr _oldWndProc = IntPtr.Zero;
        private uint _originalStyle;
        private bool _borderless;

        public bool IsAvailable
        {
            get { return true; }
        }

        public bool IsMaximized
        {
            get { return _hwnd != IntPtr.Zero && IsZoomed(_hwnd); }
        }

        [Serializable]
        private struct Rect32
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [Serializable]
        private struct Margins
        {
            public int CxLeftWidth;
            public int CxRightWidth;
            public int CyTopHeight;
            public int CyBottomHeight;
        }

        [Serializable]
        private struct Point32
        {
            public int X;
            public int Y;
        }

        [Serializable]
        private struct MinMaxInfo
        {
            public Point32 Reserved;
            public Point32 MaxSize;
            public Point32 MaxPosition;
            public Point32 MinTrackSize;
            public Point32 MaxTrackSize;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out Rect32 lpRect);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsZoomed(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int attrValue, int attrSize);

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref Margins margins);

        internal static WindowsWindowChromeService Instance { get; private set; }

        /// <summary>
        /// Поднимаем сервис как можно раньше (до Boot/Loading сцен), чтобы окно
        /// было безрамочным и с ограничением минимального размера с первого кадра,
        /// включая загрузочный экран. AppBootstrap затем переиспользует этот же
        /// экземпляр через фабрику (не создавая дубль).
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoBootstrap()
        {
            if (Instance != null || CompanionProcessMode.IsPlayerProcess)
                return;

            var go = new GameObject("WindowChromeBridge");
            go.AddComponent<WindowsWindowChromeService>();
            DontDestroyOnLoad(go);
        }

        private void Awake()
        {
            if (Instance == null)
                Instance = this;
        }

        private void Start()
        {
            if (applyOnStart)
                ApplyBorderless();
        }

        public void ApplyBorderless()
        {
            if (_borderless)
                return;

            if (!ResolveWindow())
            {
                Debug.LogWarning("[NeonCompanion] Window chrome service could not find the application window.");
                return;
            }

            // Безрамочный стиль имеет смысл только для оконного режима.
            if (Screen.fullScreenMode != FullScreenMode.Windowed)
                Screen.fullScreenMode = FullScreenMode.Windowed;

            _originalStyle = (uint)GetWindowLongPtr(_hwnd, GwlStyle).ToInt64();

            uint style = _originalStyle;
            style &= ~(WsCaption | WsBorder | WsDlgFrame);
            style |= WsThickFrame | WsSysMenu | WsMinimizeBox | WsMaximizeBox;
            SetWindowLongPtr(_hwnd, GwlStyle, new IntPtr((int)style));

            SubclassWindow();

            if (roundedCorners)
                TryApplyRoundedCorners();

            if (dropShadow)
                TryApplyShadow();

            SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged);

            _borderless = true;
            Debug.Log("[NeonCompanion] Borderless window chrome enabled.");
        }

        public void RestoreDefault()
        {
            if (!_borderless || _hwnd == IntPtr.Zero)
                return;

            UnsubclassWindow();
            SetWindowLongPtr(_hwnd, GwlStyle, new IntPtr((int)_originalStyle));
            SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged);
            _borderless = false;
        }

        public void BeginDrag()
        {
            if (_hwnd == IntPtr.Zero && !ResolveWindow())
                return;

            if (!_borderless)
                ApplyBorderless();

            // Системное перетаскивание из client-зоны: отпускаем capture и сообщаем
            // окну, что нажатие пришлось на "заголовок".
            ReleaseCapture();
            SendMessage(_hwnd, WmNcLButtonDown, new IntPtr(HtCaption), IntPtr.Zero);
        }

        public void ToggleMaximize()
        {
            if (_hwnd == IntPtr.Zero)
                return;

            ShowWindow(_hwnd, IsZoomed(_hwnd) ? SwRestore : SwMaximize);
        }

        public void Minimize()
        {
            if (_hwnd == IntPtr.Zero)
                return;

            ShowWindow(_hwnd, SwMinimize);
        }

        private bool ResolveWindow()
        {
            if (_hwnd != IntPtr.Zero)
                return true;

            _hwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            if (_hwnd == IntPtr.Zero)
                _hwnd = GetActiveWindow();

            return _hwnd != IntPtr.Zero;
        }

        private void SubclassWindow()
        {
            if (_wndProcDelegate != null)
                return;

            _wndProcDelegate = WndProc;
            IntPtr newWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
            _oldWndProc = SetWindowLongPtr(_hwnd, GwlWndProc, newWndProc);
            if (_oldWndProc == IntPtr.Zero)
            {
                Debug.LogWarning("[NeonCompanion] Window chrome service failed to subclass the application window.");
                _wndProcDelegate = null;
            }
        }

        private void UnsubclassWindow()
        {
            if (_wndProcDelegate == null)
                return;

            if (_hwnd != IntPtr.Zero && _oldWndProc != IntPtr.Zero)
                SetWindowLongPtr(_hwnd, GwlWndProc, _oldWndProc);

            _oldWndProc = IntPtr.Zero;
            _wndProcDelegate = null;
        }

        private void TryApplyRoundedCorners()
        {
            try
            {
                int pref = DwmwcpRound;
                DwmSetWindowAttribute(_hwnd, DwmwaWindowCornerPreference, ref pref, sizeof(int));
            }
            catch (Exception)
            {
                // Win10 и старше — атрибут не поддерживается, это нормально.
            }
        }

        private void TryApplyShadow()
        {
            try
            {
                var margins = new Margins();
                margins.CxLeftWidth = 0;
                margins.CxRightWidth = 0;
                margins.CyTopHeight = 1;
                margins.CyBottomHeight = 0;
                DwmExtendFrameIntoClientArea(_hwnd, ref margins);
            }
            catch (Exception)
            {
                // DWM недоступен — без тени, не критично.
            }
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WmGetMinMaxInfo)
            {
                ClampMinTrackSize(hWnd, lParam);
                return IntPtr.Zero;
            }

            if (msg == WmNcCalcSize && wParam != IntPtr.Zero)
            {
                // Клиентская область = всё окно (рамка визуально исчезает).
                // В maximized возвращаем инсет, иначе окно вылезет за экран и
                // перекроет таскбар.
                if (IsZoomed(hWnd))
                    InsetMaximizedClientArea(lParam);

                return IntPtr.Zero;
            }

            if (msg == WmNcHitTest)
            {
                int hit = HitTest(hWnd, lParam);
                if (hit != HtClient)
                    return new IntPtr(hit);

                return new IntPtr(HtClient);
            }

            return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
        }

        private void ClampMinTrackSize(IntPtr hWnd, IntPtr lParam)
        {
            if (minWindowWidth <= 0 && minWindowHeight <= 0)
                return;

            MinMaxInfo info = (MinMaxInfo)System.Runtime.InteropServices.Marshal.PtrToStructure(lParam, typeof(MinMaxInfo));

            // В физических пикселях — ровно как заданная сборочная резолюция, чтобы
            // окно можно было вернуть к минимуму. НЕ домножаем на DPI: иначе на
            // масштабе >100% минимум станет больше стартового размера и обратно
            // к нему не ужать.
            info.MinTrackSize.X = minWindowWidth;
            info.MinTrackSize.Y = minWindowHeight;

            System.Runtime.InteropServices.Marshal.StructureToPtr(info, lParam, false);
        }

        private void InsetMaximizedClientArea(IntPtr lParam)
        {
            // lParam указывает на NCCALCSIZE_PARAMS; первый член — RECT нового окна.
            Rect32 rect = (Rect32)System.Runtime.InteropServices.Marshal.PtrToStructure(lParam, typeof(Rect32));

            int frameX = GetSystemMetrics(SmCxFrame) + GetSystemMetrics(SmCxPaddedBorder);
            int frameY = GetSystemMetrics(SmCyFrame) + GetSystemMetrics(SmCxPaddedBorder);

            rect.Left += frameX;
            rect.Right -= frameX;
            rect.Top += frameY;
            rect.Bottom -= frameY;

            System.Runtime.InteropServices.Marshal.StructureToPtr(rect, lParam, false);
        }

        private int HitTest(IntPtr hWnd, IntPtr lParam)
        {
            // Ресайз краёв отключён, пока окно развёрнуто.
            if (IsZoomed(hWnd))
                return HtClient;

            Rect32 wr;
            if (!GetWindowRect(hWnd, out wr))
                return HtClient;

            long packed = lParam.ToInt64();
            int x = unchecked((short)(packed & 0xFFFF));
            int y = unchecked((short)((packed >> 16) & 0xFFFF));

            int border = resizeBorderThickness;
            bool left = x >= wr.Left && x < wr.Left + border;
            bool right = x < wr.Right && x >= wr.Right - border;
            bool top = y >= wr.Top && y < wr.Top + border;
            bool bottom = y < wr.Bottom && y >= wr.Bottom - border;

            if (top && left)
                return HtTopLeft;
            if (top && right)
                return HtTopRight;
            if (bottom && left)
                return HtBottomLeft;
            if (bottom && right)
                return HtBottomRight;
            if (left)
                return HtLeft;
            if (right)
                return HtRight;
            if (top)
                return HtTop;
            if (bottom)
                return HtBottom;

            return HtClient;
        }

        private void OnDisable()
        {
            RestoreDefault();
        }

        private void OnApplicationQuit()
        {
            // Restore the two managed links in strict LIFO order while the HWND and
            // both delegates are still alive. Unity does not guarantee callback
            // order between these components during shutdown.
            if (WindowsFileDropService.Instance != null)
                WindowsFileDropService.Instance.Stop();
            RestoreDefault();
        }

        private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        {
            if (IntPtr.Size == 8)
                return GetWindowLongPtr64(hWnd, nIndex);

            return new IntPtr(GetWindowLong32(hWnd, nIndex));
        }

        private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            if (IntPtr.Size == 8)
                return SetWindowLongPtr64(hWnd, nIndex, dwNewLong);

            return new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
        }
#else
        public bool IsAvailable
        {
            get { return false; }
        }

        public bool IsMaximized
        {
            get { return false; }
        }

        public void ApplyBorderless() { }
        public void RestoreDefault() { }
        public void BeginDrag() { }
        public void ToggleMaximize() { }
        public void Minimize() { }
#endif
    }
}
