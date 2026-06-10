using System.Text.RegularExpressions;
using UnityEngine;

namespace NeonCompanion.Runtime.Core
{
    public static class NeonLogger
    {
        /// <summary>
        /// When true (default), obvious secrets (bearer tokens, API keys, token= query params,
        /// api_key/password/secret assignments) are redacted from log output. Driven by the
        /// "Mask logs" setting (AppSettings.maskLogs) via SettingsController.
        /// </summary>
        public static bool MaskSecrets = true;

        // Conservative patterns: over-redaction at worst harms only diagnostic readability.
        private static readonly Regex BearerRegex = new Regex(
            @"[Bb]earer\s+[A-Za-z0-9._\-]+", RegexOptions.Compiled);
        private static readonly Regex ApiKeyRegex = new Regex(
            @"\b(sk|rk|pk)-[A-Za-z0-9._\-]{6,}", RegexOptions.Compiled);
        private static readonly Regex TokenQueryRegex = new Regex(
            @"([?&](?:token|access_token|api_key|apikey)=)[^&\s""]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex AssignmentRegex = new Regex(
            @"(""?(?:api[_-]?key|apikey|password|secret|token)""?\s*[:=]\s*""?)[^""\s,}]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static void Log(string message)
        {
            Debug.Log($"[NeonCompanion] {Scrub(message)}");
        }

        public static void LogWarning(string message)
        {
            Debug.LogWarning($"[NeonCompanion] {Scrub(message)}");
        }

        public static void LogError(string message)
        {
            Debug.LogError($"[NeonCompanion] {Scrub(message)}");
        }

        private static string Scrub(string message)
        {
            if (!MaskSecrets || string.IsNullOrEmpty(message))
                return message;

            string result = message;
            result = BearerRegex.Replace(result, "Bearer ***");
            result = ApiKeyRegex.Replace(result, "$1-***");
            result = TokenQueryRegex.Replace(result, "$1***");
            result = AssignmentRegex.Replace(result, "$1***");
            return result;
        }
    }
}
