using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace NeonCompanion.Runtime.Plugins
{
    public sealed class PluginConfigStorage
    {
        private const string ConfigFileName = "config.json";

        [Serializable]
        private sealed class ConfigDocument
        {
            public List<Entry> entries = new List<Entry>();
        }

        [Serializable]
        private sealed class Entry
        {
            public string key;
            public string type;
            public string valueJson;
        }

        public string PluginsRootPath => Path.Combine(Application.persistentDataPath, "Plugins");

        public bool HasAnyPluginConfigFiles()
        {
            if (!Directory.Exists(PluginsRootPath))
                return false;

            var files = Directory.GetFiles(PluginsRootPath, ConfigFileName, SearchOption.AllDirectories);
            return files.Length > 0;
        }

        public bool HasConfig(string pluginId)
        {
            return File.Exists(GetConfigPath(pluginId));
        }

        public T GetConfig<T>(string pluginId, string key)
        {
            if (string.IsNullOrWhiteSpace(pluginId) || string.IsNullOrWhiteSpace(key))
                return default;

            var doc = ReadDocument(pluginId);
            if (doc == null || doc.entries == null)
                return default;

            for (int i = 0; i < doc.entries.Count; i++)
            {
                var entry = doc.entries[i];
                if (entry == null || !string.Equals(entry.key, key, StringComparison.Ordinal))
                    continue;

                if (string.IsNullOrWhiteSpace(entry.valueJson))
                    return default;

                try
                {
                    return JsonUtility.FromJson<ConfigValue<T>>(entry.valueJson).value;
                }
                catch
                {
                    return default;
                }
            }

            return default;
        }

        public void SetConfig<T>(string pluginId, string key, T value)
        {
            if (string.IsNullOrWhiteSpace(pluginId) || string.IsNullOrWhiteSpace(key))
                return;

            EnsurePluginDirectory(pluginId);

            var doc = ReadDocument(pluginId) ?? new ConfigDocument();
            if (doc.entries == null)
                doc.entries = new List<Entry>();

            string typeName = typeof(T).AssemblyQualifiedName ?? typeof(T).FullName ?? typeof(T).Name;
            string valueJson = JsonUtility.ToJson(new ConfigValue<T> { value = value });

            for (int i = 0; i < doc.entries.Count; i++)
            {
                var entry = doc.entries[i];
                if (entry != null && string.Equals(entry.key, key, StringComparison.Ordinal))
                {
                    entry.type = typeName;
                    entry.valueJson = valueJson;
                    WriteDocument(pluginId, doc);
                    return;
                }
            }

            doc.entries.Add(new Entry
            {
                key = key,
                type = typeName,
                valueJson = valueJson
            });

            WriteDocument(pluginId, doc);
        }

        private void EnsurePluginDirectory(string pluginId)
        {
            string dir = Path.Combine(PluginsRootPath, pluginId);
            Directory.CreateDirectory(dir);
        }

        private string GetConfigPath(string pluginId)
        {
            string safePluginId = string.IsNullOrWhiteSpace(pluginId) ? "unknown" : pluginId;
            return Path.Combine(PluginsRootPath, safePluginId, ConfigFileName);
        }

        private ConfigDocument ReadDocument(string pluginId)
        {
            string path = GetConfigPath(pluginId);
            if (!File.Exists(path))
                return null;

            try
            {
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                    return new ConfigDocument();

                return JsonUtility.FromJson<ConfigDocument>(json) ?? new ConfigDocument();
            }
            catch
            {
                return new ConfigDocument();
            }
        }

        private void WriteDocument(string pluginId, ConfigDocument document)
        {
            string path = GetConfigPath(pluginId);
            string json = JsonUtility.ToJson(document, true);
            File.WriteAllText(path, json);
        }

        [Serializable]
        private sealed class ConfigValue<T>
        {
            public T value;
        }
    }
}
