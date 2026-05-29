# 03_API_Integration.md

## Подключение к провайдерам

Приложение должно поддерживать любой OpenAI-совместимый API.

### Поддерживаемые форматы
- OpenAI
- Grok (xAI)
- Hermes / OpenClaw
- Ollama / llama.cpp (OpenAI compatible mode)
- OpenRouter

### Конфигурация провайдера
Каждый провайдер хранит:
- Название
- Base URL
- API Key
- Модель по умолчанию
- Дополнительные параметры (temperature, max tokens и т.д.)

### Автоматическое определение моделей
Реализовано через `ModelDiscoveryService`:
- Кэширование по ключу `baseUrl|apiKey`
- Вызов `/models` эндпоинта при открытии редактора провайдера (если baseUrl/apiKey уже заполнены)
- Авто-обнаружение при изменении baseUrl или apiKey
- Синхронизация пресета моделей в редакторе после `TestConnectionAsync` (`SyncModelPresetFromDiscovery`)

### Сессионная маршрутизация моделей
- `ApplySessionModelAsync()` на `IAiClient` для смены модели в рамках сессии
- Заголовок `X-Hermes-Session-Id` пропагируется во все запросы
- `ProviderSessionId` в `AiChatRequest` / `AiChatResponse`

### Вложения
- `ChatAttachment` (kind, name, path, mediaType) — локальный тип
- `AiChatAttachment` — API-модель для вложений в чат-запросах

### Hermes Inventory
- Интеграция с Hermes inventory endpoint для получения списка доступных моделей
- `TryFetchHermesInventoryPayloadAsync` / `ParseHermesInventoryModelIds`

### Планируемые улучшения
- Импорт/экспорт конфигураций провайдеров