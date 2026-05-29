using System.Collections.Generic;
using UnityEngine.Networking;

namespace NeonCompanion.Runtime.Api.Adapters
{
    /// <summary>
    /// Провайдер-специфичная логика: заголовки, discovery, смена модели, capabilities.
    /// </summary>
    public interface IProviderAdapter
    {
        /// <summary>Капабилити бэкенда.</summary>
        ProviderCapabilities GetCapabilities();

        /// <summary>
        /// Провайдер-специфичные заголовки для каждого запроса.
        /// Generic: пусто. Hermes: X-Hermes-Session-Id.
        /// </summary>
        void ApplyRequestHeaders(UnityWebRequest request, string providerSessionId);

        /// <summary>
        /// Извлечь providerSessionId из ответа.
        /// Generic: null. Hermes: из X-Hermes-Session-Id заголовка.
        /// </summary>
        string ExtractSessionId(UnityWebRequest response, string fallback);

        /// <summary>
        /// Эндпоинты для discovery моделей (приоритетный порядок).
        /// Generic: [baseUrl/models]. Hermes: [root/api/model/options, baseUrl/api/model/options, ...].
        /// </summary>
        string[] BuildDiscoveryEndpoints(string baseUrl);

        /// <summary>
        /// Парсинг ответа discovery в список ID моделей.
        /// Generic: ParseModelIds (OpenAI /models формат). Hermes: ParseHermesInventoryModelIds.
        /// </summary>
        IReadOnlyList<string> ParseDiscoveryResponse(string json);

        /// <summary>
        /// Формирование payload для смены модели (если поддерживается).
        /// Возвращает null, если смена модели не поддерживается.
        /// </summary>
        ModelSwitchPayload BuildModelSwitchRequest(
            string model, string providerSessionId);

        /// <summary>
        /// Парсинг ответа на смену модели.
        /// Hermes: парсинг markdown **model**. Generic: null (смена не поддерживается).
        /// </summary>
        string ParseModelSwitchResponse(string responseContent);
    }
}