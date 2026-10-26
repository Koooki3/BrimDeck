using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using BrimDeck.Core;

internal static class PortableUpdateTests
{
    private static readonly Uri ManifestUri = new("https://updates.test/latest/update.json");

    public static async Task Run(string root, Action<string, bool> check)
    {
        Manifest(check);
        await Backend(Path.Combine(root, "portable-backend"), check);
        Replace(Path.Combine(root, "portable-replace"), check);
    }

    private static UpdateManifest Installer(byte[] setup) => new("0.2.0", "Release notes", "BrimDeck-0.2.0-Setup.exe", setup.Length, Hash(setup));
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static void Manifest(Action<string, bool> check)
    {
        var manifest = Installer([1, 2, 3]);
        check("a manifest without a portable package writes no portable field", !manifest.ToJson().Contains("portable"));
        var portable = manifest with { Portable = new("BrimDeck-0.2.0-Portable-x64.zip", 42, new string('a', 64)) };
        check("the portable package survives the manifest round trip", UpdateManifest.Parse(portable.ToJson()) == portable);
        check("an installed copy downloads the installer and a portable copy the zip", portable.Package(UpdateMode.Installer)!.File == manifest.File
            && portable.Package(UpdateMode.Portable)!.File == "BrimDeck-0.2.0-Portable-x64.zip" && manifest.Package(UpdateMode.Portable) is null);
        // Versions before the portable package read update.json with a type that has no such field.
        var older = System.Text.Json.JsonSerializer.Deserialize<OlderManifest>(portable.ToJson());
        check("a manifest with a portable package still describes the installer to older versions", older is { File: "BrimDeck-0.2.0-Setup.exe", Size: 3 });
        foreach (var (name, package) in new[]
        {
            ("an installer as portable package", new UpdatePackage("BrimDeck-0.2.0-Setup.exe", 42, new string('a', 64))),
            ("a path in the portable name", new UpdatePackage(@"..\BrimDeck.zip", 42, new string('a', 64))),
            ("a portable package without a size", new UpdatePackage("BrimDeck.zip", 0, new string('a', 64))),
            ("a short portable checksum", new UpdatePackage("BrimDeck.zip", 42, "abc")),
        })
        {
            bool rejected = false;
            try { UpdateManifest.Parse((manifest with { Portable = package }).ToJson()); } catch (InvalidDataException) { rejected = true; }
            check("a manifest with " + name + " is rejected", rejected);
        }
    }

    private sealed record OlderManifest(
        [property: System.Text.Json.Serialization.JsonPropertyName("file")] string File,
        [property: System.Text.Json.Serialization.JsonPropertyName("size")] long Size);

    private static async Task Backend(string root, Action<string, bool> check)
    {
        var package = Path.Combine(root, "package", "BrimDeck");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "BrimDeck.exe"), "new");
        File.WriteAllLines(Path.Combine(package, PortableUpdate.ListFile), ["BrimDeck.exe", PortableUpdate.ListFile]);
        var zipPath = Path.Combine(root, "BrimDeck-0.2.0-Portable-x64.zip");
        ZipFile.CreateFromDirectory(Path.Combine(root, "package"), zipPath);
        var zip = File.ReadAllBytes(zipPath);
        var setup = RandomNumberGenerator.GetBytes(1000);
        var manifest = Installer(setup) with { Portable = new("BrimDeck-0.2.0-Portable-x64.zip", zip.Length, Hash(zip)) };
        var server = new ReleaseServer { Manifest = manifest.ToJson(), Files = { ["/v0.2.0/BrimDeck-0.2.0-Portable-x64.zip"] = zip, ["/v0.2.0/BrimDeck-0.2.0-Setup.exe"] = setup } };
        using var http = new HttpClient(server);
        var directory = Path.Combine(root, "data", "updates");
        var app = Path.Combine(root, "app 目录", "BrimDeck.exe");
        var launches = new List<ProcessStartInfo>();
        InstallerUpdateBackend Backend() => new(http, ManifestUri, (m, p) => new Uri($"https://updates.test/v{m.Version}/{p.File}"), "0.1.0", directory,
            UpdateMode.Portable, start => { launches.Add(start); return true; }, app);

        var backend = Backend();
        var release = await backend.CheckAsync(CancellationToken.None);
        check("a portable copy reports the size of the zip", release?.Size == zip.Length && backend.CanInstall);
        await backend.DownloadAsync(release!, _ => { }, CancellationToken.None);
        check("a portable copy downloads and keeps the verified zip", File.Exists(Path.Combine(directory, "BrimDeck-0.2.0-Portable-x64.zip"))
            && !File.Exists(Path.Combine(directory, "BrimDeck-0.2.0-Setup.exe")) && Backend().PendingRelease == release);
        Backend().PrepareRestart();
        var start = launches.SingleOrDefault();
        var helper = Path.Combine(directory, "portable", "BrimDeck", "BrimDeck.exe");
        check("install extracts the zip, finds the folder it wraps and starts its executable as the helper without the shell", start is not null && start.FileName == helper
            && File.ReadAllText(helper) == "new" && !start.UseShellExecute && start.ArgumentList.SequenceEqual(
                PortableUpdate.HelperArguments(Path.GetDirectoryName(app)!, Environment.ProcessId, backend.RestartFailureFile)));
        check("the helper is told the app folder without a trailing separator", PortableUpdate.HelperArguments(@"C:\BrimDeck\", 1, "f")[1] == @"C:\BrimDeck");

        var corrupt = (byte[])zip.Clone(); corrupt[^30] ^= 0xFF;
        File.WriteAllBytes(Path.Combine(directory, "BrimDeck-0.2.0-Portable-x64.zip"), corrupt); launches.Clear(); bool refused = false;
        try { Backend().PrepareRestart(); } catch (InvalidDataException) { refused = true; }
        check("a zip changed after download is never extracted", refused && launches.Count == 0 && !Directory.Exists(directory));

        server.Manifest = Installer(setup).ToJson(); backend = Backend(); bool missing = false;
        try { await backend.DownloadAsync((await backend.CheckAsync(CancellationToken.None))!, _ => { }, CancellationToken.None); }
        catch (InvalidDataException) { missing = true; }
        check("a portable copy downloads nothing from a release without a zip", missing && !File.Exists(Path.Combine(directory, "BrimDeck-0.2.0-Setup.exe")));

        Directory.CreateDirectory(directory);
        File.WriteAllText(Backend().RestartFailureFile, PortableUpdate.Declined + "\n");
        var service = new AppUpdates(Backend(), Path.Combine(root, "data"));
        check("a declined administrator prompt is reported once after the old version starts again", service.RestartFailed && service.Error is not null
            && !File.Exists(Backend().RestartFailureFile) && !new AppUpdates(Backend(), Path.Combine(root, "data")).RestartFailed);
        Directory.CreateDirectory(directory); File.WriteAllText(Backend().RestartFailureFile, PortableUpdate.Failed);
        check("a failed replacement is reported as well", Backend().LastRestartFailure == RestartFailure.Failed);
    }

    private static void Replace(string root, Action<string, bool> check)
    {
        string Write(string folder, string file, string text)
        {
            var path = Path.Combine(folder, file); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); return path;
        }
        void List(string folder, params string[] files) => File.WriteAllLines(Path.Combine(folder, PortableUpdate.ListFile), files);
        var target = Path.Combine(root, "免安装 目录");
        var source = Path.Combine(root, "new");
        Write(target, "BrimDeck.exe", "old"); Write(target, @"runtimes\old.dll", "old"); Write(target, "only-old.txt", "old");
        Write(target, @"shared\kept.dll", "old"); Write(target, "notes.txt", "mine");
        List(target, "BrimDeck.exe", @"runtimes\old.dll", "only-old.txt", @"shared\kept.dll", PortableUpdate.ListFile);
        Write(source, "BrimDeck.exe", "new"); Write(source, @"shared\kept.dll", "new"); Write(source, @"shared\added.dll", "new");
        List(source, "BrimDeck.exe", @"shared\kept.dll", @"shared\added.dll");

        check("only a folder with the file list is portable", PortableUpdate.IsPortable(target) && !PortableUpdate.IsPortable(root));
        check("a writable folder can be replaced without administrator rights", PortableUpdate.CanWrite(target) && Directory.GetFiles(target, "*.tmp").Length == 0);
        PortableUpdate.Apply(source, target);
        check("listed files are replaced and new files added", File.ReadAllText(Path.Combine(target, "BrimDeck.exe")) == "new"
            && File.ReadAllText(Path.Combine(target, @"shared\kept.dll")) == "new" && File.Exists(Path.Combine(target, @"shared\added.dll")));
        check("files only the old version listed are removed with their emptied folders", !File.Exists(Path.Combine(target, "only-old.txt"))
            && !Directory.Exists(Path.Combine(target, "runtimes")));
        check("files the package never listed stay", File.ReadAllText(Path.Combine(target, "notes.txt")) == "mine");
        check("the new file list replaces the old one and no backup is left", PortableUpdate.Files(target).Contains(@"shared\added.dll")
            && !PortableUpdate.Files(target).Contains("only-old.txt") && Directory.GetDirectories(target, ".brimdeck-update-*").Length == 0);

        // A folder in the way of a new file makes the copy fail after the old files were moved aside.
        var blocked = Path.Combine(root, "blocked");
        Write(blocked, "BrimDeck.exe", "old"); Write(blocked, @"runtimes\old.dll", "old");
        List(blocked, "BrimDeck.exe", @"runtimes\old.dll", PortableUpdate.ListFile);
        Directory.CreateDirectory(Path.Combine(blocked, @"shared\added.dll"));
        bool failed = false;
        try { PortableUpdate.Apply(source, blocked); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
        check("a failed replacement restores every old file", failed && File.ReadAllText(Path.Combine(blocked, "BrimDeck.exe")) == "old"
            && File.ReadAllText(Path.Combine(blocked, @"runtimes\old.dll")) == "old" && PortableUpdate.Files(blocked).Contains(@"runtimes\old.dll")
            && !File.Exists(Path.Combine(blocked, @"shared\kept.dll")) && Directory.GetDirectories(blocked, ".brimdeck-update-*").Length == 0);

        File.Delete(Path.Combine(source, @"shared\added.dll")); failed = false;
        try { PortableUpdate.Apply(source, blocked); } catch (InvalidDataException) { failed = true; }
        check("an incomplete package is refused before anything moves", failed && File.ReadAllText(Path.Combine(blocked, "BrimDeck.exe")) == "old");

        foreach (var path in new[] { @"..\outside.dll", @"C:\Windows\x.dll", @"a\..\..\b.dll", @"\rooted.dll" })
        {
            List(source, "BrimDeck.exe", path); failed = false;
            try { PortableUpdate.Files(source); } catch (InvalidDataException) { failed = true; }
            check("a file list entry " + path + " is refused", failed);
        }
    }

    private sealed class ReleaseServer : HttpMessageHandler
    {
        public string Manifest { get; set; } = "";
        public Dictionary<string, byte[]> Files { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpContent? content = request.RequestUri == ManifestUri ? new StringContent(Manifest, Encoding.UTF8)
                : Files.TryGetValue(request.RequestUri!.AbsolutePath, out var file) ? new ByteArrayContent(file) : null;
            return Task.FromResult(new HttpResponseMessage(content is null ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = content ?? new StringContent("") });
        }
    }
}
