using System.Globalization;
using KanbanTasker.Localization;
using Microsoft.UI.Xaml.Controls;

namespace KanbanTasker.Desktop;

public sealed partial class MainWindow
{
    private TextCatalog text = new("en");
    private record PriorityChoice(string Value, string Name);
    private string T(string key, params object?[] args) => text.Get(key, args);

    private void ApplyLanguage()
    {
        Root.Language = text.Culture.Name;
        CultureInfo.CurrentCulture = text.Culture;
        CultureInfo.CurrentUICulture = text.Culture;
        Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = text.Culture.Name;
        ApplyStaticText();
        ApplyTaskbarPinText();
        if (EditorLoaded) ApplyEditorLanguage();
        ErrorBar.Title = T("Please check");
        if (ErrorBar.IsOpen && lastErrorMessage is not null) ErrorBar.Message = text.TranslateDiagnostic(lastErrorMessage);
        if (TaskPane.IsPaneOpen) { RenderDraftLabels(); RenderTags(); }
        if (loaded) Render();
    }
    private void ApplyEditorLanguage()
    {
        ApplyEditorStaticText();
        // Change labels, keeping the stable data values and every unsaved field intact.
        var priority = (TaskPriority.SelectedItem as PriorityChoice)?.Value ?? "Low";
        TaskPriority.ItemsSource = new[] { "Low", "Medium", "High" }.Select(x => new PriorityChoice(x, T(x))).ToArray();
        TaskPriority.SelectedValue = priority;
        var minutes = (ReminderPicker.SelectedItem as ReminderChoice)?.Minutes;
        ReminderChoices =
        [
            new(null, T("None")), new(0, T("At time of due date")), new(5, T("5 minutes before")),
            new(10, T("10 minutes before")), new(15, T("15 minutes before")), new(60, T("1 hour before")),
            new(120, T("2 hours before")), new(1440, T("1 day before")), new(2880, T("2 days before"))
        ];
        ReminderPicker.ItemsSource = ReminderChoices;
        ReminderPicker.SelectedItem = ReminderChoices.First(x => x.Minutes == minutes);
        StartDate.PlaceholderText = FinishDate.PlaceholderText = T("Choose a date");
    }
    private void RenderDraftLabels()
    {
        EditorHeading.Text = originalTask is null ? T("New task") : T("Edit task");
        CreatedText.Text = originalTask is null ? T("Created when first saved") : T("Created {0:g}", originalTask.CreatedAt.LocalDateTime);
        DaysText.Text = originalTask is null ? "" : T("Days since creation: {0}", (DateTime.Today - originalTask.CreatedAt.LocalDateTime.Date).Days)
            + (originalTask.StartDate is { } start && originalTask.FinishDate is { } finish
                ? "\n" + T("Days worked on: {0}", finish.DayNumber - start.DayNumber) : "");
    }
}
