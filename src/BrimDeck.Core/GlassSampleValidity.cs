namespace BrimDeck.Core;

// Whether liquid glass's backdrop readings (AcrylicProbe, on a worker) can still be trusted, and how much the glass has to guard
// the text meanwhile (review v2 U05, §5). The window asks Begin before a capture and Complete when the worker answers; an
// independent watchdog (a one-shot timer at NextDeadline, never waiting for the worker) asks Protection at any time.
//   Age: a reading is as old as its capture's START (a slow capture is not fresh because it finished late).
//   TTL: a reading answered later than ActiveTtl (bursts) or IdleTtl (rest) after its capture began is late and dropped;
//     so is one for an epoch or a place the panel has left (a drag), and a capture that read nothing (no area, no pixels).
//   Protection (0..1): rises from 0 once the last accepted capture is older than the TTL of the mode now - or HardSafety after a
//     capture that has not answered began - to 1 (the worst range: tails 1 and 0) no later than WorstBy after the last accepted
//     capture. The ring around the island cannot prove the pixels under it safe, so nothing less than the worst range is assumed
//     at 1.
//   Recovery: once protection has risen, it falls back to 0 only after two fresh readings in a row agree (within RecoverAgree on
//     both tails); a single one only starts the recovery.
//   A capture still running Abandon after it began is given up, so a worker that never returns cannot stop all later readings.
// The TTLs are trial values (review v2 §5: 250 ms active / 750 ms idle), to be tuned by measurement.
// Times are milliseconds on any steady clock.
public sealed class GlassSampleValidity
{
    public const long ActiveTtl = 250, IdleTtl = 750, HardSafety = 500, WorstBy = 1500, Abandon = 1500;
    public const double RecoverAgree = .1;

    public readonly record struct Ticket(long Id, long Start, long Epoch, bool Active);
    public enum Verdict { Accepted, Recovering, Late, Mismatch, Empty, Stale }

    private long _nextId, _validStart, _activeSince;
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
        _validStart = now; _inFlight = null; _protecting = false; _recoverFrom = null;
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
        _inFlight = null;
        Rise(now);
        if (tails is not { } read || !double.IsFinite(read.Bright) || !double.IsFinite(read.Dark)) return Verdict.Empty;
        if (ticket.Epoch != epoch || !matches) { Dropped++; return Verdict.Mismatch; }
        if (now - ticket.Start > (ticket.Active ? ActiveTtl : IdleTtl)) { Dropped++; return Verdict.Late; }
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

    // How much the glass guards the text at now (see above); latches the protection once it has risen.
    public double Protection(long now)
    {
        Rise(now);
        if (!_protecting) return 0;
        long from = ProtectFrom(), by = _validStart + WorstBy;
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
        long from = ProtectFrom(), by = _validStart + WorstBy;
        if (!First && now < by) next = Math.Min(next, now < from ? from + 1 : Math.Min(by, now + Step));
        return next;
    }

    private long ProtectFrom()
    {
        long from = _validStart + IdleTtl;
        if (_active) from = Math.Min(from, Math.Max(_validStart, _activeSince) + ActiveTtl);
        if (_inFlight is { } running) from = Math.Min(from, running.Start + HardSafety);
        return Math.Max(from, _validStart);
    }

    private void Rise(long now)
    {
        if (!_protecting && !First && now > ProtectFrom()) { _protecting = true; _recoverFrom = null; }
    }
}
