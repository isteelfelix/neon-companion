using System;
using System.Collections.Generic;

namespace NeonCompanion.Runtime.Api.Adapters
{
    public static class ProviderAdapterFactory
    {
        private static readonly Dictionary<string, Func<IProviderAdapter>> Adapters
            = new Dictionary<string, Func<IProviderAdapter>>(StringComparer.OrdinalIgnoreCase)
            {
                { "hermes", () => new HermesAdapter() },
                // Будущие: { "ollama", () => new OllamaAdapter() },
            };

        private static readonly IProviderAdapter DefaultAdapter = new GenericOpenAiAdapter();

        public static IProviderAdapter Create(string providerType)
        {
            if (!string.IsNullOrWhiteSpace(providerType) &&
                Adapters.TryGetValue(providerType, out var factory))
            {
                return factory();
            }
            return DefaultAdapter;
        }
    }
}