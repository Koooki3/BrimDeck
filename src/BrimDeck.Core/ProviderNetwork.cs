using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BrimDeck.Core;

// How a custom row's script comes by the API key. Injected: the script is given an opaque placeholder as ctx.secrets.key and the
// host puts the real key into the request (headers, body, path and query) when it leaves for an authorized host, so the script
// never holds the key. Script: the script is given the key itself, as every script was before hosts were authorized; rows saved
// by earlier versions keep this until the user changes it, because a script may compute with the key (sign, split, hash).
public enum SecretAccess { Injected, Script }

// The network a row's script may use: the hosts it may send requests to (and so may receive the key), and how it gets the key.
public sealed record ProviderNetworkPolicy(IReadOnlyList<string> Hosts, SecretAccess Secret)
{
    public bool Allows(Uri uri) => ProviderNetwork.Matches(Hosts, uri.IdnHost);
}

// A request that a script made to a host it is not authorized for, kept so that the refusal can be seen.
public sealed record ProviderNetworkDenial(Guid InstanceId, string Name, string Host, DateTimeOffset At);

public static class ProviderNetwork
{
    public const int MaximumHosts = 16;

    // Rows with their own script, or that read fixed services from the application, are bound to those services whatever
    // AppEntry.AllowedHosts says; the keys entered for them go nowhere else.
    public static IReadOnlyList<string> FixedHosts(ProviderId id) => id switch
    {
        ProviderId.GlmChina => ["open.bigmodel.cn"],
        ProviderId.GlmGlobal => ["api.z.ai"],
        ProviderId.DeepSeek => ["api.deepseek.com"],
        ProviderId.OpenRouter => ["openrouter.ai"],
        ProviderId.KimiApi => [HostOf(KimiUsage.DefaultBaseUrl), HostOf(KimiUsage.GlobalBaseUrl)],
        ProviderId.OpenCodeGo => [HostOf(OpenCodeUsage.DefaultServer)],
        _ => []
    };
    private static string HostOf(string url) => new Uri(url).IdnHost;

    // New API and Sub2API rows read their own site; a custom row may also be given hosts to read, besides its site.
    public static ProviderNetworkPolicy For(AppEntry entry)
    {
        var hosts = new List<string>(FixedHosts(entry.QuotaSource));
        if (entry.QuotaSource is ProviderId.Custom or ProviderId.NewApi or ProviderId.Sub2Api && SiteHost(entry.Site) is { } site) hosts.Add(site);
        if (entry.QuotaSource != ProviderId.Custom) return new(hosts.Distinct().ToList(), SecretAccess.Injected);
        // A custom row that has not been through Normalize yet is read as a row saved by an earlier version would be migrated.
        bool legacy = entry.AllowedHosts is null;
        hosts.AddRange(legacy ? LiteralHosts(entry.Script ?? "") : entry.AllowedHosts!.Select(NormalizePattern).OfType<string>());
        return new(hosts.Distinct().ToList(), legacy ? SecretAccess.Script : entry.SecretAccess);
    }

    // Exact names, or *.example.com for every name below example.com (not example.com itself). Ports and paths are not part of it.
    // An address is matched only by the same address.
    public static bool Matches(IEnumerable<string> patterns, string host)
    {
        host = host.TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0) return false;
        foreach (var pattern in patterns)
        {
            if (!pattern.StartsWith("*.", StringComparison.Ordinal)) { if (pattern == host) return true; continue; }
            var suffix = pattern[1..];
            if (host.Length > suffix.Length && host.EndsWith(suffix, StringComparison.Ordinal) && !IPAddress.TryParse(host, out _)) return true;
        }
        return false;
    }

    // What the user may have typed or pasted ("API.Example.com", "https://api.example.com/v1", "*.example.com") as a host pattern
    // in its stored form; null when it is not one.
    public static string? NormalizePattern(string? text)
    {
        text = (text ?? "").Trim();
        bool wildcard = text.StartsWith("*.", StringComparison.Ordinal);
        if (wildcard) text = text[2..];
        if (text.Length is 0 or > 300 || text.Any(char.IsWhiteSpace) || text.Contains('*')) return null;
        if (!Uri.TryCreate(text.Contains("://") ? text : "https://" + text, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Host.Length == 0) return null;
        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0 || wildcard && (uri.HostNameType != UriHostNameType.Dns || !host.Contains('.'))) return null;
        return wildcard ? "*." + host : host;
    }

    public static List<string> NormalizeHosts(IEnumerable<string?>? hosts) =>
        [.. (hosts ?? []).Select(NormalizePattern).OfType<string>().Distinct().Take(MaximumHosts)];

    private static string? SiteHost(string site) =>
        Uri.TryCreate((site ?? "").Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0 ? uri.IdnHost.TrimEnd('.').ToLowerInvariant() : null;

    private static readonly Regex Literal = new(@"https?://(\[[0-9A-Fa-f:.]+\]|[A-Za-z0-9](?:[A-Za-z0-9.\-]*[A-Za-z0-9])?)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly ConditionalWeakTable<string, string[]> Literals = new();

    // The hosts a script names in its own text as http(s)://host. A script that builds its addresses piecemeal names none.
    public static IReadOnlyList<string> LiteralHosts(string script)
    {
        if (script.Length == 0) return [];
        return Literals.GetValue(script, text =>
        {
            try { return NormalizeHosts(Literal.Matches(text).Select(match => match.Groups[1].Value)).ToArray(); }
            catch (RegexMatchTimeoutException) { return []; }
        });
    }

    // Brings a custom row's network settings into the form this version keeps. A row saved by an earlier version (AllowedHosts
    // absent) that has been used keeps working as it did: the hosts its script names are authorized, besides its site, it still
    // receives the key itself, and it is marked for one review (NetworkReviewPending). Grants rest on the saved text and nothing
    // else, so a request to a host the script builds up at run time is refused, with the refusal visible, and is added by the user.
    // A row that has not been used starts with no hosts and a host-injected key.
    public static void Normalize(AppEntry entry)
    {
        if (entry.QuotaSource != ProviderId.Custom) { if (entry.AllowedHosts is not null) entry.AllowedHosts = NormalizeHosts(entry.AllowedHosts); return; }
        if (entry.AllowedHosts is null)
        {
            bool used = entry.SecretRevision != Guid.Empty || (entry.Site ?? "").Trim().Length > 0 || !ProviderScripts.IsBlankOrTemplate(entry.Script);
            if (used)
            {
                var site = SiteHost(entry.Site ?? "");
                entry.AllowedHosts = [.. LiteralHosts(entry.Script ?? "").Where(host => host != site)];
                entry.SecretAccess = SecretAccess.Script;
                entry.NetworkReviewPending = true;
            }
            else { entry.AllowedHosts = []; entry.SecretAccess = SecretAccess.Injected; }
        }
        else entry.AllowedHosts = NormalizeHosts(entry.AllowedHosts);
        if (!Enum.IsDefined(entry.SecretAccess)) entry.SecretAccess = SecretAccess.Injected;
    }

    // The one-time notice for a migrated row; empty once reviewed.
    public static string ReviewNotice(AppEntry entry)
    {
        if (!entry.NetworkReviewPending || entry.QuotaSource != ProviderId.Custom) return "";
        var hosts = For(entry).Hosts;
        return hosts.Count == 0
            ? Loc.T("此来源的脚本现在只能访问被授权的域名，目前没有授权任何域名。请在设置中添加脚本需要访问的域名。", "This source's script can now reach only authorized hosts, and none is authorized yet. Add the hosts the script needs in settings.")
            : Loc.T($"此来源的脚本现在只能访问被授权的域名：{string.Join("、", hosts)}。请在设置中确认。", $"This source's script can now reach only authorized hosts: {string.Join(", ", hosts)}. Confirm them in settings.");
    }

    // Part of the keys that tell rows apart, so that a row whose authorization changed is read again and does not show the answer
    // given under the old one. Empty for every row other than a custom one whose authorization differs from the one its saved text
    // gives, so the keys of other rows, and of migrated rows left as they were, stay as they were.
    public static string Fingerprint(AppEntry entry)
    {
        if (entry.QuotaSource != ProviderId.Custom) return "";
        var policy = For(entry);
        var saved = new List<string>(); if (SiteHost(entry.Site ?? "") is { } site) saved.Add(site);
        saved.AddRange(LiteralHosts(entry.Script ?? ""));
        var now = policy.Hosts.Order(StringComparer.Ordinal).ToArray();
        if (policy.Secret == SecretAccess.Script && now.SequenceEqual(saved.Distinct().Order(StringComparer.Ordinal))) return "";
        return ":net" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", now) + "|" + policy.Secret)))[..12];
    }

    public static string DeniedMessage(string host) =>
        Loc.T($"未授权访问 {host}，请求已拒绝。如需访问，请在此来源的允许域名中添加它。", $"Access to {host} is not authorized, and the request was refused. To allow it, add it to this source's allowed hosts.");
    public static string DeniedLabel => Loc.T("域名未授权", "Host not allowed");
}
