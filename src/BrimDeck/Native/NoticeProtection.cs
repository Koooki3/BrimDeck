using System.Security.Cryptography;
using BrimDeck.Core.Notices;

namespace BrimDeck.Native;

// Windows data protection for the saved notices: only the current user, on this PC, can decrypt them. An extra entropy value
// keeps other programs of the same user that unprotect data without it from reading the file by accident. Through .NET's
// ProtectedData (the same CryptProtectData, current user, no UI), so files written before still open.
internal sealed class NoticeProtection : INoticeProtector
{
    private static readonly byte[] Entropy = "BrimDeck.Notices.v1"u8.ToArray();

    public byte[] Protect(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
}
