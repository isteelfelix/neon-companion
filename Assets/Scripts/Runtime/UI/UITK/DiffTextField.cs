using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK
{
    /// <summary>
    /// TextField с diff-подсветкой через colored background elements.
    /// Наследует от TextField → нативный текст, selection, Ctrl+C.
    /// Цветные фоны — через отдельные VisualElement позиционированные поверх текста.
    /// </summary>
    internal class DiffTextField : TextField
    {
        private struct DiffLine
        {
            public int lineIndex;
            public Color bgColor;
        }

        private List<DiffLine> _diffLines = new List<DiffLine>();
        private VisualElement _highlightLayer;
        private string _lastText;

        public DiffTextField()
        {
            isReadOnly = true;
            multiline = true;

            // Highlight layer sits behind the text
            _highlightLayer = new VisualElement();
            _highlightLayer.pickingMode = PickingMode.Ignore;
            _highlightLayer.style.position = Position.Absolute;
            _highlightLayer.style.left = 0;
            _highlightLayer.style.right = 0;
            _highlightLayer.style.top = 0;
            _highlightLayer.style.bottom = 0;
            _highlightLayer.style.flexDirection = FlexDirection.Column;
            Insert(0, _highlightLayer);

            // Rebuild highlights when text or size changes
            RegisterCallback<ChangeEvent<string>>(_ => RebuildHighlights());
            RegisterCallback<GeometryChangedEvent>(_ => RebuildHighlights());
        }

        public void SetText(string text)
        {
            _diffLines.Clear();
            _lastText = text;
            value = text ?? "";
            RebuildHighlights();
        }

        public void SetDiff(string text)
        {
            _diffLines.Clear();
            _lastText = text;
            ParseDiffLines(text ?? "");
            value = text ?? "";
            RebuildHighlights();
        }

        private void ParseDiffLines(string text)
        {
            var green = new Color(0.29f, 0.87f, 0.5f, 0.25f);
            var red = new Color(0.97f, 0.44f, 0.44f, 0.25f);
            var blue = new Color(0.37f, 0.65f, 0.98f, 0.15f);

            var lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                Color bg = Color.clear;

                if (line.StartsWith("+") && !line.StartsWith("+++"))
                    bg = green;
                else if (line.StartsWith("-") && !line.StartsWith("---"))
                    bg = red;
                else if (line.StartsWith("@@"))
                    bg = blue;

                if (bg.a > 0)
                {
                    _diffLines.Add(new DiffLine { lineIndex = i, bgColor = bg });
                }
            }
        }

        private void RebuildHighlights()
        {
            _highlightLayer.Clear();

            if (_diffLines.Count == 0) return;

            float lineHeight = 20f; // approximate
            float containerWidth = resolvedStyle.width > 0 ? resolvedStyle.width : 400;

            foreach (var dl in _diffLines)
            {
                var highlight = new VisualElement();
                highlight.style.position = Position.Absolute;
                highlight.style.left = 0;
                highlight.style.width = Length.Percent(100);
                highlight.style.top = dl.lineIndex * lineHeight;
                highlight.style.height = lineHeight;
                highlight.style.backgroundColor = dl.bgColor;
                highlight.pickingMode = PickingMode.Ignore;
                _highlightLayer.Add(highlight);
            }
        }
    }
}
