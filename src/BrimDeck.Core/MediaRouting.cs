namespace BrimDeck.Core;

public enum MediaDispatchResult { Unavailable, Sent, Uncertain }

public static class MediaRouting
{
    // Only an operation known not to have been sent may fall through to another transport.
    public static async Task<bool> DispatchAsync(Func<Task<MediaDispatchResult>> dedicated, Func<Task<bool>> system)
        => await dedicated() switch
        {
            MediaDispatchResult.Sent => true,
            MediaDispatchResult.Unavailable => await system(),
            _ => false
        };

    // Players join several artists differently ("A / B" in NetEase's log, "A/B" or "A, B" elsewhere), and
    // NetEase's SMTC session names only the first artist of a song its log lists in full (measured:
    // "<artist>" against "<artist> / <featured>"). Neither the separators nor a list that only adds
    // artists makes a different song; a different artist does.
    public static bool SameSong(MediaTrack a, MediaTrack b) =>
        string.Equals(a.Title.Trim(), b.Title.Trim(), StringComparison.OrdinalIgnoreCase) && SameArtists(a.Artist, b.Artist);
    private static bool SameArtists(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return true;
        if (string.Equals(Joined(a), Joined(b), StringComparison.OrdinalIgnoreCase)) return true;
        var first = Names(a); var second = Names(b);
        return first.Count > 0 && second.Count > 0 && (first.IsSubsetOf(second) || second.IsSubsetOf(first));
    }
    private static readonly char[] ArtistSeparators = ['/', ',', '\u3001', '&', ';', '\uFF0F', '\uFF0C', '\uFF06', '\uFF1B'];
    private static string Joined(string value) => string.Concat(value.Where(c => !char.IsWhiteSpace(c) && Array.IndexOf(ArtistSeparators, c) < 0));
    private static HashSet<string> Names(string value) => value.Split(ArtistSeparators, StringSplitOptions.RemoveEmptyEntries)
        .Select(Joined).Where(name => name.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);

    // Whether NetEase's log is on the song its SMTC session reports. The two write a title independently,
    // so full-width forms, spacing and a version suffix such as "(Live)" on one side still match.
    public static bool LogMatchesSession(MediaTrack log, MediaTrack system) =>
        !string.IsNullOrWhiteSpace(log.Title) && (SameSong(log, system) || SimilarTitle(log.Title, system.Title) && SameArtists(log.Artist, system.Artist));
    private static bool SimilarTitle(string a, string b)
    {
        string x = Folded(a), y = Folded(b);
        if (x.Length == 0 || y.Length == 0) return false;
        if (string.Equals(x, y, StringComparison.OrdinalIgnoreCase)) return true;
        var (shorter, longer) = x.Length < y.Length ? (x, y) : (y, x);
        return longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase) && "([{<-\u3010\u300A\u300C".Contains(longer[shorter.Length]);
    }
    private static string Folded(string value) => string.Concat(value.Normalize(System.Text.NormalizationForm.FormKC).Where(c => !char.IsWhiteSpace(c)));

    // A supplement owns only these three controls. It cannot replace metadata, time,
    // modes, or state, even while the primary is waiting for the next song to load.
    public static MediaTrack Merge(MediaTrack primary, MediaTrack? controls) => primary with
    {
        CanToggle = primary.CanToggle || controls?.CanToggle == true,
        CanPrevious = !primary.PreviousRestricted && (primary.CanPrevious || controls?.CanPrevious == true),
        CanNext = primary.CanNext || controls?.CanNext == true
    };

    // NetEase without full control, with its SMTC switched on: the session reports a skip about 60 ms
    // after it happens (measured in the player's own log), so it supplies the song, its artwork and its
    // state. The log still supplies what the session lacks for the same song: its id, album, timeline and
    // the FM restriction. A session without a title leaves the log in charge, as before.
    public static MediaTrack PreferSystem(MediaTrack log, MediaTrack? system)
    {
        if (system is null || string.IsNullOrWhiteSpace(system.Title)) return Merge(log, system);
        bool same = LogMatchesSession(log, system);
        // The log's position is exact for its song; the session's is used only when the log is on another song.
        var time = same && log.HasTimeline ? log : system.HasTimeline ? system : null;
        return new MediaTrack
        {
            // Once the log is on the session's song it names the song: its artist list is complete.
            Id = log.Id, Source = log.Source, Title = same ? log.Title : system.Title, Artist = same && log.Artist.Length > 0 ? log.Artist : system.Artist,
            Album = system.Album.Length == 0 && same ? log.Album : system.Album,
            SongId = same ? log.SongId : "", EmbeddedLyrics = same ? log.EmbeddedLyrics : "",
            CurrentLyric = same ? log.CurrentLyric : null, CurrentLyricStart = same ? log.CurrentLyricStart : null,
            State = system.State == MediaState.Unknown && same ? log.State : system.State,
            IsNeteaseLog = true, PreviousRestricted = log.PreviousRestricted,
            PositionKnown = time?.PositionKnown ?? false,
            // Until the log reaches the session's song, nothing has reported where that song is.
            TimelinePending = time is null && (!same || log.TimelinePending),
            Start = time?.Start ?? TimeSpan.Zero, End = time?.End ?? TimeSpan.Zero, MinSeek = time?.MinSeek ?? TimeSpan.Zero, MaxSeek = time?.MaxSeek ?? TimeSpan.Zero,
            ReportedPosition = time?.ReportedPosition ?? TimeSpan.Zero, PositionAt = time?.PositionAt ?? system.PositionAt, Rate = time?.Rate ?? 1,
            CanToggle = log.CanToggle || system.CanToggle,
            CanPrevious = !log.PreviousRestricted && (log.CanPrevious || system.CanPrevious),
            CanNext = log.CanNext || system.CanNext
        };
    }
}
