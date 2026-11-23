using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BrimDeck;

// The focus's round glyph buttons (delete, copy; NoticeGlyphFace) draw their glyph from the panel's icon font and text size tokens,
// so they follow those tokens together with the rest of the panel.
internal static class NoticeGlyphFaceTests
{
    public static void Run(Action<string, bool> check)
    {
        var face = NoticeGlyphFace.Of("\uE8C8", UI.Primary);
        check("notice glyph pill: the glyph is drawn in the panel's icon font (Fonts.Icon)", ReferenceEquals(face.FontFamily, BrimDeck.Fonts.Icon));
        check("notice glyph pill: the glyph has the strong text size (TextSize.Strong)", face.FontSize == TextSize.Strong);
        face.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        check("notice glyph pill: the glyph is laid out with ink, centred in its button",
            face.DesiredSize.Width > 0 && face.HorizontalAlignment == HorizontalAlignment.Center && face.VerticalAlignment == VerticalAlignment.Center);
    }
}
