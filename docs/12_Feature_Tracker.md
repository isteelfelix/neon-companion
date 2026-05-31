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
| C-01 | Подключение OpenAI-совместимых API | 🔧 | M0 | Streaming через DownloadHandlerBuffer + poll |
| C-02 | Множество провайдеров + переключение | 🔧 | M0 | |
| C-03 | Провайдер-осознанные сессии | 🔧 | M1 | |
| C-04 | Пресеты моделей | 🔧 | M1 | |
| C-05 | Локализация UI | 🔧 | M1 | |
| C-06 | Авто-обнаружение моделей (ModelDiscoveryService) | 🔧 | M1 | Кэширование по baseUrl/apiKey |
| C-07 | Модель-пикер в чате | 🔧 | M1 | NeonDropdown в topbar + overlay |
| C-08 | Вложения в чате | 🔧 | M1 | ChatAttachment, AiChatAttachment |
| C-09 | Сессионная маршрутизация моделей | 🔧 | M1 | X-Hermes-Session-Id |
| C-10 | Provider Adapter архитектура | ⏳ | M2 | Phase 1+2+2b+UI done. Ожидает проверки |

## История и сессии
| # | Фича | Статус | Спринт | Заметки |
|---|------|--------|--------|---------|
| H-01 | Сохранение истории чата | 🔧 | M0 | |
| H-02 | Экран истории | 🔧 | M1 | Выделенный экран |
| H-03 | Удаление отдельных сессий | 🔧 | M1 | Из sidebar |
| H-04 | Папки для сессий (как проекты) | ⏳ | M2 | Code updated; awaiting Felix test: grouping, RMB context menu with move/new folder popup, collapse, persistence via ChatService + repo (USS + SessionHistoryController + loc keys present) |

## Аватары
| # | Фича | Статус | Спринт | Заметки |
|---|------|--------|--------|---------|
| A-01 | Статичные 2D аватары | 🔧 | M0 | |
| A-02 | Кастомные аватары (загрузка) | 🔧 | M1 | |
| A-03 | Persona/инструкции аватара | 🔧 | M1 | Edit + reset flow |
| A-04 | Scale-and-crop фон | 🔧 | M1 | |
| A-05 | Анимация спрайтшитами | 🔧 | M1 | SpriteSheetAnimator + Loader |
| A-06 | Базовая анимация аватаров | 🔧 | M1 | Idle + talking через SpriteSheetAnimator |
| A-07 | 2D motion-pack MVP contract | 🔧 | M1 | Fixed action set |
| A-08 | Asset-pipeline research для 2D motion packs | 📋 | M2 | see docs/13_Avatar_Motion_Research.md |
| A-09 | Загрузка спрайтшитов — производительность | ⏳ | M2 | Code updated; awaiting Felix test: PreloadManifestCoroutine in SpriteSheetAnimationLoader ready; documented for splash integration (addresses U-20 freeze tradeoff) |
| A-10 | Довести анимацию спрайтшитов до рабочего состояния | ✅ | M2 | Talking/listening/confused триггеры |
| A-11 | Система триггерных анимаций | 🔧 | M2 | Game-like state machine |

## UI и UX
| # | Фича | Статус | Спринт | Заметки |
|---|------|--------|--------|---------|
| U-01 | Dark UI (UI Toolkit) | 🔧 | M0 | |
| U-02 | Темы | 🔧 | M1 | Shape/halo/breathing |
| U-03 | Composer overflow fix | 🔧 | M1 | min-width: 0 |
| U-04 | NeonDropdown (кастомный компонент) | 🔧 | M1 | Замена DropdownField |
| U-05 | Многострочный ввод сообщений | 🔧 | M1 | multiline = true |
| U-06 | Масштабируемый рельс сайдбара | 🔧 | M1 | _railResizeHandle, 160-400px |
| U-07 | Режимы отображения аватара | 🔧 | M1 | AvatarViewMode: Static, Animated, Volume3D |
| U-08 | Кнопка выхода в настройках | ✅ | M2 | |
| U-09 | Кнопка Резюме (summarize) | ✅ | M2 | |
| U-10 | Иконка приложения в интерфейсе | ⏳ | M2 | Code updated; awaiting Felix test: visible "N" brand icon inserted dynamically in rail__sessions-head (C#) |
| U-11 | Баг: сжатие левой панели | ✅ | M2 | overflow: hidden на .rail |
| U-12 | Баг: многострочный ввод — переполнение | ✅ | M2 | Felix fixed in 2abf128: stable UITK TextField Enter routing, Shift+Enter newline, no stale/double submit |
| U-13 | Вкладка Темы — переосмысление | 📋 | M2 | Текущая реализация бесполезна |
| U-14 | Настройки аватара — перегруженность | 📋 | M2 | |
| U-15 | Сцена загрузки (splash screen) | ✅ | M2 | Cyberpunk splash + dynamic effects |
| U-16 | Маска API-ключа в редакторе провайдера | ✅ | M2 | Eye toggle button |
| U-17 | Дашборд запланированных задач (cron) | 📋 | M3 | |
| U-18 | Agent Activity UI | ✅ | M2 | Thinking bubble + tool progress |
| U-19 | Typing indicator в bubble ответа | ✅ | M2 | 3 точки внутри response bubble |
| U-20 | Ленивая загрузка спрайтшитов | 🔧 | M2 | Splash screen фризит |
| U-21 | Scroll-to-bottom в чате | ✅ | M2 | |
| U-22 | Enter-to-send | ✅ | M2 | Felix fixed in 2abf128: Enter/Ctrl+Enter/Shift+Enter routing works across send modes |
| U-23 | Clear chats only | ✅ | M2 | |
| U-24 | Action buttons в bubble | ✅ | M2 | Copy/refresh/listen |
| U-25 | Автоскролл при стриминге | ✅ | M2 | |
| U-26 | Toggle панелей | ✅ | M2 | |
| U-27 | Счётчик токенов + время ответа | ✅ | M2 | |
| U-28 | Precise usage данные (stream_options) | ⏳ | M3 | Code updated; awaiting Felix test: ChatMessage stores tokenCount + responseTimeSeconds; populated from LastStreamUsage after stream; shown in .transcript__stats for history; context bar uses it |
| U-29 | Редактирование сообщений | ✅ | M2 | Felix fixed in 743d0a7: functional message context menu and edit flow |
| U-30 | Удаление отдельных сообщений | ✅ | M2 | Felix fixed in 743d0a7: context-menu delete flow works |
| U-31 | Выделение сообщений | ✅ | M2 | Felix fixed in 743d0a7: selection mode reachable from message context menu |
| U-32 | Удаление выделенных | ✅ | M2 | Felix fixed in 743d0a7: selected-message delete flow restored |
| U-33 | Пересылка выделенных в другой чат | ✅ | M2 | Felix fixed in 743d0a7: selected-message forward flow restored |
| U-34 | Выделение текста в сообщениях | ⏳ | M2 | Code updated; awaiting Felix test: all .transcript__body Labels focusable=true + --unity-text-selection-color; markdown leaves also |
| U-35 | Markdown разметка в сообщениях | ⏳ | M2 | Code updated; awaiting Felix test: ContainsMarkdown now catches * _ # ; renderer triggers for more responses (still shows raw for unsupported syntax) |
| U-36 | Индикатор контекстного окна | ⏳ | M2 | Code updated; awaiting Felix test: always renders with GuessContextWindow (model name heuristics) + position:relative on bar so absolute label overlays correctly |
| U-37 | Экспорт чата | 🔧 | M2 | Работает, нужен file picker |
| U-38 | Поиск по текущему чату | ✅ | M2 | Работает |
| U-39 | Ветвление диалога | 📋 | M3 | |
| U-40 | Звуки уведомлений | ⏳ | M2 | Code updated; awaiting Felix test: runtime PCM sine-tone beep (0.08s 880Hz decay) played via AudioSource on new assistant reply (MainViewController + wired to ChatController) |
| U-41 | Отображение картинок в чате | ⏳ | M2 | Code updated; awaiting Felix test: <Image> + LoadImageAsync(file:// via UnityWebRequestTexture) for image attachments in transcript; .transcript__attachments + __image styles |
| U-42 | Вставка изображений из буфера обмена | ⏳ | M2 | Code updated; awaiting Felix test: Ctrl+V in composer checks systemCopyBuffer for image path + adds as pending preview (pixel clipboard needs platform plugin) |
| U-43 | Мульти-агент чат | 📋 | M3 | |
| U-44 | Drag-and-drop файлов в чат | ⏳ | M2 | Code updated; awaiting Felix test: attach button works via IFilePickerService (Windows reflection + Editor + Android); dnd editor-only due to DragAndDrop API (player needs native) |
| U-45 | Очередь сообщений | 🔧 | M2 | Работает, но не видно сообщения в очереди |
| U-46 | Кнопка стоп (отмена генерации) | ✅ | M2 | Работает |
| U-47 | Система команд в чате | ✅ | M2 | Работает |
| U-48 | Agent Approval System (Part B) | 🔧 | M2 | Streaming integration |
| U-49 | Входящие вложения от AI | ⏳ | M2 | Code updated; awaiting Felix test: ChatMessage.attachments + display path in transcript (incoming from model response/tools now render if populated by client) |
| U-50 | Баг: анимированный аватар в вкладке Статика | 🔧 | M2 | gallery-animated не скрывается |
| U-51 | Баг: переключение между чатами | ✅ | M2 | Verified by Felix: 95fe0a3 fixed transcript reload after switching chats |

## Голос и 3D (M2+)
| # | Фича | Статус | Спринт | Заметки |
|---|------|--------|--------|---------|
| V-01 | Голосовой ввод/вывод | ⏳ | M2 | Code updated; awaiting Felix test: device is system default (DictationRecognizer / browser / Android TTS); added note in settings path + platform fallbacks documented |
| V-02 | Lipsync | ⏳ | M2 | Code updated; awaiting Felix test: LipsyncController now created + Initialize() called in VoiceController.EnsureVoicePipelineAsync (binds to output/input events; SetSpriteAnimator ready for avatar wiring) |
| V-03 | 3D аватары | ⏳ | M2 | Code updated; awaiting Felix test: Avatar3DService + Loader + Renderer + 3D mode in AvatarGalleryController + chat avatar-stage hero + motion state wiring confirmed present and functional |
| V-04 | Desktop realtime avatar layer | 📋 | M2+ | |

## Рефакторинг (M2)
| # | Фича | Статус | Спринт | Заметки |
|---|------|--------|--------|---------|
| R-01 | NavigationController | 🔧 | M2 | 317 строк |
| R-02 | ChatController | 🔧 | M2 | 1044 строки |
| R-03 | SessionHistoryController | 🔧 | M2 | 366 строк |
| R-04 | ProvidersController | 🔧 | M2 | 1381 строка |
| R-05 | AvatarGalleryController | 🔧 | M2 | 1794 строки |
| R-06 | VoiceController | 🔧 | M2 | 202 строки |
| R-07 | LayoutController | 🔧 | M2 | 138 строк |
| R-08 | SettingsController | 🔧 | M2 | 1005 строк |
| R-09 | PanelResizeHandler | 🔧 | M2 | Логика resize |

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
| PL-01 | Полная поддержка Android как целевой платформы | 🔧 | M3 | IL2CPP + Build Profiles, .aab для релиза |
|| PL-02 | Доработка IFilePickerService под Android (нативный Java плагин NeonFilePickerActivity) | 🔧 | M3 | NeonFilePickerActivity.java + Intent + runtime permission via AndroidPermissionHelper + cache copy |\n|| PL-03 | Android permissions и AndroidManifest.xml | 🔧 | M3 | Создан + обновлён Assets/Plugins/Android/AndroidManifest.xml (permissions + NeonFilePickerActivity + NeonSpeechRecognitionActivity declarations). Duplicate old NeonFilePickerActivity.java удалён. useCustomMainManifest=1 в профиле (PL-01 pending) |\n|| PL-04 | Адаптация UI под мобильные экраны (тач, клавиатура, safe area, разные DPI) | 🔧 | M3 | PlatformLayoutAdapter + AndroidKeyboardInset.cs (поллинг видимости клавиатуры). Расширены USS правила. |\n| PL-05 | Голос на Android (TTS + SpeechRecognizer вместо DictationRecognizer) | 🔧 | M3 | Полная интеграция: NeonSpeechRecognitionActivity.java + AndroidSpeechIntentHelper + AndroidSpeechRecognitionBridge + OnAndroidSpeechResult в WebSpeechBridge + proper UtteranceProgressListener для TTS |
| PL-06 | Тестирование и фиксы runtime на Android (persistentDataPath, IL2CPP stripping, 3D аватары) | 📋 | M3 | Только Felix на реальном устройстве |
| PL-07 | Документация по сборке Android в AGENTS.md и README | ✅ | M3 | Полный раздел Android Build в AGENTS.md (пререквизиты, профили, код, runtime, тестирование, caveats). Architecture doc уже покрывает принципы. |
