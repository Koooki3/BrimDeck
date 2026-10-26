using System.Globalization;

namespace BrimDeck.Core;

// A volume with a drive letter: its letter ("C:"), its label and its size and free space in bytes, as the file system reports
// them for the current user.
public sealed record StorageVolume(string Letter, string Label, ulong Total, ulong Free)
{
    public ulong Used => Total - Math.Min(Free, Total);
    public double UsedFraction => Total > 0 ? Math.Clamp((double)Used / Total, 0, 1) : 0;
}

// A physical drive and its lettered volumes, in letter order. Number is the drive's number (\\.\PhysicalDriveN), or -1 for a volume
// that spans drives or whose drive is unknown; Model is the drive's model. The temperature belongs to the drive, not to a volume.
// SolidState: a solid-state drive, the only kind asked for a temperature.
public sealed record StorageDrive(int Number, string Model, IReadOnlyList<StorageVolume> Volumes)
{
    public DiskTemperature? Temperature { get; init; }
    public bool SolidState { get; init; }
    public ulong Total => Volumes.Aggregate(0UL, (sum, volume) => sum + volume.Total);
    public ulong Used => Volumes.Aggregate(0UL, (sum, volume) => sum + volume.Used);
    public double UsedFraction => Total > 0 ? Math.Clamp((double)Used / Total, 0, 1) : 0;
    // "C:", or "C: D:" for a drive split into volumes.
    public string Letters => string.Join(" ", Volumes.Select(volume => volume.Letter));
}

// What Windows says of a physical drive: its model, its bus (STORAGE_BUS_TYPE) and whether its media is removable.
public sealed record StorageDisk(string Model, int Bus, bool Removable);

// One line of the page's storage block: a drive, or (Others) the drives that did not fit, together. Segments are the volumes'
// shares of the line's bar: each its size against the line's total and its own used share, in order.
public sealed record StorageRow(string Label, ulong Total, ulong Used, IReadOnlyList<(double Width, double Used)> Segments, double? Celsius, MemoryLevel Level, IReadOnlyList<StorageDrive> Drives, bool Others)
{
    public double UsedFraction => Total > 0 ? Math.Clamp((double)Used / Total, 0, 1) : 0;
}

// The drives the system monitor shows: fixed volumes with a letter on internal drives, grouped by physical drive. Removable media
// (a Ventoy stick, an SD card), USB drives, virtual adapters' disks, network and optical drives are left out, and so
// are volumes too small to matter (a system reserved or recovery partition that has a letter).
public static class Storage
{
    // STORAGE_BUS_TYPE values that are not internal drives.
    public const int BusUsb = 7, BusSd = 12, BusMmc = 13, BusVirtual = 14, BusFileBackedVirtual = 15;
    public const ulong SmallestVolume = 1UL << 30;
    // The letter of the volume holding Windows, "C:".
    public static string? WindowsLetter { get; } = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\', '/') is { Length: > 0 } root ? root : null;

    // A file-backed virtual disk that is not removable stays: a Dev Drive (Windows 11) is a VHDX the user keeps as a drive of its
    // own, as is a VHD mounted for good. Its space is also part of the drive that holds its file.
    public static bool Internal(StorageDisk disk) => !disk.Removable && disk.Bus is not (BusUsb or BusSd or BusMmc or BusVirtual);

    // The drives from the volumes and the physical drive each lies on (null when it spans drives or its drive is unknown), the
    // drives Windows describes, the temperatures by drive number and the solid-state drives' numbers. A volume on a drive that is
    // not internal is left out, as is one that is too small; a volume without a known drive is a drive of its own. Drives in the
    // order of their first letter.
    public static List<StorageDrive> Group(IEnumerable<(StorageVolume Volume, int? Disk)> volumes, IReadOnlyDictionary<int, StorageDisk> disks,
        IReadOnlyDictionary<int, DiskTemperature>? temperatures = null, IReadOnlySet<int>? solid = null)
    {
        var kept = volumes.Where(item => item.Volume.Total >= SmallestVolume
            && (item.Disk is not { } number || disks.TryGetValue(number, out var disk) && Internal(disk))).ToList();
        var drives = kept.Where(item => item.Disk is not null).GroupBy(item => item.Disk!.Value)
            .Select(group => new StorageDrive(group.Key, disks[group.Key].Model, group.Select(item => item.Volume).OrderBy(Letter).ToList())
            {
                Temperature = temperatures is not null && temperatures.TryGetValue(group.Key, out var heat) ? heat : null,
                SolidState = solid?.Contains(group.Key) == true
            })
            .Concat(kept.Where(item => item.Disk is null).Select(item => new StorageDrive(-1, "", [item.Volume])))
            .OrderBy(drive => Letter(drive.Volumes[0])).ToList();
        return drives;
        static string Letter(StorageVolume volume) => volume.Letter.ToUpperInvariant();
    }

    // The lines for room lines: each drive, or, with more drives than lines, the first room − 1 and a last line for the others
    // ("+2"), their sizes summed and their hottest temperature. temperatures: whether the lines carry temperatures.
    public static List<StorageRow> Rows(IReadOnlyList<StorageDrive> drives, int room, bool temperatures = true)
    {
        room = Math.Max(1, room);
        var rows = new List<StorageRow>();
        bool fold = drives.Count > room;
        foreach (var drive in fold ? drives.Take(room - 1) : drives) rows.Add(Row(drive.Letters, [drive], temperatures, false));
        // With a single line it names the first drive and counts the rest: "C: +1".
        string others = "+" + (drives.Count - room + (room == 1 ? 0 : 1)).ToString(CultureInfo.InvariantCulture);
        if (fold) rows.Add(Row(room == 1 ? drives[0].Letters + " " + others : others, [.. drives.Skip(room - 1)], temperatures, true));
        return rows;
    }

    private static StorageRow Row(string label, IReadOnlyList<StorageDrive> drives, bool temperatures, bool others)
    {
        var volumes = drives.SelectMany(drive => drive.Volumes).ToList();
        ulong total = volumes.Aggregate(0UL, (sum, volume) => sum + volume.Total), used = volumes.Aggregate(0UL, (sum, volume) => sum + volume.Used);
        // Others take one bar: their volumes are on different drives.
        IReadOnlyList<(double, double)> segments = others || volumes.Count == 1 || total == 0
            ? [(1, total > 0 ? Math.Clamp((double)used / total, 0, 1) : 0)]
            : volumes.Select(volume => ((double)volume.Total / total, volume.UsedFraction)).ToList();
        var hottest = temperatures ? drives.Select(drive => drive.Temperature).OfType<DiskTemperature>().OrderByDescending(heat => (int)Temperatures.OfDisk(heat)).ThenByDescending(heat => heat.Celsius).FirstOrDefault() : null;
        return new StorageRow(label, total, used, segments, hottest?.Celsius, hottest is null ? MemoryLevel.Normal : Temperatures.OfDisk(hottest), drives, others);
    }

    // The drive holding the Windows folder (its letter, "C:"), for the collapsed row's reading: its used share. Null without one.
    public static StorageDrive? System(IReadOnlyList<StorageDrive> drives, string? windowsLetter) =>
        drives.FirstOrDefault(drive => windowsLetter is not null && drive.Volumes.Any(volume => volume.Letter.Equals(windowsLetter, StringComparison.OrdinalIgnoreCase)))
        ?? drives.FirstOrDefault();

    // The drive holding the volume with this letter ("D:"), and that volume; null when no listed drive has it.
    public static (StorageDrive Drive, StorageVolume Volume)? Find(IReadOnlyList<StorageDrive> drives, string letter)
    {
        // By index: asked for each chosen letter on every reading of the collapsed row.
        for (int i = 0; i < drives.Count; i++)
        {
            var volumes = drives[i].Volumes;
            for (int j = 0; j < volumes.Count; j++)
                if (volumes[j].Letter.Equals(letter, StringComparison.OrdinalIgnoreCase)) return (drives[i], volumes[j]);
        }
        return null;
    }

    // A line's figures in one unit chosen by its size: binary gigabytes, whole from 10 GB ("212", "477", "GB"), or terabytes with
    // one decimal from 1000 GB ("0.4", "1.8", "TB").
    public static (string Used, string Total, string Unit) Sizes(ulong used, ulong total)
    {
        double scale = total / 1_073_741_824d >= 999.5 ? 1_099_511_627_776d : 1_073_741_824d;
        string unit = scale > 1_073_741_824d ? "TB" : "GB";
        string Figure(ulong bytes)
        {
            double value = bytes / scale;
            return unit == "TB" || value < 9.95 ? value.ToString("0.0", CultureInfo.InvariantCulture) : ((int)Math.Round(value)).ToString(CultureInfo.InvariantCulture);
        }
        return (Figure(used), Figure(total), unit);
    }
}
