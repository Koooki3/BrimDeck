using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using BrimDeck.Core;

namespace BrimDeck.Native;

internal enum AppleMusicAccess { None, Authorized, Expired }

// The media-user-token from signing in to music.apple.com, kept in apple-music.dat and encrypted for the current Windows
// user. The sign-in page itself is kept in AppleMusicWeb, so authorizing again may not ask for the password.
internal sealed class AppleMusicAccount(string directory) : IAppleMusicAccount
{
    private sealed record Saved(string Token, DateTimeOffset? Expires, bool Rejected);
    private readonly object _gate = new();
    private Saved? _saved = Load(Path.Combine(directory, "apple-music.dat"));
    private string FilePath => Path.Combine(directory, "apple-music.dat");
    public string WebProfile => Path.Combine(directory, "AppleMusicWeb");
    // Raised on the thread that changed the authorization.
    public event Action? Changed;

    public AppleMusicAccess Access
    {
        get
        {
            lock (_gate)
                return _saved is null ? AppleMusicAccess.None
                    : _saved.Rejected || _saved.Expires <= DateTimeOffset.Now ? AppleMusicAccess.Expired : AppleMusicAccess.Authorized;
        }
    }
    public DateTimeOffset? Expires { get { lock (_gate) return _saved?.Expires; } }
    public string Token { get { lock (_gate) return Access == AppleMusicAccess.Authorized ? _saved!.Token : ""; } }

    public void Authorize(string token, DateTimeOffset? expires) => Save(new(token, expires, false));
    public void Expire(string token)
    {
        Saved? rejected;
        lock (_gate) rejected = _saved is { Rejected: false } saved && saved.Token == token ? saved with { Rejected = true } : null;
        if (rejected is not null) Save(rejected);
    }
    public void Revoke()
    {
        Save(null);
        try { if (Directory.Exists(WebProfile)) Directory.Delete(WebProfile, true); }
        // The sign-in page's processes may still hold files for a moment after the window closes.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void Save(Saved? saved)
    {
        lock (_gate)
        {
            _saved = saved;
            if (saved is null) File.Delete(FilePath);
            else
            {
                Directory.CreateDirectory(directory);
                var temp = FilePath + ".tmp";
                File.WriteAllBytes(temp, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(saved), null, DataProtectionScope.CurrentUser));
                File.Move(temp, FilePath, true);
            }
        }
        Changed?.Invoke();
    }
    private static Saved? Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Saved>(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser)) : null; }
        // A file from another Windows user or a damaged one counts as no authorization.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException) { return null; }
    }
}
