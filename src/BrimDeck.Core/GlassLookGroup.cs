namespace BrimDeck.Core;

// The palette transaction of the panels that share one automatic liquid glass look (review v2 U08, §8). The shared look is the
// product contract: one decision (GlassAppearance) for the group, fed only by its leader's readings; each panel keeps its own
// material and protection.
//   DecisionEpoch: each turn the decision begins, and each reversal of one under way, starts a new epoch. A panel that takes part
//     in a turn holds the epoch it was given; only the leader, holding the current epoch, publishes the text's swap, and only
//     to the goal of that epoch: a follower never publishes, and nobody publishes an older goal (a reversal that came first).
//   Leader: kept while it is eligible (shown, automatic glass); otherwise the first eligible panel that animates, else the first
//     eligible one (a reduced-motion panel leads only when no other can, and then publishes at once). The decision and its
//     history stay with the group across a change of leader; only a group with no eligible panel left drops it.
public sealed class GlassLookGroup<TMember> where TMember : class
{
    public readonly record struct Member(TMember Panel, bool Eligible, bool Animates);

    public long Epoch { get; private set; }
    // The goal of the current epoch (true: dark), null before the first turn.
    public bool? Goal { get; private set; }
    // The goal last published and its epoch.
    public bool? Published { get; private set; }
    public long PublishedEpoch { get; private set; }
    public TMember? Leader { get; private set; }
    public GlassAppearance? Decision { get; set; }

    // Chooses the leader among the panels now (see above); the same leader while it stays eligible.
    public TMember? Elect(IEnumerable<Member> members)
    {
        Member? keep = null, animated = null, any = null;
        foreach (var member in members)
        {
            if (!member.Eligible) continue;
            if (ReferenceEquals(member.Panel, Leader)) keep = member;
            if (member.Animates) animated ??= member;
            any ??= member;
        }
        Leader = (keep ?? animated ?? any)?.Panel;
        if (Leader is null) Decision = null;
        return Leader;
    }

    // A turn begins, or one under way reverses: a new epoch with this goal.
    public long Begin(bool dark)
    {
        Epoch++;
        Goal = dark;
        return Epoch;
    }

    // Whether this panel may publish this goal for the epoch it holds now; when it may, the publication is recorded.
    public bool TryPublish(TMember panel, long epoch, bool dark)
    {
        if (!ReferenceEquals(panel, Leader) || epoch != Epoch || Goal != dark) return false;
        Published = dark; PublishedEpoch = epoch;
        return true;
    }

    // The palette was set from outside (another theme, the Windows mode): no turn of the group continues.
    public void Abandon()
    {
        Epoch++;
        Goal = null;
    }
}
