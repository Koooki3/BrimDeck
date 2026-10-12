using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrimDeck.Core;

// update.json is published with every release and names the installer that belongs to it.
public sealed record UpdateManifest(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("notes")] ReleaseNotes Notes,
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256)
{
    public const string FileName = "update.json";
    private const long MaxSize = 1L << 30;

    public static UpdateManifest Parse(string json)
    {
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(json) ?? throw new InvalidDataException("Empty update manifest.");
        return manifest.IsValid ? manifest with { Notes = manifest.Notes ?? ReleaseNotes.Of("") } : throw new InvalidDataException("Invalid update manifest.");
    }

    public string ToJson() => JsonSerializer.Serialize(this);

    // Only a plain installer name is accepted, so a manifest cannot direct the download outside the updates folder.
    private bool IsValid => IsVersion(Version) && Size is > 0 and <= MaxSize &&
        !string.IsNullOrEmpty(File) && File.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
        File.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') &&
        Sha256 is { Length: 64 } && Sha256.All(char.IsAsciiHexDigit);

    // Major.minor.patch with an optional prerelease label, as in the release tags ("0.3.0", "0.3.0-beta.1").
    // The version also names the tag the installer is downloaded from, so nothing else is accepted.
    private static bool IsVersion(string? value) => value is not null && Regex.IsMatch(value, @"^\d+\.\d+\.\d+(-[0-9A-Za-z]+(\.[0-9A-Za-z]+)*)?\z");
}

// Downloads the installer of a newer release, verifies its size and SHA-256, and runs it silently after the app exits.
// The installer waits for the running instance, installs into the existing folder and starts BrimDeck again.
public sealed class InstallerUpdateBackend : IAppUpdateBackend
{
    public const string InstallerArguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH";
    private const string PendingFile = "pending.json";
    private readonly HttpClient _http;
    private readonly Uri _manifest;
    private readonly Func<UpdateManifest, Uri> _installer;
    private readonly string _directory;
    private readonly Func<ProcessStartInfo, bool> _launch;
    private UpdateManifest? _available;

    public InstallerUpdateBackend(HttpClient http, Uri manifest, Func<UpdateManifest, Uri> installer, string currentVersion,
        string directory, bool installed, Func<ProcessStartInfo, bool>? launch = null)
    {
        _http = http; _manifest = manifest; _installer = installer; _directory = directory; CanInstall = installed;
        CurrentVersion = currentVersion;
        _launch = launch ?? (start => { using var process = Process.Start(start); return process is not null; });
    }

    public string CurrentVersion { get; }
    public bool CanInstall { get; }

    // Read once at startup. A pending installer that is not newer than the running version was applied or is stale.
    public AppRelease? PendingRelease
    {
        get
        {
            if (Pending() is { } manifest) return Describe(manifest);
            DeleteDownloads(); return null;
        }
    }

    public async Task<AppRelease?> CheckAsync(CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await _http.GetAsync(_manifest, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new UpdateCheckException(response.StatusCode, RetryAfter(response.Headers.RetryAfter));
        var manifest = UpdateManifest.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
        _available = IsNewer(manifest) ? manifest : null;
        return _available is null ? null : Describe(_available);
    }

    // Retry-After is either a number of seconds or a date.
    private static TimeSpan? RetryAfter(System.Net.Http.Headers.RetryConditionHeaderValue? value) =>
        value?.Delta ?? (value?.Date is { } date ? date - DateTimeOffset.UtcNow : null);

    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);
    public async Task DownloadAsync(AppRelease release, Action<int> progress, CancellationToken cancellation)
    {
        var manifest = _available is { } available && available.Version == release.Version ? available
            : throw new InvalidOperationException("Update selection changed.");
        Directory.CreateDirectory(_directory);
        var target = Path.Combine(_directory, manifest.File); var partial = target + ".partial";
        try
        {
            using (var response = await _http.GetAsync(_installer(manifest), HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is { } length && length != manifest.Size) throw new InvalidDataException("Installer size mismatch.");
                await using var source = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
                await using var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920]; long total = 0; int reported = -1, read;
                // The client timeout ends with the response headers; a body that stops arriving fails after 30 seconds without data.
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                stall.CancelAfter(StallTimeout);
                while ((read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
                {
                    stall.CancelAfter(StallTimeout);
                    total += read;
                    if (total > manifest.Size) throw new InvalidDataException("Installer size mismatch.");
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
                    var percent = (int)(total * 100 / manifest.Size);
                    if (percent != reported) { reported = percent; progress(percent); }
                }
                if (total != manifest.Size || !Matches(hash.GetHashAndReset(), manifest.Sha256)) throw new InvalidDataException("Installer checksum mismatch.");
            }
            File.Move(partial, target, true);
            var pending = Path.Combine(_directory, PendingFile);
            File.WriteAllText(pending + ".tmp", manifest.ToJson());
            File.Move(pending + ".tmp", pending, true);
        }
        catch { TryDelete(partial); throw; }
    }

    // The file is hashed again right before it runs, so a download changed after verification is never executed.
    public void PrepareRestart()
    {
        var manifest = Pending() ?? throw new InvalidOperationException("No verified update is ready.");
        var path = Path.Combine(_directory, manifest.File);
        bool valid;
        using (var stream = File.OpenRead(path)) valid = stream.Length == manifest.Size && Matches(SHA256.HashData(stream), manifest.Sha256);
        if (!valid) { DeleteDownloads(); throw new InvalidDataException("Installer checksum mismatch."); }
        var start = new ProcessStartInfo(path, InstallerArguments) { UseShellExecute = true, WorkingDirectory = _directory };
        if (!_launch(start)) throw new InvalidOperationException("Installer did not start.");
    }

    private UpdateManifest? Pending()
    {
        try
        {
            var path = Path.Combine(_directory, PendingFile);
            if (!File.Exists(path)) return null;
            var manifest = UpdateManifest.Parse(File.ReadAllText(path));
            var installer = new FileInfo(Path.Combine(_directory, manifest.File));
            return IsNewer(manifest) && installer.Exists && installer.Length == manifest.Size ? manifest : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException) { return null; }
    }

    private bool IsNewer(UpdateManifest manifest) => ApplicationVersion.Compare(manifest.Version, CurrentVersion) > 0;
    private static AppRelease Describe(UpdateManifest manifest) => new(manifest.Version, manifest.Notes, manifest.Size);
    private static bool Matches(byte[] hash, string expected) => Convert.ToHexString(hash).Equals(expected, StringComparison.OrdinalIgnoreCase);

    private void DeleteDownloads()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
