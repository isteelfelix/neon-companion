using System;
using System.Collections.Generic;
using NeonCompanion.Runtime.Core;

namespace NeonCompanion.Runtime.Plugins
{
    public sealed class PluginContext
    {
        private readonly ServiceRegistry _services;
        private readonly PluginConfigStorage _configStorage;
        private readonly string _pluginId;
        private readonly Dictionary<Type, List<Delegate>> _subscriptions;

        internal PluginContext(
            ServiceRegistry services,
            PluginConfigStorage configStorage,
            string pluginId,
            Dictionary<Type, List<Delegate>> subscriptions)
        {
            _services = services;
            _configStorage = configStorage;
            _pluginId = pluginId;
            _subscriptions = subscriptions;
        }

        public bool TryGetService<TService>(out TService service) where TService : class
        {
            return _services.TryGet(out service);
        }

        public TService GetRequiredService<TService>() where TService : class
        {
            return _services.GetRequired<TService>();
        }

        public void Subscribe<T>(Action<T> handler)
        {
            if (handler == null)
                return;

            var type = typeof(T);
            if (!_subscriptions.TryGetValue(type, out var handlers))
            {
                handlers = new List<Delegate>();
                _subscriptions[type] = handlers;
            }

            handlers.Add(handler);
        }

        public void Publish<T>(T payload)
        {
            var type = typeof(T);
            if (!_subscriptions.TryGetValue(type, out var handlers))
                return;

            var snapshot = handlers.ToArray();
            for (int i = 0; i < snapshot.Length; i++)
            {
                if (snapshot[i] is Action<T> callback)
                {
                    try
                    {
                        callback(payload);
                    }
                    catch (Exception ex)
                    {
                        NeonLogger.LogError($"Plugin event handler failed for {type.Name}: {ex}");
                    }
                }
            }
        }

        public T GetConfig<T>(string key)
        {
            return _configStorage.GetConfig<T>(_pluginId, key);
        }

        public void SetConfig<T>(string key, T value)
        {
            _configStorage.SetConfig(_pluginId, key, value);
        }
    }
}
