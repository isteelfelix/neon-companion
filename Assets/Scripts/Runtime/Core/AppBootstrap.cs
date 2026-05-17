using NeonCompanion.Runtime.Api;
using NeonCompanion.Runtime.Avatar;
using NeonCompanion.Runtime.Data.Repositories;
using NeonCompanion.Runtime.Data.Storage;
using UnityEngine;

namespace NeonCompanion.Runtime.Core
{
    public sealed class AppBootstrap : MonoBehaviour
    {
        public CompanionApp App { get; private set; }

        private void Awake()
        {
            AppPaths.EnsureDataDirectory();

            var services = new ServiceRegistry();
            var storage = new JsonFileStorage();

            var providers = new ProviderConfigRepository(storage);
            var sessions = new ChatSessionRepository(storage);
            var avatars = new AvatarRepository(storage);
            var settings = new AppSettingsRepository(storage);
            var aiClient = new OpenAiCompatibleClient();
            var avatarService = new AvatarService();

            App = new CompanionApp(
                services,
                aiClient,
                providers,
                sessions,
                avatars,
                settings,
                avatarService);

            services.Register<IJsonStorage>(storage);
            services.Register<IProviderConfigRepository>(providers);
            services.Register<IChatSessionRepository>(sessions);
            services.Register<IAvatarRepository>(avatars);
            services.Register<IAppSettingsRepository>(settings);
            services.Register<IAiClient>(aiClient);
            services.Register<IAvatarService>(avatarService);

            Debug.Log("[NeonCompanion] App bootstrap completed.");
        }
    }
}
