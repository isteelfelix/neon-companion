using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Api.Models;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;
using UnityEngine;
using UnityEngine.Networking;

namespace NeonCompanion.Runtime.Api
{
    public sealed class OpenAiCompatibleClient : IAiClient
    {
        private const string HermesSessionHeaderName = "X-Hermes-Session-Id";

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

            var routing = await ResolveRequestRoutingAsync(provider, request, cancellationToken);
            NeonLogger.Log($"OpenAI request send: provider={provider?.id}, requestedModel={request?.model}, routedModel={routing.Model}, providerSessionId={routing.ProviderSessionId ?? "<null>"}");
            var endpoint = BuildEndpoint(provider.baseUrl);
            var payloadJson = BuildChatCompletionPayloadJson(
                routing.Model,
                request.temperature,
                request.maxTokens,
                messages,
                stream: false);

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
                ApplyHermesSessionHeader(webRequest, routing.ProviderSessionId);

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
                response.providerSessionId = GetHermesSessionHeader(webRequest, routing.ProviderSessionId);
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
                        var modelsPayload = webRequest.downloadHandler?.text;
                        var inventoryPayload = await TryFetchHermesInventoryPayloadAsync(provider, cancellationToken);
                        if (!string.IsNullOrEmpty(inventoryPayload))
                            discoveredModels = ParseHermesInventoryModelIds(inventoryPayload);

                        if (!string.IsNullOrEmpty(modelsPayload))
                            discoveredModels ??= ParseModelIds(modelsPayload);

                        if ((discoveredModels == null || discoveredModels.Count == 0) && !string.IsNullOrEmpty(inventoryPayload))
                            discoveredModels = ParseModelIds(inventoryPayload);

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

                        if (string.IsNullOrEmpty(inventoryPayload) &&
                            discoveredModels != null &&
                            discoveredModels.Count == 1 &&
                            string.Equals(discoveredModels[0], "hermes-agent", StringComparison.OrdinalIgnoreCase))
                        {
                            modelNote = AppendStatusNote(modelNote, " · Hermes inventory недоступен, сервер экспортирует только hermes-agent");
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

            return new ModelSwitchResult(
                success: true,
                requestedModel: requestedModel,
                appliedModel: requestedModel,
                providerSessionId: null);
        }

        private static IReadOnlyList<string> ParseModelIds(string json)
        {
            if (string.IsNullOrEmpty(json))
                return null;

            var ids = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Prefer known container arrays first to avoid picking unrelated top-level ids.
            if (TryExtractNamedArray(json, "data", out string dataArray))
                CollectJsonStringPropertyValues(dataArray, "id", ids, seen);

            if (TryExtractNamedArray(json, "models", out string modelsArray))
            {
                CollectJsonStringPropertyValues(modelsArray, "id", ids, seen);
                CollectJsonStringPropertyValues(modelsArray, "name", ids, seen);
            }

            if (ids.Count == 0)
            {
                CollectJsonStringPropertyValues(json, "id", ids, seen);
                CollectJsonStringPropertyValues(json, "name", ids, seen);
            }

            return ids.Count > 0 ? ids : null;
        }

        private static IReadOnlyList<string> ParseHermesInventoryModelIds(string json)
        {
            if (string.IsNullOrEmpty(json))
                return null;

            if (!TryExtractNamedArray(json, "providers", out string providersArray))
                return null;

            var ids = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int position = 0;
            while (TryExtractNamedArray(providersArray, "models", position, out string modelsArray, out int nextPosition))
            {
                CollectJsonStringArrayValues(modelsArray, ids, seen);
                CollectJsonStringPropertyValues(modelsArray, "id", ids, seen);
                CollectJsonStringPropertyValues(modelsArray, "name", ids, seen);
                CollectJsonStringPropertyValues(modelsArray, "model", ids, seen);
                position = nextPosition;
            }

            return ids.Count > 0 ? ids : null;
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

        private static string[] BuildHermesInventoryEndpoints(string baseUrl)
        {
            var normalized = NormalizeBaseUrl(baseUrl);
            if (normalized.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(0, normalized.Length - "/chat/completions".Length);

            string root = normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                ? normalized.Substring(0, normalized.Length - 3)
                : normalized;

            var endpoints = new List<string>();
            void AddEndpoint(string url)
            {
                if (!string.IsNullOrWhiteSpace(url) && !endpoints.Contains(url))
                    endpoints.Add(url);
            }

            AddEndpoint($"{root}/api/model/options");
            AddEndpoint($"{normalized}/api/model/options");
            AddEndpoint($"{root}/model/options");
            AddEndpoint($"{normalized}/model/options");
            return endpoints.ToArray();
        }

        private async Task<string> TryFetchHermesInventoryPayloadAsync(
            ProviderConfig provider,
            CancellationToken cancellationToken)
        {
            if (provider == null)
                return null;

            var endpoints = BuildHermesInventoryEndpoints(provider.baseUrl);
            for (int i = 0; i < endpoints.Length; i++)
            {
                using (var webRequest = UnityWebRequest.Get(endpoints[i]))
                {
                    if (!string.IsNullOrWhiteSpace(provider.apiKey))
                        webRequest.SetRequestHeader("Authorization", $"Bearer {provider.apiKey}");

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
                }
            }

            return null;
        }

        private static string NormalizeBaseUrl(string baseUrl)
        {
            return (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        }

        private static string ParseErrorMessage(UnityWebRequest webRequest)
        {
            if (!string.IsNullOrEmpty(webRequest.downloadHandler?.text))
            {
                try
                {
                    var errorResponse = JsonUtility.FromJson<OpenAiErrorResponse>(webRequest.downloadHandler.text);
                    if (errorResponse?.error != null && !string.IsNullOrEmpty(errorResponse.error.message))
                    {
                        return errorResponse.error.message;
                    }
                }
                catch { /* ignore */ }
            }

            return webRequest.error ?? "Unknown error";
        }

        private static string BuildChatCompletionPayloadJson(
            string model,
            float temperature,
            int maxTokens,
            List<AiChatMessage> messages,
            bool stream)
        {
            bool omitTemperature = UsesFixedDefaultTemperature(model);
            bool useCompletionTokens = UsesMaxCompletionTokens(model);

            var sb = new StringBuilder(1024);
            sb.Append('{');
            AppendJsonProperty(sb, "model", model, isFirst: true);
            sb.Append(",\"messages\":[");
            AppendMessagesJson(sb, messages);
            sb.Append(']');

            if (stream)
                sb.Append(",\"stream\":true");

            if (!omitTemperature)
                sb.Append(",\"temperature\":").Append(temperature.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));

            string tokenProperty = useCompletionTokens ? "max_completion_tokens" : "max_tokens";
            sb.Append(",\"").Append(tokenProperty).Append("\":").Append(maxTokens);
            sb.Append('}');
            return sb.ToString();
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
            if (!hasAttachments)
            {
                AppendJsonString(sb, message?.content ?? string.Empty);
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
            sb.Append('}');
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

        private static string GuessImageMediaType(string path)
        {
            string extension = Path.GetExtension(path)?.ToLowerInvariant();
            return extension switch
            {
                ".png" => "image/png",
                ".jpg" => "image/jpeg",
                ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                _ => "application/octet-stream"
            };
        }

        private static bool UsesMaxCompletionTokens(string model)
        {
            if (string.IsNullOrWhiteSpace(model))
                return false;

            string normalized = model.Trim().ToLowerInvariant();
            return normalized.StartsWith("gpt-5", StringComparison.Ordinal) ||
                   normalized.Contains("/gpt-5") ||
                   normalized.StartsWith("o1", StringComparison.Ordinal) ||
                   normalized.StartsWith("o3", StringComparison.Ordinal) ||
                   normalized.StartsWith("o4", StringComparison.Ordinal) ||
                   normalized.Contains("/o1") ||
                   normalized.Contains("/o3") ||
                   normalized.Contains("/o4");
        }

        private static bool UsesFixedDefaultTemperature(string model)
        {
            if (string.IsNullOrWhiteSpace(model))
                return false;

            string normalized = model.Trim().ToLowerInvariant();
            return normalized.StartsWith("gpt-5", StringComparison.Ordinal) ||
                   normalized.Contains("/gpt-5");
        }

        private static bool ShouldForceNonStreaming(string model)
        {
            return UsesMaxCompletionTokens(model);
        }

        private static AiChatResponse ParseResponse(string rawJson)
        {
            if (string.IsNullOrWhiteSpace(rawJson))
                return new AiChatResponse { content = string.Empty };

            // JsonUtility throws on null fields in vLLM/Hermes responses — try first, fall back to manual.
            try
            {
                var envelope = JsonUtility.FromJson<OpenAiResponseEnvelope>(rawJson);
                if (envelope?.choices != null && envelope.choices.Length > 0)
                {
                    var msg = envelope.choices[0]?.message;
                    if (msg != null && !string.IsNullOrEmpty(msg.content))
                        return new AiChatResponse
                        {
                            id = envelope.id ?? string.Empty,
                            model = envelope.model ?? string.Empty,
                            content = msg.content,
                            receivedAtUtc = DateTime.UtcNow
                        };
                }
            }
            catch { }

            // Manual fallback: handles both compact ("content":"") and spaced ("content": "") JSON.
            int choicesIdx = rawJson.IndexOf("\"choices\"", StringComparison.Ordinal);
            string content = ExtractJsonStringValue(rawJson, "content", choicesIdx >= 0 ? choicesIdx : 0) ?? string.Empty;
            return new AiChatResponse { content = content, receivedAtUtc = DateTime.UtcNow };
        }

        private async Task<RequestRoutingInfo> ResolveRequestRoutingAsync(
            ProviderConfig provider,
            AiChatRequest request,
            CancellationToken cancellationToken)
        {
            return new RequestRoutingInfo
            {
                Model = request.model,
                ProviderSessionId = request?.providerSessionId?.Trim()
            };
        }

        private async Task<string> TryGetHermesProxyModelAsync(
            ProviderConfig provider,
            CancellationToken cancellationToken)
        {
            if (provider == null)
                return null;

            var endpoint = BuildModelsEndpoint(provider.baseUrl);
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
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    await Task.Yield();
                }

                if (webRequest.result != UnityWebRequest.Result.Success || webRequest.responseCode != 200)
                    return null;

                var discoveredModels = ParseModelIds(webRequest.downloadHandler?.text);
                return ContainsModel(discoveredModels, "hermes-agent") ? "hermes-agent" : null;
            }
        }

        private async Task<AiChatResponse> SendHermesModelSwitchAsync(
            ProviderConfig provider,
            string hermesProxyModel,
            string targetModel,
            string providerSessionId,
            CancellationToken cancellationToken)
        {
            var endpoint = BuildEndpoint(provider.baseUrl);
            var payloadJson = BuildChatCompletionPayloadJson(
                hermesProxyModel,
                0f,
                64,
                new List<AiChatMessage>
                {
                    new AiChatMessage
                    {
                        role = "user",
                        content = $"/model {targetModel}"
                    }
                },
                stream: false);

            using (var webRequest = new UnityWebRequest(endpoint, UnityWebRequest.kHttpVerbPOST))
            {
                var bodyRaw = Encoding.UTF8.GetBytes(payloadJson);
                webRequest.uploadHandler = new UploadHandlerRaw(bodyRaw);
                webRequest.downloadHandler = new DownloadHandlerBuffer();
                webRequest.SetRequestHeader("Content-Type", "application/json");

                if (!string.IsNullOrWhiteSpace(provider.apiKey))
                    webRequest.SetRequestHeader("Authorization", $"Bearer {provider.apiKey}");
                ApplyHermesSessionHeader(webRequest, providerSessionId);

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
                    throw new InvalidOperationException($"Hermes model switch failed: {ParseErrorMessage(webRequest)}");

                var response = ParseResponse(webRequest.downloadHandler?.text ?? string.Empty);
                response.providerSessionId = GetHermesSessionHeader(webRequest, providerSessionId);
                if (string.IsNullOrWhiteSpace(response.model))
                    response.model = hermesProxyModel;

                return response;
            }
        }

        private async Task<string> QueryHermesCurrentModelAsync(
            ProviderConfig provider,
            string hermesProxyModel,
            string providerSessionId,
            CancellationToken cancellationToken)
        {
            var endpoint = BuildEndpoint(provider.baseUrl);
            var payloadJson = BuildChatCompletionPayloadJson(
                hermesProxyModel,
                0f,
                96,
                new List<AiChatMessage>
                {
                    new AiChatMessage
                    {
                        role = "user",
                        content = "/model"
                    }
                },
                stream: false);

            using (var webRequest = new UnityWebRequest(endpoint, UnityWebRequest.kHttpVerbPOST))
            {
                var bodyRaw = Encoding.UTF8.GetBytes(payloadJson);
                webRequest.uploadHandler = new UploadHandlerRaw(bodyRaw);
                webRequest.downloadHandler = new DownloadHandlerBuffer();
                webRequest.SetRequestHeader("Content-Type", "application/json");

                if (!string.IsNullOrWhiteSpace(provider.apiKey))
                    webRequest.SetRequestHeader("Authorization", $"Bearer {provider.apiKey}");
                ApplyHermesSessionHeader(webRequest, providerSessionId);

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
                    return null;

                var response = ParseResponse(webRequest.downloadHandler?.text ?? string.Empty);
                return ParseHermesCurrentModelLabel(response?.content);
            }
        }

        private static string ExtractJsonStringValue(string json, string key, int startFrom)
        {
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

        public async Task<AiChatResponse> SendMessageStreamAsync(
            ProviderConfig provider,
            AiChatRequest request,
            Action<string> onToken,
            CancellationToken cancellationToken = default)
        {
            ProviderValidator.Validate(provider);

            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (ShouldForceNonStreaming(request.model))
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
                stream: true);

            using (var webRequest = new UnityWebRequest(endpoint, UnityWebRequest.kHttpVerbPOST))
            {
                var bodyRaw = Encoding.UTF8.GetBytes(payloadJson);
                webRequest.uploadHandler = new UploadHandlerRaw(bodyRaw);
                webRequest.downloadHandler = new DownloadHandlerBuffer();
                webRequest.SetRequestHeader("Content-Type", "application/json");

                if (!string.IsNullOrWhiteSpace(provider.apiKey))
                    webRequest.SetRequestHeader("Authorization", $"Bearer {provider.apiKey}");
                ApplyHermesSessionHeader(webRequest, routing.ProviderSessionId);

                var operation = webRequest.SendWebRequest();
                int lastProcessed = 0;
                bool emittedAnyToken = false;
                var collected = new StringBuilder();
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

                    lastProcessed = ParseSseText(webRequest.downloadHandler.text, lastProcessed, emitToken, flushPartialLine: false);

                    await Task.Yield();
                }

                // Drain any data that arrived after the last yield
                string finalStreamingText = webRequest.downloadHandler?.text ?? string.Empty;
                ParseSseText(finalStreamingText, lastProcessed, emitToken, flushPartialLine: true);

                if (webRequest.result != UnityWebRequest.Result.Success)
                {
                    throw new InvalidOperationException($"Streaming request failed: {ParseErrorMessage(webRequest)}");
                }

                string responseProviderSessionId = GetHermesSessionHeader(webRequest, routing.ProviderSessionId);

                // Some providers ignore `stream=true` and return a normal JSON completion.
                if (!emittedAnyToken)
                {
                    AiChatResponse fallbackResponse = null;
                    var fallback = ExtractContentFromStreamingPayload(finalStreamingText);
                    if (string.IsNullOrWhiteSpace(fallback))
                    {
                        fallbackResponse = ParseResponse(finalStreamingText);
                        fallback = fallbackResponse?.content;
                    }
                    if (string.IsNullOrWhiteSpace(fallback))
                    {
                        fallbackResponse = await SendMessageAsync(
                            provider,
                            new AiChatRequest
                            {
                                model = request.model,
                                providerSessionId = responseProviderSessionId,
                                temperature = request.temperature,
                                maxTokens = request.maxTokens,
                                systemPrompt = request.systemPrompt,
                                messages = request.messages
                            },
                            cancellationToken);
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

                if (!emittedAnyToken)
                {
                    throw new InvalidOperationException("Streaming response contained no tokens. Check provider endpoint, model id, and streaming compatibility.");
                }

                return new AiChatResponse
                {
                    model = routing.Model ?? request.model ?? string.Empty,
                    providerSessionId = responseProviderSessionId,
                    content = collected.ToString(),
                    receivedAtUtc = DateTime.UtcNow
                };
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

        private static void ApplyHermesSessionHeader(UnityWebRequest webRequest, string providerSessionId)
        {
            if (webRequest == null || string.IsNullOrWhiteSpace(providerSessionId))
                return;

            webRequest.SetRequestHeader(HermesSessionHeaderName, providerSessionId.Trim());
        }

        private static string GetHermesSessionHeader(UnityWebRequest webRequest, string fallbackValue)
        {
            string sessionId = webRequest?.GetResponseHeader(HermesSessionHeaderName);
            return string.IsNullOrWhiteSpace(sessionId) ? fallbackValue : sessionId.Trim();
        }

        [Serializable]
        private class OpenAiChatCompletionRequest
        {
            public string model;
            public float temperature;
            public int max_tokens;
            public List<AiChatMessage> messages;
        }

        [Serializable]
        private class OpenAiChatCompletionRequestWithCompletionTokens
        {
            public string model;
            public float temperature;
            public int max_completion_tokens;
            public List<AiChatMessage> messages;
        }

        [Serializable]
        private class OpenAiChatCompletionRequestWithCompletionTokensNoTemperature
        {
            public string model;
            public int max_completion_tokens;
            public List<AiChatMessage> messages;
        }

        [Serializable]
        private class OpenAiStreamingRequest
        {
            public string model;
            public float temperature;
            public int max_tokens;
            public List<AiChatMessage> messages;
            public bool stream = true;
        }

        [Serializable]
        private class OpenAiStreamingRequestWithCompletionTokens
        {
            public string model;
            public float temperature;
            public int max_completion_tokens;
            public List<AiChatMessage> messages;
            public bool stream = true;
        }

        [Serializable]
        private class OpenAiStreamingRequestWithCompletionTokensNoTemperature
        {
            public string model;
            public int max_completion_tokens;
            public List<AiChatMessage> messages;
            public bool stream = true;
        }

        private sealed class RequestRoutingInfo
        {
            public string Model;
            public string ProviderSessionId;
        }

        private static int ParseSseText(string text, int offset, Action<string> onToken, bool flushPartialLine)
        {
            if (string.IsNullOrEmpty(text) || offset >= text.Length)
                return Math.Max(0, offset);

            int searchFrom = offset;
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

                if (!TryExtractSsePayload(line, out string payload))
                    continue;

                if (payload == "[DONE]")
                    break;

                if (string.IsNullOrWhiteSpace(payload))
                    continue;

                string delta = ExtractDeltaContent(payload);
                if (!string.IsNullOrEmpty(delta))
                    onToken?.Invoke(delta);
            }

            return searchFrom;
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

        private static bool ContainsModel(IReadOnlyList<string> models, string modelId)
        {
            if (models == null || string.IsNullOrWhiteSpace(modelId))
                return false;

            for (int i = 0; i < models.Count; i++)
            {
                if (string.Equals(models[i], modelId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static bool DoesHermesModelMatch(string requestedModel, string currentModel)
        {
            if (string.IsNullOrWhiteSpace(requestedModel) || string.IsNullOrWhiteSpace(currentModel))
                return false;

            string requested = requestedModel.Trim();
            string current = currentModel.Trim();
            if (string.Equals(requested, current, StringComparison.OrdinalIgnoreCase))
                return true;

            int slash = requested.LastIndexOf('/');
            if (slash >= 0 && slash + 1 < requested.Length)
            {
                string shortRequested = requested.Substring(slash + 1);
                if (string.Equals(shortRequested, current, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (current.IndexOf(shortRequested, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return current.IndexOf(requested, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ParseHermesCurrentModelLabel(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return null;

            int marker = content.IndexOf("**", StringComparison.Ordinal);
            if (marker >= 0)
            {
                int end = content.IndexOf("**", marker + 2, StringComparison.Ordinal);
                if (end > marker + 2)
                    return content.Substring(marker + 2, end - marker - 2).Trim();
            }

            const string prefix = "Текущая модель:";
            int prefixIdx = content.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (prefixIdx >= 0)
            {
                string tail = content.Substring(prefixIdx + prefix.Length).Trim();
                int lineBreak = tail.IndexOf('\n');
                if (lineBreak >= 0)
                    tail = tail.Substring(0, lineBreak).Trim();
                int providerIdx = tail.IndexOf("(provider:", StringComparison.OrdinalIgnoreCase);
                if (providerIdx > 0)
                    tail = tail.Substring(0, providerIdx).Trim();
                return tail.Trim('`', ' ', '*');
            }

            return null;
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
        }

        [Serializable]
        private class OpenAiModelsResponse
        {
            public OpenAiModelEntry[] data;
        }

        [Serializable]
        private class OpenAiModelEntry
        {
            public string id;
        }
    }
}
