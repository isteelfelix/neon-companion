using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NeonCompanion.Runtime.Api.Hermes;
using NeonCompanion.Runtime.Chat;
using NeonCompanion.Runtime.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK.Chat
{
    /// <summary>
    /// Live slash-command and @path suggestions for the composer, backed by the Hermes gateway's
    /// commands.catalog / complete.slash / complete.path RPCs — the same three the Desktop
    /// composer uses.
    ///
    /// Two rules drive everything here:
    ///  * A response is only rendered while it still describes the current draft. Text edits,
    ///    session switches, profile switches and gateway replacement each invalidate whatever is
    ///    in flight, because the answer was computed against the old workspace/draft.
    ///  * Completion is an affordance, never an error path. A gateway that predates these methods
    ///    (JSON-RPC -32601), a timeout, or a closed socket all degrade to "no suggestions" —
    ///    nothing is ever pushed into the transcript.
    /// </summary>
    internal class ChatComposerCompletionController
    {
        private readonly TextField _messageInput;
        private readonly VisualElement _composer;
        private readonly Func<Task<ChatService>> _getChatServiceAsync;

        private VisualElement _popover;
        private ScrollView _list;
        private readonly List<ComposerCompletionItem> _items = new List<ComposerCompletionItem>();
        private readonly List<VisualElement> _rows = new List<VisualElement>();
        private ComposerCompletionTrigger _trigger = new ComposerCompletionTrigger();
        private int _activeIndex = -1;

        private IVisualElementScheduledItem _debounce;
        private int _requestToken;
        private int _lastAcceptFrame = -1;
        private bool _registered;

        // Capability memo scoped to the gateway instance that answered: a reconnect or a backend
        // switch builds a new HermesGateway, and that fresh backend gets probed again instead of
        // inheriting "unsupported" from the old one.
        private HermesGateway _capabilityGateway;
        private bool _catalogUnsupported;
        private bool _slashUnsupported;
        private bool _pathUnsupported;

        /// <summary>Keystroke coalescing before hitting the gateway (Desktop debounces at 60ms).</summary>
        private const int DebounceMs = 120;

        /// <summary>
        /// Completion RPCs run on the gateway's worker pool, and a suggestion the user has already
        /// typed past is worthless — so these get a much tighter bound than the 30s default ack
        /// timeout. A late answer would be dropped by the staleness check anyway.
        /// </summary>
        private const int RequestTimeoutMs = 8000;

        public ChatComposerCompletionController(
            TextField messageInput,
            VisualElement composer,
            Func<Task<ChatService>> getChatServiceAsync)
        {
            _messageInput = messageInput;
            _composer = composer;
            _getChatServiceAsync = getChatServiceAsync;
        }

        /// <summary>True while the popover is showing rows (Enter/Tab/arrows belong to it).</summary>
        public bool IsOpen
        {
            get { return _items.Count > 0; }
        }

        // ===== Callback registration =====

        /// <remarks>
        /// Must be registered BEFORE <see cref="ChatInputManager"/> registers its own trickle-down
        /// KeyDownEvent handler: same element, same phase means registration order decides who
        /// runs first, and the popover has to be able to claim Enter (accept the completion)
        /// before the composer treats it as send.
        /// </remarks>
        public void RegisterCallbacks()
        {
            if (_messageInput == null || _registered)
                return;

            _messageInput.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            _messageInput.RegisterValueChangedCallback(OnTextChanged);

            var backend = GlobalBackendSelector.Instance;
            if (backend != null)
                backend.OnHermesProfileChanged += OnHermesProfileChanged;

            _registered = true;
        }

        public void UnregisterCallbacks()
        {
            if (_messageInput == null || !_registered)
                return;

            _messageInput.UnregisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            _messageInput.UnregisterValueChangedCallback(OnTextChanged);

            var backend = GlobalBackendSelector.Instance;
            if (backend != null)
                backend.OnHermesProfileChanged -= OnHermesProfileChanged;

            Invalidate();

            if (_debounce != null)
            {
                _debounce.Pause();
                _debounce = null;
            }

            if (_popover != null)
            {
                _popover.RemoveFromHierarchy();
                _popover = null;
                _list = null;
            }

            _registered = false;
        }

        // ===== Query lifecycle =====

        private void OnTextChanged(ChangeEvent<string> evt)
        {
            ScheduleQuery();
        }

        private void OnHermesProfileChanged(string profile)
        {
            // The new profile has its own workspace and its own command set: everything on screen
            // and everything in flight describes the profile the user just left.
            Invalidate();
        }

        /// <summary>Drop the popover and disown every in-flight response.</summary>
        private void Invalidate()
        {
            _requestToken++;
            Hide();
        }

        private void ScheduleQuery()
        {
            if (_messageInput == null)
                return;

            // Bumping the token here is what makes "ignore stale responses after a text change"
            // hold: any reply from before this keystroke no longer matches.
            _requestToken++;

            if (_debounce == null)
            {
                _debounce = _messageInput.schedule.Execute(RunQuery);
                _debounce.Pause();
            }

            _debounce.ExecuteLater(DebounceMs);
        }

        private void RunQuery()
        {
            if (_messageInput == null || _messageInput.panel == null)
                return;

            string text = _messageInput.value ?? string.Empty;
            ComposerCompletionTrigger trigger = ComposerCompletion.Detect(text, _messageInput.cursorIndex);
            if (trigger.Kind == ComposerCompletionKind.None)
            {
                Hide();
                return;
            }

            _ = FetchAsync(trigger, _requestToken);
        }

        private async Task FetchAsync(ComposerCompletionTrigger trigger, int token)
        {
            GlobalBackendSelector backend = GlobalBackendSelector.Instance;
            HermesGateway gateway = backend != null ? backend.Gateway : null;
            if (gateway == null || gateway.State != ConnectionState.Open)
            {
                Hide();
                return;
            }

            if (!ReferenceEquals(_capabilityGateway, gateway))
            {
                _capabilityGateway = gateway;
                _catalogUnsupported = false;
                _slashUnsupported = false;
                _pathUnsupported = false;
            }

            ChatService chat = await ResolveChatServiceAsync();
            if (token != _requestToken)
                return;

            string profile = backend.ActiveHermesProfile;
            string sessionId = SessionIdOf(chat);

            List<ComposerCompletionItem> items = await RequestItemsAsync(gateway, trigger, sessionId);

            // Staleness gate. Any of these means the answer describes a draft, a workspace or a
            // connection that is no longer the one on screen.
            if (token != _requestToken)
                return;
            if (!ReferenceEquals(gateway, backend.Gateway))
                return;
            if (!string.Equals(profile, backend.ActiveHermesProfile, StringComparison.Ordinal))
                return;
            if (!string.Equals(sessionId, SessionIdOf(chat), StringComparison.Ordinal))
                return;

            Show(items, trigger);
        }

        private async Task<ChatService> ResolveChatServiceAsync()
        {
            if (_getChatServiceAsync == null)
                return null;

            try
            {
                return await _getChatServiceAsync();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The gateway-side session id. complete.path resolves its cwd from it (_completion_cwd),
        /// which is also why an unknown id is harmless — the backend falls back to the profile's
        /// configured workspace.
        /// </summary>
        private static string SessionIdOf(ChatService chat)
        {
            if (chat == null)
                return null;

            string sid = chat.CurrentChatViewModel != null ? chat.CurrentChatViewModel.ProviderSessionId : null;
            if (string.IsNullOrEmpty(sid))
                sid = chat.CurrentSessionId;
            return sid;
        }

        // ===== Gateway RPCs =====

        private async Task<List<ComposerCompletionItem>> RequestItemsAsync(
            HermesGateway gateway, ComposerCompletionTrigger trigger, string sessionId)
        {
            if (trigger.Kind == ComposerCompletionKind.Path)
                return await RequestPathItemsAsync(gateway, trigger, sessionId);

            return await RequestSlashItemsAsync(gateway, trigger);
        }

        private async Task<List<ComposerCompletionItem>> RequestPathItemsAsync(
            HermesGateway gateway, ComposerCompletionTrigger trigger, string sessionId)
        {
            if (_pathUnsupported)
                return null;

            var payload = new JObject();
            payload["word"] = trigger.Text;
            if (!string.IsNullOrEmpty(sessionId))
                payload["session_id"] = sessionId;
            // cwd is deliberately not sent: the companion has no authoritative workspace path of
            // its own, and the gateway derives a better one from session_id / the profile config.

            try
            {
                JToken result = await gateway.Request<JToken>(RpcMethods.CompletePath, payload, RequestTimeoutMs);
                return ComposerCompletion.ParsePathItems(result);
            }
            catch (Exception ex)
            {
                if (HermesGateway.IsMissingRpcMethod(ex))
                    _pathUnsupported = true;
                return null;
            }
        }

        private async Task<List<ComposerCompletionItem>> RequestSlashItemsAsync(
            HermesGateway gateway, ComposerCompletionTrigger trigger)
        {
            // A bare "/" gets the registry catalog: it is categorized, so the popover can show
            // section headers that the per-keystroke completer cannot produce.
            if (string.Equals(trigger.Text, "/", StringComparison.Ordinal) && !_catalogUnsupported)
            {
                try
                {
                    JToken catalog = await gateway.Request<JToken>(RpcMethods.CommandsCatalog, null, RequestTimeoutMs);
                    return ComposerCompletion.ParseCatalogItems(catalog);
                }
                catch (Exception ex)
                {
                    // Only a genuinely missing catalog falls through to complete.slash; a timeout
                    // or a transport failure must not immediately fire a second request.
                    if (!HermesGateway.IsMissingRpcMethod(ex))
                        return null;
                    _catalogUnsupported = true;
                }
            }

            if (_slashUnsupported)
                return null;

            var payload = new JObject();
            payload["text"] = trigger.Text;

            try
            {
                JToken result = await gateway.Request<JToken>(RpcMethods.CompleteSlash, payload, RequestTimeoutMs);
                return ComposerCompletion.ParseSlashItems(result, trigger.Text);
            }
            catch (Exception ex)
            {
                if (HermesGateway.IsMissingRpcMethod(ex))
                    _slashUnsupported = true;
                return null;
            }
        }

        // ===== Popover =====

        private void Show(List<ComposerCompletionItem> items, ComposerCompletionTrigger trigger)
        {
            if (items == null || items.Count == 0 || _composer == null)
            {
                Hide();
                return;
            }

            EnsurePopover();
            if (_list == null)
                return;

            _items.Clear();
            _rows.Clear();
            _list.Clear();
            _trigger = trigger;

            string lastGroup = null;
            for (int i = 0; i < items.Count; i++)
            {
                ComposerCompletionItem item = items[i];
                int index = _items.Count;

                if (!string.IsNullOrEmpty(item.Group) && !string.Equals(item.Group, lastGroup, StringComparison.Ordinal))
                {
                    lastGroup = item.Group;
                    var header = new Label(item.Group);
                    header.AddToClassList("composer__suggestion-group");
                    _list.Add(header);
                }

                VisualElement row = BuildRow(item);
                row.RegisterCallback<PointerDownEvent>(evt =>
                {
                    evt.StopPropagation();
                    Accept(index);
                });

                _list.Add(row);
                _items.Add(item);
                _rows.Add(row);
            }

            _activeIndex = 0;
            UpdateActiveRow();
            _popover.style.display = DisplayStyle.Flex;
        }

        private void Hide()
        {
            _items.Clear();
            _rows.Clear();
            _activeIndex = -1;
            _trigger = new ComposerCompletionTrigger();

            if (_list != null)
                _list.Clear();
            if (_popover != null)
                _popover.style.display = DisplayStyle.None;
        }

        private void EnsurePopover()
        {
            if (_popover != null || _composer == null)
                return;

            // Built in C# and inserted as the composer's first child: USS has no z-index, so an
            // in-flow row above the input is the reliable way to stack the popover, and living
            // inside the composer keeps it attached across responsive re-layouts.
            _popover = new VisualElement();
            _popover.name = "composer-suggestions";
            _popover.AddToClassList("composer__suggestions");
            _popover.style.display = DisplayStyle.None;

            _list = new ScrollView(ScrollViewMode.Vertical);
            _list.name = "composer-suggestions-list";
            _list.AddToClassList("composer__suggestions-list");
            _list.verticalScrollerVisibility = ScrollerVisibility.Auto;
            _list.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            _popover.Add(_list);

            _composer.Insert(0, _popover);
        }

        private static VisualElement BuildRow(ComposerCompletionItem item)
        {
            var row = new VisualElement();
            row.AddToClassList("composer__suggestion");

            var label = new Label(item.Display);
            label.AddToClassList("composer__suggestion-label");
            row.Add(label);

            if (!string.IsNullOrEmpty(item.Meta))
            {
                var meta = new Label(item.Meta);
                meta.AddToClassList("composer__suggestion-meta");
                row.Add(meta);
            }

            return row;
        }

        private void UpdateActiveRow()
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                if (i == _activeIndex)
                    _rows[i].AddToClassList("composer__suggestion--active");
                else
                    _rows[i].RemoveFromClassList("composer__suggestion--active");
            }

            if (_list != null && _activeIndex >= 0 && _activeIndex < _rows.Count)
                _list.ScrollTo(_rows[_activeIndex]);
        }

        private void Move(int delta)
        {
            if (_rows.Count == 0)
                return;

            _activeIndex = (_activeIndex + delta + _rows.Count) % _rows.Count;
            UpdateActiveRow();
        }

        // ===== Keyboard =====

        private void OnKeyDown(KeyDownEvent evt)
        {
            if (evt == null)
                return;

            bool isEnter = evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter
                || evt.character == '\n' || evt.character == '\r';
            bool isTab = evt.keyCode == KeyCode.Tab || evt.character == '\t';

            // Unity dispatches a keyCode event AND a character event for one press. Once the
            // popover has consumed the accept, swallow its twin for the rest of the frame —
            // otherwise the composer would see the second one as "send".
            if ((isEnter || isTab) && _lastAcceptFrame == Time.frameCount)
            {
                Consume(evt);
                return;
            }

            if (!IsOpen)
                return;

            if (evt.keyCode == KeyCode.DownArrow)
            {
                Move(1);
            }
            else if (evt.keyCode == KeyCode.UpArrow)
            {
                Move(-1);
            }
            else if (evt.keyCode == KeyCode.Escape)
            {
                Hide();
            }
            else if (isEnter || isTab)
            {
                _lastAcceptFrame = Time.frameCount;
                Accept(_activeIndex);
            }
            else
            {
                return;
            }

            Consume(evt);
        }

        private static void Consume(KeyDownEvent evt)
        {
            evt.StopImmediatePropagation();
            evt.StopPropagation();
#pragma warning disable CS0618
            evt.PreventDefault();
#pragma warning restore CS0618
        }

        // ===== Insertion =====

        private void Accept(int index)
        {
            if (index < 0 || index >= _items.Count || _messageInput == null)
            {
                Hide();
                return;
            }

            ComposerCompletionItem item = _items[index];
            ComposerCompletionTrigger trigger = _trigger;
            Hide();

            TextField field = _messageInput;
            // Deferred like ChatInputManager's newline insert: apply once UITK's editing engine
            // has settled, so the edit is not raced by the keystroke that triggered it.
            field.schedule.Execute(() => ApplyCompletion(field, trigger, item));
        }

        private static void ApplyCompletion(TextField field, ComposerCompletionTrigger trigger, ComposerCompletionItem item)
        {
            if (field == null || field.panel == null || trigger == null || item == null)
                return;

            string current = field.value ?? string.Empty;
            int start = trigger.Start;
            int end = start + (trigger.Text != null ? trigger.Text.Length : 0);

            // The trigger text must still sit where it was when the suggestion was requested;
            // if the draft moved on, inserting here would corrupt it.
            if (start < 0 || end > current.Length)
                return;
            if (!string.Equals(current.Substring(start, end - start), trigger.Text, StringComparison.Ordinal))
                return;

            int caret;
            string updated = ComposerCompletion.ApplyItem(current, start, end, item.InsertText, out caret);
            if (string.Equals(updated, current, StringComparison.Ordinal))
                return;

            field.value = updated;
            caret = Mathf.Clamp(caret, 0, updated.Length);
            field.cursorIndex = caret;
            field.selectIndex = caret;
            field.Focus();
        }
    }
}
