using System;
using System.Collections.Generic;

namespace NeonCompanion.Runtime.Terminal.Emulator
{
    /// <summary>
    /// One terminal screen: a grid of exactly <see cref="Rows"/> visible lines, each
    /// <see cref="Columns"/> cells wide, plus an optional scrollback history of lines that
    /// have scrolled off the top. The main screen keeps scrollback; the alternate screen
    /// (used by full-screen apps like vim) does not.
    ///
    /// All mutation methods take the current background color so that newly exposed cells
    /// inherit the active pen's background, matching real terminals.
    /// Not thread-safe: the emulator drives it from Unity's main thread only.
    /// </summary>
    public sealed class ScreenBuffer
    {
        private readonly List<TerminalCell[]> _lines = new List<TerminalCell[]>();
        private readonly List<TerminalCell[]> _scrollback = new List<TerminalCell[]>();
        private readonly bool _scrollbackEnabled;
        private readonly int _scrollbackMax;

        public int Columns { get; private set; }
        public int Rows { get; private set; }

        public int ScrollbackCount
        {
            get { return _scrollback.Count; }
        }

        /// <summary>Total addressable lines: scrollback followed by the visible screen.</summary>
        public int TotalRows
        {
            get { return _scrollback.Count + _lines.Count; }
        }

        public ScreenBuffer(int columns, int rows, bool scrollbackEnabled, int scrollbackMax)
        {
            Columns = Math.Max(1, columns);
            Rows = Math.Max(1, rows);
            _scrollbackEnabled = scrollbackEnabled;
            _scrollbackMax = Math.Max(0, scrollbackMax);

            for (int i = 0; i < Rows; i++)
                _lines.Add(NewBlankLine(TerminalColor.Default()));
        }

        public TerminalCell[] NewBlankLine(TerminalColor bg)
        {
            TerminalCell[] line = new TerminalCell[Columns];
            for (int i = 0; i < Columns; i++)
                line[i] = TerminalCell.Blank(TerminalColor.Default(), bg);
            return line;
        }

        /// <summary>Visible line by row index (0 = top of screen).</summary>
        public TerminalCell[] Line(int row)
        {
            if (row < 0 || row >= _lines.Count)
                return null;
            return _lines[row];
        }

        /// <summary>
        /// Addressable line spanning scrollback + screen: 0..ScrollbackCount-1 are history,
        /// then the visible rows. Used by the renderer when the viewport is scrolled up.
        /// </summary>
        public TerminalCell[] AbsoluteLine(int index)
        {
            if (index < 0)
                return null;
            if (index < _scrollback.Count)
                return _scrollback[index];
            int screenRow = index - _scrollback.Count;
            if (screenRow < _lines.Count)
                return _lines[screenRow];
            return null;
        }

        public void SetCell(int row, int col, TerminalCell cell)
        {
            if (row < 0 || row >= _lines.Count || col < 0 || col >= Columns)
                return;
            _lines[row][col] = cell;
        }

        /// <summary>
        /// Scroll the inclusive region [top, bottom] up by <paramref name="n"/> lines.
        /// When the region starts at the top of the screen and scrollback is enabled, the
        /// evicted lines are pushed into history; otherwise they are discarded. Freed lines
        /// at the bottom of the region are filled with blanks using <paramref name="bg"/>.
        /// </summary>
        public void ScrollUp(int top, int bottom, int n, TerminalColor bg)
        {
            if (n <= 0)
                return;
            top = Clamp(top, 0, Rows - 1);
            bottom = Clamp(bottom, 0, Rows - 1);
            if (bottom < top)
                return;

            int regionHeight = bottom - top + 1;
            if (n > regionHeight)
                n = regionHeight;

            // Lines are preserved in scrollback only when the whole screen scrolls; a
            // partial scroll region (e.g. set by an app) discards its evicted lines.
            bool toScrollback = _scrollbackEnabled && top == 0 && bottom == Rows - 1;

            for (int i = 0; i < n; i++)
            {
                TerminalCell[] evicted = _lines[top];
                _lines.RemoveAt(top);
                if (toScrollback)
                    PushScrollback(evicted);
                _lines.Insert(bottom, NewBlankLine(bg));
            }
        }

        /// <summary>Scroll the inclusive region [top, bottom] down by <paramref name="n"/> lines (no scrollback).</summary>
        public void ScrollDown(int top, int bottom, int n, TerminalColor bg)
        {
            if (n <= 0)
                return;
            top = Clamp(top, 0, Rows - 1);
            bottom = Clamp(bottom, 0, Rows - 1);
            if (bottom < top)
                return;

            int regionHeight = bottom - top + 1;
            if (n > regionHeight)
                n = regionHeight;

            for (int i = 0; i < n; i++)
            {
                _lines.RemoveAt(bottom);
                _lines.Insert(top, NewBlankLine(bg));
            }
        }

        /// <summary>Insert <paramref name="n"/> blank lines at <paramref name="row"/> within [top, bottom].</summary>
        public void InsertLines(int row, int n, int top, int bottom, TerminalColor bg)
        {
            if (row < top || row > bottom)
                return;
            ScrollDown(row, bottom, n, bg);
        }

        /// <summary>Delete <paramref name="n"/> lines at <paramref name="row"/> within [top, bottom].</summary>
        public void DeleteLines(int row, int n, int top, int bottom, TerminalColor bg)
        {
            if (row < top || row > bottom)
                return;
            ScrollUp(row, bottom, n, bg);
        }

        public void InsertChars(int row, int col, int n, TerminalColor bg)
        {
            TerminalCell[] line = Line(row);
            if (line == null || n <= 0)
                return;
            for (int c = Columns - 1; c >= col + n; c--)
                line[c] = line[c - n];
            int end = Math.Min(Columns, col + n);
            for (int c = col; c < end; c++)
                line[c] = TerminalCell.Blank(TerminalColor.Default(), bg);
        }

        public void DeleteChars(int row, int col, int n, TerminalColor bg)
        {
            TerminalCell[] line = Line(row);
            if (line == null || n <= 0)
                return;
            for (int c = col; c < Columns; c++)
            {
                int src = c + n;
                line[c] = src < Columns ? line[src] : TerminalCell.Blank(TerminalColor.Default(), bg);
            }
        }

        public void EraseChars(int row, int col, int n, TerminalColor bg)
        {
            TerminalCell[] line = Line(row);
            if (line == null || n <= 0)
                return;
            int end = Math.Min(Columns, col + n);
            for (int c = col; c < end; c++)
                line[c] = TerminalCell.Blank(TerminalColor.Default(), bg);
        }

        /// <summary>Erase a span of a single line, inclusive [fromCol, toCol].</summary>
        public void EraseInLine(int row, int fromCol, int toCol, TerminalColor bg)
        {
            TerminalCell[] line = Line(row);
            if (line == null)
                return;
            fromCol = Clamp(fromCol, 0, Columns - 1);
            toCol = Clamp(toCol, 0, Columns - 1);
            for (int c = fromCol; c <= toCol; c++)
                line[c] = TerminalCell.Blank(TerminalColor.Default(), bg);
        }

        /// <summary>Replace an entire visible row with blanks.</summary>
        public void ClearLine(int row, TerminalColor bg)
        {
            if (row < 0 || row >= _lines.Count)
                return;
            _lines[row] = NewBlankLine(bg);
        }

        public void ClearAll(TerminalColor bg)
        {
            for (int r = 0; r < _lines.Count; r++)
                _lines[r] = NewBlankLine(bg);
        }

        public void ClearScrollback()
        {
            _scrollback.Clear();
        }

        /// <summary>
        /// Resize to a new geometry. Columns are truncated/padded per line; rows are added
        /// at the bottom (pulling from scrollback when growing) or pushed to scrollback when
        /// shrinking. Cursor reconciliation is the emulator's responsibility.
        /// </summary>
        public void Resize(int columns, int rows, TerminalColor bg)
        {
            columns = Math.Max(1, columns);
            rows = Math.Max(1, rows);

            if (columns != Columns)
            {
                ResizeColumns(columns, bg);
                ResizeScrollbackColumns(columns, bg);
                Columns = columns;
            }

            if (rows > Rows)
            {
                int toAdd = rows - Rows;
                for (int i = 0; i < toAdd; i++)
                {
                    if (_scrollbackEnabled && _scrollback.Count > 0)
                    {
                        TerminalCell[] restored = _scrollback[_scrollback.Count - 1];
                        _scrollback.RemoveAt(_scrollback.Count - 1);
                        _lines.Insert(0, restored);
                    }
                    else
                    {
                        _lines.Add(NewBlankLine(bg));
                    }
                }
            }
            else if (rows < Rows)
            {
                int toRemove = Rows - rows;
                for (int i = 0; i < toRemove; i++)
                {
                    TerminalCell[] top = _lines[0];
                    _lines.RemoveAt(0);
                    if (_scrollbackEnabled)
                        PushScrollback(top);
                }
            }

            Rows = rows;
        }

        private void ResizeColumns(int columns, TerminalColor bg)
        {
            for (int r = 0; r < _lines.Count; r++)
                _lines[r] = ResizeLine(_lines[r], columns, bg);
        }

        private void ResizeScrollbackColumns(int columns, TerminalColor bg)
        {
            for (int r = 0; r < _scrollback.Count; r++)
                _scrollback[r] = ResizeLine(_scrollback[r], columns, bg);
        }

        private static TerminalCell[] ResizeLine(TerminalCell[] line, int columns, TerminalColor bg)
        {
            TerminalCell[] resized = new TerminalCell[columns];
            int copy = Math.Min(columns, line.Length);
            for (int c = 0; c < copy; c++)
                resized[c] = line[c];
            for (int c = copy; c < columns; c++)
                resized[c] = TerminalCell.Blank(TerminalColor.Default(), bg);
            return resized;
        }

        private void PushScrollback(TerminalCell[] line)
        {
            if (!_scrollbackEnabled || _scrollbackMax == 0)
                return;
            _scrollback.Add(line);
            while (_scrollback.Count > _scrollbackMax)
                _scrollback.RemoveAt(0);
        }

        private static int Clamp(int v, int min, int max)
        {
            if (v < min)
                return min;
            if (v > max)
                return max;
            return v;
        }
    }
}
