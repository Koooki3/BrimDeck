namespace BrimDeck.Core;

// The area an opening panel is about to cover, as a coarse grid (V3 V306, V307): the shrunk capture (at most 24 x 24 pixels,
// each already an area average) split into Columns x Rows cells, each with its median, tenth and ninetieth percentiles, mean
// colour and share of the area. From the cells: how much of the area is clearly light or clearly dark, and how far the cells
// can be trusted (a cell whose own pixels spread widely - a fine pattern, an edge - says little; a capture that may already
// show the panel's own glass says less). A white page under an orange bar is mostly light by area even when one middle mean
// of all its pixels falls short of a threshold; a checkerboard is neither.
public sealed class GlassSceneSnapshot
{
    public const int Columns = 6, Rows = 4;
    // A cell is light at or above this median luma, dark at or below Dark (its middle range is neither).
    public const double Light = .7, Dark = .3;
    // A capture that may show our own glass (taken after the panel's first frame) counts for this share of a clean one.
    public const double OccludedTrust = .5;

    public readonly record struct Cell(double Median, double P10, double P90, double R, double G, double B, double Weight);

    public IReadOnlyList<Cell> Cells { get; }
    public double LightShare { get; }
    public double DarkShare { get; }
    // The mean of the middle range of all the pixels (GlassAppearance.SortedRangeMean), what the look's decision measured before.
    public double MiddleMean { get; }
    // The area-weighted spread (p90 - p10) inside the cells, 0..1.
    public double Spread { get; }
    public double Confidence { get; }
    public bool OcclusionPossible { get; }

    private GlassSceneSnapshot(Cell[] cells, double middle, bool occlusion)
    {
        Cells = cells;
        MiddleMean = middle;
        OcclusionPossible = occlusion;
        foreach (var cell in cells)
        {
            if (cell.Median >= Light) LightShare += cell.Weight;
            else if (cell.Median <= Dark) DarkShare += cell.Weight;
            Spread += (cell.P90 - cell.P10) * cell.Weight;
        }
        double coverage = cells.Length >= Columns * Rows ? 1 : cells.Length / (double)(Columns * Rows);
        Confidence = Math.Clamp(1 - 1.25 * Spread, 0, 1) * coverage * (occlusion ? OccludedTrust : 1);
    }

    // The same grid, from a capture that may already show the panel's own glass.
    public GlassSceneSnapshot Occluded() => OcclusionPossible ? this : new([.. Cells], MiddleMean, true);

    public static double Luma(byte r, byte g, byte b) => (.2126 * r + .7152 * g + .0722 * b) / 255;

    // rgb: width x height pixels, row by row, three bytes (r, g, b) each. Null when there is nothing to read.
    public static GlassSceneSnapshot? FromPixels(ReadOnlySpan<byte> rgb, int width, int height, bool occlusionPossible = false)
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
        return new GlassSceneSnapshot(cells, GlassAppearance.SortedRangeMean(all), occlusionPossible);
    }

    public override string ToString() => FormattableString.Invariant(
        $"light={LightShare:0.00} dark={DarkShare:0.00} middle={MiddleMean:0.000} spread={Spread:0.00} confidence={Confidence:0.00} occlusion={OcclusionPossible}");
}
