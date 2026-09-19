using System.Text;
using KanbanTasker.Updates;

namespace KanbanTasker.Tests;

public class UpdateTests
{
    [Theory]
    [InlineData(null, "2.1.0.0", UpdateDisposition.Install)]
    [InlineData("2.0.1.0", "2.1.0.0", UpdateDisposition.Update)]
    [InlineData("2.9.0.0", "2.10.0.0", UpdateDisposition.Update)]
    [InlineData("2.1.0.0", "2.1.0.1", UpdateDisposition.Update)]
    [InlineData("2.1.0.0", "2.1.0.0", UpdateDisposition.AlreadyInstalled)]
    [InlineData("2.1.0.0", "2.0.1.0", UpdateDisposition.OlderVersion)]
    public void ComparesNumericPackageVersions(string? installed, string candidate, object expected) =>
        Assert.Equal((UpdateDisposition)expected, UpdateFiles.Compare(installed is null ? null : Version.Parse(installed), Version.Parse(candidate)));

    [Theory]
    [InlineData("")]
    [InlineData("2.1")]
    [InlineData("2.1.0")]
    [InlineData("2.1.0.65536")]
    [InlineData("-1.0.0.0")]
    [InlineData("2.1.0.0-beta")]
    public void RejectsInvalidPackageVersions(string version) => Assert.Throws<InvalidDataException>(() => UpdateFiles.ParseVersion(version));

    [Theory]
    [InlineData("x64")]
    [InlineData("arm64")]
    public void ReadsExpectedPackageIdentityAndVersion(string architecture)
    {
        using var manifest = Manifest("KanbanTasker.Revived", "CN=KanbanTasker.Local", architecture);
        Assert.Equal(new UpdateFile(new Version(2, 1, 0, 0), architecture, false), UpdateFiles.ReadManifest(manifest, architecture));
    }

    [Theory]
    [InlineData("Another.App", "CN=KanbanTasker.Local", "x64")]
    [InlineData("KanbanTasker.Revived", "CN=SomeoneElse", "x64")]
    [InlineData("KanbanTasker.Revived", "CN=KanbanTasker.Local", "arm64")]
    [InlineData("KanbanTasker.Revived", "CN=KanbanTasker.Local", "neutral")]
    public void RejectsWrongIdentityPublisherAndArchitecture(string name, string publisher, string architecture)
    {
        using var manifest = Manifest(name, publisher, architecture);
        Assert.Throws<InvalidDataException>(() => UpdateFiles.ReadManifest(manifest, "x64"));
    }

    [Fact]
    public void RejectsMissingIdentity()
    {
        using var manifest = new MemoryStream(Encoding.UTF8.GetBytes("<Package/>"));
        Assert.Throws<InvalidDataException>(() => UpdateFiles.ReadManifest(manifest, "x64"));
    }

    [Fact]
    public void RejectsArbitraryFileTypes() => Assert.Throws<InvalidDataException>(() => UpdateFiles.Read("update.txt", "x64"));

    private static MemoryStream Manifest(string name, string publisher, string architecture) => new(Encoding.UTF8.GetBytes($"""
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
          <Identity Name="{name}" Publisher="{publisher}" Version="2.1.0.0" ProcessorArchitecture="{architecture}"/>
        </Package>
        """));
}
