using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Terminal;
using NeonCompanion.Runtime.Terminal.Emulator;
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

        private ProcessExecutionService _processService;
        private PersistentShellService _persistentShell;
        private bool _isExecuting;

        // ---- Lifecycle ------------------------------------------------------------

        public void Initialize(VisualElement terminalRoot)
        {
            _root = terminalRoot;
            if (_root == null)
                return;

            _root.Clear();

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
            _view.schedule.Execute(() => { if (_view != null) _view.Focus(); }).ExecuteLater(50);

            ResolveService();
        }

        private void OnPointerDown(PointerDownEvent evt)
        {
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

            // Paste shortcuts: Ctrl/Cmd+V and Shift+Insert.
            bool ctrlOrCmd = evt.ctrlKey || evt.commandKey;
            if ((ctrlOrCmd && evt.keyCode == KeyCode.V) ||
                (evt.shiftKey && evt.keyCode == KeyCode.Insert))
            {
                PasteFromClipboard();
                evt.StopPropagation();
                return;
            }

            byte[] bytes = EncodeKey(evt);
            if (bytes == null)
                return;

            // Typing snaps the view back to the live bottom.
            if (_view.ScrollOffset != 0)
                _view.SetScrollOffset(0, _emulator);

            _session.Write(bytes);
            evt.StopPropagation();
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
            if (visible && _view != null)
                _view.schedule.Execute(() => { if (_view != null) _view.Focus(); }).ExecuteLater(50);
        }

        private void OnDestroy()
        {
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
