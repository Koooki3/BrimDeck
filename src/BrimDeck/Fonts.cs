using System.Windows;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// The one entry for fonts: every family BrimDeck draws text in comes from here (UI.TextFont, UI.PanelFont and UI.DisplayFont are
// these, kept for the call sites that already use them), and nothing else names a font (FontSourceTests scans for it).
//
// Text roles (UiFontRole) take the user's font (DeckSettings.UiFont) in front of their usual chain, and are republished as
// resources so that XAML follows with DynamicResource. Symbols (Icon, IconMdl2), code (Mono, Code) and the clock styles' own
// faces (Bundled) are fixed: a font chosen for text never reaches them.
//
// Anything built from a font's metrics (a Typeface, a FormattedText, a measured width) is valid for one Version only; the cache
// beside it compares the version it was made at (MonitorDraw does), or is rebuilt when Changed is raised.
internal static class Fonts
{
    private static readonly object Sync = new();
    private static string _custom = "";
    private static readonly FontFamily?[] Families = new FontFamily?[Enum.GetValues<UiFontRole>().Length];
    private static readonly Dictionary<(UiFontRole, FontWeight), Typeface> Faces = [];

    // Counts the changes of the chosen font, from 0; text laid out at another count is stale.
    public static int Version { get; private set; }
    // The family the user chose, empty for the app's own fonts.
    public static string Custom => _custom;
    // Raised after a different font is made current and published.
    public static event Action? Changed;

    public static FontFamily Text => Of(UiFontRole.Text);
    public static FontFamily Panel => Of(UiFontRole.Panel);
    public static FontFamily Display => Of(UiFontRole.Display);
    public static FontFamily Menu => Of(UiFontRole.Menu);
    // Plain Segoe UI for small fixed labels.
    public static FontFamily Label => Of(UiFontRole.Label);

    // Symbols and code do not change with the user's font.
    public static readonly FontFamily Icon = new(UiFonts.IconChain);
    public static readonly FontFamily IconMdl2 = new(UiFonts.IconMdl2Chain);
    public static readonly FontFamily Mono = new(UiFonts.MonoChain);

    // The resource each role is published under (App.xaml declares the defaults under the same keys).
    public static string ResourceKey(UiFontRole role) => role switch
    {
        UiFontRole.Text => "AppTextFont",
        UiFontRole.Display => "FontDisplay",
        UiFontRole.Menu => "FontMenu",
        UiFontRole.Label => "FontLabel",
        _ => "FontPanel"
    };

    public static FontFamily Of(UiFontRole role)
    {
        lock (Sync) return Families[(int)role] ??= new FontFamily(UiFonts.Chain(role, _custom));
    }

    // The role's family in a weight, kept until the font changes; for text drawn or measured with a Typeface.
    public static Typeface Face(UiFontRole role, FontWeight? weight = null)
    {
        var w = weight ?? FontWeights.Normal;
        lock (Sync)
        {
            if (!Faces.TryGetValue((role, w), out var face))
                Faces[(role, w)] = face = new Typeface(Of(role), FontStyles.Normal, w, FontStretches.Normal);
            return face;
        }
    }

    // Makes the saved family current. False when it already was (nothing is published or raised then).
    public static bool Use(string? name)
    {
        var clean = UiFonts.Normalize(name);
        lock (Sync)
        {
            if (clean == _custom) return false;
            _custom = clean; Array.Clear(Families); Faces.Clear(); Version++;
        }
        if (Application.Current is { } app) Publish(app.Resources);
        Changed?.Invoke();
        return true;
    }

    // Puts the current text families into a resource dictionary (the application's), where DynamicResource finds them.
    public static void Publish(ResourceDictionary resources)
    {
        foreach (var role in Enum.GetValues<UiFontRole>()) resources[ResourceKey(role)] = Of(role);
    }

    // A face shipped inside the app (Assets/Fonts): the clock styles' own letterforms, which the user's font does not replace.
    internal static FontFamily Bundled(string name) => new(new Uri("pack://application:,,,/BrimDeck;component/"), "./Assets/Fonts/#" + name);

    // The script editor's face. Consolas has no Chinese glyphs. Chinese and full-width punctuation come from Microsoft YaHei UI, whose
    // glyphs are 1 em wide, scaled so each takes exactly two Consolas columns (2 × 0.5498 em); trailing comments then line up.
    public static FontFamily Code => _code.Value;
    private static readonly Lazy<FontFamily> _code = new(() =>
    {
        var consolas = new FontFamily("Consolas");
        var family = new FontFamily { Baseline = consolas.Baseline, LineSpacing = consolas.LineSpacing };
        family.FamilyNames[System.Windows.Markup.XmlLanguage.GetLanguage("en-us")] = "BrimDeck Code";
        family.FamilyMaps.Add(new FontFamilyMap { Unicode = "2E80-9FFF,F900-FAFF,FE30-FE4F,FF00-FF60,FFE0-FFE6", Target = "Microsoft YaHei UI", Scale = 2 * 0.5498046875 });
        family.FamilyMaps.Add(new FontFamilyMap { Unicode = "0000-10FFFF", Target = "Consolas" });
        return family;
    });
}
