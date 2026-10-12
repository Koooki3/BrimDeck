using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using BrimDeck.Core;

namespace BrimDeck;

internal static class UI
{
    public static readonly FontFamily TextFont = new("Microsoft YaHei");
    // The panel sets Latin text and digits in Segoe UI Variable and falls back to YaHei for Chinese.
    public static readonly FontFamily PanelFont = new("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
    public static readonly FontFamily DisplayFont = new("Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI");
    public const string Accent = "#0A84FF";
    // Text and structure on the pure black surface: three label levels by opacity, hairlines by opacity.
    public const string Primary = "#F5F5F7", Secondary = "#9EEBEBF5", Tertiary = "#61EBEBF5";
    public const string Hairline = "#17FFFFFF", Track = "#1FFFFFFF", Fill = "#12FFFFFF", FillSelected = "#24FFFFFF", Outline = "#24FFFFFF";
    // Panels are rebuilt on every refresh with hundreds of brushes; one frozen brush per color is shared instead.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Brush> Brushes = new(StringComparer.OrdinalIgnoreCase);
    public static Brush Brush(string color) => Brushes.GetOrAdd(color, static text =>
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(text)); brush.Freeze(); return brush;
    });
    public static string ColorFor(ProviderId id) => AppPresets.ThemeColor(id);
    public static TextBlock Text(string text, double size = 13, string color = "#E6E8EF", FontWeight? weight = null)
        => new() { Text = text, FontFamily = TextFont, FontSize = size, Foreground = Brush(color), FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
    public static TextBlock Line(string text, double size = 13, string color = "#E6E6E9", FontWeight? weight = null)
        => new() { Text = text, FontFamily = TextFont, FontSize = size, Foreground = Brush(color), FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
    // Keep the font's natural line box so a parent can center it within a taller row or button.
    public static TextBlock Centered(string text, double size, string color, FontWeight? weight = null, FontFamily? font = null)
    {
        var block = Line(text, size, color, weight);
        block.FontFamily = font ?? PanelFont;
        block.VerticalAlignment = VerticalAlignment.Center;
        // Equal-width digits keep figures in neighbouring rows on the same vertical lines.
        Typography.SetNumeralAlignment(block, FontNumeralAlignment.Tabular);
        return block;
    }
    // Standalone symbols center their visible ink, without the font's baseline whitespace.
    public static Path CenteredSymbol(string text, double size, string color, FontWeight? weight = null)
    {
        var brush = Brush(color);
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(PanelFont, FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal), size, brush, 1);
        var geometry = formatted.BuildGeometry(new Point());
        var bounds = geometry.Bounds;
        geometry.Transform = new TranslateTransform(0, -bounds.Top);
        return new Path { Data = geometry, Fill = brush, Width = formatted.WidthIncludingTrailingWhitespace,
            Height = bounds.Height, Stretch = Stretch.None, VerticalAlignment = VerticalAlignment.Center, UseLayoutRounding = false };
    }
    // A single line whose height is exactly the given value, independent of the font's own line box.
    public static TextBlock Fixed(string text, double size, double lineHeight, string color, FontWeight? weight = null, FontFamily? font = null)
    {
        var block = Centered(text, size, color, weight, font);
        block.LineHeight = lineHeight; block.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        block.Height = lineHeight;
        return block;
    }
    public static Button Button(string text, Action click, bool active = false)
    {
        var button = new Button { Content = text, Background = Brush(active ? "#293A55" : "#222224"), BorderBrush = Brush(active ? "#4D83D1" : "#333336"),
            Foreground = Brush(active ? "#DBE9FF" : "#B2B2B8"), Padding = new Thickness(10, 5, 10, 5), FontSize = 11, Margin = new Thickness(0, 0, 6, 0) };
        button.Click += (_, _) => click();
        return button;
    }
    // A progress ring: a faint track with a colored arc that starts at the top and runs counterclockwise.
    public static Grid Ring(double percent, string color, double size, double thickness)
    {
        var ring = new Grid { Width = size, Height = size };
        ring.Children.Add(new Ellipse { Stroke = Brush("#29FFFFFF"), StrokeThickness = thickness });
        double fraction = Math.Clamp(percent / 100, 0, 1);
        if (fraction >= .999) ring.Children.Add(new Ellipse { Stroke = Brush(color), StrokeThickness = thickness });
        else if (fraction > 0)
        {
            double radius = size / 2 - thickness / 2, center = size / 2;
            double angle = fraction * Math.PI * 2;
            var figure = new PathFigure { StartPoint = new Point(center, center - radius), IsClosed = false };
            figure.Segments.Add(new ArcSegment(new Point(center - radius * Math.Sin(angle), center - radius * Math.Cos(angle)),
                new Size(radius, radius), 0, fraction > .5, SweepDirection.Counterclockwise, true));
            var geometry = new PathGeometry(); geometry.Figures.Add(figure);
            ring.Children.Add(new Path { Data = geometry, Stroke = Brush(color), StrokeThickness = thickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        }
        return ring;
    }
    public static Border Divider(double margin = 12) => new() { Height = 1, Background = Brush("#2D2F38"), Margin = new Thickness(0, margin, 0, margin) };
    public static string Number(long number) => number switch
    { >= 1_000_000_000 => (number / 1_000_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "B", >= 1_000_000 => (number / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "M", >= 1000 => (number / 1000d).ToString("0.#", CultureInfo.InvariantCulture) + "K", _ => number.ToString("N0") };
    public static string Cost(IEnumerable<TokenEntry> entries, Pricing pricing) => Cost(pricing.Summarize(entries));
    public static string Cost(CostSummary cost) => "$" + cost.Amount.ToString("N2", CultureInfo.InvariantCulture) + (cost.Unpriced > 0 ? " +" : "");
    public static string CostNote(IEnumerable<TokenEntry> entries, Pricing pricing) => CostNote(pricing.Summarize(entries), pricing);
    public static string CostNote(CostSummary cost, Pricing pricing)
    {
        if (!cost.HasValue && cost.Unpriced == 0) return Loc.T("无用量记录", "No usage records");
        if (!cost.HasValue && cost.Unpriced > 0) return pricing.LastError ?? Loc.T("未匹配到完整价格", "Prices could not be matched for every record");
        var note = cost.IsEstimate ? Loc.T("按模型单价计算 · USD", "Estimated from model prices · USD") : Loc.T("接口金额 · USD", "Amount from the API · USD");
        if (cost.IsEstimate && pricing.UpdatedAt is { } time) note += Loc.T($"\n价格更新 {time.LocalDateTime:MM-dd HH:mm}", $"\nPrices updated {time.LocalDateTime:MM-dd HH:mm}");
        if (cost.IsEstimate && (pricing.IsStale || pricing.LastError is not null)) note += Loc.T(" · 缓存", " · cached");
        if (cost.Unpriced > 0) note += Loc.T($"\n{cost.Unpriced} 条记录未计价", "\n" + Loc.Count(cost.Unpriced, "record has", "records have") + " no price");
        return note;
    }
    public static string Reset(DateTimeOffset? date)
    {
        if (date is null) return Loc.T("重置时间未知", "Reset time unknown");
        var remaining = date.Value - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero) return Loc.T("已到重置时间，等待更新", "Reset time reached; waiting for an update");
        if (remaining.TotalDays >= 1) return Loc.T($"{(int)remaining.TotalDays} 天 {remaining.Hours} 小时后重置", $"Resets in {(int)remaining.TotalDays} d {remaining.Hours} h");
        if (remaining.TotalHours >= 1) return Loc.T($"{remaining.Hours} 小时 {remaining.Minutes} 分后重置", $"Resets in {remaining.Hours} h {remaining.Minutes} min");
        int minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        return Loc.T($"{minutes} 分钟后重置", $"Resets in {minutes} min");
    }
    public static string ShortReset(DateTimeOffset? date)
    {
        if (date is null) return "";
        var remaining = date.Value - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero) return Loc.T("待重置", "Reset due");
        if (remaining.TotalDays >= 1) return Loc.T($"{(int)remaining.TotalDays} 天 {remaining.Hours} 时后", $"in {(int)remaining.TotalDays}d {remaining.Hours}h");
        if (remaining.TotalHours >= 1) return Loc.T($"{remaining.Hours} 时 {remaining.Minutes} 分后", $"in {remaining.Hours}h {remaining.Minutes}m");
        int minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        return Loc.T($"{minutes} 分后", $"in {minutes}m");
    }
    // "Gemini · 5 小时" → group "Gemini", which orders the quota pages. The group stays in the data's words.
    public static string QuotaGroup(Quota quota) => quota.Label.Contains('·') ? quota.Label.Split('·')[0].Trim() : "";
    // "5 小时额度" → "5 小时"; "Gemini · 5 小时" → "Gemini 5 小时" and "Claude · 每周" → "Claude 每周", written like
    // Claude's "Opus 每周", so a grouped quota names its group in its own row. The shown label is translated.
    public static string QuotaLabel(Quota quota)
    {
        var period = (quota.Label.Contains('·') ? quota.Label.Split('·')[^1] : quota.Label).Replace("额度", "", StringComparison.Ordinal).Trim();
        var group = QuotaGroup(quota);
        if (group.Length == 0) return Loc.Label(period);
        return period == "每周" ? Loc.Label(group + " 每周") : group + " " + Loc.Label(period);
    }
    public static string DisplayPlan(string plan) => plan.Trim().ToLowerInvariant() switch
    { "prolite" => "Pro Lite", "pro" or "google ai pro" => "Pro", "google ai ultra" => "Ultra", "plus" => "Plus", "free" => "Free", "max" => "Max", "team" => "Team", "business" => "Business", "enterprise" => "Enterprise", _ => plan.Trim() };
    public static void OpenUrl(string url) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
}
