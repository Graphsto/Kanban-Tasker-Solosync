using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KanbanTasker.Desktop;

public sealed partial class MainWindow
{
    private Func<Task<TaskbarPinState>> getTaskbarPinState = TaskbarPinning.GetStateAsync;
    private Func<Task<bool>> requestTaskbarPin = TaskbarPinning.RequestAsync;
    private TaskbarPinState taskbarPinState;
    private bool taskbarPinPending, taskbarPinBusy;

    internal void OfferTaskbarPinning()
    {
        taskbarPinPending = true;
        if (rootLoaded) _ = ShowTaskbarPinOfferAsync();
    }
    private async Task ShowTaskbarPinOfferAsync()
    {
        if (!taskbarPinPending || taskbarPinBusy || closed) return;
        taskbarPinPending = false; taskbarPinBusy = true;
        try
        {
            taskbarPinState = await getTaskbarPinState();
            if (closed) return;
            ApplyTaskbarPinText();
            TaskbarPinNotice.IsOpen = taskbarPinState != TaskbarPinState.Pinned;
        }
        finally { taskbarPinBusy = false; }
    }
    private async void PinToTaskbar_Click(object sender, RoutedEventArgs e)
    {
        if (taskbarPinBusy || closed) return;
        taskbarPinBusy = true; PinToTaskbarButton.IsEnabled = false;
        try
        {
            // Only this explicit click in the foreground app invokes Windows' prompt.
            taskbarPinState = await requestTaskbarPin() ? TaskbarPinState.Pinned : TaskbarPinState.Manual;
            if (!closed) ApplyTaskbarPinText();
        }
        finally { taskbarPinBusy = false; if (!closed) PinToTaskbarButton.IsEnabled = true; }
    }
    private void ApplyTaskbarPinText()
    {
        TaskbarPinNotice.Title = T("Pin to taskbar");
        TaskbarPinNotice.Message = taskbarPinState switch
        {
            TaskbarPinState.Pinned => T("Kanban Tasker is pinned to your taskbar."),
            TaskbarPinState.Manual => T("To pin the app manually, right-click the Kanban Tasker icon on the taskbar and choose Pin to taskbar."),
            _ => T("Keep Kanban Tasker on your taskbar. Windows will ask you to confirm.")
        };
        TaskbarPinNotice.Severity = taskbarPinState == TaskbarPinState.Pinned ? InfoBarSeverity.Success : InfoBarSeverity.Informational;
        PinToTaskbarButton.Content = T("Pin to taskbar");
        PinToTaskbarButton.Visibility = taskbarPinState == TaskbarPinState.Available ? Visibility.Visible : Visibility.Collapsed;
    }
}
