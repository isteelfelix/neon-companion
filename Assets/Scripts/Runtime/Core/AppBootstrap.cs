using NeonCompanion.Runtime.Api;
using NeonCompanion.Runtime.Avatar;
using NeonCompanion.Runtime.Avatar3D;
using NeonCompanion.Runtime.Chat;
using NeonCompanion.Runtime.Data.Repositories;
using NeonCompanion.Runtime.Data.Secrets;
using NeonCompanion.Runtime.Data.Storage;
using NeonCompanion.Runtime.Donation;
using NeonCompanion.Runtime.Localization;
using NeonCompanion.Runtime.Platform;
using NeonCompanion.Runtime.Plugins;
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

            // Платформенные сервисы создаются централизованно через фабрику
            // (см. docs/16_Platform_Architecture.md)
            var filePicker = PlatformServiceFactory.CreateFilePickerService();
            var platformInfo = PlatformServiceFactory.CreatePlatformInfoService();
            var voiceService = PlatformServiceFactory.CreateVoiceService(gameObject);
#if UNITY_ANDROID && !UNITY_EDITOR
            AndroidPermissionHelper.RequestPermission(AndroidPermissionHelper.RECORD_AUDIO);
            AndroidPermissionHelper.RequestPermission(AndroidPermissionHelper.READ_EXTERNAL_STORAGE);
#endif

            var providers = new ProviderConfigRepository(storage, secrets);
            var sessions = new ChatSessionRepository(storage);
            var avatars = new AvatarRepository(storage);
            var settings = new AppSettingsRepository(storage);
            var aiClient = new OpenAiCompatibleClient();
            var avatarService = new AvatarService();
            var avatar3DService = new Avatar3DService();
            var donationService = new DonationService();
            var providerManager = new ProviderManager(providers);
            var modelDiscoveryService = new ModelDiscoveryService(providers);
            var chatService = new ChatService(aiClient, providerManager, sessions);

            // Apply avatar system prompt
            var settingsData = settings.Load();
            if (settingsData != null && settingsData.useSystemPrompt)
            {
                var avatarProfiles = avatars.GetAll();
                string activeAvatarId = settingsData.activeAvatarId;
                bool knownAvatar = !string.IsNullOrWhiteSpace(activeAvatarId) &&
                                   (System.Array.IndexOf(new[] { "neon", "aurora", "ember", "glass", "flora", "mono", "cobalt", "rose" }, activeAvatarId) >= 0 ||
                                    avatarProfiles.Exists(a => a != null && a.id == activeAvatarId));

                if (!knownAvatar)
                    activeAvatarId = "neon";

                var systemPrompt = avatarService.GetSystemPrompt(activeAvatarId, avatarProfiles);
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
            services.Register<IPlatformInfoService>(platformInfo);
            services.Register<IVoiceService>(voiceService);
            services.Register<IProviderConfigRepository>(providers);
            services.Register<IChatSessionRepository>(sessions);
            services.Register<IAvatarRepository>(avatars);
            services.Register<IAppSettingsRepository>(settings);
            services.Register<IAiClient>(aiClient);
            services.Register<IAvatarService>(avatarService);
            services.Register<IAvatar3DService>(avatar3DService);
            services.Register<IDonationService>(donationService);
            services.Register<ProviderManager>(providerManager);
            services.Register<ModelDiscoveryService>(modelDiscoveryService);
            services.Register<ChatService>(chatService);
            services.Register<ILocalizationService>(localizationService);

            var pluginManager = GetComponent<PluginManager>();
            if (pluginManager == null)
                pluginManager = gameObject.AddComponent<PluginManager>();
            pluginManager.Initialize(services);
            services.Register<PluginManager>(pluginManager);

            NeonLogger.Log("App bootstrap completed.");
        }
    }
}