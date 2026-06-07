using System;

namespace NeonCompanion.Runtime.Terminal.Emulator
{
    public enum TerminalColorMode
    {
        Default = 0,
        Indexed = 1,
        Rgb = 2
    }

    /// <summary>
    /// A terminal color: either the terminal default (resolved by the renderer to its
    /// theme fg/bg), a palette index (0..255), or a 24-bit truecolor value. Kept free of
    /// any Unity dependency so the emulator stays unit-testable; the renderer resolves it
    /// to an actual color via the palette.
    /// </summary>
    [Serializable]
    public struct TerminalColor
    {
        public TerminalColorMode Mode;
        public int Index;
        public byte R;
        public byte G;
        public byte B;

        public bool IsDefault
        {
            get { return Mode == TerminalColorMode.Default; }
        }

        public static TerminalColor Default()
        {
            TerminalColor c = new TerminalColor();
            c.Mode = TerminalColorMode.Default;
            return c;
        }

        public static TerminalColor Indexed(int index)
        {
            TerminalColor c = new TerminalColor();
            c.Mode = TerminalColorMode.Indexed;
            c.Index = index;
            return c;
        }

        public static TerminalColor Rgb(byte r, byte g, byte b)
        {
            TerminalColor c = new TerminalColor();
            c.Mode = TerminalColorMode.Rgb;
            c.R = r;
            c.G = g;
            c.B = b;
            return c;
        }
    }
}
