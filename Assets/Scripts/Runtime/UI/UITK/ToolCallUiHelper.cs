using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK
{
    internal sealed class ToolCallUiHelper
    {
        private VisualElement _bubble;
        private readonly Dictionary<string, VisualElement> _entries = new Dictionary<string, VisualElement>();

        public void SetBubble(VisualElement bubble)
        {
            _bubble = bubble;
            _entries.Clear();
        }

        public void Clear()
        {
            _bubble = null;
            _entries.Clear();
        }

        internal static VisualElement CreateEntryElement(string tool, string label, string emoji, string status)
        {
            string truncated = label != null && label.Length > 60
                ? label.Substring(0, 60) + "..."
                : label ?? string.Empty;

            bool isDone = string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(status, "done", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(status, "complete", StringComparison.OrdinalIgnoreCase);
            string icon = string.IsNullOrEmpty(emoji) ? GetToolEmoji(tool) : emoji;

            // Header row (clickable to expand/collapse)
            var header = new VisualElement();
            header.AddToClassList("tool-entry");
            header.AddToClassList("tool-entry--header");

            var toggleLabel = new Label(isDone ? "▼" : "▶");
            toggleLabel.AddToClassList("tool-entry__toggle");

            var iconLabel = new Label(icon);
            iconLabel.AddToClassList("tool-entry__icon");

            var nameLabel = new Label(tool ?? string.Empty);
            nameLabel.AddToClassList("tool-entry__name");

            var detailLabel = new Label(truncated);
            detailLabel.AddToClassList("tool-entry__label");

            var statusLabel = new Label(isDone ? "✓" : "●");
            statusLabel.AddToClassList("tool-entry__status");
            statusLabel.AddToClassList(isDone ? "tool-entry__status--done" : "tool-entry__status--running");

            header.Add(toggleLabel);
            header.Add(iconLabel);
            header.Add(nameLabel);
            header.Add(detailLabel);
            header.Add(statusLabel);

            // Details panel (hidden by default)
            var details = new VisualElement();
            details.AddToClassList("tool-entry__details");
            details.style.display = DisplayStyle.None;

            var argsLabel = new Label(truncated);
            argsLabel.AddToClassList("tool-entry__args");
            details.Add(argsLabel);

            // Root container
            var root = new VisualElement();
            root.AddToClassList("tool-entry-root");
            root.Add(header);
            root.Add(details);

            // Toggle on click
            bool expanded = false;
            header.RegisterCallback<ClickEvent>(evt =>
            {
                expanded = !expanded;
                toggleLabel.text = expanded ? "▼" : "▶";
                details.style.display = expanded ? DisplayStyle.Flex : DisplayStyle.None;
                evt.StopPropagation();
            });

            // I-beam cursor on header for text selection
            header.RegisterCallback<MouseEnterEvent>(evt =>
            {
                header.style.cursor = new Cursor();
                header.style.cursor = CursorStyle.Arrow;
            });

            return root;
        }

        public bool OnToolProgress(string tool, string label, string emoji, string status)
        {
            if (_bubble == null)
                return false;

            string key = tool + "\x01" + label;

            VisualElement existing;
            if (_entries.TryGetValue(key, out existing))
            {
                if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(status, "done", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(status, "complete", StringComparison.OrdinalIgnoreCase))
                    MarkEntryDone(existing);
                return false;
            }

            var entry = CreateEntryElement(tool, label, emoji, status);
            _bubble.Insert(GetInsertIndex(), entry);
            _entries[key] = entry;
            return true;
        }

        private int GetInsertIndex()
        {
            if (_bubble == null)
                return 0;

            int insertIndex = _bubble.childCount;
            if (insertIndex > 0 && _bubble[insertIndex - 1].ClassListContains("typing--inline"))
                insertIndex--;

            return insertIndex;
        }

        private static void MarkEntryDone(VisualElement entry)
        {
            // Update root entry (may be wrapped in tool-entry-root)
            var root = entry.ClassListContains("tool-entry-root") ? entry : entry.parent;
            if (root == null) return;

            var statusLabel = root.Q<Label>(className: "tool-entry__status");
            if (statusLabel != null)
            {
                statusLabel.text = "✓";
                statusLabel.RemoveFromClassList("tool-entry__status--running");
                statusLabel.AddToClassList("tool-entry__status--done");
            }

            var toggleLabel = root.Q<Label>(className: "tool-entry__toggle");
            if (toggleLabel != null)
                toggleLabel.text = "▼";
        }

        private static string GetToolEmoji(string tool)
        {
            if (string.IsNullOrWhiteSpace(tool))
                return "⚡";

            string lower = tool.ToLowerInvariant();
            if (lower.Contains("terminal") || lower.Contains("bash") || lower.Contains("shell"))
                return "💻";
            if (lower.Contains("search") || lower.Contains("grep"))
                return "🔍";
            if (lower.Contains("read"))
                return "📄";
            if (lower.Contains("write") || lower.Contains("edit"))
                return "✏️";
            return "⚡";
        }
    }
}
