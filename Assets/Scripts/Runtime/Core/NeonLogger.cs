using UnityEngine;

namespace NeonCompanion.Runtime.Core
{
    public static class NeonLogger
    {
        public static void Log(string message)
        {
            Debug.Log($"[NeonCompanion] {message}");
        }

        public static void LogWarning(string message)
        {
            Debug.LogWarning($"[NeonCompanion] {message}");
        }

        public static void LogError(string message)
        {
            Debug.LogError($"[NeonCompanion] {message}");
        }
    }
}