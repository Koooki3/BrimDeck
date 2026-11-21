using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BrimDeck.Core;

namespace BrimDeck.Native;

// The Doubao (www.doubao.com, covering both Doubao and DoubaoWork) and Dola (www.dola.com) browser sign-ins used to read
// subscription quotas (DoubaoQuotaUsage). The desktop apps keep exclusive locks on their Chromium Cookies databases while
// running, so BrimDeck opens the edition's sign-in page in an isolated browser profile with a local DevTools port, captures
// the session cookies once the server accepts them, closes that window, deletes the temporary profile, and stores the session
// encrypted with DPAPI for the current Windows user. The user's normal browser profile is never touched.
public sealed class DoubaoQuotaAccount(string folder)
{
    private readonly object _gate = new();
    public event Action<ProviderId>? Changed;

    private string FilePath(ProviderId id) => Path.Combine(folder, DoubaoQuotaUsage.For(id).SessionFile);

    public bool SignedIn(ProviderId id) => File.Exists(FilePath(id));

    public string? Read(ProviderId id)
    {
        var path = FilePath(id);
        lock (_gate)
        {
            byte[]? plain = null;
            try
            {
                if (!File.Exists(path)) return null;
                plain = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { return null; }
            finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
        }
    }

    public void Save(ProviderId id, string session)
    {
        var path = FilePath(id);
        lock (_gate)
        {
            var plain = Encoding.UTF8.GetBytes(session);
            try
            {
                Directory.CreateDirectory(folder);
                var temp = path + ".tmp";
                File.WriteAllBytes(temp, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
                File.Move(temp, path, true);
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        Changed?.Invoke(id);
    }

    public void SignOut(ProviderId id)
    {
        var path = FilePath(id);
        lock (_gate) { try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
        Changed?.Invoke(id);
    }

    // A failure the settings page shows as it is.
    public sealed class SignInException(string message) : Exception(message);

    // Opens the edition's sign-in page and waits up to ten minutes for a sign-in the server accepts; the session is saved.
    // Closing the browser window ends the wait.
    public async Task SignInAsync(ProviderId id, CancellationToken cancellation)
    {
        var edition = DoubaoQuotaUsage.For(id);
        var browser = MiMoAccount.FindBrowser() ?? throw new SignInException(Loc.T("未找到 Chrome 或 Edge，无法打开登录页", "Chrome or Edge was not found, so the sign-in page cannot be opened."));
        var profile = Path.Combine(folder, "doubao-sign-in", Convert.ToHexString(RandomNumberGenerator.GetBytes(8)));
        Directory.CreateDirectory(profile);
        var start = new ProcessStartInfo(browser) { UseShellExecute = false };
        foreach (var argument in new[] { "--user-data-dir=" + profile, "--remote-debugging-port=0", "--no-first-run", "--no-default-browser-check",
            "--disable-background-mode", "--new-window", edition.LoginUrl }) start.ArgumentList.Add(argument);
        Process? process = null;
        try
        {
            process = Process.Start(start) ?? throw new SignInException(Loc.T("无法打开登录页", "The sign-in page cannot be opened."));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromMinutes(10));
            var session = await WaitAsync(process, profile, edition, deadline.Token);
            Save(edition.Id, session.Serialize());
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new SignInException(Loc.T("登录等待超时，请重新登录", "Signing in took too long. Sign in again.")); }
        finally
        {
            try { if (process is { HasExited: false }) process.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            process?.Dispose();
            _ = Task.Run(() => Delete(profile));
        }
    }

    // The browser leaves its folder locked for a moment after it ends.
    private static async Task Delete(string profile)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try { if (Directory.Exists(profile)) Directory.Delete(profile, true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await Task.Delay(500); }
        }
    }

    private static async Task<DoubaoQuotaUsage.Session> WaitAsync(Process process, string profile, DoubaoQuotaUsage.Edition edition, CancellationToken deadline)
    {
        Uri? socket = null;
        var waitPort = Stopwatch.StartNew();
        while (socket is null)
        {
            if (process.HasExited) throw new SignInException(Loc.T("登录窗口已关闭", "The sign-in window was closed."));
            if (waitPort.Elapsed > TimeSpan.FromSeconds(30)) throw new SignInException(Loc.T("无法连接登录窗口，请关闭浏览器窗口后重试", "The sign-in window cannot be reached. Close the browser window and try again."));
            try
            {
                var lines = File.ReadAllLines(Path.Combine(profile, "DevToolsActivePort"));
                if (lines.Length >= 2 && int.TryParse(lines[0], out var port) && port > 0 && lines[1].StartsWith("/devtools/browser/", StringComparison.Ordinal))
                    socket = new Uri($"ws://127.0.0.1:{port}{lines[1]}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (socket is null) await Task.Delay(300, deadline);
        }
        using var client = new ClientWebSocket();
        await client.ConnectAsync(socket, deadline);
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
        string tried = "";
        for (int id = 1; ; id++)
        {
            await Task.Delay(1000, deadline);
            if (process.HasExited) throw new SignInException(Loc.T("登录窗口已关闭，未保存登录", "The sign-in window was closed before signing in."));
            var cookies = ExtractCookies((await CallAsync(client, id, "Storage.getCookies", deadline)).Get("cookies"), edition);
            if (!DoubaoQuotaUsage.Session.HasAuthCookie(cookies)) continue;
            var session = new DoubaoQuotaUsage.Session(cookies);
            var header = session.CookieHeader();
            if (header.Length == 0 || header == tried) continue;
            tried = header;
            if (await AcceptedAsync(http, edition, session, deadline)) return session;
        }
    }

    private static Dictionary<string, string> ExtractCookies(JsonElement list, DoubaoQuotaUsage.Edition edition)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (list.ValueKind != JsonValueKind.Array) return result;
        var suffix = "." + (edition.China ? "doubao.com" : "dola.com");
        // Apply broader domain cookies first, then exact host cookies so the more specific cookie wins.
        var matching = list.Items()
            .Select(c => (Name: c.Get("name").Text(), Value: c.Get("value").Text(), Domain: c.Get("domain").Text()))
            .Where(c => c.Name.Length > 0 && c.Value.Length > 0 && MatchesDomain(c.Domain, edition.CookieDomains, suffix))
            .OrderBy(c => c.Domain.StartsWith('.') ? 0 : 1);
        foreach (var cookie in matching) result[cookie.Name] = cookie.Value;
        return result;
    }

    private static bool MatchesDomain(string domain, IReadOnlyList<string> allowed, string suffix)
    {
        if (domain.Length == 0) return false;
        foreach (var candidate in allowed)
            if (domain.Equals(candidate, StringComparison.OrdinalIgnoreCase)) return true;
        return domain.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> AcceptedAsync(HttpClient http, DoubaoQuotaUsage.Edition edition, DoubaoQuotaUsage.Session session, CancellationToken cancellation)
    {
        try
        {
            await DoubaoQuotaUsage.ReadAsync(http, edition, session, cancellation);
            return true;
        }
        catch (Exception ex) when (ex is DoubaoQuotaUsage.RejectedException or HttpRequestException or JsonException or InvalidDataException)
        { return false; }
    }

    private static async Task<JsonElement> CallAsync(ClientWebSocket client, int id, string method, CancellationToken cancellation)
    {
        var command = JsonSerializer.SerializeToUtf8Bytes(new { id, method });
        await client.SendAsync(command, WebSocketMessageType.Text, true, cancellation);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await client.ReceiveAsync(buffer, cancellation);
                if (result.MessageType == WebSocketMessageType.Close) throw new SignInException(Loc.T("登录窗口已关闭", "The sign-in window was closed."));
                message.Write(buffer, 0, result.Count);
                if (message.Length > 8 * 1024 * 1024) throw new SignInException(Loc.T("无法读取登录结果", "The sign-in cannot be read."));
            } while (!result.EndOfMessage);
            using var doc = JsonDocument.Parse(message.ToArray());
            if (doc.RootElement.Get("id").Number() != id) continue;
            return doc.RootElement.Get("result").Clone();
        }
    }
}
