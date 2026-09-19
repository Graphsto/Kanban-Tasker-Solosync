using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace KanbanTasker.Updates;

internal static class WindowsSignature
{
    public static void Verify(string path, X509Certificate2 expectedSigner)
    {
        VerifyWindowsTrust(path);
        using var signer = ReadSigner(path);
        if (!signer.RawData.AsSpan().SequenceEqual(expectedSigner.RawData))
            throw new CryptographicException("The update was signed with a different certificate. Use an update from the same publisher as this installation.");
    }

    private static X509Certificate2 ReadSigner(string path)
    {
        if (Path.GetExtension(path).Equals(".msix", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ZipFile.OpenRead(path);
            var entry = archive.GetEntry("AppxSignature.p7x") ?? throw new CryptographicException("The package is unsigned.");
            if (entry.Length > 4 * 1024 * 1024) throw new CryptographicException("The package signature is too large.");
            using var input = entry.Open();
            var bytes = new byte[checked((int)entry.Length)];
            input.ReadExactly(bytes);
            if (input.ReadByte() != -1) throw new CryptographicException("The package signature is too large.");
            if (bytes.Length < 4 || !bytes.AsSpan(0, 4).SequenceEqual("PKCX"u8)) throw new CryptographicException("Invalid MSIX signature.");
            var signature = new SignedCms(); signature.Decode(bytes.AsSpan(4));
            if (signature.SignerInfos.Count != 1 || signature.SignerInfos[0].Certificate is not { } cert)
                throw new CryptographicException("The package signing certificate is missing or ambiguous.");
            return X509CertificateLoader.LoadCertificate(cert.RawData);
        }
        // X509CertificateLoader reads certificate files, not Authenticode executables.
#pragma warning disable SYSLIB0057
        using var authenticode = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
        return X509CertificateLoader.LoadCertificate(authenticode.GetRawCertData());
    }

    private static void VerifyWindowsTrust(string path)
    {
        var name = Marshal.StringToCoTaskMemUni(path);
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        var data = new TrustData
        {
            Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1,
            File = filePointer, StateAction = 1, ProviderFlags = 0x1000 // Cached trust only; local updates do not fetch URLs.
        };
        var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        try
        {
            Marshal.StructureToPtr(new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = name }, filePointer, false);
            var status = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            if (status != 0) throw new CryptographicException($"Windows could not verify the update signature (0x{status:X8}). Nothing was started.");
        }
        finally
        {
            data.StateAction = 2; WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            Marshal.FreeHGlobal(filePointer); Marshal.FreeCoTaskMem(name);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustFile { public uint Size; public IntPtr Path, FileHandle, KnownSubject; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public IntPtr PolicyCallback, SipClient;
        public uint UiChoice, RevocationChecks, UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData, UrlReference;
        public uint ProviderFlags, UiContext;
        public IntPtr SignatureSettings;
    }
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
}
