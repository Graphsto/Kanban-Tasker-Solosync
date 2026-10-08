using System.Text.Json;

namespace KanbanTasker.Core.GitHub;

/// <summary>Private per-account storage. Nothing in this store is merged into a WorkspaceDocument.</summary>
public sealed class GitHubStorage(string directory)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private string Account(string id) => Path.Combine(directory, GitHubIdentity.GuidFor("account", id).ToString("N"));
    private string Project(string account, string id) => Path.Combine(Account(account), "project-" + GitHubIdentity.GuidFor("project", id).ToString("N") + ".json");
    public async Task<GitHubRegistry?> ActiveAsync(CancellationToken ct = default) => Validate(await ReadAsync<GitHubRegistry>(Path.Combine(directory, "active.json"), ct));
    public async Task<GitHubRegistry?> RegistryForAsync(string account,CancellationToken ct=default)
    {
        var value=Validate(await ReadAsync<GitHubRegistry>(Path.Combine(Account(account),"registry.json"),ct));
        if (value is not null && value.AccountId != account) throw new InvalidDataException("Unsupported private GitHub cache format.");
        return value;
    }
    private static GitHubRegistry? Validate(GitHubRegistry? value)
    {
        if (value is not null && (value.Format != 1 || string.IsNullOrEmpty(value.AccountId) || value.Links is null
            || value.GroupedProjects is null || value.ExcludedViews is null
            || value.Links.Any(l => l is null || string.IsNullOrEmpty(l.ProjectId) || l.ViewNumber < 1)))
            throw new InvalidDataException("Unsupported private GitHub cache format.");
        return value;
    }
    public async Task RegistryAsync(GitHubRegistry registry, CancellationToken ct = default)
    {
        await WriteAsync(Path.Combine(Account(registry.AccountId),"registry.json"),registry,ct);
        await WriteAsync(Path.Combine(directory,"active.json"),registry,ct);
    }
    public async Task<GitHubProjectSnapshot?> ProjectAsync(string account, string id, CancellationToken ct = default)
    {
        var value=await ReadAsync<GitHubProjectSnapshot>(Project(account,id), ct);
        if (value is not null && (value.Id != id || string.IsNullOrEmpty(value.StatusFieldId) || string.IsNullOrEmpty(value.Organization)
            || value.Options is null || value.Views is null || value.Cards is null
            || value.Options.Any(o => o is null || string.IsNullOrEmpty(o.Id) || o.Name is null)
            || value.Views.Any(v => v is null || string.IsNullOrEmpty(v.Id) || v.ItemIds is null || v.Name is null)
            || value.Cards.Any(c => c is null || string.IsNullOrEmpty(c.Id) || string.IsNullOrEmpty(c.ContentId) || c.Title is null || c.Body is null)))
            throw new InvalidDataException("Unsupported private GitHub cache format.");
        return value;
    }
    public Task ProjectAsync(string account, GitHubProjectSnapshot project, CancellationToken ct = default) => WriteAsync(Project(account,project.Id), project, ct);
    public async Task<GitHubOperation[]> OperationsAsync(string account, CancellationToken ct = default)
    {
        var value=await ReadAsync<GitHubOperation[]>(Path.Combine(Account(account), "operations.json"), ct) ?? [];
        if (value.Any(o => o is null || string.IsNullOrEmpty(o.Id) || string.IsNullOrEmpty(o.ProjectId)))
            throw new InvalidDataException("Unsupported private GitHub cache format.");
        return value;
    }
    public Task OperationsAsync(string account, IEnumerable<GitHubOperation> operations, CancellationToken ct = default) =>
        WriteAsync(Path.Combine(Account(account), "operations.json"), operations.ToArray(), ct);
    private static async Task<T?> ReadAsync<T>(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return default;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try { return JsonSerializer.Deserialize<T>(await WorkspaceJson.ReadAsync(stream, ct), Json); }
        catch (JsonException ex) { throw new InvalidDataException("The private GitHub cache needs attention. No remote data was changed.", ex); }
    }
    private static async Task WriteAsync<T>(string path, T value, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > WorkspaceJson.MaximumBytes) throw new InvalidDataException("The GitHub cache exceeds the 64 MB safety limit.");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { await file.WriteAsync(bytes, ct); await file.FlushAsync(ct); file.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
