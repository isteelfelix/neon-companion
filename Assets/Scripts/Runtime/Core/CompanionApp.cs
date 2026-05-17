using NeonCompanion.Runtime.Api;
using NeonCompanion.Runtime.Avatar;
using NeonCompanion.Runtime.Data.Repositories;

namespace NeonCompanion.Runtime.Core
{
    public sealed class CompanionApp
    {
        public CompanionApp(
            ServiceRegistry services,
            IAiClient aiClient,
            IProviderConfigRepository providerRepository,
            IChatSessionRepository chatRepository,
            IAvatarRepository avatarRepository,
            IAppSettingsRepository settingsRepository,
            IAvatarService avatarService)
        {
            Services = services;
            AiClient = aiClient;
            Providers = providerRepository;
            Chats = chatRepository;
            Avatars = avatarRepository;
            Settings = settingsRepository;
            AvatarService = avatarService;
        }

        public ServiceRegistry Services { get; }
        public IAiClient AiClient { get; }
        public IProviderConfigRepository Providers { get; }
        public IChatSessionRepository Chats { get; }
        public IAvatarRepository Avatars { get; }
        public IAppSettingsRepository Settings { get; }
        public IAvatarService AvatarService { get; }
    }
}
