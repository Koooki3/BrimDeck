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
            CheckSettledClip();
            NoticeProtectionTests.Run(Check);
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

    // The collapsed island at rest clips to a rectangle (IslandBorder.SettledClip): nothing of it, its rim and the glass's edge
    // light included, may then reach past the rounded outline it shows with the rounded clip, at the notch's shoulders or the
    // capsule's ends.
    private static void CheckSettledClip()
    {
        foreach (var (name, corners, shoulder) in new[] { ("notch", new CornerRadius(0, 0, 12, 12), 16d), ("capsule", new CornerRadius(18), 0d) })
            foreach (string look in new[] { "", "edge light", "pixel bevel" })
                foreach (double scale in new[] { 1d, 1.5 })
                {
                    byte[] Draw(bool settled)
                    {
                        var island = new BrimDeck.IslandBorder { Width = 300, Height = 36, Background = System.Windows.Media.Brushes.Black, CornerRadius = corners,
                            ShoulderRadius = shoulder, MaterialOpacity = 1, RimWidth = 1, RimBrush = System.Windows.Media.Brushes.White, ClipToBounds = true,
                            SettledClip = settled };
                        if (look == "edge light") island.EdgeBrush = System.Windows.Media.Brushes.White;
                        // The pixel theme's stepped outline, whose rim is a bevel (light on the edges facing up and left).
                        if (look == "pixel bevel") { island.Pixel = 2; island.BevelBrush = System.Windows.Media.Brushes.Gray; }
                        island.Measure(new Size(300, 36)); island.Arrange(new Rect(0, 0, 300, 36)); island.UpdateLayout();
                        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(300 * scale), (int)(36 * scale), 96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
                        bitmap.Render(island);
                        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                        return pixels;
                    }
                    byte[] rounded = Draw(false), rest = Draw(true);
                    // Without the clip the outline's own antialiased edge may tint a few pixels faintly; the pens' outer halves were
                    // whole pixels at full strength.
                    int outside = 0, strongest = 0;
                    for (int i = 3; i < rounded.Length; i += 4) if (rounded[i] == 0 && rest[i] > 8) { outside++; strongest = Math.Max(strongest, rest[i]); }
                    Check($"the {name}{(look.Length > 0 ? " with a " + look : "")} at rest draws nothing past its rounded outline at {scale * 100:0}% ({outside} faint edge pixels, strongest alpha {strongest})",
                        strongest <= 64 && outside <= 16);
                }
    }

    private static void Check(string name, bool condition)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
