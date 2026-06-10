# 15_MainViewController_Refactor.md

## Проблема
`MainViewController.cs` — 5269 строк, god object. Управляет всем: навигацией, чатом, сессиями, провайдерами, аватарами, голосом, layout. Сложно поддерживать, невозможно тестировать.

## Цель
Разбить на 7 выделенных контроллеров. MainViewController остаётся координатором.

## Статус: ✅ Выполнено (июнь 2026)

Все 7 контроллеров извлечены. MainViewController сократился с 5269 до **1676 строк**.

## Контроллеры (фактические размеры)

| # | Контроллер | Строки | Ответственность |
|---|-----------|--------|----------------|
| 1 | **NavigationController** | 317 | Навигация, переключение экранов |
| 2 | **ChatController** | 1694 | Отправка/получение сообщений, стриминг, tool calls |
| 3 | **SessionHistoryController** | 1069 | Список сессий в сайдбаре, статус-точки |
| 4 | **ProvidersController** | 2193 | CRUD провайдеров, discovery моделей, connection test |
| 5 | **AvatarGalleryController** | 1930 | Галерея аватаров, анимация, персона, built-in метаданные, загрузка текстур |
| 6 | **VoiceController** | 734 | Запись, STT, воспроизведение TTS |
| 7 | **LayoutController** | 609 | Определение форм-фактора, адаптивный layout |

## MainViewController (координатор, 1676 строк)

Осталось:
- OnEnable/Disable/Bind/RegisterCallbacks lifecycle
- Сервисы (`_app`, `_chatService`)
- `RefreshAsync`, локализация
- Кросс-кут стейт через delegates
- Сайдбар, композер, выбор модели

## Паттерн
Delegate-based deps через `BuildXxxControllerDeps()`. Все контроллеры по той же схеме:
1. Контроллер с `Init(deps)` + `RegisterCallbacks()` + `UnregisterCallbacks()`
2. MainViewController создаёт контроллер в `Bind()`, передаёт deps
3. `RegisterCallbacks`/`UnregisterCallbacks` делегируются

## Дополнительно

### ThemeColors (60 строк)
Статический синглтон акцентной палитры. 5 тем: indigo, rose, cyan, ember, mono. Свойства `Accent` и `AccentSoft` для inline-стилизации в C# (попапы, контекстные меню). Для USS — CSS-классы `.theme-*` на `#app-root`.

### AvatarCustomizationPanel (урезан)
Цветовая кастомизация аватаров (PrimaryColor, SecondaryColor, HaloColor, слайдеры, рамки) удалена. Остался только выбор эмодзи-overlay. Глобальная палитра акцента заменяет per-avatar цвета.
