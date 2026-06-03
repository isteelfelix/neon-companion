# 11_Changelog.md

## [Unreleased]

### Added
- **SelectableMarkdownElement** — custom native markdown rendering engine for UITK. Full document model (Block → InlineRun), block-level reconciliation for streaming (unchanged blocks not re-rendered), word-wrap via flex-wrap rows, glyph-level text selection with pointer drag + Ctrl+A/Ctrl+C, link click-through, ANSI escape code stripping. Replaces all TextField-based transcript bodies.
- Syntax highlighting for code blocks: language-agnostic keyword/string/comment/number tokenizer. Supported languages: C#, Python, JS/TS, Go, Rust, Java, Ruby, Shell, Kotlin, Scala, Swift, PHP, YAML, JSON. Diff-fenced blocks (`language: diff`/`patch`) render with +/-/@@ coloring.
- Shared diff palette (`DiffAddColor/Bg`, `DiffDelColor/Bg`, `DiffHunkColor/Bg`, `DiffContextColor`) used by both `SetDiff` and diff code blocks.
- Design token migration: all hardcoded `rgba()` colors in ChatView.uss replaced with `var(--bg-0)`, `var(--text-1)`, `var(--accent)`, `var(--ok)`, `var(--warn)`, `var(--danger)`, etc.
- Tool entry styling: left accent stripe (`border-left-color`) for running/done/reasoning states, hover background transition, semi-bold GeistMono font for tool names.
- Reasoning/thinking block: dedicated `.reasoning-entry__details` + `.reasoning-entry__text` styles (italic, accent stripe, dark surface).
- Approval prompt: pill buttons with border + hover transitions (approve=green, reject=red, always=accent), warning accent stripe, icon color.
- Clarify choices: pill buttons with accent border + hover fill, white-space normal for wrapping.
- Composer input wrapped in `ScrollView` (`.composer__scroll`) — TextField grows to content height, ScrollView caps at 140px with vertical scrollbar. Caret-follow on overflow.
- Message row cache (`_messageRowCache` + `BuildMessageRenderKey`) — reuses VisualElement instances across transcript re-renders instead of recreating.
- `EmitCodeChunks()` extracted for reuse between plain, highlighted, and diff code block rendering.
- `GetDiffLineStyle()` shared between `ParseDiff` (per-block) and diff-fenced code blocks.
- `StripAnsi()` removes ANSI escape sequences from incoming text before parsing.
- U-53: `IsImageFilePath` protected from `ArgumentException` on control characters (try/catch around `Path.GetExtension` + `GetInvalidPathChars` guard).

### Fixed
- **Hermes WS disconnect mid-generation**: `HermesSessionManager.HandleGatewayStateChange` now fires `OnError` when state transitions to `Disconnected` or `Error`. Previously, a WebSocket drop during streaming left `_hermesGenerationComplete` TCS unresolved, hanging the UI on "Выполнение..." until the 5-minute safety timeout.
- Composer Shift+Enter newline: caret index captured before text mutation (was racing with next keystroke), deferred apply via `schedule.Execute`. Prevents newline appending at end then getting stripped by `Trim()`.
- `_isDragOver` guarded with `#if UNITY_EDITOR` — only read in editor drag handlers, no runtime cost.
- Horizontal scrollbar hidden in transcript view (`.transcript > .unity-scroller--horizontal { display: none }`).
- Bubble sizing: markdown-heavy messages get wider bubbles (86%/92% vs 72%/86%).
- Paragraph flex-wrap: `Wrap.NoWrap` forced on column containers to prevent rows wrapping into side-by-side columns.
- `FlushParagraph` joins with `'\n'` (not `' '`) to preserve Shift+Enter hard breaks.
- `ResetTokenSpacing` zeros implicit Label margins/padding to eliminate phantom gaps between word-wrapped chunks.
- `MakeInlineLabel` uses `WhiteSpace.Pre` (not `NoWrap`) to preserve trailing spaces between words.

### Changed
- `CreateTranscriptBody` always uses `SelectableMarkdownElement` — removed TextField fallback branch.
- Streaming label (`_streamingLabel`) changed from `TextField` to `SelectableMarkdownElement` with `StringBuilder` buffer for incremental markdown re-rendering.
- `Query<Label>` → `Query<VisualElement>` in inline edit hide/show for compatibility with `SelectableMarkdownElement`.
- Inline code style: `var(--accent-text)` color + `var(--accent-soft)` background (was hardcoded gray).
- Bullet/numbered markers: `var(--accent-2)` color (was hardcoded `#888`).
- Blockquote: `var(--accent)` left border + `var(--text-2)` color (was hardcoded rgba).
- Link color: `var(--accent-text)` (was hardcoded `#6ea8fe`).
- Strikethrough color: `var(--text-3)` (was hardcoded `rgba(255,255,255,0.35)`).
- Code block: `var(--bg-0)` background + `var(--line-1)` border + `var(--text-1)` text color.
- Stats footer / timestamp: `var(--text-3)` (was hardcoded rgba).

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
