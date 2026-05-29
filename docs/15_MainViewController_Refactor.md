# 15_MainViewController_Refactor.md

## Проблема
`MainViewController.cs` — 5269 строк, god object. Управляет всем: навигацией, чатом, сессиями, провайдерами, аватарами, голосом, layout. Сложно поддерживать, невозможно тестировать, локальные модели ломаются при чтении.

## Цель
Разбить на 7 выделенных контроллеров. MainViewController остаётся координатором (~1000 строк).

## Паттерн
Уже реализован в `SettingsController` — delegate-based deps через `BuildSettingsControllerDeps()`. Все новые контроллеры по той же схеме.

## Контроллеры

### 1. NavigationController (~200 строк)
**Поля:** `_navItems`, `_sessionItems`, 6 `_nav*` VisualElements + Labels + counts, `_providerTag`, `_navCloseLabel`
**Методы:** `AddNav`, `SetActiveNav`, `ShowArea`, `OnNav*Clicked`, `UpdatePanelToggleTooltips`
**Deps:** panel visibility toggle delegate

### 2. ChatController (~700 строк)
**Поля:** `_chatPanel`, `_composer`, `_messageInput`, send/summarize/search/more/attach/copy/regen buttons, `_messagesList`, `_typingIndicator`, both typing anim sets, `_thinkingBubble`/`_thinkingText`, `_toolCallUiHelper`, pending attachments, streaming flags
**Методы:** `SendCurrentMessageAsync`, `OnSendClicked`, `SummarizeCurrentConversationAsync`, `OnStreamToken`, `OnToolProgress`, `AddStreamingBubble`, `ClearThinkingBubble`, `RenderMessages`, scroll/copy/typing methods
**Deps:** ChatService, streaming delegates

### 3. SessionHistoryController (~500 строк)
**Поля:** `_historyPanel`, session ScrollViews, `_sessionItems`, history search UI (both bar sets), `_historyState`, search query, session ids/titles
**Методы:** `RenderSessionList`, `StartNewSessionAsync`, `OnHistorySearchToggled/Cleared`, `SearchSessionsFromComposerAsync`, `OnNewSessionClicked`, `IsActiveSession`, `AddSessionHeader`
**Deps:** ChatService, provider list for labels

### 4. ProvidersController (~800 строк)
**Поля:** `_providersPanel`, `_providersList`, add/import buttons, edit panel + 8 `_edit*` fields, save/cancel/test, model picker overlay, 9+ provider/status labels, `_topbarModelPicker`, discovered/auto-discover state, preset dicts
**Методы:** `ShowProviders`, `TestProviderConnectionAsync`, `ApplyModelSelectionAsync`, provider CRUD, model discovery, header sync, import/export
**Deps:** ProviderManager, ChatService

### 5. AvatarGalleryController (~600 строк)
**Поля:** BuiltInAvatarIds/Meta, viewmode buttons, gallery containers, `_activeAvatarId/Filter`, preview/hero elements, persona editor, custom avatar tiles/textures, profile caches, filter buttons/counts, `_avatarCustomizationPanel`, emoji overlays, upload/open buttons, 2D/3D renderer/service refs, motion state
**Методы:** `ShowAvatars`, Select/Apply/Filter/ViewMode, persona editor, customization events, 3D ensure/disable, motion/reaction methods, profile refresh
**Deps:** SpriteSheetAnimator, Avatar3DService

### 6. VoiceController (~200 строк)
**Поля:** `_listenBtn`/`_micBtn`, 4 voice service/manager fields, playing/recording/bound flags
**Методы:** `EnsureVoicePipelineAsync`, `OnVoiceRecordingStarted/Stopped`, `HandleVoicePlaybackStarted/Completed`, `RefreshVoiceControls`, `Bind/UnbindVoiceAnimationEvents`
**Deps:** ChatService (for bind), settings toggle

### 7. LayoutController (~150 строк)
**Поля:** resize handles, `_panelResizeHandler`, toggle buttons, 2 visibility bools, `_railElement`
**Методы:** toggle handlers, tooltip updates
**Deps:** PanelResizeHandler

### MainViewController (координатор, ~1000 строк)
**Остаётся:** OnEnable/Disable/Bind/RegisterCallbacks lifecycle, сервисы (`_app`, `_chatService`), `RefreshAsync`, localization refresh, SettingsController init, кросс-кут стейт через delegates.

## Порядок миграции
1. **NavigationController** — самый изолированный, минимум deps
2. **LayoutController** — тоже изолирован, маленький
3. **VoiceController** — тонкий, чёткие границы
4. **SessionHistoryController** — средняя сложность
5. **ChatController** — крупный, зависит от Session
6. **ProvidersController** — крупный, зависит от Chat
7. **AvatarGalleryController** — самый сложный, зависит от Voice + Chat

## Каждый шаг
1. Создать новый контроллер с `Init(deps)` + `RegisterCallbacks()` + `UnregisterCallbacks()`
2. Перенести поля и методы
3. MainViewController создаёт контроллер в `Bind()`, передаёт deps
4. `RegisterCallbacks`/`UnregisterCallbacks` делегируются
5. Git commit: "refactor: extract XxxController from MainViewController"
6. `git diff --stat` — проверить что линейный баланс ≈ 0
