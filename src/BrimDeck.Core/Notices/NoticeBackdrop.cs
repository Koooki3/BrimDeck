namespace BrimDeck.Core.Notices;

// What lies under a notice's surface. Opaque: the theme's surface color. Clear: a glass theme's tint over the bare desktop, where
// Windows cannot blur it (battery saver, a remote session, no composition). Blur: the same tint over the blur of GlassBackdrop
// (liquid glass themes: the system acrylic), as the island itself has it. The thin indicator has no surface.
public enum NoticeBackdrop { Opaque, Clear, Blur }

public static class NoticeBackdrops
{
    // glassShown: the theme's glass is on screen (a glass theme, transparency effects on, no high contrast: PanelTheme.GlassShown).
    // blurAvailable: Windows can draw the blur now (GlassBackdrop.Available, which includes glassShown).
    public static NoticeBackdrop Of(IslandForm form, bool glassShown, bool blurAvailable)
        => form == IslandForm.Line || !glassShown ? NoticeBackdrop.Opaque : blurAvailable ? NoticeBackdrop.Blur : NoticeBackdrop.Clear;
}
