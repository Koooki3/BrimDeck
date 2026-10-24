using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;
using BrimDeck.Native;

internal static class Program
{
    // Exercises the native adapters without starting the app, reading account
    // credentials, changing playback, or creating application settings.
    [STAThread]
    private static int Main()
    {
        try
        {
            Console.WriteLine($"Windows native smoke tests: {Environment.OSVersion.Version}");
            CheckWindowChrome();
            BrimDeck.MainWindow.CheckShadowLifetime();
            var (bounds, work, scale) = WindowsHost.PrimaryScreen();
            Check("primary display has usable bounds and DPI", bounds.Width > 0 && bounds.Height > 0
                && work.Width > 0 && work.Height > 0 && scale > 0);

            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
            {
                using var capture = ProcessLoopbackCapture.TryStart((uint)Environment.ProcessId);
                Check("unsupported process capture returns control to the session peak fallback", capture is null);
            }
            else Console.WriteLine("SKIP: unsupported process capture check requires Windows 10 build < 20348.");

            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void CheckWindowChrome()
    {
        var window = new Window { Width = 400, Height = 300, ShowInTaskbar = false };
        try
        {
            SettingsChrome.Apply(window);
            // Theme changes must also be safe before a native handle exists.
            SettingsChrome.SetTheme(window, true);
            Check("settings theme does not create a native handle", new WindowInteropHelper(window).Handle == IntPtr.Zero);
            var handle = new WindowInteropHelper(window).EnsureHandle();
            Check("settings window creates a native handle", handle != IntPtr.Zero);
            SettingsChrome.SetTheme(window, true);
            SettingsChrome.SetTheme(window, false);
            var chrome = WindowChrome.GetWindowChrome(window);
            Check("settings window remains resizable", window.ResizeMode == ResizeMode.CanResize && chrome.ResizeBorderThickness.Left > 0);
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                Check("Windows 10 settings use a square outer frame", chrome.CornerRadius == new CornerRadius(0));
            else
                Check("Windows 11 settings retain rounded corners", chrome.CornerRadius == new CornerRadius(12));
        }
        finally { window.Close(); }
    }

    private static void Check(string name, bool condition)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
