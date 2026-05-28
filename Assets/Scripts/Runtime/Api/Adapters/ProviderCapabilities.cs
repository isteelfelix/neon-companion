namespace NeonCompanion.Runtime.Api.Adapters
{
    /// <summary>
    /// Декларативные возможности бэкенда. Клиент проверяет capabilities
    /// вместо проверки имён моделей.
    /// </summary>
    public sealed class ProviderCapabilities
    {
        /// <summary>Бэкенд поддерживает смену модели в рамках сессии.</summary>
        public bool SupportsModelSwitch { get; set; }

        /// <summary>Бэкенд имеет inventory/discovery эндпоинты (кроме стандартного /models).</summary>
        public bool SupportsInventory { get; set; }

        /// <summary>Бэкенд шлёт hermes.tool.progress SSE события.</summary>
        public bool SupportsToolProgress { get; set; }

        /// <summary>Бэкенд использует max_completion_tokens вместо max_tokens.</summary>
        public bool UsesMaxCompletionTokens { get; set; }

        /// <summary>Бэкенд требует опущение temperature (фиксированная температура).</summary>
        public bool RequiresTemperatureOmission { get; set; }

        /// <summary>Принудительно non-streaming (для моделей, не поддерживающих SSE).</summary>
        public bool ForceNonStreaming { get; set; }

        /// <summary>Бэкенд игнорирует stream=true и возвращает полный JSON.</summary>
        public bool IgnoresStreamFlag { get; set; }
    }
}