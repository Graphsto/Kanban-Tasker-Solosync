using KanbanTasker.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KanbanTasker.Desktop;

public sealed partial class MainWindow
{
    private bool closeEditorRequested;
    private bool taskSaveFailed;
    private TaskEditorView? taskEditor;
    private bool EditorLoaded => taskEditor is not null;
    private Grid EditorSurface => taskEditor?.EditorSurface!;
    private TextBlock EditorHeading => taskEditor?.EditorHeading!;
    private InfoBar DraftNotice => taskEditor?.DraftNotice!;
    private TextBox TaskTitle => taskEditor?.TaskTitle!;
    private TextBox TaskDescription => taskEditor?.TaskDescription!;
    private ComboBox TaskColumn => taskEditor?.TaskColumn!;
    private ComboBox TaskPriority => taskEditor?.TaskPriority!;
    private AutoSuggestBox TagInput => taskEditor?.TagInput!;
    private StackPanel TagList => taskEditor?.TagList!;
    private Expander DateInformation => taskEditor?.DateInformation!;
    private TextBlock CreatedText => taskEditor?.CreatedText!;
    private CalendarDatePicker DueDate => taskEditor?.DueDate!;
    private Button ClearDueDateButton => taskEditor?.ClearDueDateButton!;
    private TimePicker DueTime => taskEditor?.DueTime!;
    private Button ClearDueTimeButton => taskEditor?.ClearDueTimeButton!;
    private ComboBox ReminderPicker => taskEditor?.ReminderPicker!;
    private CalendarDatePicker StartDate => taskEditor?.StartDate!;
    private Button ClearStartDateButton => taskEditor?.ClearStartDateButton!;
    private CalendarDatePicker FinishDate => taskEditor?.FinishDate!;
    private Button ClearFinishDateButton => taskEditor?.ClearFinishDateButton!;
    private TextBlock DaysText => taskEditor?.DaysText!;
    private Button SaveTaskButton => taskEditor?.SaveTaskButton!;
    private Button CancelTaskButton => taskEditor?.CancelTaskButton!;
    private Button DeleteTaskButton => taskEditor?.DeleteTaskButton!;
    private void EnsureEditorLoaded()
    {
        if (EditorLoaded) return;
        // Construct date/time controls only when the user first opens a task.
        taskEditor = new TaskEditorView();
        EditorHost.Content = taskEditor;
        TagInput.QuerySubmitted += TagInput_QuerySubmitted;
        TagInput.TextChanged += TagInput_TextChanged;
        ClearDueDateButton.Click += ClearDueDate_Click;
        ClearDueTimeButton.Click += ClearDueTime_Click;
        ClearStartDateButton.Click += ClearStartDate_Click;
        ClearFinishDateButton.Click += ClearFinishDate_Click;
        SaveTaskButton.Click += SaveTask_Click;
        CancelTaskButton.Click += CancelTask_Click;
        DeleteTaskButton.Click += DeleteTask_Click;
        ApplyEditorLanguage();
        EditorSurface.Background = Brush("LayerFillColorDefaultBrush");
    }
    private TaskData? initialDraft;
    private bool HasDraftChanges => TaskPane.IsPaneOpen && initialDraft is not null
        && (!TaskDataEqual(ReadTaskDraft(), initialDraft) || TagInput.Text.Length > 0);
    private async Task OpenEditorAsync(Guid? id, Guid columnId)
    {
        if (document is null || boardId is null || !await CanDiscardDraftAsync()) return;
        EnsureEditorLoaded();
        taskSaveFailed = false;
        originalTask = id is null ? null : TaskData.From(document.Tasks.Single(x => x.Id == id));
        var data = originalTask ?? new TaskData { BoardId = boardId.Value, ColumnId = columnId };
        draftColumnId = columnId; draftBoardId = data.BoardId;
        TaskTitle.Text = data.Title; TaskDescription.Text = data.Description;
        TaskPriority.SelectedValue = data.Priority;
        draftTags.Clear(); draftTags.AddRange(data.Tags); RenderTags(); TagInput.Text = "";
        DueDate.Date = ToOffset(data.DueDate); DueTime.SelectedTime = data.DueTime?.ToTimeSpan();
        StartDate.Date = ToOffset(data.StartDate); FinishDate.Date = ToOffset(data.FinishDate);
        ReminderPicker.SelectedItem = ReminderChoices.FirstOrDefault(x => x.Minutes == data.ReminderMinutes) ?? ReminderChoices[0];
        RenderDraftLabels();
        DeleteTaskButton.Visibility = originalTask is null ? Visibility.Collapsed : Visibility.Visible;
        closeEditorRequested = false;
        TaskPane.IsPaneOpen = true; DraftNotice.IsOpen = false;
        TaskColumn.SelectedItem = null;
        RefreshDraftContext();
        // Capture the presented values, including any normalization by date/time controls.
        // Remote updates and changes of language/theme must never replace this baseline.
        initialDraft = ReadTaskDraft();
        TaskTitle.Focus(FocusState.Programmatic);
    }
    private void RefreshDraftContext()
    {
        if (document is null) return;
        var selected = (TaskColumn.SelectedItem as Choice)?.Id ?? originalTask?.ColumnId ?? draftColumnId;
        var choices = WorkspaceView.Columns(document, draftBoardId).Select(x => new Choice(x.Id, x.Get<string>(Fields.Name))).ToList();
        if (!choices.Any(x => x.Id == selected)) choices.Add(new(selected, T("Column no longer available")));
        TaskColumn.ItemsSource = choices; TaskColumn.SelectedItem = choices.FirstOrDefault(x => x.Id == selected);
        if (taskSaveFailed)
        {
            DraftNotice.IsOpen = true; DraftNotice.Severity = InfoBarSeverity.Warning;
            DraftNotice.Message = T("Your last save did not complete. Your draft is still here. Select Save to try again.");
            return;
        }
        DraftNotice.Severity = InfoBarSeverity.Informational;
        if (originalTask is not null)
        {
            var current = WorkspaceView.AllTasks(document).FirstOrDefault(x => x.Id == originalTask.Id);
            DraftNotice.IsOpen = current is null || !TaskDataEqual(TaskData.From(current), originalTask);
            DraftNotice.Message = current is null
                ? T("This task or its column was deleted on another device. Your draft is kept here, but cannot overwrite the deletion.")
                : T("This task changed on another device. Saving updates only the fields you changed.");
        }
    }
    private static bool TaskDataEqual(TaskData a, TaskData b) =>
        System.Text.Json.JsonSerializer.Serialize(a) == System.Text.Json.JsonSerializer.Serialize(b);
    private TaskData ReadTaskDraft() => new()
        {
            Id = originalTask?.Id ?? Guid.Empty, BoardId = draftBoardId, ColumnId = (TaskColumn.SelectedItem as Choice)?.Id ?? draftColumnId,
            Title = TaskTitle.Text, Description = TaskDescription.Text, Priority = (TaskPriority.SelectedItem as PriorityChoice)?.Value ?? "Low",
            Tags = draftTags.ToArray(), CreatedAt = originalTask?.CreatedAt ?? default,
            DueDate = ToDate(DueDate.Date), DueTime = DueTime.SelectedTime is { } time ? TimeOnly.FromTimeSpan(time) : null,
            StartDate = ToDate(StartDate.Date), FinishDate = ToDate(FinishDate.Date),
            ReminderMinutes = (ReminderPicker.SelectedItem as ReminderChoice)?.Minutes
        };
    private async void SaveTask_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (TaskColumn.SelectedItem is not Choice) throw new ArgumentException("Choose a column.");
        try { await store.CommitAsync(editor => editor.SaveTask(ReadTaskDraft(), originalTask)); }
        catch { taskSaveFailed = true; RefreshDraftContext(); throw; }
        CloseEditor(); Render();
    });
    private async void CancelTask_Click(object sender, RoutedEventArgs e)
    {
        if (await CanDiscardDraftAsync()) CloseEditor();
    }
    private async void DeleteTask_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (originalTask is not null && await ConfirmAsync(T("Delete task?"), T("Delete “{0}”?", originalTask.Title), T("Delete")))
        {
            await store.CommitAsync(editor => editor.DeleteTask(originalTask.Id)); CloseEditor(); Render();
        }
    });
    private void CloseEditor()
    {
        closeEditorRequested = true;
        taskSaveFailed = false;
        TaskPane.IsPaneOpen = false; originalTask = null; initialDraft = null; draftTags.Clear();
        if (EditorLoaded) { TaskColumn.SelectedItem = null; TagInput.Text = ""; }
    }
    private void RenderTags()
    {
        TagList.Children.Clear();
        foreach (var tag in draftTags)
        {
            var remove = new Button { Content = $"{tag}  ×", HorizontalAlignment = HorizontalAlignment.Left };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, T("Remove tag {0}", tag));
            remove.Click += (_, _) => { draftTags.Remove(tag); RenderTags(); };
            TagList.Children.Add(remove);
        }
    }
    private void TagInput_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var tag = (args.ChosenSuggestion?.ToString() ?? args.QueryText).Trim();
        if (tag.Length > 0 && !draftTags.Contains(tag)) { draftTags.Add(tag); RenderTags(); }
        TagInput.Text = "";
    }
    private void TagInput_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput || document is null) return;
        sender.ItemsSource = WorkspaceView.AllTasks(document).Where(x => x.BoardId == draftBoardId)
            .SelectMany(x => x.Get<string[]>(Fields.Tags)).Distinct().Where(x => !draftTags.Contains(x)
                && x.Contains(sender.Text, StringComparison.CurrentCultureIgnoreCase)).Order().Take(12).ToArray();
    }
    private static DateTimeOffset? ToOffset(DateOnly? date) => date is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue)) : null;
    private static DateOnly? ToDate(DateTimeOffset? date) => date is { } d ? DateOnly.FromDateTime(d.DateTime) : null;
    private void ClearDueDate_Click(object sender, RoutedEventArgs e) => DueDate.Date = null;
    private void ClearDueTime_Click(object sender, RoutedEventArgs e) => DueTime.SelectedTime = null;
    private void ClearStartDate_Click(object sender, RoutedEventArgs e) => StartDate.Date = null;
    private void ClearFinishDate_Click(object sender, RoutedEventArgs e) => FinishDate.Date = null;
}
