using KanbanTasker.Core;
using KanbanTasker.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace KanbanTasker.Desktop;

public sealed partial class MainWindow
{
    private async Task CheckBoardGroupsAsync(Action<bool, string> check, Func<string, FrameworkElement?, Task> capture)
    {
        check(!preferences.GroupsEnabled && GroupPicker.Visibility == Visibility.Collapsed, "Groups start disabled and occupy no header space");
        preferences.Language = "en"; text = new("en"); ApplyLanguage();
        preferences.Theme = "dark"; ApplyAppearance();
        var path = Path.Combine(SmokeProfile.DirectoryPath, "Workspace", "groups.json");
        await store.CreateAsync(path);
        Guid workBoard = default, secondBoard = default, personalBoard = default, freeBoard = default, task = default, column = default;
        await store.CommitAsync(e =>
        {
            workBoard = e.CreateBoard("Roadmap"); secondBoard = e.CreateBoard("Meeting notes");
            personalBoard = e.CreateBoard("Weekend projects"); freeBoard = e.CreateBoard("Ideas");
            column = WorkspaceView.Columns(e.Document, workBoard).First().Id;
            task = e.SaveTask(new TaskData { BoardId = workBoard, ColumnId = column, Title = "Plan next steps" });
        });
        boardId = workBoard; ErrorBar.IsOpen = false; Render(); await SettleAsync();
        var bytes = WorkspaceJson.Serialize(store.Current!);
        var settings = await Settings();
        var tabs = (Pivot)settings.Content;
        tabs.SelectedIndex = 2; await SettleAsync();
        FindVisual<ToggleSwitch>(settings, "EnableBoardGroups")!.IsOn = true;
        check(preferences.GroupsEnabled && LocalPreferences.Load().GroupsEnabled && GroupPicker.Visibility == Visibility.Visible,
            "Advanced switch enables and persists the group selector");
        check(bytes.SequenceEqual(WorkspaceJson.Serialize(store.Current!)), "Enabling groups alone never rewrites the workspace");
        var name = FindVisual<TextBox>(settings, "GroupName")!;
        var picker = FindVisual<ComboBox>(settings, "ManageGroupPicker")!;
        async Task<Guid> Create(string label)
        {
            name.Text = label; Invoke(FindVisual<Button>(settings, "CreateGroup")!);
            await WaitForAsync(() => settings.IsEnabled && WorkspaceView.Groups(store.Current!).Any(g => g.Get<string>(Fields.Name) == label));
            return WorkspaceView.Groups(store.Current!).Single(g => g.Get<string>(Fields.Name) == label).Id;
        }
        var work = await Create("Work"); var personal = await Create("Personal"); var empty = await Create("Later");
        check(store.Current!.Groups.Count == 3, "Advanced creates synced groups");
        name.Text = " work "; Invoke(FindVisual<Button>(settings, "CreateGroup")!);
        await WaitForAsync(() => settings.IsEnabled && FindVisual<TextBlock>(settings, "GroupError")!.Text.Length > 0);
        check(store.Current!.Groups.Count == 3, "Duplicate group names show validation without adding a group");
        picker.SelectedItem = ((Choice[])picker.ItemsSource).Single(g => g.Id == work);
        name.Text = "Office"; Invoke(FindVisual<Button>(settings, "RenameGroup")!);
        await WaitForAsync(() => settings.IsEnabled && store.Current!.Groups.Single(g => g.Id == work).Get<string>(Fields.Name) == "Office");
        check(((GroupChoice[])GroupPicker.ItemsSource).Any(g => g.Id == work && g.Name == "Office"), "Renaming updates the header without changing group identity");
        foreach (var language in TextCatalog.Languages)
        {
            var appearance = (StackPanel)((ScrollViewer)((PivotItem)tabs.Items[1]).Content).Content;
            var languagePicker = appearance.Children.OfType<ComboBox>().Single(c => c.Name == "LanguagePicker");
            languagePicker.SelectedItem = TextCatalog.Languages.First(l => l.Code == language.Code);
            await SettleAsync();
            check(((PivotItem)tabs.Items[2]).Header.ToString() == T("Advanced")
                && FindVisual<ToggleSwitch>(settings, "EnableBoardGroups")!.Header.ToString() == T("Enable board groups"),
                "Advanced labels switch language: " + language.Code);
        }
        await capture("groups-advanced", FindVisual<Border>(settings, "BackgroundElement"));
        settings.Hide(); await WaitForAsync(() => !working);
        preferences.Language = "en"; text = new("en"); ApplyLanguage();
        await store.CommitAsync(e => { e.AssignBoardGroup(workBoard, work); e.AssignBoardGroup(secondBoard, work); e.AssignBoardGroup(personalBoard, personal); });
        Render(); await Select(work);
        check(BoardIds().ToHashSet().SetEquals(new[] { workBoard, secondBoard }), "Group selection filters board choices");
        check(LocalPreferences.Load().SelectedGroup == work && LocalPreferences.Load().GroupWorkspaceId == document!.DocumentId,
            "Group selection is persisted with its workspace identity");
        check(BoundsInRoot(GroupPicker).Right <= BoundsInRoot(BoardPicker).Left, "Group dropdown is left of the board dropdown");
        await capture("groups-board", null);
        await Select(Guid.Empty);
        check(BoardIds().SequenceEqual(new[] { freeBoard }), "Ungrouped contains only unassigned boards");
        await Select(null); check(BoardIds().Length == 4, "All boards includes every group");
        await Select(empty);
        check(boardId is null && BoardIds().Length == 0 && WelcomeTitle.Text == T("No boards in this group") && !CalendarButton.IsEnabled,
            "Empty groups show a useful create-board state");
        var newBoardDialog = BoardDialogAsync(null); await SettleAsync();
        var createBoard = OpenDialog();
        check((FindVisual<ComboBox>(createBoard, "BoardGroupAssignment")!.SelectedItem as GroupChoice)?.Id == empty,
            "New board form defaults to the active group");
        FindAllVisual<TextBox>(createBoard).First(t => t.Header?.ToString() == T("Board name")).Text = "Future plans";
        Invoke(FindVisual<Button>(createBoard, "PrimaryButton")!); await newBoardDialog; await SettleAsync();
        check(BoardIds().Length == 1 && WorkspaceView.BoardGroupId(store.Current!, store.Current!.Boards.Single(b => b.Id == boardId)) == empty,
            "Creating a board in an empty group persists its assignment");

        await Select(work); boardId = workBoard; Render();
        await OpenEditorAsync(task, column); TaskTitle.Text = "Unsaved group-switch draft";
        Choose(personal); await SettleAsync();
        check(OpenDialog().Title.ToString() == T("Discard task draft?"), "Changing groups guards unsaved task edits");
        Invoke(FindVisual<Button>(OpenDialog(), "CloseButton")!); await WaitForAsync(() => !working);
        check(preferences.SelectedGroup == work && TaskTitle.Text == "Unsaved group-switch draft" && HasDraftChanges,
            "Cancelling a group switch restores the selector and retains the draft");
        settings = await Settings(); ((Pivot)settings.Content).SelectedIndex = 2; await SettleAsync();
        bytes = WorkspaceJson.Serialize(store.Current!);
        FindVisual<ToggleSwitch>(settings, "EnableBoardGroups")!.IsOn = false;
        check(GroupPicker.Visibility == Visibility.Collapsed && BoardIds().Length == 5 && HasDraftChanges,
            "Disabling groups shows all boards and retains the current draft");
        check(bytes.SequenceEqual(WorkspaceJson.Serialize(store.Current!)), "Disabling groups keeps all assignments and file contents");
        FindVisual<ToggleSwitch>(settings, "EnableBoardGroups")!.IsOn = true;
        settings.Hide(); await WaitForAsync(() => !working);
        CloseEditor(); await Select(work); boardId = workBoard; Render();
        await OpenEditorAsync(task, column); TaskTitle.Text = "Keep this local draft";
        var incoming = store.Current!; var clock = new ChangeClock(Guid.NewGuid()); clock.Observe(incoming);
        new WorkspaceEditor(incoming, clock).AssignBoardGroup(workBoard, personal);
        await File.WriteAllBytesAsync(path, WorkspaceJson.Serialize(incoming)); await store.RefreshAsync(); await SettleAsync();
        check(preferences.SelectedGroup is null && boardId == workBoard && HasDraftChanges && TaskTitle.Text == "Keep this local draft",
            "Incoming reassignment reveals all boards instead of hiding an open draft");
        CloseEditor(); await Select(personal);
        boardId = workBoard; Render();
        var editBoardDialog = BoardDialogAsync(workBoard); await SettleAsync();
        var editBoard = OpenDialog(); var assignment = FindVisual<ComboBox>(editBoard, "BoardGroupAssignment")!;
        assignment.SelectedItem = ((GroupChoice[])assignment.ItemsSource).Single(g => g.Id == work);
        Invoke(FindVisual<Button>(editBoard, "PrimaryButton")!); await editBoardDialog; await SettleAsync();
        check(preferences.SelectedGroup == work && boardId == workBoard && BoardIds().Contains(workBoard),
            "Editing a board's group saves it and keeps the board visible");

        AppWindow.Resize(new Windows.Graphics.SizeInt32(950, 800)); await SettleAsync();
        check(Grid.GetRow(BoardSelectors) == 1 && BoundsInRoot(GroupPicker).Right <= BoundsInRoot(BoardPicker).Left
            && BoundsInRoot(BoardSelectors).Right <= Root.ActualWidth, "Narrow windows keep both selectors visible in a second header row");
        await capture("groups-narrow", null);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1420, 900)); await SettleAsync();
        settings = await Settings(); ((Pivot)settings.Content).SelectedIndex = 2; await SettleAsync();
        picker = FindVisual<ComboBox>(settings, "ManageGroupPicker")!;
        picker.SelectedItem = ((Choice[])picker.ItemsSource).Single(g => g.Id == work);
        var delete = FindVisual<Button>(settings, "DeleteGroup")!;
        Invoke(delete);
        check(store.Current!.Groups.Single(g => g.Id == work).Deleted is null, "Group deletion requires explicit confirmation");
        Invoke(delete); await WaitForAsync(() => settings.IsEnabled && store.Current!.Groups.Single(g => g.Id == work).Deleted is not null);
        check(WorkspaceView.BoardsInGroup(store.Current!, null).Any(b => b.Id == workBoard)
            && WorkspaceView.AllTasks(store.Current!).Any(t => t.Id == task), "Confirmed deletion retains all boards and tasks");
        settings.Hide(); await WaitForAsync(() => !working);
        check(preferences.SelectedGroup is null && BoardIds().Length == 5, "Deleting the active group falls back to All boards");
        await Select(personal);
        await store.CreateAsync(Path.Combine(SmokeProfile.DirectoryPath, "Workspace", "other-groups.json")); Render(); await SettleAsync();
        check(preferences.SelectedGroup is null && ((GroupChoice[])GroupPicker.ItemsSource).Length == 2,
            "Switching workspace resets group selection and never mixes group lists");
        await store.OpenAsync(path); Render(); await SettleAsync();
        check(WorkspaceView.Groups(document!).Count() == 2 && WorkspaceView.Boards(document!).Count() == 5,
            "Reopening restores synced groups and every board");

        Guid[] BoardIds() => ((Choice[])BoardPicker.ItemsSource).Select(b => b.Id).ToArray();
        void Choose(Guid? id) => GroupPicker.SelectedItem = ((GroupChoice[])GroupPicker.ItemsSource).Single(g => g.Id == id);
        async Task Select(Guid? id) { Choose(id); await WaitForAsync(() => !working); await SettleAsync(); }
        ContentDialog OpenDialog() => VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot)
            .Select(p => FindVisual<ContentDialog>(p.Child)).First(d => d is not null)!;
        async Task<ContentDialog> Settings() { Settings_Click(this, new RoutedEventArgs()); await SettleAsync(); return OpenDialog(); }
        static void Invoke(ButtonBase button)
        {
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(button);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
        }
    }
}
