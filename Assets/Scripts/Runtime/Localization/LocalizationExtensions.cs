using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.Localization
{
    public static class LocalizationExtensions
    {
        private static ILocalizationService _service;

        public static void SetLocalizationService(ILocalizationService service)
        {
            _service = service;
        }

        public static VisualElement Localize(this VisualElement element, string key)
        {
            if (_service == null || string.IsNullOrEmpty(key))
                return element;

            string text = _service.Get(key);

            if (element is Button button)
            {
                button.text = text;
            }
            else if (element is Label label)
            {
                label.text = text;
            }
            else if (element is TextField textField)
            {
                textField.label = text;
            }

            return element;
        }

        public static string Get(string key, string fallback = null)
        {
            if (string.IsNullOrEmpty(key))
                return fallback ?? string.Empty;

            if (_service == null)
                return fallback ?? key;

            string value = _service.Get(key);
            if (string.IsNullOrEmpty(value) || value == key)
                return fallback ?? value ?? key;

            return value;
        }

        public static string GetFormat(string key, string fallback, params object[] args)
        {
            string format = Get(key, fallback);
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