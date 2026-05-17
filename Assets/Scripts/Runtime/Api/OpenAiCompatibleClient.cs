using System;
using System.Collections;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Api.Models;
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
            if (provider == null)
            {
                throw new ArgumentNullException(nameof(provider));
            }

            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var endpoint = BuildEndpoint(provider.baseUrl);
            var payloadJson = JsonUtility.ToJson(request);

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
                    throw new InvalidOperationException($"API request failed: {webRequest.error}");
                }

                var rawResponse = webRequest.downloadHandler.text;
                return ParseResponse(rawResponse);
            }
        }

        private static string BuildEndpoint(string baseUrl)
        {
            var normalized = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(normalized))
            {
                throw new InvalidOperationException("Provider baseUrl is empty.");
            }

            return $"{normalized}/chat/completions";
        }

        private static AiChatResponse ParseResponse(string rawJson)
        {
            if (string.IsNullOrWhiteSpace(rawJson))
            {
                return new AiChatResponse
                {
                    content = string.Empty
                };
            }

            var response = JsonUtility.FromJson<OpenAiResponseEnvelope>(rawJson);
            var content = string.Empty;

            if (response != null && response.choices != null && response.choices.Length > 0)
            {
                var first = response.choices[0];
                if (first != null && first.message != null)
                {
                    content = first.message.content ?? string.Empty;
                }
            }

            return new AiChatResponse
            {
                id = response != null ? response.id : string.Empty,
                model = response != null ? response.model : string.Empty,
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
    }
}
