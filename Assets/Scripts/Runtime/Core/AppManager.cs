using System.Threading.Tasks;
using NeonCompanion.Runtime.Chat;
using NeonCompanion.Runtime.Core;
using UnityEngine;

namespace NeonCompanion.Runtime.Core
{
    /// <summary>
    /// Central manager for the Neon Companion application.
    /// Handles initialization and provides easy access to core services.
    /// </summary>
    public class AppManager : MonoBehaviour
    {
        public static AppManager Instance { get; private set; }

        public CompanionApp App { get; private set; }
        public ChatService Chat { get; private set; }

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

            Debug.Log("[AppManager] Application ready.");
        }
    }
}