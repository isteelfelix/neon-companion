using System;
using System.Collections.Generic;
using NeonCompanion.Runtime.Chat;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Localization;
using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK.Chat
{
    /// <summary>
    /// In-session search bar and match navigation. Extracted from ChatController (U-38).
    /// </summary>
    internal class ChatSearchController
    {
        private readonly ScrollView _messagesList;
        private readonly Func<System.Threading.Tasks.Task<ChatService>> _getChatServiceAsync;

        private string _sessionSearchQuery = string.Empty;
        private string _searchQuery = string.Empty;
        private int _currentMatchIndex = -1;
        private readonly List<int> _matchingMessageIndices = new List<int>();

        private VisualElement _searchBar;
        private TextField _searchInput;
        private Label _searchCountLabel;
        private Button _searchUpBtn;
        private Button _searchDownBtn;
        private Button _searchCloseBtn;

        public ChatSearchController(
            ScrollView messagesList,
            Func<System.Threading.Tasks.Task<ChatService>> getChatServiceAsync)
        {
            _messagesList = messagesList;
            _getChatServiceAsync = getChatServiceAsync;
        }

        /// <summary>Stored session search query (persisted between shows).</summary>
        public string SessionSearchQuery => _sessionSearchQuery;

        /// <summary>Whether the search bar is currently visible.</summary>
        public bool IsVisible => _searchBar != null && _searchBar.style.display != DisplayStyle.None;

        /// <summary>Sets the session search query (pass-through from ChatController).</summary>
        public void SetSessionSearchQuery(string value) { _sessionSearchQuery = value ?? string.Empty; }

        /// <summary>Programmatically set the search query and trigger search.</summary>
        public void SetQuery(string query) { _searchQuery = query ?? string.Empty; }

        /// <summary>Shows the search bar and focuses the input.</summary>
        public void Show()
        {
            EnsureSearchBarCreated();
            if (_searchBar == null)
                return;

            var parent = _messagesList != null ? _messagesList.parent as VisualElement : null;
            if (parent != null && _searchBar.parent != parent)
            {
                parent.Insert(0, _searchBar);
            }

            _searchBar.style.display = DisplayStyle.Flex;

            if (_searchInput != null)
            {
                _searchInput.value = _searchQuery ?? string.Empty;
                _searchInput.Focus();
                _searchInput.schedule.Execute(() =>
                {
                    if (_searchInput != null && _searchInput.panel != null)
                        _searchInput.SelectAll();
                }).StartingIn(60);
            }

            if (!string.IsNullOrEmpty(_searchQuery))
            {
                FindMatches();
                HighlightMatches();
                ScrollToCurrentMatch();
            }
            else
            {
                UpdateSearchCountLabel();
            }
        }

        /// <summary>Hides the search bar and clears highlights.</summary>
        public void Hide() { CloseSearch(); }

        /// <summary>
        /// Removes search bar from hierarchy and releases references.
        /// Call when the owning ChatController is unregistering callbacks.
        /// </summary>
        public void Dispose()
        {
            CloseSearch();
            if (_searchBar != null)
            {
                _searchBar.RemoveFromHierarchy();
                _searchBar = null;
                _searchInput = null;
                _searchCountLabel = null;
                _searchUpBtn = null;
                _searchDownBtn = null;
                _searchCloseBtn = null;
            }
        }

        // ── Private implementation ──

        private void EnsureSearchBarCreated()
        {
            if (_searchBar != null)
                return;

            _searchBar = new VisualElement();
            _searchBar.AddToClassList("search-bar");
            _searchBar.style.display = DisplayStyle.None;

            var icon = new VisualElement();
            icon.AddToClassList("icon");
            icon.AddToClassList("icon--search");
            icon.style.width = 14;
            icon.style.height = 14;
            icon.style.marginLeft = 4;
            icon.style.marginRight = 4;
            icon.style.flexShrink = 0;
            _searchBar.Add(icon);

            _searchInput = new TextField();
            _searchInput.AddToClassList("search-bar__input");
            _searchInput.RegisterValueChangedCallback(evt => OnSearchQueryChanged(evt.newValue));
            _searchInput.RegisterCallback<KeyDownEvent>(OnSearchInputKeyDown, TrickleDown.TrickleDown);
            _searchBar.Add(_searchInput);

            _searchCountLabel = new Label("0/0");
            _searchCountLabel.AddToClassList("search-bar__count");
            _searchBar.Add(_searchCountLabel);

            _searchUpBtn = new Button(GoToPrevMatch);
            _searchUpBtn.text = "\u2191";
            _searchUpBtn.AddToClassList("iconbtn");
            _searchUpBtn.style.width = 22;
            _searchUpBtn.style.height = 22;
            _searchUpBtn.style.fontSize = 11;
            _searchUpBtn.tooltip = LocalizationExtensions.Get("chat.search.previous", "Previous match");
            _searchBar.Add(_searchUpBtn);

            _searchDownBtn = new Button(GoToNextMatch);
            _searchDownBtn.text = "\u2193";
            _searchDownBtn.AddToClassList("iconbtn");
            _searchDownBtn.style.width = 22;
            _searchDownBtn.style.height = 22;
            _searchDownBtn.style.fontSize = 11;
            _searchDownBtn.tooltip = LocalizationExtensions.Get("chat.search.next", "Next match");
            _searchBar.Add(_searchDownBtn);

            _searchCloseBtn = new Button(CloseSearch);
            _searchCloseBtn.text = "\u2715";
            _searchCloseBtn.AddToClassList("iconbtn");
            _searchCloseBtn.style.width = 22;
            _searchCloseBtn.style.height = 22;
            _searchCloseBtn.style.fontSize = 11;
            _searchCloseBtn.tooltip = LocalizationExtensions.Get("chat.search.close", "Close search");
            _searchBar.Add(_searchCloseBtn);
        }

        private void OnSearchInputKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode == KeyCode.Escape)
            {
                CloseSearch();
                evt.StopPropagation();
            }
            else if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
            {
                GoToNextMatch();
                evt.StopPropagation();
            }
        }

        private void CloseSearch()
        {
            _searchQuery = string.Empty;
            _currentMatchIndex = -1;
            _matchingMessageIndices.Clear();

            if (_searchBar != null)
            {
                _searchBar.style.display = DisplayStyle.None;
            }
            if (_searchInput != null)
            {
                _searchInput.value = string.Empty;
            }

            ClearSearchHighlights();
            UpdateSearchCountLabel();
        }

        private void ClearSearchHighlights()
        {
            if (_messagesList == null)
                return;

            foreach (var child in _messagesList.Children())
            {
                var ve = child as VisualElement;
                if (ve != null)
                {
                    ve.RemoveFromClassList("transcript__row--search-match");
                    ve.RemoveFromClassList("transcript__row--search-current");
                }
            }
        }

        private void OnSearchQueryChanged(string query)
        {
            _searchQuery = query ?? string.Empty;
            FindMatches();
            HighlightMatches();
            ScrollToCurrentMatch();
        }

        private void FindMatches()
        {
            _matchingMessageIndices.Clear();

            if (string.IsNullOrEmpty(_searchQuery))
            {
                _currentMatchIndex = -1;
                return;
            }

            var messages = GetCurrentMessages();
            if (messages == null)
            {
                _currentMatchIndex = -1;
                return;
            }

            for (int i = 0; i < messages.Count; i++)
            {
                var m = messages[i];
                if (m != null &&
                    !string.IsNullOrEmpty(m.content) &&
                    m.content.IndexOf(_searchQuery, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _matchingMessageIndices.Add(i);
                }
            }

            _currentMatchIndex = _matchingMessageIndices.Count > 0 ? 0 : -1;
        }

        private IReadOnlyList<ChatMessage> GetCurrentMessages()
        {
            try
            {
                var chatTask = _getChatServiceAsync();
                if (chatTask == null)
                    return null;
                // Safe .Result pattern used elsewhere in ChatController for UI sync paths
                var chat = chatTask.Result;
                return chat != null ? chat.CurrentChatViewModel?.Messages : null;
            }
            catch
            {
                return null;
            }
        }

        private void HighlightMatches()
        {
            if (_messagesList == null)
                return;

            ClearSearchHighlights();

            if (string.IsNullOrEmpty(_searchQuery) || _matchingMessageIndices.Count == 0)
            {
                UpdateSearchCountLabel();
                return;
            }

            int currentMsgIdx = -1;
            if (_currentMatchIndex >= 0 && _currentMatchIndex < _matchingMessageIndices.Count)
            {
                currentMsgIdx = _matchingMessageIndices[_currentMatchIndex];
            }

            foreach (var child in _messagesList.Children())
            {
                var ve = child as VisualElement;
                if (ve != null && ve.userData is int msgIdx)
                {
                    bool isMatch = false;
                    for (int k = 0; k < _matchingMessageIndices.Count; k++)
                    {
                        if (_matchingMessageIndices[k] == msgIdx)
                        {
                            isMatch = true;
                            break;
                        }
                    }
                    if (isMatch)
                    {
                        ve.AddToClassList("transcript__row--search-match");
                        if (msgIdx == currentMsgIdx)
                        {
                            ve.AddToClassList("transcript__row--search-current");
                        }
                    }
                }
            }

            UpdateSearchCountLabel();
        }

        private void UpdateSearchCountLabel()
        {
            if (_searchCountLabel == null)
                return;

            int total = _matchingMessageIndices != null ? _matchingMessageIndices.Count : 0;
            int cur = (total > 0 && _currentMatchIndex >= 0) ? (_currentMatchIndex + 1) : 0;
            _searchCountLabel.text = total > 0 ? (cur + "/" + total) : "0/0";
        }

        private void GoToNextMatch()
        {
            if (_matchingMessageIndices == null || _matchingMessageIndices.Count == 0)
                return;

            int count = _matchingMessageIndices.Count;
            if (_currentMatchIndex < 0)
                _currentMatchIndex = 0;
            else
                _currentMatchIndex = (_currentMatchIndex + 1) % count;

            HighlightMatches();
            ScrollToCurrentMatch();
        }

        private void GoToPrevMatch()
        {
            if (_matchingMessageIndices == null || _matchingMessageIndices.Count == 0)
                return;

            int count = _matchingMessageIndices.Count;
            if (_currentMatchIndex < 0)
                _currentMatchIndex = count - 1;
            else
                _currentMatchIndex = (_currentMatchIndex - 1 + count) % count;

            HighlightMatches();
            ScrollToCurrentMatch();
        }

        private void ScrollToCurrentMatch()
        {
            if (_messagesList == null || _currentMatchIndex < 0 || _matchingMessageIndices == null || _currentMatchIndex >= _matchingMessageIndices.Count)
                return;

            int targetMsgIdx = _matchingMessageIndices[_currentMatchIndex];
            VisualElement targetRow = null;

            foreach (var child in _messagesList.Children())
            {
                if (child.userData is int idx && idx == targetMsgIdx)
                {
                    targetRow = child;
                    break;
                }
            }

            if (targetRow == null)
                return;

            try
            {
                _messagesList.ScrollTo(targetRow);
            }
            catch
            {
                // Fallback for older UITK: approximate offset
                var content = _messagesList.contentContainer;
                if (content != null)
                {
                    float y = targetRow.layout.y - 60f;
                    if (y < 0f) y = 0f;
                    _messagesList.scrollOffset = new Vector2(0f, y);
                }
            }
        }
    }
}
