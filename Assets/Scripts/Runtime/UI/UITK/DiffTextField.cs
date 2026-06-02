using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK
{
    /// <summary>
    /// TextField с кастомной отрисовкой diff-подсветки.
    /// Наследует от TextField → нативный текст, selection, Ctrl+C.
    /// Подписывается на generateVisualContent → рисует цветные фоны.
    /// </summary>
    internal class DiffTextField : TextField
    {
        private struct DiffLine
        {
            public int startIndex;
            public int endIndex;
            public Color bgColor;
            public Color textColor;
        }

        private List<DiffLine> _diffLines = new List<DiffLine>();
        private bool _isDiff;

        public DiffTextField()
        {
            isReadOnly = true;
            multiline = true;
            generateVisualContent += OnGenerateVisualContent;
        }

        /// <summary>
        /// Set plain text content (no diff highlighting).
        /// </summary>
        public void SetText(string text)
        {
            _isDiff = false;
            _diffLines.Clear();
            value = text ?? "";
        }

        /// <summary>
        /// Set diff content with colored line highlighting.
        /// </summary>
        public void SetDiff(string text)
        {
            _isDiff = true;
            _diffLines.Clear();
            ParseDiffLines(text ?? "");
            value = text ?? "";
        }

        // ============================================================
        //  DIFF PARSING — compute char ranges for colored lines
        // ============================================================

        private void ParseDiffLines(string text)
        {
            var green = new Color(0.29f, 0.87f, 0.5f, 0.2f);
            var red = new Color(0.97f, 0.44f, 0.44f, 0.2f);
            var blue = new Color(0.37f, 0.65f, 0.98f, 0.15f);

            int charIdx = 0;
            var lines = text.Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int lineStart = charIdx;
                int lineEnd = charIdx + line.Length;

                Color bg = Color.clear;
                if (line.StartsWith("+") && !line.StartsWith("+++"))
                    bg = green;
                else if (line.StartsWith("-") && !line.StartsWith("---"))
                    bg = red;
                else if (line.StartsWith("@@"))
                    bg = blue;

                if (bg.a > 0)
                {
                    _diffLines.Add(new DiffLine
                    {
                        startIndex = lineStart,
                        endIndex = lineEnd,
                        bgColor = bg
                    });
                }

                charIdx = lineEnd + 1; // +1 for \n
            }
        }

        // ============================================================
        //  MESH GENERATION — draw colored rectangles behind text
        // ============================================================

        private void OnGenerateVisualContent(MeshGenerationContext mgc)
        {
            if (!_isDiff || _diffLines.Count == 0) return;

            // Get text input element for character coordinates
            var textInput = this.Q<VisualElement>("unity-text-input");
            if (textInput == null) return;

            var worldBound = textInput.worldBound;
            var localOrigin = worldBound.position;

            // For each diff line, draw a colored rectangle
            foreach (var dl in _diffLines)
            {
                DrawLineBackground(mgc, dl, localOrigin, textInput);
            }
        }

        private void DrawLineBackground(MeshGenerationContext mgc, DiffLine dl, Vector2 localOrigin, VisualElement textInput)
        {
            // Estimate line height and position from character index
            // TextField renders text with a known line height
            float lineHeight = 20f; // approximate for 14px font
            float fontSize = 14f;

            // Count newlines before this line to get Y position
            string fullText = value ?? "";
            int lineIndex = 0;
            for (int i = dl.startIndex; i > 0 && i < fullText.Length; i--)
            {
                if (fullText[i] == '\n') lineIndex++;
            }

            float y = lineIndex * lineHeight;
            float lineWidth = resolvedStyle.width > 0 ? resolvedStyle.width : textInput.resolvedStyle.width;
            float rectHeight = lineHeight;

            // Allocate a quad for the background
            var mesh = mgc.Allocate(4, 6);
            var verts = mesh.vertices;
            var colors32 = mesh.colors;
            var indices = mesh.indices;
            var uvs = mesh.uvs;

            Color bg = dl.bgColor;

            // Rectangle vertices
            verts[0] = new Vector3(0, y + rectHeight, 0);           // top-left
            verts[1] = new Vector3(lineWidth, y + rectHeight, 0);   // top-right
            verts[2] = new Vector3(lineWidth, y, 0);                 // bottom-right
            verts[3] = new Vector3(0, y, 0);                         // bottom-left

            // Colors
            colors32[0] = bg;
            colors32[1] = bg;
            colors32[2] = bg;
            colors32[3] = bg;

            // UVs (white texture)
            uvs[0] = Vector4.zero;
            uvs[1] = Vector4.zero;
            uvs[2] = Vector4.zero;
            uvs[3] = Vector4.zero;

            // Indices
            indices[0] = 0; indices[1] = 1; indices[2] = 2;
            indices[3] = 0; indices[4] = 2; indices[5] = 3;
        }
    }

    /// <summary>
    /// Markdown-aware TextField with formatted display.
    /// Shows formatted text via labels, uses TextField for selection.
    /// </summary>
    internal class MarkdownTextField : TextField
    {
        private string _rawText;

        public MarkdownTextField()
        {
            isReadOnly = true;
            multiline = true;
        }

        public void SetMarkdown(string text)
        {
            _rawText = text ?? "";
            // For now, show plain text — formatted rendering comes later
            value = _rawText;
        }
    }
}
