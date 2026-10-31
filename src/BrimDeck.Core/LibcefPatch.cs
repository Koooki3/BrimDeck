using System.IO;

namespace BrimDeck.Core;

// Every site holds either its original or its patched bytes, but not all the same, as when a patch was interrupted or
// another tool changed only some of them. Unsupported: a site is missing, is not unique, or is present in both forms.
public enum LibcefPatchState { Original, Patched, Partial, Unsupported }

// A change to a player's browser core (libcef.dll) that makes it open its DevTools port on 127.0.0.1 every time it starts.
// Each site is a short byte window — a fixed Prefix and Suffix bracketing a Core — found by searching the whole file, not by
// a fixed offset, so a build that only moved the code is still found. Only the Core is written; Prefix and Suffix are stable
// context that does not change between the original and the patched file, so a part-patched file still locates each site.
// A window must occur exactly once in the file; a build it no longer matches is reported Unsupported and left untouched.
public sealed class LibcefPatch
{
    // Original and Patched are the two forms of the Core and have the same length; Prefix and Suffix may be empty.
    public sealed record Site(byte[] Prefix, byte[] Original, byte[] Patched, byte[] Suffix)
    {
        public byte[] Window(bool patched) => [.. Prefix, .. (patched ? Patched : Original), .. Suffix];
    }

    private readonly Site[] _sites;
    public int Port { get; }
    public IReadOnlyList<Site> Sites => _sites;
    public LibcefPatch(int port, params Site[] sites) { Port = port; _sites = sites; }

    // Where each site's Core sits in this file and whether it already holds the patched bytes; null for a file no profile
    // fits (a site absent, found more than once, or present in both forms).
    private List<(Site Site, long Offset, bool Patched)>? Locate(byte[] file)
    {
        var found = new List<(Site, long, bool)>(_sites.Length);
        foreach (var site in _sites)
        {
            long original = UniqueIndex(file, site.Window(false)), patched = UniqueIndex(file, site.Window(true));
            if (original >= 0 && patched < 0) found.Add((site, original + site.Prefix.Length, false));
            else if (patched >= 0 && original < 0) found.Add((site, patched + site.Prefix.Length, true));
            else return null;
        }
        return found;
    }

    // The single offset of pattern in file, or -1 when it is absent or occurs more than once.
    private static long UniqueIndex(ReadOnlySpan<byte> file, ReadOnlySpan<byte> pattern)
    {
        int first = file.IndexOf(pattern);
        if (first < 0) return -1;
        return file[(first + 1)..].IndexOf(pattern) < 0 ? first : -1;
    }

    public LibcefPatchState Classify(Stream file) => Classify(ReadAll(file));
    private LibcefPatchState Classify(byte[] file)
    {
        if (Locate(file) is not { } sites) return LibcefPatchState.Unsupported;
        return sites.All(s => s.Patched) ? LibcefPatchState.Patched
            : sites.All(s => !s.Patched) ? LibcefPatchState.Original : LibcefPatchState.Partial;
    }

    // Writes the patched or the original Core where it is not there yet, and confirms the result. A site that already holds
    // the wanted bytes is not written; a file no profile fits is left alone.
    public void Write(Stream file, bool patch)
    {
        if (Locate(ReadAll(file)) is not { } sites) throw new InvalidDataException("libcef.dll is not a known build.");
        bool written = false;
        foreach (var (site, offset, patched) in sites)
        {
            if (patched == patch) continue;
            file.Position = offset; file.Write(patch ? site.Patched : site.Original); written = true;
        }
        if (!written) return;
        file.Flush();
        if (Classify(file) != (patch ? LibcefPatchState.Patched : LibcefPatchState.Original))
            throw new IOException("libcef.dll did not keep the written bytes.");
    }

    private static byte[] ReadAll(Stream file)
    {
        file.Position = 0;
        var bytes = new byte[file.Length];
        file.ReadExactly(bytes);
        return bytes;
    }
}
