namespace BrimDeck.Core;

// The pages of the open panel, in one place: their order in the page switch, whether each is shown, the size each asks for,
// and which page the panel falls back to. The panel and the settings window ask here instead of comparing pages themselves.
public static class DeckPages
{
    // The page switch, left to right.
    public static readonly IReadOnlyList<DeckPage> Order = [DeckPage.Usage, DeckPage.Music, DeckPage.Monitor, DeckPage.Notices];

    // The recent notices page exists while dynamic notices are on.
    public static bool Shown(DeckSettings settings, DeckPage page) => page switch
    {
        DeckPage.Usage => settings.UsagePage, DeckPage.Music => settings.MusicPage, DeckPage.Monitor => settings.MonitorPage,
        DeckPage.Notices => settings.NoticesEnabled && settings.NoticesPage, _ => false
    };

    public static IReadOnlyList<DeckPage> Visible(DeckSettings settings) => Order.Where(page => Shown(settings, page)).ToList();

    // The page shown when wanted is asked for: wanted while it is shown, otherwise the first shown page in switch order.
    // With every page switched off it is the usage page, which then says that it is off.
    public static DeckPage Resolve(DeckSettings settings, DeckPage wanted)
        => Shown(settings, wanted) ? wanted : Visible(settings) is [var first, ..] ? first : DeckPage.Usage;

    // The page the panel opens on; null keeps the last viewed page.
    public static DeckPage? Opening(DefaultDeckPage choice) => choice switch
    {
        DefaultDeckPage.Usage => DeckPage.Usage, DefaultDeckPage.Music => DeckPage.Music, DefaultDeckPage.Monitor => DeckPage.Monitor,
        DefaultDeckPage.Notices => DeckPage.Notices, _ => null
    };

    // Every page's size lies within these; the usage page is also at least as wide as its columns need (MinimumWidth).
    public const double MinWidth = 440, MaxWidth = 1200, MinHeight = 140, MaxHeight = 400;

    public static (double MinWidth, double MaxWidth, double MinHeight, double MaxHeight) Limits(DeckSettings settings, DeckPage page)
        => (page == DeckPage.Usage ? Math.Max(MinWidth, settings.MinimumWidth) : MinWidth, MaxWidth, MinHeight, MaxHeight);

    // Sets a page's size, as dragging an edge of the open panel does; the values are kept within the page's limits.
    public static void SetSize(DeckSettings settings, DeckPage page, double width, double height)
    {
        var (minWidth, maxWidth, minHeight, maxHeight) = Limits(settings, page);
        width = Math.Clamp(width, minWidth, Math.Max(minWidth, maxWidth)); height = Math.Clamp(height, minHeight, maxHeight);
        switch (page)
        {
            case DeckPage.Music: settings.MusicWidth = width; settings.MusicHeight = height; break;
            case DeckPage.Monitor: settings.MonitorWidth = width; settings.MonitorHeight = height; break;
            case DeckPage.Notices: settings.NoticesWidth = width; settings.NoticesHeight = height; break;
            default: settings.Width = width; settings.Height = height; break;
        }
    }

    public static (double Width, double Height) Size(DeckSettings settings, DeckPage page) => page switch
    {
        DeckPage.Music => (settings.MusicWidth, settings.MusicHeight),
        DeckPage.Monitor => (settings.MonitorWidth, settings.MonitorHeight),
        DeckPage.Notices => (settings.NoticesWidth, settings.NoticesHeight),
        _ => (Math.Max(settings.Width, settings.MinimumWidth), settings.Height)
    };

    // The largest width and height among the shown pages (the usage page when none is): the window holds any of them, so
    // switching pages never resizes the window itself.
    public static (double Width, double Height) Largest(DeckSettings settings)
    {
        var pages = Visible(settings) is { Count: > 0 } visible ? visible : [DeckPage.Usage];
        return (pages.Max(page => Size(settings, page).Width), pages.Max(page => Size(settings, page).Height));
    }
}
