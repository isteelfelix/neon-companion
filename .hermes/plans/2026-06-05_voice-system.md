# Voice System Implementation Plan

> **For Hermes:** Use subagent-driven-development skill to implement this plan task-by-task.

**Goal:** Replace the native WebSpeechBridge with backend-proxied voice services — one for OpenAI-compatible backends, one for Hermes backend — plus proper voice settings at both app and provider level.

**Architecture:** Two `IVoiceService` implementations call external APIs for STT/TTS instead of using OS-native speech. Voice settings live in two places: universal (device/volume) in AppSettings, provider-specific (TTS/STT provider, voice, model) in ProviderConfig. VoiceController selects the correct service based on `backendType`.

**Tech Stack:** Unity 6.4, C# 9, UITK, UnityWebRequest, Unity Microphone API, Newtonsoft (only if needed for audio API responses).

---

## Current State

- `WebSpeechBridge` — single IVoiceService, uses OS-native APIs (Windows DictationRecognizer, Android TTS, iOS AVFoundation, WebGL JS). No TTS on Windows. No provider selection.
- `VoiceController` — creates WebSpeechBridge unconditionally.
- `VoiceInputManager` / `VoiceOutputManager` — work with any IVoiceService, no changes needed.
- `ProviderConfig` — has `backendType` ("hermes" | null), but no voice fields.
- `AppSettings` — has `voiceIOEnabled` but no device/volume settings.

---

## Phase 1: Data Models

### Task 1.1: Extend ProviderConfig with voice fields

**Objective:** Add TTS/STT configuration to provider settings.

**Files:**
- Modify: `Assets/Scripts/Runtime/Data/Models/ProviderConfig.cs`

**Add fields:**
```csharp
// Voice settings (OpenAI backend)
public string sttProvider;    // "openai", "groq", "local" — null = auto
public string ttsProvider;    // "edge", "openai", "elevenlabs", "minimax", "mistral" — null = auto
public string ttsVoice;       // voice ID/name for TTS
public string ttsModel;       // TTS model (e.g. "tts-1", "tts-1-hd")
public float ttsSpeed = 1.0f; // 0.25-4.0
public string sttLanguage;    // Whisper language (e.g. "ru", "en")
```

**Commit:** `feat: add voice config fields to ProviderConfig`

---

### Task 1.2: Extend AppSettings with universal voice settings

**Objective:** Add device selection and volume to app settings.

**Files:**
- Modify: `Assets/Scripts/Runtime/Data/Models/AppSettings.cs`

**Add fields:**
```csharp
// Voice (universal)
public string inputDeviceName = "";   // microphone device name (empty = system default)
public string outputDeviceName = "";  // speaker device name (empty = system default)
public float outputVolume = 0.8f;     // 0.0-1.0
```

**Commit:** `feat: add device and volume settings to AppSettings`

---

## Phase 2: Voice Services

### Task 2.1: OpenAiVoiceService — STT

**Objective:** Implement STT via OpenAI-compatible `/v1/audio/transcriptions` endpoint.

**Files:**
- Create: `Assets/Scripts/Runtime/Voice/OpenAiVoiceService.cs`

**Implementation:**
- Implement `IVoiceService`
- `StartRecording()` — Unity `Microphone.Start()`, capture WAV audio
- `StopRecording()` — stop capture, send WAV bytes via UnityWebRequest POST to `{baseUrl}/v1/audio/transcriptions`
  - Headers: `Authorization: Bearer {apiKey}`
  - Form: multipart/form-data with `file` (audio bytes), `model` ("whisper-1"), `language` (from ProviderConfig.sttLanguage)
- Parse response JSON → `OnSpeechRecognized(text)`
- `Speak()` / `StopSpeaking()` — implemented in Task 2.2

**Key details:**
- Unity `Microphone.Start()` records to an `AudioClip`. Convert to WAV bytes via `AudioClip.GetData()` + WAV header.
- Max recording duration: 60s (Whisper limit).
- Error handling: network failure, 4xx/5xx → log + fire OnPlaybackComplete.

**Commit:** `feat: OpenAiVoiceService STT implementation`

---

### Task 2.2: OpenAiVoiceService — TTS

**Objective:** Implement TTS via OpenAI-compatible `/v1/audio/speech` endpoint.

**Files:**
- Modify: `Assets/Scripts/Runtime/Voice/OpenAiVoiceService.cs`

**Implementation:**
- `Speak(text)` — POST to `{baseUrl}/v1/audio/speech`
  - JSON body: `{ "model": "tts-1", "voice": "nova", "input": text, "speed": 1.0 }`
  - Response: raw audio bytes (mp3)
- Save to temp file, play via `AudioSource.PlayOneShot()` or `AudioClip`
- `StopSpeaking()` — stop AudioSource playback
- Fire `OnPlaybackComplete` when done

**Key details:**
- OpenAI TTS returns raw audio bytes, not base64. Write to temp file, load as AudioClip.
- Or: use `UnityWebRequest` + `DownloadHandlerAudioClip` to stream directly.
- Voice/model/speed from ProviderConfig fields.

**Commit:** `feat: OpenAiVoiceService TTS implementation`

---

### Task 2.3: HermesVoiceService — STT + TTS

**Objective:** Implement voice service that proxies to Hermes backend endpoints.

**Files:**
- Create: `Assets/Scripts/Runtime/Voice/HermesVoiceService.cs`

**Implementation:**
- Implement `IVoiceService`
- `StartRecording()` — Unity `Microphone.Start()`
- `StopRecording()` — POST to `{hermesUrl}/api/audio/transcribe`
  - JSON body: `{ "data_url": "data:audio/wav;base64,...", "mime_type": "audio/wav" }`
  - Response: `{ "ok": true, "transcript": "...", "provider": "..." }`
  - Fire `OnSpeechRecognized(transcript)`
- `Speak(text)` — POST to `{hermesUrl}/api/audio/speak`
  - JSON body: `{ "text": "..." }`
  - Response: `{ "ok": true, "data_url": "data:audio/mpeg;base64,...", "provider": "..." }`
  - Decode base64 data_url → save to temp file → play via AudioSource
- `StopSpeaking()` — stop AudioSource

**Key details:**
- Hermes URL comes from `AppSettings.hermesRestUrl` (already exists).
- Audio encoding: WAV from Microphone → base64 → data_url format (what Hermes expects).
- Hermes handles all provider resolution internally.

**Commit:** `feat: HermesVoiceService implementation`

---

### Task 2.4: VoiceServiceFactory

**Objective:** Factory that creates the correct IVoiceService based on backend type.

**Files:**
- Create: `Assets/Scripts/Runtime/Voice/VoiceServiceFactory.cs`

**Implementation:**
```csharp
public static class VoiceServiceFactory
{
    public static IVoiceService Create(ProviderConfig provider, AppSettings settings)
    {
        if (provider == null)
            return CreateFallback();

        if (ChatService.IsHermesProvider(provider))
            return CreateHermes(settings);

        return CreateOpenAi(provider);
    }

    static IVoiceService CreateOpenAi(ProviderConfig provider) { ... }
    static IVoiceService CreateHermes(AppSettings settings) { ... }
    static IVoiceService CreateFallback() { return new WebSpeechBridge(); }
}
```

**Commit:** `feat: VoiceServiceFactory for backend-based voice selection`

---

## Phase 3: VoiceController Refactoring

### Task 3.1: Wire VoiceController to factory

**Objective:** VoiceController uses VoiceServiceFactory instead of hardcoding WebSpeechBridge.

**Files:**
- Modify: `Assets/Scripts/Runtime/UI/UITK/VoiceController.cs`

**Changes:**
- `EnsureVoicePipelineAsync()` — call `VoiceServiceFactory.Create(provider, settings)` instead of `AddComponent<WebSpeechBridge>()`
- Store reference to current `IVoiceService`
- When provider changes (switch backend) — dispose old service, create new one
- Pass `AppSettings` to factory (need reference to settings)

**Commit:** `feat: VoiceController uses VoiceServiceFactory`

---

### Task 3.2: Device selection in VoiceController

**Objective:** Apply input/output device preferences from AppSettings.

**Files:**
- Modify: `Assets/Scripts/Runtime/Voice/OpenAiVoiceService.cs`
- Modify: `Assets/Scripts/Runtime/Voice/HermesVoiceService.cs`

**Implementation:**
- Before `Microphone.Start()`, find device by name from `AppSettings.inputDeviceName`
- For output: set `AudioSource.outputAudioMixerGroup` or use platform-specific device selection
- Volume: set `AudioSource.volume = settings.outputVolume`

**Note:** Unity has limited output device selection. `Microphone.devices` for input is straightforward. Output device selection may need platform-specific code or be limited to volume control only.

**Commit:** `feat: apply device and volume settings in voice services`

---

## Phase 4: Settings UI

### Task 4.1: Voice section in Settings page

**Objective:** Add voice device/volume controls to the Settings screen.

**Files:**
- Modify: `Assets/Scripts/Runtime/UI/UITK/SettingsController.cs`
- Modify: `Assets/UI/Main/SettingsView.uxml` (if exists) or create UXML inline

**UI elements:**
- Toggle: "Голосовой режим" → `voiceIOEnabled`
- Dropdown: "Устройство ввода" → populated from `Microphone.devices`, saved to `inputDeviceName`
- Slider: "Громкость вывода" → `outputVolume` (0.0-1.0)
- Label: "Устройство вывода" → informational (Unity limitation on output device selection)

**Commit:** `feat: voice settings UI in Settings page`

---

### Task 4.2: Voice section in Provider editor

**Objective:** Add TTS/STT provider selection to provider settings panel.

**Files:**
- Modify: `Assets/Scripts/Runtime/UI/UITK/ProvidersController.cs`

**UI elements (shown only when provider is OpenAI-compatible, not Hermes):**
- Dropdown: "STT провайдер" → ["OpenAI Whisper", "Groq Whisper", "Local (faster-whisper)"]
- Dropdown: "TTS провайдер" → ["Edge (бесплатно)", "OpenAI TTS", "ElevenLabs", "MiniMax", "Mistral"]
- Dropdown: "Голос TTS" → populated based on selected TTS provider
- Slider: "Скорость речи" → 0.25-4.0
- Input: "Язык STT" → text field for language code

**For Hermes backend providers:** show minimal voice section or hide entirely (Hermes manages its own providers).

**Commit:** `feat: voice settings in provider editor`

---

## Phase 5: Integration & Polish

### Task 5.1: VAD (Voice Activity Detection)

**Objective:** Auto-stop recording on silence for better UX.

**Files:**
- Modify: `Assets/Scripts/Runtime/Voice/OpenAiVoiceService.cs`
- Modify: `Assets/Scripts/Runtime/Voice/HermesVoiceService.cs`

**Implementation:**
- After `Microphone.Start()`, run RMS analysis on audio buffer (similar to Hermes Desktop's `use-mic-recorder.ts`)
- Threshold: 0.075 (configurable later)
- Silence timeout: 1250ms after speech detected
- Idle timeout: 12s (no speech at all → stop)
- Fire `OnSpeechRecognized` with recorded audio when silence detected

**Commit:** `feat: VAD silence detection in voice services`

---

### Task 5.2: Error handling & fallback

**Objective:** Graceful degradation when voice providers fail.

**Files:**
- Modify: `Assets/Scripts/Runtime/Voice/OpenAiVoiceService.cs`
- Modify: `Assets/Scripts/Runtime/Voice/HermesVoiceService.cs`

**Behavior:**
- STT failure → log warning, show notification, stay in idle (don't crash)
- TTS failure → log warning, skip playback, fire OnPlaybackComplete
- Network timeout → retry once, then fail gracefully
- No microphone permission → show localized error message

**Commit:** `feat: voice error handling and fallback`

---

### Task 5.3: Cleanup & temp file management

**Objective:** Proper cleanup of audio temp files.

**Files:**
- Modify: `Assets/Scripts/Runtime/Voice/OpenAiVoiceService.cs`
- Modify: `Assets/Scripts/Runtime/Voice/HermesVoiceService.cs`

**Implementation:**
- Track temp files created during STT/TTS
- Delete on `OnDestroy()` or service disposal
- Max temp file age: 5 minutes (auto-cleanup timer)

**Commit:** `feat: voice temp file cleanup`

---

## Files Changed Summary

| File | Action |
|------|--------|
| `Data/Models/ProviderConfig.cs` | Modify — add voice fields |
| `Data/Models/AppSettings.cs` | Modify — add device/volume fields |
| `Voice/OpenAiVoiceService.cs` | **Create** — OpenAI STT+TTS |
| `Voice/HermesVoiceService.cs` | **Create** — Hermes proxy STT+TTS |
| `Voice/VoiceServiceFactory.cs` | **Create** — factory |
| `UI/UITK/VoiceController.cs` | Modify — use factory |
| `UI/UITK/SettingsController.cs` | Modify — voice settings UI |
| `UI/UITK/ProvidersController.cs` | Modify — voice provider settings |
| `Voice/WebSpeechBridge.cs` | Keep as fallback (no changes) |
| `Voice/VoiceInputManager.cs` | Keep (no changes needed) |
| `Voice/VoiceOutputManager.cs` | Keep (no changes needed) |

---

## Risks & Tradeoffs

1. **Unity output device selection** — limited. May need to accept volume-only control for output.
2. **WAV encoding from Microphone** — need custom WAV header writer. Not hard but fiddly.
3. **OpenAI TTS audio format** — returns mp3. Need to handle mp3 playback in Unity (may need `DownloadHandlerAudioClip` or第三方 mp3 decoder).
4. **Provider capability detection** — not all OpenAI-compatible providers support audio endpoints. Need to handle 404/405 gracefully.
5. **Multiple STT/TTS providers** — the dropdown UX for selecting different providers per backend needs careful design.

---

## Verification

1. Set up Hermes backend with voice configured
2. Create Hermes provider in neon-companion → verify STT + TTS works through Hermes endpoints
3. Create OpenAI provider (e.g., OpenAI direct) → verify STT + TTS works through OpenAI APIs
4. Switch between providers → verify voice service switches correctly
5. Test device selection in Settings
6. Test volume control
7. Test VAD silence detection
8. Test error scenarios (no network, wrong API key, no microphone)
