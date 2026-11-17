using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck;

// The picture pop-up that lies beneath a drop-down while it is open (PopupMotion.OpenGhost) must cover the real list exactly, also
// when Windows opens menus right-aligned (SystemParameters.MenuDropAlignment, the left-handed pen setting): the real list then
// hangs from its button's right edge, and a picture placed with the same offsets was drawn one list's width to its left, so the
// list showed twice side by side. The setting is forced for the check inside WPF's own cache and put back; nothing on the system changes.
internal static class PopupGhostChecks
{
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    private static RECT Bounds(Visual visual)
    {
        GetWindowRect(((HwndSource)PresentationSource.FromVisual(visual)).Handle, out var rect);
        return rect;
    }

    private static void Settle() { for (int i = 0; i < 3; i++) Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }

    // Forces SystemParameters.MenuDropAlignment, returning false where the framework's cache is not where it was looked for.
    private static bool Force(bool value, out bool before)
    {
        before = SystemParameters.MenuDropAlignment;
        var type = typeof(SystemParameters);
        var field = type.GetField("_menuDropAlignment", BindingFlags.Static | BindingFlags.NonPublic);
        var valid = type.GetField("_cacheValid", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) as BitArray;
        var slot = type.GetNestedType("CacheSlot", BindingFlags.NonPublic);
        if (field is null || valid is null || slot is null || !Enum.IsDefined(slot, "MenuDropAlignment")) return false;
        field.SetValue(null, value);
        valid[(int)Enum.Parse(slot, "MenuDropAlignment")] = true;
        return SystemParameters.MenuDropAlignment == value;
    }

    public static void Run(Action<string, bool> check)
    {
        foreach (bool right in new[] { false, true })
        {
            string mode = right ? "right-aligned menus" : "left-aligned menus";
            bool before = false, forced = false;
            Window? window = null;
            Popup? real = null, ghost = null;
            try
            {
                forced = Force(right, out before);
                check($"popup picture, {mode}: the setting could be set for the check", forced);
                if (!forced) continue;
                var button = new Button { Width = 100, Height = 30, Content = "b", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 20, 20, 0) };
                window = new Window { Left = 200, Top = 200, Width = 600, Height = 400, ShowInTaskbar = false, ShowActivated = false, Content = button };
                window.Show();
                Settle();
                var list = new Border { Width = 187, Height = 100, Background = Brushes.Red };
                real = new Popup { PlacementTarget = button, Placement = PlacementMode.Bottom, StaysOpen = true, AllowsTransparency = true, Child = list, IsOpen = true };
                Settle();
                var root = (Visual)VisualTreeHelper.GetParent(list);
                var offset = VisualTreeHelper.GetOffset(list);
                var picture = new Border { Width = 187, Height = 100, Background = Brushes.Blue };
                ghost = PopupMotion.OpenGhost(window, root.PointToScreen(new Point(offset.X, offset.Y)), picture);
                Settle();
                var a = Bounds(list); var b = Bounds(picture);
                check($"popup picture, {mode}: lies exactly over the list (list x {a.Left}, picture x {b.Left})", a.Left == b.Left && a.Top == b.Top && a.Right == b.Right);
            }
            finally
            {
                if (ghost is not null) ghost.IsOpen = false;
                if (real is not null) real.IsOpen = false;
                window?.Close();
                Force(before, out _);
            }
        }
    }
}
