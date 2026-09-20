using KanbanTasker.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KanbanTasker.Desktop;

public sealed partial class MainWindow
{
    private record GroupChoice(Guid? Id, string Name);
    private bool selectingGroup;

    private GroupChoice[] BoardGroupChoices(WorkspaceDocument workspace) =>
        [new(null, T("Ungrouped")), .. WorkspaceView.Groups(workspace).Select(g => new GroupChoice(g.Id, g.Get<string>(Fields.Name)))];

    private IEnumerable<EntityRecord> FilteredBoards(WorkspaceDocument workspace) =>
        !preferences.GroupsEnabled || preferences.SelectedGroup is null ? WorkspaceView.Boards(workspace)
            : WorkspaceView.BoardsInGroup(workspace, preferences.SelectedGroup == Guid.Empty ? null : preferences.SelectedGroup);

    private Choice[] RenderGroupPicker()
    {
        GroupPicker.Visibility = preferences.GroupsEnabled ? Visibility.Visible : Visibility.Collapsed;
        GroupPicker.IsEnabled = document is not null;
        UpdateGroupSelectorLayout();
        if (document is null) { GroupPicker.ItemsSource = null; return []; }
        if (preferences.GroupWorkspaceId != document.DocumentId)
        {
            preferences.GroupWorkspaceId = document.DocumentId;
            preferences.SelectedGroup = null;
        }
        GroupChoice[] groups = [new(null, T("All boards")), new(Guid.Empty, T("Ungrouped")),
            .. WorkspaceView.Groups(document).Select(g => new GroupChoice(g.Id, g.Get<string>(Fields.Name)))];
        if (!groups.Any(g => g.Id == preferences.SelectedGroup)) preferences.SelectedGroup = null;
        // Incoming reassignment must not hide the board underneath an open task draft.
        if (TaskPane.IsPaneOpen && boardId == draftBoardId
            && WorkspaceView.Boards(document).Any(b => b.Id == boardId)
            && !FilteredBoards(document).Any(b => b.Id == boardId)) preferences.SelectedGroup = null;
        GroupPicker.ItemsSource = groups;
        GroupPicker.SelectedItem = groups.First(g => g.Id == preferences.SelectedGroup);
        return FilteredBoards(document).Select(b => new Choice(b.Id, b.Get<string>(Fields.Name))).ToArray();
    }

    private void UpdateGroupSelectorLayout()
    {
        var secondRow = preferences.GroupsEnabled && Root.ActualWidth < 1250;
        Grid.SetRow(BoardSelectors, secondRow ? 1 : 0);
        Grid.SetColumn(BoardSelectors, secondRow ? 0 : 1);
        Grid.SetColumnSpan(BoardSelectors, secondRow ? 3 : 1);
        BoardSelectors.MaxWidth = preferences.GroupsEnabled ? 512 : 340;
        BoardSelectors.Margin = secondRow ? new Thickness(0, 12, 0, 0) : new Thickness(0);
    }

    private async void GroupPicker_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (rendering || selectingGroup || working || GroupPicker.SelectedItem is not GroupChoice choice
            || choice.Id == preferences.SelectedGroup) return;
        selectingGroup = true; working = true;
        try
        {
            if (await CanDiscardDraftAsync())
            {
                var previous = preferences.SelectedGroup;
                preferences.SelectedGroup = choice.Id;
                try { preferences.Save(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { preferences.SelectedGroup = previous; ShowError(ex.Message); return; }
                CloseEditor();
            }
        }
        finally { selectingGroup = false; working = false; Render(); }
    }

    private void RevealBoardGroup(Guid id)
    {
        var current = store.Current;
        if (current is null || !preferences.GroupsEnabled || preferences.SelectedGroup is null) return;
        var board = WorkspaceView.Boards(current).FirstOrDefault(b => b.Id == id);
        if (board is not null && !FilteredBoards(current).Any(b => b.Id == id))
            preferences.SelectedGroup = WorkspaceView.BoardGroupId(current, board) ?? Guid.Empty;
    }
}
