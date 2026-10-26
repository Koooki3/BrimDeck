using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace BrimDeck;

// A minimal window for the linked production MainWindow.Shadow.cs. No App, settings or account data is loaded.
public partial class MainWindow : Window
{
    private readonly Grid ShadowHost = new() { CacheMode = new BitmapCache(), Opacity = 0 };
    private readonly Grid IslandShadow = new() { Effect = new DropShadowEffect() };
    private readonly Border ShadowBounds = new();
    private readonly IslandBorder Island = new();
    private readonly SolidColorBrush _surface = new(Colors.Black);

    public MainWindow()
    {
        ShowInTaskbar = false;
        var root = new Grid();
        root.Children.Add(ShadowHost);
        root.Children.Add(Island);
        ShadowHost.Children.Add(IslandShadow);
        ShadowHost.Children.Add(ShadowBounds);
        Content = root;
        InitializeShadow();
    }

    internal static void CheckShadowLifetime()
    {
        var references = CreateAndClose();
        // Let native-window cleanup and dispatcher operations finish before checking managed lifetime.
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Check("closed shadow window and elements can be collected", references.All(reference => !reference.IsAlive));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] CreateAndClose()
    {
        var window = new MainWindow();
        try
        {
            new WindowInteropHelper(window).EnsureHandle();
            window.ShadowHost.Opacity = 1;
            Check("visible shadow enables its cache", window.ShadowHost.CacheMode is not null);
            window.ShadowHost.Opacity = 0;
            Check("hidden shadow releases its cache", window.ShadowHost.CacheMode is null);
            window.ShadowHost.Opacity = 1;
            window.Island.CornerRadius = new CornerRadius(8);
            Check("corner changes suspend the shadow cache", window.ShadowHost.CacheMode is null && window._shadowSettle!.IsEnabled);
            window._shadowSettle!.Stop();
            window.ShadowHost.CacheMode = window._shadowCache;
            window.Island.ShoulderRadius = 10;
            Check("shoulder changes suspend the shadow cache", window.ShadowHost.CacheMode is null && window._shadowSettle.IsEnabled);
        }
        finally { window.Close(); }

        var sentinel = new BitmapCache();
        window.ShadowHost.CacheMode = sentinel;
        window.ShadowHost.Opacity = 0;
        window.Island.CornerRadius = new CornerRadius(3);
        window.Island.ShoulderRadius = 2;
        Check("closed window no longer handles shadow property changes", ReferenceEquals(window.ShadowHost.CacheMode, sentinel));
        return [new(window), new(window.Island), new(window.ShadowHost)];
    }

    private static void Check(string name, bool condition)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
