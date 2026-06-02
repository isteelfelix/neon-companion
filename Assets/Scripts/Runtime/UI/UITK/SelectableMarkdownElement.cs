using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK
{
    /// <summary>
    /// A VisualElement that renders markdown/diff text with formatted runs
    /// and supports text selection via pointer events + Ctrl+C.
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
        }

        private struct DiffRun
        {
            public string text;
            public Color bgColor;
            public Color textColor;
        }

        // === State ===
        private int _anchorChar;
        private int _focusChar;
        private bool _isSelecting;
        private string _plainText;
        private List<TextRun> _runs;
        private List<DiffRun> _diffRuns;
        private bool _isDiff;

        // === Internal labels for rendering ===
        private VisualElement _contentContainer;
        private List<Label> _lineLabels = new List<Label>();
        private string _fontName;

        public string PlainText => _plainText ?? "";

        public SelectableMarkdownElement()
        {
            focusable = true;
            tabIndex = 0;
            style.flexDirection = FlexDirection.Column;
            style.overflow = Overflow.Hidden;

            _contentContainer = new VisualElement();
            _contentContainer.style.flexDirection = FlexDirection.Column;
            Add(_contentContainer);

            RegisterCallback<AttachToPanelEvent>(_ => AttachEvents());
            RegisterCallback<DetachFromPanelEvent>(_ => DetachEvents());
        }

        private void AttachEvents()
        {
            RegisterCallback<PointerDownEvent>(OnPointerDown);
            RegisterCallback<PointerMoveEvent>(OnPointerMove);
            RegisterCallback<PointerUpEvent>(OnPointerUp);
            RegisterCallback<KeyDownEvent>(OnKeyDown);
        }

        private void DetachEvents()
        {
            UnregisterCallback<PointerDownEvent>(OnPointerDown);
            UnregisterCallback<PointerMoveEvent>(OnPointerMove);
            UnregisterCallback<PointerUpEvent>(OnPointerUp);
            UnregisterCallback<KeyDownEvent>(OnKeyDown);
        }

        // ============================================================
        //  PUBLIC API
        // ============================================================

        public void SetMarkdown(string text)
        {
            _isDiff = false;
            _runs = ParseMarkdown(text);
            _plainText = text ?? "";
            RebuildVisual();
        }

        public void SetDiff(string text)
        {
            _isDiff = true;
            _diffRuns = ParseDiff(text);
            _plainText = text ?? "";
            RebuildVisual();
        }

        // ============================================================
        //  VISUAL REBUILD — Label-based rendering
        // ============================================================

        private void RebuildVisual()
        {
            _contentContainer.Clear();
            _lineLabels.Clear();

            if (_isDiff)
                RebuildDiff();
            else
                RebuildMarkdown();
        }

        private void RebuildMarkdown()
        {
            if (_runs == null) return;

            // Group runs into lines
            var currentLine = new VisualElement();
            currentLine.style.flexDirection = FlexDirection.Row;
            currentLine.style.flexWrap = Wrap.Wrap;
            currentLine.style.alignItems = Align.FlexStart;

            foreach (var run in _runs)
            {
                if (run.text == "\n")
                {
                    _contentContainer.Add(currentLine);
                    currentLine = new VisualElement();
                    currentLine.style.flexDirection = FlexDirection.Row;
                    currentLine.style.flexWrap = Wrap.Wrap;
                    currentLine.style.alignItems = Align.FlexStart;
                    continue;
                }

                var label = CreateRunLabel(run);
                currentLine.Add(label);
            }

            _contentContainer.Add(currentLine);
        }

        private void RebuildDiff()
        {
            if (_diffRuns == null) return;

            foreach (var run in _diffRuns)
            {
                var lineRow = new VisualElement();
                lineRow.style.flexDirection = FlexDirection.Row;
                lineRow.style.alignItems = Align.FlexStart;

                if (run.bgColor.a > 0)
                {
                    lineRow.style.backgroundColor = new Color(run.bgColor.r, run.bgColor.g, run.bgColor.b, run.bgColor.a);
                }

                var label = new Label(run.text);
                label.style.color = run.textColor;
                label.style.fontSize = 13;
                label.style.whiteSpace = WhiteSpace.Normal;
                label.focusable = false;
                lineRow.Add(label);

                _lineLabels.Add(label);
                _contentContainer.Add(lineRow);
            }
        }

        private Label CreateRunLabel(TextRun run)
        {
            var label = new Label(run.text);
            label.focusable = false;

            // Apply formatting via USS-compatible inline styles
            label.style.fontSize = run.code ? 12 : 14;
            label.style.color = GetRunColor(run);

            if (run.bold)
                label.style.unityFontStyleAndWeight = FontStyle.Bold;
            if (run.italic)
                label.style.unityFontStyleAndWeight = FontStyle.Italic;
            if (run.bold && run.italic)
                label.style.unityFontStyleAndWeight = FontStyle.BoldAndItalic;

            if (run.code)
            {
                label.style.backgroundColor = new Color(0.2f, 0.2f, 0.25f, 0.8f);
                label.style.paddingLeft = 4;
                label.style.paddingRight = 4;
                label.style.borderTopLeftRadius = 3;
                label.style.borderTopRightRadius = 3;
                label.style.borderBottomLeftRadius = 3;
                label.style.borderBottomRightRadius = 3;
            }

            if (run.strikethrough)
            {
                // textDecoration not available in Unity 6.2 UITK — skip
            }

            return label;
        }

        private static Color GetRunColor(TextRun run)
        {
            if (run.code) return new Color(0.9f, 0.6f, 0.3f);
            if (run.bold && run.italic) return new Color(1f, 0.9f, 0.5f);
            if (run.bold) return Color.white;
            if (run.italic) return new Color(0.8f, 0.85f, 1f);
            return run.color;
        }

        // ============================================================
        //  MARKDOWN PARSER
        // ============================================================

        private static List<TextRun> ParseMarkdown(string text)
        {
            var runs = new List<TextRun>();
            if (string.IsNullOrEmpty(text)) return runs;

            var lines = text.Split('\n');
            var buf = new StringBuilder();
            bool bold = false, italic = false, code = false, strike = false;

            void FlushRun()
            {
                if (buf.Length > 0)
                {
                    runs.Add(new TextRun { text = buf.ToString(), bold = bold, italic = italic, code = code, strikethrough = strike, color = Color.white });
                    buf.Clear();
                }
            }

            for (int li = 0; li < lines.Length; li++)
            {
                string line = lines[li];

                if (li > 0)
                {
                    FlushRun();
                    runs.Add(new TextRun { text = "\n" });
                }

                int pos = 0;
                while (pos < line.Length)
                {
                    // Bold: **text**
                    if (pos + 1 < line.Length && line[pos] == '*' && line[pos + 1] == '*')
                    {
                        FlushRun();
                        bold = !bold;
                        pos += 2;
                        continue;
                    }

                    // Strikethrough: ~~text~~
                    if (pos + 1 < line.Length && line[pos] == '~' && line[pos + 1] == '~')
                    {
                        FlushRun();
                        strike = !strike;
                        pos += 2;
                        continue;
                    }

                    // Italic: *text*
                    if (line[pos] == '*' && (pos + 1 < line.Length && line[pos + 1] != '*'))
                    {
                        FlushRun();
                        italic = !italic;
                        pos++;
                        continue;
                    }

                    // Code: `text`
                    if (line[pos] == '`')
                    {
                        FlushRun();
                        code = !code;
                        pos++;
                        continue;
                    }

                    buf.Append(line[pos]);
                    pos++;
                }
            }

            FlushRun();
            return runs;
        }

        // ============================================================
        //  DIFF PARSER
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
                Color fg = Color.white;

                if (line.StartsWith("+") && !line.StartsWith("+++"))
                    fg = green;
                else if (line.StartsWith("-") && !line.StartsWith("---"))
                    fg = red;
                else if (line.StartsWith("@@"))
                    fg = blue;
                else
                    fg = dim;

                runs.Add(new DiffRun { text = line, bgColor = fg * new Color(1, 1, 1, 0.1f), textColor = fg });
            }

            return runs;
        }

        // ============================================================
        //  SELECTION (basic pointer-based)
        // ============================================================

        private void OnPointerDown(PointerDownEvent evt)
        {
            // Focus this element
            Focus();
            _isSelecting = true;
            _anchorChar = 0;
            _focusChar = 0;
            this.CapturePointer(evt.pointerId);
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (!_isSelecting) return;
            // Basic: select all on drag (full implementation needs char-level hit testing)
            _focusChar = _plainText.Length;
        }

        private void OnPointerUp(PointerUpEvent evt)
        {
            if (!_isSelecting) return;
            _isSelecting = false;
            this.ReleasePointer(evt.pointerId);
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            if ((evt.ctrlKey || evt.commandKey) && evt.keyCode == KeyCode.C)
            {
                if (!string.IsNullOrEmpty(_plainText))
                {
                    GUIUtility.systemCopyBuffer = _plainText;
                    evt.StopPropagation();
                }
            }

            // Select all: Ctrl+A
            if ((evt.ctrlKey || evt.commandKey) && evt.keyCode == KeyCode.A)
            {
                _anchorChar = 0;
                _focusChar = _plainText?.Length ?? 0;
                evt.StopPropagation();
            }
        }
    }
}
