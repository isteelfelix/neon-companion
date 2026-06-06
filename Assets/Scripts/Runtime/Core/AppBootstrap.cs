using NeonCompanion.Runtime.Api;
using NeonCompanion.Runtime.Api.Hermes;
using NeonCompanion.Runtime.Avatar;
using NeonCompanion.Runtime.Avatar3D;
using NeonCompanion.Runtime.Chat;
using NeonCompanion.Runtime.Data.Models;
using NeonCompanion.Runtime.Data.Repositories;
using NeonCompanion.Runtime.Data.Secrets;
using NeonCompanion.Runtime.Data.Storage;
using NeonCompanion.Runtime.Donation;
using NeonCompanion.Runtime.Localization;
using NeonCompanion.Runtime.Platform;
using NeonCompanion.Runtime.Plugins;
using NeonCompanion.Runtime.Voice;
using UnityEngine;

namespace NeonCompanion.Runtime.Core
{
    public sealed class AppBootstrap : MonoBehaviour
    {
        private static AppBootstrap _instance;

        public CompanionApp App { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void ConfigureRuntime()
        {
            Application.runInBackground = true;
        }

        private void Awake()
        {
            Application.runInBackground = true;

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
            var fileDrop = PlatformServiceFactory.CreateFileDropService(gameObject);
            var windowChrome = PlatformServiceFactory.CreateWindowChromeService(gameObject);
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
            var processExecutionService = new ProcessExecutionService();

            // Backend selector — global mode switch (Hermes vs OpenAI)
            var backendSelector = gameObject.AddComponent<GlobalBackendSelector>();
            backendSelector.Initialize(
                hermesWsUrl: "",
                hermesRestUrl: "",
                settingsRepo: settings,
                secretStore: secrets
            );

            // Load saved backend mode
            var savedSettings = settings.Load();
            if (savedSettings != null)
            {
                backendSelector.LoadFromSettings(savedSettings);
                // Apply saved Hermes token
                string savedToken = secrets.GetSecret("hermes_token");
                if (!string.IsNullOrEmpty(savedToken))
                    backendSelector.HermesToken = savedToken;
            }

            // Let the backend selector derive the Hermes endpoint from the active provider.
            backendSelector.SetActiveProviderResolver(() => chatService.CurrentProvider);

            // Wire backend mode changes to ChatService transport
            backendSelector.OnModeChanged += mode =>
            {
                if (mode == BackendMode.Hermes && backendSelector.SessionManager != null)
                    chatService.SetTransport(backendSelector.SessionManager);
                else
                    chatService.SetTransport(null);
            };

            // Apply initially loaded mode too (LoadFromSettings does not emit OnModeChanged)
            if (backendSelector.CurrentMode == BackendMode.Hermes && backendSelector.SessionManager != null)
                chatService.SetTransport(backendSelector.SessionManager);
            else
                chatService.SetTransport(null);

            // The saved backend mode is the UI/workspace context. Restore the last active
            // provider for that backend without letting a provider from another backend flip
            // the mode at startup.
            _ = ReconcileBackendModeToSavedContextAsync(backendSelector, providerManager, savedSettings);

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
            services.Register<IFileDropService>(fileDrop);
            services.Register<IWindowChromeService>(windowChrome);
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
            services.Register<ProcessExecutionService>(processExecutionService);
            services.Register<ILocalizationService>(localizationService);
            services.Register<GlobalBackendSelector>(backendSelector);

            var pluginManager = GetComponent<PluginManager>();
            if (pluginManager == null)
                pluginManager = gameObject.AddComponent<PluginManager>();
            pluginManager.Initialize(services);
            services.Register<PluginManager>(pluginManager);

            NeonLogger.Log("App bootstrap completed.");
        }

        private static async System.Threading.Tasks.Task ReconcileBackendModeToSavedContextAsync(
            GlobalBackendSelector backendSelector,
            ProviderManager providerManager,
            AppSettings settings)
        {
            BackendMode desiredMode = backendSelector.CurrentMode;
            string preferredProviderId = null;
            if (settings != null)
            {
                preferredProviderId = desiredMode == BackendMode.Hermes
                    ? settings.activeHermesProviderId
                    : settings.activeOpenAiProviderId;
                if (string.IsNullOrWhiteSpace(preferredProviderId))
                    preferredProviderId = settings.activeProviderId;
            }

            ProviderConfig activeProvider = await providerManager.GetActiveProviderForBackendAsync(desiredMode, preferredProviderId, true);

            if (desiredMode == BackendMode.Hermes && activeProvider != null)
            {
                backendSelector.ConfigureHermesEndpoint(activeProvider.baseUrl, activeProvider.apiKey);
            }
        }
    }
}
