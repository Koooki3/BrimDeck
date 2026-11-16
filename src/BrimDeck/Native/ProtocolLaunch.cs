using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using BrimDeck.Core.Notices;
using Microsoft.Win32;

namespace BrimDeck.Native;

// Reads whether Windows has a URL scheme registered: HKEY_CLASSES_ROOT\<scheme> carries a "URL Protocol" value. That one value
// is all it reads, and it writes nothing.
internal sealed class ProtocolRegistry : IProtocolRegistry
{
    public bool IsRegistered(string scheme)
    {
        if (string.IsNullOrEmpty(scheme) || scheme.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '.' && c != '+')) return false;
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(scheme);
            return key?.GetValue("URL Protocol") is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }
}

// Hands an address to the program Windows has registered for its scheme (ShellExecute): the address is the only thing given, no
// verb, arguments, directory or window handle, and no error dialog. False when Windows could not start it.
internal sealed class ProtocolLauncher : IProtocolLauncher
{
    public bool Launch(string uri)
    {
        if (!ChatRestore.IsAllowed(uri)) return false;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true, ErrorDialog = false });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException) { return false; }
    }
}
