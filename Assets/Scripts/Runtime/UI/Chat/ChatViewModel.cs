using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Api;
using NeonCompanion.Runtime.Api.Models;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;

namespace NeonCompanion.Runtime.UI.Chat
{
    public sealed class ChatViewModel
    {
        private readonly IAiClient _aiClient;
        private readonly ProviderConfig _provider;
        private CancellationTokenSource _cts = new CancellationTokenSource();

        public CancellationToken CancellationToken => _cts.Token;

        public string InputMessage { get; set; }
        public string ProviderSessionId { get; set; }
        public string SelectedModel { get; set; }
        public List<ChatAttachment> PendingAttachments { get; } = new List<ChatAttachment>();
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

        public void AddUserMessage(string content, IReadOnlyList<ChatAttachment> attachments = null)
        {
            Messages.Add(new ChatMessage
            {
                role = "user",
                content = content,
                attachments = CloneAttachments(attachments),
                unixTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
        }

        public void AddAssistantMessage(AiChatResponse response)
        {
            Messages.Add(new ChatMessage
            {
                role = "assistant",
                content = response?.content ?? string.Empty,
                model = response?.model ?? string.Empty,
                unixTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
        }

        public async Task RegenerateAsync(Action<string> onStreamToken = null, Action<string, string, string, string> onToolProgress = null)
        {
            if (IsSending) return;

            // Build request from the existing conversation (no new user message added)
            IsSending = true;
            try
            {
                await SendRequestAsync(onStreamToken, onToolProgress);
            }
            finally
            {
                IsSending = false;
            }
        }

        public async Task SendAsync(Action<string> onStreamToken = null, Action<string, string, string, string> onToolProgress = null)
        {
            bool hasPendingAttachments = PendingAttachments != null && PendingAttachments.Count > 0;
            if (IsSending || (string.IsNullOrWhiteSpace(InputMessage) && !hasPendingAttachments))
                return;

            var userMessage = (InputMessage ?? string.Empty).Trim();
            var attachments = CloneAttachments(PendingAttachments);
            InputMessage = string.Empty;
            PendingAttachments.Clear();
            AddUserMessage(userMessage, attachments);

            IsSending = true;
            try
            {
                await SendRequestAsync(onStreamToken, onToolProgress);
            }
            finally
            {
                IsSending = false;
            }
        }

        public void CancelGeneration()
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = new CancellationTokenSource();
        }

        private async Task SendRequestAsync(Action<string> onStreamToken, Action<string, string, string, string> onToolProgress = null)
        {
            try
            {
                var requestMessages = new List<AiChatMessage>();
                foreach (var message in Messages)
                {
                    bool hasText = !string.IsNullOrWhiteSpace(message?.content);
                    bool hasAttachments = message?.attachments != null && message.attachments.Count > 0;
                    if (string.IsNullOrWhiteSpace(message?.role) || (!hasText && !hasAttachments))
                        continue;

                    requestMessages.Add(new AiChatMessage
                    {
                        role = message.role,
                        content = message.content,
                        attachments = ToAiAttachments(message.attachments)
                    });
                }

                var request = new AiChatRequest
                {
                    model = string.IsNullOrWhiteSpace(SelectedModel) ? _provider.defaultModel : SelectedModel,
                    providerSessionId = ProviderSessionId,
                    temperature = Temperature,
                    maxTokens = MaxTokens,
                    systemPrompt = SystemPrompt,
                    messages = requestMessages
                };

                NeonLogger.Log($"ChatViewModel request: provider={_provider.id}, model={request.model}, providerSessionId={request.providerSessionId ?? "<null>"}, messages={requestMessages.Count}");

                if (UseStreaming && onStreamToken != null)
                {
                    var streamMsg = new ChatMessage
                    {
                        role = "assistant",
                        content = string.Empty,
                        model = string.Empty,
                        unixTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    };
                    Messages.Add(streamMsg);

                    var buf = new System.Text.StringBuilder();
                    Action<string> handleToken = token =>
                    {
                        buf.Append(token);
                        streamMsg.content = buf.ToString();
                        AppendTextSegment(streamMsg, token);
                        onStreamToken(token);
                    };
                    Action<string, string, string, string> handleToolProgress = (tool, label, emoji, status) =>
                    {
                        UpsertToolSegment(streamMsg, tool, label, emoji, status);
                        if (onToolProgress != null)
                            onToolProgress(tool, label, emoji, status);
                    };
                    var response = await _aiClient.SendMessageStreamAsync(_provider, request, token =>
                    {
                        handleToken(token);
                    }, CancellationToken, handleToolProgress);
                    ProviderSessionId = response?.providerSessionId ?? ProviderSessionId;
                    if (string.IsNullOrWhiteSpace(streamMsg.content) && !string.IsNullOrWhiteSpace(response?.content))
                    {
                        streamMsg.content = response.content;
                        AppendTextSegment(streamMsg, response.content);
                    }
                    streamMsg.model = response?.model ?? streamMsg.model;
                }
                else
                {
                    var response = await _aiClient.SendMessageAsync(_provider, request, CancellationToken);
                    ProviderSessionId = response?.providerSessionId ?? ProviderSessionId;
                    AddAssistantMessage(response);
                }
            }
            catch
            {
                if (Messages.Count > 0)
                {
                    var last = Messages[Messages.Count - 1];
                    bool isEmptyAssistantPlaceholder = last != null &&
                                                      string.Equals(last.role, "assistant", StringComparison.OrdinalIgnoreCase) &&
                                                      string.IsNullOrWhiteSpace(last.content) &&
                                                      string.IsNullOrWhiteSpace(last.model) &&
                                                      (last.segments == null || last.segments.Count == 0) &&
                                                      (last.attachments == null || last.attachments.Count == 0);
                    if (isEmptyAssistantPlaceholder)
                        Messages.RemoveAt(Messages.Count - 1);
                }

                throw;
            }
        }

        private static void AppendTextSegment(ChatMessage message, string text)
        {
            if (message == null || string.IsNullOrEmpty(text))
                return;

            if (message.segments == null)
                message.segments = new List<ChatMessageSegment>();

            ChatMessageSegment segment = null;
            if (message.segments.Count > 0)
            {
                var lastSegment = message.segments[message.segments.Count - 1];
                if (lastSegment != null && string.Equals(lastSegment.kind, ChatMessageSegment.TextKind, StringComparison.OrdinalIgnoreCase))
                    segment = lastSegment;
            }

            if (segment == null)
            {
                segment = new ChatMessageSegment
                {
                    kind = ChatMessageSegment.TextKind,
                    text = string.Empty
                };
                message.segments.Add(segment);
            }

            segment.text = (segment.text ?? string.Empty) + text;
        }

        private static void UpsertToolSegment(ChatMessage message, string tool, string label, string emoji, string status)
        {
            if (message == null)
                return;

            if (message.segments == null)
                message.segments = new List<ChatMessageSegment>();

            string key = BuildToolSegmentKey(tool, label);
            for (int i = 0; i < message.segments.Count; i++)
            {
                var segment = message.segments[i];
                if (segment == null || !string.Equals(segment.kind, ChatMessageSegment.ToolKind, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.Equals(segment.key, key, StringComparison.Ordinal))
                    continue;

                segment.tool = tool ?? string.Empty;
                segment.label = label ?? string.Empty;
                segment.emoji = emoji ?? string.Empty;
                segment.status = status ?? string.Empty;
                return;
            }

            message.segments.Add(new ChatMessageSegment
            {
                kind = ChatMessageSegment.ToolKind,
                key = key,
                tool = tool ?? string.Empty,
                label = label ?? string.Empty,
                emoji = emoji ?? string.Empty,
                status = status ?? string.Empty
            });
        }

        private static string BuildToolSegmentKey(string tool, string label)
        {
            return (tool ?? string.Empty) + "\x01" + (label ?? string.Empty);
        }

        private static List<ChatAttachment> CloneAttachments(IReadOnlyList<ChatAttachment> attachments)
        {
            var clone = new List<ChatAttachment>();
            if (attachments == null)
                return clone;

            for (int i = 0; i < attachments.Count; i++)
            {
                var attachment = attachments[i];
                if (attachment == null)
                    continue;

                clone.Add(new ChatAttachment
                {
                    kind = string.IsNullOrWhiteSpace(attachment.kind) ? "image" : attachment.kind,
                    name = attachment.name,
                    path = attachment.path,
                    mediaType = attachment.mediaType
                });
            }

            return clone;
        }

        private static List<AiChatAttachment> ToAiAttachments(IReadOnlyList<ChatAttachment> attachments)
        {
            var mapped = new List<AiChatAttachment>();
            if (attachments == null)
                return mapped;

            for (int i = 0; i < attachments.Count; i++)
            {
                var attachment = attachments[i];
                if (attachment == null)
                    continue;

                mapped.Add(new AiChatAttachment
                {
                    kind = string.IsNullOrWhiteSpace(attachment.kind) ? "image" : attachment.kind,
                    name = attachment.name,
                    path = attachment.path,
                    mediaType = attachment.mediaType
                });
            }

            return mapped;
        }
    }
}
