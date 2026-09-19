namespace KanbanTasker.Setup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.SequenceEqual(new[] { "--trust-certificate" }))
            {
                Installer.TrustEmbeddedCertificate(); return 0;
            }
            if (args.SequenceEqual(new[] { "--verify-only" }))
            {
                using var payload = SetupPayload.Extract();
                Console.WriteLine($"Verified Kanban Tasker {payload.Metadata.Version} ({payload.Metadata.Architecture}); certificate {payload.Certificate.Thumbprint}; trusted: {Installer.IsTrusted(payload.Certificate)}");
                return 0;
            }
            if (args.Length == 2 && args[0] == "--inspect-update")
            {
                var metadata = SetupPayload.ReadMetadata();
                using var certificate = SetupPayload.ReadCertificate(metadata);
                using var fileLock = new FileStream(args[1], FileMode.Open, FileAccess.Read, FileShare.Read);
                KanbanTasker.Updates.WindowsSignature.Verify(args[1], certificate);
                var update = KanbanTasker.Updates.UpdateFiles.Read(args[1], metadata.Architecture);
                Console.WriteLine($"Verified update: {update.Version} ({update.Architecture}); setup: {update.IsSetup}");
                return 0;
            }
            if (args.Length != 0) throw new ArgumentException("Unknown installer option.");
            ApplicationConfiguration.Initialize();
            using var window = new SetupWindow();
            Application.Run(window);
            // The native window is already gone. Cleanup is best-effort and must not
            // keep Setup alive indefinitely if the temporary file is busy or slow.
            try { window.CleanupCompletion.Wait(TimeSpan.FromSeconds(2)); }
            catch (AggregateException) { }
            return 0;
        }
        catch (Exception ex)
        {
            if (args.Contains("--verify-only") || args.Contains("--inspect-update")) Console.Error.WriteLine(ex.Message);
            else MessageBox.Show(ex.Message, "Kanban Tasker Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
