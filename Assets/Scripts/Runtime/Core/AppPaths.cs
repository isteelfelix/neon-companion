using System.IO;
using UnityEngine;

namespace NeonCompanion.Runtime.Core
{
    public static class AppPaths
    {
        public static string RootData => Application.persistentDataPath;
        public static string ProvidersFile => Path.Combine(RootData, "providers.json");
        public static string SecretsFile => Path.Combine(RootData, "secrets.json");
        public static string SessionsFile => Path.Combine(RootData, "sessions.json");
        public static string AvatarsFile => Path.Combine(RootData, "avatars.json");
        public static string AvatarAssetsDirectory => Path.Combine(RootData, "Avatars");
        public static string SettingsFile => Path.Combine(RootData, "appsettings.json");

        public static void EnsureDataDirectory()
        {
            if (!Directory.Exists(RootData))
            {
                Directory.CreateDirectory(RootData);
            }
        }
    }
}
