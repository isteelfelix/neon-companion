using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Api;
using NeonCompanion.Runtime.Api.Models;
using NeonCompanion.Runtime.Api.Tools;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;
using UnityEngine;
using UnityEngine.Networking;

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

            if (startedAtUtc.HasValue)
            {
                double elapsed = (DateTime.UtcNow - startedAtUtc.Value).TotalSeconds;
                message.responseTimeSeconds = elapsed > 0d ? (float)elapsed : 0f;
            }
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

            string originalContent = chatMsg.content;
            List<string> originalSegmentText = SnapshotTextSegments(chatMsg);

            var incoming = new List<AiChatAttachment>();
            if (response != null && response.attachments != null && response.attachments.Count > 0)
            {
                for (int i = 0; i < response.attachments.Count; i++)
                {
                    if (response.attachments[i] != null)
                        incoming.Add(response.attachments[i]);
                }
            }

            int mediaMarkerStart = incoming.Count;
            ExtractMediaMarkerAttachments(chatMsg, incoming);
            int mediaMarkerCount = incoming.Count - mediaMarkerStart;
            if (incoming.Count == 0)
                return;

            var localAtts = new List<ChatAttachment>();
            if (chatMsg.attachments != null && chatMsg.attachments.Count > 0)
                localAtts.AddRange(CloneAttachments(chatMsg.attachments));

            bool downloadedMediaMarker = false;
            for (int i = 0; i < incoming.Count; i++)
            {
                var cached = await DownloadAndCacheAttachment(incoming[i]);
                if (cached != null)
                {
                    localAtts.Add(cached);
                    if (i >= mediaMarkerStart)
                        downloadedMediaMarker = true;
                }
            }

            chatMsg.attachments = localAtts;
            if (mediaMarkerCount > 0 && !downloadedMediaMarker)
            {
                RestoreTextSegments(chatMsg, originalContent, originalSegmentText);
            }
        }

        private static List<string> SnapshotTextSegments(ChatMessage message)
        {
            var snapshot = new List<string>();
            if (message == null || message.segments == null)
                return snapshot;

            for (int i = 0; i < message.segments.Count; i++)
            {
                var segment = message.segments[i];
                if (segment != null && string.Equals(segment.kind, ChatMessageSegment.TextKind, StringComparison.OrdinalIgnoreCase))
                    snapshot.Add(segment.text);
            }

            return snapshot;
        }

        private static void RestoreTextSegments(ChatMessage message, string content, List<string> segmentTexts)
        {
            if (message == null)
                return;

            message.content = content ?? string.Empty;
            if (message.segments == null || segmentTexts == null)
                return;

            int textIndex = 0;
            for (int i = 0; i < message.segments.Count; i++)
            {
                var segment = message.segments[i];
                if (segment == null || !string.Equals(segment.kind, ChatMessageSegment.TextKind, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (textIndex < segmentTexts.Count)
                    segment.text = segmentTexts[textIndex];
                textIndex++;
            }
        }

        private void ExtractMediaMarkerAttachments(ChatMessage message, List<AiChatAttachment> attachments)
        {
            if (message == null || attachments == null)
                return;

            message.content = ExtractMediaMarkersFromText(message.content, attachments);

            if (message.segments == null)
                return;

            for (int i = 0; i < message.segments.Count; i++)
            {
                var segment = message.segments[i];
                if (segment == null || !string.Equals(segment.kind, ChatMessageSegment.TextKind, StringComparison.OrdinalIgnoreCase))
                    continue;

                segment.text = ExtractMediaMarkersFromText(segment.text, attachments);
            }
        }

        private string ExtractMediaMarkersFromText(string text, List<AiChatAttachment> attachments)
        {
            if (string.IsNullOrEmpty(text) || attachments == null)
                return text ?? string.Empty;

            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalized.Split('\n');
            var kept = new List<string>(lines.Length);
            bool changed = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string mediaPath;
                if (TryReadMediaMarker(line, out mediaPath))
                {
                    var attachment = CreateMediaMarkerAttachment(mediaPath);
                    if (attachment != null && !ContainsIncomingAttachment(attachments, attachment.path))
                        attachments.Add(attachment);
                    changed = true;
                    continue;
                }

                kept.Add(line);
            }

            if (!changed)
                return text;

            return TrimBlankEdges(string.Join("\n", kept.ToArray()));
        }

        private static bool TryReadMediaMarker(string line, out string mediaPath)
        {
            mediaPath = null;
            if (string.IsNullOrWhiteSpace(line))
                return false;

            string trimmed = line.Trim();
            const string marker = "MEDIA:";
            bool isMediaMarker = trimmed.StartsWith(marker, StringComparison.OrdinalIgnoreCase);
            if (!isMediaMarker)
                return TryReadMarkdownImageMarker(trimmed, out mediaPath);

            string value = trimmed.Substring(marker.Length).Trim();
            if (value.Length >= 2)
            {
                bool quoted = (value[0] == '"' && value[value.Length - 1] == '"') ||
                              (value[0] == '\'' && value[value.Length - 1] == '\'') ||
                              (value[0] == '`' && value[value.Length - 1] == '`');
                if (quoted)
                    value = value.Substring(1, value.Length - 2).Trim();
            }

            if (string.IsNullOrWhiteSpace(value))
                return false;

            mediaPath = value;
            return true;
        }

        private static bool TryReadMarkdownImageMarker(string trimmedLine, out string mediaPath)
        {
            mediaPath = null;
            if (string.IsNullOrWhiteSpace(trimmedLine))
                return false;

            if (!trimmedLine.StartsWith("![", StringComparison.Ordinal))
                return false;

            int labelEnd = trimmedLine.IndexOf("](", StringComparison.Ordinal);
            if (labelEnd < 0)
                return false;

            int pathStart = labelEnd + 2;
            int pathEnd = trimmedLine.LastIndexOf(')');
            if (pathEnd <= pathStart)
                return false;

            string value = trimmedLine.Substring(pathStart, pathEnd - pathStart).Trim();
            if (value.Length >= 2)
            {
                bool quoted = (value[0] == '"' && value[value.Length - 1] == '"') ||
                              (value[0] == '\'' && value[value.Length - 1] == '\'') ||
                              (value[0] == '`' && value[value.Length - 1] == '`');
                if (quoted)
                    value = value.Substring(1, value.Length - 2).Trim();
            }

            if (string.IsNullOrWhiteSpace(value))
                return false;

            if (!LooksLikeIncomingMediaPath(value))
                return false;

            mediaPath = value;
            return true;
        }

        private static bool LooksLikeIncomingMediaPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            if (value.StartsWith("MEDIA:", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("/root/", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("root/", StringComparison.OrdinalIgnoreCase))
                return true;

            string ext = Path.GetExtension(value);
            return !string.IsNullOrEmpty(ext) &&
                   (string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".jpg", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".jpeg", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".gif", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".webp", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".bmp", StringComparison.OrdinalIgnoreCase));
        }

        private AiChatAttachment CreateMediaMarkerAttachment(string mediaPath)
        {
            if (string.IsNullOrWhiteSpace(mediaPath))
                return null;

            string resolvedPath = ResolveMediaMarkerPath(mediaPath);
            string ext = GetFileExtensionFromUrl(mediaPath);
            return new AiChatAttachment
            {
                kind = "image",
                name = DeriveFileNameFromUrl(mediaPath, ext),
                path = resolvedPath,
                mediaType = GuessMediaTypeFromExtension(ext)
            };
        }

        private static bool ContainsIncomingAttachment(List<AiChatAttachment> attachments, string path)
        {
            if (attachments == null || string.IsNullOrWhiteSpace(path))
                return false;

            for (int i = 0; i < attachments.Count; i++)
            {
                var attachment = attachments[i];
                if (attachment == null)
                    continue;

                if (string.Equals(attachment.path, path, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private string ResolveMediaMarkerPath(string mediaPath)
        {
            string path = (mediaPath ?? string.Empty).Trim();
            const string mediaPrefix = "MEDIA:";
            if (path.StartsWith(mediaPrefix, StringComparison.OrdinalIgnoreCase))
                path = path.Substring(mediaPrefix.Length).Trim();
            if (string.IsNullOrEmpty(path))
                return path;

            if (path.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                File.Exists(path))
            {
                return path;
            }

            string normalizedPath = path.Replace('\\', '/');
            const string hermesRoot = "/root/hermes";
            const string hermesHiddenRoot = "/root/.hermes";
            if (normalizedPath.StartsWith(hermesRoot, StringComparison.OrdinalIgnoreCase))
                normalizedPath = normalizedPath.Substring(hermesRoot.Length);
            else if (normalizedPath.StartsWith(hermesHiddenRoot, StringComparison.OrdinalIgnoreCase))
                normalizedPath = normalizedPath.Substring(hermesHiddenRoot.Length);
            else if (normalizedPath.StartsWith("root/hermes/", StringComparison.OrdinalIgnoreCase))
                normalizedPath = "/" + normalizedPath.Substring("root/hermes/".Length);
            else if (normalizedPath.StartsWith("root/.hermes/", StringComparison.OrdinalIgnoreCase))
                normalizedPath = "/" + normalizedPath.Substring("root/.hermes/".Length);

            string baseUrl = normalizedPath.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase)
                ? GetProviderOriginUrl()
                : GetProviderMediaBaseUrl();
            if (string.IsNullOrWhiteSpace(baseUrl))
                return path;

            if (!normalizedPath.StartsWith("/", StringComparison.Ordinal))
                normalizedPath = "/" + normalizedPath;

            return baseUrl.TrimEnd('/') + normalizedPath;
        }

        private string GetProviderMediaBaseUrl()
        {
            string baseUrl = (_provider != null ? _provider.baseUrl : null) ?? string.Empty;
            baseUrl = baseUrl.Trim().TrimEnd('/');
            if (string.IsNullOrEmpty(baseUrl))
                return string.Empty;

            if (baseUrl.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
                baseUrl = baseUrl.Substring(0, baseUrl.Length - "/responses".Length).TrimEnd('/');
            if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                baseUrl = baseUrl.Substring(0, baseUrl.Length - 3).TrimEnd('/');

            return baseUrl;
        }

        private string GetProviderOriginUrl()
        {
            string baseUrl = GetProviderMediaBaseUrl();
            if (string.IsNullOrEmpty(baseUrl))
                return baseUrl;

            try
            {
                var uri = new Uri(baseUrl);
                return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            }
            catch
            {
                return baseUrl;
            }
        }

        private static string TrimBlankEdges(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            int start = 0;
            int end = value.Length - 1;

            while (start <= end && (value[start] == '\n' || value[start] == ' ' || value[start] == '\t'))
                start++;

            while (end >= start && (value[end] == '\n' || value[end] == ' ' || value[end] == '\t'))
                end--;

            if (start > end)
                return string.Empty;

            return value.Substring(start, end - start + 1);
        }

        private async Task<ChatAttachment> DownloadAndCacheAttachment(AiChatAttachment aiAtt)
        {
            if (aiAtt == null || string.IsNullOrWhiteSpace(aiAtt.path))
                return null;

            string url = aiAtt.path.Trim();

            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return CacheDataUrlAttachment(aiAtt, url);

            // If already a local path, return as-is (no download needed)
            if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
                File.Exists(url))
            {
                return new ChatAttachment
                {
                    kind = string.IsNullOrWhiteSpace(aiAtt.kind) ? "image" : aiAtt.kind,
                    name = !string.IsNullOrWhiteSpace(aiAtt.name) ? aiAtt.name : "image",
                    path = url,
                    mediaType = aiAtt.mediaType
                };
            }

            try
            {
                string ext = GetFileExtensionFromUrl(url);
                string fileName = Guid.NewGuid().ToString("N") + ext;
                string dir = Path.Combine(Application.persistentDataPath, "Attachments");
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                string localPath = Path.Combine(dir, fileName);

                using (var req = UnityWebRequest.Get(url))
                {
                    var operation = req.SendWebRequest();
                    while (!operation.isDone)
                        await Task.Yield();

                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        NeonLogger.LogWarning("Failed to download incoming attachment from " + url + ": " + (req.error ?? "unknown error"));
                        return null;
                    }

                    byte[] data = req.downloadHandler != null ? req.downloadHandler.data : null;
                    if (data == null || data.Length == 0)
                    {
                        NeonLogger.LogWarning("Downloaded attachment has no data from " + url);
                        return null;
                    }

                    string mediaType = !string.IsNullOrWhiteSpace(aiAtt.mediaType) ? aiAtt.mediaType : GuessMediaTypeFromExtension(ext);
                    string ct = req.GetResponseHeader("Content-Type");
                    if (string.IsNullOrWhiteSpace(aiAtt.mediaType) && !string.IsNullOrWhiteSpace(ct))
                    {
                        int semi = ct.IndexOf(';');
                        mediaType = semi > 0 ? ct.Substring(0, semi).Trim() : ct.Trim();
                    }

                    if (!IsSupportedImagePayload(data, mediaType, ext))
                    {
                        NeonLogger.LogWarning("Incoming attachment did not look like an image from " + url + " (Content-Type: " + (ct ?? "<none>") + ")");
                        return null;
                    }

                    File.WriteAllBytes(localPath, data);

                    string attName = !string.IsNullOrWhiteSpace(aiAtt.name) ? aiAtt.name : DeriveFileNameFromUrl(url, ext);

                    return new ChatAttachment
                    {
                        kind = "image",
                        name = attName,
                        path = localPath,
                        mediaType = mediaType
                    };
                }
            }
            catch (Exception ex)
            {
                NeonLogger.LogWarning("DownloadAndCacheAttachment failed for " + url + ": " + ex.Message);
                return null;
            }
        }

        private ChatAttachment CacheDataUrlAttachment(AiChatAttachment aiAtt, string dataUrl)
        {
            if (string.IsNullOrWhiteSpace(dataUrl))
                return null;

            try
            {
                int comma = dataUrl.IndexOf(',');
                if (comma < 0)
                    return null;

                string meta = dataUrl.Substring(0, comma);
                string payload = dataUrl.Substring(comma + 1);
                if (meta.IndexOf(";base64", StringComparison.OrdinalIgnoreCase) < 0)
                    return null;

                string mediaType = aiAtt != null && !string.IsNullOrWhiteSpace(aiAtt.mediaType)
                    ? aiAtt.mediaType
                    : ExtractDataUrlMediaType(meta);
                string ext = GetExtensionFromMediaType(mediaType);
                byte[] data = Convert.FromBase64String(payload);
                if (!IsSupportedImagePayload(data, mediaType, ext))
                    return null;

                string dir = Path.Combine(Application.persistentDataPath, "Attachments");
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string localPath = Path.Combine(dir, Guid.NewGuid().ToString("N") + ext);
                File.WriteAllBytes(localPath, data);

                return new ChatAttachment
                {
                    kind = "image",
                    name = aiAtt != null && !string.IsNullOrWhiteSpace(aiAtt.name) ? aiAtt.name : "image" + ext,
                    path = localPath,
                    mediaType = mediaType
                };
            }
            catch (Exception ex)
            {
                NeonLogger.LogWarning("CacheDataUrlAttachment failed: " + ex.Message);
                return null;
            }
        }

        private static string ExtractDataUrlMediaType(string meta)
        {
            if (string.IsNullOrWhiteSpace(meta))
                return "image/png";

            const string prefix = "data:";
            if (!meta.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return "image/png";

            string mediaType = meta.Substring(prefix.Length);
            int semi = mediaType.IndexOf(';');
            if (semi >= 0)
                mediaType = mediaType.Substring(0, semi);

            return string.IsNullOrWhiteSpace(mediaType) ? "image/png" : mediaType.Trim();
        }

        private static string GetExtensionFromMediaType(string mediaType)
        {
            if (string.IsNullOrWhiteSpace(mediaType))
                return ".png";

            string mt = mediaType.Trim().ToLowerInvariant();
            if (mt == "image/png")
                return ".png";
            if (mt == "image/jpeg" || mt == "image/jpg")
                return ".jpg";
            if (mt == "image/webp")
                return ".webp";
            if (mt == "image/gif")
                return ".gif";
            if (mt == "image/bmp")
                return ".bmp";
            if (mt == "image/svg+xml")
                return ".svg";
            return ".png";
        }

        private static string GetFileExtensionFromUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return ".png";
            string clean = url;
            int q = clean.IndexOf('?');
            if (q >= 0)
                clean = clean.Substring(0, q);
            int h = clean.IndexOf('#');
            if (h >= 0)
                clean = clean.Substring(0, h);
            string ext = Path.GetExtension(clean);
            if (string.IsNullOrEmpty(ext) || ext.Length > 5)
                return ".png";
            return ext.ToLowerInvariant();
        }

        private static bool IsSupportedImagePayload(byte[] data, string mediaType, string ext)
        {
            if (data == null || data.Length < 4)
                return false;

            if (HasImageMagic(data))
                return true;

            if (!string.IsNullOrWhiteSpace(mediaType) &&
                !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return false;

            string e = (ext ?? string.Empty).ToLowerInvariant();
            return e == ".svg";
        }

        private static bool HasImageMagic(byte[] data)
        {
            if (data == null || data.Length < 4)
                return false;

            if (data.Length >= 8 &&
                data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47 &&
                data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
                return true;

            if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
                return true;

            if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x38)
                return true;

            if (data[0] == 0x42 && data[1] == 0x4D)
                return true;

            if (data.Length >= 12 &&
                data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46 &&
                data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
                return true;

            return false;
        }

        private static string GuessMediaTypeFromExtension(string ext)
        {
            if (string.IsNullOrEmpty(ext))
                return "image/png";
            string e = ext.ToLowerInvariant();
            if (e == ".png")
                return "image/png";
            if (e == ".jpg" || e == ".jpeg")
                return "image/jpeg";
            if (e == ".webp")
                return "image/webp";
            if (e == ".gif")
                return "image/gif";
            if (e == ".bmp")
                return "image/bmp";
            return "application/octet-stream";
        }

        private static string DeriveFileNameFromUrl(string url, string ext)
        {
            if (string.IsNullOrWhiteSpace(url))
                return "image" + ext;
            try
            {
                string clean = url;
                int q = clean.IndexOf('?');
                if (q >= 0)
                    clean = clean.Substring(0, q);
                int h = clean.IndexOf('#');
                if (h >= 0)
                    clean = clean.Substring(0, h);
                string fname = Path.GetFileName(clean);
                if (!string.IsNullOrEmpty(fname) && fname.IndexOf('.') > 0)
                    return fname;
            }
            catch { }
            return "image" + ext;
        }
    }
}
