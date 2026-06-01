using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine.Networking;

namespace NeonCompanion.Runtime.Api.Adapters
{
    public sealed class HermesAdapter : IProviderAdapter
    {
        private const string HermesProxyModel = "hermes-agent";

        public ProviderCapabilities GetCapabilities()
        {
            return new ProviderCapabilities
            {
                SupportsModelSwitch = false, // Model switch is local — model sent in request.model field
                SupportsInventory = true,
                SupportsToolProgress = true,
                SupportsFunctionTools = false,
                UsesMaxCompletionTokens = false,
                RequiresTemperatureOmission = false,
                ForceNonStreaming = false,
                IgnoresStreamFlag = false
            };
        }

        public void ApplyRequestHeaders(UnityWebRequest request, string providerSessionId)
        {
            if (!string.IsNullOrWhiteSpace(providerSessionId))
                request.SetRequestHeader("X-Hermes-Session-Id", providerSessionId.Trim());
        }

        public string ExtractSessionId(UnityWebRequest response, string fallback)
        {
            if (response == null)
                return fallback;
            var header = response.GetResponseHeader("X-Hermes-Session-Id");
            return string.IsNullOrWhiteSpace(header) ? fallback : header.Trim();
        }

        public string[] BuildDiscoveryEndpoints(string baseUrl)
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

            AddEndpoint(root + "/api/model/options");
            AddEndpoint(normalized + "/api/model/options");
            AddEndpoint(root + "/model/options");
            AddEndpoint(normalized + "/model/options");
            return endpoints.ToArray();
        }

        public IReadOnlyList<string> ParseDiscoveryResponse(string json)
        {
            // Try Hermes inventory format first (providers[].models[])
            var result = ParseHermesInventoryModelIds(json);
            if (result != null && result.Count > 0)
                return result;

            // Fallback: standard OpenAI /models format (data[].id)
            return ParseOpenAiModelsResponse(json);
        }

        public ModelSwitchPayload BuildModelSwitchRequest(string model, string providerSessionId)
        {
            string body = BuildHermesModelSwitchChatPayload(model);
            if (string.IsNullOrEmpty(body))
                return null;

            return new ModelSwitchPayload
            {
                Endpoint = null,
                JsonBody = body,
                IsChatApi = true
            };
        }

        public string ParseModelSwitchResponse(string responseContent)
        {
            return ParseHermesCurrentModelLabel(responseContent);
        }

        private static string NormalizeBaseUrl(string baseUrl)
        {
            return (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        }

        private static string BuildHermesModelSwitchChatPayload(string targetModel)
        {
            if (string.IsNullOrWhiteSpace(targetModel))
                return null;

            string cmd = "/model " + targetModel.Trim();
            string escaped = cmd
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r");

            // Full chat completion payload to trigger Hermes model switch via command.
            // Uses hermes-agent proxy (discovered at runtime in client), small token budget, no stream.
            return "{\"model\":\"" + HermesProxyModel + "\",\"messages\":[{\"role\":\"user\",\"content\":\"" + escaped + "\"}],\"temperature\":0,\"max_tokens\":64,\"stream\":false}";
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

        private static IReadOnlyList<string> ParseOpenAiModelsResponse(string json)
        {
            if (string.IsNullOrEmpty(json))
                return null;

            try
            {
                var response = UnityEngine.JsonUtility.FromJson<OpenAiModelsResponse>(json);
                if (response == null || response.data == null || response.data.Length == 0)
                    return null;

                var ids = new List<string>();
                foreach (var entry in response.data)
                {
                    if (entry != null && !string.IsNullOrWhiteSpace(entry.id))
                        ids.Add(entry.id);
                }

                return ids.Count > 0 ? ids : null;
            }
            catch
            {
                return null;
            }
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

        // JSON parsing helpers (duplicated from OpenAiCompatibleClient for Phase 1 isolation;
        // will be deduplicated in later refactoring of the client).
        private static bool TryExtractNamedArray(string json, string propertyName, out string arrayJson)
        {
            arrayJson = null;
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(propertyName))
                return false;

            int propertyIdx = json.IndexOf("\"" + propertyName + "\"", StringComparison.OrdinalIgnoreCase);
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

            int propertyIdx = json.IndexOf("\"" + propertyName + "\"", Math.Max(0, searchStart), StringComparison.OrdinalIgnoreCase);
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
                int keyIdx = json.IndexOf("\"" + propertyName + "\"", pos, StringComparison.OrdinalIgnoreCase);
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
