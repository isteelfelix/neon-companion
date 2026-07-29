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
using System.Threading.Tasks;
using UnityEngine;

namespace NeonCompanion.Runtime.Core
{
    public sealed class AppBootstrap : MonoBehaviour
    {
        private static AppBootstrap _instance;

        private PersistentShellService _persistentShell;
        private ClientTerminalExecutionService _clientTerminal;
        private ICompanionWindowService _companionWindow;

        public CompanionApp App { get; private set; }
        public Task InitializationTask { get; private set; } = Task.CompletedTask;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void ConfigureRuntime()
        {
            Application.runInBackground = true;
        }

        private void OnApplicationQuit()
        {
            if (_companionWindow != null)
            {
                _companionWindow.Dispose();
                _companionWindow = null;
            }

            // Kill the long-lived agent shell so we don't leak a powershell/bash process.
            if (_persistentShell != null)
            {
                _persistentShell.Dispose();
                _persistentShell = null;
            }
            if (_clientTerminal != null)
            {
                _clientTerminal.Dispose();
                _clientTerminal = null;
            }
        }

        private void Awake()
        {
            Application.runInBackground = true;

            // The isolated Companion player receives a display-only snapshot. It must
            // never initialize repositories, secrets, providers, sessions, or chat.
            if (CompanionProcessMode.IsPlayerProcess)
            {
                enabled = false;
                return;
            }

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
            var companionWindow = PlatformServiceFactory.CreateCompanionWindowService();
            var voiceService = PlatformServiceFactory.CreateVoiceService(gameObject);
            _companionWindow = companionWindow;
#if UNITY_ANDROID && !UNITY_EDITOR
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
            var persistentShellService = new PersistentShellService();
            var clientTerminalService = new ClientTerminalExecutionService(processExecutionService);
            _persistentShell = persistentShellService;
            _clientTerminal = clientTerminalService;

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

            // Restore provider/backend before the main UI starts loading chat sessions.
            InitializationTask = RestoreStartupContextAsync(
                backendSelector,
                providerManager,
                chatService,
                settings,
                savedSettings);

            // Apply avatar system prompt
            var settingsData = settings.Load();
            if (settingsData != null && settingsData.useSystemPrompt)
            {
                var avatarProfiles = avatars.GetAll();
                string activeAvatarId = settingsData.activeAvatarId;
                bool knownAvatar = !string.IsNullOrWhiteSpace(activeAvatarId) &&
                                   (System.Array.IndexOf(new[] { "neon", "yorha-2b", "aurora", "ember", "glass", "flora", "mono", "cobalt", "rose" }, activeAvatarId) >= 0 ||
                                    BuiltInAvatarProfiles.TryCreate(activeAvatarId) != null ||
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

            services.Register<IJsonStorage>(storage);
            services.Register<ISecretStore>(secrets);
            services.Register<IFilePickerService>(filePicker);
            services.Register<IPlatformInfoService>(platformInfo);
            services.Register<IFileDropService>(fileDrop);
            services.Register<IWindowChromeService>(windowChrome);
            services.Register<ICompanionWindowService>(companionWindow);
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
            services.Register<PersistentShellService>(persistentShellService);
            services.Register<ClientTerminalExecutionService>(clientTerminalService);
            services.Register<ILocalizationService>(localizationService);
            services.Register<GlobalBackendSelector>(backendSelector);

            var pluginManager = GetComponent<PluginManager>();
            if (pluginManager == null)
                pluginManager = gameObject.AddComponent<PluginManager>();
            pluginManager.Initialize(services);
            services.Register<PluginManager>(pluginManager);

            // Publish App only after the registry is complete. Consumers also await
            // InitializationTask before using the restored provider/backend context.
            App = new CompanionApp(
                services,
                aiClient,
                providers,
                sessions,
                avatars,
                settings,
                avatarService);

            NeonLogger.Log("App bootstrap completed.");
        }

        private static async Task RestoreStartupContextAsync(
            GlobalBackendSelector backendSelector,
            ProviderManager providerManager,
            ChatService chatService,
            IAppSettingsRepository settingsRepository,
            AppSettings settings)
        {
            try
            {
                AppSettings startupSettings = settings ?? new AppSettings();
                BackendMode desiredMode = backendSelector.CurrentMode;
                ProviderConfig activeProvider = null;

                // activeProviderId is the last provider used across all backends. Prefer it and
                // derive the backend from the provider so the two cannot start out inconsistent.
                if (!string.IsNullOrWhiteSpace(startupSettings.activeProviderId))
                {
                    ProviderConfig lastUsed = await providerManager.GetProviderByIdAsync(startupSettings.activeProviderId);
                    if (lastUsed != null && lastUsed.isEnabled)
                    {
                        activeProvider = lastUsed;
                        desiredMode = ChatService.IsHermesProvider(lastUsed)
                            ? BackendMode.Hermes
                            : BackendMode.OpenAI;
                    }
                }

                if (activeProvider == null)
                {
                    string preferredProviderId = desiredMode == BackendMode.Hermes
                        ? startupSettings.activeHermesProviderId
                        : startupSettings.activeOpenAiProviderId;
                    activeProvider = await providerManager.GetActiveProviderForBackendAsync(
                        desiredMode,
                        preferredProviderId,
                        true);
                }

                if (backendSelector.CurrentMode != desiredMode)
                    await backendSelector.SetMode(desiredMode);

                if (activeProvider == null)
                {
                    chatService.ClearActiveProviderState();
                    NeonLogger.LogWarning("[Bootstrap] No enabled provider for backend " + desiredMode + ".");
                    return;
                }

                chatService.SetActiveProviderWithoutSession(activeProvider);

                if (desiredMode == BackendMode.Hermes)
                {
                    // Restores the endpoint AND the persisted cookie session for it.
                    backendSelector.ConfigureHermesEndpoint(activeProvider.baseUrl, activeProvider.apiKey);

                    // Nothing used to drive the socket on startup, so a restored session sat
                    // unused until the user opened Providers and pressed Connect by hand — and
                    // until then no profiles and no sessions could load. Fire the connect off
                    // without awaiting: a slow or dead gateway must not hold up the UI, which
                    // picks profiles/sessions up from the transport's Connected event.
                    _ = backendSelector.ConnectHermes();
                }

                startupSettings.backendMode = desiredMode == BackendMode.Hermes ? "hermes" : "openai";
                startupSettings.activeProviderId = activeProvider.id;
                if (desiredMode == BackendMode.Hermes)
                    startupSettings.activeHermesProviderId = activeProvider.id;
                else
                    startupSettings.activeOpenAiProviderId = activeProvider.id;
                settingsRepository.Save(startupSettings);

                NeonLogger.Log("[Bootstrap] Restored " + desiredMode + " provider: " + activeProvider.displayName);
            }
            catch (System.Exception ex)
            {
                NeonLogger.LogError("[Bootstrap] Provider restore failed: " + ex);
            }
        }
    }
}
