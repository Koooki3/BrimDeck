using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Core;

// The notice window's warm-up on the real IslandNotice (IslandNotice.Prepare shows the window once, far off screen and transparent,
// a few seconds after the panel starts, before any island has been laid out). The App and the MainWindow are not started (their
// constructors read the user's settings, accounts and monitors); the window needs only the App's settings and notice icons and the
// MainWindow's default collapsed shape, so those are stood in for.
//   exit code 0: the warm-up neither throws nor leaves the window shown; 1: it does (the first failure is printed).
internal static class Checks
{
    [STAThread]
    private static int Main()
    {
        try
        {
            var app = (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
            Set(app, "Settings", new DeckSettings());
            var center = (NoticeCenter)RuntimeHelpers.GetUninitializedObject(typeof(NoticeCenter));
            Set(center, "Icons", new NoticeIcons(Dispatcher.CurrentDispatcher, _ => Task.FromResult<byte[]?>(null), _ => false));
            Set(app, "Notices", center);
            var deck = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
            var window = new IslandNotice(deck, app);
            try { window.Prepare(); }
            catch (Exception ex) { return Fail($"warm-up threw {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}"); }
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, () => { });
            if (window.IsVisible) return Fail("warm-up left the notice window shown");
            if (window.Covering) return Fail("warm-up left the notice window covering the island");
            Console.WriteLine("PASS: the notice window warms up without throwing and ends hidden");
            window.Close();
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Set(object target, string property, object value)
    {
        for (var type = target.GetType(); type is not null; type = type.BaseType)
        {
            var field = type.GetField($"<{property}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field is not null) { field.SetValue(target, value); return; }
        }
        throw new InvalidOperationException("no backing field for " + property);
    }

    private static int Fail(string message) { Console.Error.WriteLine("FAIL: " + message); return 1; }
}
