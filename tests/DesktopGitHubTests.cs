using KanbanTasker.Core;
using KanbanTasker.Core.GitHub;
using KanbanTasker.Testing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace KanbanTasker.Desktop;

public sealed partial class MainWindow
{
    private async Task CheckGitHubBoardsAsync(Action<bool,string> check,Func<string,FrameworkElement?,Task> capture)
    {
        check(githubTimer.IsEnabled && githubTimer.Interval==TimeSpan.FromSeconds(5),"Automatic GitHub polling starts with the application at five-second intervals");
        githubTimer.Stop(); github.Changed-=GitHubChanged;
        var api=new GitHubFakeApi();
        api.Snapshot=api.Snapshot with { Cards=api.Snapshot.Cards.Select(c => c.Kind == GitHubCardKind.Issue ? c with { Title="Issue ",Body="First line\nSecond line" } : c).ToArray() };
        github=new(api,new GitHubStorage(System.IO.Path.Combine(SmokeProfile.DirectoryPath,"GitHubFixture")));
        github.Changed+=GitHubChanged;
        await github.AccountAsync();
        CloseEditor(); preferences.GroupsEnabled=false; preferences.SelectedGroup=null;
        var code=new GitHubDeviceCode("synthetic-device-code","TEST-CODE","https://github.com/login/device",5,900);
        var cancelledAuthorization=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken authorizationToken=default;
        var cancelledSignIn=WaitForGitHubSignInAsync(code,ct => { authorizationToken=ct; return cancelledAuthorization.Task.WaitAsync(ct); });
        await SettleAsync(); PressGitHubDialog(GitHubTestDialog(),"CloseButton");
        check(!await cancelledSignIn && authorizationToken.IsCancellationRequested,"Cancelling the sign-in dialog stops polling without opening a picker");
        var failedAuthorization=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failedSignIn=WaitForGitHubSignInAsync(code,ct => failedAuthorization.Task.WaitAsync(ct));
        await SettleAsync(); failedAuthorization.SetException(new GitHubApiException("Synthetic authorization failure"));
        var expectedFailure=false;
        try { await failedSignIn; } catch (GitHubApiException) { expectedFailure=true; }
        check(expectedFailure && !VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Any(p => FindVisual<ContentDialog>(p.Child) is not null),"Failed authorization closes the device dialog without masking its error");
        var authorization=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task AuthorizeAndPickAsync()
        {
            if (await WaitForGitHubSignInAsync(code,ct => authorization.Task.WaitAsync(ct))) await PickGitHubProjectAsync(api);
        }
        var linking=AuthorizeAndPickAsync(); await SettleAsync();
        check(GitHubTestDialog().Title?.ToString()==T("Sign in with GitHub"),"Device authorization displays the sign-in dialog");
        authorization.SetResult(true);
        await WaitForAsync(() => VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Any(p =>
            FindVisual<ContentDialog>(p.Child) is { } dialog && dialog.Title?.ToString()==T("Link GitHub project")
            && FindVisual<ComboBox>(dialog,"GitHubOrganization") is { IsLoaded:true }));
        var picker=GitHubTestDialog();
        check(picker.Title?.ToString()==T("Link GitHub project"),"Browser authorization automatically opens the project picker without a second WinRT completion handler");
        FindVisual<ComboBox>(picker,"GitHubOrganization")!.SelectedIndex=0;
        FindVisual<ComboBox>(picker,"GitHubProject")!.SelectedIndex=0;
        await SettleAsync();
        var detected=FindVisual<ListView>(picker,"GitHubViews")!;
        check(detected.Items.Count==3 && detected.SelectedItems.Count==2,"Project picker detects supported Status views and lists other views as unsupported");
        check(detected.ContainerFromIndex(2) is ListViewItem { IsEnabled:false },"Unsupported views are visibly disabled in the picker");
        detected.SelectedItems.Clear(); detected.SelectedItems.Add(detected.Items[2]); await SettleAsync();
        check(detected.SelectedItems.Count==0 && !picker.IsPrimaryButtonEnabled && github.Registry.Links.Count==0,"Unsupported selection is rejected and linking stays disabled");
        detected.SelectedItems.Clear(); detected.SelectedItems.Add(detected.Items[0]);
        check(picker.IsPrimaryButtonEnabled,"Selecting a supported Status view enables linking");
        PressGitHubDialog(picker,"PrimaryButton"); await linking;
        check(github.Registry.Links.Count==1 && !preferences.GroupsEnabled,"Flat-mode linking imports only the selected view without enabling groups");
        await github.LinkAsync(api.Snapshot,[1,2],false);
        var local=store.Current;
        var before=local is null ? null : WorkspaceJson.Serialize(local);
        preferences.GroupsEnabled=false; preferences.SelectedGroup=null;
        boardId=GitHubIdentity.Board("P1",1); Render(); await SettleAsync();
        check(IsGitHubBoard && (local is null || store.Current!.DocumentId == local.DocumentId),"GitHub selection uses a separate data source");
        check(((Choice[])BoardPicker.ItemsSource).Count(c => c.IsGitHub)==2,"GitHub views appear together in the board selector");
        check(local is null || ((Choice[])BoardPicker.ItemsSource).Any(c => !c.IsGitHub),"Local boards remain alongside GitHub boards");
        check(((Choice[])BoardPicker.ItemsSource).Where(c => c.IsGitHub).All(c => c.GitHubVisibility==Visibility.Visible),"GitHub choices carry an accessible icon");
        check(!CalendarButton.IsEnabled && !EditBoardMenuItem.IsEnabled,"GitHub boards disable local calendar and board settings");
        var draft=GitHubIdentity.Card("P1","draft"); var issue=GitHubIdentity.Card("P1","issue"); var todo=GitHubIdentity.Column("P1","todo");
        await RunAsync(() => OpenEditorAsync(issue,todo,allowDuringAction:true));
        check(TaskTitle.IsReadOnly && TaskDescription.IsReadOnly && TaskColumn.IsEnabled,"Issue contents are read-only while its Status can be changed");
        check(DeleteTaskButton.Visibility==Visibility.Collapsed,"Issue removal is not available");
        check(TaskPriority.Visibility==Visibility.Collapsed && DateInformation.Visibility==Visibility.Collapsed,"GitHub editor hides local-only fields");
        SaveTask_Click(this,new RoutedEventArgs()); await WaitForAsync(() => !working);
        check(!TaskPane.IsPaneOpen && api.Writes==0,"Presentation normalization of issue text never sends an issue content write");
        CloseEditor(); await OpenEditorAsync(draft,todo);
        check(!TaskTitle.IsReadOnly && !TaskDescription.IsReadOnly && SaveTaskButton.IsEnabled,"Online draft contents can be edited");
        TaskTitle.Text="Unsaved online input";
        api.Offline=true; try { await github.RefreshAsync("P1"); } catch (GitHubApiException) { }
        Render(); await SettleAsync();
        check(TaskTitle.Text=="Unsaved online input" && TaskTitle.IsReadOnly && !TaskColumn.IsEnabled && !SaveTaskButton.IsEnabled,"Disconnect preserves and locks the open editor");
        await capture("github-offline-editor",EditorSurface);
        api.Offline=false; await github.RefreshAsync("P1"); Render();
        check(TaskTitle.Text=="Unsaved online input" && !TaskTitle.IsReadOnly && SaveTaskButton.IsEnabled,"Reconnect unlocks preserved input after refresh");
        api.Snapshot=api.Snapshot with { Cards=api.Snapshot.Cards.Where(c => c.Id != "draft").ToArray() };
        await github.RefreshAsync("P1"); Render();
        check(TaskTitle.Text=="Unsaved online input" && TaskTitle.IsReadOnly && !SaveTaskButton.IsEnabled,"An externally removed card freezes its open editor without restoring it");
        api.Snapshot=GitHubFakeApi.Fixture(); await github.RefreshAsync("P1"); Render();
        CloseEditor(); await OpenEditorAsync(draft,todo);
        TaskTitle.Text="Mine"; TaskDescription.Text="Independent body edit";
        api.Snapshot=api.Snapshot with { Cards=api.Snapshot.Cards.Select(c => c.Id == "draft" ? c with { Title="Other client" } : c).ToArray() };
        SaveTask_Click(this,new RoutedEventArgs()); await SettleAsync();
        var conflict=VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Select(p => FindVisual<ContentDialog>(p.Child)).First(x => x is not null)!;
        check(conflict.Title?.ToString()==T("GitHub conflict"),"Conflicting GitHub edits require a visible choice");
        check(TaskTitle.IsReadOnly && !TaskColumn.IsEnabled && !CancelTaskButton.IsEnabled,"An in-flight GitHub write freezes editor changes and cancellation");
        var savingBoard=boardId;
        BoardPicker.SelectedItem=((Choice[])BoardPicker.ItemsSource).First(c => c.Id != savingBoard);
        await OpenEditorAsync(issue,todo);
        check(boardId==savingBoard && githubOriginal?.Id=="draft" && TaskTitle.Text=="Mine","Navigation cannot replace the editor context during a GitHub write");
        await capture("github-conflict",conflict);
        var peer=Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(FindVisual<Button>(conflict,"SecondaryButton")!);
        ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
        await WaitForAsync(() => !working);
        check(api.Snapshot.Cards[0].Title=="Other client" && api.Snapshot.Cards[0].Body=="Independent body edit" && !TaskPane.IsPaneOpen,"Keeping the GitHub field still saves independent editor changes");
        await MoveGitHubCardAsync(draft,GitHubIdentity.Column("P1","done"),0); Render();
        check(api.Snapshot.Cards.First(c => c.Id=="draft").StatusId=="done","GitHub drag/menu commands update project Status");
        linking=PickGitHubProjectAsync(api); await SettleAsync(); picker=GitHubTestDialog();
        FindVisual<ComboBox>(picker,"GitHubOrganization")!.SelectedIndex=0;
        FindVisual<ComboBox>(picker,"GitHubProject")!.SelectedIndex=0;
        await SettleAsync(); FindVisual<CheckBox>(picker,"GitHubProjectGroup")!.IsChecked=true;
        await capture("github-link-project",picker);
        PressGitHubDialog(picker,"PrimaryButton"); await linking;
        check(preferences.GroupsEnabled && github.Registry.GroupedProjects.Contains("P1"),"Project-group linking enables groups and links all supported views");
        check(((GroupChoice[])GroupPicker.ItemsSource).Any(g => g.IsGitHub && g.Name=="Project"),"GitHub project groups appear in the shared group selector");
        check(((Choice[])BoardPicker.ItemsSource).All(c => c.IsGitHub),"Selecting a GitHub group filters its own boards");
        await capture("github-project-group",null);
        await github.UngroupAsync("P1"); preferences.SelectedGroup=Guid.Empty; Render();
        check(((Choice[])BoardPicker.ItemsSource).Count(c => c.IsGitHub)==2,"Removing a GitHub group keeps its boards under Ungrouped");
        preferences.GroupsEnabled=false; preferences.SelectedGroup=null; Render();
        boardId=GitHubIdentity.Board("P1",2); Render();
        check(!CurrentCapabilities().ReorderCards && CurrentCapabilities().MoveCards,"Saved view sorting locks reordering while allowing Status moves");
        boardId=GitHubIdentity.Board("P1",1); Render();
        var beforePolling=api.Snapshot;
        var pollingInterval=githubTimer.Interval;
        try
        {
            // Exercise the real dispatcher tick without spending five seconds per fixture step.
            githubTimer.Interval=TimeSpan.FromMilliseconds(50); githubRefreshTimes.Clear();
            api.Snapshot=api.Snapshot with { Cards=api.Snapshot.Cards.Select(c => c.Id=="draft" ? c with { Title="Changed outside the app" } : c).ToArray() };
            githubTimer.Start();
            await WaitForAsync(() => FindAllVisual<TextBlock>(ColumnsPanel).Any(t => t.Text=="Changed outside the app"));
            check(StatusText.Text==T("GitHub · updated {0:T}",github.Projects["P1"].FetchedAt.ToLocalTime()),"A timer refresh displays external edits and the last successful update time");
            api.Offline=true; githubRefreshTimes.Clear();
            await WaitForAsync(() => GitHubSyncNotice.IsOpen);
            check(!CurrentCapabilities(draft).RemoveCards && GitHubSyncNotice.Message.Contains(T("Updates are retried automatically.")),"Failed background refresh explains the write lock and automatic retry");
            api.Offline=false; githubRefreshTimes.Clear();
            api.Snapshot=api.Snapshot with { Cards=api.Snapshot.Cards.Select(c => c.Id=="draft" ? c with { Title="Recovered automatically" } : c).ToArray() };
            await WaitForAsync(() => !GitHubSyncNotice.IsOpen && FindAllVisual<TextBlock>(ColumnsPanel).Any(t => t.Text=="Recovered automatically"));
            check(CurrentCapabilities(draft).RemoveCards,"The next timer refresh recovers draft removal without a manual refresh");
        }
        finally { githubTimer.Stop(); githubTimer.Interval=pollingInterval; }
        await WaitForGitHubRefreshAsync();
        var draftElement=(Border)columnLists.Values.SelectMany(l => l.Items.Cast<ListViewItem>()).Single(item => (Guid)item.Tag==draft).Content;
        var draftMenu=(MenuFlyout)draftElement.ContextFlyout;
        var removeDraft=draftMenu.Items.OfType<MenuFlyoutItem>().Single(item => item.Text==T("Remove from GitHub project"));
        check(removeDraft.IsEnabled,"The rendered draft context menu allows removal after refresh recovery");
        draftMenu.ShowAt(draftElement); await SettleAsync();
        var removePeer=Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(removeDraft);
        ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)removePeer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
        await WaitForAsync(() => VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Any(p => FindVisual<ContentDialog>(p.Child) is { IsLoaded:true }));
        check(api.Snapshot.Cards.Any(c => c.Id=="draft"),"Draft removal waits for explicit confirmation");
        PressGitHubDialog(GitHubTestDialog(),"PrimaryButton"); await WaitForAsync(() => !working);
        check(!api.Snapshot.Cards.Any(c => c.Id=="draft") && !WorkspaceView.AllTasks(document!).Any(t => t.Id==draft),"Confirming removal updates GitHub and removes the card from the displayed board");
        api.Snapshot=beforePolling; await github.RefreshAsync("P1"); Render();
        if (before is not null) check(before.SequenceEqual(WorkspaceJson.Serialize(store.Current!)),"GitHub actions never alter the local workspace JSON");
        else check(store.Current is null && store.FilePath is null,"GitHub is usable without opening or creating a local JSON file");
        var tokenPath=System.IO.Path.Combine(SmokeProfile.DirectoryPath,"test-credentials.bin");
        var tokens=new GitHubTokenStore(tokenPath);
        var secret=new GitHubTokens("synthetic-access-token","synthetic-refresh-token",DateTimeOffset.UtcNow.AddHours(8),DateTimeOffset.UtcNow.AddDays(180));
        await tokens.SaveAsync(secret); check(await tokens.LoadAsync()==secret,"Windows DPAPI round-trips device credentials");
        check(!System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(tokenPath)).Contains(secret.AccessToken),"Credential file never contains clear-text tokens");
        await tokens.DeleteAsync(); check(!File.Exists(tokenPath),"Signing out removes the credential file");
        CloseEditor();
        if (local is not null)
        {
            boardId=WorkspaceView.Boards(local).First().Id; Render();
            var column=WorkspaceView.Columns(local,boardId.Value).First().Id;
            await OpenEditorAsync(null,column);
            check(!TaskTitle.IsReadOnly && TaskPriority.Visibility==Visibility.Visible && DateInformation.Visibility==Visibility.Visible,"Returning to local boards restores the full local editor");
            CloseEditor();
        }
        await github.UnlinkAsync("P1",1); await github.UnlinkAsync("P1",2); Render();
        ContentDialog GitHubTestDialog() => VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Select(p => FindVisual<ContentDialog>(p.Child)).First(x => x is not null)!;
        void PressGitHubDialog(ContentDialog dialog,string name)
        {
            var buttonPeer=Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(FindVisual<Button>(dialog,name)!);
            ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)buttonPeer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
        }
    }
}
