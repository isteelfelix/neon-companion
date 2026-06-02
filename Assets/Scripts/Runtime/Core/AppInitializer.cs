using System.Threading.Tasks;
using NeonCompanion.Runtime.Chat;
using UnityEngine;

namespace NeonCompanion.Runtime.Core
{
    public static class AppInitializer
    {
        public static async Task<CompanionApp> InitializeAsync()
        {
            var bootstrap = Object.FindAnyObjectByType<AppBootstrap>();
            if (bootstrap == null)
            {
                Debug.LogError("[NeonCompanion] AppBootstrap not found in scene!");
                return null;
            }

            var app = bootstrap.App;
            if (app == null)
            {
                await Task.Yield();
                app = bootstrap.App;
            }

            if (app == null)
            {
                Debug.LogError("[NeonCompanion] AppBootstrap has not created the application.");
                return null;
            }

            // Pre-initialize chat service
            var chatService = app.Services.GetRequired<ChatService>();
            var settings = app.Settings.Load();
            if (settings != null)
            {
                chatService.SaveChatHistory = settings.saveChatHistory;

                var backendSelector = app.Services.GetRequired<GlobalBackendSelector>();
                BackendMode mode = backendSelector != null ? backendSelector.CurrentMode : BackendMode.OpenAI;
                string preferredProviderId = mode == BackendMode.Hermes
                    ? settings.activeHermesProviderId
                    : settings.activeOpenAiProviderId;
                if (string.IsNullOrWhiteSpace(preferredProviderId))
                    preferredProviderId = settings.activeProviderId;

                var provider = await app.ProviderManager.GetActiveProviderForBackendAsync(mode, preferredProviderId, true);
                if (provider != null)
                    chatService.SetActiveProviderWithoutSession(provider);
            }

            Debug.Log("[NeonCompanion] Application initialized successfully.");
            return app;
        }
    }
}
