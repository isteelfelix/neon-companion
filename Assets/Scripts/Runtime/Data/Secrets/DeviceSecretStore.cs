using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Storage;
using UnityEngine;

namespace NeonCompanion.Runtime.Data.Secrets
{
    public sealed class DeviceSecretStore : ISecretStore
    {
        private readonly IJsonStorage _storage;
        private readonly List<ISecretProtector> _protectors = new List<ISecretProtector>();

        public DeviceSecretStore(IJsonStorage storage)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));

#if UNITY_ANDROID && !UNITY_EDITOR
            AddProtector(new AndroidKeyStoreSecretProtector());
#endif
            AddProtector(new WindowsDpapiSecretProtector());
            AddProtector(new DeviceXorSecretProtector());
        }

        public string GetSecret(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return string.Empty;

            var collection = LoadCollection();
            var item = collection.items.Find(secret => secret.id == id);
            if (item == null || string.IsNullOrWhiteSpace(item.value))
                return string.Empty;

            foreach (var protector in _protectors)
            {
                if (protector.Scheme != item.scheme)
                    continue;

                if (protector.TryUnprotect(item.value, out string secret))
                    return secret;
            }

            return string.Empty;
        }

        public void SetSecret(string id, string secret)
        {
            if (string.IsNullOrWhiteSpace(id))
                return;

            if (string.IsNullOrEmpty(secret))
            {
                DeleteSecret(id);
                return;
            }

            ISecretProtector usedProtector = null;
            string protectedValue = string.Empty;
            foreach (var protector in _protectors)
            {
                if (protector.TryProtect(secret, out protectedValue))
                {
                    usedProtector = protector;
                    break;
                }
            }

            if (usedProtector == null)
                return;

            var collection = LoadCollection();
            var item = collection.items.Find(existing => existing.id == id);
            if (item == null)
            {
                item = new SecretRecord { id = id };
                collection.items.Add(item);
            }

            item.scheme = usedProtector.Scheme;
            item.value = protectedValue;
            SaveCollection(collection);
        }

        public void DeleteSecret(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return;

            var collection = LoadCollection();
            collection.items.RemoveAll(secret => secret.id == id);
            SaveCollection(collection);
        }

        public void DeleteSecretsExcept(HashSet<string> retainedIds)
        {
            var collection = LoadCollection();
            collection.items.RemoveAll(secret => retainedIds == null || !retainedIds.Contains(secret.id));
            SaveCollection(collection);
        }

        private void AddProtector(ISecretProtector protector)
        {
            if (protector != null && protector.IsAvailable)
                _protectors.Add(protector);
        }

        private SecretCollection LoadCollection()
        {
            var collection = _storage.Load<SecretCollection>(AppPaths.SecretsFile);
            collection.items = collection.items ?? new List<SecretRecord>();
            return collection;
        }

        private void SaveCollection(SecretCollection collection)
        {
            _storage.Save(AppPaths.SecretsFile, collection ?? new SecretCollection());
        }

        [Serializable]
        private sealed class SecretCollection
        {
            public List<SecretRecord> items = new List<SecretRecord>();
        }

        [Serializable]
        private sealed class SecretRecord
        {
            public string id;
            public string scheme;
            public string value;
        }

        private interface ISecretProtector
        {
            string Scheme { get; }
            bool IsAvailable { get; }
            bool TryProtect(string plainText, out string protectedValue);
            bool TryUnprotect(string protectedValue, out string plainText);
        }

        private sealed class WindowsDpapiSecretProtector : ISecretProtector
        {
            public string Scheme => "windows-dpapi-v1";

            public bool IsAvailable
            {
                get
                {
                    return (Application.platform == RuntimePlatform.WindowsPlayer ||
                            Application.platform == RuntimePlatform.WindowsEditor) &&
                           FindType("System.Security.Cryptography.ProtectedData") != null &&
                           FindType("System.Security.Cryptography.DataProtectionScope") != null;
                }
            }

            public bool TryProtect(string plainText, out string protectedValue)
            {
                protectedValue = string.Empty;

                try
                {
                    var protectedDataType = FindType("System.Security.Cryptography.ProtectedData");
                    var scopeType = FindType("System.Security.Cryptography.DataProtectionScope");
                    if (protectedDataType == null || scopeType == null)
                        return false;

                    var method = protectedDataType.GetMethod(
                        "Protect",
                        BindingFlags.Public | BindingFlags.Static,
                        null,
                        new[] { typeof(byte[]), typeof(byte[]), scopeType },
                        null);

                    if (method == null)
                        return false;

                    var plainBytes = Encoding.UTF8.GetBytes(plainText ?? string.Empty);
                    var scope = Enum.Parse(scopeType, "CurrentUser");
                    var protectedBytes = (byte[])method.Invoke(null, new object[] { plainBytes, null, scope });
                    protectedValue = Convert.ToBase64String(protectedBytes);
                    return true;
                }
                catch (Exception ex)
                {
                    NeonLogger.LogWarning($"DPAPI secret protection failed, falling back if available: {ex.Message}");
                    return false;
                }
            }

            public bool TryUnprotect(string protectedValue, out string plainText)
            {
                plainText = string.Empty;

                try
                {
                    var protectedDataType = FindType("System.Security.Cryptography.ProtectedData");
                    var scopeType = FindType("System.Security.Cryptography.DataProtectionScope");
                    if (protectedDataType == null || scopeType == null)
                        return false;

                    var method = protectedDataType.GetMethod(
                        "Unprotect",
                        BindingFlags.Public | BindingFlags.Static,
                        null,
                        new[] { typeof(byte[]), typeof(byte[]), scopeType },
                        null);

                    if (method == null)
                        return false;

                    var protectedBytes = Convert.FromBase64String(protectedValue);
                    var scope = Enum.Parse(scopeType, "CurrentUser");
                    var plainBytes = (byte[])method.Invoke(null, new object[] { protectedBytes, null, scope });
                    plainText = Encoding.UTF8.GetString(plainBytes);
                    return true;
                }
                catch (Exception ex)
                {
                    NeonLogger.LogWarning($"DPAPI secret read failed: {ex.Message}");
                    return false;
                }
            }

            private static Type FindType(string typeName)
            {
                var type = Type.GetType(typeName);
                if (type != null)
                    return type;

                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    type = assembly.GetType(typeName);
                    if (type != null)
                        return type;
                }

                try
                {
                    var assembly = Assembly.Load("System.Security");
                    return assembly.GetType(typeName);
                }
                catch
                {
                    return null;
                }
            }
        }

        private sealed class DeviceXorSecretProtector : ISecretProtector
        {
            public string Scheme => "device-xor-v1";
            public bool IsAvailable => true;

            public bool TryProtect(string plainText, out string protectedValue)
            {
                protectedValue = Convert.ToBase64String(XorBytes(Encoding.UTF8.GetBytes(plainText ?? string.Empty)));
                return true;
            }

            public bool TryUnprotect(string protectedValue, out string plainText)
            {
                plainText = string.Empty;

                try
                {
                    var bytes = Convert.FromBase64String(protectedValue);
                    plainText = Encoding.UTF8.GetString(XorBytes(bytes));
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            private static byte[] XorBytes(byte[] input)
            {
                var key = Encoding.UTF8.GetBytes(BuildDeviceKey());
                if (key.Length == 0)
                    key = Encoding.UTF8.GetBytes("neon-companion");

                var output = new byte[input.Length];
                for (int i = 0; i < input.Length; i++)
                    output[i] = (byte)(input[i] ^ key[i % key.Length]);

                return output;
            }

            private static string BuildDeviceKey()
            {
                return $"{SystemInfo.deviceUniqueIdentifier}|{Application.identifier}|neon-companion";
            }
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private sealed class AndroidKeyStoreSecretProtector : ISecretProtector
        {
            private const string Alias = "neon_companion_api_keys";

            public string Scheme => "android-keystore-aesgcm-v1";
            public bool IsAvailable => Application.platform == RuntimePlatform.Android;

            public bool TryProtect(string plainText, out string protectedValue)
            {
                protectedValue = string.Empty;

                try
                {
                    var cipher = new AndroidJavaClass("javax.crypto.Cipher")
                        .CallStatic<AndroidJavaObject>("getInstance", "AES/GCM/NoPadding");
                    int encryptMode = new AndroidJavaClass("javax.crypto.Cipher").GetStatic<int>("ENCRYPT_MODE");
                    cipher.Call("init", encryptMode, GetOrCreateKey());

                    var encrypted = cipher.Call<byte[]>("doFinal", Encoding.UTF8.GetBytes(plainText ?? string.Empty));
                    var iv = cipher.Call<byte[]>("getIV");
                    protectedValue = $"{Convert.ToBase64String(iv)}:{Convert.ToBase64String(encrypted)}";
                    return true;
                }
                catch (Exception ex)
                {
                    NeonLogger.LogWarning($"Android Keystore secret protection failed: {ex.Message}");
                    return false;
                }
            }

            public bool TryUnprotect(string protectedValue, out string plainText)
            {
                plainText = string.Empty;

                try
                {
                    var parts = (protectedValue ?? string.Empty).Split(':');
                    if (parts.Length != 2)
                        return false;

                    var iv = Convert.FromBase64String(parts[0]);
                    var encrypted = Convert.FromBase64String(parts[1]);

                    var cipher = new AndroidJavaClass("javax.crypto.Cipher")
                        .CallStatic<AndroidJavaObject>("getInstance", "AES/GCM/NoPadding");
                    var spec = new AndroidJavaObject("javax.crypto.spec.GCMParameterSpec", 128, iv);
                    int decryptMode = new AndroidJavaClass("javax.crypto.Cipher").GetStatic<int>("DECRYPT_MODE");
                    cipher.Call("init", decryptMode, GetOrCreateKey(), spec);

                    var plainBytes = cipher.Call<byte[]>("doFinal", encrypted);
                    plainText = Encoding.UTF8.GetString(plainBytes);
                    return true;
                }
                catch (Exception ex)
                {
                    NeonLogger.LogWarning($"Android Keystore secret read failed: {ex.Message}");
                    return false;
                }
            }

            private static AndroidJavaObject GetOrCreateKey()
            {
                var keyStore = new AndroidJavaClass("java.security.KeyStore")
                    .CallStatic<AndroidJavaObject>("getInstance", "AndroidKeyStore");
                keyStore.Call("load", new object[] { null });

                if (!keyStore.Call<bool>("containsAlias", Alias))
                {
                    var keyProperties = new AndroidJavaClass("android.security.keystore.KeyProperties");
                    int purposes = keyProperties.GetStatic<int>("PURPOSE_ENCRYPT") |
                                   keyProperties.GetStatic<int>("PURPOSE_DECRYPT");

                    string blockMode = keyProperties.GetStatic<string>("BLOCK_MODE_GCM");
                    string padding = keyProperties.GetStatic<string>("ENCRYPTION_PADDING_NONE");

                    var builder = new AndroidJavaObject("android.security.keystore.KeyGenParameterSpec$Builder", Alias, purposes);
                    builder.Call<AndroidJavaObject>("setBlockModes", new object[] { new[] { blockMode } });
                    builder.Call<AndroidJavaObject>("setEncryptionPaddings", new object[] { new[] { padding } });

                    var keyGenerator = new AndroidJavaClass("javax.crypto.KeyGenerator")
                        .CallStatic<AndroidJavaObject>("getInstance", "AES", "AndroidKeyStore");
                    keyGenerator.Call("init", builder.Call<AndroidJavaObject>("build"));
                    keyGenerator.Call<AndroidJavaObject>("generateKey");
                }

                return keyStore.Call<AndroidJavaObject>("getKey", Alias, null);
            }
        }
#endif
    }
}
