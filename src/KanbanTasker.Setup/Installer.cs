using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Windows.Management.Deployment;
using KanbanTasker.Updates;

namespace KanbanTasker.Setup;

internal static class Installer
{
    public static Version? InstalledVersion()
    {
        return new PackageManager().FindPackagesForUser("")
            .Where(p => p.Id.Name == PayloadValidation.Identity && p.Id.Publisher == PayloadValidation.Publisher)
            .Select(p => { var v = p.Id.Version; return new Version(v.Major, v.Minor, v.Build, v.Revision); })
            .OrderDescending().FirstOrDefault();
    }

    public static bool IsTrusted(X509Certificate2 certificate)
    {
        using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Any(c => c.RawData.AsSpan().SequenceEqual(certificate.RawData));
    }

    public static void TrustEmbeddedCertificate()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Windows administrator approval is required to trust the signing certificate.");
        using var certificate = SetupPayload.ReadCertificate(SetupPayload.ReadMetadata());
        using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        store.Add(certificate);
    }

    public static async Task InstallAsync(SetupPayload payload, IProgress<string> progress)
    {
        var target = UpdateFiles.ParseVersion(payload.Metadata.Version);
        var disposition = UpdateFiles.Compare(InstalledVersion(), target);
        if (disposition is UpdateDisposition.AlreadyInstalled or UpdateDisposition.OlderVersion)
            throw new InvalidOperationException($"Version {InstalledVersion()} is already installed. This installer contains {target}; use a newer installer to update.");
        if (!IsTrusted(payload.Certificate))
        {
            progress.Report("Confirm the Windows administrator prompt to trust this app's signing certificate.");
            try
            {
                using var helper = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = true, Verb = "runas", Arguments = "--trust-certificate",
                    WindowStyle = ProcessWindowStyle.Hidden
                }) ?? throw new InvalidOperationException("The certificate helper could not be started.");
                await helper.WaitForExitAsync();
                if (helper.ExitCode != 0 || !IsTrusted(payload.Certificate))
                    throw new InvalidOperationException("The signing certificate was not trusted. Installation has stopped. Run Setup again and approve the administrator prompt, or ask your administrator for help.");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                throw new OperationCanceledException("Administrator approval was cancelled. Nothing was installed.", ex);
            }
        }

        progress.Report("Installing Kanban Tasker for your Windows account…");
        // This process stays with the original user even when different administrator credentials were used for trust.
        var manager = new PackageManager();
        var operation = manager.AddPackageAsync(new Uri(payload.PackagePath), null, DeploymentOptions.None);
        var result = await operation.AsTask(new Progress<DeploymentProgress>(p => progress.Report($"Installing Kanban Tasker… {p.percentage}%")));
        if (result.ExtendedErrorCode is not null && result.ExtendedErrorCode.HResult != 0)
            throw new InvalidOperationException(result.ErrorText, result.ExtendedErrorCode);
        var installed = manager.FindPackagesForUser("").Any(p => p.Id.Name == PayloadValidation.Identity &&
            p.Id.Publisher == PayloadValidation.Publisher && p.Status.VerifyIsOK() &&
            new Version(p.Id.Version.Major, p.Id.Version.Minor, p.Id.Version.Build, p.Id.Version.Revision) == target);
        if (!installed) throw new InvalidOperationException("Windows did not confirm a successful installation.");
    }
}
