using System.Collections.Generic;

namespace NeonCompanion.Runtime.Data.Secrets
{
    public interface ISecretStore
    {
        string GetSecret(string id);
        void SetSecret(string id, string secret);
        void DeleteSecret(string id);
        void DeleteSecretsExcept(HashSet<string> retainedIds);
    }
}
