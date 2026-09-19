using KanbanTasker.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace KanbanTasker.Desktop;

public sealed partial class MainWindow
{
    private void RenderBoard()
    {
        if (boardDrag is not null) return;
        var offset = BoardScroll.HorizontalOffset;
        ColumnsPanel.Children.Clear();
        columnLists.Clear();
        if (document is null || boardId is null) return;
        foreach (var column in WorkspaceView.Columns(document, boardId.Value)) ColumnsPanel.Children.Add(BuildColumn(column));
        var add = new Button { Content = T("+ New column"), VerticalAlignment = VerticalAlignment.Top, Margin = new(0,4,8,0) };
        add.Click += async (_, _) => await RunAsync(() => ColumnDialogAsync(null));
        ColumnsPanel.Children.Add(add);
        DispatcherQueue.TryEnqueue(() => { if (!closed) BoardScroll.ChangeView(offset, null, null, true); });
    }
    private FrameworkElement BuildColumn(EntityRecord column)
    {
        var collapsed = preferences.CollapsedColumns.Contains(column.Id);
        var tasks = WorkspaceView.Tasks(document!, column.Id).ToList();
        var name = column.Get<string>(Fields.Name);
        var limit = column.Get<int>(Fields.Limit);
        var border = new Border
        {
            Width = collapsed ? 64 : 300, CornerRadius = new(8), BorderThickness = new(1),
            Background = Brush("LayerFillColorDefaultBrush"), BorderBrush = Brush("CardStrokeColorDefaultBrush"),
            Padding = new(10), Tag = column.Id
        };
        AutomationProperties.SetName(border, T("{0} column", name));
        var grid = new Grid { RowSpacing = 10 };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        border.Child = grid;
        var header = new StackPanel { Spacing = 8 };
        var title = new TextBlock
        {
            Text = name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            Padding = new(2,4,2,4)
        };
        var dragHandle = new Border { Child = title, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        ToolTipService.SetToolTip(dragHandle, T("Drag to move this column"));
        EnableBoardDrag(dragHandle, column.Id, isColumn: true, visual: border);
        header.Children.Add(dragHandle);
        var commands = new StackPanel { Orientation = collapsed ? Orientation.Vertical : Orientation.Horizontal, Spacing = 4 };
        var collapse = new Button { Content = collapsed ? "›" : "‹", Padding = new(10,3,10,3) };
        AutomationProperties.SetName(collapse, collapsed ? T("Expand {0}", name) : T("Collapse {0}", name));
        ToolTipService.SetToolTip(collapse, collapsed ? T("Expand column") : T("Collapse column"));
        collapse.Click += async (_, _) => await RunAsync(() =>
        {
            if (!preferences.CollapsedColumns.Remove(column.Id)) preferences.CollapsedColumns.Add(column.Id);
            preferences.Save(); RenderBoard(); return Task.CompletedTask;
        });
        commands.Children.Add(collapse);
        if (!collapsed)
        {
            var add = new Button { Content = "+", Padding = new(10,3,10,3) };
            AutomationProperties.SetName(add, T("New task in {0}", name));
            add.Click += async (_, _) => await OpenEditorAsync(null, column.Id);
            commands.Children.Add(add);
        }
        var menu = new Button { Content = "⋯", Padding = new(10,3,10,3) };
        AutomationProperties.SetName(menu, T("{0} column actions", name));
        var flyout = new MenuFlyout();
        var columns = WorkspaceView.Columns(document!, column.BoardId).Select(x => x.Id).ToList();
        var index = columns.IndexOf(column.Id);
        flyout.Items.Add(MenuItem(T("Move left"), async () => await store.CommitAsync(e => e.MoveColumn(column.Id, index - 1)), index > 0));
        flyout.Items.Add(MenuItem(T("Move right"), async () => await store.CommitAsync(e => e.MoveColumn(column.Id, index + 1)), index < columns.Count - 1));
        flyout.Items.Add(MenuItem(T("Edit column"), () => ColumnDialogAsync(column.Id)));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(MenuItem(T("Delete column"), async () =>
        {
            if (await ConfirmAsync(T("Delete column?"), T("Delete “{0}” and all its tasks?", name), T("Delete")))
                await store.CommitAsync(e => e.DeleteColumn(column.Id));
        }));
        menu.Flyout = flyout; commands.Children.Add(menu); header.Children.Add(commands);
        var count = new TextBlock { Text = limit == 0 ? T("{0} tasks", tasks.Count) : $"{tasks.Count} / {limit}", FontSize = 12, TextWrapping = TextWrapping.Wrap };
        if (limit > 0 && tasks.Count >= limit)
        {
            count.Text += T(" · limit reached");
            count.Foreground = Brush("SystemFillColorCriticalBrush");
        }
        header.Children.Add(count); grid.Children.Add(header);
        if (!collapsed)
        {
            var list = new ListView
            {
                SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true,
                CanDragItems = false, CanReorderItems = false,
                // A board refresh rebuilds these lists. Disable WinUI's entrance/content/
                // reorder transitions so unchanged cards do not fade or slide in again.
                // Our pointer-following drag preview is handled separately.
                ItemContainerTransitions = new(),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
            };
            columnLists[column.Id] = list;
            ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
            foreach (var task in tasks) list.Items.Add(new ListViewItem
            {
                Content = BuildCard(TaskData.From(task)), Tag = task.Id, Margin = new(0,0,0,8), Padding = new(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            });
            list.ItemClick += async (_, e) =>
            {
                if (e.ClickedItem is FrameworkElement { Tag: Guid id }) await OpenEditorAsync(id, column.Id);
            };
            Grid.SetRow(list, 1); grid.Children.Add(list);
            if (tasks.Count == 0)
            {
                var hint = new TextBlock { Text = T("Drop a task here"), FontSize = 12, IsHitTestVisible = false,
                    Foreground = Brush("TextFillColorSecondaryBrush"), Margin = new(8,16,8,0), VerticalAlignment = VerticalAlignment.Top };
                Grid.SetRow(hint, 1); grid.Children.Add(hint);
            }
        }
        return border;
    }
    private FrameworkElement BuildCard(TaskData task)
    {
        var body = new StackPanel { Spacing = 7 };
        body.Children.Add(new TextBlock { Text = task.Title, TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        if (!string.IsNullOrWhiteSpace(task.Description)) body.Children.Add(new TextBlock
        { Text = task.Description, TextWrapping = TextWrapping.Wrap, MaxLines = 4, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brush("TextFillColorSecondaryBrush") });
        if (task.Tags.Length > 0) body.Children.Add(new TextBlock { Text = string.Join("  ·  ", task.Tags), FontSize = 12, TextWrapping = TextWrapping.Wrap });
        var priority = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        priority.Children.Add(new Border { Width = 4, Height = 12, CornerRadius = new(2),
            Background = Brush(task.Priority switch { "High" => "SystemFillColorCriticalBrush", "Medium" => "SystemFillColorCautionBrush", _ => "SystemFillColorSuccessBrush" }) });
        priority.Children.Add(new TextBlock { Text = T(task.Priority + " priority"), FontSize = 12, Foreground = Brush("TextFillColorSecondaryBrush") });
        body.Children.Add(priority);
        if (task.DueDate is { } due)
            body.Children.Add(new TextBlock { Text = T("Due {0:d}", due) + (task.DueTime is { } time ? " · " + time.ToString("t", text.Culture) : ""), FontSize = 12 });
        var card = new Border { Child = body, Tag = task.Id, Padding = new(12), CornerRadius = new(6), BorderThickness = new(1),
            BorderBrush = Brush("CardStrokeColorDefaultBrush"), Background = Brush("CardBackgroundFillColorDefaultBrush") };
        AutomationProperties.SetName(card, task.Title + ", " + T(task.Priority + " priority"));
        EnableBoardDrag(card, task.Id, isColumn: false);
        var menu = new MenuFlyout();
        menu.Items.Add(MenuItem(T("Edit task"), () => OpenEditorAsync(task.Id, task.ColumnId)));
        var cards = WorkspaceView.Tasks(document!, task.ColumnId).Select(x => x.Id).ToList();
        var index = cards.IndexOf(task.Id);
        menu.Items.Add(MenuItem(T("Move up"), () => store.CommitAsync(e => e.MoveTask(task.Id, task.ColumnId, index - 1)), index > 0));
        menu.Items.Add(MenuItem(T("Move down"), () => store.CommitAsync(e => e.MoveTask(task.Id, task.ColumnId, index + 1)), index < cards.Count - 1));
        var moveTo = new MenuFlyoutSubItem { Text = T("Move to column") };
        foreach (var column in WorkspaceView.Columns(document!, task.BoardId))
            moveTo.Items.Add(MenuItem(column.Get<string>(Fields.Name), () => store.CommitAsync(e => e.MoveTask(task.Id, column.Id, int.MaxValue)), column.Id != task.ColumnId));
        menu.Items.Add(moveTo);
        menu.Items.Add(MenuItem(T("Delete task"), async () =>
        {
            if (await ConfirmAsync(T("Delete task?"), T("Delete “{0}”?", task.Title), T("Delete"))) await store.CommitAsync(e => e.DeleteTask(task.Id));
        }));
        card.ContextFlyout = menu;
        return card;
    }
    private MenuFlyoutItem MenuItem(string title, Func<Task> action, bool enabled = true)
    {
        var item = new MenuFlyoutItem { Text = title, IsEnabled = enabled };
        item.Click += async (_, _) => await RunAsync(action);
        return item;
    }
}
