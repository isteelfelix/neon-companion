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
            await chatService.GetOrCreateChatAsync(settings?.activeProviderId);

            Debug.Log("[NeonCompanion] Application initialized successfully.");
            return app;
        }
    }
}
