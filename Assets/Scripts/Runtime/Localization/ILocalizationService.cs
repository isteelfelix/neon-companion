using System;

namespace NeonCompanion.Runtime.Localization
{
    public interface ILocalizationService
    {
        string CurrentLanguage { get; }
        string Get(string key);
        string Get(string key, params object[] args);
        /// <summary>Quiet lookup — returns false on a missing key without logging a warning.</summary>
        bool TryGet(string key, out string value);
        void SetLanguage(string languageCode);
        event Action LanguageChanged;
    }
}