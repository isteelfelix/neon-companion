using System;
using NeonCompanion.Runtime.Data.Models;

namespace NeonCompanion.Runtime.Core
{
    public static class ProviderValidator
    {
        public static void Validate(ProviderConfig provider)
        {
            if (provider == null)
                throw new ArgumentNullException(nameof(provider));

            if (string.IsNullOrWhiteSpace(provider.baseUrl))
                throw new InvalidOperationException("Base URL is not configured.");

            if (string.IsNullOrWhiteSpace(provider.model))
                throw new InvalidOperationException("Model is not configured.");

            // API key can be empty for local models, so we don't require it
        }
    }
}