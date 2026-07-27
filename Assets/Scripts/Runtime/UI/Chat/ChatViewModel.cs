using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Api;
using NeonCompanion.Runtime.Api.Models;
using NeonCompanion.Runtime.Api.Tools;
using NeonCompanion.Runtime.Chat;
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
            ChatMessage message = new ChatMessage
            {
                role = "user",
                content = content,
                attachments = CloneAttachments(attachments),
                unixTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
            message.responseItems.Add(new ChatResponseItem
            {
                type = "message",
                role = "user",
                content = content
            });
            Messages.Add(message);
        }

        public async Task AddAssistantMessage(AiChatResponse response, DateTime? startedAtUtc = null)
        {
            var chatMsg = new ChatMessage
            {
                role = "assistant",
                content = response?.content ?? string.Empty,
                model = response?.model ?? string.Empty,
                unixTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            ApplyResponsesMetadata(chatMsg, response, FindLatestResponseId(), startedAtUtc);

            if (response != null && response.tool_calls != null && response.tool_calls.Count > 0)
            {
                chatMsg.tool_calls = CloneToolCalls(response.tool_calls);
            }

            await ApplyIncomingAttachmentsAsync(chatMsg, response);

            Messages.Add(chatMsg);
        }

        public async Task RegenerateAsync(Action<string> onStreamToken = null, Action<ToolProgressInfo> onToolProgress = null)
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

        public async Task SendAsync(Action<string> onStreamToken = null, Action<ToolProgressInfo> onToolProgress = null)
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

        private async Task SendRequestAsync(Action<string> onStreamToken, Action<ToolProgressInfo> onToolProgress = null)
        {
            try
            {
                DateTime requestStartedAtUtc = DateTime.UtcNow;
                var requestMessages = new List<AiChatMessage>();
                for (int i = 0; i < Messages.Count; i++)
                {
                    var message = Messages[i];
                    bool hasText = !string.IsNullOrWhiteSpace(message?.content);
                    bool canSendAttachments = message != null && string.Equals(message.role, "user", StringComparison.OrdinalIgnoreCase);
                    bool hasAttachments = canSendAttachments && message.attachments != null && message.attachments.Count > 0;
                    bool hasToolCalls = message != null && message.tool_calls != null && message.tool_calls.Count > 0;
                    bool hasToolCallRef = !string.IsNullOrEmpty(message?.tool_call_id);
                    if (string.IsNullOrWhiteSpace(message?.role) || (!hasText && !hasAttachments && !hasToolCalls && !hasToolCallRef))
                        continue;

                    requestMessages.Add(new AiChatMessage
                    {
                        role = message.role,
                        content = message.content,
                        // Manual Responses history must replay every prior user input, including
                        // the real file/image bytes, not a presentation placeholder.
                        attachments = canSendAttachments ? ToAiAttachments(message.attachments) : new List<AiChatAttachment>(),
                        tool_call_id = message.tool_call_id,
                        tool_calls = CloneToolCalls(message.tool_calls),
                        responseOutput = string.Equals(message.role, "assistant", StringComparison.OrdinalIgnoreCase)
                            ? ToResponsesOutputItems(message.responseItems)
                            : null
                    });
                }

                var request = new AiChatRequest
                {
                    model = string.IsNullOrWhiteSpace(SelectedModel) ? _provider.defaultModel : SelectedModel,
                    providerSessionId = ProviderSessionId,
                    temperature = Temperature,
                    maxTokens = MaxTokens,
                    systemPrompt = SystemPrompt,
                    messages = requestMessages,
                    tools = ToolRegistry.GetToolDefinitions()
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
                    string previousResponseId = FindLatestResponseId();
                    Messages.Add(streamMsg);

                    var buf = new System.Text.StringBuilder();
                    Action<string> handleToken = token =>
                    {
                        buf.Append(token);
                        streamMsg.content = buf.ToString();
                        AppendTextSegment(streamMsg, token);
                        onStreamToken(token);
                    };
                    Action<ToolProgressInfo> handleToolProgress = info =>
                    {
                        if (info == null)
                            return;
                        UpsertToolSegment(streamMsg, info.tool, info.label, info.emoji, info.status, info.toolId, info.inlineDiff, info.details);
                        if (onToolProgress != null)
                            onToolProgress(info);
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
                    ApplyResponsesMetadata(streamMsg, response, previousResponseId, requestStartedAtUtc);
                    if (response != null && response.tool_calls != null && response.tool_calls.Count > 0)
                    {
                        streamMsg.tool_calls = CloneToolCalls(response.tool_calls);
                    }
                    await ApplyIncomingAttachmentsAsync(streamMsg, response);
                }
                else
                {
                    var response = await _aiClient.SendMessageAsync(_provider, request, CancellationToken);
                    ProviderSessionId = response?.providerSessionId ?? ProviderSessionId;
                    await AddAssistantMessage(response, requestStartedAtUtc);
                }
            }
            catch
            {
                if (Messages.Count > 0)
                {
                    var last = Messages[Messages.Count - 1];
                    bool hasToolCalls = last != null && last.tool_calls != null && last.tool_calls.Count > 0;
                    bool isEmptyAssistantPlaceholder = last != null &&
                                                      string.Equals(last.role, "assistant", StringComparison.OrdinalIgnoreCase) &&
                                                      string.IsNullOrWhiteSpace(last.content) &&
                                                      string.IsNullOrWhiteSpace(last.model) &&
                                                      (last.segments == null || last.segments.Count == 0) &&
                                                      (last.attachments == null || last.attachments.Count == 0) &&
                                                      !hasToolCalls;
                    if (isEmptyAssistantPlaceholder)
                        Messages.RemoveAt(Messages.Count - 1);
                }

                throw;
            }
        }

        private string FindLatestResponseId()
        {
            if (Messages == null)
                return null;

            for (int i = Messages.Count - 1; i >= 0; i--)
            {
                ChatMessage message = Messages[i];
                if (message != null && !string.IsNullOrWhiteSpace(message.responseId))
                    return message.responseId;
            }

            return null;
        }

        private static void ApplyResponsesMetadata(
            ChatMessage message,
            AiChatResponse response,
            string previousResponseId,
            DateTime? startedAtUtc)
        {
            if (message == null || response == null)
                return;

            message.responseId = response.id;
            message.previousResponseId = previousResponseId;
            message.responseUsage = ToChatResponseUsage(response.usage);
            message.responseItems = ToChatResponseItems(response.responseOutput);
            message.reasoning = ExtractReasoning(response.responseOutput);

            if (message.responseUsage != null)
            {
                message.tokenCount = message.responseUsage.outputTokens;
                if (message.tokenCount <= 0 && message.responseUsage.totalTokens > message.responseUsage.inputTokens)
                    message.tokenCount = message.responseUsage.totalTokens - message.responseUsage.inputTokens;
            }

            // Responses `usage` is optional: OpenAI reports it on response.completed, most
            // OpenAI-compatible servers never send it. Without a fallback the bubble's stats
            // footer loses its token figure (ChatMessageListRenderer then prints elapsed time
            // only). Estimate from the produced text instead — the same "exact when reported,
            // estimate otherwise" rule the Hermes path applies in ChatService.
            if (message.tokenCount <= 0)
                message.tokenCount = EstimateTokenCount(message.content);

            if (startedAtUtc.HasValue)
            {
                double elapsed = (DateTime.UtcNow - startedAtUtc.Value).TotalSeconds;
                message.responseTimeSeconds = elapsed > 0d ? (float)elapsed : 0f;
            }
        }

        /// <summary>Rough output-token estimate (~4 chars/token), same heuristic as ChatService.</summary>
        private static int EstimateTokenCount(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            return Math.Max(1, (text.Length + 3) / 4);
        }

        private static ChatResponseUsage ToChatResponseUsage(ResponsesUsage usage)
        {
            if (usage == null)
                return null;

            ChatResponseUsage result = new ChatResponseUsage();
            result.inputTokens = usage.input_tokens;
            result.outputTokens = usage.output_tokens;
            result.totalTokens = usage.total_tokens;
            result.cachedInputTokens = usage.input_tokens_details != null
                ? usage.input_tokens_details.cached_tokens
                : 0;
            result.reasoningTokens = usage.output_tokens_details != null
                ? usage.output_tokens_details.reasoning_tokens
                : 0;
            return result;
        }

        private static List<ChatResponseItem> ToChatResponseItems(IReadOnlyList<ResponsesOutputItem> output)
        {
            List<ChatResponseItem> result = new List<ChatResponseItem>();
            if (output == null)
                return result;

            for (int i = 0; i < output.Count; i++)
            {
                ResponsesOutputItem item = output[i];
                if (item == null)
                    continue;

                ChatResponseItem copy = new ChatResponseItem();
                copy.id = item.id;
                copy.type = item.type;
                copy.role = item.role;
                copy.status = item.status;
                copy.callId = item.call_id;
                copy.name = item.name;
                copy.arguments = item.arguments;
                copy.encryptedContent = item.encrypted_content;
                copy.content = JoinResponseParts(item.content);
                copy.summary = JoinResponseParts(item.summary);
                copy.contentParts = ToChatResponseContentParts(item.content);
                copy.summaryParts = ToChatResponseContentParts(item.summary);
                result.Add(copy);
            }

            return result;
        }

        private static List<ResponsesOutputItem> ToResponsesOutputItems(IReadOnlyList<ChatResponseItem> items)
        {
            List<ResponsesOutputItem> result = new List<ResponsesOutputItem>();
            if (items == null)
                return result;

            for (int i = 0; i < items.Count; i++)
            {
                ChatResponseItem item = items[i];
                if (item == null)
                    continue;

                ResponsesOutputItem copy = new ResponsesOutputItem();
                copy.id = item.id;
                copy.type = item.type;
                copy.role = item.role;
                copy.status = item.status;
                copy.call_id = item.callId;
                copy.name = item.name;
                copy.arguments = item.arguments;
                copy.encrypted_content = item.encryptedContent;
                copy.content = ToResponsesContentParts(item.contentParts);
                copy.summary = ToResponsesContentParts(item.summaryParts);
                result.Add(copy);
            }

            return result;
        }

        private static ResponsesContentPart[] ToResponsesContentParts(IReadOnlyList<ChatResponseContentPart> parts)
        {
            if (parts == null || parts.Count == 0)
                return null;

            ResponsesContentPart[] result = new ResponsesContentPart[parts.Count];
            for (int i = 0; i < parts.Count; i++)
            {
                ChatResponseContentPart part = parts[i];
                if (part == null)
                    continue;
                result[i] = new ResponsesContentPart
                {
                    type = part.type,
                    text = part.text,
                    refusal = part.refusal
                };
            }
            return result;
        }

        private static string ExtractReasoning(IReadOnlyList<ResponsesOutputItem> output)
        {
            if (output == null)
                return null;

            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            for (int i = 0; i < output.Count; i++)
            {
                ResponsesOutputItem item = output[i];
                if (item == null || !string.Equals(item.type, "reasoning", StringComparison.OrdinalIgnoreCase))
                    continue;

                string summary = JoinResponseParts(item.summary);
                if (string.IsNullOrWhiteSpace(summary))
                    continue;
                if (builder.Length > 0)
                    builder.AppendLine();
                builder.Append(summary);
            }

            return builder.Length > 0 ? builder.ToString() : null;
        }

        private static string JoinResponseParts(IReadOnlyList<ResponsesContentPart> parts)
        {
            if (parts == null)
                return null;

            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            for (int i = 0; i < parts.Count; i++)
            {
                ResponsesContentPart part = parts[i];
                if (part == null)
                    continue;
                string value = !string.IsNullOrWhiteSpace(part.text) ? part.text : part.refusal;
                if (string.IsNullOrWhiteSpace(value))
                    continue;
                if (builder.Length > 0)
                    builder.AppendLine();
                builder.Append(value);
            }

            return builder.Length > 0 ? builder.ToString() : null;
        }

        private static List<ChatResponseContentPart> ToChatResponseContentParts(IReadOnlyList<ResponsesContentPart> parts)
        {
            List<ChatResponseContentPart> result = new List<ChatResponseContentPart>();
            if (parts == null)
                return result;

            for (int i = 0; i < parts.Count; i++)
            {
                ResponsesContentPart part = parts[i];
                if (part == null)
                    continue;

                result.Add(new ChatResponseContentPart
                {
                    type = part.type,
                    text = part.text,
                    refusal = part.refusal
                });
            }

            return result;
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

        private static void UpsertToolSegment(
            ChatMessage message,
            string tool,
            string label,
            string emoji,
            string status,
            string toolId = null,
            string inlineDiff = null,
            string details = null)
        {
            if (message == null)
                return;

            if (message.segments == null)
                message.segments = new List<ChatMessageSegment>();

            string key = BuildToolSegmentKey(tool, toolId, label);
            for (int i = 0; i < message.segments.Count; i++)
            {
                var existing = message.segments[i];
                if (existing == null ||
                    !string.Equals(existing.kind, ChatMessageSegment.ToolKind, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(existing.key, key, StringComparison.Ordinal))
                {
                    continue;
                }

                existing.tool = tool ?? string.Empty;
                if (!string.IsNullOrEmpty(toolId))
                    existing.toolId = toolId;
                if (!string.IsNullOrEmpty(label))
                    existing.label = label;
                if (!string.IsNullOrEmpty(emoji))
                    existing.emoji = emoji;
                existing.status = status ?? string.Empty;
                if (!string.IsNullOrEmpty(inlineDiff))
                    existing.inlineDiff = inlineDiff;
                if (!string.IsNullOrEmpty(details))
                    existing.details = details;
                return;
            }

            message.segments.Add(new ChatMessageSegment
            {
                kind = ChatMessageSegment.ToolKind,
                key = key,
                tool = tool ?? string.Empty,
                toolId = toolId ?? string.Empty,
                label = label ?? string.Empty,
                emoji = emoji ?? string.Empty,
                status = status ?? string.Empty,
                inlineDiff = inlineDiff,
                details = details
            });
        }

        private static string BuildToolSegmentKey(string tool, string toolId, string label)
        {
            if (!string.IsNullOrEmpty(toolId))
                return "id\x01" + toolId;
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

        private static List<ToolCall> CloneToolCalls(List<ToolCall> source)
        {
            var clone = new List<ToolCall>();
            if (source == null)
                return clone;

            for (int i = 0; i < source.Count; i++)
            {
                var t = source[i];
                if (t == null)
                    continue;

                var c = new ToolCall();
                c.id = t.id ?? string.Empty;
                c.type = string.IsNullOrEmpty(t.type) ? "function" : t.type;
                if (t.function != null)
                {
                    c.function = new ToolCallFunction();
                    c.function.name = t.function.name ?? string.Empty;
                    c.function.arguments = t.function.arguments ?? string.Empty;
                }
                clone.Add(c);
            }

            return clone;
        }

        private async Task ApplyIncomingAttachmentsAsync(ChatMessage chatMsg, AiChatResponse response)
        {
            if (chatMsg == null)
                return;

            var incoming = new List<AiChatAttachment>();
            if (response != null && response.attachments != null)
            {
                for (int i = 0; i < response.attachments.Count; i++)
                {
                    if (response.attachments[i] != null)
                        incoming.Add(response.attachments[i]);
                }
            }

            await ChatMediaPipeline.ApplyMediaMarkersAsync(chatMsg, _provider, incoming);
        }

    }
}
