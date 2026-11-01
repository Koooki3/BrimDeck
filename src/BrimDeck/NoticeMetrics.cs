namespace BrimDeck;

// The sizes of a notice on the island (IslandNotice), from the panel's spacing tokens (Space): the icon's plate and the gap after it,
// a countdown's ring and its stroke, and how fast words longer than the island scroll. DIP at the island's scale.
internal static class NoticeMetrics
{
    public const double PlateSize = Space.S16 + Space.S4, PlateGap = Space.S8, RingSize = Space.S16 + Space.Hair, RingThickness = Space.Hair;
    // DIP a second.
    public const double ScrollSpeed = 32;
}
