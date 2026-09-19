using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using KanbanTasker.Updates;
using Windows.ApplicationModel;
using Windows.Storage.Pickers;

namespace KanbanTasker.Desktop;

public sealed partial class MainWindow
{
    private static Version CurrentVersion
    {
        get
        {
            try { var v = Package.Current.Id.Version; return new(v.Major, v.Minor, v.Build, v.Revision); }
            catch (Exception ex) when (ex is InvalidOperationException or COMException) { return Assembly.GetExecutingAssembly().GetName().Version!; }
        }
    }
    private static bool CanSelectUpdate => Assembly.GetExecutingAssembly().GetManifestResourceInfo("KanbanTasker.PublisherCertificate") is not null;
    private static string UpdateCache
    {
        get
        {
            // Use the physical package path so the external installer can find the staged file.
            try { return Path.Combine(Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path, "Updates"); }
            catch (Exception ex) when (ex is InvalidOperationException or COMException)
            { return Path.Combine(LocalPreferences.DirectoryPath, "Updates"); }
        }
    }

    private async Task SelectUpdateAsync()
    {
        string? stage = null;
        var launched = false;
        try
        {
            if (!CanSelectUpdate) throw new InvalidOperationException("Install the packaged app to use local updates.");
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
            picker.FileTypeFilter.Add(".exe"); picker.FileTypeFilter.Add(".msix");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            var extension = Path.GetExtension(file.Path).ToLowerInvariant();
            if (extension is not (".exe" or ".msix")) throw new InvalidDataException("Select a Setup.exe or MSIX file.");
            stage = Path.Combine(UpdateCache, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            var path = Path.Combine(stage, extension == ".exe" ? "Setup.exe" : "Update.msix");
            StatusText.Text = T("Checking update…");
            // Work from a stable local copy; File.Copy also preserves Windows download-zone streams.
            await Task.Run(() => File.Copy(file.Path, path));
            using var fileLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var candidate = await Task.Run(() =>
            {
                using var certificateStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("KanbanTasker.PublisherCertificate")!;
                using var bytes = new MemoryStream(); certificateStream.CopyTo(bytes);
                using var certificate = X509CertificateLoader.LoadCertificate(bytes.ToArray());
                WindowsSignature.Verify(path, certificate);
                var architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
                return UpdateFiles.Read(path, architecture);
            });
            var disposition = UpdateFiles.Compare(CurrentVersion, candidate.Version);
            if (disposition is UpdateDisposition.AlreadyInstalled or UpdateDisposition.OlderVersion)
            {
                await Dialog(T("No newer version"), new Microsoft.UI.Xaml.Controls.TextBlock
                {
                    Text = T("Installed: {0}\nSelected: {1}\n\nChoose a file with a higher version number.", CurrentVersion, candidate.Version),
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
                }).ShowAsync();
                return;
            }
            if (!await ConfirmAsync(T("Update Kanban Tasker?"), T("Installed: {0}\nUpdate: {1}\n\nKanban Tasker will close and open the selected installer. Your saved boards and data-file selection are kept.", CurrentVersion, candidate.Version), T("Open update installer"))) return;
            if (!await CanDiscardDraftAsync()) return;
            preferences.SelectedBoard = boardId; preferences.Save();
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            launched = true;
            allowClose = true; Close();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or
            UnauthorizedAccessException or CryptographicException or System.ComponentModel.Win32Exception or COMException or System.Xml.XmlException)
        { ShowError(ex.Message); }
        finally
        {
            if (!launched && stage is not null) DeleteUpdateStage(stage);
            if (!closed) Render();
        }
    }

    private static void CleanUpdateCache(DateTime olderThanUtc)
    {
        try
        {
            if (!Directory.Exists(UpdateCache)) return;
            foreach (var directory in Directory.EnumerateDirectories(UpdateCache))
                // A new update can be selected while this background cleanup runs.
                // Only previous sessions' stages are eligible for removal.
                if (Guid.TryParseExact(Path.GetFileName(directory), "N", out _)
                    && Directory.GetCreationTimeUtc(directory) < olderThanUtc) DeleteUpdateStage(directory);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void DeleteUpdateStage(string directory)
    {
        try
        {
            File.Delete(Path.Combine(directory, "Setup.exe")); File.Delete(Path.Combine(directory, "Update.msix"));
            Directory.Delete(directory); // Never recurse into or remove arbitrary user files.
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
