using System.Collections.Generic;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Chat;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.UI.Chat;
using UnityEngine;

namespace NeonCompanion.Runtime.Core
{
    public class AppManager : MonoBehaviour
    {
        public static AppManager Instance { get; private set; }

        public CompanionApp App { get; private set; }
        public ChatService Chat { get; private set; }
        public ChatViewModel CurrentChat => Chat?.CurrentChatViewModel;
        public bool IsInitialized => App != null && Chat != null;

        private async void Awake()
        {
            if (Instance != null)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);

            App = await AppInitializer.InitializeAsync();
            Chat = App?.Services.GetRequired<ChatService>();

            NeonLogger.Log("Application ready.");
        }

        public async Task SendMessageAsync(string message)
        {
            if (Chat == null)
            {
                NeonLogger.LogError("ChatService not initialized.");
                return;
            }

            await Chat.SendMessageAsync(message);
        }

        public async Task StartNewChatSessionAsync()
        {
            if (Chat == null)
            {
                NeonLogger.LogError("ChatService not initialized.");
                return;
            }

            await Chat.StartNewSessionAsync();
        }

        public async Task ClearCurrentChatSessionAsync()
        {
            if (Chat == null)
            {
                NeonLogger.LogError("ChatService not initialized.");
                return;
            }

            await Chat.ClearCurrentSessionAsync();
        }

        public async Task SwitchProviderAsync(ProviderConfig provider)
        {
            if (Chat == null)
            {
                NeonLogger.LogError("ChatService not initialized.");
                return;
            }

            await Chat.SwitchProviderAsync(provider);
        }

        public async Task<List<ChatSession>> GetAllChatSessionsAsync()
        {
            if (Chat == null)
            {
                NeonLogger.LogError("ChatService not initialized.");
                return new List<ChatSession>();
            }

            return await Chat.GetAllSessionsAsync();
        }

        public async Task SwitchToChatSessionAsync(ChatSession session)
        {
            if (Chat == null)
            {
                NeonLogger.LogError("ChatService not initialized.");
                return;
            }

            await Chat.SwitchToSessionAsync(session);
        }
    }
}