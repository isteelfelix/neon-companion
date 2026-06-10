// HermesRestClient.cs - REST management client for Hermes backend
// Handles session listing, history and deletion via HTTP.
// Base URL: configured at runtime from provider settings

using System;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace NeonCompanion.Runtime.Api.Hermes
{
    // === Response Types ===

    [Serializable]
    public class PaginatedSessions
    {
        public HermesSession[] sessions;
        public int total;
        public int offset;
        public int limit;
    }

    [Serializable]
    public class HermesSession
    {
        public string id;
        public string title;
        public string preview;
        public string model;
        public string source;
        public long started_at;
        public long last_active;
        public bool is_active;
        public int message_count;
        public int input_tokens;
        public int output_tokens;
        public int tool_call_count;
    }

    [Serializable]
    public class ModelOptionProvider
    {
        public string slug;
        public string name;
        public string[] models;
        public bool? is_current;
        public string warning;
        public string[] unavailable_models;
    }

    [Serializable]
    public class ModelOptionsResponse
    {
        public string model;
        public string provider;
        public ModelOptionProvider[] providers;
    }

    // === HermesRestClient ===

    public class HermesRestClient
    {
        private string _baseUrl;
        private string _token;

        public HermesRestClient(string baseUrl, string token = null)
        {
            _baseUrl = (baseUrl ?? "").TrimEnd('/');
            _token = token;
        }

        public void Configure(string baseUrl, string token = null)
        {
            _baseUrl = (baseUrl ?? "").TrimEnd('/');
            _token = token;
        }

        // === Sessions ===

        public async Task<PaginatedSessions> ListSessions(int limit = 40, int minMessages = 0, int offset = 0)
        {
            return await Get<PaginatedSessions>(
                "/api/sessions?limit=" + limit + "&offset=" + offset + "&min_messages=" + minMessages);
        }

        public async Task<JToken> GetSessionMessages(string sessionId)
        {
            // Returns the full structured history (incl. tool_calls) — array or { messages: [...] }.
            return await Get<JToken>(
                "/api/sessions/" + UnityWebRequest.EscapeURL(sessionId) + "/messages");
        }

        public async Task DeleteSession(string sessionId)
        {
            await Delete("/api/sessions/" + UnityWebRequest.EscapeURL(sessionId));
        }

        // === HTTP Helpers ===

        private async Task<T> Get<T>(string path)
        {
            var json = await GetRaw(path);
            return JsonConvert.DeserializeObject<T>(json);
        }

        private async Task<string> GetRaw(string path)
        {
            var url = _baseUrl + path;
            using (var request = UnityWebRequest.Get(url))
            {
                ApplyHeaders(request);
                var op = request.SendWebRequest();
                while (!op.isDone)
                    await Task.Yield();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    string error = "REST GET failed: " + request.error + " [" + path + "]";
                    Debug.LogError("[HermesRest] " + error);
                    throw new Exception(error);
                }
                return request.downloadHandler.text;
            }
        }

        private async Task Delete(string path)
        {
            var url = _baseUrl + path;
            using (var request = UnityWebRequest.Delete(url))
            {
                ApplyHeaders(request);
                var op = request.SendWebRequest();
                while (!op.isDone)
                    await Task.Yield();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    string error = "REST DELETE failed: " + request.error + " [" + path + "]";
                    Debug.LogError("[HermesRest] " + error);
                    throw new Exception(error);
                }
            }
        }

        private void ApplyHeaders(UnityWebRequest request)
        {
            if (!string.IsNullOrEmpty(_token))
                request.SetRequestHeader("Authorization", "Bearer " + _token);
        }
    }
}
