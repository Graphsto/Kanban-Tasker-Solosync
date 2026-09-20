using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace KanbanTasker.Desktop;

public partial class App : Application
{
    private MainWindow? window;
#if !KANBAN_UI_SMOKE_TEST
    private AppInstance? instance;
#endif
    public App()
    {
#if KANBAN_UI_SMOKE_TEST
        SmokeProfile.Trace("App constructor");
        UnhandledException += (_, e) => SmokeProfile.RecordStartupFailure(e.Exception);
#endif
        InitializeComponent();
#if KANBAN_UI_SMOKE_TEST
        SmokeProfile.TrackFirstFrame();
#endif
    }
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
#if KANBAN_UI_SMOKE_TEST
        SmokeProfile.Trace("OnLaunched");
#endif
        Directory.CreateDirectory(LocalPreferences.DirectoryPath);
#if KANBAN_UI_SMOKE_TEST
        // The control test is intentionally independent of single-instance activation.
        try
        {
            SmokeProfile.Trace("Constructing main window");
            window = new MainWindow();
        }
        catch (Exception ex) { SmokeProfile.RecordStartupFailure(ex); Exit(); }
        await Task.CompletedTask;
#else
        instance = AppInstance.FindOrRegisterForKey(
            "KanbanTasker.Revived");
        if (!instance.IsCurrent)
        {
            await instance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
            Exit();
            return;
        }
        window = new MainWindow();
        instance.Activated += (_, activation) => window.DispatcherQueue.TryEnqueue(() =>
        {
            window.ActivateWhenReady();
            if (activation.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch
                && TaskbarPinning.IsRequested(launch.Arguments)) window.OfferTaskbarPinning();
        });
        if (Environment.GetCommandLineArgs().Skip(1).Contains("--pin-to-taskbar")
            || AppInstance.GetCurrent().GetActivatedEventArgs().Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs initial
                && TaskbarPinning.IsRequested(initial.Arguments)) window.OfferTaskbarPinning();
#endif
    }
}
