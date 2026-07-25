using System;
using System.Collections.Generic;

namespace NeonCompanion.Runtime.Data.Secrets
{
    /// <summary>
    /// Id conventions for the shared secret store. Two namespaces live in it: provider secrets,
    /// stored under the bare provider id and owned by the provider list, and app-scoped secrets
    /// (Hermes gateway token / cached password / persisted session cookie) which belong to the app
    /// and must survive provider-list rewrites.
    /// </summary>
    public static class SecretIds
    {
        /// <summary>Reserved prefix marking an app-scoped (non-provider) secret id.</summary>
        public const string AppScopedPrefix = "hermes_";

        /// <summary>True when <paramref name="id"/> is app-scoped and not owned by a provider row.</summary>
        public static bool IsAppScoped(string id)
        {
            return !string.IsNullOrEmpty(id)
                && id.StartsWith(AppScopedPrefix, StringComparison.Ordinal);
        }
    }

    public interface ISecretStore
    {
        string GetSecret(string id);
        void SetSecret(string id, string secret);
        void DeleteSecret(string id);

        /// <summary>
        /// Prune provider secrets, keeping <paramref name="retainedIds"/>. App-scoped ids
        /// (<see cref="SecretIds.IsAppScoped"/>) are never pruned — the provider list does not own
        /// them, and dropping them would silently sign the user out of the Hermes gateway.
        /// </summary>
        void DeleteSecretsExcept(HashSet<string> retainedIds);
    }
}
