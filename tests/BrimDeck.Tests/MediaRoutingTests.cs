using BrimDeck.Core;

internal static class MediaRoutingTests
{
    public static async Task Run(Action<string, bool> check)
    {
        int directCalls = 0, systemCalls = 0;
        async Task<bool> Route(MediaDispatchResult result) => await MediaRouting.DispatchAsync(
            () => { directCalls++; return Task.FromResult(result); },
            () => { systemCalls++; return Task.FromResult(true); });
        check("Dedicated control succeeds without sending a system command", await Route(MediaDispatchResult.Sent) && directCalls == 1 && systemCalls == 0);
        check("Unsent unsupported operation falls back exactly once", await Route(MediaDispatchResult.Unavailable) && directCalls == 2 && systemCalls == 1);
        check("Uncertain dispatch never repeats a next-track command", !await Route(MediaDispatchResult.Uncertain) && directCalls == 3 && systemCalls == 1);
        var primary = new MediaTrack { Id = "netease:1", Source = "网易云音乐", Title = "Song A", Artist = "Artist", PositionKnown = false,
            TimelinePending = true, State = MediaState.Unknown, CanPrevious = true };
        var controls = new MediaTrack { Title = "Song B", Artist = "Other", Album = "Wrong album", End = TimeSpan.FromMinutes(3),
            ReportedPosition = TimeSpan.FromSeconds(30), State = MediaState.Playing, CanToggle = true, CanNext = true, CanSeek = true,
            CanShuffle = true, CanRepeat = true, Shuffle = true, Repeat = MediaRepeat.Track };
        var merged = MediaRouting.Merge(primary, controls);
        check("A supplement adds only basic playback controls", merged.CanToggle && merged.CanNext && merged.CanPrevious && !merged.CanSeek && !merged.CanShuffle && !merged.CanRepeat);
        check("Supplement metadata and state cannot replace the primary", merged.Title == "Song A" && merged.Artist == "Artist" && merged.Album == "" && merged.State == MediaState.Unknown && merged.Id == primary.Id);
        check("Pending primary timeline never borrows system data", !merged.HasTimeline && merged.TimelinePending && merged.Duration == TimeSpan.Zero);
        check("Supplement modes cannot leak into the primary", !merged.Shuffle && merged.Repeat == MediaRepeat.None);
        check("A primary without a supplement is unchanged", MediaRouting.Merge(primary, null) == primary);
        check("FM disables previous even when SMTC advertises it", !MediaRouting.Merge(primary with { PreviousRestricted = true }, controls with { CanPrevious = true }).CanPrevious);

        var log = new MediaTrack { Id = "netease:7", Source = "网易云音乐", Title = "旧歌", Artist = "A / B", Album = "专辑", SongId = "netease:1", State = MediaState.Playing,
            End = TimeSpan.FromMinutes(3), ReportedPosition = TimeSpan.FromSeconds(100), PositionKnown = true, IsNeteaseLog = true };
        var smtc = new MediaTrack { Id = "system:x", Title = "新歌", Artist = "C", State = MediaState.Playing, CanToggle = true, CanNext = true, CanPrevious = true,
            PositionKnown = false, Shuffle = true, CanSeek = true, CanShuffle = true };
        var ahead = MediaRouting.PreferSystem(log, smtc);
        check("NetEase's session leads while the log is still on the previous song",
            ahead is { Title: "新歌", Artist: "C", Id: "netease:7", Source: "网易云音乐", SongId: "", IsNeteaseLog: true, State: MediaState.Playing } && !ahead.HasTimeline && ahead.TimelinePending);
        check("The leading session adds no seeking or modes in log mode", !ahead.CanSeek && !ahead.CanShuffle && !ahead.Shuffle && ahead.CanNext && ahead.CanToggle);
        var caught = MediaRouting.PreferSystem(log with { Title = "新歌", Artist = "C", SongId = "netease:2", ReportedPosition = TimeSpan.FromSeconds(1) }, smtc with { State = MediaState.Unknown });
        check("Once the log reaches the song it supplies id, album, timeline and a known state",
            caught is { SongId: "netease:2", Album: "专辑", State: MediaState.Playing } && caught.HasTimeline && caught.ReportedPosition == TimeSpan.FromSeconds(1));
        check("The session's state wins over the log's for the same song",
            MediaRouting.PreferSystem(log with { Title = "新歌", Artist = "C" }, smtc with { State = MediaState.Paused }).State == MediaState.Paused);
        check("A session without a title leaves the log in charge", MediaRouting.PreferSystem(log, smtc with { Title = " " }) is { Title: "旧歌", SongId: "netease:1" });
        check("The FM restriction from the log still disables previous", !MediaRouting.PreferSystem(log with { PreviousRestricted = true }, smtc).CanPrevious);
        check("Artist separators do not make a different song", MediaRouting.SameSong(log, log with { Artist = "A/B" }) && MediaRouting.SameSong(log, log with { Artist = "A、B" })
            && !MediaRouting.SameSong(log, log with { Artist = "A / C" }));
        check("A list that only adds artists is the same song", MediaRouting.SameSong(log with { Artist = "A" }, log) && MediaRouting.SameSong(log, log with { Artist = "B" })
            && !MediaRouting.SameSong(log with { Artist = "C" }, log));
        // Measured: NetEase's SMTC session names only the first artist ("\u5468\u6DF1") of a song its log lists as "\u5468\u6DF1 / <featured>".
        var logFull = log with { Title = "\u6625\u96EA", Artist = "\u5468\u6DF1 / Terry Zhong", SongId = "netease:2137074704", State = MediaState.Paused,
            End = TimeSpan.FromSeconds(208), ReportedPosition = TimeSpan.FromSeconds(6) };
        var smtcFirst = smtc with { Title = "\u6625\u96EA", Artist = "\u5468\u6DF1", State = MediaState.Paused };
        var spring = MediaRouting.PreferSystem(logFull, smtcFirst);
        check("The log's timeline is kept when the session names only the first artist",
            spring.HasTimeline && spring.Duration == TimeSpan.FromSeconds(208) && spring.ReportedPosition == TimeSpan.FromSeconds(6) && !spring.TimelinePending && spring.SongId == "netease:2137074704");
        check("The log names the matched song with its complete artist list", spring is { Title: "\u6625\u96EA", Artist: "\u5468\u6DF1 / Terry Zhong", State: MediaState.Paused });
        check("Width and spacing differences and a version suffix still match the log",
            MediaRouting.PreferSystem(logFull, smtcFirst with { Title = "\u6625\u96EA (Live)" }).HasTimeline
            && MediaRouting.PreferSystem(logFull with { Title = "Song\u3000A" }, smtcFirst with { Title = "\uFF33\uFF4F\uFF4E\uFF47 A" }).HasTimeline);
        check("A different song keeps the log's timeline out", !MediaRouting.PreferSystem(logFull, smtcFirst with { Title = "\u6625\u96EA\u4E4B\u540E" }).HasTimeline
            && !MediaRouting.PreferSystem(logFull, smtcFirst with { Artist = "\u5176\u4ED6" }).HasTimeline);

        // Measured: Apple Music reports "<artist> \u2014 <album>" as the artist with an empty album.
        const string apple = "AppleInc.AppleMusicWin_nzyj5cx40ttqa!App";
        check("Apple Music's artist field is split into artist and album",
            MediaRouting.SessionArtistAlbum(apple, "\u4F0D\u4F70 & China Blue \u2014 \u6CEA\u6865 (\"Rock Star 1\" Live) - Single", "")
                == ("\u4F0D\u4F70 & China Blue", "\u6CEA\u6865 (\"Rock Star 1\" Live) - Single"));
        check("Only the first dash ends Apple Music's artist",
            MediaRouting.SessionArtistAlbum(apple, "Artist \u2014 Album \u2014 Deluxe", "") == ("Artist", "Album \u2014 Deluxe"));
        check("Apple Music's artist without a dash or with an album is kept",
            MediaRouting.SessionArtistAlbum(apple, "Artist", "") == ("Artist", "")
            && MediaRouting.SessionArtistAlbum(apple, "Artist \u2014 Album", "Album") == ("Artist \u2014 Album", "Album"));
        check("Other players' artists are not split",
            MediaRouting.SessionArtistAlbum("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "Artist \u2014 Album", "") == ("Artist \u2014 Album", ""));

        // Measured: Apple Music reported 2.000 at :57.677, again 2.000 at :58.219, then 3.000 at :58.433.
        var t0 = new DateTimeOffset(2026, 10, 25, 23, 40, 57, 677, TimeSpan.Zero);
        var known = new MediaTrack { Title = "Song", Artist = "Artist", State = MediaState.Playing, PositionKnown = true,
            End = TimeSpan.FromSeconds(245), ReportedPosition = TimeSpan.FromSeconds(2), PositionAt = t0 };
        var repeated = known with { PositionAt = t0.AddMilliseconds(542) };
        var next = known with { ReportedPosition = TimeSpan.FromSeconds(3), PositionAt = t0.AddMilliseconds(756) };
        check("A whole-second report behind the known position keeps the known timeline",
            MediaRouting.KeepsTimeline(known, repeated, t0.AddMilliseconds(600)));
        check("A report ahead of the known position is taken", !MediaRouting.KeepsTimeline(known, next, t0.AddMilliseconds(800)));
        check("A step back of more than a second is a seek and is taken",
            !MediaRouting.KeepsTimeline(known, known with { ReportedPosition = TimeSpan.Zero, PositionAt = t0.AddMilliseconds(500) }, t0.AddMilliseconds(600)));
        check("A paused, different or untimed song is taken as reported",
            !MediaRouting.KeepsTimeline(known, repeated with { State = MediaState.Paused }, t0.AddMilliseconds(600))
            && !MediaRouting.KeepsTimeline(known, repeated with { Title = "Other" }, t0.AddMilliseconds(600))
            && !MediaRouting.KeepsTimeline(known, repeated with { PositionKnown = false }, t0.AddMilliseconds(600)));
        // Measured on the Traditional Chinese interface: off and all share a glyph, one has its own.
        string loop = AppleMusicRepeat.RepeatGlyph, one = AppleMusicRepeat.RepeatOneGlyph;
        var traditional = new AppleMusicRepeat();
        check("Apple Music's repeat states are read from the measured names and glyphs",
            traditional.Observe(loop, "不要重複") == MediaRepeat.None && traditional.Observe(loop, "全部重複") == MediaRepeat.List
            && traditional.Observe(one, "重複單首歌曲") == MediaRepeat.Track && traditional.Observe("?", "x") is null);
        var english = new AppleMusicRepeat();
        bool unknownFirst = english.Observe(loop, "Repeat Off") is null;
        english.Observe(loop, "Repeat All"); english.Observe(one, "Repeat One");
        check("Apple Music's repeat names in another language are learnt from the cycle",
            unknownFirst && english.Observe(loop, "Repeat Off") == MediaRepeat.None && english.Observe(loop, "Repeat All") == MediaRepeat.List);
        var paused = known with { State = MediaState.Paused, ReportedPosition = TimeSpan.FromSeconds(1), PositionAt = t0.AddMilliseconds(80) };
        check("The pause Apple Music reports right after a seek is passed over",
            MediaRouting.SettlingAfterSeek(known, paused, t0, t0.AddMilliseconds(100)));
        check("A pause after the settling time, without a seek, or on another song is shown",
            !MediaRouting.SettlingAfterSeek(known, paused, t0, t0 + MediaRouting.SeekSettle) && !MediaRouting.SettlingAfterSeek(known, paused, null, t0)
            && !MediaRouting.SettlingAfterSeek(known, paused with { Title = "Other" }, t0, t0.AddMilliseconds(100))
            && !MediaRouting.SettlingAfterSeek(known with { State = MediaState.Paused }, paused, t0, t0.AddMilliseconds(100)));
        check("Apple Music is recognized by its application id",
            MediaRouting.IsAppleMusic(apple) && !MediaRouting.IsAppleMusic("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"));
    }
}
