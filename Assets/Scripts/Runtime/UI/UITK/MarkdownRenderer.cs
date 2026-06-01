using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK
{
    internal static class MarkdownRenderer
    {
        public static VisualElement Render(string markdown)
        {
            var root = new VisualElement();
            root.AddToClassList("markdown-root");
            if (string.IsNullOrWhiteSpace(markdown))
            {
                return root;
            }
            RenderBlocks(root, markdown);
            return root;
        }

        public static bool ContainsMarkdown(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            if (text.IndexOf("**", StringComparison.Ordinal) >= 0)
            {
                return true;
            }
            if (text.IndexOf("```", StringComparison.Ordinal) >= 0)
            {
                return true;
            }
            if (text.IndexOf("`", StringComparison.Ordinal) >= 0)
            {
                return true;
            }
            if (text.IndexOf("](", StringComparison.Ordinal) >= 0)
            {
                return true;
            }
            if (text.IndexOf("~~", StringComparison.Ordinal) >= 0)
            {
                return true;
            }
            if (text.IndexOf("*", StringComparison.Ordinal) >= 0)
            {
                return true;
            }
            // Table separator row
            if (text.IndexOf("|---|", StringComparison.Ordinal) >= 0 || text.IndexOf("|:--", StringComparison.Ordinal) >= 0)
            {
                return true;
            }
            if (text.IndexOf("\n- ", StringComparison.Ordinal) >= 0 || text.IndexOf("\n* ", StringComparison.Ordinal) >= 0 || text.IndexOf("\n+ ", StringComparison.Ordinal) >= 0)
            {
                return true;
            }
            if (text.StartsWith("- ", StringComparison.Ordinal) || text.StartsWith("* ", StringComparison.Ordinal) || text.StartsWith("+ ", StringComparison.Ordinal))
            {
                return true;
            }
            if (text.IndexOf("\n1. ", StringComparison.Ordinal) >= 0 || text.StartsWith("1. ", StringComparison.Ordinal))
            {
                return true;
            }
            // ATX headers
            if (text.IndexOf("\n#", StringComparison.Ordinal) >= 0 || text.StartsWith("#", StringComparison.Ordinal))
            {
                return true;
            }
            // Blockquotes
            if (text.IndexOf("\n> ", StringComparison.Ordinal) >= 0 || text.StartsWith("> ", StringComparison.Ordinal))
            {
                return true;
            }
            int nl = text.IndexOf('\n');
            while (nl >= 0 && nl < text.Length - 3)
            {
                int j = nl + 1;
                if (j < text.Length && char.IsDigit(text[j]))
                {
                    j++;
                    while (j < text.Length && char.IsDigit(text[j]))
                    {
                        j++;
                    }
                    if (j < text.Length && text[j] == '.' && j + 1 < text.Length && text[j + 1] == ' ')
                    {
                        return true;
                    }
                }
                nl = text.IndexOf('\n', nl + 1);
            }
            if (text.Length > 2 && char.IsDigit(text[0]))
            {
                int j = 1;
                while (j < text.Length && char.IsDigit(text[j]))
                {
                    j++;
                }
                if (j < text.Length && text[j] == '.' && j + 1 < text.Length && text[j + 1] == ' ')
                {
                    return true;
                }
            }
            return false;
        }

        private static void RenderBlocks(VisualElement root, string markdown)
        {
            int i = 0;
            while (i < markdown.Length)
            {
                int codeStart = markdown.IndexOf("```", i, StringComparison.Ordinal);
                if (codeStart < 0)
                {
                    string rest = markdown.Substring(i);
                    RenderNonCodeBlocks(root, rest);
                    break;
                }
                if (codeStart > i)
                {
                    string before = markdown.Substring(i, codeStart - i);
                    RenderNonCodeBlocks(root, before);
                }
                int afterOpen = codeStart + 3;
                int codeEnd = markdown.IndexOf("```", afterOpen, StringComparison.Ordinal);
                string between;
                if (codeEnd < 0)
                {
                    between = markdown.Substring(afterOpen);
                    i = markdown.Length;
                }
                else
                {
                    between = markdown.Substring(afterOpen, codeEnd - afterOpen);
                    i = codeEnd + 3;
                }
                string codeContent;
                int firstNl = between.IndexOfAny(new char[] { '\n', '\r' });
                if (firstNl >= 0)
                {
                    codeContent = between.Substring(firstNl).TrimStart('\r', '\n');
                }
                else
                {
                    codeContent = between;
                }
                codeContent = codeContent.TrimEnd('\r', '\n');
                var codeBlock = new VisualElement();
                codeBlock.AddToClassList("markdown-codeblock");
                var pre = new Label(codeContent);
                pre.AddToClassList("markdown-codeblock-text");
                codeBlock.Add(pre);
                root.Add(codeBlock);
            }
        }

        private static bool IsHorizontalRule(string trimmed)
        {
            if (trimmed.Length < 3) return false;
            char c = trimmed[0];
            if (c != '-' && c != '_' && c != '=' && c != '*') return false;
            int nonSpace = 0;
            for (int k = 0; k < trimmed.Length; k++)
            {
                if (trimmed[k] != c && trimmed[k] != ' ') return false;
                if (trimmed[k] == c) nonSpace++;
            }
            return nonSpace >= 3;
        }

        private static bool IsTableSeparatorRow(string line)
        {
            string trimmed = line.Trim();
            for (int k = 0; k < trimmed.Length; k++)
            {
                char c = trimmed[k];
                if (c != '|' && c != '-' && c != ':' && c != ' ') return false;
            }
            return trimmed.IndexOf('-') >= 0;
        }

        private static string[] ParseTableCells(string line)
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0 && trimmed[0] == '|')
                trimmed = trimmed.Substring(1);
            if (trimmed.Length > 0 && trimmed[trimmed.Length - 1] == '|')
                trimmed = trimmed.Substring(0, trimmed.Length - 1);
            string[] parts = trimmed.Split('|');
            for (int k = 0; k < parts.Length; k++)
                parts[k] = parts[k].Trim();
            return parts;
        }

        private static void RenderTable(VisualElement root, List<string[]> headerRows, List<string[]> dataRows)
        {
            var table = new VisualElement();
            table.AddToClassList("markdown-table");

            for (int ri = 0; ri < headerRows.Count; ri++)
            {
                string[] row = headerRows[ri];
                var rowEl = new VisualElement();
                rowEl.AddToClassList("markdown-table-row");
                rowEl.AddToClassList("markdown-table-row--header");
                for (int ci = 0; ci < row.Length; ci++)
                {
                    var cellEl = new VisualElement();
                    cellEl.AddToClassList("markdown-table-cell");
                    cellEl.AddToClassList("markdown-table-cell--header");
                    if (ci == row.Length - 1)
                        cellEl.AddToClassList("markdown-table-cell--last");
                    RenderInline(cellEl, row[ci]);
                    rowEl.Add(cellEl);
                }
                table.Add(rowEl);
            }

            for (int ri = 0; ri < dataRows.Count; ri++)
            {
                string[] row = dataRows[ri];
                var rowEl = new VisualElement();
                rowEl.AddToClassList("markdown-table-row");
                if (ri % 2 == 1)
                    rowEl.AddToClassList("markdown-table-row--alt");
                if (ri == dataRows.Count - 1)
                    rowEl.AddToClassList("markdown-table-row--last");
                for (int ci = 0; ci < row.Length; ci++)
                {
                    var cellEl = new VisualElement();
                    cellEl.AddToClassList("markdown-table-cell");
                    if (ci == row.Length - 1)
                        cellEl.AddToClassList("markdown-table-cell--last");
                    RenderInline(cellEl, row[ci]);
                    rowEl.Add(cellEl);
                }
                table.Add(rowEl);
            }

            root.Add(table);
        }

        private static void RenderNonCodeBlocks(VisualElement root, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");
            string[] lines = text.Split('\n');
            List<string> paraBuffer = new List<string>();
            Action flushPara = () =>
            {
                if (paraBuffer.Count > 0)
                {
                    string joined = string.Join(" ", paraBuffer);
                    var para = new VisualElement();
                    para.AddToClassList("markdown-paragraph");
                    RenderInline(para, joined);
                    root.Add(para);
                    paraBuffer.Clear();
                }
            };
            for (int li = 0; li < lines.Length; li++)
            {
                string line = lines[li];
                string trimmed = line.TrimStart(' ', '\t');
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    flushPara();
                    continue;
                }

                // Horizontal rule: ---, ___, ===, *** (3+ same chars, spaces allowed)
                if (IsHorizontalRule(trimmed))
                {
                    flushPara();
                    var hr = new VisualElement();
                    hr.AddToClassList("markdown-hr");
                    root.Add(hr);
                    continue;
                }

                // ATX headers: # H1 .. ###### H6
                if (trimmed[0] == '#')
                {
                    int level = 0;
                    while (level < trimmed.Length && trimmed[level] == '#')
                    {
                        level++;
                    }
                    if (level <= 6 && level < trimmed.Length && trimmed[level] == ' ')
                    {
                        flushPara();
                        string headerText = trimmed.Substring(level + 1);
                        var header = new Label(headerText);
                        header.AddToClassList("markdown-h" + level.ToString());
                        root.Add(header);
                        continue;
                    }
                }

                // Blockquote: > text
                if (trimmed.StartsWith("> ") || trimmed == ">")
                {
                    flushPara();
                    string quoteText = trimmed.Length > 2 ? trimmed.Substring(2) : string.Empty;
                    var quote = new VisualElement();
                    quote.AddToClassList("markdown-blockquote");
                    if (!string.IsNullOrEmpty(quoteText))
                    {
                        RenderInline(quote, quoteText);
                    }
                    root.Add(quote);
                    continue;
                }

                // Markdown table: collect consecutive | rows
                if (trimmed[0] == '|')
                {
                    flushPara();
                    List<string> tableLines = new List<string>();
                    while (li < lines.Length)
                    {
                        string tl = lines[li].TrimStart(' ', '\t');
                        if (tl.Length == 0 || tl[0] != '|') break;
                        tableLines.Add(tl);
                        li++;
                    }
                    li--;

                    bool hasHeader = tableLines.Count >= 2 && IsTableSeparatorRow(tableLines[1]);
                    List<string[]> headerRows = new List<string[]>();
                    List<string[]> dataRows = new List<string[]>();

                    for (int ti = 0; ti < tableLines.Count; ti++)
                    {
                        if (hasHeader && ti == 0)
                        {
                            headerRows.Add(ParseTableCells(tableLines[ti]));
                        }
                        else if (hasHeader && ti == 1)
                        {
                            // separator row — skip
                        }
                        else
                        {
                            dataRows.Add(ParseTableCells(tableLines[ti]));
                        }
                    }

                    RenderTable(root, headerRows, dataRows);
                    continue;
                }

                bool isBullet = trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("+ ");
                if (isBullet)
                {
                    flushPara();
                    string itemText = trimmed.Substring(2);
                    var bulletRow = new VisualElement();
                    bulletRow.AddToClassList("markdown-bullet");
                    var markerLabel = new Label("•");
                    markerLabel.AddToClassList("markdown-bullet-marker");
                    bulletRow.Add(markerLabel);
                    var content = new VisualElement();
                    content.style.flexGrow = 1;
                    content.style.flexDirection = FlexDirection.Row;
                    content.style.flexWrap = Wrap.Wrap;
                    content.style.minWidth = 0;
                    RenderInline(content, itemText);
                    bulletRow.Add(content);
                    root.Add(bulletRow);
                    continue;
                }

                if (trimmed.Length > 2 && char.IsDigit(trimmed[0]))
                {
                    int j = 1;
                    while (j < trimmed.Length && char.IsDigit(trimmed[j]))
                    {
                        j++;
                    }
                    if (j < trimmed.Length && trimmed[j] == '.' && j + 1 < trimmed.Length && trimmed[j + 1] == ' ')
                    {
                        flushPara();
                        string numMarker = trimmed.Substring(0, j + 1);
                        string itemText = trimmed.Substring(j + 2);
                        var numRow = new VisualElement();
                        numRow.AddToClassList("markdown-numbered");
                        var numMarkerLabel = new Label(numMarker + " ");
                        numMarkerLabel.AddToClassList("markdown-numbered-marker");
                        numRow.Add(numMarkerLabel);
                        var content = new VisualElement();
                        content.style.flexGrow = 1;
                        content.style.flexDirection = FlexDirection.Row;
                        content.style.flexWrap = Wrap.Wrap;
                        content.style.minWidth = 0;
                        RenderInline(content, itemText);
                        numRow.Add(content);
                        root.Add(numRow);
                        continue;
                    }
                }

                paraBuffer.Add(trimmed);
            }
            flushPara();
        }

        private static void RenderInline(VisualElement parent, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            int pos = 0;
            while (pos < text.Length)
            {
                int nextTriple = text.IndexOf("***", pos, StringComparison.Ordinal);
                int nextBold   = text.IndexOf("**",  pos, StringComparison.Ordinal);
                int nextStrike = text.IndexOf("~~",  pos, StringComparison.Ordinal);
                int nextItalic = text.IndexOf("*",   pos, StringComparison.Ordinal);
                int nextCode   = text.IndexOf("`",   pos, StringComparison.Ordinal);
                int nextLink   = text.IndexOf("[",   pos, StringComparison.Ordinal);

                int next = -1;
                string marker = null;

                // Triple *** beats ** and * at same position
                if (nextTriple >= 0 && (next < 0 || nextTriple < next))
                {
                    next = nextTriple;
                    marker = "***";
                }
                // ** beats * at same position
                if (nextBold >= 0 && (next < 0 || nextBold < next))
                {
                    next = nextBold;
                    marker = "**";
                }
                if (nextStrike >= 0 && (next < 0 || nextStrike < next))
                {
                    next = nextStrike;
                    marker = "~~";
                }
                // * wins only if strictly before ** and ***
                if (nextItalic >= 0 && (next < 0 || nextItalic < next))
                {
                    if ((nextBold < 0 || nextItalic != nextBold) && (nextTriple < 0 || nextItalic != nextTriple))
                    {
                        next = nextItalic;
                        marker = "*";
                    }
                }
                if (nextCode >= 0 && (next < 0 || nextCode < next))
                {
                    next = nextCode;
                    marker = "`";
                }
                if (nextLink >= 0 && (next < 0 || nextLink < next))
                {
                    next = nextLink;
                    marker = "[";
                }

                // Plain text before the nearest marker
                if (next < 0 || next > pos)
                {
                    int end = (next >= 0 ? next : text.Length);
                    if (end > pos)
                    {
                        string plain = text.Substring(pos, end - pos);
                        var label = new Label(plain);
                        label.AddToClassList("transcript__body");
                        parent.Add(label);
                    }
                    pos = end;
                    continue;
                }

                if (marker == "***")
                {
                    int close = text.IndexOf("***", pos + 3, StringComparison.Ordinal);
                    if (close >= 0)
                    {
                        string inner = text.Substring(pos + 3, close - (pos + 3));
                        var boldItal = new Label(inner);
                        boldItal.AddToClassList("transcript__body");
                        boldItal.AddToClassList("markdown-bold");
                        boldItal.AddToClassList("markdown-italic");
                        parent.Add(boldItal);
                        pos = close + 3;
                        continue;
                    }
                    else
                    {
                        var label = new Label("***");
                        label.AddToClassList("transcript__body");
                        parent.Add(label);
                        pos += 3;
                        continue;
                    }
                }

                if (marker == "**")
                {
                    int close = text.IndexOf("**", pos + 2, StringComparison.Ordinal);
                    if (close >= 0)
                    {
                        string inner = text.Substring(pos + 2, close - (pos + 2));
                        var bold = new Label(inner);
                        bold.AddToClassList("transcript__body");
                        bold.AddToClassList("markdown-bold");
                        parent.Add(bold);
                        pos = close + 2;
                        continue;
                    }
                    else
                    {
                        var label = new Label("**");
                        label.AddToClassList("transcript__body");
                        parent.Add(label);
                        pos += 2;
                        continue;
                    }
                }

                if (marker == "~~")
                {
                    int close = text.IndexOf("~~", pos + 2, StringComparison.Ordinal);
                    if (close >= 0)
                    {
                        string inner = text.Substring(pos + 2, close - (pos + 2));
                        var strike = new Label(inner);
                        strike.AddToClassList("transcript__body");
                        strike.AddToClassList("markdown-strike");
                        parent.Add(strike);
                        pos = close + 2;
                        continue;
                    }
                    else
                    {
                        var label = new Label("~~");
                        label.AddToClassList("transcript__body");
                        parent.Add(label);
                        pos += 2;
                        continue;
                    }
                }

                if (marker == "*")
                {
                    int close = text.IndexOf("*", pos + 1, StringComparison.Ordinal);
                    if (close >= 0)
                    {
                        string inner = text.Substring(pos + 1, close - (pos + 1));
                        var ital = new Label(inner);
                        ital.AddToClassList("transcript__body");
                        ital.AddToClassList("markdown-italic");
                        parent.Add(ital);
                        pos = close + 1;
                        continue;
                    }
                    else
                    {
                        var label = new Label("*");
                        label.AddToClassList("transcript__body");
                        parent.Add(label);
                        pos += 1;
                        continue;
                    }
                }

                if (marker == "`")
                {
                    int close = text.IndexOf("`", pos + 1, StringComparison.Ordinal);
                    if (close >= 0)
                    {
                        string inner = text.Substring(pos + 1, close - (pos + 1));
                        var code = new Label(inner);
                        code.AddToClassList("transcript__body");
                        code.AddToClassList("markdown-code");
                        parent.Add(code);
                        pos = close + 1;
                        continue;
                    }
                    else
                    {
                        var label = new Label("`");
                        label.AddToClassList("transcript__body");
                        parent.Add(label);
                        pos += 1;
                        continue;
                    }
                }

                if (marker == "[")
                {
                    int closeBracket = text.IndexOf("]", pos + 1, StringComparison.Ordinal);
                    if (closeBracket >= 0)
                    {
                        int pOpen = closeBracket + 1;
                        if (pOpen < text.Length && text[pOpen] == '(')
                        {
                            int pClose = text.IndexOf(")", pOpen + 1, StringComparison.Ordinal);
                            if (pClose >= 0)
                            {
                                string linkText = text.Substring(pos + 1, closeBracket - (pos + 1));
                                string url = text.Substring(pOpen + 1, pClose - (pOpen + 1));
                                var linkLabel = new Label(linkText);
                                linkLabel.AddToClassList("transcript__body");
                                linkLabel.AddToClassList("markdown-link");
                                linkLabel.tooltip = url;
                                string capturedUrl = url;
                                linkLabel.RegisterCallback<MouseDownEvent>(evt =>
                                {
                                    if (evt.button == 0)
                                    {
                                        Application.OpenURL(capturedUrl);
                                        evt.StopImmediatePropagation();
                                    }
                                });
                                parent.Add(linkLabel);
                                pos = pClose + 1;
                                continue;
                            }
                        }
                    }
                    var plain2 = new Label("[");
                    plain2.AddToClassList("transcript__body");
                    parent.Add(plain2);
                    pos += 1;
                    continue;
                }

                pos++;
            }
        }
    }
}
