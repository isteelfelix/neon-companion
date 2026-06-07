using UnityEngine;

namespace NeonCompanion.Runtime.Terminal.Emulator
{
    /// <summary>
    /// Resolves a <see cref="TerminalColor"/> to a concrete <see cref="Color32"/>: the
    /// standard 16 ANSI colors, the 6×6×6 color cube and grayscale ramp (indices 16..255),
    /// truecolor passthrough, and theme-driven default foreground/background. The default
    /// colors are wired to the app's terminal theme by the renderer.
    /// </summary>
    public sealed class TerminalPalette
    {
        private readonly Color32[] _table = new Color32[256];

        public Color32 DefaultForeground;
        public Color32 DefaultBackground;

        public TerminalPalette()
        {
            DefaultForeground = new Color32(217, 224, 235, 255); // ~ #D9E0EB
            DefaultBackground = new Color32(26, 28, 36, 255);    // ~ #1A1C24
            BuildTable();
        }

        public Color32 Resolve(TerminalColor color, bool background)
        {
            switch (color.Mode)
            {
                case TerminalColorMode.Rgb:
                    return new Color32(color.R, color.G, color.B, 255);
                case TerminalColorMode.Indexed:
                    int idx = color.Index;
                    if (idx < 0) idx = 0;
                    if (idx > 255) idx = 255;
                    return _table[idx];
                case TerminalColorMode.Default:
                default:
                    return background ? DefaultBackground : DefaultForeground;
            }
        }

        /// <summary>The bright (8..15) variant of a standard 0..7 indexed color, for bold text.</summary>
        public static bool TryBrighten(ref TerminalColor color)
        {
            if (color.Mode == TerminalColorMode.Indexed && color.Index >= 0 && color.Index <= 7)
            {
                color = TerminalColor.Indexed(color.Index + 8);
                return true;
            }
            return false;
        }

        private void BuildTable()
        {
            // 0..15 — standard + bright ANSI (xterm-ish).
            _table[0] = Rgb(0x1a, 0x1c, 0x24);   // black (matches default bg-ish)
            _table[1] = Rgb(0xe0, 0x6c, 0x75);   // red
            _table[2] = Rgb(0x98, 0xc3, 0x79);   // green
            _table[3] = Rgb(0xe5, 0xc0, 0x7b);   // yellow
            _table[4] = Rgb(0x61, 0xaf, 0xef);   // blue
            _table[5] = Rgb(0xc6, 0x78, 0xdd);   // magenta
            _table[6] = Rgb(0x56, 0xb6, 0xc2);   // cyan
            _table[7] = Rgb(0xab, 0xb2, 0xbf);   // white
            _table[8] = Rgb(0x5c, 0x63, 0x70);   // bright black (gray)
            _table[9] = Rgb(0xef, 0x59, 0x6f);   // bright red
            _table[10] = Rgb(0x89, 0xca, 0x78);  // bright green
            _table[11] = Rgb(0xe5, 0xc0, 0x7b);  // bright yellow
            _table[12] = Rgb(0x61, 0xaf, 0xef);  // bright blue
            _table[13] = Rgb(0xd5, 0x5f, 0xde);  // bright magenta
            _table[14] = Rgb(0x56, 0xb6, 0xc2);  // bright cyan
            _table[15] = Rgb(0xff, 0xff, 0xff);  // bright white

            // 16..231 — 6×6×6 color cube.
            int[] steps = new int[] { 0, 95, 135, 175, 215, 255 };
            int index = 16;
            for (int r = 0; r < 6; r++)
            {
                for (int g = 0; g < 6; g++)
                {
                    for (int b = 0; b < 6; b++)
                    {
                        _table[index] = Rgb(steps[r], steps[g], steps[b]);
                        index++;
                    }
                }
            }

            // 232..255 — grayscale ramp.
            for (int i = 0; i < 24; i++)
            {
                int v = 8 + i * 10;
                _table[232 + i] = Rgb(v, v, v);
            }
        }

        private static Color32 Rgb(int r, int g, int b)
        {
            return new Color32((byte)r, (byte)g, (byte)b, 255);
        }
    }
}
