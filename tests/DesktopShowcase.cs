// Documentation screenshots from the real WinUI controls. Compiled only into the
// isolated developer harness; never shipped in an installable package.
using KanbanTasker.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace KanbanTasker.Desktop;

internal static class ShowcaseProfile
{
    internal static readonly DateOnly CalendarDate = new(2026, 9, 23);
    internal static Guid FeaturedTask { get; private set; }
    internal static Guid FeaturedColumn { get; private set; }

    internal static void Initialize(string directory)
    {
        var document = new WorkspaceDocument();
        var editor = new WorkspaceEditor(document, new ChangeClock(Guid.NewGuid()));
        var board = editor.CreateBoard("Studio refresh", "A little more space to make things. One project, one step at a time.");
        var columns = WorkspaceView.Columns(document, board).ToArray();
        editor.DeleteColumn(columns[3].Id); // A custom four-column workflow.
        editor.EditColumn(columns[2].Id, "In Progress", 3, "In Progress", 10);
        FeaturedColumn = columns[2].Id;

        Add(0, "Collect workspace ideas", "A place for sketches, colour palettes and things worth trying.", "Low", ["ideas"]);
        Add(0, "Find a desk lamp", "Warm light, a small footprint and an adjustable arm.", "Low", ["workspace"]);
        Add(0, "Make room for plants", "A bit of green for the reading corner.", "Low", ["weekend"]);
        Add(1, "Choose the wall colour", "Compare the samples in daylight before deciding.", "Medium", ["design"], 22);
        Add(1, "Order shelves", "Measure twice. Leave enough room for the sketchbooks.", "High", ["workspace"], 24);
        Add(1, "Sort reference photos", "Keep favourites together for the next project.", "Low", ["ideas"], 26);
        FeaturedTask = Add(2, "Plan the gallery wall", "Pick six favourite prints.\nTry the layout on paper before hanging them.", "High", ["design", "weekend"], 23, new TimeOnly(17, 30));
        Add(2, "Organise the materials", "Label the drawers so everything is easy to find.", "Medium", ["workspace"], 23, new TimeOnly(11, 0));
        Add(4, "Clear the desk", "A clean surface for a fresh start.", "Low", ["workspace"]);
        Add(4, "Sketch the room layout", "Space for work, a reading chair and a little storage.", "Medium", ["design"]);
        editor.CreateBoard("Weekend plans", "Small projects, walks and time away from the screen.");

        var path = Path.Combine(directory, "Studio.kanban.json");
        File.WriteAllBytes(path, WorkspaceJson.Serialize(document));
        new LocalPreferences { FilePath = path, SelectedBoard = board, Language = "en", Theme = "dark" }.Save();

        Guid Add(int column, string title, string description, string priority, string[] tags, int? day = null, TimeOnly? time = null) =>
            editor.SaveTask(new TaskData
            {
                BoardId = board, ColumnId = columns[column].Id, Title = title, Description = description,
                Priority = priority, Tags = tags, DueDate = day is { } d ? new DateOnly(2026, 9, d) : null,
                DueTime = time, StartDate = day is not null ? new DateOnly(2026, 9, 20) : null,
                // Do not schedule Windows notifications from the documentation fixture.
                ReminderMinutes = null
            });
    }
}

public sealed partial class MainWindow
{
    private async Task CaptureShowcaseAsync(Func<string, FrameworkElement?, Task> capture)
    {
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1420, 860));
        await Task.Delay(500);
        if (document is null || ErrorBar.IsOpen || store.Status.State != StorageState.Saved)
            throw new InvalidOperationException("Showcase workspace did not load cleanly.");

        await Capture("board-dark");

        // Render the existing pointer-following preview and the actual header drop feedback.
        var source = columnLists[ShowcaseProfile.FeaturedColumn].Items.OfType<ListViewItem>()
            .Single(x => (Guid)x.Tag == ShowcaseProfile.FeaturedTask);
        var card = (FrameworkElement)source.Content;
        var bounds = BoundsInRoot(card);
        var destination = BoundsInRoot(ColumnsPanel.Children.OfType<Border>().Last());
        var start = new Point(bounds.X + 30, bounds.Y + 20);
        var drag = new BoardDrag(card, card, ShowcaseProfile.FeaturedTask, false, document.DocumentId, boardId!.Value,
            null!, start, bounds) { Moving = true, Position = new(destination.X + 90, destination.Y + 55) };
        boardDrag = drag;
        await ShowDragPreviewAsync(drag);
        UpdateDragFeedback(false);
        await SettleAsync();
        await Capture("drag-and-drop");
        CancelBoardDrag();

        preferences.Theme = "lightBlue"; ApplyAppearance(); await SettleAsync();
        await OpenEditorAsync(ShowcaseProfile.FeaturedTask, ShowcaseProfile.FeaturedColumn);
        DateInformation.IsExpanded = false;
        await Task.Delay(500);
        await Capture("task-details");
        CloseEditor();

        preferences.Theme = "light"; ApplyAppearance(); await SettleAsync();
        Calendar_Click(this, new RoutedEventArgs()); await SettleAsync();
        var calendarDialog = OpenShowcaseDialog();
        var calendar = FindVisual<CalendarView>(calendarDialog, "TaskCalendar")!;
        var date = new DateTimeOffset(ShowcaseProfile.CalendarDate.ToDateTime(TimeOnly.MinValue));
        calendar.SetDisplayDate(date);
        calendar.SelectedDates.Clear(); calendar.SelectedDates.Add(date);
        await Task.Delay(500);
        await Capture("calendar", FindVisual<Border>(calendarDialog, "BackgroundElement")!);
        calendarDialog.Hide(); await WaitForAsync(() => !working);

        preferences.Theme = "darkBlue"; ApplyAppearance(); await SettleAsync();
        await Capture("board-dark-blue");
        Settings_Click(this, new RoutedEventArgs()); await SettleAsync();
        var settings = OpenShowcaseDialog();
        FindVisual<Pivot>(settings, "SettingsTabs")!.SelectedIndex = 1;
        await SettleAsync();
        await Capture("appearance", FindVisual<Border>(settings, "BackgroundElement")!);
        settings.Hide(); await WaitForAsync(() => !working);

        async Task Capture(string name, FrameworkElement? target = null)
        {
            if (ErrorBar.IsOpen) throw new InvalidOperationException("Do not publish a showcase with an app error.");
            // Only abbreviate the status-bar path; never expose a developer's Windows profile.
            PathText.Text = Path.GetFileName(store.FilePath);
            Root.UpdateLayout();
            await capture(name, target);
        }
        ContentDialog OpenShowcaseDialog() => VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot)
            .Select(p => FindVisual<ContentDialog>(p.Child)).First(x => x is not null)!;
    }
}
