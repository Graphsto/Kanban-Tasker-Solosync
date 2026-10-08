using KanbanTasker.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KanbanTasker.Desktop;

public sealed partial class MainWindow
{
    private Action PopulateAdvancedSettings(StackPanel panel, Action<bool> setBusy)
    {
        var enabled = new ToggleSwitch { Name = "EnableBoardGroups", Header = T("Enable board groups"), IsOn = preferences.GroupsEnabled,
            OnContent = T("On"), OffContent = T("Off") };
        var description = new TextBlock
        {
            Text = T("Show a group selector beside your boards, for example Work and Personal. This switch applies only to this device; groups are saved in the data file."),
            TextWrapping = TextWrapping.Wrap
        };
        var manager = new StackPanel { Spacing = 12 };
        var picker = new ComboBox { Name = "ManageGroupPicker", Header = T("Board group"), DisplayMemberPath = "Name",
            PlaceholderText = T("Choose a group"), HorizontalAlignment = HorizontalAlignment.Stretch };
        var name = new TextBox { Name = "GroupName", Header = T("Group name") };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var create = new Button { Name = "CreateGroup", Content = T("Create") };
        var rename = new Button { Name = "RenameGroup", Content = T("Rename") };
        var delete = new Button { Name = "DeleteGroup", Content = T("Delete") };
        var hint = new TextBlock { Text = T("Deleting a group keeps its boards and tasks under Ungrouped."), TextWrapping = TextWrapping.Wrap };
        var error = new TextBlock { Name = "GroupError", TextWrapping = TextWrapping.Wrap };
        actions.Children.Add(create); actions.Children.Add(rename); actions.Children.Add(delete);
        manager.Children.Add(picker); manager.Children.Add(name); manager.Children.Add(actions); manager.Children.Add(hint);
        panel.Children.Add(enabled); panel.Children.Add(description); panel.Children.Add(manager); panel.Children.Add(error);
        var githubGroups=new ComboBox { Name="ManageGitHubGroup",Header=T("GitHub project groups"),DisplayMemberPath="Name",HorizontalAlignment=HorizontalAlignment.Stretch };
        var removeGithubGroup=new Button { Name="RemoveGitHubGroup",Content=T("Remove GitHub group") };
        var githubGroupHint=new TextBlock { Text=T("Removing a GitHub group keeps its linked boards under Ungrouped. Nothing is deleted on GitHub."),TextWrapping=TextWrapping.Wrap };
        panel.Children.Add(githubGroups); panel.Children.Add(removeGithubGroup); panel.Children.Add(githubGroupHint);
        string? confirmGithubGroup=null;
        void RefreshGitHubGroups()
        {
            var choices=github.Registry.GroupedProjects.Where(id => github.Projects.ContainsKey(id))
                .Select(id => new Choice(KanbanTasker.Core.GitHub.GitHubIdentity.Group(id),github.Projects[id].Title,true)).ToArray();
            var previous=(githubGroups.SelectedItem as Choice)?.Id;
            githubGroups.ItemsSource=choices;
            githubGroups.SelectedItem=choices.FirstOrDefault(g => g.Id == previous) ?? choices.FirstOrDefault();
            removeGithubGroup.IsEnabled=githubGroups.SelectedItem is Choice;
            githubGroups.Visibility=removeGithubGroup.Visibility=githubGroupHint.Visibility=choices.Length>0 ? Visibility.Visible : Visibility.Collapsed;
        }
        githubGroups.SelectionChanged += (_,_) => { confirmGithubGroup=null; removeGithubGroup.Content=T("Remove GitHub group"); };
        removeGithubGroup.Click += async (_,_) =>
        {
            if (githubGroups.SelectedItem is not Choice choice) return;
            var project=github.Registry.GroupedProjects.First(id => KanbanTasker.Core.GitHub.GitHubIdentity.Group(id) == choice.Id);
            if (confirmGithubGroup != project) { confirmGithubGroup=project; removeGithubGroup.Content=T("Confirm delete"); return; }
            setBusy(true);
            try { await github.UngroupAsync(project); RefreshGitHubGroups(); Render(); }
            catch (IOException ex) { error.Text=text.TranslateDiagnostic(ex.Message); }
            finally { setBusy(false); }
        };
        RefreshGitHubGroups();
        Guid? confirmDelete = null;
        bool refreshing = false, busy = false;
        void ResetConfirmation() { confirmDelete = null; delete.Content = T("Delete"); }
        void Refresh()
        {
            var current = store.Current;
            manager.Visibility = preferences.GroupsEnabled ? Visibility.Visible : Visibility.Collapsed;
            picker.IsEnabled = name.IsEnabled = create.IsEnabled = current is not null && !busy;
            var previous = (picker.SelectedItem as Choice)?.Id;
            var groups = current is null ? [] : WorkspaceView.Groups(current).Select(g => new Choice(g.Id, g.Get<string>(Fields.Name))).ToArray();
            refreshing = true;
            picker.ItemsSource = groups;
            picker.SelectedItem = groups.FirstOrDefault(g => g.Id == previous) ?? groups.FirstOrDefault();
            refreshing = false;
            if ((picker.SelectedItem as Choice)?.Id != previous)
            {
                name.Text = (picker.SelectedItem as Choice)?.Name ?? "";
                ResetConfirmation();
            }
            rename.IsEnabled = delete.IsEnabled = current is not null && !busy && picker.SelectedItem is Choice;
            hint.Text = current is null ? T("Open a data file to manage groups.") : T("Deleting a group keeps its boards and tasks under Ungrouped.");
        }
        enabled.Toggled += (_, _) =>
        {
            if (enabled.IsOn == preferences.GroupsEnabled) return;
            var previous = preferences.GroupsEnabled;
            var previousSelection = preferences.SelectedGroup;
            preferences.GroupsEnabled = enabled.IsOn;
            preferences.SelectedGroup = null;
            try { preferences.Save(); error.Text = ""; Render(); Refresh(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                preferences.GroupsEnabled = previous; preferences.SelectedGroup = previousSelection;
                enabled.IsOn = previous; error.Text = text.TranslateDiagnostic(ex.Message);
            }
        };
        picker.SelectionChanged += (_, _) =>
        {
            if (refreshing) return;
            name.Text = (picker.SelectedItem as Choice)?.Name ?? "";
            rename.IsEnabled = delete.IsEnabled = picker.SelectedItem is Choice;
            ResetConfirmation(); error.Text = "";
        };
        name.TextChanged += (_, _) => ResetConfirmation();
        async Task Commit(Action<WorkspaceEditor> action, Func<Guid?>? select = null)
        {
            if (busy) return;
            busy = true; setBusy(true);
            try
            {
                await store.CommitAsync(action);
                error.Text = ""; ResetConfirmation(); Refresh();
                if (select?.Invoke() is { } id) picker.SelectedItem = ((Choice[])picker.ItemsSource).FirstOrDefault(g => g.Id == id);
                Render();
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            { error.Text = text.TranslateDiagnostic(ex.Message); }
            finally { busy = false; setBusy(false); Refresh(); }
        }
        create.Click += async (_, _) =>
        {
            var value = name.Text;
            Guid? created = null;
            await Commit(editor => created = editor.CreateGroup(value), () => created);
        };
        rename.Click += async (_, _) =>
        {
            if (picker.SelectedItem is not Choice selected) return;
            var value = name.Text;
            await Commit(editor => editor.RenameGroup(selected.Id, value));
        };
        delete.Click += async (_, _) =>
        {
            if (picker.SelectedItem is not Choice selected) return;
            if (confirmDelete != selected.Id)
            {
                confirmDelete = selected.Id; delete.Content = T("Confirm delete");
                error.Text = T("Click Confirm delete to remove this group. Its boards and tasks will be kept.");
                return;
            }
            await Commit(editor => editor.DeleteGroup(selected.Id));
        };
        Refresh();
        return Refresh;
    }
}
