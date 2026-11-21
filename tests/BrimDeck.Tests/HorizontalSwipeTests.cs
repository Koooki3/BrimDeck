using BrimDeck.Core;

internal static class HorizontalSwipeTests
{
    public static void Run(Action<string, bool> check)
    {
        var start = DateTimeOffset.UtcNow;
        DateTimeOffset At(int ms) => start.AddMilliseconds(ms);

        var swipe = new HorizontalSwipe();
        check("Touchpad parts add up to one swipe", swipe.Feed(40, At(0)) == 0 && swipe.Feed(40, At(10)) == 0 && swipe.Feed(40, At(20)) == 1);
        check("The coasting rest of a swipe does not act again", swipe.Feed(200, At(30)) == 0 && swipe.Feed(120, At(300)) == 0);
        check("A swipe after a quiet wheel acts again", swipe.Feed(-120, At(300 + 300)) == -1);

        swipe = new HorizontalSwipe();
        check("A swipe back made while the first still coasts acts at once; a flick the other way between coasting parts does not",
            swipe.Feed(120, At(0)) == 1 && swipe.Feed(60, At(20)) == 0 && swipe.Feed(-30, At(40)) == 0 && swipe.Feed(40, At(60)) == 0
            && swipe.Feed(-60, At(80)) == 0 && swipe.Feed(-60, At(100)) == -1 && swipe.Feed(-80, At(120)) == 0
            && swipe.Feed(70, At(140)) == 0 && swipe.Feed(50, At(160)) == 1);
        swipe = new HorizontalSwipe();
        swipe.Vertical(At(0));
        check("A scroll that turns sideways and back never acts", swipe.Feed(200, At(50)) == 0 && swipe.Feed(-200, At(100)) == 0 && swipe.Feed(200, At(150)) == 0);

        swipe = new HorizontalSwipe();
        check("Parts that turn back start counting afresh", swipe.Feed(100, At(0)) == 0 && swipe.Feed(-100, At(10)) == 0 && swipe.Feed(-20, At(20)) == -1);

        swipe = new HorizontalSwipe();
        swipe.Vertical(At(0));
        check("A gesture that also scrolls up or down never acts", swipe.Feed(200, At(50)) == 0 && swipe.Feed(200, At(100)) == 0);
        check("A swipe well after the scroll acts", swipe.Feed(120, At(100 + 300)) == 1);

        swipe = new HorizontalSwipe();
        check("A held tilt wheel acts once", swipe.Feed(120, At(0)) == 1 && swipe.Feed(120, At(100)) == 0 && swipe.Feed(120, At(200)) == 0);

        // The pace recorded on a precision touchpad: a part every 7 ms while the fingers move, every 15 to 25 ms as the swipe coasts.
        swipe = new HorizontalSwipe();
        int[] fingers = [0, 7, 14, 21, 28, 35], coast = [50, 70, 85, 105, 120, 140];
        check("The coasting rest of a touchpad swipe does not act again", fingers.Select(t => swipe.Feed(-150, At(t))).Count(s => s != 0) == 1
            && coast.All(t => swipe.Feed(-60, At(t)) == 0));
        check("A swipe the same way made while the first still coasts acts once more",
            new[] { 147, 154, 161, 168, 175 }.Select(t => swipe.Feed(-300, At(t))).Where(s => s != 0).SequenceEqual([-1]));
        check("The fingers' irregular pace as a swipe begins is not taken for a new swipe", swipe.Feed(-150, At(187)) == 0
            && new[] { 194, 201, 208 }.All(t => swipe.Feed(-150, At(t)) == 0));

        swipe = new HorizontalSwipe();
        check("A single part the other way in the middle of a swipe does not act",
            swipe.Feed(-300, At(0)) == -1 && swipe.Feed(-150, At(7)) == 0 && swipe.Feed(320, At(20)) == 0 && swipe.Feed(-100, At(27)) == 0 && swipe.Feed(-300, At(50)) == 0);

        // A straight drag from the origin, in steps of about 4 DIPs as the trail records it, with a little wobble across.
        static List<(double X, double Y)> Line(double dx, double dy, double wobble = 0)
        {
            int steps = (int)Math.Ceiling(Math.Max(Math.Abs(dx), Math.Abs(dy)) / 4);
            return [.. Enumerable.Range(0, steps + 1).Select(i => (dx * i / steps + (dy != 0 && dx == 0 ? wobble * (i % 2) : 0), dy * i / steps + (dx != 0 ? wobble * (i % 2) : 0)))];
        }
        check("A short drag is no gesture", MusicGestures.Direction(Line(30, 0)) == GestureDirection.None);
        check("A straight drag takes its direction", MusicGestures.Direction(Line(-60, 0)) == GestureDirection.Left
            && MusicGestures.Direction(Line(80, 0, 1.5)) == GestureDirection.Right && MusicGestures.Direction(Line(0, -50)) == GestureDirection.Up && MusicGestures.Direction(Line(0, 70, 1.5)) == GestureDirection.Down);
        check("A slightly slanted drag still counts", MusicGestures.Direction(Line(90, 25)) == GestureDirection.Right);
        check("A drag at about 45 degrees is no gesture", MusicGestures.Direction(Line(60, 50)) == GestureDirection.None);
        check("A drag that turns back is no gesture", MusicGestures.Direction([.. Line(-80, 0), .. Line(80, 0).Select(p => (p.X - 80, p.Y))]) == GestureDirection.None
            && MusicGestures.Direction([.. Line(120, 0), .. Line(-70, 0).Select(p => (p.X + 120, p.Y))]) == GestureDirection.None);
        // A loop, as a scribble draws: the end lies well to the right of the start, but the path circles on the way.
        var scribble = Enumerable.Range(0, 200).Select(i => i / 199.0 * 4 * Math.PI).Select(t => (t * 12 + 35 * Math.Sin(t), 30 * Math.Cos(t) - 30)).ToList();
        check("A scribble that ends to one side is no gesture", MusicGestures.Direction(scribble) == GestureDirection.None);
        check("A drag that bulges far across its axis is no gesture",
            MusicGestures.Direction(Enumerable.Range(0, 41).Select(i => (i * 3.0, 40 * Math.Sin(i / 40.0 * Math.PI))).ToList()) == GestureDirection.None);
        var settings = new DeckSettings { MusicDragUp = MusicGestureAction.PlayPause };
        check("Each drag direction runs its own action", MusicGestures.Drag(settings, GestureDirection.Left) == MusicGestureAction.PreviousTrack
            && MusicGestures.Drag(settings, GestureDirection.Right) == MusicGestureAction.NextTrack
            && MusicGestures.Drag(settings, GestureDirection.Up) == MusicGestureAction.PlayPause && MusicGestures.Drag(settings, GestureDirection.Down) == MusicGestureAction.None);
        check("Drags are off only when every direction does nothing", MusicGestures.AnyDrag(settings)
            && !MusicGestures.AnyDrag(new DeckSettings { MusicDragLeft = MusicGestureAction.None, MusicDragRight = MusicGestureAction.None,
                MusicDragUp = MusicGestureAction.None, MusicDragDown = MusicGestureAction.None }));
        check("Fingers move against the scroll in the standard direction and with it when reversed",
            MusicGestures.Fingers(1, reversed: false) == GestureDirection.Left && MusicGestures.Fingers(1, reversed: true) == GestureDirection.Right
            && MusicGestures.Fingers(-1, reversed: false) == GestureDirection.Right && MusicGestures.Fingers(-1, reversed: true) == GestureDirection.Left);
        check("Dragging up set to volume up raises the volume as the pointer rises, and lowers it past the start",
            MusicGestures.VolumeSign(GestureDirection.Up, MusicGestureAction.VolumeUp) == -1
            && Math.Abs(MusicGestures.VolumeLevel(.5, -1, -40) - .7) < 1e-9 && Math.Abs(MusicGestures.VolumeLevel(.5, -1, 20) - .4) < 1e-9);
        check("Dragging right set to volume down lowers it", MusicGestures.VolumeSign(GestureDirection.Right, MusicGestureAction.VolumeDown) == -1
            && MusicGestures.VolumeSign(GestureDirection.Left, MusicGestureAction.NextTrack) == 0);
        check("The volume stays within its range", MusicGestures.VolumeLevel(.9, 1, 100) == 1 && MusicGestures.VolumeLevel(.1, -1, 100) == 0);
        check("An unknown saved action does nothing", new DeckSettings { MusicSwipeLeft = (MusicGestureAction)99 }.MusicSwipeLeft == MusicGestureAction.None);
        check("The player's volume drags as the system volume does", MusicGestures.VolumeSign(GestureDirection.Up, MusicGestureAction.PlayerVolumeUp) == -1
            && MusicGestures.VolumeSign(GestureDirection.Right, MusicGestureAction.PlayerVolumeDown) == -1);
        check("The player's volume is not the system volume", MusicGestures.IsPlayerVolume(MusicGestureAction.PlayerVolumeUp)
            && !MusicGestures.IsSystemVolume(MusicGestureAction.PlayerVolumeDown) && !MusicGestures.IsPlayerVolume(MusicGestureAction.VolumeUp)
            && MusicGestures.IsVolume(MusicGestureAction.PlayerVolumeDown) && MusicGestures.IsVolume(MusicGestureAction.VolumeDown) && !MusicGestures.IsVolume(MusicGestureAction.PlayPause));
        check("A swipe steps the player's volume by a tenth and keeps it within its range",
            MusicGestures.PlayerVolumeStepped(.5, MusicGestureAction.PlayerVolumeUp) == .6 && MusicGestures.PlayerVolumeStepped(.5, MusicGestureAction.PlayerVolumeDown) == .4
            && MusicGestures.PlayerVolumeStepped(.95, MusicGestureAction.PlayerVolumeUp) == 1 && MusicGestures.PlayerVolumeStepped(.04, MusicGestureAction.PlayerVolumeDown) == 0);
        check("The player's volume is saved", new DeckSettings { MusicDragUp = MusicGestureAction.PlayerVolumeUp }.MusicDragUp == MusicGestureAction.PlayerVolumeUp);
    }
}
