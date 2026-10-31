namespace BrimDeck;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // The uninstaller gives back the Windows banners BrimDeck turned off (NoticeBanners) and exits: no window, no instance.
        // The installer passes its data folder, so an isolated test install never touches the real ledger.
        if (Array.IndexOf(args, "--restore-banners") is var restore && restore >= 0)
        {
            var data = restore + 1 < args.Length && System.IO.Path.IsPathFullyQualified(args[restore + 1]) ? args[restore + 1]
                : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrimDeck");
            Environment.ExitCode = Native.NoticeBannerRegistry.RestoreAll(data) ? 0 : 1;
            return;
        }
        // A newer portable version started to replace this folder's files; it does that and exits without opening the app.
        if (Core.PortableUpdate.TryRun(args, out var exitCode)) { Environment.ExitCode = exitCode; return; }
        // The elevated instance KugouInstall starts writes KuGou's file and exits without opening the app.
        if (args is [Native.KugouInstall.ElevatedSwitch, var mode, var library]) { Environment.ExitCode = Native.KugouInstall.RunElevated(mode, library); return; }
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
