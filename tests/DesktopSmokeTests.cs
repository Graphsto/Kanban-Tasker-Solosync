// Compiled only with -p:KanbanUiSmokeTest=true. Exercises this app's controls directly;
// no OS input injection and no access to the user's normal preferences or workspace.
using KanbanTasker.Core;
using KanbanTasker.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace KanbanTasker.Desktop;

internal static class SmokeProfile
{
    internal static string Scenario { get; } = Environment.GetCommandLineArgs()
        .FirstOrDefault(x => x.StartsWith("--startup-"))?[10..] ?? "workspace";
    internal static string DirectoryPath { get; } = Path.Combine(AppContext.BaseDirectory,
        Scenario == "workspace" ? "smoke-results" : "smoke-results-" + Scenario);
    internal static bool WindowWasHiddenBeforeLoaded { get; set; }
    internal static bool FirstFrameSeen { get; private set; }
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Initialize()
    {
        Trace("Module initialized");
        // Keep sync files separate from the private app profile, as in a normal installation.
        // Otherwise conflict discovery reads preferences while the UI test replaces that file.
        var workspaceDirectory = Path.Combine(DirectoryPath, "Workspace");
        Directory.CreateDirectory(workspaceDirectory);
        if (Scenario == "showcase")
        {
            ShowcaseProfile.Initialize(workspaceDirectory);
            return;
        }
        if (Scenario != "workspace")
        {
            var preferences = new LocalPreferences();
            if (Scenario != "first-run")
            {
                preferences.FilePath = Path.Combine(workspaceDirectory, "startup.json");
                if (Scenario == "invalid") File.WriteAllText(preferences.FilePath, "{broken json");
            }
            preferences.Save();
            Trace("Startup fixture ready: " + Scenario);
            return;
        }
        var document = new WorkspaceDocument();
        var editor = new WorkspaceEditor(document, new ChangeClock(Guid.NewGuid()));
        var board = editor.CreateBoard("Startup fixture");
        var column = WorkspaceView.Columns(document, board).First().Id;
        for (var i = 0; i < 30; i++)
            editor.SaveTask(new TaskData { BoardId = board, ColumnId = column, Title = "Startup task " + i });
        var path = Path.Combine(workspaceDirectory, "startup.json");
        File.WriteAllBytes(path, WorkspaceJson.Serialize(document));
        new LocalPreferences { FilePath = path, SelectedBoard = board }.Save();
        Trace("Startup fixture ready");
    }
    internal static void TrackFirstFrame() => Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += FirstRendering;
    private static void FirstRendering(object? sender, object args)
    {
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= FirstRendering;
        FirstFrameSeen = true;
        Trace("First composition frame");
    }
    internal static void Trace(string message)
    {
        Directory.CreateDirectory(DirectoryPath);
        File.AppendAllText(Path.Combine(DirectoryPath, "startup.log"), DateTimeOffset.Now.ToString("O") + " " + message + "\n");
    }
    internal static void RecordStartupFailure(Exception exception)
    {
        Trace(exception.ToString());
        File.WriteAllText(Path.Combine(DirectoryPath, "result.json"),
            JsonSerializer.Serialize(new { success = false, error = exception.ToString() }));
    }
}

public sealed partial class MainWindow
{
    private async Task RunDesktopSmokeTestsAsync()
    {
        SmokeProfile.Trace("Root loaded");
        // This harness invokes controls directly and uses synthetic drags without an
        // OS pointer. Incidental mouse movement must not enter those drag handlers.
        Root.IsHitTestVisible = false;
        var checks = new List<string>();
        var output = SmokeProfile.DirectoryPath;
        try
        {
            await WaitForAsync(() => SmokeProfile.FirstFrameSeen);
            if (SmokeProfile.Scenario == "showcase")
            {
                await CaptureShowcaseAsync(CaptureAsync);
                await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { success = true }));
                return;
            }
            Check(SmokeProfile.WindowWasHiddenBeforeLoaded && Root.IsLoaded && AppWindow.IsVisible,
                "The window becomes visible only after its XAML shell is loaded");
            Check(Root.Background is SolidColorBrush { Color.A: 255 }, "Startup shell has an opaque theme background");
            Check(!EditorLoaded && EditorSurface is null, "Startup does not construct the hidden task editor");
            CloseEditor();
            Check(!TaskbarPinNotice.IsOpen, "Ordinary startup never asks to pin the app");
            if (SmokeProfile.Scenario != "workspace")
            {
                Check(document is null && WelcomePanel.Visibility == Visibility.Visible, "Startup without a valid workspace shows the welcome screen");
                if (SmokeProfile.Scenario == "first-run")
                    Check(store.Status.State == StorageState.Closed && !ErrorBar.IsOpen, "First run offers file selection without an error");
                else
                {
                    Check(store.Status.State == StorageState.Error && ErrorBar.IsOpen, "Unavailable startup data produces a visible error");
                    Check(SmokeProfile.Scenario == "missing" ? !File.Exists(preferences.FilePath)
                        : File.ReadAllText(preferences.FilePath!) == "{broken json", "Startup never overwrites unavailable or damaged data");
                }
                await CaptureAsync("startup-" + SmokeProfile.Scenario);
                await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { success = true, checks }, new JsonSerializerOptions { WriteIndented = true }));
                return;
            }
            Check(document is not null && WorkspaceView.AllTasks(document).Count() == 30,
                "Startup opens the saved workspace without opening the task editor");
            Check(TaskbarPinning.IsRequested("--pin-to-taskbar")
                && TaskbarPinning.IsRequested("\"C:\\Program Files\\KanbanTasker.exe\" --pin-to-taskbar")
                && !TaskbarPinning.IsRequested("\"C:\\Folder --pin-to-taskbar\\app.exe\"")
                && !TaskbarPinning.IsRequested("--pin-to-taskbar-other") && !TaskbarPinning.IsRequested(null),
                "Pinning activation parses exact arguments, including redirected command lines");
            var pinRequests = 0;
            getTaskbarPinState = () => Task.FromResult(TaskbarPinState.Available);
            requestTaskbarPin = () => { pinRequests++; return Task.FromResult(false); };
            OfferTaskbarPinning(); await SettleAsync();
            Check(TaskbarPinNotice.IsOpen && PinToTaskbarButton.Visibility == Visibility.Visible && pinRequests == 0,
                "Installer activation offers pinning without opening a Windows prompt automatically");
            Invoke(PinToTaskbarButton); await SettleAsync();
            Check(pinRequests == 1 && TaskbarPinNotice.IsOpen && PinToTaskbarButton.Visibility == Visibility.Collapsed,
                "A declined or unavailable Windows prompt shows manual instructions without retrying");
            foreach (var language in TextCatalog.Languages)
            {
                text = new(language.Code); ApplyLanguage();
                Check(TaskbarPinNotice.Title == T("Pin to taskbar") && TaskbarPinNotice.Message == T("To pin the app manually, right-click the Kanban Tasker icon on the taskbar and choose Pin to taskbar."),
                    "Taskbar instructions switch language: " + language.Code);
            }
            text = new("en"); ApplyLanguage();
            getTaskbarPinState = () => Task.FromResult(TaskbarPinState.Pinned);
            OfferTaskbarPinning(); await SettleAsync();
            Check(!TaskbarPinNotice.IsOpen && pinRequests == 1, "An already pinned app is not prompted again");
            getTaskbarPinState = () => Task.FromResult(TaskbarPinState.Manual);
            OfferTaskbarPinning(); await SettleAsync();
            Check(TaskbarPinNotice.IsOpen && PinToTaskbarButton.Visibility == Visibility.Collapsed, "Unsupported Windows versions offer manual pinning");
            getTaskbarPinState = () => Task.FromResult(TaskbarPinState.Available);
            requestTaskbarPin = () => Task.FromResult(true);
            OfferTaskbarPinning(); await SettleAsync(); Invoke(PinToTaskbarButton); await SettleAsync();
            Check(TaskbarPinNotice.Severity == InfoBarSeverity.Success && PinToTaskbarButton.Visibility == Visibility.Collapsed,
                "A confirmed pin is shown as successful");
            TaskbarPinNotice.IsOpen = false;
            // Probe platform support without requesting or changing the real taskbar.
            Check(Enum.IsDefined(await TaskbarPinning.GetStateAsync()), "Native taskbar capability probe handles this Windows environment");
            getTaskbarPinState = TaskbarPinning.GetStateAsync; requestTaskbarPin = TaskbarPinning.RequestAsync;
            await Task.Delay(350);
            preferences.Theme = "light"; ApplyAppearance();
            await store.CreateAsync(Path.Combine(output, "Workspace", "test-" + Guid.NewGuid().ToString("N") + ".json"));
            Guid taskId = default, secondTask = default, firstColumn = default;
            await store.CommitAsync(editor =>
            {
                boardId = editor.CreateBoard("Language & drag test", "Only generated test data");
                firstColumn = WorkspaceView.Columns(editor.Document, boardId.Value).First().Id;
                taskId = editor.SaveTask(new TaskData
                {
                    BoardId = boardId.Value, ColumnId = firstColumn, Title = "Visible card — 日本語",
                    Description = "Drag this complete card, including its description, priority and tags.",
                    Priority = "High", Tags = ["Français", "Español"], DueDate = new(2026, 10, 21)
                });
                secondTask = editor.SaveTask(new TaskData { BoardId = boardId.Value, ColumnId = firstColumn, Title = "Second card" });
            });
            Render(); await CheckCardFramesAsync(firstColumn, "Initial board load");
            Render(); await CheckCardFramesAsync(firstColumn, "Unchanged board refresh after a header click");
            await OpenEditorAsync(taskId, firstColumn);
            Check(EditorLoaded && EditorSurface is not null && TaskTitle.Text == "Visible card — 日本語",
                "First task selection creates and populates the deferred editor");
            var unchanged = CanDiscardDraftAsync();
            Check(!HasDraftChanges && unchanged.IsCompletedSuccessfully && unchanged.Result, "Opening an existing task does not prompt on cancel");
            CancelTask_Click(this, new RoutedEventArgs());
            Check(!TaskPane.IsPaneOpen, "Cancel closes an unchanged task immediately");
            await OpenEditorAsync(taskId, firstColumn);
            TaskTitle.Text = "Draft after a blocked recovery write";
            var recoveryFile = Path.Combine(LocalPreferences.DirectoryPath, "Recovery", $"{document!.DocumentId:N}.recovery.json");
            using (var reader = new FileStream(recoveryFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                SaveTask_Click(this, new RoutedEventArgs());
                await WaitForAsync(() => !working);
                Check(TaskPane.IsPaneOpen && HasDraftChanges && taskSaveFailed && DraftNotice.IsOpen
                    && DraftNotice.Severity == InfoBarSeverity.Warning && store.Status.State == StorageState.Error,
                    "A persistent real recovery-file lock keeps the failed task draft and warning visible");
                Check(store.Current!.Tasks.Single(t => t.Id == taskId).Get<string>(Fields.Title) == "Visible card — 日本語",
                    "Failed recovery writes do not falsely accept a task edit");
            }
            await store.RefreshAsync(); Render(); await SettleAsync();
            Check(store.Status.State == StorageState.Saved && TaskPane.IsPaneOpen && taskSaveFailed && DraftNotice.IsOpen
                && TaskTitle.Text == "Draft after a blocked recovery write",
                "A successful background refresh cannot hide or replace the failed unsaved draft");
            foreach (var language in TextCatalog.Languages)
            {
                text = new(language.Code); ApplyLanguage();
                Check(DraftNotice.Message == T("Your last save did not complete. Your draft is still here. Select Save to try again."),
                    "Failed-save notice remains translated after refresh: " + language.Code);
            }
            text = new("en"); ApplyLanguage();
            SaveTask_Click(this, new RoutedEventArgs()); await WaitForAsync(() => !working);
            Check(!TaskPane.IsPaneOpen && !taskSaveFailed
                && store.Current!.Tasks.Single(t => t.Id == taskId).Get<string>(Fields.Title) == "Draft after a blocked recovery write",
                "Explicitly saving again after unlock persists the draft and clears its failed-save state");
            await OpenEditorAsync(taskId, firstColumn);
            TaskTitle.Text = "Visible card — 日本語";
            SaveTask_Click(this, new RoutedEventArgs()); await WaitForAsync(() => !working);
            await OpenEditorAsync(taskId, firstColumn);
            var originalTitle = TaskTitle.Text;
            var originalDescription = TaskDescription.Text;
            var originalDate = DueDate.Date;
            var otherColumn = ((IEnumerable<Choice>)TaskColumn.ItemsSource).First(x => x.Id != firstColumn);
            void ChangeAndRevert(string field, Action change, Action undo)
            {
                change(); Check(HasDraftChanges, field + " edit is dirty");
                undo(); Check(!HasDraftChanges, field + " undo restores a clean draft");
            }
            ChangeAndRevert("Title", () => TaskTitle.Text += " changed", () => TaskTitle.Text = originalTitle);
            ChangeAndRevert("Description", () => TaskDescription.Text += " changed", () => TaskDescription.Text = originalDescription);
            ChangeAndRevert("Priority", () => TaskPriority.SelectedValue = "Low", () => TaskPriority.SelectedValue = "High");
            ChangeAndRevert("Column", () => TaskColumn.SelectedItem = otherColumn,
                () => TaskColumn.SelectedItem = ((IEnumerable<Choice>)TaskColumn.ItemsSource).First(x => x.Id == firstColumn));
            ChangeAndRevert("Tags", () => draftTags.Add("changed"), () => draftTags.Remove("changed"));
            ChangeAndRevert("Pending tag", () => TagInput.Text = "not submitted", () => TagInput.Text = "");
            ChangeAndRevert("Due date", () => DueDate.Date = null, () => DueDate.Date = originalDate);
            ChangeAndRevert("Due time", () => DueTime.SelectedTime = TimeSpan.FromHours(13), () => DueTime.SelectedTime = null);
            ChangeAndRevert("Start date", () => StartDate.Date = DateTimeOffset.Now, () => StartDate.Date = null);
            ChangeAndRevert("Finish date", () => FinishDate.Date = DateTimeOffset.Now, () => FinishDate.Date = null);
            ChangeAndRevert("Reminder", () => ReminderPicker.SelectedItem = ReminderChoices.Single(x => x.Minutes == 15),
                () => ReminderPicker.SelectedItem = ReminderChoices.Single(x => x.Minutes is null));
            TaskTitle.Text += " changed";
            var confirmation = CanDiscardDraftAsync();
            await SettleAsync();
            var discard = VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Select(p => FindVisual<ContentDialog>(p.Child)).First(x => x is not null)!;
            Check(!confirmation.IsCompleted && discard.Title.ToString() == T("Discard task draft?"), "Changed task opens the discard confirmation");
            discard.Hide();
            Check(!await confirmation && HasDraftChanges, "Cancelling discard retains the edited draft");
            TaskTitle.Text = originalTitle;
            CloseEditor();
            await OpenEditorAsync(null, firstColumn);
            Check(!HasDraftChanges && await CanDiscardDraftAsync(), "A blank new task closes without a prompt");
            CloseEditor();
            await OpenEditorAsync(taskId, firstColumn);
            var originalSnapshot = originalTask!;
            await store.CommitAsync(editor => editor.SaveTask(originalSnapshot with { Description = "Remote edit while viewing" }, originalSnapshot));
            Render();
            Check(!HasDraftChanges && await CanDiscardDraftAsync() && TaskDescription.Text == originalSnapshot.Description,
                "Incoming saved edits do not turn an untouched local draft dirty");
            CloseEditor(); await OpenEditorAsync(taskId, firstColumn);
            TaskTitle.Text = "Unsaved draft — äéñ";
            TaskDescription.Text = "A local draft must survive all five language switches.";
            TaskPriority.SelectedValue = "Medium";
            draftTags.Add("Local tag"); RenderTags();
            DueTime.SelectedTime = TimeSpan.FromHours(16);
            ReminderPicker.SelectedItem = ReminderChoices.Single(x => x.Minutes == 15);
            var before = WorkspaceJson.Serialize(store.Current!);
            foreach (var language in TextCatalog.Languages)
            {
                preferences.Language = language.Code; preferences.Save(); text = new(language.Code); ApplyLanguage();
                await SettleAsync();
                Check(TaskTitle.Text == "Unsaved draft — äéñ" && TaskDescription.Text.StartsWith("A local draft"), "Draft text retained: " + language.Code);
                Check((TaskPriority.SelectedItem as PriorityChoice)?.Value == "Medium", "Stable priority: " + language.Code);
                Check((ReminderPicker.SelectedItem as ReminderChoice)?.Minutes == 15 && DueTime.SelectedTime == TimeSpan.FromHours(16), "Reminder retained: " + language.Code);
                Check(draftTags.Contains("Local tag") && (TaskColumn.SelectedItem as Choice)?.Id == firstColumn, "Tags/column retained: " + language.Code);
                Check(BoardPicker.SelectedItem is Choice { Name: "Language & drag test" }, "Board content retained: " + language.Code);
                Check(TaskPriority.Header?.ToString() == text.Get("Priority") && EditorHeading.Text == text.Get("Edit task"), "Labels refreshed: " + language.Code);
                Check(LocalPreferences.Load().Language == language.Code, "Language persisted: " + language.Code);
                await CaptureAsync("language-" + language.Code);
            }
            Check(before.SequenceEqual(WorkspaceJson.Serialize(store.Current!)), "Switching languages never writes workspace data");
            await CheckDistributionUiAsync(Check, Invoke);
            Settings_Click(this, new RoutedEventArgs());
            await SettleAsync();
            var settings = VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot)
                .Select(p => FindVisual<ContentDialog>(p.Child)).First(x => x is not null)!;
            var tabs = (Pivot)settings.Content;
            Check(tabs.SelectedIndex == 0 && tabs.Items.Count == 3, "Settings opens on General with Appearance and Advanced sections");
            await CaptureAsync("settings-general", settings);
            tabs.SelectedIndex = 1;
            await SettleAsync();
            foreach (var language in TextCatalog.Languages)
            {
                var panel = (StackPanel)((ScrollViewer)((PivotItem)tabs.Items[1]).Content).Content;
                var picker = panel.Children.OfType<ComboBox>().Single(x => x.Name == "LanguagePicker");
                picker.SelectedItem = TextCatalog.Languages.First(x => x.Code == language.Code);
                await SettleAsync();
                Check(text.Language == language.Code && LocalPreferences.Load().Language == language.Code
                    && settings.Title.ToString() == T("Settings") && settings.CloseButtonText == T("Close")
                    && tabs.SelectedIndex == 1, "Settings selection updates live without changing tabs: " + language.Code);
                if (language.Code == "de") await CaptureAsync("settings-de", settings);
            }
            foreach (var choice in ThemeChoices())
            {
                var panel = (StackPanel)((ScrollViewer)((PivotItem)tabs.Items[1]).Content).Content;
                var picker = panel.Children.OfType<ComboBox>().Single(x => x.Name == "ThemePicker");
                picker.SelectedItem = ((IEnumerable<ThemeChoice>)picker.ItemsSource).Single(x => x.Value == choice.Value);
                await SettleAsync();
                Check(preferences.Theme == choice.Value && LocalPreferences.Load().Theme == choice.Value, "Theme selection persists: " + choice.Value);
                Check(HasDraftChanges && TaskTitle.Text == "Unsaved draft — äéñ" && (TaskPriority.SelectedItem as PriorityChoice)?.Value == "Medium",
                    "Theme preserves unsaved fields: " + choice.Value);
                if (choice.Value != "system")
                    Check(Root.ActualTheme == (choice.Value.StartsWith("dark") ? ElementTheme.Dark : ElementTheme.Light)
                        && settings.RequestedTheme == Root.ActualTheme, "Window and dialog base theme agree: " + choice.Value);
                await CaptureAsync("settings-theme-" + choice.Value, settings);
                await CheckOpaqueDialogAsync(settings, "Settings " + choice.Value);
            }
            settings.Hide();
            await WaitForAsync(() => !working);
            Check(TaskPane.IsPaneOpen && TaskTitle.Text == "Unsaved draft — äéñ", "Settings keeps the open editor draft");
            preferences.Theme = "dark"; ApplyAppearance(); await SettleAsync();
            confirmation = CanDiscardDraftAsync(); await SettleAsync();
            discard = VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Select(p => FindVisual<ContentDialog>(p.Child)).First(x => x is not null)!;
            await CheckOpaqueDialogAsync(discard, "Dark discard confirmation");
            await CaptureAsync("discard-dark", discard);
            discard.Hide(); await confirmation;
            SaveTask_Click(this, new RoutedEventArgs());
            await WaitForAsync(() => !working);
            await CheckCardFramesAsync(firstColumn, "Task update");
            var saved = TaskData.From(store.Current!.Tasks.Single(x => x.Id == taskId));
            Check(saved.Priority == "Medium" && saved.Title == "Unsaved draft — äéñ" && saved.Tags.Contains("Local tag") && saved.ReminderMinutes == 15,
                "Saving a translated editor retains stable priority and draft data");
            CloseEditor();
            text = new("de"); ApplyLanguage();
            preferences.Theme = "dark"; ApplyAppearance();
            Render(); await SettleAsync();
            var column = ColumnsPanel.Children.OfType<Border>().First();
            var card = (Border)((ListViewItem)columnLists[firstColumn].Items[0]).Content;
            await CheckPreviewAsync(card, taskId, false, "card-drag");
            await store.CommitAsync(editor => editor.MoveTask(taskId, firstColumn, 1));
            Render(); await CheckCardFramesAsync(firstColumn, "Card reorder");
            Check(WorkspaceView.Tasks(store.Current!, firstColumn).Select(x => x.Id).SequenceEqual([secondTask, taskId]), "Card reorder persists");
            await CheckHeaderDropsAsync(taskId, firstColumn);
            column = ColumnsPanel.Children.OfType<Border>().First();
            await CheckPreviewAsync(column, firstColumn, true, "column-drag");
            var position = WorkspaceView.Columns(store.Current!, boardId!.Value).Count() - 1;
            preferences.CollapsedColumns.Add(firstColumn); Render(); await SettleAsync();
            column = ColumnsPanel.Children.OfType<Border>().First();
            await CheckPreviewAsync(column, firstColumn, true, "collapsed-column-drag");
            await store.CommitAsync(editor => editor.MoveColumn(firstColumn, position));
            Render(); await SettleAsync();
            Check(WorkspaceView.Columns(store.Current!, boardId.Value).Last().Id == firstColumn && preferences.CollapsedColumns.Contains(firstColumn),
                "Collapsed column moves with task membership preserved");
            Check(store.Current!.Tasks.Single(x => x.Id == taskId).Get<Guid>(Fields.ColumnId) == firstColumn, "Task membership survives column move");
            await CaptureAsync("final-dark");
            await store.OpenAsync(store.FilePath!);
            Check(WorkspaceView.Columns(store.Current!, boardId.Value).Last().Id == firstColumn, "Reopening keeps order");
            // Bring the populated column into view for appearance inspection.
            await store.CommitAsync(editor => editor.MoveColumn(firstColumn, 0));
            preferences.CollapsedColumns.Clear(); Render();
            await CheckCardFramesAsync(firstColumn, "Column move and expansion");
            await OpenEditorAsync(taskId, firstColumn);
            foreach (var choice in ThemeChoices())
            {
                preferences.Theme = choice.Value; ApplyAppearance(); await SettleAsync();
                Check(!HasDraftChanges && await CanDiscardDraftAsync(), "Appearance changes leave an untouched task clean: " + choice.Value);
                await CaptureAsync("board-theme-" + choice.Value);
            }
            CloseEditor();
            Settings_Click(this, new RoutedEventArgs()); await SettleAsync();
            settings = VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Select(p => FindVisual<ContentDialog>(p.Child)).First(x => x is not null)!;
            Check(((Pivot)settings.Content).SelectedIndex == 0, "Settings returns to General on the next opening");
            settings.Hide(); await WaitForAsync(() => !working);
            var editColumn = ColumnDialogAsync(firstColumn); await SettleAsync();
            var columnDialog = OpenDialog();
            var limit = FindVisual<NumberBox>(columnDialog, "ColumnLimit")!;
            var up = FindVisual<Microsoft.UI.Xaml.Controls.Primitives.RepeatButton>(limit, "UpSpinButton")!;
            var down = FindVisual<Microsoft.UI.Xaml.Controls.Primitives.RepeatButton>(limit, "DownSpinButton")!;
            Check(up.Visibility == Visibility.Visible && down.Visibility == Visibility.Visible, "Limit arrows are visible without opening a popup");
            Invoke(up); await SettleAsync(); Check(limit.Value == 11, "Limit up arrow immediately increments by one");
            Invoke(down); await SettleAsync(); Check(limit.Value == 10, "Limit down arrow immediately decrements by one");
            limit.Value = 0; await SettleAsync(); Check(!down.IsEnabled, "Limit down arrow stops at zero");
            Invoke(up); await SettleAsync(); Check(limit.Value == 1, "Limit can increase again from zero");
            limit.Value = 10; limit.Text = "20";
            Invoke(up); await SettleAsync(); Check(limit.Value == 21, "Limit arrows commit and increment typed input");
            await CaptureAsync("column-limit-inline", columnDialog);
            Invoke(FindVisual<Button>(columnDialog, "PrimaryButton")!); await editColumn;
            Check(store.Current!.Columns.Single(x => x.Id == firstColumn).Get<int>(Fields.Limit) == 21, "Edited limit is saved");

            Calendar_Click(this, new RoutedEventArgs()); await SettleAsync();
            var calendarDialog = OpenDialog();
            var calendar = FindVisual<CalendarView>(calendarDialog, "TaskCalendar")!;
            var calendarTasks = FindVisual<ListView>(calendarDialog, "CalendarTasks")!;
            Check(calendar.ActualHeight > 0 && FindVisual<CalendarDatePicker>(calendarDialog) is null, "Calendar opens directly as a month view");
            var due = new DateOnly(2026, 10, 21);
            calendar.SetDisplayDate(ToOffset(due)!.Value);
            calendar.SelectedDates.Clear(); calendar.SelectedDates.Add(ToOffset(due)!.Value); await SettleAsync();
            CalendarViewDayItem Day(DateOnly d) => FindAllVisual<CalendarViewDayItem>(calendar)
                .First(x => DateOnly.FromDateTime(x.Date.DateTime) == d);
            bool Marked(DateOnly d) => Day(d).Background is SolidColorBrush { Color.A: > 0 };
            Check(Marked(due) && !Marked(due.AddDays(1)), "Only dates with due tasks are highlighted");
            Check(calendarTasks.Items.Count == 1 && ((Choice)calendarTasks.Items[0]).Id == taskId, "Selected date lists the due task");
            await CaptureAsync("calendar-preview", calendarDialog);
            await store.CommitAsync(editor =>
            {
                var otherBoard = editor.CreateBoard("Other calendar board", "");
                var otherColumnId = WorkspaceView.Columns(editor.Document, otherBoard).First().Id;
                editor.SaveTask(new TaskData { BoardId = otherBoard, ColumnId = otherColumnId, Title = "Unrelated", DueDate = due });
                var second = TaskData.From(editor.Document.Tasks.Single(x => x.Id == secondTask));
                editor.SaveTask(second with { DueDate = due }, second);
            });
            await SettleAsync();
            Check(calendarTasks.Items.Count == 2 && Microsoft.UI.Xaml.Automation.AutomationProperties.GetHelpText(Day(due)) == T("{0} tasks", 2),
                "Open calendar updates incoming due tasks and ignores other boards");
            await store.CommitAsync(editor =>
            {
                editor.DeleteTask(secondTask);
                var current = TaskData.From(editor.Document.Tasks.Single(x => x.Id == taskId));
                editor.SaveTask(current with { DueDate = due.AddDays(1) }, current);
            });
            await SettleAsync();
            Check(!Marked(due) && Marked(due.AddDays(1)) && calendarTasks.Items.Count == 0, "Moving and deleting due dates updates marks and the selected list");
            await CaptureAsync("calendar-unselected-due-day", calendarDialog);
            calendar.SetDisplayDate(ToOffset(due.AddMonths(1))!.Value); await SettleAsync();
            calendar.SetDisplayDate(ToOffset(due)!.Value); await SettleAsync();
            Check(!Marked(due) && Marked(due.AddDays(1)), "Calendar marks remain correct after month navigation");
            calendar.SelectedDates.Clear(); calendar.SelectedDates.Add(ToOffset(due.AddDays(1))!.Value); await SettleAsync();
            var dueTaskPeer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(
                (ListViewItem)calendarTasks.ContainerFromIndex(0));
            ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)dueTaskPeer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
            await WaitForAsync(() => !working);
            Check(TaskPane.IsPaneOpen && originalTask?.Id == taskId, "Choosing a calendar task opens its editor");
            CloseEditor();
            await CheckBoardGroupsAsync(Check, CaptureAsync);
            await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { success = true, checks }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            await CaptureAsync("failure");
            await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { success = false, error = ex.ToString(), appError = lastErrorMessage, storage = store.Status, checks }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { CancelBoardDrag(); allowClose = true; Close(); }

        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks.Add(name);
        }
        ContentDialog OpenDialog() => VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot)
            .Select(p => FindVisual<ContentDialog>(p.Child)).First(x => x is not null)!;
        void Invoke(Microsoft.UI.Xaml.Controls.Primitives.ButtonBase button)
        {
            var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(button);
            ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
        }
        async Task CheckPreviewAsync(FrameworkElement visual, Guid id, bool isColumn, string name)
        {
            var bounds = BoundsInRoot(visual);
            var start = new Point(bounds.X + 25, bounds.Y + 20);
            var drag = new BoardDrag(visual, visual, id, isColumn, document!.DocumentId, boardId!.Value, null!, start, bounds)
                { Moving = true, Position = new(start.X + 140, start.Y + 65) };
            boardDrag = drag;
            await ShowDragPreviewAsync(drag);
            await SettleAsync();
            Check(drag.Preview?.Child is Image { Source: RenderTargetBitmap { PixelWidth: > 0, PixelHeight: > 0 } }, name + ": full visual rendered");
            Check(DragLayer.Children.Count == 1 && Math.Abs(Canvas.GetLeft(drag.Preview!) - (bounds.X + 140)) < 1, name + ": follows horizontal offset");
            Check(Math.Abs(Canvas.GetTop(drag.Preview!) - (bounds.Y + 65)) < 1 && Math.Abs(visual.Opacity - .22) < .001, name + ": follows vertical offset");
            await CaptureAsync(name);
            CancelBoardDrag();
            Check(DragLayer.Children.Count == 0 && visual.Opacity == 1, name + ": cancel restores source");
            // An async capture completing after release must never bring a stale preview back.
            boardDrag = drag;
            var pending = ShowDragPreviewAsync(drag); CancelBoardDrag(); await pending;
            Check(DragLayer.Children.Count == 0 && visual.Opacity == 1, name + ": late capture cannot resurrect preview");
        }
        async Task CheckHeaderDropsAsync(Guid taskId, Guid sourceColumn)
        {
            var targetColumn = WorkspaceView.Columns(document!, boardId!.Value).First(x => x.Id != sourceColumn).Id;
            var fixtures = new List<Guid>();
            await store.CommitAsync(editor =>
            {
                for (var i = 0; i < 2; i++) fixtures.Add(editor.SaveTask(new TaskData
                { BoardId = boardId.Value, ColumnId = targetColumn, Title = "Header drop target " + i }));
            });
            Render(); await SettleAsync();
            var source = (Border)columnLists[sourceColumn].Items.OfType<ListViewItem>().Single(x => (Guid)x.Tag == taskId).Content;
            var target = ColumnsPanel.Children.OfType<Border>().Single(x => (Guid)x.Tag == targetColumn);
            var list = columnLists[targetColumn];
            var bounds = BoundsInRoot(source);
            var drag = new BoardDrag(source, source, taskId, false, document!.DocumentId, boardId.Value, null!,
                new(bounds.Left + 20, bounds.Top + 20), bounds) { Moving = true };
            var originalBrush = target.BorderBrush;
            var originalThickness = target.BorderThickness;
            boardDrag = drag;
            var x = BoundsInRoot(target).Left + target.ActualWidth / 2;
            foreach (var y in new[] { BoundsInRoot(target).Top + 25, BoundsInRoot(target).Top + 60, BoundsInRoot(list).Top - 3 })
            {
                drag.Position = new(x, y);
                var drop = FindBoardDrop(drag);
                Check(drop is { Index: 0 } && drop.Column == targetColumn && drop.Highlight == target && drop.Edge == new Thickness(2),
                    "Column heading, controls and gap accept a card at the beginning with a full outline");
            }
            UpdateDragFeedback(scroll: false);
            await ShowDragPreviewAsync(drag); await SettleAsync();
            Check(target.BorderThickness == new Thickness(2) && target.BorderBrush == Brush("AccentFillColorDefaultBrush"),
                "Header drop highlights all four column edges in the accent color");
            await CaptureAsync("card-drop-column-header");
            var items = list.Items.OfType<ListViewItem>().ToArray();
            drag.Position = new(x, BoundsInRoot(items[0]).Top + 4);
            UpdateDragFeedback(scroll: false);
            Check(FindBoardDrop(drag) is { Index: 0 } overCard && ReferenceEquals(overCard.Highlight, items[0].Content),
                "Moving from the header onto the first card restores the precise card insertion marker");
            Check(target.BorderThickness == originalThickness && target.BorderBrush == originalBrush,
                "Leaving the header clears its full-column outline");
            drag.Position = new(x, BoundsInRoot(items[1]).Top - 2);
            Check(FindBoardDrop(drag) is { Index: 1 }, "Insertion between cards is unchanged");
            drag.Position = new(x, BoundsInRoot(items[1]).Bottom + 10);
            Check(FindBoardDrop(drag) is { Index: 2 }, "Insertion below the last card is unchanged");
            ClearDragHighlight();
            list.Height = 70; list.VerticalAlignment = VerticalAlignment.Top;
            Root.UpdateLayout(); await SettleAsync();
            var viewer = FindScrollViewer(list)!;
            viewer.ChangeView(null, viewer.ScrollableHeight, null, true);
            await SettleAsync();
            Check(viewer.VerticalOffset > 0, "Header drop is exercised with a genuinely scrolled card list");
            drag.Position = new(x, BoundsInRoot(list).Top - 3);
            var headerDrop = FindBoardDrop(drag)!;
            Check(headerDrop.Index == 0 && headerDrop.Highlight == target, "A scrolled column header still inserts before every card");
            var sourceBorder = ColumnsPanel.Children.OfType<Border>().Single(c => (Guid)c.Tag == sourceColumn);
            drag.Position = new(BoundsInRoot(sourceBorder).Left + 30, BoundsInRoot(sourceBorder).Top + 25);
            Check(FindBoardDrop(drag) is { Index: 0 } sameColumn && sameColumn.Column == sourceColumn && sameColumn.Highlight == sourceBorder,
                "Dragging within the same column onto its header selects the first position");
            drag.Position = new(-10, -10); UpdateDragFeedback(scroll: false);
            Check(FindBoardDrop(drag) is null && dragHighlight is null, "Leaving the board removes the drop target and highlight");
            CancelBoardDrag();
            await store.CommitAsync(editor => editor.MoveTask(taskId, headerDrop.Column, headerDrop.Index));
            await store.OpenAsync(store.FilePath!);
            Check(WorkspaceView.Tasks(store.Current!, targetColumn).Select(t => t.Id).SequenceEqual(new[] { taskId }.Concat(fixtures))
                && WorkspaceView.Tasks(store.Current!, sourceColumn).All(t => t.Id != taskId),
                "Dropping on the header saves the new column and first position across reopening");
            await store.CommitAsync(editor =>
            {
                editor.MoveTask(taskId, sourceColumn, 1);
                foreach (var id in fixtures) editor.DeleteTask(id);
            });
            Render(); await SettleAsync();
        }
        async Task CaptureAsync(string name, FrameworkElement? target = null)
        {
            var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(target ?? Root);
            var pixels = (await bitmap.GetPixelsAsync()).ToArray();
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
            await encoder.FlushAsync(); stream.Seek(0);
            using var file = File.Create(Path.Combine(output, name + ".png"));
            await stream.AsStreamForRead().CopyToAsync(file);
        }
        async Task CheckOpaqueDialogAsync(ContentDialog dialog, string name)
        {
            // Render the actual dialog surface without the dimmed board behind it.
            // Padding pixels in its header, body and footer must block ALL board content.
            var surface = FindVisual<Border>(dialog, "BackgroundElement")
                ?? throw new InvalidOperationException("Dialog surface missing.");
            var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(surface);
            var pixels = (await bitmap.GetPixelsAsync()).ToArray();
            var alphas = new[] { 12, bitmap.PixelHeight / 2, bitmap.PixelHeight - 12 }
                .Select(y => pixels[(y * bitmap.PixelWidth + 12) * 4 + 3]).ToArray();
            Check(alphas.All(alpha => alpha == 255), name + " header/body/footer are opaque (alpha " + string.Join(",", alphas) + ")");
        }
        async Task CheckCardFramesAsync(Guid columnId, string name)
        {
            Root.UpdateLayout();
            // Compare the actual list rendering just after layout and after any entrance
            // animation would have finished. Exclude the editor/caret and other controls.
            await Task.Delay(40);
            async Task<byte[]> FrameAsync()
            {
                Root.UpdateLayout();
                var list = columnLists[columnId];
                var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(list);
                return (await bitmap.GetPixelsAsync()).ToArray();
            }
            var first = await FrameAsync();
            await Task.Delay(700);
            var settled = await FrameAsync();
            Check(first.Length > 0 && first.SequenceEqual(settled), name + ": cards appear immediately without fading or sliding");
        }
    }
    private static async Task SettleAsync() => await Task.Delay(200);
    private static IEnumerable<T> FindAllVisual<T>(DependencyObject element) where T : DependencyObject
    {
        if (element is T value) yield return value;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            foreach (var child in FindAllVisual<T>(VisualTreeHelper.GetChild(element, i))) yield return child;
    }
    private static T? FindVisual<T>(DependencyObject? element, string? name = null) where T : DependencyObject
    {
        if (element is T result && (name is null || element is FrameworkElement visual && visual.Name == name)) return result;
        if (element is null) return null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            if (FindVisual<T>(VisualTreeHelper.GetChild(element, i), name) is { } child) return child;
        return null;
    }
    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        if (!condition()) throw new TimeoutException("UI operation did not finish.");
    }
}
