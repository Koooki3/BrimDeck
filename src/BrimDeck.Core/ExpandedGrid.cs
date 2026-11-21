namespace BrimDeck.Core;

// One reading in the expanded monitor's two-column grid. Storage readings are one tile per drive letter; all other readings
// have one tile each. Rows and columns describe occupied grid cells, not pixels.
public sealed record ExpandedGridTile(ExpandedItem Item, string? Letter, int Column, int Row, int Columns, int Rows);

public static class ExpandedGrid
{
    public const int ColumnCount = 2;
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

    // Seed a first grid view from the old reading table and optional card arrangement. A prior custom card layout keeps its
    // order, widths and disconnected drive slots; new drives append in letter order. CPU/GPU charts start at two rows when the
    // corresponding graph was visible in the old layout.
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
        var cpuMain = shown.Contains(ExpandedItem.Cpu);
        bool GraphVisible(ExpandedItem item) => item switch
        {
            ExpandedItem.Cpu => settings.ExpandedCpuCores,
            ExpandedItem.Gpu => !cpuMain || settings.MonitorMain == MonitorMain.Gpu,
            _ => false
        };
        bool FullWidth(ExpandedItem item) => item is ExpandedItem.Power or ExpandedItem.Frames or ExpandedItem.Processes;

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

        var result = new List<ExpandedGridTile>();
        int nextRow = 0, nextColumn = 0;
        foreach (var key in order)
        {
            var desired = candidates[key] with { Row = nextRow, Column = nextColumn };
            if (!TryFindSeedPosition(desired, result, nextRow, nextColumn, out var position)) continue;
            result.Add(position);
            nextRow = position.Row;
            nextColumn = position.Column + position.Columns;
            if (nextColumn >= ColumnCount) { nextRow++; nextColumn = 0; }
        }
        return result;

        void Add(ExpandedItem item, string? letter, bool wide)
        {
            var tile = new ExpandedGridTile(item, letter, 0, 0, FullWidth(item) || wide ? ColumnCount : 1,
                item == ExpandedItem.Processes || GraphVisible(item) ? 2 : 1);
            candidates.TryAdd(Key(tile), Constrain(tile));
        }
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
        IEnumerable<int> Columns()
        {
            if (tile.Columns == ColumnCount) { yield return 0; yield break; }
            yield return tile.Column;
            yield return 1 - tile.Column;
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

    // Seed follows row-major reading order. A full-width tile that encounters a partly occupied row starts the next row; later
    // items never backfill the space before that explicit break.
    private static bool TryFindSeedPosition(ExpandedGridTile tile, IReadOnlyList<ExpandedGridTile> placed, int startRow, int startColumn,
        out ExpandedGridTile result)
    {
        int maxStart = MaxRows - tile.Rows;
        for (int row = startRow; row <= maxStart; row++)
        {
            int firstColumn = row == startRow ? startColumn : 0;
            if (tile.Columns == ColumnCount)
            {
                if (firstColumn > 0) continue;
                var candidate = tile with { Row = row, Column = 0 };
                if (placed.All(other => !Overlaps(candidate, other))) { result = candidate; return true; }
                continue;
            }
            for (int column = firstColumn; column < ColumnCount; column++)
            {
                var candidate = tile with { Row = row, Column = column };
                if (placed.All(other => !Overlaps(candidate, other))) { result = candidate; return true; }
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

    private static int RowsFor(ExpandedItem item, int rows) => item switch
    {
        ExpandedItem.Processes => Math.Clamp(rows, 2, 4),
        ExpandedItem.Cpu or ExpandedItem.Gpu => Math.Clamp(rows, 1, 4),
        _ => 1
    };

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
