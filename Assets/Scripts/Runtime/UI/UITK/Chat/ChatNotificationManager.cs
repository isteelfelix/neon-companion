using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK.Chat
{
    /// <summary>
    /// Manages notification badge state and unread tracking.
    /// Extracted from ChatController.
    /// </summary>
    internal class ChatNotificationManager
    {
        private readonly Label _navChatCount;
        private bool _hasUnread;

        public ChatNotificationManager(Label navChatCount)
        {
            _navChatCount = navChatCount;
            Application.focusChanged += OnApplicationFocusChanged;
        }

        public bool HasUnread => _hasUnread;

        public void NotifyNewMessage()
        {
            _hasUnread = true;
            if (_navChatCount != null)
            {
                _navChatCount.AddToClassList("nav__count--notify");
            }
        }

        public void MarkRead()
        {
            _hasUnread = false;
            if (_navChatCount != null)
            {
                _navChatCount.RemoveFromClassList("nav__count--notify");
            }
        }

        public void Dispose()
        {
            Application.focusChanged -= OnApplicationFocusChanged;
        }

        private void OnApplicationFocusChanged(bool hasFocus)
        {
            if (hasFocus && _hasUnread)
            {
                _hasUnread = false;
                if (_navChatCount != null)
                {
                    _navChatCount.RemoveFromClassList("nav__count--notify");
                }
            }
        }
    }
}
