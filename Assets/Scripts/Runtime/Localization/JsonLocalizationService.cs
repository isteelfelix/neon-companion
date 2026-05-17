using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
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
                var data = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);

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