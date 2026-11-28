using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using BrimDeck.Core;

internal static class InstallerUpdateTests
{
    private static readonly Uri ManifestUri = new("https://updates.test/latest/update.json");
    private static readonly Uri BetaManifestUri = new("https://updates.test/beta-channel/update.json");

    public static async Task Run(string root, Action<string, bool> check)
    {
        var installer = RandomNumberGenerator.GetBytes(300_000);
        var manifest = new UpdateManifest("0.2.0", "Release notes", "BrimDeck-0.2.0-Setup.exe", installer.Length,
            Convert.ToHexString(SHA256.HashData(installer)).ToLowerInvariant());
        var server = new ReleaseServer { Manifest = manifest.ToJson(), Installer = installer };
        using var http = new HttpClient(server);
        var directory = Path.Combine(root, "installer-updates");
        var launches = new List<ProcessStartInfo>();
        InstallerUpdateBackend Backend(string version = "0.1.0", UpdateMode mode = UpdateMode.Installer) => new(http, ManifestUri,
            (m, p) => new Uri($"https://updates.test/v{m.Version}/{p.File}"), version, directory, mode, start => { launches.Add(start); return true; }) { ResumeWait = TimeSpan.Zero };

        var backend = Backend();
        var release = await backend.CheckAsync(CancellationToken.None);
        check("installer manifest reports a newer release with notes and size", release == new AppRelease("0.2.0", "Release notes", installer.Length));
        var localized = UpdateManifest.Parse((manifest with { Notes = new([("zh-CN", "中文说明"), ("en-US", "English notes")]) }).ToJson()).Notes;
        check("localized notes survive the manifest round trip", localized == new ReleaseNotes([("zh-CN", "中文说明"), ("en-US", "English notes")]));
        check("notes follow the interface language", localized.For("zh-CN") == "中文说明" && localized.For("en-US") == "English notes");
        check("another region of the same language uses that language", localized.For("zh-TW") == "中文说明");
        check("an unknown language falls back to English", localized.For("ja-JP") == "English notes");
        check("notes without English fall back to the first language", new ReleaseNotes([("zh-CN", "中文说明")]).For("en-US") == "中文说明");
        check("plain-text notes show in every language", UpdateManifest.Parse(manifest.ToJson()).Notes.For("ja-JP") == "Release notes");
        check("the same version is not offered again", await Backend("0.2.0").CheckAsync(CancellationToken.None) is null);
        check("a newer running version ignores an older manifest", await Backend("0.10.0").CheckAsync(CancellationToken.None) is null);
        check("a prerelease is offered its final release", await Backend("0.2.0-beta.3").CheckAsync(CancellationToken.None) is not null);
        check("prereleases order by label and before their release", ApplicationVersion.Compare("0.2.0-alpha.2", "0.2.0-beta.1") < 0
            && ApplicationVersion.Compare("0.2.0-beta.2", "0.2.0-beta.10") < 0 && ApplicationVersion.Compare("0.2.0-beta.10", "0.2.0") < 0
            && ApplicationVersion.Compare("0.2.0", "0.3.0-alpha.1") < 0 && ApplicationVersion.Compare("0.2.0-beta.1", "0.2.0-beta.1+abc") == 0);

        foreach (var (name, json) in new[]
        {
            ("path in installer name", (manifest with { File = @"..\BrimDeck-0.2.0-Setup.exe" }).ToJson()),
            ("non-executable installer", (manifest with { File = "BrimDeck-0.2.0-Setup.zip" }).ToJson()),
            ("short checksum", (manifest with { Sha256 = "abc" }).ToJson()),
            ("four-part version", (manifest with { Version = "0.2.0.1" }).ToJson()),
            ("path in version", (manifest with { Version = "0.2.0-beta/../x" }).ToJson()),
            ("missing size", (manifest with { Size = 0 }).ToJson()),
        })
        {
            server.Manifest = json; bool rejected = false;
            try { await Backend().CheckAsync(CancellationToken.None); } catch (InvalidDataException) { rejected = true; }
            check("manifest with " + name + " is rejected", rejected);
        }
        server.Manifest = manifest.ToJson();

        async Task<bool> DownloadFails(byte[] served)
        {
            server.Installer = served; var attempt = Backend(); var found = await attempt.CheckAsync(CancellationToken.None);
            try { await attempt.DownloadAsync(found!, _ => { }, CancellationToken.None); return false; }
            catch (InvalidDataException) { return true; }
        }
        var corrupt = (byte[])installer.Clone(); corrupt[1234] ^= 0xFF;
        check("an installer with a wrong checksum is rejected", await DownloadFails(corrupt));
        check("an installer larger than announced is rejected", await DownloadFails([.. installer, 1]));
        check("a rejected download leaves no pending installer", Backend().PendingRelease is null && !Directory.Exists(directory));

        server.Installer = installer; backend = Backend(); release = await backend.CheckAsync(CancellationToken.None);
        var reported = new List<int>();
        await backend.DownloadAsync(release!, reported.Add, CancellationToken.None);
        var saved = Path.Combine(directory, manifest.File);
        check("verified installer is stored with progress up to 100%", File.Exists(saved) && reported.LastOrDefault() == 100 &&
            reported.SequenceEqual(reported.Order()) && !File.Exists(saved + ".partial"));
        await Resumes(server, installer, () => Backend(), directory, manifest.File, check);
        backend = Backend(); release = await backend.CheckAsync(CancellationToken.None);
        await backend.DownloadAsync(release!, _ => { }, CancellationToken.None);
        var requests = server.Requests;
        check("a verified download is restored after restart without network", Backend().PendingRelease == release && server.Requests == requests);

        var restarted = Backend(); _ = restarted.PendingRelease; restarted.PrepareRestart();
        var start = launches.SingleOrDefault();
        check("install runs the verified installer silently, with its error prompts, and relaunches the app", start is not null && start.FileName == saved &&
            start.Arguments == "/SILENT /NORESTART /RELAUNCH" && start.UseShellExecute);
        // The installer could not install 0.2.0, put 0.1.0 back, started it and left the note beside the download.
        File.WriteAllText(restarted.RestartFailureFile, PortableUpdate.Failed);
        var kept = new AppUpdates(Backend(), Path.Combine(root, "installer-updates-kept"));
        check("a failed installation is reported once after the old version starts again, with the download still ready", kept.RestartFailed
            && kept.Error is not null && kept.Stage == AppUpdateStage.Ready && !File.Exists(restarted.RestartFailureFile)
            && Backend().LastRestartFailure is null);
        File.WriteAllText(restarted.RestartFailureFile, PortableUpdate.Failed);
        check("an installed copy names the installer as the reason", Backend().LastRestartFailure == RestartFailure.Installer);

        File.WriteAllBytes(saved, corrupt); launches.Clear(); bool refused = false;
        try { Backend().PrepareRestart(); } catch (InvalidDataException) { refused = true; }
        check("an installer changed after download is never run", refused && launches.Count == 0 && !Directory.Exists(directory));

        backend = Backend(); await backend.DownloadAsync((await backend.CheckAsync(CancellationToken.None))!, _ => { }, CancellationToken.None);
        check("the downloaded installer is removed once that version is running", Backend("0.2.0").PendingRelease is null && !Directory.Exists(directory));

        var service = new AppUpdates(Backend(mode: UpdateMode.None), Path.Combine(root, "installer-updates-data"));
        await service.CheckAsync(true); await service.DownloadAsync();
        check("a copy that was not installed by the installer only reports the release", service.HasUpdate && service.Stage == AppUpdateStage.Available && !Directory.Exists(directory));
        service = new AppUpdates(Backend(), Path.Combine(root, "installer-updates-data"));
        await service.CheckAsync(true); await service.DownloadAsync(); launches.Clear();
        check("the update service prepares and starts the installer", service.Stage == AppUpdateStage.Ready && service.PrepareRestart(() => { }) && launches.Count == 1);

        var betaBackend = new InstallerUpdateBackend(http, ManifestUri, (m, p) => new Uri($"https://updates.test/v{m.Version}/{p.File}"), "0.1.0",
            Path.Combine(root, "installer-updates-beta"), UpdateMode.Installer, betaManifest: BetaManifestUri);
        server.BetaManifest = (manifest with { Version = "0.3.0-beta.1" }).ToJson();
        check("without betas the beta channel is not read", (await betaBackend.CheckAsync(false, CancellationToken.None))?.Version == "0.2.0" && server.BetaRequests == 0);
        check("a beta newer than the release is offered", (await betaBackend.CheckAsync(true, CancellationToken.None))?.Version == "0.3.0-beta.1");
        server.BetaManifest = (manifest with { Version = "0.2.0-beta.2" }).ToJson();
        check("a release newer than the beta is offered", (await betaBackend.CheckAsync(true, CancellationToken.None))?.Version == "0.2.0");
        server.BetaManifest = null;
        check("a missing beta channel leaves the release", (await betaBackend.CheckAsync(true, CancellationToken.None))?.Version == "0.2.0" && server.BetaRequests == 3);
        server.BetaManifest = "broken";
        check("an unreadable beta manifest leaves the release", (await betaBackend.CheckAsync(true, CancellationToken.None))?.Version == "0.2.0");
    }

    // The connection breaks off midway (issue #416: update downloads often failed): the download goes on from where it stopped.
    private static async Task Resumes(ReleaseServer server, byte[] installer, Func<InstallerUpdateBackend> backend, string directory, string file,
        Action<string, bool> check)
    {
        async Task<(Exception? Error, bool Saved)> Download()
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            var attempt = backend(); var found = await attempt.CheckAsync(CancellationToken.None);
            server.Ranges.Clear(); server.Served = 0;
            try { await attempt.DownloadAsync(found!, _ => { }, CancellationToken.None); }
            catch (Exception ex) { return (ex, false); }
            var path = Path.Combine(directory, file);
            return (null, File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(installer));
        }
        bool NoLeftovers() => !File.Exists(Path.Combine(directory, file + ".partial")) && !File.Exists(Path.Combine(directory, file));

        server.CutAfter.Enqueue(100_000); server.CutAfter.Enqueue(100_000);
        var (error, saved) = await Download();
        check("a download cut off twice goes on from the bytes received and is verified", error is null && saved);
        check("the resumed requests ask only for the rest, so nothing is downloaded twice",
            server.Ranges.SequenceEqual(new long?[] { null, 100_000, 200_000 }) && server.Served == installer.Length);

        server.CutAfter.Enqueue(120_000); server.IgnoreRange = true;
        (error, saved) = await Download();
        server.IgnoreRange = false;
        check("a server that ignores the range sends the whole file again, and the result is still verified", error is null && saved && server.Ranges.Count == 2);

        server.Statuses.Enqueue(HttpStatusCode.ServiceUnavailable); server.Statuses.Enqueue(HttpStatusCode.BadGateway);
        (error, saved) = await Download();
        check("a busy or failing server (5xx) is asked again", error is null && saved && server.Ranges.Count == 3);

        server.Statuses.Enqueue(HttpStatusCode.NotFound);
        (error, _) = await Download();
        check("a missing package (404) is not asked for again", error is HttpRequestException && server.Ranges.Count == 1 && NoLeftovers());

        for (int i = 0; i <= InstallerUpdateBackend.MaxResumes; i++) server.CutAfter.Enqueue(10_000);
        (error, _) = await Download();
        check("a download that keeps breaking off gives up after the last resume and leaves nothing",
            error is IOException && server.Ranges.Count == InstallerUpdateBackend.MaxResumes + 1 && NoLeftovers());
        server.CutAfter.Clear();

        var corrupt = (byte[])installer.Clone(); corrupt[250_000] ^= 0xFF;
        server.Installer = corrupt; server.CutAfter.Enqueue(100_000);
        (error, _) = await Download();
        server.Installer = installer;
        check("a resumed download with a wrong byte after the break is still rejected", error is InvalidDataException && NoLeftovers());
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    // Serves a file from a given offset and breaks off (an IOException, as a dropped connection does) after limit bytes.
    private sealed class BreakingStream(byte[] data, long from, int? limit) : Stream
    {
        private long _at = from;
        private int _sent;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (limit is { } most && _sent >= most) throw new IOException("The connection was reset.");
            int n = (int)Math.Min(count, data.Length - _at);
            if (limit is { } cap) n = Math.Min(n, cap - _sent);
            Array.Copy(data, _at, buffer, offset, n); _at += n; _sent += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _at; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ReleaseServer : HttpMessageHandler
    {
        // For the package: bytes to send before breaking off, one per response; statuses to answer instead, one per request;
        // whether a Range is ignored. Recorded: the start of each Range asked for (null for none), and the bytes sent.
        public Queue<int> CutAfter { get; } = new();
        public Queue<HttpStatusCode> Statuses { get; } = new();
        public bool IgnoreRange { get; set; }
        public List<long?> Ranges { get; } = [];
        public long Served { get; set; }
        public string Manifest { get; set; } = "";
        public byte[] Installer { get; set; } = [];
        // Null answers the beta manifest with 404, as before the first beta.
        public string? BetaManifest { get; set; }
        public int Requests { get; private set; }
        public int BetaRequests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (request.RequestUri == BetaManifestUri) BetaRequests++;
            if (request.RequestUri?.AbsolutePath == "/v0.2.0/BrimDeck-0.2.0-Setup.exe") return Task.FromResult(Package(request));
            HttpContent? content = request.RequestUri == ManifestUri ? new StringContent(Manifest)
                : request.RequestUri == BetaManifestUri && BetaManifest is not null ? new StringContent(BetaManifest)
                : request.RequestUri?.AbsolutePath == "/v0.2.0/BrimDeck-0.2.0-Setup.exe" ? new ByteArrayContent(Installer) : null;
            return Task.FromResult(new HttpResponseMessage(content is null ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = content ?? new StringContent("") });
        }
        private HttpResponseMessage Package(HttpRequestMessage request)
        {
            long? from = request.Headers.Range?.Ranges.Single().From;
            Ranges.Add(from);
            if (Statuses.TryDequeue(out var status)) return new HttpResponseMessage(status) { Content = new StringContent("") };
            long start = IgnoreRange ? 0 : from ?? 0;
            int? limit = CutAfter.TryDequeue(out var cut) ? cut : null;
            Served += Math.Min(Installer.Length - start, limit ?? int.MaxValue);
            var content = new StreamContent(new BreakingStream(Installer, start, limit));
            content.Headers.ContentLength = Installer.Length - start;
            if (start > 0) content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(start, Installer.Length - 1, Installer.Length);
            return new HttpResponseMessage(start > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = content };
        }
    }
}
