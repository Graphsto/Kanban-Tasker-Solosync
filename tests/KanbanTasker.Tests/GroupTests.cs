using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KanbanTasker.Core;
using KanbanTasker.Desktop;

namespace KanbanTasker.Tests;

public sealed class GroupTests
{
    private static WorkspaceEditor Edit(WorkspaceDocument document, int device = 1, long milliseconds = 2000)
    {
        var clock = new ChangeClock(new Guid(device, 0, 0, new byte[8]), () => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds));
        clock.Observe(document);
        return new(document, clock);
    }

    [Fact]
    public void GroupsDefaultToDisabledAndSelectionSurvivesPreferenceRoundTrip()
    {
        Assert.False(new LocalPreferences().GroupsEnabled);
        Assert.False(JsonSerializer.Deserialize<LocalPreferences>("{}")!.GroupsEnabled);
        var preferences = new LocalPreferences { GroupsEnabled = true, GroupWorkspaceId = Guid.NewGuid(), SelectedGroup = Guid.NewGuid() };
        var restored = JsonSerializer.Deserialize<LocalPreferences>(JsonSerializer.Serialize(preferences))!;
        Assert.True(restored.GroupsEnabled);
        Assert.Equal(preferences.GroupWorkspaceId, restored.GroupWorkspaceId);
        Assert.Equal(preferences.SelectedGroup, restored.SelectedGroup);
    }

    [Fact]
    public void GroupsAndAssignmentsRoundTripWithoutChangingTaskOrColumnIdentity()
    {
        var (document, board, column, task) = MergeTests.Example();
        var group = Edit(document).CreateGroup(" Work ");
        Edit(document).AssignBoardGroup(board, group);
        Edit(document).RenameGroup(group, "Office");
        var parsed = WorkspaceJson.Parse(WorkspaceJson.Serialize(document));
        Assert.Equal(2, parsed.SchemaVersion);
        Assert.Equal("Office", WorkspaceView.Groups(parsed).Single().Get<string>(Fields.Name));
        Assert.Equal(board, WorkspaceView.BoardsInGroup(parsed, group).Single().Id);
        Assert.Empty(WorkspaceView.BoardsInGroup(parsed, null));
        Assert.Equal(column, WorkspaceView.AllTasks(parsed).Single().Get<Guid>(Fields.ColumnId));
        Assert.Equal(task, WorkspaceView.AllTasks(parsed).Single().Id);
        Assert.True(WorkspaceJson.Equal(document, parsed));
        Edit(parsed).AssignBoardGroup(board, null);
        Assert.Equal(board, WorkspaceView.BoardsInGroup(parsed, null).Single().Id);
    }

    [Fact]
    public void DeletingGroupKeepsEveryBoardColumnAndTaskAndDoesNotResurrectOnOldCopies()
    {
        var (document, board, _, task) = MergeTests.Example();
        var group = Edit(document).CreateGroup("Work");
        Edit(document).AssignBoardGroup(board, group);
        var old = document.Clone();
        Edit(document).DeleteGroup(group);
        var merged = WorkspaceMerge.Merge(old, document);
        Assert.Empty(WorkspaceView.Groups(merged));
        Assert.NotNull(merged.Groups.Single().Deleted);
        Assert.Equal(board, WorkspaceView.BoardsInGroup(merged, null).Single().Id);
        Assert.Equal(5, WorkspaceView.Columns(merged, board).Count());
        Assert.Equal(task, WorkspaceView.AllTasks(merged).Single().Id);
        Assert.True(WorkspaceJson.Equal(document, merged));
        Assert.Throws<InvalidOperationException>(() => Edit(merged).AssignBoardGroup(board, group));
    }

    [Fact]
    public void ConcurrentCreationAndAssignmentToADeletedGroupRemainsVisibleAndConverges()
    {
        var seed = new WorkspaceDocument();
        var group = Edit(seed).CreateGroup("Work");
        var a = seed.Clone(); var b = seed.Clone();
        var board = Edit(a, 2).CreateBoard("Parallel board", groupId: group);
        Edit(b, 3).DeleteGroup(group);
        var result = WorkspaceMerge.Merge(a, b);
        Assert.Equal(board, WorkspaceView.BoardsInGroup(result, null).Single().Id);
        Assert.True(WorkspaceJson.Equal(result, WorkspaceMerge.Merge(b, a)));
        Assert.True(WorkspaceJson.Equal(result, WorkspaceMerge.Merge(result, a)));
        WorkspaceJson.Parse(WorkspaceJson.Serialize(result));
    }

    [Fact]
    public void GroupChangesMergeWithIndependentBoardEditsAndClockTies()
    {
        var (seed, board, _, _) = MergeTests.Example();
        var work = Edit(seed).CreateGroup("Work");
        var home = Edit(seed).CreateGroup("Home");
        var a = seed.Clone(); var b = seed.Clone(); var c = seed.Clone();
        Edit(a, 2).AssignBoardGroup(board, work);
        Edit(b, 3).AssignBoardGroup(board, home);
        Edit(c, 4).EditBoard(board, "Renamed board", "Updated notes", "Work", "Notes");
        Edit(c, 4).RenameGroup(home, "Personal");
        var ab = WorkspaceMerge.Merge(a, b);
        Assert.Equal(home, WorkspaceView.BoardGroupId(ab, ab.Boards.Single()));
        var abc = WorkspaceMerge.Merge(ab, c);
        Assert.Equal(home, WorkspaceView.BoardGroupId(abc, abc.Boards.Single()));
        Assert.Equal("Renamed board", abc.Boards.Single().Get<string>(Fields.Name));
        Assert.Equal("Personal", abc.Groups.Single(g => g.Id == home).Get<string>(Fields.Name));
        Assert.True(WorkspaceJson.Equal(abc, WorkspaceMerge.Merge(c, WorkspaceMerge.Merge(b, a))));
        Assert.True(WorkspaceJson.Equal(abc, WorkspaceMerge.Merge(a, WorkspaceMerge.Merge(b, c))));
    }

    [Fact]
    public void RenameKeepsIdentityAndDuplicateOrEmptyNamesAreRejected()
    {
        var d = new WorkspaceDocument();
        var work = Edit(d).CreateGroup("Work");
        var home = Edit(d).CreateGroup("Home");
        Assert.Throws<ArgumentException>(() => Edit(d).CreateGroup(" work "));
        Assert.Throws<ArgumentException>(() => Edit(d).RenameGroup(home, "WORK"));
        Assert.Throws<ArgumentException>(() => Edit(d).CreateGroup(" \t"));
        Assert.Throws<ArgumentException>(() => Edit(d).RenameGroup(work, ""));
        Edit(d).RenameGroup(work, "Office");
        Assert.Equal(work, d.Groups.Single(g => g.Get<string>(Fields.Name) == "Office").Id);
        Assert.Throws<InvalidOperationException>(() => Edit(d).CreateBoard("Invalid", groupId: Guid.NewGuid()));
        Assert.Empty(d.Boards);
    }

    [Theory]
    [InlineData("missingGroups")] [InlineData("nullGroups")] [InlineData("nullGroup")]
    [InlineData("unknownGroup")] [InlineData("missingAssignment")] [InlineData("groupParent")]
    [InlineData("duplicateIdentity")] [InlineData("emptyName")]
    public void MalformedGroupsAreRejected(string mutation)
    {
        var (d, _, _, _) = MergeTests.Example();
        Edit(d).CreateGroup("Work");
        var json = JsonNode.Parse(WorkspaceJson.Serialize(d))!.AsObject();
        switch (mutation)
        {
            case "missingGroups": json.Remove("groups"); break;
            case "nullGroups": json["groups"] = null; break;
            case "nullGroup": json["groups"]!.AsArray().Add((JsonNode?)null); break;
            case "unknownGroup": json["boards"]![0]!["fields"]!["groupId"]!["value"] = Guid.NewGuid().ToString(); break;
            case "missingAssignment": json["boards"]![0]!["fields"]!.AsObject().Remove("groupId"); break;
            case "groupParent": json["groups"]![0]!["boardId"] = d.Boards[0].Id.ToString(); break;
            case "duplicateIdentity": json["groups"]![0]!["id"] = d.Boards[0].Id.ToString(); break;
            case "emptyName": json["groups"]![0]!["fields"]!["name"]!["value"] = " "; break;
        }
        Assert.Throws<InvalidDataException>(() => WorkspaceJson.Parse(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    [Fact]
    public void PreviousFormatIsExplicitlyRejectedWithoutMigration()
    {
        var error = Assert.Throws<InvalidDataException>(() => WorkspaceJson.Parse(Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":1,\"documentId\":\"11111111-1111-1111-1111-111111111111\",\"boards\":[],\"columns\":[],\"tasks\":[]}")));
        Assert.Contains("previous format", error.Message);
    }
}
