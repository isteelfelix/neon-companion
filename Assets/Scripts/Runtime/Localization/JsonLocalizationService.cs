using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using UnityEngine;

namespace NeonCompanion.Runtime.Localization
{
    public sealed class JsonLocalizationService : ILocalizationService
    {
        private readonly Dictionary<string, string> _strings = new();
        private string _currentLanguage;
        private readonly string _fallbackLanguage = "en";

        public string CurrentLanguage => _currentLanguage;
        public event Action LanguageChanged;

        public JsonLocalizationService(string languageCode = "ru")
        {
            SetLanguage(languageCode);
        }

        public void SetLanguage(string languageCode)
        {
            if (string.IsNullOrEmpty(languageCode))
                languageCode = _fallbackLanguage;

            _currentLanguage = languageCode;
            _strings.Clear();

            LoadLanguage(languageCode);
            
            // Load fallback if current language is different
            if (languageCode != _fallbackLanguage)
            {
                LoadLanguage(_fallbackLanguage, asFallback: true);
            }

            LanguageChanged?.Invoke();
        }

        private void LoadLanguage(string languageCode, bool asFallback = false)
        {
            string path = Path.Combine(Application.streamingAssetsPath, "Localization", $"{languageCode}.json");

            if (!File.Exists(path))
            {
                Debug.LogWarning($"[Localization] Language file not found: {path}");
                return;
            }

            try
            {
                string json = File.ReadAllText(path);
                var data = ParseStringMap(json);

                foreach (var kvp in data)
                {
                    if (asFallback && _strings.ContainsKey(kvp.Key))
                        continue; // Don't override existing translations

                    _strings[kvp.Key] = kvp.Value;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Localization] Failed to load {languageCode}.json: {ex.Message}");
            }
        }

        private static Dictionary<string, string> ParseStringMap(string json)
        {
            var result = new Dictionary<string, string>();
            if (string.IsNullOrWhiteSpace(json))
                return result;

            int index = 0;
            if (json.Length > 0 && json[0] == '\uFEFF')
                index++;

            SkipWhitespace(json, ref index);
            Expect(json, ref index, '{');

            while (true)
            {
                SkipWhitespace(json, ref index);
                if (TryConsume(json, ref index, '}'))
                    return result;

                string key = ReadString(json, ref index);
                SkipWhitespace(json, ref index);
                Expect(json, ref index, ':');
                SkipWhitespace(json, ref index);
                string value = ReadString(json, ref index);
                result[key] = value;

                SkipWhitespace(json, ref index);
                if (TryConsume(json, ref index, ','))
                    continue;

                Expect(json, ref index, '}');
                return result;
            }
        }

        private static void SkipWhitespace(string json, ref int index)
        {
            while (index < json.Length && char.IsWhiteSpace(json[index]))
                index++;
        }

        private static bool TryConsume(string json, ref int index, char expected)
        {
            if (index >= json.Length || json[index] != expected)
                return false;

            index++;
            return true;
        }

        private static void Expect(string json, ref int index, char expected)
        {
            if (!TryConsume(json, ref index, expected))
                throw new FormatException($"Expected '{expected}' at position {index}.");
        }

        private static string ReadString(string json, ref int index)
        {
            Expect(json, ref index, '"');

            var value = new System.Text.StringBuilder();
            while (index < json.Length)
            {
                char ch = json[index++];
                if (ch == '"')
                    return value.ToString();

                if (ch != '\\')
                {
                    value.Append(ch);
                    continue;
                }

                if (index >= json.Length)
                    throw new FormatException("Unexpected end of JSON string escape.");

                char escape = json[index++];
                switch (escape)
                {
                    case '"':
                    case '\\':
                    case '/':
                        value.Append(escape);
                        break;
                    case 'b':
                        value.Append('\b');
                        break;
                    case 'f':
                        value.Append('\f');
                        break;
                    case 'n':
                        value.Append('\n');
                        break;
                    case 'r':
                        value.Append('\r');
                        break;
                    case 't':
                        value.Append('\t');
                        break;
                    case 'u':
                        value.Append(ReadUnicodeEscape(json, ref index));
                        break;
                    default:
                        throw new FormatException($"Unsupported JSON escape '\\{escape}' at position {index}.");
                }
            }

            throw new FormatException("Unterminated JSON string.");
        }

        private static char ReadUnicodeEscape(string json, ref int index)
        {
            if (index + 4 > json.Length)
                throw new FormatException("Incomplete unicode escape.");

            string hex = json.Substring(index, 4);
            index += 4;
            return (char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        public string Get(string key)
        {
            if (_strings.TryGetValue(key, out string value))
                return value;

            Debug.LogWarning($"[Localization] Missing key: {key}");
            return key;
        }

        public string Get(string key, params object[] args)
        {
            string format = Get(key);
            try
            {
                return string.Format(format, args);
            }
            catch
            {
                return format;
            }
        }
    }
}
