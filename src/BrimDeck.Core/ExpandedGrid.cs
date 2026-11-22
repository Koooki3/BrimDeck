namespace BrimDeck.Core;

// One reading in the expanded monitor's grid of four columns. Storage readings are one tile per drive letter; all other readings
// have one tile each. Rows and columns describe occupied grid cells, not pixels.
public sealed record ExpandedGridTile(ExpandedItem Item, string? Letter, int Column, int Row, int Columns, int Rows);

public static class ExpandedGrid
{
    public const int ColumnCount = 4;
    // The grid is intentionally bounded so a damaged settings file cannot make the monitor allocate an unbounded number of rows.
    public const int MaxRows = 128;

    // Clamp a proposed tile to a valid grid position and shape. Invalid item/drive identities are programmer errors here;
    // Normalize ignores them when reading saved settings.
    public static ExpandedGridTile Constrain(ExpandedGridTile tile)
    {
        ArgumentNullException.ThrowIfNull(tile);
        if (TryConstrain(tile, out var constrained)) return constrained;
        if (!Enum.IsDefined(tile.Item)) throw new ArgumentOutOfRangeException(nameof(tile), "Unknown expanded monitor item.");
        throw new ArgumentException("Storage tiles need a drive letter in the form X:.", nameof(tile));
    }

    // Invalid identities and shapes are dropped, duplicate readings keep their first saved position, and colliding tiles move
    // down to the next free cells. Existing non-colliding positions remain intact.
    public static List<ExpandedGridTile> Normalize(IEnumerable<ExpandedGridTile?>? tiles)
    {
        var unique = new List<ExpandedGridTile>();
        var seen = new HashSet<(ExpandedItem Item, string? Letter)>();
        foreach (var tile in tiles ?? [])
        {
            if (!TryConstrain(tile, out var constrained)) continue;
            if (seen.Add(Key(constrained))) unique.Add(constrained);
        }
        return PlaceCore(unique);
    }

    // Adds, moves or resizes a tile. The proposed tile has priority over existing occupants; only tiles that overlap it (or
    // another tile they must move around) are relocated.
    public static List<ExpandedGridTile> Place(IEnumerable<ExpandedGridTile> tiles, ExpandedGridTile tile)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(tile);
        var current = Normalize(tiles);
        if (!TryConstrain(tile, out var target)) return current;

        int oldIndex = current.FindIndex(existing => SameReading(existing, target));
        var priority = new List<ExpandedGridTile> { target };
        priority.AddRange(current.Where(existing => !SameReading(existing, target)));
        var placed = PlaceCore(priority);
        var byReading = placed.ToDictionary(Key);

        // Keep the caller's established collection order; the coordinates carry the visual order.
        var result = new List<ExpandedGridTile>(current.Count + (oldIndex < 0 ? 1 : 0));
        foreach (var existing in current)
            if (byReading.TryGetValue(Key(existing), out var updated)) result.Add(updated);
        if (oldIndex < 0 && byReading.TryGetValue(Key(target), out var added)) result.Add(added);
        return result;
    }

    public static bool SameReading(ExpandedGridTile a, ExpandedGridTile b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a.Item == b.Item && (a.Item != ExpandedItem.Storage ||
            StringComparer.OrdinalIgnoreCase.Equals(a.Letter?.Trim(), b.Letter?.Trim()));
    }

    // Seed a first grid view from the old reading table and optional card arrangement, laid out as the page before the grid (see
    // TryFindDefaultPosition). A prior custom card layout keeps its order, widths and disconnected drive slots; new drives append in
    // letter order. The CPU and the GPU start two rows high, showing their graphs.
    public static List<ExpandedGridTile> Seed(DeckSettings settings, IEnumerable<string> driveLetters)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(driveLetters);

        var readings = ExpandedMonitor.Normalize(settings.ExpandedReadings);
        var shown = readings.Where(reading => reading.Visible).Select(reading => reading.Item).ToHashSet();
        var drives = driveLetters.Select(NormalizeLetter).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        var legacy = ExpandedCards.Normalize(settings.ExpandedCardOrder ?? []);
        var saved = settings.ExpandedCustomCards ? legacy : [];
        bool bothGauges = shown.Contains(ExpandedItem.Cpu) && shown.Contains(ExpandedItem.Gpu);
        var main = settings.MonitorMain == MonitorMain.Gpu ? ExpandedItem.Gpu : ExpandedItem.Cpu;
        // As the page before the grid: the process list down the right, the readings in the columns left of it.
        bool sideList = shown.Contains(ExpandedItem.Processes);
        int readingColumns = sideList ? ColumnCount - 1 : ColumnCount;

        var candidates = new Dictionary<(ExpandedItem Item, string? Letter), ExpandedGridTile>();
        var storageLetters = new List<string>();
        if (shown.Contains(ExpandedItem.Storage))
        {
            if (settings.ExpandedCustomCards)
                storageLetters.AddRange(saved.Where(card => card.Item == ExpandedItem.Storage).Select(card => card.Letter!));
            storageLetters.AddRange(drives);
            storageLetters = [.. storageLetters.Distinct(StringComparer.OrdinalIgnoreCase)];
        }

        foreach (var reading in readings.Where(reading => reading.Visible))
        {
            if (reading.Item == ExpandedItem.Storage)
            {
                foreach (var letter in storageLetters)
                {
                    var old = saved.FirstOrDefault(card => card.Item == ExpandedItem.Storage && SameLetter(card.Letter, letter));
                    Add(ExpandedItem.Storage, letter, old?.Wide == true);
                }
            }
            else
            {
                var old = saved.FirstOrDefault(card => card.Item == reading.Item);
                Add(reading.Item, null, old?.Wide == true);
            }
        }

        var order = new List<(ExpandedItem Item, string? Letter)>();
        void AddIfPresent(ExpandedItem item, string? letter = null)
        {
            var key = (item, letter);
            if (candidates.ContainsKey(key) && !order.Contains(key)) order.Add(key);
        }

        if (settings.ExpandedCustomCards)
        {
            // The old card layout kept CPU/GPU above its card stack and the process list outside it.
            foreach (var reading in readings.Where(reading => reading.Visible && reading.Item is ExpandedItem.Cpu or ExpandedItem.Gpu))
                AddIfPresent(reading.Item);
            foreach (var card in saved)
            {
                if (card.Item == ExpandedItem.Storage) AddIfPresent(card.Item, card.Letter);
                else AddIfPresent(card.Item);
            }
            foreach (var reading in readings.Where(reading => reading.Visible && reading.Item is not (ExpandedItem.Cpu or ExpandedItem.Gpu or ExpandedItem.Processes)))
            {
                if (reading.Item == ExpandedItem.Storage)
                    foreach (var letter in storageLetters) AddIfPresent(ExpandedItem.Storage, letter);
                else AddIfPresent(reading.Item);
            }
            AddIfPresent(ExpandedItem.Processes);
        }
        else
        {
            foreach (var reading in readings.Where(reading => reading.Visible))
            {
                if (reading.Item == ExpandedItem.Storage)
                    foreach (var letter in storageLetters) AddIfPresent(ExpandedItem.Storage, letter);
                else AddIfPresent(reading.Item);
            }
        }

        // The process list first, down the right for four rows. Each reading then takes the first place it fits, in reading order
        // (filling a gap left beside an earlier one); one as wide as the readings' columns takes the whole width below the list.
        var placed = new List<ExpandedGridTile>();
        int listRows = 0;
        if (sideList && candidates.TryGetValue((ExpandedItem.Processes, null), out var processes))
        {
            placed.Add(processes with { Column = ColumnCount - 1, Row = 0 });
            listRows = processes.Rows;
        }
        foreach (var key in order)
        {
            if (sideList && key.Item == ExpandedItem.Processes) continue;
            if (TryFindDefaultPosition(candidates[key], placed, readingColumns, listRows, out var position)) placed.Add(position);
        }
        return [.. order.Select(key => placed.FirstOrDefault(tile => Key(tile) == key)).OfType<ExpandedGridTile>()];

        void Add(ExpandedItem item, string? letter, bool wide)
        {
            // The main gauge large and the other small beside it, as tall as it; memory two columns; the rates and the drives one
            // each (a drive the old cards made wide, all the readings' columns); power and the frame rate the readings' whole width.
            int columns = item switch
            {
                ExpandedItem.Cpu or ExpandedItem.Gpu => !bothGauges ? readingColumns : item == main ? readingColumns - 1 : 1,
                ExpandedItem.Memory => 2,
                ExpandedItem.Power or ExpandedItem.Frames => readingColumns,
                ExpandedItem.Processes => sideList ? 1 : ColumnCount,
                _ => wide ? readingColumns : 1
            };
            int rows = item switch
            {
                ExpandedItem.Processes => 4,
                ExpandedItem.Cpu or ExpandedItem.Gpu => 2,
                _ => 1
            };
            var tile = new ExpandedGridTile(item, letter, 0, 0, columns, rows);
            candidates.TryAdd(Key(tile), Constrain(tile));
        }
    }

    // The first place a tile fits, row by row: beside the process list (its first listRows rows) only in the readings' columns,
    // below it in all of them, where a tile as wide as the readings' columns widens to the whole row.
    private static bool TryFindDefaultPosition(ExpandedGridTile tile, IReadOnlyList<ExpandedGridTile> placed, int readingColumns, int listRows,
        out ExpandedGridTile result)
    {
        for (int row = 0; row <= MaxRows - tile.Rows; row++)
        {
            bool beside = row < listRows;
            int limit = beside ? readingColumns : ColumnCount;
            int columns = !beside && tile.Columns >= readingColumns ? ColumnCount : Math.Min(tile.Columns, limit);
            for (int column = 0; column + columns <= limit; column++)
            {
                var candidate = tile with { Row = row, Column = column, Columns = columns };
                if (placed.All(other => !Overlaps(candidate, other))) { result = candidate; return true; }
            }
        }
        result = tile;
        return false;
    }

    // What the expanded page shows of a grid: only the columns and rows its tiles use, from the first used to the last, so a layout
    // in two of the four columns fills the page's width. Gaps between tiles stay.
    public static (List<ExpandedGridTile> Tiles, int Columns) Trim(IEnumerable<ExpandedGridTile> tiles)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        var list = tiles.ToList();
        if (list.Count == 0) return ([], ColumnCount);
        int left = list.Min(tile => tile.Column), right = list.Max(tile => tile.Column + tile.Columns), top = list.Min(tile => tile.Row);
        return ([.. list.Select(tile => tile with { Column = tile.Column - left, Row = tile.Row - top })], right - left);
    }

    private static List<ExpandedGridTile> PlaceCore(IReadOnlyList<ExpandedGridTile> items)
    {
        var placed = new List<ExpandedGridTile>(items.Count);
        foreach (var item in items)
        {
            if (TryFindPosition(item, placed, out var position)) placed.Add(position);
        }
        // The stable order also determines collision priority.
        return placed;
    }

    private static bool TryFindPosition(ExpandedGridTile tile, IReadOnlyList<ExpandedGridTile> placed, out ExpandedGridTile result)
    {
        int maxStart = MaxRows - tile.Rows;
        IEnumerable<int> Rows()
        {
            for (int row = tile.Row; row <= maxStart; row++) yield return row;
            for (int row = 0; row < tile.Row && row <= maxStart; row++) yield return row;
        }
        // Its own column first, then the nearest ones either side.
        IEnumerable<int> Columns()
        {
            int last = ColumnCount - tile.Columns, own = Math.Clamp(tile.Column, 0, last);
            yield return own;
            for (int step = 1; step <= last; step++)
            {
                if (own - step >= 0) yield return own - step;
                if (own + step <= last) yield return own + step;
            }
        }

        foreach (int row in Rows())
            foreach (int column in Columns())
            {
                var candidate = tile with { Row = row, Column = column };
                if (placed.All(other => !Overlaps(candidate, other)))
                {
                    result = candidate;
                    return true;
                }
            }
        result = tile;
        return false;
    }

    private static bool Overlaps(ExpandedGridTile a, ExpandedGridTile b)
        => a.Column < b.Column + b.Columns && b.Column < a.Column + a.Columns
            && a.Row < b.Row + b.Rows && b.Row < a.Row + a.Rows;

    private static bool TryConstrain(ExpandedGridTile? tile, out ExpandedGridTile constrained)
    {
        constrained = null!;
        if (tile is null || !Enum.IsDefined(tile.Item)) return false;
        string? letter = tile.Item == ExpandedItem.Storage ? NormalizeLetter(tile.Letter) : null;
        if (tile.Item == ExpandedItem.Storage && letter is null) return false;
        int columns = Math.Clamp(tile.Columns, 1, ColumnCount);
        int rows = RowsFor(tile.Item, tile.Rows);
        constrained = tile with
        {
            Letter = letter,
            Column = columns == ColumnCount ? 0 : Math.Clamp(tile.Column, 0, ColumnCount - columns),
            Row = Math.Clamp(tile.Row, 0, MaxRows - rows),
            Columns = columns,
            Rows = rows
        };
        return true;
    }

    // How many rows a tile may take: only what shows more when taller can be taller. The gauges gain their graphs from two rows,
    // memory puts its bar under its figure, the process list shows more rows; a rate, a drive, power and the frame rate are one line.
    public static (int Min, int Max) RowRange(ExpandedItem item) => item switch
    {
        ExpandedItem.Processes => (2, 4),
        ExpandedItem.Cpu or ExpandedItem.Gpu => (1, 4),
        ExpandedItem.Memory => (1, 2),
        _ => (1, 1)
    };

    private static int RowsFor(ExpandedItem item, int rows)
    {
        var (min, max) = RowRange(item);
        return Math.Clamp(rows, min, max);
    }

    private static string? NormalizeLetter(string? letter)
    {
        var normalized = letter?.Trim().ToUpperInvariant();
        return normalized is [>= 'A' and <= 'Z', ':'] ? normalized : null;
    }

    private static bool SameLetter(string? a, string? b)
        => StringComparer.OrdinalIgnoreCase.Equals(a?.Trim(), b?.Trim());

    private static (ExpandedItem Item, string? Letter) Key(ExpandedGridTile tile)
        => (tile.Item, tile.Item == ExpandedItem.Storage ? NormalizeLetter(tile.Letter) : null);
}
