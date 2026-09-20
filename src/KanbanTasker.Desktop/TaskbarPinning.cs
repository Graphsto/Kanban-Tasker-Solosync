using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.UI.Shell;

namespace KanbanTasker.Desktop;

internal enum TaskbarPinState { Available, Pinned, Manual }

internal static class TaskbarPinning
{
    internal static bool IsRequested(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return false;
        // Activation redirection can include the executable as well as its arguments.
        var argv = CommandLineToArgvW("KanbanTasker.exe " + arguments, out var count);
        if (argv == IntPtr.Zero) return false;
        try
        {
            for (var i = 1; i < count; i++)
                if (Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) == "--pin-to-taskbar") return true;
            return false;
        }
        finally { LocalFree(argv); }
    }

    internal static async Task<TaskbarPinState> GetStateAsync()
    {
        try
        {
            if (!SupportsDesktopPinning()) return TaskbarPinState.Manual;
            var manager = TaskbarManager.GetDefault();
            if (await manager.IsCurrentAppPinnedAsync()) return TaskbarPinState.Pinned;
            return manager.IsPinningAllowed ? TaskbarPinState.Available : TaskbarPinState.Manual;
        }
        catch (Exception ex) when (IsUnavailable(ex)) { return TaskbarPinState.Manual; }
    }

    internal static async Task<bool> RequestAsync()
    {
        try
        {
            if (!SupportsDesktopPinning()) return false;
            var manager = TaskbarManager.GetDefault();
            return manager.IsPinningAllowed && await manager.RequestPinCurrentAppAsync();
        }
        catch (Exception ex) when (IsUnavailable(ex)) { return false; }
    }

    private static bool SupportsDesktopPinning()
    {
        // Official desktop-support marker; API presence alone also matches unsupported
        // Windows 10 UWP implementations. Never request/forge a restricted access token.
        const string className = "Windows.UI.Shell.TaskbarManager";
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, (uint)className.Length, out var name));
        try
        {
            var iid = new Guid("CDFEFD63-E879-4134-B9A7-8283F05F9480");
            var result = RoGetActivationFactory(name, ref iid, out var factory);
            if (factory != IntPtr.Zero) Marshal.Release(factory);
            if (result < 0) return false;
        }
        finally { WindowsDeleteString(name); }
        // Microsoft removed this restriction in newer Windows 11 updates. Older
        // versions need an app-specific LAF approval, so use manual instructions.
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModel\LimitedAccessFeatures\com.microsoft.windows.taskbar.pin");
        return key?.GetValue("4096B239A7295B635C090E647E867B5707DA6AB6CB78340B01FE4E0C8F4953D4") is not int value || value == 0;
    }
    private static bool IsUnavailable(Exception ex) => ex is COMException or InvalidOperationException
        or NotSupportedException or UnauthorizedAccessException or System.Security.SecurityException or IOException;

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string value, uint length, out IntPtr result);
    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr value);
    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr name, ref Guid iid, out IntPtr factory);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
