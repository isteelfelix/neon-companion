using System;

namespace NeonCompanion.Runtime.Terminal.Emulator
{
    /// <summary>Per-cell rendition flags set via SGR sequences.</summary>
    [Flags]
    public enum CellAttributes
    {
        None = 0,
        Bold = 1 << 0,
        Dim = 1 << 1,
        Italic = 1 << 2,
        Underline = 1 << 3,
        Blink = 1 << 4,
        Inverse = 1 << 5,
        Hidden = 1 << 6,
        Strikethrough = 1 << 7
    }

    /// <summary>
    /// One character cell in the terminal grid: the glyph plus its foreground/background
    /// color and rendition attributes. A value type — a screen is a dense array of these.
    /// NOTE (v1 limitation): <see cref="Char"/> is a single BMP char, so non-BMP emoji and
    /// double-width CJK glyphs are not yet modeled as wide cells.
    /// </summary>
    public struct TerminalCell
    {
        public char Char;
        public TerminalColor Foreground;
        public TerminalColor Background;
        public CellAttributes Attributes;

        public static TerminalCell Blank(TerminalColor fg, TerminalColor bg)
        {
            TerminalCell cell = new TerminalCell();
            cell.Char = ' ';
            cell.Foreground = fg;
            cell.Background = bg;
            cell.Attributes = CellAttributes.None;
            return cell;
        }
    }
}
