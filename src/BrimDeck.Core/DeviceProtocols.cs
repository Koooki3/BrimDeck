namespace BrimDeck.Core;

// The vendor protocols the devices page reads batteries with, as bytes in and readings out: building each request and parsing
// each reply, with no I/O, so every one is tested against the bytes it is documented with. The app's HID layer
// (Native/HidBatteries.cs) sends them. Sources, by protocol:
// - Logitech HID++ 2.0: after device-battery-viewer by CharlesGool (https://github.com/CharlesGool/device-battery-viewer,
//   MIT License; see ThirdPartyNotices.txt), whose flow, battery features and voltage curve are ported; the HID++ 1.0 battery
//   registers and the 0x1F20 headset feature are from the protocol as the Linux kernel and Solaar describe it (facts only).
// - Razer: after razer-battery-report by Timofey Gelazoniya (https://github.com/xzeldon/razer-battery-report, MIT License;
//   see ThirdPartyNotices.txt): the 90-byte report, its checksum and the catalog of models and transaction ids.
// - ATK/VXE (Nordic "BitMouse") and Compx frames: after Lkig/ATK-A9-Battery and cobanov/atkctl (MIT); Inphic IN9: after
//   InphicBatteryTray by Tan-TanZi (MIT). See ThirdPartyNotices.txt.
// - SteelSeries, HyperX and MCHOSE headsets, MCHOSE mice, VGN keyboards, Endgame Gear, VAXEE, Nordic receivers and the
//   PlayStation controllers: written from the documented bytes alone (HeadsetControl, the Linux drivers and the vendors' own
//   web drivers are look-only).
// - AirPods and Beats: the proximity pairing advertisement's layout as OpenPods and AirPodsDesktop read it (GPL; look-only,
//   facts only).

// One battery reading from a protocol: the percentage (null: the device answered without one), whether it charges, and whether
// the level is coarse (a voltage, or a few steps).
public readonly record struct BatteryRead(int? Percent, bool Charging = false, bool Coarse = false);

public static class Hidpp
{
    public const int Vendor = 0x046D;
    public const byte Short = 0x10, Long = 0x11, SoftwareId = 0x0D;
    public const int ShortLength = 7, LongLength = 20;
    public const int DeviceInfo = 0x0003, DeviceName = 0x0005, BatteryStatus = 0x1000, BatteryVoltage = 0x1001, UnifiedBattery = 0x1004, AdcMeasurement = 0x1F20;
    // HID++ 1.0's error for a request it does not know: the device speaks only HID++ 1.0.
    public const int InvalidSubId = 0x01;

    // A receiver (Unifying, Bolt, Lightspeed, Nano) holds devices 1 to 6; a device on a cable or its own dongle is 0xFF.
    public static bool IsReceiver(int product, string productName) => product is >= 0xC500 and <= 0xC5FF || productName.Contains("receiver", StringComparison.OrdinalIgnoreCase);
    public static IReadOnlyList<byte> Indices(bool receiver) => receiver ? [1, 2, 3, 4, 5, 6] : [0xFF];

    // A long request: [0x11, device, feature index, function << 4 | software id, parameters...], padded to 20 bytes.
    public static byte[] Request(byte device, byte feature, int function, params byte[] parameters)
    {
        var message = new byte[LongLength];
        message[0] = Long; message[1] = device; message[2] = feature; message[3] = (byte)((function << 4) | SoftwareId);
        Array.Copy(parameters, 0, message, 4, Math.Min(parameters.Length, LongLength - 4));
        return message;
    }

    // What a report says to a request: its parameters (bytes 4 on), an error code (HID++ 2.0's 0xFF or 1.0's 0x8F), or nothing
    // (another report: a movement, an event, another program's reply).
    public static (byte[]? Reply, int? Error) Match(byte[] report, byte device, byte feature, int function)
    {
        byte fsw = (byte)((function << 4) | SoftwareId);
        if (report.Length < 6 || report[0] is not (Short or Long) || report[1] != device) return (null, null);
        if (report[2] == feature && report[3] == fsw) return (report[4..], null);
        if (report[0] == Long && report[2] == 0xFF && report[3] == feature && report[4] == fsw) return (null, report[5]);
        if (report[0] == Short && report[2] == 0x8F && report[3] == feature && report[4] == fsw) return (null, report[5]);
        return (null, null);
    }

    // Root (feature index 0) function 0: the index of a feature, 0 when the device lacks it.
    public static byte[] FeatureParameters(int feature) => [(byte)(feature >> 8), (byte)feature];

    // The voltage curve device-battery-viewer uses for the Lightspeed mice' 0x1001: millivolts to percent between its points.
    private static readonly (int Millivolts, int Percent)[] Curve =
        [(4186, 100), (4067, 90), (3989, 80), (3922, 70), (3859, 60), (3811, 50), (3778, 40), (3751, 30), (3717, 20), (3671, 10), (3646, 5), (3579, 2), (3500, 0)];
    public static int VoltageToPercent(int millivolts)
    {
        if (millivolts >= Curve[0].Millivolts) return 100;
        for (int i = 1; i < Curve.Length; i++)
        {
            var (high, highPercent) = Curve[i - 1]; var (low, lowPercent) = Curve[i];
            if (millivolts >= low) return (int)Math.Round(lowPercent + (highPercent - lowPercent) * (millivolts - low) / (double)(high - low));
        }
        return 0;
    }

    // 0x1004 status (function 1) with its capabilities (function 0): the state of charge, or, where the device has none, the
    // level's step (full, good, low, critical); charging 1 to 3 (charging, slowly, complete on power).
    public static BatteryRead Unified(byte[] status, byte[]? capabilities)
    {
        int soc = status[0], bits = status[1], state = status[2];
        bool steps = capabilities is { Length: > 1 } && (capabilities[1] & 0x02) == 0;
        if (steps) soc = (bits & 8) != 0 ? 100 : (bits & 4) != 0 ? 50 : (bits & 2) != 0 ? 20 : 5;
        return new(Devices.Clamp(soc), state is >= 1 and <= 3, steps);
    }

    // 0x1000 function 0: the level (0 is no reading), and the status, 1 to 4 charging; 3 full.
    public static BatteryRead Status(byte[] reply) => new(reply[2] == 3 ? 100 : reply[0] > 0 ? Devices.Clamp(reply[0]) : null, reply[2] is >= 1 and <= 4);

    // 0x1001 function 0: millivolts big endian, and bit 7 of the flags for external power.
    public static BatteryRead Voltage(byte[] reply) => new(VoltageToPercent((reply[0] << 8) | reply[1]), (reply[2] & 0x80) != 0, Coarse: true);

    // 0x1F20 (the G headsets) function 0: millivolts big endian and flags, as Solaar reads them: bit 0x01 the measurement is
    // valid (without it the headset is off, which is no reading, not an empty battery), 0x02 charging, 0x07 full. The level
    // goes through the Lightspeed mice' curve above, not one for the headset's own cell (HeadsetControl calibrates each
    // headset), so it is coarse.
    public static BatteryRead? Adc(byte[] reply)
    {
        int millivolts = (reply[0] << 8) | reply[1], flags = reply[2];
        if ((flags & 0x01) == 0) return null;
        return new(flags == 0x07 ? 100 : VoltageToPercent(millivolts), (flags & 0x02) != 0, Coarse: true);
    }

    // HID++ 1.0 register 0x0D (battery mileage): the percentage, and the top two bits of byte 2, 1 charging and 2 full.
    public static byte[] RegisterRequest(byte device, byte register) => [Short, device, 0x81, register, 0, 0, 0];
    public static BatteryRead Mileage(byte[] reply) => new(Devices.Clamp(reply[0]), (reply[2] >> 6) is 1 or 2);
    // Register 0x07 (battery status): a level 1 to 7 and its status, 0x21, 0x24, 0x25 charging and 0x22, 0x26 full.
    public static BatteryRead Level(byte[] reply)
    {
        int percent = reply[0] switch { 1 => 5, 2 => 10, 3 => 20, 4 => 35, 5 => 50, 6 => 75, 7 => 100, _ => 0 };
        return new(reply[1] is 0x22 or 0x26 ? 100 : percent > 0 ? percent : null, reply[1] is 0x21 or 0x22 or 0x24 or 0x25 or 0x26, Coarse: true);
    }

    // 0x0005 function 2: the kind of device.
    public static DeviceKind Kind(int type) => type switch
    {
        0 or 2 => DeviceKind.Keyboard, 3 or 4 or 5 => DeviceKind.Mouse, 8 => DeviceKind.Headphones, 11 or 12 => DeviceKind.Controller, _ => DeviceKind.Other
    };
}

public static class Razer
{
    public const int Vendor = 0x1532, ReportLength = 90;
    // Byte 0 of a reply: 0x02 success, 0x01 busy and 0x04 no answer (try again), 0x03 failure, 0x05 not supported.
    public const byte Success = 0x02, Busy = 0x01, NoResponse = 0x04;
    public static readonly (byte Class, byte Id, byte Size) Battery = (0x07, 0x80, 0x02), ChargingState = (0x07, 0x84, 0x02);

    // The request with its checksum: XOR of bytes 2 to 87.
    public static byte[] Request(byte transaction, (byte Class, byte Id, byte Size) command)
    {
        var bytes = new byte[ReportLength];
        bytes[1] = transaction; bytes[5] = command.Size; bytes[6] = command.Class; bytes[7] = command.Id;
        bytes[88] = Checksum(bytes);
        return bytes;
    }
    public static byte Checksum(byte[] bytes) { byte crc = 0; for (int i = 2; i < 88; i++) crc ^= bytes[i]; return crc; }

    // A reply to the command: its status, and argument 1 (bytes 8 on are the arguments), or null when it is another report or
    // its checksum is wrong.
    public static (byte Status, byte Value)? Reply(byte[] bytes, (byte Class, byte Id, byte Size) command)
    {
        if (bytes.Length != ReportLength || Checksum(bytes) != bytes[88] || bytes[6] != command.Class || bytes[7] != command.Id) return null;
        return (bytes[0], bytes[9]);
    }
    public static int Percent(byte raw) => (raw * 100 + 127) / 255;

    // The catalog: the transaction id each model answers to, the interface to use, how long to wait between the request and the
    // reply (microseconds), and whether it reports charging. Values from razer-battery-report's catalog (derived from openrazer).
    public sealed record Model(int Product, string Name, byte Transaction, int Interface, int WaitMicroseconds, bool Charging);
    public static readonly IReadOnlyList<Model> Catalog =
    [
        new(0x001F, "Razer Naga Epic", 0xFF, 0, 600, true), new(0x0024, "Razer Mamba 2012", 0xFF, 0, 600, true), new(0x0025, "Razer Mamba 2012", 0xFF, 0, 600, true),
        new(0x0032, "Razer Ouroboros", 0xFF, 0, 600, true), new(0x003E, "Razer Naga Epic Chroma", 0xFF, 0, 600, true), new(0x003F, "Razer Naga Epic Chroma", 0xFF, 0, 600, true),
        new(0x0044, "Razer Mamba Chroma", 0xFF, 0, 600, true), new(0x0045, "Razer Mamba Chroma", 0xFF, 0, 600, true),
        new(0x0059, "Razer Lancehead", 0x3F, 0, 600, true), new(0x005A, "Razer Lancehead", 0x3F, 0, 600, true),
        new(0x0062, "Razer Atheris", 0x1F, 0, 400_000, false), new(0x006F, "Razer Lancehead Wireless", 0x1F, 0, 31_000, true), new(0x0070, "Razer Lancehead Wireless", 0x1F, 0, 31_000, true),
        new(0x0072, "Razer Mamba Wireless", 0x3F, 0, 31_000, true), new(0x0073, "Razer Mamba Wireless", 0x3F, 0, 31_000, true),
        new(0x0077, "Razer Pro Click", 0x1F, 0, 31_000, true), new(0x007A, "Razer Viper Ultimate", 0xFF, 0, 59_900, true), new(0x007B, "Razer Viper Ultimate", 0xFF, 0, 59_900, true),
        new(0x007C, "Razer DeathAdder V2 Pro", 0x3F, 0, 59_900, true), new(0x007D, "Razer DeathAdder V2 Pro", 0x3F, 0, 59_900, true),
        new(0x0080, "Razer Pro Click", 0x1F, 0, 31_000, true), new(0x0083, "Razer Basilisk X HyperSpeed", 0xFF, 0, 31_000, false),
        new(0x0086, "Razer Basilisk Ultimate", 0x1F, 0, 31_000, true), new(0x0088, "Razer Basilisk Ultimate", 0x1F, 0, 31_000, true),
        new(0x008F, "Razer Naga Pro", 0x1F, 0, 600, true), new(0x0090, "Razer Naga Pro", 0x1F, 0, 31_000, true),
        new(0x0094, "Razer Orochi V2", 0x1F, 0, 400_000, false), new(0x0095, "Razer Orochi V2", 0x1F, 0, 400_000, false),
        new(0x009A, "Razer Pro Click Mini", 0x1F, 0, 31_000, true), new(0x009C, "Razer DeathAdder V2 X HyperSpeed", 0x1F, 0, 31_000, false),
        new(0x009E, "Razer Viper Mini SE", 0x1F, 0, 59_900, true), new(0x009F, "Razer Viper Mini SE", 0x1F, 0, 59_900, true),
        new(0x00A5, "Razer Viper V2 Pro", 0x1F, 0, 31_000, true), new(0x00A6, "Razer Viper V2 Pro", 0x1F, 0, 31_000, true),
        new(0x00A7, "Razer Naga V2 Pro", 0x1F, 0, 31_000, true), new(0x00A8, "Razer Naga V2 Pro", 0x1F, 0, 31_000, true),
        new(0x00AA, "Razer Basilisk V3 Pro", 0x1F, 0, 31_000, true), new(0x00AB, "Razer Basilisk V3 Pro", 0x1F, 0, 31_000, true),
        new(0x00AF, "Razer Cobra Pro", 0x1F, 0, 31_000, true), new(0x00B0, "Razer Cobra Pro", 0x1F, 0, 31_000, true),
        new(0x00B3, "Razer HyperPolling Wireless Dongle", 0x1F, 0, 59_900, true), new(0x00B4, "Razer Naga V2 HyperSpeed", 0x1F, 0, 31_000, false),
        new(0x00B6, "Razer DeathAdder V3 Pro", 0x1F, 0, 31_000, true), new(0x00B7, "Razer DeathAdder V3 Pro", 0x1F, 0, 31_000, true),
        new(0x00B8, "Razer Viper V3 HyperSpeed", 0x1F, 0, 59_900, false), new(0x00B9, "Razer Basilisk V3 X HyperSpeed", 0x1F, 0, 31_000, false),
        new(0x00BE, "Razer DeathAdder V4 Pro", 0x1F, 0, 31_000, true), new(0x00BF, "Razer DeathAdder V4 Pro", 0x1F, 0, 31_000, true),
        new(0x00C0, "Razer Viper V3 Pro", 0x1F, 0, 31_000, true), new(0x00C1, "Razer Viper V3 Pro", 0x1F, 0, 59_900, true),
        new(0x00C2, "Razer DeathAdder V3 Pro", 0x1F, 0, 31_000, true), new(0x00C3, "Razer DeathAdder V3 Pro", 0x1F, 0, 31_000, true),
        new(0x00C4, "Razer DeathAdder V3 HyperSpeed", 0x1F, 0, 31_000, true), new(0x00C5, "Razer DeathAdder V3 HyperSpeed", 0x1F, 0, 31_000, true),
        new(0x00C7, "Razer Pro Click V2 Vertical Edition", 0x1F, 0, 31_000, true), new(0x00C8, "Razer Pro Click V2 Vertical Edition", 0x1F, 0, 31_000, true),
        new(0x00CC, "Razer Basilisk V3 Pro 35K", 0x1F, 0, 31_000, true), new(0x00CD, "Razer Basilisk V3 Pro 35K", 0x1F, 0, 31_000, true),
        new(0x00D0, "Razer Pro Click V2", 0x1F, 0, 31_000, true), new(0x00D1, "Razer Pro Click V2", 0x1F, 0, 31_000, true),
        new(0x00D3, "Razer Basilisk Mobile", 0x1F, 0, 31_000, false), new(0x00D4, "Razer Basilisk Mobile", 0x1F, 0, 31_000, false),
        new(0x00D6, "Razer Basilisk V3 Pro 35K Phantom Green Edition", 0x1F, 0, 31_000, true), new(0x00D7, "Razer Basilisk V3 Pro 35K Phantom Green Edition", 0x1F, 0, 31_000, true),
        new(0x00DA, "Razer Cobra HyperSpeed", 0x1F, 0, 31_000, true), new(0x00DB, "Razer Cobra HyperSpeed", 0x1F, 0, 31_000, true),
        new(0x00DE, "Razer Viper V3 Pro SE", 0x1F, 0, 31_000, true), new(0x00DF, "Razer Viper V3 Pro SE", 0x1F, 0, 59_900, true),
        new(0x00E2, "Razer Basilisk V4 Pro", 0x1F, 3, 59_900, true), new(0x00E3, "Razer Basilisk V4 Pro", 0x1F, 3, 59_900, true),
        new(0x00E5, "Razer Viper V4 Pro", 0x1F, 3, 59_900, true), new(0x00E6, "Razer Viper V4 Pro", 0x1F, 3, 59_900, true),
    ];
    public static Model? Find(int product) => Catalog.FirstOrDefault(model => model.Product == product);
}

// The 2.4 GHz mice of the Chinese brands, each a frame on a vendor collection.
public static class Dongles
{
    // ATK, VXE and VGN on Nordic receivers (VID 0x373B, usage page 0xFF05, report 0x08, 64 bytes): [checksum, 0x72, parameter
    // length, sequence, target (1 the mouse behind a receiver), command, payload length, payload...], the checksum the low byte
    // of the sum of the bytes after it. Battery is command 0x07.
    public const int AtkVendor = 0x373B;
    public static byte[] BitMouseRequest(byte command, byte target = 1)
    {
        var frame = new byte[64];
        frame[0] = 0x08; frame[2] = 0x72; frame[3] = 0x02; frame[4] = 0; frame[5] = target; frame[6] = command; frame[7] = 0x01;
        int sum = 0; for (int i = 2; i < 64; i++) sum += frame[i];
        frame[1] = (byte)sum;
        return frame;
    }
    // The reply: [0x08, 0x72, status (0 the mouse answered), _, target, command, length, percent, millivolts little endian]. A
    // wired mouse asked for itself gives 0 % at 0 mV, which is no reading.
    public static BatteryRead? BitMouseBattery(byte[] reply)
    {
        if (reply.Length < 10 || reply[0] != 0x08 || reply[1] != 0x72 || reply[5] != 0x07 || reply[2] != 0) return null;
        int percent = reply[7], millivolts = reply[8] | (reply[9] << 8);
        if (percent > 100 || percent == 0 && millivolts < 2000) return null;
        return new(percent);
    }
    public static bool? BitMouseCharging(byte[] reply) => reply.Length >= 8 && reply[0] == 0x08 && reply[1] == 0x72 && reply[5] == 0x0F && reply[2] == 0 ? reply[7] == 1 : null;

    // Compx receivers, report 0x08, 17 bytes: [0x08, command, status, address high, address low, length, data × 10, checksum],
    // the checksum making the sum of all seventeen bytes 0x55. Battery is command 0x04. atkctl documents the ATK A9 Ultra
    // (373B:1119 and 111A, usage page 0xFF04, the request sent as feature report 8); the other vendor and page (0x3554, 0xFF02:
    // VXE R1, VGN F1) are not from atkctl and are unverified on those receivers.
    public const int CompxVendor = 0x3554;
    public static byte[] CompxRequest(byte command)
    {
        var frame = new byte[17];
        frame[0] = 0x08; frame[1] = command;
        int sum = 0; for (int i = 0; i < 16; i++) sum += frame[i];
        frame[16] = (byte)(0x55 - sum);
        return frame;
    }
    // The reply (atkctl): [0x08, 0x04, ..., percent at 6, 1 at 7 while charging].
    public static BatteryRead? CompxBattery(byte[] reply)
        => reply.Length >= 8 && reply[0] == 0x08 && reply[1] == 0x04 && reply[6] <= 100 ? new(reply[6], reply[7] == 1) : null;

    // VGN 2.4 GHz keyboards (320F:5088, usage page 0xFF1C, report 0x04, 64 bytes): command 0x1A; the reply echoes it at byte 3,
    // the percentage at 8 and charging at 9.
    public const int VgnVendor = 0x320F;
    public static byte[] VgnRequest() { var frame = new byte[64]; frame[0] = 0x04; frame[3] = 0x1A; frame[4] = 0x06; return frame; }
    public static BatteryRead? VgnBattery(byte[] reply) => reply.Length >= 10 && reply[0] == 0x04 && reply[3] == 0x1A && reply[8] <= 100 ? new(reply[8], reply[9] != 0) : null;

    // Endgame Gear (VID 0x3367, usage page 0xFF01, feature 0xA1): A1 B4; byte 1 is 0x01 for a reading, the percentage at 16.
    public const int EndgameVendor = 0x3367;
    public static byte[] EndgameRequest(int length) { var frame = new byte[Math.Max(2, length)]; frame[0] = 0xA1; frame[1] = 0xB4; return frame; }
    public static BatteryRead? EndgameBattery(byte[] reply) => reply.Length >= 17 && reply[0] == 0xA1 && reply[1] == 0x01 && reply[16] <= 100 ? new(reply[16]) : null;

    // VAXEE (VID 0x3057, usage page 0xFF05, feature 0x0E): 0E A5 0B 01 01; byte 5 is the level 0 to 20, five points each.
    public const int VaxeeVendor = 0x3057;
    public static byte[] VaxeeRequest(int length) { var frame = new byte[Math.Max(5, length)]; frame[0] = 0x0E; frame[1] = 0xA5; frame[2] = 0x0B; frame[3] = 0x01; frame[4] = 0x01; return frame; }
    public static BatteryRead? VaxeeBattery(byte[] reply) => reply.Length >= 6 && reply[0] == 0x0E && reply[5] <= 20 ? new(reply[5] * 5, Coarse: true) : null;

    // Nordic's own receivers (VID 0x1915, usage page 0xFFA0, feature 0x05): 05 15 00 00 01; the percentage at 9.
    public const int NordicVendor = 0x1915;
    public static byte[] NordicRequest(int length) { var frame = new byte[Math.Max(5, length)]; frame[0] = 0x05; frame[1] = 0x15; frame[4] = 0x01; return frame; }
    public static BatteryRead? NordicBattery(byte[] reply) => reply.Length >= 10 && reply[0] == 0x05 && reply[9] <= 100 ? new(reply[9]) : null;

    // Inphic IN9 (248A:5B2E cable, 5B2F receiver; on 2.4 GHz vendor 0x249A, whose product is not fixed; feature 0x0C, 33
    // bytes): [0x0C, 0x01, command, 0, sequence, payload...]; 0xF0 the handshake (payload 01 01 FF), 0x20 the battery, the two
    // with one sequence number. The reply echoes 0x20 at 2, is valid with byte 6 set, the percentage at 0x12, 2 at 0x13 charging.
    public const int InphicVendor = 0x248A, InphicWireless = 0x249A;
    public static readonly byte[] InphicHandshake = [0x01, 0x01, 0xFF];
    public static byte[] InphicRequest(byte command, byte sequence, params byte[] payload)
    {
        var frame = new byte[33];
        frame[0] = 0x0C; frame[1] = 0x01; frame[2] = command; frame[4] = sequence;
        Array.Copy(payload, 0, frame, 5, Math.Min(payload.Length, frame.Length - 5));
        return frame;
    }
    public static BatteryRead? InphicBattery(byte[] reply) => reply.Length >= 0x14 && reply[0] == 0x0C && reply[2] == 0x20 && reply[6] != 0 && reply[0x12] <= 100 ? new(reply[0x12], reply[0x13] == 2) : null;

    // MCHOSE mice (VID 0x3837, or Realtek's 0x0BDA:0x4762; usage page 0xFF01 or 0xFF0B; output report 0x4D, 64 bytes):
    // [0x4D, 0x01, flags 1 (checksummed), length, command low, command high, 0, 0, data..., XOR of bytes 2 to 7 + length].
    // Device info is command 0x0900: its payload (from byte 8) holds the connection at 10, charging at 11 and the level at 12;
    // a level of 0 means the mouse is not connected, and a first byte of 0xFF that the receiver is not ready.
    public const int MchoseVendor = 0x3837;
    public static byte[] MchoseRequest()
    {
        var frame = new byte[64];
        frame[0] = 0x4D; frame[1] = 0x01; frame[2] = 0x01; frame[3] = 0x00; frame[4] = 0x00; frame[5] = 0x09;
        byte xor = 0; for (int i = 2; i < 8; i++) xor ^= frame[i];
        frame[8] = xor;
        return frame;
    }
    // The MCHOSE devices the mouse protocol is sent to: those that name themselves a mouse (its keyboards, headsets and the
    // MagDock 3837:1012 speak other protocols).
    public static bool IsMchoseMouse(int product, string name) => product is not (0x1012 or 0x6045)
        && (name.Contains("mouse", StringComparison.OrdinalIgnoreCase) || name.Contains("鼠标", StringComparison.Ordinal));
    public static BatteryRead? MchoseBattery(byte[] reply)
    {
        if (reply.Length < 21 || reply[0] != 0x4D || reply[4] != 0x00 || reply[5] != 0x09 || reply[8] == 0xFF) return null;
        int level = reply[8 + 12];
        return level is > 0 and <= 100 ? new(level, reply[8 + 11] != 0) : null;
    }
}

// Wireless headsets on their own dongles. Each is a request on an interface and a reply with the level.
public static class Headsets
{
    public const int SteelSeries = 0x1038, HyperXHp = 0x03F0, HyperXKingston = 0x0951;
    public enum Kind { Arctis7, Arctis9, Nova7Percent, Nova7Steps, NovaPro, CloudAlpha, CloudII, CloudFlight, MchoseX9 }

    // Which headset a vendor and product are, and the interface (or usage page) its battery lives on.
    public static Kind? Find(int vendor, int product) => (vendor, product) switch
    {
        (SteelSeries, 0x1260 or 0x12AD or 0x1252 or 0x1280) => Kind.Arctis7,
        (SteelSeries, 0x12C2 or 0x12C4) => Kind.Arctis9,
        (SteelSeries, 0x22A1 or 0x227E or 0x2258 or 0x229E or 0x22AD or 0x22A9 or 0x22A5) => Kind.Nova7Percent,
        (SteelSeries, 0x2202 or 0x2206 or 0x223A or 0x227A or 0x22A4) => Kind.Nova7Steps,
        (SteelSeries, 0x12E0 or 0x12E5) => Kind.NovaPro,
        (HyperXHp, 0x098D) => Kind.CloudAlpha,
        (HyperXHp, 0x0696 or 0x018B) => Kind.CloudII,
        (HyperXKingston, 0x16C4 or 0x1723) => Kind.CloudFlight,
        (Dongles.MchoseVendor, 0x6045) => Kind.MchoseX9,
        _ => null
    };
    // The interface (MI_xx) to talk on, or the usage page when that is what tells it apart; -1 for either.
    // VendorPages: any vendor-defined usage page (0xFF00 and up), so a request never goes to the headset's consumer or audio
    // control collections.
    public const int VendorPages = -2;
    public static (int Interface, int UsagePage) Channel(Kind kind) => kind switch
    {
        Kind.Arctis7 => (5, -1), Kind.Arctis9 => (0, -1), Kind.Nova7Percent or Kind.Nova7Steps => (-1, 0xFFC0), Kind.NovaPro => (4, -1),
        Kind.CloudII => (-1, 0xFF90), Kind.MchoseX9 => (-1, 0xFF90), _ => (-1, VendorPages)
    };

    // The requests, report id first.
    public static byte[] Request(Kind kind) => kind switch
    {
        Kind.Arctis7 => [0x06, 0x18],
        Kind.Arctis9 => [0x00, 0x20],
        Kind.Nova7Percent or Kind.Nova7Steps => [0x00, 0xB0],
        Kind.NovaPro => Padded([0x06, 0xB0], 31),
        Kind.CloudAlpha => Padded([0x21, 0xBB, 0x0B], 31),
        Kind.CloudII => Padded([0x06, 0xFF, 0xBB, 0x02], 52),
        Kind.CloudFlight => Padded([0x21, 0xFF, 0x05], 20),
        Kind.MchoseX9 => Padded([0x55, 0x65, 0x01], 64),
        _ => []
    };
    // A request asking whether the headset is connected to its dongle at all, where the level alone would not tell (a dongle
    // whose headset is off may still answer with a level).
    public static byte[]? ConnectionRequest(Kind kind) => kind switch { Kind.CloudAlpha => Padded([0x21, 0xBB, 0x03], 31), _ => null };
    // The answer: false when the headset is not connected (Cloud Alpha: 0x01 at 3); null for another report.
    public static bool? Connected(Kind kind, byte[] r) => kind switch
    {
        Kind.CloudAlpha when r.Length >= 4 && r[0] == 0x21 && r[1] == 0xBB && r[2] == 0x03 => r[3] != 0x01,
        _ => null
    };
    // A second request for the charging state, where it comes apart from the level.
    public static byte[]? ChargingRequest(Kind kind) => kind switch
    {
        Kind.CloudAlpha => Padded([0x21, 0xBB, 0x0C], 31), Kind.CloudII => Padded([0x06, 0xFF, 0xBB, 0x03], 52), _ => null
    };
    private static byte[] Padded(byte[] head, int length) { var frame = new byte[length]; head.CopyTo(frame, 0); return frame; }

    // The reading in a reply; null for another report, or a headset that is off or not connected.
    public static BatteryRead? Battery(Kind kind, byte[] r) => kind switch
    {
        // The replies are read at the documented offsets (none of them is documented to echo its request); a value outside its
        // range is no reading, never an empty battery. The level of the Arctis 9 runs from 0x64 to 0x9A. The queue is flushed
        // before each request (Hid.Transact), so the first report after it is the reply, as HeadsetControl reads it; another
        // report (a volume change) arriving in between could still be read as a level, which is not verified on a headset.
        // The offsets count the report id at 0, so a report of another id (a button, padded with zeros) is passed over.
        Kind.Arctis7 when r.Length >= 3 && r[0] == 0x06 && r[2] <= 100 => new(r[2]),
        Kind.Arctis9 when r.Length >= 5 && r[3] is >= 0x64 and <= 0x9A => new(Devices.Clamp((int)Math.Round((r[3] - 0x64) * 100.0 / (0x9A - 0x64))), r[4] == 0x01),
        Kind.Nova7Percent when r.Length >= 4 && r[3] != 0 && r[2] <= 100 => new(r[2], r[3] is 0x01 or 0x02),
        Kind.Nova7Steps when r.Length >= 4 && r[3] != 0 && r[2] <= 4 => new(r[2] * 25, r[3] is 0x01 or 0x02, Coarse: true),
        Kind.NovaPro when r.Length >= 16 && r[0] == 0x06 && r[15] != 0x01 && r[6] <= 8 => new((int)Math.Round(r[6] * 12.5), r[15] == 0x02, Coarse: true),
        Kind.CloudAlpha when r.Length >= 4 && r[0] == 0x21 && r[1] == 0xBB && r[2] == 0x0B && r[3] <= 100 => new(r[3]),
        Kind.CloudII when r.Length >= 8 && r[0] == 0x06 && r[1] == 0xFF && r[2] == 0xBB && r[3] == 0x02 && r[7] <= 100 => new(r[7]),
        // Millivolts; above 4107 the headset is on its cable. The level is a line between 3648 and 3975 mV.
        Kind.CloudFlight when r.Length >= 5 && r[0] == 0x21 && r[1] == 0xFF && r[2] == 0x05 && ((r[3] << 8) | r[4]) is var mv && mv > 3000
            => new(mv > 4107 ? null : Devices.Clamp((int)Math.Round((mv - 3648) * 100.0 / (3975 - 3648))), mv > 4107, Coarse: true),
        // 55 65 VV FF: a value and the field it belongs to. Only field 0x02 is the battery; the others (a heartbeat, other
        // telemetry) are passed over. No field is documented to say charging.
        Kind.MchoseX9 when r.Length >= 4 && r[0] == 0x55 && r[1] == 0x65 && r[3] == 0x02 && r[2] <= 100 => new(r[2]),
        _ => null
    };
    public static bool? Charging(Kind kind, byte[] r) => kind switch
    {
        Kind.CloudAlpha when r.Length >= 4 && r[0] == 0x21 && r[1] == 0xBB && r[2] == 0x0C => r[3] == 1,
        Kind.CloudII when r.Length >= 5 && r[0] == 0x06 && r[1] == 0xFF && r[2] == 0xBB && r[3] == 0x03 => r[4] == 1,
        _ => null
    };

    public static string Name(Kind kind) => kind switch
    {
        Kind.Arctis7 => "SteelSeries Arctis 7", Kind.Arctis9 => "SteelSeries Arctis 9", Kind.Nova7Percent or Kind.Nova7Steps => "SteelSeries Arctis Nova 7",
        Kind.NovaPro => "SteelSeries Arctis Nova Pro Wireless", Kind.CloudAlpha => "HyperX Cloud Alpha Wireless", Kind.CloudII => "HyperX Cloud II Wireless",
        Kind.CloudFlight => "HyperX Cloud Flight", _ => "MCHOSE X9"
    };
    public static DeviceSource Source(Kind kind) => kind switch
    {
        Kind.CloudAlpha or Kind.CloudII or Kind.CloudFlight => DeviceSource.HyperX, Kind.MchoseX9 => DeviceSource.Hid, _ => DeviceSource.SteelSeries
    };
}

// PlayStation controllers: their battery rides in every full input report, so it is read without asking.
public static class PlayStation
{
    public const int Vendor = 0x054C;
    public static bool IsDualSense(int product) => product is 0x0CE6 or 0x0DF2;
    public static bool IsDualShock(int product) => product is 0x05C4 or 0x09CC or 0x0BA0;

    // The status byte of a report: DualSense at 53 on USB (report 0x01) and 54 on Bluetooth (0x31); DualShock 4 at 30 on USB
    // (0x01) and 32 on Bluetooth (0x11). The link is the collection's, not the report's length: Windows pads every report to the
    // collection's longest, so a short Bluetooth report 0x01 (sent before a program asks for full reports) is as long as a USB
    // one, and carries no battery.
    public static BatteryRead? Battery(int product, byte[] r, bool bluetooth)
    {
        if (IsDualSense(product))
        {
            int at = !bluetooth && r.Length >= 64 && r[0] == 0x01 ? 53 : bluetooth && r.Length >= 78 && r[0] == 0x31 ? 54 : -1;
            if (at < 0) return null;
            int level = r[at] & 0x0F, state = r[at] >> 4;
            return state switch
            {
                // 0x2 is full (hid-playstation): on its cable, no longer charging.
                0x0 => new(Math.Min(level * 10 + 5, 100)), 0x1 => new(Math.Min(level * 10 + 5, 100), true), 0x2 => new(100), _ => null
            };
        }
        if (IsDualShock(product))
        {
            int at = !bluetooth && r.Length >= 64 && r[0] == 0x01 ? 30 : bluetooth && r.Length >= 78 && r[0] == 0x11 ? 32 : -1;
            if (at < 0) return null;
            int level = r[at] & 0x0F; bool cable = (r[at] & 0x10) != 0;
            // On its cable: 0 to 10 charging, 11 full (hid-playstation), no longer charging.
            if (cable) return level <= 9 ? new(level * 10 + 5, true) : level == 10 ? new(100, true) : level == 11 ? new(100) : null;
            return level <= 10 ? new(Math.Min(level * 10 + 5, 100)) : null;
        }
        return null;
    }
}

// Only feature reports are read: a battery usage carried in input reports alone (many Bluetooth keyboards and mice, USI and
// Wacom pens) sits in a collection Windows keeps to itself, which no program can read; those devices show through Windows'
// own Bluetooth level instead, or not at all.
// The HID battery usages any device may carry: Battery Strength (Generic Device Controls 0x06/0x20, Digitizer 0x0D/0x3B), and the
// Battery System page's state of charge (0x85/0x65 absolute, 0x66 remaining). The value is scaled by its logical range.
public static class HidBattery
{
    public static bool IsBattery(int page, int usage) => (page, usage) is (0x06, 0x20) or (0x0D, 0x3B) or (0x85, 0x65) or (0x85, 0x66);
    public static int? Percent(int value, int logicalMin, int logicalMax)
    {
        if (logicalMax <= logicalMin) logicalMax = logicalMin + 100;
        if (value < logicalMin || value > logicalMax) return null;
        return logicalMin == 0 && logicalMax == 100 ? value : Devices.Clamp((int)Math.Round((value - logicalMin) * 100.0 / (logicalMax - logicalMin)));
    }
}

// AirPods and Beats: their charge rides in Apple's proximity pairing advertisement (manufacturer data of company 0x004C, type
// 0x07, length 0x19), in steps of ten; written from the layout OpenPods and AirPodsDesktop read (facts only). Byte 6 holds the
// buds' levels, byte 7 the charging bits (high nibble) and the case (low); 15 is no reading. With byte 5's bit 0x20 set the
// left bud is byte 6's low nibble; clear (flipped), its high one. Single-unit models (AirPods Max, the one-piece Beats) carry
// their one level in byte 6's low nibble.
public static class AppleAdvert
{
    public const ushort Company = 0x004C;
    public static string? Model(byte[] data) => data.Length >= 5 ? ((data[3] << 8) | data[4]) switch
    {
        0x0220 => "AirPods", 0x0F20 => "AirPods", 0x1320 => "AirPods", 0x0E20 => "AirPods Pro", 0x1420 or 0x2420 => "AirPods Pro", 0x2720 => "AirPods Pro",
        0x0A20 => "AirPods Max", 0x0B20 => "Powerbeats Pro", 0x0520 => "BeatsX", 0x1020 => "Beats Flex", 0x0620 => "Beats Solo", 0x0920 => "Beats Studio",
        0x0320 => "Powerbeats", _ => null
    } : null;

    private static bool IsProximity(byte[] data) => data.Length >= 8 && data[0] == 0x07 && data[1] == 0x19;
    private static int? Level(int nibble) => nibble <= 10 ? nibble * 10 : null;
    // One piece, without buds or a case of its own: AirPods Max, Beats Solo, Studio and Flex, BeatsX, Powerbeats 3.
    public static bool IsSingle(byte[] data) => data.Length >= 5 && ((data[3] << 8) | data[4]) is 0x0A20 or 0x0620 or 0x0920 or 0x0520 or 0x1020 or 0x0320;

    // A single-unit model's one level, and charging (bit 0x01); null for earbuds, another advertisement, or no reading.
    public static BatteryRead? Single(byte[] data)
        => IsProximity(data) && IsSingle(data) && Level(data[6] & 0x0F) is { } level ? new(level, (data[7] & 0x10) != 0, Coarse: true) : null;

    // The parts' levels: left, right and the case; empty for a single-unit model (Single), null when this is not a proximity
    // pairing advertisement.
    public static IReadOnlyList<PartBattery>? Parts(byte[] data)
    {
        if (!IsProximity(data)) return null;
        if (IsSingle(data)) return [];
        bool flipped = (data[5] & 0x20) == 0;
        int high = data[6] >> 4, low = data[6] & 0x0F, caseLevel = data[7] & 0x0F, charging = data[7] >> 4;
        int left = flipped ? high : low, right = flipped ? low : high;
        bool leftCharging = (charging & (flipped ? 0x02 : 0x01)) != 0, rightCharging = (charging & (flipped ? 0x01 : 0x02)) != 0, caseCharging = (charging & 0x04) != 0;
        var parts = new List<PartBattery>();
        if (Level(left) is not null || Level(right) is not null)
        {
            parts.Add(new(BatteryPart.Left, Level(left), leftCharging));
            parts.Add(new(BatteryPart.Right, Level(right), rightCharging));
        }
        if (Level(caseLevel) is not null) parts.Add(new(BatteryPart.Case, Level(caseLevel), caseCharging));
        return parts;
    }
}
