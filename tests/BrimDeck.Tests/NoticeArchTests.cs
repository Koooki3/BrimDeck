using BrimDeck.Protocol;

// W15: the WSL agent build a distribution gets, by its processor and C library, and where BrimDeck finds that build.
internal static class NoticeArchTests
{
    public static void Run(Action<string, bool> check)
    {
        Selection(check);
        Unsupported(check);
        Probe(check);
        Packaged(check);
        Published(check);
    }

    private static void Selection(Action<string, bool> check)
    {
        check("W15: Ubuntu on x64 gets the glibc x64 build", WslPlatform.Select("x86_64 ld-linux-x86-64.so.2") == ("linux-x64", "x86_64", "glibc"));
        check("W15: Debian on ARM64 Windows gets the glibc arm64 build", WslPlatform.Select("aarch64 ld-linux-aarch64.so.1") == ("linux-arm64", "aarch64", "glibc"));
        check("W15: Alpine on x64 gets the musl x64 build", WslPlatform.Select("x86_64 ld-musl-x86_64.so.1") == ("linux-musl-x64", "x86_64", "musl"));
        check("W15: Alpine on ARM64 gets the musl arm64 build", WslPlatform.Select("aarch64 ld-musl-aarch64.so.1") == ("linux-musl-arm64", "aarch64", "musl"));
        check("W15: musl installed beside glibc still gets the glibc build", WslPlatform.Select("x86_64 ld-musl-x86_64.so.1 ld-linux-x86-64.so.2").Rid == "linux-x64");
        check("W15: a loader found twice (/lib and /lib64) changes nothing", WslPlatform.Select("x86_64 ld-linux-x86-64.so.2 ld-linux-x86-64.so.2").Rid == "linux-x64");
        check("W15: other spellings of the machine", WslPlatform.Select("amd64 ld-linux-x86-64.so.2").Rid == "linux-x64" && WslPlatform.Select("arm64 ld-linux-aarch64.so.1").Rid == "linux-arm64");
        // The check script's lines are split on line feeds and trimmed; a stray carriage return or spaces change nothing.
        check("W15: spaces and a carriage return around the line", WslPlatform.Select("  x86_64  ld-linux-x86-64.so.2 \r").Rid == "linux-x64");
        foreach (var probe in new[] { "x86_64 ld-linux-x86-64.so.2", "aarch64 ld-linux-aarch64.so.1", "x86_64 ld-musl-x86_64.so.1", "aarch64 ld-musl-aarch64.so.1" })
            check("W15: every selected build is one that is published (" + probe + ")", WslPlatform.Select(probe).Rid is { } rid && WslPlatform.Rids.Contains(rid));
    }

    private static void Unsupported(Action<string, bool> check)
    {
        check("W15: a 32-bit ARM distribution has no build, and says its machine", WslPlatform.Select("armv7l") == (null, "armv7l", ""));
        check("W15: so has riscv64", WslPlatform.Select("riscv64 ld-linux-riscv64-lp64d.so.1").Rid is null);
        // uname -m tells the kernel; a 32-bit user space on it lacks the 64-bit loader.
        check("W15: an aarch64 kernel with an armhf user space has no build", WslPlatform.Select("aarch64 ld-linux-armhf.so.3") == (null, "aarch64", ""));
        check("W15: an x64 machine with neither loader (no glibc or musl where the build looks)", WslPlatform.Select("x86_64") == (null, "x86_64", ""));
        check("W15: the other processor's loader does not count", WslPlatform.Select("x86_64 ld-linux-aarch64.so.1 ld-musl-aarch64.so.1").Rid is null);
        check("W15: no answer is no build, not a guess", WslPlatform.Select(null) == (null, "", "") && WslPlatform.Select("") == (null, "", ""));
    }

    private static void Probe(Action<string, bool> check)
    {
        check("W15: the probe is one line of plain sh, without carriage returns", !WslPlatform.Probe.Contains('\r') && !WslPlatform.Probe.Contains('\n'));
        check("W15: the probe prints the machine", WslPlatform.Probe.StartsWith("printf '%s' \"$(uname -m", StringComparison.Ordinal));
        // Select looks for these names; the probe must look for every one of them, at the interpreter path each build names
        // (read from the published files' ELF headers).
        foreach (var loader in new[] { "/lib64/ld-linux-x86-64.so.2", "/lib/ld-linux-aarch64.so.1", "/lib/ld-musl-x86_64.so.1", "/lib/ld-musl-aarch64.so.1" })
            check("W15: the probe looks for " + loader, WslPlatform.Probe.Contains(" " + loader + " ", StringComparison.Ordinal) || WslPlatform.Probe.Contains(" " + loader + ";", StringComparison.Ordinal));
    }

    private static void Packaged(Action<string, bool> check)
    {
        string app = Path.Combine("C:", "BrimDeck");
        string Of(string rid) => Path.Combine(app, "wsl", rid, "brimdeck-agent");
        string legacy = Path.Combine(app, "wsl", "brimdeck-agent");
        var all = WslPlatform.Rids.Select(Of).ToHashSet();
        check("W15: each build lies in wsl\\<rid>", WslPlatform.Rids.All(rid => WslPlatform.Find(app, rid, all.Contains) == Of(rid)));
        var x64Only = new HashSet<string> { Of("linux-x64") };
        check("W15: a build that is not included is not found", WslPlatform.Find(app, "linux-musl-arm64", x64Only.Contains) is null);
        // A copy published the old way: its one x64 file still serves x64 distributions, and only them.
        var old = new HashSet<string> { legacy };
        check("W15: the old wsl\\brimdeck-agent still serves linux-x64", WslPlatform.Find(app, "linux-x64", old.Contains) == legacy);
        check("W15: but never another build", WslPlatform.Find(app, "linux-arm64", old.Contains) is null && WslPlatform.Find(app, "linux-musl-x64", old.Contains) is null);
        var both = new HashSet<string> { legacy, Of("linux-x64") };
        check("W15: the new x64 build comes before a leftover old one", WslPlatform.Find(app, "linux-x64", both.Contains) == Of("linux-x64"));
        check("W15: packaged: any build, the old file included", WslPlatform.AnyPackaged(app, old.Contains) && WslPlatform.AnyPackaged(app, x64Only.Contains));
        check("W15: not packaged without any", !WslPlatform.AnyPackaged(app, _ => false));
    }

    // The release script publishes exactly the builds WslPlatform knows, each into its own folder.
    private static void Published(Action<string, bool> check)
    {
        var source = new DirectoryInfo(AppContext.BaseDirectory);
        while (source is not null && !File.Exists(Path.Combine(source.FullName, "BrimDeck.sln"))) source = source.Parent;
        if (source is null) { check("W15: the source tree is found", false); return; }
        var script = File.ReadAllText(Path.Combine(source.FullName, "scripts", "Publish-Release.ps1"));
        var list = System.Text.RegularExpressions.Regex.Match(script, @"foreach \(\$rid in ([^)]*)\)");
        var rids = list.Success ? System.Text.RegularExpressions.Regex.Matches(list.Groups[1].Value, "'([^']+)'").Select(match => match.Groups[1].Value).ToList() : [];
        check("W15: Publish-Release.ps1 publishes every build WslPlatform picks, and no other", rids.Order().SequenceEqual(WslPlatform.Rids.Order()));
        check("W15: each into wsl\\<rid>", script.Contains("\"wsl\\$rid\"", StringComparison.Ordinal) && script.Contains("-r $rid", StringComparison.Ordinal));
    }
}
