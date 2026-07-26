using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace NeonCompanion.Runtime.UI.UITK.Chat
{
    /// <summary>Which gateway completion RPC a composer draft is asking for.</summary>
    internal enum ComposerCompletionKind
    {
        None,
        Slash,
        Path
    }

    /// <summary>
    /// The composer substring a completion request was built from: the token that runs from
    /// <see cref="Start"/> up to the caret. Accepting an item replaces exactly that range, so the
    /// trigger is also the staleness key — if the draft no longer holds <see cref="Text"/> at
    /// <see cref="Start"/>, the suggestion was computed for text the user has since edited away.
    /// </summary>
    internal class ComposerCompletionTrigger
    {
        public ComposerCompletionKind Kind = ComposerCompletionKind.None;
        public int Start = -1;
        public string Text = string.Empty;
    }

    /// <summary>One popover row. <see cref="InsertText"/> is the full replacement for the trigger range.</summary>
    internal class ComposerCompletionItem
    {
        public string InsertText = string.Empty;
        public string Display = string.Empty;
        public string Meta = string.Empty;
        public string Group = string.Empty;
    }

    /// <summary>
    /// Pure parsing/insertion helpers for the composer completion popover. Mirrors the Desktop
    /// composer's use of the gateway RPCs (apps/desktop/src/app/chat/composer/hooks):
    ///
    ///   commands.catalog → {pairs: [[cmd, desc]], categories: [{name, pairs}], ...}
    ///   complete.slash   → {items: [{text, display, meta}], replace_from: int}
    ///   complete.path    → {items: [{text, display, meta}]}
    ///
    /// No UI and no gateway access here so the contract handling stays readable on its own.
    /// </summary>
    internal static class ComposerCompletion
    {
        /// <summary>
        /// Longest draft prefix still treated as a completion trigger. A prose message that merely
        /// happens to start with "/" must stop asking the backend on every keystroke.
        /// </summary>
        public const int MaxTriggerLength = 100;

        /// <summary>Row cap for the popover. complete.* already cap themselves at 30 server-side.</summary>
        public const int MaxItems = 60;

        /// <summary>
        /// Classify the draft at the caret. `@`-references win over `/`-commands so that
        /// "/handoff @fi" completes the path argument rather than re-completing the command.
        /// </summary>
        public static ComposerCompletionTrigger Detect(string text, int caret)
        {
            var none = new ComposerCompletionTrigger();
            if (string.IsNullOrEmpty(text))
                return none;

            if (caret < 0)
                caret = 0;
            if (caret > text.Length)
                caret = text.Length;
            if (caret == 0)
                return none;

            string head = text.Substring(0, caret);

            // Nearest word-initial '@' left of the caret, with no whitespace in between:
            // that whole token is the `word` complete.path expects (`@`, `@file:src/ma`, `@dif`).
            for (int i = head.Length - 1; i >= 0; i--)
            {
                char c = head[i];
                if (char.IsWhiteSpace(c))
                    break;
                if (c != '@')
                    continue;
                if (i > 0 && !char.IsWhiteSpace(head[i - 1]))
                    break;

                string word = head.Substring(i);
                if (word.Length > MaxTriggerLength)
                    break;

                var path = new ComposerCompletionTrigger();
                path.Kind = ComposerCompletionKind.Path;
                path.Start = i;
                path.Text = word;
                return path;
            }

            // complete.slash requires text.startswith("/"), and a slash command is a single line,
            // so only a draft that *begins* with '/' qualifies. Args (spaces) stay part of the
            // trigger — that is what drives the backend's arg-stage completions.
            if (head[0] != '/')
                return none;
            if (head.Length > MaxTriggerLength)
                return none;
            if (head.IndexOf('\n') >= 0 || head.IndexOf('\r') >= 0)
                return none;

            var slash = new ComposerCompletionTrigger();
            slash.Kind = ComposerCompletionKind.Slash;
            slash.Start = 0;
            slash.Text = head;
            return slash;
        }

        /// <summary>
        /// complete.path items already carry the whole replacement token
        /// (`@file:src/main.tsx`, `@folder:src/`, `@diff`), so they insert verbatim.
        /// </summary>
        public static List<ComposerCompletionItem> ParsePathItems(JToken result)
        {
            var items = new List<ComposerCompletionItem>();
            JToken raw = result != null ? result["items"] : null;
            if (raw == null || raw.Type != JTokenType.Array)
                return items;

            foreach (JToken child in raw.Children())
            {
                string text = TextValue(child["text"]);
                if (string.IsNullOrEmpty(text))
                    continue;

                var item = new ComposerCompletionItem();
                item.InsertText = text;
                item.Display = FirstNonEmpty(TextValue(child["display"]), text);
                item.Meta = TextValue(child["meta"]);
                items.Add(item);

                if (items.Count >= MaxItems)
                    break;
            }

            return items;
        }

        /// <summary>
        /// complete.slash items are relative to `replace_from`, an index into the text that was
        /// sent. replace_from &gt; 1 means the backend is completing an *argument* and each item
        /// carries only the arg stub ("alice" for "/personality alic"), so the command prefix has
        /// to be put back or the draft would collapse to "/alice". Same rewrite the Desktop
        /// composer does.
        /// </summary>
        public static List<ComposerCompletionItem> ParseSlashItems(JToken result, string requestText)
        {
            var items = new List<ComposerCompletionItem>();
            if (result == null)
                return items;

            requestText = requestText ?? string.Empty;

            int replaceFrom = 1;
            JToken rf = result["replace_from"];
            if (rf != null && (rf.Type == JTokenType.Integer || rf.Type == JTokenType.Float))
                replaceFrom = (int)rf;
            if (replaceFrom < 0)
                replaceFrom = 0;
            if (replaceFrom > requestText.Length)
                replaceFrom = requestText.Length;

            bool isArgStage = replaceFrom > 1;
            string prefix = isArgStage ? requestText.Substring(0, replaceFrom) : string.Empty;

            JToken raw = result["items"];
            if (raw == null || raw.Type != JTokenType.Array)
                return items;

            foreach (JToken child in raw.Children())
            {
                string text = TextValue(child["text"]);
                if (string.IsNullOrEmpty(text))
                    continue;

                var item = new ComposerCompletionItem();
                item.InsertText = isArgStage ? prefix + text : CommandText(text);
                // Arg items can carry a leading space (`/details` → " hidden", which is how the
                // backend keeps the inserted token separated); that belongs in the draft, not in
                // the row label.
                item.Display = FirstNonEmpty(TextValue(child["display"]), item.InsertText).Trim();
                item.Meta = TextValue(child["meta"]);
                items.Add(item);

                if (items.Count >= MaxItems)
                    break;
            }

            return items;
        }

        /// <summary>
        /// commands.catalog is the registry-backed listing used for a bare "/". Prefer the
        /// categorized layout so the popover can render section headers, and fall back to the
        /// flat pairs when the backend did not categorize.
        /// </summary>
        public static List<ComposerCompletionItem> ParseCatalogItems(JToken result)
        {
            var items = new List<ComposerCompletionItem>();
            if (result == null)
                return items;

            JToken categories = result["categories"];
            if (categories != null && categories.Type == JTokenType.Array && categories.HasValues)
            {
                foreach (JToken category in categories.Children())
                {
                    string name = TextValue(category["name"]);
                    AppendPairs(items, category["pairs"], name);
                    if (items.Count >= MaxItems)
                        break;
                }

                if (items.Count > 0)
                    return items;
            }

            AppendPairs(items, result["pairs"], string.Empty);
            return items;
        }

        /// <summary>
        /// Replace [start, end) with <paramref name="insertText"/> and report the new caret.
        /// A completion that deliberately ends mid-token (a `@folder:src/` directory step, or a
        /// bare `@file:` tag) gets no trailing space — the user is meant to keep typing into it.
        /// </summary>
        public static string ApplyItem(string text, int start, int end, string insertText, out int caret)
        {
            text = text ?? string.Empty;
            insertText = insertText ?? string.Empty;

            if (start < 0)
                start = 0;
            if (start > text.Length)
                start = text.Length;
            if (end < start)
                end = start;
            if (end > text.Length)
                end = text.Length;

            bool openEnded = insertText.EndsWith("/", StringComparison.Ordinal)
                || insertText.EndsWith(":", StringComparison.Ordinal);
            string tail = openEnded ? string.Empty : " ";

            // Don't stack a second space when the draft already continues with one.
            if (tail.Length > 0 && end < text.Length && text[end] == ' ')
                tail = string.Empty;

            string updated = text.Substring(0, start) + insertText + tail + text.Substring(end);
            caret = start + insertText.Length + tail.Length;
            return updated;
        }

        private static void AppendPairs(List<ComposerCompletionItem> items, JToken pairs, string group)
        {
            if (pairs == null || pairs.Type != JTokenType.Array)
                return;

            foreach (JToken pair in pairs.Children())
            {
                JArray fields = pair as JArray;
                if (fields == null || fields.Count == 0)
                    continue;

                string command = TextValue(fields[0]);
                if (string.IsNullOrEmpty(command))
                    continue;

                var item = new ComposerCompletionItem();
                item.InsertText = CommandText(command);
                item.Display = item.InsertText;
                item.Meta = fields.Count > 1 ? TextValue(fields[1]) : string.Empty;
                item.Group = group ?? string.Empty;
                items.Add(item);

                if (items.Count >= MaxItems)
                    return;
            }
        }

        private static string CommandText(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            return value.StartsWith("/", StringComparison.Ordinal) ? value : "/" + value;
        }

        private static string FirstNonEmpty(string value, string fallback)
        {
            return string.IsNullOrEmpty(value) ? (fallback ?? string.Empty) : value;
        }

        /// <summary>
        /// display/meta are plain strings over this gateway, but the TUI protocol also allows
        /// prompt_toolkit FormattedText (a list of [style, text] pairs) — flatten that instead of
        /// rendering a JSON blob in the popover.
        /// </summary>
        private static string TextValue(JToken token)
        {
            if (token == null)
                return string.Empty;

            if (token.Type == JTokenType.String)
                return token.Value<string>() ?? string.Empty;

            if (token.Type == JTokenType.Array)
            {
                var sb = new System.Text.StringBuilder();
                foreach (JToken part in token.Children())
                {
                    if (part.Type == JTokenType.String)
                    {
                        sb.Append(part.Value<string>());
                        continue;
                    }

                    JArray tuple = part as JArray;
                    if (tuple != null && tuple.Count > 1)
                        sb.Append(TextValue(tuple[1]));
                }
                return sb.ToString().Trim();
            }

            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float || token.Type == JTokenType.Boolean)
                return token.ToString();

            return string.Empty;
        }
    }
}
