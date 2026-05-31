# AGENTS.md

Instructions for AI coding agents working on neon-companion.

## What This Is

Unity 6 (6000.4+) desktop/mobile client for chatting with personal AI agents via OpenAI-compatible APIs. C# / UI Toolkit / JSON storage. Current version: see `VERSION` file.

**You cannot build or run this project on this server.** There is no Unity installation. Felix builds and tests locally. Your job is to write correct code and push — Felix confirms it works.

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

## Architecture

```
Assets/Scripts/Runtime/
  Api/              IAiClient, OpenAiCompatibleClient (all HTTP via UnityWebRequest)
  Api/Models/       AiChatRequest/Response, ConnectionTestResult, ModelSwitchResult
  Chat/             ChatService (session lifecycle, provider switching)
  Avatar/           SpriteSheetAnimator, SpriteSheetAnimationLoader, AvatarMotionPack
  Avatar3D/         Avatar3DLoader, Avatar3DRenderer (GLB/GLTF)
  Voice/            VoiceInputManager, VoiceOutputManager, LipsyncController
  Core/             CompanionApp (app root), ServiceRegistry (poor-man's DI),
                    AppBootstrap (MonoBehaviour singleton), ModelDiscoveryService,
                    ProviderValidator, NeonLogger
  Data/Models/      ProviderConfig, ChatModels, AppSettings, AvatarProfile
  Data/Repositories/ JSON-file-backed repos (IProviderConfigRepository, etc.)
  Data/Storage/     JsonFileStorage (Application.persistentDataPath)
  Data/Secrets/     DeviceSecretStore
  UI/UITK/          MainViewController (5700+ lines, the god object), NeonDropdown
  UI/Chat/          ChatViewModel
  UI/Avatars/       AvatarCustomizationPanel
  UI/Settings/      SettingsViewModel
  Plugins/          IPlugin, PluginManager (DLL-based extension system)
  Localization/     JsonLocalizationService, en.json/ru.json in StreamingAssets
  Donation/         IDonationService
  Platform/         IFilePickerService

**Важно:** Полная архитектура платформенной поддержки (Android + desktop) описана в `docs/16_Platform_Architecture.md`.
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

Assets/StreamingAssets/
  Avatars/neon/     motion_pack.json + sprite sheet PNGs (runtime-loaded)
  Localization/     en.json, ru.json
```

## MainViewController

`Assets/Scripts/Runtime/UI/UITK/MainViewController.cs` — **5700+ lines**. This is the single controller for almost all UI: chat, providers, avatars, settings, themes, model picker, sidebar, composer.

Rules:
- Do not refactor lightly. Any change here can break multiple screens.
- Before adding new UI features, check if the element already exists in UXML but lacks a binding.
- New screens/panels should ideally get their own controller, but check existing patterns first.
- Callbacks are registered in `RegisterCallbacks()` and unregistered in `UnregisterCallbacks()`. Always add both.

## Data Flow

```
ProviderConfig → ProviderConfigRepository → JsonFileStorage → persistentDataPath
OpenAiCompatibleClient → UnityWebRequest → /v1/chat/completions
ChatService → ChatViewModel → MainViewController UI
ModelDiscoveryService → /models endpoint → cached per provider
```

Hermes-specific: `X-Hermes-Session-Id` header, inventory endpoint, model switch protocol. These are in `OpenAiCompatibleClient` — do not break Hermes routing while making generic OpenAI changes.

## Localization

All user-facing strings go through `LocalizationExtensions.Get("key", "fallback")`. Keys live in `Assets/StreamingAssets/Localization/{en,ru}.json`. When adding new UI text:
1. Add key to both en.json and ru.json
2. Use `LocalizationExtensions.Get("your.key", "Fallback text")` in code
3. Never hardcode display strings in C#

## Key Conventions

- **No DI framework.** `ServiceRegistry` is a simple `Dictionary<Type, object>`. Services are registered in `AppBootstrap` and resolved via `GetRequired<T>()`.
- **Models are `[Serializable]` plain classes** — serialized with `JsonUtility.ToJson/FromJson`. No Newtonsoft for runtime models (Newtonsoft is used only where Unity's JsonUtility falls short).
- **Repositories are JSON-file-backed.** Each entity type has its own `I*Repository` interface + `*Repository` implementation using `JsonFileStorage`.
- **UnityWebRequest for all HTTP.** No `HttpClient`. Async pattern: `SendWebRequest()` + `await Task.Yield()` loop or callback.
- **UI Toolkit (UITK).** UXML for templates, USS for styles. No legacy uGUI. `NeonDropdown` is a custom UITK element — use it instead of Unity's `DropdownField`.
- **Spritesheets live in StreamingAssets.** `Assets/UI/Avatars/` has legacy descriptor JSONs. `Assets/StreamingAssets/Avatars/neon/` has the runtime-loaded motion pack.

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
- Push directly to `main` without committing clean, reviewed diffs
- Merge two unrelated feature changes in one commit

## Build

No CI/CD. Felix builds locally via Unity Editor (`File → Build Settings`). Version is in the `VERSION` file at project root.

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
- AndroidPermissionHelper, AndroidSpeechIntentHelper, AndroidSpeechRecognitionBridge
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
