using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck;

// Liquid glass as a base theme of its own (#55), kept out of the general theme system: what only the liquid glass themes have.
//   The renderer (GlassBackdrop's liquid glass, DesktopSource, LiquidRenderer) is shown only for BrimDeck's built-in liquid
//   glass themes, liquid-glass-light and liquid-glass-dark, which declare it (PanelThemes.IsLiquid): Renders. Any other
//   theme's glass, a user's theme file declaring the renderer or based on a liquid glass theme included, is the blur.
//   Automatic liquid glass ("liquid-glass", Auto) shows one of the two by what lies behind the island: its look (Dark), the
//   turns between the looks (the App part below), and the theme fades they skip (SnapGeneration, QuietGeneration).
//   What the island does differently over liquid glass (the shadow kept open, the painted rim and edge stepping aside, the
//   jelly press, the ambient light at rest) follows Shown, which only liquid glass on screen makes true.
//   The theme page's card and "当前主题" rows for automatic liquid glass (the SettingsWindow part below).
internal static class LiquidTheme
{
    public const string Auto = PanelThemes.GlassAuto;
    // The backdrop's brightness band the renderer holds for the themes' text (PanelThemes.LiquidTextLight; LiquidFrame uses them).
    public const float TextLight = PanelThemes.LiquidTextLight, TextDark = PanelThemes.LiquidTextDark;

    public static bool IsAuto(string? id) => PanelThemes.IsGlassAuto(id);
    // Automatic liquid glass: whether it shows its dark look, as the island's backdrop decided (App.TurnGlass); until a first
    // measure, the Windows app theme's.
    public static bool Dark { get; set; } = SettingsPalette.IsDark(SettingsTheme.System);
    // The Windows app mode (dark), read only: the prior of automatic liquid glass's decision (GlassAppearance), which keeps
    // to it unless the backdrop clearly asks for the other look. Read again when Windows' preferences change (App).
    public static bool AppDark { get; private set; } = SettingsPalette.IsDark(SettingsTheme.System);
    public static void ReadAppMode() => AppDark = SettingsPalette.IsDark(SettingsTheme.System);
    // The theme id a choice stands for now: automatic liquid glass its look (PanelTheme.Find), any other id as it is.
    public static string Look(string id) => PanelThemes.LiquidLook(id, Dark);

    // Whether the theme on screen is drawn as liquid glass: the gate of the renderer (GlassBackdrop).
    public static bool Renders => PanelThemes.IsLiquid(PanelTheme.Current);
    // Whether liquid glass is shown under an island now.
    public static bool Shown(GlassBackdrop? glass) => glass?.BodyShown == true && Renders;

    // A theme change to show at once, without the fade (the first look automatic liquid glass decides): the Generation it
    // becomes. MainWindow.ThemeFade and GlassBackdrop skip their fades into it.
    public static int SnapGeneration { get; set; } = -1;
    // A theme change whose text turns in one step, with no crossfade of the old look over the new (automatic liquid glass's
    // turn, where the glass itself eases across): MainWindow.ThemeFade skips it; the glass does not.
    public static int QuietGeneration { get; set; } = -1;
    // Whether the theme change about to come (PanelTheme.Changing, before Generation counts it) is shown without the theme's
    // crossfade.
    public static bool SkipsFade => SnapGeneration == PanelTheme.Generation + 1 || QuietGeneration == PanelTheme.Generation + 1;
    // A fade skipped for the change that was to come is let go when no change came, or it would skip the fade of a later,
    // unrelated one (a theme chosen by hand).
    public static void NoChange()
    {
        if (SnapGeneration == PanelTheme.Generation + 1) SnapGeneration = -1;
        if (QuietGeneration == PanelTheme.Generation + 1) QuietGeneration = -1;
    }
}

// Automatic liquid glass's turns between its looks.
public partial class App
{
    // Automatic liquid glass ("liquid-glass"): the main island's glass measures what lies behind it and decides the look
    // (GlassBackdrop, BackdropLook, GlassAppearance). The first decision, at start or when the theme is chosen, is shown at
    // once (LiquidTheme.SnapGeneration). A later one turns the glass first (MainWindow.LeadGlass); the theme, and with it every
    // text and icon brush, turns in one step when the glass passes the brightness where both looks' text reads
    // (GlassBackdrop.Crossed), without the theme's crossfade (LiquidTheme.QuietGeneration), so no glyph is ever drawn twice.
    // TurnByMs is the latest the theme follows, should the glass not report (no liquid glass shown): past the glass's own
    // ease (GlassBackdrop.DarkFadeMs), at whose end the text turns at the latest.
    private const int TurnByMs = (int)GlassBackdrop.DarkFadeMs + 150;
    private DispatcherTimer? _turnBy;
    internal bool AutoLookFrom(MainWindow deck) => !Exiting && deck == Deck && LiquidTheme.IsAuto(Settings.PanelTheme);
    internal void TurnGlass(MainWindow deck, bool dark, bool instant)
    {
        // With nothing to measure, the Windows app theme decides (FollowWindowsGlass); a last measure still under way is let go.
        if (!AutoLookFrom(deck) || !GlassMeasures || LiquidTheme.Dark == dark && PanelTheme.Current.IsDark == dark) return;
        LiquidTheme.Dark = dark;
        if (instant || !Soft.Enabled)
        {
            _turnBy?.Stop();
            ApplyGlassTurn(snap: true);
            return;
        }
        foreach (var each in Decks.ToList()) each.LeadGlass(dark);
        if (_turnBy is null)
        {
            _turnBy = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(TurnByMs) };
            _turnBy.Tick += (_, _) => TurnTheme();
        }
        _turnBy.Stop(); _turnBy.Start();
    }
    internal void GlassCrossed(MainWindow deck)
    {
        if (deck == Deck) TurnTheme();
    }
    private void TurnTheme()
    {
        _turnBy?.Stop();
        if (Exiting) return;
        // Automatic liquid glass given up meanwhile: the glass, led toward a look the theme will not take, follows the theme again.
        if (!LiquidTheme.IsAuto(Settings.PanelTheme)) { foreach (var each in Decks.ToList()) each.LeadGlass(null); return; }
        if (PanelTheme.Current.IsDark == LiquidTheme.Dark) return;
        ApplyGlassTurn(quiet: true);
    }

    // A turn of automatic liquid glass made current: shown at once (snap, the first look decided), with the text turning in
    // one step under the glass's own ease (quiet), or else with the theme's fade (following Windows). The clock's preset
    // gradient carries over as on Windows switching "system", but nothing is saved (PanelThemes.CarryOnGlassTurn), and the
    // theme page only names the new look instead of being built again, as a turn can come every few seconds over a video.
    private void ApplyGlassTurn(bool snap = false, bool quiet = false)
    {
        if (snap) LiquidTheme.SnapGeneration = PanelTheme.Generation + 1;
        if (quiet) LiquidTheme.QuietGeneration = PanelTheme.Generation + 1;
        var previous = PanelTheme.Current;
        bool changed = PanelTheme.Use(Settings, systemChanged: true);
        ScheduleDaylight();
        if (!changed) { LiquidTheme.NoChange(); return; }
        PanelThemes.CarryOnGlassTurn(previous, PanelTheme.Current, Settings);
        foreach (var deck in Decks.ToList()) deck.ApplySettings();
        _settingsWindow?.GlassTurned();
    }

    // Whether automatic liquid glass can measure what lies behind the island: liquid glass can be shown on the main island
    // (transparency effects on, no high contrast, no battery saver, a local session, and the capture has not failed there).
    // Otherwise it follows the Windows app theme, as "system" does, and the theme page says so.
    internal bool GlassMeasures => GlassBackdrop.Available && MainLiquidUsable;
    private bool MainLiquidUsable => Deck?.LiquidUsable != false;
    // Automatic liquid glass with nothing to measure takes the Windows app theme's look. True when that turned the theme.
    private bool FollowWindowsGlass()
    {
        if (Exiting || !LiquidTheme.IsAuto(Settings.PanelTheme) || GlassMeasures) return false;
        bool dark = SettingsPalette.IsDark(SettingsTheme.System);
        if (LiquidTheme.Dark == dark && PanelTheme.Current.IsDark == dark) return false;
        LiquidTheme.Dark = dark;
        _turnBy?.Stop();
        foreach (var each in Decks.ToList()) each.LeadGlass(null);
        // As "system" turns: with the theme's fade.
        ApplyGlassTurn();
        return true;
    }
    // Something that decides whether liquid glass can be shown changed (battery saver, transparency effects, the capture
    // failing): automatic liquid glass may have to follow Windows now, and the theme page's note may change.
    private bool? _glassMeasured;
    private void GlassEnvironmentChanged()
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (Exiting || Deck is null || !LiquidTheme.IsAuto(Settings.PanelTheme)) return;
            bool measures = GlassMeasures;
            bool turned = FollowWindowsGlass();
            if (measures != _glassMeasured) { bool known = _glassMeasured is not null; _glassMeasured = measures; if (known && !turned) _settingsWindow?.PanelThemeChanged(onlyThemePage: true); }
        });
    }
    // Automatic liquid glass shown at start or just chosen: a clock gradient that is a preset of its other look (the look saved
    // last time, or the one the theme card was chosen in) becomes this look's, as it does when the look turns.
    private void MatchGlassGradient()
    {
        if (!LiquidTheme.IsAuto(Settings.PanelTheme)) return;
        var other = PanelThemes.Resolve(PanelTheme.Find(PanelTheme.Current.IsDark ? PanelThemes.GlassLight : PanelThemes.GlassDark));
        PanelThemes.CarryOnGlassTurn(other, PanelTheme.Current, Settings);
    }
}

// Automatic liquid glass on the theme page: its card among the base themes and its rows in "当前主题".
public sealed partial class SettingsWindow
{
    // Automatic liquid glass's name with the look it shows now, in the "当前主题" section: a turn of the look only renames it
    // (GlassTurned), as it can come every few seconds over a video.
    private TextBlock? _glassNow;

    // Automatic liquid glass comes first among the glass themes, its fixed light and dark looks after it.
    private static List<(string Id, string Name, PanelThemeInfo Theme)> WithGlassAuto(List<(string Id, string Name, PanelThemeInfo Theme)> basics)
    {
        int glass = basics.FindIndex(card => card.Id == PanelThemes.GlassLight);
        basics.Insert(glass < 0 ? basics.Count : glass, (LiquidTheme.Auto, Loc.T("液态玻璃（自动）alpha", "Liquid Glass (auto) alpha"), PanelTheme.Find(LiquidTheme.Auto)));
        return basics;
    }

    private static string GlassTitle(PanelThemeInfo theme) => Loc.T("液态玻璃（自动）alpha，当前：", "Liquid Glass (auto) alpha, now: ") + theme.DisplayName;
    private static string GlassDescription => Loc.T("清透的液态玻璃，按岛后方内容的明暗自动换成浅色或深色玻璃，文字随之切换，始终清晰可读。开启液态玻璃时，用 DXGI 桌面复制录屏的软件录不到岛。",
        "Clear liquid glass that turns light or dark with what lies behind the island, its text following, so it always reads. While liquid glass is on, recorders that use DXGI desktop duplication do not capture the island.");

    // The "当前主题" rows for automatic liquid glass: its name with the look shown now (renamed by GlassTurned), and, with
    // nothing to measure (App.GlassMeasures), a note that it takes the Windows app theme's look meanwhile.
    private void GlassAutoDetails(string chosen, TextBlock name, List<FrameworkElement> rows)
    {
        _glassNow = LiquidTheme.IsAuto(chosen) ? name : null;
        if (LiquidTheme.IsAuto(chosen) && !_app.GlassMeasures)
            rows.Add(Row(Loc.T("提示", "Note"), Wrapped(Loc.T("现在无法测量岛后方的明暗（节电模式、远程会话、透明效果关闭、高对比度或无法捕获屏幕），自动外观暂时跟随 Windows 的应用主题。",
                "What lies behind the island cannot be measured now (battery saver, a remote session, transparency effects off, high contrast, or no screen capture), so the auto look follows the Windows app theme meanwhile."), false)));
    }

    // Automatic liquid glass took its other look: only the name of the look shown changes on the theme page. The rest of the
    // settings shows the look of when it was built until it is built again, as a turn can come every few seconds.
    internal void GlassTurned()
    {
        if (IsLoaded && _page == ThemePage && _glassNow is not null && LiquidTheme.IsAuto(S.PanelTheme))
            _glassNow.Text = GlassTitle(PanelTheme.Find(LiquidTheme.Auto));
    }
}
