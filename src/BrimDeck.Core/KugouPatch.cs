namespace BrimDeck.Core;

// Partial: every place holds either its original or its patched bytes, but not all the same, as when another tool changed
// only some of them. Unsupported: a place holds bytes of neither kind.
public enum KugouPatchState { Original, Patched, Partial, Unsupported }

// KuGou's libcef.dll ignores --remote-debugging-port. Nine changes in it open the DevTools port on 127.0.0.1:12233 each
// time KuGou starts: the port is written where CEF keeps its "not set" value, and the checks that would skip starting the
// DevTools server are removed. The table comes from Metabox-Nexus-PlayerCap (player/kugou/watchdog/watchdog.go,
// Copyright (c) 2026 VTB-LINK, MIT License); it was made for KuGou 20.1.22.27795 and matches 20.1.51.27967 (CEF 89).
// A place is written only while it holds its original or its patched bytes, and restoring writes the original bytes back,
// so no copy of the file is kept. Nothing outside the nine places is read or written, so changes other tools made
// elsewhere stay. Tools that patch KuGou the same way (LyricsWall, PlayerCap) write the same bytes.
public static class KugouPatch
{
    public const int Port = 12233;
    private sealed record Change(long Offset, byte[] Original, byte[] Patched);
    private static readonly Change[] Changes =
    [
        new(0x58C63EB, [0xC7, 0x06, 0xAA, 0xAA, 0xAA, 0xAA], [0xC7, 0x06, 0xC9, 0x2F, 0x00, 0x00]),
        new(0x58C6415, [0xE8, 0x96, 0xA9, 0xE9, 0xFC], [0x90, 0x90, 0x90, 0x90, 0x90]),
        new(0x58C6420, [0x0F, 0x87, 0x96, 0x01, 0x00, 0x00], [0x90, 0x90, 0x90, 0x90, 0x90, 0x90]),
        new(0x58C6428, [0x0F, 0x84, 0x8E, 0x01, 0x00, 0x00], [0x90, 0x90, 0x90, 0x90, 0x90, 0x90]),
        new(0x4BC180E, [0x8B, 0x95, 0x7C, 0x01, 0x00, 0x00], [0xBA, 0xC9, 0x2F, 0x00, 0x00, 0x90]),
        new(0x4BEDE41, [0x0F, 0x84, 0x70, 0x01, 0x00, 0x00], [0x90, 0x90, 0x90, 0x90, 0x90, 0x90]),
        new(0x4BEDE4C, [0x41, 0xC7, 0x07, 0xAA, 0xAA, 0xAA, 0xAA], [0x41, 0xC7, 0x07, 0xC9, 0x2F, 0x00, 0x00]),
        new(0x4BEDEB5, [0xE8, 0xF6, 0x2E, 0xB7, 0xFD], [0x90, 0x90, 0x90, 0x90, 0x90]),
        new(0x4BEDED0, [0x0F, 0x44, 0xDA], [0x90, 0x90, 0x90]),
    ];

    // A place that holds other bytes belongs to a build the table was not made for, or was changed by another tool in its
    // own way; writing there could undo that tool's change or break KuGou, so such a file is left alone.
    public static KugouPatchState Classify(Stream file)
    {
        bool original = true, patched = true;
        foreach (var change in Changes)
        {
            var bytes = Read(file, change);
            if (bytes is null) return KugouPatchState.Unsupported;
            bool isOriginal = bytes.AsSpan().SequenceEqual(change.Original), isPatched = bytes.AsSpan().SequenceEqual(change.Patched);
            if (!isOriginal && !isPatched) return KugouPatchState.Unsupported;
            original &= isOriginal; patched &= isPatched;
        }
        return patched ? KugouPatchState.Patched : original ? KugouPatchState.Original : KugouPatchState.Partial;
    }

    // Writes the patched or the original bytes where they are not there yet, and confirms them. A place that already holds
    // the wanted bytes is not written.
    public static void Write(Stream file, bool patch)
    {
        if (Classify(file) == KugouPatchState.Unsupported) throw new InvalidDataException("libcef.dll is not a known KuGou build.");
        bool written = false;
        foreach (var change in Changes)
        {
            var wanted = patch ? change.Patched : change.Original;
            if (Read(file, change)!.AsSpan().SequenceEqual(wanted)) continue;
            file.Position = change.Offset; file.Write(wanted); written = true;
        }
        if (!written) return;
        file.Flush();
        if (Classify(file) != (patch ? KugouPatchState.Patched : KugouPatchState.Original)) throw new IOException("libcef.dll did not keep the written bytes.");
    }

    private static byte[]? Read(Stream file, Change change)
    {
        if (file.Length < change.Offset + change.Original.Length) return null;
        file.Position = change.Offset;
        var bytes = new byte[change.Original.Length];
        file.ReadExactly(bytes);
        return bytes;
    }
}
