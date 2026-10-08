using System.Security.Cryptography;
using System.Text;

namespace KanbanTasker.Core.GitHub;

public enum BoardSourceKind { Local, GitHub }
public readonly record struct BoardIdentity(BoardSourceKind Source, string Id);
public sealed record BoardCapabilities(bool EditContent, bool MoveCards, bool RemoveCards, bool ManageColumns, bool ReorderCards);
public interface IBoardSource
{
    BoardSourceKind Kind { get; }
    BoardCapabilities Capabilities(string boardId, string? cardId = null);
}

public sealed record GitHubOrganization(string Login, string Name);
public sealed record GitHubProjectSummary(string Id, int Number, string Title, string Organization);
public sealed record GitHubOption(string Id, string Name, string Color, string Description);
public sealed record GitHubView(string Id, int Number, string Name, string Layout, string? GroupFieldId,
    bool Sorted, string Filter, string[] ItemIds, bool HasRowGrouping = false)
{
    public bool IsSupported(string statusId) => Layout == "BOARD_LAYOUT" && GroupFieldId == statusId && !HasRowGrouping;
}
public enum GitHubCardKind { Draft, Issue }
public sealed record GitHubCard(string Id, string ContentId, GitHubCardKind Kind, string Title, string Body,
    string? StatusId, string? Url, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record GitHubProjectSnapshot(string Id, int Number, string Organization, string Title,
    string StatusFieldId, bool CanWrite, GitHubOption[] Options, GitHubView[] Views, GitHubCard[] Cards,
    DateTimeOffset FetchedAt);
public sealed record GitHubLink(string ProjectId, int ViewNumber);
public sealed class GitHubRegistry
{
    public int Format { get; set; } = 1;
    public string AccountId { get; set; } = "";
    public string Login { get; set; } = "";
    public List<GitHubLink> Links { get; set; } = [];
    public HashSet<string> GroupedProjects { get; set; } = [];
    public HashSet<string> ExcludedViews { get; set; } = [];
}
public sealed record GitHubTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt,
    DateTimeOffset RefreshExpiresAt);
public sealed record GitHubDeviceCode(string DeviceCode, string UserCode, string VerificationUri,
    int Interval, int ExpiresIn);
public interface IGitHubTokenStore
{
    Task<GitHubTokens?> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(GitHubTokens tokens, CancellationToken ct = default);
    Task DeleteAsync(CancellationToken ct = default);
}
public sealed record GitHubEdit(string? Title = null, string? Body = null, bool ChangeStatus = false, string? StatusId = null);
public sealed record GitHubConflict(string Field, string Original, string Remote, string Mine);
public sealed class GitHubConflictException(GitHubConflict[] conflicts) : IOException("This card changed on GitHub. Choose which values to keep.")
{
    public GitHubConflict[] Conflicts { get; } = conflicts;
}
public sealed class GitHubApiException(string message, bool uncertain = false, DateTimeOffset? retryAt = null) : IOException(message)
{
    public bool Uncertain { get; } = uncertain;
    public DateTimeOffset? RetryAt { get; } = retryAt;
}
public sealed record GitHubOperation(string Id, string ProjectId, string Kind, string? CardId, string Stage,
    DateTimeOffset StartedAt, string? Title = null, string? Body = null, string? StatusId = null);

public static class GitHubIdentity
{
    // Presentation IDs are stable and source-qualified; they never enter the local workspace file.
    public static Guid GuidFor(string kind, string id) => new(SHA256.HashData(Encoding.UTF8.GetBytes("github:" + kind + ":" + id)).AsSpan(0, 16));
    public static Guid Board(string project, int view) => GuidFor("board", project + ":" + view);
    public static Guid Group(string project) => GuidFor("group", project);
    public static Guid Column(string project, string? option) => GuidFor("column", project + ":" + (option ?? "unassigned"));
    public static Guid Card(string project, string item) => GuidFor("card", project + ":" + item);
}
