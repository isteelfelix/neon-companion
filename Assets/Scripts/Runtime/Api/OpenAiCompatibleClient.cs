using System;
using System.Collections;
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

            var messages = new System.Collections.Generic.List<AiChatMessage>(request.messages);

            // Add system prompt if provided
            if (!string.IsNullOrWhiteSpace(request.systemPrompt))
            {
                messages.Insert(0, new AiChatMessage
                {
                    role = "system",
                    content = request.systemPrompt
                });
            }

            var requestWithSystem = new AiChatRequest
            {
                model = request.model,
                temperature = request.temperature,
                maxTokens = request.maxTokens,
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
                    cancellationToken.ThrowIfCancellationRequested();
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

        private static string BuildEndpoint(string baseUrl)
        {
            var normalized = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
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