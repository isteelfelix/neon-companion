using System;
using System.Text;
using NeonCompanion.Runtime.Terminal.Emulator;
using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK.Terminal
{
    /// <summary>
    /// Renders a <see cref="TerminalEmulator"/> screen in UI Toolkit: one rich-text
    /// <see cref="Label"/> per visible row (cells grouped into styled runs), a translucent
    /// block cursor overlay, monospace font via the shared <c>text-mono</c> USS class, and
    /// mouse-wheel scrollback. Character cell size is measured from the live font so the
    /// grid stays aligned regardless of DPI/font.
    ///
    /// The view owns no terminal state; the controller drives <see cref="Render"/> and reads
    /// <see cref="ViewportColumns"/>/<see cref="ViewportRows"/> to resize the PTY.
    /// </summary>
    public sealed class TerminalScreenView : VisualElement
    {
        private readonly TerminalPalette _palette = new TerminalPalette();
        private readonly VisualElement _rows;
        private readonly VisualElement _cursor;
        private readonly Label _measure;
        private readonly Label _message;
        private readonly StringBuilder _sb = new StringBuilder(256);

        private float _cellWidth;
        private float _cellHeight;
        private int _fontSize;

        private int _viewportColumns = 80;
        private int _viewportRows = 24;
        private int _scrollOffset;

        private bool _focused;
        private bool _blinkOn = true;
        private bool _cursorWanted;
        private float _cursorX;
        private float _cursorY;

        /// <summary>Raised when the number of whole cells that fit changes (columns, rows).</summary>
        public event Action<int, int> ViewportChanged;

        /// <summary>Raised on wheel scroll: positive = scroll up into history.</summary>
        public event Action<int> ScrollRequested;

        public int ViewportColumns { get { return _viewportColumns; } }
        public int ViewportRows { get { return _viewportRows; } }
        public int ScrollOffset { get { return _scrollOffset; } }

        public TerminalScreenView(int fontSize)
        {
            _fontSize = fontSize > 0 ? fontSize : 12;

            AddToClassList("terminal-screen");
            style.flexGrow = 1;
            style.overflow = Overflow.Hidden;
            style.backgroundColor = ToStyleColor(_palette.DefaultBackground);

            _rows = new VisualElement();
            _rows.name = "terminal-rows";
            _rows.AddToClassList("terminal-screen__rows");
            Add(_rows);

            _cursor = new VisualElement();
            _cursor.name = "terminal-cursor";
            _cursor.style.position = Position.Absolute;
            _cursor.style.backgroundColor = ToStyleColor(WithAlpha(_palette.DefaultForeground, 130));
            _cursor.style.display = DisplayStyle.None;
            Add(_cursor);

            _message = new Label();
            _message.name = "terminal-message";
            _message.AddToClassList("text-mono");
            _message.AddToClassList("terminal-screen__message");
            _message.style.color = ToStyleColor(_palette.DefaultForeground);
            _message.style.fontSize = _fontSize;
            _message.style.whiteSpace = WhiteSpace.Normal;
            _message.style.display = DisplayStyle.None;
            Add(_message);

            // Hidden ruler used purely to measure the monospace advance + line height.
            _measure = new Label("MMMMMMMMMMMMMMMMMMMM"); // 20 chars
            _measure.AddToClassList("text-mono");
            _measure.style.position = Position.Absolute;
            _measure.style.fontSize = _fontSize;
            _measure.style.whiteSpace = WhiteSpace.NoWrap;
            _measure.style.opacity = 0f;
            _measure.style.left = 0;
            _measure.style.top = -10000;
            Add(_measure);

            _measure.RegisterCallback<GeometryChangedEvent>(OnMeasureGeometry);
            RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            RegisterCallback<WheelEvent>(OnWheel);
            RegisterCallback<FocusInEvent>(OnFocusIn);
            RegisterCallback<FocusOutEvent>(OnFocusOut);

            // Cursor blink (~530ms, the conventional terminal rate).
            schedule.Execute(OnBlinkTick).Every(530);

            focusable = true;
            pickingMode = PickingMode.Position;
        }

        // ---- Public API -----------------------------------------------------------

        public void ShowMessage(string text)
        {
            _message.text = text ?? string.Empty;
            _message.style.display = DisplayStyle.Flex;
            _rows.style.display = DisplayStyle.None;
            _cursor.style.display = DisplayStyle.None;
        }

        public void HideMessage()
        {
            _message.style.display = DisplayStyle.None;
            _rows.style.display = DisplayStyle.Flex;
        }

        public void SetScrollOffset(int offset, TerminalEmulator emulator)
        {
            int max = emulator != null ? emulator.ScrollbackCount : 0;
            _scrollOffset = Mathf.Clamp(offset, 0, max);
            if (emulator != null)
                Render(emulator);
        }

        /// <summary>Rebuild the visible grid from the emulator's current state.</summary>
        public void Render(TerminalEmulator emulator)
        {
            if (emulator == null)
                return;

            HideMessage();

            int rows = emulator.Rows;
            int cols = emulator.Columns;
            EnsureRowCount(rows);

            ScreenBuffer buffer = emulator.ActiveBuffer;
            int total = buffer.TotalRows;
            _scrollOffset = Mathf.Clamp(_scrollOffset, 0, emulator.ScrollbackCount);

            // Top absolute line of the viewport, scrolled up by _scrollOffset into history.
            int start = total - rows - _scrollOffset;
            if (start < 0)
                start = 0;

            for (int r = 0; r < rows; r++)
            {
                Label rowLabel = (Label)_rows.ElementAt(r);
                TerminalCell[] line = buffer.AbsoluteLine(start + r);
                rowLabel.text = BuildRowText(line, cols);
            }

            UpdateCursor(emulator);
        }

        // ---- Rendering internals --------------------------------------------------

        private void EnsureRowCount(int rows)
        {
            while (_rows.childCount < rows)
            {
                Label l = new Label();
                l.AddToClassList("text-mono");
                l.enableRichText = true;
                l.style.color = ToStyleColor(_palette.DefaultForeground);
                l.style.fontSize = _fontSize;
                l.style.whiteSpace = WhiteSpace.NoWrap;
                l.style.marginTop = 0;
                l.style.marginBottom = 0;
                l.style.marginLeft = 0;
                l.style.marginRight = 0;
                l.style.paddingTop = 0;
                l.style.paddingBottom = 0;
                l.style.flexShrink = 0;
                if (_cellHeight > 0f)
                    l.style.height = _cellHeight;
                _rows.Add(l);
            }
            while (_rows.childCount > rows)
                _rows.RemoveAt(_rows.childCount - 1);
        }

        private string BuildRowText(TerminalCell[] line, int cols)
        {
            _sb.Length = 0;
            if (line == null)
                return string.Empty;

            int col = 0;
            int limit = Math.Min(cols, line.Length);
            while (col < limit)
            {
                int run = col + 1;
                while (run < limit && SameStyle(line[run], line[col]))
                    run++;
                AppendRun(line, col, run);
                col = run;
            }
            return _sb.ToString();
        }

        private static bool SameStyle(TerminalCell a, TerminalCell b)
        {
            return a.Attributes == b.Attributes &&
                   ColorEquals(a.Foreground, b.Foreground) &&
                   ColorEquals(a.Background, b.Background);
        }

        private static bool ColorEquals(TerminalColor a, TerminalColor b)
        {
            if (a.Mode != b.Mode)
                return false;
            switch (a.Mode)
            {
                case TerminalColorMode.Indexed:
                    return a.Index == b.Index;
                case TerminalColorMode.Rgb:
                    return a.R == b.R && a.G == b.G && a.B == b.B;
                default:
                    return true;
            }
        }

        private void AppendRun(TerminalCell[] line, int from, int to)
        {
            TerminalCell sample = line[from];
            CellAttributes attrs = sample.Attributes;
            bool inverse = (attrs & CellAttributes.Inverse) != 0;
            bool hidden = (attrs & CellAttributes.Hidden) != 0;

            TerminalColor fgColor = sample.Foreground;
            if ((attrs & CellAttributes.Bold) != 0)
                TerminalPalette.TryBrighten(ref fgColor);

            Color32 fg = _palette.Resolve(fgColor, false);
            Color32 bg = _palette.Resolve(sample.Background, true);
            if (inverse)
            {
                Color32 t = fg;
                fg = bg;
                bg = t;
            }
            if (hidden)
                fg = bg;

            bool hasBg = inverse || !ColorEquals(sample.Background, TerminalColor.Default());
            bool bold = (attrs & CellAttributes.Bold) != 0;
            bool italic = (attrs & CellAttributes.Italic) != 0;
            bool underline = (attrs & CellAttributes.Underline) != 0;
            bool strike = (attrs & CellAttributes.Strikethrough) != 0;

            _sb.Append("<color=#").Append(Hex(fg)).Append('>');
            if (hasBg)
                _sb.Append("<mark=#").Append(Hex(bg)).Append("ff>");
            if (bold) _sb.Append("<b>");
            if (italic) _sb.Append("<i>");
            if (underline) _sb.Append("<u>");
            if (strike) _sb.Append("<s>");

            _sb.Append("<noparse>");
            for (int i = from; i < to; i++)
            {
                char c = line[i].Char;
                _sb.Append(c == '\0' ? ' ' : c);
            }
            _sb.Append("</noparse>");

            if (strike) _sb.Append("</s>");
            if (underline) _sb.Append("</u>");
            if (italic) _sb.Append("</i>");
            if (bold) _sb.Append("</b>");
            if (hasBg) _sb.Append("</mark>");
            _sb.Append("</color>");
        }

        private void UpdateCursor(TerminalEmulator emulator)
        {
            _cursorWanted = emulator.CursorVisible && _scrollOffset == 0 && _cellWidth > 0f && _cellHeight > 0f;
            if (_cursorWanted)
            {
                _cursorX = resolvedStyle.paddingLeft + emulator.CursorCol * _cellWidth;
                _cursorY = resolvedStyle.paddingTop + emulator.CursorRow * _cellHeight;
            }
            // Keep the cursor solid right after an update; blink resumes when idle.
            _blinkOn = true;
            ApplyCursorVisual();
        }

        private void ApplyCursorVisual()
        {
            if (!_cursorWanted)
            {
                _cursor.style.display = DisplayStyle.None;
                return;
            }

            _cursor.style.width = _cellWidth;
            _cursor.style.height = _cellHeight;
            _cursor.style.left = _cursorX;
            _cursor.style.top = _cursorY;

            if (_focused)
            {
                // Focused: filled block that blinks.
                _cursor.style.display = _blinkOn ? DisplayStyle.Flex : DisplayStyle.None;
                _cursor.style.backgroundColor = ToStyleColor(WithAlpha(_palette.DefaultForeground, 150));
                SetCursorBorder(0f);
            }
            else
            {
                // Unfocused: steady hollow outline — makes focus state obvious.
                _cursor.style.display = DisplayStyle.Flex;
                _cursor.style.backgroundColor = ToStyleColor(new Color32(0, 0, 0, 0));
                SetCursorBorder(1f);
            }
        }

        private void SetCursorBorder(float width)
        {
            _cursor.style.borderTopWidth = width;
            _cursor.style.borderRightWidth = width;
            _cursor.style.borderBottomWidth = width;
            _cursor.style.borderLeftWidth = width;

            StyleColor c = ToStyleColor(_palette.DefaultForeground);
            _cursor.style.borderTopColor = c;
            _cursor.style.borderRightColor = c;
            _cursor.style.borderBottomColor = c;
            _cursor.style.borderLeftColor = c;
        }

        private void OnBlinkTick()
        {
            if (!_focused || !_cursorWanted)
                return;
            _blinkOn = !_blinkOn;
            ApplyCursorVisual();
        }

        private void OnFocusIn(FocusInEvent evt)
        {
            _focused = true;
            _blinkOn = true;
            ApplyCursorVisual();
        }

        private void OnFocusOut(FocusOutEvent evt)
        {
            _focused = false;
            ApplyCursorVisual();
        }

        // ---- Measurement / layout -------------------------------------------------

        private void OnMeasureGeometry(GeometryChangedEvent evt)
        {
            float w = _measure.resolvedStyle.width;
            float h = _measure.resolvedStyle.height;
            if (w <= 0f || h <= 0f)
                return;

            float newCellW = w / 20f;
            float newCellH = h;
            bool changed = Mathf.Abs(newCellW - _cellWidth) > 0.01f || Mathf.Abs(newCellH - _cellHeight) > 0.01f;
            _cellWidth = newCellW;
            _cellHeight = newCellH;

            if (changed)
            {
                for (int i = 0; i < _rows.childCount; i++)
                    _rows.ElementAt(i).style.height = _cellHeight;
                RecomputeViewport();
            }
        }

        private void OnGeometryChanged(GeometryChangedEvent evt)
        {
            RecomputeViewport();
        }

        private void RecomputeViewport()
        {
            if (_cellWidth <= 0f || _cellHeight <= 0f)
                return;

            float availW = contentRect.width;
            float availH = contentRect.height;
            if (availW <= 0f || availH <= 0f)
                return;

            int cols = Mathf.Max(1, Mathf.FloorToInt(availW / _cellWidth));
            int rows = Mathf.Max(1, Mathf.FloorToInt(availH / _cellHeight));

            if (cols == _viewportColumns && rows == _viewportRows)
                return;

            _viewportColumns = cols;
            _viewportRows = rows;

            Action<int, int> handler = ViewportChanged;
            if (handler != null)
                handler(cols, rows);
        }

        private void OnWheel(WheelEvent evt)
        {
            int lines = evt.delta.y > 0f ? -3 : 3;
            Action<int> handler = ScrollRequested;
            if (handler != null)
                handler(lines);
            evt.StopPropagation();
        }

        // ---- Helpers --------------------------------------------------------------

        private static string Hex(Color32 c)
        {
            return c.r.ToString("x2") + c.g.ToString("x2") + c.b.ToString("x2");
        }

        private static StyleColor ToStyleColor(Color32 c)
        {
            return new StyleColor(c);
        }

        private static Color32 WithAlpha(Color32 c, byte a)
        {
            return new Color32(c.r, c.g, c.b, a);
        }
    }
}
