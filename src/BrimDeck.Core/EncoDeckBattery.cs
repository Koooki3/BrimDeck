using System.Text.Json;

namespace BrimDeck.Core;

// EncoDeck, a separate program for earbuds and headphones that reads them over their makers' own Bluetooth protocols, can offer
// its reading on the named pipe EncoDeck.BatteryBridge: a client writes "read\n" and gets one line of JSON,
//   {"schema":1,"device":{"name","address","kind","connection"},"battery":{"left","right","case":{"level","charging"}}}
// Only this JSON passes between the two programs, no code. EncoDeck leaves out a part it has no level from the device for
// (its cached and assumed levels are never offered), and a device it has lost keeps its last levels, so a reading counts only
// while the connection is one EncoDeck reads the device over (Live).
public static class EncoDeckBattery
{
    public const string PipeName = "EncoDeck.BatteryBridge";
    public static readonly byte[] Request = "read\n"u8.ToArray();
    // The longest answer read; EncoDeck's is a few hundred bytes.
    public const int MaximumLength = 64 * 1024;
    private static readonly string[] Live = ["ControlReady", "UsbReceiverConnected", "UsbHeadsetConnected"];

    // The device EncoDeck reads now, or null when it reads none, has lost it, or the answer is not one it gives.
    public static DeviceBattery? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schema", out var schema) || !schema.TryGetInt32(out var version) || version != 1)
                return null;
            if (!root.TryGetProperty("device", out var device) || device.ValueKind != JsonValueKind.Object) return null;
            if (Text(device, "connection") is not { } connection || !Live.Contains(connection, StringComparer.OrdinalIgnoreCase)) return null;
            if (Text(device, "name") is not { Length: > 0 } name) return null;
            var battery = root.TryGetProperty("battery", out var b) && b.ValueKind == JsonValueKind.Object ? b : default;
            var left = Part(battery, "left"); var right = Part(battery, "right"); var @case = Part(battery, "case");
            var kind = Text(device, "kind") is { } named && Enum.TryParse<DeviceKind>(named, ignoreCase: true, out var given) && given is DeviceKind.Headphones or DeviceKind.Earbuds
                ? given : Devices.Named(name) ?? DeviceKind.Earbuds;
            var address = Address(Text(device, "address"));
            var reading = new DeviceBattery(address is not null ? "bt:" + address : "encodeck:" + name, name, kind, DeviceSource.EncoDeck, null) { Address = address };
            if (kind == DeviceKind.Headphones)
                return (left ?? right) is { } one ? reading with { Percent = one.Percent, Charging = one.Charging } : null;
            List<PartBattery> parts = [];
            if (left is { } l) parts.Add(new(BatteryPart.Left, l.Percent, l.Charging));
            if (right is { } r) parts.Add(new(BatteryPart.Right, r.Percent, r.Charging));
            if (@case is { } c) parts.Add(new(BatteryPart.Case, c.Percent, c.Charging));
            if (parts.Count == 0) return null;
            var buds = parts.Where(part => part.Part != BatteryPart.Case).ToList();
            return reading with
            {
                Percent = buds.Count > 0 ? buds.Min(part => part.Percent) : parts[0].Percent, Parts = parts,
                Charging = buds.Count > 0 ? buds.All(part => part.Charging) : parts[0].Charging
            };
        }
        catch (JsonException) { return null; }
    }

    private static (int Percent, bool Charging)? Part(JsonElement battery, string name)
    {
        if (battery.ValueKind != JsonValueKind.Object || !battery.TryGetProperty(name, out var part) || part.ValueKind != JsonValueKind.Object) return null;
        if (!part.TryGetProperty("level", out var level) || level.ValueKind != JsonValueKind.Number || !level.TryGetInt32(out var percent) || percent is < 0 or > 100) return null;
        return (percent, part.TryGetProperty("charging", out var charging) && charging.ValueKind == JsonValueKind.True);
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // EncoDeck gives the address as twelve hex digits ("AABBCCDDEEFF"); the colons of "AA:BB:..." are taken off as well.
    private static string? Address(string? address)
    {
        var digits = address?.Replace(":", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
        return digits is { Length: 12 } && digits.All(Uri.IsHexDigit) && digits != "000000000000" ? digits : null;
    }
}
