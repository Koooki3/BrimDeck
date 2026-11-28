using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BrimDeck.Core;

namespace BrimDeck.Native;

public sealed class ProviderSecrets(string directory) : IProviderSecrets
{
    private readonly object _gate = new();
    private string FilePath => Path.Combine(directory, "secrets.dat");
    private Dictionary<Guid, string> Load() => File.Exists(FilePath)
        ? JsonSerializer.Deserialize<Dictionary<Guid, string>>(File.ReadAllBytes(FilePath)) ?? [] : [];
    public string Read(Guid instanceId)
    {
        lock (_gate)
        {
            if (!Load().TryGetValue(instanceId, out var encrypted)) return "";
            var plain = ProtectedData.Unprotect(Convert.FromBase64String(encrypted), null, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
    }
    public void Write(Guid instanceId, string secret)
    {
        lock (_gate)
        {
            var values = LoadForWrite();
            if (secret.Length == 0) values.Remove(instanceId);
            else
            {
                var plain = Encoding.UTF8.GetBytes(secret);
                try { values[instanceId] = Convert.ToBase64String(ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser)); }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            Directory.CreateDirectory(directory);
            var temp = FilePath + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(values)); File.Move(temp, FilePath, true);
        }
    }
    public void Delete(Guid instanceId) => Write(instanceId, "");
    // A file that no longer reads as the store's JSON (cut short, written over by another program) would refuse every change
    // and leave the user no way to save a key again. Its keys cannot be read either way, so it is kept beside the store as
    // secrets.dat.bad and the change starts from an empty store.
    private Dictionary<Guid, string> LoadForWrite()
    {
        try { return Load(); }
        catch (JsonException) { File.Copy(FilePath, FilePath + ".bad", true); return []; }
    }
}
