using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BrimDeck.Core;

namespace BrimDeck;

// The system monitor page. The sampler (App.Monitor) runs while any panel shows this page open; every panel draws the
// same readings. The page is built once per size, palette and sort, and each new reading only changes its figures, so a
// reading a second costs a few text changes and one redraw of the core columns.
public partial class MainWindow
{
    private MonitorView? _monitorView;
    private string _monitorKey = "";

    private void InitializeMonitor()
    {
        IsVisibleChanged += (_, _) => UpdateMonitorDemand();
        _app.MonitorUpdated += MonitorUpdated;
        Closed += (_, _) => { _app.MonitorUpdated -= MonitorUpdated; _app.PresentMonitor(this, false); };
    }

    // Asked whenever the panel opens, closes, changes page or is hidden.
    private void UpdateMonitorDemand()
    {
        bool showing = _expanded && IsMonitorPage && IsVisible && !IsSuppressed;
        // A list that is not on screen cannot be under the pointer.
        if (!showing && _app.Monitor is { } monitor) monitor.HoldProcesses = false;
        _app.PresentMonitor(this, showing);
        // The page took no readings while it was closed: it shows the current ones, or dashes once the sampler has paused,
        // never the figures it showed last time.
        if (showing && !_monitorShowing) _monitorView?.Update(_app.Monitor?.Latest ?? WaitingSnapshot, motion: false);
        _monitorShowing = showing;
    }
    private bool _monitorShowing;
    private static MonitorSnapshot WaitingSnapshot => MonitorSnapshot.Empty(Environment.ProcessorCount, Loc.T("正在读取", "Reading"));

    private bool MonitorMotion => Settings.Animations && SystemParameters.ClientAreaAnimation;

    private void MonitorUpdated(MonitorSnapshot snapshot)
    {
        if (!_expanded || !IsMonitorPage || _monitorView is null) return;
        if (MonitorKey() != _monitorKey) { RenderMonitor(); return; }
        _monitorView.Update(snapshot, MonitorMotion);
    }

    // What the view is built from. The readings are not part of it: the core columns follow a change in the number of cores.
    private string MonitorKey()
        => string.Join("|", ContentCanvas.Width, ContentCanvas.Height, _header.Side, _header.Row, Settings.MonitorPalette, Settings.MonitorProcesses, Settings.MonitorSort, Loc.Language,
            PanelTheme.Generation);

    private void RenderMonitor()
    {
        RefreshStatus();
        UsageContent.Visibility = Visibility.Collapsed; MusicContent.Visibility = Visibility.Collapsed; MonitorContent.Visibility = Visibility.Visible;
        ModelToolbar.Visibility = Visibility.Collapsed; ApplicationPages.Visibility = Visibility.Collapsed; ClockLabel.Visibility = Visibility.Visible;
        double width = ContentCanvas.Width, height = ContentCanvas.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height)) return;
        // The header keeps the usage page's controls, as the music page does, so the switch does not move between pages.
        var header = PanelHeader.For(ToolbarFor(height), height < 170 ? 14 : 20);
        ApplyHeader(Settings.UsagePage ? header.WithControlsOf(UsageHeader(_usageWidth, UsagePageHeight)) : header);
        double side = _header.Side, bottom = height < 170 ? 10 : 16;
        MonitorContent.Margin = new Thickness(side, 0, side, bottom);
        var snapshot = _app.Monitor?.Latest ?? WaitingSnapshot;
        var key = MonitorKey();
        if (_monitorView is null || key != _monitorKey || !MonitorContent.Children.Contains(_monitorView))
        {
            _monitorKey = key;
            // The list being replaced may have been under the pointer; its leave event never comes.
            if (_app.Monitor is { } monitor) monitor.HoldProcesses = false;
            var layout = MonitorLayout.For(width - 2 * side, height - _header.Row - bottom, Settings.MonitorProcesses);
            _monitorView = new MonitorView(layout, MonitorThemeColors.For(Settings.MonitorPalette), Settings.MonitorSort, SortProcesses,
                hold => { if (_app.Monitor is { } sampler) sampler.HoldProcesses = hold; });
            MonitorContent.Children.Clear(); MonitorContent.Children.Add(_monitorView);
        }
        _monitorView.Update(snapshot, motion: false);
    }

    // The sort is the same for every panel and is kept with the settings.
    private void SortProcesses(ProcessSort sort)
    {
        if (Settings.MonitorSort == sort) return;
        Settings.MonitorSort = sort; _app.QueueSave();
        foreach (var deck in _app.Decks) if (deck._expanded && deck.IsMonitorPage) deck.RenderMonitor();
    }
}
