using System;
using UnityEngine;

namespace NeonCompanion.Runtime.Core
{
    /// <summary>
    /// Opens URLs that originate from untrusted content (assistant messages, rendered markdown).
    /// Only http/https/mailto are allowed through to <see cref="Application.OpenURL"/>; any other
    /// scheme (file://, javascript:, custom app schemes, etc.) is refused so a crafted message
    /// cannot launch local files or other handlers when the user taps a link.
    /// App-initiated opens (e.g. opening a local folder) should call Application.OpenURL directly.
    /// </summary>
    public static class SafeLinkOpener
    {
        public static void Open(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return;

            string trimmed = url.Trim();

            if (StartsWithScheme(trimmed, "http://") ||
                StartsWithScheme(trimmed, "https://") ||
                StartsWithScheme(trimmed, "mailto:"))
            {
                Application.OpenURL(trimmed);
                return;
            }

            NeonLogger.LogWarning("Refused to open link with disallowed scheme: " + trimmed);
        }

        private static bool StartsWithScheme(string value, string scheme)
        {
            return value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase);
        }
    }
}
