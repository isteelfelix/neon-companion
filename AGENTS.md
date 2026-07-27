# AGENTS.md

Instructions for AI coding agents working on neon-companion.

## What This Is

Unity 6 (6000.4+) desktop/mobile client for chatting with personal AI agents via OpenAI-compatible APIs. C# / UI Toolkit / JSON storage. The app version is set in the active Unity Build Profile's Player Settings (`bundleVersion`).

**You cannot build or run this project on this server.** There is no Unity installation. Felix builds and tests locally. Your job is to write correct code and **commit on the runner branch only**. Do **not** `git push`, open PRs, or configure remotes/credentials — Neon merges and pushes after review. A missing GitHub auth in the runner is expected.

## C# Compatibility (Critical)

Unity 6 still defaults to C# 9 for broad compatibility. These features will **fail to compile**:

```
❌ switch expressions          → use switch statement
❌ is not null / is not string → use == null / !(x is string)
❌ tuple deconstruction        → use separate variables
❌ target-typed new()          → use new TypeName()
❌ pattern matching with properties → use if/else chains
```

The project uses `System.Threading.Tasks` for async — **not** UniTask. Do not introduce UniTask.

Additional C# constraints:
- `[Serializable]` requires `using System;`; do not use `[UnityEngine.Serializable]`.
- Remove `async` when a method has no `await`; return `Task.CompletedTask` where appropriate.
- Declare loop-index copies outside nested `if`/`for` blocks before capturing them in closures.
- Use `UnityWebRequest`, never `HttpClient`.

## Unity UI Toolkit / USS Constraints

Unity USS supports flex layout, standard sizing and spacing, borders, backgrounds,
text styling, transforms, transitions, positioning, supported pseudo-classes, and
custom properties.

Do not use unsupported web CSS features:
- `z-index`, `gap`, `line-height`, `pointer-events`, `box-shadow`
- `@media`, `@keyframes`, `::before`, `::after`
- `!important`, `calc()`, grid layout, `float`, or `clear`

USS caveats:
- One invalid property can cause Unity to ignore an entire USS rule block.
- Use margins on children instead of `gap`.
- Use `visibility: hidden` when pointer interaction must be suppressed.
- Vertical elements in a flex-row must be siblings in a column parent, not children of the row.
- Prefer creating new UI elements dynamically in C# instead of modifying UXML.
- Initialize runtime-hidden elements with `style.display = DisplayStyle.None`.

## Architecture

```
Assets/Scripts/Runtime/
  Api/              IAiClient, OpenAiCompatibleClient (all HTTP via UnityWebRequest)
  Api/Models/       AiChatRequest/Response, ConnectionTestResult, ModelSwitchResult
  Chat/             ChatService (session lifecycle, provider switching)
  Avatar/           SpriteSheetAnimator, SpriteSheetAnimationLoader, AvatarMotionPack
  Avatar3D/         Avatar3DLoader, Avatar3DRenderer (GLB/GLTF)
  Voice/            VoiceInputManager, VoiceOutputManager, LipsyncController
  Terminal/         IPtySession, PtySessionFactory, Emulator/ (VT parser, ScreenBuffer, TerminalEmulator),
                    Windows/ (ConPtySession, NativePtyWindows), Unix/ (UnixPtySession, NativePtyUnix)
  UI/UITK/Terminal/ TerminalController, TerminalScreenView
  Core/             CompanionApp (app root), ServiceRegistry (poor-man's DI),
                    AppBootstrap (MonoBehaviour singleton), ModelDiscoveryService,
                    ProviderValidator, NeonLogger, PersistentShellService
  Data/Models/      ProviderConfig, ChatModels, AppSettings, AvatarProfile
  Data/Repositories/ JSON-file-backed repos (IProviderConfigRepository, etc.)
  Data/Storage/     JsonFileStorage (Application.persistentDataPath)
  Data/Secrets/     DeviceSecretStore
  UI/UITK/          MainViewController (1676 lines), AvatarGalleryController (1930 lines),
                    ProvidersController, ChatController, SettingsController,
                    SessionHistoryController, VoiceController, LayoutController,
                    NavigationController, SplashViewController, NeonDropdown,
                    ThemeColors (accent palette singleton)
  UI/Chat/          ChatViewModel
  UI/Avatars/       AvatarCustomizationPanel
  UI/Settings/      SettingsViewModel
  Plugins/          IPlugin, PluginManager (DLL-based extension system)
  Localization/     JsonLocalizationService, en.json/ru.json in Resources/Localization
  Donation/         IDonationService
  Platform/         IFilePickerService

**Важно:** Полная архитектура платформенной поддержки описана в `docs/16_Platform_Architecture.md` (Android) и `docs/17_iOS_Platform_Architecture.md` (iOS + общие мобильные принципы).
Все изменения, связанные с мобильной версией, должны следовать правилам из этого документа.
Ключевые изменения:
- PlatformServiceFactory для создания платформенных сервисов
- AppBootstrap использует фабрику вместо прямого new
- IPlatformInfoService для safe area и информации об устройстве

Assets/UI/          UXML templates + USS styles per screen
  Chat/             ChatView.uxml, ChatView.uss
  Providers/        ProvidersView.uxml, ProvidersView.uss
  Avatars/          AvatarsView.uxml, AvatarsView.uss (+ sprite sheets + JSON descriptors)
  Themes/           ThemesView.uxml, ThemesView.uss
  Main/             MainView.uxml, MainView.uss, MainView.Tints.uss, SettingsView.*
  Theme/            Tokens.uss, Components.uss (global design tokens + shared components)

Assets/Resources/
  Avatars/<id>/     built-in motion_pack.json + imported sprite sheet PNGs
  Localization/     en.json, ru.json (loaded through Resources.Load)

Assets/StreamingAssets/
  Avatars/<id>/     optional legacy/custom filesystem-backed motion packs
```

## MainViewController

`Assets/Scripts/Runtime/UI/UITK/MainViewController.cs` — **1676 lines**. Orchestration hub: wires up sub-controllers, handles app lifecycle, sidebar, composer, and model picker. Avatar logic lives in `AvatarGalleryController`, providers in `ProvidersController`, chat in `ChatController`, settings in `SettingsController`, voice in `VoiceController`.

Sub-controllers (all in `UI/UITK/`):
- `AvatarGalleryController` (1930 lines) — avatar gallery, animation, persona, built-in metadata, texture loading
- `ProvidersController` (2193 lines) — provider CRUD, model discovery, connection test
- `ChatController` (1694 lines) — message send/receive, session streaming, tool calls
- `SettingsController` (1305 lines) — app settings UI, theme palette card
- `SessionHistoryController` (1069 lines) — sidebar session list, status dots
- `VoiceController` (734 lines) — recording, STT, TTS playback
- `LayoutController` (609 lines) — form factor detection, responsive layout
- `NavigationController` (317 lines) — screen routing
- `SplashViewController` (543 lines) — splash/onboarding

Rules:
- Do not refactor lightly. Any change here can break multiple screens.
- Before adding new UI features, check if the element already exists in UXML but lacks a binding.
- New screens/panels should get their own controller — the main VC is no longer the dumping ground.
- Callbacks are registered in `RegisterCallbacks()` and unregistered in `UnregisterCallbacks()`. Always add both.
- Avatar-related code belongs in `AvatarGalleryController`, not MainViewController.

## Data Flow

```
ProviderConfig → ProviderConfigRepository → JsonFileStorage → persistentDataPath
OpenAiCompatibleClient → UnityWebRequest → /v1/chat/completions
ChatService → ChatViewModel → MainViewController UI
ModelDiscoveryService → /models endpoint → cached per provider
```

Hermes-specific: `X-Hermes-Session-Id` header, inventory endpoint, model switch protocol. These are in `OpenAiCompatibleClient` — do not break Hermes routing while making generic OpenAI changes.

## Localization

All user-facing strings go through `LocalizationExtensions.Get("key", "fallback")`. Keys live in `Assets/Resources/Localization/{en,ru}.json` and are loaded with `Resources.Load<TextAsset>()` so localization works inside Android/iOS application packages. When adding new UI text:
1. Add key to both en.json and ru.json
2. Use `LocalizationExtensions.Get("your.key", "Fallback text")` in code
3. Never hardcode display strings in C#

## Key Conventions

- **No DI framework.** `ServiceRegistry` is a simple `Dictionary<Type, object>`. Services are registered in `AppBootstrap` and resolved via `GetRequired<T>()`.
- **Models are `[Serializable]` plain classes** — serialized with `JsonUtility.ToJson/FromJson`. No Newtonsoft for runtime models (Newtonsoft is used only where Unity's JsonUtility falls short).
- **Repositories are JSON-file-backed.** Each entity type has its own `I*Repository` interface + `*Repository` implementation using `JsonFileStorage`.
- **UnityWebRequest for all HTTP.** No `HttpClient`. Async pattern: `SendWebRequest()` + `await Task.Yield()` loop or callback.
- **UI Toolkit (UITK).** UXML for templates, USS for styles. No legacy uGUI. `NeonDropdown` is a custom UITK element — use it instead of Unity's `DropdownField`.
- **Built-in spritesheets live in Resources.** Each built-in animated avatar has `Assets/Resources/Avatars/<id>/motion_pack.json` plus imported PNG sprite sheets. `AvatarMotionPackLoader` resolves these through `Resources.Load`, which works inside Android APKs. `Assets/UI/Avatars/` contains static gallery previews and legacy descriptor JSONs. Filesystem-backed packs under `StreamingAssets/Avatars/` remain an optional fallback, not the canonical built-in location.

## Adding New Assets

Every new file in `Assets/` needs a corresponding `.meta` file. Unity generates these automatically in the editor. If you create a `.cs` file or asset from outside Unity:
- Create the `.meta` file with a new GUID (format: `guid: <32 hex chars>`)
- Or let Felix regenerate by opening the project in Unity

For new C# scripts, a minimal `.meta`:
```yaml
fileFormatVersion: 2
guid: <generate-32-hex>
MonoImporter:
  externalObjects: {}
  serializedVersion: 2
  defaultReferences: []
  executionOrder: 0
  icon: {instanceID: 0}
  userData: 
  assetBundleName: 
  assetBundleVariant: 
```

## Provider/Model System

- `ProviderConfig` stores: id, displayName, baseUrl, apiKey, defaultModel, temperature, maxTokens
- Model discovery: `ModelDiscoveryService` hits `/models` endpoint, caches by `baseUrl|apiKey`
- `ConnectionTestResult` includes `DiscoveredModels` — provider test auto-populates the model preset dropdown
- `ModelSwitchResult` handles Hermes-specific model routing (session-based, not just header swap)
- `NeonDropdown` is the standard dropdown component — `choicesCsv` for UXML, `choices` list for code

## Do Not

- Touch `Main.unity` scene file unless absolutely necessary (merge conflicts are painful)
- Add NuGet packages or new DLL dependencies without explicit approval
- Use `switch` expressions or C# 10+ syntax anywhere
- Assume API response formats — always check actual provider behavior (Hermes, OpenAI, Ollama differ)
- Declare features "working" based on code inspection alone — only Felix's build+test confirms it
- Push anywhere (origin/main, runner branch, forks) — Neon owns all remote git writes after review
- Merge two unrelated feature changes in one commit

## Build

No CI/CD. Felix builds locally via Unity Editor (`File → Build Settings`). Version is set in the active Build Profile's Player Settings and exposed at runtime through `Application.version`.

## Documentation

- `docs/` contains architecture, features, API, avatar system, UI flows, data model, roadmap, changelog, feature tracker
- Feature tracker (`12_Feature_Tracker.md`) is the source of truth for what's done
- When adding features, update: tracker, changelog, and relevant architecture/feature docs
- Changelog follows Keep a Changelog format under `[Unreleased]`

## When In doubt

Read the code. The source of truth is `Assets/Scripts/Runtime/`, not the docs. If docs and code disagree, code wins — then fix the docs.

## Android Build (PL-01 / PL-07)

Felix performs all real builds and device testing on Windows.

### Prerequisites (Unity Hub)
- Unity 6.2+ with modules: Android Build Support, OpenJDK, Android SDK & NDK Tools, Android SDK Platform (API 34+).

### Build Profile
- Use `Android` Build Profile (Assets/Settings/Build Profiles/Android.asset).
- Package name: com.isteelfelix.neoncompanion
- IL2CPP, ARM64 primary, min API 26.
- Build .aab (Play) or .apk (sideload).

### Android code already implemented
- Plugins/Android/AndroidManifest.xml (RECORD_AUDIO + storage permissions)
- NeonFilePickerActivity.java and NeonSpeechRecognitionActivity.java
- AndroidPermissionHelper, AndroidSpeechIntentHelper (AndroidSpeechRecognitionBridge legacy, not used; direct OnAndroidSpeechResult on WebSpeechBridge)
- Full voice in WebSpeechBridge (TTS listeners + speech intent)
- UI: .platform-android rules in MainView.uss + ChatView.uss + PlatformLayoutAdapter (safe area)

### Runtime on Android
- persistentDataPath for storage
- Early permissions in AppBootstrap
- Custom Activities + SendMessage bridges for picker/speech
- Keyboard/safe area via IPlatformInfoService + USS

### Device testing
1. Build from Android profile.
2. adb install the apk.
3. Test mic/TTS, file picker, layout on real device.
4. Use adb logcat for debugging.

### Caveats
- No server builds.
- Watch IL2CPP stripping.
- Update the Android.asset profile for changes.

See docs/16_Platform_Architecture.md for full platform rules.

## iOS Build (M4+)

- Build Profile: `Assets/Settings/Build Profiles/iOS.asset` (copied from Android, adjusted BuildTarget 9)
- Native plugins: `Assets/Plugins/iOS/` — NeonSpeech.mm, NeonFilePicker.mm, Info.plist
- Capabilities via Info.plist: NSMicrophoneUsageDescription, NSPhotoLibraryUsageDescription, NSCameraUsageDescription
- Scripting defines in profile: UNITY_IOS
- Voice: WebSpeechBridge routes to native AVSpeechSynthesizer / SFSpeechRecognizer (see NeonSpeech.mm + iOSSpeechBridge)
- File picker: iOSFilePickerService + iOSFilePickerBridge + native UIDocumentPicker/PHPicker stubs
- Safe area / layout: DefaultPlatformInfoService (Screen.safeArea for iOS), PlatformLayoutAdapter + LayoutController add "platform-ios" class
- USS: .platform-ios rules in MainView.uss (and share .platform-mobile)
- Permissions: iOSPermissionHelper (uses Unity Permission API for Microphone; plist for others)
- Architecture: Follow 17_iOS_Platform_Architecture.md and 16_Platform_Architecture.md exactly. No #if in controllers.
- Testing: .verify/check.sh for C#; full test only in Unity Editor + Xcode on macOS (Felix side)
- Current state: Full scaffolding + wiring complete (no real device build here)

See docs/17_iOS_Platform_Architecture.md for IOS-01..08 details and tracker.
