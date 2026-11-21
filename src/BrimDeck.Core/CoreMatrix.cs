namespace BrimDeck.Core;

// How the processor's cores are drawn: as columns, as a dot matrix (one square per logical processor), or by Auto, columns while
// they stay readable and the matrix once the count would make them thinner than that (a machine with 192 threads).
// Saved as a number; a missing or unknown value is Auto.
public enum CoreDisplay { Auto = 0, Columns = 1, Matrix = 2 }

// One placement of a core matrix: the squares fill column by column (top to bottom, then the next column to the right), so the
// kinds of core, fastest first, read as stripes from left to right just as the columns do. Every size is in DIPs on whole device
// pixels. Origin is where the grid starts inside the rect it was fitted to (the grid is centred vertically, left-aligned).
// Fits is false when even the smallest squares asked for would not fit the rect: the grid is then wider than the rect.
public readonly record struct MatrixGrid(int Count, int Rows, int Columns, double Cell, double Gap, double Width, double Height, double X, double Y, bool Fits,
    double TailOffset = 0, bool Spread = false)
{
    public static readonly MatrixGrid Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0, true);
    public double Pitch => Cell + Gap;

    // The left and top edges of a core's square.
    public (double X, double Y) Origin(int index)
    {
        int longColumns = Spread ? Count % Columns : 0;
        int full = longColumns * Rows;
        int stride = Spread && index >= full ? Rows - 1 : Rows;
        int offset = stride == Rows ? index : index - full;
        int column = offset / stride + (stride == Rows ? 0 : longColumns);
        return (X + column * Pitch, Y + offset % stride * Pitch + (column == Columns - 1 ? TailOffset : 0));
    }

    // The core under a point (in the rect's own coordinates), or -1. The gap belongs to the square before it, so no point of the
    // grid is dead; the empty places of the last column and anything outside the grid give -1.
    public int CellAt(double x, double y)
    {
        if (Count == 0 || Rows == 0 || Pitch <= 0 || !double.IsFinite(x) || !double.IsFinite(y)) return -1;
        double px = x - X, py = y - Y;
        if (px < 0 || py < 0 || px >= Width || py >= Height) return -1;
        int column = Math.Min((int)(px / Pitch), Columns - 1);
        int shortBefore = Spread ? Math.Max(0, column - Count % Columns) : 0;
        int start = column * Rows - shortBefore;
        int rows = Spread && column >= Count % Columns ? Rows - 1 : Math.Min(Rows, Count - start);
        if (column == Columns - 1) py -= TailOffset;
        return py >= 0 && py < rows * Pitch - Gap ? start + Math.Min((int)(py / Pitch), rows - 1) : -1;
    }
}

public static class CoreMatrix
{
    public const int MaxPreferredColumns = 64;
    public static int NormalizeColumns(int columns) => columns is >= 0 and <= MaxPreferredColumns ? columns : 0;
    // Auto keeps the columns while each is at least this many device pixels wide.
    public const double ReadableColumn = 2;
    // The load is shown in this many brightness steps (0 .. Steps - 1) of the core's color over the empty cell: a cell steps
    // between them as the eased load moves, so no brush is made per frame (the strip draws every frame while values ease).
    public const int Steps = 8;
    // The share of the color shown at each step: the idle step is still visibly a cell, the busiest the whole color.
    public const double IdleAlpha = .22;

    public static int Step(double share) => (int)Math.Round(Math.Clamp(double.IsFinite(share) ? share : 0, 0, 1) * (Steps - 1));
    public static byte Alpha(int step) => (byte)Math.Round(255 * (IdleAlpha + (1 - IdleAlpha) * Math.Clamp(step, 0, Steps - 1) / (Steps - 1)));

    // The columns are drawn at least this many DIPs wide (the graph clamps to it), so narrower than this they would overflow.
    public const double LeastColumnDip = 1;
    // The same for a width in DIPs and the device pixels per DIP: Auto also takes the matrix where the clamp would overflow the graph.
    public static bool Use(CoreDisplay display, double columnDip, double pixelsPerDip) =>
        Use(display, columnDip * pixelsPerDip) || display == CoreDisplay.Auto && columnDip < LeastColumnDip;

    // Whether the matrix draws the cores, for the setting and the width, in device pixels, the columns would have.
    public static bool Use(CoreDisplay display, double columnDevicePixels) => display switch
    {
        CoreDisplay.Matrix => true,
        CoreDisplay.Columns => false,
        _ => columnDevicePixels < ReadableColumn
    };

    // The gap between squares of this pitch (device pixels): none below 3, so the smallest squares touch; one up to 7; then
    // a seventh of the pitch.
    public static int GapPixels(int pitch) => pitch < 3 ? 0 : pitch < 8 ? 1 : (int)Math.Round(pitch / 7d);

    // Fit a requested column count while cells stay individually readable. Auto favors the largest cells, allowing a nearby
    // full rectangle; otherwise its partial last column is centered. All geometry and hit testing share this placement.
    public static MatrixGrid Fit(int count, double width, double height, double dpi, double maxCell = double.PositiveInfinity, int minPitch = 1,
        int preferredColumns = 0)
    {
        if (count <= 0) return MatrixGrid.Empty;
        if (!double.IsFinite(dpi) || dpi <= 0) dpi = 1;
        minPitch = Math.Max(1, minPitch);
        int wp = Pixels(width, dpi), hp = Pixels(height, dpi);
        int capCell = double.IsFinite(maxCell) ? Math.Max(1, Pixels(maxCell, dpi)) : int.MaxValue;
        int columnsWanted = Math.Min(count, NormalizeColumns(preferredColumns));
        MatrixGrid? largest = null, balanced = null;
        for (int pitch = Math.Min(Math.Min(wp, hp), capCell) * 2; pitch >= minPitch; pitch--)
        {
            int gap = GapPixels(pitch), cell = pitch - gap;
            if (cell > capCell || cell > hp || cell > wp) continue;
            int maxRows = Math.Min(count, (hp + gap) / pitch);
            int maxCols = Math.Min(count, (wp + gap) / pitch);
            if (maxRows == 0 || maxCols == 0 || maxRows * (long)maxCols < count) continue;
            if (columnsWanted > 0 && cell >= 2 && columnsWanted <= maxCols)
            {
                int rowsWanted = DivideUp(count, columnsWanted);
                if (rowsWanted <= maxRows)
                    return Make(count, rowsWanted, columnsWanted, pitch, gap, dpi, hp, fits: true, spread: count % columnsWanted != 0);
            }
            if (largest is null)
            {
                int columns = DivideUp(count, maxRows);
                largest = Make(count, DivideUp(count, columns), columns, pitch, gap, dpi, hp, fits: true);
            }
            // In a very shallow strip an extra row must not halve already-small cells.
            int bestCell = (int)Math.Round(largest.Value.Cell * dpi);
            if (balanced is null && (cell == bestCell || cell >= Math.Max(3, (bestCell * 3 + 4) / 5)))
            {
                for (int rows = maxRows; rows >= DivideUp(count, maxCols); rows--)
                    if (count % rows == 0)
                    {
                        balanced = Make(count, rows, count / rows, pitch, gap, dpi, hp, fits: true);
                        break;
                    }
            }
            if ((columnsWanted == 0 || cell < 2) &&
                (balanced is not null || cell < Math.Max(3, (bestCell * 3 + 4) / 5))) break;
        }
        if ((balanced ?? largest) is { } fit) return fit;
        int small = minPitch, smallGap = GapPixels(small);
        int fallbackColumns = DivideUp(count, Math.Clamp((hp + smallGap) / small, 1, count));
        return Make(count, DivideUp(count, fallbackColumns), fallbackColumns, small, smallGap, dpi, hp, fits: false);
    }

    // The strip's matrix: the room the columns have (56 wide and 14 high at the strip's scale), widening only when the squares
    // would otherwise be under 2 device pixels.
    public static MatrixGrid Strip(int count, double scale, double dpi, int preferredColumns = 0)
        => Fit(count, Math.Round(CompactMonitor.ColumnsRoom * SafeScale(scale)), Math.Round(CompactMonitor.ColumnsHeight * SafeScale(scale)), dpi,
            minPitch: 2, preferredColumns: preferredColumns);

    private static double SafeScale(double scale) => double.IsFinite(scale) && scale > 0 ? scale : 1;
    private static int Pixels(double size, double dpi) => double.IsFinite(size) && size > 0 ? (int)Math.Min(Math.Floor(size * dpi + 1e-6), 1 << 20) : 0;

    private static int DivideUp(int count, int divisor) => 1 + (count - 1) / divisor;

    private static MatrixGrid Make(int count, int rows, int columns, int pitch, int gap, double dpi, int hp, bool fits, bool spread = false)
    {
        int height = rows * pitch - gap;
        long width = (long)columns * pitch - gap;
        int top = fits || height <= hp ? (hp - height) / 2 : 0;
        int missing = spread ? 0 : (int)((long)rows * columns - count);
        return new(count, rows, columns, (pitch - gap) / dpi, gap / dpi, width / dpi, height / dpi, 0, top / dpi, fits,
            (missing * pitch / 2) / dpi, spread);
    }
}
