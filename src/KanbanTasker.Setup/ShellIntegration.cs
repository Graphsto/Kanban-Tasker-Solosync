using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Windows.Management.Deployment;

namespace KanbanTasker.Setup;

internal sealed record CompletionOptions(bool DesktopShortcut, bool LaunchApp, bool PinToTaskbar);

internal static class ShellIntegration
{
    internal static Task CompleteAsync(CompletionOptions options)
    {
        if (!options.DesktopShortcut && !options.LaunchApp && !options.PinToTaskbar) return Task.CompletedTask;
        // Shell COM runs in an STA; disk/Explorer delays must not block the installer UI.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var package = new PackageManager().FindPackagesForUser("")
                    .Where(p => p.Id.Name == PayloadValidation.Identity && p.Id.Publisher == PayloadValidation.Publisher && p.Status.VerifyIsOK())
                    .OrderByDescending(p => new Version(p.Id.Version.Major, p.Id.Version.Minor, p.Id.Version.Build, p.Id.Version.Revision))
                    .FirstOrDefault() ?? throw new InvalidOperationException("The installed app could not be found. Open it from the Start menu.");
                var appId = package.Id.FamilyName + "!App";
                if (options.DesktopShortcut)
                    CreateShortcut(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "shell:AppsFolder\\" + appId);
                if (options.LaunchApp || options.PinToTaskbar) Launch(appId, options.PinToTaskbar);
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true, Name = "Kanban setup completion" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task;
    }

    internal static string CreateShortcut(string directory, string shellTarget)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new IOException("Windows did not provide a desktop folder.");
        Directory.CreateDirectory(directory);
        Marshal.ThrowExceptionForHR(SHParseDisplayName(shellTarget, IntPtr.Zero, out var targetId, 0, out _));
        try
        {
            // A shell item targets the stable package app ID, never a versioned WindowsApps
            // path or the temporary installer. Windows supplies the app's registered icon.
            var link = (IShellLink)new ShellLink();
            try
            {
                link.SetIDList(targetId); link.SetDescription("Kanban Tasker (SoloSync)");
                for (var suffix = 0; suffix < 1000; suffix++)
                {
                    var name = suffix == 0 ? "Kanban Tasker (SoloSync).lnk" : $"Kanban Tasker (SoloSync) ({suffix}).lnk";
                    var destination = Path.Combine(directory, name);
                    if (File.Exists(destination))
                    {
                        if (HasTarget(destination, targetId)) return destination;
                        continue; // Preserve shortcuts belonging to the original app or the user.
                    }
                    var temporary = Path.Combine(directory, ".kanban-shortcut-" + Guid.NewGuid().ToString("N") + ".lnk");
                    try
                    {
                        ((IPersistFile)link).Save(temporary, true);
                        File.Move(temporary, destination, overwrite: false);
                        return destination;
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                throw new IOException("A free desktop shortcut name could not be found.");
            }
            finally { Marshal.FinalReleaseComObject(link); }
        }
        finally { Marshal.FreeCoTaskMem(targetId); }
    }

    private static bool HasTarget(string path, IntPtr expected)
    {
        var link = (IShellLink)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(path, 0);
            link.GetIDList(out var target);
            try { return target != IntPtr.Zero && ILIsEqual(target, expected); }
            finally { Marshal.FreeCoTaskMem(target); }
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException) { return false; }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    private static void Launch(string appId, bool pinToTaskbar)
    {
        var manager = (IApplicationActivationManager)new ApplicationActivationManager();
        try { manager.ActivateApplication(appId, pinToTaskbar ? "--pin-to-taskbar" : "", 0, out _); }
        finally { Marshal.FinalReleaseComObject(manager); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr itemIdList, uint attributes, out uint attributesOut);
    [DllImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ILIsEqual(IntPtr first, IntPtr second);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLink
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int length, IntPtr findData, uint flags);
        void GetIDList(out IntPtr itemIdList);
        void SetIDList(IntPtr itemIdList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int length);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
    }
    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager { }
    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        void ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
    }
}
