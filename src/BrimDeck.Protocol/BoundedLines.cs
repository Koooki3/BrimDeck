using System.Text;

namespace BrimDeck.Protocol;

// Reads a program's standard error to its end, passing on each non-blank line cut to limit characters, without ever holding
// more than that of a line: StreamReader.ReadLine and Process.BeginErrorReadLine hold a line until it ends, so a program
// writing endlessly without a line break would grow BrimDeck's memory without bound (S8). Read and I/O errors end the
// reading quietly: the stream belongs to a process that may be released at any time.
public static class BoundedLines
{
    public static async Task ReadAsync(Stream stream, int limit, Action<string> line, CancellationToken cancellation = default)
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[4096];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var current = new StringBuilder(limit);
        void End()
        {
            if (current.ToString().Trim() is { Length: > 0 } text) line(text);
            current.Clear();
        }
        try
        {
            int read;
            while ((read = await stream.ReadAsync(bytes, cancellation).ConfigureAwait(false)) > 0)
            {
                int count = decoder.GetChars(bytes, 0, read, chars, 0);
                for (int i = 0; i < count; i++)
                {
                    if (chars[i] == '\n') End();
                    else if (chars[i] != '\r' && current.Length < limit) current.Append(chars[i]);
                }
            }
            End();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException) { }
    }
}
