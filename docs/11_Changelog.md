# 11_Changelog.md

## [Unreleased]

### Changed

- OpenAI-compatible provider context now defaults to **Auto**: Neon prefers the loaded LM Studio runtime value, then model metadata from the provider API, then the built-in OpenAI model catalog. A manual value acts as a cap and cannot exceed a known model limit; when no source knows the limit, the UI explicitly shows **Unknown** and accepts any positive manual value. The former “Max tokens” label is now “Max response tokens” to distinguish the output budget from the context window.
- Client-host terminal execution now uses a dedicated session-scoped executor instead of `TerminalController`: the first command offers run-once/session/deny, a session grant avoids repetitive prompts, grants reset on disconnect, and persistent shells cannot leak state between chats. Terminal tabs now use runtime-loaded USS classes and fit the existing right-panel design instead of inline unstyled buttons.
- Terminal shell input now derives Ctrl/Alt bytes from `keyCode` when Windows UITK omits `character`, emits xterm-compatible modifier sequences for arrow keys, preserves terminal focus during chat streaming until an intentional outside click, supports multiple independent local PTY tabs, and mirrors backend `agent.terminal.output` streams in read-only tabs.
- Corrected the Hermes terminal contract documentation: the current upstream gateway does not expose the dormant Companion `client.register` / `terminal.execute` / `terminal.respond` extension.

### Added
- **Avatar/Companion hardening Phase D** — added Unity EditMode regressions for legacy static/sprite profiles, generic GLB/glTF mappings, import mutation/catalog guards, isolated display snapshots, and immediate TTS stop/replay; added a Windows lifecycle/persistence evidence harness and a machine-checked ten-item acceptance ledger. VRM 0.x/1.0 and Win32/TTS runtime evidence remain explicit Felix-side environment gates.
- **Windows Companion window Phase C** — Windows Player can move the selected avatar from the main column into a distinct transparent display-only Unity process. A private named pipe carries only avatar display snapshots and idle/listening/thinking/speaking/stop events; child mode exits before secrets, providers, sessions, chat, plugins, or voice initialize. Show/hide, topmost pin, monitor, scale, drag position and user-toggleable click-through persist, with Ctrl+Shift+F12 as the emergency click-through release. Child exit/crash leaves chat running; parent shutdown cleans up the child. Mobile and Editor use a no-spawn stub.
- **VRM runtime Phase B** — explicit `.vrm` imports now use embedded UniVRM 0.131.2, render in the main 3D preview, expose only detected humanoid/blink/gaze/expression/lipsync capabilities, and fall back to restricted 3D when optional model features are absent. Packaged VRMA states follow idle/thinking/listening/talking/reaction activity, voice visemes drive only available VRM mouth expressions, and stop/cancel/interrupt/barge-in clear speaking immediately. Invalid VRM selections preserve the prior avatar; arbitrary GLB remains on the generic glTF path.
- **Avatar backend import Phase A** — avatar settings now imports static 2D, motion-pack sprite sheets, generic GLB/glTF, and VRM through an explicit validation dialog with preview, diagnostics, and evidence-based capabilities. User-owned assets are copied into isolated persistent-data directories only after format/size/scene validation; failed inputs preserve the previous active avatar. `AvatarProfile` now carries a versioned type/source/capability contract and 3D state-to-clip mapping while legacy profiles remain readable.
- **Companion client-terminal protocol v2** — documented the exact backend `client.register` / `terminal.execute` / `terminal.respond` contract and the separate `client_terminal` agent tool in `docs/23_Client_Terminal_Protocol.md`.
- **Composer completions (slash commands + `@` references)** — the chat composer now shows live suggestions from the Hermes gateway: a bare `/` lists the categorized `commands.catalog`, further typing queries `complete.slash` (arg-stage items respect `replace_from`), and an `@…` token queries `complete.path` for files/folders/git refs. Up/Down to move, Enter/Tab to accept, Esc to dismiss, click to pick. Responses that arrive after the draft, session, profile or connection changed are discarded, and a gateway without these methods simply shows nothing — completion never raises a chat error.
- **Automatic Hermes browser OAuth session capture** — Gated-gateway Connect (password + Nous/OIDC) launches a dedicated Edge/Chrome profile on `{gateway}/login`, polls CDP `Network.getAllCookies` until `hermes_session_*` appear (Desktop `openOauthLoginWindow` parity), then mints ws-ticket and connects. No in-app credentials form and no cookie paste. Token mode remains under Advanced only.
- **Desktop-style Remote Hermes gateway UX** — Providers editor primary path is Gateway URL + Connect / Sign in (auto-probe `/api/status` + `/api/auth/providers`), with Signed in / Needs sign-in / Connected status and Sign out. Gated gateways complete login in the browser window (password form and OAuth IDP live on gateway `/login`); Bearer token only under Advanced. Reuses P8 cookie + ws-ticket plumbing; token mode preserved.
- **Hermes REST v2 read surface** — `HermesRestClient` now mirrors Desktop read endpoints for status, model info/options, config, skills, toolsets, and cron jobs, with bearer-auth GET/POST/PATCH/DELETE helpers and a typed missing-endpoint exception for 404 `No such API endpoint` capability gaps.

- **Desktop-parity attachment path** — non-image files now go out through `file.attach` (data-URL upload) with the returned `@file:` ref prefixed to the prompt, images through `image.attach_bytes` with the `filename` extension hint, with path-based `image.attach` as the fallback for older gateways. Attachments are staged against the session that actually runs the turn (re-staged after a stale-session resume) and taken back with `image.detach` when the send never reaches the agent, so a failed turn cannot resend them. The agent-initiated `file.transfer.*` protocol is unchanged.

### Fixed
- Built-in Neon VRM and VRMA states now load from raw packaged bytes through the
  UniVRM runtime importer. This preserves both control rigs, eliminates the
  per-frame retarget null reference, removes legacy `UnityEngine.Input` polling,
  preserves runtime-import shaders against build stripping, and makes the
  Companion child report its actual ready backend instead of
  allowing a sprite launch to masquerade as VRM acceptance.
- Avatar import now rejects catalog complexity before runtime instantiation and revalidates source metadata before copy; generic 3D retains only one cached template instead of growing for every selected model.
- TTS stop/cancel/barge-in now releases the active queue wait locally, so a backend that omits `OnPlaybackComplete` cannot block later speech until the safety timeout.
- Hermes TTS/STT no longer bypass OAuth authentication and depend on Unity's incidental cookie jar: direct audio requests now apply the live `HermesRemoteAuth` cookie, persist rotated `Set-Cookie` values, and move rejected sessions to explicit reauthentication without retrying the same unauthenticated STT request. Legacy Bearer-token voice auth is unchanged.
- Hermes slash commands surfaced by composer autocomplete now execute through `slash.exec` instead of falling into Companion's local “unknown command” branch. Gateway output is rendered inline, `command.dispatch` remains the compatibility fallback, and returned send/prefill directives are followed while `/new`, `/clear`, `/help`, `/model`, `/system`, `/temp`, and `/tokens` stay local.
- Assistant bubbles lost their token count in the stats footer. The Responses migration made the per-message figure depend solely on a `usage` object in the stream, which OpenAI sends but most OpenAI-compatible servers do not, and it dropped the only caller of `ChatStreamingCoordinator.SetFinalStats`, so the live footer never settled off its running estimate. Both backends now fall back to a text-derived estimate when no exact usage is reported (matching what the Hermes path already did for a normal turn, and now also covering interrupted and timed-out Hermes turns), and the live footer is finalized from the message the turn actually persisted.
- Attachments of any kind were uploaded as images: a dropped text/PDF file was written into the gateway's images dir as a bogus PNG (no `filename` hint meant magic-byte sniffing fell back to `.png`) and handed to the vision pipeline instead of being staged as a readable file.
- Audited and documented the complete OpenAI-compatible chat path; stopped advertising an incomplete generic function-tool loop, removed duplicate completion replay after an empty successful stream, and preserved structured OpenAI error diagnostics and request IDs.
- All runtime version labels now use Unity Player Settings through `Application.version`; removed stale hardcoded splash/version-file values and aligned mobile Build Profiles.

## [0.4.0] - 2026-06-10

### Added
- **YoRHa 2B animated avatar** — added a built-in pixel-art avatar with GIF-derived motion clips for idle, thinking, talking, listening, smile, and confused states, plus static/animated gallery entries and localized persona metadata.
- **Accent palette themes (U-13)** — the Themes tab now switches real UI color themes: 5 accent palettes (Indigo default, Rose, Cyan, Ember, Mono) defined as token overrides in `Tokens.uss`, applied via a `theme-*` class on `app-root`. New "Палитра" card with color swatches (built in C# by `SettingsController`), persisted as `uiTheme` in `AppSettings`. New static `ThemeColors` supplies the current accent to C#-styled popups (message context menu, session picker, history context menu); chat-stage halo and Themes preview follow the palette via `--accent-soft`/`--accent-glow`.
- **Seekable voice bubbles** — cached user/assistant audio now has an animated playback timeline, elapsed/total time, drag/tap seeking, and a real play/pause toggle.
- **Voice operation feedback** — recorded WAV previews now appear in the composer immediately while STT is still transcribing; assistant headphones actions animate while TTS audio is being prepared and return to normal when playback actually starts; Android recording start/stop uses light haptic feedback.
- **Adaptive form factor detection** — `LayoutController` rewrite (577 lines): resolves Phone/Tablet/Desktop from physical width via `ConstantPhysicalSize` breakpoints. Phone: off-canvas drawer (rail) + fullscreen avatar overlay with scrim. Tablet/Desktop: `app--compact` / `app--narrow` sub-breakpoints, auto-hide avatar panel. Safe-area padding recomputed on every geometry change (rotation). `ff-phone` / `ff-tablet` / `ff-desktop` classes on app-root.
- **PlatformLayoutAdapter removed** — all platform-adaptive layout logic consolidated into `LayoutController`. No more split between two classes.
- **Localization via Resources** — JSON localization files moved from `StreamingAssets/` to `Resources/Localization/`. Uses `Resources.Load<TextAsset>()` (works synchronously on every platform, including inside Android APK where StreamingAssets files can't be read with `File.*`).
- **AndroidHeadlessBuild diagnostic** — `DiagEntry()` method for runtime `applicationEntry` enum inspection. Icon set from `Assets/UI/Branding/app-icon-1024.png` during headless build.
- **Multiplexed session transport** — IChatTransport events now carry `sessionId` for parallel session streaming. Per-session busy/awaiting/runtime-info state in `HermesSessionManager`. `ChatService` owns `HermesStream` per display-session-id with independent buffers, callbacks, and TCS. Background sessions generate silently; foreground re-attach preserves partial replies.
- **Session status indicators** — sidebar shows per-session pulsing dots: cyan = generating, orange = needs attention (pending approval/clarify). `SessionNeedsAttention()` / `IsSessionGenerating()` on ChatService.
- **Session listing via WS** — `session.list` RPC in `HermesSessionManager.ListSessions()`. Server DB is source of truth for session history; local JSON repo ignored in Hermes mode.
- **Session deletion** — `CloseSession()` (WS `session.close` for in-memory cleanup) + `RestClient.DeleteSession()` (REST `DELETE /api/sessions/{id}` for physical DB removal). Orphaning of child sessions, FK-safe.
- **Runtime vs display ID mapping** — `session.create` returns both `session_id` (runtime) and `stored_session_id` (DB). Client translates via `_runtimeByDisplaySession` / `_displayByRuntimeSession`. WS events route correctly across session switches.
- **WS connection guard** — `SwitchToHermesSessionAsync` ensures WS is connected before `ResumeSession` (fixes silent failure when switching sessions before first `StartNewSession`).

### Changed
- **Avatars preview card decluttered (U-14)** — fake "Параметры" section (hardcoded values) removed; persona block collapsed into a "Персона" foldout with edit/reset buttons and the inline editor inside it; customization reduced to the emoji overlay only — color tint, accent border, halo color/intensity, saturation/brightness sliders, and frame styles removed end-to-end (UXML, `AvatarCustomizationPanel`, `AvatarCustomizationData`, both controller copies, USS). Action row now holds only Применить + Удалить (the latter still shown only for custom avatars).
- `IChatTransport` interface: `SendMessage(sessionId, text)`, `Interrupt(sessionId)` — session-aware. Events carry `Action<string, ...>` signatures.
- `HermesSessionManager`: single `Busy`/`AwaitingResponse` → per-session dictionaries `IsSessionBusy(sessionId)`, `RuntimeInfoFor(sessionId)`.
- `ChatService.SendViaTransport()`: pinned to foreground session's stream context; mid-send UI switch doesn't misroute tokens.
- `ChatController`: `IsForegroundGenerating()` gates queue drain and send button per-session. `OnForegroundSessionChanged()` re-attach or abort streaming animation.
- `SessionHistoryController`: `RerenderStatus()` for live status dot refresh without server round-trip.

### Fixed
- **Voice composer allowed conflicting audio states** — the microphone now has an explicit dimmed/outlined disabled state while one audio preview is attached, preventing a second recording from replacing the first. Typed composer text is preserved and sent together with the voice transcription and the single audio bubble; typed text can also accompany audio when STT fails.
- **Hermes session can appear active for 30 minutes after a lost completion event** — generation now tracks token/reasoning/tool activity and triggers a 5-minute inactivity watchdog. The client reconciles against REST history, interrupts only if the turn is still incomplete, clears stale busy state, stops the elapsed timer, and marks unfinished tool entries as failed. Hermes REST requests now have a 30-second timeout.
- **Composer clips the first visible line** — composer scroll synchronization now clears stale vertical offset whenever the draft fits inside the viewport, while preserving bottom-follow only for genuinely overflowing text.
- **Enabled provider missing after restart** — startup now restores the last used `activeProviderId`, derives and restores its backend, installs it into `ChatService`, and makes the main UI await this initialization before opening sessions. Provider resolution also falls back to an enabled provider for the current backend.
- **Main scene startup stall after avatar asset migration** — built-in motion sheets are imported Unity sprites in `Resources` again instead of runtime-decoded PNG `TextAsset`s. The loading scene now preloads `res://` motion packs, avoiding the long CPU/memory spike after `Main` appears.
- **Cannot open sessions after provider selection** — `SetMode(Hermes)` creates transport but doesn't connect WS. `SwitchToHermesSessionAsync` now calls `ConnectHermes()` when `IsConnected` is false.
- **ResumeHermesSessionAsync silent failure** — added `IsConnected` guard to prevent RPC on closed socket.
- **Foreground stream misroute** — send now pinned to `sendSid`; completion renders only if user still views the target session.
- **Message queue cross-session drain** — queue only processes when foreground is idle.

- **Terminal remote execution for Hermes** — Phase 2 WS RPC: `terminal.execute` event handler + `terminal.respond` RPC. Client executes via ProcessExecutionService (local shell on user machine) and responds with stdout/stderr/exit/timed_out. Follows exact clarify/approval request-respond pattern (GatewayEvents, IsActiveEvent, Handle*, RespondTo*). Bridge in MainViewController subscribes OnTerminalExecute (Hermes-only), lazy-inits TerminalController, calls ExecuteRemoteCommand + RespondToTerminal. C# 9 compliant, no chat code changes beyond wiring.
- **Terminal emulator** — VT100/ANSI-compatible terminal emulator: `VtParser` (CSI/OSC/DCS sequence parsing), `ScreenBuffer` (2D cell grid with scrollback), `TerminalEmulator` (state machine: cursor movement, colors, erase, scroll, modes), `TerminalCell`/`TerminalColor`/`TerminalPalette` data model. Full VT sequence support: cursor ops, erase, scroll, SGR attributes (256-color + 24-bit), mode set/reset (DECAWM, DECOM, DECTCEM, bracketed paste).
- **PersistentShellService** — гибрид one-shot + persistent PTY для терминальных команд агента. Most commands: one-shot через ProcessExecutionService (надёжно, чистый exit code). Persistent PTY: через IPtySession когда нужна сессия (env/venv/cd persists). Маркер-based вывод изPTY. Зарегистрирован как инструмент в ToolRegistry.
- **Unix PTY** — `UnixPtySession` (forkpty/posix_spawn, non-blocking read/write через async), `NativePtyUnix` P/Invoke (libutil/libc). Полная поддержка кроме Windows (у которого уже есть ConPtySession).
- **TerminalScreenView** — UITK-based terminal renderer (`TerminalScreenView.uxml` + `.uss`). Character-grid rendering, selection, copy, scroll. Wired into `TerminalController` for local terminal mode.
- **WS client bridge foundation** — single-writer WebSocket sends, `client.register` capability registration, `client.ping`/`client.pong`, terminal response duration, bidirectional file-transfer DTOs, safe roots (`downloads/workspace/temp/session`), strict path validation, receive-to-client `.part` → SHA-256 → atomic move, and send-from-client streaming path. Gateway has `client_terminal`, `client_file_push`, and `client_file_pull` tools. Awaiting Felix Unity/end-to-end verification.

## [0.3.1] - 2026-06-06

### Added
- **Voice recording & preview** — VoicePreviewPlayer для записи и предпрослушивания голосовых сообщений перед отправкой. UI превью с кнопками Play/Send/Cancel.
- **Voice attachments в чате** — аудио-вложения отображаются как playble bubbles в ChatMessageListRenderer с поддержкой воспроизведения.
- **Voice settings UI** — выбор устройства ввода, громкость вывода, выбор устройства вывода (с ограничениями Unity), VAD параметры.
- **IVoiceService расширение** — новые методы для записи, preview, и availability tracking.
- **Localization** — новые ключи для voice preview, recording, settings (en + ru).

### Changed
- VoiceController значительно расширен: preview flow, attachment integration, state machine для recording.
- VoiceInputManager: улучшена обработка аудио, VAD integration, availability tracking.
- VoiceOutputManager: расширен для поддержки playback из recorded files.
- HermesVoiceService / OpenAiVoiceService: добавлены методы для preview и availability.

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
