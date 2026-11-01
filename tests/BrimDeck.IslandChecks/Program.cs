using System.Windows;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Core;
using Path = System.IO.Path;

// The notice window's warm-up on the real MainWindow, offscreen (B8, 2026-11-01): IslandNotice.Prepare() shows the window once, far
// off screen and transparent, six seconds after the panel starts. Until 11-01 it threw a NullReferenceException there on every start
// (the layers drew before the island was laid out and asked for its form), left the window shown, and crash.log recorded it.
//   exit code 0: every check passed; 1: one failed (the first failure is printed).
internal static class Checks
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            AppInstance.ForTest();
            var app = new App();
            app.InitializeComponent();
            var settings = new DeckSettings { Language = Loc.English, Style = CompactStyle.Capsule, Animations = false, NoticesEnabled = true, NoticesPage = true };
            settings.Normalize();
            var deck = app.UseOffscreen(settings, new Rect(0, 0, 1920, 1080), new Rect(0, 0, 1920, 1032), 1);
            app.Updates = new AppUpdates(new NoUpdates(), Path.Combine(Path.GetTempPath(), "BrimDeck.IslandChecks." + Environment.ProcessId));
            foreach (var style in new[] { CompactStyle.Capsule, CompactStyle.Notch, CompactStyle.Line })
            {
                settings.Style = style;
                deck.ApplySettings();
                var window = new IslandNotice(deck, app);
                try { window.Prepare(); }
                catch (Exception ex) { return Fail($"warm-up ({style}) threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); }
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, () => { });
                if (window.IsVisible) return Fail($"warm-up ({style}) left the notice window shown");
                if (window.Covering) return Fail($"warm-up ({style}) left the notice window covering the island");
                Console.WriteLine($"PASS: the notice window warms up on a {style} island without throwing and ends hidden");
                window.Close();
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static int Fail(string message) { Console.Error.WriteLine("FAIL: " + message); return 1; }

    private sealed class NoUpdates : IAppUpdateBackend
    {
        public string CurrentVersion => ApplicationVersion.Current;
        public bool CanInstall => false;
        public AppRelease? PendingRelease => null;
        public Task<AppRelease?> CheckAsync(CancellationToken cancellation) => Task.FromResult<AppRelease?>(null);
        public Task DownloadAsync(AppRelease release, Action<int> progress, CancellationToken cancellation) => Task.CompletedTask;
        public void PrepareRestart() { }
    }
}
