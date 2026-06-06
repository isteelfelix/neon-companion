using NeonCompanion.Runtime.Chat;
using NeonCompanion.Runtime.Data.Models;
using UnityEngine;

namespace NeonCompanion.Runtime.Voice
{
    public static class VoiceServiceFactory
    {
        public static IVoiceService Create(ProviderConfig provider, AppSettings settings)
        {
            if (provider == null)
                return CreateFallback();
            if (ChatService.IsHermesProvider(provider))
                return CreateHermes(provider, settings);
            return CreateOpenAi(provider, settings);
        }

        private static IVoiceService CreateOpenAi(ProviderConfig provider, AppSettings settings)
        {
            string inputDevice = settings != null ? settings.inputDeviceName : "";
            float outputVolume = settings != null ? settings.outputVolume : 0.8f;

            GameObject go = new GameObject("OpenAiVoiceService");
            Object.DontDestroyOnLoad(go);
            OpenAiVoiceService service = go.AddComponent<OpenAiVoiceService>();
            service.Initialize(
                provider.baseUrl ?? "",
                provider.apiKey ?? "",
                provider.sttLanguage,
                provider.ttsVoice,
                provider.ttsModel,
                provider.ttsSpeed > 0f ? provider.ttsSpeed : 1f,
                inputDevice,
                outputVolume);
            return service;
        }

        private static IVoiceService CreateHermes(ProviderConfig provider, AppSettings settings)
        {
            string hermesRestUrl = settings != null ? settings.hermesRestUrl : "";
            string apiKey        = provider != null ? provider.apiKey ?? "" : "";
            string inputDevice   = settings != null ? settings.inputDeviceName : "";
            float outputVolume   = settings != null ? settings.outputVolume : 0.8f;

            GameObject go = new GameObject("HermesVoiceService");
            Object.DontDestroyOnLoad(go);
            HermesVoiceService service = go.AddComponent<HermesVoiceService>();
            service.Initialize(hermesRestUrl, apiKey, inputDevice, outputVolume);
            return service;
        }

        private static IVoiceService CreateFallback()
        {
            return null;
        }
    }
}
