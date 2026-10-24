using System.Runtime.InteropServices;
using System.Security.Cryptography;
using BrimDeck.Native;

// The saved notices are encrypted with ProtectedData now; files written before were encrypted by calling CryptProtectData
// directly. Both must open each other's data, for the current user, with the same entropy and only with it.
internal static class NoticeProtectionTests
{
    public static void Run(Action<string, bool> check)
    {
        var protector = new NoticeProtection();
        byte[] notices = "{\"Version\":1,\"Notices\":[{\"Title\":\"构建完成\"}]}"u8.ToArray();
        var sealedNow = protector.Protect(notices);
        check("notices: protected data opens again", protector.Unprotect(sealedNow).AsSpan().SequenceEqual(notices) && !sealedNow.AsSpan().SequenceEqual(notices));
        var sealedBefore = Earlier.Protect(notices);
        check("notices: a file written by the earlier direct CryptProtectData call still opens", protector.Unprotect(sealedBefore).AsSpan().SequenceEqual(notices));
        check("notices: data written now opens with the earlier call too (a downgrade keeps the history)", Earlier.Unprotect(sealedNow).AsSpan().SequenceEqual(notices));
        bool refused;
        try { ProtectedData.Unprotect(sealedNow, null, DataProtectionScope.CurrentUser); refused = false; }
        catch (CryptographicException) { refused = true; }
        check("notices: without BrimDeck's entropy the data does not open", refused);
        check("notices: empty data round-trips", protector.Unprotect(protector.Protect([])).Length == 0);
    }

    // The earlier NoticeProtection, kept here only to prove compatibility; BrimDeck itself no longer imports crypt32.
    private static class Earlier
    {
        private static readonly byte[] Entropy = "BrimDeck.Notices.v1"u8.ToArray();
        public static byte[] Protect(byte[] data) => Run(data, protect: true);
        public static byte[] Unprotect(byte[] data) => Run(data, protect: false);

        private static byte[] Run(byte[] data, bool protect)
        {
            var input = GCHandle.Alloc(data, GCHandleType.Pinned);
            var entropy = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
            try
            {
                var inBlob = new Blob { Size = data.Length, Data = input.AddrOfPinnedObject() };
                var entropyBlob = new Blob { Size = Entropy.Length, Data = entropy.AddrOfPinnedObject() };
                const int UiForbidden = 1;
                bool ok = protect
                    ? CryptProtectData(ref inBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output)
                    : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
                if (!ok) throw new CryptographicException(Marshal.GetLastPInvokeError());
                try
                {
                    var result = new byte[output.Size];
                    Marshal.Copy(output.Data, result, 0, output.Size);
                    return result;
                }
                finally { LocalFree(output.Data); }
            }
            finally { input.Free(); entropy.Free(); }
        }

        [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
        [DllImport("crypt32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref Blob data, string? description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
        [DllImport("crypt32", SetLastError = true)]
        private static extern bool CryptUnprotectData(ref Blob data, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
        [DllImport("kernel32")] private static extern IntPtr LocalFree(IntPtr memory);
    }
}
