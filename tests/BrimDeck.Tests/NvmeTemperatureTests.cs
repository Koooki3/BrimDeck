using System.Buffers.Binary;
using BrimDeck.Core;

// An NVMe drive whose storage-stack temperature property fails (a Kingston SKC3000D: ERROR_IO_DEVICE) is read from its health
// log instead: the request's bytes, the answer's parsing (kelvin to Celsius, length and bounds, values no drive gives) and the
// choice between the two readings, which is made with the readings passed in, so no drive is asked.
static class NvmeTemperatureTests
{
    private static readonly StorageDisk Kingston = new("KINGSTON SKC3000D2048G", Storage.BusNvme, false);
    private static readonly StorageDisk Optane = new("INTEL MEMPEK1J016GAH", Storage.BusNvme, false);
    private static readonly StorageDisk Sata = new("WD Blue", 11, false);
    private static readonly StorageDisk Raid = new("Behind RAID", Storage.BusRaid, false);

    // What the stack answers to the request: the descriptor (Version, Size), the request's structure and then the log, whose bytes 1
    // and 2 hold the Composite Temperature in kelvin.
    private static byte[] Answer(int kelvin, int offset = NvmeHealth.SpecificDataLength, int length = NvmeHealth.LogLength)
    {
        var buffer = new byte[NvmeHealth.BufferLength];
        NvmeHealth.WriteQuery(buffer);
        BinaryPrimitives.WriteInt32LittleEndian(buffer, 40);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), 40);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8 + 16), offset);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8 + 20), length);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8 + NvmeHealth.SpecificDataLength + 1), (ushort)kelvin);
        return buffer;
    }

    // A drive that answers the standard health log request as one does: it checks what it is asked, and answers only that.
    private static (double? Celsius, bool Asked) Drive(int kelvin)
    {
        var buffer = new byte[NvmeHealth.BufferLength];
        NvmeHealth.WriteQuery(buffer);
        var request = buffer.AsSpan(0, NvmeHealth.QueryLength);
        bool health = BinaryPrimitives.ReadInt32LittleEndian(request) == 50 && BinaryPrimitives.ReadInt32LittleEndian(request[4..]) == 0
            && BinaryPrimitives.ReadInt32LittleEndian(request[8..]) == 3 && BinaryPrimitives.ReadInt32LittleEndian(request[12..]) == 2
            && BinaryPrimitives.ReadInt32LittleEndian(request[16..]) == 2;
        if (!health) return (null, false);
        var answer = Answer(kelvin);
        return (NvmeHealth.Celsius(answer, answer.Length), true);
    }

    public static void Run(Action<string, bool> check)
    {
        Request(check);
        Parsing(check);
        Choice(check);
    }

    private static void Request(Action<string, bool> check)
    {
        var buffer = new byte[NvmeHealth.BufferLength];
        Array.Fill(buffer, (byte)0xAA);
        NvmeHealth.WriteQuery(buffer);
        var expected = new byte[NvmeHealth.QueryLength];
        BinaryPrimitives.WriteInt32LittleEndian(expected, 50);                       // StorageDeviceProtocolSpecificProperty
        BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(8), 3);              // ProtocolTypeNvme
        BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(12), 2);             // NVMeDataTypeLogPage
        BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(16), 2);             // the health information log
        BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(24), 40);            // ProtocolDataOffset
        BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(28), 512);           // ProtocolDataLength
        check("NVMe health log: the request is the standard (QueryType 0) query for the health information log, from its start",
            buffer.AsSpan(0, NvmeHealth.QueryLength).SequenceEqual(expected) && NvmeHealth.QueryLength == 48 && NvmeHealth.BufferLength == 560);
        check("NVMe health log: the request leaves the rest of the buffer, where the log is written, alone", buffer.Skip(NvmeHealth.QueryLength).All(b => b == 0xAA));
        check("NVMe health log: a buffer too small for the request is refused rather than written past",
            Throws(() => NvmeHealth.WriteQuery(new byte[NvmeHealth.QueryLength - 1])) && !Throws(() => NvmeHealth.WriteQuery(new byte[NvmeHealth.QueryLength])));
        static bool Throws(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } }
    }

    private static void Parsing(Action<string, bool> check)
    {
        // The Kingston's 36 °C (309 K is 35.85 °C, shown as 36) and the Intel's 38 °C (311 K).
        var kingston = NvmeHealth.Celsius(Answer(309), NvmeHealth.BufferLength);
        check("NVMe health log: the Composite Temperature in kelvin is read as Celsius", kingston is { } heat && Math.Abs(heat - 35.85) < 1e-9 && Temperatures.Format(heat) == "36");
        check("NVMe health log: the other drive's reading, its own", NvmeHealth.Celsius(Answer(311), NvmeHealth.BufferLength) is { } other && Temperatures.Format(other) == "38");
        check("NVMe health log: the temperature is bytes 1 and 2 of the log (little-endian), not byte 0 or 3", ReadOnly(a =>
        {
            a[8 + NvmeHealth.SpecificDataLength + 0] = 0xFF; a[8 + NvmeHealth.SpecificDataLength + 3] = 0xFF;
        }, 309) is { } at && Math.Abs(at - 35.85) < 1e-9);
        check("NVMe health log: only the bytes returned count, the answer need not fill the buffer",
            NvmeHealth.Celsius(Answer(309), 8 + NvmeHealth.SpecificDataLength + 3) is { } part && Math.Abs(part - 35.85) < 1e-9);
        check("NVMe health log: a larger count than the buffer is held to the buffer", NvmeHealth.Celsius(Answer(309), 100000) is not null);
        check("NVMe health log: an answer shorter than its header is no reading", NvmeHealth.Celsius(Answer(309), 0) is null && NvmeHealth.Celsius(Answer(309), 8 + NvmeHealth.SpecificDataLength - 1) is null
            && NvmeHealth.Celsius([], 0) is null && NvmeHealth.Celsius(new byte[4], 4) is null && NvmeHealth.Celsius(Answer(309), -1) is null);
        check("NVMe health log: an answer that stops before the temperature is no reading", NvmeHealth.Celsius(Answer(309), 8 + NvmeHealth.SpecificDataLength + 2) is null && NvmeHealth.Celsius(Answer(309), 8 + NvmeHealth.SpecificDataLength) is null);
        var wrongProtocol = Answer(309); BinaryPrimitives.WriteInt32LittleEndian(wrongProtocol.AsSpan(8), 2);
        var wrongType = Answer(309); BinaryPrimitives.WriteInt32LittleEndian(wrongType.AsSpan(8 + 4), 1);
        check("NVMe health log: an answer that is not an NVMe log page is no reading", NvmeHealth.Celsius(wrongProtocol, wrongProtocol.Length) is null && NvmeHealth.Celsius(wrongType, wrongType.Length) is null);
        check("NVMe health log: an offset inside the structure, negative or beyond the answer is no reading",
            NvmeHealth.Celsius(Answer(309, offset: 0), NvmeHealth.BufferLength) is null && NvmeHealth.Celsius(Answer(309, offset: 39), NvmeHealth.BufferLength) is null
            && NvmeHealth.Celsius(Answer(309, offset: -8), NvmeHealth.BufferLength) is null && NvmeHealth.Celsius(Answer(309, offset: int.MaxValue), NvmeHealth.BufferLength) is null
            && NvmeHealth.Celsius(Answer(309, offset: 600), NvmeHealth.BufferLength) is null);
        check("NVMe health log: a log shorter than the temperature, or of negative length, is no reading",
            NvmeHealth.Celsius(Answer(309, length: 2), NvmeHealth.BufferLength) is null && NvmeHealth.Celsius(Answer(309, length: -1), NvmeHealth.BufferLength) is null
            && NvmeHealth.Celsius(Answer(309, length: 0), NvmeHealth.BufferLength) is null && NvmeHealth.Celsius(Answer(309, length: 3), NvmeHealth.BufferLength) is not null);
        check("NVMe health log: a drive with nothing to say (0 K, 0xFFFF, below freezing, above 125 °C) is no reading",
            new[] { 0, 273, 0xFFFF, 100, 399, 600 }.All(kelvin => NvmeHealth.Celsius(Answer(kelvin), NvmeHealth.BufferLength) is null));
        check("NVMe health log: the edges of what a working drive gives", NvmeHealth.Celsius(Answer(275), NvmeHealth.BufferLength) is not null && NvmeHealth.Celsius(Answer(398), NvmeHealth.BufferLength) is not null);
        check("NVMe health log: request and answer fit: a drive asked as the code asks answers with the temperature", Drive(309) is { Asked: true, Celsius: { } c } && Math.Abs(c - 35.85) < 1e-9);

        static double? ReadOnly(Action<byte[]> change, int kelvin)
        {
            var answer = Answer(kelvin); change(answer);
            return NvmeHealth.Celsius(answer, answer.Length);
        }
    }

    private static void Choice(Action<string, bool> check)
    {
        int logAsked = 0;
        double? Log(double? value) { logAsked++; return value; }
        DiskTemperature Own(StorageDisk disk, double celsius) => Temperatures.Disk(disk.Model, celsius, 70, 80)!;

        // The Kingston: the property fails, the log answers. This is the reported case; without the log it shows no temperature.
        var read = Storage.ReadTemperature(Kingston, () => null, () => Log(35.85));
        check("NVMe temperature: the property fails, the health log gives the drive's temperature", read is { } heat && Math.Abs(heat.Celsius - 35.85) < 1e-9 && heat.Name == "KINGSTON SKC3000D2048G" && heat.Warning is null && heat.Critical is null);
        check("NVMe temperature: a reading from the log is the drive's own temperature, levelled by the default thresholds", read is { } r && Temperatures.OfDisk(r) == MemoryLevel.Normal
            && Storage.ReadTemperature(Kingston, () => null, () => 65) is { } hot && Temperatures.OfDisk(hot) == MemoryLevel.Elevated);

        // The Intel: the property answers, so the log is not asked.
        logAsked = 0;
        var intel = Storage.ReadTemperature(Optane, () => Own(Optane, 38), () => Log(99));
        check("NVMe temperature: while the property answers, its reading (thresholds too) is used and the log is not asked", intel is { Celsius: 38, Warning: 70, Critical: 80 } && logAsked == 0);

        // Not NVMe: the property only.
        logAsked = 0;
        check("NVMe temperature: a SATA drive that does not answer shows nothing and is not asked for a log", Storage.ReadTemperature(Sata, () => null, () => Log(40)) is null && logAsked == 0);
        check("NVMe temperature: a SATA drive that answers keeps its reading", Storage.ReadTemperature(Sata, () => Own(Sata, 41), () => Log(40)) is { Celsius: 41 } && logAsked == 0);
        check("NVMe temperature: a drive behind a RAID controller is neither asked here nor given a log (and is not asked at all)",
            Storage.ReadTemperature(Raid, () => null, () => Log(40)) is null && logAsked == 0 && !Storage.AsksTemperature(Raid));

        // The log gives nothing usable: no reading, no error.
        check("NVMe temperature: a log that fails or gives nothing plausible is no reading", Storage.ReadTemperature(Kingston, () => null, () => null) is null
            && Storage.ReadTemperature(Kingston, () => null, () => 0) is null && Storage.ReadTemperature(Kingston, () => null, () => 200) is null
            && Storage.ReadTemperature(Kingston, () => null, () => double.NaN) is null && Storage.ReadTemperature(Kingston, () => null, () => -273.15) is null);
        check("NVMe temperature: a drive without a model is shown as SSD", Storage.ReadTemperature(new("", Storage.BusNvme, false), () => null, () => 36) is { Name: "SSD" });

        // Several drives are read each on its own: the Kingston from its log, the Intel from its property, the SATA drive not at all.
        var disks = new[] { (Disk: 0, Info: Kingston, Property: (double?)null, Log: (double?)35.85), (1, Optane, 38, 38.1), (2, Sata, null, 40) };
        var known = new Dictionary<int, DiskTemperature>();
        foreach (var item in disks)
            if (Storage.ReadTemperature(item.Info, () => item.Property is { } v ? Own(item.Info, v) : null, () => item.Log) is { } found) known[item.Disk] = found;
        check("NVMe temperature: each drive on its own, from its property or its log", known.Count == 2 && known[0].Celsius == 35.85 && known[1].Celsius == 38 && !known.ContainsKey(2));
        var grouped = Storage.Group([(new StorageVolume("C:", "", 500UL << 30, 100UL << 30), 0), (new StorageVolume("D:", "", 500UL << 30, 100UL << 30), 1)],
            new Dictionary<int, StorageDisk> { [0] = Kingston, [1] = Optane }, known.ToDictionary(pair => pair.Key, pair => pair.Value with { Number = pair.Key }), new HashSet<int> { 0, 1 });
        check("NVMe temperature: the drive's reading from the log shows on the drive's volumes like any other", grouped.Count == 2 && grouped[0].Temperature is { Number: 0 } && grouped[1].Temperature is { Number: 1, Celsius: 38 });
    }
}
