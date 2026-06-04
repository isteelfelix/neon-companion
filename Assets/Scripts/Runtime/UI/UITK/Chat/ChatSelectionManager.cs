using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Localization;
using NeonCompanion.Runtime.Models.Chat;
using NeonCompanion.Runtime.UI.UITK;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK.Chat
{
    internal sealed class ChatSelectionManager
    {
        private readonly ScrollView _messagesList;
        private readonly Action _dismissSessionPicker;
        private readonly Action _onSelectionModeChanged;
        private readonly ChatController.Deps _d;
        private readonly ChatMessageListRenderer _messageListRenderer;
        private readonly Func<List<ChatSession>, Task<ChatSession>> _showSessionPickerAsync;

        private bool _isSelectionMode;
        private readonly HashSet<int> _selectedMessages = new HashSet<int>();
        private VisualElement _selectionBar;
        private Label _selectionCountLabel;

        internal bool IsSelecting => _isSelectionMode;
        internal VisualElement SelectionBar => _selectionBar;

        internal IReadOnlyList<string> SelectedIds
        {
            get
            {
                var result = new List<string>(_selectedMessages.Count);
                foreach (int idx in _selectedMessages)
                    result.Add(idx.ToString());
                return result;
            }
        }

        internal bool IsIndexSelected(int index) => _selectedMessages.Contains(index);

        internal event Action<IReadOnlyList<string>> OnBulkDelete;
        internal event Action<IReadOnlyList<string>> OnBulkForward;

        internal ChatSelectionManager(
            ScrollView messagesList,
            VisualElement composer,
            Action dismissSessionPicker,
            Action onSelectionModeChanged,
            ChatController.Deps deps,
            ChatMessageListRenderer messageListRenderer,
            Func<List<ChatSession>, Task<ChatSession>> showSessionPickerAsync)
        {
            _messagesList = messagesList;
            _dismissSessionPicker = dismissSessionPicker;
            _onSelectionModeChanged = onSelectionModeChanged;
            _d = deps;
            _messageListRenderer = messageListRenderer;
            _showSessionPickerAsync = showSessionPickerAsync;
            BuildSelectionBar(composer);
        }

        internal void OnSelectionBulkDelete(IReadOnlyList<string> ids)
        {
            var chat = _d.GetChatServiceAsync().Result;
            if (chat == null || chat.CurrentChatViewModel == null)
                return;

            var indices = new List<int>(ids.Count);
            for (int i = 0; i < ids.Count; i++)
            {
                int idx;
                if (int.TryParse(ids[i], out idx))
                    indices.Add(idx);
            }
            indices.Sort((a, b) => b.CompareTo(a));

            for (int i = 0; i < indices.Count; i++)
            {
                int idx = indices[i];
                if (idx >= 0 && idx < chat.CurrentChatViewModel.Messages.Count)
                    chat.CurrentChatViewModel.Messages.RemoveAt(idx);
            }

            _ = chat.SaveCurrentSessionAsync();
            _messageListRenderer.Render(chat.CurrentChatViewModel.Messages);
        }

        internal void OnSelectionBulkForward(IReadOnlyList<string> ids)
        {
            _ = ForwardSelectedAsync(ids);
        }

        private async Task ForwardSelectedAsync(IReadOnlyList<string> ids)
        {
            if (ids == null || ids.Count == 0)
                return;

            var chat = await _d.GetChatServiceAsync();
            if (chat == null || chat.CurrentChatViewModel == null)
                return;

            var source = chat.CurrentChatViewModel.Messages;
            var toForward = new List<ChatMessage>();
            var indices = new List<int>(ids.Count);
            for (int i = 0; i < ids.Count; i++)
            {
                int idx;
                if (int.TryParse(ids[i], out idx))
                    indices.Add(idx);
            }
            indices.Sort();

            for (int i = 0; i < indices.Count; i++)
            {
                int idx = indices[i];
                if (idx >= 0 && idx < source.Count)
                    toForward.Add(source[idx]);
            }

            if (toForward.Count == 0)
            {
                ExitSelectionMode();
                return;
            }

            var all = await chat.GetAllSessionsAsync();
            string currentId = chat.CurrentSessionId;

            var candidates = new List<ChatSession>();
            for (int i = 0; i < all.Count; i++)
            {
                var session = all[i];
                if (session != null && session.sessionId != currentId)
                    candidates.Add(session);
            }

            if (candidates.Count == 0)
            {
                ExitSelectionMode();
                return;
            }

            var target = await _showSessionPickerAsync(candidates);
            if (target == null)
                return;

            int added = await chat.AppendMessagesToSessionAsync(target.sessionId, toForward);
            ExitSelectionMode();

            if (added > 0)
            {
                string done = LocalizationExtensions.Get("chat.selection.forward_done", "Forwarded {0} messages")
                    .Replace("{0}", added.ToString());
                _d.ShowSystemMessage(done);
            }
        }

        private void BuildSelectionBar(VisualElement composer)
        {
            _selectionBar = new VisualElement();
            _selectionBar.name = "selection-bar";
            _selectionBar.AddToClassList("selection-bar");
            _selectionBar.style.display = DisplayStyle.None;

            _selectionCountLabel = new Label();
            _selectionCountLabel.AddToClassList("selection-bar__count");
            _selectionBar.Add(_selectionCountLabel);

            var deleteBtn = new Button(OnDeleteSelected) { text = LocalizationExtensions.Get("chat.selection.delete", "Delete") };
            deleteBtn.AddToClassList("selection-bar__btn selection-bar__btn--danger");
            _selectionBar.Add(deleteBtn);

            var forwardBtn = new Button(OnForwardSelected) { text = LocalizationExtensions.Get("chat.selection.forward", "Forward") };
            forwardBtn.AddToClassList("selection-bar__btn");
            _selectionBar.Add(forwardBtn);

            var cancelBtn = new Button(ExitSelectionMode) { text = LocalizationExtensions.Get("chat.selection.cancel", "Cancel") };
            cancelBtn.AddToClassList("selection-bar__btn");
            _selectionBar.Add(cancelBtn);

            if (composer?.parent != null)
            {
                var parent = composer.parent;
                int composerIndex = parent.IndexOf(composer);
                parent.Insert(composerIndex, _selectionBar);
            }
        }

        internal void Teardown()
        {
            if (_selectionBar != null)
            {
                _selectionBar.RemoveFromHierarchy();
                _selectionBar = null;
                _selectionCountLabel = null;
            }
        }

        internal void EnterSelectionMode(int initialIndex)
        {
            _isSelectionMode = true;
            _selectedMessages.Clear();
            _selectedMessages.Add(initialIndex);
            RenderSelectionUI();
            _onSelectionModeChanged?.Invoke();
        }

        internal void ExitSelectionMode()
        {
            _dismissSessionPicker?.Invoke();
            _isSelectionMode = false;
            _selectedMessages.Clear();
            RenderSelectionUI();
            _onSelectionModeChanged?.Invoke();
        }

        internal void ToggleSelection(int index)
        {
            bool isSelected;
            if (_selectedMessages.Contains(index))
            {
                _selectedMessages.Remove(index);
                isSelected = false;
            }
            else
            {
                _selectedMessages.Add(index);
                isSelected = true;
            }

            UpdateSelectionRowState(index, isSelected);

            if (_selectedMessages.Count == 0)
                ExitSelectionMode();
            else
                RenderSelectionUI();
        }

        private void UpdateSelectionRowState(int index, bool isSelected)
        {
            if (_messagesList == null)
                return;

            foreach (var child in _messagesList.Children())
            {
                var row = child as VisualElement;
                if (row == null || !(row.userData is int))
                    continue;

                int rowIndex = (int)row.userData;
                if (rowIndex != index)
                    continue;

                if (isSelected)
                    row.AddToClassList("transcript__row--selected");
                else
                    row.RemoveFromClassList("transcript__row--selected");
                break;
            }
        }

        private void RenderSelectionUI()
        {
            if (_selectionBar == null) return;
            if (_isSelectionMode)
            {
                _selectionBar.style.display = DisplayStyle.Flex;
                _selectionCountLabel.text = LocalizationExtensions.Get("chat.selection.count", "Selected: {0}")
                    .Replace("{0}", _selectedMessages.Count.ToString());
            }
            else
            {
                _selectionBar.style.display = DisplayStyle.None;
            }
        }

        private void OnDeleteSelected()
        {
            if (_selectedMessages.Count == 0) return;
            var ids = SelectedIds;
            ExitSelectionMode();
            OnBulkDelete?.Invoke(ids);
        }

        private void OnForwardSelected()
        {
            if (_selectedMessages.Count == 0) return;
            var ids = SelectedIds;
            OnBulkForward?.Invoke(ids);
        }
    }
}
