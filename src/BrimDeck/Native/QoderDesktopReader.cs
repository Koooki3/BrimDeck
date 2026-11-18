using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BrimDeck.Core;
using Microsoft.Win32;

namespace BrimDeck.Native;

// The Qoder desktop app (and Qoder CN) is an Electron app installed per user or for every user; its uninstall entry is named
// "Qoder 0.5.14" ("Qoder CN 0.5.14") with the publisher Qoder, and its uninstaller sits in the install folder. The Qoder IDE has
// entries of its own ("Qoder IDE"), which the version in the name keeps apart. The app keeps its sign-in in auth.v1.dat in its
// data folder, encrypted with Chromium's os_crypt like the other Electron apps (ChromiumSecrets).
internal static partial class QoderDesktopReader
{
    private const string Uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    public static string? Find(bool china)
    {
        var name = china ? NameCn() : NameGlobal();
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var root = hive.OpenSubKey(Uninstall);
                if (root is null) continue;
                foreach (var subkey in root.GetSubKeyNames())
                {
                    using var entry = root.OpenSubKey(subkey);
                    if (entry?.GetValue("DisplayName") is not string display || !name.IsMatch(display) || entry.GetValue("Publisher") as string != "Qoder") continue;
                    if (Folder(entry.GetValue("UninstallString") as string) is { } folder && File.Exists(Path.Combine(folder, china ? "Qoder CN.exe" : "Qoder.exe"))) return folder;
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return null;
    }

    // The folder of the quoted uninstaller: "D:\Programs\Qoder\Uninstall Qoder.exe" /currentuser.
    private static string? Folder(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var match = Quoted().Match(command);
        var path = match.Success ? match.Groups[1].Value : command.Trim();
        try { return Path.GetDirectoryName(path); } catch (ArgumentException) { return null; }
    }

    // The QwenWork desktop apps (QwenWork CN, 千问办公, and the international QwenWork) are built on the same app and keep their
    // sign-in the same way, in auth-v2.dat.
    public static string? ReadSignIn(string folder, string name = "auth.v1.dat")
    {
        var file = Path.Combine(folder, name);
        if (!File.Exists(file)) return null;
        byte[]? key = null, plain = null;
        try
        {
            key = ChromiumSecrets.Key(Path.Combine(folder, "Local State"));
            plain = ChromiumSecrets.Decrypt(key, Convert.ToBase64String(File.ReadAllBytes(file)));
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or FormatException or CryptographicException or ArgumentException)
        { return null; }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            if (plain is not null) CryptographicOperations.ZeroMemory(plain);
        }
    }

    [GeneratedRegex(@"^Qoder \d")] private static partial Regex NameGlobal();
    [GeneratedRegex(@"^Qoder CN \d")] private static partial Regex NameCn();
    [GeneratedRegex("^\"([^\"]+)\"")] private static partial Regex Quoted();
}
