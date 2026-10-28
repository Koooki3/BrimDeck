namespace BrimDeck;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // A newer portable version started to replace this folder's files; it does that and exits without opening the app.
        if (Core.PortableUpdate.TryRun(args, out var exitCode)) { Environment.ExitCode = exitCode; return; }
        // The uninstaller gives back the Windows banners BrimDeck turned off (NoticeBanners) and exits: no window, no instance.
        // The installer passes its data folder, so an isolated test install never touches the real ledger.
        if (Array.IndexOf(args, "--restore-banners") is var restore && restore >= 0)
        {
            var data = restore + 1 < args.Length && System.IO.Path.IsPathFullyQualified(args[restore + 1]) ? args[restore + 1]
                : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrimDeck");
            Environment.ExitCode = Native.NoticeBannerRegistry.RestoreAll(data) ? 0 : 1;
            return;
        }
        // --exit [seconds]: asks the running BrimDeck to quit as the tray's Exit does and waits for it (0: gone, 2: still running after the
        // wait, 3: none was running). Used by the local deploy so the settings are written and the panels power off before the files change.
        if (Array.IndexOf(args, "--exit") is var exit && exit >= 0)
        {
            Environment.ExitCode = Core.AppInstance.RequestExit(exit + 1 < args.Length && int.TryParse(args[exit + 1], out var seconds) ? seconds : 20);
            return;
        }
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
