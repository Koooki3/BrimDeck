namespace BrimDeck.LiquidGlass;

// A rectangle in device pixels, screen coordinates (right and bottom exclusive), as DesktopSource takes it.
internal readonly record struct RECT(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Right <= Left || Bottom <= Top;

    public RECT Intersect(RECT other) => new(Math.Max(Left, other.Left), Math.Max(Top, other.Top), Math.Min(Right, other.Right), Math.Min(Bottom, other.Bottom));
    public bool Overlaps(RECT other) => !Intersect(other).IsEmpty;
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
    public RECT Offset(int x, int y) => new(Left + x, Top + y, Right + x, Bottom + y);
}
