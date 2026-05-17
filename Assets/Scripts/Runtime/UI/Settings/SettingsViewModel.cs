using System.Collections.Generic;
using NeonCompanion.Runtime.Data.Models;

namespace NeonCompanion.Runtime.UI.Settings
{
    public sealed class SettingsViewModel
    {
        public List<ProviderConfig> Providers { get; } = new List<ProviderConfig>();
        public ProviderConfig DraftProvider { get; set; }

        public void BeginNewProvider()
        {
            DraftProvider = ProviderConfig.CreateDefault("New Provider", "https://api.openai.com/v1");
        }
    }
}
