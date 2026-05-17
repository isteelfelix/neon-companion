using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Api;
using NeonCompanion.Runtime.Api.Models;
using NeonCompanion.Runtime.Data.Models;

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
        public bool UseStreaming { get; set; }

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

        public async Task RegenerateAsync(Action<string> onStreamToken = null)
        {
            if (IsSending) return;

            // Build request from the existing conversation (no new user message added)
            IsSending = true;
            try
            {
                await SendRequestAsync(onStreamToken);
            }
            finally
            {
                IsSending = false;
            }
        }

        public async Task SendAsync(Action<string> onStreamToken = null)
        {
            if (IsSending || string.IsNullOrWhiteSpace(InputMessage))
                return;

            var userMessage = InputMessage.Trim();
            InputMessage = string.Empty;
            AddUserMessage(userMessage);

            IsSending = true;
            try
            {
                await SendRequestAsync(onStreamToken);
            }
            finally
            {
                IsSending = false;
            }
        }

        private async Task SendRequestAsync(Action<string> onStreamToken)
        {
            try
            {
                var requestMessages = new List<AiChatMessage>();
                foreach (var message in Messages)
                {
                    if (string.IsNullOrWhiteSpace(message?.role) || string.IsNullOrWhiteSpace(message.content))
                        continue;

                    requestMessages.Add(new AiChatMessage { role = message.role, content = message.content });
                }

                var request = new AiChatRequest
                {
                    model = _provider.defaultModel,
                    temperature = Temperature,
                    maxTokens = MaxTokens,
                    systemPrompt = SystemPrompt,
                    messages = requestMessages
                };

                if (UseStreaming && onStreamToken != null)
                {
                    var streamMsg = new ChatMessage
                    {
                        role = "assistant",
                        content = string.Empty,
                        unixTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    };
                    Messages.Add(streamMsg);

                    var buf = new System.Text.StringBuilder();
                    await _aiClient.SendMessageStreamAsync(_provider, request, token =>
                    {
                        buf.Append(token);
                        streamMsg.content = buf.ToString();
                        onStreamToken(token);
                    }, _cts.Token);
                }
                else
                {
                    var response = await _aiClient.SendMessageAsync(_provider, request, _cts.Token);
                    AddAssistantMessage(response);
                }
            }
            catch (Exception ex)
            {
                AddAssistantMessage(new AiChatResponse { content = $"[Error] {ex.Message}" });
            }
        }
    }
}
