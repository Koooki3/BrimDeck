using System.Windows.Media.Imaging;

namespace BrimDeck;

// What a track change shows on the whole island (IslandNotice, MusicTrackNoticeWide): the song's title, its artist and its cover.
// It is the payload of the coordinator's track item; nothing of it is kept or listed among the notices.
internal sealed record TrackNoticeContent(string Title, string Artist, BitmapSource? Cover);
