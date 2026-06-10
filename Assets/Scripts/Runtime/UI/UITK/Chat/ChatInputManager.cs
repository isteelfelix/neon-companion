using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Chat;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Localization;
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
        private readonly Func<Task<ChatService>> _getChatServiceAsync;
        private readonly Action<string> _showSystemMessage;
        private readonly Func<Task> _openModelPickerAsync;
        private readonly Func<string, bool, Task> _applyModelSelectionAsync;
        private readonly Action<IReadOnlyList<ChatMessage>> _renderMessages;
        private readonly Action _clearMessageQueue;
        private readonly Func<Task<bool>> _startNewSessionAsync;

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

        public ChatInputManager(
            TextField messageInput,
            VisualElement composer,
            Func<bool> enterToSend,
            Func<Task<ChatService>> getChatServiceAsync,
            Action<string> showSystemMessage,
            Func<Task> openModelPickerAsync,
            Func<string, bool, Task> applyModelSelectionAsync,
            Action<IReadOnlyList<ChatMessage>> renderMessages,
            Action clearMessageQueue,
            Func<Task<bool>> startNewSessionAsync)
        {
            _messageInput = messageInput;
            _composer = composer;
            _enterToSend = enterToSend;
            _getChatServiceAsync = getChatServiceAsync;
            _showSystemMessage = showSystemMessage;
            _openModelPickerAsync = openModelPickerAsync;
            _applyModelSelectionAsync = applyModelSelectionAsync;
            _renderMessages = renderMessages;
            _clearMessageQueue = clearMessageQueue;
            _startNewSessionAsync = startNewSessionAsync;
        }

        public void SetVoiceRecording(bool value) { _isVoiceRecording = value; }

        public void Clear()
        {
            if (_messageInput != null)
                _messageInput.value = string.Empty;
            QueueComposerHeightUpdate();
        }


        public async Task<bool> TryHandleCommandAsync(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;

            string trimmed = message.Trim();
            if (!trimmed.StartsWith("/", StringComparison.Ordinal))
                return false;

            string command;
            string args = null;
            int spaceIdx = trimmed.IndexOf(' ');
            if (spaceIdx > 0)
            {
                command = trimmed.Substring(0, spaceIdx).ToLowerInvariant();
                args = trimmed.Substring(spaceIdx + 1).Trim();
            }
            else
            {
                command = trimmed.ToLowerInvariant();
            }

            switch (command)
            {
                case "/model":
                    return await HandleModelCommandAsync(args);
                case "/help":
                    return HandleHelpCommand();
                case "/clear":
                    return await HandleClearCommandAsync();
                case "/new":
                    return await HandleNewCommandAsync();
                case "/system":
                    return HandleSystemCommand(args);
                case "/temp":
                    return HandleTempCommand(args);
                case "/tokens":
                    return HandleTokensCommand(args);
                default:
                    _showSystemMessage?.Invoke($"Неизвестная команда: {command}. Введите /help для списка.");
                    return true;
            }
        }

        private async Task<bool> HandleModelCommandAsync(string args)
        {
            if (string.IsNullOrWhiteSpace(args))
            {
                if (_openModelPickerAsync != null)
                    await _openModelPickerAsync();
                return true;
            }

            if (_applyModelSelectionAsync != null)
                await _applyModelSelectionAsync(args, false);
            return true;
        }

        private bool HandleHelpCommand()
        {
            _showSystemMessage?.Invoke(
                "Доступные команды:\n" +
                "/model — выбрать модель\n" +
                "/model <id> — установить модель\n" +
                "/system <текст> — установить системный промпт\n" +
                "/system — очистить системный промпт\n" +
                "/temp <0-2> — установить температуру\n" +
                "/tokens <число> — установить макс. токенов\n" +
                "/clear — очистить историю чата\n" +
                "/new — новая сессия\n" +
                "/help — показать эту справку");
            return true;
        }

        private async Task<bool> HandleClearCommandAsync()
        {
            ChatService chat = _getChatServiceAsync != null ? await _getChatServiceAsync() : null;
            if (chat != null)
            {
                await chat.ClearCurrentSessionAsync();
                _renderMessages?.Invoke(chat.CurrentChatViewModel?.Messages);
                _clearMessageQueue?.Invoke();
                _showSystemMessage?.Invoke("История очищена.");
            }
            return true;
        }

        private async Task<bool> HandleNewCommandAsync()
        {
            bool started = _startNewSessionAsync != null && await _startNewSessionAsync();
            if (started)
                _showSystemMessage?.Invoke(LocalizationExtensions.Get("chat.new.started", "Новая сессия начата."));
            return true;
        }

        private bool HandleSystemCommand(string args)
        {
            _ = SetSystemPromptAsync(args);
            return true;
        }

        private async Task SetSystemPromptAsync(string args)
        {
            ChatService chat = _getChatServiceAsync != null ? await _getChatServiceAsync() : null;
            if (chat?.CurrentChatViewModel == null)
                return;

            if (string.IsNullOrWhiteSpace(args))
            {
                chat.CurrentChatViewModel.SystemPrompt = null;
                _showSystemMessage?.Invoke("Системный промпт очищен.");
            }
            else
            {
                chat.CurrentChatViewModel.SystemPrompt = args;
                _showSystemMessage?.Invoke($"Системный промпт установлен: {args}");
            }
        }

        private bool HandleTempCommand(string args)
        {
            if (string.IsNullOrWhiteSpace(args))
            {
                _showSystemMessage?.Invoke("Использование: /temp <0-2>");
                return true;
            }

            float temp;
            if (float.TryParse(args, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out temp))
            {
                if (temp < 0f || temp > 2f)
                {
                    _showSystemMessage?.Invoke("Температура должна быть от 0 до 2.");
                    return true;
                }
                _ = SetTempAsync(temp);
                return true;
            }

            _showSystemMessage?.Invoke("Неверное число. Использование: /temp <0-2>");
            return true;
        }

        private async Task SetTempAsync(float temp)
        {
            ChatService chat = _getChatServiceAsync != null ? await _getChatServiceAsync() : null;
            if (chat?.CurrentChatViewModel != null)
            {
                chat.CurrentChatViewModel.Temperature = temp;
                _showSystemMessage?.Invoke($"Температура: {temp}");
            }
        }

        private bool HandleTokensCommand(string args)
        {
            if (string.IsNullOrWhiteSpace(args))
            {
                _showSystemMessage?.Invoke("Использование: /tokens <число>");
                return true;
            }

            int tokens;
            if (int.TryParse(args, out tokens) && tokens > 0)
            {
                _ = SetTokensAsync(tokens);
                return true;
            }

            _showSystemMessage?.Invoke("Неверное число. Использование: /tokens <число>");
            return true;
        }

        private async Task SetTokensAsync(int tokens)
        {
            ChatService chat = _getChatServiceAsync != null ? await _getChatServiceAsync() : null;
            if (chat?.CurrentChatViewModel != null)
            {
                chat.CurrentChatViewModel.MaxTokens = tokens;
                _showSystemMessage?.Invoke($"Макс. токенов: {tokens}");
            }
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
            {
                QueueComposerScrollSync(false);
                return;
            }

            _composerInputHeight = target;
            field.style.height = target;
            textEl.style.minHeight = target;

            QueueComposerScrollSync(true);
        }

        private void QueueComposerScrollSync(bool followOverflow)
        {
            var scroll = _composerScroll;
            if (scroll == null)
                return;

            // Wait for the resized TextField and ScrollView viewport to finish layout. A stale
            // bottom offset must be cleared when the draft becomes short enough to fit, otherwise
            // the first line remains clipped even though no scrollbar is needed.
            scroll.schedule.Execute(() =>
            {
                if (scroll.panel == null)
                    return;

                float viewportHeight = scroll.contentViewport != null
                    ? scroll.contentViewport.resolvedStyle.height
                    : scroll.contentRect.height;
                float contentHeight = scroll.contentContainer != null
                    ? scroll.contentContainer.resolvedStyle.height
                    : _composerInputHeight;
                float maxScroll = Mathf.Max(0f, contentHeight - viewportHeight);

                if (maxScroll <= 0.5f)
                    scroll.scrollOffset = Vector2.zero;
                else if (followOverflow)
                    scroll.scrollOffset = new Vector2(0f, maxScroll);
            }).StartingIn(0);
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
