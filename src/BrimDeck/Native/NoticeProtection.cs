using System.Runtime.InteropServices;
using System.Security.Cryptography;
using BrimDeck.Core.Notices;

namespace BrimDeck.Native;

// Windows data protection for the saved notices: only the current user, on this PC, can decrypt them. An extra entropy value
// keeps other programs of the same user that call CryptUnprotectData without it from reading the file by accident.
internal sealed class NoticeProtection : INoticeProtector
{
    private static readonly byte[] Entropy = "BrimDeck.Notices.v1"u8.ToArray();

    public byte[] Protect(byte[] data) => Run(data, protect: true);
    public byte[] Unprotect(byte[] data) => Run(data, protect: false);

    private static byte[] Run(byte[] data, bool protect)
    {
        var input = Pin(data);
        var entropy = Pin(Entropy);
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

    private static GCHandle Pin(byte[] data) => GCHandle.Alloc(data, GCHandleType.Pinned);

    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob data, string? description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob data, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32")] private static extern IntPtr LocalFree(IntPtr memory);
}
