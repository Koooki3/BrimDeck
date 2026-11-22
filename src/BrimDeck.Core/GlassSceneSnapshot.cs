namespace BrimDeck.Core;

// The area an opening panel is about to cover, as a coarse grid (V3 V306, V307): the shrunk capture (at most 24 x 24 pixels,
// each already an area average) split into Columns x Rows cells, each with its median, tenth and ninetieth percentiles, mean
// colour and share of the area. From the cells: how much of the area is clearly light or clearly dark, and how far the cells
// can be trusted (a cell whose own pixels spread widely - a fine pattern, an edge - says little). A white page under an orange
// bar is mostly light by area even when one middle mean of all its pixels falls short of a threshold; a checkerboard is neither.
//   A capture taken after the panel's first frame may already show the panel's own glass on the side it grows in from (V4 F01):
// it is judged on the part of the area the panel had not reached (Uncovered), and is Unknown when too little of it is left.
// (It used to be trusted half, under the trust a turn needs, so such a capture could never turn the look.)
public sealed class GlassSceneSnapshot
{
    public const int Columns = 6, Rows = 4;
    // A cell is light at or above this median luma, dark at or below Dark (its middle range is neither).
    public const double Light = .7, Dark = .3;
    // The least share of the area a capture must show past the panel to be judged at all (Uncovered).
    public const double VisibleLeast = .5;
    // The bright and dark tails (BrightTail, DarkTail) as AcrylicProbe reads them: the 95th and 5th percentiles.
    public const double Tail = .95;

    // The side of the area nearest the island: the one the opening panel grows in from.
    public enum Side { Top, Bottom, Left, Right }

    public readonly record struct Cell(double Median, double P10, double P90, double R, double G, double B, double Weight);

    public IReadOnlyList<Cell> Cells { get; }
    public double LightShare { get; }
    public double DarkShare { get; }
    // The mean of the middle range of all the pixels (GlassAppearance.SortedRangeMean), what the look's decision measured before.
    public double MiddleMean { get; }
    // The luma tails of all the pixels (the 95th and 5th percentiles), what the veils answer for.
    public double BrightTail { get; }
    public double DarkTail { get; }
    // The area-weighted spread (p90 - p10) inside the cells, 0..1.
    public double Spread { get; }
    public double Confidence { get; }
    // The share of the area read (1: all of it; less: the part the panel had not reached, Uncovered).
    public double Visible { get; }

    private readonly byte[] _rgb;
    private readonly int _width, _height;

    private GlassSceneSnapshot(Cell[] cells, double middle, double bright, double dark, byte[] rgb, int width, int height, double visible)
    {
        Cells = cells;
        MiddleMean = middle;
        BrightTail = bright;
        DarkTail = dark;
        _rgb = rgb; _width = width; _height = height;
        Visible = visible;
        foreach (var cell in cells)
        {
            if (cell.Median >= Light) LightShare += cell.Weight;
            else if (cell.Median <= Dark) DarkShare += cell.Weight;
            Spread += (cell.P90 - cell.P10) * cell.Weight;
        }
        double coverage = cells.Length >= Columns * Rows ? 1 : cells.Length / (double)(Columns * Rows);
        Confidence = Math.Clamp(1 - 1.25 * Spread, 0, 1) * coverage;
    }

    // The part of the area beyond what the panel already covered when the capture began (covered: the share of the area's depth
    // from the near side, 0..1), as a grid of its own; this one when the panel had not reached the area; null (Unknown) when less
    // than VisibleLeast of the area is left.
    public GlassSceneSnapshot? Uncovered(Side near, double covered)
    {
        double share = double.IsFinite(covered) ? Math.Clamp(covered, 0, 1) : 1;
        if (share <= 0) return this;
        bool across = near is Side.Left or Side.Right;
        int depth = across ? _width : _height;
        int hidden = Math.Min(depth, (int)Math.Ceiling(share * depth - 1e-9));
        int left = depth - hidden;
        if (left < 1 || left / (double)depth * Visible < VisibleLeast) return null;
        int width = across ? left : _width, height = across ? _height : left;
        int x0 = near == Side.Left ? hidden : 0, y0 = near == Side.Top ? hidden : 0;
        var rgb = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
            Array.Copy(_rgb, ((y0 + y) * _width + x0) * 3, rgb, y * width * 3, width * 3);
        return FromPixels(rgb, width, height, Visible * left / depth);
    }

    public static double Luma(byte r, byte g, byte b) => (.2126 * r + .7152 * g + .0722 * b) / 255;

    // rgb: width x height pixels, row by row, three bytes (r, g, b) each. Null when there is nothing to read.
    public static GlassSceneSnapshot? FromPixels(ReadOnlySpan<byte> rgb, int width, int height) => FromPixels(rgb, width, height, 1);

    private static GlassSceneSnapshot? FromPixels(ReadOnlySpan<byte> rgb, int width, int height, double visible)
    {
        if (width < 1 || height < 1 || rgb.Length < width * height * 3) return null;
        int columns = Math.Min(Columns, width), rows = Math.Min(Rows, height);
        var cells = new Cell[columns * rows];
        var all = new double[width * height];
        var lumas = new double[width * height];
        int total = width * height;
        for (int row = 0, k = 0; row < rows; row++)
        for (int column = 0; column < columns; column++, k++)
        {
            int x0 = column * width / columns, x1 = (column + 1) * width / columns;
            int y0 = row * height / rows, y1 = (row + 1) * height / rows;
            int n = 0; double r = 0, g = 0, b = 0;
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int at = (y * width + x) * 3;
                r += rgb[at]; g += rgb[at + 1]; b += rgb[at + 2];
                lumas[n++] = Luma(rgb[at], rgb[at + 1], rgb[at + 2]);
            }
            Array.Sort(lumas, 0, n);
            cells[k] = new Cell(lumas[n / 2], lumas[(int)(n * .1)], lumas[Math.Min(n - 1, (int)(n * .9))],
                r / n / 255, g / n / 255, b / n / 255, n / (double)total);
        }
        for (int i = 0; i < total; i++) { int at = i * 3; all[i] = Luma(rgb[at], rgb[at + 1], rgb[at + 2]); }
        Array.Sort(all);
        return new GlassSceneSnapshot(cells, GlassAppearance.SortedRangeMean(all), all[Math.Min(total - 1, (int)(total * Tail))],
            all[(int)(total * (1 - Tail))], rgb[..(total * 3)].ToArray(), width, height, visible);
    }

    public override string ToString() => FormattableString.Invariant(
        $"light={LightShare:0.00} dark={DarkShare:0.00} middle={MiddleMean:0.000} spread={Spread:0.00} confidence={Confidence:0.00} visible={Visible:0.00}");
}
