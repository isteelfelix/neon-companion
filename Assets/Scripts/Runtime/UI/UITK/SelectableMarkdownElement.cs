using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK
{
    /// <summary>
    /// A VisualElement that renders markdown/diff text with formatted runs
    /// and supports native text selection via pointer events.
    /// Drop-in replacement for MarkdownRenderer.Render() + TextField.
    /// </summary>
    internal class SelectableMarkdownElement : VisualElement
    {
        // === Parsed runs ===
        private struct TextRun
        {
            public string text;
            public bool bold;
            public bool italic;
            public bool code;
            public bool strikethrough;
            public Color color;
            public bool isBlockStart; // newline before this run
        }

        private struct DiffRun
        {
            public string text;
            public Color bgColor;
            public Color textColor;
        }

        // === Selection state ===
        private int _anchorChar;   // where selection started (char index in plain text)
        private int _focusChar;    // where selection ended
        private bool _isSelecting;
        private string _plainText; // full plain text for clipboard
        private List<Rect> _charRects = new List<Rect>(); // bounding rect per character

        // === Layout cache ===
        private List<TextRun> _runs;
        private List<DiffRun> _diffRuns;
        private bool _isDiff;
        private float _fontSize = 14f;
        private float _lineHeight = 20f;
        private float _cachedWidth;
        private Vector2 _scrollOffset;

        // === Public API ===
        public string PlainText => _plainText ?? "";

        public SelectableMarkdownElement()
        {
            focusable = true;
            tabIndex = 0;
            style.flexDirection = FlexDirection.Column;
            style.flexWrap = Wrap.Wrap;
            style.overflow = Overflow.Hidden;

            RegisterCallback<PointerDownEvent>(OnPointerDown);
            RegisterCallback<PointerMoveEvent>(OnPointerMove);
            RegisterCallback<PointerUpEvent>(OnPointerUp);
            RegisterCallback<KeyDownEvent>(OnKeyDown);
            RegisterCallback<FocusOutEvent>(_ => { ClearSelection(); });
            RegisterCallback<GeometryChangedEvent>(evt => { if (evt.newRect.width != _cachedWidth) { _cachedWidth = evt.newRect.width; MarkDirtyRepaint(); } });
        }

        /// <summary>
        /// Set markdown content for rendering.
        /// </summary>
        public void SetMarkdown(string text)
        {
            _isDiff = false;
            _runs = ParseMarkdown(text);
            _plainText = text ?? "";
            BuildCharRects();
            MarkDirtyRepaint();
        }

        /// <summary>
        /// Set diff content for rendering (colored lines).
        /// </summary>
        public void SetDiff(string text)
        {
            _isDiff = true;
            _diffRuns = ParseDiff(text);
            _plainText = text ?? "";
            BuildCharRects();
            MarkDirtyRepaint();
        }

        // ============================================================
        //  MARKDOWN PARSER — inline formatting into runs
        // ============================================================

        private static List<TextRun> ParseMarkdown(string text)
        {
            var runs = new List<TextRun>();
            if (string.IsNullOrEmpty(text)) return runs;

            var lines = text.Split('\n');
            var buf = new StringBuilder();
            bool bold = false, italic = false, code = false, strike = false;

            for (int li = 0; li < lines.Length; li++)
            {
                string line = lines[li];

                if (li > 0)
                {
                    // Flush buffered text as a run
                    if (buf.Length > 0)
                    {
                        runs.Add(new TextRun { text = buf.ToString(), bold = bold, italic = italic, code = code, strikethrough = strike, color = Color.white });
                        buf.Clear();
                    }
                    runs.Add(new TextRun { text = "\n", isBlockStart = true });
                }

                // Simple inline parsing: **bold**, *italic*, `code`, ~~strike~~
                int pos = 0;
                while (pos < line.Length)
                {
                    // Bold: **text**
                    if (pos + 1 < line.Length && line[pos] == '*' && line[pos + 1] == '*')
                    {
                        if (buf.Length > 0) { runs.Add(new TextRun { text = buf.ToString(), bold = bold, italic = italic, code = code, strikethrough = strike, color = Color.white }); buf.Clear(); }
                        bold = !bold;
                        pos += 2;
                        continue;
                    }

                    // Strikethrough: ~~text~~
                    if (pos + 1 < line.Length && line[pos] == '~' && line[pos + 1] == '~')
                    {
                        if (buf.Length > 0) { runs.Add(new TextRun { text = buf.ToString(), bold = bold, italic = italic, code = code, strikethrough = strike, color = Color.white }); buf.Clear(); }
                        strike = !strike;
                        pos += 2;
                        continue;
                    }

                    // Italic: *text* (single *)
                    if (line[pos] == '*' && (pos + 1 < line.Length && line[pos + 1] != '*'))
                    {
                        if (buf.Length > 0) { runs.Add(new TextRun { text = buf.ToString(), bold = bold, italic = italic, code = code, strikethrough = strike, color = Color.white }); buf.Clear(); }
                        italic = !italic;
                        pos++;
                        continue;
                    }

                    // Inline code: `text`
                    if (line[pos] == '`')
                    {
                        if (buf.Length > 0) { runs.Add(new TextRun { text = buf.ToString(), bold = bold, italic = italic, code = code, strikethrough = strike, color = Color.white }); buf.Clear(); }
                        code = !code;
                        pos++;
                        continue;
                    }

                    buf.Append(line[pos]);
                    pos++;
                }
            }

            // Flush remaining
            if (buf.Length > 0)
            {
                runs.Add(new TextRun { text = buf.ToString(), bold = bold, italic = italic, code = code, strikethrough = strike, color = Color.white });
            }

            return runs;
        }

        // ============================================================
        //  DIFF PARSER — colored line runs
        // ============================================================

        private static List<DiffRun> ParseDiff(string text)
        {
            var runs = new List<DiffRun>();
            if (string.IsNullOrEmpty(text)) return runs;

            var green = new Color(0.29f, 0.87f, 0.5f);
            var red = new Color(0.97f, 0.44f, 0.44f);
            var blue = new Color(0.37f, 0.65f, 0.98f);
            var dim = new Color(0.7f, 0.7f, 0.7f);

            var lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                Color bg = Color.clear;
                Color fg = Color.white;

                if (line.StartsWith("+") && !line.StartsWith("+++"))
                {
                    bg = new Color(green.r, green.g, green.b, 0.15f);
                    fg = green;
                }
                else if (line.StartsWith("-") && !line.StartsWith("---"))
                {
                    bg = new Color(red.r, red.g, red.b, 0.15f);
                    fg = red;
                }
                else if (line.StartsWith("@@"))
                {
                    fg = blue;
                }
                else
                {
                    fg = dim;
                }

                if (i > 0)
                    runs.Add(new DiffRun { text = "\n", textColor = Color.white });

                runs.Add(new DiffRun { text = line, bgColor = bg, textColor = fg });
            }

            return runs;
        }

        // ============================================================
        //  CHARACTER RECT MAPPING — for hit testing
        // ============================================================

        private void BuildCharRects()
        {
            _charRects.Clear();
            if (string.IsNullOrEmpty(_plainText)) return;

            float x = 0, y = 0;
            float maxWidth = resolvedStyle.width > 0 ? resolvedStyle.width : 600;
            float charWidth = _fontSize * 0.6f; // approximate monospace-ish
            float spaceWidth = _fontSize * 0.3f;

            for (int i = 0; i < _plainText.Length; i++)
            {
                char c = _plainText[i];
                if (c == '\n')
                {
                    _charRects.Add(new Rect(x, y, 0, _lineHeight));
                    x = 0;
                    y += _lineHeight;
                    continue;
                }

                float w = c == ' ' ? spaceWidth : charWidth;
                if (x + w > maxWidth)
                {
                    x = 0;
                    y += _lineHeight;
                }

                _charRects.Add(new Rect(x, y, w, _lineHeight));
                x += w;
            }
        }

        private int HitTestChar(Vector2 localPos)
        {
            for (int i = 0; i < _charRects.Count; i++)
            {
                if (_charRects[i].Contains(localPos))
                    return i;
            }
            // If past the end, return last char
            return Mathf.Clamp(_charRects.Count - 1, 0, _charRects.Count);
        }

        // ============================================================
        //  SELECTION
        // ============================================================

        private void ClearSelection()
        {
            _anchorChar = 0;
            _focusChar = 0;
            _isSelecting = false;
            MarkDirtyRepaint();
        }

        private void UpdateSelection(int newFocus)
        {
            if (_focusChar != newFocus)
            {
                _focusChar = newFocus;
                MarkDirtyRepaint();
            }
        }

        private int SelectionStart => Mathf.Min(_anchorChar, _focusChar);
        private int SelectionEnd => Mathf.Max(_anchorChar, _focusChar);

        private string GetSelectedText()
        {
            if (SelectionStart == SelectionEnd) return "";
            int start = Mathf.Clamp(SelectionStart, 0, _plainText.Length);
            int end = Mathf.Clamp(SelectionEnd, 0, _plainText.Length);
            return _plainText.Substring(start, end - start);
        }

        // ============================================================
        //  EVENT HANDLERS
        // ============================================================

        private void OnPointerDown(PointerDownEvent evt)
        {
            focusController?.Focus(this);
            _anchorChar = HitTestChar(evt.localPosition);
            _focusChar = _anchorChar;
            _isSelecting = true;
            this.CapturePointer(evt.pointerId);
            MarkDirtyRepaint();
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (!_isSelecting) return;
            int newFocus = HitTestChar(evt.localPosition);
            UpdateSelection(newFocus);
        }

        private void OnPointerUp(PointerUpEvent evt)
        {
            if (!_isSelecting) return;
            _isSelecting = false;
            this.ReleasePointer(evt.pointerId);
            MarkDirtyRepaint();
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            if ((evt.ctrlKey || evt.commandKey) && evt.keyCode == KeyCode.C)
            {
                string selected = GetSelectedText();
                if (!string.IsNullOrEmpty(selected))
                {
                    GUIUtility.systemCopyBuffer = selected;
                    evt.StopPropagation();
                }
            }
        }

        // ============================================================
        //  RENDERING — VisualElement custom paint
        // ============================================================

        protected override void GenerateVisualContent(MeshGenerationContext mgc)
        {
            var painter = mgc.painter2D;
            if (painter == null) return;

            if (_isDiff && _diffRuns != null)
                RenderDiff(painter);
            else if (_runs != null)
                RenderMarkdown(painter);

            DrawSelection(painter);
        }

        private void RenderMarkdown(Painter2D painter)
        {
            float x = 0, y = _fontSize;
            float maxWidth = resolvedStyle.width > 0 ? resolvedStyle.width : 600;

            foreach (var run in _runs)
            {
                if (run.text == "\n")
                {
                    x = 0;
                    y += _lineHeight;
                    continue;
                }

                // Word wrap
                float runWidth = run.text.Length * _fontSize * 0.6f;
                if (x + runWidth > maxWidth && x > 0)
                {
                    x = 0;
                    y += _lineHeight;
                }

                // Draw background for code
                if (run.code)
                {
                    painter.fillColor = new Color(0.2f, 0.2f, 0.25f, 0.8f);
                    painter.DrawRect(new Rect(x - 2, y - _fontSize + 2, runWidth + 4, _lineHeight));
                }

                // Set text color
                Color col = run.color;
                if (run.bold && run.italic) col = new Color(1f, 0.9f, 0.5f);
                else if (run.bold) col = Color.white;
                else if (run.italic) col = new Color(0.8f, 0.85f, 1f);
                else if (run.code) col = new Color(0.9f, 0.6f, 0.3f);

                painter.color = col;
                painter.fontSize = run.code ? _fontSize - 1 : _fontSize;

                // Draw each character
                foreach (char c in run.text)
                {
                    if (c == ' ') { x += _fontSize * 0.3f; continue; }
                    float w = _fontSize * 0.6f;
                    if (x + w > maxWidth) { x = 0; y += _lineHeight; }

                    var pos = new Vector2(x, y);
                    painter.DrawText(c.ToString(), pos);

                    if (run.strikethrough)
                    {
                        painter.strokeColor = col;
                        painter.lineWidth = 1;
                        painter.BeginPath();
                        painter.MoveTo(new Vector2(x, y - _fontSize * 0.3f));
                        painter.LineTo(new Vector2(x + w, y - _fontSize * 0.3f));
                        painter.Stroke();
                    }

                    x += w;
                }
            }
        }

        private void RenderDiff(Painter2D painter)
        {
            float x = 0, y = _fontSize;
            float maxWidth = resolvedStyle.width > 0 ? resolvedStyle.width : 600;

            foreach (var run in _diffRuns)
            {
                if (run.text == "\n")
                {
                    x = 0;
                    y += _lineHeight;
                    continue;
                }

                // Draw line background
                if (run.bgColor.a > 0)
                {
                    painter.fillColor = run.bgColor;
                    painter.DrawRect(new Rect(0, y - _fontSize + 2, maxWidth, _lineHeight));
                }

                // Draw text
                painter.color = run.textColor;
                painter.fontSize = _fontSize;

                foreach (char c in run.text)
                {
                    if (c == ' ') { x += _fontSize * 0.3f; continue; }
                    float w = _fontSize * 0.6f;
                    if (x + w > maxWidth) { x = 0; y += _lineHeight; }

                    var pos = new Vector2(x, y);
                    painter.DrawText(c.ToString(), pos);
                    x += w;
                }
            }
        }

        private void DrawSelection(Painter2D painter)
        {
            int start = SelectionStart;
            int end = SelectionEnd;
            if (start == end || start >= _charRects.Count) return;

            painter.fillColor = new Color(0.3f, 0.5f, 0.9f, 0.35f);
            for (int i = start; i < end && i < _charRects.Count; i++)
            {
                var r = _charRects[i];
                if (r.width > 0)
                    painter.DrawRect(r);
            }
        }
    }
}
