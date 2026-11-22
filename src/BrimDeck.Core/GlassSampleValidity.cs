namespace BrimDeck.Core;

// Whether liquid glass's backdrop readings (AcrylicProbe, on a worker) can still be trusted, and how much the glass has to guard
// the text meanwhile (review v2 U05, §5). The window asks Begin before a capture and Complete when the worker answers; an
// independent watchdog (a one-shot timer at NextDeadline, never waiting for the worker) asks Protection at any time.
//   Age: a reading is as old as its capture's START (a slow capture is not fresh because it finished late).
//   Pace (V3 C4): how long the last capture took to answer (at most HardSafety). On a busy machine a ring read takes 150-490 ms,
//     so the next reading can only answer that much after it begins: the TTLs below allow for it (on an idle machine about 30 ms).
//   TTL: a reading answered later than ActiveTtl (bursts) or IdleTtl (rest) plus the pace after its capture began, and later
//     than HardSafety, is late and dropped; so is one for an epoch or a place the panel has left (a drag), and a capture that read
//     nothing (no area, no pixels).
//   Protection (0..1): rises from 0 once the last accepted capture is older than the TTL of the mode now plus the pace - or
//     HardSafety after a capture that has not answered began; a capture that began before that holds it off until then, as it
//     is no gap but a slow reading - to 1 (the worst range: tails 1 and 0) no later than WorstBy after the last accepted
//     capture. The ring around the island cannot prove the pixels under it safe, so nothing less than the worst range is
//     assumed at 1.
//   Recovery: once protection has risen, it falls back to 0 only after two fresh readings in a row agree (within RecoverAgree on
//     both tails); a single one only starts the recovery.
//   A capture still running Abandon after it began is given up, so a worker that never returns cannot stop all later readings.
//   Pause (V3 C4): while the island moves the ring is not read (GlassMotionGate); the time paused, up to GlassMotionGate.MaxPause,
//     is no age: the TTLs and WorstBy count from the last reading as if the pause had not been. A capture under way when the pause
//     began keeps its HardSafety (a stuck capture is not hidden), and a protection already risen holds its level while paused.
// The TTLs are trial values (review v2 §5: 250 ms active / 750 ms idle), to be tuned by measurement.
// Times are milliseconds on any steady clock.
public sealed class GlassSampleValidity
{
    public const long ActiveTtl = 250, IdleTtl = 750, HardSafety = 500, WorstBy = 1500, Abandon = 1500;
    public const double RecoverAgree = .1;

    public readonly record struct Ticket(long Id, long Start, long Epoch, bool Active);
    public enum Verdict { Accepted, Recovering, Late, Mismatch, Empty, Stale }

    private long _nextId, _validStart, _activeSince, _pausedSince, _pace;
    private bool _paused;
    private Ticket? _inFlight;
    private bool _active, _protecting;
    private (double Bright, double Dark)? _recoverFrom;

    public GlassSampleValidity(long now) => Restart(now);

    // The tails of the last accepted reading (the worst range before the first).
    public (double Bright, double Dark) Tails { get; private set; } = (1, 0);
    // No reading accepted since the start: the glass shows the worst range anyway.
    public bool First { get; private set; } = true;
    public bool Reading => _inFlight is not null;
    // The protection has risen and not yet recovered.
    public bool Protecting => _protecting;
    // Counters for diagnosis: captures given up, readings dropped late or misplaced.
    public int Abandoned { get; private set; }
    public int Dropped { get; private set; }

    // Shown anew: the worst range until a first reading, with the clock running from now.
    public void Restart(long now)
    {
        _validStart = now; _inFlight = null; _protecting = false; _recoverFrom = null; _paused = false;
        Tails = (1, 0); First = true;
    }

    // The mode the TTL follows: bursts (active) or rest. A burst's shorter TTL counts from the burst's start: a reading taken at
    // rest is not expired by a hint that only asks for faster readings.
    public void Mode(bool active, long now)
    {
        if (active && !_active) _activeSince = now;
        _active = active;
    }

    // A capture may start now (null: one is still running and not yet given up).
    public Ticket? Begin(long now, long epoch)
    {
        if (_inFlight is { } running)
        {
            if (now - running.Start < Abandon) return null;
            Abandoned++;
        }
        var ticket = new Ticket(++_nextId, now, epoch, _active);
        _inFlight = ticket;
        return ticket;
    }

    // A capture that could not start after all (no slot in the app's budget): as if it had not begun.
    public void Cancel(Ticket ticket)
    {
        if (_inFlight?.Id == ticket.Id) _inFlight = null;
    }

    // A capture answered at now. Matches: the reading is for the place and the epoch the panel has now. Tails: null when it read
    // nothing. Only Accepted readings may move the material and the decision.
    public Verdict Complete(Ticket ticket, long now, long epoch, bool matches, (double Bright, double Dark)? tails)
    {
        if (_inFlight?.Id != ticket.Id) { Dropped++; return Verdict.Stale; }
        // Risen or not as of now with the capture still under way (it may hold the protection off, see ProtectFrom).
        Rise(now);
        _inFlight = null;
        long took = now - ticket.Start, allowed = Math.Max((ticket.Active ? ActiveTtl : IdleTtl) + _pace, HardSafety);
        _pace = Math.Clamp(took, 0, HardSafety);
        if (tails is not { } read || !double.IsFinite(read.Bright) || !double.IsFinite(read.Dark)) return Verdict.Empty;
        if (ticket.Epoch != epoch || !matches) { Dropped++; return Verdict.Mismatch; }
        if (took > allowed) { Dropped++; return Verdict.Late; }
        if (_protecting && !First)
        {
            bool agrees = _recoverFrom is { } before && Math.Abs(before.Bright - read.Bright) <= RecoverAgree
                && Math.Abs(before.Dark - read.Dark) <= RecoverAgree;
            _recoverFrom = read;
            if (!agrees) return Verdict.Recovering;
        }
        _protecting = false; _recoverFrom = null; First = false;
        _validStart = ticket.Start;
        Tails = read;
        return Verdict.Accepted;
    }

    public bool Paused => _paused;

    // The readings pause for the island's motion from now (see above).
    public void Pause(long now)
    {
        if (_paused) return;
        Rise(now);
        _paused = true;
        _pausedSince = now;
    }

    // The readings resume at now: the pause (up to its ceiling) is taken off the last reading's age for good.
    public void Resume(long now)
    {
        if (!_paused) return;
        long credit = Credit(now);
        _paused = false;
        _validStart += credit;
        _activeSince += credit;
    }

    private long Credit(long now) => _paused ? Math.Clamp(now - _pausedSince, 0, GlassMotionGate.MaxPause) : 0;

    // How much the glass guards the text at now (see above); latches the protection once it has risen.
    public double Protection(long now)
    {
        Rise(now);
        if (!_protecting) return 0;
        long from = ProtectFrom(now), by = _validStart + Credit(now) + WorstBy;
        return by <= from ? 1 : Math.Clamp((now - from) / (double)(by - from), 0, 1);
    }

    // The tails the glass answers for at now: the last accepted ones widened toward the worst range by the protection.
    public (double Bright, double Dark) SafeTails(long now)
    {
        double level = First ? 1 : Protection(now);
        return (Tails.Bright + (1 - Tails.Bright) * level, Tails.Dark * (1 - level));
    }

    // When the watchdog must look next (long.MaxValue: nothing pending): the protection's start, its steps to the worst range
    // (every Step), and the abandon time of a capture under way.
    public const long Step = 100;
    public long NextDeadline(long now)
    {
        long next = long.MaxValue;
        if (_inFlight is { } running && running.Start + Abandon > now) next = running.Start + Abandon;
        // While paused the deadlines only move later until the pause reaches its ceiling: look at the earliest they can be then
        // (a resume looks again).
        long at = _paused ? Math.Max(now, _pausedSince + GlassMotionGate.MaxPause) : now;
        long from = ProtectFrom(at), by = _validStart + Credit(at) + WorstBy;
        if (_protecting) from = Math.Min(from, now);
        if (!First && now < by) next = Math.Min(next, now < from ? from + 1 : Math.Min(by, now + Step));
        return next;
    }

    private long ProtectFrom(long now)
    {
        long credit = Credit(now);
        long from = _validStart + IdleTtl + _pace;
        if (_active) from = Math.Min(from, Math.Max(_validStart, _activeSince) + ActiveTtl + _pace);
        from += credit;
        // A capture under way decides from HardSafety after it began: one that has not answered by then may be stuck (it may be
        // before the TTL runs out), and one that began before the TTL ran out and answers by then is no gap. WorstBy still holds.
        if (_inFlight is { } running && running.Start <= from) from = Math.Min(running.Start + HardSafety, _validStart + credit + WorstBy);
        return Math.Max(from, _validStart);
    }

    private void Rise(long now)
    {
        if (!_protecting && !First && now > ProtectFrom(now)) { _protecting = true; _recoverFrom = null; }
    }
}
