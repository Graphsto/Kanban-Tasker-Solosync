using System.Net;
using System.Text;
using System.Text.Json;
using KanbanTasker.Core.GitHub;
using KanbanTasker.Testing;

namespace KanbanTasker.Tests;

public sealed class GitHubTests : IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"kanban-github-tests-"+Guid.NewGuid().ToString("N"));
    private async Task<(GitHubBoardSource Source,GitHubFakeApi Api)> SourceAsync()
    {
        var api=new GitHubFakeApi(); var source=new GitHubBoardSource(api,new GitHubStorage(directory));
        await source.AccountAsync(); await source.LinkAsync(api.Snapshot,[1,2],false); return (source,api);
    }
    [Fact] public async Task CacheRestartsReadOnlyAndKeepsCanonicalCardsAcrossViews()
    {
        var (source,api)=await SourceAsync();
        var restarted=new GitHubBoardSource(api,new GitHubStorage(directory)); await restarted.InitializeAsync();
        Assert.Equal(2,restarted.Registry.Links.Count); Assert.Equal(2,restarted.Projects["P1"].Cards.Length);
        Assert.False(restarted.Capabilities(GitHubIdentity.Board("P1",1).ToString()).EditContent);
        await restarted.RefreshAsync("P1"); Assert.True(restarted.Capabilities(GitHubIdentity.Board("P1",1).ToString()).EditContent);
        Assert.DoesNotContain("accessToken",string.Join("",Directory.GetFiles(directory,"*.json",SearchOption.AllDirectories).Select(File.ReadAllText)));
    }
    [Fact] public async Task IssueOnlyMovesAndSortedViewDisablesReordering()
    {
        var (source,api)=await SourceAsync(); var issue=api.Snapshot.Cards[1];
        var caps=source.Capabilities(GitHubIdentity.Board("P1",1).ToString(),GitHubIdentity.Card("P1",issue.Id).ToString());
        Assert.False(caps.EditContent); Assert.False(caps.RemoveCards); Assert.True(caps.MoveCards);
        Assert.False(source.Capabilities(GitHubIdentity.Board("P1",2).ToString()).ReorderCards);
        await Assert.ThrowsAsync<GitHubApiException>(() => source.SaveAsync("P1",issue,new(Title:"Changed")));
        await source.RefreshAsync("P1"); await Assert.ThrowsAsync<GitHubApiException>(() => source.RemoveAsync("P1",issue));
        await source.MoveAsync("P1",issue,"done",null,false);
        Assert.Equal("done",api.Snapshot.Cards[1].StatusId); Assert.Equal("Issue",api.Snapshot.Cards[1].Title);
    }
    [Fact] public async Task SwitchingAccountsKeepsTheirLinksAndCachesSeparate()
    {
        var (source,api)=await SourceAsync(); await source.LinkAsync(api.Snapshot,[1,2],true);
        api.AccountId="OTHER"; await source.AccountAsync();
        Assert.Empty(source.Registry.Links); Assert.Empty(source.Projects);
        await source.LinkAsync(api.Snapshot,[2],false);
        api.AccountId="ACCOUNT"; await source.AccountAsync();
        Assert.Equal(2,source.Registry.Links.Count); Assert.Contains("P1",source.Registry.GroupedProjects);
        Assert.Equal(2,source.Projects["P1"].Cards.Length); Assert.False(source.Online("P1"));
    }
    [Fact] public async Task MalformedPrivateRegistryIsPreservedAndRejectedSafely()
    {
        Directory.CreateDirectory(directory); var path=Path.Combine(directory,"active.json");
        const string invalid="{\"format\":1,\"accountId\":\"ACCOUNT\",\"links\":null}"; await File.WriteAllTextAsync(path,invalid);
        var source=new GitHubBoardSource(new GitHubFakeApi(),new GitHubStorage(directory));
        await Assert.ThrowsAsync<InvalidDataException>(() => source.InitializeAsync());
        Assert.Equal(invalid,await File.ReadAllTextAsync(path)); Assert.Empty(source.Projects);
    }
    [Fact] public async Task DifferentFieldsMergeAndSameFieldRequiresChoice()
    {
        var (source,api)=await SourceAsync(); var original=api.Snapshot.Cards[0];
        api.Snapshot=api.Snapshot with { Cards=[original with { Body="Remote" },api.Snapshot.Cards[1]] };
        await source.SaveAsync("P1",original,new(Title:"Mine"));
        Assert.Equal("Remote",api.Snapshot.Cards[0].Body);
        var before=api.Snapshot.Cards[0]; api.Snapshot=api.Snapshot with { Cards=[before with { Title="Other" },api.Snapshot.Cards[1]] };
        var ex=await Assert.ThrowsAsync<GitHubConflictException>(() => source.SaveAsync("P1",before,new(Title:"Chosen")));
        Assert.Equal("Other",Assert.Single(ex.Conflicts).Remote);
        await source.SaveAsync("P1",before,new(Title:"Chosen"),new Dictionary<string,string> { ["Title"]="Other" });
        Assert.Equal("Chosen",api.Snapshot.Cards[0].Title);
    }
    [Fact] public async Task LostCreateResponseNeverAutomaticallyCreatesAgain()
    {
        var (source,api)=await SourceAsync(); api.FailAfterCreate=true;
        await Assert.ThrowsAsync<GitHubApiException>(() => source.SaveAsync("P1",null,new("New","Body",true,"todo")));
        Assert.Single(source.Operations); Assert.Equal(1,api.Creates);
        await source.RefreshAsync("P1");
        await Assert.ThrowsAsync<GitHubApiException>(() => source.SaveAsync("P1",null,new("New","Body",true,"todo")));
        Assert.Equal(1,api.Creates);
        var restarted=new GitHubBoardSource(api,new GitHubStorage(directory)); await restarted.InitializeAsync();
        Assert.Single(restarted.Operations); Assert.Equal(1,api.Creates);
        await restarted.AcknowledgeAsync(restarted.Operations[0].Id); Assert.Empty(restarted.Operations);
    }
    [Fact] public async Task PartialCreateRecordsReturnedIdentityAndCanBeReviewed()
    {
        var (source,api)=await SourceAsync(); api.FailStatus=true;
        await Assert.ThrowsAsync<GitHubApiException>(() => source.SaveAsync("P1",null,new("New","Body",true,"todo")));
        Assert.Equal("created1",Assert.Single(source.Operations).CardId); Assert.Equal("created",source.Operations[0].Stage);
        Assert.Equal(1,api.Creates); Assert.Equal(3,api.Snapshot.Cards.Length);
    }
    [Fact] public async Task OfflineAndRevokedPermissionsDoNotWriteOrReplaceCache()
    {
        var (source,api)=await SourceAsync(); api.Offline=true;
        await Assert.ThrowsAsync<GitHubApiException>(() => source.RefreshAsync("P1"));
        Assert.Equal(2,source.Projects["P1"].Cards.Length); Assert.False(source.Online("P1"));
        await Assert.ThrowsAsync<GitHubApiException>(() => source.SaveAsync("P1",api.Snapshot.Cards[0],new(Title:"No")));
        Assert.Equal(0,api.Writes);
        api.Offline=false; api.Snapshot=api.Snapshot with { CanWrite=false }; await source.RefreshAsync("P1");
        Assert.False(source.Capabilities(GitHubIdentity.Board("P1",1).ToString()).MoveCards);
        await Assert.ThrowsAsync<GitHubApiException>(() => source.SaveAsync("P1",api.Snapshot.Cards[0],new(Title:"No")));
        Assert.Equal(0,api.Writes);
    }
    [Fact] public async Task DeletedCardsAreNeverResurrectedByEditor()
    {
        var (source,api)=await SourceAsync(); var original=api.Snapshot.Cards[0]; api.Snapshot=api.Snapshot with { Cards=[api.Snapshot.Cards[1]] };
        await Assert.ThrowsAsync<GitHubApiException>(() => source.SaveAsync("P1",original,new(Title:"Changed")));
        Assert.Equal(0,api.Writes); Assert.Equal(0,api.Creates);
    }
    [Fact] public async Task ColumnChangesPreserveOptionIdsColorsDescriptionsAndCardAssignments()
    {
        var (source,api)=await SourceAsync(); var before=api.Snapshot.Options;
        await source.ColumnAsync("P1",before[0],"Ready");
        Assert.Equal(before[0] with { Name="Ready" },api.LastOptions![0]); Assert.Equal(before[1],api.LastOptions[1]);
        Assert.Equal("todo",api.Snapshot.Cards[0].StatusId);
        await source.ColumnAsync("P1",null,"Review");
        Assert.Equal(3,api.LastOptions.Length); Assert.Equal("todo",api.LastOptions[0].Id);
    }
    [Fact] public async Task GroupRemovalKeepsBoardsAndExplicitUnlinkDoesNotReappear()
    {
        var (source,api)=await SourceAsync(); await source.LinkAsync(api.Snapshot,[1,2],true);
        await source.UnlinkAsync("P1",2); await source.RefreshAsync("P1"); Assert.Single(source.Registry.Links);
        api.Snapshot=api.Snapshot with { Views=[..api.Snapshot.Views,new("V4",4,"New","BOARD_LAYOUT","STATUS",false,"",[])] };
        await source.RefreshAsync("P1"); Assert.Contains(source.Registry.Links,l => l.ViewNumber==4);
        await source.UngroupAsync("P1"); Assert.Empty(source.Registry.GroupedProjects); Assert.Equal(2,source.Registry.Links.Count); Assert.Equal(0,api.Writes);
    }
    [Fact] public async Task TwoClientsSerializeLocalWritesAndObserveOtherClientChanges()
    {
        var (source,api)=await SourceAsync(); var other=new GitHubBoardSource(api,new GitHubStorage(Path.Combine(directory,"other")));
        await other.AccountAsync(); await other.LinkAsync(api.Snapshot,[1],false);
        var baseline=api.Snapshot.Cards[0]; await source.SaveAsync("P1",baseline,new(Title:"First client"));
        await other.SaveAsync("P1",baseline,new(Body:"Second client"));
        await source.RefreshAsync("P1"); Assert.Equal("First client",source.Projects["P1"].Cards[0].Title); Assert.Equal("Second client",source.Projects["P1"].Cards[0].Body);
    }
    [Fact] public async Task ApiPaginationAppliesViewFilterAndHidesPullRequests()
    {
        var calls=new List<string>();
        using var http=new HttpClient(new Handler(async request =>
        {
            var payload=request.Content is null ? "" : await request.Content.ReadAsStringAsync(); calls.Add(payload+request.RequestUri);
            if (request.Method==HttpMethod.Get) return Reply("[{\"node_id\":\"draft\"}]");
            var variables=JsonDocument.Parse(payload).RootElement.GetProperty("variables");
            if (payload.Contains("viewerCanUpdate")) return Graph("{\"node\":{\"id\":\"P1\",\"number\":1,\"title\":\"Project\",\"viewerCanUpdate\":true,\"owner\":{\"login\":\"example\"}}}");
            if (payload.Contains("fields(first")) return Connection("fields","[{\"id\":\"STATUS\",\"name\":\"Status\",\"dataType\":\"SINGLE_SELECT\",\"options\":[{\"id\":\"todo\",\"name\":\"To do\",\"color\":\"GREEN\",\"description\":\"\"}]}]");
            if (payload.Contains("views(first")) return Connection("views","[{\"id\":\"V1\",\"number\":1,\"name\":\"Backlog\",\"layout\":\"BOARD_LAYOUT\",\"filter\":\"is:draft\",\"groupByFields\":{\"nodes\":[{\"id\":\"STATUS\"}],\"pageInfo\":{\"hasNextPage\":false}},\"sortByFields\":{\"nodes\":[],\"pageInfo\":{\"hasNextPage\":false}}}]");
            var second=variables.GetProperty("cursor").ValueKind==JsonValueKind.String;
            return second ? Connection("items","["+Item("pr","PullRequest")+"]")
                : Connection("items","["+Item("draft","DraftIssue")+","+Item("issue","Issue")+"]",true);
        }));
        var api=new GitHubApi(http,_ => Task.FromResult("fixture-token")); var snapshot=await api.ProjectAsync("P1");
        Assert.Equal(2,snapshot.Cards.Length); Assert.Single(snapshot.Views[0].ItemIds); Assert.Equal("draft",snapshot.Views[0].ItemIds[0]);
        Assert.Equal(2,calls.Count(c => c.Contains("items(first"))); Assert.Contains(calls,c => c.Contains("/views/1/items"));
    }
    [Fact] public async Task FailedLaterPageRejectsEntireSnapshot()
    {
        var pages=0;
        using var http=new HttpClient(new Handler(async request =>
        {
            var body=await request.Content!.ReadAsStringAsync();
            if (body.Contains("viewer {")) return Graph("{\"viewer\":{\"id\":\"ACCOUNT\",\"login\":\"tester\"}}");
            if (body.Contains("viewerCanUpdate")) return Graph("{\"node\":{\"id\":\"P1\",\"number\":1,\"title\":\"Project\",\"viewerCanUpdate\":true,\"owner\":{\"login\":\"example\"}}}");
            pages++;
            return pages==1 ? Connection("fields","[]",true) : Reply("{}",HttpStatusCode.ServiceUnavailable);
        }));
        var api=new GitHubApi(http,_ => Task.FromResult("fixture-token"));
        var source=new GitHubBoardSource(api,new GitHubStorage(directory));
        await source.AccountAsync(); await source.LinkAsync(GitHubFakeApi.Fixture(),[1],false);
        await Assert.ThrowsAsync<GitHubApiException>(() => source.RefreshAsync("P1"));
        Assert.Equal(2,pages); Assert.Equal(2,source.Projects["P1"].Cards.Length); Assert.False(source.Online("P1"));
        var restarted=new GitHubBoardSource(api,new GitHubStorage(directory)); await restarted.InitializeAsync();
        Assert.Equal(2,restarted.Projects["P1"].Cards.Length);
    }
    [Fact] public async Task StaleMovesAndRemovalsRequireExplicitChoice()
    {
        var (source,api)=await SourceAsync(); var original=api.Snapshot.Cards[0]; var order=api.Snapshot.Cards.Select(c => c.Id).ToArray();
        api.Snapshot=api.Snapshot with { Cards=[original with { StatusId="done",Body="External edit" },api.Snapshot.Cards[1]] };
        var status=await Assert.ThrowsAsync<GitHubConflictException>(() => source.MoveAsync("P1",original,"todo",null,false));
        Assert.Equal("Status",Assert.Single(status.Conflicts).Field); Assert.True(source.Online("P1"));
        var current=api.Snapshot.Cards[0]; api.Snapshot=api.Snapshot with { Cards=[api.Snapshot.Cards[1],current] };
        var position=await Assert.ThrowsAsync<GitHubConflictException>(() => source.MoveAsync("P1",current,"done",null,true,originalOrder:order));
        Assert.Equal("Position",Assert.Single(position.Conflicts).Field); Assert.Equal(0,api.Writes);
        await source.MoveAsync("P1",current,"done",null,true,new Dictionary<string,string> { ["Position"]=position.Conflicts[0].Remote },originalOrder:order);
        Assert.Equal("draft",api.Snapshot.Cards[0].Id);
        var removal=await Assert.ThrowsAsync<GitHubConflictException>(() => source.RemoveAsync("P1",original));
        Assert.Equal("Card",Assert.Single(removal.Conflicts).Field);
        await source.RemoveAsync("P1",original,accepted:new Dictionary<string,string> { ["Card"]=removal.Conflicts[0].Remote });
        Assert.DoesNotContain(api.Snapshot.Cards,c => c.Id==original.Id);
        Assert.False(source.Capabilities(GitHubIdentity.Board("P1",1).ToString(),GitHubIdentity.Card("P1",original.Id).ToString()).MoveCards);
    }
    [Fact] public async Task ExternalColumnRenameConflictsWithoutLosingOtherOptionChanges()
    {
        var (source,api)=await SourceAsync(); var original=api.Snapshot.Options[0];
        api.Snapshot=api.Snapshot with { Options=[original with { Name="Remote",Color="BLUE" },api.Snapshot.Options[1] with { Description="External description" }] };
        await Assert.ThrowsAsync<GitHubConflictException>(() => source.ColumnAsync("P1",original,"Mine")); Assert.Equal(0,api.Writes);
        await source.ColumnAsync("P1",original,"Mine",true);
        Assert.Equal("BLUE",api.Snapshot.Options[0].Color); Assert.Equal("External description",api.Snapshot.Options[1].Description);
    }
    [Fact] public async Task ApiExposesOnlyProjectMutationsAndKeepsOptionIdentities()
    {
        var bodies=new List<string>();
        using var http=new HttpClient(new Handler(async request =>
        {
            var body=await request.Content!.ReadAsStringAsync(); bodies.Add(body);
            if (body.Contains("addProjectV2DraftIssue")) return Graph("{\"addProjectV2DraftIssue\":{\"projectItem\":{\"id\":\"new\",\"content\":{\"id\":\"draft-content\",\"title\":\"New\",\"body\":\"Body\",\"createdAt\":\"2026-10-08T10:00:00Z\",\"updatedAt\":\"2026-10-08T10:00:00Z\"}}}}");
            var query=JsonDocument.Parse(body).RootElement.GetProperty("query").GetString()!;
            var name=System.Text.RegularExpressions.Regex.Match(query,@"\{ (\w+)\(input:").Groups[1].Value;
            return Graph(JsonSerializer.Serialize(new Dictionary<string,object> { [name]=new { clientMutationId=(string?)null } }));
        }));
        var api=new GitHubApi(http,_ => Task.FromResult("fixture-token"));
        await api.CreateDraftAsync("P1","New","Body","operation"); await api.UpdateDraftAsync("D1",new(Title:"Changed"));
        await api.SetStatusAsync("P1","draft","STATUS","todo"); await api.SetStatusAsync("P1","draft","STATUS",null);
        await api.PositionAsync("P1","draft","issue"); await api.RemoveAsync("P1","draft"); await api.OptionsAsync("STATUS",GitHubFakeApi.Fixture().Options);
        Assert.Equal(7,bodies.Count);
        Assert.All(bodies,b => Assert.DoesNotContain("client_secret",b));
        var options=JsonDocument.Parse(bodies[^1]).RootElement.GetProperty("variables").GetProperty("input").GetProperty("singleSelectOptions");
        Assert.Equal("todo",options[0].GetProperty("id").GetString()); Assert.Equal("GREEN",options[0].GetProperty("color").GetString());
        Assert.DoesNotContain(bodies,b => b.Contains("createIssue(") || b.Contains("updateIssue(") || b.Contains("deleteIssue("));
    }
    [Fact] public async Task DeviceFlowSignsInWithExpiringTokensAndCanBeCancelled()
    {
        var store=new Tokens(); var polls=0;
        using var http=new HttpClient(new Handler(async request =>
        {
            var body=await request.Content!.ReadAsStringAsync(); Assert.DoesNotContain("client_secret",body);
            if (request.RequestUri!.AbsolutePath.EndsWith("/code")) return Reply("{\"device_code\":\"synthetic-device\",\"user_code\":\"ABCD-EFGH\",\"verification_uri\":\"https://github.com/login/device\",\"interval\":5,\"expires_in\":900}");
            polls++; return Reply("{\"access_token\":\"synthetic-access\",\"refresh_token\":\"synthetic-refresh\",\"expires_in\":28800,\"refresh_token_expires_in\":15897600}");
        }));
        var auth=new GitHubAuthentication(http,"public-client-id",store); var code=await auth.BeginAsync();
        using var cancel=new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => auth.SignInAsync(code,cancel.Token)); Assert.Equal(0,polls);
        await auth.SignInAsync(code); Assert.Equal(1,polls); Assert.Equal("synthetic-access",await auth.AccessTokenAsync());
    }
    [Theory]
    [InlineData("{\"error\":\"device_flow_disabled\"}")]
    [InlineData("{}")]
    [InlineData("invalid json")]
    public async Task FailedDeviceSetupReturnsAnActionableError(string response)
    {
        using var http=new HttpClient(new Handler(request => Task.FromResult(Reply(response))));
        await Assert.ThrowsAsync<GitHubApiException>(() => new GitHubAuthentication(http,"public-client-id",new Tokens()).BeginAsync());
    }
    [Fact] public async Task MalformedMutationResponseIsUncertainAndIsNotRetried()
    {
        var calls=0;
        using var http=new HttpClient(new Handler(request => { calls++; return Task.FromResult(Graph("{}")); }));
        var api=new GitHubApi(http,_ => Task.FromResult("fixture-token"));
        Assert.True((await Assert.ThrowsAsync<GitHubApiException>(() => api.CreateDraftAsync("P1","New","Body","operation"))).Uncertain);
        Assert.Equal(1,calls);
        Assert.True((await Assert.ThrowsAsync<GitHubApiException>(() => api.SetStatusAsync("P1","draft","STATUS","todo"))).Uncertain);
        Assert.Equal(2,calls);
    }
    [Fact] public async Task OrganizationAccessRevokedDuringSelectionReturnsHandledError()
    {
        using var http=new HttpClient(new Handler(request => Task.FromResult(Graph("{\"organization\":null}"))));
        await Assert.ThrowsAsync<GitHubApiException>(() => new GitHubApi(http,_ => Task.FromResult("fixture-token")).ProjectsAsync("example"));
    }
    [Fact] public async Task RateLimitAndUnsafePaginationStopRequests()
    {
        var requests=0;
        using var http=new HttpClient(new Handler(request =>
        {
            requests++; var response=Reply("{}",HttpStatusCode.TooManyRequests); response.Headers.Add("Retry-After","60"); return Task.FromResult(response);
        }));
        var api=new GitHubApi(http,_ => Task.FromResult("fixture-token"));
        await Assert.ThrowsAsync<GitHubApiException>(() => api.ViewerAsync()); await Assert.ThrowsAsync<GitHubApiException>(() => api.ViewerAsync());
        Assert.Equal(1,requests); Assert.True(api.RetryAt>DateTimeOffset.UtcNow);
        using var other=new HttpClient(new Handler(request =>
        {
            var response=Reply("{\"installations\":[]}"); response.Headers.Add("Link","<https://example.com/token-steal>; rel=\"next\""); return Task.FromResult(response);
        }));
        await Assert.ThrowsAsync<GitHubApiException>(() => new GitHubApi(other,_ => Task.FromResult("fixture-token")).OrganizationsAsync());
        var graphCalls=0;
        using var limited=new HttpClient(new Handler(request =>
        {
            graphCalls++; var response=Reply("{\"errors\":[{\"type\":\"RATE_LIMITED\"}]}");
            response.Headers.Add("X-RateLimit-Remaining","0"); response.Headers.Add("X-RateLimit-Reset",DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds().ToString());
            return Task.FromResult(response);
        }));
        var graphApi=new GitHubApi(limited,_ => Task.FromResult("fixture-token"));
        await Assert.ThrowsAsync<GitHubApiException>(() => graphApi.ViewerAsync()); await Assert.ThrowsAsync<GitHubApiException>(() => graphApi.ViewerAsync());
        Assert.Equal(1,graphCalls); Assert.True(graphApi.RetryAt>DateTimeOffset.UtcNow);
    }
    [Fact] public async Task DeviceFlowRefreshNeedsNoSecretAndPersistsRotatedTokens()
    {
        var store=new Tokens { Value=new("old","refresh",DateTimeOffset.UtcNow.AddSeconds(-1),DateTimeOffset.UtcNow.AddDays(1)) };
        string body="";
        using var http=new HttpClient(new Handler(async request =>
        {
            body=await request.Content!.ReadAsStringAsync(); return Reply("{\"access_token\":\"new\",\"refresh_token\":\"rotated\",\"expires_in\":28800,\"refresh_token_expires_in\":15897600}");
        }));
        var auth=new GitHubAuthentication(http,"public-client-id",store);
        Assert.Equal("new",await auth.AccessTokenAsync()); Assert.DoesNotContain("client_secret",body); Assert.Equal("rotated",store.Value!.RefreshToken);
        await auth.SignOutAsync(); Assert.Null(store.Value);
    }
    private static string Item(string id,string kind) => JsonSerializer.Serialize(new { id,isArchived=false,
        status=new { optionId="todo",field=new { id="STATUS" } },
        content=new { __typename=kind,id="content-"+id,title=id,body="body",url="https://github.com/example/repo/issues/1",createdAt="2026-10-08T10:00:00Z",updatedAt="2026-10-08T10:00:00Z" } });
    private static HttpResponseMessage Connection(string name,string nodes,bool more=false) => Graph("{\"node\":{\""+name+"\":{\"nodes\":"+nodes+",\"pageInfo\":{\"hasNextPage\":"+(more ? "true" : "false")+",\"endCursor\":\"next\"}}}}");
    private static HttpResponseMessage Graph(string data) => Reply("{\"data\":"+data+"}");
    private static HttpResponseMessage Reply(string body,HttpStatusCode code=HttpStatusCode.OK) => new(code) { Content=new StringContent(body,Encoding.UTF8,"application/json") };
    private sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) => action(request); }
    private sealed class Tokens : IGitHubTokenStore
    {
        public GitHubTokens? Value;
        public Task<GitHubTokens?> LoadAsync(CancellationToken ct=default) => Task.FromResult(Value);
        public Task SaveAsync(GitHubTokens value,CancellationToken ct=default) { Value=value; return Task.CompletedTask; }
        public Task DeleteAsync(CancellationToken ct=default) { Value=null; return Task.CompletedTask; }
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory,true); }
}
