using KanbanTasker.Distribution;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;

namespace KanbanTasker.Desktop;

public sealed partial class MainWindow
{
    private Func<Uri, Task<bool>> launchStoreUri = async uri => await Windows.System.Launcher.LaunchUriAsync(uri);

    private async Task OpenStoreAsync()
    {
        try
        {
            if (await launchStoreUri(AppDistribution.StoreUri)) return;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // A missing/disabled Store is recoverable. Offer an explicit browser link.
        }
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = T("Microsoft Store could not be opened. You can visit the app's page in your browser."),
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new HyperlinkButton { Content = T("Open Store website"), NavigateUri = AppDistribution.StoreWebUri });
        await Dialog(T("App updates"), panel).ShowAsync();
    }
}
