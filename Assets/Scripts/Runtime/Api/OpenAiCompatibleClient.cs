using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Api.Models;
using NeonCompanion.Runtime.Api.Adapters;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;
using UnityEngine;
using UnityEngine.Networking;

namespace NeonCompanion.Runtime.Api
{
    public struct UsageData
    {
        public int prompt_tokens;
        public int completion_tokens;
        public int total_tokens;
    }

    public sealed class OpenAiCompatibleClient : IAiClient
    {
        private static UsageData _lastStreamUsage;

        public UsageData LastStreamUsage => _lastStreamUsage;
        public async Task<AiChatResponse> SendMessageAsync(
            ProviderConfig provider,
            AiChatRequest request,
            CancellationToken cancellationToken = default)
        {
            ProviderValidator.Validate(provider);

            if (request == null)
                throw new ArgumentNullException(nameof(request));

            var messages = new List<AiChatMessage>(request.messages ?? new List<AiChatMessage>());

            // Add system prompt if provided
            if (!string.IsNullOrWhiteSpace(request.systemPrompt))
            {
                messages.Insert(0, new AiChatMessage
                {
                    role = "system",
                    content = request.systemPrompt
                });
            }

            var adapter = GetAdapter(provider);
            var capabilities = adapter.GetCapabilities();

            var routing = await ResolveRequestRoutingAsync(provider, request, cancellationToken);
            NeonLogger.Log($"OpenAI request send: provider={provider?.id}, requestedModel={request?.model}, routedModel={routing.Model}, providerSessionId={routing.ProviderSessionId ?? "<null>"}");
            var endpoint = BuildEndpoint(provider.baseUrl);
            var payloadJson = BuildChatCompletionPayloadJson(
                routing.Model,
                request.temperature,
                request.maxTokens,
                messages,
                stream: false,
                capabilities,
                request.tools);

            using (var webRequest = new UnityWebRequest(endpoint, UnityWebRequest.kHttpVerbPOST))
            {
                var bodyRaw = Encoding.UTF8.GetBytes(payloadJson);
                webRequest.uploadHandler = new UploadHandlerRaw(bodyRaw);
                webRequest.downloadHandler = new DownloadHandlerBuffer();
                webRequest.SetRequestHeader("Content-Type", "application/json");

                if (!string.IsNullOrWhiteSpace(provider.apiKey))
                {
                    webRequest.SetRequestHeader("Authorization", $"Bearer {provider.apiKey}");
                }
                adapter.ApplyRequestHeaders(webRequest, routing.ProviderSessionId);

                var operation = webRequest.SendWebRequest();

                while (!operation.isDone)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        webRequest.Abort();
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    await Task.Yield();
                }

                if (webRequest.result != UnityWebRequest.Result.Success)
                {
                    string errorMessage = ParseErrorMessage(webRequest);
                    throw new InvalidOperationException($"API request failed: {errorMessage}");
                }

                var rawResponse = webRequest.downloadHandler.text;
                var response = ParseResponse(rawResponse);
                response.providerSessionId = adapter.ExtractSessionId(webRequest, routing.ProviderSessionId);
                if (string.IsNullOrWhiteSpace(response.model))
                    response.model = routing.Model ?? request.model ?? string.Empty;

                return response;
            }
        }

        public async Task<ConnectionTestResult> TestConnectionAsync(
            ProviderConfig provider,
            CancellationToken cancellationToken = default)
        {
            ProviderValidator.ValidateForConnection(provider);

            var endpoint = BuildModelsEndpoint(provider.baseUrl);
            var startMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            using (var webRequest = UnityWebRequest.Get(endpoint))
            {
                if (!string.IsNullOrWhiteSpace(provider.apiKey))
                    webRequest.SetRequestHeader("Authorization", $"Bearer {provider.apiKey}");

                var operation = webRequest.SendWebRequest();

                while (!operation.isDone)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        webRequest.Abort();
                        return new ConnectionTestResult(false, "Cancelled");
                    }

                    await Task.Yield();
                }

                long latency = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startMs;

                if (webRequest.result == UnityWebRequest.Result.Success ||
                    webRequest.responseCode == 200 ||
                    webRequest.responseCode == 401)
                {
                    // 401 = server reachable, credentials wrong — still proves endpoint is live
                    bool authed = webRequest.responseCode != 401;
                    IReadOnlyList<string> discoveredModels = null;
                    string modelNote = null;

                    if (authed)
                    {
                        var adapter = GetAdapter(provider);
                        var capabilities = adapter.GetCapabilities();
                        var modelsPayload = webRequest.downloadHandler?.text;

                        if (capabilities != null && capabilities.SupportsInventory)
                        {
                            var endpoints = adapter.BuildDiscoveryEndpoints(provider.baseUrl);
                            for (int i = 0; i < endpoints.Length; i++)
                            {
                                string payload = null;
                                string ep = endpoints[i];
                                if (!string.IsNullOrEmpty(modelsPayload) &&
                                    ep.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
                                {
                                    payload = modelsPayload;
                                }
                                else
                                {
                                    payload = await FetchJsonAsync(ep, provider.apiKey, cancellationToken);
                                }

                                if (!string.IsNullOrEmpty(payload))
                                {
                                    var parsed = adapter.ParseDiscoveryResponse(payload);
                                    if (parsed != null && parsed.Count > 0)
                                    {
                                        discoveredModels = parsed;
                                        break;
                                    }
                                    if (string.IsNullOrEmpty(modelsPayload) || !ep.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
                                        modelsPayload = payload;
                                }
                            }
                        }

                        if (discoveredModels == null && !string.IsNullOrEmpty(modelsPayload))
                            discoveredModels = adapter.ParseDiscoveryResponse(modelsPayload);

                        if (!string.IsNullOrWhiteSpace(provider.defaultModel) && discoveredModels != null)
                        {
                            bool found = false;
                            var trimmedModel = provider.defaultModel.Trim();
                            foreach (var m in discoveredModels)
                            {
                                if (string.Equals(m, trimmedModel, StringComparison.OrdinalIgnoreCase))
                                {
                                    found = true;
                                    break;
                                }
                            }
                            if (!found)
                            {
                                modelNote = $" · модель «{provider.defaultModel}» не найдена — выберите из списка";
                            }
                        }

                        if (capabilities != null && capabilities.SupportsInventory &&
                            discoveredModels != null &&
                            discoveredModels.Count == 1 &&
                            string.Equals(discoveredModels[0], "hermes-agent", StringComparison.OrdinalIgnoreCase))
                        {
                            modelNote = AppendStatusNote(modelNote, " · inventory недоступен, сервер экспортирует только прокси-модель");
                        }

                        if (discoveredModels == null || discoveredModels.Count == 0)
                            modelNote = AppendStatusNote(modelNote, " · список моделей не распознан");
                    }

                    string msg = authed
                        ? $"OK · {latency} ms{modelNote}"
                        : $"Reachable but unauthorized · {latency} ms";
                    return new ConnectionTestResult(authed, msg, latency, discoveredModels);
                }

                return new ConnectionTestResult(false,
                    $"{webRequest.error ?? "error"} (HTTP {webRequest.responseCode}) · {latency} ms",
                    latency);
            }
        }

        public async Task<ModelSwitchResult> ApplySessionModelAsync(
            ProviderConfig provider,
            string targetModel,
            string providerSessionId = null,
            CancellationToken cancellationToken = default)
        {
            ProviderValidator.Validate(provider);

            string requestedModel = targetModel?.Trim();
            if (string.IsNullOrWhiteSpace(requestedModel))
                throw new ArgumentException("Target model is required.", nameof(targetModel));

            var adapter = GetAdapter(provider);
            var capabilities = adapter.GetCapabilities();

            if (capabilities == null || !capabilities.SupportsModelSwitch)
            {
                return new ModelSwitchResult(true, requestedModel, requestedModel, null);
            }

            ModelSwitchPayload payload = adapter.BuildModelSwitchRequest(requestedModel, providerSessionId);
            if (payload == null)
            {
                return new ModelSwitchResult(false, requestedModel, requestedModel, providerSessionId, "Model switch not supported.", true);
            }

            string switchEndpoint = !string.IsNullOrWhiteSpace(payload.Endpoint)
                ? payload.Endpoint
                : BuildEndpoint(provider.baseUrl);

            using (var webRequest = new UnityWebRequest(switchEndpoint, UnityWebRequest.kHttpVerbPOST))
            {
                string body = payload.JsonBody ?? string.Empty;
                var bodyRaw = Encoding.UTF8.GetBytes(body);
                webRequest.uploadHandler = new UploadHandlerRaw(bodyRaw);
                webRequest.downloadHandler = new DownloadHandlerBuffer();
                webRequest.SetRequestHeader("Content-Type", "application/json");

                if (!string.IsNullOrWhiteSpace(provider.apiKey))
                    webRequest.SetRequestHeader("Authorization", $"Bearer {provider.apiKey}");

                adapter.ApplyRequestHeaders(webRequest, providerSessionId);

                var operation = webRequest.SendWebRequest();
                while (!operation.isDone)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        webRequest.Abort();
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    await Task.Yield();
                }

                if (webRequest.result != UnityWebRequest.Result.Success)
                {
                    string err = ParseErrorMessage(webRequest);
                    return new ModelSwitchResult(false, requestedModel, requestedModel, providerSessionId, err, true);
                }

                string responseContent = webRequest.downloadHandler != null ? webRequest.downloadHandler.text : string.Empty;
                string appliedLabel = adapter.ParseModelSwitchResponse(responseContent);
                string newSessionId = adapter.ExtractSessionId(webRequest, providerSessionId);
                string finalApplied = !string.IsNullOrWhiteSpace(appliedLabel) ? appliedLabel : requestedModel;

                return new ModelSwitchResult(true, requestedModel, finalApplied, newSessionId, null, true);
            }
        }

        private async Task<string> FetchJsonAsync(string url, string apiKey, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(url))
                return null;

            using (var webRequest = UnityWebRequest.Get(url))
            {
                if (!string.IsNullOrWhiteSpace(apiKey))
                    webRequest.SetRequestHeader("Authorization", $"Bearer {apiKey}");

                var operation = webRequest.SendWebRequest();
                while (!operation.isDone)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        webRequest.Abort();
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    await Task.Yield();
                }

                if (webRequest.result == UnityWebRequest.Result.Success &&
                    webRequest.responseCode == 200 &&
                    !string.IsNullOrWhiteSpace(webRequest.downloadHandler?.text))
                {
                    return webRequest.downloadHandler.text;
                }

                return null;
            }
        }

        private static string BuildEndpoint(string baseUrl)
        {
            var normalized = NormalizeBaseUrl(baseUrl);
            if (normalized.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                return normalized;

            return $"{normalized}/chat/completions";
        }

        private static string BuildModelsEndpoint(string baseUrl)
        {
            var normalized = NormalizeBaseUrl(baseUrl);
            if (normalized.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(0, normalized.Length - "/chat/completions".Length);

            return $"{normalized}/models";
        }

        private static string NormalizeBaseUrl(string baseUrl)
        {
            return (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        }

        private static string ParseErrorMessage(UnityWebRequest webRequest)
        {
            string message = null;
            string type = null;
            string code = null;
            if (!string.IsNullOrEmpty(webRequest.downloadHandler?.text))
            {
                try
                {
                    var errorResponse = JsonUtility.FromJson<OpenAiErrorResponse>(webRequest.downloadHandler.text);
                    if (errorResponse?.error != null && !string.IsNullOrEmpty(errorResponse.error.message))
                    {
                        message = errorResponse.error.message;
                        type = errorResponse.error.type;
                        code = errorResponse.error.code;
                    }
                }
                catch { /* ignore */ }
            }

            if (string.IsNullOrWhiteSpace(message))
                message = webRequest.error ?? "Unknown error";

            var details = new List<string>();
            if (webRequest.responseCode > 0)
                details.Add("HTTP " + webRequest.responseCode);
            if (!string.IsNullOrWhiteSpace(type))
                details.Add(type);
            if (!string.IsNullOrWhiteSpace(code) &&
                !string.Equals(code, type, StringComparison.OrdinalIgnoreCase))
            {
                details.Add(code);
            }

            string requestId = webRequest.GetResponseHeader("x-request-id");
            if (!string.IsNullOrWhiteSpace(requestId))
                details.Add("request " + requestId);

            return details.Count == 0
                ? message
                : message + " (" + string.Join(", ", details.ToArray()) + ")";
        }

        private static void AppendMessagesJson(StringBuilder sb, List<AiChatMessage> messages)
        {
            if (messages == null || messages.Count == 0)
                return;

            for (int i = 0; i < messages.Count; i++)
            {
                if (i > 0)
                    sb.Append(',');

                AppendSingleMessageJson(sb, messages[i]);
            }
        }

        private static void AppendSingleMessageJson(StringBuilder sb, AiChatMessage message)
        {
            sb.Append('{');
            AppendJsonProperty(sb, "role", message?.role, isFirst: true);
            sb.Append(",\"content\":");

            bool hasAttachments = message?.attachments != null && message.attachments.Count > 0;
            bool hasToolCalls = message?.tool_calls != null && message.tool_calls.Count > 0;
            if (!hasAttachments)
            {
                if (hasToolCalls && string.IsNullOrEmpty(message?.content))
                    sb.Append("null");
                else
                    AppendJsonString(sb, message?.content ?? string.Empty);
                AppendToolMessageFieldsJson(sb, message);
                sb.Append('}');
                return;
            }

            sb.Append('[');
            bool appendedPart = false;
            if (!string.IsNullOrWhiteSpace(message.content))
            {
                sb.Append("{\"type\":\"text\",\"text\":");
                AppendJsonString(sb, message.content);
                sb.Append('}');
                appendedPart = true;
            }

            for (int i = 0; i < message.attachments.Count; i++)
            {
                var attachment = message.attachments[i];
                if (attachment == null)
                    continue;

                if (appendedPart)
                    sb.Append(',');

                string dataUrl = BuildImageDataUrl(attachment);
                sb.Append("{\"type\":\"image_url\",\"image_url\":{\"url\":");
                AppendJsonString(sb, dataUrl);
                sb.Append("}}");
                appendedPart = true;
            }

            sb.Append(']');
            AppendToolMessageFieldsJson(sb, message);
            sb.Append('}');
        }

        private static void AppendToolMessageFieldsJson(StringBuilder sb, AiChatMessage message)
        {
            if (message == null)
                return;

            if (!string.IsNullOrWhiteSpace(message.tool_call_id))
            {
                sb.Append(",\"tool_call_id\":");
                AppendJsonString(sb, message.tool_call_id);
            }

            if (message.tool_calls != null && message.tool_calls.Count > 0)
            {
                sb.Append(",\"tool_calls\":[");
                for (int i = 0; i < message.tool_calls.Count; i++)
                {
                    if (i > 0)
                        sb.Append(',');
                    AppendToolCallJson(sb, message.tool_calls[i]);
                }
                sb.Append(']');
            }
        }

        private static void AppendToolCallJson(StringBuilder sb, ToolCall toolCall)
        {
            sb.Append('{');
            AppendJsonProperty(sb, "id", toolCall?.id ?? string.Empty, isFirst: true);
            sb.Append(",\"type\":");
            AppendJsonString(sb, string.IsNullOrWhiteSpace(toolCall?.type) ? "function" : toolCall.type);
            sb.Append(",\"function\":{");
            AppendJsonProperty(sb, "name", toolCall?.function?.name ?? string.Empty, isFirst: true);
            sb.Append(",\"arguments\":");
            AppendJsonString(sb, toolCall?.function?.arguments ?? "{}");
            sb.Append("}}");
        }

        private static string BuildImageDataUrl(AiChatAttachment attachment)
        {
            if (attachment == null)
                throw new InvalidOperationException("Attachment payload is missing.");

            if (string.IsNullOrWhiteSpace(attachment.path))
                throw new InvalidOperationException($"Attachment \"{attachment.name ?? "image"}\" has no local path.");

            if (!File.Exists(attachment.path))
                throw new InvalidOperationException($"Image file not found: {attachment.path}");

            string mediaType = !string.IsNullOrWhiteSpace(attachment.mediaType)
                ? attachment.mediaType.Trim()
                : GuessImageMediaType(attachment.path);
            byte[] bytes = File.ReadAllBytes(attachment.path);
            return $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}";
        }

        private static void AppendJsonProperty(StringBuilder sb, string propertyName, string value, bool isFirst)
        {
            if (!isFirst)
                sb.Append(',');

            AppendJsonString(sb, propertyName);
            sb.Append(':');
            AppendJsonString(sb, value ?? string.Empty);
        }

        private static void AppendJsonString(StringBuilder sb, string value)
        {
            sb.Append('"');
            if (!string.IsNullOrEmpty(value))
            {
                for (int i = 0; i < value.Length; i++)
                {
                    char c = value[i];
                    switch (c)
                    {
                        case '\\': sb.Append("\\\\"); break;
                        case '"': sb.Append("\\\""); break;
                        case '\b': sb.Append("\\b"); break;
                        case '\f': sb.Append("\\f"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < 32)
                                sb.Append("\\u").Append(((int)c).ToString("x4"));
                            else
                                sb.Append(c);
                            break;
                    }
                }
            }

            sb.Append('"');
        }

        private static void AppendToolDefinitionJson(StringBuilder sb, ToolDefinition tool)
        {
            if (tool == null)
                return;

            sb.Append("{\"type\":\"function\",\"function\":{");
            AppendJsonProperty(sb, "name", tool.name, isFirst: true);
            sb.Append(",\"description\":");
            AppendJsonString(sb, tool.description ?? string.Empty);

            sb.Append(",\"parameters\":");
            AppendToolParametersJson(sb, tool.parameters);
            sb.Append("}}");
        }

        private static void AppendToolParametersJson(StringBuilder sb, ToolParameterSchema schema)
        {
            if (schema == null)
            {
                sb.Append("{\"type\":\"object\"}");
                return;
            }

            sb.Append('{');
            AppendJsonProperty(sb, "type", schema.type ?? "object", isFirst: true);

            if (schema.properties != null && schema.properties.Count > 0)
            {
                sb.Append(",\"properties\":{");
                bool firstProp = true;
                foreach (var kv in schema.properties)
                {
                    if (!firstProp)
                        sb.Append(',');
                    firstProp = false;
                    AppendJsonString(sb, kv.Key);
                    sb.Append(':');
                    AppendToolPropertyJson(sb, kv.Value);
                }
                sb.Append('}');
            }

            if (schema.required != null && schema.required.Count > 0)
            {
                sb.Append(",\"required\":[");
                for (int i = 0; i < schema.required.Count; i++)
                {
                    if (i > 0)
                        sb.Append(',');
                    AppendJsonString(sb, schema.required[i]);
                }
                sb.Append(']');
            }

            sb.Append('}');
        }

        private static void AppendToolPropertyJson(StringBuilder sb, ToolParameterProperty prop)
        {
            if (prop == null)
            {
                sb.Append("{\"type\":\"string\"}");
                return;
            }

            sb.Append('{');
            bool first = true;
            if (!string.IsNullOrEmpty(prop.type))
            {
                AppendJsonProperty(sb, "type", prop.type, isFirst: true);
                first = false;
            }
            if (!string.IsNullOrEmpty(prop.description))
            {
                if (!first)
                    sb.Append(',');
                sb.Append("\"description\":");
                AppendJsonString(sb, prop.description);
            }
            sb.Append('}');
        }

        private static string GuessImageMediaType(string path)
        {
            string extension = Path.GetExtension(path)?.ToLowerInvariant();
            if (extension == ".png")
                return "image/png";
            if (extension == ".jpg" || extension == ".jpeg")
                return "image/jpeg";
            if (extension == ".webp")
                return "image/webp";
            if (extension == ".gif")
                return "image/gif";
            if (extension == ".bmp")
                return "image/bmp";
            return "application/octet-stream";
        }

        private static string BuildChatCompletionPayloadJson(
            string model,
            float temperature,
            int maxTokens,
            List<AiChatMessage> messages,
            bool stream,
            ProviderCapabilities capabilities,
            List<ToolDefinition> tools)
        {
            bool omitTemperature = (capabilities != null) && capabilities.RequiresTemperatureOmission;
            bool useCompletionTokens = (capabilities != null) && capabilities.UsesMaxCompletionTokens;

            var sb = new StringBuilder(1024);
            sb.Append('{');
            AppendJsonProperty(sb, "model", model, isFirst: true);
            sb.Append(",\"messages\":[");
            AppendMessagesJson(sb, messages);
            sb.Append(']');

            if (stream)
            {
                sb.Append(",\"stream\":true");
                sb.Append(",\"stream_options\":{\"include_usage\":true}");
            }

            if (!omitTemperature)
                sb.Append(",\"temperature\":").Append(temperature.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));

            string tokenProperty = useCompletionTokens ? "max_completion_tokens" : "max_tokens";
            sb.Append(",\"").Append(tokenProperty).Append("\":").Append(maxTokens);

            if (capabilities != null && capabilities.SupportsFunctionTools && tools != null && tools.Count > 0)
            {
                sb.Append(",\"tools\":[");
                for (int i = 0; i < tools.Count; i++)
                {
                    if (i > 0)
                        sb.Append(',');
                    AppendToolDefinitionJson(sb, tools[i]);
                }
                sb.Append(']');
            }

            sb.Append('}');
            return sb.ToString();
        }

        private static AiChatResponse ParseResponse(string rawJson)
        {
            if (string.IsNullOrWhiteSpace(rawJson))
                return new AiChatResponse { content = string.Empty };

            // JsonUtility throws on null fields in some provider responses — try first, fall back to manual.
            try
            {
                var envelope = JsonUtility.FromJson<OpenAiResponseEnvelope>(rawJson);
                if (envelope?.choices != null && envelope.choices.Length > 0)
                {
                    var msg = envelope.choices[0]?.message;
                    if (msg != null)
                    {
                        string content = msg.content ?? string.Empty;
                        List<ToolCall> toolCalls = null;
                        if (msg.tool_calls != null && msg.tool_calls.Length > 0)
                        {
                            toolCalls = new List<ToolCall>();
                            for (int i = 0; i < msg.tool_calls.Length; i++)
                            {
                                var src = msg.tool_calls[i];
                                if (src == null)
                                    continue;

                                var tc = new ToolCall();
                                tc.id = src.id ?? string.Empty;
                                tc.type = string.IsNullOrEmpty(src.type) ? "function" : src.type;
                                if (src.function != null)
                                {
                                    tc.function = new ToolCallFunction();
                                    tc.function.name = src.function.name ?? string.Empty;
                                    tc.function.arguments = src.function.arguments ?? string.Empty;
                                }
                                toolCalls.Add(tc);
                            }
                        }

                        List<AiChatAttachment> attachments = null;
                        if (rawJson.IndexOf("\"content\":[", StringComparison.Ordinal) >= 0)
                        {
                            var arrRes = ExtractContentArray(rawJson, 0);
                            if (!string.IsNullOrEmpty(arrRes.TextContent))
                                content = arrRes.TextContent;
                            if (arrRes.Attachments != null && arrRes.Attachments.Count > 0)
                                attachments = arrRes.Attachments;
                        }

                        if (!string.IsNullOrEmpty(content) || (toolCalls != null && toolCalls.Count > 0) || (attachments != null && attachments.Count > 0))
                        {
                            return new AiChatResponse
                            {
                                id = envelope.id ?? string.Empty,
                                model = envelope.model ?? string.Empty,
                                content = content,
                                tool_calls = toolCalls,
                                attachments = attachments,
                                receivedAtUtc = DateTime.UtcNow
                            };
                        }
                    }
                }
            }
            catch { }

            // Manual fallback: handles both compact and spaced JSON. Also extracts tool_calls if present.
            int choicesIdx = rawJson.IndexOf("\"choices\"", StringComparison.Ordinal);
            string contentManual = ExtractJsonStringValue(rawJson, "content", choicesIdx >= 0 ? choicesIdx : 0) ?? string.Empty;
            List<AiChatAttachment> attachmentsManual = null;
            if (rawJson.IndexOf("\"content\":[", StringComparison.Ordinal) >= 0)
            {
                var arrRes = ExtractContentArray(rawJson, choicesIdx >= 0 ? choicesIdx : 0);
                contentManual = arrRes.TextContent ?? string.Empty;
                if (arrRes.Attachments != null && arrRes.Attachments.Count > 0)
                    attachmentsManual = arrRes.Attachments;
            }
            List<ToolCall> manualToolCalls = ExtractToolCalls(rawJson, choicesIdx >= 0 ? choicesIdx : 0);
            return new AiChatResponse
            {
                content = contentManual,
                tool_calls = manualToolCalls,
                attachments = attachmentsManual,
                receivedAtUtc = DateTime.UtcNow
            };
        }

        private static List<ToolCall> ExtractToolCalls(string json, int startFrom)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            int toolCallsIdx = json.IndexOf("\"tool_calls\"", startFrom, StringComparison.Ordinal);
            if (toolCallsIdx < 0)
                return null;

            // Find the array start [
            int arrayStart = json.IndexOf('[', toolCallsIdx);
            if (arrayStart < 0)
                return null;

            // Find matching ]
            int depth = 0;
            int arrayEnd = -1;
            bool inString = false;
            for (int i = arrayStart; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"' && (i == 0 || json[i - 1] != '\\'))
                {
                    inString = !inString;
                    continue;
                }
                if (inString)
                    continue;

                if (c == '[')
                    depth++;
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        arrayEnd = i;
                        break;
                    }
                }
            }

            if (arrayEnd < 0)
                return null;

            string arrayJson = json.Substring(arrayStart, arrayEnd - arrayStart + 1);
            var result = new List<ToolCall>();

            // Very simple parser for array of objects: look for "id", "name", "arguments" in sequence
            int pos = 0;
            while (pos < arrayJson.Length)
            {
                string idVal = ExtractJsonStringValue(arrayJson, "id", pos);
                if (string.IsNullOrEmpty(idVal))
                    break;

                // find name after this id
                int nameStart = arrayJson.IndexOf("\"name\"", pos, StringComparison.Ordinal);
                string nameVal = null;
                if (nameStart >= 0)
                    nameVal = ExtractJsonStringValue(arrayJson, "name", nameStart);

                int argsStart = arrayJson.IndexOf("\"arguments\"", pos, StringComparison.Ordinal);
                string argsVal = null;
                if (argsStart >= 0)
                    argsVal = ExtractJsonStringValue(arrayJson, "arguments", argsStart);

                var tc = new ToolCall();
                tc.id = idVal;
                tc.type = "function";
                tc.function = new ToolCallFunction();
                tc.function.name = nameVal ?? string.Empty;
                tc.function.arguments = argsVal ?? string.Empty;
                result.Add(tc);

                // advance pos past this object roughly
                int nextObj = arrayJson.IndexOf('}', pos);
                if (nextObj < 0)
                    break;
                pos = nextObj + 1;
            }

            return result.Count > 0 ? result : null;
        }

        private static ContentArrayParseResult ExtractContentArray(string json, int startFrom)
        {
            var result = new ContentArrayParseResult
            {
                TextContent = string.Empty,
                Attachments = new List<AiChatAttachment>()
            };

            if (string.IsNullOrWhiteSpace(json))
                return result;

            string keyMarker = "\"content\"";
            int pos = startFrom;
            int keyIdx = json.IndexOf(keyMarker, pos, StringComparison.Ordinal);
            if (keyIdx < 0)
                return result;

            int p = keyIdx + keyMarker.Length;
            while (p < json.Length && (json[p] == ' ' || json[p] == '\t' || json[p] == '\n' || json[p] == '\r'))
                p++;
            if (p >= json.Length || json[p] != ':')
                return result;
            p++;
            while (p < json.Length && (json[p] == ' ' || json[p] == '\t' || json[p] == '\n' || json[p] == '\r'))
                p++;
            if (p >= json.Length || json[p] != '[')
                return result;

            int arrayStart = p;
            int depth = 0;
            bool inString = false;
            int arrayEnd = -1;
            for (int i = arrayStart; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"' && (i == 0 || json[i - 1] != '\\'))
                {
                    inString = !inString;
                    continue;
                }
                if (inString)
                    continue;
                if (c == '[')
                    depth++;
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        arrayEnd = i;
                        break;
                    }
                }
            }
            if (arrayEnd < 0)
                return result;

            string arrayJson = json.Substring(arrayStart, arrayEnd - arrayStart + 1);

            var contentItems = new List<string>();
            int idx = 1;
            while (idx < arrayJson.Length - 1)
            {
                while (idx < arrayJson.Length && (arrayJson[idx] == ' ' || arrayJson[idx] == '\t' || arrayJson[idx] == '\n' || arrayJson[idx] == '\r' || arrayJson[idx] == ','))
                    idx++;
                if (idx >= arrayJson.Length || arrayJson[idx] != '{')
                    break;

                int objStart = idx;
                int objDepth = 0;
                inString = false;
                int objEnd = -1;
                for (int j = objStart; j < arrayJson.Length; j++)
                {
                    char c = arrayJson[j];
                    if (c == '"' && (j == 0 || arrayJson[j - 1] != '\\'))
                    {
                        inString = !inString;
                        continue;
                    }
                    if (inString)
                        continue;
                    if (c == '{')
                        objDepth++;
                    else if (c == '}')
                    {
                        objDepth--;
                        if (objDepth == 0)
                        {
                            objEnd = j;
                            break;
                        }
                    }
                }
                if (objEnd < 0)
                    break;

                string itemJson = arrayJson.Substring(objStart, objEnd - objStart + 1);
                contentItems.Add(itemJson);
                idx = objEnd + 1;
            }

            var textParts = new List<string>();
            for (int i = 0; i < contentItems.Count; i++)
            {
                string item = contentItems[i];
                string type = ExtractJsonStringValue(item, "type", 0);
                if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
                {
                    string txt = ExtractJsonStringValue(item, "text", 0);
                    if (!string.IsNullOrEmpty(txt))
                        textParts.Add(txt);
                }
                else if (string.Equals(type, "image_url", StringComparison.OrdinalIgnoreCase))
                {
                    int imgUrlKey = item.IndexOf("\"image_url\"", StringComparison.Ordinal);
                    if (imgUrlKey >= 0)
                    {
                        string url = ExtractJsonStringValue(item, "url", imgUrlKey);
                        if (!string.IsNullOrEmpty(url))
                        {
                            string attName = DeriveNameFromUrl(url);
                            string cleanForExt = CleanUrlForExtension(url);
                            string media = GuessImageMediaType(cleanForExt);
                            var att = new AiChatAttachment
                            {
                                kind = "image",
                                name = attName,
                                path = url,
                                mediaType = media
                            };
                            result.Attachments.Add(att);
                        }
                    }
                }
            }

            if (textParts.Count > 0)
                result.TextContent = string.Join("\n\n", textParts);

            return result;
        }

        private static string CleanUrlForExtension(string url)
        {
            if (string.IsNullOrEmpty(url))
                return string.Empty;
            string s = url;
            int q = s.IndexOf('?');
            if (q >= 0)
                s = s.Substring(0, q);
            int h = s.IndexOf('#');
            if (h >= 0)
                s = s.Substring(0, h);
            return s;
        }

        private static string DeriveNameFromUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
                return "image.png";
            try
            {
                string clean = CleanUrlForExtension(url);
                string fname = Path.GetFileName(clean);
                if (!string.IsNullOrEmpty(fname) && fname.IndexOf('.') > 0)
                    return fname;
            }
            catch { }
            return "generated_image.png";
        }

        private struct ContentArrayParseResult
        {
            public string TextContent;
            public List<AiChatAttachment> Attachments;
        }

        private Task<RequestRoutingInfo> ResolveRequestRoutingAsync(
            ProviderConfig provider,
            AiChatRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new RequestRoutingInfo
            {
                Model = request.model,
                ProviderSessionId = request?.providerSessionId?.Trim()
            });
        }

        private static string ExtractJsonStringValue(string json, string key, int startFrom)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key))
                return null;

            string keyMarker = $"\"{key}\"";
            int pos = startFrom;
            while (pos < json.Length)
            {
                int keyIdx = json.IndexOf(keyMarker, pos, StringComparison.Ordinal);
                if (keyIdx < 0) return null;

                int p = keyIdx + keyMarker.Length;
                while (p < json.Length && (json[p] == ' ' || json[p] == '\t')) p++;
                if (p >= json.Length || json[p] != ':') { pos = keyIdx + keyMarker.Length; continue; }
                p++;
                while (p < json.Length && (json[p] == ' ' || json[p] == '\t')) p++;
                if (p >= json.Length || json[p] != '"') { pos = keyIdx + keyMarker.Length; continue; }
                p++;

                var sb = new StringBuilder();
                while (p < json.Length && json[p] != '"')
                {
                    if (json[p] == '\\' && p + 1 < json.Length)
                    {
                        AppendEscapedJsonCharacter(json, ref p, sb, preserveUnknownEscape: false);
                    }
                    else sb.Append(json[p]);
                    p++;
                }
                return sb.ToString();
            }
            return null;
        }

        private static int ExtractJsonIntValue(string json, string key, int startFrom)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key))
                return -1;

            string keyMarker = $"\"{key}\"";
            int pos = Math.Max(0, startFrom);
            while (pos < json.Length)
            {
                int keyIdx = json.IndexOf(keyMarker, pos, StringComparison.Ordinal);
                if (keyIdx < 0)
                    return -1;

                int p = keyIdx + keyMarker.Length;
                while (p < json.Length && char.IsWhiteSpace(json[p]))
                    p++;
                if (p >= json.Length || json[p] != ':')
                {
                    pos = keyIdx + keyMarker.Length;
                    continue;
                }

                p++;
                while (p < json.Length && char.IsWhiteSpace(json[p]))
                    p++;

                int start = p;
                while (p < json.Length && char.IsDigit(json[p]))
                    p++;

                if (p <= start)
                {
                    pos = keyIdx + keyMarker.Length;
                    continue;
                }

                int value;
                if (int.TryParse(json.Substring(start, p - start), out value))
                    return value;

                return -1;
            }

            return -1;
        }

        public async Task<AiChatResponse> SendMessageStreamAsync(
            ProviderConfig provider,
            AiChatRequest request,
            Action<string> onToken,
            CancellationToken cancellationToken = default,
            Action<ToolProgressInfo> onToolProgress = null)
        {
            ProviderValidator.Validate(provider);

            if (request == null)
                throw new ArgumentNullException(nameof(request));

            _lastStreamUsage = default;

            var adapter = GetAdapter(provider);
            var capabilities = adapter.GetCapabilities();

            if (capabilities != null && capabilities.ForceNonStreaming)
            {
                var fallbackResponse = await SendMessageAsync(provider, request, cancellationToken);
                if (!string.IsNullOrWhiteSpace(fallbackResponse?.content))
                    onToken?.Invoke(fallbackResponse.content);
                return fallbackResponse ?? new AiChatResponse { content = string.Empty };
            }

            var messages = new List<AiChatMessage>(request.messages ?? new List<AiChatMessage>());

            if (!string.IsNullOrWhiteSpace(request.systemPrompt))
            {
                messages.Insert(0, new AiChatMessage
                {
                    role = "system",
                    content = request.systemPrompt
                });
            }

            var routing = await ResolveRequestRoutingAsync(provider, request, cancellationToken);
            var endpoint = BuildEndpoint(provider.baseUrl);
            var payloadJson = BuildChatCompletionPayloadJson(
                routing.Model,
                request.temperature,
                request.maxTokens,
                messages,
                stream: true,
                capabilities,
                request.tools);

            using (var webRequest = new UnityWebRequest(endpoint, UnityWebRequest.kHttpVerbPOST))
            {
                var bodyRaw = Encoding.UTF8.GetBytes(payloadJson);
                webRequest.uploadHandler = new UploadHandlerRaw(bodyRaw);
                webRequest.downloadHandler = new DownloadHandlerBuffer();
                webRequest.SetRequestHeader("Content-Type", "application/json");

                if (!string.IsNullOrWhiteSpace(provider.apiKey))
                    webRequest.SetRequestHeader("Authorization", $"Bearer {provider.apiKey}");
                adapter.ApplyRequestHeaders(webRequest, routing.ProviderSessionId);

                var operation = webRequest.SendWebRequest();
                int lastProcessed = 0;
                bool emittedAnyToken = false;
                var collected = new StringBuilder();
                var toolCallAccumulator = new StreamingToolCallAccumulator();
                var sseState = new SseParseState();
                Action<string> emitToken = token =>
                {
                    if (string.IsNullOrEmpty(token))
                        return;

                    emittedAnyToken = true;
                    collected.Append(token);
                    onToken?.Invoke(token);
                };

                while (!operation.isDone)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        webRequest.Abort();
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    lastProcessed = ParseSseText(webRequest.downloadHandler.text, lastProcessed, emitToken, flushPartialLine: false, onToolProgress: onToolProgress, toolCallAccumulator: toolCallAccumulator, state: sseState);

                    await Task.Yield();
                }

                // Drain any data that arrived after the last yield
                string finalStreamingText = webRequest.downloadHandler?.text ?? string.Empty;
                ParseSseText(finalStreamingText, lastProcessed, emitToken, flushPartialLine: true, onToolProgress: onToolProgress, toolCallAccumulator: toolCallAccumulator, state: sseState);

                if (webRequest.result != UnityWebRequest.Result.Success)
                {
                    throw new InvalidOperationException($"Streaming request failed: {ParseErrorMessage(webRequest)}");
                }

                string responseProviderSessionId = adapter.ExtractSessionId(webRequest, routing.ProviderSessionId);

                // Some providers ignore `stream=true` and return a normal JSON completion.
                AiChatResponse fallbackResponse = null;
                if (!emittedAnyToken && !toolCallAccumulator.HasToolCalls)
                {
                    var fallback = ExtractContentFromStreamingPayload(finalStreamingText);
                    if (string.IsNullOrWhiteSpace(fallback))
                    {
                        fallbackResponse = ParseResponse(finalStreamingText);
                        fallback = fallbackResponse?.content;
                    }
                    if (!string.IsNullOrWhiteSpace(fallback))
                    {
                        emittedAnyToken = true;
                        collected.Append(fallback);
                        onToken?.Invoke(fallback);
                    }

                    if (!string.IsNullOrWhiteSpace(fallbackResponse?.providerSessionId))
                        responseProviderSessionId = fallbackResponse.providerSessionId;
                }

                if (!emittedAnyToken && !toolCallAccumulator.HasToolCalls)
                {
                    throw new InvalidOperationException("Streaming response contained no tokens. Check provider endpoint, model id, and streaming compatibility.");
                }

                var streamFinal = new AiChatResponse
                {
                    model = routing.Model ?? request.model ?? string.Empty,
                    providerSessionId = responseProviderSessionId,
                    content = collected.ToString(),
                    receivedAtUtc = DateTime.UtcNow
                };

                if (toolCallAccumulator.HasToolCalls)
                {
                    streamFinal.tool_calls = toolCallAccumulator.ToToolCalls();
                }

                if (streamFinal.tool_calls == null || streamFinal.tool_calls.Count == 0)
                {
                    var parsedForTools = ParseResponse(finalStreamingText);
                    if (parsedForTools != null && parsedForTools.tool_calls != null && parsedForTools.tool_calls.Count > 0)
                        streamFinal.tool_calls = parsedForTools.tool_calls;
                }

                if (streamFinal.attachments == null || streamFinal.attachments.Count == 0)
                {
                    if (fallbackResponse != null && fallbackResponse.attachments != null && fallbackResponse.attachments.Count > 0)
                    {
                        streamFinal.attachments = fallbackResponse.attachments;
                    }
                    else
                    {
                        var parsedForAtts = ParseResponse(finalStreamingText);
                        if (parsedForAtts != null && parsedForAtts.attachments != null && parsedForAtts.attachments.Count > 0)
                            streamFinal.attachments = parsedForAtts.attachments;
                    }
                }

                return streamFinal;
            }
        }

        private static bool TryExtractNamedArray(string json, string propertyName, out string arrayJson)
        {
            arrayJson = null;
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(propertyName))
                return false;

            int propertyIdx = json.IndexOf($"\"{propertyName}\"", StringComparison.OrdinalIgnoreCase);
            if (propertyIdx < 0)
                return false;

            int colonIdx = json.IndexOf(':', propertyIdx + propertyName.Length + 2);
            if (colonIdx < 0)
                return false;

            int arrayStart = SkipWhitespace(json, colonIdx + 1);
            if (arrayStart >= json.Length || json[arrayStart] != '[')
                return false;

            int depth = 0;
            bool inString = false;
            for (int i = arrayStart; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"' && (i == 0 || json[i - 1] != '\\'))
                {
                    inString = !inString;
                    continue;
                }

                if (inString)
                    continue;

                if (c == '[')
                    depth++;
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        arrayJson = json.Substring(arrayStart, i - arrayStart + 1);
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryExtractNamedArray(
            string json,
            string propertyName,
            int searchStart,
            out string arrayJson,
            out int nextSearchStart)
        {
            arrayJson = null;
            nextSearchStart = searchStart;
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(propertyName))
                return false;

            int propertyIdx = json.IndexOf($"\"{propertyName}\"", Math.Max(0, searchStart), StringComparison.OrdinalIgnoreCase);
            if (propertyIdx < 0)
                return false;

            int colonIdx = json.IndexOf(':', propertyIdx + propertyName.Length + 2);
            if (colonIdx < 0)
                return false;

            int arrayStart = SkipWhitespace(json, colonIdx + 1);
            if (arrayStart >= json.Length || json[arrayStart] != '[')
            {
                nextSearchStart = colonIdx + 1;
                return false;
            }

            int depth = 0;
            bool inString = false;
            for (int i = arrayStart; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"' && (i == 0 || json[i - 1] != '\\'))
                {
                    inString = !inString;
                    continue;
                }

                if (inString)
                    continue;

                if (c == '[')
                    depth++;
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        arrayJson = json.Substring(arrayStart, i - arrayStart + 1);
                        nextSearchStart = i + 1;
                        return true;
                    }
                }
            }

            nextSearchStart = json.Length;
            return false;
        }

        private static void CollectJsonStringArrayValues(
            string jsonArray,
            List<string> values,
            HashSet<string> seen)
        {
            if (string.IsNullOrEmpty(jsonArray) || values == null || seen == null)
                return;

            int pos = 0;
            while (pos < jsonArray.Length)
            {
                int quoteIdx = jsonArray.IndexOf('"', pos);
                if (quoteIdx < 0)
                    break;

                if (TryReadJsonString(jsonArray, quoteIdx, out string value, out int nextPos) &&
                    !string.IsNullOrWhiteSpace(value) &&
                    seen.Add(value))
                {
                    values.Add(value);
                }

                pos = nextPos > quoteIdx ? nextPos : quoteIdx + 1;
            }
        }

        private static void CollectJsonStringPropertyValues(
            string json,
            string propertyName,
            List<string> values,
            HashSet<string> seen)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(propertyName))
                return;

            int pos = 0;
            while (pos < json.Length)
            {
                int keyIdx = json.IndexOf($"\"{propertyName}\"", pos, StringComparison.OrdinalIgnoreCase);
                if (keyIdx < 0)
                    break;

                int colonIdx = json.IndexOf(':', keyIdx + propertyName.Length + 2);
                if (colonIdx < 0)
                    break;

                int valueStart = SkipWhitespace(json, colonIdx + 1);
                if (valueStart >= json.Length || json[valueStart] != '"')
                {
                    pos = colonIdx + 1;
                    continue;
                }

                if (TryReadJsonString(json, valueStart, out string value, out int nextPos) &&
                    !string.IsNullOrWhiteSpace(value) &&
                    seen.Add(value))
                {
                    values.Add(value);
                }

                pos = nextPos > valueStart ? nextPos : valueStart + 1;
            }
        }

        private static int SkipWhitespace(string text, int index)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
                index++;

            return index;
        }

        private static bool TryReadJsonString(string json, int quoteIndex, out string value, out int nextPos)
        {
            value = null;
            nextPos = quoteIndex;

            if (quoteIndex < 0 || quoteIndex >= json.Length || json[quoteIndex] != '"')
                return false;

            int start = quoteIndex + 1;
            var sb = new StringBuilder();
            for (int i = start; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '\\' && i + 1 < json.Length)
                {
                    AppendEscapedJsonCharacter(json, ref i, sb, preserveUnknownEscape: false);
                    continue;
                }

                if (c == '"')
                {
                    value = sb.ToString();
                    nextPos = i + 1;
                    return true;
                }

                sb.Append(c);
            }

            nextPos = json.Length;
            return false;
        }

        private IProviderAdapter GetAdapter(ProviderConfig provider)
        {
            return ProviderAdapterFactory.Create(provider != null ? provider.backendType : null);
        }

        [Serializable]
        private class ChatCompletionRequest
        {
            public string model;
            public List<AiChatMessage> messages;
            public bool stream;
            public float? temperature;
            public int? max_tokens;
            public int? max_completion_tokens;
        }

        private sealed class RequestRoutingInfo
        {
            public string Model;
            public string ProviderSessionId;
        }

        private sealed class SseParseState
        {
            public string PendingEventType;
        }

        private sealed class StreamingToolCallAccumulator
        {
            private sealed class ToolCallState
            {
                public string Id;
                public string Type = "function";
                public string Name;
                public StringBuilder Arguments = new StringBuilder();
                public bool EmittedRequest;
            }

            private readonly List<ToolCallState> _states = new List<ToolCallState>();

            public bool HasToolCalls
            {
                get
                {
                    for (int i = 0; i < _states.Count; i++)
                    {
                        if (!string.IsNullOrWhiteSpace(_states[i].Name))
                            return true;
                    }

                    return false;
                }
            }

            public void ProcessPayload(string payload, Action<ToolProgressInfo> onToolProgress)
            {
                if (string.IsNullOrWhiteSpace(payload))
                    return;

                if (payload.IndexOf("\"tool_calls\"", StringComparison.Ordinal) >= 0 &&
                    TryExtractNamedArray(payload, "tool_calls", out string arrayJson))
                {
                    int pos = 0;
                    while (TryReadNextObject(arrayJson, pos, out string itemJson, out int nextPos))
                    {
                        ApplyToolCallDelta(itemJson, onToolProgress);
                        pos = nextPos;
                    }
                }
                else if (payload.IndexOf("\"function_call\"", StringComparison.Ordinal) >= 0)
                {
                    ApplyLegacyFunctionCallDelta(payload, onToolProgress);
                }
            }

            public List<ToolCall> ToToolCalls()
            {
                var result = new List<ToolCall>();
                for (int i = 0; i < _states.Count; i++)
                {
                    var state = _states[i];
                    if (state == null || string.IsNullOrWhiteSpace(state.Name))
                        continue;

                    var call = new ToolCall();
                    call.id = string.IsNullOrWhiteSpace(state.Id) ? "call_" + i.ToString() : state.Id;
                    call.type = string.IsNullOrWhiteSpace(state.Type) ? "function" : state.Type;
                    call.function = new ToolCallFunction();
                    call.function.name = state.Name;
                    call.function.arguments = state.Arguments != null ? state.Arguments.ToString() : string.Empty;
                    result.Add(call);
                }

                return result;
            }

            private void ApplyToolCallDelta(string itemJson, Action<ToolProgressInfo> onToolProgress)
            {
                int index = ExtractJsonIntValue(itemJson, "index", 0);
                if (index < 0)
                    index = 0;

                var state = GetState(index);
                string id = ExtractJsonStringValue(itemJson, "id", 0);
                if (!string.IsNullOrEmpty(id))
                    state.Id = id;

                string type = ExtractJsonStringValue(itemJson, "type", 0);
                if (!string.IsNullOrEmpty(type))
                    state.Type = type;

                int functionIdx = itemJson.IndexOf("\"function\"", StringComparison.Ordinal);
                int searchStart = functionIdx >= 0 ? functionIdx : 0;

                string name = ExtractJsonStringValue(itemJson, "name", searchStart);
                if (!string.IsNullOrEmpty(name))
                    state.Name = name;

                string argsDelta = ExtractJsonStringValue(itemJson, "arguments", searchStart);
                if (!string.IsNullOrEmpty(argsDelta))
                    state.Arguments.Append(argsDelta);

                EmitRequestOnce(state, onToolProgress);
            }

            private void ApplyLegacyFunctionCallDelta(string payload, Action<ToolProgressInfo> onToolProgress)
            {
                var state = GetState(0);
                int functionIdx = payload.IndexOf("\"function_call\"", StringComparison.Ordinal);
                int searchStart = functionIdx >= 0 ? functionIdx : 0;

                string name = ExtractJsonStringValue(payload, "name", searchStart);
                if (!string.IsNullOrEmpty(name))
                    state.Name = name;

                string argsDelta = ExtractJsonStringValue(payload, "arguments", searchStart);
                if (!string.IsNullOrEmpty(argsDelta))
                    state.Arguments.Append(argsDelta);

                EmitRequestOnce(state, onToolProgress);
            }

            private ToolCallState GetState(int index)
            {
                while (_states.Count <= index)
                    _states.Add(new ToolCallState());

                return _states[index];
            }

            private static void EmitRequestOnce(ToolCallState state, Action<ToolProgressInfo> onToolProgress)
            {
                if (state == null || state.EmittedRequest || string.IsNullOrWhiteSpace(state.Name) || onToolProgress == null)
                    return;

                state.EmittedRequest = true;
                onToolProgress.Invoke(ToolProgressInfo.Create(state.Name, state.Name, "\uD83D\uDD27", "requesting"));
            }

            private static bool TryReadNextObject(string json, int start, out string objectJson, out int nextPos)
            {
                objectJson = null;
                nextPos = start;
                if (string.IsNullOrEmpty(json))
                    return false;

                int objectStart = json.IndexOf('{', Math.Max(0, start));
                if (objectStart < 0)
                    return false;

                int depth = 0;
                bool inString = false;
                for (int i = objectStart; i < json.Length; i++)
                {
                    char c = json[i];
                    if (c == '"' && (i == 0 || json[i - 1] != '\\'))
                    {
                        inString = !inString;
                        continue;
                    }

                    if (inString)
                        continue;

                    if (c == '{')
                        depth++;
                    else if (c == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            objectJson = json.Substring(objectStart, i - objectStart + 1);
                            nextPos = i + 1;
                            return true;
                        }
                    }
                }

                nextPos = json.Length;
                return false;
            }
        }

        private static int ParseSseText(
            string text,
            int offset,
            Action<string> onToken,
            bool flushPartialLine,
            Action<ToolProgressInfo> onToolProgress = null,
            StreamingToolCallAccumulator toolCallAccumulator = null,
            SseParseState state = null)
        {
            if (string.IsNullOrEmpty(text) || offset >= text.Length)
                return Math.Max(0, offset);

            int searchFrom = offset;
            string pendingEventType = state != null ? state.PendingEventType : null;

            while (searchFrom < text.Length)
            {
                int lineEnd = FindLineEnd(text, searchFrom, out int nextLineStart);
                if (lineEnd < 0)
                {
                    if (!flushPartialLine)
                        break;

                    lineEnd = text.Length;
                    nextLineStart = text.Length;
                }

                string line = text.Substring(searchFrom, lineEnd - searchFrom).Trim();
                searchFrom = nextLineStart;

                if (string.IsNullOrEmpty(line))
                {
                    // SSE event boundary — reset pending event type
                    pendingEventType = null;
                    if (state != null)
                        state.PendingEventType = null;
                    continue;
                }

                // Track custom SSE event types
                if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
                {
                    pendingEventType = line.Length > 6 ? line.Substring(6).Trim() : string.Empty;
                    if (state != null)
                        state.PendingEventType = pendingEventType;
                    continue;
                }

                if (!TryExtractSsePayload(line, out string payload))
                    continue;

                if (payload == "[DONE]")
                    break;

                // Extract usage data from final chunk
                string usageChunk = ExtractUsagePayload(payload);
                if (usageChunk != null)
                {
                    _lastStreamUsage = ParseUsage(usageChunk);
                }

                if (string.IsNullOrWhiteSpace(payload))
                    continue;

                // Handle provider tool progress events
                if (onToolProgress != null &&
                    string.Equals(pendingEventType, "hermes.tool.progress", StringComparison.OrdinalIgnoreCase))
                {
                    ParseAndEmitToolProgress(payload, onToolProgress);
                    pendingEventType = null;
                    if (state != null)
                        state.PendingEventType = null;
                    continue;
                }

                // Part B: tool call request detection for approval flow (Hermes + native OpenAI tool_calls)
                if (onToolProgress != null)
                {
                    if (string.Equals(pendingEventType, "hermes.tool.request", StringComparison.OrdinalIgnoreCase))
                    {
                        ParseAndEmitToolRequest(payload, onToolProgress);
                        pendingEventType = null;
                        if (state != null)
                            state.PendingEventType = null;
                        continue;
                    }

                    // Minimal fallback detection of tool_calls in SSE chunks. When an
                    // accumulator is present it emits the request once, so avoid
                    // surfacing the same tool call twice.
                    if (toolCallAccumulator == null)
                        TryDetectAndEmitToolCallRequest(payload, onToolProgress);
                }

                if (toolCallAccumulator != null)
                    toolCallAccumulator.ProcessPayload(payload, onToolProgress);

                string delta = ExtractDeltaContent(payload);
                if (!string.IsNullOrEmpty(delta))
                    onToken?.Invoke(delta);
            }

            return searchFrom;
        }

        private static void ParseAndEmitToolProgress(string json, Action<ToolProgressInfo> onToolProgress)
        {
            if (string.IsNullOrWhiteSpace(json) || onToolProgress == null)
                return;

            string tool = ExtractJsonStringValue(json, "tool", 0) ?? string.Empty;
            string emoji = ExtractJsonStringValue(json, "emoji", 0) ?? string.Empty;
            string label = ExtractJsonStringValue(json, "label", 0) ?? ExtractJsonStringValue(json, "description", 0) ?? string.Empty;
            string status = ExtractJsonStringValue(json, "status", 0) ?? string.Empty;
            if (string.IsNullOrEmpty(status) && LooksLikeApprovalText(label))
                status = "approval_required";

            onToolProgress.Invoke(ToolProgressInfo.Create(tool, label, emoji, status));
        }

        // Part B: emit tool request as progress with "requesting" status so ChatController can trigger approval UI
        private static void ParseAndEmitToolRequest(string json, Action<ToolProgressInfo> onToolProgress)
        {
            if (string.IsNullOrWhiteSpace(json) || onToolProgress == null)
                return;

            string tool = ExtractJsonStringValue(json, "tool", 0) ?? ExtractJsonStringValue(json, "name", 0) ?? string.Empty;
            string emoji = ExtractJsonStringValue(json, "emoji", 0) ?? "\uD83D\uDD27"; // 🔧
            string label = ExtractJsonStringValue(json, "label", 0) ?? ExtractJsonStringValue(json, "description", 0) ?? tool;

            onToolProgress.Invoke(ToolProgressInfo.Create(tool, label, emoji, "approval_required"));
        }

        private static bool LooksLikeApprovalText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            string lower = value.ToLowerInvariant();
            return lower.IndexOf("approval", StringComparison.Ordinal) >= 0 ||
                   lower.IndexOf("approve", StringComparison.Ordinal) >= 0 ||
                   lower.IndexOf("confirm", StringComparison.Ordinal) >= 0 ||
                   lower.IndexOf("permission", StringComparison.Ordinal) >= 0 ||
                   lower.IndexOf("разреш", StringComparison.Ordinal) >= 0 ||
                   lower.IndexOf("подтверж", StringComparison.Ordinal) >= 0;
        }

        // Minimal OpenAI tool_calls detection (no full delta accumulation; enough to surface approval prompt)
        private static bool TryDetectAndEmitToolCallRequest(string json, Action<ToolProgressInfo> onToolProgress)
        {
            if (string.IsNullOrWhiteSpace(json) || onToolProgress == null)
                return false;

            if (json.IndexOf("\"tool_calls\"", StringComparison.Ordinal) < 0 &&
                json.IndexOf("\"function_call\"", StringComparison.Ordinal) < 0)
                return false;

            string toolName = null;
            int namePos = json.IndexOf("\"name\"", StringComparison.Ordinal);
            if (namePos >= 0)
            {
                toolName = ExtractJsonStringValue(json, "name", namePos);
            }
            if (string.IsNullOrEmpty(toolName))
            {
                toolName = ExtractJsonStringValue(json, "tool", 0);
            }

            if (!string.IsNullOrEmpty(toolName))
            {
                string label = ExtractJsonStringValue(json, "label", 0) ?? toolName;
                string emoji = ExtractJsonStringValue(json, "emoji", 0) ?? "\uD83D\uDD27";
                onToolProgress.Invoke(ToolProgressInfo.Create(toolName, label, emoji, "requesting"));
                return true;
            }

            return false;
        }

        private static string ExtractContentFromStreamingPayload(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var sb = new StringBuilder();
            ParseSseText(text, 0, token => sb.Append(token), flushPartialLine: true);
            if (sb.Length > 0)
                return sb.ToString();

            return null;
        }

        private static int FindLineEnd(string text, int startIndex, out int nextLineStart)
        {
            for (int i = startIndex; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r')
                {
                    nextLineStart = i + 1;
                    if (nextLineStart < text.Length && text[nextLineStart] == '\n')
                        nextLineStart++;

                    return i;
                }

                if (c == '\n')
                {
                    nextLineStart = i + 1;
                    return i;
                }
            }

            nextLineStart = text.Length;
            return -1;
        }

        private static bool TryExtractSsePayload(string line, out string payload)
        {
            payload = null;
            if (string.IsNullOrWhiteSpace(line))
                return false;

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return false;

            payload = line.Length > 5 ? line.Substring(5).TrimStart() : string.Empty;
            return true;
        }

        private static string ExtractDeltaContent(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            int choicesIdx = json.IndexOf("\"choices\"", StringComparison.Ordinal);
            return ExtractJsonStringValue(json, "content", choicesIdx >= 0 ? choicesIdx : 0);
        }

        private static string ExtractUsagePayload(string json)
        {
            // Usage is in the top-level "usage" field of the final chunk
            int usageIdx = json.IndexOf("\"usage\"", StringComparison.Ordinal);
            if (usageIdx < 0)
                return null;
            // Extract the usage object
            int colonIdx = json.IndexOf(':', usageIdx + 7);
            if (colonIdx < 0)
                return null;
            int braceStart = json.IndexOf('{', colonIdx);
            if (braceStart < 0)
                return null;
            int depth = 0;
            for (int i = braceStart; i < json.Length; i++)
            {
                if (json[i] == '{') depth++;
                else if (json[i] == '}') depth--;
                if (depth == 0)
                    return json.Substring(braceStart, i - braceStart + 1);
            }
            return null;
        }

        private static UsageData ParseUsage(string json)
        {
            var usage = new UsageData();
            usage.prompt_tokens = ExtractJsonIntValue(json, "prompt_tokens");
            usage.completion_tokens = ExtractJsonIntValue(json, "completion_tokens");
            usage.total_tokens = ExtractJsonIntValue(json, "total_tokens");
            return usage;
        }

        private static int ExtractJsonIntValue(string json, string key)
        {
            string search = "\"" + key + "\"";
            int idx = json.IndexOf(search, StringComparison.Ordinal);
            if (idx < 0)
                return 0;
            int colonIdx = json.IndexOf(':', idx + search.Length);
            if (colonIdx < 0)
                return 0;
            int start = colonIdx + 1;
            while (start < json.Length && json[start] == ' ')
                start++;
            int end = start;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-'))
                end++;
            if (end > start && int.TryParse(json.Substring(start, end - start), out int val))
                return val;
            return 0;
        }

        private static string AppendStatusNote(string current, string note)
        {
            if (string.IsNullOrWhiteSpace(note))
                return current;

            return string.IsNullOrWhiteSpace(current)
                ? note
                : $"{current}{note}";
        }

        private static void AppendEscapedJsonCharacter(
            string json,
            ref int index,
            StringBuilder sb,
            bool preserveUnknownEscape)
        {
            index++;
            if (index >= json.Length)
                return;

            switch (json[index])
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    if (TryParseUnicodeEscape(json, index + 1, out char unicodeChar))
                    {
                        sb.Append(unicodeChar);
                        index += 4;
                    }
                    else if (preserveUnknownEscape)
                    {
                        sb.Append("\\u");
                    }
                    else
                    {
                        sb.Append('u');
                    }
                    break;
                default:
                    if (preserveUnknownEscape)
                        sb.Append('\\');
                    sb.Append(json[index]);
                    break;
            }
        }

        private static bool TryParseUnicodeEscape(string json, int startIndex, out char value)
        {
            value = '\0';
            if (string.IsNullOrEmpty(json) || startIndex < 0 || startIndex + 3 >= json.Length)
                return false;

            int code = 0;
            for (int i = 0; i < 4; i++)
            {
                int hex = HexToInt(json[startIndex + i]);
                if (hex < 0)
                    return false;

                code = (code << 4) | hex;
            }

            value = (char)code;
            return true;
        }

        private static int HexToInt(char c)
        {
            if (c >= '0' && c <= '9')
                return c - '0';
            if (c >= 'a' && c <= 'f')
                return 10 + (c - 'a');
            if (c >= 'A' && c <= 'F')
                return 10 + (c - 'A');
            return -1;
        }

        [Serializable]
        private class OpenAiResponseEnvelope
        {
            public string id;
            public string model;
            public OpenAiChoice[] choices;
        }

        [Serializable]
        private class OpenAiChoice
        {
            public OpenAiMessage message;
        }

        [Serializable]
        private class OpenAiMessage
        {
            public string role;
            public string content;
            public OpenAiToolCall[] tool_calls;
        }

        [Serializable]
        private class OpenAiToolCall
        {
            public string id;
            public string type;
            public OpenAiToolCallFunction function;
        }

        [Serializable]
        private class OpenAiToolCallFunction
        {
            public string name;
            public string arguments;
        }

        [Serializable]
        private class OpenAiErrorResponse
        {
            public OpenAiError error;
        }

        [Serializable]
        private class OpenAiError
        {
            public string message;
            public string type;
            public string code;
        }
    }
}
