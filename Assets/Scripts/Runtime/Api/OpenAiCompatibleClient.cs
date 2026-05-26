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

            var requestWithSystem = new OpenAiChatCompletionRequest
            {
                model = request.model,
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
            ProviderValidator.Validate(provider);

            var normalized = (provider.baseUrl ?? string.Empty).Trim().TrimEnd('/');
            if (normalized.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(0, normalized.Length - "/chat/completions".Length);

            var endpoint = $"{normalized}/models";
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

                    if (authed)
                    {
                        var modelsPayload = webRequest.downloadHandler?.text;
                        if (!string.IsNullOrWhiteSpace(provider.defaultModel) &&
                            !ModelExistsInModelsResponse(modelsPayload, provider.defaultModel))
                        {
                            return new ConnectionTestResult(false,
                                $"Connected, but model '{provider.defaultModel}' was not found in /models · {latency} ms",
                                latency);
                        }
                    }

                    string msg = authed
                        ? $"OK · {latency} ms"
                        : $"Reachable but unauthorized · {latency} ms";
                    return new ConnectionTestResult(authed, msg, latency);
                }

                return new ConnectionTestResult(false,
                    $"{webRequest.error ?? "error"} (HTTP {webRequest.responseCode}) · {latency} ms",
                    latency);
            }
        }

        private static string BuildEndpoint(string baseUrl)
        {
            var normalized = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            if (normalized.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                return normalized;
            }

            return $"{normalized}/chat/completions";
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
            {
                return new AiChatResponse { content = string.Empty };
            }

            var response = JsonUtility.FromJson<OpenAiResponseEnvelope>(rawJson);
            var content = string.Empty;

            if (response?.choices != null && response.choices.Length > 0)
            {
                var first = response.choices[0];
                if (first?.message != null)
                {
                    content = first.message.content ?? string.Empty;
                }
            }

            return new AiChatResponse
            {
                id = response?.id ?? string.Empty,
                model = response?.model ?? string.Empty,
                content = content,
                receivedAtUtc = DateTime.UtcNow
            };
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
                model = request.model,
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

        private static bool ModelExistsInModelsResponse(string payload, string model)
        {
            if (string.IsNullOrWhiteSpace(payload) || string.IsNullOrWhiteSpace(model))
                return true;

            // Best-effort check for OpenAI-compatible `/models` payloads.
            string escapedModel = JsonEscape(model.Trim());
            string marker = $"\"id\":\"{escapedModel}\"";
            return payload.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string JsonEscape(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
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
            const string marker = "\"content\":\"";
            int idx = json.IndexOf(marker);
            if (idx < 0) return null;

            int start = idx + marker.Length;
            var sb = new StringBuilder();
            for (int i = start; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '\\' && i + 1 < json.Length)
                {
                    i++;
                    switch (json[i])
                    {
                        case '"':  sb.Append('"');  break;
                        case '\\': sb.Append('\\'); break;
                        case 'n':  sb.Append('\n'); break;
                        case 'r':  sb.Append('\r'); break;
                        case 't':  sb.Append('\t'); break;
                        case 'b':  sb.Append('\b'); break;
                        case 'f':  sb.Append('\f'); break;
                        default:   sb.Append('\\'); sb.Append(json[i]); break;
                    }
                }
                else if (c == '"')
                    break;
                else
                    sb.Append(c);
            }

            return sb.Length > 0 ? sb.ToString() : null;
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
    }
}
