using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK
{
    /// <summary>
    /// Custom element that renders markdown/diff with formatted labels.
    /// Text selection: Ctrl+A selects all, Ctrl+C copies plain text.
    /// </summary>
    internal class SelectableMarkdownElement : VisualElement
    {
        private struct TextRun
        {
            public string text;
            public bool bold;
            public bool italic;
            public bool code;
        }

        private struct DiffLine
        {
            public string text;
            public Color bgColor;
            public Color textColor;
        }

        private string _plainText = "";
        private List<TextRun> _runs;
        private List<DiffLine> _diffLines;
        private bool _isDiff;
        private bool _isSelected;

        public string PlainText => _plainText;

        public SelectableMarkdownElement()
        {
            focusable = true;
            tabIndex = 0;
            style.flexDirection = FlexDirection.Column;

            // Keyboard shortcuts
            RegisterCallback<KeyDownEvent>(OnKeyDown);
            // Click to select all
            RegisterCallback<ClickEvent>(evt =>
            {
                _isSelected = !_isSelected;
                if (_isSelected)
                    style.backgroundColor = new Color(0.3f, 0.5f, 0.9f, 0.15f);
                else
                    style.backgroundColor = Color.clear;
            });
        }

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
            _diffLines = ParseDiff(text);
            _plainText = text ?? "";
            RebuildVisual();
        }

        private void RebuildVisual()
        {
            Clear();

            if (_isDiff && _diffLines != null)
                RebuildDiff();
            else if (_runs != null)
                RebuildMarkdown();
        }

        private void RebuildMarkdown()
        {
            VisualElement currentLine = CreateLine();

            foreach (var run in _runs)
            {
                if (run.text == "\n")
                {
                    Add(currentLine);
                    currentLine = CreateLine();
                    continue;
                }

                var label = new Label(run.text);
                label.focusable = false;
                label.style.fontSize = run.code ? 12 : 14;

                if (run.bold) label.style.unityFontStyleAndWeight = FontStyle.Bold;
                if (run.italic) label.style.unityFontStyleAndWeight = FontStyle.Italic;
                if (run.bold && run.italic) label.style.unityFontStyleAndWeight = FontStyle.BoldAndItalic;

                if (run.code)
                {
                    label.style.backgroundColor = new Color(0.2f, 0.2f, 0.25f, 0.8f);
                    label.style.paddingLeft = 4;
                    label.style.paddingRight = 4;
                    label.style.borderTopLeftRadius = 3;
                    label.style.borderTopRightRadius = 3;
                    label.style.borderBottomLeftRadius = 3;
                    label.style.borderBottomRightRadius = 3;
                    label.style.color = new Color(0.9f, 0.6f, 0.3f);
                }

                currentLine.Add(label);
            }

            Add(currentLine);
        }

        private void RebuildDiff()
        {
            foreach (var dl in _diffLines)
            {
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.FlexStart;

                if (dl.bgColor.a > 0)
                    row.style.backgroundColor = new Color(dl.bgColor.r, dl.bgColor.g, dl.bgColor.b, dl.bgColor.a);

                var label = new Label(dl.text);
                label.focusable = false;
                label.style.fontSize = 13;
                label.style.color = dl.textColor;
                label.style.whiteSpace = WhiteSpace.Normal;
                row.Add(label);

                Add(row);
            }
        }

        private static VisualElement CreateLine()
        {
            var line = new VisualElement();
            line.style.flexDirection = FlexDirection.Row;
            line.style.flexWrap = Wrap.Wrap;
            line.style.alignItems = Align.FlexStart;
            return line;
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
            else if ((evt.ctrlKey || evt.commandKey) && evt.keyCode == KeyCode.A)
            {
                _isSelected = !_isSelected;
                style.backgroundColor = _isSelected
                    ? new Color(0.3f, 0.5f, 0.9f, 0.15f)
                    : Color.clear;
                evt.StopPropagation();
            }
        }

        // ============================================================
        //  PARSERS
        // ============================================================

        private static List<TextRun> ParseMarkdown(string text)
        {
            var runs = new List<TextRun>();
            if (string.IsNullOrEmpty(text)) return runs;

            var lines = text.Split('\n');
            var buf = new StringBuilder();
            bool bold = false, italic = false, code = false;

            void Flush()
            {
                if (buf.Length > 0)
                {
                    runs.Add(new TextRun { text = buf.ToString(), bold = bold, italic = italic, code = code });
                    buf.Clear();
                }
            }

            for (int li = 0; li < lines.Length; li++)
            {
                if (li > 0) { Flush(); runs.Add(new TextRun { text = "\n" }); }

                int pos = 0;
                string line = lines[li];
                while (pos < line.Length)
                {
                    if (pos + 1 < line.Length && line[pos] == '*' && line[pos + 1] == '*')
                    { Flush(); bold = !bold; pos += 2; continue; }
                    if (line[pos] == '*' && pos + 1 < line.Length && line[pos + 1] != '*')
                    { Flush(); italic = !italic; pos++; continue; }
                    if (line[pos] == '`')
                    { Flush(); code = !code; pos++; continue; }

                    buf.Append(line[pos]);
                    pos++;
                }
            }
            Flush();
            return runs;
        }

        private static List<DiffLine> ParseDiff(string text)
        {
            var result = new List<DiffLine>();
            if (string.IsNullOrEmpty(text)) return result;

            var green = new Color(0.29f, 0.87f, 0.5f);
            var red = new Color(0.97f, 0.44f, 0.44f);
            var blue = new Color(0.37f, 0.65f, 0.98f);
            var dim = new Color(0.7f, 0.7f, 0.7f);

            foreach (var raw in text.Split('\n'))
            {
                Color fg = raw.StartsWith("+") && !raw.StartsWith("+++") ? green
                    : raw.StartsWith("-") && !raw.StartsWith("---") ? red
                    : raw.StartsWith("@@") ? blue : dim;

                result.Add(new DiffLine { text = raw, bgColor = fg * new Color(1, 1, 1, 0.1f), textColor = fg });
            }
            return result;
        }
    }
}
