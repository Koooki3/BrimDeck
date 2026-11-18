using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using BrimDeck.Native;

internal static class CaptureChecks
{
    public static void Run(Action<string, bool> check)
    {
        var window = new Window { Title = "BrimDeck capture test", Width = 160, Height = 80,
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Black,
            ShowInTaskbar = false, ShowActivated = false, Topmost = true };
        window.SourceInitialized += (_, _) => WindowsHost.ConfigureOverlay(window, false);
        try
        {
            window.Show();
            var handle = new WindowInteropHelper(window).Handle;
            check("overlay delivers mouse clicks without activation", SendMessage(handle, 0x21, IntPtr.Zero, new IntPtr(0x02010001)) == new IntPtr(3));
            check("overlay keeps the no-activate style by default", (GetWindowLongPtrW(handle, -20).ToInt64() & 0x08000000) != 0);
            WindowsHost.SetCapturable(window, true);
            check("capturable overlay drops the no-activate style", (GetWindowLongPtrW(handle, -20).ToInt64() & 0x08000000) == 0);
            check("capturable transparent overlay supports WGC", CreateItem(handle) == 0);
            CheckForegroundDuringClick(handle, check);
            WindowsHost.SetCapturable(window, false);
            check("overlay takes the no-activate style back", (GetWindowLongPtrW(handle, -20).ToInt64() & 0x08000000) != 0);
        }
        finally { window.Close(); }
    }

    private static void CheckForegroundDuringClick(IntPtr panel, Action<string, bool> check)
    {
        var original = new Window { Width = 80, Height = 60, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
        var next = new Window { Width = 80, Height = 60, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
        bool interacting = true;
        using var host = new WindowsHost(WindowsHost.PrimaryMonitor, () => panel, () => null,
            interactingWithCapturePanel: () => interacting);
        try
        {
            original.Show(); next.Show();
            var first = new WindowInteropHelper(original).Handle;
            var second = new WindowInteropHelper(next).Handle;
            check("no foreground before the first reading stays unknown", host.ForegroundForRules(IntPtr.Zero) == IntPtr.Zero);
            check("capture click keeps the previous window across repeated empty readings and panel activation",
                host.ForegroundForRules(first) == first && host.ForegroundForRules(IntPtr.Zero) == first
                && host.ForegroundForRules(panel) == first && host.ForegroundForRules(IntPtr.Zero) == first);
            check("a real foreground switch takes effect immediately", host.ForegroundForRules(second) == second && host.ForegroundForRules(panel) == second);
            next.Hide();
            check("a hidden window cannot stand in for the foreground", host.ForegroundForRules(IntPtr.Zero) == IntPtr.Zero);
            host.ForegroundForRules(first);
            interacting = false;
            check("outside the capturable open panel normal detection resumes", host.ForegroundForRules(panel) == panel);
            interacting = true;
            check("a later interaction cannot reuse the discarded window", host.ForegroundForRules(IntPtr.Zero) == IntPtr.Zero);
        }
        finally { next.Close(); original.Close(); }
    }

    private static int CreateItem(IntPtr handle)
    {
        const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(name, name.Length, out var text));
        CaptureInterop factory;
        try { var iid = typeof(CaptureInterop).GUID; Marshal.ThrowExceptionForHR(RoGetActivationFactory(text, ref iid, out factory)); }
        finally { WindowsDeleteString(text); }
        try
        {
            var iid = new Guid("79c3f95b-31f7-4ec2-a464-632ef5d30760");
            int hr = factory.CreateForWindow(handle, ref iid, out var item);
            if (item != IntPtr.Zero) Marshal.Release(item);
            return hr;
        }
        finally { Marshal.ReleaseComObject(factory); }
    }
    [ComImport, Guid("3628e81b-3cac-4c60-b7f4-23ce0e0c3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface CaptureInterop
    {
        [PreserveSig] int CreateForWindow(IntPtr handle, ref Guid iid, out IntPtr item);
        [PreserveSig] int CreateForMonitor(IntPtr handle, ref Guid iid, out IntPtr item);
    }
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string value, int length, out IntPtr text);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr text);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr text, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out CaptureInterop factory);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
}
