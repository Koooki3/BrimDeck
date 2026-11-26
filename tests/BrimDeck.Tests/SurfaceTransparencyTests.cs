using System.Text.Json;
using BrimDeck.Core;

// The background transparency setting (DeckSettings.SurfaceTransparency): opaque by default and in files saved before it existed,
// and held to 0 to MaxSurfaceTransparency percent whatever a hand-edited file says.
internal static class SurfaceTransparencyTests
{
    public static void Run(Action<string, bool> check)
    {
        check("the surface starts opaque", new DeckSettings().SurfaceTransparency == 0);
        var old = JsonSerializer.Deserialize<DeckSettings>("{}")!; old.Normalize();
        check("a file without the setting keeps the surface opaque", old.SurfaceTransparency == 0);
        var high = new DeckSettings { SurfaceTransparency = 150 }; high.Normalize();
        var low = new DeckSettings { SurfaceTransparency = -20 }; low.Normalize();
        var kept = new DeckSettings { SurfaceTransparency = 35 }; kept.Normalize();
        check("the transparency is held to its range", high.SurfaceTransparency == DeckSettings.MaxSurfaceTransparency && low.SurfaceTransparency == 0 && kept.SurfaceTransparency == 35);
        check("a copy keeps the transparency", kept.Copy().SurfaceTransparency == 35);
        check("legacy background carries over to both ordinary states and leaves active glass unchanged",
            kept.PanelBackgroundTransparency(false, false) == 35 && kept.PanelBackgroundTransparency(true, false) == 35
            && kept.PanelBackgroundTransparency(false, true) == 0 && kept.PanelBackgroundTransparency(true, true) == 0
            && kept.PanelRimTransparency(false) == 0 && kept.PanelRimTransparency(true) == 0);
        kept.CollapsedBackgroundTransparency = 0;
        kept.ExpandedBackgroundTransparency = 150;
        kept.CollapsedRimTransparency = -15;
        kept.ExpandedRimTransparency = 75;
        kept.Normalize();
        var imported = JsonSerializer.Deserialize<DeckSettings>(JsonSerializer.Serialize(kept.Copy()))!;
        imported.Normalize();
        check("independent settings preserve explicit zero and full transparency through a settings round trip",
            imported.PanelBackgroundTransparency(false, false) == 0 && imported.PanelBackgroundTransparency(true, true) == 100
            && imported.PanelRimTransparency(false) == 0 && imported.PanelRimTransparency(true) == 75);
        imported.ResetPanelTransparency();
        check("reset clears independent overrides and the legacy background", imported.SurfaceTransparency == 0
            && imported.CollapsedBackgroundTransparency is null && imported.ExpandedBackgroundTransparency is null
            && imported.CollapsedRimTransparency is null && imported.ExpandedRimTransparency is null);
    }
}
