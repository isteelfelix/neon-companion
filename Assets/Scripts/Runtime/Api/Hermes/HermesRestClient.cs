// HermesRestClient.cs - REST management client for Hermes backend
// Handles Hermes management endpoints via HTTP.
// Base URL: configured at runtime from provider settings

using System;
using System.Collections.Generic;
using System.Text;
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
        public int? total_models;
        public bool? is_current;
        public bool? authenticated;
        public string auth_type;
        public string key_env;
        public bool? is_user_defined;
        public string warning;
        public JObject pricing;
        public bool? free_tier;
        public string[] unavailable_models;
        public JObject capabilities;
    }

    [Serializable]
    public class ModelOptionsResponse
    {
        public string model;
        public string provider;
        public ModelOptionProvider[] providers;
    }

    [Serializable]
    public class PlatformStatus
    {
        public string error_code;
        public string error_message;
        public string state;
        public string updated_at;
    }

    [Serializable]
    public class StatusResponse
    {
        public int active_sessions;
        public string config_path;
        public int config_version;
        public string env_path;
        public string gateway_exit_reason;
        public string gateway_health_url;
        public long? gateway_pid;
        public Dictionary<string, PlatformStatus> gateway_platforms;
        public bool gateway_running;
        public string gateway_state;
        public string gateway_updated_at;
        public string hermes_home;
        public int latest_config_version;
        public string release_date;
        public string version;
    }

    [Serializable]
    public class ModelInfoResponse
    {
        public int? auto_context_length;
        public JObject capabilities;
        public int? config_context_length;
        public int? effective_context_length;
        public string model;
        public string provider;
    }

    [Serializable]
    public class SkillInfo
    {
        public string category;
        public string description;
        public bool enabled;
        public string name;
        public int? usage;
        public string provenance;
    }

    [Serializable]
    public class ToolsetInfo
    {
        public bool configured;
        public string description;
        public bool enabled;
        public string label;
        public string name;
        public string[] tools;
    }

    [Serializable]
    public class CronJobSchedule
    {
        public string display;
        public string expr;
        public string kind;
    }

    [Serializable]
    public class CronJob
    {
        public string deliver;
        public bool enabled;
        public string id;
        public string last_error;
        public string last_run_at;
        public string model;
        public string name;
        public string next_run_at;
        public bool? no_agent;
        public string prompt;
        public string provider;
        public CronJobSchedule schedule;
        public string schedule_display;
        public string script;
        public string state;
    }

    [Serializable]
    public class HermesConfigRecord : Dictionary<string, JToken>
    {
    }

    public class HermesEndpointMissingException : Exception
    {
        public readonly string Path;
        public readonly long StatusCode;

        public HermesEndpointMissingException(string path, long statusCode, string message) : base(message)
        {
            Path = path;
            StatusCode = statusCode;
        }
    }

    // === HermesRestClient ===

    public class HermesRestClient
    {
        private const int DefaultRequestTimeoutSeconds = 30;
        private const int StatusRequestTimeoutSeconds = 15;
        private const int StartupRequestTimeoutSeconds = 60;
        private const int SessionListRequestTimeoutSeconds = 60;
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

        // Desktop hermes.ts: listSessions
        public async Task<PaginatedSessions> ListSessions(
            int limit = 40,
            int minMessages = 0,
            int offset = 0,
            string archived = "exclude",
            string order = "recent")
        {
            return await Get<PaginatedSessions>(
                "/api/sessions?limit=" + limit +
                "&offset=" + offset +
                "&min_messages=" + Math.Max(0, minMessages) +
                "&archived=" + EscapeQueryValue(archived) +
                "&order=" + EscapeQueryValue(order),
                SessionListRequestTimeoutSeconds);
        }

        // Desktop hermes.ts: getSessionMessages
        public async Task<JToken> GetSessionMessages(string sessionId)
        {
            // Returns the full structured history (incl. tool_calls) - array or { messages: [...] }.
            return await Get<JToken>(
                "/api/sessions/" + UnityWebRequest.EscapeURL(sessionId) + "/messages");
        }

        // Desktop hermes.ts: deleteSession
        public async Task DeleteSession(string sessionId)
        {
            await Delete("/api/sessions/" + UnityWebRequest.EscapeURL(sessionId));
        }

        // === Status ===

        // Desktop hermes.ts: getStatus
        public async Task<StatusResponse> GetStatus()
        {
            return await Get<StatusResponse>("/api/status", StatusRequestTimeoutSeconds);
        }

        // === Model ===

        // Desktop hermes.ts: getGlobalModelInfo
        public async Task<ModelInfoResponse> GetModelInfo()
        {
            return await Get<ModelInfoResponse>("/api/model/info", StartupRequestTimeoutSeconds);
        }

        // Desktop hermes.ts: getGlobalModelOptions
        public async Task<ModelOptionsResponse> GetModelOptions(
            bool refresh = false,
            bool includeUnconfigured = false,
            bool explicitOnly = true)
        {
            List<string> query = new List<string>();
            if (refresh)
                query.Add("refresh=1");
            if (includeUnconfigured)
                query.Add("include_unconfigured=1");
            if (explicitOnly)
                query.Add("explicit_only=1");

            string path = "/api/model/options";
            if (query.Count > 0)
                path += "?" + string.Join("&", query.ToArray());

            return await Get<ModelOptionsResponse>(path, StartupRequestTimeoutSeconds);
        }

        // === Config ===

        // Desktop hermes.ts: getHermesConfig / getHermesConfigRecord
        public async Task<HermesConfigRecord> GetConfig()
        {
            return await Get<HermesConfigRecord>("/api/config", StartupRequestTimeoutSeconds);
        }

        // === Skills ===

        // Desktop hermes.ts: getSkills
        public async Task<SkillInfo[]> GetSkills()
        {
            return await Get<SkillInfo[]>("/api/skills", StartupRequestTimeoutSeconds);
        }

        // === Tools ===

        // Desktop hermes.ts: getToolsets
        public async Task<ToolsetInfo[]> GetToolsets()
        {
            return await Get<ToolsetInfo[]>("/api/tools/toolsets", StartupRequestTimeoutSeconds);
        }

        // === Cron ===

        // Desktop hermes.ts: getCronJobs
        public async Task<CronJob[]> GetCronJobs(string profile = null)
        {
            string suffix = string.IsNullOrEmpty(profile) ? "" : "?profile=" + EscapeQueryValue(profile);
            return await Get<CronJob[]>("/api/cron/jobs" + suffix, StartupRequestTimeoutSeconds);
        }

        // === HTTP Helpers ===

        private async Task<T> Get<T>(string path, int timeoutSeconds = DefaultRequestTimeoutSeconds)
        {
            string json = await GetRaw(path, timeoutSeconds);
            return JsonConvert.DeserializeObject<T>(json);
        }

        private async Task<string> GetRaw(string path, int timeoutSeconds = DefaultRequestTimeoutSeconds)
        {
            return await SendRaw("GET", path, null, timeoutSeconds);
        }

        private async Task<T> Post<T>(string path, object body = null, int timeoutSeconds = DefaultRequestTimeoutSeconds)
        {
            string json = await SendJson("POST", path, body, timeoutSeconds);
            return JsonConvert.DeserializeObject<T>(json);
        }

        private async Task<T> Patch<T>(string path, object body = null, int timeoutSeconds = DefaultRequestTimeoutSeconds)
        {
            string json = await SendJson("PATCH", path, body, timeoutSeconds);
            return JsonConvert.DeserializeObject<T>(json);
        }

        private async Task Delete(string path, int timeoutSeconds = DefaultRequestTimeoutSeconds)
        {
            await SendRaw("DELETE", path, null, timeoutSeconds);
        }

        private async Task<string> SendJson(string method, string path, object body, int timeoutSeconds)
        {
            return await SendRaw(method, path, body, timeoutSeconds);
        }

        private async Task<string> SendRaw(string method, string path, object body, int timeoutSeconds)
        {
            string url = _baseUrl + path;
            using (UnityWebRequest request = CreateRequest(url, method, body))
            {
                request.timeout = timeoutSeconds;
                ApplyHeaders(request);
                UnityWebRequestAsyncOperation op = request.SendWebRequest();
                while (!op.isDone)
                    await Task.Yield();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    string response = request.downloadHandler != null ? request.downloadHandler.text : null;
                    if (IsEndpointMissing(request.responseCode, response))
                    {
                        string message = "Hermes REST endpoint missing: " + path;
                        Debug.LogWarning("[HermesRest] " + message);
                        throw new HermesEndpointMissingException(path, request.responseCode, message);
                    }

                    string detail = string.IsNullOrEmpty(response) ? request.error : response;
                    string error = "REST " + method + " failed: " + detail + " [" + path + "]";
                    Debug.LogError("[HermesRest] " + error);
                    throw new Exception(error);
                }

                return request.downloadHandler != null ? request.downloadHandler.text : "";
            }
        }

        private UnityWebRequest CreateRequest(string url, string method, object body)
        {
            if (method == "GET")
                return UnityWebRequest.Get(url);

            UnityWebRequest request = new UnityWebRequest(url, method);
            request.downloadHandler = new DownloadHandlerBuffer();

            if (body != null)
            {
                string json = JsonConvert.SerializeObject(body);
                byte[] data = Encoding.UTF8.GetBytes(json);
                request.uploadHandler = new UploadHandlerRaw(data);
                request.SetRequestHeader("Content-Type", "application/json");
            }

            return request;
        }

        private void ApplyHeaders(UnityWebRequest request)
        {
            if (!string.IsNullOrEmpty(_token))
                request.SetRequestHeader("Authorization", "Bearer " + _token);
        }

        private bool IsEndpointMissing(long statusCode, string responseText)
        {
            if (statusCode != 404 || string.IsNullOrEmpty(responseText))
                return false;

            return responseText.IndexOf("No such API endpoint", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   responseText.IndexOf("endpoint is likely missing", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private string EscapeQueryValue(string value)
        {
            return UnityWebRequest.EscapeURL(value ?? "");
        }
    }
}
