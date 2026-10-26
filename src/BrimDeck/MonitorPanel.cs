using System.Windows;
using System.Windows.Media.Animation;
using BrimDeck.Core;

namespace BrimDeck;

// The system monitor page and the readings in the collapsed row. The sampler (App.Monitor) runs while any panel shows either;
// every panel draws the same readings. The page is built once per layout step (MonitorLayout), palette, sort, main gauge and
// theme; within a step it stretches, and each new reading only changes what its parts draw.
public partial class MainWindow
{
    private MonitorView? _monitorView;
    private string _monitorKey = "";
    private MonitorStrip? _compactStrip;
    // Which readings the collapsed row shows and how far each has come in or gone out, kept while the row is built again.
    private readonly StripPresence _stripPresence = new();
    // When the temperature and the system drive join the collapsed row: only once their first figures arrive (StripAdmission).
    private readonly StripAdmission _stripAdmission = new();
    private bool _monitorShowing, _stripShowing, _monitorFade;

    private void InitializeMonitor()
    {
        IsVisibleChanged += (_, _) => UpdateMonitorDemand();
        _app.MonitorUpdated += MonitorUpdated;
        Closed += (_, _) => { _app.MonitorUpdated -= MonitorUpdated; _app.PresentMonitor(this, false, false); };
    }

    // Asked whenever the panel opens, closes, changes page or is hidden, and whenever the collapsed row is built.
    private void UpdateMonitorDemand()
    {
        bool page = _expanded && IsMonitorPage && IsVisible && !IsSuppressed;
        bool strip = !_expanded && _compactStrip is not null && IsVisible && !IsSuppressed && _alert is null && EffectiveStyle != CompactStyle.Line;
        if (!page)
        {
            // A list that is not on screen cannot be under the pointer, and a closed page keeps no card open.
            if (_monitorShowing && _app.Monitor is { } monitor) monitor.HoldProcesses = false;
            _monitorView?.CloseCard();
        }
        _app.PresentMonitor(this, page, strip);
        // Neither took readings while hidden: they show the current ones, or dashes once the sampler has paused, never the
        // figures they showed last time.
        if (page && !_monitorShowing) _monitorView?.Update(_app.Monitor?.Latest ?? WaitingSnapshot, motion: false);
        if (strip && !_stripShowing) _compactStrip?.Update(_app.Monitor?.Latest);
        _monitorShowing = page; _stripShowing = strip;
    }
    // The collapsed readings the row was last built without (StripAdmission.Hidden): the temperature and the system drive until
    // their first figures, and for good on a machine without the sensor.
    private CompactMonitorItems _stripUnavailable;
    private static MonitorSnapshot WaitingSnapshot => MonitorSnapshot.Empty(Environment.ProcessorCount, Loc.T("正在读取", "Reading"));

    private bool MonitorMotion => Settings.Animations && SystemParameters.ClientAreaAnimation;

    private void MonitorUpdated(MonitorSnapshot snapshot)
    {
        Motion.Enabled = MonitorMotion;
        var monitor = _app.Monitor;
        var (hidden, arriving) = _stripAdmission.Update(snapshot, monitor?.CpuSensor ?? SensorState.Unknown, monitor?.GpuSensor ?? SensorState.Unknown,
            monitor?.StorageState ?? SensorState.Unknown);
        if (!_expanded)
        {
            // The temperature or the system drive got its first figure: the row is built again with it, and it comes in with the
            // row's own entrance while the others slide aside. Until then the row did not show it, so nothing else changes.
            if (_compactStrip is not null && hidden != _stripUnavailable) { _stripPresence.Arrive(arriving); RenderCompact(); return; }
            _compactStrip?.Update(snapshot); return;
        }
        if (!IsMonitorPage || _monitorView is null) return;
        if (MonitorKey() != _monitorKey)
        {
            // Rebuilt because a kind of sensor turned out to have none (or turned up), the page fades in rather than cuts.
            if (BuiltSensors() != _monitorSensors) _monitorFade = true;
            RenderMonitor(); return;
        }
        _monitorView.Update(snapshot, MonitorMotion);
    }

    // The page's bottom margin for a page of this height; its sides are the header's inset.
    private static double MonitorBottom(double height) => height < 170 ? 10 : 16;
    // The step the page is built for, kept while a resize stays within MonitorLayout.Hysteresis of it.
    private MonitorLayout? _monitorLayout;
    private MonitorLayout CurrentMonitorLayout()
    {
        double height = ContentCanvas.Height;
        return MonitorLayout.For(ContentCanvas.Width - 2 * _header.Side, height - _header.Row - MonitorBottom(height), Settings.MonitorProcesses, MonitorSensors().Drives,
            _monitorView is not null ? _monitorLayout : null);
    }

    // What the view is built from: its layout step, not its exact size, so a page resized within a step is not built again.
    // The sort is not part of it: a new sort reorders the list in place. The page keeps lines for the drives (as many as were found,
    // or as many as fit while they are not read yet, within the layout's room: MonitorLayout.StorageLines) and room for a
    // temperature beside the small gauge (MonitorSensors), so it is built again only when the lines it shows or that room change:
    // when the drives are first counted, a sensor turns out to have none or first shows, or a drive comes or goes past the room.
    private string MonitorKey() => string.Join("|", CurrentMonitorLayout(), Settings.MonitorPalette, Settings.MonitorPalette == MonitorPalette.Custom ? Settings.MonitorCustomColors.Key : "", Settings.MonitorProcesses,
        Settings.MonitorMain, Settings.MonitorProcessView, Loc.Language, PanelTheme.Generation, Settings.MonitorTemperatures, MonitorSensors().Small);
    // What the shown page was built for: its drives' lines and the small gauge's room.
    private (int Lines, bool Small) _monitorSensors;
    private (int Lines, bool Small) BuiltSensors() => (CurrentMonitorLayout().StorageLines, MonitorSensors().Small);
    // Drives: the drives to keep lines for, 0 for none; while they are not counted yet, as many as the page can hold.
    private (int Drives, bool Small) MonitorSensors()
    {
        var monitor = _app.Monitor;
        int lines = !Settings.MonitorStorage || monitor?.StorageState == SensorState.None ? 0 : monitor?.StorageCount is > 0 and var count ? count : 3;
        // The processor's zones are reserved for only once one has shown (a fixed zone never does); a graphics adapter is
        // reserved for until it is known to have no sensor.
        bool small = Settings.MonitorMain == MonitorMain.Cpu ? monitor?.GpuSensor != SensorState.None : monitor?.CpuSensor == SensorState.Found;
        return (lines, Settings.MonitorTemperatures && small);
    }

    private void RenderMonitor()
    {
        // A page on screen that is built again (a resize across a step, a sensor found or missing, a new main gauge) gives way to
        // the new one with a crossfade; a page opened from another one appears as it always has.
        bool shown = MonitorContent.Visibility == Visibility.Visible && _monitorView is not null && MonitorContent.Children.Contains(_monitorView);
        RefreshStatus();
        UsageContent.Visibility = Visibility.Collapsed; MusicContent.Visibility = Visibility.Collapsed; MonitorContent.Visibility = Visibility.Visible;
        ModelToolbar.Visibility = Visibility.Collapsed; ApplicationPages.Visibility = Visibility.Collapsed; ClockLabel.Visibility = Visibility.Visible;
        double width = ContentCanvas.Width, height = ContentCanvas.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height)) return;
        // The header keeps the usage page's controls, as the music page does, so the switch does not move between pages. Fitting
        // the usage page for them is kept until its size, data or settings change: a drag resizes this page many times a second.
        var header = PanelHeader.For(ToolbarFor(height), height < 170 ? 14 : 20);
        ApplyHeader(Settings.UsagePage ? header.WithControlsOf(CachedUsageHeader()) : header);
        MonitorContent.Margin = new Thickness(_header.Side, 0, _header.Side, MonitorBottom(height));
        Motion.Enabled = MonitorMotion;
        var key = MonitorKey();
        bool fade = _monitorFade; _monitorFade = false;
        if (_monitorView is not null && key == _monitorKey && MonitorContent.Children.Contains(_monitorView)) return;
        var layout = CurrentMonitorLayout();
        _monitorKey = key; _monitorLayout = layout; _monitorSensors = BuiltSensors();
        // The list being replaced may have been under the pointer; its leave event never comes.
        if (_app.Monitor is { } monitor) monitor.HoldProcesses = false;
        var old = shown ? _monitorView : null;
        old?.CloseCard();
        _monitorView = new MonitorView(layout, MonitorThemeColors.For(Settings), Settings.MonitorMain, Settings.MonitorSort,
            Settings.MonitorProcessView, SwitchMonitorMain, SortProcesses, hold => { if (_app.Monitor is { } sampler) sampler.HoldProcesses = hold; },
            Settings.MonitorTemperatures, _monitorSensors.Small);
        _monitorView.Update(_app.Monitor?.Latest ?? WaitingSnapshot, motion: false);
        if (old is not null) MonitorCrossfade.Swap(MonitorContent, _monitorView, old, MonitorMotion);
        else
        {
            MonitorContent.Children.Clear(); MonitorContent.Children.Add(_monitorView);
            // A new main gauge, asked for on the page, arrives with a short fade instead of a cut.
            if (fade && MonitorMotion) _monitorView.BeginAnimation(OpacityProperty, Soft.Fade(.15, 1, Soft.Short));
        }
    }

    private (double Width, double Height, IReadOnlyList<ProviderSnapshot> Snapshots, DeckSettings Settings, PanelHeader Header)? _usageHeader;
    private PanelHeader CachedUsageHeader()
    {
        double width = _usageWidth, height = UsagePageHeight;
        if (_usageHeader is { } known && known.Width == width && known.Height == height && ReferenceEquals(known.Snapshots, Snapshots) && ReferenceEquals(known.Settings, Settings))
            return known.Header;
        var header = UsageHeader(width, height);
        _usageHeader = (width, height, Snapshots, Settings, header);
        return header;
    }

    // The sort and the main gauge are the same for every panel and are kept with the settings. A new main gauge rebuilds the page,
    // which fades in; a new sort only reorders the list.
    private void SortProcesses(ProcessSort sort)
    {
        if (Settings.MonitorSort == sort) return;
        Settings.MonitorSort = sort; _app.QueueSave();
        // Every panel's page, shown or not: a page kept for later is not built again, so it must not keep the old sort.
        foreach (var deck in _app.Decks) deck._monitorView?.SetSort(sort);
    }
    private void SwitchMonitorMain(MonitorMain main)
    {
        if (Settings.MonitorMain == main) return;
        Settings.MonitorMain = main; _app.QueueSave();
        RefreshMonitorPages();
    }
    private void RefreshMonitorPages()
    {
        foreach (var deck in _app.Decks) if (deck._expanded && deck.IsMonitorPage) { deck._monitorFade = true; deck.RenderMonitor(); }
    }
}
