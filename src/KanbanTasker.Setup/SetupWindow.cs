using KanbanTasker.Updates;

namespace KanbanTasker.Setup;

internal sealed partial class SetupWindow : Form
{
    private readonly Label summary = new() { AutoSize = true, MaximumSize = new Size(550, 0) };
    private readonly TextBox details = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly Button install = new() { Text = "Install", AutoSize = true, Enabled = false };
    private readonly Button close = new() { Text = "Cancel", AutoSize = true };
    private readonly CheckBox desktopShortcut = new() { Text = "Add a desktop shortcut", AutoSize = true, Checked = true };
    private readonly CheckBox startMenu = new() { Text = "Add to Start menu (included automatically)", AutoSize = true, Checked = true, Enabled = false };
    private readonly CheckBox launchApp = new() { Text = "Start after installation", AutoSize = true, Checked = true };
    private readonly CheckBox pinToTaskbar = new() { Text = "Add to taskbar (confirm in the app)", AutoSize = true };
    private readonly FlowLayoutPanel completionOptions = new() { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Enabled = false };
    private Func<CompletionOptions, Task> completeInstallation = ShellIntegration.CompleteAsync;
    private bool installationComplete;
    private SetupPayload? payload;
    private Action? cleanupPayload;
    internal Task CleanupCompletion { get; private set; } = Task.CompletedTask;
    private bool busy;

    public SetupWindow()
    {
        Text = "Kanban Tasker Setup";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new Size(610, 510); MinimumSize = new Size(570, 530);
        StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), RowCount = 5, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var heading = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 16) };
        using var logoStream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("Setup.Logo")!;
        using var logo = Image.FromStream(logoStream);
        heading.Controls.Add(new PictureBox { Image = new Bitmap(logo), Size = new Size(48, 48), SizeMode = PictureBoxSizeMode.Zoom });
        heading.Controls.Add(new Label { Text = "Kanban Tasker", AutoSize = true, Font = new Font(Font.FontFamily, 18, FontStyle.Bold), Margin = new Padding(8, 9, 0, 0) });
        layout.Controls.Add(heading);
        summary.Text = "Checking the included app package…"; summary.Margin = new Padding(0, 0, 0, 16);
        layout.Controls.Add(summary); layout.Controls.Add(details);
        completionOptions.Margin = new Padding(0, 12, 0, 0);
        completionOptions.Controls.AddRange([desktopShortcut, startMenu, launchApp, pinToTaskbar,
            new Label { Text = "Taskbar pinning opens the app. Windows may require manual pinning.", AutoSize = true, MaximumSize = new Size(540, 0) }]);
        layout.Controls.Add(completionOptions);
        pinToTaskbar.CheckedChanged += (_, _) =>
        {
            if (pinToTaskbar.Checked) launchApp.Checked = true;
            launchApp.Enabled = !pinToTaskbar.Checked;
        };
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 16, 0, 0) };
        buttons.Controls.Add(close); buttons.Controls.Add(install); layout.Controls.Add(buttons); Controls.Add(layout);
        AcceptButton = install; CancelButton = close;
        close.Click += async (_, _) => await FinishAsync();
        install.Click += async (_, _) => await InstallAsync();
        FormClosing += (_, e) => e.Cancel = busy;
        FormClosed += (_, _) =>
        {
            // Closing the window must never wait for file deletion/antivirus scanning.
            var cleanup = cleanupPayload; cleanupPayload = null; payload = null;
            CleanupCompletion = cleanup is null ? Task.CompletedTask : Task.Run(cleanup);
        };
#if KANBAN_SETUP_SMOKE_TEST
        Shown += async (_, _) => await RunSmokeTestsAsync();
#else
        Shown += async (_, _) => await PrepareAsync();
#endif
    }

    private async Task PrepareAsync()
    {
        busy = true; close.Enabled = false;
        try
        {
            payload = await Task.Run(SetupPayload.Extract);
            cleanupPayload = payload.Dispose;
            var installed = Installer.InstalledVersion();
            var disposition = UpdateFiles.Compare(installed, UpdateFiles.ParseVersion(payload.Metadata.Version));
            summary.Text = $"Installer: {payload.Metadata.Version} · {payload.Metadata.Architecture}\r\nInstalled: {installed?.ToString() ?? "Not installed"}";
            if (disposition is UpdateDisposition.AlreadyInstalled or UpdateDisposition.OlderVersion)
            {
                details.Text = disposition == UpdateDisposition.AlreadyInstalled
                    ? "This version is already installed. To update, select a Setup.exe or MSIX with a higher version number."
                    : "A newer version is already installed. This older installer will not replace it.";
                close.Text = "Close"; return;
            }
            install.Text = disposition == UpdateDisposition.Update ? "Update" : "Install";
            details.Text = Installer.IsTrusted(payload.Certificate)
                ? "The package is verified. Save any open drafts and close Kanban Tasker before continuing. Your board files and data-file selection are kept."
                : "Windows will ask for administrator approval to trust this app's local signing certificate.\r\n\r\nPublisher: " + payload.Certificate.Subject + "\r\nCertificate: " + payload.Certificate.Thumbprint;
            install.Enabled = true;
            completionOptions.Enabled = true;
        }
        catch (Exception ex) { ShowFailure(ex); }
        finally { busy = false; close.Enabled = true; }
    }

    private async Task InstallAsync()
    {
        if (payload is null) return;
        busy = true; install.Enabled = false; close.Enabled = false; completionOptions.Enabled = false;
        try
        {
            await Installer.InstallAsync(payload, new Progress<string>(message =>
            {
                if (!IsDisposed && !Disposing && busy) details.Text = message;
            }));
            ShowInstalled(payload.Metadata.Version);
        }
        catch (Exception ex) { ShowFailure(ex); install.Enabled = true; install.Text = "Retry"; }
        finally { busy = false; close.Enabled = true; completionOptions.Enabled = true; }
    }

    private void ShowInstalled(string version)
    {
        installationComplete = true;
        summary.Text = $"Kanban Tasker {version} is installed.";
        details.Text = "Choose the options below, then select Finish. The app is already in the Windows Start menu.\r\n\r\nYour existing data-file selection is kept. On first launch, choose Create data file or Open data file.";
        close.Text = "Finish"; install.Visible = false;
        completionOptions.Enabled = true; AcceptButton = close;
        // Only Finish applies options; the title-bar close button skips them.
        CancelButton = null;
    }

    private async Task FinishAsync()
    {
        if (busy) return;
        if (!installationComplete) { Close(); return; }
        busy = true; close.Enabled = false; completionOptions.Enabled = false;
        try
        {
            await completeInstallation(new(desktopShortcut.Checked, launchApp.Checked, pinToTaskbar.Checked));
            busy = false; Close();
        }
        catch (Exception ex)
        {
            summary.Text = "Kanban Tasker is installed; an optional action could not be completed.";
            details.Text = ex.Message + "\r\n\r\nAdjust the options and select Finish to retry, or close this window. You can open the app from Start.";
            busy = false; close.Enabled = true; completionOptions.Enabled = true;
        }
    }

    private void ShowFailure(Exception ex)
    {
        summary.Text = "Installation was not completed.";
        details.Text = (ex.HResult == unchecked((int)0x80073D02)
            ? "Kanban Tasker is still running. Save your draft, close the app and select Retry."
            : ex.Message) + $"\r\n\r\nError: 0x{ex.HResult:X8}\r\nYour board files have not been changed.";
        close.Text = "Close";
    }
}
