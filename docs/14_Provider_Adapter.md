# 14_Provider_Adapter.md

## Проблема

`OpenAiCompatibleClient` (1533 строки) — один класс, содержащий:

- **Hermes-специфику:** `X-Hermes-Session-Id` заголовок, brute-force 4 эндпоинта для inventory, смена модели через chat API (`/model {model}`), парсинг markdown-ответа для определения текущей модели, fuzzy matching имён моделей
- **OpenAI-специфику:** `max_completion_tokens` вместо `max_tokens` для GPT-5/o-серии, подавление temperature для GPT-5, принудительный non-streaming для o-серии
- **6 вариантов Request-классов** с комбинациями `max_tokens`/`max_completion_tokens` + `temperature`/без temperature
- **Fallback-цепочку streaming** (stream → parse non-streaming JSON → retry non-streaming) для провайдеров, игнорящих `stream: true`

`ProviderConfig` — плоский data-class без понятия "я Hermes" vs "я Ollama". Клиент угадывает тип бэкенда по имени модели.

Результат: при добавлении нового бэкенда приходится лезть в монолит и добавлять `if`/`switch` по строкам. Фрагментация кода растёт с каждым провайдером.

## Решение: Provider Adapter

### Принцип

Минимальная абстракция с двумя реализациями: **Hermes** и **Generic**. Потом расширяем.

```
ProviderConfig.type = "hermes" | null (generic)
         ↓
ProviderAdapterFactory → IProviderAdapter
         ↓
┌─────────────────┬──────────────────────┐
│  HermesAdapter   │  GenericOpenAiAdapter │
│  (всё Hermes-    │  (OpenAI, Ollama,     │
│   специфичное)   │   LM Studio, vLLM,   │
│                  │   OpenRouter, xAI)    │
└─────────────────┴──────────────────────┘
         ↓
OpenAiCompatibleClient (чистый HTTP-транспорт + SSE)
```

### Цели

1. **Изоляция Hermes-специфики** в один класс — при Hermes-багах не трогаем generic-клиент
2. **GenericOpenAiAdapter** покрывает 80% провайдеров без изменений
3. **Добавление нового бэкенда** = новый адаптер + `case` в factory
4. **OpenAiCompatibleClient** падает до ~300-400 строк (HTTP + SSE парсинг)

---

## Интерфейсы

### IProviderAdapter

```csharp
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
```

### ProviderCapabilities

```csharp
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
```

### ModelSwitchPayload

```csharp
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
```

---

## Реализации

### GenericOpenAiAdapter

Покрывает: OpenAI, Grok (xAI), OpenRouter, Ollama, LM Studio, vLLM — всё, что говорит чистым OpenAI API.

```csharp
public sealed class GenericOpenAiAdapter : IProviderAdapter
{
    public ProviderCapabilities GetCapabilities() => new ProviderCapabilities
    {
        SupportsModelSwitch = false,
        SupportsInventory = false,
        SupportsToolProgress = false,
        UsesMaxCompletionTokens = false,
        RequiresTemperatureOmission = false,
        ForceNonStreaming = false,
        IgnoresStreamFlag = false
    };

    public void ApplyRequestHeaders(UnityWebRequest request, string providerSessionId)
    {
        // Generic — ничего специфичного
    }

    public string ExtractSessionId(UnityWebRequest response, string fallback)
        => fallback;

    public string[] BuildDiscoveryEndpoints(string baseUrl)
    {
        var normalized = NormalizeBaseUrl(baseUrl);
        return new[] { $"{normalized}/models" };
    }

    public IReadOnlyList<string> ParseDiscoveryResponse(string json)
        => ParseModelIds(json); // Стандартный OpenAI /models формат

    public ModelSwitchPayload BuildModelSwitchRequest(string model, string providerSessionId)
        => null; // Generic не поддерживает сессионную смену модели

    public string ParseModelSwitchResponse(string responseContent)
        => null;
}
```

### HermesAdapter

Вся Hermes-специфика в одном месте.

```csharp
public sealed class HermesAdapter : IProviderAdapter
{
    public ProviderCapabilities GetCapabilities() => new ProviderCapabilities
    {
        SupportsModelSwitch = true,
        SupportsInventory = true,
        SupportsToolProgress = true,
        UsesMaxCompletionTokens = false,
        RequiresTemperatureOmission = false,
        ForceNonStreaming = false,
        IgnoresStreamFlag = false
    };

    public void ApplyRequestHeaders(UnityWebRequest request, string providerSessionId)
    {
        if (!string.IsNullOrWhiteSpace(providerSessionId))
            request.SetRequestHeader("X-Hermes-Session-Id", providerSessionId.Trim());
    }

    public string ExtractSessionId(UnityWebRequest response, string fallback)
    {
        var header = response?.GetResponseHeader("X-Hermes-Session-Id");
        return string.IsNullOrWhiteSpace(header) ? fallback : header.Trim();
    }

    public string[] BuildDiscoveryEndpoints(string baseUrl)
    {
        // Brute-force 4 эндпоинта (текущая логика из BuildHermesInventoryEndpoints)
        var normalized = NormalizeBaseUrl(baseUrl);
        // ... (текущая реализация)
    }

    public IReadOnlyList<string> ParseDiscoveryResponse(string json)
    {
        // Парсинг Hermes inventory формата (текущая ParseHermesInventoryModelIds)
        // ...
    }

    public ModelSwitchPayload BuildModelSwitchRequest(string model, string providerSessionId)
    {
        // Отправка /model через chat API
        return new ModelSwitchPayload
        {
            Endpoint = null, // Используется текущий /chat/completions
            JsonBody = BuildModelSwitchChatPayload(model),
            IsChatApi = true
        };
    }

    public string ParseModelSwitchResponse(string responseContent)
    {
        // Парсинг **model** из markdown-ответа
        // (текущая ParseHermesCurrentModelLabel)
    }
}
```

### ProviderAdapterFactory

```csharp
public static class ProviderAdapterFactory
{
    private static readonly Dictionary<string, Func<IProviderAdapter>> Adapters
        = new Dictionary<string, Func<IProviderAdapter>>(StringComparer.OrdinalIgnoreCase)
    {
        { "hermes", () => new HermesAdapter() },
        // Будущие: { "ollama", () => new OllamaAdapter() },
    };

    private static readonly IProviderAdapter DefaultAdapter = new GenericOpenAiAdapter();

    public static IProviderAdapter Create(string providerType)
    {
        if (!string.IsNullOrWhiteSpace(providerType) &&
            Adapters.TryGetValue(providerType, out var factory))
        {
            return factory();
        }
        return DefaultAdapter;
    }
}
```

---

## Изменения в существующем коде

### ProviderConfig

```csharp
[Serializable]
public class ProviderConfig
{
    public string id;
    public string displayName;
    public string baseUrl;
    public string apiKey;
    public string defaultModel;
    public float temperature = 0.7f;
    public int maxTokens = 512;
    public bool isEnabled = true;

    // ← НОВОЕ
    /// <summary>
    /// Тип бэкенда: "hermes", null (generic OpenAI-compatible).
    /// Определяет, какой IProviderAdapter используется.
    /// </summary>
    public string backendType; // null = generic
}
```

**Миграция:** существующие конфиги без `backendType` автоматически получают `null` (generic) — обратно совместимо.

### OpenAiCompatibleClient

**Убирается:**
- `HermesSessionHeaderName` константа
- `ApplyHermesSessionHeader` / `GetHermesSessionHeader`
- `TryFetchHermesInventoryPayloadAsync`
- `ParseHermesInventoryModelIds`
- `BuildHermesInventoryEndpoints`
- `SendHermesModelSwitchAsync`
- `QueryHermesCurrentModelAsync`
- `ParseHermesCurrentModelLabel`
- `DoesHermesModelMatch`
- `TryGetHermesProxyModelAsync`
- `UsesMaxCompletionTokens` → заменяется на `capabilities.UsesMaxCompletionTokens`
- `UsesFixedDefaultTemperature` → заменяется на `capabilities.RequiresTemperatureOmission`
- `ShouldForceNonStreaming` → заменяется на `capabilities.ForceNonStreaming`
- 6 вариантов Request-классов → 1 + condition-логика

**Добавляется:**
- `_adapter: IProviderAdapter` (резолвится из `ProviderConfig.backendType`)
- Делегирование заголовков, discovery, model-switch адаптеру

**Итог:** клиент падает с ~1533 до ~300-400 строк.

### ChatService

Без изменений — `ChatService` работает через `IAiClient`, который не меняется.

### MainViewController

Без изменений — UI логика не зависит от adapter-уровня. Поля `backendType` добавляется в UI редактора провайдера (опционально, в M3).

### ModelDiscoveryService

Использует адаптер для discovery:
```csharp
var adapter = ProviderAdapterFactory.Create(provider.backendType);
var endpoints = adapter.BuildDiscoveryEndpoints(provider.baseUrl);
var capabilities = adapter.GetCapabilities();
// ... capabilities.SupportsInventory для приоритизации
```

---

## Порядок реализации

### Phase 1: Foundation (текущий PR)
1. Создать `IProviderAdapter`, `ProviderCapabilities`, `ModelSwitchPayload`
2. Создать `GenericOpenAiAdapter`
3. Создать `HermesAdapter` (перенести логику из `OpenAiCompatibleClient`)
4. Создать `ProviderAdapterFactory`
5. Добавить `ProviderConfig.backendType`
6. Рефакторить `OpenAiCompatibleClient` — делегировать адаптеру
7. Обновить `ModelDiscoveryService`

### Phase 2: UI (M2-M3)
8. Добавить выпадающий список "Backend Type" в редактор провайдера
9. Авто-определение типа при добавлении нового провайдера (по URL/baseUrl)

### Phase 3: Расширение (M3+)
10. `OllamaAdapter` — Ollama-specific endpoints, `/api/tags`, `/api/show`
11. `LmStudioAdapter` — LM Studio-specific model loading
12. Стандартизация capabilities в ProviderConfig для переопределения

---

## Диаграмма потока запроса

```
User sends message
         ↓
ChatService.SendMessageAsync()
         ↓
ChatViewModel.SendAsync()
         ↓
OpenAiCompatibleClient.SendMessageStreamAsync()
         ↓
┌─ ResolveRequestRouting()
│   ├─ adapter.ApplyRequestHeaders()     ← Hermes: X-Hermes-Session-Id
│   ├─ adapter.GetCapabilities()
│   └─ capabilities.ForceNonStreaming? → SendMessageAsync()
├─ BuildChatCompletionPayloadJson()
│   ├─ capabilities.UsesMaxCompletionTokens? → "max_completion_tokens"
│   ├─ capabilities.RequiresTemperatureOmission? → omit temperature
│   └─ stream: !capabilities.ForceNonStreaming
├─ Send via UnityWebRequest
├─ ParseSseText() → onToken callback
│   └─ capabilities.SupportsToolProgress? → ParseAndEmitToolProgress()
└─ adapter.ExtractSessionId() → AiChatResponse.providerSessionId
```

---

## Совместимость

- **Обратная:** существующие конфиги без `backendType` работают как generic
- **Прямая:** новый `backendType` не ломает generic-путь
- **API:** `IAiClient` интерфейс не меняется — `ChatService`, `ChatViewModel`, UI не затронуты

---

## Файлы

```
Assets/Scripts/Runtime/Api/
  Adapters/
    IProviderAdapter.cs         ← новый
    ProviderCapabilities.cs     ← новый
    ModelSwitchPayload.cs       ← новый
    ProviderAdapterFactory.cs   ← новый
    GenericOpenAiAdapter.cs     ← новый
    HermesAdapter.cs            ← новый (перенос из OpenAiCompatibleClient)
  OpenAiCompatibleClient.cs     ← уменьшается с 1533 до ~300-400 строк
  IAiClient.cs                  ← без изменений
  Models/
    AiChatModels.cs             ← без изменений
    ProviderConfig.cs           ← + backendType поле
```

---

## Связанные документы

- [01_Architecture.md](01_Architecture.md) — общая архитектура
- [03_API_Integration.md](03_API_Integration.md) — API интеграция
- [06_Data_Model.md](06_Data_Model.md) — модель данных
- [12_Feature_Tracker.md](12_Feature_Tracker.md) — трекер фич
