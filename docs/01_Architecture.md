# 01_Architecture.md

## Общая архитектура

Приложение состоит из следующих основных модулей:

- **Core** — базовая логика, управление сессиями, конфигурация провайдеров
- **ModelDiscoveryService** — кэшированное обнаружение моделей по эндпоинту провайдера (ключ: `baseUrl|apiKey`), авто-обнаружение при открытии редактора и при изменении baseUrl/apiKey
- **API Layer** — работа с OpenAI-совместимыми API
  - **Provider Adapter** — провайдер-специфичная логика (заголовки, discovery, смена модели, capabilities) через `IProviderAdapter`. Две реализации: `HermesAdapter` и `GenericOpenAiAdapter`. Factory по `ProviderConfig.backendType`. Подробности — [14_Provider_Adapter.md](14_Provider_Adapter.md)
- **OpenAiCompatibleClient** — HTTP-транспорт + SSE парсинг. Делегирует провайдер-специфику адаптеру
- **Avatar System** — управление 2D аватарами, sprite-sheet motion packs для low-end/mobile, state mapper (`idle` / `thinking` / `talking` / `listening`) и one-shot reactions (`smile` / `confused`), подготовка к desktop-first 3D realtime аватарам
- **UI Layer** — интерфейс чата и настроек
- **NeonDropdown** — кастомный UITK компонент (`INotifyValueChanged<string>`), заменяет `DropdownField` во всём интерфейсе (пикер моделей, пресет в редакторе провайдера, язык в настройках). Поддерживает `choicesCsv` атрибут, popup overlay, программный API
- **Data Layer** — локальное хранение истории, конфигов, аватаров и motion-pack metadata
- **Platform Layer** — специфичный код для Desktop / Mobile / VR

## Технологии
- Unity 2022.3+
- Newtonsoft.Json
- UniTask (для асинхронности)
- Возможно: Zenject или VContainer (DI)

## Диаграмма компонентов (упрощённая)

```text
[UI Layer] ↔ [Core] ↔ [API Layer]
                ↕
          [Avatar System]
                ↕
          [Data Layer]
```

## Avatar System contract (MVP)
- Runtime читает motion pack (`manifest.json` + sprite sheets)
- State mapper выбирает continuous state: `idle`, `thinking`, `talking`, `listening`
- Reaction policy триггерит `smile` и `confused`
- Формат не решает эмоцию сам по себе; он только описывает доступные клипы

## Будущие расширения
- Голосовой ввод/вывод + lipsync
- 3D realtime аватары для desktop
- Генерация motion assets через внешний asset-pipeline без обязательной runtime-зависимости клиента
- VR режим
- Локальные модели (через llama.cpp / Ollama)
