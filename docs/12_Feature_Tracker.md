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
| C-01 | Подключение OpenAI-совместимых API | ✅ | M0 | Streaming через DownloadHandlerBuffer + poll |
| C-02 | Множество провайдеров + переключение | ✅ | M0 | |
| C-03 | Провайдер-осознанные сессии | ✅ | M1 | |
| C-04 | Пресеты моделей | ✅ | M1 | |
| C-05 | Локализация UI | ✅ | M1 | |
| C-06 | Авто-обнаружение моделей (ModelDiscoveryService) | ✅ | M1 | Кэширование по baseUrl\|apiKey, `/models` эндпоинт |
| C-07 | Модель-пикер в чате | ✅ | M1 | NeonDropdown в topbar + overlay-диалог |
| C-08 | Вложения в чате | ✅ | M1 | `ChatAttachment`, `AiChatAttachment` |
| C-09 | Сессионная маршрутизация моделей | ✅ | M1 | `X-Hermes-Session-Id`, Hermes inventory |
| C-10 | Provider Adapter архитектура | 🔧 | M2 | Phase 1 done: `IProviderAdapter` + adapters. Подробности — [14_Provider_Adapter.md](14_Provider_Adapter.md) |

## История и сессии
| # | Фича | Статус | Спринт | Заметки |
|---|------|--------|--------|---------|
| H-01 | Сохранение истории чата | ✅ | M0 | |
| H-02 | Экран истории | ✅ | M1 | Выделенный экран |
| H-03 | Удаление отдельных сессий | ✅ | M1 | Из sidebar |
| H-04 | Папки для сессий (как проекты) | 📋 | M2 | Группировка сессий по папкам |

## Аватары
| # | Фича | Статус | Спринт | Заметки |
|---|------|--------|--------|---------|
| A-01 | Статичные 2D аватары | ✅ | M0 | |
| A-02 | Кастомные аватары (загрузка) | ✅ | M1 | |
| A-03 | Persona/инструкции аватара | ✅ | M1 | Edit + reset flow |
| A-04 | Scale-and-crop фон | ✅ | M1 | |
| A-05 | Анимация спрайтшитами | ✅ | M1 | SpriteSheetAnimator + Loader, talking/idle clips |
| A-06 | Базовая анимация аватаров | ✅ | M1 | Idle + talking через SpriteSheetAnimator |
| A-07 | 2D motion-pack MVP contract | ✅ | M1 | Fixed action set, continuous + one-shot split |
| A-08 | Asset-pipeline research для 2D motion packs | 📋 | M2 | see docs/13_Avatar_Motion_Research.md |
| A-09 | Загрузка спрайтшитов — производительность | ⏳ | M2 | Preload during splash screen. Фриз при загрузке — нужна lazy loading |
| A-10 | Довести анимацию спрайтшитов до рабочего состояния | ⏳ | M2 | Talking/listening/confused триггеры. Ожидает проверки |
| A-11 | Система триггерных анимаций | 📋 | M2 | Game-like state machine |

## UI и UX
| # | Фича | Статус | Спринт | Заметки |
|---|------|--------|--------|---------|
| U-01 | Dark UI (UI Toolkit) | ✅ | M0 | |
| U-02 | Темы | ✅ | M1 | Shape/halo/breathing, live preview sync |
| U-03 | Composer overflow fix | ✅ | M1 | min-width: 0 |
| U-04 | NeonDropdown (кастомный компонент) | ✅ | M1 | Замена DropdownField, `choicesCsv` |
| U-05 | Многострочный ввод сообщений | ✅ | M1 | `multiline = true`, auto vertical scroller |
| U-06 | Масштабируемый рельс сайдбара | ✅ | M1 | `_railResizeHandle`, 160–400px |
| U-07 | Режимы отображения аватара | ✅ | M1 | `AvatarViewMode`: Static, Animated, Volume3D |
| U-08 | Кнопка выхода в настройках | ⏳ | M2 | Кнопка «Выход» в settings. Ожидает проверки |
| U-09 | Кнопка «Резюме» (summarize) не работает | 🔧 | M2 | Кнопка в topbar справа |
| U-10 | Иконка приложения в интерфейсе | 📋 | M2 | Вверху слева, рядом с названием |
| U-11 | Баг: сжатие левой панели | ⏳ | M2 | overflow: hidden на .rail. Ожидает проверки |
| U-12 | Баг: многострочный ввод — переполнение | ⏳ | M2 | overflow: auto на inner text field. Ожидает проверки |
| U-13 | Вкладка «Темы» — переосмысление | 📋 | M2 | Текущая реализация бесполезна |
| U-14 | Настройки аватара — перегруженность | 📋 | M2 | Правая panel перегружена |
| U-15 | Сцена загрузки (splash screen) | ⏳ | M2 | Cyberpunk splash + dynamic effects. Ожидает проверки |
| U-16 | Маска API-ключа в редакторе провайдера | 📋 | M2 | Пароль + show/hide |
| U-17 | Дашборд запланированных задач (cron) | 📋 | M3 | Экран: кроны, расписание, статус, логи |
| U-18 | Agent Activity UI | ⏳ | M2 | Thinking bubble + tool progress. Ожидает проверки |
| U-19 | Typing indicator в bubble ответа | ⏳ | M2 | 3 точки внутри response bubble. Ожидает проверки |
| U-20 | Ленивая загрузка спрайтшитов | 📋 | M2 | Splash screen фризит при синхронной загрузке |
| U-21 | Scroll-to-bottom в чате | ⏳ | M2 | Кнопка прокрутки вниз. Ожидает проверки |
| U-22 | Enter-to-send | ⏳ | M2 | Toggle в настройках. Ожидает проверки |
| U-23 | Clear chats only | ⏳ | M2 | Очистка + сброс in-memory ChatService. Ожидает проверки |
| U-24 | Action buttons в bubble | ⏳ | M2 | Copy/refresh/listen при ховере на assistant bubble. Felix фиксит позицию |
| U-25 | Автоскролл при стриминге | ⏳ | M2 | Перелопачен, ожидает проверки |
| U-26 | Toggle панелей | ⏳ | M2 | Кнопки скрытия левой/правой панели. Иконки пофикшены. Ожидает проверки |

## Голос и 3D (M2+)
| # | Фича | Статус | Спринт | Заметки |
|---|------|--------|--------|---------|
| V-01 | Голосовой ввод/вывод | ✅ | M2 | IVoiceService, VoiceInputManager, VoiceOutputManager |
| V-02 | Lipsync | ✅ | M2 | LipsyncController: phoneme→viseme |
| V-03 | 3D аватары | ✅ | M2 | Avatar3DLoader (GLB/GLTF), Avatar3DRenderer |
| V-04 | Desktop realtime avatar layer | 📋 | M2+ | 3D-first realtime path: blendshapes/visemes |

## Рефакторинг (M2)
| # | Фича | Статус | Спринт | Заметки |
|---|------|--------|--------|---------|
| R-01 | NavigationController | ✅ | M2 | 317 строк, deps-based |
| R-02 | ChatController | ✅ | M2 | 1044 строки — чат, стриминг, ввод |
| R-03 | SessionHistoryController | ✅ | M2 | 366 строк — история сессий |
| R-04 | ProvidersController | ✅ | M2 | 1381 строка — провайдеры, модели |
| R-05 | AvatarGalleryController | ✅ | M2 | 1794 строки — галерея аватаров |
| R-06 | VoiceController | ✅ | M2 | 202 строки — голос |
| R-07 | LayoutController | ✅ | M2 | 138 строки — панели, resize |
| R-08 | SettingsController | ✅ | M2 | 1005 строк — настройки |
| R-09 | PanelResizeHandler | ✅ | M2 | Логика resize |

## VR (M3+)
| # | Фича | Статус | Спринт | Заметки |
|---|------|--------|--------|---------|
| VR-01 | Поддержка VR (Quest, PCVR) | 📋 | M3 | |
| VR-02 | Кастомизация аватаров | ✅ | M3 | |
| VR-03 | Плагины и расширения | ✅ | M3 | IPlugin, PluginManager, DLL loading |

## Публикация (M4)
| # | Фича | Статус | Спринт | Заметки |
|---|------|--------|--------|---------|
| P-01 | itch.io / GitHub Releases | ✅ | M4 | Ручная публикация через GitHub Releases |
| P-02 | Документация для контрибьюторов | ✅ | M4 | docs/10_Contribution.md |
| P-03 | Донат-система | ✅ | M4 | IDonationService, кнопка «Поддержать» |
