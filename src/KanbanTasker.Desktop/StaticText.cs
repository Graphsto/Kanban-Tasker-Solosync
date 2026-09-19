using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace KanbanTasker.Desktop;

public sealed partial class MainWindow
{
    private void ApplyStaticText()
    {
        AutomationProperties.SetName(AppLogo, T("Kanban Tasker logo"));
        BoardPicker.PlaceholderText = T("Choose a board");
        AutomationProperties.SetName(BoardPicker, T("Board"));
        NewBoardButton.Content = T("New board");
        ToolTipService.SetToolTip(NewBoardButton, T("Create a board"));
        CalendarButton.Content = T("Calendar");
        AutomationProperties.SetName(BoardMenuButton, T("Board actions"));
        ToolTipService.SetToolTip(BoardMenuButton, T("Board actions"));
        EditBoardMenuItem.Text = T("Edit board");
        ManageBoardsMenuItem.Text = T("Manage boards");
        DeleteBoardMenuItem.Text = T("Delete board");
        AutomationProperties.SetName(SettingsButton, T("Settings"));
        ToolTipService.SetToolTip(SettingsButton, T("Settings"));
        WelcomeTitle.Text = T("Your boards, in one local file");
        WelcomeText.Text = T("Create a data file or open an existing one. Choose a locally available Nextcloud folder to use the same boards on your other devices.");
        CreateDataFileButton.Content = T("Create data file");
        OpenDataFileButton.Content = T("Open data file");
        EmptyBoardButton.Content = T("Create your first board");
        StatusText.Text = T("No data file open");
    }
    private void ApplyEditorStaticText()
    {
        EditorHeading.Text = T("New task");
        DraftNotice.Message = T("This task changed on another device. Saving updates only the fields you changed.");
        TaskTitle.Header = T("Title");
        TaskDescription.Header = T("Description");
        TaskColumn.Header = T("Column");
        TaskPriority.Header = T("Priority");
        TagInput.Header = T("Tags");
        TagInput.PlaceholderText = T("Type a tag and press Enter");
        DateInformation.Header = T("Date information");
        DueDate.Header = T("Due date");
        DueDate.PlaceholderText = T("Choose a date");
        ClearDueDateButton.Content = T("Clear due date");
        DueTime.Header = T("Due time");
        ClearDueTimeButton.Content = T("Clear due time");
        ReminderPicker.Header = T("Reminder");
        StartDate.Header = T("Start date");
        ClearStartDateButton.Content = T("Clear start date");
        FinishDate.Header = T("Finish date");
        ClearFinishDateButton.Content = T("Clear finish date");
        SaveTaskButton.Content = T("Save");
        CancelTaskButton.Content = T("Cancel");
        DeleteTaskButton.Content = T("Delete");
    }
}
