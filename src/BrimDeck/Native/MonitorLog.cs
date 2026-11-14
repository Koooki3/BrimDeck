using System.Diagnostics;
using System.IO;
using BrimDeck.Core;

namespace BrimDeck.Native;

// Where the readings' swallowed failures go: crash.log in the data folder, next to the unhandled errors (App.RecordCrashes), and the
// debugger. Each kind of failure of each source is written once in ten minutes (FailureNotes), so a provider that fails every tick
// leaves one line.
internal static class MonitorLog
{
    private const long Limit = 1_048_576;
    private static readonly FailureNotes Notes = new(() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency, line => (Sink ?? Write)(line));
    // Where the lines go instead of the log file (the tests).
    public static Action<string>? Sink { get; set; }

    public static void Note(string source, Exception error)
    {
        Debug.WriteLine(error);
        Notes.Note(source, error);
    }

    public static void Note(string source, string kind, string text)
    {
        Debug.WriteLine(source + ": " + text);
        Notes.Note(source, kind, text);
    }

    private static void Write(string line)
    {
        try
        {
            var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrimDeck", "crash.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            if (File.Exists(log) && new FileInfo(log).Length > Limit) File.Delete(log);
            File.AppendAllText(log, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] monitor{Environment.NewLine}{line}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
