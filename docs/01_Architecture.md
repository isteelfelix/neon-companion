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
  - **SelectableMarkdownElement** — кастомный markdown rendering engine для UITK. Блочная модель (paragraph/heading/quote/list/code/table/rule), inline tokenizer, word-wrap, glyph-level text selection, streaming block-level reconciliation. Syntax highlighting для 15+ языков. Diff-fenced code blocks. Заменяет TextField для всего рендеринга текста в чате.
  - **ChatController** — основной контроллер чата (1315 строк, рефакторинг v0.3.0). Логика распределена по подконтроллерам:
    - `ChatStreamingCoordinator` — стриминг и координация генерации
    - `ChatMessageListRenderer` — рендеринг списка сообщений
    - `ChatSelectionManager` — выделение и bulk-операции
    - `ChatMessageEditController` — редактирование сообщений
    - `ChatAttachmentManager` — вложения
    - `ChatSearchController` — поиск по чату
    - `ChatInputManager` — ввод, slash-команды
    - `ChatNotificationManager` — уведомления и звуки
    - `ToolCallApprovalController` — approval flow
    - `QueuedMessage` — DTO для очереди сообщений
  - **ToolCallUiHelper** — рендеринг tool entries с expand/collapse, inline diffs, статусами.
- **NeonDropdown** — кастомный UITK компонент (`INotifyValueChanged<string>`), заменяет `DropdownField` во всём интерфейсе (пикер моделей, пресет в редакторе провайдера, язык в настройках). Поддерживает `choicesCsv` атрибут, popup overlay, программный API
- **Data Layer** — локальное хранение истории, конфигов, аватаров и motion-pack metadata
- **Platform Layer** — специфичный код для Desktop / Mobile / VR

## Технологии
- Unity 6.4 (6000.4+)
- C# 9 (Unity default)
- System.Threading.Tasks для асинхронности (не UniTask)
- Newtonsoft.Json (только там где JsonUtility не хватает)
- UI Toolkit (UXML + USS, без legacy uGUI)

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

## Текущий статус и планы
- Голосовой ввод/вывод — полностью реализован: VoiceInputManager, VoiceOutputManager, VoiceController, VoicePreviewPlayer, settings UI (устройства, громкость, VAD), аудио-вложения в чате, HermesVoiceService + OpenAiVoiceService с фабрикой
- 3D аватары — архитектура реализована (Avatar3DLoader, Avatar3DRenderer), модели не добавлены
- Генерация motion assets через внешний asset-pipeline без обязательной runtime-зависимости клиента
- VR режим (M4+)
- Локальные модели (через llama.cpp / Ollama)
