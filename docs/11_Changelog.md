# 11_Changelog.md

## [0.3.0] - 2026-06-04

### Added
- **SelectableMarkdownElement** — нативный markdown-движок для UITK. Блочная модель (paragraph/heading/quote/list/code/table/rule), инлайн-токенизация (bold/italic/strike/code/links), word-wrap через flex-wrap rows, glyph-level выделение текста с pointer drag + Ctrl+A/Ctrl+C, link click-through. Заменяет все TextField-based тела транскрипта.
- **Синтаксис-хайлайтинг** для code blocks: C#, Python, JS/TS, Go, Rust, Java, Ruby, Shell, Kotlin, Scala, Swift, PHP, YAML, JSON. Diff-fenced блоки (`language: diff`/`patch`) с +/-/@@ окраской.
- **Window chrome service** — borderless desktop window management.
- **Agent Approval System** — WebSocket RPC approval flow для tool calls в чате.
- **Drag-and-drop файлов** в чат.
- **Emoji для tool events** — ToolEventPayload + ToolCallUpdate с эмодзи-маппингом.
- **Avatar view mode settings** + предзагрузка спрайтшитов при старте.
- **Чат-команды** — /help, /clear, /new, /system, /temp, /tokens.
- **Кнопка стоп** — отмена генерации.
- **Экспорт чата** в markdown.
- **API key toggle** — show/hide в редакторе провайдера.
- **Счётчик токенов + время ответа** в bubbles.
- **A-04** — scale-and-crop для кастомных аватаров (Telegram-style crop editor).
- **Plugin/extension system** — IPlugin, PluginManager, DLL loading.
- **Contributor docs** + donate system (Buy Me a Coffee, GitHub Sponsors).
- **3D avatar architecture** — Avatar3DLoader, Avatar3DRenderer (GLB/GLTF).
- **Voice pipeline** — VoiceInputManager, VoiceOutputManager, WebGL + Android support.
- **Lipsync controller** — phoneme-to-viseme mapping.
- **Sprite sheet animation system** — SpriteSheetAnimator, SpriteSheetAnimationLoader, AvatarMotionPack.
- **Themes page** + настройки тем.
- **Cyberpunk splash screen** с динамическими эффектами.
- **History screen** — экран сессий с удалением.
- **Provider-aware sessions** — сессии сохраняют контекст провайдера.
- **Custom avatar management** — загрузка, кастомизация, persona.
- **Localization system** — JsonLocalizationService + en.json/ru.json.
- **AppManager + NeonLogger** — logging infrastructure.
- **NeonDropdown** — кастомный UITK компонент (замена DropdownField).
- **ModelDiscoveryService** — кэшированное обнаружение моделей по /v1/models.
- **Model picker в чате** — NeonDropdown в topbar + overlay.
- **Hermes inventory endpoint** интеграция.
- **Многострочный ввод** — auto vertical scroller.
- **Масштабируемый rail сайдбара** (160–400px).
- **Режимы аватара** — Static, Animated, Volume3D.
- **Motion pack формат** — formatVersion, spriteSheetPath, frameRate, pingPong.
- **DiffTextField** — TextField + generateVisualContent event для diff highlighting.
- **Inline diff display** в expandable tool entries.
- **Reasoning/thinking block** — dedicated стили для thinking bubble.
- **Clarify choices** — pill buttons с hover fill.

### Changed
- **ChatController рефакторинг**: 5477→1315 строк, 11 подклассов вынесено:
  - ChatMessageListRenderer, ChatStreamingCoordinator, ToolCallApprovalController
  - ChatSelectionManager, ChatMessageEditController, ChatAttachmentManager
  - ChatSearchController, ChatInputManager, ChatNotificationManager
  - QueuedMessage DTO → Models/Chat/
- **MainViewController рефакторинг**: вынесены NavigationController, ProvidersController, SessionHistoryController, AvatarGalleryController, VoiceController, LayoutController, SettingsController, PanelResizeHandler.
- **Design token migration**: все hardcoded `rgba()` → `var(--bg-0)`, `var(--text-1)`, `var(--accent)`, `var(--ok)`, `var(--warn)`, `var(--danger)`, `var(--line-*)`.
- **Streaming label**: `TextField` → `SelectableMarkdownElement` с `StringBuilder` buffer.
- **Composer wrapped in ScrollView** — растёт до 140px, дальше scrollbar.
- **Message row cache** (`_messageRowCache` + `BuildMessageRenderKey`) — переиспользование VisualElement instances.
- Tool entry styling: left accent stripe для running/done/reasoning states.
- Bubble sizing: markdown-heavy messages получают шире (86%/92% vs 72%/86%).
- Providers UI refactored — improved layout + provider edit overlay.
- Avatar gallery → ScrollView.

### Fixed
- **Hermes WS disconnect mid-generation**: `HandleGatewayStateChange` теперь firing `OnError` при `Disconnected`/`Error`. Раньше WS-drop во время streaming оставлял UI зависшим на «Выполнение...» до 5-минутного timeout.
- **Session not found after restart** + approval hanging.
- **Approval**: REST API вместо nonexistent RPC, затем WS RPC с правильными params.
- Composer Shift+Enter newline: caret index перед text mutation, deferred apply.
- Text duplication — `AddMessageSegments` теперь трекает text vs tool segments отдельно.
- Text invisible в bubble когда есть tools + missing stats + raw call ID в thinking bubble.
- Model switch: updates UI immediately, fires gateway async (matches Desktop).
- Model switching через `slash.exec` via gateway.
- ContainsMarkdown: tables detect by any line starting with `|`.
- Merge ALL text segments для markdown table rendering.
- Compile errors: array fields, nullable bool, USS border shorthand.
- USS border-left shorthand → longhand properties.
- Horizontal scrollbar hidden в transcript view.
- Paragraph flex-wrap: `Wrap.NoWrap` на column containers.
- `FlushParagraph` joins с `'\n'` (не `' '`) для Shift+Enter hard breaks.
- `ResetTokenSpacing` zeros implicit Label margins/padding.
- `MakeInlineLabel` uses `WhiteSpace.Pre` для trailing spaces.
- IsImageFilePath crash на control characters (try/catch + `GetInvalidPathChars` guard).
- U-11: rail overflow hidden.
- U-12: multiline input overflow.
- A-10: все 6 avatar animation clips теперь trigger correctly.
- Model selection saves locally, no global /model mutation.
- `HermesSessionManager.HandleGatewayStateChange` fires `OnError` on disconnect.
- `_isDragOver` guarded с `#if UNITY_EDITOR`.
- Various cursor issues: removed unsupported `Cursor.SetCursor` runtime texture.
- SelectableMarkdownElement v3: Label-based (Unity 6.4 compat), убраны Painter2D/TextDecoration dependencies.
- Tool segments show human-readable context вместо raw call ID.
- Context window shows correct size из gateway + accurate token count.

### Known Issues
- **U-33**: Пересылка сообщений между чатами не работает.
- **U-49**: Входящие вложения от AI — gateway отдаёт HTML вместо изображений.
- **C-10**: Provider Adapter — model switching работает для OpenAI, но не для Hermes (model list shows, но switching не применяется).

## [0.2.0] - 2026-05-27

### Added
- ModelDiscoveryService: кэшированное обнаружение моделей по эндпоинту провайдера
- NeonDropdown: кастомный UITK компонент, замена DropdownField
- Модель-пикер в чате (topbar NeonDropdown + overlay-диалог)
- Применение модели к сессии: `ApplySessionModelAsync`, `ModelSwitchResult`
- Сессионная маршрутизация моделей с заголовком `X-Hermes-Session-Id`
- Вложения в чате: `ChatAttachment`, `AiChatAttachment`
- Hermes inventory endpoint интеграция
- Многострочный ввод сообщений (auto vertical scroller)
- Масштабируемый рельс сайдбара (160–400px)
- Режимы отображения аватара: `AvatarViewMode` (Static, Animated, Volume3D)
- Обновлённый формат `motion_pack.json` (formatVersion, spriteSheetPath, frameRate, pingPong)
- Спрайтшиты для neon: idle, thinking, talking, listening, smile, confused
- Система плагинов (IPlugin, PluginManager, DLL loading)
- Донат-система (Buy Me a Coffee, GitHub Sponsors)
- Диалог подтверждения выхода с настраиваемой горячей клавишей
- AGENTS.md для AI-агентов

### Changed
- Зафиксирован MVP contract для 2D avatar motion: `idle`, `thinking`, `talking`, `listening`, `smile`, `confused`
- Убраны неиспользуемые build-скрипты (scripts/build.sh, scripts/release.sh, BuildScript.cs)
- bundleVersion синхронизирован с VERSION файлом (0.2.0)
- Company Name: iSteelFelix

### Fixed
- Авто-обнаружение моделей при открытии редактора провайдера с предзаполненными значениями
- Сравнение несохранённых изменений через SameText
- Тест соединения использует TestConnectionAsync для получения моделей

## [0.1.0] - 2026-05-XX

- Первый прототип (текст + 2D аватар)
- Подключение к OpenAI-совместимым API
