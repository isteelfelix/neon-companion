using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Core;
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
    /// Note: <see cref="ExecuteRemoteCommand"/> (the Hermes <c>terminal.execute</c> bridge)
    /// deliberately stays on the one-shot <see cref="ProcessExecutionService"/> — it needs a
    /// structured stdout/stderr/exit-code result, not an interactive stream.
    /// </summary>
    public sealed class TerminalController : MonoBehaviour
    {
        private const int FontSize = 12;

        private sealed class TerminalTab
        {
            public string Id;
            public string ProcessId;
            public bool ReadOnly;
            public Button Button;
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

        private ProcessExecutionService _processService;
        private PersistentShellService _persistentShell;
        private bool _isExecuting;
        private bool _isWorkspace;
        private VisualElement _tabBar;
        private VisualElement _paneHost;
        private readonly List<TerminalTab> _tabs = new List<TerminalTab>();
        private TerminalTab _activeTab;

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
            _tabBar.style.flexDirection = FlexDirection.Row;
            _tabBar.style.flexShrink = 0;

            Button add = new Button(AddUserTab);
            add.text = "+";
            add.tooltip = LocalizationExtensions.Get("terminal.new", "New terminal");
            add.style.flexShrink = 0;
            _tabBar.Add(add);

            _paneHost = new VisualElement();
            _paneHost.name = "terminal-panes";
            _paneHost.style.flexGrow = 1;
            _paneHost.style.flexDirection = FlexDirection.Column;

            _root.Add(_tabBar);
            _root.Add(_paneHost);
            AddUserTab();
            ResolveService();
        }

        private void InitializePane(VisualElement terminalRoot, bool startShell)
        {
            _root = terminalRoot;

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
            ResolveService();
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
            tab.Host.style.flexGrow = 1;
            tab.Host.style.display = DisplayStyle.None;
            _paneHost.Add(tab.Host);

            tab.Pane = gameObject.AddComponent<TerminalController>();
            tab.Pane.InitializePane(tab.Host, !readOnly);
            if (readOnly && !string.IsNullOrEmpty(initialOutput))
                tab.Pane.AppendReadOnlyOutput(initialOutput);

            var button = new Button(() => SelectTab(tab));
            button.text = readOnly ? "agent " + ShortId(processId) : "shell " + (_tabs.Count + 1);
            button.tooltip = readOnly
                ? LocalizationExtensions.Get("terminal.agent_readonly", "Agent output (read-only)")
                : LocalizationExtensions.Get("terminal.switch", "Switch terminal");
            tab.Button = button;

            var close = new Button();
            close.text = "×";
            close.tooltip = LocalizationExtensions.Get("terminal.close", "Close terminal");
            close.RegisterCallback<ClickEvent>(evt =>
            {
                evt.StopPropagation();
                CloseTab(tab);
            });
            button.Add(close);
            _tabBar.Insert(_tabBar.childCount - 1, button);
            _tabs.Add(tab);
            if (!readOnly || _activeTab == null)
                SelectTab(tab);
            return tab;
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

            // UITK can move focus to a rebuilt chat TextField while streamed messages are
            // re-rendered. Keep terminal ownership until the user actually points elsewhere.
            if (_keepKeyboardFocus && _view != null && _view.panel != null
                && _root != null && _root.resolvedStyle.display != DisplayStyle.None
                && _view.panel.focusController.focusedElement != _view)
                _view.Focus();
        }

        private void AttachFocusGuard()
        {
            if (_view == null || _view.panel == null)
                return;

            _documentRoot = _view.panel.visualTree;
            if (_documentRoot != null)
                _documentRoot.RegisterCallback<PointerDownEvent>(OnDocumentPointerDown, TrickleDown.TrickleDown);
            if (_root != null && _root.resolvedStyle.display != DisplayStyle.None)
            {
                _keepKeyboardFocus = true;
                _view.Focus();
            }
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
                    return Bytes("\x7f");
                case KeyCode.Tab:
                    return Bytes("\t");
                case KeyCode.Escape:
                    return Bytes("\x1b");
                case KeyCode.UpArrow:
                    return CursorKey('A');
                case KeyCode.DownArrow:
                    return CursorKey('B');
                case KeyCode.RightArrow:
                    return CursorKey('C');
                case KeyCode.LeftArrow:
                    return CursorKey('D');
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

        private byte[] CursorKey(char final)
        {
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
                _view.schedule.Execute(() => { if (_view != null) _view.Focus(); }).ExecuteLater(50);
            }
            else if (!visible)
                _keepKeyboardFocus = false;
        }

        private void OnDestroy()
        {
            if (_isWorkspace)
            {
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

        // ---- Remote one-shot execution (Hermes terminal.execute) ------------------

        private void ResolveService()
        {
            if (_processService != null && _persistentShell != null)
                return;

            var bootstrap = FindAnyObjectByType<AppBootstrap>();
            if (bootstrap == null || bootstrap.App == null || bootstrap.App.Services == null)
                return;

            try
            {
                if (_processService == null)
                    _processService = bootstrap.App.Services.GetRequired<ProcessExecutionService>();
            }
            catch (Exception)
            {
                _processService = null;
            }

            try
            {
                if (_persistentShell == null)
                    _persistentShell = bootstrap.App.Services.GetRequired<PersistentShellService>();
            }
            catch (Exception)
            {
                _persistentShell = null;
            }
        }

        /// <summary>
        /// Runs a command for the Hermes <c>terminal.execute</c> bridge. When
        /// <paramref name="persistent"/> is true it goes to the long-lived agent shell
        /// (state survives across commands); otherwise it's a clean one-shot.
        /// </summary>
        public async Task<ProcessResult> ExecuteRemoteCommand(string command, int timeoutMs = 30000, bool persistent = false)
        {
            ResolveService();

            if (persistent)
            {
                if (_persistentShell == null)
                {
                    return new ProcessResult
                    {
                        exitCode = -1,
                        stderr = "PersistentShellService not available"
                    };
                }
                // The persistent shell serializes commands internally via its own gate.
                return await _persistentShell.ExecuteAsync(command, timeoutMs);
            }

            if (_processService == null)
            {
                return new ProcessResult
                {
                    exitCode = -1,
                    stderr = "ProcessExecutionService not available"
                };
            }

            if (_isExecuting)
            {
                return new ProcessResult
                {
                    exitCode = -1,
                    stderr = "Terminal is busy"
                };
            }

            _isExecuting = true;
            try
            {
                return await _processService.ExecuteAsync(command, timeoutMs);
            }
            finally
            {
                _isExecuting = false;
            }
        }
    }
}
