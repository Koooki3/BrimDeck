using BrimDeck.Core;

// The Phone Link Start card: the shapes it takes while the phone is connected, charging, away, and while the app has
// nothing to say. No phone and no Phone Link are needed to check the parse.
static class PhoneLinkCardTests
{
    private static string Card(params string[] fields) => "{\"body\":[" + string.Join(",", fields) + "]}";
    private static string Text(string text) => "{\"type\":\"TextBlock\",\"text\":\"" + text + "\"}";
    private static string Tip(string text) => "{\"type\":\"TextBlock\",\"tooltip\":\"" + text + "\"}";
    private static string Speak(string text) => "{\"type\":\"TextBlock\",\"speak\":\"" + text + "\"}";

    public static void Run(Action<string, bool> check)
    {
        var connected = PhoneLinkCard.Parse(Card(Text("Xiaomi 13 Ultra"), Text("87%"), Tip("正在充电")));
        check("Phone Link card: name, charge and charging",
            connected is { Name: "Xiaomi 13 Ultra", Percent: 87, Charging: true });
        check("Phone Link card: a charge without a charging word is not charging",
            PhoneLinkCard.Parse(Card(Text("Pixel 8"), Text("64%"), Tip("未充电"))) is { Percent: 64, Charging: false });
        check("Phone Link card: discharging is not charging",
            PhoneLinkCard.Parse(Card(Text("Pixel 8"), Text("31%"), Tip("Discharging"))) is { Charging: false });
        check("Phone Link card: a negation wins over a charging word",
            PhoneLinkCard.Parse(Card(Text("Pixel 8"), Text("52%"), Tip("未充电"), Tip("charging"))) is { Charging: false });
        check("Phone Link card: traditional Chinese charging",
            PhoneLinkCard.Parse(Card(Text("Pixel 8"), Text("74%"), Tip("正在充電"))) is { Charging: true });

        check("Phone Link card: the name comes from the spoken line",
            PhoneLinkCard.Parse(Card(Speak("Pixel 8 Pro - 已连接到你的电脑"), Text("90%"))) is { Name: "Pixel 8 Pro", Percent: 90 });
        check("Phone Link card: the spoken line's suffix is not the name",
            PhoneLinkCard.Parse(Card(Speak("Xiaomi 13 Ultra - connected to your PC"), Text("45%"))) is { Name: "Xiaomi 13 Ultra" });
        check("Phone Link card: interface text is never the name",
            PhoneLinkCard.Parse(Card(Text("短信"), Text("打开“手机连接”以重新连接移动设备"), Text("电池状态不可用"), Text("58%"))) is { Percent: 58 }
            && PhoneLinkCard.Parse(Card(Text("短信"), Text("58%"))) is { Name.Length: > 0 } unnamed && unnamed.Name != "短信");
        check("Phone Link card: what the phone is doing is never the name",
            PhoneLinkCard.Parse(Card(Text("83%"), Tip("正在充电"))) is { Percent: 83 } chargingOnly
            && chargingOnly.Name != "正在充电" && chargingOnly.Name != "charging");
        check("Phone Link card: a label with a figure is never the name",
            PhoneLinkCard.Parse(Card(Text("电池 - 83%"), Text("61%"))) is { Percent: 61 } labelled
            && !labelled.Name.Contains("电池", StringComparison.Ordinal));
        check("Phone Link card: a name that carries a word of the interface",
            PhoneLinkCard.Parse(Card(Text("Callie's iPhone"), Text("77%"))) is { Name: "Callie's iPhone", Percent: 77 });
        check("Phone Link card: a phone whose model is a word of the interface",
            PhoneLinkCard.Parse(Card(Speak("OnePlus Open - connected to your PC"), Text("63%"))) is { Name: "OnePlus Open", Percent: 63 });

        check("Phone Link card: a disconnected card has no reading",
            PhoneLinkCard.Parse(Card(Speak("Xiaomi 13 Ultra - 已从电脑断开连接"), Tip("电池状态不可用"))) is null);
        check("Phone Link card: a card without a percentage has no reading",
            PhoneLinkCard.Parse(Card(Text("Xiaomi 13 Ultra"), Text("短信"))) is null);
        check("Phone Link card: an empty or broken card has no reading",
            PhoneLinkCard.Parse(null) is null && PhoneLinkCard.Parse("") is null && PhoneLinkCard.Parse("{not json") is null
            && PhoneLinkCard.Parse("[]") is null);
        check("Phone Link card: the whole range is accepted, beyond it is not",
            PhoneLinkCard.Parse(Card(Text("X"), Text("0%"))) is { Percent: 0 }
            && PhoneLinkCard.Parse(Card(Text("X"), Text("100%"))) is { Percent: 100 }
            && PhoneLinkCard.Parse(Card(Text("X"), Text("140%"))) is null
            && PhoneLinkCard.Parse(Card(Text("X"), Text("87"))) is null);
        check("Phone Link card: a percentage with spaces and the first one win",
            PhoneLinkCard.Parse(Card(Text("87 %"), Text("40%"))) is { Percent: 87 });

        var stamp = new DateTime(2026, 11, 16, 14, 33, 27, DateTimeKind.Utc);
        check("Phone Link card: an unchanged write time needs no second read",
            PhoneLinkCard.IsCurrent(stamp, stamp, force: false)
            && !PhoneLinkCard.IsCurrent(stamp.AddSeconds(1), stamp, force: false)
            && !PhoneLinkCard.IsCurrent(stamp, null, force: false)
            && !PhoneLinkCard.IsCurrent(stamp, stamp, force: true));
        check("Phone Link card: an idle card is bounded by Phone Link running",
            !PhoneLinkCard.Expired(TimeSpan.FromMinutes(119)) && PhoneLinkCard.Expired(TimeSpan.FromMinutes(121))
            && !PhoneLinkCard.NeedsLiveness(TimeSpan.FromMinutes(29)) && PhoneLinkCard.NeedsLiveness(TimeSpan.FromMinutes(31)));
    }
}
