using KanbanTasker.Core;
using KanbanTasker.Core.GitHub;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace KanbanTasker.Desktop;

public sealed partial class MainWindow
{
    private readonly System.Net.Http.HttpClient githubHttp = new(new System.Net.Http.HttpClientHandler { AllowAutoRedirect=false }) { Timeout=TimeSpan.FromSeconds(25) };
    private GitHubAuthentication githubAuthentication = null!;
    private GitHubBoardSource github = null!;
    private LocalBoardSource localBoards = null!;
    private readonly DispatcherTimer githubTimer = new() { Interval=TimeSpan.FromSeconds(5) };
    private readonly Dictionary<string,DateTimeOffset> githubRefreshTimes = [];
    private bool githubRefreshing;
    private readonly CancellationTokenSource githubLifetime = new();
    private GitHubCard? githubOriginal;
    private bool githubEditor;
    private GitHubProjectSnapshot? SelectedGitHubProject => boardId is { } id ? GitHubBoard(id)?.Project : null;
    private (GitHubProjectSnapshot Project,GitHubView View)? GitHubBoard(Guid id)
    {
        foreach (var link in github.Registry.Links)
            if (GitHubIdentity.Board(link.ProjectId,link.ViewNumber) == id && github.Projects.TryGetValue(link.ProjectId,out var p))
            {
                var view = p.Views.FirstOrDefault(v => v.Number == link.ViewNumber && v.IsSupported(p.StatusFieldId));
                if (view is not null) return (p,view);
            }
        return null;
    }
    private bool IsGitHubBoard => SelectedGitHubProject is not null;
    private IBoardSource CurrentBoardSource => IsGitHubBoard ? github : localBoards;
    private BoardCapabilities CurrentCapabilities(Guid? card = null) => CurrentBoardSource.Capabilities(boardId?.ToString() ?? "",card?.ToString());
    private void InitializeGitHub()
    {
        var directory = System.IO.Path.Combine(LocalPreferences.DirectoryPath,"GitHub");
        githubAuthentication = new(githubHttp,Distribution.AppDistribution.GitHubClientId,new GitHubTokenStore(System.IO.Path.Combine(directory,"credentials.bin")));
        github = new(new GitHubApi(githubHttp,githubAuthentication.AccessTokenAsync),new GitHubStorage(directory));
        localBoards = new(store);
        github.Changed += GitHubChanged;
        githubTimer.Tick += async (_,_) => await RefreshGitHubAsync(false);
        Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged += GitHubNetworkChanged;
        Closed += (_,_) => { githubTimer.Stop(); github.Changed -= GitHubChanged; Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged -= GitHubNetworkChanged; githubLifetime.Cancel(); githubHttp.Dispose(); };
    }
    private void GitHubNetworkChanged(object sender) => DispatcherQueue.TryEnqueue(() =>
    {
        if (closed) return;
        var level=Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile()?.GetNetworkConnectivityLevel();
        if (level is null or Windows.Networking.Connectivity.NetworkConnectivityLevel.None)
        { github.LockAll(); CancelBoardDrag(); }
        else _=RefreshGitHubAsync(true);
    });
    private void GitHubChanged(object? sender,EventArgs args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!loaded || closed) return;
        if (IsGitHubBoard && !CurrentCapabilities().MoveCards) CancelBoardDrag();
        Render();
    });
    private async Task RefreshGitHubAsync(bool force)
    {
        if (githubRefreshing || closed || !loaded || github.Registry.AccountId.Length == 0) return;
        githubRefreshing = true;
        try
        {
            foreach (var id in github.Registry.Links.Select(l => l.ProjectId).Concat(github.Registry.GroupedProjects).Distinct().ToArray())
            {
                var interval = SelectedGitHubProject?.Id == id ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(60);
                if (github.MinimumRefreshInterval > interval) interval=github.MinimumRefreshInterval;
                if (!force && githubRefreshTimes.TryGetValue(id,out var last) && DateTimeOffset.UtcNow-last < interval) continue;
                if (github.RetryAt > DateTimeOffset.UtcNow) { github.LockAll(); break; }
                githubRefreshTimes[id] = DateTimeOffset.UtcNow;
                try { await github.RefreshAsync(id,githubLifetime.Token); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or System.Security.Cryptography.CryptographicException)
                { if (SelectedGitHubProject?.Id == id && !closed) { PathText.Text=T("GitHub cached board · read-only"); ToolTipService.SetToolTip(StatusText,text.TranslateDiagnostic(ex.Message)); } }
            }
        }
        finally { githubRefreshing=false; }
    }
    private WorkspaceDocument GitHubProjection(GitHubProjectSnapshot project,GitHubView view)
    {
        var workspace = new WorkspaceDocument { DocumentId=GitHubIdentity.GuidFor("project",project.Id) };
        var board = GitHubIdentity.Board(project.Id,view.Number);
        var clock = new ChangeClock(GitHubIdentity.GuidFor("clock",project.Id));
        EntityRecord Entity(Guid id,Guid parent) => new() { Id=id,BoardId=parent,Created=clock.Next() };
        var b = Entity(board,Guid.Empty);
        b.Set(Fields.Name,view.Name,clock.Next()); b.Set(Fields.Notes,project.Organization+" / "+project.Title,clock.Next());
        b.Set(Fields.GroupId,(Guid?)null,clock.Next());
        var options = project.Options.Select(o => (Id:(string?)o.Id,o.Name)).Prepend((Id:(string?)null,Name:T("No status"))).ToArray();
        b.Set(Fields.Order,options.Select(o => GitHubIdentity.Column(project.Id,o.Id)).ToArray(),clock.Next()); workspace.Boards.Add(b);
        foreach (var option in options)
        {
            var col = Entity(GitHubIdentity.Column(project.Id,option.Id),board);
            col.Set(Fields.Name,option.Name,clock.Next()); col.Set(Fields.Limit,0,clock.Next());
            var cards = view.ItemIds.Select(id => project.Cards.FirstOrDefault(c => c.Id == id)).Where(c => c is not null && c.StatusId == option.Id).Cast<GitHubCard>().ToArray();
            col.Set(Fields.Order,cards.Select(c => GitHubIdentity.Card(project.Id,c.Id)).ToArray(),clock.Next()); workspace.Columns.Add(col);
            var editor = new WorkspaceEditor(workspace,clock);
            foreach (var card in cards)
            {
                var generated = editor.SaveTask(new() { BoardId=board,ColumnId=col.Id,Title=card.Title,Description=card.Body,CreatedAt=card.CreatedAt });
                workspace.Tasks.Single(t => t.Id == generated).Id = GitHubIdentity.Card(project.Id,card.Id);
            }
            // SaveTask appended its generated IDs; restore canonical presentation IDs.
            col.Set(Fields.Order,cards.Select(c => GitHubIdentity.Card(project.Id,c.Id)).ToArray(),clock.Next());
        }
        return workspace;
    }
    private Choice[] GitHubChoices() => github.Registry.Links.Select(l => (l,board:GitHubBoard(GitHubIdentity.Board(l.ProjectId,l.ViewNumber))))
        .Where(x => x.board is not null).Select(x => new Choice(GitHubIdentity.Board(x.l.ProjectId,x.l.ViewNumber),x.board!.Value.View.Name,true)).ToArray();
    private Guid? GitHubGroup(Guid board) => GitHubBoard(board) is { } entry && github.Registry.GroupedProjects.Contains(entry.Project.Id)
        ? GitHubIdentity.Group(entry.Project.Id) : null;
    private GitHubCard? GitHubCardFor(Guid id) => SelectedGitHubProject is { } p ? p.Cards.FirstOrDefault(c => GitHubIdentity.Card(p.Id,c.Id) == id) : null;
    private string? GitHubStatusFor(Guid column) => SelectedGitHubProject?.Options.FirstOrDefault(o => GitHubIdentity.Column(SelectedGitHubProject.Id,o.Id) == column)?.Id;
    private Task MoveCardAsync(Guid id,Guid column,int index,GitHubCard? baseline=null,IReadOnlyList<string>? order=null) => IsGitHubBoard ? MoveGitHubCardAsync(id,column,index,baseline,order) : localBoards.CommitAsync(e => e.MoveTask(id,column,index));
    private async Task MoveGitHubCardAsync(Guid id,Guid column,int index,GitHubCard? baseline=null,IReadOnlyList<string>? order=null)
    {
        var entry = GitHubBoard(boardId!.Value)!.Value;
        if (!CurrentCapabilities(id).MoveCards) throw new GitHubApiException("GitHub is unreachable. Cached boards are read-only.");
        var original = baseline ?? GitHubCardFor(id) ?? throw new InvalidOperationException("Card unavailable.");
        order ??= entry.Project.Cards.Select(c => c.Id).ToArray();
        var siblings = WorkspaceView.Tasks(document!,column).Where(c => c.Id != id).ToArray();
        var anchor = index <= 0 || siblings.Length == 0 ? null : GitHubCardFor(siblings[Math.Min(index,siblings.Length)-1].Id)?.Id;
        var status=GitHubStatusFor(column);
        await ResolveGitHubConflictsAsync(accepted => github.MoveAsync(entry.Project.Id,original,status,anchor,!entry.View.Sorted,accepted,githubLifetime.Token,order));
    }
    private async Task<bool> RemoveCardAsync(Guid id,GitHubCard? baseline=null)
    {
        if (!IsGitHubBoard) { await localBoards.CommitAsync(e => e.DeleteTask(id)); return true; }
        if (!CurrentCapabilities(id).RemoveCards) throw new GitHubApiException("Issue contents are read-only in this app.");
        var projectId=SelectedGitHubProject!.Id;
        var original=baseline ?? GitHubCardFor(id)!;
        return await ResolveGitHubConflictsAsync(accepted => github.RemoveAsync(projectId,original,githubLifetime.Token,accepted));
    }
    private async Task<bool> ResolveGitHubConflictsAsync(Func<IReadOnlyDictionary<string,string>,Task> action,Action<string,string>? keepRemote=null)
    {
        var accepted = new Dictionary<string,string>();
        while (true)
        {
            try { await action(accepted); return true; }
            catch (GitHubConflictException ex)
            {
                foreach (var conflict in ex.Conflicts)
                {
                    var content = new StackPanel { Spacing=12,MaxWidth=460 };
                    content.Children.Add(new TextBlock { Text=T("Changed on GitHub: {0}",T(conflict.Field)),TextWrapping=TextWrapping.Wrap });
                    content.Children.Add(new TextBlock { Text=T("GitHub: {0}",ConflictValue(conflict.Field,conflict.Remote)),TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true });
                    content.Children.Add(new TextBlock { Text=T("Your edit: {0}",ConflictValue(conflict.Field,conflict.Mine)),TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true });
                    var dialog = Dialog(T("GitHub conflict"),content,T("Keep my edit")); dialog.SecondaryButtonText=T("Keep GitHub version");
                    var result = await dialog.ShowAsync();
                    if (result == ContentDialogResult.None) return false;
                    if (result == ContentDialogResult.Secondary)
                    {
                        if (keepRemote is null) return false;
                        keepRemote(conflict.Field,conflict.Remote);
                    }
                    accepted[conflict.Field]=conflict.Remote;
                }
            }
        }
    }
    private string ConflictValue(string field,string value)
    {
        if (field == "Status") return SelectedGitHubProject?.Options.FirstOrDefault(o => o.Id == value)?.Name ?? T("No status");
        if (field == "Position")
        {
            string Name(string id) => SelectedGitHubProject?.Cards.FirstOrDefault(c => c.Id == id)?.Title ?? T("Card");
            if (value.StartsWith('[')) return string.Join(" → ",System.Text.Json.JsonSerializer.Deserialize<string[]>(value)!.Take(10).Select(Name));
            return value.Length == 0 ? T("Move to start") : T("After {0}",Name(value));
        }
        if (field == "Card" && value.StartsWith('{') && System.Text.Json.JsonSerializer.Deserialize<GitHubCard>(value) is { } card)
            return card.Title+"\n"+card.Body+"\n"+ConflictValue("Status",card.StatusId ?? "");
        if (field == "Card") return T("Remove from GitHub project");
        return text.TranslateDiagnostic(value);
    }
    private FrameworkElement BuildGitHubCard(TaskData task)
    {
        var remote=GitHubCardFor(task.Id)!;
        var order=SelectedGitHubProject!.Cards.Select(c => c.Id).ToArray();
        var capabilities=CurrentCapabilities(task.Id);
        var body=new StackPanel { Spacing=7 };
        body.Children.Add(new TextBlock { Text=task.Title,TextWrapping=TextWrapping.Wrap,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold });
        if (!string.IsNullOrEmpty(task.Description)) body.Children.Add(new TextBlock { Text=task.Description,TextWrapping=TextWrapping.Wrap,MaxLines=4,TextTrimming=TextTrimming.CharacterEllipsis,Foreground=Brush("TextFillColorSecondaryBrush") });
        body.Children.Add(new TextBlock { Text=T(remote.Kind == GitHubCardKind.Draft ? "GitHub draft" : "GitHub issue · content read-only"),FontSize=12,Foreground=Brush("TextFillColorSecondaryBrush") });
        if (remote.Url is { } url && Uri.TryCreate(url,UriKind.Absolute,out var uri) && uri.Scheme == "https" && uri.Host == "github.com")
            body.Children.Add(new HyperlinkButton { Content=T("Open on GitHub"),NavigateUri=uri,Padding=new(0) });
        var card=new Border { Child=body,Tag=task.Id,Padding=new(12),CornerRadius=new(6),BorderThickness=new(1),BorderBrush=Brush("CardStrokeColorDefaultBrush"),Background=Brush("CardBackgroundFillColorDefaultBrush") };
        AutomationProperties.SetName(card,task.Title+", GitHub");
        if (capabilities.MoveCards) EnableBoardDrag(card,task.Id,false);
        var menu=new MenuFlyout();
        menu.Items.Add(MenuItem(T("Open card"),() => OpenEditorAsync(task.Id,task.ColumnId,allowDuringAction:true)));
        var siblings=WorkspaceView.Tasks(document!,task.ColumnId).Select(c => c.Id).ToList(); var index=siblings.IndexOf(task.Id);
        menu.Items.Add(MenuItem(T("Move up"),() => MoveCardAsync(task.Id,task.ColumnId,index-1,remote,order),capabilities.ReorderCards && index>0));
        menu.Items.Add(MenuItem(T("Move down"),() => MoveCardAsync(task.Id,task.ColumnId,index+1,remote,order),capabilities.ReorderCards && index<siblings.Count-1));
        var move=new MenuFlyoutSubItem { Text=T("Move to column"),IsEnabled=capabilities.MoveCards };
        foreach (var column in WorkspaceView.Columns(document!,task.BoardId)) move.Items.Add(MenuItem(column.Get<string>(Fields.Name),() => MoveCardAsync(task.Id,column.Id,int.MaxValue,remote,order),capabilities.MoveCards && column.Id != task.ColumnId));
        menu.Items.Add(move);
        menu.Items.Add(MenuItem(T("Remove from GitHub project"),async () =>
        {
            if (await ConfirmAsync(T("Remove from GitHub project?"),T("Remove “{0}” from this GitHub project?",task.Title),T("Remove")))
                await RemoveCardAsync(task.Id,remote);
        },capabilities.RemoveCards));
        card.ContextFlyout=menu; return card;
    }
    private async Task GitHubColumnDialogAsync(Guid? id)
    {
        var project=SelectedGitHubProject!;
        if (!CurrentCapabilities().ManageColumns) return;
        var original=id is null ? null : project.Options.FirstOrDefault(o => GitHubIdentity.Column(project.Id,o.Id) == id);
        if (id is not null && original is null) return;
        var name=new TextBox { Header=T("Column name"),Text=original?.Name ?? "",MinWidth=320 };
        var message=new TextBlock { Text=T("Status columns are shared by every view in this GitHub project."),TextWrapping=TextWrapping.Wrap,MaxWidth=440 };
        var panel=new StackPanel { Spacing=14 }; panel.Children.Add(name); panel.Children.Add(message);
        var dialog=Dialog(T(id is null ? "New column" : "Edit column"),panel,T("Save"));
        dialog.PrimaryButtonClick += async (_,args) =>
        {
            var deferral=args.GetDeferral();
            try { await github.ColumnAsync(project.Id,original,name.Text,ct:githubLifetime.Token); }
            catch (GitHubConflictException ex)
            {
                // Keep the form open; replacing its baseline still requires another deliberate Save.
                args.Cancel=true; original=github.Projects[project.Id].Options.FirstOrDefault(o => o.Id == original?.Id);
                message.Text=T("Column changed on GitHub: {0}. Keep your name and save again, or use the GitHub name.",ex.Conflicts[0].Remote);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            { args.Cancel=true; message.Text=text.TranslateDiagnostic(ex.Message); }
            finally { deferral.Complete(); }
        };
        await dialog.ShowAsync();
    }
    private async void ReviewGitHub_Click(object sender,RoutedEventArgs e) => await RunAsync(ReviewGitHubAsync);
    private async Task ReviewGitHubAsync()
    {
        foreach (var op in github.Operations.ToArray())
        {
            var content=new StackPanel { Spacing=12,MaxWidth=460 };
            content.Children.Add(new TextBlock { Text=T("A GitHub write may have completed. Refresh and inspect the project before unlocking further edits. The app will not repeat this operation."),TextWrapping=TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text=(op.Title ?? op.Kind)+" · "+op.Stage,TextWrapping=TextWrapping.Wrap });
            if (github.Projects.TryGetValue(op.ProjectId,out var p)) content.Children.Add(new HyperlinkButton { Content=T("Open on GitHub"),NavigateUri=new Uri("https://github.com/orgs/"+Uri.EscapeDataString(p.Organization)+"/projects/"+p.Number) });
            var dialog=Dialog(T("Review GitHub write"),content,T("I checked the project; unlock"));
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await github.AcknowledgeAsync(op.Id,githubLifetime.Token);
        }
        if (github.Operations.Count == 0) Render();
    }
    private async void LinkGitHub_Click(object sender,RoutedEventArgs e) => await RunAsync(LinkGitHubProjectAsync);
    private async void RefreshGitHub_Click(object sender,RoutedEventArgs e) => await RefreshGitHubAsync(true);
    private async Task WaitForGitHubRefreshAsync()
    {
        while (githubRefreshing) await Task.Delay(50,githubLifetime.Token);
    }
    private async Task LinkGitHubProjectAsync()
    {
        githubTimer.Stop();
        try { await WaitForGitHubRefreshAsync(); await LinkGitHubProjectCoreAsync(); }
        finally { if (!closed && loaded) githubTimer.Start(); }
    }
    private sealed record GitHubProjectChoice(string Id,string Name,GitHubProjectSummary Summary);
    private sealed record GitHubViewChoice(int Number,string Name,bool Supported);
    private async Task LinkGitHubProjectCoreAsync()
    {
        if (!await CanDiscardDraftAsync()) return;
        if (!githubAuthentication.Configured)
        {
            await Dialog(T("GitHub setup"),new TextBlock { Text=T("This build needs a registered GitHub App client ID before you can sign in. See the development guide."),TextWrapping=TextWrapping.Wrap,MaxWidth=440 }).ShowAsync(); return;
        }
        try { await github.AccountAsync(githubLifetime.Token); }
        catch (GitHubApiException)
        {
            var code = await githubAuthentication.BeginAsync(githubLifetime.Token);
            if (code.VerificationUri != "https://github.com/login/device") throw new GitHubApiException("GitHub returned an unsafe sign-in address.");
            var content = new StackPanel { Spacing=14 };
            content.Children.Add(new TextBlock { Text=T("Enter this code on GitHub: {0}",code.UserCode),IsTextSelectionEnabled=true });
            content.Children.Add(new HyperlinkButton { Content=T("Open GitHub sign-in"),NavigateUri=new Uri(code.VerificationUri) });
            content.Children.Add(new TextBlock { Text=T("Waiting for browser authorization…"),TextWrapping=TextWrapping.Wrap });
            var dialog = Dialog(T("Sign in with GitHub"),content);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(githubLifetime.Token);
            var signIn = githubAuthentication.SignInAsync(code,cancellation.Token);
            var showing = dialog.ShowAsync();
            var completed = await Task.WhenAny(signIn,showing.AsTask());
            if (completed != signIn) { cancellation.Cancel(); try { await signIn; } catch (OperationCanceledException) { } return; }
            try { await signIn; } finally { dialog.Hide(); }
            await showing; await github.AccountAsync(githubLifetime.Token);
        }
        await PickGitHubProjectAsync(new GitHubApi(githubHttp,githubAuthentication.AccessTokenAsync));
    }
    private async Task PickGitHubProjectAsync(IGitHubApi api)
    {
        var organizations = await api.OrganizationsAsync(githubLifetime.Token);
        var organization = new ComboBox { Name="GitHubOrganization",Header=T("Organization"),DisplayMemberPath="Login",ItemsSource=organizations,HorizontalAlignment=HorizontalAlignment.Stretch };
        var projects = new ComboBox { Name="GitHubProject",Header=T("Project"),DisplayMemberPath="Name",HorizontalAlignment=HorizontalAlignment.Stretch };
        var views = new ListView { Name="GitHubViews",SelectionMode=ListViewSelectionMode.Multiple,DisplayMemberPath="Name",MaxHeight=210 };
        var group = new CheckBox { Name="GitHubProjectGroup",Content=T("Link project as a group"),IsChecked=preferences.GroupsEnabled };
        var enableGroups = new CheckBox { Name="GitHubEnableGroups",Content=T("Enable board groups"),IsChecked=preferences.GroupsEnabled };
        var message = new TextBlock { Text=organizations.Length == 0 ? T("Install the GitHub App in your organization, then reopen this dialog.") : T("Only Status Kanban views can be linked."),TextWrapping=TextWrapping.Wrap };
        var panel = new StackPanel { Spacing=12,MinWidth=380,MaxWidth=460 };
        panel.Children.Add(organization); panel.Children.Add(projects); panel.Children.Add(views); panel.Children.Add(group); panel.Children.Add(enableGroups); panel.Children.Add(message);
        var picker = Dialog(T("Link GitHub project"),panel,T("Link"));
        GitHubProjectSnapshot? snapshot = null;
        var generation=0; var busy=false;
        picker.Closing += (_,args) => args.Cancel=busy;
        organization.SelectionChanged += async (_,_) =>
        {
            var current=++generation; snapshot=null; projects.ItemsSource=null; views.ItemsSource=null;
            if (organization.SelectedItem is not GitHubOrganization selected) return;
            busy=true; picker.IsPrimaryButtonEnabled=false; projects.IsEnabled=false;
            try
            {
                var list = await api.ProjectsAsync(selected.Login,githubLifetime.Token);
                if (current == generation) projects.ItemsSource=list.Select(p => new GitHubProjectChoice(p.Id,p.Title,p)).ToArray();
            }
            catch (IOException ex) { message.Text=text.TranslateDiagnostic(ex.Message); }
            finally { busy=false; projects.IsEnabled=true; }
        };
        projects.SelectionChanged += async (_,_) =>
        {
            var current=++generation; snapshot=null; views.ItemsSource=null;
            if (projects.SelectedItem is not GitHubProjectChoice selected) return;
            busy=true; picker.IsPrimaryButtonEnabled=false;
            try
            {
                var loadedProject = await api.ProjectAsync(selected.Id,githubLifetime.Token);
                if (current != generation) return;
                snapshot=loadedProject;
                var choices=snapshot.Views.Select(v => new GitHubViewChoice(v.Number,v.Name+(v.IsSupported(snapshot.StatusFieldId) ? "" : " · "+T("Not supported")),v.IsSupported(snapshot.StatusFieldId))).ToArray();
                views.ItemsSource=choices;
                foreach (var choice in choices.Where(v => v.Supported)) views.SelectedItems.Add(choice);
                var count=choices.Count(v => v.Supported);
                message.Text=count > 1 && !preferences.GroupsEnabled ? T("Multiple boards found. Enable groups to keep this project together.") : T("Only Status Kanban views can be linked.");
                picker.IsPrimaryButtonEnabled=count > 0;
            }
            catch (IOException ex) { message.Text=text.TranslateDiagnostic(ex.Message); }
            finally { busy=false; }
        };
        picker.IsPrimaryButtonEnabled=false;
        picker.PrimaryButtonClick += async (_,args) =>
        {
            if (snapshot is null) { args.Cancel=true; return; }
            var numbers=group.IsChecked == true ? snapshot.Views.Where(v => v.IsSupported(snapshot.StatusFieldId)).Select(v => v.Number).ToArray()
                : views.SelectedItems.Cast<GitHubViewChoice>().Where(v => v.Supported).Select(v => v.Number).ToArray();
            if (numbers.Length == 0 || views.SelectedItems.Cast<GitHubViewChoice>().Any(v => !v.Supported))
            { args.Cancel=true; message.Text=T("Select at least one supported Status board."); return; }
            var deferral=args.GetDeferral(); busy=true;
            try
            {
                await github.LinkAsync(snapshot,numbers,group.IsChecked == true,githubLifetime.Token);
                preferences.GroupsEnabled=enableGroups.IsChecked == true || group.IsChecked == true;
                preferences.SelectedGroup=group.IsChecked == true ? GitHubIdentity.Group(snapshot.Id) : null;
                boardId=GitHubIdentity.Board(snapshot.Id,numbers[0]); preferences.SelectedBoard=boardId; preferences.Save(); CloseEditor(); Render();
            }
            catch (IOException ex) { args.Cancel=true; message.Text=text.TranslateDiagnostic(ex.Message); }
            finally { busy=false; deferral.Complete(); }
        };
        await picker.ShowAsync();
    }
}
