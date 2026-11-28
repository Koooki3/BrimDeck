using System.IO;
using System.Text;
using BrimDeck.Native;

// The key file (secrets.dat) in a temporary folder, encrypted for this user as the app does. A file that no longer reads as
// the expected JSON (cut short, overwritten by another program) must not lock the user out: a key saved afresh is kept and
// read back, a provider can still be removed, and the unreadable file is set aside rather than lost.
internal static class ProviderSecretsChecks
{
    public static void Run(Action<string, bool> check)
    {
        var folder = Path.Combine(Path.GetTempPath(), "BrimDeckSecretsCheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var store = new ProviderSecrets(folder);
            Guid one = Guid.NewGuid(), two = Guid.NewGuid();
            store.Write(one, "first-key");
            store.Write(two, "second-key");
            check("secrets: a saved key reads back", store.Read(one) == "first-key" && store.Read(two) == "second-key");

            var file = Path.Combine(folder, "secrets.dat");
            var damaged = Encoding.UTF8.GetBytes("{\"" + one + "\":\"AQAAANCMnd8BFdERjHoAwE");
            File.WriteAllBytes(file, damaged);
            check("secrets: a key can be saved over an unreadable key file", Try(() => store.Write(one, "new-key")));
            check("secrets: the key saved over an unreadable file reads back", Try(() => store.Read(one)) && store.Read(one) == "new-key");
            check("secrets: the unreadable key file is set aside unchanged", File.Exists(file + ".bad") && File.ReadAllBytes(file + ".bad").AsSpan().SequenceEqual(damaged));

            File.WriteAllBytes(file, damaged);
            check("secrets: a provider's key can be removed while the key file is unreadable", Try(() => store.Delete(two)));
            check("secrets: after the removal the file reads again and holds no key for it", Try(() => store.Read(two)) && store.Read(two) == "");
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
    }

    private static bool Try(Action action)
    {
        try { action(); return true; }
        catch (Exception ex) { Console.WriteLine($"  {ex.GetType().Name}: {ex.Message}"); return false; }
    }
    private static bool Try(Func<string> read) => Try(() => { read(); });
}
