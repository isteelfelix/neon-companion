// HermesRestClient.cs - REST management client for Hermes backend
// Handles sessions, config, models, skills, cron, logs via HTTP.
// Base URL: configured at runtime from provider settings

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Core;
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
        public bool? free_tier;
        public string warning;
        public string[] unavailable_models;
        public int total_models;
    }

    [Serializable]
    public class ModelOptionsResponse
    {
        public string model;
        public string provider;
        public ModelOptionProvider[] providers;
    }

    [Serializable]
    public class StatusResponse
    {
        public string version;
        public string gateway;
        public string[] platforms;
    }

    [Serializable]
    public class SkillInfo
    {
        public string name;
        public string description;
        public bool enabled;
        public string category;
    }

    [Serializable]
    public class ToolsetInfo
    {
        public string name;
        public string description;
        public bool enabled;
    }

    [Serializable]
    public class CronJobInfo
    {
        public string id;
        public string name;
        public string schedule;
        public string prompt;
        public bool enabled;
        public string last_run;
        public string next_run;
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

        public async Task<PaginatedSessions> ListSessions(int limit = 40, int minMessages = 0)
        {
            return await Get<PaginatedSessions>(
                "/api/sessions?limit=" + limit + "&offset=0&min_messages=" + minMessages);
        }

        public async Task<JObject> SearchSessions(string query)
        {
            return await Get<JObject>(
                "/api/sessions/search?q=" + UnityWebRequest.EscapeURL(query));
        }

        public async Task<JObject> GetSessionMessages(string sessionId)
        {
            return await Get<JObject>(
                "/api/sessions/" + UnityWebRequest.EscapeURL(sessionId) + "/messages");
        }

        public async Task DeleteSession(string sessionId)
        {
            await Delete("/api/sessions/" + UnityWebRequest.EscapeURL(sessionId));
        }

        public async Task RenameSession(string sessionId, string title)
        {
            await Patch<object>("/api/sessions/" + UnityWebRequest.EscapeURL(sessionId),
                new { title });
        }

        // === Model ===

        public async Task<JObject> GetModelInfo()
        {
            return await Get<JObject>("/api/model/info");
        }

        public async Task<ModelOptionsResponse> GetModelOptions()
        {
            return await Get<ModelOptionsResponse>("/api/model/options");
        }

        public async Task SetModel(string model)
        {
            await Post("/api/model/set", new { model });
        }

        // === Config ===

        public async Task<JObject> GetConfig()
        {
            return await Get<JObject>("/api/config");
        }

        public async Task SaveConfig(JObject config)
        {
            await Put("/api/config", new { config });
        }

        public async Task<JObject> GetConfigSchema()
        {
            return await Get<JObject>("/api/config/schema");
        }

        // === Skills ===

        public async Task<SkillInfo[]> GetSkills()
        {
            return await Get<SkillInfo[]>("/api/skills");
        }

        public async Task ToggleSkill(string name, bool enabled)
        {
            await Put("/api/skills/toggle", new { name, enabled });
        }

        // === Toolsets ===

        public async Task<ToolsetInfo[]> GetToolsets()
        {
            return await Get<ToolsetInfo[]>("/api/tools/toolsets");
        }

        // === Cron ===

        public async Task<CronJobInfo[]> GetCronJobs()
        {
            return await Get<CronJobInfo[]>("/api/cron/jobs");
        }

        public async Task<CronJobInfo> CreateCronJob(object payload)
        {
            return await Post<CronJobInfo>("/api/cron/jobs", payload);
        }

        public async Task DeleteCronJob(string jobId)
        {
            await Delete("/api/cron/jobs/" + UnityWebRequest.EscapeURL(jobId));
        }

        // === Logs ===

        public async Task<JObject> GetLogs(string level = null, int lines = 100)
        {
            var query = "/api/logs?lines=" + lines;
            if (!string.IsNullOrEmpty(level) && level != "ALL")
                query += "&level=" + UnityWebRequest.EscapeURL(level);
            return await Get<JObject>(query);
        }

        // === Analytics ===

        public async Task<JObject> GetUsageAnalytics(int days = 30)
        {
            return await Get<JObject>("/api/analytics/usage?days=" + days);
        }

        // === Status ===

        public async Task<StatusResponse> GetStatus()
        {
            return await Get<StatusResponse>("/api/status");
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

        private async Task<T> Post<T>(string path, object body)
        {
            var json = await PostRaw(path, body);
            return JsonConvert.DeserializeObject<T>(json);
        }

        private async Task<string> PostRaw(string path, object body)
        {
            var url = _baseUrl + path;
            var bodyJson = JsonConvert.SerializeObject(body);
            var bodyBytes = Encoding.UTF8.GetBytes(bodyJson);

            using (var request = new UnityWebRequest(url, "POST"))
            {
                request.uploadHandler = new UploadHandlerRaw(bodyBytes);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                ApplyHeaders(request);

                var op = request.SendWebRequest();
                while (!op.isDone)
                    await Task.Yield();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    string error = "REST POST failed: " + request.error + " [" + path + "]";
                    Debug.LogError("[HermesRest] " + error);
                    throw new Exception(error);
                }
                return request.downloadHandler.text;
            }
        }

        private async Task Post(string path, object body)
        {
            await PostRaw(path, body);
        }

        private async Task<T> Put<T>(string path, object body)
        {
            var json = await PutRaw(path, body);
            return JsonConvert.DeserializeObject<T>(json);
        }

        private async Task<string> PutRaw(string path, object body)
        {
            var url = _baseUrl + path;
            var bodyJson = JsonConvert.SerializeObject(body);
            var bodyBytes = Encoding.UTF8.GetBytes(bodyJson);

            using (var request = new UnityWebRequest(url, "PUT"))
            {
                request.uploadHandler = new UploadHandlerRaw(bodyBytes);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                ApplyHeaders(request);

                var op = request.SendWebRequest();
                while (!op.isDone)
                    await Task.Yield();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    string error = "REST PUT failed: " + request.error + " [" + path + "]";
                    Debug.LogError("[HermesRest] " + error);
                    throw new Exception(error);
                }
                return request.downloadHandler.text;
            }
        }

        private async Task Put(string path, object body)
        {
            await PutRaw(path, body);
        }

        private async Task<T> Patch<T>(string path, object body)
        {
            var url = _baseUrl + path;
            var bodyJson = JsonConvert.SerializeObject(body);
            var bodyBytes = Encoding.UTF8.GetBytes(bodyJson);

            using (var request = new UnityWebRequest(url, "PATCH"))
            {
                request.uploadHandler = new UploadHandlerRaw(bodyBytes);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                ApplyHeaders(request);

                var op = request.SendWebRequest();
                while (!op.isDone)
                    await Task.Yield();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    string error = "REST PATCH failed: " + request.error + " [" + path + "]";
                    Debug.LogError("[HermesRest] " + error);
                    throw new Exception(error);
                }
                return JsonConvert.DeserializeObject<T>(request.downloadHandler.text);
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
