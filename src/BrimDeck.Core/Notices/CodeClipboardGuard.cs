namespace BrimDeck.Core.Notices;

// The text clipboard as the guard needs it: what it holds, and emptying it. A clipboard another program holds open for a moment
// answers false.
public interface ICodeClipboard
{
    bool TryRead(out string? text);
    bool TryClear();
}

public enum CodeClearResult
{
    // Nothing was copied, or it has been dealt with.
    Idle,
    // Not time yet.
    Waiting,
    // The clipboard still held the code and was emptied.
    Cleared,
    // The clipboard holds something else now (the user copied again): left alone.
    Kept,
    // The clipboard could not be reached; ask again shortly.
    Retry
}

// Empties the clipboard some time after a verification code was copied to it, and only if it still holds exactly that code: a
// text the user copied meanwhile is never touched. The time is a constant, not a setting: long enough to paste the code into the
// page or app asking for it (codes themselves last a few minutes), short enough that a code does not wait on the clipboard for the
// rest of the day; a setting would be one more choice about a thing nobody wants to think about.
public sealed class CodeClipboardGuard(Func<DateTimeOffset> clock, ICodeClipboard clipboard)
{
    public static readonly TimeSpan ClearAfter = TimeSpan.FromSeconds(60);
    // Tries the clipboard this many times when it is busy, then gives it up.
    public const int MaxBusy = 5;

    private string? _code;
    private DateTimeOffset _due;
    private int _busy;

    public bool Armed => _code is not null;
    public DateTimeOffset? Due => _code is null ? null : _due;

    // The code was copied now. A later copy replaces an earlier one and restarts the time.
    public void Arm(string code)
    {
        _code = code.Length > 0 ? code : null; _due = clock() + ClearAfter; _busy = 0;
    }

    // The code is to stay (the guard is switched off, BrimDeck is closing without clearing).
    public void Disarm() { _code = null; _busy = 0; }

    // Called when the time may be up: decides, and clears the clipboard when it should be.
    public CodeClearResult Tick()
    {
        if (_code is null) return CodeClearResult.Idle;
        if (clock() < _due) return CodeClearResult.Waiting;
        if (!clipboard.TryRead(out var current)) return Busy();
        if (!string.Equals(current, _code, StringComparison.Ordinal)) { Disarm(); return CodeClearResult.Kept; }
        if (!clipboard.TryClear()) return Busy();
        Disarm();
        return CodeClearResult.Cleared;
    }

    private CodeClearResult Busy()
    {
        if (++_busy >= MaxBusy) { Disarm(); return CodeClearResult.Kept; }
        return CodeClearResult.Retry;
    }
}
