using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK
{
    /// <summary>
    /// Native UITK chat text engine slice: markdown/diff blocks, manual wrapping,
    /// and custom selection/copy without relying on TextField text geometry.
    /// </summary>
    internal class SelectableMarkdownElement : VisualElement
    {
        private enum SourceMode
        {
            Markdown,
            Diff
        }

        private enum LineKind
        {
            Text,
            Heading,
            Quote,
            Code,
            Diff,
            Rule
        }

        private class TextRun
        {
            public string Text;
            public bool Bold;
            public bool Italic;
            public bool Code;
            public bool Strike;
            public string LinkUrl;

            public TextRun CloneWithText(string value)
            {
                return new TextRun
                {
                    Text = value,
                    Bold = Bold,
                    Italic = Italic,
                    Code = Code,
                    Strike = Strike,
                    LinkUrl = LinkUrl
                };
            }
        }

        private class LineSpec
        {
            public readonly List<TextRun> Runs = new List<TextRun>();
            public LineKind Kind;
            public int HeadingLevel;
            public float Indent;
            public float MarginTop;
            public float MarginBottom;
            public Color Background;
            public Color TextColor;
            public bool HasTextColor;
            public bool CodeBlockLine;
            public bool PlainInline;
            public bool IncludeTrailingNewline = true;
        }

        private class RunBox
        {
            public int PlainStart;
            public int PlainEnd;
            public float X;
            public float Width;
            public TextRun Run;
            public float[] CharOffsets;
        }

        private class VisualLine
        {
            public readonly List<RunBox> Runs = new List<RunBox>();
            public int PlainStart;
            public int PlainEnd;
            public float Y;
            public float Height;
            public float Indent;
        }

        private class BackgroundBox
        {
            public float X;
            public float Y;
            public float Width;
            public float Height;
            public Color Color;
            public float Radius;
        }

        private readonly VisualElement _backgroundLayer;
        private readonly VisualElement _selectionLayer;
        private readonly VisualElement _contentLayer;
        private readonly Label _measureLabel;
        private readonly List<LineSpec> _lineSpecs = new List<LineSpec>();
        private readonly List<VisualLine> _visualLines = new List<VisualLine>();
        private readonly List<BackgroundBox> _backgrounds = new List<BackgroundBox>();

        private string _sourceText = string.Empty;
        private string _plainText = string.Empty;
        private SourceMode _mode = SourceMode.Markdown;
        private int _selectionAnchor = -1;
        private int _selectionFocus = -1;
        private bool _isDragging;
        private int _capturedPointerId = -1;
        private int _lastPointerIndex = -1;
        private string _lastPointerLink;
        private float _lastLayoutWidth = -1f;
        private bool _layoutRebuildScheduled;

        public string PlainText
        {
            get { return _plainText; }
        }

        public SelectableMarkdownElement()
        {
            focusable = true;
            tabIndex = 0;
            pickingMode = PickingMode.Position;

            style.flexDirection = FlexDirection.Column;
            style.position = Position.Relative;
            style.minWidth = 0;
            style.width = Length.Percent(100);

            _backgroundLayer = CreateAbsoluteLayer();
            _selectionLayer = CreateAbsoluteLayer();

            _contentLayer = new VisualElement();
            _contentLayer.pickingMode = PickingMode.Ignore;
            _contentLayer.style.flexDirection = FlexDirection.Column;
            _contentLayer.style.minWidth = 0;

            _measureLabel = new Label();
            _measureLabel.pickingMode = PickingMode.Ignore;
            _measureLabel.style.position = Position.Absolute;
            _measureLabel.style.left = -10000;
            _measureLabel.style.top = -10000;
            _measureLabel.style.opacity = 0;
            _measureLabel.style.whiteSpace = WhiteSpace.NoWrap;

            Add(_backgroundLayer);
            Add(_selectionLayer);
            Add(_contentLayer);
            Add(_measureLabel);

            RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            RegisterCallback<PointerDownEvent>(OnPointerDown);
            RegisterCallback<PointerMoveEvent>(OnPointerMove);
            RegisterCallback<PointerUpEvent>(OnPointerUp);
            RegisterCallback<KeyDownEvent>(OnKeyDown);
        }

        public void SetMarkdown(string text)
        {
            _mode = SourceMode.Markdown;
            _sourceText = text ?? string.Empty;
            ClearSelection();
            ParseSource();
            ScheduleLayoutRebuild(true);
        }

        public void SetDiff(string text)
        {
            _mode = SourceMode.Diff;
            _sourceText = text ?? string.Empty;
            ClearSelection();
            ParseSource();
            ScheduleLayoutRebuild(true);
        }

        private static VisualElement CreateAbsoluteLayer()
        {
            var layer = new VisualElement();
            layer.pickingMode = PickingMode.Ignore;
            layer.style.position = Position.Absolute;
            layer.style.left = 0;
            layer.style.right = 0;
            layer.style.top = 0;
            layer.style.bottom = 0;
            return layer;
        }

        private void OnGeometryChanged(GeometryChangedEvent evt)
        {
            float width = contentRect.width;
            if (width <= 1f)
                return;
            if (Mathf.Abs(width - _lastLayoutWidth) < 0.5f)
                return;
            ScheduleLayoutRebuild(false);
        }

        private void ScheduleLayoutRebuild(bool force)
        {
            if (force)
                _lastLayoutWidth = -1f;
            if (_layoutRebuildScheduled)
                return;
            _layoutRebuildScheduled = true;
            schedule.Execute(() =>
            {
                _layoutRebuildScheduled = false;
                if (panel == null)
                    return;
                RebuildLayout();
            }).StartingIn(0);
        }

        private void ParseSource()
        {
            _lineSpecs.Clear();
            if (_mode == SourceMode.Diff)
                ParseDiff(_sourceText);
            else
                ParseMarkdown(_sourceText);
            BuildPlainText();
        }

        private void ParseMarkdown(string markdown)
        {
            if (string.IsNullOrEmpty(markdown))
                return;

            string normalized = markdown.Replace("\r\n", "\n").Replace("\r", "\n");
            string[] lines = normalized.Split('\n');
            List<string> paragraph = new List<string>();

            int i = 0;
            while (i < lines.Length)
            {
                string raw = lines[i];
                string trimmedStart = raw.TrimStart(' ', '\t');

                if (trimmedStart.StartsWith("```", StringComparison.Ordinal))
                {
                    FlushParagraph(paragraph);
                    i++;
                    while (i < lines.Length && !lines[i].TrimStart(' ', '\t').StartsWith("```", StringComparison.Ordinal))
                    {
                        AddCodeLine(lines[i]);
                        i++;
                    }
                    if (i < lines.Length)
                        i++;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(raw))
                {
                    FlushParagraph(paragraph);
                    i++;
                    continue;
                }

                if (IsHorizontalRule(trimmedStart))
                {
                    FlushParagraph(paragraph);
                    AddRuleLine();
                    i++;
                    continue;
                }

                int headingLevel = GetHeadingLevel(trimmedStart);
                if (headingLevel > 0)
                {
                    FlushParagraph(paragraph);
                    string headingText = trimmedStart.Substring(headingLevel + 1);
                    AddInlineLine(headingText, LineKind.Heading, headingLevel, 0, 6, 3);
                    i++;
                    continue;
                }

                if (trimmedStart.StartsWith("> ", StringComparison.Ordinal) || string.Equals(trimmedStart, ">", StringComparison.Ordinal))
                {
                    FlushParagraph(paragraph);
                    string quoteText = trimmedStart.Length > 2 ? trimmedStart.Substring(2) : string.Empty;
                    AddInlineLine(quoteText, LineKind.Quote, 0, 14, 3, 3);
                    i++;
                    continue;
                }

                string bulletMarker;
                string bulletText;
                if (TryParseListItem(trimmedStart, out bulletMarker, out bulletText))
                {
                    FlushParagraph(paragraph);
                    AddInlineLine(bulletMarker + " " + bulletText, LineKind.Text, 0, 12, 2, 2);
                    i++;
                    continue;
                }

                if (IsPotentialTableStart(trimmedStart) && i + 1 < lines.Length && IsTableSeparatorRow(lines[i + 1]))
                {
                    FlushParagraph(paragraph);
                    i = ParseTable(lines, i);
                    continue;
                }

                paragraph.Add(trimmedStart);
                i++;
            }

            FlushParagraph(paragraph);
        }

        private void FlushParagraph(List<string> paragraph)
        {
            if (paragraph.Count == 0)
                return;
            string joined = string.Join(" ", paragraph.ToArray());
            AddInlineLine(joined, LineKind.Text, 0, 0, 0, 4);
            paragraph.Clear();
        }

        private void AddInlineLine(string text, LineKind kind, int headingLevel, float indent, float marginTop, float marginBottom)
        {
            var spec = new LineSpec();
            spec.Kind = kind;
            spec.HeadingLevel = headingLevel;
            spec.Indent = indent;
            spec.MarginTop = marginTop;
            spec.MarginBottom = marginBottom;
            spec.Runs.AddRange(ParseInline(text));
            _lineSpecs.Add(spec);
        }

        private void AddPlainLine(string text, LineKind kind, float indent, float marginTop, float marginBottom)
        {
            var spec = new LineSpec();
            spec.Kind = kind;
            spec.Indent = indent;
            spec.MarginTop = marginTop;
            spec.MarginBottom = marginBottom;
            spec.PlainInline = true;
            spec.Runs.Add(new TextRun { Text = text ?? string.Empty });
            _lineSpecs.Add(spec);
        }

        private void AddCodeLine(string text)
        {
            var spec = new LineSpec();
            spec.Kind = LineKind.Code;
            spec.CodeBlockLine = true;
            spec.Indent = 12;
            spec.MarginTop = 0;
            spec.MarginBottom = 0;
            spec.Background = new Color(0f, 0f, 0f, 0.28f);
            spec.Runs.Add(new TextRun { Text = text ?? string.Empty, Code = true });
            _lineSpecs.Add(spec);
        }

        private void AddRuleLine()
        {
            var spec = new LineSpec();
            spec.Kind = LineKind.Rule;
            spec.MarginTop = 8;
            spec.MarginBottom = 8;
            spec.IncludeTrailingNewline = true;
            _lineSpecs.Add(spec);
        }

        private int ParseTable(string[] lines, int startIndex)
        {
            int i = startIndex;
            while (i < lines.Length)
            {
                string trimmed = lines[i].TrimStart(' ', '\t');
                if (!IsPotentialTableStart(trimmed))
                    break;
                string normalized = NormalizeTableLine(trimmed);
                if (i == startIndex + 1 && IsTableSeparatorRow(trimmed))
                {
                    i++;
                    continue;
                }

                string[] cells = ParseTableCells(normalized);
                AddTableLine(cells, i == startIndex, i == startIndex ? 4 : 0, 2);
                LineSpec last = _lineSpecs[_lineSpecs.Count - 1];
                last.Background = new Color(1f, 1f, 1f, i == startIndex ? 0.07f : 0.025f);
                i++;
            }
            return i;
        }

        private void AddTableLine(string[] cells, bool isHeader, float marginTop, float marginBottom)
        {
            var spec = new LineSpec();
            spec.Kind = LineKind.Text;
            spec.Indent = 8;
            spec.MarginTop = marginTop;
            spec.MarginBottom = marginBottom;
            for (int i = 0; i < cells.Length; i++)
            {
                if (i > 0)
                    spec.Runs.Add(new TextRun { Text = "    " });

                string cell = cells[i] ?? string.Empty;
                if (isHeader)
                {
                    spec.Runs.Add(new TextRun { Text = cell, Bold = true });
                }
                else if (i == 0)
                {
                    spec.Runs.Add(new TextRun { Text = UnescapeMarkdownSyntax(cell), Code = LooksLikeMarkdownSyntax(cell) });
                }
                else
                {
                    List<TextRun> parsed = ParseInline(UnescapeMarkdownSyntax(cell));
                    for (int r = 0; r < parsed.Count; r++)
                        spec.Runs.Add(parsed[r]);
                }
            }
            _lineSpecs.Add(spec);
        }

        private void ParseDiff(string text)
        {
            string normalized = (text ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n");
            string[] lines = normalized.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i];
                var spec = new LineSpec();
                spec.Kind = LineKind.Diff;
                spec.Indent = 10;
                spec.MarginTop = i == 0 ? 4 : 0;
                spec.MarginBottom = i == lines.Length - 1 ? 4 : 0;
                spec.Runs.Add(new TextRun { Text = raw, Code = true });

                if (raw.StartsWith("+", StringComparison.Ordinal) && !raw.StartsWith("+++", StringComparison.Ordinal))
                {
                    spec.Background = new Color(0.1f, 0.45f, 0.22f, 0.25f);
                    spec.TextColor = new Color(0.45f, 0.95f, 0.6f, 1f);
                    spec.HasTextColor = true;
                }
                else if (raw.StartsWith("-", StringComparison.Ordinal) && !raw.StartsWith("---", StringComparison.Ordinal))
                {
                    spec.Background = new Color(0.55f, 0.12f, 0.14f, 0.25f);
                    spec.TextColor = new Color(1f, 0.45f, 0.45f, 1f);
                    spec.HasTextColor = true;
                }
                else if (raw.StartsWith("@@", StringComparison.Ordinal))
                {
                    spec.Background = new Color(0.12f, 0.28f, 0.6f, 0.22f);
                    spec.TextColor = new Color(0.45f, 0.7f, 1f, 1f);
                    spec.HasTextColor = true;
                }
                else
                {
                    spec.TextColor = new Color(0.7f, 0.72f, 0.78f, 1f);
                    spec.HasTextColor = true;
                }

                _lineSpecs.Add(spec);
            }
        }

        private void BuildPlainText()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < _lineSpecs.Count; i++)
            {
                LineSpec spec = _lineSpecs[i];
                for (int r = 0; r < spec.Runs.Count; r++)
                    sb.Append(spec.Runs[r].Text);
                if (spec.IncludeTrailingNewline && i < _lineSpecs.Count - 1)
                    sb.Append('\n');
            }
            _plainText = sb.ToString();
            if (_selectionAnchor > _plainText.Length)
                _selectionAnchor = _plainText.Length;
            if (_selectionFocus > _plainText.Length)
                _selectionFocus = _plainText.Length;
        }

        private void RebuildLayout()
        {
            if (panel == null)
                return;

            _lastLayoutWidth = contentRect.width;
            if (_lastLayoutWidth <= 1f)
                _lastLayoutWidth = resolvedStyle.width;
            if (_lastLayoutWidth <= 1f)
                _lastLayoutWidth = 480f;

            _backgroundLayer.Clear();
            _selectionLayer.Clear();
            _contentLayer.Clear();
            _visualLines.Clear();
            _backgrounds.Clear();

            float y = 0;
            int plainIndex = 0;
            for (int i = 0; i < _lineSpecs.Count; i++)
            {
                LineSpec spec = _lineSpecs[i];
                AddSpacer(spec.MarginTop);
                y += spec.MarginTop;

                if (spec.Kind == LineKind.Rule)
                {
                    AddRuleVisual(y);
                    y += 1f + spec.MarginBottom;
                    AddSpacer(spec.MarginBottom);
                    if (spec.IncludeTrailingNewline && i < _lineSpecs.Count - 1)
                        plainIndex++;
                    continue;
                }

                int linePlainStart = plainIndex;
                List<VisualLine> created = AddWrappedLineVisuals(spec, ref y, ref plainIndex);
                if (spec.Background.a > 0 && created.Count > 0)
                {
                    VisualLine first = created[0];
                    VisualLine last = created[created.Count - 1];
                    _backgrounds.Add(new BackgroundBox
                    {
                        X = spec.CodeBlockLine || spec.Kind == LineKind.Diff ? 0 : spec.Indent,
                        Y = first.Y - 2,
                        Width = Mathf.Max(40f, _lastLayoutWidth - (spec.CodeBlockLine || spec.Kind == LineKind.Diff ? 0 : spec.Indent) - 16f),
                        Height = (last.Y + last.Height) - first.Y + 4,
                        Color = spec.Background,
                        Radius = spec.CodeBlockLine ? 5 : 0
                    });
                }

                y += spec.MarginBottom;
                AddSpacer(spec.MarginBottom);
                if (spec.IncludeTrailingNewline && i < _lineSpecs.Count - 1)
                {
                    plainIndex++;
                    if (created.Count > 0)
                        created[created.Count - 1].PlainEnd = plainIndex;
                }

                if (linePlainStart == plainIndex && spec.Runs.Count == 0)
                    plainIndex++;
            }

            DrawBackgrounds();
            RebuildSelectionVisuals();
            MarkDirtyRepaint();
        }

        private void AddSpacer(float height)
        {
            if (height <= 0)
                return;
            var spacer = new VisualElement();
            spacer.pickingMode = PickingMode.Ignore;
            spacer.style.height = height;
            spacer.style.flexShrink = 0;
            _contentLayer.Add(spacer);
        }

        private List<VisualLine> AddWrappedLineVisuals(LineSpec spec, ref float y, ref int plainIndex)
        {
            var created = new List<VisualLine>();
            float fontSize = GetFontSize(spec);
            float lineHeight = Mathf.Ceil(fontSize * 1.45f);
            float availableWidth = Mathf.Max(40f, _lastLayoutWidth - spec.Indent - 16f);

            VisualElement row = CreateRow(y, lineHeight, spec.Indent);
            VisualLine visualLine = CreateVisualLine(y, lineHeight, spec.Indent, plainIndex);
            float x = spec.Indent;

            for (int r = 0; r < spec.Runs.Count; r++)
            {
                TextRun run = spec.Runs[r];
                string text = run.Text ?? string.Empty;
                int pos = 0;
                while (pos < text.Length || (text.Length == 0 && pos == 0))
                {
                    string remaining = text.Length == 0 ? string.Empty : text.Substring(pos);
                    if (remaining.Length > 0 && remaining[0] == '\n')
                    {
                        FinishRow(row, visualLine, created);
                        y += lineHeight;
                        row = CreateRow(y, lineHeight, spec.Indent);
                        visualLine = CreateVisualLine(y, lineHeight, spec.Indent, plainIndex);
                        x = spec.Indent;
                        pos++;
                        plainIndex++;
                        continue;
                    }

                    string segment = TakeFittingSegment(remaining, fontSize, availableWidth - (x - spec.Indent));
                    if (segment.Length == 0 && x > spec.Indent + 0.1f)
                    {
                        FinishRow(row, visualLine, created);
                        y += lineHeight;
                        row = CreateRow(y, lineHeight, spec.Indent);
                        visualLine = CreateVisualLine(y, lineHeight, spec.Indent, plainIndex);
                        x = spec.Indent;
                        continue;
                    }
                    if (segment.Length == 0)
                        segment = remaining.Length > 0 ? remaining.Substring(0, 1) : string.Empty;

                    TextRun segmentRun = run.CloneWithText(segment);
                    Label label = CreateRunLabel(segmentRun, spec);
                    row.Add(label);

                    float width = MeasureRun(segmentRun, fontSize);
                    var box = new RunBox();
                    box.PlainStart = plainIndex;
                    box.PlainEnd = plainIndex + segment.Length;
                    box.X = x;
                    box.Width = width;
                    box.Run = segmentRun;
                    box.CharOffsets = BuildCharOffsets(segmentRun, fontSize);
                    visualLine.Runs.Add(box);

                    x += width;
                    plainIndex += segment.Length;
                    visualLine.PlainEnd = plainIndex;
                    pos += segment.Length;

                    if (text.Length == 0)
                        break;
                }
            }

            FinishRow(row, visualLine, created);
            y += lineHeight;
            return created;
        }

        private VisualElement CreateRow(float y, float lineHeight, float indent)
        {
            var row = new VisualElement();
            row.pickingMode = PickingMode.Ignore;
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexWrap = Wrap.NoWrap;
            row.style.height = lineHeight;
            row.style.marginLeft = indent;
            row.style.minWidth = 0;
            row.style.width = Length.Percent(100);
            _contentLayer.Add(row);
            return row;
        }

        private static VisualLine CreateVisualLine(float y, float lineHeight, float indent, int plainStart)
        {
            var visualLine = new VisualLine();
            visualLine.Y = y;
            visualLine.Height = lineHeight;
            visualLine.Indent = indent;
            visualLine.PlainStart = plainStart;
            visualLine.PlainEnd = plainStart;
            return visualLine;
        }

        private void FinishRow(VisualElement row, VisualLine visualLine, List<VisualLine> created)
        {
            if (row.childCount == 0)
            {
                var spacer = new Label(" ");
                spacer.pickingMode = PickingMode.Ignore;
                spacer.style.opacity = 0;
                row.Add(spacer);
            }
            _visualLines.Add(visualLine);
            created.Add(visualLine);
        }

        private Label CreateRunLabel(TextRun run, LineSpec spec)
        {
            var label = new Label(run.Text);
            label.pickingMode = PickingMode.Ignore;
            label.focusable = false;
            label.style.whiteSpace = WhiteSpace.NoWrap;
            label.style.fontSize = GetFontSize(spec);
            label.style.marginRight = 0;
            label.style.marginLeft = 0;
            label.style.paddingLeft = run.Code && spec.Kind != LineKind.Code && spec.Kind != LineKind.Diff ? 3 : 0;
            label.style.paddingRight = run.Code && spec.Kind != LineKind.Code && spec.Kind != LineKind.Diff ? 3 : 0;

            if (run.Bold && run.Italic)
                label.style.unityFontStyleAndWeight = FontStyle.BoldAndItalic;
            else if (run.Bold || spec.Kind == LineKind.Heading)
                label.style.unityFontStyleAndWeight = FontStyle.Bold;
            else if (run.Italic)
                label.style.unityFontStyleAndWeight = FontStyle.Italic;

            if (spec.HasTextColor)
                label.style.color = spec.TextColor;
            else if (run.LinkUrl != null)
                label.style.color = new Color(0.43f, 0.66f, 1f, 1f);
            else if (run.Strike)
                label.style.color = new Color(0.62f, 0.64f, 0.7f, 1f);
            else if (spec.Kind == LineKind.Quote)
                label.style.color = new Color(0.72f, 0.74f, 0.82f, 1f);

            if (run.Code)
            {
                if (spec.Kind != LineKind.Code && spec.Kind != LineKind.Diff)
                {
                    label.style.backgroundColor = new Color(1f, 1f, 1f, 0.08f);
                    label.style.borderTopLeftRadius = 3;
                    label.style.borderTopRightRadius = 3;
                    label.style.borderBottomLeftRadius = 3;
                    label.style.borderBottomRightRadius = 3;
                    label.style.color = new Color(1f, 0.68f, 0.36f, 1f);
                }
            }

            if (run.LinkUrl != null)
                label.tooltip = run.LinkUrl;

            return label;
        }

        private void AddRuleVisual(float y)
        {
            var row = new VisualElement();
            row.pickingMode = PickingMode.Ignore;
            row.style.height = 1;
            row.style.marginTop = 0;
            row.style.marginBottom = 0;
            row.style.backgroundColor = new Color(1f, 1f, 1f, 0.12f);
            _contentLayer.Add(row);
            _backgrounds.Add(new BackgroundBox
            {
                X = 0,
                Y = y,
                Width = _lastLayoutWidth,
                Height = 1,
                Color = new Color(1f, 1f, 1f, 0.12f),
                Radius = 0
            });
        }

        private void DrawBackgrounds()
        {
            _backgroundLayer.Clear();
            for (int i = 0; i < _backgrounds.Count; i++)
            {
                BackgroundBox bg = _backgrounds[i];
                var rect = new VisualElement();
                rect.pickingMode = PickingMode.Ignore;
                rect.style.position = Position.Absolute;
                rect.style.left = bg.X;
                rect.style.top = bg.Y;
                rect.style.width = bg.Width;
                rect.style.height = bg.Height;
                rect.style.backgroundColor = bg.Color;
                if (bg.Radius > 0)
                {
                    rect.style.borderTopLeftRadius = bg.Radius;
                    rect.style.borderTopRightRadius = bg.Radius;
                    rect.style.borderBottomLeftRadius = bg.Radius;
                    rect.style.borderBottomRightRadius = bg.Radius;
                }
                _backgroundLayer.Add(rect);
            }
        }

        private string TakeFittingSegment(string text, float fontSize, float widthLeft)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            if (text.Length == 0)
                return string.Empty;

            int newline = text.IndexOf('\n');
            if (newline == 0)
                return string.Empty;
            if (newline > 0)
                text = text.Substring(0, newline);

            if (MeasureText(text, fontSize) <= widthLeft)
                return text;

            int low = 1;
            int high = text.Length;
            int best = 1;
            while (low <= high)
            {
                int mid = (low + high) / 2;
                string candidate = text.Substring(0, mid);
                if (MeasureText(candidate, fontSize) <= widthLeft)
                {
                    best = mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            int breakAt = -1;
            for (int i = best - 1; i > 0; i--)
            {
                if (char.IsWhiteSpace(text[i]))
                {
                    breakAt = i + 1;
                    break;
                }
            }
            if (breakAt > 0)
                best = breakAt;

            return text.Substring(0, Mathf.Clamp(best, 1, text.Length));
        }

        private float[] BuildCharOffsets(TextRun run, float fontSize)
        {
            string text = run.Text ?? string.Empty;
            var offsets = new float[text.Length + 1];
            offsets[0] = 0;
            for (int i = 1; i <= text.Length; i++)
                offsets[i] = MeasureText(text.Substring(0, i), fontSize);
            return offsets;
        }

        private float MeasureRun(TextRun run, float fontSize)
        {
            return MeasureText(run.Text ?? string.Empty, fontSize);
        }

        private float MeasureText(string text, float fontSize)
        {
            if (string.IsNullOrEmpty(text))
                return 0;
            _measureLabel.style.fontSize = fontSize;
            Vector2 size = _measureLabel.MeasureTextSize(text, 0, VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined);
            if (float.IsNaN(size.x) || size.x <= 0)
                return text.Length * fontSize * 0.55f;
            return size.x;
        }

        private static float GetFontSize(LineSpec spec)
        {
            if (spec.Kind == LineKind.Code || spec.Kind == LineKind.Diff)
                return 12f;
            if (spec.Kind == LineKind.Heading)
            {
                if (spec.HeadingLevel <= 1)
                    return 18f;
                if (spec.HeadingLevel == 2)
                    return 16f;
                if (spec.HeadingLevel == 3)
                    return 14f;
                return 13f;
            }
            return 13f;
        }

        private void RebuildSelectionVisuals()
        {
            _selectionLayer.Clear();
            int start;
            int end;
            if (!GetSelectionRange(out start, out end))
                return;

            for (int i = 0; i < _visualLines.Count; i++)
            {
                VisualLine line = _visualLines[i];
                int lineStart = line.PlainStart;
                int lineEnd = line.PlainEnd;
                if (lineEnd < lineStart)
                    lineEnd = lineStart;
                if (end < lineStart || start > lineEnd)
                    continue;

                int from = Mathf.Max(start, lineStart);
                int to = Mathf.Min(end, lineEnd);
                if (to < from)
                    continue;

                float x1 = GetXForPlainIndex(line, from);
                float x2 = GetXForPlainIndex(line, to);
                if (Mathf.Abs(x2 - x1) < 1f)
                    x2 = x1 + 2f;

                var rect = new VisualElement();
                rect.pickingMode = PickingMode.Ignore;
                rect.style.position = Position.Absolute;
                rect.style.left = x1;
                rect.style.top = line.Y + 1;
                rect.style.width = Mathf.Max(2f, x2 - x1);
                rect.style.height = Mathf.Max(2f, line.Height - 2);
                rect.style.backgroundColor = new Color(0.43f, 0.42f, 0.95f, 0.35f);
                _selectionLayer.Add(rect);
            }
        }

        private bool GetSelectionRange(out int start, out int end)
        {
            start = 0;
            end = 0;
            if (_selectionAnchor < 0 || _selectionFocus < 0)
                return false;
            start = Mathf.Min(_selectionAnchor, _selectionFocus);
            end = Mathf.Max(_selectionAnchor, _selectionFocus);
            return end > start;
        }

        private float GetXForPlainIndex(VisualLine line, int index)
        {
            if (line.Runs.Count == 0)
                return line.Indent;
            for (int i = 0; i < line.Runs.Count; i++)
            {
                RunBox run = line.Runs[i];
                if (index <= run.PlainEnd)
                {
                    int local = Mathf.Clamp(index - run.PlainStart, 0, run.CharOffsets.Length - 1);
                    return run.X + run.CharOffsets[local];
                }
            }
            RunBox last = line.Runs[line.Runs.Count - 1];
            return last.X + last.Width;
        }

        private int HitTestPlainIndex(Vector2 localPosition, out string linkUrl)
        {
            linkUrl = null;
            if (_visualLines.Count == 0)
                return 0;

            VisualLine line = _visualLines[0];
            for (int i = 0; i < _visualLines.Count; i++)
            {
                VisualLine candidate = _visualLines[i];
                if (localPosition.y >= candidate.Y && localPosition.y <= candidate.Y + candidate.Height)
                {
                    line = candidate;
                    break;
                }
                if (localPosition.y > candidate.Y)
                    line = candidate;
            }

            if (line.Runs.Count == 0)
                return line.PlainStart;

            for (int r = 0; r < line.Runs.Count; r++)
            {
                RunBox run = line.Runs[r];
                if (localPosition.x <= run.X + run.Width)
                {
                    linkUrl = run.Run.LinkUrl;
                    for (int i = 0; i < run.CharOffsets.Length - 1; i++)
                    {
                        float mid = run.X + (run.CharOffsets[i] + run.CharOffsets[i + 1]) * 0.5f;
                        if (localPosition.x <= mid)
                            return run.PlainStart + i;
                    }
                    return run.PlainEnd;
                }
            }

            RunBox last = line.Runs[line.Runs.Count - 1];
            linkUrl = last.Run.LinkUrl;
            return last.PlainEnd;
        }

        private void OnPointerDown(PointerDownEvent evt)
        {
            if (evt.button != 0)
                return;

            Focus();
            _lastPointerIndex = HitTestPlainIndex(evt.localPosition, out _lastPointerLink);
            _selectionAnchor = _lastPointerIndex;
            _selectionFocus = _lastPointerIndex;
            _isDragging = true;
            _capturedPointerId = evt.pointerId;
            this.CapturePointer(evt.pointerId);
            RebuildSelectionVisuals();
            evt.StopImmediatePropagation();
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (!_isDragging || evt.pointerId != _capturedPointerId)
                return;
            string linkUrl;
            _selectionFocus = HitTestPlainIndex(evt.localPosition, out linkUrl);
            RebuildSelectionVisuals();
            evt.StopImmediatePropagation();
        }

        private void OnPointerUp(PointerUpEvent evt)
        {
            if (!_isDragging || evt.pointerId != _capturedPointerId)
                return;

            string linkUrl;
            int upIndex = HitTestPlainIndex(evt.localPosition, out linkUrl);
            _selectionFocus = upIndex;
            _isDragging = false;
            this.ReleasePointer(evt.pointerId);
            _capturedPointerId = -1;

            int start;
            int end;
            if (!GetSelectionRange(out start, out end))
            {
                ClearSelection();
                if (!string.IsNullOrEmpty(linkUrl) && upIndex == _lastPointerIndex && string.Equals(linkUrl, _lastPointerLink, StringComparison.Ordinal))
                    Application.OpenURL(linkUrl);
            }
            else
            {
                RebuildSelectionVisuals();
            }

            evt.StopImmediatePropagation();
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            if ((evt.ctrlKey || evt.commandKey) && evt.keyCode == KeyCode.A)
            {
                _selectionAnchor = 0;
                _selectionFocus = _plainText.Length;
                RebuildSelectionVisuals();
                evt.StopImmediatePropagation();
                return;
            }

            if ((evt.ctrlKey || evt.commandKey) && evt.keyCode == KeyCode.C)
            {
                string selected = GetSelectedText();
                if (!string.IsNullOrEmpty(selected))
                    GUIUtility.systemCopyBuffer = selected;
                else if (!string.IsNullOrEmpty(_plainText))
                    GUIUtility.systemCopyBuffer = _plainText;
                evt.StopImmediatePropagation();
                return;
            }

            if (evt.keyCode == KeyCode.Escape)
            {
                ClearSelection();
                evt.StopImmediatePropagation();
            }
        }

        private string GetSelectedText()
        {
            int start;
            int end;
            if (!GetSelectionRange(out start, out end))
                return string.Empty;
            start = Mathf.Clamp(start, 0, _plainText.Length);
            end = Mathf.Clamp(end, 0, _plainText.Length);
            if (end <= start)
                return string.Empty;
            return _plainText.Substring(start, end - start);
        }

        private void ClearSelection()
        {
            _selectionAnchor = -1;
            _selectionFocus = -1;
            _isDragging = false;
            _capturedPointerId = -1;
            if (_selectionLayer != null)
                _selectionLayer.Clear();
        }

        private static List<TextRun> ParseInline(string text)
        {
            var runs = new List<TextRun>();
            if (string.IsNullOrEmpty(text))
                return runs;

            int pos = 0;
            while (pos < text.Length)
            {
                int next = FindNextInlineMarker(text, pos);
                if (next < 0)
                {
                    AddPlainRun(runs, text.Substring(pos));
                    break;
                }
                if (next > pos)
                {
                    AddPlainRun(runs, text.Substring(pos, next - pos));
                    pos = next;
                    continue;
                }

                if (StartsWithAt(text, pos, "***"))
                {
                    int close = text.IndexOf("***", pos + 3, StringComparison.Ordinal);
                    if (close >= 0)
                    {
                        AddStyledRun(runs, text.Substring(pos + 3, close - pos - 3), true, true, false, false, null);
                        pos = close + 3;
                        continue;
                    }
                }
                if (StartsWithAt(text, pos, "**"))
                {
                    int close = text.IndexOf("**", pos + 2, StringComparison.Ordinal);
                    if (close >= 0)
                    {
                        AddStyledRun(runs, text.Substring(pos + 2, close - pos - 2), true, false, false, false, null);
                        pos = close + 2;
                        continue;
                    }
                }
                if (StartsWithAt(text, pos, "~~"))
                {
                    int close = text.IndexOf("~~", pos + 2, StringComparison.Ordinal);
                    if (close >= 0)
                    {
                        AddStyledRun(runs, text.Substring(pos + 2, close - pos - 2), false, false, false, true, null);
                        pos = close + 2;
                        continue;
                    }
                }
                if (text[pos] == '`')
                {
                    int close = text.IndexOf("`", pos + 1, StringComparison.Ordinal);
                    if (close >= 0)
                    {
                        AddStyledRun(runs, text.Substring(pos + 1, close - pos - 1), false, false, true, false, null);
                        pos = close + 1;
                        continue;
                    }
                }
                if (StartsWithAt(text, pos, "!["))
                {
                    int closeBracket = text.IndexOf("]", pos + 2, StringComparison.Ordinal);
                    if (closeBracket >= 0 && closeBracket + 1 < text.Length && text[closeBracket + 1] == '(')
                    {
                        int closeParen = text.IndexOf(")", closeBracket + 2, StringComparison.Ordinal);
                        if (closeParen >= 0)
                        {
                            string altText = text.Substring(pos + 2, closeBracket - pos - 2);
                            string url = text.Substring(closeBracket + 2, closeParen - closeBracket - 2);
                            AddStyledRun(runs, string.IsNullOrEmpty(altText) ? "Image" : altText, false, false, false, false, url);
                            pos = closeParen + 1;
                            continue;
                        }
                    }
                }
                if (text[pos] == '*')
                {
                    int close = text.IndexOf("*", pos + 1, StringComparison.Ordinal);
                    if (close >= 0)
                    {
                        AddStyledRun(runs, text.Substring(pos + 1, close - pos - 1), false, true, false, false, null);
                        pos = close + 1;
                        continue;
                    }
                }
                if (text[pos] == '[')
                {
                    int closeBracket = text.IndexOf("]", pos + 1, StringComparison.Ordinal);
                    if (closeBracket >= 0 && closeBracket + 1 < text.Length && text[closeBracket + 1] == '(')
                    {
                        int closeParen = text.IndexOf(")", closeBracket + 2, StringComparison.Ordinal);
                        if (closeParen >= 0)
                        {
                            string linkText = text.Substring(pos + 1, closeBracket - pos - 1);
                            string url = text.Substring(closeBracket + 2, closeParen - closeBracket - 2);
                            AddStyledRun(runs, linkText, false, false, false, false, url);
                            pos = closeParen + 1;
                            continue;
                        }
                    }
                }

                AddPlainRun(runs, text.Substring(pos, 1));
                pos++;
            }

            return runs;
        }

        private static int FindNextInlineMarker(string text, int start)
        {
            int next = -1;
            string[] markers = new string[] { "***", "**", "~~", "`", "![", "*", "[" };
            for (int i = 0; i < markers.Length; i++)
            {
                int found = text.IndexOf(markers[i], start, StringComparison.Ordinal);
                if (found >= 0 && (next < 0 || found < next))
                    next = found;
            }
            return next;
        }

        private static bool StartsWithAt(string text, int pos, string marker)
        {
            if (pos + marker.Length > text.Length)
                return false;
            return string.CompareOrdinal(text, pos, marker, 0, marker.Length) == 0;
        }

        private static void AddPlainRun(List<TextRun> runs, string text)
        {
            AddStyledRun(runs, text, false, false, false, false, null);
        }

        private static void AddStyledRun(List<TextRun> runs, string text, bool bold, bool italic, bool code, bool strike, string linkUrl)
        {
            if (string.IsNullOrEmpty(text))
                return;
            runs.Add(new TextRun
            {
                Text = text,
                Bold = bold,
                Italic = italic,
                Code = code,
                Strike = strike,
                LinkUrl = linkUrl
            });
        }

        private static bool LooksLikeMarkdownSyntax(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;
            return text.IndexOf("*", StringComparison.Ordinal) >= 0 ||
                   text.IndexOf("~", StringComparison.Ordinal) >= 0 ||
                   text.IndexOf("`", StringComparison.Ordinal) >= 0 ||
                   text.IndexOf("[", StringComparison.Ordinal) >= 0;
        }

        private static string UnescapeMarkdownSyntax(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            return text
                .Replace("\\*", "*")
                .Replace("\\~", "~")
                .Replace("\\`", "`")
                .Replace("\\[", "[")
                .Replace("\\]", "]")
                .Replace("\\(", "(")
                .Replace("\\)", ")")
                .Replace("\\|", "|");
        }

        private static int GetHeadingLevel(string trimmed)
        {
            if (string.IsNullOrEmpty(trimmed) || trimmed[0] != '#')
                return 0;
            int level = 0;
            while (level < trimmed.Length && trimmed[level] == '#')
                level++;
            if (level > 6)
                return 0;
            if (level < trimmed.Length && trimmed[level] == ' ')
                return level;
            return 0;
        }

        private static bool TryParseListItem(string trimmed, out string marker, out string text)
        {
            marker = null;
            text = null;
            if (trimmed.Length >= 2 && (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal) || trimmed.StartsWith("+ ", StringComparison.Ordinal)))
            {
                marker = "·";
                text = trimmed.Substring(2);
                return true;
            }

            int j = 0;
            while (j < trimmed.Length && char.IsDigit(trimmed[j]))
                j++;
            if (j > 0 && j + 1 < trimmed.Length && trimmed[j] == '.' && trimmed[j + 1] == ' ')
            {
                marker = trimmed.Substring(0, j + 1);
                text = trimmed.Substring(j + 2);
                return true;
            }

            return false;
        }

        private static bool IsHorizontalRule(string trimmed)
        {
            if (trimmed.Length < 3)
                return false;
            char c = trimmed[0];
            if (c != '-' && c != '_' && c != '=' && c != '*')
                return false;
            int count = 0;
            for (int i = 0; i < trimmed.Length; i++)
            {
                if (trimmed[i] == c)
                {
                    count++;
                    continue;
                }
                if (trimmed[i] != ' ')
                    return false;
            }
            return count >= 3;
        }

        private static bool IsPotentialTableStart(string trimmed)
        {
            return !string.IsNullOrEmpty(trimmed) && (trimmed[0] == '|' || trimmed.StartsWith("\\|", StringComparison.Ordinal));
        }

        private static bool IsTableSeparatorRow(string line)
        {
            string trimmed = NormalizeTableLine(line.Trim());
            if (trimmed.IndexOf('-') < 0)
                return false;
            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                if (c != '|' && c != '-' && c != ':' && c != ' ')
                    return false;
            }
            return true;
        }

        private static string[] ParseTableCells(string line)
        {
            string trimmed = NormalizeTableLine(line.Trim());
            if (trimmed.Length > 0 && trimmed[0] == '|')
                trimmed = trimmed.Substring(1);
            if (trimmed.Length > 0 && trimmed[trimmed.Length - 1] == '|')
                trimmed = trimmed.Substring(0, trimmed.Length - 1);
            string[] parts = trimmed.Split('|');
            for (int i = 0; i < parts.Length; i++)
                parts[i] = parts[i].Trim();
            return parts;
        }

        private static string NormalizeTableLine(string line)
        {
            return (line ?? string.Empty).Replace("\\|", "|");
        }
    }
}
