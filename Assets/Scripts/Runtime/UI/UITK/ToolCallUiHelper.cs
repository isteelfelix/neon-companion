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
        private static readonly Dictionary<string, string> ToolEmojiByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "read_file", "📖" },
            { "write_file", "✍️" },
            { "patch", "🔧" },
            { "search_files", "🔎" },
            { "web_search", "🌐" },
            { "web_extract", "📄" },
            { "terminal", "⚡" },
            { "process", "⚡" },
            { "vision_analyze", "👁" },
            { "image_generate", "🎨" },
            { "skills_list", "📋" },
            { "skill_view", "📋" },
            { "skill_manage", "📋" },
            { "todo", "✅" },
            { "memory", "🧠" },
            { "session_search", "🔍" },
            { "clarify", "❓" },
            { "execute_code", "🐍" },
            { "delegate_task", "🤖" },
            { "cronjob", "⏰" },
            { "send_message", "💬" },
            { "browser_navigate", "🌍" },
            { "browser_click", "🖱" },
            { "browser_type", "⌨️" },
            { "browser_scroll", "🖱" },
            { "browser_back", "↩️" },
            { "browser_press", "⌨️" },
            { "browser_get_images", "🖼" },
            { "browser_vision", "👁" },
            { "browser_console", "🖥" },
            { "browser_cdp", "🖥" },
            { "browser_dialog", "💬" },
            { "text_to_speech", "🔊" },
            { "computer_use", "🖥" },
            { "kanban_show", "📋" },
            { "kanban_list", "📋" },
            { "kanban_complete", "✅" },
            { "kanban_block", "🚫" },
            { "kanban_heartbeat", "💓" },
            { "kanban_comment", "💬" },
            { "kanban_create", "➕" },
            { "kanban_link", "🔗" },
            { "kanban_unblock", "🔓" },
            { "ha_list_entities", "🏠" },
            { "ha_get_state", "🏠" },
            { "ha_list_services", "🏠" },
            { "ha_call_service", "🏠" }
        };

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

        internal static VisualElement CreateEntryElement(string tool, string label, string emoji, string status, string inlineDiff = null, string detailsText = null)
        {
            string cleanLabel = SelectableMarkdownElement.StripAnsi(label);
            string truncated = cleanLabel != null && cleanLabel.Length > 60
                ? cleanLabel.Substring(0, 60) + "..."
                : cleanLabel ?? string.Empty;

            bool isDone = string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(status, "done", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(status, "complete", StringComparison.OrdinalIgnoreCase);
            string icon = string.IsNullOrEmpty(emoji) ? GetToolEmoji(tool) : emoji;

            // Header row (clickable to expand/collapse)
            var header = new VisualElement();
            header.AddToClassList("tool-entry");
            header.AddToClassList("tool-entry--header");
            header.AddToClassList(isDone ? "tool-entry--done" : "tool-entry--running");

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

            if (!string.IsNullOrEmpty(inlineDiff))
            {
                var diffView = new SelectableMarkdownElement();
                diffView.SetDiff(inlineDiff);
                diffView.AddToClassList("tool-entry__diff");
                diffView.style.flexGrow = 1;
                diffView.style.minHeight = 20;
                details.Add(diffView);
            }
            else if (!string.IsNullOrWhiteSpace(detailsText))
            {
                var detailsView = new SelectableMarkdownElement();
                detailsView.SetMarkdown(detailsText);
                detailsView.AddToClassList("tool-entry__args");
                detailsView.style.flexGrow = 1;
                detailsView.style.minWidth = 0;
                detailsView.style.width = Length.Percent(100);
                detailsView.style.minHeight = 20;
                details.Add(detailsView);
            }
            else
            {
                var argsLabel = new Label(truncated);
                argsLabel.AddToClassList("tool-entry__args");
                details.Add(argsLabel);
            }

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

            var headerEl = root.Q(className: "tool-entry--header");
            if (headerEl != null)
            {
                headerEl.RemoveFromClassList("tool-entry--running");
                headerEl.AddToClassList("tool-entry--done");
            }

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
                return "⚙️";

            string normalized = tool.Trim();
            string mapped;
            if (ToolEmojiByName.TryGetValue(normalized, out mapped))
                return mapped;

            string lower = normalized.ToLowerInvariant();
            if (lower.Contains("skill"))
                return "📋";
            if (lower.Contains("execute_code") || lower.Contains("python") || lower.Contains("eval"))
                return "🐍";
            if (lower.Contains("delegate") || lower.Contains("subagent"))
                return "🤖";
            if (lower.Contains("process"))
                return "⚡";
            if (lower.Contains("todo"))
                return "✅";
            if (lower.Contains("clarify"))
                return "❓";
            if (lower.Contains("session_search"))
                return "🔍";
            if (lower.Contains("memory"))
                return "🧠";
            if (lower.Contains("fact"))
                return "📌";
            if (lower.Contains("terminal") || lower.Contains("bash") || lower.Contains("shell"))
                return "⚡";
            if (lower.Contains("patch"))
                return "🔧";
            if (lower.Contains("search") || lower.Contains("grep"))
                return "🔎";
            if (lower.Contains("read"))
                return "📖";
            if (lower.Contains("write") || lower.Contains("edit"))
                return "✍️";
            return "⚙️";
        }
    }
}
