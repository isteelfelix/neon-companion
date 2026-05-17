using System.IO;
using UnityEngine;

namespace NeonCompanion.Runtime.Data.Storage
{
    public sealed class JsonFileStorage : IJsonStorage
    {
        public T Load<T>(string path) where T : new()
        {
            if (!File.Exists(path))
            {
                return new T();
            }

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new T();
            }

            var value = JsonUtility.FromJson<T>(json);
            return value ?? new T();
        }

        public void Save<T>(string path, T data)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonUtility.ToJson(data, true);
            File.WriteAllText(path, json);
        }
    }
}
