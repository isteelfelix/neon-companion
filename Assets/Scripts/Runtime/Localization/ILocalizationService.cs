using System;

namespace NeonCompanion.Runtime.Localization
{
    public interface ILocalizationService
    {
        string CurrentLanguage { get; }
        string Get(string key);
        string Get(string key, params object[] args);
        void SetLanguage(string languageCode);
        event Action LanguageChanged;
    }
}