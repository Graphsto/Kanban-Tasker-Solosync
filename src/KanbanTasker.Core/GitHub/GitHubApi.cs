using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KanbanTasker.Core.GitHub;

public interface IGitHubApi
{
    DateTimeOffset? RetryAt { get; }
    TimeSpan MinimumRefreshInterval { get; }
    Task<(string Id, string Login)> ViewerAsync(CancellationToken ct = default);
    Task<GitHubOrganization[]> OrganizationsAsync(CancellationToken ct = default);
    Task<GitHubProjectSummary[]> ProjectsAsync(string organization, CancellationToken ct = default);
    Task<GitHubProjectSnapshot> ProjectAsync(string id, CancellationToken ct = default);
    Task<GitHubCard> CreateDraftAsync(string project, string title, string body, string operation, CancellationToken ct = default);
    Task UpdateDraftAsync(string contentId, GitHubEdit edit, CancellationToken ct = default);
    Task SetStatusAsync(string project, string item, string field, string? option, CancellationToken ct = default);
    Task PositionAsync(string project, string item, string? after, CancellationToken ct = default);
    Task RemoveAsync(string project, string item, CancellationToken ct = default);
    Task OptionsAsync(string field, GitHubOption[] options, CancellationToken ct = default);
}

/// <summary>Only the explicitly allowed project mutations are exposed. Never creates or edits repository issues.</summary>
public sealed class GitHubApi(HttpClient http, Func<CancellationToken, Task<string>> token) : IGitHubApi
{
    public DateTimeOffset? RetryAt { get; private set; }
    public TimeSpan MinimumRefreshInterval { get; private set; }=TimeSpan.FromSeconds(5);
    private double secondsPerRestRequest;
    private int restRequests;
    private double secondsPerGraphPoint;
    private int graphPoints;
    public async Task<(string Id, string Login)> ViewerAsync(CancellationToken ct = default)
    {
        var data = await GraphAsync("query { viewer { id login } }", new { }, false, ct);
        var viewer = Required(data,"viewer");
        if (S(viewer,"id").Length == 0 || S(viewer,"login").Length == 0) throw new GitHubApiException("GitHub sign-in expired. Sign in again.");
        return (S(viewer, "id"), S(viewer, "login"));
    }
    public async Task<GitHubOrganization[]> OrganizationsAsync(CancellationToken ct = default)
    {
        var result = new Dictionary<string, GitHubOrganization>(StringComparer.OrdinalIgnoreCase);
        foreach (var installation in await PagesAsync("https://api.github.com/user/installations?per_page=100", "installations", ct))
        {
            var account = Required(installation,"account");
            if (S(account, "type") == "Organization")
            { var login = S(account, "login"); result[login] = new(login, login); }
        }
        return result.Values.OrderBy(x => x.Login).ToArray();
    }
    public async Task<GitHubProjectSummary[]> ProjectsAsync(string organization, CancellationToken ct = default)
    {
        var result = new List<GitHubProjectSummary>();
        var visited = new HashSet<string>();
        string? cursor = null;
        do
        {
            var data = await GraphAsync("""
                query($org:String!,$cursor:String) { organization(login:$org) { projectsV2(first:100,after:$cursor) {
                  nodes { id number title } pageInfo { hasNextPage endCursor } } } }
                """, new { org = organization, cursor }, false, ct);
            var connection = Required(Required(data,"organization"),"projectsV2");
            result.AddRange(connection.GetProperty("nodes").EnumerateArray().Select(p =>
                new GitHubProjectSummary(S(p, "id"), p.GetProperty("number").GetInt32(), S(p, "title"), organization)));
            cursor = Next(connection);
            if (cursor is not null && (!visited.Add(cursor) || visited.Count > 10000)) throw new GitHubApiException("GitHub returned invalid pagination.");
        } while (cursor is not null);
        return result.ToArray();
    }
    public async Task<GitHubProjectSnapshot> ProjectAsync(string id, CancellationToken ct = default)
    {
        try { return await LoadProjectAsync(id,ct); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new GitHubApiException("GitHub returned an incomplete response."); }
    }
    private async Task<GitHubProjectSnapshot> LoadProjectAsync(string id, CancellationToken ct)
    {
        var restBefore=restRequests;
        var graphBefore=graphPoints;
        var fields = new List<JsonElement>();
        var views = new List<JsonElement>();
        var items = new List<JsonElement>();
        var project = await NodeAsync(id, "id number title viewerCanUpdate owner { ... on Organization { login } }", new { id }, ct);
        var organization = S(project.GetProperty("owner"), "login");
        if (string.IsNullOrEmpty(organization)) throw new GitHubApiException("Only organization projects are supported.");
        await ConnectionAsync(id, "fields", "... on ProjectV2FieldCommon { id name dataType } ... on ProjectV2SingleSelectField { options { id name color description } }", fields, ct);
        var status = fields.FirstOrDefault(f => S(f, "name") == "Status" && S(f, "dataType") == "SINGLE_SELECT");
        if (status.ValueKind == JsonValueKind.Undefined) throw new GitHubApiException("This project does not have a Status selection field.");
        var statusId = S(status, "id");
        var options = status.GetProperty("options").EnumerateArray().Select(o => new GitHubOption(S(o, "id"), S(o, "name"), S(o, "color"), S(o, "description"))).ToArray();
        await ConnectionAsync(id, "views", """
            id number name layout filter groupByFields(first:100) { nodes { ... on ProjectV2FieldCommon { id } } pageInfo { hasNextPage } }
            verticalGroupByFields(first:100) { nodes { ... on ProjectV2FieldCommon { id } } pageInfo { hasNextPage } }
            sortByFields(first:100) { nodes { direction field { ... on ProjectV2FieldCommon { id name dataType } } } pageInfo { hasNextPage } }
            """, views, ct);
        var sortFields=views.SelectMany(v => v.GetProperty("sortByFields").GetProperty("nodes").EnumerateArray())
            .Select(s => s.GetProperty("field")).Where(f => S(f,"name") != "Title").DistinctBy(f => S(f,"id")).ToArray();
        const string valueSelection="""
              ... on ProjectV2ItemFieldSingleSelectValue { optionId name field { ... on ProjectV2FieldCommon { id } } }
              ... on ProjectV2ItemFieldTextValue { text field { ... on ProjectV2FieldCommon { id } } }
              ... on ProjectV2ItemFieldNumberValue { number field { ... on ProjectV2FieldCommon { id } } }
              ... on ProjectV2ItemFieldDateValue { date field { ... on ProjectV2FieldCommon { id } } }
            """;
        var selection="id isArchived status:fieldValueByName(name:\"Status\"){ "+valueSelection+" } ";
        for (var i=0;i<sortFields.Length;i++) selection+="sort"+i+":fieldValueByName(name:"+JsonSerializer.Serialize(S(sortFields[i],"name"))+"){ "+valueSelection+" } ";
        await ConnectionAsync(id, "items", selection + """
            content { __typename ... on DraftIssue { id title body createdAt updatedAt }
              ... on Issue { id title body url createdAt updatedAt } }
            """, items, ct);
        var cards = new List<GitHubCard>();
        var values = new Dictionary<string, Dictionary<string, string>>();
        foreach (var item in items)
        {
            if (item.GetProperty("isArchived").GetBoolean()) continue;
            var content = item.GetProperty("content");
            if (content.ValueKind != JsonValueKind.Object || S(content, "__typename") is not ("DraftIssue" or "Issue")) continue;
            var map = new Dictionary<string, string>();
            string? option = null;
            foreach (var alias in Enumerable.Range(0,sortFields.Length).Select(i => "sort"+i).Prepend("status"))
            {
                if (!item.TryGetProperty(alias,out var v) || v.ValueKind != JsonValueKind.Object) continue;
                if (!v.TryGetProperty("field", out var f) || !f.TryGetProperty("id", out var fid)) continue;
                var key = fid.GetString()!;
                if (v.TryGetProperty("optionId", out var oid))
                {
                    if (key == statusId) option = oid.GetString();
                    var def = fields.FirstOrDefault(d => S(d, "id") == key);
                    var index = def.ValueKind == JsonValueKind.Object && def.TryGetProperty("options", out var opts)
                        ? Array.FindIndex(opts.EnumerateArray().ToArray(), o => S(o, "id") == oid.GetString()) : -1;
                    map[key] = index.ToString("D10", CultureInfo.InvariantCulture);
                }
                else foreach (var name in new[] { "text", "number", "date" })
                    if (v.TryGetProperty(name, out var value)) map[key] = value.ToString();
            }
            var itemId = S(item, "id");
            values[itemId] = map;
            cards.Add(new(itemId, S(content, "id"), S(content, "__typename") == "Issue" ? GitHubCardKind.Issue : GitHubCardKind.Draft,
                S(content, "title"), S(content, "body"), option, content.TryGetProperty("url", out var url) ? url.GetString() : null,
                content.GetProperty("createdAt").GetDateTimeOffset(), content.GetProperty("updatedAt").GetDateTimeOffset()));
        }
        var boardViews = new List<GitHubView>();
        foreach (var view in views)
        {
            RequireComplete(view.GetProperty("groupByFields")); RequireComplete(view.GetProperty("verticalGroupByFields")); RequireComplete(view.GetProperty("sortByFields"));
            // GitHub board columns are vertical groups; groupByFields configures swimlanes.
            var columns = view.GetProperty("verticalGroupByFields").GetProperty("nodes").EnumerateArray().ToArray();
            var hasRowGrouping = view.GetProperty("groupByFields").GetProperty("nodes").GetArrayLength() > 0;
            var sorts = view.GetProperty("sortByFields").GetProperty("nodes").EnumerateArray().ToArray();
            var number = view.GetProperty("number").GetInt32();
            var filter = view.GetProperty("filter").GetString() ?? "";
            var ids = Array.Empty<string>();
            if (S(view, "layout") == "BOARD_LAYOUT" && columns.Length == 1 && S(columns[0], "id") == statusId && !hasRowGrouping)
            {
                // An unfiltered view contains the complete, already paginated project item set.
                // Do not make its availability depend on the redundant REST view endpoint.
                if (string.IsNullOrWhiteSpace(filter)) ids=cards.Select(c => c.Id).ToArray();
                else
                {
                    var visible = await PagesAsync($"https://api.github.com/orgs/{Uri.EscapeDataString(organization)}/projectsV2/{project.GetProperty("number").GetInt32()}/views/{number}/items?per_page=100", null, ct);
                    ids = visible.Select(v => S(v, "node_id")).Where(x => cards.Any(c => c.Id == x)).ToArray();
                }
                if (sorts.Length == 0)
                {
                    var membership=ids.ToHashSet(StringComparer.Ordinal);
                    ids=cards.Where(c => membership.Contains(c.Id)).Select(c => c.Id).ToArray();
                }
                // The view endpoint applies filters; GraphQL configuration supplies saved sort rules.
                foreach (var sort in sorts.Reverse())
                {
                    var f = sort.GetProperty("field"); var key = S(f, "id"); var type = S(f, "dataType");
                    string Value(string itemId) => S(f, "name") == "Title" ? cards.First(c => c.Id == itemId).Title
                        : values.GetValueOrDefault(itemId)?.GetValueOrDefault(key) ?? "";
                    IComparer<string> comparer = type == "NUMBER" ? Comparer<string>.Create((a,b) =>
                        ParseNumber(a).CompareTo(ParseNumber(b))) : StringComparer.OrdinalIgnoreCase;
                    ids = (S(sort, "direction") == "DESC" ? ids.OrderByDescending(Value, comparer) : ids.OrderBy(Value, comparer)).ToArray();
                }
            }
            boardViews.Add(new(S(view, "id"), number, S(view, "name"), S(view, "layout"), columns.Length == 1 ? S(columns[0], "id") : null,
                sorts.Length > 0, filter, ids, hasRowGrouping));
        }
        MinimumRefreshInterval=TimeSpan.FromSeconds(Math.Max(5,Math.Max(secondsPerRestRequest*Math.Max(1,restRequests-restBefore),
            secondsPerGraphPoint*Math.Max(1,graphPoints-graphBefore))*1.25));
        return new(id, project.GetProperty("number").GetInt32(), organization, S(project, "title"), statusId,
            project.GetProperty("viewerCanUpdate").GetBoolean(), options, boardViews.ToArray(), cards.ToArray(), DateTimeOffset.UtcNow);
    }
    private static double ParseNumber(string value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : double.NegativeInfinity;
    private async Task<JsonElement> NodeAsync(string id, string selection, object variables, CancellationToken ct)
    {
        var data = await GraphAsync("query($id:ID!){ node(id:$id){ ... on ProjectV2 { " + selection + " } } }", variables, false, ct);
        var node = data.GetProperty("node");
        if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty("id", out _))
            throw new GitHubApiException("The GitHub project is unavailable or access was revoked.");
        return node;
    }
    private async Task ConnectionAsync(string id, string name, string selection, List<JsonElement> result, CancellationToken ct)
    {
        var visited = new HashSet<string>();
        string? cursor = null;
        do
        {
            var data = await GraphAsync("query($id:ID!,$cursor:String){ node(id:$id){ ... on ProjectV2 { " + name
                + "(first:100,after:$cursor){ nodes { " + selection + " } pageInfo { hasNextPage endCursor } } } } }", new { id, cursor }, false, ct);
            var connection = data.GetProperty("node").GetProperty(name);
            result.AddRange(connection.GetProperty("nodes").EnumerateArray().Select(x => x.Clone()));
            cursor = Next(connection);
            if (cursor is not null && (!visited.Add(cursor) || visited.Count > 10000)) throw new GitHubApiException("GitHub returned invalid pagination.");
        } while (cursor is not null);
    }
    private static string? Next(JsonElement connection) => connection.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean()
        ? connection.GetProperty("pageInfo").GetProperty("endCursor").GetString() ?? throw new GitHubApiException("GitHub returned incomplete pagination.") : null;
    private static void RequireComplete(JsonElement connection)
    {
        if (connection.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean())
            throw new GitHubApiException("This project has too many card fields or view rules to load safely.");
    }
    private async Task<JsonElement[]> PagesAsync(string url, string? root, CancellationToken ct)
    {
        var result = new List<JsonElement>();
        var visited = new HashSet<string>();
        while (!string.IsNullOrEmpty(url))
        {
            if (!visited.Add(url) || visited.Count > 10000) throw new GitHubApiException("GitHub returned invalid pagination.");
            var (data, next) = await SendAsync(url, null, false, ct);
            var array = root is null ? data : data.GetProperty(root);
            result.AddRange(array.EnumerateArray().Select(x => x.Clone()));
            url = next ?? "";
        }
        return result.ToArray();
    }
    public async Task<GitHubCard> CreateDraftAsync(string project, string title, string body, string operation, CancellationToken ct = default)
    {
        var data = await GraphAsync("mutation($input:AddProjectV2DraftIssueInput!){ addProjectV2DraftIssue(input:$input){ projectItem { id content { ... on DraftIssue { id title body createdAt updatedAt } } } } }",
            new { input = new { projectId = project, title, body, clientMutationId = operation } }, true, ct);
        try
        {
            var item = data.GetProperty("addProjectV2DraftIssue").GetProperty("projectItem"); var c = item.GetProperty("content");
            if (S(item,"id").Length == 0 || S(c,"id").Length == 0) throw new GitHubApiException("GitHub returned an incomplete response.",true);
            return new(S(item,"id"), S(c,"id"), GitHubCardKind.Draft, S(c,"title"), S(c,"body"), null, null,
                c.GetProperty("createdAt").GetDateTimeOffset(), c.GetProperty("updatedAt").GetDateTimeOffset());
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new GitHubApiException("GitHub returned an incomplete response.",true); }
    }
    public Task UpdateDraftAsync(string contentId, GitHubEdit edit, CancellationToken ct = default)
    {
        var input = new Dictionary<string, object?> { ["draftIssueId"] = contentId };
        if (edit.Title is not null) input["title"] = edit.Title;
        if (edit.Body is not null) input["body"] = edit.Body;
        return MutationAsync("UpdateProjectV2DraftIssueInput", "updateProjectV2DraftIssue", input, ct);
    }
    public Task SetStatusAsync(string project, string item, string field, string? option, CancellationToken ct = default) => option is null
        ? MutationAsync("ClearProjectV2ItemFieldValueInput", "clearProjectV2ItemFieldValue", new { projectId=project, itemId=item, fieldId=field }, ct)
        : MutationAsync("UpdateProjectV2ItemFieldValueInput", "updateProjectV2ItemFieldValue", new { projectId=project, itemId=item, fieldId=field, value=new { singleSelectOptionId=option } }, ct);
    public Task PositionAsync(string project, string item, string? after, CancellationToken ct = default) =>
        MutationAsync("UpdateProjectV2ItemPositionInput", "updateProjectV2ItemPosition", new { projectId=project, itemId=item, afterId=after }, ct);
    public Task RemoveAsync(string project, string item, CancellationToken ct = default) =>
        MutationAsync("DeleteProjectV2ItemInput", "deleteProjectV2Item", new { projectId=project, itemId=item }, ct);
    public Task OptionsAsync(string field, GitHubOption[] options, CancellationToken ct = default) =>
        MutationAsync("UpdateProjectV2FieldInput", "updateProjectV2Field", new { fieldId=field,
            singleSelectOptions=options.Select(o => new { id=string.IsNullOrEmpty(o.Id) ? null : o.Id, name=o.Name, color=o.Color, description=o.Description }).ToArray() }, ct);
    private async Task MutationAsync(string inputType, string name, object input, CancellationToken ct)
    {
        var data = await GraphAsync("mutation($input:" + inputType + "!){ " + name + "(input:$input){ clientMutationId } }", new { input }, true, ct);
        if (!data.TryGetProperty(name,out var result) || result.ValueKind != JsonValueKind.Object)
            throw new GitHubApiException("GitHub returned an incomplete response.",true);
    }
    private async Task<JsonElement> GraphAsync(string query, object variables, bool mutation, CancellationToken ct)
    {
        if (!mutation) query=query[..query.LastIndexOf('}')]+" rateLimit { cost remaining resetAt } }";
        var (json, _) = await SendAsync("https://api.github.com/graphql", new { query, variables }, mutation, ct);
        if (json.TryGetProperty("errors", out var errors))
        {
            if (errors.ValueKind == JsonValueKind.Array && errors.EnumerateArray().Any(e => S(e,"type") == "RATE_LIMITED"))
            {
                if (!(RetryAt > DateTimeOffset.UtcNow)) RetryAt = DateTimeOffset.UtcNow.AddMinutes(1);
                throw new GitHubApiException("GitHub rate limit reached. Editing is paused until requests are allowed again.",retryAt:RetryAt);
            }
            var uncertain=mutation && json.TryGetProperty("data",out var partial) && partial.ValueKind != JsonValueKind.Null;
            if (errors.ValueKind == JsonValueKind.Array && errors.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("extensions",out var extension)
                && S(extension,"code") is "selectionMismatch" or "undefinedField" or "argumentNotAccepted" or "variableMismatch" or "missingRequiredArguments" or "GRAPHQL_VALIDATION_FAILED"))
                throw new GitHubApiException("GitHub rejected an unsupported API request. Update the app and try again.",uncertain);
            var denied=errors.ValueKind == JsonValueKind.Array && errors.EnumerateArray().Any(e => S(e,"type") is "FORBIDDEN" or "INSUFFICIENT_SCOPES");
            throw new GitHubApiException(denied
                ? "GitHub access denied. Check the App installation, organization approval, and project permissions."
                : "GitHub could not complete the request. Refresh the board before trying again.",uncertain);
        }
        if (!json.TryGetProperty("data",out var data) || data.ValueKind != JsonValueKind.Object)
            throw new GitHubApiException("GitHub returned an incomplete response.",mutation);
        if (data.TryGetProperty("rateLimit",out var budget) && budget.ValueKind == JsonValueKind.Object)
        {
            graphPoints+=budget.GetProperty("cost").GetInt32();
            var available=budget.GetProperty("remaining").GetInt32();
            var reset=budget.GetProperty("resetAt").GetDateTimeOffset();
            secondsPerGraphPoint=Math.Max(0,(reset-DateTimeOffset.UtcNow).TotalSeconds)/Math.Max(1,available);
            if (available == 0) RetryAt=reset;
        }
        return data;
    }
    private async Task<(JsonElement Data, string? Next)> SendAsync(string url, object? body, bool mutation, CancellationToken ct)
    {
        var uri = new Uri(url);
        if (uri.Scheme != "https" || uri.Host != "api.github.com" || !uri.IsDefaultPort)
            throw new GitHubApiException("GitHub returned an unsafe API address.");
        if (RetryAt > DateTimeOffset.UtcNow) throw new GitHubApiException("GitHub rate limit reached. Editing is paused until requests are allowed again.", retryAt:RetryAt);
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await token(ct));
        request.Headers.UserAgent.ParseAdd("KanbanTasker-SoloSync");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        if (body is not null) request.Content = JsonContent.Create(body);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.Headers.TryGetValues("X-RateLimit-Remaining",out var remainingBudget) && remainingBudget.FirstOrDefault() == "0"
                && response.Headers.TryGetValues("X-RateLimit-Reset",out var budgetReset) && long.TryParse(budgetReset.FirstOrDefault(),out var resetSeconds))
                RetryAt=DateTimeOffset.FromUnixTimeSeconds(resetSeconds);
            if (request.Method == HttpMethod.Get)
            {
                restRequests++;
                if (response.Headers.TryGetValues("X-RateLimit-Remaining",out var available) && int.TryParse(available.FirstOrDefault(),out var count)
                    && response.Headers.TryGetValues("X-RateLimit-Reset",out var resetTime) && long.TryParse(resetTime.FirstOrDefault(),out var unix))
                    secondsPerRestRequest=Math.Max(0,(DateTimeOffset.FromUnixTimeSeconds(unix)-DateTimeOffset.UtcNow).TotalSeconds)/Math.Max(1,count);
            }
            if (response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode == HttpStatusCode.Forbidden &&
                (response.Headers.Contains("Retry-After") || response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.FirstOrDefault() == "0"))
            {
                RetryAt = response.Headers.RetryAfter?.Date ?? DateTimeOffset.UtcNow.Add(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
                if (response.Headers.TryGetValues("X-RateLimit-Reset", out var reset) && long.TryParse(reset.FirstOrDefault(), out var seconds))
                    RetryAt = DateTimeOffset.FromUnixTimeSeconds(seconds) > RetryAt ? DateTimeOffset.FromUnixTimeSeconds(seconds) : RetryAt;
                throw new GitHubApiException("GitHub rate limit reached. Editing is paused until requests are allowed again.", retryAt:RetryAt);
            }
            if (!response.IsSuccessStatusCode) throw new GitHubApiException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "GitHub sign-in expired. Sign in again.",
                HttpStatusCode.Forbidden => "GitHub access denied. Check the App installation, organization approval, and project permissions.",
                HttpStatusCode.NotFound => "The GitHub project is unavailable or access was revoked.",
                _ when (int)response.StatusCode >= 500 => "GitHub is temporarily unavailable.",
                _ => "GitHub could not complete the request. Refresh the board before trying again."
            }, mutation && (int)response.StatusCode >= 500);
            string? next = null;
            if (response.Headers.TryGetValues("Link", out var links))
            {
                var match = Regex.Match(string.Join(",", links), "<([^>]+)>;\\s*rel=\"next\"", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                if (match.Success) next = match.Groups[1].Value;
            }
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var json = JsonDocument.Parse(await WorkspaceJson.ReadAsync(stream, ct));
            return (json.RootElement.Clone(), next);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        { throw new GitHubApiException("GitHub is unreachable. Cached boards are read-only.", mutation); }
        catch (JsonException) { throw new GitHubApiException("GitHub returned an invalid response. Refresh the board.", mutation); }
    }
    private static string S(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
    private static JsonElement Required(JsonElement e,string name) => e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(name,out var value) && value.ValueKind == JsonValueKind.Object ? value
        : throw new GitHubApiException("The GitHub project is unavailable or access was revoked.");
}
