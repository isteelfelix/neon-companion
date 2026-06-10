using System;
using System.Collections.Generic;
using NeonCompanion.Runtime.Data.Models;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.Avatars
{
    public sealed class AvatarCustomizationPanel
    {
        private static readonly string[] EmojiOptions =
        {
            "", "✨", "🔥", "💖", "💙", "🌟", "😊", "😎", "🤖", "💫", "⚡", "🎯"
        };

        private readonly VisualElement _root;
        private readonly Button _saveButton;
        private readonly Button _cancelButton;
        private readonly VisualElement _emojiGrid;

        private readonly Dictionary<string, Button> _emojiButtons = new Dictionary<string, Button>();

        private AvatarCustomizationData _data = new AvatarCustomizationData();
        private bool _isBinding;

        public event Action<AvatarCustomizationData> Changed;
        public event Action Saved;
        public event Action Canceled;
        public AvatarCustomizationData CurrentData => CloneOrDefault(_data);

        public AvatarCustomizationPanel(VisualElement root)
        {
            _root = root;
            _saveButton = root.Q<Button>("customize-save-btn");
            _cancelButton = root.Q<Button>("customize-cancel-btn");
            _emojiGrid = root.Q<VisualElement>("customize-emoji-grid");

            BuildEmojiButtons();
            RegisterCallbacks();
            Bind(null);
        }

        public void Bind(AvatarCustomizationData source)
        {
            _isBinding = true;
            _data = CloneOrDefault(source);
            HighlightEmoji(_data.OverlayEmoji);
            _isBinding = false;
        }

        private void RegisterCallbacks()
        {
            if (_saveButton != null) _saveButton.clicked += () => Saved?.Invoke();
            if (_cancelButton != null) _cancelButton.clicked += () => Canceled?.Invoke();
        }

        private void BuildEmojiButtons()
        {
            if (_emojiGrid == null) return;
            _emojiGrid.Clear();
            _emojiButtons.Clear();
            foreach (var emoji in EmojiOptions)
            {
                string value = emoji;
                string labelText = string.IsNullOrEmpty(value) ? "∅" : value;
                var button = new Button(() =>
                {
                    _data.OverlayEmoji = value;
                    HighlightEmoji(value);
                    EmitChanged();
                })
                {
                    text = labelText
                };
                button.AddToClassList("customize__emoji-btn");
                _emojiGrid.Add(button);
                _emojiButtons[value] = button;
            }
        }

        private void HighlightEmoji(string value)
        {
            foreach (var kvp in _emojiButtons)
                kvp.Value.EnableInClassList("is-active", kvp.Key == (value ?? string.Empty));
        }

        private void EmitChanged()
        {
            if (_isBinding) return;
            Changed?.Invoke(CloneOrDefault(_data));
        }

        private static AvatarCustomizationData CloneOrDefault(AvatarCustomizationData source)
        {
            var output = new AvatarCustomizationData();
            if (source == null)
                return output;

            output.OverlayEmoji = source.OverlayEmoji ?? string.Empty;
            return output;
        }
    }
}
