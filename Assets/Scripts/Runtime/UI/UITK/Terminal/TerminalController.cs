using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using NeonCompanion.Runtime.Localization;
using NeonCompanion.Runtime.Terminal;
using NeonCompanion.Runtime.Terminal.Emulator;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK.Terminal
{
    /// <summary>
    /// Drives the interactive terminal panel: a real shell over a pseudo terminal
    /// (<see cref="IPtySession"/>), interpreted by a <see cref="TerminalEmulator"/> and drawn
    /// by a <see cref="TerminalScreenView"/>. PTY output arrives on a background thread, is
    /// queued, and is fed to the emulator on Unity's main thread in <see cref="Update"/>.
    ///
    /// Remote agent execution belongs to ClientTerminalExecutionService. This controller owns
    /// only the human-facing PTY workspace and read-only backend process views.
    /// </summary>
    public sealed class TerminalController : MonoBehaviour
    {
        private const int FontSize = 12;

        private sealed class TerminalTab
        {
            public string Id;
            public string ProcessId;
            public bool ReadOnly;
            public VisualElement Button;
            public VisualElement Host;
            public TerminalController Pane;
        }

        private VisualElement _root;
        private TerminalScreenView _view;
        private TerminalEmulator _emulator;
        private IPtySession _session;

        private readonly ConcurrentQueue<byte[]> _pending = new ConcurrentQueue<byte[]>();
        private volatile bool _exited;
        private volatile int _exitCode;
        private bool _exitReported;
        private bool _sessionStarted;
        private bool _dirty;
        private bool _keepKeyboardFocus;
        private VisualElement _documentRoot;

        private bool _isWorkspace;
        private VisualElement _tabBar;
        private ScrollView _tabStrip;
        private VisualElement _tabList;
        private VisualElement _paneHost;
        private readonly List<TerminalTab> _tabs = new List<TerminalTab>();
        private TerminalTab _activeTab;
        private TerminalTab _pressedTab;
        private int _tabStripPointerId = -1;
        private float _tabStripPointerStartX;
        private float _tabStripStartOffsetX;
        private bool _tabStripDragged;

        private const float TabStripDragThreshold = 4f;

        // ---- Lifecycle ------------------------------------------------------------

        public void Initialize(VisualElement terminalRoot)
        {
            _root = terminalRoot;
            if (_root == null)
                return;

            _root.Clear();
            _isWorkspace = true;

            _tabBar = new VisualElement();
            _tabBar.name = "terminal-tabs";
            _tabBar.AddToClassList("terminal-tabs");

            Button add = new Button(AddUserTab);
            add.text = "+";
            add.tooltip = LocalizationExtensions.Get("terminal.new", "New terminal");
            add.AddToClassList("terminal-tabs__add");

            _tabStrip = new ScrollView(ScrollViewMode.Horizontal);
            _tabStrip.name = "terminal-tab-strip";
            _tabStrip.AddToClassList("terminal-tabs__scroll");
            _tabStrip.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            _tabStrip.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            _tabStrip.RegisterCallback<PointerDownEvent>(OnTabStripPointerDown, TrickleDown.TrickleDown);
            _tabStrip.RegisterCallback<PointerMoveEvent>(OnTabStripPointerMove);
            _tabStrip.RegisterCallback<PointerUpEvent>(OnTabStripPointerUp);
            _tabStrip.RegisterCallback<PointerCaptureOutEvent>(OnTabStripPointerCaptureOut);
            _tabStrip.RegisterCallback<WheelEvent>(OnTabStripWheel);

            _tabList = _tabStrip.contentContainer;
            _tabList.name = "terminal-tab-list";
            _tabList.AddToClassList("terminal-tabs__list");
            _tabBar.Add(_tabStrip);
            _tabBar.Add(add);

            _paneHost = new VisualElement();
            _paneHost.name = "terminal-panes";
            _paneHost.AddToClassList("terminal-panes");

            _root.Add(_tabBar);
            _root.Add(_paneHost);
            AddUserTab();
        }

        private void InitializePane(VisualElement terminalRoot, bool startShell)
        {
            _root = terminalRoot;
            _root.AddToClassList("terminal-pane");

            _emulator = new TerminalEmulator(80, 24);
            _emulator.Respond += OnEmulatorRespond;

            _view = new TerminalScreenView(FontSize);
            _view.style.flexGrow = 1;
            _view.ViewportChanged += OnViewportChanged;
            _view.ScrollRequested += OnScrollRequested;
            _view.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            _view.RegisterCallback<PointerDownEvent>(OnPointerDown);
            _root.Add(_view);

            _view.ShowMessage("Starting shell...");
            _view.schedule.Execute(AttachFocusGuard).ExecuteLater(50);

            _sessionStarted = !startShell;
            if (!startShell)
            {
                _view.HideMessage();
                _dirty = true;
            }
        }

        private void AddUserTab()
        {
            AddTab(null, false, null);
        }

        private TerminalTab AddTab(string processId, bool readOnly, string initialOutput)
        {
            if (!_isWorkspace || _paneHost == null)
                return null;

            var tab = new TerminalTab();
            tab.Id = Guid.NewGuid().ToString("N");
            tab.ProcessId = processId;
            tab.ReadOnly = readOnly;
            tab.Host = new VisualElement();
            tab.Host.AddToClassList("terminal-pane-host");
            tab.Host.style.display = DisplayStyle.None;
            _paneHost.Add(tab.Host);

            tab.Pane = gameObject.AddComponent<TerminalController>();
            tab.Pane.InitializePane(tab.Host, !readOnly);
            if (readOnly && !string.IsNullOrEmpty(initialOutput))
                tab.Pane.AppendReadOnlyOutput(initialOutput);

            var button = new VisualElement();
            button.AddToClassList("terminal-tab");
            button.focusable = true;
            button.userData = tab;
            if (readOnly)
                button.AddToClassList("terminal-tab--agent");

            string title = readOnly
                ? LocalizationExtensions.Get("terminal.agent", "agent") + " " + ShortId(processId)
                : LocalizationExtensions.Get("terminal.shell", "shell") + " " + NextShellNumber();
            button.tooltip = title + " — " + (readOnly
                ? LocalizationExtensions.Get("terminal.agent_readonly", "Agent output (read-only)")
                : LocalizationExtensions.Get("terminal.switch", "Switch terminal"));
            button.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.Space)
                    return;
                SelectTab(tab);
                evt.StopPropagation();
            });
            tab.Button = button;

            var icon = new VisualElement();
            icon.AddToClassList("terminal-tab__icon");

            var close = new Button();
            close.text = "×";
            close.tooltip = LocalizationExtensions.Get("terminal.close", "Close terminal");
            close.AddToClassList("terminal-tab__close");
            close.RegisterCallback<ClickEvent>(evt =>
            {
                evt.StopPropagation();
                CloseTab(tab);
            });
            button.Add(icon);
            button.Add(close);
            _tabList.Add(button);
            _tabs.Add(tab);
            if (!readOnly || _activeTab == null)
                SelectTab(tab);
            return tab;
        }

        private int NextShellNumber()
        {
            int count = 1;
            for (int i = 0; i < _tabs.Count; i++)
            {
                if (!_tabs[i].ReadOnly)
                    count++;
            }
            return count;
        }

        private static string ShortId(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            return value.Length <= 8 ? value : value.Substring(0, 8);
        }

        private void SelectTab(TerminalTab tab)
        {
            if (tab == null || !_tabs.Contains(tab))
                return;

            for (int i = 0; i < _tabs.Count; i++)
            {
                bool active = _tabs[i] == tab;
                _tabs[i].Host.style.display = active ? DisplayStyle.Flex : DisplayStyle.None;
                _tabs[i].Button.EnableInClassList("terminal-tab--active", active);
            }
            _activeTab = tab;
            tab.Pane.SetVisible(true);
            if (_tabStrip != null)
                _tabStrip.schedule.Execute(() => _tabStrip.ScrollTo(tab.Button));
        }

        private void OnTabStripPointerDown(PointerDownEvent evt)
        {
            if (evt.button != 0 || _tabStrip == null)
                return;

            VisualElement target = evt.target as VisualElement;
            if (HasClassInParents(target, "terminal-tab__close"))
                return;

            TerminalTab tab = TabFromTarget(target);
            if (tab == null)
                return;

            _pressedTab = tab;
            _tabStripPointerId = evt.pointerId;
            _tabStripPointerStartX = evt.position.x;
            _tabStripStartOffsetX = _tabStrip.scrollOffset.x;
            _tabStripDragged = false;
            _tabStrip.CapturePointer(evt.pointerId);
        }

        private void OnTabStripPointerMove(PointerMoveEvent evt)
        {
            if (_tabStrip == null || evt.pointerId != _tabStripPointerId)
                return;
            if (!_tabStrip.HasPointerCapture(evt.pointerId))
                return;

            float delta = evt.position.x - _tabStripPointerStartX;
            if (!_tabStripDragged && Mathf.Abs(delta) >= TabStripDragThreshold)
                _tabStripDragged = true;
            if (!_tabStripDragged)
                return;

            Vector2 offset = _tabStrip.scrollOffset;
            offset.x = Mathf.Clamp(_tabStripStartOffsetX - delta, 0f, MaxTabStripScrollX());
            _tabStrip.scrollOffset = offset;
            evt.StopPropagation();
        }

        private void OnTabStripPointerUp(PointerUpEvent evt)
        {
            if (_tabStrip == null || evt.pointerId != _tabStripPointerId)
                return;

            bool wasDrag = _tabStripDragged;
            TerminalTab pressed = _pressedTab;
            if (_tabStrip.HasPointerCapture(evt.pointerId))
                _tabStrip.ReleasePointer(evt.pointerId);
            ResetTabStripDrag();

            if (!wasDrag && pressed != null)
                SelectTab(pressed);
            evt.StopPropagation();
        }

        private void OnTabStripPointerCaptureOut(PointerCaptureOutEvent evt)
        {
            ResetTabStripDrag();
        }

        private void OnTabStripWheel(WheelEvent evt)
        {
            if (_tabStrip == null)
                return;

            float max = MaxTabStripScrollX();
            if (max <= 0f)
                return;

            Vector2 offset = _tabStrip.scrollOffset;
            float wheelDelta = Mathf.Abs(evt.delta.x) > Mathf.Abs(evt.delta.y)
                ? evt.delta.x
                : evt.delta.y;
            offset.x = Mathf.Clamp(offset.x + wheelDelta * 18f, 0f, max);
            _tabStrip.scrollOffset = offset;
            evt.StopPropagation();
        }

        private void ResetTabStripDrag()
        {
            _tabStripPointerId = -1;
            _tabStripDragged = false;
            _pressedTab = null;
        }

        private float MaxTabStripScrollX()
        {
            if (_tabStrip == null)
                return 0f;
            float content = _tabStrip.contentContainer.layout.width;
            float viewport = _tabStrip.contentViewport.layout.width;
            if (float.IsNaN(content) || float.IsNaN(viewport))
                return 0f;
            return Mathf.Max(0f, content - viewport);
        }

        private TerminalTab TabFromTarget(VisualElement target)
        {
            VisualElement current = target;
            while (current != null && current != _tabStrip)
            {
                TerminalTab tab = current.userData as TerminalTab;
                if (tab != null)
                    return tab;
                current = current.parent;
            }
            return null;
        }

        private static bool HasClassInParents(VisualElement target, string className)
        {
            VisualElement current = target;
            while (current != null)
            {
                if (current.ClassListContains(className))
                    return true;
                current = current.parent;
            }
            return false;
        }

        private void CloseTab(TerminalTab tab)
        {
            int index = _tabs.IndexOf(tab);
            if (index < 0)
                return;

            _tabs.RemoveAt(index);
            tab.Button.RemoveFromHierarchy();
            tab.Host.RemoveFromHierarchy();
            if (tab.Pane != null)
                Destroy(tab.Pane);

            if (_activeTab == tab)
            {
                _activeTab = null;
                if (_tabs.Count > 0)
                    SelectTab(_tabs[Math.Min(index, _tabs.Count - 1)]);
            }
        }

        public void AppendAgentOutput(string processId, string chunk, string backlog)
        {
            if (!_isWorkspace || string.IsNullOrEmpty(processId))
                return;

            TerminalTab tab = null;
            for (int i = 0; i < _tabs.Count; i++)
            {
                if (_tabs[i].ReadOnly && _tabs[i].ProcessId == processId)
                {
                    tab = _tabs[i];
                    break;
                }
            }

            if (tab == null)
                tab = AddTab(processId, true, backlog);
            else
                tab.Pane.AppendReadOnlyOutput(chunk);
        }

        public void CloseAgentOutput(string processId)
        {
            for (int i = _tabs.Count - 1; i >= 0; i--)
            {
                if (_tabs[i].ReadOnly && _tabs[i].ProcessId == processId)
                    CloseTab(_tabs[i]);
            }
        }

        private void AppendReadOnlyOutput(string text)
        {
            if (_emulator == null || string.IsNullOrEmpty(text))
                return;
            _emulator.Feed(Encoding.UTF8.GetBytes(text));
            _dirty = true;
        }

        private void OnPointerDown(PointerDownEvent evt)
        {
            _keepKeyboardFocus = true;
            if (_view != null)
                _view.Focus();

            // Right-click pastes, like a console. (No selection model yet, so no copy.)
            if (evt.button == 1)
            {
                PasteFromClipboard();
                evt.StopPropagation();
            }
        }

        private void PasteFromClipboard()
        {
            if (_session == null)
                return;

            string clip = GUIUtility.systemCopyBuffer;
            if (string.IsNullOrEmpty(clip))
                return;

            // Shells expect CR for line breaks; normalize CRLF/LF to CR.
            clip = clip.Replace("\r\n", "\r").Replace("\n", "\r");

            string payload = clip;
            if (_emulator != null && _emulator.BracketedPasteEnabled)
                payload = "\x1b[200~" + clip + "\x1b[201~";

            if (_view != null && _view.ScrollOffset != 0)
                _view.SetScrollOffset(0, _emulator);

            _session.Write(Bytes(payload));
        }

        private void StartSessionIfNeeded(int columns, int rows)
        {
            if (_sessionStarted)
                return;
            _sessionStarted = true;

            if (!PtySessionFactory.IsSupported)
            {
                _view.ShowMessage("Terminal is not supported on this platform yet.");
                return;
            }

            try
            {
                string cwd = System.IO.Directory.GetCurrentDirectory();
                _session = PtySessionFactory.Create(cwd, columns, rows);
                _session.OutputReceived += OnSessionOutput;
                _session.Exited += OnSessionExited;
                _view.HideMessage();
                _dirty = true;
            }
            catch (Exception ex)
            {
                _view.ShowMessage("Failed to start shell:\n" + ex.Message);
                Debug.LogWarning("[Terminal] PTY start failed: " + ex);
            }
        }

        private void Update()
        {
            // Start the shell deterministically on the first frame rather than waiting for a
            // viewport-size change (which may never fire if the measured size equals 80x24).
            if (!_sessionStarted && _view != null)
                StartSessionIfNeeded(_view.ViewportColumns, _view.ViewportRows);

            bool fed = false;
            byte[] chunk;
            while (_pending.TryDequeue(out chunk))
            {
                if (_emulator != null)
                    _emulator.Feed(chunk);
                fed = true;
            }

            if (fed || _dirty)
            {
                if (_emulator != null && _view != null)
                    _view.Render(_emulator);
                _dirty = false;
            }

            if (_exited && !_exitReported)
            {
                _exitReported = true;
                if (_view != null)
                    _view.ShowMessage("[shell exited: " + _exitCode + "]");
            }

            // A streamed UI rebuild can leave UITK with no focused element. Restore the terminal
            // only in that case; a real dialog/TextField focus must always win.
            if (_keepKeyboardFocus && _view != null && _view.panel != null
                && IsDisplayed(_root)
                && _view.panel.focusController.focusedElement == null)
                _view.Focus();
        }

        private void AttachFocusGuard()
        {
            if (_view == null || _view.panel == null)
                return;

            _documentRoot = _view.panel.visualTree;
            if (_documentRoot != null)
                _documentRoot.RegisterCallback<PointerDownEvent>(OnDocumentPointerDown, TrickleDown.TrickleDown);
            if (IsDisplayed(_root))
            {
                _keepKeyboardFocus = true;
                _view.Focus();
            }
        }

        private static bool IsDisplayed(VisualElement element)
        {
            VisualElement current = element;
            while (current != null)
            {
                if (current.resolvedStyle.display == DisplayStyle.None)
                    return false;
                current = current.parent;
            }
            return element != null;
        }

        private void OnDocumentPointerDown(PointerDownEvent evt)
        {
            VisualElement target = evt.target as VisualElement;
            _keepKeyboardFocus = target != null && (target == _view || _view.Contains(target));
        }

        // ---- PTY callbacks (background thread) ------------------------------------

        private void OnSessionOutput(byte[] data)
        {
            if (data != null && data.Length > 0)
                _pending.Enqueue(data);
        }

        private void OnSessionExited(int code)
        {
            _exitCode = code;
            _exited = true;
        }

        private void OnEmulatorRespond(string reply)
        {
            if (_session != null && !string.IsNullOrEmpty(reply))
                _session.Write(Encoding.UTF8.GetBytes(reply));
        }

        // ---- View callbacks (main thread) -----------------------------------------

        private void OnViewportChanged(int columns, int rows)
        {
            StartSessionIfNeeded(columns, rows);

            if (_emulator != null)
                _emulator.Resize(columns, rows);
            if (_session != null)
                _session.Resize(columns, rows);

            _dirty = true;
        }

        private void OnScrollRequested(int lines)
        {
            if (_view == null || _emulator == null)
                return;
            _view.SetScrollOffset(_view.ScrollOffset + lines, _emulator);
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            if (_session == null)
                return;

            _keepKeyboardFocus = true;
            // Paste shortcuts: Ctrl/Cmd+V and Shift+Insert.
            bool ctrlOrCmd = evt.ctrlKey || evt.commandKey;
            if ((ctrlOrCmd && evt.keyCode == KeyCode.V) ||
                (evt.shiftKey && evt.keyCode == KeyCode.Insert))
            {
                PasteFromClipboard();
                evt.StopImmediatePropagation();
                return;
            }

            byte[] bytes = EncodeKey(evt);
            if (bytes == null)
                return;

            // Typing snaps the view back to the live bottom.
            if (_view.ScrollOffset != 0)
                _view.SetScrollOffset(0, _emulator);

            _session.Write(bytes);
            evt.StopImmediatePropagation();
        }

        // ---- Key encoding ---------------------------------------------------------

        private byte[] EncodeKey(KeyDownEvent evt)
        {
            bool ctrl = evt.ctrlKey || evt.commandKey;
            bool alt = evt.altKey;

            switch (evt.keyCode)
            {
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    return Bytes("\r");
                case KeyCode.Backspace:
                    return alt ? Bytes("\x1b\x7f") : Bytes("\x7f");
                case KeyCode.Tab:
                    return Bytes("\t");
                case KeyCode.Escape:
                    return Bytes("\x1b");
                case KeyCode.UpArrow:
                    return CursorKey('A', evt.shiftKey, alt, ctrl);
                case KeyCode.DownArrow:
                    return CursorKey('B', evt.shiftKey, alt, ctrl);
                case KeyCode.RightArrow:
                    return CursorKey('C', evt.shiftKey, alt, ctrl);
                case KeyCode.LeftArrow:
                    return CursorKey('D', evt.shiftKey, alt, ctrl);
                case KeyCode.Home:
                    return _emulator != null && _emulator.ApplicationCursorKeys ? Bytes("\x1bOH") : Bytes("\x1b[H");
                case KeyCode.End:
                    return _emulator != null && _emulator.ApplicationCursorKeys ? Bytes("\x1bOF") : Bytes("\x1b[F");
                case KeyCode.PageUp:
                    return Bytes("\x1b[5~");
                case KeyCode.PageDown:
                    return Bytes("\x1b[6~");
                case KeyCode.Insert:
                    return Bytes("\x1b[2~");
                case KeyCode.Delete:
                    return Bytes("\x1b[3~");
                case KeyCode.F1:
                    return Bytes("\x1bOP");
                case KeyCode.F2:
                    return Bytes("\x1bOQ");
                case KeyCode.F3:
                    return Bytes("\x1bOR");
                case KeyCode.F4:
                    return Bytes("\x1bOS");
                case KeyCode.F5:
                    return Bytes("\x1b[15~");
                case KeyCode.F6:
                    return Bytes("\x1b[17~");
                case KeyCode.F7:
                    return Bytes("\x1b[18~");
                case KeyCode.F8:
                    return Bytes("\x1b[19~");
                case KeyCode.F9:
                    return Bytes("\x1b[20~");
                case KeyCode.F10:
                    return Bytes("\x1b[21~");
                case KeyCode.F11:
                    return Bytes("\x1b[23~");
                case KeyCode.F12:
                    return Bytes("\x1b[24~");
            }

            if (ctrl)
            {
                // On Windows, UITK commonly reports Ctrl+letter with character == '\0'.
                // keyCode remains reliable, so derive the control byte from it first.
                if (evt.keyCode >= KeyCode.A && evt.keyCode <= KeyCode.Z)
                {
                    if (evt.keyCode == KeyCode.V)
                        return null; // paste is handled by OnKeyDown
                    return new byte[] { (byte)((int)evt.keyCode - (int)KeyCode.A + 1) };
                }

                switch (evt.keyCode)
                {
                    case KeyCode.Space: return new byte[] { 0 };
                    case KeyCode.LeftBracket: return new byte[] { 0x1b };
                    case KeyCode.Backslash: return new byte[] { 0x1c };
                    case KeyCode.RightBracket: return new byte[] { 0x1d };
                    case KeyCode.Caret: return new byte[] { 0x1e };
                    case KeyCode.Underscore:
                    case KeyCode.Minus: return new byte[] { 0x1f };
                }
            }

            char ch = evt.character;
            if (ch == '\0' && alt)
            {
                if (evt.keyCode >= KeyCode.A && evt.keyCode <= KeyCode.Z)
                {
                    char letter = (char)('a' + (int)evt.keyCode - (int)KeyCode.A);
                    ch = evt.shiftKey ? char.ToUpperInvariant(letter) : letter;
                }
                else if (evt.keyCode >= KeyCode.Alpha0 && evt.keyCode <= KeyCode.Alpha9)
                {
                    ch = (char)('0' + (int)evt.keyCode - (int)KeyCode.Alpha0);
                }
            }
            if (ch == '\0')
                return null;

            if (ctrl)
            {
                char lower = char.ToLowerInvariant(ch);

                // Ctrl+V is paste (handled in OnKeyDown); swallow its control-code echo.
                if (ch == '\x16' || lower == 'v')
                    return null;

                // Already a control code (e.g. Ctrl+C delivered as 0x03).
                if (ch >= '\x01' && ch <= '\x1a')
                    return new byte[] { (byte)ch };
                if (lower >= 'a' && lower <= 'z')
                    return new byte[] { (byte)(lower - 'a' + 1) };
                if (ch == ' ')
                    return new byte[] { 0 }; // Ctrl+Space -> NUL
            }

            // Control chars not handled above are owned by the keyCode switch — drop them
            // here so we don't double-send (e.g. Enter arriving again as '\n').
            if (ch < 0x20 || ch == 0x7f)
                return null;

            byte[] body = Bytes(ch.ToString());
            if (alt)
            {
                byte[] withEsc = new byte[body.Length + 1];
                withEsc[0] = 0x1b;
                Array.Copy(body, 0, withEsc, 1, body.Length);
                return withEsc;
            }
            return body;
        }

        private byte[] CursorKey(char final, bool shift, bool alt, bool ctrl)
        {
            int modifier = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);
            if (modifier > 1)
                return Bytes("\x1b[1;" + modifier + final);

            bool app = _emulator != null && _emulator.ApplicationCursorKeys;
            return Bytes((app ? "\x1bO" : "\x1b[") + final);
        }

        private static byte[] Bytes(string s)
        {
            return Encoding.UTF8.GetBytes(s);
        }

        // ---- Visibility / teardown ------------------------------------------------

        public void SetVisible(bool visible)
        {
            if (_root != null)
                _root.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (_isWorkspace)
            {
                if (visible && _activeTab != null)
                    _activeTab.Pane.SetVisible(true);
                return;
            }
            if (visible && _view != null)
            {
                _keepKeyboardFocus = true;
                _view.schedule.Execute(() =>
                {
                    if (_view != null && IsDisplayed(_root))
                        _view.Focus();
                }).ExecuteLater(50);
            }
            else if (!visible)
                _keepKeyboardFocus = false;
        }

        private void OnDestroy()
        {
            if (_isWorkspace)
            {
                if (_tabStrip != null)
                {
                    _tabStrip.UnregisterCallback<PointerDownEvent>(OnTabStripPointerDown, TrickleDown.TrickleDown);
                    _tabStrip.UnregisterCallback<PointerMoveEvent>(OnTabStripPointerMove);
                    _tabStrip.UnregisterCallback<PointerUpEvent>(OnTabStripPointerUp);
                    _tabStrip.UnregisterCallback<PointerCaptureOutEvent>(OnTabStripPointerCaptureOut);
                    _tabStrip.UnregisterCallback<WheelEvent>(OnTabStripWheel);
                }
                for (int i = _tabs.Count - 1; i >= 0; i--)
                {
                    if (_tabs[i].Pane != null)
                        Destroy(_tabs[i].Pane);
                }
                _tabs.Clear();
                return;
            }

            if (_documentRoot != null)
                _documentRoot.UnregisterCallback<PointerDownEvent>(OnDocumentPointerDown, TrickleDown.TrickleDown);
            if (_emulator != null)
                _emulator.Respond -= OnEmulatorRespond;

            if (_session != null)
            {
                _session.OutputReceived -= OnSessionOutput;
                _session.Exited -= OnSessionExited;
                try { _session.Dispose(); } catch (Exception) { }
                _session = null;
            }
        }

        // ---- Remote buffer read (Hermes terminal.read.request) --------------------

        /// <summary>
        /// Serialize the live terminal buffer for the Hermes <c>read_terminal</c> tool, mirroring
        /// Desktop's <c>makeTerminalReader</c> (buffer.ts): absolute line indices into
        /// scrollback+screen, a default window of the visible screen, right-trimmed lines with the
        /// blank tail dropped. Returns the JSON string the backend expects, or <c>null</c> when
        /// there is no live pane (no shell started) so the caller answers with empty text.
        /// Runs on Unity's main thread (the gateway dispatches events there), same as the emulator.
        /// </summary>
        public string ReadScreenJson(int start, int count)
        {
            if (_isWorkspace)
                return _activeTab != null ? _activeTab.Pane.ReadScreenJson(start, count) : null;

            if (_emulator == null || !_sessionStarted)
                return null;

            ScreenBuffer buffer = _emulator.ActiveBuffer;
            if (buffer == null)
                return null;

            int total = buffer.TotalRows;
            int rows = _emulator.Rows;
            // Absolute index of the first visible row (Desktop buf.baseY): scrollback precedes the
            // visible screen in AbsoluteLine indexing.
            int baseY = buffer.ScrollbackCount;

            int from = Math.Max(0, Math.Min(start >= 0 ? start : baseY, total));
            // count provided (incl. 0) -> max(1, count); absent (-1) -> the visible screen height.
            int window = count >= 0 ? Math.Max(1, count) : rows;
            int to = Math.Max(from, Math.Min(from + window, total));

            List<string> lines = new List<string>();
            for (int i = from; i < to; i++)
                lines.Add(LineToText(buffer.AbsoluteLine(i)));

            // Drop trailing blank lines so the agent sees a tight view (Desktop pops empty tail).
            while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0)
                lines.RemoveAt(lines.Count - 1);

            JObject result = new JObject();
            result["total_lines"] = total;
            result["start"] = from;
            result["end"] = to;
            result["viewport_rows"] = rows;
            result["cursor_row"] = baseY + _emulator.CursorRow;
            result["text"] = string.Join("\n", lines);
            return result.ToString(Formatting.None);
        }

        private static string LineToText(TerminalCell[] line)
        {
            if (line == null)
                return string.Empty;

            StringBuilder sb = new StringBuilder(line.Length);
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i].Char;
                sb.Append(c == '\0' ? ' ' : c);
            }

            // Right-trim, matching xterm translateToString(true).
            int end = sb.Length;
            while (end > 0 && sb[end - 1] == ' ')
                end--;
            return sb.ToString(0, end);
        }

    }
}
