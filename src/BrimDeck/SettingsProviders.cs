using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using BrimDeck.Core;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    private Guid? _expandedSource;
    private CancellationTokenSource? _sourceTestCancellation;
    private readonly Dictionary<Guid, (Border Row, FrameworkElement Detail, Button Arrow)> _sourceRows = [];

    private void CollapseSourceRows()
    {
        _expandedSource = null;
        foreach (var (_, view) in _sourceRows)
        { view.Detail.Visibility = Visibility.Collapsed; view.Row.Height = AppRowHeight; PointArrow(view.Arrow, false); }
    }
    // The same chevron as the selectors, turned to point right while the row is closed.
    private static void PointArrow(Button arrow, bool open)
    {
        if (arrow.Content is UIElement chevron) chevron.RenderTransform = new RotateTransform(open ? 0 : -90);
        AutomationProperties.SetItemStatus(arrow, open ? Loc.T("已展开", "Expanded") : Loc.T("已收起", "Collapsed"));
    }
    // The Windows 11 icon font's small chevron (ChevronDownSmall) is hinted like text and heavy enough beside a bold label.
    // Expandable rows, selectors, combo boxes and the panel's source button all use it.
    // Measured ink: the glyph's center sits at 0.4688 of its box, so it turns about that point, and the labels' glyphs sit
    // lower than their boxes, so it drops 1 DIP to meet them.
    internal static readonly Thickness ChevronDrop = new(0, 1, 0, -1);
    internal static TextBlock Chevron() => new()
    {
        Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12, Margin = ChevronDrop,
        RenderTransformOrigin = new Point(.5, .4688), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
    };
    // The chevron follows the button's foreground, so hover brightens it.
    private Button ChevronButton(string label, Action action)
    {
        var button = new Button { Content = Chevron(), Style = (Style)FindResource("SettingsGlyphButton"), Width = 18, Height = 28 };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action();
        return button;
    }
    // A bare icon whose stroke follows the button, so hover brightens the icon instead of filling a box.
    private Button GlyphButton(string data, double width, double height, string label, Action action)
    {
        var icon = Stroke(data, width, height, TextSecondary); icon.StrokeThickness = 1.3; icon.RenderTransformOrigin = new Point(0.5, 0.5);
        icon.SetBinding(Shape.StrokeProperty, new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1) });
        var button = new Button { Content = icon, Style = (Style)FindResource("SettingsGlyphButton"), ToolTip = label };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action();
        return button;
    }
    private void AttachSourceDetails(AppEntry entry, Grid row, Border border)
    {
        var arrow = ChevronButton(Loc.T("展开 " + entry.Name + " 配额配置", "Show " + entry.Name + " quota configuration"), () => ToggleSourceRow(entry.InstanceId));
        arrow.HorizontalAlignment = HorizontalAlignment.Right; arrow.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(arrow, "expand-source-" + entry.InstanceId.ToString("N"));
        row.Children.Add(arrow);
        var detail = SourceDetails(entry); Grid.SetRow(detail, 1); Grid.SetColumnSpan(detail, 9);
        bool open = _expandedSource == entry.InstanceId;
        detail.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        row.Children.Add(detail);
        if (open) border.Height = double.NaN;
        PointArrow(arrow, open);
        _sourceRows[entry.InstanceId] = (border, detail, arrow);
        row.MouseLeftButtonDown += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, row)) { ToggleSourceRow(entry.InstanceId); e.Handled = true; }
        };
    }
    internal void ToggleSourceRow(Guid id)
    {
        bool open = _expandedSource != id;
        CollapseSourceRows();
        if (open && _sourceRows.TryGetValue(id, out var view))
        { _expandedSource = id; view.Detail.Visibility = Visibility.Visible; view.Row.Height = double.NaN; PointArrow(view.Arrow, true); }
    }

    // Claude and Grok Bot need no site or key; the expanded row holds this row's own switch for reading the quota online.
    private FrameworkElement OnlineQuotaDetails(AppEntry entry)
    {
        var grid = AppColumns(); grid.Margin = new Thickness(0, 0, 0, 12);
        bool grokBot = entry.QuotaSource == ProviderId.GrokBot;
        string title = Loc.T("联网获取额度", "Fetch quota online");
        string help = grokBot
            ? Loc.T("开启：使用 Grok Bot 桌面版的登录，联网获取每周额度。\n关闭：不联网，只显示 Grok Bot 桌面版在本机缓存的最新每周额度。",
                "On: fetches the weekly allowance online with the Grok Bot desktop app sign-in.\nOff: no network access; shows the latest weekly allowance the Grok Bot desktop app cached on this PC.")
            : Loc.T("开启：使用 Claude Code 或桌面版的登录，联网获取额度。\n关闭：不联网，只显示 Claude 桌面版在本机记录的最新额度。",
                "On: fetches the quota online with the Claude Code or desktop app sign-in.\nOff: no network access; shows the latest quota the Claude desktop app recorded on this PC.");
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition());
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        var caption = TextLine(title, 12, TextSecondary); caption.VerticalAlignment = VerticalAlignment.Center;
        heading.Children.Add(caption);
        // The same help mark as the column headings of the application list.
        var mark = Stroke("M 6,1 A 5,5 0 1 1 6,11 A 5,5 0 1 1 6,1 Z M 6,5.4 V 8.4 M 6,3.6 V 3.7", 12, 12, TextTertiary); mark.StrokeThickness = 1.1;
        var info = new Border { Width = 12, Height = 12, Background = Brushes.Transparent, Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Help, Focusable = true, Child = mark, RenderTransform = new TranslateTransform(0, 0.5),
            FocusVisualStyle = (Style)FindResource("SettingsSmallHelpFocus") };
        AttachSettingHelp(info, title, () => help);
        heading.Children.Add(info);
        line.Children.Add(heading);
        var toggle = new CheckBox { Style = (Style)FindResource("SettingsToggle"), IsChecked = entry.QuotaOnline, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(toggle, title); AutomationProperties.SetHelpText(toggle, help);
        AutomationProperties.SetAutomationId(toggle, (grokBot ? "grokbot-online-" : "claude-online-") + entry.InstanceId.ToString("N"));
        Grid.SetColumn(toggle, 1); line.Children.Add(toggle);
        void Set(bool online)
        {
            if (S.Entry(entry.InstanceId) is not { } current || current.QuotaOnline == online) return;
            Change(s => s.Entry(entry.InstanceId)!.QuotaOnline = online);
        }
        toggle.Checked += (_, _) => Set(true); toggle.Unchecked += (_, _) => Set(false);
        line.Margin = new Thickness(0, 5, 0, 5);
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(line, 1); Grid.SetColumnSpan(line, 6); grid.Children.Add(line);
        return grid;
    }

    // The expanded configuration starts under the quota source: a short label column, then fields that end midway
    // between the warning and critical colors. Labels share one width so every field starts at the same place.
    private FrameworkElement SourceDetails(AppEntry entry)
    {
        if (ProviderCatalog.HasOnlineSwitch(entry.QuotaSource)) return OnlineQuotaDetails(entry);
        var grid = AppColumns(); grid.Margin = new Thickness(0, 0, 0, 12); Grid.SetIsSharedSizeScope(grid, true);
        var result = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(6, 4, 6, 4) };
        Func<bool> commitSite = () => true;
        Action commitScript = () => { };
        void InvalidateTest() { result.Visibility = Visibility.Collapsed; _sourceTestCancellation?.Cancel(); }
        TextBlock? Line(string label, FrameworkElement control, int span = 5)
        {
            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "SourceLabel" });
            line.ColumnDefinitions.Add(new ColumnDefinition());
            // A minimum width keeps fields of different sources starting on the same line.
            var caption = TextLine(label, 12, TextSecondary); caption.Margin = new Thickness(16, 0, 16, 0); caption.VerticalAlignment = VerticalAlignment.Center; caption.MinWidth = 52;
            // An unlabeled line still shares the label width but adds no height of its own.
            if (label.Length == 0) caption.Visibility = Visibility.Collapsed;
            line.Children.Add(caption);
            control.Margin = new Thickness(0, 5, 0, 5); Grid.SetColumn(control, 1); line.Children.Add(control);
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(line, grid.RowDefinitions.Count - 1); Grid.SetColumn(line, 1); Grid.SetColumnSpan(line, span); grid.Children.Add(line);
            return label.Length > 0 ? caption : null;
        }
        // A field drawn like the display name box; the hint sits inside it until something is typed.
        Border Field(UIElement input, TextBlock hint, UIElement? trailing = null)
        {
            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition()); content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            // Typed text starts 2 in from the input's edge; the hint starts 5 in, so the caret stands just before it rather than on it.
            hint.VerticalAlignment = VerticalAlignment.Center; hint.IsHitTestVisible = false; hint.TextTrimming = TextTrimming.CharacterEllipsis; hint.Margin = new Thickness(5, 0, 0, 0);
            content.Children.Add(hint); content.Children.Add(input);
            if (trailing is not null) { Grid.SetColumn(trailing, 1); content.Children.Add(trailing); }
            var field = new Border { Child = content, Height = 34, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 0, 4, 0),
                Background = UI.Brush(_palette.Input), BorderBrush = UI.Brush(_palette.Border), BorderThickness = new Thickness(1) };
            field.IsKeyboardFocusWithinChanged += (_, _) => field.BorderBrush = UI.Brush(field.IsKeyboardFocusWithin ? _palette.Accent : _palette.Border);
            return field;
        }
        TextBox Plain(string id, string name)
        {
            var box = new TextBox { Style = (Style)FindResource("SettingsNumber"), TextAlignment = TextAlignment.Left, FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center, MaxLength = 2000, Margin = new Thickness(0, 0, 6, 0) };
            AutomationProperties.SetAutomationId(box, id + "-" + entry.InstanceId.ToString("N")); AutomationProperties.SetName(box, name);
            return box;
        }

        if (ProviderCatalog.NeedsSite(entry.QuotaSource))
        {
            bool custom = entry.QuotaSource == ProviderId.Custom;
            var site = Plain("site", Loc.T("站点地址", "Site address")); site.Text = entry.Site;
            string usage = custom ? Loc.T("可选，作为 ctx.site 传给脚本", "Optional. Passed to the script as ctx.site")
                : Loc.T("末尾的 /、/v1 和控制台路径会自动去掉", "A trailing /, /v1 or console path is removed automatically");
            var hint = TextLine(usage, 12.5, TextTertiary);
            // An HTTP address the row does not allow (one saved by an earlier version included) is not read; the line under
            // the field says why, apart from the hint inside it.
            var warning = TextLine(ProviderCatalog.HttpsRequired, 12, PanelTheme.WarningDefault); warning.TextWrapping = TextWrapping.Wrap;
            bool AllowsHttp() => S.Entry(entry.InstanceId)?.AllowHttp == true;
            void Hint()
            {
                hint.Visibility = site.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                bool insecure = Uri.TryCreate(site.Text.Trim(), UriKind.Absolute, out var typed) && ProviderCatalog.NeedsHttps(typed, AllowsHttp());
                warning.Visibility = insecure ? Visibility.Visible : Visibility.Collapsed;
            }
            bool CommitSite()
            {
                if (S.Entry(entry.InstanceId) is not { } current) return false;
                var normalized = ProviderCatalog.NormalizeSite(site.Text, !custom);
                if (normalized.Length > 0)
                {
                    try
                    {
                        var uri = ProviderCatalog.ValidateUrl(normalized, current.AllowHttp);
                        if (uri.Query.Length > 0) throw new InvalidDataException(Loc.T("站点地址不应包含查询参数。", "The site address must not contain query parameters."));
                    }
                    catch (InvalidDataException ex)
                    {
                        // HTTPS is already explained by the line under the field; other faults still go to the status bar.
                        if (!string.Equals(ex.Message, ProviderCatalog.HttpsRequired, StringComparison.Ordinal)) SaveStatus(ex.Message, true);
                        return false;
                    }
                }
                if (normalized != current.Site) Change(s => s.Entry(entry.InstanceId)!.Site = normalized);
                site.Text = normalized;
                return true;
            }
            commitSite = CommitSite;
            site.TextChanged += (_, _) => { Hint(); InvalidateTest(); };
            site.LostKeyboardFocus += (_, _) => CommitSite();
            site.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitSite(); e.Handled = true; } };
            Line(Loc.T("站点地址", "Site address"), Field(site, hint));
            Line("", warning);

            string allowTitle = Loc.T("允许 HTTP", "Allow HTTP");
            string allowHelp = Loc.T("允许用 HTTP 访问，但 API 密钥会以明文发送。只建议在可信网络中开启，例如局域网。",
                "HTTP is allowed, but the API key is sent in plain text. Turn this on only on a trusted network, such as a LAN.");
            var allow = new CheckBox { Style = (Style)FindResource("SettingsToggle"), IsChecked = entry.AllowHttp, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(allow, allowTitle); AutomationProperties.SetHelpText(allow, allowHelp);
            AutomationProperties.SetAutomationId(allow, "allow-http-" + entry.InstanceId.ToString("N"));
            var allowLine = new StackPanel { Orientation = Orientation.Horizontal };
            var allowInfo = HelpIcon(allowTitle, () => allowHelp, 14); allowInfo.Margin = new Thickness(8, 0, 0, 0);
            allowLine.Children.Add(allow); allowLine.Children.Add(allowInfo);
            void SetAllowHttp(bool value)
            {
                if (S.Entry(entry.InstanceId) is not { } current || current.AllowHttp == value) return;
                Change(s => s.Entry(entry.InstanceId)!.AllowHttp = value);
                Hint(); InvalidateTest();
                // An HTTP address typed while it was refused is saved now that it is allowed.
                if (value) CommitSite();
            }
            allow.Checked += (_, _) => SetAllowHttp(true); allow.Unchecked += (_, _) => SetAllowHttp(false);
            Line(allowTitle, allowLine);
            Hint();
        }

        var password = new PasswordBox { FontSize = 12.5, MaxLength = 4096, Padding = new Thickness(0), BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, Foreground = UI.Brush(TextPrimary), CaretBrush = UI.Brush(_palette.Accent),
            VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        AutomationProperties.SetAutomationId(password, "secret-" + entry.InstanceId.ToString("N")); AutomationProperties.SetName(password, Loc.T("API 密钥", "API key"));
        var visible = Plain("secret-text", Loc.T("API 密钥", "API key")); visible.MaxLength = 4096; visible.Visibility = Visibility.Collapsed;
        string emptyHint = entry.QuotaSource switch
        {
            ProviderId.GlmChina or ProviderId.GlmGlobal => Loc.T("GLM 套餐密钥，加密保存在本机", "GLM plan key, stored encrypted on this PC"),
            ProviderId.DeepSeek => Loc.T("DeepSeek API 密钥，加密保存在本机", "DeepSeek API key, stored encrypted on this PC"),
            ProviderId.KimiApi => Loc.T("Kimi Code API 密钥（sk-kimi-…），加密保存在本机", "Kimi Code API key (sk-kimi-…), stored encrypted on this PC"),
            ProviderId.OpenCodeGo => Loc.T("OpenCode Console API 密钥（oc_sk_…），加密保存在本机", "OpenCode Console API key (oc_sk_…), stored encrypted on this PC"),
            ProviderId.Custom => Loc.T("可选，加密保存在本机", "Optional, stored encrypted on this PC"),
            _ => Loc.T("填写 API 密钥，加密保存在本机", "Enter the API key; it is stored encrypted on this PC")
        };
        string savedHint = Loc.T("已加密保存在本机，输入新密钥以更换", "Stored encrypted on this PC. Enter a new key to replace it");
        var keyHint = TextLine(entry.SecretRevision != Guid.Empty ? savedHint : emptyHint, 12.5, TextTertiary);
        var inputs = new Grid(); inputs.Children.Add(password); inputs.Children.Add(visible);
        // The icon shows the current state: struck through while the key is dots, open while it is plain text.
        const string eye = "M 1,6 C 3.5,0.5 12.5,0.5 15,6 C 12.5,11.5 3.5,11.5 1,6 Z M 8,4 A 2,2 0 1 1 8,8 A 2,2 0 1 1 8,4 Z";
        const string eyeOff = eye + " M 2.5,0.5 L 13.5,11.5";
        bool syncing = false, dirty = false, swapping = false;
        Button? reveal = null;
        reveal = GlyphButton(eyeOff, 16, 12, Loc.T("显示密钥", "Show key"), () => ToggleReveal());
        reveal.Width = 28; reveal.Height = 28; reveal.Focusable = false; reveal.ToolTip = null; ((Shape)reveal.Content).StrokeThickness = 1.2;
        // Only the other box is updated: writing back into the box being typed in would move its caret to the start.
        void EditSecret(string value, bool fromPassword)
        {
            if (syncing) return;
            syncing = true; if (fromPassword) visible.Text = value; else password.Password = value; syncing = false;
            dirty = true; keyHint.Visibility = value.Length == 0 ? Visibility.Visible : Visibility.Collapsed; InvalidateTest();
        }
        password.PasswordChanged += (_, _) => EditSecret(password.Password, true); visible.TextChanged += (_, _) => EditSecret(visible.Text, false);
        void SaveSecret()
        {
            if (!dirty || S.Entry(entry.InstanceId) is null) return;
            try
            {
                var secret = password.Password.Trim();
                _app.Secrets.Write(entry.InstanceId, secret);
                Change(s => s.Entry(entry.InstanceId)!.SecretRevision = secret.Length == 0 ? Guid.Empty : Guid.NewGuid());
                dirty = false;
                syncing = true; password.Clear(); visible.Clear(); syncing = false;
                keyHint.Text = S.Entry(entry.InstanceId)!.SecretRevision == Guid.Empty ? emptyHint : savedHint;
                keyHint.Visibility = Visibility.Visible;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or System.Text.Json.JsonException)
            { SaveStatus(Loc.T("密钥保存失败，请检查本机设置目录。", "The key could not be saved. Check the local settings folder."), true); }
        }
        // Swapping the dots for plain text hides the box being typed in; that is not leaving the field, so nothing is saved.
        void ToggleReveal()
        {
            bool show = visible.Visibility != Visibility.Visible, editing = password.IsKeyboardFocused || visible.IsKeyboardFocused;
            swapping = true;
            syncing = true;
            // With nothing typed, revealing shows the saved key and hiding puts the saved hint back.
            if (!dirty && show && S.Entry(entry.InstanceId)?.SecretRevision is { } revision && revision != Guid.Empty)
            {
                string saved = "";
                try { saved = _app.Secrets.Read(entry.InstanceId); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or System.Text.Json.JsonException or FormatException) { }
                password.Password = saved; visible.Text = saved;
            }
            else if (!dirty && !show) { password.Clear(); visible.Clear(); }
            syncing = false;
            keyHint.Visibility = password.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            visible.Visibility = show ? Visibility.Visible : Visibility.Collapsed; password.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
            if (editing)
            {
                if (show) { visible.Focus(); visible.CaretIndex = visible.Text.Length; }
                else
                {
                    // PasswordBox puts the caret at the start when focused and exposes no caret property;
                    // its internal Select(start, length) keeps the caret where it was in the plain-text box.
                    int caret = visible.CaretIndex;
                    password.Focus();
                    typeof(PasswordBox).GetMethod("Select", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, [typeof(int), typeof(int)])
                        ?.Invoke(password, [caret, 0]);
                }
            }
            swapping = false;
            ((Shape)reveal!.Content).SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse(show ? eye : eyeOff));
            AutomationProperties.SetName(reveal, show ? Loc.T("隐藏密钥", "Hide key") : Loc.T("显示密钥", "Show key"));
        }
        password.LostKeyboardFocus += (_, _) => { if (!swapping) SaveSecret(); };
        visible.LostKeyboardFocus += (_, _) => { if (!swapping) SaveSecret(); };
        Line(entry.QuotaSource == ProviderId.Custom ? Loc.T("API 密钥（可选）", "API key (optional)") : Loc.T("API 密钥", "API key"), Field(inputs, keyHint, reveal));

        if (entry.QuotaSource == ProviderId.Custom)
        {
            var (frame, code) = ScriptEditor.Create(entry.Script, _palette);
            AutomationProperties.SetAutomationId(code, "script-" + entry.InstanceId.ToString("N")); AutomationProperties.SetName(code, Loc.T("配额脚本", "Quota script"));
            void SaveCode() { if (S.Entry(entry.InstanceId) is { } current && current.Script != code.Text) Change(s => s.Entry(entry.InstanceId)!.Script = code.Text); }
            commitScript = SaveCode;
            code.IsKeyboardFocusWithinChanged += (_, _) => { if (!code.IsKeyboardFocusWithin) SaveCode(); };
            code.TextChanged += (_, _) => InvalidateTest();
            // The code reaches across to the show switch: the switch is 38 wide and centred in its 50-wide column, so the frame stops 6 short of it.
            var codeLabel = Line(Loc.T("代码", "Code"), frame, 7)!; codeLabel.VerticalAlignment = VerticalAlignment.Top; codeLabel.Margin = new Thickness(16, 13, 16, 0);
            frame.Margin = new Thickness(0, 5, 6, 5);
        }

        var test = ActionButton(Loc.T("测试连接", "Test connection"), () => { }); test.Style = (Style)FindResource("SettingsOutlineButton");
        test.Height = 34; test.HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetAutomationId(test, "test-source-" + entry.InstanceId.ToString("N"));
        test.Click += async (_, _) =>
        {
            if (!commitSite()) return;
            commitScript();
            SaveSecret();
            if (dirty || S.Entry(entry.InstanceId) is not { } current) return;
            _sourceTestCancellation?.Cancel();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_app.Lifetime.Token); _sourceTestCancellation = cancellation;
            var configuration = current.ConfigurationKey;
            test.IsEnabled = false; test.Content = Loc.T("正在测试…", "Testing…"); result.Visibility = Visibility.Collapsed;
            try
            {
                var response = await _app.Usage.TestProviderAsync(current, cancellation.Token);
                if (cancellation.IsCancellationRequested || S.Entry(entry.InstanceId)?.ConfigurationKey != configuration) return;
                result.Children.Clear(); result.Visibility = Visibility.Visible;
                var snapshot = response.Snapshot;
                result.Children.Add(TextLine(snapshot.StatusLabel + Loc.T($" · {response.Seconds:0.0} 秒", $" · {response.Seconds:0.0} s"), 12, TextPrimary, FontWeights.SemiBold));
                // The first line already names the state, so only the extra explanation is shown below it.
                string separator = Loc.T("；", "; ");
                var explanation = snapshot.Status.StartsWith(snapshot.StatusLabel + separator, StringComparison.Ordinal) ? snapshot.Status[(snapshot.StatusLabel.Length + separator.Length)..] : snapshot.Status;
                if (explanation.Length > 0 && explanation != snapshot.StatusLabel)
                { var description = TextLine(explanation, 11.5, TextSecondary); description.TextWrapping = TextWrapping.Wrap; description.Margin = new Thickness(0, 3, 0, 0); result.Children.Add(description); }
                if (snapshot.LiveQuota && snapshot.Details.Count > 0)
                { var details = TextLine(string.Join("\n", snapshot.Details.Select(p => p.Key + Loc.T("：", ": ") + p.Value)), 11.5, TextSecondary); details.TextWrapping = TextWrapping.Wrap; result.Children.Add(details); }
                if (response.Logs.Count > 0) { var logs = TextLine(string.Join("\n", response.Logs), 11.5, TextSecondary); logs.TextWrapping = TextWrapping.Wrap; result.Children.Add(logs); }
            }
            catch (OperationCanceledException) { }
            finally { test.Content = Loc.T("测试连接", "Test connection"); test.IsEnabled = true; if (_sourceTestCancellation == cancellation) _sourceTestCancellation = null; cancellation.Dispose(); }
        };
        Line("", test);
        Line("", result);
        return grid;
    }
}
