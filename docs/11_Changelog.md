# 11_Changelog.md

## [Unreleased]

### Added
- Базовая структура проекта
- Документация
- План MVP
- Avatar motion research: 2D sprite-sheet baseline для weak PC/mobile и desktop-first 3D realtime path
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
- Локализация для авто-обнаружения моделей (en/ru)

### Changed
- Зафиксирован MVP contract для 2D avatar motion: `idle`, `thinking`, `talking`, `listening`, `smile`, `confused`
- Документация приведена к одному vocabulary для continuous states и one-shot reactions

## [0.1.0] - 2026-05-XX

- Первый прототип (текст + 2D аватар)
- Подключение к OpenAI-совместимым API
