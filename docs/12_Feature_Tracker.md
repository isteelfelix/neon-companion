# 12_Feature_Tracker.md

## Статусы
- ✅ Done — реализовано и работает
- 🔧 In Progress — в разработке
- 📋 Planned — запланировано
- ❌ Blocked — заблокировано (зависимости)

---

## Чат и API
| # | Фича | Статус | Спринт | Заметки |
|---|------|--------|--------|---------|
| C-01 | Подключение OpenAI-совместимых API | ✅ | M0 | Streaming через DownloadHandlerBuffer + poll |
| C-02 | Множество провайдеров + переключение | ✅ | M0 | |
| C-03 | Провайдер-осознанные сессии | ✅ | M1 | |
| C-04 | Пресеты моделей | ✅ | M1 | |
| C-05 | Локализация UI | ✅ | M1 | |
| C-06 | Авто-обнаружение моделей (ModelDiscoveryService) | ✅ | M1 | Кэширование по baseUrl|apiKey, `/models` эндпоинт, авто-обнаружение при изменении |
| C-07 | Модель-пикер в чате | ✅ | M1 | NeonDropdown в topbar + overlay-диалог, `ApplySessionModelAsync` |
| C-08 | Вложения в чате | ✅ | M1 | `ChatAttachment`, `AiChatAttachment` |
| C-09 | Сессионная маршрутизация моделей | ✅ | M1 | `X-Hermes-Session-Id`, `ProviderSessionId`, Hermes inventory |
| C-10 | Provider Adapter архитектура | 📋 | M2 | `IProviderAdapter` + `HermesAdapter` + `GenericOpenAiAdapter`, `ProviderConfig.backendType`. Подробности — [14_Provider_Adapter.md](14_Provider_Adapter.md) |

## История и сессии
| # | Фича | Статус | Спринт | Заметки |
|---|------|--------|--------|---------|
| H-01 | Сохранение истории чата | ✅ | M0 | |
| H-02 | Экран истории | ✅ | M1 | Выделенный экран |
| H-03 | Удаление отдельных сессий | ✅ | M1 | Из sidebar |
| H-04 | Папки для сессий (как проекты) | 📋 | M2 | Группировка сессий по папкам, возможность задавать системный промпт на папку |

## Аватары
| # | Фича | Статус | Спринт | Заметки |
|---|------|--------|--------|---------|
| A-01 | Статичные 2D аватары | ✅ | M0 | |
| A-02 | Кастомные аватары (загрузка) | ✅ | M1 | |
| A-03 | Persona/инструкции аватара | ✅ | M1 | Edit + reset flow |
| A-04 | Scale-and-crop фон | ✅ | M1 | |
| A-05 | Анимация спрайтшитами | ✅ | M1 | SpriteSheetAnimator + Loader, talking/idle clips, backward compatible |
| A-06 | Базовая анимация аватаров | ✅ | M1 | Idle + talking clips через SpriteSheetAnimator, auto-switch при отправке |
|| A-07 | 2D motion-pack MVP contract | ✅ | M1 | Fixed action set: `idle`, `thinking`, `talking`, `listening`, `smile`, `confused`; continuous + one-shot split; `motion_pack.json` format |
| A-08 | Asset-pipeline research для 2D motion packs | 📋 | M2 | Исследование внешней генерации/подготовки motion clips без runtime-зависимости клиента; see docs/13_Avatar_Motion_Research.md |
| A-09 | Загрузка спрайтшитов — производительность | ✅ | M2 | Preload during splash screen via PreloadManifestCoroutine, live progress in boot log |
| A-10 | Довести анимацию спрайтшитов до рабочего состояния | ✅ | M2 | Talking: _isStreamingResponse flag при первом токене стрима. Listening: триггер при вводе текста в композер. Confused: триггер на ошибки провайдера/модели |
| A-11 | Система триггерных анимаций | 📋 | M2 | Персонаж всегда в idle. Триггер запускает одну анимацию, она проигрывается (все не-loop анимации — ping-pong), затем возврат в idle. Game-like state machine |

## UI и UX
| # | Фича | Статус | Спринт | Заметки |
|---|------|--------|--------|---------|
| U-01 | Dark UI (UI Toolkit) | ✅ | M0 | |
| U-02 | Темы | ✅ | M1 | Shape/halo/breathing, live preview sync |
| U-03 | Composer overflow fix | ✅ | M1 | min-width: 0 |
| U-08 | Кнопка выхода в настройках | ✅ | M2 | Кнопка «Выход» в settings-actions-row, вызывает ShowQuitDialog() |
| U-09 | Кнопка «Резюме» (summarize) не работает | 🔧 | M2 | Кнопка в topbar справа, нужна диагностика |
| U-10 | Иконка приложения в интерфейсе | 📋 | M2 | Вверху слева, рядом с названием |
| U-11 | Баг: сжатие левой панели | ✅ | M2 | overflow: hidden на .rail и .nav__label предотвращает вылезание за границу при ресайзе |
| U-12 | Баг: многострочный ввод — переполнение | ✅ | M2 | overflow: auto на inner text field, overflow: hidden на outer container |
| U-13 | Вкладка «Темы» — переосмысление | 📋 | M2 | Текущая реализация бесполезна, подумать над функционалом |
| U-14 | Настройки аватара — перегруженность | 📋 | M2 | Правая пanel настроек аватара перегружена, упростить |
| U-16 | Маска API-ключа в редакторе провайдера | 📋 | M2 | Поле API key показывать как пароль (звездочки), с кнопкой show/hide |
| U-15 | Сцена загрузки (splash screen) | 📋 | M2 | Подумать над экраном загрузки при старте приложения |
| U-17 | Дашборд запланированных задач (cron) | 📋 | M3 | Экран в приложении: список кронов, расписание, статус последнего запуска, логи/ошибки. Видимость того что делает агент в фоне |
| U-04 | NeonDropdown (кастомный компонент) | ✅ | M1 | Замена DropdownField, `choicesCsv`, popup overlay, programmatic API |
| U-05 | Многострочный ввод сообщений | ✅ | M1 | `multiline = true`, auto vertical scroller |
| U-06 | Масштабируемый рельс сайдбара | ✅ | M1 | `_railResizeHandle`, 160–400px |
| U-07 | Режимы отображения аватара | ✅ | M1 | `AvatarViewMode`: Static, Animated, Volume3D; toggle buttons |

## Голос и 3D (M2+)
| # | Фича | Статус | Спринт | Заметки |
|---|------|--------|--------|---------|
| V-01 | Голосовой ввод/вывод | ✅ | M2 | IVoiceService, VoiceInputManager, VoiceOutputManager, WebSpeechBridge, mic button, settings toggle |
| V-02 | Lipsync | ✅ | M2 | LipsyncController: phoneme→viseme, 2D sprite frames, 3D blend shapes, hooked into VoiceOutputManager/VoiceInputManager |
| V-03 | 3D аватары | ✅ | M2 | IAvatar3DService, Avatar3DLoader (GLB/GLTF), Avatar3DRenderer (orbit, pinch-zoom), Avatar3DService, AvatarProfile.is3D |
| V-04 | Desktop realtime avatar layer | 📋 | M2+ | 3D-first realtime path: blendshapes/visemes/live lipsync; separate from 2D mobile baseline |

## VR (M3+)
| # | Фича | Статус | Спринт | Заметки |
|---|------|--------|--------|---------|
| VR-01 | Поддержка VR (Quest, PCVR) | 📋 | M3 | |
| VR-02 | Кастомизация аватаров | ✅ | M3 | |
| VR-03 | Плагины и расширения | ✅ | M3 | IPlugin, PluginManager, PluginContext, PluginConfigStorage, DLL loading, settings UI |

## Публикация (M4)
| # | Фича | Статус | Спринт | Заметки |
|---|------|--------|--------|---------|
| P-01 | itch.io / GitHub Releases | ✅ | M4 | Ручная публикация через GitHub Releases; VERSION файл |
| P-02 | Документация для контрибьюторов | ✅ | M4 | docs/10_Contribution.md |
| P-03 | Донат-система | ✅ | M4 | IDonationService, DonationService, Settings: кнопка «Поддержать» |
