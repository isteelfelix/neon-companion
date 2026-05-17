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
    }
}