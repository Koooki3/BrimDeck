using System.Text.Json.Serialization;

namespace BrimDeck.Core;

// The system monitor page's color schemes: ftop's three palettes by Nongfsq (https://github.com/Nongfsq/ftop, MIT License;
// see ThirdPartyNotices.txt), Theme, which takes the colors of the panel theme in use (the app resolves it, as only it knows
// the theme), and Custom, a scheme of the user's own (MonitorCustomColors). Saved as numbers, so new members go at the end.
public enum MonitorPalette { Sea, Graphite, Warm, Theme, Custom }

// The colors of a scheme, in the order the settings list them: Cpu the CPU readings and the fastest cores, Efficiency the
// slowest cores (the kinds between take the colors between), then memory, GPU, download and upload. The collapsed readings
// and the expanded page take the same ones.
public enum MonitorRole { Cpu, Efficiency, Memory, Gpu, Download, Upload }

// The colors of one of ftop's palettes on a dark or a light panel theme. Theme and Custom have no fixed colors and give Sea's
// here. ftop's palettes have three main colors; memory and upload take the efficiency color, download the performance color.
// Warning and Critical are not part of a scheme: they are the collapsed readings' colors at those levels (ReadingLevels),
// which the app takes from the settings or the panel theme; For gives fixed ones.
public sealed record MonitorColors(string Performance, string Efficiency, string Memory, string Gpu, string Download, string Upload, string Warning, string Critical)
{
    public static MonitorColors For(MonitorPalette palette, bool dark = true)
    {
        var (performance, efficiency, gpu) = (palette, dark) switch
        {
            (MonitorPalette.Graphite, true) => ("#F3F4F6", "#9AA1AD", "#C9B98F"),
            (MonitorPalette.Graphite, false) => ("#1F2937", "#6B7280", "#8A6D2F"),
            (MonitorPalette.Warm, true) => ("#FF8468", "#FFC857", "#FF8FC4"),
            (MonitorPalette.Warm, false) => ("#DC4A2B", "#B9770A", "#B83280"),
            (_, true) => ("#5B9BFF", "#45D1BD", "#B294FF"),
            _ => ("#1F5FE0", "#0B8F7E", "#7A4FD6")
        };
        var (warning, critical) = dark ? ("#F0B54A", "#FF6B5E") : ("#B26A00", "#D23F31");
        return new(performance, efficiency, efficiency, gpu, performance, efficiency, warning, critical);
    }

    // The Theme scheme from a theme's colors (opaque "#RRGGBB"): the accent for the fastest cores, and for the slowest cores and
    // the GPU the first of the theme's other colors (cyan, pink, lime, amber in that order) that is clearly not a color already
    // taken, so a theme whose accent is its cyan still tells the kinds of core apart. Memory, download and upload as in For.
    public static MonitorColors FromTheme(string accent, IReadOnlyList<string> others, string warning, string critical)
    {
        const double Apart = 64;
        string Pick(params string[] taken) => others.FirstOrDefault(color => taken.All(other => ColorMath.Distance(color, other) >= Apart))
            ?? others.Where(color => !taken.Contains(color, StringComparer.OrdinalIgnoreCase)).DefaultIfEmpty(taken[^1]).First();
        string efficiency = Pick(accent), gpu = Pick(accent, efficiency);
        return new(accent, efficiency, efficiency, gpu, accent, efficiency, warning, critical);
    }

    public string this[MonitorRole role] => role switch
    {
        MonitorRole.Efficiency => Efficiency, MonitorRole.Memory => Memory, MonitorRole.Gpu => Gpu, MonitorRole.Download => Download,
        MonitorRole.Upload => Upload, _ => Performance
    };
    public MonitorColors With(MonitorRole role, string color) => role switch
    {
        MonitorRole.Efficiency => this with { Efficiency = color }, MonitorRole.Memory => this with { Memory = color }, MonitorRole.Gpu => this with { Gpu = color },
        MonitorRole.Download => this with { Download = color }, MonitorRole.Upload => this with { Upload = color }, _ => this with { Performance = color }
    };
    // Every color as drawn (the app moves them to read on the panel theme).
    public MonitorColors Map(Func<string, string> drawn) =>
        new(drawn(Performance), drawn(Efficiency), drawn(Memory), drawn(Gpu), drawn(Download), drawn(Upload), drawn(Warning), drawn(Critical));

    // Core groups run from the performance color to the efficiency color in even steps, so a processor with any number of
    // kinds of core has a color for each.
    public string Core(int group, int groups) => ColorMath.Mix(Performance, Efficiency, groups > 1 ? Math.Clamp(group, 0, groups - 1) / (double)(groups - 1) : 0);
    // A collapsed reading's color at a level: its own color in the normal state, the warning or critical color above it.
    public string Level(MemoryLevel level, string normal) => level switch { MemoryLevel.Critical => Critical, MemoryLevel.Elevated => Warning, _ => normal };

    public static string Name(MonitorPalette palette) => palette switch
    {
        MonitorPalette.Graphite => Loc.T("石墨", "Graphite"), MonitorPalette.Warm => Loc.T("暖色", "Warm"), MonitorPalette.Theme => Loc.T("跟随主题", "Theme"),
        MonitorPalette.Custom => Loc.T("自定义", "Custom"), _ => Loc.T("海洋", "Sea")
    };
}

// The Custom scheme: the preset it starts from (Base, never Custom) and the colors changed from it, "#RRGGBB" each, or null to
// follow the base. The colors not changed keep following the base, so they still suit a light or a dark panel theme.
public sealed class MonitorCustomColors
{
    public MonitorPalette Base { get; set; } = MonitorPalette.Theme;
    public string? Cpu { get; set; }
    public string? Efficiency { get; set; }
    public string? Memory { get; set; }
    public string? Gpu { get; set; }
    public string? Download { get; set; }
    public string? Upload { get; set; }

    public string? Get(MonitorRole role) => role switch
    {
        MonitorRole.Efficiency => Efficiency, MonitorRole.Memory => Memory, MonitorRole.Gpu => Gpu, MonitorRole.Download => Download,
        MonitorRole.Upload => Upload, _ => Cpu
    };
    public void Set(MonitorRole role, string? color)
    {
        switch (role)
        {
            case MonitorRole.Efficiency: Efficiency = color; break;
            case MonitorRole.Memory: Memory = color; break;
            case MonitorRole.Gpu: Gpu = color; break;
            case MonitorRole.Download: Download = color; break;
            case MonitorRole.Upload: Upload = color; break;
            default: Cpu = color; break;
        }
    }
    // Whether any color differs from the base's.
    [JsonIgnore] public bool Changed => Enum.GetValues<MonitorRole>().Any(role => Get(role) is not null);
    // The base's colors with the changed ones over them, each as drawn (by default as given).
    public MonitorColors Over(MonitorColors colors, Func<string, string>? drawn = null) =>
        Enum.GetValues<MonitorRole>().Aggregate(colors, (result, role) => Get(role) is { } own ? result.With(role, drawn is null ? own : drawn(own)) : result);
    // What the expanded page is built from (part of its key).
    [JsonIgnore] public string Key => string.Join(",", Enum.GetValues<MonitorRole>().Select(Get).Prepend(Base.ToString()));
    public MonitorCustomColors Copy() => (MonitorCustomColors)MemberwiseClone();
    // The base is a fixed scheme or Theme; a color that is not "#RRGGBB" follows the base.
    public void Normalize()
    {
        if (!Enum.IsDefined(Base) || Base == MonitorPalette.Custom) Base = MonitorPalette.Theme;
        foreach (var role in Enum.GetValues<MonitorRole>())
            Set(role, AppPresets.NormalizeColor(Get(role)) is { Length: > 0 } valid ? valid : null);
    }
}

public enum ListPlacement { None, Side, Below }
// How memory and network are shown: both on one line, one line each, or as blocks with their second lines and sparklines.
public enum MetricsMode { Line, Lines, Blocks }

// How the system monitor page fills the space it has, after ftop's layout ladder: a small page keeps the essentials, a
// large one shows more rather than bigger type. Sizes are DIPs of the page's content area, below its header.
// The ladder has steps only: within a step the page stretches (the list's share, its rows and columns, the graph's height), so
// resizing the panel by dragging rebuilds the page only where it crosses a step.
// Metrics: how much room memory and network take (MetricsMode). The core columns carry no figures at any size: pointing at one
// gives them, so the page stays light to read.
public sealed record MonitorLayout(double Figure, double Body, double Caption, MetricsMode Metrics, ListPlacement List, double RowHeight)
{
    public bool CompactMetrics => Metrics == MetricsMode.Line;
    public bool Processes => List != ListPlacement.None;
    // The most rows one column of the list shows, however tall the page, and the most columns a wide list has.
    public const int MaximumRows = 14, MaximumColumns = 2;
    // Fewer rows than this are not worth a list.
    public const int MinimumRows = 2;
    // The list's own header: its title and the sort switch.
    public const double ListHead = 26;
    // The readings keep this much height when the list sits below them.
    public const double StackedReadings = 170;
    // A side list takes this share of the width, within these bounds; a list this wide or wider has two columns.
    public const double ListShare = .42, ListMinimum = 220, ListMaximum = 560, TwoColumns = 470;

    public static MonitorLayout For(double width, double height, bool processes)
    {
        width = double.IsFinite(width) ? Math.Max(0, width) : 0;
        height = double.IsFinite(height) ? Math.Max(0, height) : 0;
        double figure = height < 120 ? 16 : height < 200 ? 19 : 23;
        double body = height < 120 ? 11 : 12, caption = height < 120 ? 10 : 10.5;
        double row = height < 160 ? 19 : 22;
        var list = !processes ? ListPlacement.None
            : width >= 560 && height - ListHead >= MinimumRows * row ? ListPlacement.Side
            : height >= StackedReadings + ListHead + MinimumRows * row ? ListPlacement.Below : ListPlacement.None;
        // The core columns need height to read; memory and network give theirs first. Readings above a list have its fixed height.
        double readings = list == ListPlacement.Below ? StackedReadings : height;
        var metrics = readings < 150 ? MetricsMode.Line : readings < 250 ? MetricsMode.Lines : MetricsMode.Blocks;
        return new(figure, body, caption, metrics, list, row);
    }

    // The rows and columns a list of this size shows.
    public static (int Rows, int Columns) ListGrid(double width, double height, double rowHeight)
    {
        if (!(rowHeight > 0) || !double.IsFinite(width) || !double.IsFinite(height)) return (0, 0);
        int rows = Math.Clamp((int)Math.Floor((height - ListHead) / rowHeight), 0, MaximumRows);
        if (rows < MinimumRows) return (0, 0);
        return (rows, width >= TwoColumns ? MaximumColumns : 1);
    }

    // The side list's width in a page of this width: its share, within its bounds, leaving the readings at least 260 DIP.
    public static double SideListWidth(double width) => Math.Clamp(Math.Round(width * ListShare), ListMinimum, Math.Min(ListMaximum, Math.Max(ListMinimum, width - 260)));
}
