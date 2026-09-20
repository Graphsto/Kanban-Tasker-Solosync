using System.Diagnostics;
using System.Text.Json;

namespace KanbanTasker.Setup;

internal sealed partial class SetupWindow
{
    private async Task RunSmokeTestsAsync()
    {
        var checks = new List<string>();
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        try
        {
            await Task.Delay(200);
            Check(!completionOptions.Enabled && desktopShortcut.Checked && launchApp.Checked && !pinToTaskbar.Checked,
                "Optional actions are unavailable before package verification and pinning is opt-in");
            Check(startMenu.Checked && !startMenu.Enabled, "Start menu registration is shown as included, without a duplicate shortcut option");
            using (var cancelled = new SetupWindow())
            {
                var ran = false;
                cancelled.completeInstallation = _ => { ran = true; return Task.CompletedTask; };
                await cancelled.FinishAsync();
                Check(!ran, "Closing before successful installation never creates shortcuts or launches the app");
            }
            var shortcuts = Path.Combine(AppContext.BaseDirectory, "shortcut-tests");
            Directory.CreateDirectory(shortcuts);
            var occupied = Path.Combine(shortcuts, "Kanban Tasker (SoloSync).lnk");
            File.WriteAllText(occupied, "An unrelated user file");
            var package = new Windows.Management.Deployment.PackageManager().FindPackagesForUser("")
                .FirstOrDefault(p => p.Id.Name == PayloadValidation.Identity && p.Id.Publisher == PayloadValidation.Publisher);
            var shellTarget = package is null ? "shell:AppsFolder" : "shell:AppsFolder\\" + package.Id.FamilyName + "!App";
            var shortcut = ShellIntegration.CreateShortcut(shortcuts, shellTarget);
            Check(File.Exists(shortcut) && shortcut != occupied && File.ReadAllText(occupied) == "An unrelated user file",
                "Real Shell shortcut creation preserves an occupied desktop name");
            Check(ShellIntegration.CreateShortcut(shortcuts, shellTarget) == shortcut && Directory.GetFiles(shortcuts, "*.lnk").Length == 2,
                "Repeated setup reuses the shortcut after verifying its actual Shell target");
            Check(!Directory.GetFiles(shortcuts, ".kanban-shortcut-*").Any(), "Shortcut writing leaves no temporary files");
            await ShellIntegration.CompleteAsync(new(false, false, false));
            Check(true, "All optional actions can be disabled without requiring an installed app");
            ShowInstalled("2.4.1.4");
            Check(installationComplete && completionOptions.Enabled && !install.Visible && AcceptButton == close && CancelButton is null,
                "Successful installation presents Finish and optional actions without a reinstall action");
            launchApp.Checked = false; pinToTaskbar.Checked = true;
            Check(launchApp.Checked && !launchApp.Enabled, "Selecting taskbar pinning explicitly requires opening the installed app");
            pinToTaskbar.Checked = false;
            Check(launchApp.Enabled, "Deselecting pinning makes app launch optional again");
            using (var bitmap = new Bitmap(Width, Height))
            {
                DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));
                bitmap.Save(Path.Combine(AppContext.BaseDirectory, "completion-options.png"));
            }
            CompletionOptions? received = null;
            completeInstallation = options => { received = options; return Task.FromException(new IOException("Test: desktop unavailable")); };
            desktopShortcut.Checked = true; launchApp.Checked = false;
            await FinishAsync();
            Check(received == new CompletionOptions(true, false, false), "Finish passes the chosen desktop/launch/pinning options unchanged");
            Check(Visible && !busy && close.Enabled && completionOptions.Enabled && installationComplete && !install.Visible
                && summary.Text.Contains("is installed") && details.Text.Contains("desktop unavailable"),
                "Optional action failure keeps successful installation and allows adjusting options or closing");
            var pendingAction = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            completeInstallation = _ => { calls++; return pendingAction.Task; };
            close.PerformClick();
            await Task.Delay(100);
            Check(busy && Visible && !close.Enabled && !completionOptions.Enabled, "Slow optional actions yield to the installer UI");
            await FinishAsync();
            Check(calls == 1, "Repeated Finish cannot start the same optional actions twice");
            pendingAction.SetException(new IOException("Test: retry"));
            for (var i = 0; busy && i < 100; i++) await Task.Delay(10);
            Check(!busy && close.Enabled, "A failed asynchronous completion can be retried");
            completeInstallation = _ => Task.CompletedTask;
            // Simulate a file deletion blocked by a scanner, using the same close path.
            var uiThread = Environment.CurrentManagedThreadId;
            var cleanupThread = uiThread;
            cleanupPayload = () =>
            {
                cleanupThread = Environment.CurrentManagedThreadId;
                started.Set(); release.Wait(TimeSpan.FromSeconds(10));
            };
            busy = true; close.PerformClick();
            Check(Visible && !IsDisposed, "Installer cannot close during deployment");
            busy = false; close.Text = "Finish"; AcceptButton = close;
            var elapsed = Stopwatch.StartNew();
            close.PerformClick(); elapsed.Stop();
            Check(!Visible && elapsed.Elapsed < TimeSpan.FromSeconds(1), "Finish closes the native window immediately while cleanup is blocked");
            Check(started.Wait(TimeSpan.FromSeconds(2)) && cleanupThread != uiThread && !CleanupCompletion.IsCompleted,
                "Cleanup runs independently of the closed UI");
            release.Set(); Check(CleanupCompletion.Wait(TimeSpan.FromSeconds(2)), "Cleanup can finish after the window closes");
            Write(new { success = true, checks });
        }
        catch (Exception ex) { Write(new { success = false, error = ex.ToString(), checks }); }
        finally { release.Set(); busy = false; if (!IsDisposed) Close(); }
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException(name);
            checks.Add(name);
        }
        static void Write(object value) => File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "result.json"),
            JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
}
