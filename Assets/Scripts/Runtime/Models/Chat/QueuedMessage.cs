using System.Collections.Generic;
using NeonCompanion.Runtime.Data.Models;

namespace NeonCompanion.Runtime.Models.Chat
{
    /// <summary>
    /// DTO for messages queued while another message is being sent.
    /// Extracted from ChatController inner class.
    /// </summary>
    internal class QueuedMessage
    {
        public string Message;
        public List<ChatAttachment> Attachments;
    }
}
