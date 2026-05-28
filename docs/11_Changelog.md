# 11_Changelog.md

## [Unreleased]

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
