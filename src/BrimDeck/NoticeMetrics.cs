namespace BrimDeck;

// The sizes of a notice on the island (IslandNotice), from the panel's spacing tokens (Space): the icon's plate and the gap after it,
// . DIP at the island's scale.
internal static class NoticeMetrics
{
    public const double PlateSize = Space.S16 + Space.S4, PlateGap = Space.S8;
}
