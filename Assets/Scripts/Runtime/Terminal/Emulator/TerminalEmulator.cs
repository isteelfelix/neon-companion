using System;
using System.Globalization;
using System.Text;

namespace NeonCompanion.Runtime.Terminal.Emulator
{
    /// <summary>
    /// A VT100/xterm terminal emulator: consumes raw PTY bytes, interprets escape
    /// sequences via <see cref="VtParser"/>, and maintains a <see cref="ScreenBuffer"/>
    /// (main + alternate) with cursor, pen, scroll region, and mode state. The renderer
    /// reads <see cref="ActiveBuffer"/> and the cursor; replies (cursor reports, device
    /// attributes) are surfaced through <see cref="Respond"/> for the controller to write
    /// back to the PTY.
    ///
    /// Threading: drive <see cref="Feed(byte[])"/> from Unity's main thread only.
    /// </summary>
    public sealed class TerminalEmulator : IVtParserHandler
    {
        private const int ScrollbackMax = 2000;

        private readonly VtParser _parser;
        private readonly ScreenBuffer _main;
        private readonly ScreenBuffer _alt;
        private ScreenBuffer _active;
        private bool _usingAlt;

        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        private char[] _charBuf = new char[512];

        // Cursor + deferred-wrap (the cursor "hangs" past the last column until the next print).
        private int _row;
        private int _col;
        private bool _wrapPending;

        // Pen (current graphic rendition).
        private TerminalColor _fg = TerminalColor.Default();
        private TerminalColor _bg = TerminalColor.Default();
        private CellAttributes _attrs = CellAttributes.None;

        // Saved cursor (DECSC / DECRC and ANSI.SYS s/u).
        private int _savedRow;
        private int _savedCol;
        private TerminalColor _savedFg = TerminalColor.Default();
        private TerminalColor _savedBg = TerminalColor.Default();
        private CellAttributes _savedAttrs = CellAttributes.None;
        private bool _savedOrigin;

        // Scroll region (inclusive, 0-based).
        private int _scrollTop;
        private int _scrollBottom;

        // Modes.
        private bool _autoWrap = true;
        private bool _originMode;
        private bool _insertMode;
        private bool _cursorVisible = true;
        private bool _applicationCursorKeys;
        private bool _bracketedPaste;

        private bool[] _tabStops;

        /// <summary>Raised after a <see cref="Feed(byte[])"/> batch mutates the screen.</summary>
        public event Action Changed;

        /// <summary>Raised when the terminal must reply to the shell (DSR, DA, etc.).</summary>
        public event Action<string> Respond;

        public TerminalEmulator(int columns, int rows)
        {
            columns = Math.Max(1, columns);
            rows = Math.Max(1, rows);

            _main = new ScreenBuffer(columns, rows, true, ScrollbackMax);
            _alt = new ScreenBuffer(columns, rows, false, 0);
            _active = _main;

            _scrollTop = 0;
            _scrollBottom = rows - 1;
            InitTabStops(columns);

            _parser = new VtParser(this);
        }

        public int Columns { get { return _active.Columns; } }
        public int Rows { get { return _active.Rows; } }
        public int CursorRow { get { return _row; } }
        public int CursorCol { get { return _col; } }
        public bool CursorVisible { get { return _cursorVisible; } }
        public bool UsingAlternateScreen { get { return _usingAlt; } }
        public bool ApplicationCursorKeys { get { return _applicationCursorKeys; } }
        public bool BracketedPasteEnabled { get { return _bracketedPaste; } }
        public ScreenBuffer ActiveBuffer { get { return _active; } }
        public int ScrollbackCount { get { return _active.ScrollbackCount; } }

        // ---- Input ----------------------------------------------------------------

        public void Feed(byte[] data)
        {
            if (data == null || data.Length == 0)
                return;
            Feed(data, data.Length);
        }

        public void Feed(byte[] data, int length)
        {
            if (data == null || length <= 0)
                return;

            int needed = _decoder.GetCharCount(data, 0, length, false);
            if (needed <= 0)
            {
                // Bytes consumed into a pending multibyte sequence — wait for more.
                return;
            }
            if (_charBuf.Length < needed)
                _charBuf = new char[needed];

            int produced = _decoder.GetChars(data, 0, length, _charBuf, 0, false);
            for (int i = 0; i < produced; i++)
                _parser.Process(_charBuf[i]);

            RaiseChanged();
        }

        public void Resize(int columns, int rows)
        {
            columns = Math.Max(1, columns);
            rows = Math.Max(1, rows);
            if (columns == Columns && rows == Rows)
                return;

            bool fullRegion = _scrollTop == 0 && _scrollBottom == Rows - 1;

            _main.Resize(columns, rows, _bg);
            _alt.Resize(columns, rows, _bg);

            if (fullRegion)
            {
                _scrollTop = 0;
                _scrollBottom = rows - 1;
            }
            else
            {
                _scrollTop = Clamp(_scrollTop, 0, rows - 1);
                _scrollBottom = Clamp(_scrollBottom, _scrollTop, rows - 1);
            }

            _row = Clamp(_row, 0, rows - 1);
            _col = Clamp(_col, 0, columns - 1);
            _wrapPending = false;
            InitTabStops(columns);
            RaiseChanged();
        }

        // ---- IVtParserHandler -----------------------------------------------------

        public void Print(char c)
        {
            if (_wrapPending && _autoWrap)
            {
                _col = 0;
                CursorDownScroll();
                _wrapPending = false;
            }

            if (_insertMode)
                _active.InsertChars(_row, _col, 1, _bg);

            TerminalCell cell = new TerminalCell();
            cell.Char = c;
            cell.Foreground = _fg;
            cell.Background = _bg;
            cell.Attributes = _attrs;
            _active.SetCell(_row, _col, cell);

            if (_col + 1 >= Columns)
            {
                if (_autoWrap)
                    _wrapPending = true;
            }
            else
            {
                _col++;
            }
        }

        public void Execute(char control)
        {
            switch (control)
            {
                case '\x07': // BEL
                    break;
                case '\x08': // BS
                    _wrapPending = false;
                    if (_col > 0)
                        _col--;
                    break;
                case '\x09': // HT
                    HorizontalTab();
                    break;
                case '\x0a': // LF
                case '\x0b': // VT
                case '\x0c': // FF
                    _wrapPending = false;
                    CursorDownScroll();
                    break;
                case '\x0d': // CR
                    _wrapPending = false;
                    _col = 0;
                    break;
            }
        }

        public void EscDispatch(char finalByte, char intermediate)
        {
            if (intermediate != '\0')
            {
                // Charset designation (ESC ( B etc.) and similar — not translated in v1.
                return;
            }

            switch (finalByte)
            {
                case 'c': // RIS — full reset
                    FullReset();
                    break;
                case 'D': // IND — index (down, scroll)
                    _wrapPending = false;
                    CursorDownScroll();
                    break;
                case 'M': // RI — reverse index (up, scroll)
                    _wrapPending = false;
                    CursorUpScroll();
                    break;
                case 'E': // NEL — next line
                    _wrapPending = false;
                    _col = 0;
                    CursorDownScroll();
                    break;
                case '7': // DECSC
                    SaveCursor();
                    break;
                case '8': // DECRC
                    RestoreCursor();
                    break;
                case 'H': // HTS — set tab stop at cursor
                    if (_tabStops != null && _col >= 0 && _col < _tabStops.Length)
                        _tabStops[_col] = true;
                    break;
            }
        }

        public void CsiDispatch(char finalByte, int[] parameters, int paramCount, char prefix, char intermediate)
        {
            switch (finalByte)
            {
                case 'A':
                    MoveCursorVertical(-ArgOr(parameters, paramCount, 0, 1));
                    break;
                case 'B':
                    MoveCursorVertical(ArgOr(parameters, paramCount, 0, 1));
                    break;
                case 'C':
                    _wrapPending = false;
                    _col = Clamp(_col + ArgOr(parameters, paramCount, 0, 1), 0, Columns - 1);
                    break;
                case 'D':
                    _wrapPending = false;
                    _col = Clamp(_col - ArgOr(parameters, paramCount, 0, 1), 0, Columns - 1);
                    break;
                case 'E':
                    _col = 0;
                    MoveCursorVertical(ArgOr(parameters, paramCount, 0, 1));
                    break;
                case 'F':
                    _col = 0;
                    MoveCursorVertical(-ArgOr(parameters, paramCount, 0, 1));
                    break;
                case 'G':
                case '`':
                    _wrapPending = false;
                    _col = Clamp(ArgOr(parameters, paramCount, 0, 1) - 1, 0, Columns - 1);
                    break;
                case 'd':
                    SetCursorPosition(ArgOr(parameters, paramCount, 0, 1) - 1, _col);
                    break;
                case 'H':
                case 'f':
                    SetCursorPosition(ArgOr(parameters, paramCount, 0, 1) - 1, ArgOr(parameters, paramCount, 1, 1) - 1);
                    break;
                case 'J':
                    EraseInDisplay(Arg(parameters, paramCount, 0, 0));
                    break;
                case 'K':
                    EraseInLine(Arg(parameters, paramCount, 0, 0));
                    break;
                case 'L':
                    _active.InsertLines(_row, ArgOr(parameters, paramCount, 0, 1), _scrollTop, _scrollBottom, _bg);
                    break;
                case 'M':
                    _active.DeleteLines(_row, ArgOr(parameters, paramCount, 0, 1), _scrollTop, _scrollBottom, _bg);
                    break;
                case '@':
                    _active.InsertChars(_row, _col, ArgOr(parameters, paramCount, 0, 1), _bg);
                    break;
                case 'P':
                    _active.DeleteChars(_row, _col, ArgOr(parameters, paramCount, 0, 1), _bg);
                    break;
                case 'X':
                    _active.EraseChars(_row, _col, ArgOr(parameters, paramCount, 0, 1), _bg);
                    break;
                case 'S':
                    _active.ScrollUp(_scrollTop, _scrollBottom, ArgOr(parameters, paramCount, 0, 1), _bg);
                    break;
                case 'T':
                    _active.ScrollDown(_scrollTop, _scrollBottom, ArgOr(parameters, paramCount, 0, 1), _bg);
                    break;
                case 'm':
                    ApplySgr(parameters, paramCount);
                    break;
                case 'r':
                    SetScrollRegion(parameters, paramCount);
                    break;
                case 'h':
                    SetMode(parameters, paramCount, prefix, true);
                    break;
                case 'l':
                    SetMode(parameters, paramCount, prefix, false);
                    break;
                case 's':
                    if (prefix == '\0')
                        SaveCursor();
                    break;
                case 'u':
                    if (prefix == '\0')
                        RestoreCursor();
                    break;
                case 'n':
                    DeviceStatusReport(Arg(parameters, paramCount, 0, 0));
                    break;
                case 'c':
                    if (Respond != null)
                        Respond("\x1b[?1;2c"); // VT100 with Advanced Video Option
                    break;
            }
        }

        public void OscDispatch(int command, string data)
        {
            // OSC 0/1/2 set the window/icon title — surfaced later if the UI wants it.
            // All other OSC commands (hyperlinks, clipboard, color queries) are ignored in v1.
        }

        // ---- Cursor / scrolling ---------------------------------------------------

        private void CursorDownScroll()
        {
            if (_row == _scrollBottom)
                _active.ScrollUp(_scrollTop, _scrollBottom, 1, _bg);
            else if (_row < Rows - 1)
                _row++;
        }

        private void CursorUpScroll()
        {
            if (_row == _scrollTop)
                _active.ScrollDown(_scrollTop, _scrollBottom, 1, _bg);
            else if (_row > 0)
                _row--;
        }

        private void MoveCursorVertical(int delta)
        {
            _wrapPending = false;
            int topLimit = _originMode ? _scrollTop : 0;
            int bottomLimit = _originMode ? _scrollBottom : Rows - 1;
            _row = Clamp(_row + delta, topLimit, bottomLimit);
        }

        private void SetCursorPosition(int row, int col)
        {
            _wrapPending = false;
            if (_originMode)
            {
                _row = Clamp(_scrollTop + row, _scrollTop, _scrollBottom);
            }
            else
            {
                _row = Clamp(row, 0, Rows - 1);
            }
            _col = Clamp(col, 0, Columns - 1);
        }

        private void HorizontalTab()
        {
            _wrapPending = false;
            int next = _col + 1;
            while (next < Columns && (_tabStops == null || !_tabStops[next]))
                next++;
            _col = Math.Min(next, Columns - 1);
        }

        private void SetScrollRegion(int[] p, int count)
        {
            int top = ArgOr(p, count, 0, 1) - 1;
            int bottom = ArgOr(p, count, 1, Rows) - 1;
            if (top < 0)
                top = 0;
            if (bottom >= Rows)
                bottom = Rows - 1;
            if (top >= bottom)
            {
                _scrollTop = 0;
                _scrollBottom = Rows - 1;
            }
            else
            {
                _scrollTop = top;
                _scrollBottom = bottom;
            }
            // DECSTBM homes the cursor (origin-aware).
            SetCursorPosition(0, 0);
        }

        // ---- Erase ----------------------------------------------------------------

        private void EraseInDisplay(int mode)
        {
            switch (mode)
            {
                case 0: // cursor to end of screen
                    _active.EraseInLine(_row, _col, Columns - 1, _bg);
                    for (int r = _row + 1; r < Rows; r++)
                        _active.ClearLine(r, _bg);
                    break;
                case 1: // start of screen to cursor
                    for (int r = 0; r < _row; r++)
                        _active.ClearLine(r, _bg);
                    _active.EraseInLine(_row, 0, _col, _bg);
                    break;
                case 2: // whole screen
                    _active.ClearAll(_bg);
                    break;
                case 3: // scrollback
                    _active.ClearScrollback();
                    break;
            }
        }

        private void EraseInLine(int mode)
        {
            switch (mode)
            {
                case 0:
                    _active.EraseInLine(_row, _col, Columns - 1, _bg);
                    break;
                case 1:
                    _active.EraseInLine(_row, 0, _col, _bg);
                    break;
                case 2:
                    _active.EraseInLine(_row, 0, Columns - 1, _bg);
                    break;
            }
        }

        // ---- SGR (colors / attributes) --------------------------------------------

        private void ApplySgr(int[] p, int count)
        {
            if (count == 0)
            {
                ResetPen();
                return;
            }

            int i = 0;
            while (i < count)
            {
                int code = p[i];
                switch (code)
                {
                    case 0:
                        ResetPen();
                        break;
                    case 1:
                        _attrs |= CellAttributes.Bold;
                        break;
                    case 2:
                        _attrs |= CellAttributes.Dim;
                        break;
                    case 3:
                        _attrs |= CellAttributes.Italic;
                        break;
                    case 4:
                        _attrs |= CellAttributes.Underline;
                        break;
                    case 5:
                    case 6:
                        _attrs |= CellAttributes.Blink;
                        break;
                    case 7:
                        _attrs |= CellAttributes.Inverse;
                        break;
                    case 8:
                        _attrs |= CellAttributes.Hidden;
                        break;
                    case 9:
                        _attrs |= CellAttributes.Strikethrough;
                        break;
                    case 22:
                        _attrs &= ~(CellAttributes.Bold | CellAttributes.Dim);
                        break;
                    case 23:
                        _attrs &= ~CellAttributes.Italic;
                        break;
                    case 24:
                        _attrs &= ~CellAttributes.Underline;
                        break;
                    case 25:
                        _attrs &= ~CellAttributes.Blink;
                        break;
                    case 27:
                        _attrs &= ~CellAttributes.Inverse;
                        break;
                    case 28:
                        _attrs &= ~CellAttributes.Hidden;
                        break;
                    case 29:
                        _attrs &= ~CellAttributes.Strikethrough;
                        break;
                    case 38:
                        i = ParseExtendedColor(p, count, i, true);
                        break;
                    case 39:
                        _fg = TerminalColor.Default();
                        break;
                    case 48:
                        i = ParseExtendedColor(p, count, i, false);
                        break;
                    case 49:
                        _bg = TerminalColor.Default();
                        break;
                    default:
                        ApplySgrColorRange(code);
                        break;
                }
                i++;
            }
        }

        private void ApplySgrColorRange(int code)
        {
            if (code >= 30 && code <= 37)
                _fg = TerminalColor.Indexed(code - 30);
            else if (code >= 40 && code <= 47)
                _bg = TerminalColor.Indexed(code - 40);
            else if (code >= 90 && code <= 97)
                _fg = TerminalColor.Indexed(code - 90 + 8);
            else if (code >= 100 && code <= 107)
                _bg = TerminalColor.Indexed(code - 100 + 8);
        }

        // Returns the index of the LAST consumed parameter (caller increments past it).
        private int ParseExtendedColor(int[] p, int count, int i, bool foreground)
        {
            if (i + 1 >= count)
                return i;

            int mode = p[i + 1];
            if (mode == 5 && i + 2 < count)
            {
                TerminalColor c = TerminalColor.Indexed(p[i + 2]);
                if (foreground) _fg = c; else _bg = c;
                return i + 2;
            }
            if (mode == 2 && i + 4 < count)
            {
                TerminalColor c = TerminalColor.Rgb((byte)(p[i + 2] & 0xff), (byte)(p[i + 3] & 0xff), (byte)(p[i + 4] & 0xff));
                if (foreground) _fg = c; else _bg = c;
                return i + 4;
            }
            return i + 1;
        }

        private void ResetPen()
        {
            _fg = TerminalColor.Default();
            _bg = TerminalColor.Default();
            _attrs = CellAttributes.None;
        }

        // ---- Modes ----------------------------------------------------------------

        private void SetMode(int[] p, int count, char prefix, bool enable)
        {
            for (int i = 0; i < count; i++)
            {
                int code = p[i];
                if (prefix == '?')
                    SetDecPrivateMode(code, enable);
                else
                    SetAnsiMode(code, enable);
            }
        }

        private void SetAnsiMode(int code, bool enable)
        {
            switch (code)
            {
                case 4: // IRM — insert/replace
                    _insertMode = enable;
                    break;
            }
        }

        private void SetDecPrivateMode(int code, bool enable)
        {
            switch (code)
            {
                case 1: // DECCKM — application cursor keys
                    _applicationCursorKeys = enable;
                    break;
                case 6: // DECOM — origin mode
                    _originMode = enable;
                    SetCursorPosition(0, 0);
                    break;
                case 7: // DECAWM — autowrap
                    _autoWrap = enable;
                    break;
                case 25: // DECTCEM — cursor visibility
                    _cursorVisible = enable;
                    break;
                case 47:
                case 1047:
                    SwitchAlternateScreen(enable, false);
                    break;
                case 1049:
                    SwitchAlternateScreen(enable, true);
                    break;
                case 2004: // bracketed paste
                    _bracketedPaste = enable;
                    break;
            }
        }

        private void SwitchAlternateScreen(bool toAlt, bool saveRestoreCursor)
        {
            if (toAlt)
            {
                if (saveRestoreCursor)
                    SaveCursor();
                _usingAlt = true;
                _active = _alt;
                _scrollTop = 0;
                _scrollBottom = Rows - 1;
                _active.ClearScrollback();
                _active.ClearAll(_bg);
                _row = 0;
                _col = 0;
                _wrapPending = false;
            }
            else
            {
                _usingAlt = false;
                _active = _main;
                _scrollTop = 0;
                _scrollBottom = Rows - 1;
                if (saveRestoreCursor)
                    RestoreCursor();
                _wrapPending = false;
            }
        }

        // ---- Save / restore / reset ----------------------------------------------

        private void SaveCursor()
        {
            _savedRow = _row;
            _savedCol = _col;
            _savedFg = _fg;
            _savedBg = _bg;
            _savedAttrs = _attrs;
            _savedOrigin = _originMode;
        }

        private void RestoreCursor()
        {
            _row = Clamp(_savedRow, 0, Rows - 1);
            _col = Clamp(_savedCol, 0, Columns - 1);
            _fg = _savedFg;
            _bg = _savedBg;
            _attrs = _savedAttrs;
            _originMode = _savedOrigin;
            _wrapPending = false;
        }

        private void FullReset()
        {
            _usingAlt = false;
            _active = _main;
            ResetPen();
            _row = 0;
            _col = 0;
            _wrapPending = false;
            _scrollTop = 0;
            _scrollBottom = Rows - 1;
            _autoWrap = true;
            _originMode = false;
            _insertMode = false;
            _cursorVisible = true;
            _applicationCursorKeys = false;
            _bracketedPaste = false;
            InitTabStops(Columns);
            _main.ClearAll(_bg);
            _main.ClearScrollback();
            _alt.ClearAll(_bg);
        }

        private void DeviceStatusReport(int code)
        {
            if (Respond == null)
                return;
            if (code == 5)
            {
                Respond("\x1b[0n"); // terminal OK
            }
            else if (code == 6)
            {
                int reportRow = _row + 1;
                int reportCol = _col + 1;
                Respond("\x1b[" + reportRow.ToString(CultureInfo.InvariantCulture) + ";" +
                        reportCol.ToString(CultureInfo.InvariantCulture) + "R");
            }
        }

        // ---- Helpers --------------------------------------------------------------

        private void InitTabStops(int columns)
        {
            _tabStops = new bool[columns];
            for (int i = 0; i < columns; i++)
                _tabStops[i] = (i % 8) == 0;
        }

        private void RaiseChanged()
        {
            Action handler = Changed;
            if (handler != null)
                handler();
        }

        private static int Arg(int[] p, int count, int index, int def)
        {
            if (index < count)
                return p[index];
            return def;
        }

        private static int ArgOr(int[] p, int count, int index, int def)
        {
            if (index < count && p[index] != 0)
                return p[index];
            return def;
        }

        private static int Clamp(int v, int min, int max)
        {
            if (max < min)
                return min;
            if (v < min)
                return min;
            if (v > max)
                return max;
            return v;
        }
    }
}
