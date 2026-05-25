using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NeonCompanion.Runtime.Core;
using UnityEngine;

namespace NeonCompanion.Runtime.Plugins
{
    public sealed class PluginManager : MonoBehaviour
    {
        public enum PluginRuntimeStatus
        {
            Loaded,
            Failed,
            Skipped
        }

        public sealed class PluginRuntimeInfo
        {
            public string Id;
            public string Name;
            public string Version;
            public string AssemblyPath;
            public PluginRuntimeStatus Status;
            public string Error;
            public bool HasConfig;
        }

        private readonly List<IPlugin> _loadedPlugins = new List<IPlugin>();
        private readonly List<PluginRuntimeInfo> _pluginInfos = new List<PluginRuntimeInfo>();
        private readonly Dictionary<Type, List<Delegate>> _eventSubscriptions = new Dictionary<Type, List<Delegate>>();

        private ServiceRegistry _services;
        private PluginConfigStorage _configStorage;
        private bool _initialized;

        public IReadOnlyList<PluginRuntimeInfo> Plugins => _pluginInfos;
        public bool HasAnyPluginConfigFiles => _configStorage != null && _configStorage.HasAnyPluginConfigFiles();
        public string PluginsRootPath => _configStorage != null
            ? _configStorage.PluginsRootPath
            : Path.Combine(Application.persistentDataPath, "Plugins");

        public void Initialize(ServiceRegistry services)
        {
            if (_initialized)
                return;

            _initialized = true;
            _services = services;
            _configStorage = new PluginConfigStorage();
            DiscoverAndLoadPlugins();
        }

        private void OnDestroy()
        {
            ShutdownPlugins();
        }

        private void DiscoverAndLoadPlugins()
        {
            try
            {
                Directory.CreateDirectory(PluginsRootPath);
                var dllFiles = Directory.GetFiles(PluginsRootPath, "*.dll", SearchOption.AllDirectories);
                Array.Sort(dllFiles, StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < dllFiles.Length; i++)
                    LoadPluginAssembly(dllFiles[i]);

                NeonLogger.Log($"Plugin discovery completed. Loaded {_loadedPlugins.Count} plugin(s), found {_pluginInfos.Count} runtime record(s).");
            }
            catch (Exception ex)
            {
                NeonLogger.LogError($"Plugin discovery failed: {ex}");
            }
        }

        private void LoadPluginAssembly(string dllPath)
        {
            Assembly assembly;
            try
            {
                var bytes = File.ReadAllBytes(dllPath);
                assembly = Assembly.Load(bytes);
            }
            catch (Exception ex)
            {
                RecordFailure(dllPath, null, null, null, PluginRuntimeStatus.Failed, $"Assembly load failed: {ex.Message}");
                return;
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types;
                NeonLogger.LogWarning($"Plugin assembly type load warnings in {dllPath}: {ex.Message}");
            }
            catch (Exception ex)
            {
                RecordFailure(dllPath, null, null, null, PluginRuntimeStatus.Failed, $"Type discovery failed: {ex.Message}");
                return;
            }

            bool foundPluginType = false;
            for (int i = 0; i < types.Length; i++)
            {
                var type = types[i];
                if (type == null || !typeof(IPlugin).IsAssignableFrom(type) || type.IsAbstract || type.IsInterface)
                    continue;

                foundPluginType = true;
                LoadPluginType(dllPath, type);
            }

            if (!foundPluginType)
                RecordFailure(dllPath, null, null, null, PluginRuntimeStatus.Skipped, "No IPlugin implementations found.");
        }

        private void LoadPluginType(string dllPath, Type type)
        {
            IPlugin plugin;
            try
            {
                plugin = Activator.CreateInstance(type) as IPlugin;
                if (plugin == null)
                {
                    RecordFailure(dllPath, type.FullName, null, null, PluginRuntimeStatus.Skipped, "Type does not create a valid IPlugin instance.");
                    return;
                }
            }
            catch (Exception ex)
            {
                RecordFailure(dllPath, type.FullName, null, null, PluginRuntimeStatus.Failed, $"Plugin instantiation failed: {ex.Message}");
                return;
            }

            string pluginId = string.IsNullOrWhiteSpace(plugin.Id) ? type.FullName : plugin.Id;
            string pluginName = string.IsNullOrWhiteSpace(plugin.Name) ? type.Name : plugin.Name;
            string version = string.IsNullOrWhiteSpace(plugin.Version) ? "0.0.0" : plugin.Version;

            try
            {
                var context = new PluginContext(_services, _configStorage, pluginId, _eventSubscriptions);
                plugin.OnInitialize(context);
                _loadedPlugins.Add(plugin);

                _pluginInfos.Add(new PluginRuntimeInfo
                {
                    Id = pluginId,
                    Name = pluginName,
                    Version = version,
                    AssemblyPath = dllPath,
                    Status = PluginRuntimeStatus.Loaded,
                    Error = string.Empty,
                    HasConfig = _configStorage.HasConfig(pluginId)
                });

                NeonLogger.Log($"Plugin loaded: {pluginId} ({version})");
            }
            catch (Exception ex)
            {
                RecordFailure(dllPath, type.FullName, pluginId, version, PluginRuntimeStatus.Failed, $"Plugin initialization failed: {ex.Message}");
            }
        }

        private void RecordFailure(string dllPath, string typeName, string pluginId, string version, PluginRuntimeStatus status, string error)
        {
            _pluginInfos.Add(new PluginRuntimeInfo
            {
                Id = string.IsNullOrWhiteSpace(pluginId) ? (typeName ?? Path.GetFileNameWithoutExtension(dllPath)) : pluginId,
                Name = string.IsNullOrWhiteSpace(typeName) ? Path.GetFileNameWithoutExtension(dllPath) : typeName,
                Version = string.IsNullOrWhiteSpace(version) ? "unknown" : version,
                AssemblyPath = dllPath,
                Status = status,
                Error = error,
                HasConfig = !string.IsNullOrWhiteSpace(pluginId) && _configStorage != null && _configStorage.HasConfig(pluginId)
            });

            if (status == PluginRuntimeStatus.Failed)
                NeonLogger.LogError($"Plugin load failure ({dllPath}): {error}");
            else
                NeonLogger.LogWarning($"Plugin skipped ({dllPath}): {error}");
        }

        public void ShutdownPlugins()
        {
            if (_loadedPlugins.Count == 0)
                return;

            for (int i = _loadedPlugins.Count - 1; i >= 0; i--)
            {
                try
                {
                    _loadedPlugins[i].OnShutdown();
                }
                catch (Exception ex)
                {
                    NeonLogger.LogError($"Plugin shutdown failed: {ex}");
                }
            }

            _loadedPlugins.Clear();
            _eventSubscriptions.Clear();
        }
    }
}
