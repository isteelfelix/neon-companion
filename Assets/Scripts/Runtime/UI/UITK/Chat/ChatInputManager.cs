using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK.Chat
{
    /// <summary>
    /// Manages composer text input — enter-to-send, height auto-grow, scroll wrapping,
    /// focus styling, and newline insertion. Extracted from ChatController.
    /// </summary>
    internal class ChatInputManager
    {
        private readonly TextField _messageInput;
        private readonly VisualElement _composer;
        private readonly Func<bool> _enterToSend;

        private TextElement _composerTextElement;
        private ScrollView _composerScroll;
        private float _composerInputHeight = -1f;
        private int _lastComposerEnterEventFrame = -1;
        private bool _isVoiceRecording;

        private const float ComposerInputMinHeight = 36f;
        private const float ComposerInputVerticalPadding = 12f;

        /// <summary>Fired when the user confirms send (Enter or Ctrl+Enter depending on setting).</summary>
        public event Action<string> OnSubmit;

        /// <summary>Current text in the composer field.</summary>
        public string CurrentText => _messageInput != null ? _messageInput.value ?? string.Empty : string.Empty;

        public ChatInputManager(TextField messageInput, VisualElement composer, Func<bool> enterToSend)
        {
            _messageInput = messageInput;
            _composer = composer;
            _enterToSend = enterToSend;
        }

        public void SetVoiceRecording(bool value) { _isVoiceRecording = value; }

        public void Clear()
        {
            if (_messageInput != null)
                _messageInput.value = string.Empty;
            QueueComposerHeightUpdate();
        }

        public void SetFocus()
        {
            if (_messageInput != null)
                _messageInput.Focus();
        }

        /// <summary>Queues a deferred height recalculation for the composer field.</summary>
        public void QueueComposerHeightUpdate()
        {
            var field = _messageInput;
            if (field == null)
                return;

            field.schedule.Execute(UpdateComposerHeight);
        }

        // ===== Callback registration =====

        public void RegisterCallbacks()
        {
            if (_messageInput == null)
                return;

            _messageInput.multiline = true;
            WrapComposerInScrollView();
            _messageInput.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            _messageInput.RegisterValueChangedCallback(OnTextChanged);
            _messageInput.RegisterCallback<FocusInEvent>(OnFocusIn);
            _messageInput.RegisterCallback<FocusOutEvent>(OnFocusOut);
            _messageInput.RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);

            QueueComposerHeightUpdate();
        }

        public void UnregisterCallbacks()
        {
            if (_messageInput == null)
                return;

            _messageInput.UnregisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            _messageInput.UnregisterValueChangedCallback(OnTextChanged);
            _messageInput.UnregisterCallback<FocusInEvent>(OnFocusIn);
            _messageInput.UnregisterCallback<FocusOutEvent>(OnFocusOut);
            _messageInput.UnregisterCallback<GeometryChangedEvent>(OnGeometryChanged);

            _lastComposerEnterEventFrame = -1;
            _composerTextElement = null;
            _composerInputHeight = -1f;
        }

        // ===== Enter key handling =====

        private void OnKeyDown(KeyDownEvent evt)
        {
            if (evt == null)
                return;

            bool isEnterKey = evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter;
            bool isEnterChar = evt.character == '\n' || evt.character == '\r';
            if (!isEnterKey && !isEnterChar)
                return;

            // Consume Enter: UITK's native multiline handling ignores Shift+Enter and doesn't reliably
            // commit its newline to value, so we own both send and newline. The per-frame guard collapses
            // the paired keyCode+character KeyDownEvents Unity dispatches for a single press.
            evt.StopImmediatePropagation();
            evt.StopPropagation();
#pragma warning disable CS0618
            evt.PreventDefault();
#pragma warning restore CS0618

            int frame = Time.frameCount;
            if (_lastComposerEnterEventFrame == frame)
                return;
            _lastComposerEnterEventFrame = frame;

            bool hasShift = evt.shiftKey;
            bool hasCtrl = evt.ctrlKey || evt.commandKey;
            bool enterToSend = _enterToSend != null && _enterToSend();

            // EnterToSend = true  → Enter sends, Shift+Enter = newline
            // EnterToSend = false → Ctrl+Enter sends, normal Enter = newline
            bool shouldSend = enterToSend ? (!hasShift && !hasCtrl) : hasCtrl;

            if (shouldSend)
            {
                OnSubmit?.Invoke(CurrentText);
                return;
            }

            QueueComposerNewLineInsert();
        }

        // ===== Composer focus =====

        private void OnFocusIn(FocusInEvent evt)
        {
            _composer?.AddToClassList("composer--focused");
        }

        private void OnFocusOut(FocusOutEvent evt)
        {
            _composer?.RemoveFromClassList("composer--focused");
        }

        private void OnGeometryChanged(GeometryChangedEvent evt)
        {
            QueueComposerHeightUpdate();
        }

        // ===== Text change =====

        private void OnTextChanged(ChangeEvent<string> evt)
        {
            QueueComposerHeightUpdate();
        }

        // ===== Composer scroll wrapping =====

        // UITK TextField has no built-in scrollbar, so wrap the input in a ScrollView. The ScrollView
        // owns the row layout + 140px cap (via .composer__scroll) and shows a vertical scrollbar when
        // the draft overflows; the TextField inside grows to its full content height.
        private void WrapComposerInScrollView()
        {
            var field = _messageInput;
            if (field == null)
                return;
            if (field.parent is ScrollView)
            {
                _composerScroll = (ScrollView)field.parent;
                return;
            }
            var parent = field.parent;
            if (parent == null)
                return;

            int index = parent.IndexOf(field);
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.name = "composer-scroll";
            scroll.AddToClassList("composer__scroll");
            scroll.verticalScrollerVisibility = ScrollerVisibility.Auto;
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;

            parent.Insert(index, scroll);
            scroll.Add(field);
            _composerScroll = scroll;
        }

        // ===== Composer height =====

        private void UpdateComposerHeight()
        {
            var field = _messageInput;
            if (field == null || field.panel == null)
                return;

            TextElement textEl = GetComposerTextElement(field);
            if (textEl == null)
                return;

            float width = textEl.contentRect.width;
            if (width <= 1f)
                width = field.contentRect.width;
            if (width <= 1f)
                return;

            string text = string.IsNullOrEmpty(field.value) ? " " : field.value;
            if (text.EndsWith("\n", StringComparison.Ordinal) || text.EndsWith("\r", StringComparison.Ordinal))
                text += " ";

            Vector2 size = textEl.MeasureTextSize(text, width, VisualElement.MeasureMode.Exactly, 0f, VisualElement.MeasureMode.Undefined);
            if (float.IsNaN(size.y) || size.y <= 0f)
                size.y = textEl.resolvedStyle.fontSize * 1.35f;

            // Grow to full content height (no upper clamp here); the wrapping ScrollView caps the
            // visible height at 140px and provides the scrollbar. Floor at the min height only.
            float target = Mathf.Max(size.y + ComposerInputVerticalPadding, ComposerInputMinHeight);
            if (_composerInputHeight > 0f && Mathf.Abs(_composerInputHeight - target) < 0.5f)
                return;

            _composerInputHeight = target;
            field.style.height = target;
            textEl.style.minHeight = target;

            // When the draft grows past the cap, keep the newest line visible (caret-follow), since a
            // ScrollView does not auto-track a TextField caret. Height only changes when lines are
            // added/removed, so this won't fight the user scrolling up to review same-height text.
            if (_composerScroll != null)
            {
                var scroll = _composerScroll;
                float bottom = target;
                scroll.schedule.Execute(() =>
                {
                    if (scroll.panel != null)
                        scroll.scrollOffset = new Vector2(0f, bottom);
                }).StartingIn(0);
            }
        }

        private TextElement GetComposerTextElement(TextField field)
        {
            if (_composerTextElement != null && _composerTextElement.panel != null)
                return _composerTextElement;

            TextElement textEl = field.Q<TextElement>(className: "unity-text-field__input");
            if (textEl == null)
                textEl = field.Q<TextElement>(className: "unity-base-text-field__input");
            if (textEl == null)
                textEl = field.Q<TextElement>();

            _composerTextElement = textEl;
            return _composerTextElement;
        }

        // ===== Newline insertion =====

        private void QueueComposerNewLineInsert()
        {
            var field = _messageInput;
            if (field == null)
                return;

            // Capture the caret NOW — before the user types the next character. Apply the mutation
            // deferred so UITK's editing engine has settled. Reading the caret at execution time
            // instead races with the next keystroke and appends the newline at the very end, which
            // OnSendClicked's Trim() then strips — collapsing a single Shift+Enter ("1\n2" → "12").
            int start = Math.Min(field.cursorIndex, field.selectIndex);
            int end = Math.Max(field.cursorIndex, field.selectIndex);

            field.schedule.Execute(() => InsertComposerNewLine(field, start, end));
        }

        private void InsertComposerNewLine(TextField field, int selectionStart, int selectionEnd)
        {
            if (field == null || field.panel == null)
                return;

            string current = field.value ?? string.Empty;
            int start = Mathf.Clamp(Math.Min(selectionStart, selectionEnd), 0, current.Length);
            int end = Mathf.Clamp(Math.Max(selectionStart, selectionEnd), 0, current.Length);

            string updated = current.Substring(0, start) + "\n" + current.Substring(end);
            field.value = updated;

            int caret = Mathf.Clamp(start + 1, 0, updated.Length);
            field.cursorIndex = caret;
            field.selectIndex = caret;
            field.Focus();

            QueueComposerHeightUpdate();
        }
    }
}
