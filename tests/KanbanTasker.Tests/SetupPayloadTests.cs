using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using KanbanTasker.Setup;

namespace KanbanTasker.Tests;

public class SetupPayloadTests
{
    private static PayloadMetadata Metadata => new("2.0.1.0", "x64", "", "", "");

    [Theory]
    [InlineData("valid")]
    [InlineData("bad hash")]
    [InlineData("wrong publisher")]
    [InlineData("wrong thumbprint")]
    [InlineData("expired")]
    [InlineData("wrong usage")]
    public void OnlyTheEmbeddedValidCodeSigningCertificateIsAccepted(string scenario)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(scenario == "wrong publisher" ? "CN=Other" : PayloadValidation.Publisher,
            key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new(scenario == "wrong usage" ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.3") }, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-3),
            DateTimeOffset.UtcNow.AddDays(scenario == "expired" ? -1 : 1));
        var bytes = certificate.Export(X509ContentType.Cert);
        var metadata = Metadata with
        {
            CertificateSha256 = scenario == "bad hash" ? "wrong" : Convert.ToHexString(SHA256.HashData(bytes)),
            CertificateThumbprint = scenario == "wrong thumbprint" ? "wrong" : certificate.Thumbprint
        };
        if (scenario == "valid")
        {
            using var verified = PayloadValidation.Certificate(bytes, metadata);
            Assert.Equal(certificate.Thumbprint, verified.Thumbprint);
            Assert.False(verified.HasPrivateKey);
        }
        else Assert.Throws<InvalidDataException>(() => PayloadValidation.Certificate(bytes, metadata));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("bad hash")]
    [InlineData("wrong identity")]
    [InlineData("wrong publisher")]
    [InlineData("wrong version")]
    [InlineData("wrong architecture")]
    [InlineData("no signature")]
    [InlineData("no resources")]
    public void CorruptIncompleteOrMismatchedPackagesAreRejected(string scenario)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("AppxManifest.xml").Open()))
                writer.Write($"""
                    <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
                      <Identity Name="{(scenario == "wrong identity" ? "Other" : PayloadValidation.Identity)}"
                        Publisher="{(scenario == "wrong publisher" ? "CN=Other" : PayloadValidation.Publisher)}"
                        Version="{(scenario == "wrong version" ? "1.0.0.0" : Metadata.Version)}"
                        ProcessorArchitecture="{(scenario == "wrong architecture" ? "arm64" : Metadata.Architecture)}" />
                    </Package>
                    """);
            if (scenario != "no signature") zip.CreateEntry("AppxSignature.p7x");
            if (scenario != "no resources") zip.CreateEntry("resources.pri");
        }
        var metadata = Metadata with { PackageSha256 = scenario == "bad hash" ? "wrong" : Convert.ToHexString(SHA256.HashData(bytes.ToArray())) };
        if (scenario == "valid") PayloadValidation.Package(bytes, metadata);
        else Assert.Throws<InvalidDataException>(() => PayloadValidation.Package(bytes, metadata));
    }
}
