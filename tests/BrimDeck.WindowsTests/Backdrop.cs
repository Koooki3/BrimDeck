using System.Runtime.InteropServices;
using BrimDeck.LiquidGlass;

// A test fixture: a small opaque window of one color, so the capture tests see a desktop that holds still and whose pixels are
// known, whatever else is on the screen. Shown without activating (focus stays where it was) and topmost (an always-on-top
// window elsewhere would otherwise cover it); once DesktopSource.ExcludeOurs runs in this process it would leave such a window
// of ours out of the capture as it is shown, so the fixture puts itself back in it. Kept small and for as short as each test
// needs (under a few seconds each), in the top-left corner of the screen, then destroyed.
internal sealed class Backdrop : IDisposable
{
    public nint Handle { get; }
    public RECT Bounds { get; }
    public (byte R, byte G, byte B) Color { get; }
    private readonly nint _brush;

    public Backdrop(RECT bounds, byte r, byte g, byte b)
    {
        Bounds = bounds; Color = (r, g, b);
        _brush = CreateSolidBrush((uint)(r | g << 8 | b << 16));
        string name = "BrimDeckTestBackdrop" + ++_count;
        var type = new WindowClass
        {
            Size = Marshal.SizeOf<WindowClass>(), Procedure = GetProcAddress(GetModuleHandle("user32"), "DefWindowProcW"),
            Instance = GetModuleHandle(null), Background = _brush, Name = name
        };
        RegisterClassEx(ref type);
        // A tool window that never activates and stays on top (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST): no taskbar button,
        // no focus taken, not covered.
        // A tool window that never activates and stays on top (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST).
        Handle = CreateWindowEx(0x80 | 0x8000000 | 0x8, name, "BrimDeck test backdrop", 0x80000000, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0, 0, type.Instance, 0); // WS_POPUP
        if (Handle == 0) throw new InvalidOperationException("No backdrop window.");
        ShowWindow(Handle, 4); // SW_SHOWNOACTIVATE
        SetWindowPos(Handle, -1, 0, 0, 0, 0, 0x1 | 0x2 | 0x10); // HWND_TOPMOST; NOSIZE | NOMOVE | NOACTIVATE
        UpdateWindow(Handle);
        Pump();
        Include(Handle);
        // Composed and duplicated a frame or two later.
        Thread.Sleep(120);
        Pump();
    }

    private static int _count;

    // Back in the capture (WCA_EXCLUDED_FROM_DDA off).
    private static void Include(nint window)
    {
        var off = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(off, 0);
            var data = new CompositionAttribute { Attribute = 24, Data = off, Size = sizeof(int) };
            SetWindowCompositionAttribute(window, ref data);
        }
        finally { Marshal.FreeHGlobal(off); }
    }

    // The top-left corner of the primary monitor, width x height device pixels: kept small and out of the way, as the tests
    // run on a screen someone may be using.
    public static RECT Corner(int width, int height) => new(0, 0, width, height);

    // The fixture's messages (and the liquid glass's window hook's), handled.
    public static void Pump()
    {
        while (PeekMessage(out var message, 0, 0, 0, 1)) { TranslateMessage(ref message); DispatchMessage(ref message); }
    }

    public void Dispose()
    {
        DestroyWindow(Handle);
        DeleteObject(_brush);
        Pump();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public int Size; public uint Style; public nint Procedure; public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background; public string? Menu; public string Name; public nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct CompositionAttribute { public int Attribute; public nint Data; public int Size; }
    [DllImport("user32")] private static extern int SetWindowCompositionAttribute(nint window, ref CompositionAttribute data);
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nint W, L; public uint Time; public int X, Y; }
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WindowClass type);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(int exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32")] private static extern bool UpdateWindow(nint window);
    [DllImport("user32")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32")] private static extern bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);
    [DllImport("user32")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("gdi32")] private static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32")] private static extern bool DeleteObject(nint handle);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("kernel32", CharSet = CharSet.Ansi)] private static extern nint GetProcAddress(nint module, string name);
}
