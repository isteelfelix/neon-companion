using System.Collections.Generic;

namespace NeonCompanion.Runtime.UI.UITK.Chat
{
    internal static class ToolExecutorHelper
    {
        internal static Dictionary<string, string> ParseToolArguments(string argumentsJson)
        {
            var result = new Dictionary<string, string>();
            if (string.IsNullOrWhiteSpace(argumentsJson))
                return result;

            try
            {
                int start = argumentsJson.IndexOf('{');
                int end = argumentsJson.LastIndexOf('}');
                if (start < 0 || end <= start)
                    return result;

                string obj = argumentsJson.Substring(start, end - start + 1);
                int pos = 0;
                while (pos < obj.Length)
                {
                    int keyStart = obj.IndexOf('"', pos);
                    if (keyStart < 0)
                        break;
                    int keyEnd = obj.IndexOf('"', keyStart + 1);
                    if (keyEnd < 0)
                        break;
                    string key = obj.Substring(keyStart + 1, keyEnd - keyStart - 1);
                    pos = keyEnd + 1;

                    int colon = obj.IndexOf(':', pos);
                    if (colon < 0)
                        break;
                    pos = colon + 1;

                    while (pos < obj.Length && char.IsWhiteSpace(obj[pos]))
                        pos++;

                    if (pos >= obj.Length || obj[pos] != '"')
                    {
                        pos++;
                        continue;
                    }

                    int valStart = pos + 1;
                    int valEnd = obj.IndexOf('"', valStart);
                    if (valEnd < 0)
                        break;

                    string val = obj.Substring(valStart, valEnd - valStart);
                    val = val.Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\\"", "\"").Replace("\\\\", "\\");
                    result[key] = val;
                    pos = valEnd + 1;
                }
            }
            catch
            {
                // Return any arguments parsed before malformed input was encountered.
            }

            return result;
        }
    }
}
