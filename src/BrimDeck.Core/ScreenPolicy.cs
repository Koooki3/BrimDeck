namespace BrimDeck.Core;

public static class ScreenPolicy
{
    public static ScreenContext Classify(bool exclusive, bool coversMonitor, bool hasCaption, bool maximized)
    {
        if (exclusive) return ScreenContext.Exclusive;
        if (coversMonitor && !hasCaption) return ScreenContext.Borderless;
        if (maximized) return ScreenContext.Maximized;
        return coversMonitor ? ScreenContext.Borderless : ScreenContext.Desktop;
    }

    // Whether a saved rule is about the app the context was detected for. The name is the process name and the path the
    // executable; either one saved in a rule is enough. A rule that names a path also matches by name, so it keeps
    // working when the executable's path cannot be read, for example for a process running as administrator.
    public static bool Matches(FullscreenRule? exception, string? processName, string? imagePath)
    {
        if (exception is null) return false;
        if (NormalizePath(exception.Path).Length > 0 && NormalizePath(imagePath).Length > 0 &&
            string.Equals(NormalizePath(exception.Path), NormalizePath(imagePath), StringComparison.OrdinalIgnoreCase)) return true;
        return SameName(exception.Name, processName);
    }

    // The setting list's identity: the path when one is saved, the process name otherwise. Case and slash direction do not
    // make a second entry.
    public static string Key(FullscreenRule exception)
        => NormalizePath(exception.Path) is { Length: > 0 } path
            ? "path:" + path.ToLowerInvariant()
            : "name:" + NormalizeName(exception.Name).ToLowerInvariant();

    // Process names are compared without the .exe suffix, so an entry may be saved either way.
    public static string NormalizeName(string? name)
    {
        var text = (name ?? "").Trim();
        return text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? text[..^4].Trim() : text;
    }

    public static string NormalizePath(string? path) => (path ?? "").Trim().Replace('/', '\\');

    private static bool SameName(string? left, string? right)
    {
        var name = NormalizeName(left);
        return name.Length > 0 && string.Equals(name, NormalizeName(right), StringComparison.OrdinalIgnoreCase);
    }
}
