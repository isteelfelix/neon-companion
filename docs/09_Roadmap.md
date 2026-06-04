# 09_Roadmap.md

## Фазы развития

### M0 — Базовый MVP ✅
- Текстовой чат
- Подключение OpenAI-совместимых API
- Смена 2D аватаров
- Сохранение истории

### M1 — Улучшенный опыт ✅
- Несколько аватаров с базовой sprite-sheet анимацией
- Фиксация MVP action set: `idle`, `thinking`, `talking`, `listening`, `smile`, `confused`
- Разделение continuous states и one-shot reactions
- Автоматическое определение доступных моделей через `/models` с кэшированием
- Переключение модели из окна чата (модель-пикер в topbar)
- Улучшенный UI
- Поддержка нескольких провайдеров одновременно

### M2 — UI и полировка ✅
- SelectableMarkdownElement — нативный markdown-движок
- ChatController рефакторинг (5477→1315 строк, 11 подклассов)
- Design token migration (hardcoded rgba → CSS variables)
- Agent approval system
- Drag-and-drop, clipboard paste
- Chat commands, stop button, export
- Window chrome service
- Cyberpunk splash screen
- Provider Adapter архитектура

### M3 — Голос, 3D, мобильные платформы 🔧
- Голосовой ввод/вывод — pipeline реализован, UI не завершена
- Lipsync controller — реализован
- 3D аватары — архитектура (Avatar3DLoader, Avatar3DRenderer), модели не добавлены
- Android — код готов, ожидает device testing
- iOS — в разработке
- Тестирование и runtime фиксы

### M4 — VR, публикация, полировка 📋
- Поддержка VR (Quest, PCVR)
- Публикация itch.io / GitHub Releases
- Документация для контрибьюторов ✅
- Донат-система ✅
