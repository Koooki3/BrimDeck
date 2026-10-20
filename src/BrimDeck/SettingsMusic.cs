using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BrimDeck.Core;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    private DeckPage _previewPage;
    private FrameworkElement MusicChoice<T>(string title, T selected, (T Value, string Label)[] options, Action<T> change, bool enabled = true) where T : notnull
    {
        var strip = new UniformGrid { Columns = options.Length, Width = 292, IsEnabled = enabled };
        foreach (var option in options)
        {
            var choice = new RadioButton { Style = (Style)FindResource("SettingsSegment"), Content = option.Label, GroupName = title, IsChecked = EqualityComparer<T>.Default.Equals(option.Value, selected) };
            AutomationProperties.SetName(choice, title + " " + option.Label); choice.Checked += (_, _) => change(option.Value); strip.Children.Add(choice);
        }
        var row = Row(title, SegmentBorder(strip)); row.IsEnabled = enabled; return row;
    }
    // The options under the style cards belong to the chosen style. Choosing another card slides the current options
    // out and the chosen style's options in, moving the way the chosen card lies from the previous one.
    private Grid? _styleOptions;
    private void ShowStyleOptions(CompactStyle style, CompactStyle? previous)
    {
        if (_styleOptions is not { } host) return;
        var incoming = Collect(() => { StyleOptions(style); CollapsedSizeOptions(style); });
        var outgoing = host.Children.OfType<FrameworkElement>().LastOrDefault();
        double width = host.ActualWidth;
        if (outgoing is null || previous is not { } from || from == style || width <= 0 || !SystemParameters.ClientAreaAnimation)
        { host.Children.Clear(); host.Children.Add(incoming); return; }
        // A panel still leaving from an earlier choice is dropped; the one arriving becomes the one leaving.
        foreach (var leaving in host.Children.OfType<FrameworkElement>().Where(child => child != outgoing).ToList()) host.Children.Remove(leaving);
        double direction = style > from ? 1 : -1;
        host.ClipToBounds = true; outgoing.IsHitTestVisible = false; host.Children.Add(incoming);
        var leave = outgoing.RenderTransform as TranslateTransform ?? new TranslateTransform(); outgoing.RenderTransform = leave;
        var enter = new TranslateTransform(direction * width, 0); incoming.RenderTransform = enter;
        DoubleAnimation Slide(double to) => new(to, TimeSpan.FromMilliseconds(300)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var leaveMotion = Slide(-direction * width); leaveMotion.Completed += (_, _) => host.Children.Remove(outgoing);
        var enterMotion = Slide(0); enterMotion.Completed += (_, _) => { if (host.Children.Count == 1) host.ClipToBounds = false; };
        leave.BeginAnimation(TranslateTransform.XProperty, leaveMotion); enter.BeginAnimation(TranslateTransform.XProperty, enterMotion);
    }
    private void StyleOptions(CompactStyle style)
    {
        SectionTitle(Loc.T("显示内容 · ", "Content · ") + StyleName(style));
        if (style == CompactStyle.Line)
        {
            Group(Toggle(Loc.T("显示播放进度", "Show playback progress"), S.MusicIndicatorProgress, v => Change(s => s.MusicIndicatorProgress = v)));
            return;
        }
        bool notch = style == CompactStyle.Notch;
        Group(Toggle(Loc.T("配额环", "Quota rings"), S.ShowsSummary(style), v => Change(s => { if (notch) s.NotchSummary = v; else s.CapsuleSummary = v; })),
            Toggle(Loc.T("正在播放的音乐", "Music now playing"), S.ShowsMusic(style), v => Change(s => { if (notch) s.NotchMusic = v; else s.CapsuleMusic = v; })),
            ClockRow(style));
    }
    // Settings every player shares: colors, cover and text.
    private void MusicStyle()
    {
        SectionTitle(Loc.T("收起时", "When collapsed"));
        Group(MusicChoice(Loc.T("中间文字", "Middle text"), S.MusicText, new[] { (CompactMusicText.None, Loc.T("无", "None")), (CompactMusicText.Title, Loc.T("歌名", "Title")), (CompactMusicText.Lyrics, Loc.T("歌词", "Lyrics")) }, v => Change(s => s.MusicText = v)),
            MusicChoice(Loc.T("律动条颜色", "Equalizer color"), S.MusicCoverColor, new[] { (true, Loc.T("封面主色", "Cover color")), (false, Loc.T("主题默认色", "Theme default")) }, v => Change(s => s.MusicCoverColor = v)),
            Toggle(Loc.T("切歌时短暂显示歌名", "Briefly show the title on a track change"), S.MusicTrackNotice, v => Change(s => s.MusicTrackNotice = v)));
        SectionTitle(Loc.T("展开时", "When expanded"));
        Group(Toggle(Loc.T("显示歌词", "Show lyrics"), S.LyricsEnabled, v => Change(s => s.LyricsEnabled = v), S.MusicPage));
    }
    // Operations only one player supports, grouped under that player's name.
    private void Players()
    {
        SectionTitle(Loc.T("网易云音乐", "NetEase Cloud Music"));
        Group(Toggle(Loc.T("始终显示“启用完整控制”按钮", "Always show \"Enable full control\""), S.NeteaseFullControlEntry, v => Change(s => s.NeteaseFullControlEntry = v), S.MusicPage,
            Loc.T("在音乐页面右上角的来源菜单末尾固定显示“网易云音乐 · 完整控制”，以便随时启用完整控制。启用完整控制后，可以在面板中拖动网易云音乐的播放进度，并切换播放模式。",
                "Keeps \"NetEase Cloud Music · Full control\" at the end of the source menu at the top right of the music page, so full control can be enabled at any time. With full control, NetEase Cloud Music's playback position can be dragged in the panel and its playback mode switched.")));
    }
    private void MusicSizeControls()
    {
        var dimensions = new Grid { Margin = new Thickness(0, 8, 0, 0) }; dimensions.ColumnDefinitions.Add(new()); dimensions.ColumnDefinitions.Add(new());
        var width = Dimension(Loc.T("宽度", "Width"), S.MusicWidth, 440, 1200, value => Resize(s => s.MusicWidth = value, DeckPage.Music)); width.Margin = new Thickness(0, 0, 20, 0); dimensions.Children.Add(width);
        var height = Dimension(Loc.T("高度", "Height"), S.MusicHeight, 140, 400, value => Resize(s => s.MusicHeight = value, DeckPage.Music)); height.Margin = new Thickness(20, 0, 0, 0); Grid.SetColumn(height, 1); dimensions.Children.Add(height);
        Target.Children.Add(dimensions);
        var sizes = KeepPreview(new UniformGrid { Columns = 3, Width = 292 });
        foreach (var size in Enum.GetValues<PanelSize>())
        {
            var (w, h) = MusicSizes.For(size);
            var choice = new RadioButton { Style = (Style)FindResource("SettingsSegment"), Content = PanelSizes.Name(size), GroupName = "MusicSize", IsChecked = (S.MusicWidth, S.MusicHeight) == (w, h) };
            AutomationProperties.SetName(choice, Loc.T("音乐快捷尺寸 ", "Music quick size ") + PanelSizes.Name(size)); choice.Checked += (_, _) => { Resize(s => { s.MusicWidth = w; s.MusicHeight = h; }, DeckPage.Music); ShowPage(2); }; sizes.Children.Add(choice);
        }
        QuickSizeRow(sizes);
    }
}
