using NeonCompanion.Runtime.Data.Secrets;
using NeonCompanion.Runtime.Data.Storage;
using NeonCompanion.Runtime.Voice;
using System.Threading.Tasks;
using UnityEngine;

namespace NeonCompanion.Runtime.Platform
{
    /// <summary>
    /// Центральная фабрика для создания платформенно-зависимых сервисов.
    /// Используется в AppBootstrap для регистрации в ServiceRegistry.
    /// 
    /// Следуй правилам из docs/16_Platform_Architecture.md и docs/17_iOS_Platform_Architecture.md:
    /// - Вся платформенная логика создания должна быть здесь.
    /// - Контроллеры не должны знать о конкретных реализациях.
    /// - iOS и Android изолированы, общие мобильные случаи — через IsMobile.
    /// </summary>
    public static class PlatformServiceFactory
    {
        public static IFilePickerService CreateFilePickerService()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return new DefaultFilePickerService(); // Android-логика внутри через #if
#elif UNITY_IOS && !UNITY_EDITOR
            return new iOS.iOSFilePickerService(); // iOS native bridge (placeholder + fallback, см. 17_iOS_Platform_Architecture.md)
#elif UNITY_EDITOR || UNITY_STANDALONE_WIN
            return new DefaultFilePickerService();
#else
            Debug.LogWarning("[NeonCompanion] Unknown platform for FilePicker. Using stub.");
            return new StubFilePickerService();
#endif
        }

        public static IPlatformInfoService CreatePlatformInfoService()
        {
            return new DefaultPlatformInfoService();
        }

        public static IVoiceService CreateVoiceService(GameObject host = null)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // On Android we prefer the WebSpeechBridge component (handles native TTS + Intent speech)
            if (host == null)
                host = GameObject.Find("VoiceBridge") ?? new GameObject("VoiceBridge");

            var bridge = host.GetComponent<WebSpeechBridge>();
            if (bridge == null)
                bridge = host.AddComponent<WebSpeechBridge>();

            DontDestroyOnLoad(host);
            return bridge;
#elif UNITY_EDITOR || UNITY_STANDALONE_WIN
            if (host == null)
                host = GameObject.Find("VoiceBridge") ?? new GameObject("VoiceBridge");

            var bridge = host.GetComponent<WebSpeechBridge>();
            if (bridge == null)
                bridge = host.AddComponent<WebSpeechBridge>();

            DontDestroyOnLoad(host);
            return bridge;
#elif UNITY_IOS && !UNITY_EDITOR
            // iOS: for now use WebSpeechBridge (will be extended with native AVSpeech/SFSpeech in IOS-05)
            // See 17_iOS_Platform_Architecture.md
            if (host == null)
                host = GameObject.Find("VoiceBridge") ?? new GameObject("VoiceBridge");

            var bridge = host.GetComponent<WebSpeechBridge>();
            if (bridge == null)
                bridge = host.AddComponent<WebSpeechBridge>();

            DontDestroyOnLoad(host);
            return bridge;
#else
            Debug.LogWarning("[NeonCompanion] Voice not supported on this platform. Returning stub.");
            return new StubVoiceService();
#endif
        }
    }

    /// <summary>
    /// Заглушка для неподдерживаемых платформ.
    /// </summary>
    public sealed class StubFilePickerService : IFilePickerService
    {
        public Task<string> PickImagePathAsync() => Task.FromResult<string>(null);
        public Task<string> PickFileAsync(string extension) => Task.FromResult<string>(null);
    }

    /// <summary>
    /// Заглушка для voice на неподдерживаемых платформах.
    /// </summary>
    public sealed class StubVoiceService : IVoiceService
    {
        public bool IsRecording => false;
        public bool IsSpeaking => false;
        public bool IsAvailable => false;

        public event System.Action<string> OnSpeechRecognized;
        public event System.Action OnPlaybackComplete;

        public void StartRecording() { }
        public byte[] StopRecording() => null;
        public void Speak(string text) { OnPlaybackComplete?.Invoke(); }
        public void StopSpeaking() { }
    }
}