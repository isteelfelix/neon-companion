# 16_Platform_Architecture.md

**Версия:** 1.2  
**Дата:** 2026-06-09  
**Статус:** Частично реализовано (core platform layer + filepicker + voice + permissions + safe area + UI adaptation + adaptive layout rewrite; Build Profile present (base for iOS) + device testing pending)  
**Связанные задачи трекера:** PL-01 — PL-07 (см. актуальный статус в 12_Feature_Tracker.md)

## 1. Цели и ограничения

Цель — добавить полноценную поддержку Android (и в будущем других мобильных платформ) **без разрушения** текущей архитектуры проекта.

### Ключевые ограничения проекта (учитывать обязательно)
- Unity 6 + UI Toolkit (UITK) как единственный UI-фреймворк.
- 3 общие сцены: `Boot.unity`, `Loading.unity`, `Main.unity`.
- ServiceRegistry (простой in-memory DI).
- Идёт активная декомпозиция `MainViewController` (см. `15_MainViewController_Refactor.md`).
- C# 9 ограничения (никаких switch expressions, `is not`, target-typed new).
- Уже существуют Build Profiles (Windows + Android™).
- Уже есть частичная платформенная абстракция (`IFilePickerService`, `#if UNITY_ANDROID` в Voice и Secrets).
- "Наше приложение" — релизный процесс ведёт Neon, но реальные сборки и визуальный тест делает Felix на Windows.

**Главный принцип:**  
Платформенные различия должны быть инкапсулированы как можно ближе к границе платформы. Контроллеры и UI-шаблоны должны оставаться максимально общими.

## 2. Основные архитектурные решения

### 2.1 Сцены
**Решение:** Использовать одни и те же сцены для всех платформ.

**Обоснование:**
- Текущий flow (Boot → Loading → Main) одинаков для desktop и mobile.
- Создание отдельных Android-сцен приведёт к дублированию и расхождению фич.
- Различия решаются на уровне сервисов, контроллеров и стилей, а не сцен.

**Исключения** (крайне редко):
- Только если появится совершенно другой onboarding flow на мобильных (маловероятно в ближайшие 2 года).

### 2.2 Build Profiles (Unity 6) — источник истины
Unity 6 Build Profiles — основной инструмент управления платформенными различиями.

- Каждый профиль определяет:
  - Целевую платформу
  - Player Settings (Package Name, API levels, architectures, keystore и т.д.)
  - Scripting Define Symbols (рекомендуется)
  - Сцены (ссылаются на те же 3 сцены)
  - Качество, stripping, IL2CPP настройки

**Рекомендуемые профили:**
- `Windows`
- `Android` (переименовать из `Android™.asset`)
- В будущем: `Android-Development`, `Android-Release`

### 2.3 Платформенный слой (Platform Abstraction Layer)
**Главное место**, где должна жить платформенная логика.

Расположение: `Assets/Scripts/Runtime/Platform/`

**Структура (целевая):**
```
Platform/
├── IFilePickerService.cs
├── DefaultFilePickerService.cs          (Editor + Windows реализация)
├── AndroidFilePickerService.cs          (или оставить в одном файле с #if)
├── IPlatformInfoService.cs              (экран, safe area, orientation и т.д.)
├── IVoiceService.cs                     (или более гранулярно)
├── PlatformServiceFactory.cs            (рекомендуется)
└── Android/
    └── Plugins/                         (нативный Java/Kotlin код)
        └── src/
            └── com/neoncompanion/...
```

> **Примечание (v1.2):** Адаптивная раскладка (form factor detection, drawer/overlay
> на телефоне, safe-area padding) **полностью** инкапсулирована в `LayoutController`
> (`Assets/Scripts/Runtime/UI/UITK/LayoutController.cs`). Класс `PlatformLayoutAdapter`
> удалён — его логика поглощена. Для работы UI с платформенными данными используйте
> `IPlatformInfoService` через `LayoutController.ApplyPlatformLayout(info)`.

### 2.4 Регистрация сервисов
**Текущая проблема:** В `AppBootstrap.cs` сервисы создаются жёстко (`new DefaultFilePickerService()`).

**Целевое решение:**
- Создать `PlatformServiceFactory` (или метод в `AppBootstrap`).
- Выбор реализации происходит **один раз** при старте на основе `Application.platform` + define symbols из Build Profile.
- Контроллеры получают сервисы только через `ServiceRegistry.GetRequired<T>()`.

**Localization:** JSON-файлы лежат в `Assets/Resources/Localization/` (en.json, ru.json). Загружаются через `Resources.Load<TextAsset>()` — работает на всех платформах, включая Android APK (где StreamingAssets недоступны через `File.*`). Fallback: если ключ не найден в текущем языке, берётся `en`.

### 2.5 Windows-isolated Companion display

`ICompanionWindowService` создаётся только фабрикой. Реальная
`WindowsCompanionWindowService` компилируется для
`UNITY_STANDALONE_WIN && !UNITY_EDITOR`; все остальные платформы получают stub.
Сервис запускает текущий Windows Player как отдельный display-only процесс и
контролирует его через local named pipe. Контроллеры не используют Win32 напрямую.

Win32 transparency, topmost, click-through, monitor placement и system drag живут
в дочернем `CompanionPlayerRuntime`. Main process отвечает за supervision,
persisted controls и display snapshot. Child-mode guard в `AppBootstrap` должен
оставаться до любого чтения repository/secret/session state. Это security boundary:
в протокол нельзя добавлять `ProviderConfig`, API keys, system prompt, chat history
или session identity. Mobile builds никогда не запускают display process.

Пример (целевая форма):

```csharp
// В AppBootstrap.Awake()
IFilePickerService filePicker;

#if UNITY_ANDROID && !UNITY_EDITOR
filePicker = new AndroidFilePickerService();
#elif UNITY_STANDALONE_WIN || UNITY_EDITOR
filePicker = new DefaultFilePickerService();
#else
filePicker = new StubFilePickerService();
#endif

services.Register<IFilePickerService>(filePicker);
```

Или (предпочтительнее при росте):

```csharp
var factory = new PlatformServiceFactory();
services.Register<IFilePickerService>(factory.CreateFilePicker());
```

### 2.5 Адаптивная раскладка (LayoutController)
Единый контроллер, определяющий form factor и управляющий раскладкой.

**Форм-факторы** (по физической ширине в `ConstantPhysicalSize` поинтах):
- **Phone** (<520dp) — рейл превращается в off-canvas drawer со скримом, аватар-панель — fullscreen overlay
- **Tablet** (520–900dp) — многопанельная раскладка, аватар может авто-скрываться
- **Desktop** (>900dp) — полная раскладка

**Классы на `.app`:**
- `ff-phone` / `ff-tablet` / `ff-desktop` — текущий форм-фактор
- `app--compact` (<1100dp) / `app--narrow` (<900dp) — под-брейкпоинты (только tablet/desktop)
- `platform-android` / `platform-ios` — ОС (через `IPlatformInfoService`)

**Safe Area:** применяется автоматически через `ApplySafeAreaPadding()` на каждом `GeometryChangedEvent`.

**На мобильной платформе** (`UNITY_ANDROID || UNITY_IOS`) form factor определяется через `Screen.dpi`:
- DPI 200–700 + minSideDp ≥600 → Tablet
- Иначе → Phone

На десктопе — по ширине окна.

## 3. Как обрабатывать различия в UI (UITK)

**Приоритетный порядок (от простого к сложному):**

1. **USS + классы состояний** (самый предпочтительный)
   ```csharp
   root.EnableInClassList("mobile", isMobile);
   root.EnableInClassList("desktop", !isMobile);
   ```
   В USS:
   ```uss
   .mobile .some-button { font-size: 18px; }
   ```

2. **Conditional loading style sheets** в контроллере.

3. **Runtime создание/скрытие элементов** в `Refresh*()` методах контроллеров (для мелких различий).

4. **Отдельные UXML-варианты** — только для кардинально разных экранов (пока не требуется).

**Запрещено:**
- Создавать отдельные UXML-файлы "MobileMainView.uxml" без очень веской причины.
- Пихать платформенную логику внутрь `MainViewController` и подконтроллеров.

## 4. Правила написания кода

### Где допустимы `#if UNITY_ANDROID`
- В платформенных сервисах (`Runtime/Platform/`).
- В очень тонких адаптерах (Voice bridges, Secret stores).
- В `AppBootstrap` при регистрации сервисов.

### Где `#if` запрещён
- В `MainViewController` и подконтроллерах (кроме минимальных флагов).
- В UXML/USS (использовать классы).
- В бизнес-логике (ChatService, ProviderManager и т.д.).

### Дополнительные правила
- Все платформенные сервисы должны иметь **интерфейс**.
- Для Android-нативного кода — использовать `AndroidJavaClass` / `AndroidJavaObject` только внутри реализации сервиса.
- `Application.persistentDataPath` — единственный правильный путь для сохранения данных на всех платформах.
- При добавлении новой платформенной фичи — сначала добавить интерфейс + factory, потом реализацию.

## 5. План реализации (привязка к трекеру)

| Задача трекера | Что делать в рамках архитектуры                          | Приоритет |
|----------------|-----------------------------------------------------------|---------|
| PL-01          | Настроить Android Build Profile, переименовать файл      | Высокий |
| PL-02          | Доработать/вынести Android-реализацию FilePicker         | Высокий |
| PL-03          | Создать AndroidManifest.xml + permissions                | Высокий |
| PL-04          | Внедрить `PlatformServiceFactory` + рефакторинг bootstrap | Высокий |
| PL-05          | Унифицировать Voice под платформенный сервис             | Средний |
| PL-06          | Добавить `IPlatformInfoService` (safe area, density и т.д.) | Средний |
| PL-07          | Обновить AGENTS.md + этот документ примерами             | Средний |

## 6. Примеры для агентской работы

### Пример 1: Добавление нового платформенного сервиса

1. Создать интерфейс `IPlatformInfoService.cs`.
2. Реализовать `DefaultPlatformInfoService` (desktop-заглушка) и `AndroidPlatformInfoService`.
3. Зарегистрировать в `AppBootstrap` через платформенный выбор.
4. Использовать в контроллерах только через `GetRequired<IPlatformInfoService>()`.

### Пример 2: Safe Area на Android

```csharp
// В PlatformInfoService
public Rect GetSafeArea() 
{
#if UNITY_ANDROID && !UNITY_EDITOR
    return Screen.safeArea;
#else
    return new Rect(0, 0, Screen.width, Screen.height);
#endif
}
```

Затем в UITK-контроллере:
```csharp
var safeArea = platformInfo.GetSafeArea();
root.style.paddingLeft = safeArea.xMin;
...
```

## 7. Анти-паттерны (что не делать)

- Создавать отдельные сцены под Android.
- Разбрасывать `#if UNITY_ANDROID` по 10+ файлам.
- Писать Android-специфичный код прямо в `ChatController` / `MainViewController`.
- Игнорировать уже существующие Build Profiles.
- Делать тяжёлый рефакторинг большого контроллера "потому что теперь мобильный".

### Voice feedback

- После остановки записи composer показывает локальный audio preview сразу, не ожидая STT.
- Пока STT/TTS выполняется, UI обязан показывать явное промежуточное состояние.
- Android haptic вызывается через платформенный helper только после фактического старта/остановки записи.

## 8. Следующие шаги после утверждения

1. Утвердить этот документ Felix'ом.
2. Создать `PlatformServiceFactory`.
3. Зафиксировать правила в `AGENTS.md` и `CLAUDE.md`.
4. Начать реализацию по приоритетам из таблицы выше (можно отдавать агентам отдельными brief'ами).

---

**Этот документ предназначен для передачи агентам (Claude Code, Codex, Grok).**  
После утверждения — все задачи PL-0x должны ссылаться на него.
