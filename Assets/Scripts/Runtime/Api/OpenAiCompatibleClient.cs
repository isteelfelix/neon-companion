using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Api.Adapters;
using NeonCompanion.Runtime.Api.Models;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;
using UnityEngine;
using UnityEngine.Networking;

namespace NeonCompanion.Runtime.Api
{
    /// <summary>
    /// Single OpenAI-compatible HTTP implementation. The only generation endpoint is
    /// POST /responses; providers that do not implement it are incompatible.
    /// </summary>
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

            string endpoint = BuildResponsesEndpoint(provider.baseUrl);
            bool omitTemperature = RequiresTemperatureOmission(provider);
            while (true)
            {
                string payload = BuildResponsesPayloadJson(request, false, omitTemperature);
                using (UnityWebRequest webRequest = CreatePostRequest(endpoint, payload, provider.apiKey))
                {
                    await SendAsync(webRequest, cancellationToken);
                    if (ShouldRetryWithoutTemperature(webRequest, omitTemperature))
                    {
                        omitTemperature = true;
                        continue;
                    }
                    ThrowIfRequestFailed(webRequest, "API request failed");

                    AiChatResponse response = ParseResponsesResponse(webRequest.downloadHandler != null
                        ? webRequest.downloadHandler.text
                        : string.Empty);
                    EnsureCompletedResponse(response, "Response request failed");
                    if (string.IsNullOrWhiteSpace(response.model))
                        response.model = request.model ?? string.Empty;
                    return response;
                }
            }
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

            string endpoint = BuildResponsesEndpoint(provider.baseUrl);
            bool omitTemperature = RequiresTemperatureOmission(provider);
            while (true)
            {
                string payload = BuildResponsesPayloadJson(request, true, omitTemperature);
                using (UnityWebRequest webRequest = CreatePostRequest(endpoint, payload, provider.apiKey))
                {
                    bool emittedAnyToken = false;
                    ResponsesStreamReducer reducer = new ResponsesStreamReducer(token =>
                    {
                        emittedAnyToken = true;
                        if (onToken != null)
                            onToken(token);
                    });
                    UnityWebRequestAsyncOperation operation = webRequest.SendWebRequest();
                    int consumedLength = 0;
                    ResponsesSseParser parser = new ResponsesSseParser();

                    while (!operation.isDone)
                    {
                        ThrowIfCancelled(webRequest, cancellationToken);
                        string allText = webRequest.downloadHandler != null ? webRequest.downloadHandler.text : string.Empty;
                        ConsumeNewStreamText(allText, ref consumedLength, parser, reducer, false);
                        await Task.Yield();
                    }

                    ThrowIfCancelled(webRequest, cancellationToken);
                    string finalText = webRequest.downloadHandler != null ? webRequest.downloadHandler.text : string.Empty;
                    ConsumeNewStreamText(finalText, ref consumedLength, parser, reducer, true);
                    if (!emittedAnyToken && ShouldRetryWithoutTemperature(webRequest, omitTemperature))
                    {
                        omitTemperature = true;
                        continue;
                    }
                    ThrowIfRequestFailed(webRequest, "Streaming request failed");

                    AiChatResponse response = reducer.BuildResponse(request.model);
                    EnsureCompletedResponse(response, "Streaming response failed");
                    return response;
                }
            }
        }

        public async Task<ConnectionTestResult> TestConnectionAsync(
            ProviderConfig provider,
            CancellationToken cancellationToken = default)
        {
            ProviderValidator.ValidateForConnection(provider);
            string endpoint = BuildModelsEndpoint(provider.baseUrl);
            long startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            using (UnityWebRequest webRequest = UnityWebRequest.Get(endpoint))
            {
                ApplyAuthorization(webRequest, provider.apiKey);
                try
                {
                    await SendAsync(webRequest, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return new ConnectionTestResult(false, "Cancelled");
                }

                long latency = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startedAt;
                if (webRequest.responseCode == 401)
                    return new ConnectionTestResult(false, "Reachable but unauthorized · " + latency + " ms", latency);

                if (webRequest.result != UnityWebRequest.Result.Success && webRequest.responseCode != 200)
                {
                    return new ConnectionTestResult(false,
                        (webRequest.error ?? "error") + " (HTTP " + webRequest.responseCode + ") · " + latency + " ms",
                        latency);
                }

                IReadOnlyList<string> models = ParseModelIds(webRequest.downloadHandler != null
                    ? webRequest.downloadHandler.text
                    : string.Empty);
                string note = models == null || models.Count == 0 ? " · список моделей не распознан" : string.Empty;
                return new ConnectionTestResult(true, "OK · " + latency + " ms" + note, latency, models);
            }
        }

        public Task<ModelSwitchResult> ApplySessionModelAsync(
            ProviderConfig provider,
            string targetModel,
            string providerSessionId = null,
            CancellationToken cancellationToken = default)
        {
            ProviderValidator.Validate(provider);
            if (string.IsNullOrWhiteSpace(targetModel))
                throw new ArgumentException("Target model is required.", nameof(targetModel));

            // Responses selects its model per request. Hermes model switching is handled by
            // its WebSocket transport, not by this HTTP client.
            return Task.FromResult(new ModelSwitchResult(true, targetModel.Trim(), targetModel.Trim(), providerSessionId));
        }

        private static UnityWebRequest CreatePostRequest(string endpoint, string payload, string apiKey)
        {
            UnityWebRequest request = new UnityWebRequest(endpoint, UnityWebRequest.kHttpVerbPOST);
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Accept", "application/json, text/event-stream");
            ApplyAuthorization(request, apiKey);
            return request;
        }

        private static void ApplyAuthorization(UnityWebRequest request, string apiKey)
        {
            if (request != null && !string.IsNullOrWhiteSpace(apiKey))
                request.SetRequestHeader("Authorization", "Bearer " + apiKey.Trim());
        }

        private static async Task SendAsync(UnityWebRequest webRequest, CancellationToken cancellationToken)
        {
            UnityWebRequestAsyncOperation operation = webRequest.SendWebRequest();
            while (!operation.isDone)
            {
                ThrowIfCancelled(webRequest, cancellationToken);
                await Task.Yield();
            }
            ThrowIfCancelled(webRequest, cancellationToken);
        }

        private static void ThrowIfCancelled(UnityWebRequest request, CancellationToken cancellationToken)
        {
            if (!cancellationToken.IsCancellationRequested)
                return;

            if (request != null)
                request.Abort();
            cancellationToken.ThrowIfCancellationRequested();
        }

        private static void ThrowIfRequestFailed(UnityWebRequest webRequest, string prefix)
        {
            if (webRequest.result == UnityWebRequest.Result.Success &&
                webRequest.responseCode >= 200 && webRequest.responseCode < 300)
            {
                return;
            }

            throw new ResponsesApiException(prefix + ": " + ParseErrorMessage(webRequest));
        }

        private static bool RequiresTemperatureOmission(ProviderConfig provider)
        {
            IProviderAdapter adapter = ProviderAdapterFactory.Create(provider != null ? provider.backendType : null);
            ProviderCapabilities capabilities = adapter.GetCapabilities();
            return capabilities != null && capabilities.RequiresTemperatureOmission;
        }

        private static bool ShouldRetryWithoutTemperature(UnityWebRequest webRequest, bool temperatureOmitted)
        {
            if (temperatureOmitted || webRequest == null || webRequest.responseCode != 400)
                return false;

            string body = webRequest.downloadHandler != null ? webRequest.downloadHandler.text : null;
            if (string.IsNullOrWhiteSpace(body))
                return false;

            try
            {
                ResponsesApiResponse response = JsonUtility.FromJson<ResponsesApiResponse>(body);
                ResponsesApiError error = response != null ? response.error : null;
                if (error == null ||
                    !string.Equals(error.param, "temperature", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                bool unsupportedCode =
                    string.Equals(error.code, "unsupported_parameter", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(error.code, "unsupported_value", StringComparison.OrdinalIgnoreCase);
                bool unsupportedMessage = !string.IsNullOrWhiteSpace(error.message) &&
                    error.message.IndexOf("unsupported", StringComparison.OrdinalIgnoreCase) >= 0;
                return unsupportedCode || unsupportedMessage;
            }
            catch
            {
                return false;
            }
        }

        private static string BuildResponsesEndpoint(string baseUrl)
        {
            return BuildApiRoot(baseUrl) + "/responses";
        }

        private static string BuildModelsEndpoint(string baseUrl)
        {
            return BuildApiRoot(baseUrl) + "/models";
        }

        private static string BuildApiRoot(string baseUrl)
        {
            string root = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            if (root.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
                root = root.Substring(0, root.Length - "/responses".Length);
            else if (root.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
                root = root.Substring(0, root.Length - "/models".Length);

            if (string.IsNullOrWhiteSpace(root))
                throw new InvalidOperationException("Base URL is not configured.");
            return root;
        }

        private static string BuildResponsesPayloadJson(
            AiChatRequest request,
            bool stream,
            bool omitTemperature)
        {
            List<AiChatMessage> messages = request.messages ?? new List<AiChatMessage>();
            StringBuilder sb = new StringBuilder(1024);
            sb.Append('{');
            AppendJsonProperty(sb, "model", request.model, true);

            string instructions = BuildInstructions(request.systemPrompt, messages);
            if (!string.IsNullOrWhiteSpace(instructions))
            {
                sb.Append(",\"instructions\":");
                AppendJsonString(sb, instructions);
            }

            sb.Append(",\"input\":[");
            bool firstInput = true;
            for (int i = 0; i < messages.Count; i++)
                AppendInputItemsJson(sb, messages[i], ref firstInput);
            sb.Append(']');

            AppendToolsJson(sb, request.tools);

            sb.Append(",\"max_output_tokens\":").Append(Math.Max(1, request.maxTokens));
            if (!omitTemperature)
                sb.Append(",\"temperature\":").Append(request.temperature.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            if (stream)
                sb.Append(",\"stream\":true");
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendToolsJson(StringBuilder sb, List<ToolDefinition> tools)
        {
            if (tools == null || tools.Count == 0)
                return;

            sb.Append(",\"tools\":[");
            bool firstTool = true;
            for (int i = 0; i < tools.Count; i++)
            {
                ToolDefinition tool = tools[i];
                if (tool == null || string.IsNullOrWhiteSpace(tool.name))
                    continue;

                if (!firstTool)
                    sb.Append(',');
                firstTool = false;
                sb.Append("{\"type\":\"function\",\"name\":");
                AppendJsonString(sb, tool.name);
                sb.Append(",\"description\":");
                AppendJsonString(sb, tool.description ?? string.Empty);
                sb.Append(",\"parameters\":");
                AppendToolParametersJson(sb, tool.parameters);
                sb.Append('}');
            }
            sb.Append(']');
        }

        private static void AppendToolParametersJson(StringBuilder sb, ToolParameterSchema schema)
        {
            sb.Append("{\"type\":");
            AppendJsonString(sb, schema != null && !string.IsNullOrWhiteSpace(schema.type) ? schema.type : "object");
            sb.Append(",\"properties\":{");
            bool firstProperty = true;
            if (schema != null && schema.properties != null)
            {
                foreach (KeyValuePair<string, ToolParameterProperty> property in schema.properties)
                {
                    if (string.IsNullOrWhiteSpace(property.Key))
                        continue;
                    if (!firstProperty)
                        sb.Append(',');
                    firstProperty = false;
                    AppendJsonString(sb, property.Key);
                    sb.Append(":{\"type\":");
                    AppendJsonString(sb, property.Value != null && !string.IsNullOrWhiteSpace(property.Value.type)
                        ? property.Value.type
                        : "string");
                    sb.Append(",\"description\":");
                    AppendJsonString(sb, property.Value != null ? property.Value.description : string.Empty);
                    sb.Append('}');
                }
            }
            sb.Append("},\"required\":[");
            bool firstRequired = true;
            if (schema != null && schema.required != null)
            {
                for (int i = 0; i < schema.required.Count; i++)
                {
                    string name = schema.required[i];
                    if (string.IsNullOrWhiteSpace(name))
                        continue;
                    if (!firstRequired)
                        sb.Append(',');
                    firstRequired = false;
                    AppendJsonString(sb, name);
                }
            }
            sb.Append("]}");
        }

        private static string BuildInstructions(string systemPrompt, List<AiChatMessage> messages)
        {
            StringBuilder sb = new StringBuilder();
            AppendInstructionPart(sb, systemPrompt);
            if (messages == null)
                return sb.ToString();

            for (int i = 0; i < messages.Count; i++)
            {
                AiChatMessage message = messages[i];
                if (message != null && string.Equals(message.role, "system", StringComparison.OrdinalIgnoreCase))
                    AppendInstructionPart(sb, message.content);
            }
            return sb.ToString();
        }

        private static void AppendInstructionPart(StringBuilder sb, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            if (sb.Length > 0)
                sb.Append("\n\n");
            sb.Append(value.Trim());
        }

        private static void AppendInputItemsJson(StringBuilder sb, AiChatMessage message, ref bool firstInput)
        {
            if (message == null || string.IsNullOrWhiteSpace(message.role))
                return;

            if (string.Equals(message.role, "system", StringComparison.OrdinalIgnoreCase))
                return;

            if (string.Equals(message.role, "tool", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(message.tool_call_id))
            {
                AppendInputSeparator(sb, ref firstInput);
                sb.Append("{\"type\":\"function_call_output\",\"call_id\":");
                AppendJsonString(sb, message.tool_call_id);
                sb.Append(",\"output\":");
                AppendJsonString(sb, message.content ?? string.Empty);
                sb.Append('}');
                return;
            }

            if (string.Equals(message.role, "assistant", StringComparison.OrdinalIgnoreCase) &&
                AppendResponseOutputItemsJson(sb, message.responseOutput, ref firstInput))
            {
                return;
            }

            if (message.tool_calls != null)
            {
                for (int i = 0; i < message.tool_calls.Count; i++)
                    AppendFunctionCallInputJson(sb, message.tool_calls[i], ref firstInput);
            }

            bool hasText = !string.IsNullOrWhiteSpace(message.content);
            bool hasAttachments = message.attachments != null && message.attachments.Count > 0;
            if (!hasText && !hasAttachments)
                return;

            string role = NormalizeInputRole(message.role);
            AppendInputSeparator(sb, ref firstInput);
            sb.Append("{\"type\":\"message\",\"role\":");
            AppendJsonString(sb, role);
            sb.Append(",\"content\":[");
            bool firstPart = true;
            if (hasText)
            {
                sb.Append("{\"type\":");
                AppendJsonString(sb, string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)
                    ? "output_text"
                    : "input_text");
                sb.Append(",\"text\":");
                AppendJsonString(sb, message.content);
                sb.Append('}');
                firstPart = false;
            }

            if (message.attachments != null)
            {
                for (int i = 0; i < message.attachments.Count; i++)
                {
                    ResponsesAttachmentPayload payload;
                    string error;
                    if (!ResponsesAttachmentPayloadBuilder.TryBuild(message.attachments[i], out payload, out error))
                        throw new InvalidOperationException(error ?? "Unable to prepare attachment.");
                    if (!firstPart)
                        sb.Append(',');
                    AppendAttachmentPartJson(sb, payload);
                    firstPart = false;
                }
            }
            sb.Append("]}");
        }

        private static bool AppendResponseOutputItemsJson(
            StringBuilder sb,
            List<ResponsesOutputItem> items,
            ref bool firstInput)
        {
            if (items == null || items.Count == 0)
                return false;

            bool appended = false;
            for (int i = 0; i < items.Count; i++)
            {
                ResponsesOutputItem item = items[i];
                if (item == null)
                    continue;
                if (!string.Equals(item.type, "reasoning", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(item.type, "message", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(item.type, "function_call", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                AppendInputSeparator(sb, ref firstInput);
                sb.Append("{\"type\":");
                AppendJsonString(sb, item.type);
                AppendOptionalJsonString(sb, "id", item.id);
                AppendOptionalJsonString(sb, "status", item.status);

                if (string.Equals(item.type, "reasoning", StringComparison.OrdinalIgnoreCase))
                {
                    AppendOptionalJsonString(sb, "encrypted_content", item.encrypted_content);
                    AppendResponseContentPartsJson(sb, "summary", item.summary);
                }
                else if (string.Equals(item.type, "message", StringComparison.OrdinalIgnoreCase))
                {
                    AppendOptionalJsonString(sb, "role", string.IsNullOrWhiteSpace(item.role) ? "assistant" : item.role);
                    AppendResponseContentPartsJson(sb, "content", item.content);
                }
                else
                {
                    AppendOptionalJsonString(sb, "call_id", item.call_id);
                    AppendOptionalJsonString(sb, "name", item.name);
                    AppendOptionalJsonString(sb, "arguments", item.arguments ?? "{}");
                }
                sb.Append('}');
                appended = true;
            }
            return appended;
        }

        private static void AppendResponseContentPartsJson(
            StringBuilder sb,
            string propertyName,
            ResponsesContentPart[] parts)
        {
            sb.Append(',');
            AppendJsonString(sb, propertyName);
            sb.Append(":[");
            bool first = true;
            if (parts != null)
            {
                for (int i = 0; i < parts.Length; i++)
                {
                    ResponsesContentPart part = parts[i];
                    if (part == null || string.IsNullOrWhiteSpace(part.type))
                        continue;
                    if (!first)
                        sb.Append(',');
                    first = false;
                    sb.Append("{\"type\":");
                    AppendJsonString(sb, part.type);
                    AppendOptionalJsonString(sb, "text", part.text);
                    AppendOptionalJsonString(sb, "refusal", part.refusal);
                    sb.Append('}');
                }
            }
            sb.Append(']');
        }

        private static void AppendOptionalJsonString(StringBuilder sb, string name, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            sb.Append(',');
            AppendJsonString(sb, name);
            sb.Append(':');
            AppendJsonString(sb, value);
        }

        private static void AppendFunctionCallInputJson(StringBuilder sb, ToolCall call, ref bool firstInput)
        {
            if (call == null || call.function == null || string.IsNullOrWhiteSpace(call.function.name))
                return;
            AppendInputSeparator(sb, ref firstInput);
            sb.Append("{\"type\":\"function_call\",\"call_id\":");
            AppendJsonString(sb, call.id ?? string.Empty);
            sb.Append(",\"name\":");
            AppendJsonString(sb, call.function.name);
            sb.Append(",\"arguments\":");
            AppendJsonString(sb, call.function.arguments ?? "{}");
            sb.Append('}');
        }

        private static void AppendInputSeparator(StringBuilder sb, ref bool firstInput)
        {
            if (!firstInput)
                sb.Append(',');
            firstInput = false;
        }

        private static string NormalizeInputRole(string role)
        {
            if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
                return "assistant";
            if (string.Equals(role, "developer", StringComparison.OrdinalIgnoreCase))
                return "developer";
            return "user";
        }

        private static void AppendAttachmentPartJson(StringBuilder sb, ResponsesAttachmentPayload payload)
        {
            if (payload == null || string.IsNullOrWhiteSpace(payload.type))
                throw new InvalidOperationException("Attachment payload is invalid.");
            sb.Append("{\"type\":");
            AppendJsonString(sb, payload.type);
            if (string.Equals(payload.type, "input_image", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append(",\"image_url\":");
                AppendJsonString(sb, payload.image_url);
            }
            else if (string.Equals(payload.type, "input_file", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append(",\"file_data\":");
                AppendJsonString(sb, payload.file_data);
                sb.Append(",\"filename\":");
                AppendJsonString(sb, payload.filename);
            }
            else
            {
                throw new InvalidOperationException("Unsupported Responses attachment type: " + payload.type);
            }
            sb.Append('}');
        }

        private static void AppendJsonProperty(StringBuilder sb, string name, string value, bool first)
        {
            if (!first)
                sb.Append(',');
            AppendJsonString(sb, name);
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

        private static AiChatResponse ParseResponsesResponse(string rawJson)
        {
            if (string.IsNullOrWhiteSpace(rawJson))
                throw new ResponsesApiException("Responses API returned an empty body.");

            ResponsesApiResponse source;
            try
            {
                source = JsonUtility.FromJson<ResponsesApiResponse>(rawJson);
            }
            catch (Exception ex)
            {
                throw new ResponsesApiException("Responses API returned invalid JSON: " + ex.Message);
            }

            if (source == null)
                throw new ResponsesApiException("Responses API returned an invalid response object.");
            return ToAiChatResponse(source, null);
        }

        private static AiChatResponse ToAiChatResponse(ResponsesApiResponse source, string fallbackModel)
        {
            List<ResponsesOutputItem> output = CopyOutput(source != null ? source.output : null);
            AiChatResponse result = new AiChatResponse();
            result.id = source != null ? source.id : string.Empty;
            result.model = !string.IsNullOrWhiteSpace(source != null ? source.model : null)
                ? source.model
                : (fallbackModel ?? string.Empty);
            result.status = source != null ? source.status : null;
            result.content = ExtractOutputText(output);
            result.responseOutput = output;
            result.usage = source != null ? source.usage : null;
            result.error = source != null ? source.error : null;
            result.incompleteDetails = source != null ? source.incomplete_details : null;
            result.tool_calls = ExtractToolCalls(output);
            result.receivedAtUtc = DateTime.UtcNow;
            return result;
        }

        private static List<ResponsesOutputItem> CopyOutput(ResponsesOutputItem[] output)
        {
            if (output == null || output.Length == 0)
                return new List<ResponsesOutputItem>();
            List<ResponsesOutputItem> result = new List<ResponsesOutputItem>(output.Length);
            for (int i = 0; i < output.Length; i++)
            {
                if (output[i] != null)
                    result.Add(output[i]);
            }
            return result;
        }

        private static string ExtractOutputText(List<ResponsesOutputItem> output)
        {
            if (output == null)
                return string.Empty;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < output.Count; i++)
            {
                ResponsesOutputItem item = output[i];
                if (item == null || !string.Equals(item.type, "message", StringComparison.OrdinalIgnoreCase) || item.content == null)
                    continue;
                for (int j = 0; j < item.content.Length; j++)
                {
                    ResponsesContentPart part = item.content[j];
                    if (part == null)
                        continue;
                    if (string.Equals(part.type, "output_text", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrEmpty(part.text))
                        sb.Append(part.text);
                    else if (string.Equals(part.type, "refusal", StringComparison.OrdinalIgnoreCase) &&
                             !string.IsNullOrEmpty(part.refusal))
                        sb.Append(part.refusal);
                }
            }
            return sb.ToString();
        }

        private static List<ToolCall> ExtractToolCalls(List<ResponsesOutputItem> output)
        {
            if (output == null)
                return null;
            List<ToolCall> result = new List<ToolCall>();
            for (int i = 0; i < output.Count; i++)
            {
                ResponsesOutputItem item = output[i];
                if (item == null || !string.Equals(item.type, "function_call", StringComparison.OrdinalIgnoreCase))
                    continue;
                ToolCall call = new ToolCall();
                call.id = !string.IsNullOrWhiteSpace(item.call_id) ? item.call_id : (item.id ?? string.Empty);
                call.type = "function";
                call.function = new ToolCallFunction();
                call.function.name = item.name ?? string.Empty;
                call.function.arguments = item.arguments ?? string.Empty;
                result.Add(call);
            }
            return result.Count > 0 ? result : null;
        }

        private static void EnsureCompletedResponse(AiChatResponse response, string prefix)
        {
            if (response == null)
                throw new ResponsesApiException(prefix + ": empty response.");
            if (string.Equals(response.status, "completed", StringComparison.OrdinalIgnoreCase))
                return;

            string details = response.error != null ? DescribeError(response.error) : string.Empty;
            if (string.IsNullOrWhiteSpace(details) && response.incompleteDetails != null &&
                !string.IsNullOrWhiteSpace(response.incompleteDetails.reason))
            {
                details = response.incompleteDetails.reason;
            }
            if (string.IsNullOrWhiteSpace(details))
                details = string.IsNullOrWhiteSpace(response.status) ? "missing terminal status" : response.status;
            if (!string.IsNullOrWhiteSpace(response.id))
                details += " (response " + response.id + ")";
            throw new ResponsesApiException(prefix + ": " + details);
        }

        private static string ParseErrorMessage(UnityWebRequest webRequest)
        {
            string message = null;
            string body = webRequest.downloadHandler != null ? webRequest.downloadHandler.text : null;
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    ResponsesApiResponse response = JsonUtility.FromJson<ResponsesApiResponse>(body);
                    if (response != null && response.error != null)
                        message = DescribeError(response.error);
                }
                catch { }
            }
            if (string.IsNullOrWhiteSpace(message))
                message = webRequest.error ?? "Unknown error";

            List<string> details = new List<string>();
            if (webRequest.responseCode > 0)
                details.Add("HTTP " + webRequest.responseCode);
            string requestId = webRequest.GetResponseHeader("x-request-id");
            if (!string.IsNullOrWhiteSpace(requestId))
                details.Add("request " + requestId);
            return details.Count == 0 ? message : message + " (" + string.Join(", ", details.ToArray()) + ")";
        }

        private static string DescribeError(ResponsesApiError error)
        {
            if (error == null)
                return null;
            List<string> parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(error.message)) parts.Add(error.message);
            if (!string.IsNullOrWhiteSpace(error.type)) parts.Add(error.type);
            if (!string.IsNullOrWhiteSpace(error.code) && !string.Equals(error.code, error.type, StringComparison.OrdinalIgnoreCase))
                parts.Add(error.code);
            return parts.Count == 0 ? null : string.Join(" · ", parts.ToArray());
        }

        private static IReadOnlyList<string> ParseModelIds(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;
            try
            {
                OpenAiModelsResponse response = JsonUtility.FromJson<OpenAiModelsResponse>(json);
                if (response == null || response.data == null || response.data.Length == 0)
                    return null;
                List<string> ids = new List<string>();
                for (int i = 0; i < response.data.Length; i++)
                {
                    if (response.data[i] != null && !string.IsNullOrWhiteSpace(response.data[i].id))
                        ids.Add(response.data[i].id);
                }
                return ids.Count > 0 ? ids : null;
            }
            catch
            {
                return null;
            }
        }

        private static void ConsumeNewStreamText(
            string allText,
            ref int consumedLength,
            ResponsesSseParser parser,
            ResponsesStreamReducer reducer,
            bool final)
        {
            if (string.IsNullOrEmpty(allText))
                return;
            if (allText.Length < consumedLength)
                consumedLength = 0;
            if (allText.Length > consumedLength)
            {
                parser.Feed(allText.Substring(consumedLength), false, reducer.Process);
                consumedLength = allText.Length;
            }
            if (final)
                parser.Feed(string.Empty, true, reducer.Process);
        }

        private sealed class ResponsesSseParser
        {
            private readonly StringBuilder _pending = new StringBuilder();
            private string _eventType;
            private readonly StringBuilder _data = new StringBuilder();

            public void Feed(string chunk, bool final, Action<string, string> onEvent)
            {
                if (!string.IsNullOrEmpty(chunk))
                    _pending.Append(chunk);

                int lineStart = 0;
                while (lineStart < _pending.Length)
                {
                    int lineEnd = FindLineEnd(_pending, lineStart, out int nextLineStart);
                    if (lineEnd < 0)
                        break;
                    string line = _pending.ToString(lineStart, lineEnd - lineStart);
                    ProcessLine(line, onEvent);
                    lineStart = nextLineStart;
                }
                if (lineStart > 0)
                    _pending.Remove(0, lineStart);

                if (final && _pending.Length > 0)
                {
                    ProcessLine(_pending.ToString(), onEvent);
                    _pending.Length = 0;
                }
                if (final)
                    Dispatch(onEvent);
            }

            private void ProcessLine(string line, Action<string, string> onEvent)
            {
                if (string.IsNullOrEmpty(line))
                {
                    Dispatch(onEvent);
                    return;
                }
                if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
                {
                    _eventType = line.Substring(6).Trim();
                    return;
                }
                if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    if (_data.Length > 0)
                        _data.Append('\n');
                    _data.Append(line.Substring(5).TrimStart());
                }
            }

            private void Dispatch(Action<string, string> onEvent)
            {
                if (_data.Length == 0)
                {
                    _eventType = null;
                    return;
                }
                string data = _data.ToString();
                string eventType = _eventType;
                _data.Length = 0;
                _eventType = null;
                if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
                    return;
                if (onEvent != null)
                    onEvent(eventType, data);
            }

            private static int FindLineEnd(StringBuilder text, int start, out int nextStart)
            {
                for (int i = start; i < text.Length; i++)
                {
                    if (text[i] == '\r')
                    {
                        nextStart = i + 1;
                        if (nextStart < text.Length && text[nextStart] == '\n')
                            nextStart++;
                        return i;
                    }
                    if (text[i] == '\n')
                    {
                        nextStart = i + 1;
                        return i;
                    }
                }
                nextStart = text.Length;
                return -1;
            }
        }

        private sealed class ResponsesStreamReducer
        {
            private readonly Action<string> _onToken;
            private readonly StringBuilder _text = new StringBuilder();
            private readonly List<ResponsesOutputItem> _output = new List<ResponsesOutputItem>();
            private string _id;
            private string _model;
            private string _status;
            private ResponsesUsage _usage;
            private ResponsesApiError _error;
            private ResponsesIncompleteDetails _incompleteDetails;
            private bool _completed;
            private bool _terminalFailure;

            public ResponsesStreamReducer(Action<string> onToken)
            {
                _onToken = onToken;
            }

            public void Process(string sseEventType, string json)
            {
                if (string.IsNullOrWhiteSpace(json))
                    return;
                ResponsesStreamEvent streamEvent;
                try
                {
                    streamEvent = JsonUtility.FromJson<ResponsesStreamEvent>(json);
                }
                catch (Exception ex)
                {
                    SetFailure(new ResponsesApiError { message = "Invalid stream event: " + ex.Message });
                    return;
                }
                if (streamEvent == null)
                {
                    SetFailure(new ResponsesApiError { message = "Invalid stream event." });
                    return;
                }

                string type = !string.IsNullOrWhiteSpace(sseEventType) ? sseEventType : streamEvent.type;
                if (string.IsNullOrWhiteSpace(type))
                    return;

                if (streamEvent.response != null)
                    ApplyResponse(streamEvent.response);
                if (string.Equals(type, "response.output_text.delta", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(streamEvent.delta))
                    {
                        _text.Append(streamEvent.delta);
                        if (_onToken != null)
                            _onToken(streamEvent.delta);
                    }
                    return;
                }
                if (string.Equals(type, "response.output_item.added", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type, "response.output_item.done", StringComparison.OrdinalIgnoreCase))
                {
                    UpsertOutput(streamEvent.item);
                    return;
                }
                if (string.Equals(type, "response.function_call_arguments.done", StringComparison.OrdinalIgnoreCase))
                {
                    UpsertFunctionCall(streamEvent);
                    return;
                }
                if (string.Equals(type, "response.function_call_arguments.delta", StringComparison.OrdinalIgnoreCase))
                {
                    AppendFunctionCallArgumentsDelta(streamEvent);
                    return;
                }
                if (string.Equals(type, "response.failed", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type, "response.incomplete", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type, "response.cancelled", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type, "error", StringComparison.OrdinalIgnoreCase))
                {
                    SetFailure(EventError(streamEvent));
                    return;
                }
                if (string.Equals(type, "response.completed", StringComparison.OrdinalIgnoreCase))
                {
                    _completed = true;
                    return;
                }
            }

            public AiChatResponse BuildResponse(string fallbackModel)
            {
                AiChatResponse result = new AiChatResponse();
                result.id = _id ?? string.Empty;
                result.model = _model ?? fallbackModel ?? string.Empty;
                result.status = _status;
                result.content = _text.Length > 0 ? _text.ToString() : ExtractOutputText(_output);
                result.responseOutput = new List<ResponsesOutputItem>(_output);
                result.usage = _usage;
                result.error = _error;
                result.incompleteDetails = _incompleteDetails;
                result.tool_calls = ExtractToolCalls(result.responseOutput);
                result.receivedAtUtc = DateTime.UtcNow;

                if (_terminalFailure && result.error == null)
                    result.error = new ResponsesApiError { message = "Responses stream failed." };
                if (!_terminalFailure && !_completed)
                {
                    result.status = string.IsNullOrWhiteSpace(result.status) ? "incomplete" : result.status;
                    result.error = new ResponsesApiError { message = "Stream ended before response.completed." };
                }
                return result;
            }

            private void ApplyResponse(ResponsesApiResponse response)
            {
                if (response == null)
                    return;
                if (!string.IsNullOrWhiteSpace(response.id)) _id = response.id;
                if (!string.IsNullOrWhiteSpace(response.model)) _model = response.model;
                if (!string.IsNullOrWhiteSpace(response.status)) _status = response.status;
                if (response.usage != null) _usage = response.usage;
                if (response.error != null) _error = response.error;
                if (response.incomplete_details != null) _incompleteDetails = response.incomplete_details;
                if (response.output != null)
                {
                    _output.Clear();
                    for (int i = 0; i < response.output.Length; i++)
                        UpsertOutput(response.output[i]);
                }
            }

            private void SetFailure(ResponsesApiError error)
            {
                _terminalFailure = true;
                if (error != null)
                    _error = error;
            }

            private static ResponsesApiError EventError(ResponsesStreamEvent streamEvent)
            {
                if (streamEvent == null)
                    return null;
                if (streamEvent.error != null)
                    return streamEvent.error;
                if (streamEvent.response != null && streamEvent.response.error != null)
                    return streamEvent.response.error;
                if (!string.IsNullOrWhiteSpace(streamEvent.message) ||
                    !string.IsNullOrWhiteSpace(streamEvent.code))
                {
                    ResponsesApiError error = new ResponsesApiError();
                    error.message = streamEvent.message;
                    error.code = streamEvent.code;
                    error.param = streamEvent.param;
                    return error;
                }
                return null;
            }

            private void UpsertFunctionCall(ResponsesStreamEvent streamEvent)
            {
                if (streamEvent == null)
                    return;
                for (int i = 0; i < _output.Count; i++)
                {
                    ResponsesOutputItem existing = _output[i];
                    if (existing != null && string.Equals(existing.id, streamEvent.item_id, StringComparison.Ordinal))
                    {
                        if (!string.IsNullOrWhiteSpace(streamEvent.name))
                            existing.name = streamEvent.name;
                        if (!string.IsNullOrWhiteSpace(streamEvent.call_id))
                            existing.call_id = streamEvent.call_id;
                        existing.arguments = streamEvent.arguments ?? existing.arguments;
                        return;
                    }
                }

                ResponsesOutputItem item = new ResponsesOutputItem();
                item.id = streamEvent.item_id;
                item.type = "function_call";
                item.call_id = streamEvent.call_id;
                item.name = streamEvent.name;
                item.arguments = streamEvent.arguments;
                UpsertOutput(item);
            }

            private void AppendFunctionCallArgumentsDelta(ResponsesStreamEvent streamEvent)
            {
                if (streamEvent == null || string.IsNullOrWhiteSpace(streamEvent.item_id))
                    return;

                for (int i = 0; i < _output.Count; i++)
                {
                    ResponsesOutputItem item = _output[i];
                    if (item != null && string.Equals(item.id, streamEvent.item_id, StringComparison.Ordinal))
                    {
                        if (!string.IsNullOrWhiteSpace(streamEvent.name))
                            item.name = streamEvent.name;
                        if (!string.IsNullOrEmpty(streamEvent.delta))
                            item.arguments = (item.arguments ?? string.Empty) + streamEvent.delta;
                        return;
                    }
                }

                ResponsesOutputItem newItem = new ResponsesOutputItem();
                newItem.id = streamEvent.item_id;
                newItem.type = "function_call";
                newItem.call_id = streamEvent.call_id;
                newItem.name = streamEvent.name;
                newItem.arguments = streamEvent.delta ?? string.Empty;
                _output.Add(newItem);
            }

            private void UpsertOutput(ResponsesOutputItem item)
            {
                if (item == null)
                    return;
                string key = !string.IsNullOrWhiteSpace(item.id) ? item.id : item.call_id;
                if (!string.IsNullOrWhiteSpace(key))
                {
                    for (int i = 0; i < _output.Count; i++)
                    {
                        ResponsesOutputItem existing = _output[i];
                        string existingKey = existing != null && !string.IsNullOrWhiteSpace(existing.id) ? existing.id : (existing != null ? existing.call_id : null);
                        if (string.Equals(key, existingKey, StringComparison.Ordinal))
                        {
                            _output[i] = item;
                            return;
                        }
                    }
                }
                _output.Add(item);
            }
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

        private sealed class ResponsesApiException : InvalidOperationException
        {
            public ResponsesApiException(string message) : base(message) { }
        }
    }
}
