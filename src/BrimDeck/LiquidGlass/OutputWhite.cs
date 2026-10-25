using System.Runtime.InteropServices;

namespace BrimDeck.LiquidGlass;

// The SDR white of a display, as a linear scRGB value (1 = 80 nits): an output in HDR composes the desktop in 16-bit float with
// SDR content at the white the user set ("SDR content brightness"), and the refraction, an 8-bit picture of that content, takes
// that white as its white. 1 where Windows does not say (an SDR display in advanced colour composes SDR white at 1).
internal static class OutputWhite
{
    public static float For(string gdiDeviceName)
    {
        try
        {
            const uint OnlyActivePaths = 2;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (GetDisplayConfigBufferSizes(OnlyActivePaths, out uint pathCount, out uint modeCount) != 0) return 1;
                var paths = new PathInfo[pathCount];
                var modes = new byte[modeCount * ModeInfoSize];
                int result = QueryDisplayConfig(OnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                const int InsufficientBuffer = 122;
                if (result == InsufficientBuffer) continue;
                if (result != 0) return 1;
                for (int i = 0; i < pathCount; i++)
                {
                    var source = new SourceName { Header = new Header { Type = GetSourceName, Size = Marshal.SizeOf<SourceName>(), Adapter = paths[i].SourceAdapter, Id = paths[i].SourceId } };
                    if (DisplayConfigGetDeviceInfo(ref source) != 0 || !string.Equals(source.GdiDeviceName, gdiDeviceName, StringComparison.OrdinalIgnoreCase)) continue;
                    var white = new SdrWhiteLevel { Header = new Header { Type = GetSdrWhiteLevel, Size = Marshal.SizeOf<SdrWhiteLevel>(), Adapter = paths[i].TargetAdapter, Id = paths[i].TargetId } };
                    if (DisplayConfigGetDeviceInfo(ref white) != 0 || white.Level == 0) return 1;
                    return Math.Clamp(white.Level / 1000f, .5f, 20f);
                }
                return 1;
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { }
        return 1;
    }

    private const int ModeInfoSize = 64, GetSourceName = 1, GetSdrWhiteLevel = 11;

    // DISPLAYCONFIG_PATH_INFO: the source's and target's adapter and id; the rest is not read.
    [StructLayout(LayoutKind.Explicit, Size = 72)]
    private struct PathInfo
    {
        [FieldOffset(0)] public long SourceAdapter;
        [FieldOffset(8)] public uint SourceId;
        [FieldOffset(20)] public long TargetAdapter;
        [FieldOffset(28)] public uint TargetId;
    }

    // LUIDs are two 32-bit halves: the native structures align them to 4 bytes.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct Header { public int Type, Size; public long Adapter; public uint Id; }

    [StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Unicode)]
    private struct SourceName { public Header Header; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string GdiDeviceName; }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SdrWhiteLevel { public Header Header; public uint Level; }

    [DllImport("user32")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);
    [DllImport("user32")] private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PathInfo[] paths, ref uint modeCount, [Out] byte[] modes, IntPtr topology);
    [DllImport("user32")] private static extern int DisplayConfigGetDeviceInfo(ref SourceName info);
    [DllImport("user32")] private static extern int DisplayConfigGetDeviceInfo(ref SdrWhiteLevel info);
}
