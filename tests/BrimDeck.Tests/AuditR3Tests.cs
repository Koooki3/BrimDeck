using System.Diagnostics;
using BrimDeck.Core.Notices;
using BrimDeck.Protocol;

// Round three of the 2026-10-24 audit (group "r3"): cmd's call and start refused for "run", the pending limit taking from
// the application that floods by BrimDeck's own time, the agent reaching what WSL's brim-notify sent over the pipe, the
// combining-mark and flag-tag limits counted on what is kept, generic rights, BrimDeck's own names in a notices folder,
// and links told from other reparse points. Fixed clocks only.
internal static class AuditR3Tests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 6, 0, 0, TimeSpan.Zero);

    public static void Run(string root, Action<string, bool> check)
    {
        Builtins(check);
        PendingLimit(check);
        Links(check);
        Cleaning(check);
        Folder(root, check);
    }

    private static void Builtins(Action<string, bool> check)
    {
        check("r3 N2: call and start read their arguments again and are refused", WindowsPrograms.Reparses("call") && WindowsPrograms.Reparses("START")
            && !WindowsPrograms.Reparses("echo") && !WindowsPrograms.Reparses("callme"));
        check("r3 N2: they are no builtins run through cmd any more", !WindowsPrograms.IsBuiltin("call") && !WindowsPrograms.IsBuiltin("start") && WindowsPrograms.IsBuiltin("echo"));
    }

    private static NoticeInput Watch(string app, string key, DateTimeOffset? created = null) => new()
    {
        Origin = NoticeOrigin.Cli, AppKey = "cli::" + app, AppName = app, InstanceKey = "cli::" + app + ":" + key, ReplaceKey = key, Title = key, CreatedAt = created,
        Timing = new NoticeTiming { Kind = NoticeTimeKind.Stopwatch, StartsAt = T0, Native = true }
    };

    private static void PendingLimit(Action<string, bool> check)
    {
        var hub = new NoticeHub();
        // Set long ago in another application, before a script starts timers in a loop; the script's first one claims to be
        // from the future.
        var alarm = hub.Receive(Watch("clock", "tea"), T0).Notice!;
        var first = hub.Receive(Watch("loop", "w0", created: T0.AddYears(5)), T0.AddSeconds(1)).Notice!;
        Notice? last = null;
        NoticeOutcome? outcome = null;
        for (int i = 1; i <= NoticeHub.MaxPending + 3; i++)
        {
            outcome = hub.Receive(Watch("loop", "w" + i, created: T0.AddYears(-1)), T0.AddSeconds(1 + i));
            last = outcome.Notice;
        }
        check("r3 M1: the flooding application loses its own, the one received longest ago first, whatever it says it was created",
            hub.Find(alarm.Id) is not null && hub.Find(first.Id) is null && hub.Notices.Count(n => n.Timing is not null) == NoticeHub.MaxPending);
        check("r3 M1: the notice just received and accepted stays", outcome!.Receipt == NoticeReceipt.Accepted && hub.Find(last!.Id) is not null);
        var other = hub.Receive(Watch("other", "x"), T0.AddMinutes(10));
        check("r3 M1: a newcomer from another application is kept, the flood gives way", other.Receipt == NoticeReceipt.Accepted && hub.Find(other.Notice!.Id) is not null
            && hub.Find(alarm.Id) is not null);

        // Only pending notices and a small count limit: the plain notice just received is not pruned away.
        var small = new NoticeHub { MaxCount = 3 };
        for (int i = 0; i < 5; i++) small.Receive(Watch("loop", "s" + i), T0.AddSeconds(i));
        var plain = small.Receive(new NoticeInput { Origin = NoticeOrigin.Windows, AppKey = "windows:Mail", AppName = "Mail", InstanceKey = "m1", Title = "邮件" }, T0.AddMinutes(1));
        check("r3 M1: a notice accepted beyond the count limit is still there", plain.Receipt == NoticeReceipt.Accepted && small.Find(plain.Notice!.Id) is not null);
    }

    private static void Links(Action<string, bool> check)
    {
        var hub = new NoticeHub();
        // The agent was down: WSL's brim-notify sent over the pipe. Back up, the agent's -r updates that notice.
        var piped = hub.Receive(NoticeIntake.FromPipe(new NoticeMessage { AppName = "b", Distro = "Ubuntu", ReplaceId = "build", Title = "构建中" }, T0), T0).Notice!;
        var update = hub.Receive(NoticeIntake.FromAgent(new NoticeMessage { Origin = "cli", AppName = "b", ReplaceId = "build", Title = "构建完成" }, "Ubuntu", false, T0), T0.AddSeconds(1));
        check("r3 M2: the agent's -r reaches what the pipe delivered under its distribution", update.Receipt == NoticeReceipt.Updated && update.Notice == piped
            && piped.Title == "构建完成" && hub.Notices.Count == 1);
        var otherDistro = hub.Receive(NoticeIntake.FromAgent(new NoticeMessage { Origin = "cli", AppName = "b", ReplaceId = "build", Title = "x" }, "Debian", false, T0), T0.AddSeconds(2));
        check("r3 M2: but not what came under another distribution's name", otherDistro.Receipt == NoticeReceipt.Accepted && hub.Notices.Count == 2);

        // Both links have one: each reaches its own first.
        var both = new NoticeHub();
        var fromPipe = both.Receive(NoticeIntake.FromPipe(new NoticeMessage { AppName = "b", Distro = "Ubuntu", ReplaceId = "k", Title = "p" }, T0), T0).Notice!;
        var fromAgent = both.Receive(NoticeIntake.FromAgent(new NoticeMessage { Origin = "cli", AppName = "b", ReplaceId = "k", Title = "a" }, "Ubuntu", false, T0), T0.AddSeconds(1));
        // fromAgent took over the pipe's (no agent one existed yet), which is the agent's from then on; a pipe message after it
        // makes a new one, and the agent's next -r reaches its own, not that.
        var pipeAgain = both.Receive(NoticeIntake.FromPipe(new NoticeMessage { AppName = "b", Distro = "Ubuntu", ReplaceId = "k", Title = "p2" }, T0), T0.AddSeconds(2));
        string afterPipe = fromPipe.Title;
        var agentAgain = both.Receive(NoticeIntake.FromAgent(new NoticeMessage { Origin = "cli", AppName = "b", ReplaceId = "k", Title = "a2" }, "Ubuntu", false, T0), T0.AddSeconds(3));
        check("r3 M2: S3 still holds: a pipe sender never reaches what the agent delivered", fromAgent.Notice == fromPipe && pipeAgain.Receipt == NoticeReceipt.Accepted
            && pipeAgain.Notice != fromPipe && afterPipe == "a" && agentAgain.Notice == fromPipe && fromPipe.Title == "a2" && pipeAgain.Notice!.Title == "p2");
    }

    private static void Cleaning(Action<string, bool> check)
    {
        const string Four = "́́́́";
        string stacked = "a" + string.Concat(Enumerable.Repeat(Four + "​", 5));
        check("r3 L1: removed characters between combining marks do not start the count again", NoticeText.Clean(stacked) == "a" + Four
            && NoticeProtocol.Clip(stacked, 100) == "a" + Four && NoticeText.Clean("a" + Four + "ㅤ" + Four + "b" + Four) == "a" + Four + "b" + Four);
        check("r3 L1: marks after a kept letter count again", NoticeText.Clean("é" + Four + "x́") == "é" + Four + "x́");

        const string Flag = "\U0001F3F4";
        string Tags(int count) => string.Concat(Enumerable.Repeat("\U000E0061", count));
        const string Scotland = "\U0001F3F4\U000E0067\U000E0062\U000E0073\U000E0063\U000E0074\U000E007F";
        check("r3 L1: a subdivision flag keeps its tags", NoticeText.Clean(Scotland) == Scotland);
        check("r3 L1: a flag keeps at most MaxFlagTags tags", NoticeText.Clean(Flag + Tags(20) + "z") == Flag + Tags(NoticeProtocol.MaxFlagTags) + "z");
        var watch = Stopwatch.StartNew();
        var many = NoticeText.Clean(Flag + Tags(1990));
        check("r3 L1: a long run of tags is read in one pass", many == Flag + Tags(NoticeProtocol.MaxFlagTags) && watch.ElapsedMilliseconds < 500);
    }

    private static void Folder(string root, Action<string, bool> check)
    {
        const int GenericAll = 0x10000000, GenericWrite = 0x40000000, WriteData = 0x2, Delete = 0x10000;
        check("r3 L2: GENERIC_WRITE and GENERIC_ALL are read as the writes they grant", (NoticeHistory.SpecificRights(GenericWrite) & WriteData) != 0
            && (NoticeHistory.SpecificRights(GenericAll) & Delete) != 0 && NoticeHistory.SpecificRights(0x1200A9) == 0x1200A9);

        check("r3 M3: BrimDeck's own names in a notices folder", NoticeHistory.OwnName("notices.dat", false) && NoticeHistory.OwnName(".notices.0123abcd.tmp", false)
            && NoticeHistory.OwnName("0123456789ABCDEF01234567.png", true) && NoticeHistory.OwnName(".icon.abc.tmp", true));
        check("r3 M3: and nothing else", !NoticeHistory.OwnName("report.docx", false) && !NoticeHistory.OwnName("notices.dat", true)
            && !NoticeHistory.OwnName("0123456789ABCDEF0123456.png", true) && !NoticeHistory.OwnName("0123456789ABCDEF0123456Z.png", true));

        var plain = Path.Combine(root, "r3-plain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(plain);
        check("r3 L7: a plain folder is no link", !NoticeHistory.Linked(plain));
        // A junction needs no special right; without cmd's mklink this check has nothing to test.
        var junction = Path.Combine(root, "r3-junction-" + Guid.NewGuid().ToString("N"));
        if (MakeJunction(junction, plain))
        {
            check("r3 L7: a junction is still a link", NoticeHistory.Linked(junction));
            Directory.Delete(junction);
        }
        Directory.Delete(plain, true);
    }

    private static bool MakeJunction(string link, string target)
    {
        try
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            process.WaitForExit(10_000);
            return process.HasExited && process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }
}
