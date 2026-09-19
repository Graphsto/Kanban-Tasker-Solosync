using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;

namespace KanbanTasker.Setup;

internal sealed record PayloadMetadata(string Version, string Architecture, string PackageSha256,
    string CertificateSha256, string CertificateThumbprint);

internal static class PayloadValidation
{
    public const string Identity = "KanbanTasker.Revived";
    public const string Publisher = "CN=KanbanTasker.Local";

    public static X509Certificate2 Certificate(byte[] bytes, PayloadMetadata metadata)
    {
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(metadata.CertificateSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The embedded certificate is damaged. Obtain a fresh installer.");
        var certificate = X509CertificateLoader.LoadCertificate(bytes);
        if (certificate.Subject != Publisher || certificate.HasPrivateKey ||
            !certificate.Thumbprint.Equals(metadata.CertificateThumbprint, StringComparison.OrdinalIgnoreCase) ||
            DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() || DateTime.UtcNow > certificate.NotAfter.ToUniversalTime() ||
            !certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()
                .Any(e => e.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == "1.3.6.1.5.5.7.3.3")))
        {
            certificate.Dispose();
            throw new InvalidDataException("The embedded signing certificate is invalid or expired. Obtain a fresh installer.");
        }
        return certificate;
    }

    public static void Package(Stream package, PayloadMetadata metadata)
    {
        if (!package.CanSeek) throw new ArgumentException("Package verification requires a seekable stream.");
        package.Position = 0;
        var hash = Convert.ToHexString(SHA256.HashData(package));
        if (!hash.Equals(metadata.PackageSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The embedded app package is damaged. Obtain a fresh installer.");
        package.Position = 0;
        using var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
        var entry = archive.GetEntry("AppxManifest.xml") ?? throw new InvalidDataException("The app package has no manifest.");
        using var manifestStream = entry.Open();
        var manifest = XDocument.Load(manifestStream);
        XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        var identity = manifest.Root?.Element(ns + "Identity");
        if ((string?)identity?.Attribute("Name") != Identity || (string?)identity?.Attribute("Publisher") != Publisher ||
            (string?)identity?.Attribute("Version") != metadata.Version ||
            (string?)identity?.Attribute("ProcessorArchitecture") != metadata.Architecture ||
            archive.GetEntry("AppxSignature.p7x") is null || archive.GetEntry("resources.pri") is null)
            throw new InvalidDataException("The app package does not match this installer or is incomplete.");
    }
}
