namespace BrimDeck;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // A newer portable version started to replace this folder's files; it does that and exits without opening the app.
        if (Core.PortableUpdate.TryRun(args, out var exitCode)) { Environment.ExitCode = exitCode; return; }
        // The elevated instance KugouInstall starts writes KuGou's file and exits without opening the app.
        if (args is [Native.KugouInstall.ElevatedSwitch, var mode, var library]) { Environment.ExitCode = Native.KugouInstall.RunElevated(mode, library); return; }
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
