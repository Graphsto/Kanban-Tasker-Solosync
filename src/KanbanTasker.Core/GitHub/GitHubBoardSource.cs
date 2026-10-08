namespace KanbanTasker.Core.GitHub;

public sealed class LocalBoardSource(WorkspaceStore store) : IBoardSource
{
    public BoardSourceKind Kind => BoardSourceKind.Local;
    public BoardCapabilities Capabilities(string boardId, string? cardId = null) => new(true, true, true, true, true);
    public Task CommitAsync(Action<WorkspaceEditor> edit, CancellationToken ct = default) => store.CommitAsync(edit, ct);
}

public sealed class GitHubBoardSource(IGitHubApi api, GitHubStorage storage) : IBoardSource
{
    private readonly Dictionary<string, SemaphoreSlim> gates = [];
    private readonly SemaphoreSlim persistence = new(1);
    private readonly Dictionary<string, GitHubProjectSnapshot> projects = [];
    private readonly HashSet<string> online = [];
    private readonly Dictionary<string, string> errors = [];
    private readonly List<GitHubOperation> operations = [];
    public BoardSourceKind Kind => BoardSourceKind.GitHub;
    public GitHubRegistry Registry { get; private set; } = new();
    public IReadOnlyDictionary<string, GitHubProjectSnapshot> Projects => projects;
    public IReadOnlyList<GitHubOperation> Operations => operations;
    public DateTimeOffset? RetryAt => api.RetryAt;
    public TimeSpan MinimumRefreshInterval => api.MinimumRefreshInterval;
    public event EventHandler? Changed;
    public bool Online(string project) => online.Contains(project);
    public string? Error(string project) => errors.GetValueOrDefault(project);
    private SemaphoreSlim Gate(string project)
    {
        lock (gates) { if (!gates.TryGetValue(project, out var gate)) gates[project] = gate = new(1); return gate; }
    }
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var registry = await storage.ActiveAsync(ct);
        if (registry is null) return;
        if (registry.Format != 1 || string.IsNullOrEmpty(registry.AccountId)) throw new InvalidDataException("Unsupported private GitHub cache format.");
        Registry = registry;
        operations.AddRange(await storage.OperationsAsync(registry.AccountId, ct));
        foreach (var id in registry.Links.Select(l => l.ProjectId).Concat(registry.GroupedProjects).Distinct())
        {
            var project = await storage.ProjectAsync(registry.AccountId, id, ct);
            if (project is not null && project.Id == id) projects[id] = project;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public async Task AccountAsync(CancellationToken ct = default)
    {
        var (id, login) = await api.ViewerAsync(ct);
        if (Registry.AccountId != id)
        {
            projects.Clear(); online.Clear(); operations.Clear(); errors.Clear();
            Registry = await storage.RegistryForAsync(id,ct) ?? new() { AccountId=id, Login=login };
            operations.AddRange(await storage.OperationsAsync(id,ct));
            foreach (var projectId in Registry.Links.Select(l => l.ProjectId).Concat(Registry.GroupedProjects).Distinct())
                if (await storage.ProjectAsync(id,projectId,ct) is { } cached) projects[projectId]=cached;
        }
        Registry.Login = login;
        await storage.RegistryAsync(Registry, ct);
    }
    public void LockAll()
    {
        online.Clear(); Changed?.Invoke(this, EventArgs.Empty);
    }
    public BoardCapabilities Capabilities(string boardId, string? cardId = null)
    {
        var link = Registry.Links.FirstOrDefault(l => GitHubIdentity.Board(l.ProjectId,l.ViewNumber).ToString() == boardId);
        if (link is null || !projects.TryGetValue(link.ProjectId, out var p)) return new(false,false,false,false,false);
        var write = Online(p.Id) && p.CanWrite && !operations.Any(o => o.ProjectId == p.Id) && !(RetryAt > DateTimeOffset.UtcNow);
        var view = p.Views.FirstOrDefault(v => v.Number == link.ViewNumber);
        write &= view is not null && view.IsSupported(p.StatusFieldId);
        var card = cardId is null ? null : p.Cards.FirstOrDefault(c => GitHubIdentity.Card(p.Id,c.Id).ToString() == cardId);
        if (cardId is not null && card is null) return new(false,false,false,false,false);
        return new(write && (card is null || card.Kind == GitHubCardKind.Draft), write,
            write && card?.Kind == GitHubCardKind.Draft, write, write && view is { Sorted:false });
    }
    public async Task<GitHubProjectSnapshot> RefreshAsync(string id, CancellationToken ct = default)
    {
        await Gate(id).WaitAsync(ct);
        try { return await ReadAsync(id, ct); }
        finally { Gate(id).Release(); }
    }
    private async Task<GitHubProjectSnapshot> ReadAsync(string id, CancellationToken ct)
    {
        try
        {
            var project = await api.ProjectAsync(id, ct);
            if (Registry.AccountId.Length == 0) throw new GitHubApiException("Sign in to GitHub to edit this board.");
            await storage.ProjectAsync(Registry.AccountId, project, ct);
            projects[id] = project; online.Add(id); errors.Remove(id);
            if (Registry.GroupedProjects.Contains(id))
            {
                var links = project.Views.Where(v => v.IsSupported(project.StatusFieldId)
                    && !Registry.ExcludedViews.Contains(id + ":" + v.Number)).Select(v => new GitHubLink(id,v.Number));
                foreach (var link in links) if (!Registry.Links.Contains(link)) Registry.Links.Add(link);
                await storage.RegistryAsync(Registry, ct);
            }
            Changed?.Invoke(this, EventArgs.Empty);
            return project;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or System.Security.Cryptography.CryptographicException)
        { online.Remove(id); errors[id] = ex.Message; Changed?.Invoke(this, EventArgs.Empty); throw; }
    }
    public async Task LinkAsync(GitHubProjectSnapshot project, IEnumerable<int> views, bool group, CancellationToken ct = default)
    {
        if (Registry.AccountId.Length == 0) throw new GitHubApiException("Sign in to GitHub to edit this board.");
        await storage.ProjectAsync(Registry.AccountId, project, ct);
        projects[project.Id] = project; online.Add(project.Id);
        if (group) Registry.GroupedProjects.Add(project.Id);
        foreach (var number in views)
        {
            if (!project.Views.Any(v => v.Number == number && v.IsSupported(project.StatusFieldId))) throw new ArgumentException("Only Status board views are supported.");
            var link = new GitHubLink(project.Id,number);
            if (!Registry.Links.Contains(link)) Registry.Links.Add(link);
            Registry.ExcludedViews.Remove(project.Id + ":" + number);
        }
        await storage.RegistryAsync(Registry, ct); Changed?.Invoke(this,EventArgs.Empty);
    }
    public async Task UnlinkAsync(string project, int view, CancellationToken ct = default)
    {
        Registry.Links.RemoveAll(l => l.ProjectId == project && l.ViewNumber == view);
        Registry.ExcludedViews.Add(project + ":" + view);
        await storage.RegistryAsync(Registry,ct); Changed?.Invoke(this,EventArgs.Empty);
    }
    public async Task UngroupAsync(string project, CancellationToken ct = default)
    {
        Registry.GroupedProjects.Remove(project);
        await storage.RegistryAsync(Registry,ct); Changed?.Invoke(this,EventArgs.Empty);
    }
    public async Task AcknowledgeAsync(string operationId, CancellationToken ct = default)
    {
        var operation = operations.First(o => o.Id == operationId);
        await RefreshAsync(operation.ProjectId,ct);
        await EndAsync(operation,ct); Changed?.Invoke(this,EventArgs.Empty);
    }
    private static GitHubCard RequireCard(GitHubProjectSnapshot p, string id) => p.Cards.FirstOrDefault(c => c.Id == id)
        ?? throw new GitHubApiException("This card was removed from GitHub. It cannot be restored by saving this editor.");
    private void RequireWrite(GitHubProjectSnapshot p)
    {
        if (!p.CanWrite || !Online(p.Id)) throw new GitHubApiException("GitHub access denied. Check the App installation, organization approval, and project permissions.");
        if (operations.Any(o => o.ProjectId == p.Id)) throw new GitHubApiException("A previous GitHub write needs review before more changes can be made.");
    }
    public static GitHubConflict[] Conflicts(GitHubCard original, GitHubCard current, GitHubEdit edit, IReadOnlyDictionary<string,string>? accepted = null)
    {
        var conflicts = new List<GitHubConflict>();
        void Check(string field, string baseline, string remote, string mine)
        {
            if (baseline != remote && mine != remote && accepted?.GetValueOrDefault(field) != remote)
                conflicts.Add(new(field,baseline,remote,mine));
        }
        if (edit.Title is { } title) Check("Title",original.Title,current.Title,title);
        if (edit.Body is { } body) Check("Description",original.Body,current.Body,body);
        if (edit.ChangeStatus) Check("Status",original.StatusId ?? "",current.StatusId ?? "",edit.StatusId ?? "");
        return conflicts.ToArray();
    }
    public async Task SaveAsync(string projectId, GitHubCard? original, GitHubEdit edit,
        IReadOnlyDictionary<string,string>? accepted = null, CancellationToken ct = default)
    {
        if (edit.Title is not null && string.IsNullOrWhiteSpace(edit.Title)) throw new ArgumentException("Task title is required.");
        if (original is null && edit.Title is null) throw new ArgumentException("Task title is required.");
        await Gate(projectId).WaitAsync(ct);
        GitHubOperation? operation = null;
        try
        {
            var p = await ReadAsync(projectId,ct); RequireWrite(p);
            if (edit.ChangeStatus && edit.StatusId is not null && !p.Options.Any(o => o.Id == edit.StatusId)) throw new GitHubApiException("This Status column was removed on GitHub. Choose another column.");
            var card = original is null ? null : RequireCard(p,original.Id);
            if (card is not null)
            {
                if (card.Kind != GitHubCardKind.Draft && (edit.Title is not null || edit.Body is not null)) throw new GitHubApiException("Issue contents are read-only in this app.");
                var conflicts = Conflicts(original!,card,edit,accepted);
                if (conflicts.Length > 0) throw new GitHubConflictException(conflicts);
                if (edit.Title is null && edit.Body is null && !edit.ChangeStatus) return;
            }
            operation = new(Guid.NewGuid().ToString("N"),projectId,card is null ? "create" : "edit",card?.Id,"sending",DateTimeOffset.UtcNow,edit.Title,edit.Body,edit.StatusId);
            await BeginAsync(operation,ct);
            if (card is null)
            {
                card = await api.CreateDraftAsync(projectId,edit.Title ?? throw new ArgumentException("Task title is required."),edit.Body ?? "",operation.Id,ct);
                var created = operation with { CardId=card.Id,Stage="created" };
                await ReplaceAsync(operation,created,ct); operation = created;
            }
            else if (edit.Title is not null || edit.Body is not null) await api.UpdateDraftAsync(card.ContentId,edit,ct);
            if (edit.ChangeStatus && card.StatusId != edit.StatusId) await api.SetStatusAsync(projectId,card.Id,p.StatusFieldId,edit.StatusId,ct);
            var verified = await ReadAsync(projectId,ct);
            var saved = RequireCard(verified,card.Id);
            if (edit.Title is not null && saved.Title != edit.Title || edit.Body is not null && saved.Body != edit.Body || edit.ChangeStatus && saved.StatusId != edit.StatusId)
                throw new GitHubApiException("The card changed again while saving. Review the current GitHub values.",true);
            await EndAsync(operation,ct); operation = null;
        }
        catch (GitHubConflictException) { throw; }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            online.Remove(projectId); errors[projectId] = ex.Message;
            // Keep any operation that might have reached GitHub. It is an audit/recovery record, not a retry queue.
            throw;
        }
        finally { Gate(projectId).Release(); Changed?.Invoke(this,EventArgs.Empty); }
    }
    public async Task MoveAsync(string projectId, GitHubCard original, string? option, string? after, bool position,
        IReadOnlyDictionary<string,string>? accepted = null, CancellationToken ct = default, IReadOnlyList<string>? originalOrder = null)
    {
        await Gate(projectId).WaitAsync(ct);
        try
        {
            var p = await ReadAsync(projectId,ct); RequireWrite(p);
            var card = RequireCard(p,original.Id);
            var conflicts = Conflicts(original,card,new(ChangeStatus:true,StatusId:option),accepted);
            if (conflicts.Length > 0) throw new GitHubConflictException(conflicts);
            var remoteOrder = System.Text.Json.JsonSerializer.Serialize(p.Cards.Select(c => c.Id));
            if (position && originalOrder is not null && !originalOrder.SequenceEqual(p.Cards.Select(c => c.Id))
                && accepted?.GetValueOrDefault("Position") != remoteOrder)
                throw new GitHubConflictException([new("Position",System.Text.Json.JsonSerializer.Serialize(originalOrder),remoteOrder,after ?? "")]);
            if (option is not null && !p.Options.Any(o => o.Id == option)) throw new GitHubApiException("This Status column was removed on GitHub. Choose another column.");
            if (after is not null) RequireCard(p,after);
            var op = new GitHubOperation(Guid.NewGuid().ToString("N"),projectId,"move",card.Id,"sending",DateTimeOffset.UtcNow,StatusId:option);
            await BeginAsync(op,ct);
            if (card.StatusId != option) await api.SetStatusAsync(projectId,card.Id,p.StatusFieldId,option,ct);
            if (position) await api.PositionAsync(projectId,card.Id,after,ct);
            var verified = await ReadAsync(projectId,ct);
            if (RequireCard(verified,card.Id).StatusId != option) throw new GitHubApiException("The card changed again while saving. Review the current GitHub values.",true);
            if (position && verified.Cards.TakeWhile(c => c.Id != card.Id).LastOrDefault()?.Id != after)
                throw new GitHubApiException("The card changed again while saving. Review the current GitHub values.",true);
            await EndAsync(op,ct);
        }
        catch (GitHubConflictException) { throw; }
        catch (IOException ex) { online.Remove(projectId); errors[projectId]=ex.Message; throw; }
        finally { Gate(projectId).Release(); Changed?.Invoke(this,EventArgs.Empty); }
    }
    public async Task RemoveAsync(string projectId, GitHubCard original, CancellationToken ct = default, IReadOnlyDictionary<string,string>? accepted = null)
    {
        await Gate(projectId).WaitAsync(ct);
        try
        {
            var p = await ReadAsync(projectId,ct); RequireWrite(p);
            var card = RequireCard(p,original.Id);
            if (card.Kind != GitHubCardKind.Draft) throw new GitHubApiException("Issue contents are read-only in this app.");
            var remote = System.Text.Json.JsonSerializer.Serialize(card);
            if (card != original && accepted?.GetValueOrDefault("Card") != remote)
                throw new GitHubConflictException([new("Card",System.Text.Json.JsonSerializer.Serialize(original),remote,"Remove from project")]);
            var op = new GitHubOperation(Guid.NewGuid().ToString("N"),projectId,"remove",card.Id,"sending",DateTimeOffset.UtcNow);
            await BeginAsync(op,ct); await api.RemoveAsync(projectId,card.Id,ct);
            var verified = await ReadAsync(projectId,ct);
            if (verified.Cards.Any(c => c.Id == card.Id)) throw new GitHubApiException("GitHub could not confirm the removal. Refresh and review the board.",true);
            await EndAsync(op,ct);
        }
        finally { Gate(projectId).Release(); Changed?.Invoke(this,EventArgs.Empty); }
    }
    public async Task ColumnAsync(string projectId, GitHubOption? original, string name, bool acceptRename = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Column name is required.");
        await Gate(projectId).WaitAsync(ct);
        try
        {
            var p = await ReadAsync(projectId,ct); RequireWrite(p);
            var current = original is null ? null : p.Options.FirstOrDefault(o => o.Id == original.Id)
                ?? throw new GitHubApiException("This Status column was removed on GitHub. Choose another column.");
            if (current is not null && current.Name != original!.Name && current.Name != name && !acceptRename)
                throw new GitHubConflictException([new("Column name",original.Name,current.Name,name)]);
            if (p.Options.Any(o => o.Id != current?.Id && o.Name.Equals(name.Trim(),StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("A column with this name already exists.");
            var options = current is null ? p.Options.Append(new GitHubOption("",name.Trim(),"GRAY","")).ToArray()
                : p.Options.Select(o => o.Id == current.Id ? o with { Name=name.Trim() } : o).ToArray();
            var op = new GitHubOperation(Guid.NewGuid().ToString("N"),projectId,"column",null,"sending",DateTimeOffset.UtcNow,Title:name);
            await BeginAsync(op,ct); await api.OptionsAsync(p.StatusFieldId,options,ct);
            var verified = await ReadAsync(projectId,ct);
            if (!verified.Options.Any(o => o.Name == name.Trim()) || options.Where(o => o.Id.Length > 0).Any(o => !verified.Options.Contains(o)))
                throw new GitHubApiException("GitHub could not confirm the column change. Refresh and review the board.",true);
            await EndAsync(op,ct);
        }
        finally { Gate(projectId).Release(); Changed?.Invoke(this,EventArgs.Empty); }
    }
    private async Task BeginAsync(GitHubOperation operation, CancellationToken ct)
    {
        await persistence.WaitAsync(ct);
        try { var next=operations.Append(operation).ToArray(); await storage.OperationsAsync(Registry.AccountId,next,ct); operations.Add(operation); }
        finally { persistence.Release(); }
    }
    private async Task ReplaceAsync(GitHubOperation previous, GitHubOperation next, CancellationToken ct)
    {
        await persistence.WaitAsync(ct);
        try { var updated=operations.Select(o => o.Id == previous.Id ? next : o).ToArray(); await storage.OperationsAsync(Registry.AccountId,updated,ct); operations[operations.IndexOf(previous)] = next; }
        finally { persistence.Release(); }
    }
    private async Task EndAsync(GitHubOperation operation, CancellationToken ct)
    {
        await persistence.WaitAsync(ct);
        try { await storage.OperationsAsync(Registry.AccountId,operations.Where(o => o.Id != operation.Id),ct); operations.RemoveAll(o => o.Id == operation.Id); }
        finally { persistence.Release(); }
    }
}
