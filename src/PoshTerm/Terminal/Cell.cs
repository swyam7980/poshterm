namespace PoshTerm.Terminal;

// One screen cell: its text (usually a single rune) and visual attributes.
[Flags]
public enum CellFlags : ushort
{
    None = 0,
    Bold = 1 << 0,
    Faint = 1 << 1,
    Italic = 1 << 2,
    Underline = 1 << 3,
    Blink = 1 << 4,
    Inverse = 1 << 5,
    Invisible = 1 << 6,
    Strikethrough = 1 << 7,
    DoubleUnderline = 1 << 8,
    WideLeft = 1 << 9,   // left half of a double-width glyph
    WideRight = 1 << 10, // placeholder right half (not drawn)
    Overline = 1 << 11,
}

// A terminal color: a default sentinel, one of the 256 palette indices, or a true-color RGB.
public readonly struct TermColor : IEquatable<TermColor>
{
    public readonly byte Kind;  // 0 = default, 1 = palette index, 2 = RGB
    public readonly byte R, G, B;

    private TermColor(byte kind, byte r, byte g, byte b) { Kind = kind; R = r; G = g; B = b; }
    public static readonly TermColor Default = new(0, 0, 0, 0);
    public static TermColor Palette(int i) => new(1, (byte)Math.Clamp(i, 0, 255), 0, 0);
    public static TermColor Rgb(byte r, byte g, byte b) => new(2, r, g, b);

    public bool IsDefault => Kind == 0;
    public int Index => R;

    public bool Equals(TermColor o) => Kind == o.Kind && R == o.R && G == o.G && B == o.B;
    public override bool Equals(object? o) => o is TermColor c && Equals(c);
    public override int GetHashCode() => (Kind << 24) | (R << 16) | (G << 8) | B;
}

public struct Cell
{
    public string? Text;        // null = blank (space). Grapheme cluster for combined chars.
    public TermColor Fg;
    public TermColor Bg;
    public CellFlags Flags;

    public readonly bool IsBlank => Text is null && Flags == CellFlags.None && Bg.IsDefault && Fg.IsDefault;

    public void Clear(TermColor bg)
    {
        Text = null;
        Fg = TermColor.Default;
        Bg = bg;
        Flags = CellFlags.None;
    }
}
