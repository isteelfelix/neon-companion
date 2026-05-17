using NeonCompanion.Runtime.Api;
using NeonCompanion.Runtime.Avatar;
using NeonCompanion.Runtime.Chat;
using NeonCompanion.Runtime.Data.Repositories;
using NeonCompanion.Runtime.Data.Secrets;
using NeonCompanion.Runtime.Data.Storage;
using NeonCompanion.Runtime.Localization;
using NeonCompanion.Runtime.Platform;
using UnityEngine;

namespace NeonCompanion.Runtime.Core
{
    public sealed class AppBootstrap : MonoBehaviour
    {
        private static AppBootstrap _instance;

        public CompanionApp App { get; private set; }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);

            AppPaths.EnsureDataDirectory();

            var services = new ServiceRegistry();
            var storage = new JsonFileStorage();
            var secrets = new DeviceSecretStore(storage);
            var filePicker = new DefaultFilePickerService();

            var providers = new ProviderConfigRepository(storage, secrets);
            var sessions = new ChatSessionRepository(storage);
            var avatars = new AvatarRepository(storage);
            var settings = new AppSettingsRepository(storage);
            var aiClient = new OpenAiCompatibleClient();
            var avatarService = new AvatarService();
            var providerManager = new ProviderManager(providers);
            var chatService = new ChatService(aiClient, providerManager, sessions);

            // Apply avatar system prompt
            var settingsData = settings.Load();
            if (settingsData != null && settingsData.useSystemPrompt)
            {
                var avatarProfiles = avatars.GetAll();
                var systemPrompt = avatarService.GetSystemPrompt(settingsData.activeAvatarId, avatarProfiles);
                if (!string.IsNullOrEmpty(systemPrompt))
                    chatService.SystemPrompt = systemPrompt;
            }

            // Localization
            string language = settingsData?.language ?? "ru";
            var localizationService = new JsonLocalizationService(language);
            LocalizationExtensions.SetLocalizationService(localizationService);

            App = new CompanionApp(
                services,
                aiClient,
                providers,
                sessions,
                avatars,
                settings,
                avatarService);

            services.Register<IJsonStorage>(storage);
            services.Register<ISecretStore>(secrets);
            services.Register<IFilePickerService>(filePicker);
            services.Register<IProviderConfigRepository>(providers);
            services.Register<IChatSessionRepository>(sessions);
            services.Register<IAvatarRepository>(avatars);
            services.Register<IAppSettingsRepository>(settings);
            services.Register<IAiClient>(aiClient);
            services.Register<IAvatarService>(avatarService);
            services.Register<ProviderManager>(providerManager);
            services.Register<ChatService>(chatService);
            services.Register<ILocalizationService>(localizationService);

            NeonLogger.Log("App bootstrap completed.");
        }
    }
}
