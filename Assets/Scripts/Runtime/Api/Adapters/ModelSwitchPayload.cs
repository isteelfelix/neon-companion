namespace NeonCompanion.Runtime.Api.Adapters
{
    /// <summary>
    /// Результат BuildModelSwitchRequest. null = смена модели не поддерживается.
    /// </summary>
    public sealed class ModelSwitchPayload
    {
        public string Endpoint { get; set; }
        public string JsonBody { get; set; }
        public bool IsChatApi { get; set; } // true = через /chat/completions, false = отдельный эндпоинт
    }
}