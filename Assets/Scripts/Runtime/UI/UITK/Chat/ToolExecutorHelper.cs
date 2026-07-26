using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NeonCompanion.Runtime.UI.UITK.Chat
{
    internal static class ToolExecutorHelper
    {
        internal static Dictionary<string, string> ParseToolArguments(string argumentsJson)
        {
            Dictionary<string, string> result;
            TryParseToolArguments(argumentsJson, out result, out _);
            return result;
        }

        internal static bool TryParseToolArguments(string argumentsJson, out Dictionary<string, string> result, out string error)
        {
            result = new Dictionary<string, string>();
            error = null;
            if (string.IsNullOrWhiteSpace(argumentsJson))
                return true;

            try
            {
                var obj = JObject.Parse(argumentsJson);
                foreach (var property in obj.Properties())
                {
                    if (property.Value == null || property.Value.Type == JTokenType.Null)
                        result[property.Name] = string.Empty;
                    else if (property.Value.Type == JTokenType.String ||
                             property.Value.Type == JTokenType.Integer ||
                             property.Value.Type == JTokenType.Float ||
                             property.Value.Type == JTokenType.Boolean)
                        result[property.Name] = property.Value.ToString();
                    else
                        result[property.Name] = property.Value.ToString(Formatting.None);
                }
                return true;
            }
            catch (JsonException ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
