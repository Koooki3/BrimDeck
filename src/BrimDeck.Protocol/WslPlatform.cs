namespace BrimDeck.Protocol;

// Which build of the WSL agent a distribution needs (W15). BrimDeck ships one per runtime identifier, under
// <BrimDeck>\wsl\<rid>\brimdeck-agent; the bridge's check script reports the distribution's machine and the program
// loaders it has (Probe), and Select picks the build from them. Logic without I/O, so that it can be tested.
public static class WslPlatform
{
    public const string AgentName = "brimdeck-agent";
    // Every build BrimDeck publishes (scripts/Publish-Release.ps1 publishes exactly these).
    public static readonly string[] Rids = ["linux-x64", "linux-arm64", "linux-musl-x64", "linux-musl-arm64"];
    // Before W15 the only build, linux-x64, lay directly in <BrimDeck>\wsl; still used for linux-x64 when its own folder is
    // missing (a copy published the old way).
    public const string LegacyRid = "linux-x64";

    // One line for the check script: the machine (uname -m), then the file names of the program loaders found at exactly the
    // paths the builds name as their interpreter (the kernel looks nowhere else). The loader, not uname alone, tells the C
    // library, and also a 32-bit user space on a 64-bit kernel. Plain sh, no other tool.
    public const string Probe = "printf '%s' \"$(uname -m 2>/dev/null)\"; for f in /lib64/ld-linux-x86-64.so.2 /lib/ld-linux-aarch64.so.1 /lib/ld-musl-x86_64.so.1 /lib/ld-musl-aarch64.so.1; do [ -e \"$f\" ] && printf ' %s' \"${f##*/}\"; done; echo";

    // The build for a Probe line: Rid is null when none fits, and Machine and Libc then say why (Libc is "glibc", "musl" or
    // "" when neither loader of the machine is there). A distribution with both C libraries (musl installed beside glibc)
    // gets the glibc build, which is what its own programs use.
    public static (string? Rid, string Machine, string Libc) Select(string? probe)
    {
        var parts = (probe ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string machine = parts.Length > 0 ? parts[0] : "";
        string? arch = machine.ToLowerInvariant() switch
        {
            "x86_64" or "amd64" => "x64",
            "aarch64" or "arm64" => "arm64",
            _ => null
        };
        if (arch is null) return (null, machine, "");
        var loaders = parts.Skip(1).ToHashSet(StringComparer.Ordinal);
        if (loaders.Contains(arch == "x64" ? "ld-linux-x86-64.so.2" : "ld-linux-aarch64.so.1")) return ("linux-" + arch, machine, "glibc");
        if (loaders.Contains(arch == "x64" ? "ld-musl-x86_64.so.1" : "ld-musl-aarch64.so.1")) return ("linux-musl-" + arch, machine, "musl");
        return (null, machine, "");
    }

    // Where a build lies, relative to BrimDeck's folder.
    public static string AgentFile(string rid) => Path.Combine("wsl", rid, AgentName);
    public static string LegacyAgentFile => Path.Combine("wsl", AgentName);

    // The agent file for a build in BrimDeck's folder (baseDirectory), or null when this copy of BrimDeck does not include it.
    public static string? Find(string baseDirectory, string rid, Func<string, bool> exists)
    {
        var path = Path.Combine(baseDirectory, AgentFile(rid));
        if (exists(path)) return path;
        var legacy = Path.Combine(baseDirectory, LegacyAgentFile);
        return rid == LegacyRid && exists(legacy) ? legacy : null;
    }

    // Whether this copy of BrimDeck includes any build at all.
    public static bool AnyPackaged(string baseDirectory, Func<string, bool> exists)
        => Rids.Any(rid => Find(baseDirectory, rid, exists) is not null);
}
