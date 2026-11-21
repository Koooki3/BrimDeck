using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using BrimDeck.Core;

namespace BrimDeck;

public partial class MainWindow
{
    private List<ExpandedGridTile> CurrentMonitorGridTiles()
    {
        var letters = (_app.Monitor?.Latest?.Storage ?? []).SelectMany(drive => drive.Volumes)
            .Select(volume => volume.Letter).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        var saved = Settings.ExpandedGridTiles ?? ExpandedGrid.Seed(Settings, letters);
        return ExpandedGrid.Normalize(saved);
    }

    private ExpandedDemand GridPageDemand()
    {
        var tiles = CurrentMonitorGridTiles();
        var demand = ExpandedMonitor.Demand(tiles.Select(tile => tile.Item), Settings.MonitorTemperatures);
        // A null grid is still the settings-derived seed. If its Storage switch is on but no volume has been discovered yet,
        // keep the sampler looking until the seed can include the actual drive letters. An explicit [] stays idle.
        if (Settings.ExpandedGridTiles is null && Settings.ShowsExpanded(ExpandedItem.Storage))
            demand |= ExpandedDemand.Storage;
        return demand;
    }

    private MonitorLayout CurrentMonitorGridLayout()
    {
        double width = ContentCanvas.Width - 2 * _header.ContentSide;
        double height = ContentCanvas.Height - _header.Row - MonitorBottom(ContentCanvas.Height);
        return MonitorLayout.For(width, height, processes: false);
    }

    private string MonitorGridKey(MonitorLayout layout, IReadOnlyList<ExpandedGridTile> tiles, int columns)
    {
        string cells = string.Join(",", tiles.Select(tile => $"{tile.Item}/{tile.Letter}/{tile.Column}/{tile.Row}/{tile.Columns}/{tile.Rows}"));
        return string.Join("|", "grid", layout, columns, cells, Settings.MonitorPalette,
            Settings.MonitorPalette == MonitorPalette.Custom ? Settings.MonitorCustomColors.Key : "", Loc.Language, Look.Generation,
            Settings.MonitorTemperatures, Native.BatteryProvider.SystemHasBattery, Settings.MonitorProcessView, Settings.MonitorShowFree,
            Settings.CoreDisplay, Settings.CoreMatrixColumns);
    }

    private void RenderMonitorGrid(bool shown, bool fade)
    {
        // The page keeps the saved arrangement at every size: the grid only grows and shrinks with the page (MonitorView.Grid).
        var (tiles, columns) = ExpandedGrid.Trim(CurrentMonitorGridTiles());
        var layout = CurrentMonitorGridLayout();
        string key = MonitorGridKey(layout, tiles, columns);
        if (_monitorView is not null && key == _monitorKey && MonitorContent.Children.Contains(_monitorView)) return;

        _monitorKey = key;
        _monitorSensors = default;
        if (_app.Monitor is { } monitor) monitor.HoldProcesses = false;
        var old = shown ? _monitorView : null;
        old?.CloseCard();
        var message = tiles.Count == 0 ? MonitorGridEmptyMessage(layout) : null;
        _monitorView = new MonitorView(tiles, columns, layout, MonitorThemeColors.For(Settings, Look), Settings.MonitorMain, Settings.MonitorSort,
            Settings.MonitorProcessView, SortProcesses, hold => { if (_app.Monitor is { } sampler) sampler.HoldProcesses = hold; }, SwitchMonitorGpu,
            Settings.MonitorTemperatures,
            PowerBlockFor(old is null && MonitorMotion && !Resizing), Settings.CoreDisplay, Settings.MonitorShowFree,
            Settings.CoreMatrixColumns, message);
        if (old is not null)
        {
            _monitorView.KeepCardScrollFrom(old);
            _monitorView.KeepGridScrollFrom(old);
        }
        _monitorView.Update(_app.Monitor?.Latest ?? WaitingSnapshot, motion: false);
        if (_monitorShowing) UpdateMonitorDemand();
        if (old is not null) MonitorCrossfade.Swap(MonitorContent, _monitorView, old, MonitorMotion);
        else
        {
            MonitorContent.Children.Clear(); MonitorContent.Children.Add(_monitorView);
            if (fade && MonitorMotion) _monitorView.BeginAnimation(OpacityProperty, Soft.Fade(.15, 1, Soft.Short));
        }
    }

    private FrameworkElement MonitorGridEmptyMessage(MonitorLayout layout)
    {
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        string title = Loc.T("还没有选择要显示的监控项", "No readings chosen");
        string note = Loc.T("在设置的系统监控页将项目添加到网格。", "Add readings to the grid on the System monitor settings page.");
        panel.Children.Add(Label(title, layout.Body + 1, Look.Secondary, HorizontalAlignment.Center));
        panel.Children.Add(Label(note, layout.Caption + .5, Look.Tertiary, HorizontalAlignment.Center, top: 4));
        var open = PillButton(Loc.T("打开设置", "Open settings"), primary: false, () => _app.OpenSettingsPage(SettingsWindow.MonitorSettingsPage));
        open.Margin = new Thickness(0, 10, 0, 0); open.HorizontalAlignment = HorizontalAlignment.Center; panel.Children.Add(open);
        AutomationProperties.SetName(panel, title + ". " + note);
        return panel;
    }
}
