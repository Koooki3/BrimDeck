using System.Windows;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// The devices page and the collapsed battery reading. The sources (App.Devices) run while any panel shows either; every panel
// draws the same readings. As the monitor page, the page is built once per layout step (DevicesLayout), theme and language,
// stretches within a step and crossfades into a new build of itself (MonitorCrossfade); readings only change what it draws.
public partial class MainWindow
{
    private DevicesView? _devicesView;
    private string _devicesKey = "";
    private DevicesLayout? _devicesLayout;
    private bool _devicesShowing;

    private void InitializeDevices()
    {
        _app.DevicesChanged += DevicesChanged;
        Closed += (_, _) => { _app.DevicesChanged -= DevicesChanged; _app.PresentDevices(this, false, false); };
    }

    // The devices shown: every device read, without those switched off in the settings. Made once per roster and per list of
    // hidden devices (each change of either is a new list), as it is asked several times for each change.
    internal IReadOnlyList<DeviceBattery> ShownDevices
    {
        get
        {
            var all = _app.Devices?.All ?? [];
            var hidden = Settings.HiddenDevices;
            if (!ReferenceEquals(all, _shownFrom.All) || !ReferenceEquals(hidden, _shownFrom.Hidden))
            {
                _shown = hidden.Count == 0 ? all : [.. all.Where(device => !Settings.Hides(device.Key))];
                _shownFrom = (all, hidden);
            }
            return _shown;
        }
    }
    private IReadOnlyList<DeviceBattery> _shown = [];
    private (IReadOnlyList<DeviceBattery>? All, List<HiddenDevice>? Hidden) _shownFrom;
    private bool DevicesSettled => _app.Devices?.Settled == true;

    // Asked with the monitor's demand (UpdateMonitorDemand): whenever the panel opens, closes, changes page or is hidden, and
    // whenever the collapsed row is built.
    private void UpdateDevicesDemand()
    {
        bool seen = IsVisible && !IsSuppressed && !DisplayPower.Off;
        bool page = _expanded && CurrentPage == DeckPage.Devices && seen;
        // The collapsed reading is read for while it is chosen, before it shows: it joins the row only once a device reports.
        bool strip = !_expanded && StripChoosesBattery && seen && _alert is null && EffectiveStyle != CompactStyle.Line;
        _app.PresentDevices(this, page, strip);
        // Opened again, the page shows the readings as they are now, not as it left them.
        if (page && !_devicesShowing) { _devicesView?.Update(ShownDevices, DevicesSettled, motion: false); _app.Devices?.Refresh(); }
        _devicesShowing = page;
    }

    private void DevicesChanged()
    {
        if (!_expanded)
        {
            if (StripChoosesBattery && !AdmitStrip(_app.Monitor?.Latest)) _compactStrip?.SetBattery(StripBattery());
            return;
        }
        if (CurrentPage != DeckPage.Devices || _devicesView is null) return;
        if (DevicesKey() != _devicesKey) { RenderDevices(); return; }
        _devicesView.Update(ShownDevices, DevicesSettled, Soft.Enabled);
    }

    // Whether this surface's collapsed row chooses the battery reading (the system readings are on and the table shows it).
    private bool StripChoosesBattery => Settings.ShowsMonitor(EffectiveStyle) && (CompactMonitor.Shown(Settings.CompactReadings) & CompactMonitorItems.Battery) != 0;

    // The collapsed reading: the device with the lowest charge among those shown, null while none reports one.
    internal BatteryReading? LowestBattery()
    {
        var lowest = ShownDevices.Where(device => device.Lowest is not null).OrderBy(device => device.Lowest).FirstOrDefault();
        return lowest is null ? null : new BatteryReading(lowest.Lowest!.Value, lowest.Charging, Devices.Level(lowest.Lowest, lowest.Charging, Settings.DeviceLowPercent), lowest.Name);
    }
    // What the collapsed row shows: the lowest battery, kept while the devices are being listed again (the display came back
    // on, a suppression ended), so the reading does not drop to a dash for the moment the sources take to report.
    private BatteryReading? StripBattery()
    {
        var lowest = LowestBattery();
        if (lowest is not null || DevicesSettled) _stripBattery = lowest;
        return _stripBattery;
    }
    private BatteryReading? _stripBattery;

    private DevicesLayout CurrentDevicesLayout()
    {
        double height = ContentCanvas.Height;
        return DevicesLayout.For(ContentCanvas.Width - 2 * _header.Side, height - _header.Row - MonitorBottom(height), ShownDevices.Count, _devicesView is not null ? _devicesLayout : null);
    }
    private string DevicesKey() => string.Join("|", CurrentDevicesLayout(), Settings.MonitorPalette, Settings.MonitorPalette == MonitorPalette.Custom ? Settings.MonitorCustomColors.Key : "",
        Settings.DeviceLowPercent, Loc.Language, PanelTheme.Generation, SystemParameters.HighContrast);

    private void RenderDevices()
    {
        bool shown = DevicesContent.Visibility == Visibility.Visible && _devicesView is not null && DevicesContent.Children.Contains(_devicesView);
        RefreshStatus();
        UsageContent.Visibility = Visibility.Collapsed; MusicContent.Visibility = Visibility.Collapsed; MonitorContent.Visibility = Visibility.Collapsed;
        DevicesContent.Visibility = Visibility.Visible;
        ModelToolbar.Visibility = Visibility.Collapsed; ApplicationPages.Visibility = Visibility.Collapsed; ClockLabel.Visibility = Visibility.Visible;
        double width = ContentCanvas.Width, height = ContentCanvas.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height)) return;
        // The header keeps the usage page's controls, as the monitor page does, so the switch does not move between pages.
        var header = PanelHeader.For(ToolbarFor(height), height < 170 ? 14 : 20);
        ApplyHeader(Settings.UsagePage ? header.WithControlsOf(CachedUsageHeader()) : header);
        DevicesContent.Margin = new Thickness(_header.Side, 0, _header.Side, MonitorBottom(height));
        var key = DevicesKey();
        // The same step: the page stays, and only shows the devices as they are now (a device switched off in the settings).
        if (_devicesView is not null && key == _devicesKey && DevicesContent.Children.Contains(_devicesView)) { _devicesView.Update(ShownDevices, DevicesSettled, Soft.Enabled); return; }
        var layout = CurrentDevicesLayout();
        _devicesKey = key; _devicesLayout = layout;
        var old = shown ? _devicesView : null;
        _devicesView = new DevicesView(layout, MonitorThemeColors.For(Settings), Settings.DeviceLowPercent);
        _devicesView.Update(ShownDevices, DevicesSettled, motion: false);
        if (old is not null) MonitorCrossfade.Swap(DevicesContent, _devicesView, old, MonitorMotion);
        else { DevicesContent.Children.Clear(); DevicesContent.Children.Add(_devicesView); }
    }
}

// The collapsed row's battery reading: the lowest charge among the devices shown, and the device it is.
internal readonly record struct BatteryReading(int Percent, bool Charging, MemoryLevel Level, string Name);
