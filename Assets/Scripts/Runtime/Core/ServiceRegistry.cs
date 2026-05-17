using System;
using System.Collections.Generic;

namespace NeonCompanion.Runtime.Core
{
    public sealed class ServiceRegistry
    {
        private readonly Dictionary<Type, object> _services = new Dictionary<Type, object>();

        public void Register<TService>(TService service) where TService : class
        {
            if (service == null)
            {
                throw new ArgumentNullException(nameof(service));
            }

            _services[typeof(TService)] = service;
        }

        public bool TryGet<TService>(out TService service) where TService : class
        {
            if (_services.TryGetValue(typeof(TService), out var instance))
            {
                service = instance as TService;
                return service != null;
            }

            service = null;
            return false;
        }

        public TService GetRequired<TService>() where TService : class
        {
            if (TryGet<TService>(out var service))
            {
                return service;
            }

            throw new InvalidOperationException($"Service is not registered: {typeof(TService).Name}");
        }
    }
}
