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
            // Support common italic markers (fixes cases where AI uses * or _)
            if (text.IndexOf("*", StringComparison.Ordinal) >= 0 || text.IndexOf("_", StringComparison.Ordinal) >= 0)
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
            // Detect ATX headers (# )
            if (text.IndexOf("# ", StringComparison.Ordinal) >= 0 || text.StartsWith("#", StringComparison.Ordinal))
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
                bool isBullet = trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("+ ");
                if (isBullet)
                {
                    flushPara();
                    string itemText = trimmed.Substring(2);
                    var bulletRow = new VisualElement();
                    bulletRow.AddToClassList("markdown-bullet");
                    var marker = new Label("•");
                    marker.AddToClassList("markdown-bullet-marker");
                    bulletRow.Add(marker);
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
                int markerLen = 0;
                string numMarker = null;
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
                        markerLen = j + 2;
                        numMarker = trimmed.Substring(0, j + 1);
                        string itemText = trimmed.Substring(markerLen);
                        var numRow = new VisualElement();
                        numRow.AddToClassList("markdown-numbered");
                        var marker = new Label(numMarker + " ");
                        marker.AddToClassList("markdown-numbered-marker");
                        numRow.Add(marker);
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
                int nextBold = text.IndexOf("**", pos, StringComparison.Ordinal);
                int nextItalic = text.IndexOf("*", pos, StringComparison.Ordinal);
                int nextCode = text.IndexOf("`", pos, StringComparison.Ordinal);
                int nextLink = text.IndexOf("[", pos, StringComparison.Ordinal);
                int next = -1;
                string marker = null;
                if (nextBold >= 0 && (next < 0 || nextBold < next))
                {
                    next = nextBold;
                    marker = "**";
                }
                if (nextItalic >= 0 && (next < 0 || nextItalic < next))
                {
                    next = nextItalic;
                    marker = "*";
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
                else if (marker == "*")
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
                else if (marker == "`")
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
                else if (marker == "[")
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
                    var plain = new Label("[");
                    plain.AddToClassList("transcript__body");
                    parent.Add(plain);
                    pos += 1;
                    continue;
                }
                pos++;
            }
        }
    }
}
