using System;
using System.Collections.Generic;
using UnityEngine;

namespace NeonCompanion.Runtime.UI.UITK
{
    /// <summary>
    /// Current accent palette for C# code that styles elements inline
    /// (context menus, popups) and therefore can't rely on USS variables.
    /// Mirrors the theme classes in Tokens.uss; SettingsController calls
    /// SetTheme when the UI theme loads or changes.
    /// </summary>
    public static class ThemeColors
    {
        public static readonly string[] ThemeIds = { "indigo", "rose", "cyan", "ember", "mono" };

        private static readonly Dictionary<string, Color> AccentById = new Dictionary<string, Color>(StringComparer.Ordinal)
        {
            { "indigo", new Color(0.486f, 0.478f, 0.929f) }, // #7C7AED
            { "rose",   new Color(0.925f, 0.282f, 0.600f) }, // #EC4899
            { "cyan",   new Color(0.133f, 0.827f, 0.933f) }, // #22D3EE
            { "ember",  new Color(0.976f, 0.451f, 0.086f) }, // #F97316
            { "mono",   new Color(0.541f, 0.576f, 0.659f) }  // #8A93A8
        };

        public static string CurrentTheme { get; private set; }
        public static Color Accent { get; private set; }

        /// <summary>Accent at 16% alpha — matches --accent-soft in Tokens.uss.</summary>
        public static Color AccentSoft
        {
            get { return new Color(Accent.r, Accent.g, Accent.b, 0.16f); }
        }

        static ThemeColors()
        {
            CurrentTheme = "indigo";
            Accent = AccentById["indigo"];
        }

        public static string Normalize(string theme)
        {
            if (string.IsNullOrWhiteSpace(theme))
                return "indigo";
            string normalized = theme.Trim().ToLowerInvariant();
            return AccentById.ContainsKey(normalized) ? normalized : "indigo";
        }

        public static void SetTheme(string theme)
        {
            CurrentTheme = Normalize(theme);
            Accent = AccentById[CurrentTheme];
        }

        public static Color GetAccent(string theme)
        {
            return AccentById[Normalize(theme)];
        }
    }
}
