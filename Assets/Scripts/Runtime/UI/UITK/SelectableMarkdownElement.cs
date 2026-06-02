using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.TextCore;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK
{
    /// <summary>
    /// Custom VisualElement that renders markdown/diff text with proper formatting
    /// and supports text selection via pointer events + Ctrl+C.
    /// Uses MeshGenerationContext for character-level rendering.
    /// </summary>
    internal class SelectableMarkdownElement : VisualElement
    {
        private struct CharInfo
        {
            public Rect rect;      // bounding rect in local coords
            public int lineIndex;  // which line
            public int colIndex;   // which column
        }

        private struct TextRun
        {
            public string text;
            public bool bold;
            public bool italic;
            public bool code;
            public Color color;
        }

        private struct DiffLine
        {
            public string text;
            public Color bgColor;
            public Color textColor;
        }

        // State
        private string _plainText = "";
        private List<TextRun> _runs;
        private List<DiffLine> _diffLines;
        private bool _isDiff;

        // Layout cache
        private List<CharInfo> _charInfos = new List<CharInfo>();
        private int _lineCount;
        private float _maxLineWidth;

        // Selection
        private int _selStart;
        private int _selEnd;
        private bool _isSelecting;

        // Rendering params
        private const float FontSize = 14f;
        private const float LineHeight = 20f;
        private const float CharWidth = 8.4f; // approx for 14px
        private const float SpaceWidth = 4.2f;

        public string PlainText => _plainText;

        public SelectableMarkdownElement()
        {
            focusable = true;
            tabIndex = 0;
            style.flexDirection = FlexDirection.Column;
            style.overflow = Overflow.Hidden;

            // Register for mesh generation
            RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
        }

        // ============================================================
        //  PUBLIC API
        // ============================================================

        public void SetMarkdown(string text)
        {
            _isDiff = false;
            _runs = ParseMarkdown(text);
            _plainText = text ?? "";
            RebuildLayout();
            MarkDirtyRepaint();
        }

        public void SetDiff(string text)
        {
            _isDiff = true;
            _diffLines = ParseDiff(text);
            _plainText = text ?? "";
            RebuildLayout();
            MarkDirtyRepaint();
        }

        // ============================================================
        //  LAYOUT — compute character positions
        // ============================================================

        private void RebuildLayout()
        {
            _charInfos.Clear();
            _lineCount = 0;
            _maxLineWidth = 0;

            float containerWidth = resolvedStyle.width > 10 ? resolvedStyle.width : 600;
            float x = 0;
            int line = 0;

            if (_isDiff && _diffLines != null)
            {
                foreach (var dl in _diffLines)
                {
                    foreach (char c in dl.text)
                    {
                        if (c == '\n')
                        {
                            if (x > _maxLineWidth) _maxLineWidth = x;
                            x = 0;
                            line++;
                            continue;
                        }

                        float w = c == ' ' ? SpaceWidth : CharWidth;
                        if (x + w > containerWidth && x > 0)
                        {
                            if (x > _maxLineWidth) _maxLineWidth = x;
                            x = 0;
                            line++;
                        }

                        _charInfos.Add(new CharInfo
                        {
                            rect = new Rect(x, line * LineHeight, w, LineHeight),
                            lineIndex = line,
                            colIndex = _charInfos.Count
                        });
                        x += w;
                    }
                    // newline after each diff line
                    if (x > _maxLineWidth) _maxLineWidth = x;
                    x = 0;
                    line++;
                }
            }
            else if (_runs != null)
            {
                foreach (var run in _runs)
                {
                    if (run.text == "\n")
                    {
                        if (x > _maxLineWidth) _maxLineWidth = x;
                        x = 0;
                        line++;
                        continue;
                    }

                    foreach (char c in run.text)
                    {
                        if (c == '\n')
                        {
                            if (x > _maxLineWidth) _maxLineWidth = x;
                            x = 0;
                            line++;
                            continue;
                        }

                        float w = (run.code ? CharWidth * 0.9f : CharWidth);
                        if (x + w > containerWidth && x > 0)
                        {
                            if (x > _maxLineWidth) _maxLineWidth = x;
                            x = 0;
                            line++;
                        }

                        _charInfos.Add(new CharInfo
                        {
                            rect = new Rect(x, line * LineHeight, w, LineHeight),
                            lineIndex = line,
                            colIndex = _charInfos.Count
                        });
                        x += w;
                    }
                }
            }

            if (x > _maxLineWidth) _maxLineWidth = x;
            _lineCount = line + 1;

            // Update element height
            style.height = _lineCount * LineHeight;
        }

        private void OnGeometryChanged(GeometryChangedEvent evt)
        {
            if (Mathf.Abs(evt.newRect.width - evt.oldRect.width) > 1)
            {
                RebuildLayout();
                MarkDirtyRepaint();
            }
        }

        // ============================================================
        //  MESH RENDERING
        // ============================================================

        protected override void GenerateVisualContent(MeshGenerationContext mgc)
        {
            if (_charInfos.Count == 0) return;

            var mesh = mgc.Allocate(_charInfos.Count * 4, _charInfos.Count * 6);
            var vertices = mesh.vertices;
            var uvs = mesh.uvs;
            var indices = mesh.indices;
            var colors = mesh.colors;

            // Get font metrics from the panel
            var font = panel?.context?.settings?.fallbackOSFont ?? Font.CreateDynamicFontFromOSFont("Arial", (int)FontSize);
            var fontAsset = GetFontAsset();

            int vertIdx = 0;
            int charIdx = 0;

            if (_isDiff && _diffLines != null)
            {
                charIdx = RenderDiffMesh(mesh, vertices, uvs, indices, colors, charIdx, fontAsset);
            }
            else if (_runs != null)
            {
                charIdx = RenderMarkdownMesh(mesh, vertices, uvs, indices, colors, charIdx, fontAsset);
            }
        }

        private int RenderMarkdownMesh(MeshWriteData mesh, Vector3[] vertices, Vector4[] uvs, int[] indices, Color32[] colors, int charIdx, FontAsset fontAsset)
        {
            foreach (var run in _runs)
            {
                if (run.text == "\n") continue;

                Color col = GetRunColor(run);

                foreach (char c in run.text)
                {
                    if (c == ' ' || charIdx >= _charInfos.Count) { charIdx++; continue; }

                    var ci = _charInfos[charIdx];
                    DrawChar(mesh, vertices, uvs, indices, colors, ci.rect, c, col, charIdx, fontAsset);
                    charIdx++;
                }
            }
            return charIdx;
        }

        private int RenderDiffMesh(MeshWriteData mesh, Vector3[] vertices, Vector4[] uvs, int[] indices, Color32[] colors, int charIdx, FontAsset fontAsset)
        {
            foreach (var dl in _diffLines)
            {
                foreach (char c in dl.text)
                {
                    if (charIdx >= _charInfos.Count) break;

                    var ci = _charInfos[charIdx];
                    DrawChar(mesh, vertices, uvs, indices, colors, ci.rect, c, dl.textColor, charIdx, fontAsset);
                    charIdx++;
                }
                charIdx++; // skip newline
            }
            return charIdx;
        }

        private void DrawChar(MeshWriteData mesh, Vector3[] verts, Vector4[] uvs, int[] indices, Color32[] colors, Rect r, char c, Color col, int idx, FontAsset fontAsset)
        {
            int vi = idx * 4;
            int ii = idx * 6;

            if (vi + 4 > verts.Length || ii + 6 > indices.Length) return;

            // Quad vertices
            verts[vi + 0] = new Vector3(r.x, r.y + r.height, 0);
            verts[vi + 1] = new Vector3(r.x + r.width, r.y + r.height, 0);
            verts[vi + 2] = new Vector3(r.x + r.width, r.y, 0);
            verts[vi + 3] = new Vector3(r.x, r.y, 0);

            // UVs — simple atlas lookup or fallback
            if (fontAsset != null && fontAsset.TryGetCharacter(c, out Glyph glyph))
            {
                var rect = glyph.glyphRect;
                var texSize = new Vector2(fontAsset.atlasWidth, fontAsset.atlasHeight);

                float u0 = rect.x / texSize.x;
                float v0 = 1f - (rect.y + rect.height) / texSize.y;
                float u1 = (rect.x + rect.width) / texSize.x;
                float v1 = 1f - rect.y / texSize.y;

                uvs[vi + 0] = new Vector4(u0, v1, 0, 0);
                uvs[vi + 1] = new Vector4(u1, v1, 0, 0);
                uvs[vi + 2] = new Vector4(u1, v0, 0, 0);
                uvs[vi + 3] = new Vector4(u0, v0, 0, 0);

                mesh.SetTexture(fontAsset.atlasTexture);
            }
            else
            {
                // Fallback: white quad (glyph not in atlas)
                uvs[vi + 0] = Vector4.zero;
                uvs[vi + 1] = Vector4.zero;
                uvs[vi + 2] = Vector4.zero;
                uvs[vi + 3] = Vector4.zero;
            }

            // Colors
            colors[vi + 0] = col;
            colors[vi + 1] = col;
            colors[vi + 2] = col;
            colors[vi + 3] = col;

            // Indices (two triangles)
            indices[ii + 0] = vi;
            indices[ii + 1] = vi + 1;
            indices[ii + 2] = vi + 2;
            indices[ii + 3] = vi;
            indices[ii + 4] = vi + 2;
            indices[ii + 5] = vi + 3;
        }

        private FontAsset GetFontAsset()
        {
            // Try to get the default font asset from the panel
            if (panel?.context?.styleRenderer != null)
            {
                var settings = panel.context?.settings;
                if (settings != null)
                {
                    // PanelSettings has a defaultFontAsset in newer Unity versions
                    // Fallback: use Arial SDF if available
                }
            }

            // Try loading Arial SDF from project
            return Resources.Load<FontAsset>("Fonts & Materials/Arial SDF");
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
        //  SELECTION — pointer events
        // ============================================================

        protected override void ExecuteDefaultAction(EventBase evt)
        {
            base.ExecuteDefaultAction(evt);

            if (evt is PointerDownEvent pointerDown)
            {
                Focus();
                _isSelecting = true;
                _selStart = HitTest(pointerDown.localPosition);
                _selEnd = _selStart;
                this.CapturePointer(pointerDown.pointerId);
                MarkDirtyRepaint();
            }
            else if (evt is PointerMoveEvent pointerMove && _isSelecting)
            {
                int newEnd = HitTest(pointerMove.localPosition);
                if (newEnd != _selEnd)
                {
                    _selEnd = newEnd;
                    MarkDirtyRepaint();
                }
            }
            else if (evt is PointerUpEvent pointerUp && _isSelecting)
            {
                _isSelecting = false;
                this.ReleasePointer(pointerUp.pointerId);
                MarkDirtyRepaint();
            }
            else if (evt is KeyDownEvent keyDown)
            {
                if ((keyDown.ctrlKey || keyDown.commandKey) && keyDown.keyCode == KeyCode.C)
                {
                    string selected = GetSelectedText();
                    if (!string.IsNullOrEmpty(selected))
                    {
                        GUIUtility.systemCopyBuffer = selected;
                        keyDown.StopPropagation();
                    }
                }
                else if ((keyDown.ctrlKey || keyDown.commandKey) && keyDown.keyCode == KeyCode.A)
                {
                    _selStart = 0;
                    _selEnd = _plainText?.Length ?? 0;
                    MarkDirtyRepaint();
                    keyDown.StopPropagation();
                }
            }
        }

        private int HitTest(Vector2 localPos)
        {
            for (int i = 0; i < _charInfos.Count; i++)
            {
                if (_charInfos[i].rect.Contains(localPos))
                    return i;
            }
            return Mathf.Clamp(_charInfos.Count - 1, 0, _charInfos.Count);
        }

        private int SelMin => Mathf.Min(_selStart, _selEnd);
        private int SelMax => Mathf.Max(_selStart, _selEnd);

        private string GetSelectedText()
        {
            if (SelMin == SelMax) return "";
            int start = Mathf.Clamp(SelMin, 0, _plainText.Length);
            int end = Mathf.Clamp(SelMax, 0, _plainText.Length);
            return _plainText.Substring(start, end - start);
        }

        // ============================================================
        //  PARSERS (same as before)
        // ============================================================

        private static List<TextRun> ParseMarkdown(string text)
        {
            var runs = new List<TextRun>();
            if (string.IsNullOrEmpty(text)) return runs;

            var lines = text.Split('\n');
            var buf = new System.Text.StringBuilder();
            bool bold = false, italic = false, code = false;

            void Flush()
            {
                if (buf.Length > 0)
                {
                    runs.Add(new TextRun { text = buf.ToString(), bold = bold, italic = italic, code = code, color = Color.white });
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
                    if (pos + 1 < line.Length && line[pos] == '~' && line[pos + 1] == '~')
                    { Flush(); pos += 2; continue; } // skip strikethrough content
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
            var lines = new List<DiffLine>();
            if (string.IsNullOrEmpty(text)) return lines;

            var green = new Color(0.29f, 0.87f, 0.5f);
            var red = new Color(0.97f, 0.44f, 0.44f);
            var blue = new Color(0.37f, 0.65f, 0.98f);
            var dim = new Color(0.7f, 0.7f, 0.7f);

            foreach (var raw in text.Split('\n'))
            {
                Color fg = raw.StartsWith("+") && !raw.StartsWith("+++") ? green
                    : raw.StartsWith("-") && !raw.StartsWith("---") ? red
                    : raw.StartsWith("@@") ? blue : dim;

                lines.Add(new DiffLine { text = raw, bgColor = fg * new Color(1, 1, 1, 0.1f), textColor = fg });
            }
            return lines;
        }
    }
}
