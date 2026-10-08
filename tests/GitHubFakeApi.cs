using KanbanTasker.Core.GitHub;

namespace KanbanTasker.Testing;

public sealed class GitHubFakeApi : IGitHubApi
{
    public GitHubProjectSnapshot Snapshot { get; set; } = Fixture();
    public bool Offline { get; set; }
    public bool FailAfterCreate { get; set; }
    public bool FailStatus { get; set; }
    public int Creates { get; private set; }
    public int Writes { get; private set; }
    public string AccountId { get; set; }="ACCOUNT";
    public GitHubOption[]? LastOptions { get; private set; }
    public DateTimeOffset? RetryAt { get; set; }
    public TimeSpan MinimumRefreshInterval => TimeSpan.FromSeconds(5);
    public static GitHubProjectSnapshot Fixture()
    {
        var date=new DateTimeOffset(2026,10,8,10,0,0,TimeSpan.Zero);
        return new("P1",1,"example","Project","STATUS",true,
            [new("todo","To do","GREEN","First column"),new("done","Done","PURPLE","Last column")],
            [new("V1",1,"Backlog","BOARD_LAYOUT","STATUS",false,"",["draft","issue"]),
             new("V2",2,"My items","BOARD_LAYOUT","STATUS",true,"",["draft"]),
             new("V3",3,"Priority","BOARD_LAYOUT","PRIORITY",false,"",[])],
            [new("draft","D1",GitHubCardKind.Draft,"Draft","Draft description","todo",null,date,date),
             new("issue","I1",GitHubCardKind.Issue,"Issue","Issue description","todo","https://github.com/example/repo/issues/1",date,date)],date);
    }
    private void Check() { if (Offline) throw new GitHubApiException("GitHub is unreachable. Cached boards are read-only."); }
    public Task<(string Id,string Login)> ViewerAsync(CancellationToken ct=default) { Check(); return Task.FromResult((AccountId,"tester")); }
    public Task<GitHubOrganization[]> OrganizationsAsync(CancellationToken ct=default) => Task.FromResult<GitHubOrganization[]>([new("example","Example")]);
    public Task<GitHubProjectSummary[]> ProjectsAsync(string organization,CancellationToken ct=default) => Task.FromResult<GitHubProjectSummary[]>([new("P1",1,"Project",organization)]);
    public Task<GitHubProjectSnapshot> ProjectAsync(string id,CancellationToken ct=default) { Check(); return Task.FromResult(Snapshot with { FetchedAt=DateTimeOffset.UtcNow }); }
    public Task<GitHubCard> CreateDraftAsync(string project,string title,string body,string operation,CancellationToken ct=default)
    {
        Check(); Creates++; Writes++;
        var card=new GitHubCard("created"+Creates,"CONTENT"+Creates,GitHubCardKind.Draft,title,body,null,null,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow);
        Snapshot=Snapshot with { Cards=[..Snapshot.Cards,card],Views=Snapshot.Views.Select(v => v with { ItemIds=[..v.ItemIds,card.Id] }).ToArray() };
        if (FailAfterCreate) throw new GitHubApiException("Connection lost",true);
        return Task.FromResult(card);
    }
    public Task UpdateDraftAsync(string contentId,GitHubEdit edit,CancellationToken ct=default)
    {
        Check(); Writes++;
        Snapshot=Snapshot with { Cards=Snapshot.Cards.Select(c => c.ContentId == contentId ? c with { Title=edit.Title ?? c.Title,Body=edit.Body ?? c.Body,UpdatedAt=DateTimeOffset.UtcNow } : c).ToArray() };
        return Task.CompletedTask;
    }
    public Task SetStatusAsync(string project,string item,string field,string? option,CancellationToken ct=default)
    {
        Check(); Writes++;
        if (FailStatus) throw new GitHubApiException("Status request failed",true);
        Snapshot=Snapshot with { Cards=Snapshot.Cards.Select(c => c.Id == item ? c with { StatusId=option,UpdatedAt=DateTimeOffset.UtcNow } : c).ToArray() };
        return Task.CompletedTask;
    }
    public Task PositionAsync(string project,string item,string? after,CancellationToken ct=default)
    {
        Check(); Writes++;
        var cards=Snapshot.Cards.Where(c => c.Id != item).ToList(); var moved=Snapshot.Cards.Single(c => c.Id == item);
        cards.Insert(after is null ? 0 : cards.FindIndex(c => c.Id == after)+1,moved);
        Snapshot=Snapshot with { Cards=cards.ToArray(),Views=Snapshot.Views.Select(v => v with { ItemIds=cards.Where(c => v.ItemIds.Contains(c.Id)).Select(c => c.Id).ToArray() }).ToArray() };
        return Task.CompletedTask;
    }
    public Task RemoveAsync(string project,string item,CancellationToken ct=default)
    {
        Check(); Writes++;
        Snapshot=Snapshot with { Cards=Snapshot.Cards.Where(c => c.Id != item).ToArray(),Views=Snapshot.Views.Select(v => v with { ItemIds=v.ItemIds.Where(id => id != item).ToArray() }).ToArray() };
        return Task.CompletedTask;
    }
    public Task OptionsAsync(string field,GitHubOption[] options,CancellationToken ct=default)
    {
        Check(); Writes++; LastOptions=options;
        Snapshot=Snapshot with { Options=options.Select(o => string.IsNullOrEmpty(o.Id) ? o with { Id="new-option" } : o).ToArray() };
        return Task.CompletedTask;
    }
}
