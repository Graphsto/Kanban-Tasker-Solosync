using KanbanTasker.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace KanbanTasker.Desktop;

public sealed partial class MainWindow
{
    private async void Calendar_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (document is null || boardId is not { } calendarBoard) return;
        var calendar = new CalendarView
        {
            Name = "TaskCalendar", SelectionMode = CalendarViewSelectionMode.Single,
            FirstDayOfWeek = (Windows.Globalization.DayOfWeek)text.Culture.DateTimeFormat.FirstDayOfWeek,
            MinWidth = 320, Height = 320, HorizontalAlignment = HorizontalAlignment.Stretch
        };
        calendar.SelectedDates.Add(DateTimeOffset.Now);
        var dateLabel = new TextBlock { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        var tasks = new ListView
        {
            Name = "CalendarTasks", MaxHeight = 160, IsItemClickEnabled = true,
            SelectionMode = ListViewSelectionMode.None, DisplayMemberPath = "Name", ItemContainerTransitions = new()
        };
        var empty = new TextBlock { Text = T("No tasks due on this date."), TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 12, MinWidth = 340 };
        panel.Children.Add(calendar); panel.Children.Add(dateLabel); panel.Children.Add(empty); panel.Children.Add(tasks);
        var scroll = new ScrollViewer
        {
            Content = panel, MaxHeight = Math.Clamp(Root.ActualHeight - 240, 220, 560),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var dialog = Dialog(T("Task calendar"), scroll); dialog.CloseButtonText = T("Close");
        var dayItems = new HashSet<CalendarViewDayItem>();
        TaskData[] datedTasks = [];
        Dictionary<DateOnly, int> dueCounts = [];
        var active = true;
        Guid? chosen = null;
        void Decorate(CalendarViewDayItem item)
        {
            var count = dueCounts.GetValueOrDefault(DateOnly.FromDateTime(item.Date.DateTime));
            var accent = ((SolidColorBrush)Brush("AccentFillColorDefaultBrush")).Color;
            item.SetDensityColors(Enumerable.Repeat(accent, Math.Min(count, 3)));
            if (count > 0)
            {
                // Retain native today/selection/focus treatment; add a tinted surface and due bars.
                var tint = accent; tint.A = 32;
                item.Background = new SolidColorBrush(tint);
                var description = T("{0} tasks", count);
                ToolTipService.SetToolTip(item, description);
                AutomationProperties.SetHelpText(item, description);
            }
            else
            {
                item.ClearValue(Control.BackgroundProperty);
                ToolTipService.SetToolTip(item, null);
                item.ClearValue(AutomationProperties.HelpTextProperty);
            }
        }
        void RefreshSelection()
        {
            var selected = DateOnly.FromDateTime(calendar.SelectedDates.FirstOrDefault(DateTimeOffset.Now).DateTime);
            dateLabel.Text = T("Tasks due on") + " " + selected.ToString("d", text.Culture);
            var matches = datedTasks.Where(x => x.DueDate == selected).OrderBy(x => x.DueTime).ThenBy(x => x.Title).ToArray();
            tasks.ItemsSource = matches.Select(x => new Choice(x.Id,
                (x.DueTime is { } time ? time.ToString("t", text.Culture) : T("Any time")) + " · " + x.Title)).ToArray();
            empty.Visibility = matches.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        void Refresh()
        {
            if (!active) return;
            var current = store.Current;
            datedTasks = current is null ? [] : WorkspaceView.AllTasks(current).Where(x => x.BoardId == calendarBoard)
                .Select(TaskData.From).Where(x => x.DueDate is not null).ToArray();
            dueCounts = datedTasks.GroupBy(x => x.DueDate!.Value).ToDictionary(x => x.Key, x => x.Count());
            foreach (var item in dayItems) Decorate(item);
            RefreshSelection();
        }
        calendar.CalendarViewDayItemChanging += (_, args) =>
        {
            if (args.InRecycleQueue) dayItems.Remove(args.Item);
            else { dayItems.Add(args.Item); Decorate(args.Item); }
        };
        calendar.SelectedDatesChanged += (_, _) => RefreshSelection();
        tasks.ItemClick += (_, args) => { chosen = (args.ClickedItem as Choice)?.Id; dialog.Hide(); };
        EventHandler refresh = (_, _) => DispatcherQueue.TryEnqueue(Refresh);
        store.Changed += refresh;
        try { Refresh(); await dialog.ShowAsync(); }
        finally { active = false; store.Changed -= refresh; dayItems.Clear(); }
        if (chosen is { } id && store.Current is { } latest
            && WorkspaceView.AllTasks(latest).FirstOrDefault(x => x.Id == id && x.BoardId == calendarBoard) is { } task)
            await OpenEditorAsync(id, task.Get<Guid>(Fields.ColumnId));
    });
}
