namespace BrimDeck.Core;

// When the picture beneath an open pop-up is taken (BrimDeck.PopupMotion, motion-review F7). Until the pop-up has entered
// (faded in and dropped into place) nothing is taken: a picture taken then lay opaque under the real list while it still
// faded in and showed through it, a few DIPs off. The first picture is taken once it has entered; after that, each change
// that can alter the list is taken at once and once more when a hover's fade has settled.
public sealed class PopupPicture
{
    public bool Entered { get; private set; }

    // The pop-up has entered: its first picture is taken now.
    public void Enter() => Entered = true;

    // Something that can change the list happened: whether its picture is taken now (and again once settled). Before the
    // pop-up has entered, the first picture is still to come and takes it in.
    public bool Changed() => Entered;

    // How long after a change the picture is taken again: the hover fade's time (milliseconds) and a little more, so a hover's
    // fade has settled.
    public static int SettleMilliseconds(int hover) => hover + 40;
}
