# 17_iOS_Platform_Architecture.md

**Версия:** 1.0  
**Дата:** 2026-05-31  
**Статус:** Частично реализовано (IOS-01..IOS-08 advanced: native bridges wired, WebSpeechBridge iOS complete, file picker full service+bridge, safe area unified, .platform-ios USS, Build Profile, permissions helper, docs/tracker updated). Ready for Unity iOS build.  
**Связанные задачи трекера:** IOS-01 — IOS-08 (см. 12_Feature_Tracker.md)  
**Связанные документы:** docs/16_Platform_Architecture.md (Android + общие принципы мобильных платформ)

## 1. Цели и ограничения

Цель — добавить полноценную поддержку iOS (iPhone/iPad) **без разрушения** текущей архитектуры и без дублирования Android-логики. iOS рассматривается как вторая мобильная платформа после Android.

### Ключевые ограничения проекта (учитывать обязательно)
- Unity 6 + UI Toolkit (UITK) как единственный UI-фреймворк.
- 3 общие сцены: `Boot.unity`, `Loading.unity`, `Main.unity`.
- ServiceRegistry (простой in-memory DI).
- C# 9 ограничения.
- Уже существует частичная платформенная абстракция (IPlatformInfoService с IsMobile, PlatformLayoutAdapter с .platform-ios классом).
- iOS требует нативного кода (Objective-C / Swift) в `Assets/Plugins/iOS/`.
- Подпись, capabilities (Microphone, Photos), Info.plist — через Build Profile + Player Settings.
- "Наше приложение" — релизный процесс ведёт Neon, но реальные сборки и визуальный тест делает Felix на Windows + macOS (Xcode).
- **Главный принцип:** Платформенные различия инкапсулируются максимально близко к границе платформы. Контроллеры и UI-шаблоны остаются общими.

## 2. Основные архитектурные решения

### 2.1 Сцены и общий flow
**Решение:** Использовать одни и те же сцены для Windows, Android и iOS.

**Обоснование:** Текущий flow (Boot → Loading → Main) одинаков. Различия только в сервисах и стилях.

### 2.2 Build Profiles (Unity 6) — источник истины
- Профили: `Windows`, `Android`, `iOS` (и варианты Development/Release).
- В iOS профиле:
  - Target SDK: iOS
  - Bundle Identifier, Signing, Capabilities (Microphone, Photo Library)
  - Scripting Define Symbols: `UNITY_IOS`
  - Player Settings → Other Settings → Configuration → `useCustomMainManifest` не нужен (iOS использует Info.plist)
  - Post-processing: можно добавить Xcode project modifier (если потребуется)

### 2.3 Платформенный слой (расширение существующего)
Расположение: `Assets/Scripts/Runtime/Platform/`

**Целевая структура (расширение Android-варианта):**
```
Platform/
├── IFilePickerService.cs
├── DefaultFilePickerService.cs          (Editor + Windows + общая мобильная заглушка)
├── AndroidFilePickerService.cs          (или #if внутри Default)
├── iOSFilePickerService.cs              (рекомендуется отдельный класс)
├── IPlatformInfoService.cs
├── DefaultPlatformInfoService.cs        (уже частично поддерживает iOS)
├── IVoiceService.cs
├── PlatformServiceFactory.cs            (центральная точка выбора)
├── Android/
│   └── PermissionHelper, SpeechIntentHelper, Bridges...
└── iOS/
    └── Plugins/                         (нативные .mm / .h файлы)
        └── NeonFilePicker.mm
        └── NeonSpeech.mm
        └── ...
```

**Регистрация в AppBootstrap / PlatformServiceFactory:**
```csharp
#if UNITY_IOS && !UNITY_EDITOR
    filePicker = new iOSFilePickerService();
    voice = new iOSVoiceService(host);
#elif UNITY_ANDROID && !UNITY_EDITOR
    ...
#else
    filePicker = new DefaultFilePickerService();
#endif
```

### 2.4 Нативный слой для iOS
- Только в `Assets/Plugins/iOS/`
- Файлы: `.mm` (Objective-C++), `.h`, иногда Swift с bridging header.
- Связь: 
  - `UnitySendMessage("GameObjectName", "MethodName", "string")` (как в Android)
  - Или `[DllImport("__Internal")]` extern методы.
- Примеры:
  - File picker: `UIDocumentPickerViewController` + `PHPicker` (iOS 14+)
  - Voice: `AVSpeechSynthesizer` (TTS) + `SFSpeechRecognizer` (recognition)
  - Permissions: запрос через `AVAudioSession` / Photos framework + Info.plist ключи.

**Важно:** Не использовать AndroidJavaObject на iOS. Весь нативный код изолирован в iOS/ папке.

### 2.5 UI и адаптация (UITK + USS)
**Приоритет:**
1. USS классы (уже частично есть):
   ```uss
   .app.platform-ios .composer { ... }
   .app.platform-mobile .nav__item { min-height: 44px; }
   ```
2. `PlatformLayoutAdapter` / `LayoutController` (нужно унифицировать — текущий дубликат кода).
3. Safe Area: `Screen.safeArea` работает на iOS из коробки (уже используется в DefaultPlatformInfoService).
4. Keyboard inset: на iOS более агрессивный (нужен расширенный `iOSKeyboardInset` или общий `MobileKeyboardInset`).

**Запрещено:**
- Отдельные UXML для iOS.
- Платформенная логика в MainViewController.

## 3. Как обрабатывать различия

### Где допустимы `#if UNITY_IOS`
- В платформенных сервисах (`Runtime/Platform/`)
- В тонких адаптерах (Voice, Secrets, FilePicker)
- В `AppBootstrap` и `PlatformServiceFactory`
- В `Voice/WebSpeechBridge.cs` (расширение Android-блока)

### Где `#if` запрещён
- В контроллерах и ViewModel
- В UXML/USS (использовать классы `.platform-ios`)
- В бизнес-логике (ChatService и т.д.)

## 4. Правила написания кода для iOS

- Все нативные плагины должны иметь C# wrapper с интерфейсом.
- Для file picker и voice — следовать проверенному паттерну Android (Bridge MonoBehaviour + UnitySendMessage).
- Info.plist ключи (NSMicrophoneUsageDescription, NSPhotoLibraryUsageDescription) задавать в Player Settings iOS профиля или через custom plist.
- PersistentDataPath работает одинаково.
- 3D аватары / GLTF: должны работать (Metal поддержка в Unity 6).
- IL2CPP обязателен для iOS.

## 5. План реализации (привязка к трекеру)

| Задача трекера | Что делать в рамках архитектуры                          | Приоритет |
|----------------|-----------------------------------------------------------|---------|
| IOS-01         | Создать iOS Build Profile + базовые Player Settings      | Высокий |
| IOS-02         | Реализовать iOSFilePickerService + нативный .mm плагин   | Высокий |
| IOS-03         | Добавить iOS permissions (Info.plist + runtime запросы)  | Высокий |
| IOS-04         | Расширить PlatformServiceFactory под iOS                 | Высокий |
| IOS-05         | Унифицировать Voice под iOS (AVSpeech + SFSpeech)        | Средний |
| IOS-06         | Улучшить keyboard inset + safe area для iPad             | Средний |
| IOS-07         | Добавить .platform-ios правила в USS (расширение мобильных) | Средний |
| IOS-08         | Обновить AGENTS.md, 16_ и 17_ документы + примеры        | Средний |

## 6. Примеры для агентской работы

### Пример: iOS File Picker
1. Создать `iOSFilePickerService : IFilePickerService`
2. Нативный `NeonFilePicker.mm` с `UIDocumentPickerViewController`
3. Bridge MonoBehaviour `iOSFilePickerBridge`
4. Регистрация в фабрике под `#if UNITY_IOS`

### Пример: Safe Area + Keyboard (уже частично работает)
```csharp
// В PlatformInfoService
public Rect SafeArea => Screen.safeArea; // работает на iOS и Android

// В iOSKeyboardInset (расширение AndroidKeyboardInset)
public static void PollKeyboardState() { /* iOS specific via Unity callbacks или native */ }
```

## 7. Анти-паттерны (что не делать)

- Создавать отдельные сцены под iOS.
- Разбрасывать `#if UNITY_IOS` по 10+ файлам вне Platform/.
- Писать iOS-код прямо в MainViewController.
- Игнорировать существующий `platform-ios` класс в USS и адаптерах.
- Делать тяжёлый рефакторинг "потому что теперь и iOS".
- Предполагать, что Android-решения (AndroidJavaClass) перенесутся 1-в-1.

## 8. Следующие шаги после утверждения

1. Утвердить этот документ Felix'ом.
2. Создать iOS Build Profile.
3. Начать с IOS-01 и IOS-04 (фабрика + профиль) — самый высокий leverage.
4. Зафиксировать правила в AGENTS.md и CLAUDE.md.
5. Делегировать конкретные задачи агентам (Claude Code / Codex) с ссылкой на этот + 16_ документ.

---

**Этот документ предназначен для передачи агентам (Claude Code, Codex, Grok).**  
После утверждения — все задачи IOS-0x должны ссылаться на него + 16_Platform_Architecture.md.

**Принцип единства мобильных платформ:** Android и iOS делят 80% мобильной логики через `IsMobile` + `.platform-mobile`. Специфические различия (Java vs Objective-C, permissions модели) изолированы в Platform/iOS/ и Platform/Android/.
