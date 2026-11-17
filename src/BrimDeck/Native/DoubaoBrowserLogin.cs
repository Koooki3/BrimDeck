using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using BrimDeck.Core;
using BrimDeck.Core.ClipboardSync;

namespace BrimDeck.Native;

internal sealed class DoubaoSignInWindowClosedException()
    : IOException(Loc.T("登录窗口已关闭。", "The sign-in window was closed."));

// A dedicated profile in the installed browser, as used by MiMoAccount. No installed IME
// or normal browser profile is inspected, and no browser runtime is distributed.
internal static class DoubaoBrowserLogin
{
    internal static async Task SignInAsync(ClipboardSyncService service, Action<string> status, CancellationToken ct)
    {
        string browser = MiMoAccount.FindBrowser() ?? throw new InvalidOperationException(Loc.T("登录需要 Chrome 或 Edge 浏览器。", "Signing in needs Chrome or Edge."));
        string root = Path.GetFullPath(Path.Combine(service.Folder, "browser-login"));
        string profile = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        var start = new ProcessStartInfo(browser) { UseShellExecute = false };
        foreach (string arg in new[] { "--user-data-dir=" + profile, "--remote-debugging-port=0", "--remote-debugging-address=127.0.0.1",
            "--no-first-run", "--no-default-browser-check", "--disable-background-mode", "--new-window", "https://www.doubao.com/chat/" }) start.ArgumentList.Add(arg);
        Process? process = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            process = Process.Start(start) ?? throw new IOException("Cannot open browser.");
            status(Loc.T("正在等待登录…", "Waiting for you to sign in…"));
            var portWait = Stopwatch.StartNew(); Uri? socket = null;
            while (socket is null)
            {
                if (process.HasExited) throw new DoubaoSignInWindowClosedException();
                if (portWait.Elapsed > TimeSpan.FromSeconds(30)) throw new IOException(Loc.T("浏览器没有打开登录窗口。", "The browser did not open the sign-in window."));
                try
                {
                    string[] lines = await File.ReadAllLinesAsync(Path.Combine(profile, "DevToolsActivePort"), deadline.Token);
                    if (lines.Length >= 2 && int.TryParse(lines[0], out int port) && port is > 0 and <= 65535 &&
                        lines[1].StartsWith("/devtools/browser/", StringComparison.Ordinal) && !lines[1].Any(char.IsWhiteSpace))
                        socket = new Uri($"ws://127.0.0.1:{port}{lines[1]}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                if (socket is null) await Task.Delay(300, deadline.Token);
            }
            using var client = new ClientWebSocket(); await client.ConnectAsync(socket, deadline.Token);
            DateTime? rejectedSince = null;
            for (int id = 1; ; id++)
            {
                await Task.Delay(1500, deadline.Token);
                if (process.HasExited) throw new DoubaoSignInWindowClosedException();
                var response = await CallAsync(client, id, "Storage.getCookies", deadline.Token);
                var identity = new DoubaoIdentity { DeviceId = service.Accounts.DoubaoDeviceId, DeviceName = service.Accounts.DeviceName };
                foreach (var cookie in response.GetProperty("cookies").EnumerateArray())
                {
                    string domain = cookie.GetProperty("domain").GetString() ?? "";
                    if (domain is not (".doubao.com" or "www.doubao.com" or "doubao.com")) continue;
                    string name = cookie.GetProperty("name").GetString() ?? "", value = cookie.GetProperty("value").GetString() ?? "";
                    if (name is "sessionid" or "sessionid_ss" or "passport_csrf_token" or "passport_csrf_token_default")
                        if (value.Length is > 0 and < 8192 && !value.Any(c => c is '\r' or '\n' or ';')) identity.Cookies[name] = value;
                }
                if (!identity.Cookies.ContainsKey("sessionid")) continue;
                status(Loc.T("已登录，正在连接…", "Signed in. Connecting…"));
                try { await service.ConnectDoubaoAsync(identity, deadline.Token); return; }
                catch (Exception ex) when (ex is InvalidDataException or DoubaoRequestException)
                {
                    rejectedSince ??= DateTime.UtcNow;
                    if (DateTime.UtcNow - rejectedSince.Value > TimeSpan.FromSeconds(20))
                        throw new InvalidDataException(Loc.T("豆包没有接受这次登录。", "Doubao did not accept this sign-in."));
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException(Loc.T("等待登录超时。", "Timed out waiting for sign-in.")); }
        catch (WebSocketException) when (process is { HasExited: true })
        { throw new DoubaoSignInWindowClosedException(); }
        finally
        {
            // This process belongs to the random profile created above; never terminate the user's ordinary browser.
            try
            {
                if (process is { HasExited: false })
                {
                    process.Kill(true);
                    using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(cleanupDeadline.Token);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException) { }
            process?.Dispose();
            _ = CleanupAsync(root, profile);
        }
    }
    private static async Task CleanupAsync(string root, string profile)
    {
        string full = Path.GetFullPath(profile);
        if (!full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
        for (int i = 0; i < 10; i++)
        {
            try { if (Directory.Exists(full)) Directory.Delete(full, true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await Task.Delay(500); }
        }
    }
    private static async Task<JsonElement> CallAsync(ClientWebSocket client, int id, string method, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await client.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { id, method }), WebSocketMessageType.Text, true, timeout.Token);
        byte[] buffer = new byte[16384];
        while (true)
        {
            using var message = new MemoryStream(); WebSocketReceiveResult part;
            do
            {
                part = await client.ReceiveAsync(buffer, timeout.Token);
                if (part.MessageType == WebSocketMessageType.Close) throw new DoubaoSignInWindowClosedException();
                if (part.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("Invalid browser response.");
                message.Write(buffer, 0, part.Count);
                if (message.Length > 2 * 1024 * 1024) throw new InvalidDataException("Browser response too large.");
            } while (!part.EndOfMessage);
            using var doc = JsonDocument.Parse(message.ToArray());
            if (!doc.RootElement.TryGetProperty("id", out var reply) || reply.GetInt32() != id) continue;
            if (doc.RootElement.TryGetProperty("error", out _)) throw new InvalidDataException("Browser rejected sign-in request.");
            return doc.RootElement.GetProperty("result").Clone();
        }
    }
}
