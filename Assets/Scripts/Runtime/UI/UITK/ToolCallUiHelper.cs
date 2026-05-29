using System;
using System.Collections.Generic;
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
            string truncated = label != null && label.Length > 40
                ? label.Substring(0, 40) + "..."
                : label ?? string.Empty;

            bool isDone = string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(status, "done", StringComparison.OrdinalIgnoreCase);
            string icon = string.IsNullOrEmpty(emoji) ? GetToolEmoji(tool) : emoji;
            return BuildEntry(icon, tool ?? string.Empty, truncated, isDone);
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
                    string.Equals(status, "done", StringComparison.OrdinalIgnoreCase))
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

        private static VisualElement BuildEntry(string icon, string name, string labelText, bool done)
        {
            var entry = new VisualElement();
            entry.AddToClassList("tool-entry");

            var iconLabel = new Label(icon);
            iconLabel.AddToClassList("tool-entry__icon");

            var nameLabel = new Label(name);
            nameLabel.AddToClassList("tool-entry__name");

            var detailLabel = new Label(labelText);
            detailLabel.AddToClassList("tool-entry__label");

            var statusLabel = new Label(done ? "✓" : "●");
            statusLabel.AddToClassList("tool-entry__status");
            statusLabel.AddToClassList(done ? "tool-entry__status--done" : "tool-entry__status--running");

            entry.Add(iconLabel);
            entry.Add(nameLabel);
            entry.Add(detailLabel);
            entry.Add(statusLabel);
            return entry;
        }

        private static void MarkEntryDone(VisualElement entry)
        {
            var statusLabel = entry.Q<Label>(className: "tool-entry__status");
            if (statusLabel == null)
                return;

            statusLabel.text = "✓";
            statusLabel.RemoveFromClassList("tool-entry__status--running");
            statusLabel.AddToClassList("tool-entry__status--done");
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
