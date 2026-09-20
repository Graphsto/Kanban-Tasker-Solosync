using KanbanTasker.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KanbanTasker.Desktop;

public sealed partial class MainWindow
{
    private record ThemeChoice(string Value, string Name);
    private ThemeChoice[] ThemeChoices() =>
    [
        new("system", T("Use system setting")), new("dark", T("Dark")), new("light", T("Light")),
        new("lightBlue", T("Light blue")), new("darkBlue", T("Dark blue"))
    ];

    private async void Settings_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var general = new StackPanel { Spacing = 16, Padding = new(0,8,12,12) };
        var appearance = new StackPanel { Spacing = 16, Padding = new(0,8,12,12) };
        var advanced = new StackPanel { Spacing = 16, Padding = new(0,8,12,12) };
        var tabs = new Pivot { Name = "SettingsTabs", MaxWidth = 460, MinWidth = 320, Height = Math.Clamp(Root.ActualHeight - 240, 240, 520) };
        ScrollViewer Page(StackPanel panel) => new()
        {
            Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled
        };
        var generalTab = new PivotItem { Content = Page(general) };
        var appearanceTab = new PivotItem { Content = Page(appearance) };
        var advancedTab = new PivotItem { Content = Page(advanced) };
        tabs.Items.Add(generalTab); tabs.Items.Add(appearanceTab); tabs.Items.Add(advancedTab); tabs.SelectedIndex = 0;
        bool? create = null;
        var updateSelected = false;
        var dialog = Dialog(T("Settings"), tabs);
        settingsDialog = dialog;
        var advancedBusy = false;
        Action? refreshGroups = null;
        dialog.Closing += (_, args) => args.Cancel = advancedBusy;
        void Populate()
        {
            general.Children.Clear(); appearance.Children.Clear(); advanced.Children.Clear();
            dialog.Title = T("Settings"); dialog.CloseButtonText = T("Close"); dialog.Language = text.Culture.Name;
            generalTab.Header = T("General"); appearanceTab.Header = T("Appearance");
            advancedTab.Header = T("Advanced");
            refreshGroups = PopulateAdvancedSettings(advanced, busy => { advancedBusy = busy; dialog.IsEnabled = !busy; });
            ApplyDialogAppearance(dialog);
            void Heading(StackPanel panel, string label) => panel.Children.Add(new TextBlock
                { Text = label, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
            void Paragraph(StackPanel panel, string message) => panel.Children.Add(new TextBlock
                { Text = message, TextWrapping = TextWrapping.Wrap });

            Heading(general, T("Data file"));
            general.Children.Add(new TextBlock { Text = store.FilePath ?? T("No data file selected."),
                IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap });
            Paragraph(general, text.TranslateDiagnostic(store.Status.Message));
            Paragraph(general, T("The app saves locally after each change. Nextcloud or another sync app transfers the file. Keep it available offline on every device. Conflicting edits to the same field use the latest change."));
            var open = new Button { Content = T("Open data file") };
            var fresh = new Button { Content = T("Create data file") };
            open.Click += (_, _) => { create = false; dialog.Hide(); };
            fresh.Click += (_, _) => { create = true; dialog.Hide(); };
            general.Children.Add(open); general.Children.Add(fresh);
            Heading(general, T("App updates"));
            general.Children.Add(new TextBlock { Text = T("Installed version: {0}", CurrentVersion), IsTextSelectionEnabled = true });
            Paragraph(general, T("Select a newer Kanban Tasker Setup.exe or MSIX file stored on this PC."));
            var update = new Button { Content = T("Install update from file…"), IsEnabled = CanSelectUpdate };
            update.Click += (_, _) => { updateSelected = true; dialog.Hide(); }; general.Children.Add(update);
            if (!CanSelectUpdate) Paragraph(general, T("Local updates are available in the installed app."));
            general.Children.Add(new TextBlock
            {
                Text = T("Based on Kanban Tasker by Hunter Johnson. MIT license.\nLocal file edition · No account required."),
                TextWrapping = TextWrapping.Wrap, FontSize = 12
            });

            var language = new ComboBox
            {
                Name = "LanguagePicker", Header = T("Language"), ItemsSource = TextCatalog.Languages, DisplayMemberPath = "Name",
                SelectedItem = TextCatalog.Languages.First(x => x.Code == text.Language), HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var themeChoices = ThemeChoices();
            var theme = new ComboBox
            {
                Name = "ThemePicker", Header = T("Theme"), ItemsSource = themeChoices, DisplayMemberPath = "Name",
                SelectedItem = themeChoices.First(x => x.Value == AppearanceColors.Normalize(preferences.Theme)),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            language.SelectionChanged += (_, _) =>
            {
                if (language.SelectedItem is not AppLanguage selected || selected.Code == text.Language) return;
                var previous = preferences.Language;
                try
                {
                    preferences.Language = selected.Code; preferences.Save();
                    text = new(selected.Code); ApplyLanguage(); Populate();
                    ((ComboBox)appearance.Children[0]).Focus(FocusState.Programmatic);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    preferences.Language = previous;
                    language.SelectedItem = TextCatalog.Languages.First(x => x.Code == text.Language);
                    error.Text = text.TranslateDiagnostic(ex.Message);
                }
            };
            theme.SelectionChanged += (_, _) =>
            {
                if (theme.SelectedItem is not ThemeChoice selected || selected.Value == AppearanceColors.Normalize(preferences.Theme)) return;
                var previous = preferences.Theme;
                try
                {
                    preferences.Theme = selected.Value; preferences.Save();
                    ApplyAppearance(); error.Text = "";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    preferences.Theme = previous;
                    theme.SelectedItem = themeChoices.First(x => x.Value == AppearanceColors.Normalize(previous));
                    error.Text = text.TranslateDiagnostic(ex.Message);
                }
            };
            appearance.Children.Add(language);
            Paragraph(appearance, T("Applies immediately on this device. Board names and task content stay unchanged."));
            appearance.Children.Add(theme);
            Paragraph(appearance, T("Use system setting follows Windows light or dark mode automatically."));
            appearance.Children.Add(error);
        }
        Populate();
        EventHandler groupsChanged = (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (settingsDialog == dialog) refreshGroups?.Invoke();
        });
        store.Changed += groupsChanged;
        try { await dialog.ShowAsync(); }
        finally { store.Changed -= groupsChanged; settingsDialog = null; }
        if (create is { } newFile) await SelectFileAsync(newFile);
        else if (updateSelected) await SelectUpdateAsync();
    });
}
