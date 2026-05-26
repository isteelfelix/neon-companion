using System;
using System.Collections.Generic;
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

            string resolvedModel = await ResolveRequestModelAsync(provider, request.model, cancellationToken);
            var requestWithSystem = new OpenAiChatCompletionRequest
            {
                model = resolvedModel,
                temperature = request.temperature,
                max_tokens = request.maxTokens,
                messages = messages
            };

            var endpoint = BuildEndpoint(provider.baseUrl);
            var payloadJson = JsonUtility.ToJson(requestWithSystem);

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
                return ParseResponse(rawResponse);
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
                        if (!string.IsNullOrEmpty(modelsPayload))
                            discoveredModels = ParseModelIds(modelsPayload);

                        bool isHermesProxy = LooksLikeHermesProxy(discoveredModels);
                        if (isHermesProxy)
                        {
                            var hermesModels = await TryFetchHermesPickerModelsAsync(provider, cancellationToken);
                            if (hermesModels != null && hermesModels.Count > 0)
                            {
                                discoveredModels = hermesModels;
                                isHermesProxy = false;
                                modelNote = AppendStatusNote(modelNote, " · список моделей получен из Hermes picker inventory");
                            }
                        }

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
                                modelNote = isHermesProxy
                                    ? $" · Hermes будет маршрутизировать внутреннюю модель «{provider.defaultModel}» через /model"
                                    : $" · модель «{provider.defaultModel}» не найдена — выберите из списка";
                            }
                        }

                        if (discoveredModels == null || discoveredModels.Count == 0)
                            modelNote = AppendStatusNote(modelNote, " · список моделей не распознан");
                        else if (isHermesProxy)
                            modelNote = AppendStatusNote(modelNote, await BuildHermesModelNoteAsync(provider, discoveredModels[0], cancellationToken));
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

        private static string BuildHermesModelOptionsEndpoint(string baseUrl)
        {
            var normalized = NormalizeBaseUrl(baseUrl);
            if (normalized.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(0, normalized.Length - "/chat/completions".Length);
            if (normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(0, normalized.Length - "/v1".Length);

            return $"{normalized}/api/model/options";
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

        private async Task<string> BuildHermesModelNoteAsync(
            ProviderConfig provider,
            string proxyModel,
            CancellationToken cancellationToken)
        {
            const string genericNote = " · Hermes через OpenAI API экспортирует только hermes-agent";

            if (provider == null || string.IsNullOrWhiteSpace(proxyModel))
                return genericNote;

            try
            {
                var probeRequest = new AiChatRequest
                {
                    model = proxyModel,
                    temperature = 0f,
                    maxTokens = 64,
                    messages = new List<AiChatMessage>
                    {
                        new AiChatMessage
                        {
                            role = "user",
                            content = "/model"
                        }
                    }
                };

                var response = await SendMessageAsync(provider, probeRequest, cancellationToken);
                string currentModel = ParseHermesCurrentModelLabel(response?.content);
                if (string.IsNullOrWhiteSpace(currentModel))
                    return genericNote;

                return $" · Hermes через OpenAI API экспортирует только hermes-agent (текущая внутренняя модель: {currentModel})";
            }
            catch
            {
                return genericNote;
            }
        }

        private async Task<string> ResolveRequestModelAsync(
            ProviderConfig provider,
            string requestedModel,
            CancellationToken cancellationToken)
        {
            string trimmedRequested = requestedModel?.Trim();
            if (string.IsNullOrWhiteSpace(trimmedRequested))
                return requestedModel;

            string hermesProxyModel = await TryGetHermesProxyModelAsync(provider, cancellationToken);
            if (string.IsNullOrWhiteSpace(hermesProxyModel) ||
                string.Equals(trimmedRequested, hermesProxyModel, StringComparison.OrdinalIgnoreCase))
            {
                return requestedModel;
            }

            await SendHermesModelSwitchAsync(provider, hermesProxyModel, trimmedRequested, cancellationToken);
            return hermesProxyModel;
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
                return LooksLikeHermesProxy(discoveredModels) ? discoveredModels[0] : null;
            }
        }

        private async Task<IReadOnlyList<string>> TryFetchHermesPickerModelsAsync(
            ProviderConfig provider,
            CancellationToken cancellationToken)
        {
            if (provider == null)
                return null;

            var endpoint = BuildHermesModelOptionsEndpoint(provider.baseUrl);
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

                return ParseHermesPickerModelIds(webRequest.downloadHandler?.text);
            }
        }

        private async Task SendHermesModelSwitchAsync(
            ProviderConfig provider,
            string proxyModel,
            string targetModel,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(proxyModel) || string.IsNullOrWhiteSpace(targetModel))
                return;

            await SendMessageAsync(
                provider,
                new AiChatRequest
                {
                    model = proxyModel,
                    temperature = 0f,
                    maxTokens = 64,
                    messages = new List<AiChatMessage>
                    {
                        new AiChatMessage
                        {
                            role = "user",
                            content = $"/model {targetModel}"
                        }
                    }
                },
                cancellationToken);
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

        private static IReadOnlyList<string> ParseHermesPickerModelIds(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            if (!TryExtractNamedArray(json, "providers", out string providersArray))
                return null;

            var models = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string providerObject in SplitTopLevelObjects(providersArray))
            {
                if (string.IsNullOrWhiteSpace(providerObject))
                    continue;

                if (!TryExtractNamedArray(providerObject, "models", out string modelsArray))
                    continue;

                CollectHermesModelsFromArray(modelsArray, models, seen);
            }

            return models.Count > 0 ? models : null;
        }

        private static void CollectHermesModelsFromArray(string arrayJson, List<string> models, HashSet<string> seen)
        {
            if (string.IsNullOrWhiteSpace(arrayJson))
                return;

            int firstItem = SkipWhitespace(arrayJson, 1);
            if (firstItem >= arrayJson.Length)
                return;

            if (arrayJson[firstItem] == '{')
            {
                foreach (string modelObject in SplitTopLevelObjects(arrayJson))
                {
                    string modelId =
                        ExtractJsonStringValue(modelObject, "id", 0) ??
                        ExtractJsonStringValue(modelObject, "model", 0) ??
                        ExtractJsonStringValue(modelObject, "slug", 0) ??
                        ExtractJsonStringValue(modelObject, "name", 0);

                    if (!string.IsNullOrWhiteSpace(modelId) && seen.Add(modelId))
                        models.Add(modelId);
                }

                return;
            }

            foreach (string modelId in ParseTopLevelStringArray(arrayJson))
            {
                if (!string.IsNullOrWhiteSpace(modelId) && seen.Add(modelId))
                    models.Add(modelId);
            }
        }

        private static IEnumerable<string> SplitTopLevelObjects(string arrayJson)
        {
            if (string.IsNullOrWhiteSpace(arrayJson))
                yield break;

            int depth = 0;
            bool inString = false;
            int objectStart = -1;

            for (int i = 0; i < arrayJson.Length; i++)
            {
                char c = arrayJson[i];
                if (c == '"' && (i == 0 || arrayJson[i - 1] != '\\'))
                {
                    inString = !inString;
                    continue;
                }

                if (inString)
                    continue;

                if (c == '{')
                {
                    if (depth == 0)
                        objectStart = i;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && objectStart >= 0)
                    {
                        yield return arrayJson.Substring(objectStart, i - objectStart + 1);
                        objectStart = -1;
                    }
                }
            }
        }

        private static IEnumerable<string> ParseTopLevelStringArray(string arrayJson)
        {
            if (string.IsNullOrWhiteSpace(arrayJson))
                yield break;

            int depth = 0;
            bool inString = false;
            int stringStart = -1;

            for (int i = 0; i < arrayJson.Length; i++)
            {
                char c = arrayJson[i];
                if (c == '"' && (i == 0 || arrayJson[i - 1] != '\\'))
                {
                    if (!inString)
                    {
                        inString = true;
                        if (depth == 1)
                            stringStart = i;
                    }
                    else
                    {
                        inString = false;
                        if (depth == 1 && stringStart >= 0 && TryReadJsonString(arrayJson, stringStart, out string value, out _))
                            yield return value;
                    }
                    continue;
                }

                if (inString)
                    continue;

                if (c == '[')
                    depth++;
                else if (c == ']')
                    depth--;
            }
        }

        public async Task SendMessageStreamAsync(
            ProviderConfig provider,
            AiChatRequest request,
            Action<string> onToken,
            CancellationToken cancellationToken = default)
        {
            ProviderValidator.Validate(provider);

            if (request == null)
                throw new ArgumentNullException(nameof(request));

            var messages = new List<AiChatMessage>(request.messages ?? new List<AiChatMessage>());

            if (!string.IsNullOrWhiteSpace(request.systemPrompt))
            {
                messages.Insert(0, new AiChatMessage
                {
                    role = "system",
                    content = request.systemPrompt
                });
            }

            var streamRequest = new OpenAiStreamingRequest
            {
                model = await ResolveRequestModelAsync(provider, request.model, cancellationToken),
                temperature = request.temperature,
                max_tokens = request.maxTokens,
                messages = messages
            };

            var endpoint = BuildEndpoint(provider.baseUrl);
            var payloadJson = JsonUtility.ToJson(streamRequest);

            using (var webRequest = new UnityWebRequest(endpoint, UnityWebRequest.kHttpVerbPOST))
            {
                var bodyRaw = Encoding.UTF8.GetBytes(payloadJson);
                webRequest.uploadHandler = new UploadHandlerRaw(bodyRaw);
                webRequest.downloadHandler = new DownloadHandlerBuffer();
                webRequest.SetRequestHeader("Content-Type", "application/json");

                if (!string.IsNullOrWhiteSpace(provider.apiKey))
                    webRequest.SetRequestHeader("Authorization", $"Bearer {provider.apiKey}");

                var operation = webRequest.SendWebRequest();
                int lastProcessed = 0;
                bool emittedAnyToken = false;
                Action<string> emitToken = token =>
                {
                    if (string.IsNullOrEmpty(token))
                        return;

                    emittedAnyToken = true;
                    onToken?.Invoke(token);
                };

                while (!operation.isDone)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        webRequest.Abort();
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    lastProcessed = ParseSseText(webRequest.downloadHandler.text, lastProcessed, emitToken);

                    await Task.Yield();
                }

                // Drain any data that arrived after the last yield
                ParseSseText(webRequest.downloadHandler.text, lastProcessed, emitToken);

                if (webRequest.result != UnityWebRequest.Result.Success)
                {
                    throw new InvalidOperationException($"Streaming request failed: {ParseErrorMessage(webRequest)}");
                }

                // Some providers ignore `stream=true` and return a normal JSON completion.
                if (!emittedAnyToken)
                {
                    var fallback = ParseResponse(webRequest.downloadHandler?.text)?.content;
                    if (!string.IsNullOrWhiteSpace(fallback))
                    {
                        emittedAnyToken = true;
                        onToken?.Invoke(fallback);
                    }
                }

                if (!emittedAnyToken)
                {
                    throw new InvalidOperationException("Streaming response contained no tokens. Check provider endpoint, model id, and streaming compatibility.");
                }
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

        [Serializable]
        private class OpenAiChatCompletionRequest
        {
            public string model;
            public float temperature;
            public int max_tokens;
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

        private static int ParseSseText(string text, int offset, Action<string> onToken)
        {
            int searchFrom = offset;
            while (true)
            {
                int nl = text.IndexOf('\n', searchFrom);
                if (nl < 0) break;

                string line = text.Substring(searchFrom, nl - searchFrom).TrimEnd('\r');
                searchFrom = nl + 1;

                if (!line.StartsWith("data: ")) continue;
                string payload = line.Substring(6);
                if (payload == "[DONE]") break;
                if (string.IsNullOrWhiteSpace(payload)) continue;

                string delta = ExtractDeltaContent(payload);
                if (!string.IsNullOrEmpty(delta))
                    onToken?.Invoke(delta);
            }
            return searchFrom;
        }

        private static string ExtractDeltaContent(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            int choicesIdx = json.IndexOf("\"choices\"", StringComparison.Ordinal);
            return ExtractJsonStringValue(json, "content", choicesIdx >= 0 ? choicesIdx : 0);
        }

        private static bool LooksLikeHermesProxy(IReadOnlyList<string> discoveredModels)
        {
            return discoveredModels != null &&
                   discoveredModels.Count == 1 &&
                   string.Equals(discoveredModels[0], "hermes-agent", StringComparison.OrdinalIgnoreCase);
        }

        private static string AppendStatusNote(string current, string note)
        {
            if (string.IsNullOrWhiteSpace(note))
                return current;

            return string.IsNullOrWhiteSpace(current)
                ? note
                : $"{current}{note}";
        }

        private static string ParseHermesCurrentModelLabel(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return null;

            int boldStart = content.IndexOf("**", StringComparison.Ordinal);
            if (boldStart < 0)
                return null;

            int boldEnd = content.IndexOf("**", boldStart + 2, StringComparison.Ordinal);
            if (boldEnd < 0)
                return null;

            string modelName = content.Substring(boldStart + 2, boldEnd - (boldStart + 2)).Trim();
            if (string.IsNullOrWhiteSpace(modelName))
                return null;

            int pos = boldEnd + 2;
            while (pos < content.Length && char.IsWhiteSpace(content[pos]))
                pos++;

            if (pos < content.Length && content[pos] == '(')
            {
                int close = content.IndexOf(')', pos + 1);
                if (close > pos + 1)
                {
                    string provider = content.Substring(pos + 1, close - pos - 1).Trim();
                    if (!string.IsNullOrWhiteSpace(provider))
                        return $"{modelName} ({provider})";
                }
            }

            return modelName;
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
