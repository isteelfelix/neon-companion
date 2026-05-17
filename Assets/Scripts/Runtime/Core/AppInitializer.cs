using System.Threading.Tasks;
using NeonCompanion.Runtime.Chat;
using NeonCompanion.Runtime.Core;
using UnityEngine;

namespace NeonCompanion.Runtime.Core
{
    public static class AppInitializer
    {
        public static async Task<CompanionApp> InitializeAsync()
        {
            var bootstrap = Object.FindObjectOfType<AppBootstrap>();
            if (bootstrap == null)
            {
                Debug.LogError("[NeonCompanion] AppBootstrap not found in scene!");
                return null;
            }

            var app = bootstrap.App;

            // Pre-initialize chat service
            var chatService = app.Services.GetRequired<ChatService>();
            await chatService.GetOrCreateChatAsync();

            Debug.Log("[NeonCompanion] Application initialized successfully.");
            return app;
        }
    }
}