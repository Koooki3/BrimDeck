using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BrimDeck.Core;

public enum CompactStyle { Notch, Capsule, Line }
public enum WindowBehavior { Normal, Line, Hide }
public enum ScreenContext { Desktop, Maximized, Borderless, Exclusive }
// ZCode reads the ZCode desktop application's local request records and its signed-in Coding Plan quota.
// Dsh reads the DeepSeek account or API key that DeepSeek Harness (desktop or web) signed in with; DeepSeek uses a key entered by hand.
// GrokBot reads the weekly allowance of the Grok Bot desktop app; it is a quota source only.
// Statistics sources are saved as numbers. 8 and 9 were a short-lived split of Claude and are retired:
// SettingsMigrations maps them back to Claude, and later members keep their saved numbers.
public enum ProviderId { Claude, Codex, Antigravity, Cursor, GlmChina, NewApi, Sub2Api, Custom, ZCode = 10, GlmGlobal = 11, Dsh = 12, DeepSeek = 13, GrokBot = 14 }
public enum SettingsTheme { System, Light, Dark }
public enum DeckPage { Usage, Music }
public enum DefaultDeckPage { Last, Usage, Music }
public enum CompactMusicText { None, Title, Lyrics }
public enum ClockStyle { Minimal, Stacked, Digits, Dots, Segment, Neon, Serif, Wide, Condensed }
// Every clock style takes this one color. Gradient spreads ClockGradient across the digits, Custom is ClockCustomColor,
// and Cover follows the color picked from the playing song's cover. Saved as numbers, so new members go at the end.
public enum ClockColor { White, Amber, Cyan, Pink, Lime, Gradient, Cover, Custom }

public static class ClockColors
{
    public const string DefaultCustom = "#B9A8FF";
    // Every gradient has three colors: start, middle and end.
    public static readonly string[] Sunset = ["#FFB38A", "#FF7EB3", "#B69CFF"];
    public static IReadOnlyList<(string Name, string[] Stops)> Gradients =>
    [
        (Loc.T("日落", "Sunset"), Sunset),
        (Loc.T("海洋", "Ocean"), ["#7CF3FF", "#4FA8FF", "#6A6CFF"]),
        (Loc.T("极光", "Aurora"), ["#8BFFB0", "#5CE1E6", "#B69CFF"]),
        (Loc.T("火焰", "Ember"), ["#FFE27A", "#FF9F43", "#FF5E5E"]),
        (Loc.T("霓虹", "Neon"), ["#FF5CF0", "#A66BFF", "#5CE1E6"])
    ];
}

// A dashboard column has an identity independent of either data source.
public sealed class AppEntry
{
    private ProviderId? _usageSource;
    public Guid InstanceId { get; set; } = Guid.NewGuid();
    [JsonConverter(typeof(QuotaSourceConverter))]
    public ProviderId QuotaSource { get; set; }
    public ProviderId UsageSource { get => _usageSource ?? (ProviderCatalog.IsUsageSource(QuotaSource) ? QuotaSource : ProviderId.Claude); set => _usageSource = value; }
    public string Site { get; set; } = "";
    public string Script { get; set; } = "";
    public Guid SecretRevision { get; set; }
    // Claude and Grok Bot rows only: read the quota from the account API. When off, the row sends no request and shows the
    // quota the desktop app (Claude or Grok Bot) recorded on this PC. Each row has its own choice.
    public bool QuotaOnline { get; set; } = true;
    [JsonIgnore] public string ConfigurationKey => ProviderCatalog.ConfigurationKey(this);
    // Shorthand for QuotaSource in code. The "Id" member of old files is converted by SettingsMigrations.
    [JsonIgnore] public ProviderId Id { get => QuotaSource; set => QuotaSource = value; }
    // Empty means the automatically supplied quota-source name.
    public string DisplayName { get; set; } = "";
    [JsonIgnore] public string Name => string.IsNullOrWhiteSpace(DisplayName) ? AppPresets.Name(QuotaSource) : DisplayName;
    public bool Enabled { get; set; } = true;
    public string ThemeColor { get; set; } = "";
    public string WarningColor { get; set; } = "";
    public string CriticalColor { get; set; } = "";
    [JsonIgnore] public string Theme => ThemeColor.Length > 0 ? ThemeColor : AppPresets.ThemeColor(QuotaSource);
    [JsonIgnore] public string Warning => WarningColor.Length > 0 ? WarningColor : AppPresets.WarningColor;
    [JsonIgnore] public string Critical => CriticalColor.Length > 0 ? CriticalColor : AppPresets.CriticalColor;
    public AppEntry Copy()
    {
        var copy = (AppEntry)MemberwiseClone();
        copy.UsageSource = UsageSource;
        return copy;
    }
    public void Normalize()
    {
        UsageSource = ProviderCatalog.IsUsageSource(UsageSource) ? UsageSource : ProviderId.Claude;
        Site = ProviderCatalog.NormalizeSite(Site ?? "", QuotaSource != ProviderId.Custom);
        Script ??= "";
        // A custom row always starts from the template, which is the only place its return format is described.
        if (QuotaSource == ProviderId.Custom && Script.Trim().Length == 0) Script = ProviderScripts.Example;
        DisplayName = Regex.Replace(DisplayName ?? "", @"\s+", " ").Trim();
        if (DisplayName.Length > 64) DisplayName = DisplayName[..64];
        ThemeColor = AppPresets.NormalizeColor(ThemeColor);
        WarningColor = AppPresets.NormalizeColor(WarningColor);
        CriticalColor = AppPresets.NormalizeColor(CriticalColor);
    }
}

public static class AppPresets
{
    public const int MaximumApps = 7;
    public const string WarningColor = "#E5C890", CriticalColor = "#E78284";
    public const double WarningPercent = 70, CriticalPercent = 90;
    // Each column needs this much width; the panel's minimum width follows the number of enabled applications.
    public const double MinimumColumnWidth = 150;
    public static readonly string[] Palette =
    [
        "#E5A385", "#F0B37E", "#E5C890", "#98DBB0", "#7FD1C4", "#9CB9FF",
        "#B9A8FF", "#E7A0D0", "#E78284", "#D4D5DF", "#8E8E93", "#F5F5F7"
    ];
    public static string Name(ProviderId id) => ProviderCatalog.Name(id);
    // The original preset colors: coral for Claude, mint for Codex, periwinkle for Antigravity, light gray for Cursor.
    public static string ThemeColor(ProviderId id) => id switch
    { ProviderId.Claude => "#E5A385", ProviderId.Codex => "#98DBB0", ProviderId.Antigravity => "#9CB9FF", ProviderId.ZCode => "#3A538D", ProviderId.Dsh => "#6081DA",
      ProviderId.GlmChina or ProviderId.GlmGlobal or ProviderId.DeepSeek or ProviderId.NewApi or ProviderId.Sub2Api or ProviderId.Custom => ManualColor, _ => "#D4D5DF" };
    // Sources entered by hand share one purple by default.
    public const string ManualColor = "#CC9EED";
    public static string QuotaColor(AppEntry entry, double usedPercent)
        => usedPercent >= CriticalPercent ? entry.Critical : usedPercent >= WarningPercent ? entry.Warning : entry.Theme;
    public static int Level(double usedPercent) => usedPercent >= CriticalPercent ? 2 : usedPercent >= WarningPercent ? 1 : 0;
    public static bool IsColor(string value) => Regex.IsMatch(value, "^#[0-9A-Fa-f]{6}$");
    public static string NormalizeColor(string? value)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 6 && Regex.IsMatch(text, "^[0-9A-Fa-f]{6}$")) text = "#" + text;
        return IsColor(text) ? "#" + text[1..].ToUpperInvariant() : "";
    }
}

public enum PanelSize { Compact, Standard, Spacious }

// Quick sizes depend on the number of enabled applications at the moment they are chosen; the stored width
// and height do not move afterwards. One to four applications use the values settled with the user; more
// applications extend the same pattern per column, capped at the 1200 DIP maximum width.
public static class PanelSizes
{
    public static (double Width, double Height) For(PanelSize size, int apps)
    {
        int columns = Math.Max(1, apps);
        static double Round(double value) => Math.Ceiling(value / 10) * 10;
        var (width, height) = (size, columns) switch
        {
            (PanelSize.Compact, 1) => (500d, 170d), (PanelSize.Standard, 1) => (550d, 200d), (PanelSize.Spacious, 1) => (630d, 220d),
            (PanelSize.Compact, 2) => (500d, 150d), (PanelSize.Standard, 2) => (520d, 200d), (PanelSize.Spacious, 2) => (760d, 310d),
            (PanelSize.Compact, 3) => (530d, 160d), (PanelSize.Standard, 3) => (730d, 240d), (PanelSize.Spacious, 3) => (860d, 310d),
            (PanelSize.Compact, 4) => (700d, 170d), (PanelSize.Standard, 4) => (760d, 240d), (PanelSize.Spacious, 4) => (1100d, 310d),
            (PanelSize.Compact, _) => (Round(columns * 170 + 12), 170d),
            (PanelSize.Spacious, _) => (Round(columns * 270 + 12), 310d),
            _ => (Round(columns * 187 + 12), 240d)
        };
        return (Math.Clamp(width, 440, 1200), Math.Clamp(height, 140, 400));
    }
    public static string Name(PanelSize size) => size switch { PanelSize.Compact => Loc.T("紧凑", "Compact"), PanelSize.Spacious => Loc.T("宽敞", "Spacious"), _ => Loc.T("标准", "Standard") };
}

public sealed class DeckSettings
{
    private List<AppEntry>? _apps;
    // Version of the BrimDeck that last saved settings.json; see SettingsMigrations. Empty in files saved before 0.1.0.
    public string AppVersion { get; set; } = "";
    public bool LaunchAtStartup { get; set; }
    public SettingsTheme Theme { get; set; } = SettingsTheme.System;
    // A first start replaces the default with the Windows display language (SettingsStore.Load); files saved before
    // English existed are Chinese (SettingsMigrations).
    public string Language { get; set; } = Loc.English;
    public CompactStyle Style { get; set; } = CompactStyle.Notch;
    public bool MusicPage { get; set; } = true;
    public double MusicWidth { get; set; } = 520;
    public double MusicHeight { get; set; } = 200;
    // The notch and the capsule each keep their own choices; the indicator only offers the playback progress.
    public bool NotchSummary { get; set; } = true;
    public bool NotchMusic { get; set; } = true;
    public bool CapsuleSummary { get; set; } = true;
    public bool CapsuleMusic { get; set; } = true;
    // The notch and the capsule each switch the clock on; its style, color and hour format are shared.
    public bool NotchClock { get; set; }
    public bool CapsuleClock { get; set; }
    public ClockStyle ClockStyle { get; set; }
    public ClockColor ClockColor { get; set; }
    public string ClockCustomColor { get; set; } = ClockColors.DefaultCustom;
    public List<string> ClockGradient { get; set; } = [.. ClockColors.Sunset];
    public bool Clock24Hour { get; set; } = true;
    public CompactMusicText MusicText { get; set; }
    public bool MusicCoverColor { get; set; } = true;
    public bool MusicTrackNotice { get; set; } = true;
    public bool MusicIndicatorProgress { get; set; } = true;
    public bool LyricsEnabled { get; set; } = true;
    // Offers "网易云音乐 · 完整控制" in the source menu, or alone in its place when nothing plays.
    public bool NeteaseFullControlEntry { get; set; }
    public DefaultDeckPage DefaultPage { get; set; }
    // A right click on the expanded panel opens the settings window.
    public bool RightClickSettings { get; set; } = true;
    public DeckPage LastPage { get; set; }
    public double Width { get; set; } = 520;
    public double Height { get; set; } = 200;
    // The quick size last chosen in settings; cleared when the width or height is edited by hand.
    public PanelSize? QuickSize { get; set; }
    public int OpenDelay { get; set; } = 0;
    public int CloseDelay { get; set; } = 0;
    public int AnimationDuration { get; set; } = 360;
    public bool Animations { get; set; } = true;
    public WindowBehavior Maximized { get; set; } = WindowBehavior.Line;
    public WindowBehavior Borderless { get; set; } = WindowBehavior.Hide;
    public WindowBehavior Exclusive { get; set; } = WindowBehavior.Hide;
    public bool UsagePage { get; set; } = true;
    public bool UsageAutoSync { get; set; } = true;
    // The settings window's size when last closed, in DIPs, and whether it was maximized. Empty until it is closed once;
    // it opens at the default size then. The size is fitted to the screen the window opens on.
    public double? SettingsWindowWidth { get; set; }
    public double? SettingsWindowHeight { get; set; }
    public bool SettingsWindowMaximized { get; set; }
    // Shows the compact notice when a quota rises into the warning or critical level.
    public bool QuotaAlerts { get; set; } = true;
    // The ordered application list; the order is the column order of the expanded panel. Claude and Codex are shown
    // at first; the other built-in applications are listed but hidden.
    public List<AppEntry> Apps
    {
        get => _apps ??= ProviderCatalog.BuiltIns.Select(id => new AppEntry { QuotaSource = id, UsageSource = id, Enabled = id is ProviderId.Claude or ProviderId.Codex }).ToList();
        set => _apps = value;
    }

    public bool ShowsSummary(CompactStyle style) => style switch { CompactStyle.Notch => NotchSummary, CompactStyle.Capsule => CapsuleSummary, _ => false };
    public bool ShowsMusic(CompactStyle style) => style switch { CompactStyle.Notch => NotchMusic, CompactStyle.Capsule => CapsuleMusic, _ => false };
    // The indicator is too thin for text, so it never shows the clock.
    public bool ShowsClock(CompactStyle style) => style switch { CompactStyle.Notch => NotchClock, CompactStyle.Capsule => CapsuleClock, _ => false };
    // Media is detected for the expanded music page, the chosen style's music, or an indicator that shows playback
    // progress; a window rule can turn any style into the indicator.
    [JsonIgnore] public bool MediaWanted => MusicPage || ShowsMusic(Style) || MusicIndicatorProgress
        && (Style == CompactStyle.Line || Maximized == WindowBehavior.Line || Borderless == WindowBehavior.Line || Exclusive == WindowBehavior.Line);
    // The switched-on applications. Their quotas are read for the AI usage page or for the rings of the chosen compact
    // style; the two are shown independently, so either one keeps the data current.
    [JsonIgnore] public IReadOnlyList<AppEntry> ConfiguredApps => Apps.Where(app => app.Enabled).ToList();
    [JsonIgnore] public bool QuotaWanted => UsagePage || ShowsSummary(Style);
    [JsonIgnore] public IReadOnlyList<AppEntry> EnabledApps => QuotaWanted ? ConfiguredApps : [];
    [JsonIgnore] public IReadOnlySet<ProviderId> RequiredProviders => EnabledApps.SelectMany(app => new[] { app.QuotaSource, app.UsageSource }).ToHashSet();
    [JsonIgnore] public IReadOnlyList<ProviderId> StatisticsProviders => EnabledApps.Select(app => app.UsageSource).Distinct().ToList();
    [JsonIgnore] public double MinimumWidth => Math.Clamp(Math.Max(440, AppPresets.MinimumColumnWidth * Math.Max(1, ConfiguredApps.Count) + 40), 440, 1200);
    public (double Width, double Height) PresetSize(PanelSize size)
    {
        var (width, height) = PanelSizes.For(size, ConfiguredApps.Count);
        return (Math.Max(width, MinimumWidth), height);
    }
    // The quick size whose width and height equal the current ones, if any.
    [JsonIgnore] public PanelSize? CurrentPreset => Enum.GetValues<PanelSize>().Cast<PanelSize?>().FirstOrDefault(size => PresetSize(size!.Value) == (Width, Height));
    public AppEntry? Entry(ProviderId id) => Apps.FirstOrDefault(app => app.QuotaSource == id);
    public AppEntry? Entry(Guid instanceId) => Apps.FirstOrDefault(app => app.InstanceId == instanceId);
    public bool Enabled(ProviderId id) => RequiredProviders.Contains(id);
    public void SetEnabled(ProviderId id, bool enabled)
    {
        var entries = Apps.Where(app => app.QuotaSource == id || app.UsageSource == id).ToList();
        if (entries.Count == 0) { if (enabled && Apps.Count < AppPresets.MaximumApps) Apps.Add(new AppEntry { QuotaSource = id, UsageSource = id }); }
        else foreach (var entry in entries) entry.Enabled = enabled;
    }
    public WindowBehavior Behavior(ScreenContext context) => context switch
    {
        ScreenContext.Maximized => Maximized, ScreenContext.Borderless => Borderless,
        ScreenContext.Exclusive => Exclusive, _ => WindowBehavior.Normal
    };
    public DeckSettings Copy()
    {
        var copy = (DeckSettings)MemberwiseClone();
        copy._apps = _apps?.Select(app => app.Copy()).ToList();
        copy.ClockGradient = [.. ClockGradient ?? [.. ClockColors.Sunset]];
        return copy;
    }
    public void Normalize()
    {
        var seen = new HashSet<Guid>();
        Apps = Apps.Where(app => app is not null && ProviderCatalog.Sources.Contains(app.QuotaSource)).Take(AppPresets.MaximumApps).ToList();
        foreach (var app in Apps)
        {
            if (app.InstanceId == Guid.Empty || !seen.Add(app.InstanceId))
            {
                do { app.InstanceId = Guid.NewGuid(); } while (!seen.Add(app.InstanceId));
            }
            app.Normalize();
        }
        Width = double.IsFinite(Width) ? Math.Clamp(Width, 440, 1200) : 760;
        Width = Math.Max(Width, MinimumWidth);
        Height = double.IsFinite(Height) ? Math.Clamp(Height, 140, 400) : 240;
        MusicWidth = double.IsFinite(MusicWidth) ? Math.Clamp(MusicWidth, 440, 1200) : 520;
        MusicHeight = double.IsFinite(MusicHeight) ? Math.Clamp(MusicHeight, 140, 400) : 200;
        if (!Enum.IsDefined(MusicText)) MusicText = CompactMusicText.None;
        if (!Enum.IsDefined(ClockStyle)) ClockStyle = ClockStyle.Minimal;
        if (!Enum.IsDefined(ClockColor)) ClockColor = ClockColor.White;
        ClockCustomColor = AppPresets.NormalizeColor(ClockCustomColor) is { Length: > 0 } custom ? custom : ClockColors.DefaultCustom;
        var stops = (ClockGradient ?? []).Select(AppPresets.NormalizeColor).ToList();
        ClockGradient = stops.Count == 3 && stops.All(stop => stop.Length > 0) ? stops : [.. ClockColors.Sunset];
        if (!Enum.IsDefined(DefaultPage)) DefaultPage = DefaultDeckPage.Last;
        if (!Enum.IsDefined(LastPage)) LastPage = DeckPage.Usage;
        OpenDelay = Math.Clamp(OpenDelay, 0, 2000);
        CloseDelay = Math.Clamp(CloseDelay, 0, 2000);
        AnimationDuration = Math.Clamp(AnimationDuration, 100, 1000);
        if (!Enum.IsDefined(Theme)) Theme = SettingsTheme.System;
        Language = Loc.Normalize(Language);
        if (!Enum.IsDefined(Style)) Style = CompactStyle.Notch;
        if (QuickSize is { } quick && !Enum.IsDefined(quick)) QuickSize = null;
        if (!Enum.IsDefined(Maximized)) Maximized = WindowBehavior.Line;
        if (!Enum.IsDefined(Borderless)) Borderless = WindowBehavior.Hide;
        if (!Enum.IsDefined(Exclusive)) Exclusive = WindowBehavior.Hide;
    }
}

// appVersion is the running application's version; it defaults to the entry assembly's version.
// settings.json holds the settings of the newest version that saved it, plus "Snapshots": the settings each earlier
// version last saved, keyed by version. Upgrading keeps the older version's settings there; a downgraded BrimDeck reads
// and saves only its own snapshot, so the newer version's settings stay as they were.
public sealed class SettingsStore(string directory, string? appVersion = null)
{
    private const string SnapshotsKey = "Snapshots";
    private const int KeptSnapshots = 5;
    private readonly string _appVersion = ApplicationVersion.Parse(appVersion ?? ApplicationVersion.Current).ToString(3);
    private JsonObject _snapshots = [];
    // The file as saved by a newer version; saving replaces only this version's snapshot in it.
    private JsonObject? _newerFile;
    public string DirectoryPath { get; } = directory;
    public string FilePath => Path.Combine(DirectoryPath, "settings.json");
    // Worded when shown, in the language the loaded settings choose.
    private Func<string>? _loadWarning;
    public string? LoadWarning => _loadWarning?.Invoke();
    // True when the last Load found no settings file, or none it could read, and returned defaults.
    public bool IsFirstStart { get; private set; }
    // Without saved settings the interface follows the Windows display language; saved settings keep their language.
    private DeckSettings FirstStart() { IsFirstStart = true; return new() { Language = Loc.SystemLanguage() }; }
    public DeckSettings Load()
    {
        _snapshots = []; _newerFile = null; IsFirstStart = false; _loadWarning = null;
        try
        {
            if (!File.Exists(FilePath)) return FirstStart();
            var text = File.ReadAllText(FilePath);
            var root = SettingsMigrations.Parse(text);
            var running = ApplicationVersion.Parse(_appVersion);
            var saved = SettingsMigrations.VersionOf(root);
            var snapshots = root[SnapshotsKey] as JsonObject ?? [];
            root.Remove(SnapshotsKey);
            if (saved > running)
            {
                _newerFile = SettingsMigrations.Parse(text);
                var own = snapshots.Where(pair => pair.Value is JsonObject && ApplicationVersion.Parse(pair.Key) <= running)
                    .OrderByDescending(pair => ApplicationVersion.Parse(pair.Key)).Select(pair => (JsonObject)pair.Value!.DeepClone()).FirstOrDefault();
                _loadWarning = own is null
                    ? () => Loc.T($"设置文件由 BrimDeck {saved.ToString(3)} 保存。当前版本 {_appVersion} 读取其中能识别的设置，之后的修改只保存给 {_appVersion}，不影响 {saved.ToString(3)} 的设置。",
                        $"The settings file was saved by BrimDeck {saved.ToString(3)}. Version {_appVersion} reads the settings it recognizes; later changes are saved for {_appVersion} only and leave the settings of {saved.ToString(3)} unchanged.")
                    : () => Loc.T($"设置文件由 BrimDeck {saved.ToString(3)} 保存。当前版本 {_appVersion} 使用自己上次保存的设置，修改不影响 {saved.ToString(3)} 的设置。",
                        $"The settings file was saved by BrimDeck {saved.ToString(3)}. Version {_appVersion} uses the settings it saved last; changes leave the settings of {saved.ToString(3)} unchanged.");
                return SettingsMigrations.Read(own ?? root, running, out _);
            }
            _snapshots = snapshots;
            // Keep what an older version saved so that returning to it restores its settings. Files saved before
            // versioning cannot be told apart and are not kept.
            if (saved < running && saved > new Version(0, 0, 0)) _snapshots[saved.ToString(3)] = root.DeepClone();
            return SettingsMigrations.Read(root, running, out _);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            _loadWarning = () => Loc.T("无法读取已保存的设置，本次使用默认值。", "The saved settings could not be read. Default settings are used this time.");
            return FirstStart();
        }
    }
    public void Save(DeckSettings settings)
    {
        settings.AppVersion = _appVersion;
        settings.Normalize();
        var own = JsonSerializer.SerializeToNode(settings)!.AsObject();
        JsonObject file;
        if (_newerFile is not null)
        {
            file = (JsonObject)_newerFile.DeepClone();
            var snapshots = file[SnapshotsKey] as JsonObject ?? [];
            file.Remove(SnapshotsKey);
            snapshots[_appVersion] = own;
            file[SnapshotsKey] = snapshots;
        }
        else
        {
            file = own;
            var running = ApplicationVersion.Parse(_appVersion);
            var kept = _snapshots.Where(pair => pair.Value is JsonObject && ApplicationVersion.Parse(pair.Key) < running)
                .OrderByDescending(pair => ApplicationVersion.Parse(pair.Key)).Take(KeptSnapshots)
                .Select(pair => KeyValuePair.Create(pair.Key, pair.Value!.DeepClone())).ToList();
            if (kept.Count > 0) file[SnapshotsKey] = new JsonObject(kept!);
        }
        Directory.CreateDirectory(DirectoryPath);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, file.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, FilePath, true);
    }
}
