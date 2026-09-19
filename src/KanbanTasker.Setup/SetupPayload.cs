using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace KanbanTasker.Setup;

internal sealed class SetupPayload : IDisposable
{
    private readonly string directory;
    private readonly FileStream packageLock;
    public string PackagePath { get; }
    public PayloadMetadata Metadata { get; }
    public X509Certificate2 Certificate { get; }

    private SetupPayload(string directory, string path, FileStream packageLock, PayloadMetadata metadata, X509Certificate2 certificate)
    {
        this.directory = directory; PackagePath = path; this.packageLock = packageLock;
        Metadata = metadata; Certificate = certificate;
    }

    private static Stream Resource(string name) => Assembly.GetExecutingAssembly().GetManifestResourceStream("Setup." + name)
        ?? throw new InvalidDataException("The installer is incomplete. Build it with scripts/package-installer.ps1.");

    public static PayloadMetadata ReadMetadata()
    {
        using var stream = Resource("Metadata");
        return JsonSerializer.Deserialize<PayloadMetadata>(stream) ?? throw new InvalidDataException("Installer metadata is missing.");
    }

    public static X509Certificate2 ReadCertificate(PayloadMetadata metadata)
    {
        using var input = Resource("Certificate");
        using var bytes = new MemoryStream(); input.CopyTo(bytes);
        return PayloadValidation.Certificate(bytes.ToArray(), metadata);
    }

    public static SetupPayload Extract()
    {
        var metadata = ReadMetadata();
        var architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64", Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException("Kanban Tasker requires a 64-bit Windows PC (x64 or ARM64).")
        };
        if (architecture != metadata.Architecture)
            throw new PlatformNotSupportedException($"This installer is for {metadata.Architecture}. Use the {architecture} installer on this PC.");
        var certificate = ReadCertificate(metadata);
        var directory = Path.Combine(Path.GetTempPath(), "KanbanTasker-Setup-" + Guid.NewGuid().ToString("N"));
        FileStream? handle = null;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "KanbanTasker.msix");
            using (var source = Resource("Package"))
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) source.CopyTo(output);
            // Keep the verified file read-locked through deployment so it cannot be replaced after verification.
            handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            PayloadValidation.Package(handle, metadata);
            return new(directory, path, handle, metadata, certificate);
        }
        catch
        {
            handle?.Dispose(); certificate.Dispose(); DeleteTemporaryPayload(directory); throw;
        }
    }

    public void Dispose()
    {
        packageLock.Dispose(); Certificate.Dispose(); DeleteTemporaryPayload(directory);
    }

    private static void DeleteTemporaryPayload(string directory)
    {
        // Delete only the one file and empty directory created by this invocation; never recurse.
        try { File.Delete(Path.Combine(directory, "KanbanTasker.msix")); Directory.Delete(directory); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
