using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// The readout bubble of an indicator that peeks (WindowBehavior.Peek): a small borderless window beside the indicator that shows
// the system readings the panel's collapsed row would show (MonitorStrip), in the panel's own look. It never takes focus, reads
// only (a click on it asks the panel to open, Clicked), and is placed by PeekBubblePlacement; MainWindow.Peek.cs owns it.
internal sealed class PeekBubbleWindow : Window
{
    private readonly Border _frame = new();
    private readonly double _scale;
    private MonitorStrip? _strip;
    private bool _placed;

    // look: the look of the panel it belongs to; scale: the panel's scale; capturable: DeckSettings.WindowCapture.
    public PeekBubbleWindow(PanelLook look, double scale, bool capturable)
    {
        _scale = CompactMusicLayout.SafeScale(scale);
        PanelLook.Set(this, look); look.PublishTo(Resources, onlyOwn: true);
        Title = Loc.T("系统读数预览", "System readings preview");
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; Topmost = true; ShowActivated = false; Focusable = false; FontFamily = UI.PanelFont;
        _frame.Background = new SolidColorBrush(look.ColorOf("popup")); _frame.BorderBrush = new SolidColorBrush(look.ColorOf("popupBorder"));
        _frame.BorderThickness = new Thickness(1); _frame.CornerRadius = new CornerRadius(look.Corner(Math.Round(10 * _scale)));
        _frame.Cursor = Cursors.Hand;
        _frame.MouseLeftButtonUp += (_, e) => { e.Handled = true; Clicked?.Invoke(); };
        AutomationProperties.SetName(_frame, Title);
        Content = _frame;
        SourceInitialized += (_, _) => WindowsHost.ConfigureOverlay(this, capturable);
    }

    // The bubble was clicked: the panel opens.
    public event Action? Clicked;

    // The room around the readings, in DIPs, and the border's.
    public double PadX => Math.Round(10 * _scale);
    public double PadY => Math.Round(7 * _scale);
    private const double Rim = 1;

    // The window's size in DIPs for the readings it holds, before it is cut to the room there is.
    public (double Width, double Height) Wanted => _strip is null ? (0, 0) : (_strip.PreferredWidth + 2 * (PadX + Rim), _strip.Height + 2 * (PadY + Rim));

    // The readings shown, in the strip the panel builds for its collapsed row.
    public void SetStrip(MonitorStrip strip)
    {
        _strip = strip;
        strip.HorizontalAlignment = HorizontalAlignment.Center; strip.VerticalAlignment = VerticalAlignment.Center;
        _frame.Padding = new Thickness(PadX, PadY, PadX, PadY);
        _frame.Child = strip;
    }

    public void Update(MonitorSnapshot? snapshot, bool motion, int frames) => _strip?.Update(snapshot, motion, frames);

    // The window's place and size in device pixels. Moved onto the monitor in one step first, which gives the window that monitor's
    // DPI (as MoveOverlay does), and sized in its DIPs then. The readings keep to the room inside the border and the padding.
    public void Place(int x, int y, int width, int height, double fallbackDpi)
    {
        void Apply()
        {
            WindowsHost.MoveTo(this, x, y);
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (!(dpi > 0)) dpi = fallbackDpi;
            Left = x / dpi; Top = y / dpi; Width = width / dpi; Height = height / dpi;
            if (_strip is not null) _strip.Width = Math.Max(0, width / dpi - 2 * (PadX + Rim));
        }
        if (_placed) { Apply(); return; }
        _placed = true;
        Left = x / fallbackDpi; Top = y / fallbackDpi; Width = width / fallbackDpi; Height = height / fallbackDpi;
        SourceInitialized += (_, _) => Apply();
    }

    // Shown without taking focus; faded in when the panel animates.
    public void Appear(bool animate)
    {
        if (animate) Opacity = 0;
        Show();
        WindowsHost.RaiseOnce(this);
        if (animate) BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(Soft.Quick)) { EasingFunction = Soft.Out });
    }

    // Gone: faded out when the panel animates.
    public void Dismiss(bool animate)
    {
        if (!animate || !IsVisible) { Close(); return; }
        IsHitTestVisible = false;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(Soft.Quick)) { EasingFunction = Soft.Out };
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }
}
