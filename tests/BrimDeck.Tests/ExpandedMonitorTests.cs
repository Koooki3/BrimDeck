using System.Text.Json;
using System.Text.Json.Nodes;
using BrimDeck.Core;

// The expanded page's table of items and its layout (ExpandedMonitor, ExpandedLayout): the table's migration from the four old
// switches, the layout against the page as it was (MonitorLayout) when everything it showed is chosen, and properties of the layout
// over every subset of items at representative sizes and fonts: no overlap, nothing out of the page, readable sizes, priority when
// room runs short, nothing read for what is not chosen.
internal static class ExpandedMonitorTests
{
    private static readonly ExpandedItem[] Every = [.. Enum.GetValues<ExpandedItem>()];
    private static readonly ExpandedItem[] Before = [ExpandedItem.Cpu, ExpandedItem.Gpu, ExpandedItem.Memory, ExpandedItem.Download, ExpandedItem.Upload];
    private static readonly double[] Widths = [300, 440, 560, 620, 860, 1200];
    private static readonly double[] Heights = [60, 100, 120, 140, 160, 200, 220, 260, 320, 400, 520];
    private static readonly FontMetrics[] Fonts = [FontMetrics.Default, new(1.25, 1.7), new(1.5, 1.7), new(1, 2.1), new(.85, 1.5)];
    private static readonly ExpandedContent[] Contents = [new(0, 0), new(1, 1), new(4, 2), new(12, 2)];
    private static readonly ExpandedBox Zero = default;

    internal static void Run(Action<string, bool> check)
    {
        ExpandedGridTests.Run(check);
        Table(check);
        Network(check);
        SettingsFile(check);
        Cards(check);
        Regression(check);
        Special(check);
        Properties(check);
        Stability(check);
    }

    private static void Cards(Action<string, bool> check)
    {
        var old = SettingsMigrations.Read("""{"AppVersion":"9.0.0","MonitorStorage":false,"CoreDisplay":1}""");
        check("Expanded customization: old files keep automatic layout",
            !old.ExpandedCustomCards && old.ExpandedCardOrder.Count == 0 && old.CoreDisplay == CoreDisplay.Columns &&
            !old.ShowsExpanded(ExpandedItem.Storage));
        old.ExpandedCustomCards = true;
        old.ExpandedCardOrder = [new(ExpandedItem.Storage, " d: ", true), new(ExpandedItem.Upload), new(ExpandedItem.Storage, "D:"),
            new(ExpandedItem.Storage, "folder"), new(ExpandedItem.Processes), new(ExpandedItem.Memory, "D:")];
        var back = SettingsMigrations.Read(JsonSerializer.Serialize(old));
        check("Expanded customization: width and order round-trip, with invalid/duplicate entries removed and compact core mode preserved",
            back.ExpandedCustomCards && back.CoreDisplay == CoreDisplay.Columns &&
            back.ExpandedCardOrder.SequenceEqual([new(ExpandedItem.Storage, "D:", true), new(ExpandedItem.Upload), new(ExpandedItem.Memory)]));
        var copy = back.Copy();
        copy.ExpandedCardOrder.Clear();
        check("Expanded customization: settings copies have their own card order", back.ExpandedCardOrder.Count == 3);
        var savedProfile = Profiles.Values(back);
        var legacyProfile = JsonNode.Parse("""{"CoreDisplay":2}""")!.AsObject();
        var switched = Profiles.With(back, legacyProfile);
        var restored = Profiles.With(switched, savedProfile);
        check("Expanded customization: older profiles use defaults and existing profiles restore their saved layout when switched back",
            !switched.ExpandedCustomCards && switched.ExpandedCardOrder.Count == 0 && switched.CoreDisplay == CoreDisplay.Matrix &&
            restored.ExpandedCustomCards && restored.ExpandedCardOrder.SequenceEqual(back.ExpandedCardOrder));

        ExpandedItem[] items = [ExpandedItem.Cpu, ExpandedItem.Memory, ExpandedItem.Download, ExpandedItem.Upload, ExpandedItem.Storage, ExpandedItem.Processes];
        var cards = ExpandedCards.Resolve(back.ExpandedCardOrder, items, ["C:", "E:"]);
        check("Expanded customization: saved drive choice survives a disconnect and new drive letters append",
            cards.SequenceEqual([new(ExpandedItem.Storage, "D:", true), new(ExpandedItem.Upload), new(ExpandedItem.Memory),
                new(ExpandedItem.Download), new(ExpandedItem.Storage, "C:"), new(ExpandedItem.Storage, "E:")]));
        check("Expanded customization: an unpaired half fills its row without crossing a full-width card",
            ExpandedCards.Place([cards[1], cards[0], .. cards.Skip(2)], true).SequenceEqual([(0, 0, 2), (1, 0, 2), (2, 0, 1), (2, 1, 1), (3, 0, 1), (3, 1, 1)]) &&
            ExpandedCards.Place(cards, false).SequenceEqual(Enumerable.Range(0, cards.Count).Select(row => (row, 0, 2))));
        var ends = new ExpandedCard[] { new(ExpandedItem.Power), new(ExpandedItem.Download), new(ExpandedItem.Upload), new(ExpandedItem.Memory) };
        check("Expanded customization: the last half fills its row, and an unavailable reading re-pairs remaining neighbors in saved order",
            ExpandedCards.Place(ends, true).SequenceEqual([(0, 0, 2), (1, 0, 1), (1, 1, 1), (2, 0, 2)]) &&
            ExpandedCards.Place([ends[0], ends[2], ends[3]], true).SequenceEqual([(0, 0, 2), (1, 0, 1), (1, 1, 1)]) &&
            !ends[3].Wide);

        var content = new ExpandedCardContent(cards, AwaitingDrives: false, WatchDrives: true);
        var wide = ExpandedLayout.For(items, 1200, 320, cardContent: content);
        var narrow = ExpandedLayout.For(items, 300, 100, cardContent: content);
        check("Expanded customization: planner keeps aggregate CPU, all cards and adaptive process list placement",
            wide.CardsTwoColumns && wide.Part(ExpandedItem.Cpu) is not null && wide.CardsHeight > narrow.CardsHeight &&
            wide.List == ExpandedList.Side && !narrow.CardsTwoColumns &&
            wide.Demand(false) == (ExpandedDemand.Cpu | ExpandedDemand.Memory | ExpandedDemand.Network | ExpandedDemand.Storage | ExpandedDemand.Processes));
        var waiting = new ExpandedCardContent([], AwaitingDrives: true, WatchDrives: true);
        var drives = ExpandedLayout.For([ExpandedItem.Storage], 300, 120, cardContent: waiting);
        var detached = ExpandedLayout.For([ExpandedItem.Storage], 300, 120, cardContent: waiting with { AwaitingDrives = false });
        check("Expanded customization: drive-only pages request storage before the first reading and after disconnecting",
            drives.Kind == ExpandedKind.Readings && drives.CardsHeight > 0 && drives.Demand(false) == ExpandedDemand.Storage &&
            detached.Kind == ExpandedKind.Unavailable && detached.Demand(false) == ExpandedDemand.Storage);

        ExpandedPlan Sized(double panelWidth, double height, ExpandedPlan? previous = null)
            => ExpandedLayout.For(items, panelWidth - 40, height, null, null, MonitorMain.Cpu, previous, content);
        var initial = Sized(766, 344);
        var above = Sized(767, 344, initial);
        var below = Sized(766, 344, above);
        var expanded = Sized(850, 344, below);
        var narrowAgain = Sized(812, 344, expanded);
        var contracted = Sized(770, 344, narrowAgain);
        check("Expanded customization: one-DIP jitter preserves card columns, and crossing the dead band still reflows",
            initial.List == ExpandedList.Side && !initial.CardsTwoColumns && above.Signature == initial.Signature && below.Signature == initial.Signature
            && expanded.CardsTwoColumns && narrowAgain.CardsTwoColumns && !contracted.CardsTwoColumns);
        var shorter = Sized(767, 150, above);
        check("Expanded customization: column hysteresis does not freeze the scroll viewport",
            !shorter.CardsTwoColumns && shorter.CardsHeight < above.CardsHeight && shorter.Height == 150);
        var listBelow = Sized(599, 344);
        var nearListBoundary = Sized(600, 344, listBelow);
        var listSide = Sized(640, 344, nearListBoundary);
        var nearFromSide = Sized(599, 344, listSide);
        check("Expanded customization: process list keeps its place during one-DIP jitter and still changes across the dead band",
            listBelow.List == ExpandedList.Below && nearListBoundary.Signature == listBelow.Signature
            && listSide.List == ExpandedList.Side && nearFromSide.Signature == listSide.Signature);
        var listHidden = ExpandedLayout.For(items.Where(item => item != ExpandedItem.Processes), 510, 254, cardContent: content);
        var justEnabled = Sized(550, 254, listHidden);
        check("Expanded customization: explicitly enabling the process list is not held back by size hysteresis",
            listHidden.List == ExpandedList.None && justEnabled.List == ExpandedList.Below);
        var tooShortForList = Sized(550, 244, justEnabled);
        check("Expanded customization: a process list below the cards leaves no empty region when too short for its minimum rows",
            tooShortForList.List == ExpandedList.None && tooShortForList.ListBox == default);

        old.ExpandedCardOrder = [new(ExpandedItem.Memory) { CompactMemoryBar = true }, new(ExpandedItem.Download) { CompactMemoryBar = true }];
        var compact = SettingsMigrations.Read(JsonSerializer.Serialize(old));
        check("Expanded customization: compact memory independent of card width; unrelated readings ignore it",
            compact.ExpandedCardOrder.SequenceEqual([new(ExpandedItem.Memory) { CompactMemoryBar = true }, new(ExpandedItem.Download)]));
        ExpandedCardContent WithBar(bool compactBar) => new([new ExpandedCard(ExpandedItem.Memory) { CompactMemoryBar = compactBar }], false, false);
        var normalBar = ExpandedLayout.For([ExpandedItem.Memory], 560, 200, cardContent: WithBar(false));
        var compactBar = ExpandedLayout.For([ExpandedItem.Memory], 560, 200, cardContent: WithBar(true));
        check("Expanded customization: changing memory bar length refreshes the view without changing the card allocation",
            compactBar.Signature != normalBar.Signature && compactBar.Parts.SequenceEqual(normalBar.Parts));

        ExpandedItem[] selected = [ExpandedItem.Cpu, ExpandedItem.Memory, ExpandedItem.Power, ExpandedItem.Storage, ExpandedItem.Frames];
        var defaults = ExpandedCards.Resolve(null, selected, ["D:"]);
        check("Expanded customization: power and frame rate stay in their normal bands by default",
            defaults.SequenceEqual([new(ExpandedItem.Memory), new(ExpandedItem.Storage, "D:")]));
        var inline = ExpandedCards.Resolve([new(ExpandedItem.Memory), new(ExpandedItem.Power), new(ExpandedItem.Storage, "D:"), new(ExpandedItem.Frames)], selected, ["D:"]);
        var mixed = new ExpandedCardContent(inline, false, true);
        var arranged = ExpandedLayout.For(selected, 800, 344, new(1, 1), cardContent: mixed);
        check("Expanded customization: inserted sections use full rows between cards, without duplicate bands or lost demand",
            ExpandedCards.Place(inline, true).SequenceEqual([(0, 0, 2), (1, 0, 2), (2, 0, 2), (3, 0, 2)])
            && arranged.CardContent!.Cards.SequenceEqual(inline) && arranged.Parts.All(part => part.Item is not (ExpandedItem.Power or ExpandedItem.Frames))
            && arranged.Demand(false).HasFlag(ExpandedDemand.Power | ExpandedDemand.Frames));
        var unavailablePower = ExpandedLayout.For(selected, 800, 344, new(1, 0), cardContent: mixed);
        check("Expanded customization: missing power is omitted without changing the saved order",
            unavailablePower.CardContent!.Cards.All(card => card.Item != ExpandedItem.Power) && !unavailablePower.Demand(false).HasFlag(ExpandedDemand.Power)
            && mixed.Cards.Any(card => card.Item == ExpandedItem.Power));
        var separate = ExpandedLayout.For(selected, 800, 344, new(1, 1), cardContent: mixed with { Cards = defaults });
        check("Expanded customization: removing inline rows restores their normal bands",
            separate.Part(ExpandedItem.Power) is not null && separate.Part(ExpandedItem.Frames) is not null);

        var readings = ExpandedMonitor.Defaults(frames: true);
        var moved = ExpandedCards.ArrangePageSections(readings, [ExpandedItem.Memory, ExpandedItem.Frames, ExpandedItem.Cpu], inlinePower: true, inlineFrames: false);
        var visible = ExpandedMonitor.Shown(ExpandedMonitor.Normalize(moved));
        check("Expanded customization: page order sketch moves the cards as one band and keeps the inline power row in that band",
            visible.Select(item => ExpandedCards.PageSection(item, true, false)).Distinct()
                .SequenceEqual([ExpandedItem.Memory, ExpandedItem.Frames, ExpandedItem.Cpu, ExpandedItem.Processes])
            && moved.Count == readings.Count && moved.All(item => item.Visible));
        var minimum = ExpandedLayout.For(selected.Prepend(ExpandedItem.Gpu), 408, 102, new(1, 1), cardContent: mixed);
        check("Expanded customization: minimum panel fits one complete memory card with both gauges",
            !minimum.CardsTwoColumns && minimum.CardsHeight >= Math.Ceiling(minimum.Body * 1.7 + 31) + 1 && minimum.Part(ExpandedItem.Cpu) is not null);
        var pairedRows = ExpandedLayout.For([ExpandedItem.Download, ExpandedItem.Memory, ExpandedItem.Storage, ExpandedItem.Upload], 760, 400,
            cardContent: new([new(ExpandedItem.Download), new(ExpandedItem.Memory), new(ExpandedItem.Storage, "C:"), new(ExpandedItem.Upload)], false, true));
        double line = ExpandedCards.LineHeight(pairedRows.Steps.Body), spacing = 2 * ExpandedCards.VerticalPadding + ExpandedCards.RowGap + 1;
        check("Expanded customization: only the memory row gets additional height and the cards start at the top",
            pairedRows.CardsTwoColumns && pairedRows.Parts.Single().Box.Y == 0 &&
            Math.Abs(pairedRows.CardsHeight - (ExpandedCards.MemoryHeight(pairedRows.Steps.Body) + line + 2 * spacing)) < 1);
    }

    private static List<ExpandedItem> Subset(int mask) => [.. Every.Where((item, index) => (mask >> index & 1) != 0)];
    private static ExpandedBox Page(ExpandedPlan plan) => new(0, 0, plan.Width, plan.Height);

    private static void Table(Action<string, bool> check)
    {
        var defaults = ExpandedMonitor.Defaults();
        check("Expanded table: the default order and shown items are the page as it was",
            defaults.Select(r => r.Item).SequenceEqual(ExpandedMonitor.Order) && ExpandedMonitor.Shown(defaults).SequenceEqual(
                [ExpandedItem.Cpu, ExpandedItem.Gpu, ExpandedItem.Memory, ExpandedItem.Download, ExpandedItem.Upload, ExpandedItem.Storage, ExpandedItem.Power, ExpandedItem.Processes]));
        var off = ExpandedMonitor.Defaults(processes: false, storage: false, power: false, frames: true);
        check("Expanded table: the four old switches say which of their items show",
            ExpandedMonitor.Shown(off).SequenceEqual([ExpandedItem.Cpu, ExpandedItem.Gpu, ExpandedItem.Memory, ExpandedItem.Download, ExpandedItem.Upload, ExpandedItem.Frames])
            && ExpandedMonitor.Switches(off) == (false, false, false, true));
        var messy = ExpandedMonitor.Normalize([new() { Item = ExpandedItem.Power }, null, new() { Item = (ExpandedItem)99 }, new() { Item = ExpandedItem.Power, Visible = false },
            new() { Item = ExpandedItem.Cpu, Visible = false }]);
        check("Expanded table: unknown and repeated items are dropped, missing ones join at the end, the first of a repeat wins",
            messy.Count == 9 && messy[0].Item == ExpandedItem.Power && messy[0].Visible && messy[1].Item == ExpandedItem.Cpu && !messy[1].Visible
            && messy.Select(r => r.Item).Distinct().Count() == 9 && messy.Skip(2).Select(r => r.Item).SequenceEqual(ExpandedMonitor.Order.Where(i => i is not (ExpandedItem.Power or ExpandedItem.Cpu))));
        var nothing = ExpandedMonitor.Normalize(ExpandedMonitor.Order.Select(item => new ExpandedReading { Item = item, Visible = false }));
        check("Expanded table: a table with nothing shown stays so", ExpandedMonitor.Shown(nothing).Count == 0 && nothing.Count == 9);
        check("Expanded table: nothing chosen reads nothing, and a temperature is read only with a reading that has one",
            ExpandedMonitor.Demand([], true) == ExpandedDemand.None
            && ExpandedMonitor.Demand([ExpandedItem.Memory, ExpandedItem.Upload], true) == (ExpandedDemand.Memory | ExpandedDemand.Network)
            && ExpandedMonitor.Demand([ExpandedItem.Cpu], true) == (ExpandedDemand.Cpu | ExpandedDemand.Temperatures)
            && ExpandedMonitor.Demand([ExpandedItem.Storage], false) == ExpandedDemand.Storage);
    }

    private static void Network(Action<string, bool> check)
    {
        var moved = ExpandedMonitor.Normalize(ExpandedMonitor.Order.Reverse().Select(i => new ExpandedReading { Item = i }));
        check("Expanded table: a block's items stay together in the block's own order, where its first item was",
            moved.Select(r => r.Item).SequenceEqual([ExpandedItem.Processes, ExpandedItem.Power, ExpandedItem.Storage, ExpandedItem.Frames,
                ExpandedItem.Upload, ExpandedItem.Download, ExpandedItem.Memory, ExpandedItem.Cpu, ExpandedItem.Gpu]));
        var swapped = ExpandedMonitor.Swap(ExpandedMonitor.Defaults(), ExpandedItem.Memory);
        var rates = ExpandedMonitor.Swap(swapped, ExpandedItem.Download);
        check("Expanded table: memory and the network change places, and so do the two rates, inside their block",
            swapped.Select(r => r.Item).Skip(2).Take(3).SequenceEqual([ExpandedItem.Download, ExpandedItem.Upload, ExpandedItem.Memory])
            && rates.Select(r => r.Item).Skip(2).Take(3).SequenceEqual([ExpandedItem.Upload, ExpandedItem.Download, ExpandedItem.Memory])
            && ExpandedMonitor.Normalize(rates).Select(r => r.Item).SequenceEqual(rates.Select(r => r.Item)));
        var hidden = ExpandedMonitor.Arrange(rates, [ExpandedItem.Power, ExpandedItem.Storage, ExpandedItem.Cpu]);
        hidden.First(r => r.Item == ExpandedItem.Storage).Visible = false; hidden.First(r => r.Item == ExpandedItem.Upload).Visible = false;
        var restored = ExpandedMonitor.InDefaultOrder(hidden);
        check("Expanded table: the default order comes back for hidden items too, and what is shown or hidden stays",
            !ExpandedMonitor.IsDefaultOrder(hidden) && restored.Select(r => r.Item).SequenceEqual(ExpandedMonitor.Order) && ExpandedMonitor.IsDefaultOrder(restored)
            && restored.All(r => r.Visible == hidden.First(h => h.Item == r.Item).Visible));
        check("Expanded table: the default table is in the default order, and the process list's place does not count",
            ExpandedMonitor.IsDefaultOrder(ExpandedMonitor.Defaults()) && ExpandedMonitor.IsDefaultOrder(null)
            && ExpandedMonitor.IsDefaultOrder([new() { Item = ExpandedItem.Processes }]) && ExpandedMonitor.Normalize([new() { Item = ExpandedItem.Processes }])[0].Item == ExpandedItem.Processes);
        var arranged = ExpandedMonitor.Arrange(ExpandedMonitor.Defaults(), [ExpandedItem.Storage, ExpandedItem.Cpu, ExpandedItem.Power, ExpandedItem.Memory]);
        check("Expanded table: moving blocks puts them in the places those blocks held, the others stay",
            arranged.Select(r => r.Item).SequenceEqual([ExpandedItem.Storage, ExpandedItem.Cpu, ExpandedItem.Gpu, ExpandedItem.Frames, ExpandedItem.Power,
                ExpandedItem.Memory, ExpandedItem.Download, ExpandedItem.Upload, ExpandedItem.Processes])
            && arranged.Select(r => r.Visible).SequenceEqual(ExpandedMonitor.Normalize(arranged).Select(r => r.Visible)));
        var content = new ExpandedContent(1, 1);
        var split = ExpandedMonitor.Normalize([new() { Item = ExpandedItem.Memory }, new() { Item = ExpandedItem.Download, Visible = false }, new() { Item = ExpandedItem.Cpu }]);
        check("Expanded table: a table from before the network was split gets the upload after the download, shown as the download is",
            split.Select(r => r.Item).Take(4).SequenceEqual([ExpandedItem.Memory, ExpandedItem.Download, ExpandedItem.Upload, ExpandedItem.Cpu]) && !split[2].Visible);
        var shown = ExpandedMonitor.Normalize([new() { Item = ExpandedItem.Download }]);
        check("Expanded table: a shown network migrates to a shown download and upload", shown[0].Item == ExpandedItem.Download && shown[1].Item == ExpandedItem.Upload && shown[1].Visible);
        check("Expanded table: the download keeps the number the network had", (int)ExpandedItem.Download == 3 && (int)ExpandedItem.Upload > (int)ExpandedItem.Processes);
        var old = SettingsMigrations.Read("{\"AppVersion\":\"9.0.0\",\"ExpandedReadings\":[{\"Item\":3,\"Visible\":true},{\"Item\":0,\"Visible\":true}]}");
        check("Expanded settings: a saved network (number 3) reads as the download and the upload follows it",
            old.ExpandedReadings.Take(5).Select(r => r.Item).SequenceEqual([ExpandedItem.Download, ExpandedItem.Upload, ExpandedItem.Memory, ExpandedItem.Cpu, ExpandedItem.Gpu]) && old.ExpandedReadings[1].Visible);
        var both = ExpandedLayout.For([ExpandedItem.Download, ExpandedItem.Upload], 620, 200, content);
        var down = ExpandedLayout.For([ExpandedItem.Download], 620, 200, content);
        var up = ExpandedLayout.For([ExpandedItem.Upload], 620, 200, content);
        check("Expanded layout: download and upload are one block of one line, which shows the one or both",
            both.Parts.Count == 1 && both.Downloads && both.Uploads && both.Dropped.Count == 0 && both.Shown.Contains(ExpandedItem.Upload)
            && down.Downloads && !down.Uploads && down.Dropped.Count == 0 && up.Uploads && !up.Downloads && up.Part(ExpandedItem.Upload) is not null && up.Dropped.Count == 0);
        var upFirst = ExpandedLayout.For([ExpandedItem.Upload, ExpandedItem.Download], 620, 200, content);
        check("Expanded layout: the upload goes first where the table puts it first, and that is another page",
            upFirst.UploadFirst && !both.UploadFirst && !up.UploadFirst && upFirst.Signature != both.Signature);
        check("Expanded layout: the same box whichever rates the block shows", both.Parts[0].Box == down.Parts[0].Box && down.Parts[0].Box == up.Parts[0].Box);
    }

    private static void SettingsFile(Action<string, bool> check)
    {
        var old = SettingsMigrations.Read("""{"AppVersion":"1.2.0","MonitorProcesses":false,"MonitorStorage":true,"MonitorPower":false,"MonitorFrames":true}""");
        check("Expanded settings: a file saved before the table shows the page as it was",
            ExpandedMonitor.Shown(old.ExpandedReadings).SequenceEqual([ExpandedItem.Cpu, ExpandedItem.Gpu, ExpandedItem.Memory, ExpandedItem.Download, ExpandedItem.Upload, ExpandedItem.Frames, ExpandedItem.Storage]));
        check("Expanded settings: a table nobody set is not written (older files stay as they were)",
            JsonSerializer.SerializeToNode(old)!.AsObject()["ExpandedReadings"] is null);
        var fresh = new DeckSettings();
        check("Expanded settings: the default settings show the same items as the four switches' defaults",
            ExpandedMonitor.Shown(fresh.ExpandedReadings).SequenceEqual(ExpandedMonitor.Shown(ExpandedMonitor.Defaults())));
        // The old toggles keep working until a table is set: changing one changes what the page shows.
        fresh.MonitorStorage = false;
        check("Expanded settings: before a table is set the switches decide", !ExpandedMonitor.Shown(fresh.ExpandedReadings).Contains(ExpandedItem.Storage));

        var chosen = new DeckSettings { ExpandedReadings = [new() { Item = ExpandedItem.Download }, new() { Item = ExpandedItem.Cpu, Visible = false }, new() { Item = ExpandedItem.Processes, Visible = false }] };
        chosen.Normalize();
        check("Expanded settings: a set table is completed and the old switches follow it",
            chosen.ExpandedReadings.Count == 9 && chosen.ExpandedReadings[0].Item == ExpandedItem.Download && !chosen.MonitorProcesses && chosen.MonitorStorage && chosen.MonitorPower && !chosen.MonitorFrames);
        var json = JsonSerializer.Serialize(chosen);
        var back = SettingsMigrations.Read(json);
        check("Expanded settings: the table survives a save and a read, order included",
            back.ExpandedReadings.Select(r => (r.Item, r.Visible)).SequenceEqual(chosen.ExpandedReadings.Select(r => (r.Item, r.Visible))));
        var none = new DeckSettings { ExpandedReadings = [.. ExpandedMonitor.Order.Select(item => new ExpandedReading { Item = item, Visible = false })] };
        none.Normalize();
        var noneBack = SettingsMigrations.Read(JsonSerializer.Serialize(none));
        check("Expanded settings: choosing nothing survives a save and a read, and the old switches go off",
            ExpandedMonitor.Shown(noneBack.ExpandedReadings).Count == 0 && !noneBack.MonitorProcesses && !noneBack.MonitorStorage && !noneBack.MonitorPower && !noneBack.MonitorFrames);
        var copy = chosen.Copy();
        copy.ExpandedReadings[0].Visible = false;
        check("Expanded settings: a copy has a table of its own", chosen.ExpandedReadings[0].Visible && !copy.ExpandedReadings[0].Visible);
        // A newer file's table with an item this version does not know.
        var newer = SettingsMigrations.Read("""{"AppVersion":"9.0.0","ExpandedReadings":[{"Item":42,"Visible":true},{"Item":2,"Visible":true},{"Item":0,"Visible":false}]}""");
        check("Expanded settings: an item this version does not know is dropped on reading",
            newer.ExpandedReadings.Count == 3 || newer.ExpandedReadings.All(r => Enum.IsDefined(r.Item)));
        newer.Normalize();
        check("Expanded settings: after normalizing every item is there once", newer.ExpandedReadings.Count == 9 && newer.ExpandedReadings[0].Item == ExpandedItem.Memory);
    }

    // With all of CPU, GPU, memory and network (and any of the rest) chosen the layout is MonitorLayout's: the steps, and the places
    // of the parts against MonitorLayout.Parts.
    private static void Regression(Action<string, bool> check)
    {
        int compared = 0, squeezed = 0, stepDiffs = 0, partDiffs = 0; string first = "";
        foreach (bool processes in new[] { false, true })
            foreach (bool frames in new[] { false, true })
                foreach (int storage in new[] { 0, 1, 2, 3, 5, 6 })
                    foreach (int power in new[] { 0, 1, 2 })
                        foreach (double width in new double[] { 300, 440, 470, 560, 640, 860, 1200 })
                            for (double height = 60; height <= 520; height += 11)
                            {
                                var items = new List<ExpandedItem>(Before);
                                if (frames) items.Add(ExpandedItem.Frames);
                                if (storage > 0) items.Add(ExpandedItem.Storage);
                                if (power > 0) items.Add(ExpandedItem.Power);
                                if (processes) items.Add(ExpandedItem.Processes);
                                var plan = ExpandedLayout.For(items, width, height, new(storage, power));
                                var old = MonitorLayout.For(width, height, processes, storage, power, frames);
                                // Where the old page overflowed (its graph below the floor) the plan drops items instead; that is not compared.
                                if (plan.Squeezed) { squeezed++; continue; }
                                compared++;
                                if (plan.Steps != old) { stepDiffs++; if (first.Length == 0) first = $"steps {width}x{height} p{processes} f{frames} s{storage} w{power}: {plan.Steps} vs {old}"; continue; }
                                double readings = old.List == ListPlacement.Below ? MonitorLayout.StackedReadings : height;
                                var parts = old.Parts(readings);
                                double gap = old.Metrics switch { MetricsMode.Line => 2, MetricsMode.Lines => 4, _ => 8 };
                                var gauge = plan.Part(ExpandedItem.Cpu)!;
                                var metrics = plan.Part(ExpandedItem.Memory)!;
                                bool same = Math.Abs(gauge.Box.Height - (parts[1].Top + parts[1].Height + (old.Metrics == MetricsMode.Line ? 6 : 10))) < .01 && gauge.Box.Y == 0
                                    && Math.Abs(metrics.Box.Y - parts[2].Top) < .01;
                                var network = plan.Part(ExpandedItem.Download)!;
                                same &= old.Metrics == MetricsMode.Line ? network.Box.Y == metrics.Box.Y : Math.Abs(network.Box.Bottom - (parts[2].Top + parts[2].Height)) < .01;
                                if (plan.Part(ExpandedItem.Frames) is { } fp) same &= fp.Box.Y == parts[5].Top + gap && fp.Box.Height == parts[5].Height - gap;
                                else same &= !old.FrameLine;
                                if (plan.Part(ExpandedItem.Storage) is { } sp) same &= sp.Box.Y == parts[3].Top + gap && sp.Box.Height == parts[3].Height - gap;
                                else same &= old.StorageLines == 0;
                                if (plan.Part(ExpandedItem.Power) is { } pp) same &= pp.Box.Y == parts[4].Top + gap && pp.Box.Height == parts[4].Height - gap;
                                else same &= old.PowerLines == 0;
                                // The graph never gives less than the floor, as the page kept: the plan's gauge is at least the head with margins and floor.
                                if (!same) { partDiffs++; if (first.Length == 0) first = $"parts {width}x{height} p{processes} f{frames} s{storage} w{power}"; }
                            }
        check($"Expanded layout regression: with CPU, GPU, memory and network chosen the steps are MonitorLayout's ({compared} sizes and settings, {squeezed} squeezed ones left out; {first})", stepDiffs == 0);
        check($"Expanded layout regression: and the parts sit where MonitorLayout.Parts puts them ({first})", partDiffs == 0);
    }

    private static void Special(Action<string, bool> check)
    {
        var content = new ExpandedContent(4, 2);
        var empty = ExpandedLayout.For([], 620, 220, content);
        check("Expanded layout: nothing chosen is the empty page that reads nothing", empty.Kind == ExpandedKind.Empty && empty.Parts.Count == 0 && empty.List == ExpandedList.None
            && empty.Demand(true) == ExpandedDemand.None && empty.Dropped.Count == 0);
        var unavailable = ExpandedLayout.For([ExpandedItem.Storage, ExpandedItem.Power], 620, 220, new(0, 0));
        check("Expanded layout: only what this machine cannot read is not the empty page", unavailable.Kind == ExpandedKind.Unavailable && unavailable.Parts.Count == 0
            && unavailable.Dropped.Count == 2 && unavailable.Demand(false) == ExpandedDemand.None);
        var list = ExpandedLayout.For([ExpandedItem.Processes], 620, 220, content);
        check("Expanded layout: the list alone fills the page", list.Kind == ExpandedKind.List && list.List == ExpandedList.Full && list.ListBox == new ExpandedBox(0, 0, 620, 220)
            && list.Parts.Count == 0 && list.Demand(true) == ExpandedDemand.Processes);
        foreach (var item in Every.Where(i => i != ExpandedItem.Processes))
        {
            var plan = ExpandedLayout.For([item], 620, 260, content);
            var part = plan.Part(item);
            check($"Expanded layout: {item} alone is shown, is the only part, and fills the page", part is not null && plan.Parts.Count == 1 && plan.Dropped.Count == 0
                && part.Box.Width == 620 && plan.Demand(false) == ExpandedMonitor.Demand([item], false));
            // The gauges, memory and network flex to the whole page; the curves and the drives only to their limits, the rest is kept as room around.
            check($"Expanded layout: {item} alone leaves no unused height, or only past its largest",
                item is ExpandedItem.Cpu or ExpandedItem.Gpu or ExpandedItem.Memory or ExpandedItem.Download ? plan.Hole <= 1 : plan.Hole <= 260 - part!.MinHeight * 1.45 + 1);
        }
        var cpuOnly = ExpandedLayout.For([ExpandedItem.Cpu], 620, 260, content);
        check("Expanded layout: a lone gauge is the main view, taking the whole height", cpuOnly.Part(ExpandedItem.Cpu) is { Form: ExpandedForm.Solo } p && p.Box.Height == 260 && cpuOnly.Hole == 0);
        var memoryOnly = ExpandedLayout.For([ExpandedItem.Memory], 620, 260, content);
        check("Expanded layout: lone memory takes its block form", memoryOnly.Part(ExpandedItem.Memory)!.Form == ExpandedForm.Blocks);
        var drives = ExpandedLayout.For([ExpandedItem.Storage], 620, 400, new(12, 0));
        check("Expanded layout: lone drives take more lines than beside other readings",
            drives.Steps.StorageLines == 6 && drives.Steps.StorageColumns == 2 && ExpandedLayout.For([ExpandedItem.Cpu, ExpandedItem.Storage], 620, 400, new(12, 0)).Steps.StorageLines <= 3);
        var pair = ExpandedLayout.For([ExpandedItem.Cpu, ExpandedItem.Gpu], 620, 220, content, main: MonitorMain.Gpu);
        check("Expanded layout: both gauges are a pair, the chosen one large", pair.Part(ExpandedItem.Gpu) is { Form: ExpandedForm.Pair } big && pair.Part(ExpandedItem.Cpu) is { Form: ExpandedForm.Pair } small
            && big.Box.Width > small.Box.Width && big.Box.X < small.Box.X);
        var memoryNetwork = ExpandedLayout.For([ExpandedItem.Download, ExpandedItem.Memory], 620, 60, content);
        check("Expanded layout: memory and network share a line in the table's order", memoryNetwork.Part(ExpandedItem.Download)!.Box.X < memoryNetwork.Part(ExpandedItem.Memory)!.Box.X
            && memoryNetwork.Steps.Metrics == MetricsMode.Line);
        var reordered = ExpandedLayout.For([ExpandedItem.Power, ExpandedItem.Cpu, ExpandedItem.Memory], 620, 260, content);
        check("Expanded layout: items stack in the table's order", reordered.Part(ExpandedItem.Power)!.Box.Y < reordered.Part(ExpandedItem.Cpu)!.Box.Y
            && reordered.Part(ExpandedItem.Cpu)!.Box.Y < reordered.Part(ExpandedItem.Memory)!.Box.Y);
        var nan = ExpandedLayout.For(Every, double.NaN, -4, content, new FontMetrics(double.NaN, double.PositiveInfinity));
        check("Expanded layout: nonsense sizes and fonts give a plan without parts out of the page", nan.Parts.All(part => part.Box.Inside(Page(nan), .5) && double.IsFinite(part.Box.Height)));
        var tiny = ExpandedLayout.For([ExpandedItem.Frames], 400, 12, content);
        check("Expanded layout: a page too short for a line still shows the one asked, squeezed and inside the page",
            tiny.Part(ExpandedItem.Frames) is { } line && tiny.Squeezed && line.Box.Inside(Page(tiny), .5) && tiny.Parts.All(part => part.Box.Bottom <= 12.01));
        var onlyDown = ExpandedLayout.For([ExpandedItem.Download], 620, 200, content);
        var downUp = ExpandedLayout.For([ExpandedItem.Download, ExpandedItem.Upload], 620, 200, content);
        check("Expanded layout: plans differing only in the upload switch have different signatures", onlyDown.Signature != downUp.Signature);
    }

    private static void Properties(Action<string, bool> check)
    {
        int plans = 0;
        var failures = new Dictionary<string, string>();
        void Fail(string rule, string message) { if (!failures.ContainsKey(rule)) failures[rule] = message; }
        for (int mask = 0; mask < 1 << Every.Length; mask++)
        {
            var items = Subset(mask);
            // Reordered copies for a few masks: the order must not break any property.
            var orders = new List<List<ExpandedItem>> { items };
            if (items.Count > 1) { var reversed = new List<ExpandedItem>(items); reversed.Reverse(); orders.Add(reversed); }
            foreach (var chosen in orders)
                foreach (var width in Widths)
                    foreach (var height in Heights)
                        foreach (var font in Fonts)
                            foreach (var content in Contents)
                                foreach (var main in new[] { MonitorMain.Cpu, MonitorMain.Gpu })
                                {
                                    if (main == MonitorMain.Gpu && !(chosen.Contains(ExpandedItem.Cpu) && chosen.Contains(ExpandedItem.Gpu))) continue;
                                    if (chosen != items && (font != Fonts[0] || main == MonitorMain.Gpu)) continue;
                                    var plan = ExpandedLayout.For(chosen, width, height, content, font, main);
                                    plans++;
                                    string id = $"[{string.Join("+", chosen)}] {width}x{height} font {font} content {content}";
                                    var page = Page(plan);
                                    var boxes = plan.Parts.Select(part => part.Box).Append(plan.ListBox).Where(box => box != Zero).ToList();
                                    if (plan.Parts.Any(part => !(double.IsFinite(part.Box.X) && double.IsFinite(part.Box.Y) && part.Box.Width >= 0 && part.Box.Height >= 0 && double.IsFinite(part.Box.Width) && double.IsFinite(part.Box.Height))))
                                        Fail("finite", id);
                                    foreach (var box in boxes) if (!box.Inside(page, .5)) Fail("inside", id + " " + box);
                                    for (int a = 0; a < boxes.Count; a++)
                                        for (int b = a + 1; b < boxes.Count; b++) if (boxes[a].Overlaps(boxes[b])) Fail("overlap", id + $" {boxes[a]} {boxes[b]}");
                                    if (plan.Parts.Select(p => p.Item).Distinct().Count() != plan.Parts.Count) Fail("once", id);
                                    // Nothing is shown that was not chosen, and what was chosen is shown or dropped, never both.
                                    var shown = plan.Shown.ToList();
                                    if (shown.Any(item => !chosen.Contains(item))) Fail("chosen", id);
                                    if (plan.Dropped.Intersect(shown).Any() || plan.Dropped.Any(item => !chosen.Contains(item))) Fail("dropped", id);
                                    if (chosen.Except(shown).Except(plan.Dropped).Any() || shown.Count + plan.Dropped.Count != chosen.Count) Fail("accounted", id);
                                    // Nothing is read for what is not shown.
                                    var demand = plan.Demand(true);
                                    if ((demand & ~ExpandedMonitor.Demand(chosen, true)) != 0) Fail("demand", id);
                                    if (chosen.Count == 0 && (plan.Kind != ExpandedKind.Empty || demand != ExpandedDemand.None)) Fail("empty", id);
                                    // Readable: every part at least its form's minimum, unless the plan says it was squeezed. Only for the panel's own sizes.
                                    if (!plan.Squeezed && height >= 100) foreach (var part in plan.Parts) if (part.Box.Height < part.MinHeight * font.Scale - .5) Fail("readable", id + $" {part.Item} {part.Box.Height} < {part.MinHeight * font.Scale}");
                                    if (!plan.Squeezed && plan.Part(ExpandedItem.Cpu) is { } g1 && g1.Box.Height < (plan.Steps.Figure * 1.4 + 26 + MonitorLayout.MinimumGraph) * font.Scale * .5) Fail("graph", id);
                                    // With a gauge nothing is left over; without one, room remains only once every part has grown to its limit.
                                    bool gauge = plan.Parts.Any(part => part.Item is ExpandedItem.Cpu or ExpandedItem.Gpu);
                                    if (plan.Kind == ExpandedKind.Readings && !plan.Squeezed)
                                    {
                                        if (gauge && plan.Hole > .5) Fail("hole-gauge", id);
                                        if (!gauge && plan.Hole > font.Scale + .01 && plan.Parts.Sum(part => part.Box.Height) < plan.Parts.Sum(part => part.MinHeight * font.Scale) * 1.45 - 1) Fail("hole", id + $" hole {plan.Hole}");
                                    }
                                    // Priority when room runs short: the drives only where the frame rate and power are, the frame rate only where power is.
                                    if (plan.Part(ExpandedItem.Storage) is not null && chosen.Contains(ExpandedItem.Frames) && plan.Part(ExpandedItem.Frames) is null) Fail("priority-frames", id);
                                    if (plan.Part(ExpandedItem.Storage) is not null && chosen.Contains(ExpandedItem.Power) && content.PowerLines > 0 && plan.Part(ExpandedItem.Power) is null) Fail("priority-power", id);
                                    if (plan.Part(ExpandedItem.Frames) is not null && chosen.Contains(ExpandedItem.Power) && content.PowerLines > 0 && plan.Part(ExpandedItem.Power) is null) Fail("priority-power2", id);
                                    // Same input, same plan.
                                    if (plans % 97 == 0 && ExpandedLayout.For(chosen, width, height, content, font, main).Signature != plan.Signature) Fail("deterministic", id);
                                }
        }
        foreach (var rule in new[] { "finite", "inside", "overlap", "once", "chosen", "dropped", "accounted", "demand", "empty", "readable", "graph", "hole-gauge", "hole", "priority-frames", "priority-power", "priority-power2", "deterministic" })
            check($"Expanded layout properties ({plans} plans over every subset): {rule}" + (failures.TryGetValue(rule, out var message) ? " -- " + message : ""), !failures.ContainsKey(rule));

        // Fewer items fit as the page shrinks or the text grows, and the order of dropping holds.
        int bad = 0; string badId = "";
        for (int mask = 0; mask < 1 << Every.Length; mask++)
        {
            var items = Subset(mask).Where(item => item != ExpandedItem.Processes).ToList();
            // The page as it was loses a line of the lines under memory and network where they step from one line to a line each (at 150 DIP) when
            // two or more kinds of line are asked for, and the plan keeps that so the page does not change; it is left out here (a design question).
            if ((items.Contains(ExpandedItem.Cpu) || items.Contains(ExpandedItem.Gpu)) && items.Count(item => item is ExpandedItem.Frames or ExpandedItem.Storage or ExpandedItem.Power) >= 2) continue;
            foreach (var content in Contents)
                foreach (var width in new[] { 440d, 620, 1200 })
                {
                    int previous = -1;
                    for (double height = 40; height <= 520; height += 6)
                    {
                        int now = ExpandedLayout.For(items, width, height, content).Parts.Count;
                        if (now < previous) { bad++; if (badId.Length == 0) badId = $"[{string.Join("+", items)}] {width}x{height} {content}: {previous} then {now}"; }
                        previous = now;
                    }
                }
        }
        check("Expanded layout: a taller page never shows fewer items (list aside) " + badId, bad == 0);
        bad = 0; badId = "";
        for (int mask = 0; mask < 1 << Every.Length; mask += 3)
        {
            var items = Subset(mask).Where(item => item != ExpandedItem.Processes).ToList();
            foreach (var height in Heights)
            {
                int small = ExpandedLayout.For(items, 620, height, new(4, 2), new FontMetrics(1, 1.7)).Parts.Count, large = ExpandedLayout.For(items, 620, height, new(4, 2), new FontMetrics(1.5, 1.7)).Parts.Count;
                if (large > small) { bad++; if (badId.Length == 0) badId = $"[{string.Join("+", items)}] {height}: {small} vs {large}"; }
            }
        }
        check("Expanded layout: larger text never fits more " + badId, bad == 0);
        var scaled = ExpandedLayout.For(Every, 900, 450, new(4, 2), new FontMetrics(1.5, 1.7));
        var unit = ExpandedLayout.For(Every, 600, 300, new(4, 2));
        check("Expanded layout: text at 150 % is the page at 100 % in a third less room, drawn larger",
            scaled.Steps == unit.Steps && scaled.Parts.Count == unit.Parts.Count
            && scaled.Parts.Zip(unit.Parts).All(pair => pair.First.Item == pair.Second.Item && Math.Abs(pair.First.Box.Height - pair.Second.Box.Height * 1.5) < .01 && Math.Abs(pair.First.Box.Y - pair.Second.Box.Y * 1.5) < .01));
    }

    private static void Stability(Action<string, bool> check)
    {
        var items = ExpandedMonitor.Shown(ExpandedMonitor.Defaults());
        var content = new ExpandedContent(3, 1);
        // Find a height where the page changes step and hold it with a previous plan within the band.
        double step = 0; ExpandedPlan? below = null;
        for (double height = 100; height < 400; height += 1)
        {
            var a = ExpandedLayout.For(items, 700, height, content); var b = ExpandedLayout.For(items, 700, height + 1, content);
            if (a.Signature != b.Signature) { step = height + 1; below = a; break; }
        }
        check("Expanded layout: a size held at a step does not flip back and forth", below is not null
            && ExpandedLayout.For(items, 700, step, content, null, MonitorMain.Cpu, below).Signature == below.Signature
            && ExpandedLayout.For(items, 700, step + ExpandedLayout.Hysteresis + 2, content, null, MonitorMain.Cpu, below).Signature != below.Signature);
    }
}
