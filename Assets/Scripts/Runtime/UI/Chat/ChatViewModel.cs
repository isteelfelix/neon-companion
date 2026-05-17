using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Api;
using NeonCompanion.Runtime.Api.Models;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Core;

namespace NeonCompanion.Runtime.UI.Chat
{
    public sealed class ChatViewModel
    {
        private readonly IAiClient _aiClient;
        private readonly ProviderConfig _provider;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        public string InputMessage { get; set; }
        public List<ChatMessage> Messages { get; } = new List<ChatMessage>();
        public bool IsSending { get; private set; }

        public float Temperature { get; set; } = 0.7f;
        public int MaxTokens { get; set; } = 512;
        public string SystemPrompt { get; set; }

        public ChatViewModel(IAiClient aiClient, ProviderConfig provider)
        {
            _aiClient = aiClient ?? throw new ArgumentNullException(nameof(aiClient));
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        }

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
                content = response?.content ?? string.Empty,
                unixTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
        }

        public async Task SendAsync()
        {
            if (IsSending || string.IsNullOrWhiteSpace(InputMessage))
                return;

            var userMessage = InputMessage.Trim();
            InputMessage = string.Empty;

            AddUserMessage(userMessage);

            IsSending = true;

            try
            {
                var request = new AiChatRequest
                {
                    model = _provider.defaultModel,
                    temperature = Temperature,
                    maxTokens = MaxTokens,
                    systemPrompt = SystemPrompt,
                    messages = new List<AiChatMessage>
                    {
                        new AiChatMessage { role = "user", content = userMessage }
                    }
                };

                var response = await _aiClient.SendMessageAsync(_provider, request, _cts.Token);
                AddAssistantMessage(response);
            }
            catch (Exception ex)
            {
                AddAssistantMessage(new AiChatResponse
                {
                    content = $"[Error] {ex.Message}"
                });
            }
            finally
            {
                IsSending = false;
            }
        }
    }
}