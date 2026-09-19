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
