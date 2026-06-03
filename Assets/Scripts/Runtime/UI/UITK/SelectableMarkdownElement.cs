using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK
{
    /// <summary>
    /// Native chat text engine. Owns a markdown/diff document model and a real line-box
    /// layout (one normal-flow VisualElement per block; inline text tokenized into words so
    /// Unity wraps and measures glyphs). Heights come from layout, never a multiplier.
    /// Selection geometry is read back from the resolved layout, so it stays correct across
    /// fonts/DPI/wrap. Streaming reuses unchanged leading blocks (block-level reconcile) so
    /// appending tokens does not reflow or jitter the content above.
    ///
    /// Public API is intentionally frozen: SetMarkdown, SetDiff, PlainText.
    /// </summary>
    internal class SelectableMarkdownElement : VisualElement
    {
        private enum BlockKind
        {
            Paragraph,
            Heading,
            Quote,
            Bullet,
            Numbered,
            CodeBlock,
            Rule,
            Table,
            DiffLine
        }

        private class InlineRun
        {
            public string Text;
            public bool Bold;
            public bool Italic;
            public bool Code;
            public bool Strike;
            public string LinkUrl;
        }

        private class TableRow
        {
            public readonly List<List<InlineRun>> Cells = new List<List<InlineRun>>();
            public bool IsHeader;
            public bool Alt;
            public bool IsLast;
        }

        private class Block
        {
            public BlockKind Kind;
            public int HeadingLevel;
            public string Marker;
            public string CodeText;
            public string CodeLanguage;
            public int Indent;
            public readonly List<InlineRun> Inlines = new List<InlineRun>();
            public List<TableRow> TableRows;

            public bool DiffHasColor;
            public Color DiffColor;
            public bool DiffHasBackground;
            public Color DiffBackground;

            // Computed by FinalizeBlock.
            public string PlainText = string.Empty;
            public string Signature = string.Empty;

            // Filled when the block element is built; references live Labels.
            public readonly List<PlacedToken> Tokens = new List<PlacedToken>();
            public int GlobalStart;
        }

        private class PlacedToken
        {
            public Label Label;
            public InlineRun Run;
            public int LocalStart;
            public int LocalEnd;
            public int GlobalStart;
            public int GlobalEnd;
            public Rect Rect;
            public bool HasRect;
            public float[] CharOffsets;
        }

        private class VisualTextLine
        {
            public readonly List<PlacedToken> Tokens = new List<PlacedToken>();
            public float Y;
            public float Height;
            public int PlainStart;
            public int PlainEnd;
        }

        private readonly VisualElement _selectionLayer;
        private readonly VisualElement _content;

        private readonly List<Block> _blocks = new List<Block>();
        private readonly List<VisualElement> _blockElements = new List<VisualElement>();
        private readonly List<PlacedToken> _tokens = new List<PlacedToken>();
        private readonly List<VisualTextLine> _lines = new List<VisualTextLine>();

        private string _sourceText = string.Empty;
        private string _plainText = string.Empty;

        private int _selectionAnchor = -1;
        private int _selectionFocus = -1;
        private bool _isDragging;
        private int _capturedPointerId = -1;
        private int _downIndex = -1;
        private string _downLink;
        private bool _captureScheduled;

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

            _selectionLayer = new VisualElement();
            _selectionLayer.pickingMode = PickingMode.Ignore;
            _selectionLayer.style.position = Position.Absolute;
            _selectionLayer.style.left = 0;
            _selectionLayer.style.top = 0;
            _selectionLayer.style.right = 0;
            _selectionLayer.style.bottom = 0;

            _content = new VisualElement();
            _content.pickingMode = PickingMode.Ignore;
            _content.style.flexDirection = FlexDirection.Column;
            _content.style.minWidth = 0;
            _content.style.width = Length.Percent(100);

            // Selection layer must render ABOVE the content: code/diff blocks have an opaque
            // background (.markdown-codeblock => var(--bg-0)) that would otherwise occlude a
            // highlight drawn beneath them, making selection look broken inside code/diff.
            // The highlight color is translucent (alpha ~0.35), so text stays readable on top.
            // The layer is pickingMode = Ignore, so it never intercepts pointer events.
            Add(_content);
            Add(_selectionLayer);

            RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            RegisterCallback<PointerDownEvent>(OnPointerDown);
            RegisterCallback<PointerMoveEvent>(OnPointerMove);
            RegisterCallback<PointerUpEvent>(OnPointerUp);
            RegisterCallback<KeyDownEvent>(OnKeyDown);
        }

        // ===================== Public API =====================

        public void SetMarkdown(string text)
        {
            _sourceText = StripAnsi(text);
            var newBlocks = new List<Block>();
            ParseMarkdown(_sourceText, newBlocks);
            ApplyBlocks(newBlocks);
        }

        public void SetDiff(string text)
        {
            _sourceText = StripAnsi(text);
            var newBlocks = new List<Block>();
            ParseDiff(_sourceText, newBlocks);
            ApplyBlocks(newBlocks);
        }

        private void ApplyBlocks(List<Block> newBlocks)
        {
            ReconcileBlocks(newBlocks);
            AssembleTokensAndPlainText();
            ClampSelection();
            ScheduleCapture();
        }

        // ===================== Streaming reconcile =====================

        private void ReconcileBlocks(List<Block> newBlocks)
        {
            int common = 0;
            while (common < _blocks.Count && common < newBlocks.Count &&
                   string.Equals(_blocks[common].Signature, newBlocks[common].Signature, StringComparison.Ordinal))
            {
                common++;
            }

            for (int i = _blockElements.Count - 1; i >= common; i--)
            {
                _content.Remove(_blockElements[i]);
                _blockElements.RemoveAt(i);
            }
            if (_blocks.Count > common)
                _blocks.RemoveRange(common, _blocks.Count - common);

            for (int i = common; i < newBlocks.Count; i++)
            {
                Block block = newBlocks[i];
                VisualElement element = BuildBlockElement(block);
                _content.Add(element);
                _blockElements.Add(element);
                _blocks.Add(block);
            }
        }

        private void AssembleTokensAndPlainText()
        {
            _tokens.Clear();
            var sb = new StringBuilder();
            int offset = 0;
            for (int i = 0; i < _blocks.Count; i++)
            {
                Block block = _blocks[i];
                block.GlobalStart = offset;
                sb.Append(block.PlainText);
                for (int t = 0; t < block.Tokens.Count; t++)
                {
                    PlacedToken token = block.Tokens[t];
                    token.GlobalStart = offset + token.LocalStart;
                    token.GlobalEnd = offset + token.LocalEnd;
                    _tokens.Add(token);
                }
                offset += block.PlainText.Length;
                if (i < _blocks.Count - 1)
                {
                    sb.Append('\n');
                    offset += 1;
                }
            }
            _plainText = sb.ToString();
        }

        // ===================== Block element construction =====================

        // Max chars per token before an unbreakable word is split so it can wrap. Sized so a chunk
        // fits comfortably inside a normal bubble width; smaller = finer wrap granularity.
        private const int InlineChunkMax = 24;
        private const int CodeChunkMax = 16;

        private VisualElement BuildBlockElement(Block block)
        {
            switch (block.Kind)
            {
                case BlockKind.Heading:
                    return BuildInlineContainer(block, "markdown-h" + block.HeadingLevel.ToString(), true);
                case BlockKind.Quote:
                    return BuildInlineContainer(block, "markdown-blockquote", false);
                case BlockKind.Bullet:
                    return BuildListItem(block, "markdown-bullet", "markdown-bullet-marker");
                case BlockKind.Numbered:
                    return BuildListItem(block, "markdown-numbered", "markdown-numbered-marker");
                case BlockKind.CodeBlock:
                    return BuildCodeBlock(block);
                case BlockKind.Rule:
                    return BuildRule();
                case BlockKind.Table:
                    return BuildTable(block);
                case BlockKind.DiffLine:
                    return BuildDiffLine(block);
                default:
                    return BuildParagraph(block);
            }
        }

        private VisualElement BuildInlineContainer(Block block, string className, bool forceRowWrap)
        {
            var container = new VisualElement();
            container.AddToClassList(className);
            container.pickingMode = PickingMode.Ignore;
            if (forceRowWrap)
            {
                container.style.flexDirection = FlexDirection.Row;
                container.style.flexWrap = Wrap.Wrap;
            }
            container.style.minWidth = 0;
            int local = 0;
            AddInlineTokens(container, block, block.Inlines, ref local);
            return container;
        }

        private VisualElement BuildListItem(Block block, string rowClass, string markerClass)
        {
            var row = new VisualElement();
            row.AddToClassList(rowClass);
            row.pickingMode = PickingMode.Ignore;
            if (block.Indent > 0)
                row.style.marginLeft = 16 + block.Indent * 8;

            int local = 0;
            string marker = block.Marker ?? string.Empty;
            var markerLabel = new Label(marker);
            markerLabel.AddToClassList(markerClass);
            markerLabel.pickingMode = PickingMode.Ignore;
            markerLabel.style.whiteSpace = WhiteSpace.NoWrap;
            AddToken(block, markerLabel, null, local, local + marker.Length);
            row.Add(markerLabel);
            // PlainText for the item is "<marker> <content>"; the single space between marker
            // and content lives in PlainText but in no token (an unselectable gap, like newlines).
            local += marker.Length + 1;

            var content = new VisualElement();
            content.pickingMode = PickingMode.Ignore;
            content.style.flexDirection = FlexDirection.Row;
            content.style.flexWrap = Wrap.Wrap;
            content.style.flexGrow = 1;
            content.style.minWidth = 0;
            AddInlineTokens(content, block, block.Inlines, ref local);
            row.Add(content);
            return row;
        }

        private VisualElement BuildCodeBlock(Block block)
        {
            var container = new VisualElement();
            container.AddToClassList("markdown-codeblock");
            container.pickingMode = PickingMode.Ignore;

            string code = block.CodeText ?? string.Empty;

            // Header chrome: language label (left) + Copy button (right). Not selectable text.
            var header = new VisualElement();
            header.pickingMode = PickingMode.Ignore;
            header.style.flexDirection = FlexDirection.Row;
            header.style.justifyContent = Justify.SpaceBetween;
            header.style.alignItems = Align.Center;
            header.style.marginBottom = 4;

            var langLabel = new Label(string.IsNullOrEmpty(block.CodeLanguage) ? "code" : block.CodeLanguage);
            langLabel.pickingMode = PickingMode.Ignore;
            langLabel.style.fontSize = 9;
            langLabel.style.color = new Color(1f, 1f, 1f, 0.4f);
            header.Add(langLabel);

            var copyButton = new Label("Copy");
            copyButton.pickingMode = PickingMode.Position;
            copyButton.style.fontSize = 9;
            copyButton.style.color = new Color(1f, 1f, 1f, 0.55f);
            copyButton.style.paddingLeft = 6;
            copyButton.style.paddingRight = 6;
            copyButton.style.paddingTop = 2;
            copyButton.style.paddingBottom = 2;
            AttachCopyHandler(copyButton, code);
            header.Add(copyButton);

            container.Add(header);

            string lang = (block.CodeLanguage ?? string.Empty).Trim().ToLowerInvariant();
            // Color as a diff when the fence says so, or when a generic/unlabeled fence
            // (```code / ```text / ```) actually contains a unified diff — models often
            // emit ```code instead of ```diff, which left the diff uncolored.
            bool isDiff = IsDiffLanguage(lang) || (IsGenericCodeLanguage(lang) && LooksLikeUnifiedDiff(code));
            bool highlight = !isDiff && IsHighlightLanguage(lang);

            string[] lines = code.Split('\n');
            int local = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                // One logical line = a flex-wrap row of fixed-size chunks. Long lines therefore wrap
                // to the container width instead of overflowing, while PlainText stays one line
                // (chunks carry continuous indices; only the real '\n' below advances the offset).
                var lineRow = NewWrapRow();
                if (line.Length == 0)
                {
                    var spacer = new Label(" ");
                    spacer.AddToClassList("markdown-codeblock-text");
                    spacer.pickingMode = PickingMode.Ignore;
                    ResetTokenSpacing(spacer, false);
                    AddToken(block, spacer, null, local, local); // zero-length: gives the empty line height
                    lineRow.Add(spacer);
                }
                else if (isDiff)
                {
                    bool hasBg = false;
                    Color bg = default(Color);
                    bool hasCol = false;
                    Color col = default(Color);
                    GetDiffLineStyle(line, out hasBg, out bg, out hasCol, out col);
                    if (hasBg)
                        lineRow.style.backgroundColor = bg;
                    EmitCodeChunks(block, lineRow, line, ref local, hasCol, col);
                }
                else if (highlight)
                {
                    List<CodeSeg> segs = HighlightLine(line);
                    for (int s = 0; s < segs.Count; s++)
                    {
                        CodeSeg seg = segs[s];
                        EmitCodeChunks(block, lineRow, seg.Text, ref local, seg.HasColor, seg.Color);
                    }
                }
                else
                {
                    EmitCodeChunks(block, lineRow, line, ref local, false, default(Color));
                }
                container.Add(lineRow);
                if (i < lines.Length - 1)
                    local += 1; // newline character in PlainText
            }
            return container;
        }

        // Splits text into fixed-size chunks (so a long, space-less code line still wraps to the
        // container width) and adds each as a selection token with continuous local indices.
        private void EmitCodeChunks(Block block, VisualElement lineRow, string text, ref int local, bool hasColor, Color color)
        {
            int p = 0;
            while (p < text.Length)
            {
                int len = Mathf.Min(CodeChunkMax, text.Length - p);
                var chunkLabel = new Label(text.Substring(p, len));
                chunkLabel.AddToClassList("markdown-codeblock-text");
                chunkLabel.pickingMode = PickingMode.Ignore;
                ResetTokenSpacing(chunkLabel, false);
                if (hasColor)
                    chunkLabel.style.color = color;
                AddToken(block, chunkLabel, null, local, local + len);
                lineRow.Add(chunkLabel);
                local += len;
                p += len;
            }
        }

        private VisualElement BuildRule()
        {
            var rule = new VisualElement();
            rule.AddToClassList("markdown-hr");
            rule.pickingMode = PickingMode.Ignore;
            return rule;
        }

        private VisualElement BuildDiffLine(Block block)
        {
            var row = new VisualElement();
            row.pickingMode = PickingMode.Ignore;
            row.style.flexDirection = FlexDirection.Row;
            row.style.paddingLeft = 10;
            row.style.paddingRight = 6;
            if (block.DiffHasBackground)
                row.style.backgroundColor = block.DiffBackground;

            string text = block.Inlines.Count > 0 ? (block.Inlines[0].Text ?? string.Empty) : string.Empty;
            var label = new Label(text);
            label.AddToClassList("markdown-codeblock-text");
            label.pickingMode = PickingMode.Ignore;
            if (block.DiffHasColor)
                label.style.color = block.DiffColor;
            AddToken(block, label, null, 0, text.Length);
            row.Add(label);
            return row;
        }

        private VisualElement BuildTable(Block block)
        {
            var table = new VisualElement();
            table.AddToClassList("markdown-table");
            table.pickingMode = PickingMode.Ignore;
            if (block.TableRows == null)
                return table;

            int local = 0;
            for (int r = 0; r < block.TableRows.Count; r++)
            {
                TableRow tableRow = block.TableRows[r];
                var rowEl = new VisualElement();
                rowEl.AddToClassList("markdown-table-row");
                rowEl.pickingMode = PickingMode.Ignore;
                if (tableRow.IsHeader)
                    rowEl.AddToClassList("markdown-table-row--header");
                else if (tableRow.Alt)
                    rowEl.AddToClassList("markdown-table-row--alt");
                if (tableRow.IsLast)
                    rowEl.AddToClassList("markdown-table-row--last");

                for (int c = 0; c < tableRow.Cells.Count; c++)
                {
                    var cellEl = new VisualElement();
                    cellEl.AddToClassList("markdown-table-cell");
                    cellEl.pickingMode = PickingMode.Ignore;
                    if (c == tableRow.Cells.Count - 1)
                        cellEl.AddToClassList("markdown-table-cell--last");
                    if (tableRow.IsHeader)
                        cellEl.AddToClassList("markdown-table-cell--header");

                    AddInlineTokens(cellEl, block, tableRow.Cells[c], ref local);
                    if (c < tableRow.Cells.Count - 1)
                        local += 1; // tab separator in PlainText
                    rowEl.Add(cellEl);
                }
                if (r < block.TableRows.Count - 1)
                    local += 1; // newline between rows in PlainText
                table.Add(rowEl);
            }
            return table;
        }

        private void AddInlineTokens(VisualElement container, Block block, List<InlineRun> runs, ref int local)
        {
            for (int i = 0; i < runs.Count; i++)
            {
                InlineRun run = runs[i];
                bool keepWhole = run.Code || run.LinkUrl != null;
                if (keepWhole)
                {
                    string text = run.Text ?? string.Empty;
                    Label label = MakeInlineLabel(text, run);
                    container.Add(label);
                    AddToken(block, label, run, local, local + text.Length);
                    local += text.Length;
                }
                else
                {
                    AddWordTokens(container, block, run, ref local);
                }
            }
        }

        private void AddWordTokens(VisualElement container, Block block, InlineRun run, ref int local)
        {
            string text = run.Text ?? string.Empty;
            int i = 0;
            while (i < text.Length)
            {
                int start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]))
                    i++;
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                    i++;
                EmitWord(container, block, run, text.Substring(start, i - start), ref local);
            }
        }

        // Emits one word as a token, splitting over-long unbreakable words into chunks so the
        // flex-wrap row can break them (CSS word-break: break-all). Without this a single long
        // word (URL / hash / a no-space string) overflows the bubble because NoWrap labels never break.
        private void EmitWord(VisualElement container, Block block, InlineRun run, string word, ref int local)
        {
            if (word.Length == 0)
                return;
            if (word.Length <= InlineChunkMax)
            {
                Label label = MakeInlineLabel(word, run);
                container.Add(label);
                AddToken(block, label, run, local, local + word.Length);
                local += word.Length;
                return;
            }
            int p = 0;
            while (p < word.Length)
            {
                int len = Mathf.Min(InlineChunkMax, word.Length - p);
                string chunk = word.Substring(p, len);
                Label label = MakeInlineLabel(chunk, run);
                container.Add(label);
                AddToken(block, label, run, local, local + len);
                local += len;
                p += len;
            }
        }

        private static VisualElement NewWrapRow()
        {
            var row = new VisualElement();
            row.pickingMode = PickingMode.Ignore;
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexWrap = Wrap.Wrap;
            row.style.minWidth = 0;
            return row;
        }

        // Paragraph renders as a column of flex-wrap rows so hard line breaks ('\n', e.g. the user's
        // Shift+Enter newlines) are preserved instead of being collapsed into a single wrapped line.
        private VisualElement BuildParagraph(Block block)
        {
            var container = new VisualElement();
            container.AddToClassList("markdown-paragraph");
            container.pickingMode = PickingMode.Ignore;
            container.style.flexDirection = FlexDirection.Column; // override the class's row direction
            // The class sets flex-wrap: wrap (for the old single-row layout). On a column of line-rows
            // that makes a 2-line paragraph wrap its rows into side-by-side columns ("что"+"тут?" →
            // "чтотут?"). Force NoWrap so hard-break rows stack vertically.
            container.style.flexWrap = Wrap.NoWrap;
            container.style.minWidth = 0;

            VisualElement row = NewWrapRow();
            container.Add(row);
            int local = 0;

            for (int r = 0; r < block.Inlines.Count; r++)
            {
                InlineRun run = block.Inlines[r];
                string text = run.Text ?? string.Empty;
                bool keepWhole = run.Code || run.LinkUrl != null;
                if (keepWhole)
                {
                    Label label = MakeInlineLabel(text, run);
                    row.Add(label);
                    AddToken(block, label, run, local, local + text.Length);
                    local += text.Length;
                    continue;
                }

                int i = 0;
                while (i < text.Length)
                {
                    if (text[i] == '\n')
                    {
                        local += 1; // newline lives in PlainText but in no token (a gap)
                        i++;
                        row = NewWrapRow();
                        container.Add(row);
                        continue;
                    }
                    int start = i;
                    while (i < text.Length && !char.IsWhiteSpace(text[i]))
                        i++;
                    while (i < text.Length && text[i] != '\n' && char.IsWhiteSpace(text[i]))
                        i++;
                    EmitWord(row, block, run, text.Substring(start, i - start), ref local);
                }
            }
            return container;
        }

        // Tokens sit edge-to-edge inside flex-wrap rows. Unity's default Label carries a small
        // implicit margin which is hidden between normal words (their trailing space masks it) but
        // shows up as phantom gaps between the space-less chunks of one long word. Zero it out so a
        // split string renders continuously. Padding is preserved for code so the inline pill keeps shape.
        private static void ResetTokenSpacing(Label label, bool keepPadding)
        {
            label.style.marginTop = 0;
            label.style.marginBottom = 0;
            label.style.marginLeft = 0;
            label.style.marginRight = 0;
            label.style.letterSpacing = 0;
            if (!keepPadding)
            {
                label.style.paddingTop = 0;
                label.style.paddingBottom = 0;
                label.style.paddingLeft = 0;
                label.style.paddingRight = 0;
            }
        }

        private Label MakeInlineLabel(string text, InlineRun run)
        {
            var label = new Label(text);
            label.AddToClassList("transcript__body");
            label.pickingMode = PickingMode.Ignore;
            // Pre (not NoWrap): preserves the word's trailing space so words stay separated by a real
            // space, while space-less chunks of a long word still butt together (margin is zeroed below).
            // NoWrap would trim the trailing space, making words collide once the default margin is gone.
            label.style.whiteSpace = WhiteSpace.Pre;
            ResetTokenSpacing(label, run != null && run.Code);
            if (run != null)
            {
                if (run.Bold)
                    label.AddToClassList("markdown-bold");
                if (run.Italic)
                    label.AddToClassList("markdown-italic");
                if (run.Code)
                    label.AddToClassList("markdown-code");
                if (run.Strike)
                    label.AddToClassList("markdown-strike");
                if (run.LinkUrl != null)
                {
                    label.AddToClassList("markdown-link");
                    label.tooltip = run.LinkUrl;
                }
            }
            return label;
        }

        private void AttachCopyHandler(Label button, string textToCopy)
        {
            string payload = textToCopy ?? string.Empty;
            button.RegisterCallback<PointerDownEvent>(evt =>
            {
                GUIUtility.systemCopyBuffer = payload;
                button.text = "Copied ✓";
                button.schedule.Execute(() => { button.text = "Copy"; }).StartingIn(1200);
                evt.StopImmediatePropagation();
            });
        }

        private void AddToken(Block block, Label label, InlineRun run, int localStart, int localEnd)
        {
            var token = new PlacedToken();
            token.Label = label;
            token.Run = run;
            token.LocalStart = localStart;
            token.LocalEnd = localEnd;
            block.Tokens.Add(token);
        }

        // ===================== Geometry capture =====================

        private void OnGeometryChanged(GeometryChangedEvent evt)
        {
            CaptureGeometry();
            RebuildSelectionVisuals();
        }

        private void ScheduleCapture()
        {
            if (_captureScheduled)
                return;
            _captureScheduled = true;
            schedule.Execute(() =>
            {
                _captureScheduled = false;
                if (panel == null)
                    return;
                CaptureGeometry();
                RebuildSelectionVisuals();
            }).StartingIn(0);
        }

        private void CaptureGeometry()
        {
            for (int i = 0; i < _tokens.Count; i++)
            {
                PlacedToken token = _tokens[i];
                token.HasRect = false;
                token.CharOffsets = null;
                Label label = token.Label;
                if (label == null || label.panel == null || label.parent == null)
                    continue;
                Rect r = label.layout;
                if (float.IsNaN(r.x) || float.IsNaN(r.y) || float.IsNaN(r.width) || float.IsNaN(r.height))
                    continue;
                Vector2 tl = label.parent.ChangeCoordinatesTo(this, new Vector2(r.xMin, r.yMin));
                Vector2 br = label.parent.ChangeCoordinatesTo(this, new Vector2(r.xMax, r.yMax));
                token.Rect = new Rect(tl.x, tl.y, Mathf.Max(0f, br.x - tl.x), Mathf.Max(0f, br.y - tl.y));
                token.HasRect = true;
            }
            BuildVisualLines();
        }

        private void BuildVisualLines()
        {
            _lines.Clear();
            VisualTextLine current = null;
            for (int i = 0; i < _tokens.Count; i++)
            {
                PlacedToken token = _tokens[i];
                if (!token.HasRect)
                    continue;
                bool newLine = current == null ||
                               Mathf.Abs(token.Rect.y - current.Y) > Mathf.Max(2f, current.Height * 0.5f);
                if (newLine)
                {
                    current = new VisualTextLine();
                    current.Y = token.Rect.y;
                    current.Height = token.Rect.height;
                    current.PlainStart = token.GlobalStart;
                    current.PlainEnd = token.GlobalEnd;
                    _lines.Add(current);
                }
                current.Tokens.Add(token);
                current.Height = Mathf.Max(current.Height, token.Rect.height);
                current.PlainStart = Mathf.Min(current.PlainStart, token.GlobalStart);
                current.PlainEnd = Mathf.Max(current.PlainEnd, token.GlobalEnd);
            }
        }

        private float[] EnsureOffsets(PlacedToken token)
        {
            if (token.CharOffsets != null)
                return token.CharOffsets;
            Label label = token.Label;
            string text = label != null ? (label.text ?? string.Empty) : string.Empty;
            var offsets = new float[text.Length + 1];
            offsets[0] = 0f;
            for (int i = 1; i <= text.Length; i++)
            {
                float w;
                if (label != null)
                {
                    Vector2 size = label.MeasureTextSize(text.Substring(0, i), 0, VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined);
                    w = float.IsNaN(size.x) ? token.Rect.width * ((float)i / Mathf.Max(1, text.Length)) : size.x;
                }
                else
                {
                    w = 0f;
                }
                offsets[i] = w;
            }
            token.CharOffsets = offsets;
            return offsets;
        }

        // ===================== Selection rendering =====================

        private void RebuildSelectionVisuals()
        {
            _selectionLayer.Clear();
            int start;
            int end;
            if (!GetSelectionRange(out start, out end))
                return;

            for (int i = 0; i < _lines.Count; i++)
            {
                VisualTextLine line = _lines[i];
                if (end <= line.PlainStart || start >= line.PlainEnd)
                    continue;
                int from = Mathf.Max(start, line.PlainStart);
                int to = Mathf.Min(end, line.PlainEnd);
                if (to <= from)
                    continue;

                float x1 = XForIndex(line, from);
                float x2 = XForIndex(line, to);
                if (x2 < x1)
                {
                    float tmp = x1;
                    x1 = x2;
                    x2 = tmp;
                }
                if (x2 - x1 < 1.5f)
                    x2 = x1 + 2f;

                var rect = new VisualElement();
                rect.pickingMode = PickingMode.Ignore;
                rect.style.position = Position.Absolute;
                rect.style.left = x1;
                rect.style.top = line.Y;
                rect.style.width = x2 - x1;
                rect.style.height = line.Height;
                rect.style.backgroundColor = new Color(0.43f, 0.42f, 0.95f, 0.35f);
                _selectionLayer.Add(rect);
            }
        }

        private float XForIndex(VisualTextLine line, int index)
        {
            if (line.Tokens.Count == 0)
                return 0f;
            for (int i = 0; i < line.Tokens.Count; i++)
            {
                PlacedToken token = line.Tokens[i];
                if (index <= token.GlobalEnd)
                {
                    float[] offsets = EnsureOffsets(token);
                    int local = Mathf.Clamp(index - token.GlobalStart, 0, offsets.Length - 1);
                    return token.Rect.x + offsets[local];
                }
            }
            PlacedToken last = line.Tokens[line.Tokens.Count - 1];
            return last.Rect.xMax;
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

        // ===================== Hit testing =====================

        private int HitTestPlainIndex(Vector2 localPosition, out string linkUrl)
        {
            linkUrl = null;
            if (_lines.Count == 0)
                return 0;

            VisualTextLine line = _lines[0];
            for (int i = 0; i < _lines.Count; i++)
            {
                VisualTextLine candidate = _lines[i];
                if (localPosition.y >= candidate.Y && localPosition.y <= candidate.Y + candidate.Height)
                {
                    line = candidate;
                    break;
                }
                if (localPosition.y > candidate.Y)
                    line = candidate;
            }

            if (line.Tokens.Count == 0)
                return line.PlainStart;

            for (int i = 0; i < line.Tokens.Count; i++)
            {
                PlacedToken token = line.Tokens[i];
                if (localPosition.x <= token.Rect.xMax)
                {
                    linkUrl = token.Run != null ? token.Run.LinkUrl : null;
                    return IndexInToken(token, localPosition.x);
                }
            }

            PlacedToken lastToken = line.Tokens[line.Tokens.Count - 1];
            linkUrl = lastToken.Run != null ? lastToken.Run.LinkUrl : null;
            return lastToken.GlobalEnd;
        }

        private int IndexInToken(PlacedToken token, float x)
        {
            float[] offsets = EnsureOffsets(token);
            for (int i = 0; i < offsets.Length - 1; i++)
            {
                float mid = token.Rect.x + (offsets[i] + offsets[i + 1]) * 0.5f;
                if (x <= mid)
                    return token.GlobalStart + i;
            }
            return token.GlobalEnd;
        }

        // ===================== Pointer / keyboard =====================

        private void OnPointerDown(PointerDownEvent evt)
        {
            if (evt.button != 0)
                return;

            Focus();
            _downIndex = HitTestPlainIndex(evt.localPosition, out _downLink);
            _selectionAnchor = _downIndex;
            _selectionFocus = _downIndex;
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
                if (!string.IsNullOrEmpty(linkUrl) && upIndex == _downIndex &&
                    string.Equals(linkUrl, _downLink, StringComparison.Ordinal))
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

        private void ClampSelection()
        {
            if (_selectionAnchor > _plainText.Length)
                _selectionAnchor = _plainText.Length;
            if (_selectionFocus > _plainText.Length)
                _selectionFocus = _plainText.Length;
        }

        // ===================== Markdown parsing =====================

        private void ParseMarkdown(string markdown, List<Block> blocks)
        {
            if (string.IsNullOrEmpty(markdown))
                return;

            string normalized = markdown.Replace("\r\n", "\n").Replace("\r", "\n");
            string[] lines = normalized.Split('\n');
            var paragraph = new List<string>();

            int i = 0;
            while (i < lines.Length)
            {
                string raw = lines[i];
                string trimmedStart = raw.TrimStart(' ', '\t');

                if (trimmedStart.StartsWith("```", StringComparison.Ordinal))
                {
                    FlushParagraph(paragraph, blocks);
                    string fenceInfo = trimmedStart.Length > 3 ? trimmedStart.Substring(3).Trim() : string.Empty;
                    var code = new StringBuilder();
                    i++;
                    while (i < lines.Length)
                    {
                        string codeTrimmed = lines[i].TrimStart(' ', '\t');
                        bool isClosingFence = string.Equals(codeTrimmed, "```", StringComparison.Ordinal);
                        if (isClosingFence)
                            break;
                        if (!string.Equals(fenceInfo, "markdown", StringComparison.OrdinalIgnoreCase) &&
                            codeTrimmed.StartsWith("```", StringComparison.Ordinal))
                            break;
                        if (code.Length > 0)
                            code.Append('\n');
                        code.Append(lines[i]);
                        i++;
                    }
                    AddCodeBlock(blocks, code.ToString(), fenceInfo);
                    if (i < lines.Length)
                        i++;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(raw))
                {
                    FlushParagraph(paragraph, blocks);
                    i++;
                    continue;
                }

                if (IsHorizontalRule(trimmedStart))
                {
                    FlushParagraph(paragraph, blocks);
                    AddRule(blocks);
                    i++;
                    continue;
                }

                int headingLevel = GetHeadingLevel(trimmedStart);
                if (headingLevel > 0)
                {
                    FlushParagraph(paragraph, blocks);
                    string headingText = trimmedStart.Substring(headingLevel + 1);
                    AddHeading(blocks, headingText, headingLevel);
                    i++;
                    continue;
                }

                if (trimmedStart.StartsWith("> ", StringComparison.Ordinal) || string.Equals(trimmedStart, ">", StringComparison.Ordinal))
                {
                    FlushParagraph(paragraph, blocks);
                    string quoteText = trimmedStart.Length > 2 ? trimmedStart.Substring(2) : string.Empty;
                    AddQuote(blocks, quoteText);
                    i++;
                    continue;
                }

                string bulletMarker;
                string bulletText;
                if (TryParseListItem(trimmedStart, out bulletMarker, out bulletText))
                {
                    FlushParagraph(paragraph, blocks);
                    int listIndent = CountLeadingColumns(raw);
                    if (string.Equals(bulletMarker, "•", StringComparison.Ordinal))
                        AddBullet(blocks, bulletText, listIndent);
                    else
                        AddNumbered(blocks, bulletMarker, bulletText, listIndent);
                    i++;
                    continue;
                }

                if (IsPotentialTableBlock(lines, i))
                {
                    FlushParagraph(paragraph, blocks);
                    i = ParseTable(lines, i, blocks);
                    continue;
                }

                paragraph.Add(trimmedStart);
                i++;
            }

            FlushParagraph(paragraph, blocks);
        }

        private void FlushParagraph(List<string> paragraph, List<Block> blocks)
        {
            if (paragraph.Count == 0)
                return;
            // Join with '\n' (not ' ') so intra-paragraph line breaks — e.g. the user's Shift+Enter
            // newlines — are preserved as hard breaks instead of being collapsed onto one line.
            string joined = string.Join("\n", paragraph.ToArray());
            AddParagraph(blocks, joined);
            paragraph.Clear();
        }

        private void AddParagraph(List<Block> blocks, string text)
        {
            var block = new Block();
            block.Kind = BlockKind.Paragraph;
            block.Inlines.AddRange(ParseInline(text));
            FinalizeBlock(block);
            blocks.Add(block);
        }

        private void AddHeading(List<Block> blocks, string text, int level)
        {
            var block = new Block();
            block.Kind = BlockKind.Heading;
            block.HeadingLevel = Mathf.Clamp(level, 1, 6);
            block.Inlines.AddRange(ParseInline(text));
            FinalizeBlock(block);
            blocks.Add(block);
        }

        private void AddQuote(List<Block> blocks, string text)
        {
            var block = new Block();
            block.Kind = BlockKind.Quote;
            block.Inlines.AddRange(ParseInline(text));
            FinalizeBlock(block);
            blocks.Add(block);
        }

        private void AddBullet(List<Block> blocks, string text, int indent)
        {
            var block = new Block();
            block.Kind = BlockKind.Bullet;
            block.Marker = "•";
            block.Indent = indent;
            block.Inlines.AddRange(ParseInline(text));
            FinalizeBlock(block);
            blocks.Add(block);
        }

        private void AddNumbered(List<Block> blocks, string marker, string text, int indent)
        {
            var block = new Block();
            block.Kind = BlockKind.Numbered;
            block.Marker = marker;
            block.Indent = indent;
            block.Inlines.AddRange(ParseInline(text));
            FinalizeBlock(block);
            blocks.Add(block);
        }

        private void AddCodeBlock(List<Block> blocks, string code, string language)
        {
            var block = new Block();
            block.Kind = BlockKind.CodeBlock;
            block.CodeText = code ?? string.Empty;
            block.CodeLanguage = language ?? string.Empty;
            FinalizeBlock(block);
            blocks.Add(block);
        }

        private static int CountLeadingColumns(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return 0;
            int columns = 0;
            for (int i = 0; i < raw.Length; i++)
            {
                if (raw[i] == ' ')
                    columns += 1;
                else if (raw[i] == '\t')
                    columns += 4;
                else
                    break;
            }
            return columns;
        }

        private void AddRule(List<Block> blocks)
        {
            var block = new Block();
            block.Kind = BlockKind.Rule;
            FinalizeBlock(block);
            blocks.Add(block);
        }

        private int ParseTable(string[] lines, int startIndex, List<Block> blocks)
        {
            var rows = new List<TableRow>();
            int i = startIndex;
            while (i < lines.Length)
            {
                string trimmed = lines[i].TrimStart(' ', '\t');
                if (!IsPotentialTableStart(trimmed))
                    break;
                if (i == startIndex + 1 && IsTableSeparatorRow(trimmed))
                {
                    i++;
                    continue;
                }

                string normalized = NormalizeTableLine(trimmed);
                string[] cells = ParseTableCells(normalized);
                var row = new TableRow();
                row.IsHeader = i == startIndex;
                for (int c = 0; c < cells.Length; c++)
                    row.Cells.Add(ParseInline(UnescapeMarkdownSyntax(cells[c])));
                rows.Add(row);
                i++;
            }

            int dataIndex = 0;
            for (int r = 0; r < rows.Count; r++)
            {
                if (!rows[r].IsHeader)
                {
                    rows[r].Alt = dataIndex % 2 == 1;
                    dataIndex++;
                }
                rows[r].IsLast = r == rows.Count - 1;
            }

            var block = new Block();
            block.Kind = BlockKind.Table;
            block.TableRows = rows;
            FinalizeBlock(block);
            blocks.Add(block);
            return i;
        }

        // ===================== Diff parsing =====================

        private void ParseDiff(string text, List<Block> blocks)
        {
            string normalized = (text ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n");
            string[] lines = normalized.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i];
                var block = new Block();
                block.Kind = BlockKind.DiffLine;
                block.Inlines.Add(new InlineRun { Text = raw, Code = true });

                bool hasBg;
                Color bg;
                bool hasCol;
                Color col;
                GetDiffLineStyle(raw, out hasBg, out bg, out hasCol, out col);
                block.DiffHasBackground = hasBg;
                block.DiffBackground = bg;
                block.DiffHasColor = hasCol;
                block.DiffColor = col;

                FinalizeBlock(block);
                blocks.Add(block);
            }
        }

        // Shared diff-line styling — used both by SetDiff (per-block) and by diff-fenced code blocks.
        private static void GetDiffLineStyle(string raw, out bool hasBackground, out Color background, out bool hasColor, out Color color)
        {
            raw = raw ?? string.Empty;
            if (raw.StartsWith("+", StringComparison.Ordinal) && !raw.StartsWith("+++", StringComparison.Ordinal))
            {
                hasBackground = true;
                background = DiffAddBg;
                hasColor = true;
                color = DiffAddColor;
            }
            else if (raw.StartsWith("-", StringComparison.Ordinal) && !raw.StartsWith("---", StringComparison.Ordinal))
            {
                hasBackground = true;
                background = DiffDelBg;
                hasColor = true;
                color = DiffDelColor;
            }
            else if (raw.StartsWith("@@", StringComparison.Ordinal))
            {
                hasBackground = true;
                background = DiffHunkBg;
                hasColor = true;
                color = DiffHunkColor;
            }
            else
            {
                hasBackground = false;
                background = default(Color);
                hasColor = true;
                color = DiffContextColor;
            }
        }

        // ===================== Code highlighting =====================

        // Theme-aligned syntax colors (see Tokens.uss: accent-2, ok, warn, text-3).
        private static readonly Color CodeKeyword = new Color(0.64f, 0.52f, 0.94f, 1f); // accent-2 violet
        private static readonly Color CodeString = new Color(0.42f, 0.80f, 0.55f, 1f);  // green
        private static readonly Color CodeNumber = new Color(0.90f, 0.70f, 0.36f, 1f);  // warn amber
        private static readonly Color CodeComment = new Color(0.42f, 0.45f, 0.53f, 1f); // muted gray

        // Diff palette (shared by SetDiff and diff code blocks).
        private static readonly Color DiffAddColor = new Color(0.45f, 0.95f, 0.6f, 1f);
        private static readonly Color DiffAddBg = new Color(0.1f, 0.45f, 0.22f, 0.25f);
        private static readonly Color DiffDelColor = new Color(1f, 0.45f, 0.45f, 1f);
        private static readonly Color DiffDelBg = new Color(0.55f, 0.12f, 0.14f, 0.25f);
        private static readonly Color DiffHunkColor = new Color(0.45f, 0.7f, 1f, 1f);
        private static readonly Color DiffHunkBg = new Color(0.12f, 0.28f, 0.6f, 0.22f);
        private static readonly Color DiffContextColor = new Color(0.7f, 0.72f, 0.78f, 1f);

        private struct CodeSeg
        {
            public string Text;
            public bool HasColor;
            public Color Color;
        }

        private static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract","and","as","async","await","base","bool","break","byte","case","catch","char",
            "class","const","continue","def","default","defer","do","double","elif","else","end","enum",
            "export","extends","false","final","finally","float","fn","for","foreach","from","func",
            "function","get","go","if","implements","import","in","int","interface","internal","is","lambda",
            "let","long","namespace","new","nil","none","not","null","object","or","out","override","package",
            "params","private","protected","public","readonly","ref","return","sealed","self","set","short",
            "static","string","struct","switch","then","this","throw","throws","true","try","type","typeof",
            "using","val","var","virtual","void","when","while","with","yield"
        };

        private static readonly HashSet<string> HighlightLanguages = new HashSet<string>(StringComparer.Ordinal)
        {
            "c","c#","cs","csharp","cpp","c++","go","golang","java","javascript","js","jsx","json","kotlin",
            "kt","php","python","py","ruby","rb","rust","rs","scala","shell","sh","bash","swift","ts","tsx",
            "typescript","yaml","yml"
        };

        private static bool IsDiffLanguage(string lang)
        {
            return string.Equals(lang, "diff", StringComparison.Ordinal) ||
                   string.Equals(lang, "patch", StringComparison.Ordinal);
        }

        // Generic/unlabeled fences where we still try to sniff diff content.
        private static bool IsGenericCodeLanguage(string lang)
        {
            return string.IsNullOrEmpty(lang) ||
                   string.Equals(lang, "code", StringComparison.Ordinal) ||
                   string.Equals(lang, "text", StringComparison.Ordinal) ||
                   string.Equals(lang, "plain", StringComparison.Ordinal) ||
                   string.Equals(lang, "plaintext", StringComparison.Ordinal);
        }

        // True only for a real unified diff: needs a structural marker (@@ hunk, "diff --git",
        // or a ---/+++ file-header pair) AND at least one +/- change line. The double condition
        // avoids mis-coloring ordinary text (e.g. a bullet list using "-").
        private static bool LooksLikeUnifiedDiff(string code)
        {
            if (string.IsNullOrEmpty(code))
                return false;

            string[] lines = code.Split('\n');
            bool hasMarker = false;
            bool hasFileMinus = false;
            bool hasFilePlus = false;
            bool hasChangeLine = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string l = lines[i];
                if (l.StartsWith("@@", StringComparison.Ordinal) ||
                    l.StartsWith("diff --git", StringComparison.Ordinal))
                {
                    hasMarker = true;
                }
                else if (l.StartsWith("---", StringComparison.Ordinal))
                {
                    hasFileMinus = true;
                }
                else if (l.StartsWith("+++", StringComparison.Ordinal))
                {
                    hasFilePlus = true;
                }
                else if (l.StartsWith("+", StringComparison.Ordinal) ||
                         l.StartsWith("-", StringComparison.Ordinal))
                {
                    hasChangeLine = true;
                }
            }

            if (hasFileMinus && hasFilePlus)
                hasMarker = true;

            return hasMarker && hasChangeLine;
        }

        private static bool IsHighlightLanguage(string lang)
        {
            return !string.IsNullOrEmpty(lang) && HighlightLanguages.Contains(lang);
        }

        // Lightweight, language-agnostic tokenizer. Returns segments covering every char of the line
        // exactly once, in order, so the selection model's local-index accounting stays exact.
        private static List<CodeSeg> HighlightLine(string line)
        {
            var segs = new List<CodeSeg>();
            var pending = new StringBuilder();
            int i = 0;
            int n = line.Length;
            while (i < n)
            {
                char c = line[i];

                // Line comment: // (C-family) or # (python/shell/yaml).
                bool slashComment = c == '/' && i + 1 < n && line[i + 1] == '/';
                if (slashComment || c == '#')
                {
                    FlushPending(segs, pending);
                    AddSeg(segs, line.Substring(i), CodeComment);
                    i = n;
                    break;
                }

                // String literal.
                if (c == '"' || c == '\'' || c == '`')
                {
                    FlushPending(segs, pending);
                    int j = i + 1;
                    while (j < n)
                    {
                        if (line[j] == '\\')
                        {
                            j += 2;
                            continue;
                        }
                        if (line[j] == c)
                        {
                            j++;
                            break;
                        }
                        j++;
                    }
                    if (j > n)
                        j = n;
                    AddSeg(segs, line.Substring(i, j - i), CodeString);
                    i = j;
                    continue;
                }

                // Number literal.
                if (c >= '0' && c <= '9')
                {
                    FlushPending(segs, pending);
                    int j = i;
                    while (j < n && (IsHexChar(line[j]) || line[j] == '.' || line[j] == 'x' || line[j] == 'X' || line[j] == '_'))
                        j++;
                    AddSeg(segs, line.Substring(i, j - i), CodeNumber);
                    i = j;
                    continue;
                }

                // Identifier / keyword.
                if (char.IsLetter(c) || c == '_')
                {
                    int j = i;
                    while (j < n && (char.IsLetterOrDigit(line[j]) || line[j] == '_'))
                        j++;
                    string word = line.Substring(i, j - i);
                    if (Keywords.Contains(word))
                    {
                        FlushPending(segs, pending);
                        AddSeg(segs, word, CodeKeyword);
                    }
                    else
                    {
                        pending.Append(word);
                    }
                    i = j;
                    continue;
                }

                pending.Append(c);
                i++;
            }
            FlushPending(segs, pending);
            return segs;
        }

        private static void FlushPending(List<CodeSeg> segs, StringBuilder pending)
        {
            if (pending.Length == 0)
                return;
            segs.Add(new CodeSeg { Text = pending.ToString(), HasColor = false });
            pending.Length = 0;
        }

        private static void AddSeg(List<CodeSeg> segs, string text, Color color)
        {
            if (string.IsNullOrEmpty(text))
                return;
            segs.Add(new CodeSeg { Text = text, HasColor = true, Color = color });
        }

        private static bool IsHexChar(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }

        // ===================== ANSI =====================

        // Strips ANSI escape sequences (CSI, incl. truecolor SGR) so terminal/`git diff --color`
        // output renders clean instead of leaking raw codes like [38;2;218;165;32m.
        private static readonly Regex AnsiRegex = BuildAnsiRegex();

        private static Regex BuildAnsiRegex()
        {
            // Built from char codes (ESC=27, BEL=7) so no control bytes live in source.
            string esc = ((char)27).ToString();
            string bel = ((char)7).ToString();
            // CSI sequences (incl. truecolor SGR like ESC[38;2;r;g;bm) and OSC sequences.
            string pattern = esc + "\\[[0-9;?]*[ -/]*[@-~]" +
                             "|" + esc + "\\][^" + bel + "]*(?:" + bel + "|" + esc + "\\\\)";
            return new Regex(pattern, RegexOptions.Compiled);
        }

        public static string StripAnsi(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            if (text.IndexOf((char)27) < 0)
                return text;
            return AnsiRegex.Replace(text, string.Empty);
        }

        // ===================== Finalize (PlainText + Signature) =====================

        private void FinalizeBlock(Block block)
        {
            var plain = new StringBuilder();
            var sig = new StringBuilder();
            sig.Append((int)block.Kind);
            sig.Append('|');
            sig.Append(block.HeadingLevel);
            sig.Append('|');
            sig.Append(block.Marker ?? string.Empty);
            sig.Append('|');
            sig.Append(block.Indent);
            sig.Append('|');
            sig.Append(block.CodeLanguage ?? string.Empty);
            sig.Append('|');

            switch (block.Kind)
            {
                case BlockKind.CodeBlock:
                    plain.Append(block.CodeText ?? string.Empty);
                    sig.Append(block.CodeText ?? string.Empty);
                    break;
                case BlockKind.Rule:
                    sig.Append("hr");
                    break;
                case BlockKind.Bullet:
                case BlockKind.Numbered:
                    plain.Append(block.Marker ?? string.Empty);
                    plain.Append(' ');
                    AppendInline(plain, sig, block.Inlines);
                    break;
                case BlockKind.Table:
                    AppendTable(plain, sig, block.TableRows);
                    break;
                case BlockKind.DiffLine:
                    if (block.Inlines.Count > 0)
                        plain.Append(block.Inlines[0].Text ?? string.Empty);
                    sig.Append('d');
                    AppendInline(new StringBuilder(), sig, block.Inlines);
                    break;
                default:
                    AppendInline(plain, sig, block.Inlines);
                    break;
            }

            block.PlainText = plain.ToString();
            block.Signature = sig.ToString();
        }

        private static void AppendInline(StringBuilder plain, StringBuilder sig, List<InlineRun> runs)
        {
            for (int i = 0; i < runs.Count; i++)
            {
                InlineRun run = runs[i];
                string t = run.Text ?? string.Empty;
                plain.Append(t);
                sig.Append(t);
                sig.Append('\x02');
                if (run.Bold) sig.Append('b');
                if (run.Italic) sig.Append('i');
                if (run.Code) sig.Append('c');
                if (run.Strike) sig.Append('s');
                if (run.LinkUrl != null)
                {
                    sig.Append('l');
                    sig.Append(run.LinkUrl);
                }
                sig.Append('\x03');
            }
        }

        private static void AppendTable(StringBuilder plain, StringBuilder sig, List<TableRow> rows)
        {
            if (rows == null)
                return;
            for (int r = 0; r < rows.Count; r++)
            {
                TableRow row = rows[r];
                sig.Append(row.IsHeader ? 'H' : 'R');
                for (int c = 0; c < row.Cells.Count; c++)
                {
                    if (c > 0)
                        plain.Append('\t');
                    AppendInline(plain, sig, row.Cells[c]);
                    sig.Append('\x04');
                }
                if (r < rows.Count - 1)
                    plain.Append('\n');
                sig.Append('\x05');
            }
        }

        // ===================== Inline parsing =====================

        private static List<InlineRun> ParseInline(string text)
        {
            var runs = new List<InlineRun>();
            if (string.IsNullOrEmpty(text))
                return runs;

            text = UnescapeMarkdownSyntax(text);
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

        private static void AddPlainRun(List<InlineRun> runs, string text)
        {
            AddStyledRun(runs, text, false, false, false, false, null);
        }

        private static void AddStyledRun(List<InlineRun> runs, string text, bool bold, bool italic, bool code, bool strike, string linkUrl)
        {
            if (string.IsNullOrEmpty(text))
                return;
            runs.Add(new InlineRun
            {
                Text = text,
                Bold = bold,
                Italic = italic,
                Code = code,
                Strike = strike,
                LinkUrl = linkUrl
            });
        }

        // ===================== Markdown helpers =====================

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
                marker = "•";
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
            if (string.IsNullOrEmpty(trimmed))
                return false;
            string normalized = NormalizeTableLine(trimmed);
            int first = normalized.IndexOf('|');
            if (first < 0)
                return false;
            int second = normalized.IndexOf('|', first + 1);
            return second >= 0;
        }

        private static bool IsPotentialTableBlock(string[] lines, int index)
        {
            if (lines == null || index < 0 || index >= lines.Length)
                return false;
            string current = lines[index].TrimStart(' ', '\t');
            if (!IsPotentialTableStart(current))
                return false;
            if (index + 1 >= lines.Length)
                return false;

            string next = lines[index + 1].TrimStart(' ', '\t');
            if (IsTableSeparatorRow(next))
                return true;
            return IsPotentialTableStart(next);
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
