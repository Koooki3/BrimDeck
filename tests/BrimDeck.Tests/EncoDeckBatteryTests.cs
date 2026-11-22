using BrimDeck.Core;

// EncoDeck's answer on its pipe, as its battery bridge writes it, and how the reading joins the Bluetooth entry of the device.
static class EncoDeckBatteryTests
{
    private static string Answer(string device, string battery)
        => "{\"schema\":1,\"revision\":7,\"generatedAt\":\"2026-11-19T12:00:00Z\",\"device\":" + device + ",\"battery\":{" + battery + "}}\n";
    private static string Device(string name, string? address = "AABBCCDDEEFF", string kind = "Earbuds", string connection = "ControlReady")
        => "{\"name\":\"" + name + "\",\"address\":" + (address is null ? "null" : "\"" + address + "\"") + ",\"kind\":\"" + kind + "\",\"connection\":\"" + connection + "\"}";
    private static string Part(string name, int level, bool charging = false)
        => "\"" + name + "\":{\"level\":" + level + ",\"charging\":" + (charging ? "true" : "false") + ",\"source\":\"DeviceNotification\",\"timestamp\":\"2026-11-19T11:00:00Z\"}";

    public static void Run(Action<string, bool> check)
    {
        var buds = EncoDeckBattery.Parse(Answer(Device("OPPO Enco X4"), Part("left", 80) + "," + Part("right", 70) + "," + Part("case", 40, charging: true)));
        check("EncoDeck: earbuds give left, right and case", buds is { Source: DeviceSource.EncoDeck, Kind: DeviceKind.Earbuds, Parts.Count: 3 }
            && buds.Parts[0] == new PartBattery(BatteryPart.Left, 80, false) && buds.Parts[2] == new PartBattery(BatteryPart.Case, 40, true));
        check("EncoDeck: earbuds read as the lower bud, not the case", buds?.Percent == 70 && buds.Lowest == 70);
        check("EncoDeck: a charging case alone is not the earbuds charging", buds is { Charging: false });
        check("EncoDeck: the device keys by its Bluetooth address", buds is { Key: "bt:AABBCCDDEEFF", Address: "AABBCCDDEEFF" });

        var inCase = EncoDeckBattery.Parse(Answer(Device("OPPO Enco X4"), Part("left", 50, true) + "," + Part("right", 60, true)));
        check("EncoDeck: both buds charging is the earbuds charging", inCase is { Charging: true, Percent: 50 });
        var oneBud = EncoDeckBattery.Parse(Answer(Device("OPPO Enco X4"), Part("right", 65)));
        check("EncoDeck: one bud alone is listed", oneBud is { Percent: 65, Parts.Count: 1 } && oneBud.Parts[0].Part == BatteryPart.Right);
        var caseOnly = EncoDeckBattery.Parse(Answer(Device("OPPO Enco X4"), Part("case", 30)));
        check("EncoDeck: the case alone gives the reading", caseOnly is { Percent: 30, Parts.Count: 1 });

        var headphones = EncoDeckBattery.Parse(Answer(Device("WH-1000XM5", kind: "Headphones"), Part("left", 83, true)));
        check("EncoDeck: headphones are one level, without parts", headphones is { Kind: DeviceKind.Headphones, Percent: 83, Charging: true, Parts.Count: 0 });
        check("EncoDeck: an unknown kind falls back to the name", EncoDeckBattery.Parse(Answer(Device("Sony WH-1000XM5", kind: "Gadget"), Part("left", 60)))?.Kind
            == (Devices.Named("Sony WH-1000XM5") ?? DeviceKind.Earbuds));

        check("EncoDeck: a USB headset over its receiver counts",
            EncoDeckBattery.Parse(Answer(Device("Headset", address: null, kind: "Headphones", connection: "UsbReceiverConnected"), Part("left", 55))) is { Key: "encodeck:Headset", Address: null, Percent: 55 });
        check("EncoDeck: an address with colons is the same address",
            EncoDeckBattery.Parse(Answer(Device("Buds", address: "aa:bb:cc:dd:ee:ff"), Part("left", 55)))?.Key == "bt:AABBCCDDEEFF");
        check("EncoDeck: an address of zeros is none",
            EncoDeckBattery.Parse(Answer(Device("Buds", address: "000000000000"), Part("left", 55)))?.Key == "encodeck:Buds");

        foreach (var connection in new[] { "AudioConnected", "ControlConnecting", "ControlUnavailable", "Reconnecting", "Disconnected", "PairedNotConnected", "NotDetected", "DisconnectedByUser" })
            check("EncoDeck: no reading while " + connection, EncoDeckBattery.Parse(Answer(Device("OPPO Enco X4", connection: connection), Part("left", 80))) is null);
        check("EncoDeck: no reading without a device", EncoDeckBattery.Parse("{\"schema\":1,\"revision\":0,\"device\":null,\"battery\":{}}") is null);
        check("EncoDeck: no reading without levels", EncoDeckBattery.Parse(Answer(Device("OPPO Enco X4"), "")) is null);
        check("EncoDeck: a level out of range is left out", EncoDeckBattery.Parse(Answer(Device("OPPO Enco X4"), Part("left", 101) + "," + Part("right", 90))) is { Parts.Count: 1, Percent: 90 });
        check("EncoDeck: another schema is not read", EncoDeckBattery.Parse(Answer(Device("OPPO Enco X4"), Part("left", 80)).Replace("\"schema\":1", "\"schema\":2")) is null);
        check("EncoDeck: an answer that is not JSON is not read", EncoDeckBattery.Parse("busy") is null && EncoDeckBattery.Parse("") is null && EncoDeckBattery.Parse("[1]") is null);

        // Joined with Windows' own level of the same earbuds, EncoDeck's parts take the entry over, and Windows' comes back
        // when EncoDeck stops.
        var windows = new DeviceBattery("bt:AABBCCDDEEFF", "OPPO Enco X4", DeviceKind.Earbuds, DeviceSource.Bluetooth, 70) { Address = "AABBCCDDEEFF" };
        var roster = new DeviceRoster();
        roster.Set(DeviceSource.Bluetooth, [windows]);
        roster.Set(DeviceSource.EncoDeck, [buds!]);
        check("EncoDeck: its parts replace Windows' level of the same earbuds",
            roster.All is { Count: 1 } one && one[0].Source == DeviceSource.EncoDeck && one[0].Parts.Count == 3 && one[0].Key == "bt:AABBCCDDEEFF");
        var gatt = windows with { Source = DeviceSource.Gatt };
        check("EncoDeck: its parts come before the Bluetooth battery service",
            DeviceRoster.Merge([gatt, buds!]) is { Count: 1 } two && two[0].Source == DeviceSource.EncoDeck);
        roster.Set(DeviceSource.EncoDeck, []);
        check("EncoDeck: closed, Windows' level returns", roster.All is { Count: 1 } back && back[0].Source == DeviceSource.Bluetooth && back[0].Percent == 70);
        check("EncoDeck: the source is named", Devices.SourceName(DeviceSource.EncoDeck) == "EncoDeck");
    }
}
