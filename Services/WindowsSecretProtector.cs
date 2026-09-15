using System.Runtime.InteropServices;
using System.Text;
using BannerlordEnvironmentManager.Core.Nexus;

namespace BannerlordEnvironmentManager.Services
{
    // Windows DPAPI at current-user scope. The ciphertext is bound to this Windows account on this
    // machine, so another user on the same box cannot read it and a copied file is useless anywhere
    // else. That is "never leaves the workstation" enforced by the platform rather than by a promise.
    //
    // Called through CryptProtectData directly rather than through the ProtectedData package so the
    // single-file exe gains no new dependency for sixty lines of interop.
    public sealed partial class WindowsSecretProtector : ISecretProtector
    {
        private const uint CryptProtectUiForbidden = 0x1;

        // An extra secret mixed into the key derivation. It is not itself a secret, it only means
        // ciphertext written by BEM cannot be decrypted by another program on the same account
        // handing DPAPI the same bytes.
        private static readonly byte[] Entropy =
            Encoding.UTF8.GetBytes("Bannerlord Environment Manager, Nexus personal API key, v1");

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int Length;
            public IntPtr Data;
        }

        [LibraryImport("crypt32.dll", EntryPoint = "CryptProtectData", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CryptProtectData(
            ref DataBlob input,
            IntPtr description,
            ref DataBlob entropy,
            IntPtr reserved,
            IntPtr prompt,
            uint flags,
            out DataBlob output);

        [LibraryImport("crypt32.dll", EntryPoint = "CryptUnprotectData", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CryptUnprotectData(
            ref DataBlob input,
            IntPtr description,
            ref DataBlob entropy,
            IntPtr reserved,
            IntPtr prompt,
            uint flags,
            out DataBlob output);

        [LibraryImport("kernel32.dll", EntryPoint = "LocalFree")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial IntPtr LocalFree(IntPtr handle);

        public byte[] Protect(string plaintext)
        {
            var bytes = Encoding.UTF8.GetBytes(plaintext);

            try
            {
                return Transform(bytes, protect: true) ?? throw new InvalidOperationException(
                    $"Windows could not encrypt the value (error {Marshal.GetLastWin32Error()}).");
            }
            finally
            {
                Array.Clear(bytes);
            }
        }

        public string? Unprotect(byte[] ciphertext)
        {
            var bytes = Transform(ciphertext, protect: false);

            if (bytes is null)
                return null;

            try
            {
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                Array.Clear(bytes);
            }
        }

        private static byte[]? Transform(byte[] input, bool protect)
        {
            if (input.Length == 0)
                return null;

            var inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
            var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
            var output = default(DataBlob);

            try
            {
                var inputBlob = new DataBlob { Length = input.Length, Data = inputHandle.AddrOfPinnedObject() };
                var entropyBlob = new DataBlob { Length = Entropy.Length, Data = entropyHandle.AddrOfPinnedObject() };

                var succeeded = protect
                    ? CryptProtectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero,
                        CryptProtectUiForbidden, out output)
                    : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero,
                        CryptProtectUiForbidden, out output);

                if (!succeeded || output.Data == IntPtr.Zero)
                    return null;

                var result = new byte[output.Length];
                Marshal.Copy(output.Data, result, 0, output.Length);

                return result;
            }
            finally
            {
                if (output.Data != IntPtr.Zero)
                    _ = LocalFree(output.Data);

                inputHandle.Free();
                entropyHandle.Free();
            }
        }
    }
}
