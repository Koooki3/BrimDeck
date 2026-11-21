using System.Text.Json;
using System.Text.Json.Nodes;
using BrimDeck.Core;

internal static class ExpandedGridTests
{
    internal static void Run(Action<string, bool> check)
    {
        ConstraintsAndNormalization(check);
        Placement(check);
        Seeding(check);
        SettingsAndProfiles(check);
    }

    private static void ConstraintsAndNormalization(Action<string, bool> check)
    {
        var clamped = ExpandedGrid.Constrain(new(ExpandedItem.Cpu, "D:", 8, -3, 0, 99));
        var full = ExpandedGrid.Constrain(new(ExpandedItem.Memory, "C:", 1, int.MaxValue, 2, 4));
        var process = ExpandedGrid.Constrain(new(ExpandedItem.Processes, null, 1, ExpandedGrid.MaxRows, 1, 9));
        var shortProcess = ExpandedGrid.Constrain(new(ExpandedItem.Processes, null, 0, 0, 2, 1));
        check("Expanded grid: constrain clears unrelated letters, clamps widths and graph heights, and bounds row extents",
            clamped == new ExpandedGridTile(ExpandedItem.Cpu, null, 1, 0, 1, 4)
            && full == new ExpandedGridTile(ExpandedItem.Memory, null, 0, ExpandedGrid.MaxRows - 1, 2, 1)
            && process == new ExpandedGridTile(ExpandedItem.Processes, null, 1, ExpandedGrid.MaxRows - 4, 1, 4)
            && shortProcess.Rows == 2);

        var normalized = ExpandedGrid.Normalize([
            new(ExpandedItem.Cpu, null, 9, -2, 2, 8),
            new(ExpandedItem.Gpu, null, 0, 0, 1, 2),
            new(ExpandedItem.Memory, "C:", 0, 0, 1, 8),
            new(ExpandedItem.Storage, " d: ", 1, 0, 2, 4),
            new(ExpandedItem.Storage, "D:", 0, 3, 1, 1),
            new(ExpandedItem.Storage, "folder", 0, 0, 1, 1),
            null,
            new((ExpandedItem)999, null, 0, 0, 1, 1)
        ]);
        var cpu = normalized.Single(tile => tile.Item == ExpandedItem.Cpu);
        var gpu = normalized.Single(tile => tile.Item == ExpandedItem.Gpu);
        var memory = normalized.Single(tile => tile.Item == ExpandedItem.Memory);
        var drive = normalized.Single(tile => tile.Item == ExpandedItem.Storage);
        check("Expanded grid: normalization filters invalid identities, deduplicates drives and relocates colliding tiles",
            cpu is { Column: 0, Row: 0, Columns: 2, Rows: 4 }
            && gpu is { Column: 0, Row: 4, Columns: 1, Rows: 2 }
            && memory is { Column: 1, Row: 4, Columns: 1, Rows: 1, Letter: null }
            && drive is { Letter: "D:", Columns: 2, Column: 0, Rows: 1 }
            && normalized.Count == 4);

        check("Expanded grid: same-reading identity uses the drive letter only for storage",
            ExpandedGrid.SameReading(new(ExpandedItem.Storage, " d: ", 0, 0, 1, 1), new(ExpandedItem.Storage, "D:", 1, 4, 2, 3))
            && !ExpandedGrid.SameReading(new(ExpandedItem.Storage, "C:", 0, 0, 1, 1), new(ExpandedItem.Storage, "D:", 0, 0, 1, 1))
            && ExpandedGrid.SameReading(new(ExpandedItem.Cpu, "C:", 0, 0, 1, 1), new(ExpandedItem.Cpu, null, 1, 4, 2, 3)));
    }

    private static void Placement(Action<string, bool> check)
    {
        var initial = ExpandedGrid.Normalize([
            new(ExpandedItem.Cpu, null, 0, 0, 1, 1),
            new(ExpandedItem.Gpu, null, 1, 0, 1, 1),
            new(ExpandedItem.Memory, null, 0, 1, 1, 1)
        ]);
        var moved = ExpandedGrid.Place(initial, new(ExpandedItem.Cpu, null, 1, 1, 2, 2));
        var cpu = moved.Single(tile => tile.Item == ExpandedItem.Cpu);
        var gpu = moved.Single(tile => tile.Item == ExpandedItem.Gpu);
        var memory = moved.Single(tile => tile.Item == ExpandedItem.Memory);
        check("Expanded grid: move and resize gives the target priority while preserving unaffected tiles",
            cpu is { Column: 0, Row: 1, Columns: 2, Rows: 2 }
            && gpu is { Column: 1, Row: 0 }
            && memory.Row >= 3
            && moved.All(a => moved.Where(b => b != a).All(b => !Overlaps(a, b))));

        var added = ExpandedGrid.Place(moved, new(ExpandedItem.Storage, "e:", 1, 0, 1, 1));
        check("Expanded grid: adding a tile normalizes its drive identity and relocates the existing occupant",
            added.Single(tile => tile.Item == ExpandedItem.Storage) is { Letter: "E:", Column: 1, Row: 0 }
            && added.Single(tile => tile.Item == ExpandedItem.Gpu) is { Column: 0, Row: 0 });
    }

    private static void Seeding(Action<string, bool> check)
    {
        var settings = new DeckSettings
        {
            ExpandedCpuCores = false,
            ExpandedCustomCards = true,
            MonitorMain = MonitorMain.Gpu,
            ExpandedReadings =
            [
                new() { Item = ExpandedItem.Cpu }, new() { Item = ExpandedItem.Gpu }, new() { Item = ExpandedItem.Memory },
                new() { Item = ExpandedItem.Download }, new() { Item = ExpandedItem.Upload }, new() { Item = ExpandedItem.Frames },
                new() { Item = ExpandedItem.Storage }, new() { Item = ExpandedItem.Power }, new() { Item = ExpandedItem.Processes }
            ],
            ExpandedCardOrder =
            [
                new(ExpandedItem.Download), new(ExpandedItem.Storage, "D:", true), new(ExpandedItem.Memory),
                new(ExpandedItem.Power), new(ExpandedItem.Frames)
            ]
        };
        var seed = ExpandedGrid.Seed(settings, ["c:", "D:", " bad "]);
        var ordered = seed.Select(tile => (tile.Item, tile.Letter)).ToArray();
        var cpu = seed.Single(tile => tile.Item == ExpandedItem.Cpu);
        var gpu = seed.Single(tile => tile.Item == ExpandedItem.Gpu);
        var download = seed.Single(tile => tile.Item == ExpandedItem.Download);
        var wideDrive = seed.Single(tile => tile.Item == ExpandedItem.Storage && tile.Letter == "D:");
        var memory = seed.Single(tile => tile.Item == ExpandedItem.Memory);
        check("Expanded grid: legacy custom order and drive width seed the grid while new drives append",
            ordered.SequenceEqual([
                (ExpandedItem.Cpu, (string?)null), (ExpandedItem.Gpu, null), (ExpandedItem.Download, null), (ExpandedItem.Storage, "D:"),
                (ExpandedItem.Memory, null), (ExpandedItem.Power, null), (ExpandedItem.Frames, null), (ExpandedItem.Upload, null),
                (ExpandedItem.Storage, "C:"), (ExpandedItem.Processes, null)
            ])
            && wideDrive.Columns == 2 && wideDrive.Row > download.Row && memory.Row > wideDrive.Row
            && gpu is { Column: 1, Row: 0 }
            && seed.Where(tile => tile.Item is ExpandedItem.Power or ExpandedItem.Frames or ExpandedItem.Processes).All(tile => tile.Columns == 2)
            && cpu.Rows == 1 && gpu.Rows == 2 && seed.Single(tile => tile.Item == ExpandedItem.Processes).Rows == 2);

        settings.ExpandedCustomCards = false;
        settings.ExpandedCpuCores = true;
        settings.MonitorMain = MonitorMain.Cpu;
        var defaults = ExpandedGrid.Seed(settings, ["D:"]);
        check("Expanded grid: regular reading order seeds visible chart sizes and ignores stale card layout",
            defaults.Select(tile => tile.Item).SequenceEqual([ExpandedItem.Cpu, ExpandedItem.Gpu, ExpandedItem.Memory, ExpandedItem.Download, ExpandedItem.Upload,
                ExpandedItem.Frames, ExpandedItem.Storage, ExpandedItem.Power, ExpandedItem.Processes])
            && defaults.Single(tile => tile.Item == ExpandedItem.Cpu).Rows == 2
            && defaults.Single(tile => tile.Item == ExpandedItem.Gpu).Rows == 1
            && defaults.Single(tile => tile.Item == ExpandedItem.Power).Columns == 2
            && defaults.Single(tile => tile.Item == ExpandedItem.Processes).Rows == 2);

        var hidden = new DeckSettings { ExpandedReadings = [.. ExpandedMonitor.Order.Select(item => new ExpandedReading { Item = item, Visible = false })] };
        check("Expanded grid: hidden readings do not produce seeded tiles", ExpandedGrid.Seed(hidden, ["C:"]).Count == 0);
    }

    private static void SettingsAndProfiles(Action<string, bool> check)
    {
        var neverEdited = SettingsMigrations.Read("""{"AppVersion":"9.0.0"}""");
        neverEdited.Normalize();
        var empty = new DeckSettings { ExpandedGridTiles = [] };
        empty.Normalize();
        var roundTrip = SettingsMigrations.Read(JsonSerializer.Serialize(empty));
        var nullCopy = neverEdited.Copy();
        var emptyCopy = roundTrip.Copy();
        emptyCopy.ExpandedGridTiles!.Add(new(ExpandedItem.Memory, null, 0, 0, 1, 1));
        check("Expanded grid: null remains never-edited and an explicit empty list remains empty through copies and settings round trips",
            neverEdited.ExpandedGridTiles is null && nullCopy.ExpandedGridTiles is null
            && roundTrip.ExpandedGridTiles is { Count: 0 }
            && emptyCopy.ExpandedGridTiles is { Count: 1 }
            && roundTrip.ExpandedGridTiles.Count == 0);

        var saved = new DeckSettings { ExpandedGridTiles = [new(ExpandedItem.Cpu, null, 1, 3, 1, 2)] };
        saved.Normalize();
        var profile = Profiles.Values(saved);
        var restored = Profiles.With(new DeckSettings(), profile);
        var oldProfile = Profiles.With(saved, JsonNode.Parse("""{"CoreDisplay":2}""")!.AsObject());
        check("Expanded grid: a profile restores its edited tiles and an older profile returns to null seeding",
            restored.ExpandedGridTiles is { Count: 1 } && restored.ExpandedGridTiles[0] == new ExpandedGridTile(ExpandedItem.Cpu, null, 1, 3, 1, 2)
            && oldProfile.ExpandedGridTiles is null);

        var damaged = new DeckSettings
        {
            ExpandedGridTiles =
            [
                new(ExpandedItem.Memory, "C:", 9, -1, 1, 7), new(ExpandedItem.Memory, null, 1, 2, 2, 1),
                new(ExpandedItem.Storage, "x:", 1, 0, 1, 1), new(ExpandedItem.Storage, "folder", 0, 0, 1, 1)
            ]
        };
        damaged.Normalize();
        check("Expanded grid: settings normalization preserves the collection and removes malformed duplicate entries",
            damaged.ExpandedGridTiles is { Count: 2 }
            && damaged.ExpandedGridTiles[0] is { Item: ExpandedItem.Memory, Letter: null, Columns: 1, Rows: 1, Row: 0, Column: 1 }
            && damaged.ExpandedGridTiles[1] is { Item: ExpandedItem.Storage, Letter: "X:" });
    }

    private static bool Overlaps(ExpandedGridTile a, ExpandedGridTile b)
        => a.Column < b.Column + b.Columns && b.Column < a.Column + a.Columns
            && a.Row < b.Row + b.Rows && b.Row < a.Row + a.Rows;
}
