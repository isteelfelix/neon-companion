# 11_Changelog.md

## [Unreleased]

### Added
- U-33: Forward selected messages to another chat session (selection bar now has Forward between Delete/Cancel; opens centered overlay picker with session titles + timestamps from ChatService.GetAllSessionsAsync; messages deep-copied via JsonUtility snapshot in new AppendMessagesToSessionAsync; persists to target; shows confirmation; excludes current session; outside-click or Cancel aborts). All in ChatController + ChatView.uss + loc (no new .cs files).
- U-38: Chat search with highlight and navigation (topbar search button now toggles in-chat transcript search bar; live filtering, match count X/Y, ↑↓ nav, Esc/Enter keys, yellow highlight rows; closes on session change or re-render). Implemented entirely in ChatController with dynamic UI.
- U-41: Inline image rendering for attachments (replaces `[image] filename` text with actual <Image> elements loaded via UnityWebRequestTexture from local file:// paths; supports png/jpg/jpeg/gif/webp by kind or extension; non-images keep file label; max-size + rounded styles).
- U-16: API key show/hide toggle button in provider editor (next to password field)
- U-37: Export current chat as Markdown file (topbar "Export" button wires to ChatController.ExportChatAsync; writes .md to Application.persistentDataPath following SettingsController.ExportChatsAsync pattern; localized messages)
- U-29 + U-30: Right-click (desktop) and long-press (mobile) context menu on message bubbles with Edit (user messages only), Delete, Copy. Inline editing with Save/Cancel (+ Save & Regenerate if assistant follows). Delete and edit persist via SaveCurrentSessionAsync + re-render. All strings localized.
- Agent approval system Part B: streaming integration (alwaysApprovedTools in AppSettings, RequestToolApproval + Handle in ChatController wired to OnToolProgress "requesting" status, minimal tool call detection in OpenAiCompatibleClient for hermes.tool.request and OpenAI tool_calls chunks; auto/manual modes + Always persist; prompt added/removed from transcript; reject stops generation)

### Fixed
- A-10: Avatar animation — all 6 clips now trigger correctly. Talking plays during AI streaming, listening on composer input, confused on provider/model errors
- U-08: Exit button added to settings panel (triggers quit confirmation dialog)

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
