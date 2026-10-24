using System.Security.Cryptography;
using BrimDeck.Native;

// The saved notices are encrypted with ProtectedData. It wraps the same CryptProtectData/CryptUnprotectData that BrimDeck called
// directly before, with the same parameters (current user, no UI, BrimDeck's entropy), so files written before still open;
// nothing in BrimDeck or its tests imports crypt32 itself any more.
internal static class NoticeProtectionTests
{
    private static readonly byte[] Entropy = "BrimDeck.Notices.v1"u8.ToArray();

    public static void Run(Action<string, bool> check)
    {
        var protector = new NoticeProtection();
        byte[] notices = "{\"Version\":1,\"Notices\":[{\"Title\":\"构建完成\"}]}"u8.ToArray();
        var sealedNow = protector.Protect(notices);
        check("notices: protected data opens again", protector.Unprotect(sealedNow).AsSpan().SequenceEqual(notices) && !sealedNow.AsSpan().SequenceEqual(notices));
        var sealedWithEntropy = ProtectedData.Protect(notices, Entropy, DataProtectionScope.CurrentUser);
        check("notices: data protected for the current user with BrimDeck's entropy opens", protector.Unprotect(sealedWithEntropy).AsSpan().SequenceEqual(notices));
        check("notices: BrimDeck's data opens with the same entropy outside it", ProtectedData.Unprotect(sealedNow, Entropy, DataProtectionScope.CurrentUser).AsSpan().SequenceEqual(notices));
        bool refused;
        try { ProtectedData.Unprotect(sealedNow, null, DataProtectionScope.CurrentUser); refused = false; }
        catch (CryptographicException) { refused = true; }
        check("notices: without BrimDeck's entropy the data does not open", refused);
        check("notices: empty data round-trips", protector.Unprotect(protector.Protect([])).Length == 0);
    }
}
