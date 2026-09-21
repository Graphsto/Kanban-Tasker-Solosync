using KanbanTasker.Distribution;
using KanbanTasker.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;

namespace KanbanTasker.Desktop;

public sealed partial class MainWindow
{
    private async Task CheckDistributionUiAsync(Action<bool, string> check, Action<Microsoft.UI.Xaml.Controls.Primitives.ButtonBase> invoke)
    {
        var languageBefore = text.Language;
        var titleBefore = TaskTitle.Text;
        var descriptionBefore = TaskDescription.Text;
        var launcherBefore = launchStoreUri;
        var requests = new List<Uri>();
        ContentDialog OpenDialog() => VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot)
            .Select(p => FindVisual<ContentDialog>(p.Child)).First(x => x is not null)!;
        try
        {
            check(CurrentVersion == AppDistribution.ProductVersion, "The UI shows the product version, independently of Store package numbering");
            if (AppDistribution.IsStore) check(!CanSelectUpdate, "Store builds cannot select a local installer");
            foreach (var language in TextCatalog.Languages)
            {
                text = new(language.Code); ApplyLanguage();
                Settings_Click(this, new RoutedEventArgs()); await SettleAsync();
                var settings = OpenDialog();
                var tabs = (Pivot)settings.Content;
                var general = (StackPanel)((ScrollViewer)((PivotItem)tabs.Items[0]).Content).Content;
                var update = general.Children.OfType<Button>().Single(x => x.Name == "AppUpdateButton");
                check(update.Content.ToString() == T(AppDistribution.IsStore ? "Update in Microsoft Store" : "Install update from file…"),
                    "Update action matches installation channel and language: " + language.Code);
                check(general.Children.OfType<HyperlinkButton>().Any(x => x.NavigateUri == AppDistribution.PrivacyUri && x.Content.ToString() == T("Privacy policy")),
                    "Public privacy link is translated: " + language.Code);
                settings.Hide(); await WaitForAsync(() => !working);
            }
            if (AppDistribution.IsStore)
            {
                launchStoreUri = uri => { requests.Add(uri); return Task.FromResult(true); };
                Settings_Click(this, new RoutedEventArgs()); await SettleAsync();
                var settings = OpenDialog();
                invoke(FindVisual<Button>(settings, "AppUpdateButton")!);
                await WaitForAsync(() => requests.Count == 1);
                await WaitForAsync(() => !working);
                check(requests[0] == AppDistribution.StoreUri && !closed && TaskPane.IsPaneOpen && HasDraftChanges,
                    "The real Store button opens only the correct product page and keeps an unsaved draft");
                foreach (var failure in new[] { "unavailable", "exception" })
                {
                    launchStoreUri = _ => failure == "exception" ? throw new COMException("Store unavailable") : Task.FromResult(false);
                    var opening = OpenStoreAsync(); await SettleAsync();
                    var fallback = OpenDialog();
                    check(((StackPanel)fallback.Content).Children.OfType<HyperlinkButton>().Single().NavigateUri == AppDistribution.StoreWebUri,
                        "Store failure offers the correct explicit browser fallback: " + failure);
                    fallback.Hide(); await opening;
                }
            }
            check(TaskTitle.Text == titleBefore && TaskDescription.Text == descriptionBefore && HasDraftChanges,
                "Update/settings navigation never discards or saves a task draft");
        }
        finally { launchStoreUri = launcherBefore; text = new(languageBefore); ApplyLanguage(); }
    }
}
