using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Chat;
using NeonCompanion.Runtime.Localization;
using NeonCompanion.Runtime.Data.Models;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK.Chat
{
    internal sealed class ChatMessageEditController
    {
        private readonly Func<Task<ChatService>> _getChatServiceAsync;
        private readonly Action<IReadOnlyList<ChatMessage>> _renderMessages;
        private readonly Func<Task> _loadSessionsAsync;
        private readonly Func<Task> _regenerateAsync;

        private int? _editingMessageIndex;
        private VisualElement _editingBubble;
        private VisualElement _editingContainer;
        private TextField _editingTextField;
        private Button _editingSaveBtn;
        private Button _editingCancelBtn;

        internal bool IsEditing => _editingMessageIndex.HasValue;

        internal ChatMessageEditController(
            Func<Task<ChatService>> getChatServiceAsync,
            Action<IReadOnlyList<ChatMessage>> renderMessages,
            Func<Task> loadSessionsAsync,
            Func<Task> regenerateAsync)
        {
            _getChatServiceAsync = getChatServiceAsync;
            _renderMessages = renderMessages;
            _loadSessionsAsync = loadSessionsAsync;
            _regenerateAsync = regenerateAsync;
        }

        internal void BeginEditMessage(int index, VisualElement bubble, string currentContent)
        {
            if (_editingMessageIndex != null)
                CancelEdit();

            _editingMessageIndex = index;
            _editingBubble = bubble;

            ShowEditOverlay(index, bubble, currentContent);
        }

        internal void CommitEdit(bool doSave, bool regenerateAfter)
        {
            if (_editingMessageIndex == null || _editingTextField == null || _editingBubble == null)
            {
                CancelEdit();
                return;
            }

            int index = _editingMessageIndex.Value;
            var chat = _getChatServiceAsync().Result;
            if (chat == null || chat.CurrentChatViewModel == null || chat.CurrentChatViewModel.Messages == null)
            {
                CancelEdit();
                return;
            }

            var messages = chat.CurrentChatViewModel.Messages;
            if (index < 0 || index >= messages.Count)
            {
                CancelEdit();
                return;
            }

            if (doSave)
            {
                string newContent = (_editingTextField.value ?? string.Empty).Trim();
                messages[index].content = newContent;

                if (messages[index].segments != null)
                    messages[index].segments.Clear();

                if (regenerateAfter)
                {
                    while (messages.Count > index + 1)
                        messages.RemoveAt(messages.Count - 1);

                    _renderMessages(messages);
                    _ = chat.SaveCurrentSessionAsync();
                    _ = _loadSessionsAsync();

                    _ = _regenerateAsync();
                    CancelEdit();
                    return;
                }

                _renderMessages(messages);
                _ = chat.SaveCurrentSessionAsync();
                _ = _loadSessionsAsync();
            }

            CancelEdit();
        }

        internal void CancelEdit()
        {
            HideEditOverlay();

            _editingMessageIndex = null;
            _editingBubble = null;
            _editingContainer = null;
            _editingTextField = null;
            _editingSaveBtn = null;
            _editingCancelBtn = null;
        }

        private void ShowEditOverlay(int index, VisualElement bubble, string currentContent)
        {
            var bodies = bubble.Query<VisualElement>(className: "transcript__body").ToList();
            for (int i = 0; i < bodies.Count; i++)
                bodies[i].style.display = DisplayStyle.None;

            var container = new VisualElement();
            container.AddToClassList("message-edit-container");

            var tf = new TextField();
            tf.AddToClassList("message-edit-field");
            tf.multiline = true;
            tf.value = currentContent;
            container.Add(tf);

            var btnRow = new VisualElement();
            btnRow.AddToClassList("message-edit-buttons");
            btnRow.style.flexDirection = FlexDirection.Row;

            string saveLabel = LocalizationExtensions.Get("msg.edit.save", "Save");
            var saveBtn = new Button(() => CommitEdit(true, false));
            saveBtn.text = saveLabel;
            saveBtn.AddToClassList("message-edit-btn");
            saveBtn.AddToClassList("message-edit-btn--save");
            btnRow.Add(saveBtn);

            string cancelLabel = LocalizationExtensions.Get("msg.edit.cancel", "Cancel");
            var cancelBtn = new Button(() => CommitEdit(false, false));
            cancelBtn.text = cancelLabel;
            cancelBtn.AddToClassList("message-edit-btn");
            cancelBtn.AddToClassList("message-edit-btn--cancel");
            btnRow.Add(cancelBtn);

            var chat = _getChatServiceAsync().Result;
            var msgs = (chat != null && chat.CurrentChatViewModel != null) ? chat.CurrentChatViewModel.Messages : null;
            bool hasFollowingAssistant = msgs != null &&
                                         index + 1 < msgs.Count &&
                                         msgs[index + 1] != null &&
                                         IsAssistantRole(msgs[index + 1].role);
            if (hasFollowingAssistant)
            {
                string regenLabel = LocalizationExtensions.Get("msg.edit.save_regen", "Save & Regenerate");
                var regenBtn = new Button(() => CommitEdit(true, true));
                regenBtn.text = regenLabel;
                regenBtn.AddToClassList("message-edit-btn");
                regenBtn.AddToClassList("message-edit-btn--regen");
                btnRow.Add(regenBtn);
            }

            container.Add(btnRow);

            var meta = bubble.Q<VisualElement>(className: "transcript__meta");
            if (meta != null)
            {
                int metaIdx = bubble.IndexOf(meta);
                if (metaIdx >= 0)
                    bubble.Insert(metaIdx + 1, container);
                else
                    bubble.Add(container);
            }
            else
            {
                bubble.Add(container);
            }

            _editingContainer = container;
            _editingTextField = tf;
            _editingSaveBtn = saveBtn;
            _editingCancelBtn = cancelBtn;

            tf.Focus();
        }

        private void HideEditOverlay()
        {
            if (_editingContainer != null && _editingContainer.parent != null)
                _editingContainer.RemoveFromHierarchy();

            if (_editingBubble != null)
            {
                var bodies = _editingBubble.Query<VisualElement>(className: "transcript__body").ToList();
                for (int i = 0; i < bodies.Count; i++)
                    bodies[i].style.display = DisplayStyle.Flex;
            }
        }

        private static bool IsAssistantRole(string role)
        {
            if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
                return false;
            if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase))
                return false;
            if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }
    }
}
