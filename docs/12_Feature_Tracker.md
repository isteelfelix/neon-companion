# 12_Feature_Tracker.md

## Статусы
- ✅ Done — реализовано и проверено
- 🔧 In Progress — в разработке
- ⏳ Pending — ожидает проверки
- 📋 Planned — запланировано
- ❌ Blocked — заблокировано

---

## Чат и API
| # | Фича | Статус | Спринт | Заметки |
|---|------|--------|--------|---------|
| C-01 | Подключение OpenAI-совместимых API | ✅ | M0 | Verified: streaming works in daily use |
| C-02 | Множество провайдеров + переключение | ✅ | M0 | Verified: Hermes + OpenAI providers configured and switchable |
| C-03 | Провайдер-осознанные сессии | ✅ | M1 | Verified: sessions retain provider context |
| C-04 | Пресеты моделей | ✅ | M1 | Verified: model preset dropdown present |
| C-05 | Локализация UI | ✅ | M1 | Verified: all UI elements in Russian, no raw keys |
| C-06 | Авто-обнаружение моделей (ModelDiscoveryService) | ✅ | M1 | Verified: models discovered from /v1/models |
| C-07 | Модель-пикер в чате | ✅ | M1 | NeonDropdown в topbar + overlay — UI works, model list shows, switching works (OpenAI + Hermes tested) |
| C-08 | Вложения в чате | ✅ | M1 | Verified by Felix (same fix as U-41) |
| C-09 | Сессионная маршрутизация моделей | ✅ | M1 | Verified: Hermes knows current model per session |
| C-10 | Provider Adapter архитектура | ✅ | M2 | Model switching works for OpenAI and Hermes (tested 2026-06-08). Context overflow on undersized models is expected behavior. |
| C-11 | Gateway status + restart | ⏳ | M2 | Backend: gateway exposes status endpoint (running/stopped, uptime, model). Restart endpoint. Companion: displays status badge + restart button. Gateway-only, no client logic. |
| C-12 | Восстановление провайдера после рестарта | ✅ | M3 | Startup restores last activeProviderId, derives backend, installs into ChatService. Main UI awaits init before opening sessions (61c12e1) |

## История и сессии
| # | Фича | Статус | Спринт | Заметки |
|---|------|--------|--------|---------| 
| H-01 | Сохранение истории чата | ✅ | M0 | Verified |
| H-02 | Экран истории | ✅ | M1 | Verified: delete sessions works |
| H-03 | Удаление отдельных сессий | ✅ | M1 | Verified. WS close + REST delete (FK-safe orphaning) |
| H-04 | Папки для сессий (как проекты) | ✅ | M2 | Fixed in 477ab2a: inline styles + WorldToLocal positioning + anti-self-close guard + proper folder input popup |
| H-05 | Multiplexed parallel sessions | ✅ | M3 | Per-session HermesStream, background generate, foreground re-attach. IChatTransport events carry sessionId |
| H-06 | Session status indicators | ✅ | M3 | Sidebar pulsing dots: cyan=generating, orange=needs attention. RerenderStatus() refresh |
| H-07 | Runtime vs display ID mapping | ✅ | M3 | session.create → stored_session_id (DB) + session_id (runtime). WS events route via _displayByRuntimeSession |
| H-08 | Session listing via WS | ✅ | M3 | session.list RPC. Server DB = source of truth in Hermes mode |
| H-09 | WS connection guard | ✅ | M3 | SwitchToHermesSessionAsync connects WS before ResumeSession (fix for silent failure) |

## Аватары
| # | Фича | Статус | Спринт | Заметки |
|---|------|--------|--------|---------|
| A-01 | Статичные 2D аватары | ✅ | M0 | Verified: avatars display and selectable |
| A-02 | Кастомные аватары (загрузка) | ✅ | M1 | Verified by Felix: загрузка аватара в настройках работает |
| A-03 | Persona/инструкции аватара | ✅ | M1 | Verified: persona edit and save work |
| A-04 | Scale-and-crop фон | ✅ | M1 | Verified by Felix: scale slider works |
| A-05 | Анимация спрайтшитами | ✅ | M1 | Verified: sprite animation works in chat @@
| A-06 | Базовая анимация аватаров | ✅ | M1 | Verified: idle and talking animations work @@
| A-07 | 2D motion-pack MVP contract | ✅ | M1 | Verified: motion pack triggers correctly @@
| A-08 | Asset-pipeline research для 2D motion packs | 📋 | M2 | Research task — no user testing needed |
| A-09 | Загрузка спрайтшитов — производительность | ✅ | M2 | Verified by Felix: ApplyAvatarViewMode() called at startup. Avatars in Resources as imported Unity sprites (.png). frameCount field in motion_pack avoids pixel reads (61c12e1) |
| A-10 | Довести анимацию спрайтшитов до рабочего состояния | ✅ | M2 | Talking/listening/confused триггеры |
| A-11 | Система триггерных анимаций | ✅ | M2 | Verified: avatar transitions idle→thinking→talking |

## UI и UX
| # | Фича | Статус | Спринт | Заметки |
|---|------|--------|--------|---------|
| U-01 | Dark UI (UI Toolkit) | ✅ | M0 | Verified: dark theme active in all screens |
| U-02 | Темы | ✅ | M1 | Verified: theme switcher works |
| U-03 | Composer overflow fix | ✅ | M1 | Verified: multiline composer works correctly |
| U-04 | NeonDropdown (кастомный компонент) | ✅ | M1 | Verified: used in model picker and settings |
| U-05 | Многострочный ввод сообщений | ✅ | M1 | Verified: multiline with Shift+Enter newline |
| U-06 | Масштабируемый рельс сайдбара | ✅ | M1 | Verified: rail resize works |
| U-07 | Режимы отображения аватара | ✅ | M1 | Verified: Static/Animated/3D switch works @@
| U-08 | Кнопка выхода в настройках | ✅ | M2 | |
| U-09 | Кнопка Резюме (summarize) | ✅ | M2 | |
| U-10 | Иконка приложения в интерфейсе | ✅ | M2 | Verified by Felix: removed "N" from rail + brand__mark updated to app-icon-1024.png |
| U-11 | Баг: сжатие левой панели | ✅ | M2 | overflow: hidden на .rail |
| U-12 | Баг: многострочный ввод — переполнение | ✅ | M2 | Felix fixed in 2abf128: stable UITK TextField Enter routing, Shift+Enter newline, no stale/double submit |
| U-13 | Вкладка Темы — переосмысление | 📋 | M2 | Текущая реализация бесполезна |
| U-14 | Настройки аватара — перегруженность | 📋 | M2 | |
| U-15 | Сцена загрузки (splash screen) | ✅ | M2 | Cyberpunk splash + dynamic effects. SplashViewController removed as dead code (18d0e2b) |
| U-16 | Маска API-ключа в редакторе провайдера | ✅ | M2 | Eye toggle button |
| U-17 | Дашборд запланированных задач (cron) | 📋 | M3 | |
| U-18 | Agent Activity UI | ✅ | M2 | Thinking bubble + tool progress |
| U-19 | Typing indicator в bubble ответа | ✅ | M2 | 3 точки внутри response bubble |
| U-20 | Ленивая загрузка спрайтшитов | ✅ | M2 | Verified by Felix: same fix as A-09 |
| U-21 | Scroll-to-bottom в чате | ✅ | M2 | |
| U-22 | Enter-to-send | ✅ | M2 | Felix fixed in 2abf128: Enter/Ctrl+Enter/Shift+Enter routing works across send modes |
| U-23 | Clear chats only | ✅ | M2 | |
| U-24 | Action buttons в bubble | ✅ | M2 | Copy/refresh/listen |
| U-25 | Автоскролл при стриминге | ✅ | M2 | |
| U-26 | Toggle панелей | ✅ | M2 | |
| U-27 | Счётчик токенов + время ответа | ✅ | M2 | |
| U-28 | Precise usage данные (stream_options) | ✅ | M3 | Verified: token count + response time shown in context bar and transcript stats |
| U-29 | Редактирование сообщений | ✅ | M2 | Felix fixed in 743d0a7: functional message context menu and edit flow |
| U-30 | Удаление отдельных сообщений | ✅ | M2 | Felix fixed in 743d0a7: context-menu delete flow works |
| U-31 | Выделение сообщений | ✅ | M2 | Felix fixed in 743d0a7: selection mode reachable from message context menu |
| U-32 | Удаление выделенных | ✅ | M2 | Felix fixed in 743d0a7: selected-message delete flow restored |
| U-33 | Пересылка выделенных в другой чат | ⏳ | M2 | UI-операция: пересланные сообщения отображаются в целевом чате, но агент их не видит — они не попадают в session history на gateway. Нужно при пересылке отправлять в backend сессии. |
| U-34 | Выделение текста в сообщениях | ✅ | M2 | Verified by Felix: Label→TextField, I-beam cursor, long-press guard |
| U-35 | Markdown разметка в сообщениях | ✅ | M2 | **Upgraded to SelectableMarkdownElement** — full native rendering engine: block model (paragraph/heading/quote/list/code/table/rule), inline tokenizer (bold/italic/strike/code/links), word-wrap, glyph-level selection, streaming block-level reconciliation. Syntax highlighting for 15+ languages. Diff-fenced code blocks with +/-/@@ coloring. Design tokens throughout. Previously: TextField-based with basic markdown parsing. |
| U-36 | Индикатор контекстного окна | ✅ | M2 | Verified by Felix: real context_length from discovery API, fallback chain to heuristics |
| U-37 | Экспорт чата | ✅ | M2 | Verified by Felix: save-file dialog via IFilePickerService + Windows SaveFileDialog + iOS fallback |
| U-38 | Поиск по текущему чату | ✅ | M2 | Работает |
| U-39 | Ветвление диалога | 📋 | M3 | |
| U-40 | Звуки уведомлений | ✅ | M2 | Verified: PCM beep plays on new assistant reply |
| U-41 | Отображение картинок в чате | ✅ | M2 | Verified by Felix |
| U-42 | Вставка изображений из буфера обмена | ✅ | M2 | Verified by Felix: full Windows clipboard bitmap support (PNG/JFIF/CF_DIB via P/Invoke), DIB→PNG conversion, text paste no longer intercepted, composer preview works |
| U-43 | Мульти-агент чат | 📋 | M3 | |
| U-44 | Drag-and-drop файлов в чат | ✅ | M2 | Verified by Felix: Windows standalone drag-and-drop works; dropped supported files become pending composer attachments/previews |
| U-45 | Очередь сообщений | ✅ | M2 | Verified: queue visible when sending while response in progress |
| U-46 | Кнопка стоп (отмена генерации) | ✅ | M2 | Работает |
| U-47 | Система команд в чате | ✅ | M2 | Работает |
| U-48 | Agent Approval System (Part B) | ✅ | M2 | Verified by Felix: local OpenAI tool-call approval blocks before ToolExecutor; Hermes SSE approval/request/progress statuses surface in-chat approval prompt |
| U-49 | Входящие вложения от AI | ⏳ | M2 | Client-side fixed in 7ce26be (MEDIA: parsing, path resolution, magic bytes). Blocked: gateway serves HTML instead of actual images — needs gateway-level image serving layer |
| U-60 | Tools UI в бабле — расположение | ✅ | M2 | Fixed 9db20fa: replaced two-pass render (all text then all tools) with single-pass over segments — tool calls now appear inline in streaming order |
| U-61 | Время ответа + токены под каждым сообщением | ✅ | M2 | Fixed: responseTimeSeconds now always persisted (moved outside usage-check block) — every assistant message shows at least response time in footer |
| U-62 | Контекст сессии (общие токены) | 🔧 | M2 | Считается некорректно. Должен обновляться при входе в чат и смене модели. Сравнить с расчётом Hermes Desktop | **BUG**: UpdateContextBar() только из RenderMessages(). Не обновляется при смене модели/чата. FIX: вызывать при смене ActiveSessionId, после SwitchModelAsync(), при session.info event. Контекст = бэкенд (context_max/context_used/context_percent), не клиент. |
| U-50 | Баг: анимированный аватар в вкладке Статика | ✅ | M2 | Verified by Felix: mode check before null guard + HideAllAvatarImageOverlays |
| U-51 | Баг: переключение между чатами | ✅ | M2 | Verified by Felix: 95fe0a3 fixed transcript reload after switching chats |
| U-52 | Image Lightbox (просмотр картинок) | ✅ | M2 | Verified by Felix: клик по картинке в чате/превью → полноэкранный оверлей, ESC закрывает, close button |
| U-53 | IsImageFilePath crash on control characters | ✅ | M2 | Path.GetExtension throws ArgumentException on newlines/tabs in pasted multi-line markdown. Fixed: GetInvalidPathChars guard + try/catch. |
| U-54 | Terminal remote exec (Hermes WS RPC) | ✅ | M2 | Verified by Felix: GatewayEvents.TerminalExecute, RpcMethods.TerminalRespond, TerminalExecutePayload/Request, HandleTerminalExecute, RespondToTerminal, TerminalController.ExecuteRemoteCommand, MainViewController bridge — end-to-end working |
| U-55 | Terminal emulator (VT100/ANSI) | ✅ | M2 | Verified by Felix: VtParser (CSI/OSC/DCS), ScreenBuffer (2D grid + scrollback), TerminalEmulator (cursor, colors, erase, scroll, SGR 256/24bit, modes DECAWM/DECOM/DECTCEM/bracketed paste) — working |
| U-56 | PTY sessions (IPtySession + ConPTY) | ✅ | M2 | Verified by Felix: IPtySession interface, PtySessionFactory. Windows: ConPtySession (CreatePseudoConsole, ReadFile/WriteFile), NativePtyWindows P/Invoke. Unix: UnixPtySession (forkpty/posix_spawn), NativePtyUnix — working |
| U-57 | TerminalScreenView (UITK renderer) | ✅ | M2 | Verified by Felix: Character-grid UITK rendering, selection, copy, scroll. TerminalScreenView.uxml + .uss. Wired into TerminalController — working |
| U-58 | PersistentShellService (гибрид one-shot + PTY) | ✅ | M2 | Verified by Felix: Гибрид one-shot + PTY, маркер-based вывод, зарегистрирован в ToolRegistry — working |
| U-59 | WS client bridge capabilities + file transfer | ✅ | M2 | Verified by Felix: single-writer WS sends, client.register, client.ping/pong, bidirectional file transfer — end-to-end working |
| U-63 | SafeLinkOpener (безопасное открытие ссылок) | ✅ | M3 | Whitelist http/https/mailto. file://, javascript:, custom schemes refused. Используется в markdown рендере для ссылок из сообщений ассистента |
| U-64 | DeviceSecretStore (хранение секретов) | ✅ | M3 | ISecretStore implementation: OS keystore (Windows DPAPI, Android KeyStore) с fallback на device-xor-v1 obfuscation. IsObfuscationOnly flag для UI warning |

## Голос и 3D (M2+)
| # | Фича | Статус | Спринт | Заметки |
|---|------|--------|--------|---------|
| V-01 | Голосовой ввод/вывод | ✅ | M2 | Voice input/output fully implemented: VoiceInputManager, VoiceController, VoicePreviewPlayer, settings UI, chat audio attachments, HermesVoiceService + OpenAiVoiceService. VoiceOutputManager + LipsyncController removed as dead code (18d0e2b) |
| V-02 | Lipsync | 📋 | M3 | Blocked on V-01 completion → ready to start. Depends on avatar motion system. |
| V-03 | 3D аватары | 📋 | M3 | Deferred: 3D models not added to project yet |
| V-04 | Desktop realtime avatar layer | 📋 | M2+ | |
| V-05 | Проигрывание аудио из бабла | 📋 | M2+ | Сейчас при клике на бабл с аудио проигрывается TTS-озвучка, а не оригинальный звук. Нужно чтобы воспроизводился именно звук аудио-бабла |
| V-06 | Персистентное хранение аудио бабла | 📋 | M2+ | Сейчас аудио-файлы хранятся в кеше (TTL ~5 мин). Нужно постоянное хранение аудио, привязанного к сообщению |
| V-07 | Отложенная отправка аудио на STT | 📋 | M2+ | Сейчас после записи аудио бабл сразу отправляется на STT. Нужно: запись → превью в композере → отправка на STT по кнопке "Отправить" |

## Рефакторинг (M2)
| # | Фича | Статус | Спринт | Заметки |
|---|------|--------|--------|---------|
| R-01 | NavigationController | ✅ | M2 | 317 строк — extracted, integrated, later removed as dead code (18d0e2b) |
| R-02 | ChatController | ✅ | M2 | 1315 строк — 11 sub-classes extracted (5477→1315, −76%). Later removed as dead code (18d0e2b) |
| R-03 | SessionHistoryController | ✅ | M2 | 366 строк — extracted and integrated |
| R-04 | ProvidersController | ✅ | M2 | 1381 строка — extracted and integrated |
| R-05 | AvatarGalleryController | ✅ | M2 | 1794 строки — extracted and integrated |
| R-06 | VoiceController | ✅ | M2 | 202 строки — extracted and integrated |
| R-07 | LayoutController | ✅ | M2 | 138 строк — extracted and integrated |
| R-08 | SettingsController | ✅ | M2 | 1005 строк — extracted and integrated |
| R-09 | PanelResizeHandler | ✅ | M2 | Логика resize — extracted and integrated |

## VR (M3+)
| # | Фича | Статус | Спринт | Заметки |
|---|------|--------|--------|---------|
| VR-01 | Поддержка VR (Quest, PCVR) | 📋 | M3 | |
| VR-02 | Кастомизация аватаров | 🔧 | M3 | |
| VR-03 | Плагины и расширения | 🔧 | M3 | IPlugin, PluginManager, DLL loading |

## Публикация (M4)
| # | Фича | Статус | Спринт | Заметки |
|---|------|--------|--------|---------|
| P-01 | itch.io / GitHub Releases | 🔧 | M4 | Ручная публикация |
| P-02 | Документация для контрибьюторов | 🔧 | M4 | |
| P-03 | Донат-система | 🔧 | M4 | IDonationService |

## Платформа и Android (M3+)
| # | Фича | Статус | Спринт | Заметки |
|---|------|--------|--------|---------|
| PL-01 | Полная поддержка Android как целевой платформы | 🔧 | M3 | IL2CPP + Build Profile (Android.asset); .aab for release. ForceClassicActivityEntry via reflection фиксит Unity 6 GameActivity. DiagEntry() для diagnostics |
|| PL-02 | Доработка IFilePickerService под Android (нативный Java плагин NeonFilePickerActivity) | 🔧 | M3 | NeonFilePickerActivity.java + Intent + runtime permission via AndroidPermissionHelper + cache copy |\n|| PL-03 | Android permissions и AndroidManifest.xml | 🔧 | M3 | Создан + обновлён Assets/Plugins/Android/AndroidManifest.xml (permissions + NeonFilePickerActivity + NeonSpeechRecognitionActivity declarations). Duplicate old NeonFilePickerActivity.java удалён. useCustomMainManifest=1 in Android.asset (done) |\n|| PL-04 | Адаптация UI под мобильные экраны (тач, клавиатура, safe area, разные DPI) | 🔧 | M3 | LayoutController handles all adaptive layout (PlatformLayoutAdapter + AndroidKeyboardInset removed as dead code). Safe area recalc on rotation. USS правила расширены |\n| PL-05 | Голос на Android (TTS + SpeechRecognizer вместо DictationRecognizer) | 🔧 | M3 | Полная интеграция: NeonSpeechRecognitionActivity.java + AndroidSpeechIntentHelper + AndroidSpeechRecognitionBridge + OnAndroidSpeechResult в WebSpeechBridge + proper UtteranceProgressListener для TTS |
| PL-06 | Тестирование и фиксы runtime на Android (persistentDataPath, IL2CPP stripping, спрайтшиты) | ✅ | M3 | Android сборка рабочая и юзабельная на реальном устройстве. Верификация Felix (июнь 2026) |
| PL-07 | Документация по сборке Android в AGENTS.md и README | ✅ | M3 | Полный раздел Android Build в AGENTS.md (пререквизиты, профили, код, runtime, тестирование, caveats). Architecture doc уже покрывает принципы. |

## Платформа iOS (M4+)

| # | Фича | Статус | Спринт | Заметки |
|---|------|--------|--------|---------|
| IOS-01 | Полная поддержка iOS как целевой платформы | 🔧 | M4 | PlatformServiceFactory + iOS Build Profile + Info.plist + services. WebSpeechBridge fully wired for iOS. |
| IOS-02 | iOSFilePickerService + нативный плагин (UIDocumentPicker / PHPicker) | 🔧 | M4 | Full iOSFilePickerService + iOSFilePickerBridge.cs + expanded NeonFilePicker.mm with UnitySendMessage. |
| IOS-03 | iOS permissions (Info.plist + runtime) + unified PermissionHelper | 🔧 | M4 | Info.plist with keys present. iOSPermissionHelper.cs removed as dead code (18d0e2b) |
| IOS-04 | Расширение PlatformServiceFactory под iOS | 🔧 | M4 | iOS branches added for FilePicker and Voice (routes to WebSpeechBridge for now) |
| IOS-05 | Голос на iOS (AVSpeechSynthesizer + SFSpeechRecognizer) | 🔧 | M4 | Complete: NeonSpeech.mm (AVSpeech + SFSpeech stubs + callbacks), iOSSpeechBridge, WebSpeechBridge iOS DllImport + routing + InitializeIOS. |
| IOS-06 | Keyboard inset + улучшенная safe area для iPad / notch | 🔧 | M4 | DefaultPlatformInfoService updated for iOS safeArea. iOSKeyboardInset.cs removed — LayoutController handles safe area |
| IOS-07 | .platform-ios USS правила + LayoutController (единый адаптивный контроллер) | 🔧 | M4 | .platform-ios rules added to MainView.uss. LayoutController handles platform-ios class + safe area. PlatformLayoutAdapter removed — logic consolidated. |
| IOS-08 | Документация iOS в AGENTS.md + 17_iOS_Platform_Architecture.md | 🔧 | M4 | Full docs + tracker + AGENTS.md cross-refs. iOS sections added. |

**Примечание:** iOS и Android делят общую мобильную логику через `IsMobile` + `.platform-mobile`. Специфика изолирована в Platform/iOS/ и Platform/Android/. См. docs/17_iOS_Platform_Architecture.md и docs/16_Platform_Architecture.md.
