using KanbanTasker.Core;
using KanbanTasker.Core.GitHub;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KanbanTasker.Desktop;

public sealed partial class MainWindow
{
    private record GroupChoice(Guid? Id, string Name, bool IsGitHub = false)
    {
        public Visibility GitHubVisibility => IsGitHub ? Visibility.Visible : Visibility.Collapsed;
    }
    private bool selectingGroup;

    private GroupChoice[] BoardGroupChoices(WorkspaceDocument workspace) =>
        [new(null, T("Ungrouped")), .. WorkspaceView.Groups(workspace).Select(g => new GroupChoice(g.Id, g.Get<string>(Fields.Name)))];

    private IEnumerable<EntityRecord> FilteredBoards(WorkspaceDocument workspace) =>
        !preferences.GroupsEnabled || preferences.SelectedGroup is null ? WorkspaceView.Boards(workspace)
            : WorkspaceView.BoardsInGroup(workspace, preferences.SelectedGroup == Guid.Empty ? null : preferences.SelectedGroup);

    private Choice[] RenderGroupPicker()
    {
        var local = store.Current;
        GroupPicker.Visibility = preferences.GroupsEnabled ? Visibility.Visible : Visibility.Collapsed;
        GroupPicker.IsEnabled = local is not null || github.Registry.Links.Count > 0;
        UpdateGroupSelectorLayout();
        if (local is null && github.Registry.Links.Count == 0) { GroupPicker.ItemsSource = null; return []; }
        var remoteGroups = github.Registry.GroupedProjects.Where(id => github.Projects.ContainsKey(id))
            .Select(id => new GroupChoice(GitHubIdentity.Group(id),github.Projects[id].Title,true)).ToArray();
        if (local is not null && preferences.GroupWorkspaceId != local.DocumentId)
        {
            preferences.GroupWorkspaceId = local.DocumentId;
            if (!remoteGroups.Any(g => g.Id == preferences.SelectedGroup)) preferences.SelectedGroup = null;
        }
        GroupChoice[] groups = [new(null, T("All boards")), new(Guid.Empty, T("Ungrouped")),
            .. (local is null ? [] : WorkspaceView.Groups(local).Select(g => new GroupChoice(g.Id, g.Get<string>(Fields.Name)))), .. remoteGroups];
        if (!groups.Any(g => g.Id == preferences.SelectedGroup)) preferences.SelectedGroup = null;
        // Incoming reassignment must not hide the board underneath an open task draft.
        if (TaskPane.IsPaneOpen && boardId == draftBoardId
            && local is not null && WorkspaceView.Boards(local).Any(b => b.Id == boardId)
            && !FilteredBoards(local).Any(b => b.Id == boardId)) preferences.SelectedGroup = null;
        GroupPicker.ItemsSource = groups;
        GroupPicker.SelectedItem = groups.First(g => g.Id == preferences.SelectedGroup);
        var choices = (local is null ? [] : FilteredBoards(local).Select(b => new Choice(b.Id,b.Get<string>(Fields.Name))))
            .Concat(GitHubChoices().Where(b => !preferences.GroupsEnabled || preferences.SelectedGroup is null
                || (preferences.SelectedGroup == Guid.Empty ? GitHubGroup(b.Id) is null : GitHubGroup(b.Id) == preferences.SelectedGroup))).ToArray();
        if (TaskPane.IsPaneOpen && boardId == draftBoardId && IsGitHubBoard && !choices.Any(b => b.Id == boardId))
        { preferences.SelectedGroup=null; GroupPicker.SelectedItem=groups.First(g => g.Id is null); return (local is null ? [] : WorkspaceView.Boards(local).Select(b => new Choice(b.Id,b.Get<string>(Fields.Name)))).Concat(GitHubChoices()).ToArray(); }
        return choices;
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
