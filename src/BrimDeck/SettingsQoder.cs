using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    // A row whose statistics app is Qoder (or Qoder CN) offers to switch on the token counts of the Qoder CLI and desktop app, which
    // record none unless the user's environment sets QODER_EXPOSE_TOKEN_USAGE=1 (QoderUsage.TokenSwitch). The variable is set for
    // the current user, so Qoder started afterwards inherits it; Qoder already running keeps the environment it started with
    // (Native.QoderProcesses). The state is read when the line is shown, every few seconds while it stays shown, and after a click:
    // green when on and nothing running lacks it, amber when a running program still has to be opened again, red when off.
    private FrameworkElement QoderTokenDetails(ProviderId usage)
    {
        var edition = QoderUsage.For(usage);
        var variable = QoderUsage.TokenSwitch(edition);
        var grid = AppColumns(); grid.Margin = new Thickness(0, 0, 0, 12);
        string title = Loc.T("CLI 与桌面版 Token", "CLI and desktop tokens");
        string help = Loc.T($"{edition.Name} CLI 与桌面版默认不记录 Token 数。开启后，BrimDeck 为当前用户设置环境变量 {variable}=1，重新打开 {edition.Name} CLI 与桌面版后，新的对话开始记录。",
            $"{edition.Name} CLI and the desktop app record no token counts by default. Switching this on sets the user environment variable {variable}=1; conversations after {edition.Name} CLI and the desktop app are opened again are recorded.");
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "SourceLabel" });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition());
        var caption = TextLine(title, 12, TextSecondary);
        var heading = new HelpHeading(caption, SmallHelp(title, () => help)) { Margin = new Thickness(16, 0, 16, 0), MinWidth = 52, VerticalAlignment = VerticalAlignment.Center };
        line.Children.Add(heading);
        var dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        var state = TextLine("", 12.5, TextPrimary); state.VerticalAlignment = VerticalAlignment.Center;
        var shown = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        shown.Children.Add(dot); shown.Children.Add(state);
        Grid.SetColumn(shown, 1); line.Children.Add(shown);
        var toggle = ActionButton("", () => { }); toggle.Style = (Style)FindResource("SettingsOutlineButton"); toggle.Height = 34; toggle.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(toggle, "qoder-tokens-" + (edition.China ? "cn" : "global"));
        Grid.SetColumn(toggle, 2); line.Children.Add(toggle);
        var note = TextLine("", 11.5, TextSecondary); note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(0, 4, 0, 0);
        bool busy = false, on = false;
        string failure = "";
        List<string> stale = [];
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        void Show()
        {
            bool restart = on && stale.Count > 0;
            if (on) dot.Fill = UI.Brush(restart ? "#E5A23A" : "#30D158"); else dot.SetResourceReference(Shape.FillProperty, "SettingsError");
            state.Text = !on ? Loc.T("未开启", "Off") : restart ? Loc.T("已开启，待重新打开", "On, to be opened again") : Loc.T("已开启", "On");
            toggle.Content = on ? Loc.T("关闭", "Switch off") : Loc.T("开启", "Switch on");
            toggle.IsEnabled = !busy;
            AutomationProperties.SetName(toggle, (on ? Loc.T("关闭 ", "Switch off ") : Loc.T("开启 ", "Switch on ")) + title);
            AutomationProperties.SetName(shown, state.Text);
            note.Text = failure.Length > 0 ? failure
                : restart ? Loc.T($"{string.Join("、", stale)} 正在运行，重新打开后开始记录", $"{string.Join(" and ", stale)} running; recording starts once opened again") : "";
            note.Visibility = note.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        async Task Check()
        {
            var read = await Task.Run(() =>
            {
                bool now = QoderUsage.UserSwitch(edition) == true;
                List<string> missing;
                try { missing = now ? Native.QoderProcesses.WithoutSwitch(edition, home) : []; }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { missing = []; }
                return (now, missing);
            });
            (on, stale) = read;
            Show();
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += async (_, _) => { if (!busy && line.IsVisible) await Check(); };
        line.Loaded += (_, _) => { timer.Start(); _ = Check(); };
        line.Unloaded += (_, _) => timer.Stop();
        line.IsVisibleChanged += (_, e) => { if (e.NewValue is true) _ = Check(); };
        toggle.Click += async (_, _) =>
        {
            bool enable = !on;
            busy = true; failure = ""; Show();
            try
            {
                // Setting a user variable tells every top-level window about it, which can take a moment.
                await Task.Run(() => Environment.SetEnvironmentVariable(variable, enable ? "1" : null, EnvironmentVariableTarget.User));
                _ = _app.Deck.RefreshAsync();
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or ArgumentException)
            { failure = Loc.T("无法修改环境变量", "The environment variable cannot be changed"); }
            finally { busy = false; await Check(); }
        };
        on = QoderUsage.UserSwitch(edition) == true;
        Show();
        line.Margin = new Thickness(0, 5, 0, 5);
        var lines = new StackPanel(); lines.Children.Add(line);
        var noteLine = new Grid();
        noteLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "SourceLabel" });
        noteLine.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(note, 1); noteLine.Children.Add(note); lines.Children.Add(noteLine);
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(lines, 1); Grid.SetColumnSpan(lines, 6); grid.Children.Add(lines);
        return grid;
    }
}
