using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine.UIElements;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Localization;

namespace NeonCompanion.Runtime.UI.UITK
{
    internal sealed class ApprovalPrompt
    {
        public event Action<bool, bool> OnDecision; // (approved, alwaysApprove)
        /// <summary>Fires when a custom choice button is selected (Hermes approval with server-sent choices).</summary>
        public event Action<string> OnChoiceSelected;

        private VisualElement _root;

        public VisualElement Create(ToolCallRequest request)
        {
            _root = new VisualElement();
            _root.AddToClassList("approval-prompt");

            // Header row: icon + tool name
            var header = new VisualElement();
            header.AddToClassList("approval-prompt__header");

            var icon = new Label("\uD83D\uDD27"); // 🔧
            icon.AddToClassList("approval-prompt__icon");

            string toolTitle = LocalizationExtensions.Get("approval.title", "Tool Request");
            string toolName = request != null && !string.IsNullOrEmpty(request.toolName) ? request.toolName : "";
            var titleLabel = new Label(toolTitle + ": " + toolName);
            titleLabel.AddToClassList("approval-prompt__tool-name");

            header.Add(icon);
            header.Add(titleLabel);
            _root.Add(header);

            // Description
            string descText = request != null ? request.description : null;
            if (!string.IsNullOrEmpty(descText))
            {
                var desc = new Label(descText);
                desc.AddToClassList("approval-prompt__description");
                _root.Add(desc);
            }

            // Parameters (if any)
            if (request != null && request.parameters != null && request.parameters.Count > 0)
            {
                var sb = new StringBuilder();
                bool first = true;
                foreach (var kv in request.parameters)
                {
                    if (!first) sb.Append("\n");
                    first = false;
                    sb.Append(kv.Key);
                    sb.Append(": ");
                    sb.Append(kv.Value);
                }

                var paramsLabel = new Label(sb.ToString());
                paramsLabel.AddToClassList("approval-prompt__params");
                _root.Add(paramsLabel);
            }

            // Buttons row
            var buttons = new VisualElement();
            buttons.AddToClassList("approval-prompt__buttons");

            string approveText = "\u2713 " + LocalizationExtensions.Get("approval.approve", "Approve");
            var approveBtn = new Button(() => FireDecision(true, false));
            approveBtn.text = approveText;
            approveBtn.AddToClassList("approval-prompt__btn");
            approveBtn.AddToClassList("approval-prompt__btn--approve");

            string rejectText = "\u2717 " + LocalizationExtensions.Get("approval.reject", "Reject");
            var rejectBtn = new Button(() => FireDecision(false, false));
            rejectBtn.text = rejectText;
            rejectBtn.AddToClassList("approval-prompt__btn");
            rejectBtn.AddToClassList("approval-prompt__btn--reject");

            buttons.Add(approveBtn);
            buttons.Add(rejectBtn);

            // "Always" is offered only for safe tools. Dangerous tools (code execution, shell,
            // file writes) require explicit consent on every call, so a persistent grant is hidden.
            if (!NeonCompanion.Runtime.Api.Tools.ToolExecutor.IsDangerousTool(toolName))
            {
                string alwaysText = "\u26A1 " + LocalizationExtensions.Get("approval.always", "Always");
                var alwaysBtn = new Button(() => FireDecision(true, true));
                alwaysBtn.text = alwaysText;
                alwaysBtn.AddToClassList("approval-prompt__btn");
                alwaysBtn.AddToClassList("approval-prompt__btn--always");
                buttons.Add(alwaysBtn);
            }

            _root.Add(buttons);

            return _root;
        }

        /// <summary>
        /// Hermes-specific overload that renders server-sent choices instead of the default
        /// Approve/Reject/Always triad. When the backend supplies choices (e.g. ["once","deny"]
        /// for smart_denied, or omits "always" when allow_permanent=false), each choice becomes
        /// a labeled button. Falls back to the default layout when choices is null or empty.
        /// </summary>
        public VisualElement Create(ToolCallRequest request, string[] choices, bool allowPermanent, bool smartDenied)
        {
            if (choices == null || choices.Length == 0)
                return Create(request); // fall back to default Approve/Reject/Always

            _root = new VisualElement();
            _root.AddToClassList("approval-prompt");

            // Header
            var header = new VisualElement();
            header.AddToClassList("approval-prompt__header");

            var icon = new Label("\uD83D\uDD27");
            icon.AddToClassList("approval-prompt__icon");

            string toolTitle = LocalizationExtensions.Get("approval.title", "Tool Request");
            string toolName = request != null && !string.IsNullOrEmpty(request.toolName) ? request.toolName : "";
            var titleLabel = new Label(toolTitle + ": " + toolName);
            titleLabel.AddToClassList("approval-prompt__tool-name");

            header.Add(icon);
            header.Add(titleLabel);
            _root.Add(header);

            if (smartDenied)
            {
                var warn = new Label(LocalizationExtensions.Get("approval.smart_denied_warning",
                    "This command was flagged as potentially dangerous."));
                warn.AddToClassList("approval-prompt__description");
                _root.Add(warn);
            }

            string descText = request != null ? request.description : null;
            if (!string.IsNullOrEmpty(descText))
            {
                var desc = new Label(descText);
                desc.AddToClassList("approval-prompt__description");
                _root.Add(desc);
            }

            if (request != null && request.parameters != null && request.parameters.Count > 0)
            {
                var sb = new StringBuilder();
                bool first = true;
                foreach (var kv in request.parameters)
                {
                    if (!first) sb.Append("\n");
                    first = false;
                    sb.Append(kv.Key);
                    sb.Append(": ");
                    sb.Append(kv.Value);
                }
                var paramsLabel = new Label(sb.ToString());
                paramsLabel.AddToClassList("approval-prompt__params");
                _root.Add(paramsLabel);
            }

            var buttons = new VisualElement();
            buttons.AddToClassList("approval-prompt__buttons");

            for (int i = 0; i < choices.Length; i++)
            {
                string choice = choices[i];
                if (string.IsNullOrEmpty(choice))
                    continue;
                // "always" requires allowPermanent; skip it when the backend forbids persistent grants.
                if (string.Equals(choice, "always", StringComparison.OrdinalIgnoreCase) && !allowPermanent)
                    continue;

                string label = GetChoiceLabel(choice);
                string captured = choice; // local copy for closure
                var btn = new Button(() => OnChoiceSelected?.Invoke(captured));
                btn.text = label;
                btn.AddToClassList("approval-prompt__btn");
                if (string.Equals(choice, "deny", StringComparison.OrdinalIgnoreCase))
                    btn.AddToClassList("approval-prompt__btn--reject");
                else
                    btn.AddToClassList("approval-prompt__btn--approve");
                buttons.Add(btn);
            }

            _root.Add(buttons);
            return _root;
        }

        private static string GetChoiceLabel(string choice)
        {
            if (string.Equals(choice, "once", StringComparison.OrdinalIgnoreCase))
                return "\u2713 " + LocalizationExtensions.Get("approval.run_once", "Run once");
            if (string.Equals(choice, "session", StringComparison.OrdinalIgnoreCase))
                return "\u2713 " + LocalizationExtensions.Get("approval.allow_session", "Allow for session");
            if (string.Equals(choice, "always", StringComparison.OrdinalIgnoreCase))
                return "\u26A1 " + LocalizationExtensions.Get("approval.always", "Always");
            if (string.Equals(choice, "deny", StringComparison.OrdinalIgnoreCase))
                return "\u2717 " + LocalizationExtensions.Get("approval.reject", "Reject");
            return choice; // unknown choice: show raw string
        }

        public void SetVisible(bool visible)
        {
            if (_root != null)
            {
                _root.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        private void FireDecision(bool approved, bool always)
        {
            if (OnDecision != null)
            {
                OnDecision(approved, always);
            }
        }
    }
}
