using BrimDeck.Core;

internal static class OffPeakTests
{
    public static void Run(string root, Action<string, bool> check)
    {
        check("off-peak times accept a colon, a full-width colon, four digits or an hour alone",
            OffPeakPeriod.TryParse("8:30", out int a) && a == 510 && OffPeakPeriod.TryParse("08：30", out int b) && b == 510
            && OffPeakPeriod.TryParse("0830", out int c) && c == 510 && OffPeakPeriod.TryParse(" 23 ", out int d) && d == 23 * 60);
        check("off-peak times past 23:59 or in another form are refused",
            !OffPeakPeriod.TryParse("24:00", out _) && !OffPeakPeriod.TryParse("8:60", out _) && !OffPeakPeriod.TryParse("8.30", out _) && !OffPeakPeriod.TryParse("", out _));
        check("off-peak times are written as two-digit hours and minutes", OffPeakPeriod.Format(65) == "01:05" && OffPeakPeriod.Format(23 * 60 + 59) == "23:59");

        var day = new DateTime(2026, 10, 27);
        var morning = new OffPeakPeriod { Start = 30, End = 8 * 60 + 30 };
        check("a period includes its start and excludes its end", morning.Contains(day.AddMinutes(30)) && morning.Contains(day.AddMinutes(8 * 60 + 29))
            && !morning.Contains(day.AddMinutes(8 * 60 + 30)) && !morning.Contains(day.AddMinutes(29)));
        var night = new OffPeakPeriod { Start = 22 * 60, End = 6 * 60 };
        check("a period whose end is earlier than its start runs past midnight", night.Contains(day.AddHours(23)) && night.Contains(day.AddHours(2))
            && !night.Contains(day.AddHours(6)) && !night.Contains(day.AddHours(12)));
        check("a period whose end equals its start covers the whole day", new OffPeakPeriod { Start = 720, End = 720 }.Contains(day.AddHours(1))
            && new OffPeakPeriod { Start = 720, End = 720 }.Contains(day.AddHours(12)));

        var entry = new AppEntry { QuotaSource = ProviderId.Codex, OffPeak = [morning, night] };
        check("an application is off-peak inside any of its periods", entry.IsOffPeak(day.AddHours(3)) && entry.IsOffPeak(day.AddHours(23)) && !entry.IsOffPeak(day.AddHours(12)));
        check("the next change is the end of the current period or the start of the next one",
            entry.NextOffPeakChange(day.AddHours(3)) == day.AddMinutes(8 * 60 + 30) && entry.NextOffPeakChange(day.AddHours(12)) == day.AddHours(22)
            && entry.NextOffPeakChange(day.AddHours(23)) == day.AddDays(1).AddMinutes(8 * 60 + 30) && entry.NextOffPeakChange(day.AddHours(22)) == day.AddDays(1).AddMinutes(8 * 60 + 30));
        var joined = new AppEntry { QuotaSource = ProviderId.Codex, OffPeak = [new OffPeakPeriod { Start = 0, End = 120 }, new OffPeakPeriod { Start = 60, End = 180 }] };
        check("a boundary inside another period is not a change", joined.NextOffPeakChange(day.AddMinutes(30)) == day.AddHours(3));
        check("without periods, or with one covering the whole day, nothing changes",
            new AppEntry().NextOffPeakChange(day) is null && new AppEntry { OffPeak = [new OffPeakPeriod { Start = 720, End = 720 }] }.NextOffPeakChange(day) is null);
        var copy = entry.Copy(); copy.OffPeak[0].Start = 0; copy.OffPeak.Add(new OffPeakPeriod());
        check("a copied application keeps its own off-peak periods", entry.OffPeak.Count == 2 && entry.OffPeak[0].Start == 30);
        var levels = new AppEntry { QuotaSource = ProviderId.Codex, OffPeakWarningColor = "123abc", OffPeakCriticalColor = "nope" };
        levels.Normalize();
        check("off-peak warning and critical colors are normalized, and an invalid one is left unset",
            levels.OffPeakWarningColor == "#123ABC" && levels.OffPeakCriticalColor == "");

        var store = new SettingsStore(Path.Combine(root, "off-peak"), "0.1.0");
        var settings = new DeckSettings();
        var first = settings.Apps[0];
        first.OffPeak = [new OffPeakPeriod { Start = 22 * 60, End = 6 * 60 }]; first.OffPeakColor = "7fd1c4";
        store.Save(settings);
        var loaded = store.Load().Entry(first.InstanceId)!;
        check("off-peak periods and color are saved and read back", loaded.OffPeak.Count == 1 && loaded.OffPeak[0].Start == 22 * 60 && loaded.OffPeak[0].End == 6 * 60
            && loaded.OffPeakColor == "#7FD1C4");
        var old = SettingsMigrations.Read("""{"AppVersion":"0.1.0","Apps":[{"QuotaSource":"Codex","OffPeak":null},{"QuotaSource":"Claude","OffPeak":[{"Start":-5,"End":5000}]}]}""", new Version(0, 1, 0), out _);
        check("a file without off-peak periods, or with times out of range, is read safely", old.Apps[0].OffPeak.Count == 0
            && old.Apps[1].OffPeak[0].Start == 0 && old.Apps[1].OffPeak[0].End == OffPeakPeriod.Day - 1);
    }
}
