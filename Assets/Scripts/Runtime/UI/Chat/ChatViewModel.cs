using System;
using System.Collections.Generic;
using NeonCompanion.Runtime.Api.Models;
using NeonCompanion.Runtime.Data.Models;

namespace NeonCompanion.Runtime.UI.Chat
{
    public sealed class ChatViewModel
    {
        public string InputMessage { get; set; }
        public List<ChatMessage> Messages { get; } = new List<ChatMessage>();

        public void AddUserMessage(string content)
        {
            Messages.Add(new ChatMessage
            {
                role = "user",
                content = content,
                unixTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
        }

        public void AddAssistantMessage(AiChatResponse response)
        {
            Messages.Add(new ChatMessage
            {
                role = "assistant",
                content = response != null ? response.content : string.Empty,
                unixTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
        }
    }
}
