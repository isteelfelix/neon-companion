# 06_Data_Model.md

## Локальное хранение данных

### Основные сущности
- **ProviderConfig** — настройки подключения к AI-провайдеру
- **Avatar** — метаданные аватара (путь к изображению, название, motion pack, preview и persona metadata)
- **AvatarMotionPack** — описание 2D sprite-sheet набора для одного аватара (`motion_pack.json`, clips)
- **AvatarMotionClip** — один action-клип (`action`, `spriteSheetPath`, `columns`, `rows`, `frameRate`, `loop`, `pingPong`)
- **ChatSession** — история сообщений с конкретным агентом
- **AppSettings** — общие настройки приложения
- **ChatAttachment** — вложение в сообщении (`kind`, `name`, `path`, `mediaType`)
- **ModelSwitchResult** — результат смены модели в сессии

### Поля ChatSession (расширенные)
- `selectedModel` — выбранная модель для текущей сессии
- `providerSessionId` — идентификатор сессии провайдера (`X-Hermes-Session-Id`)

### Поля ChatMessage (расширенные)
- `model` — модель, использовавшаяся для генерации сообщения
- `attachments[]` — массив вложений типа `AiChatAttachment`

### Поля AiChatRequest
- `providerSessionId` — передаётся провайдеру в каждом запросе

### Поля AiChatResponse
- `providerSessionId` — возвращается провайдером для пропагации

### MVP avatar motion contract
- Фиксированный action set: `idle`, `thinking`, `talking`, `listening`, `smile`, `confused`
- `idle`, `thinking`, `talking`, `listening` — continuous states
- `smile`, `confused` — one-shot reactions
- `idle` обязателен как fallback action

### Хранилище
- Unity PlayerPrefs (для простых настроек)
- JSON-файлы в `Application.persistentDataPath` (для истории, конфигов и avatar metadata)
- Motion packs хранятся как `manifest.json` + PNG sprite sheets в persistent data path или в packaged assets
- В будущем: SQLite или PlayerPrefs + шифрование

### Сохранение истории
История чатов сохраняется локально и может быть экспортирована.
