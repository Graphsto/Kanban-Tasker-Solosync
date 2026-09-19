using KanbanTasker.Core;
using System.Text;

namespace KanbanTasker.Tests;

public class MergeTests
{
    private static ChangeClock Clock(int device, long milliseconds = 1000) => new(new Guid(device, 0, 0, new byte[8]), () => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds));
    internal static (WorkspaceDocument Document, Guid Board, Guid Column, Guid Task) Example()
    {
        var d = new WorkspaceDocument(); var clock = Clock(1);
        var board = new WorkspaceEditor(d, clock).CreateBoard("Work", "Notes");
        var column = WorkspaceView.Columns(d, board).First().Id;
        var task = new WorkspaceEditor(d, clock).SaveTask(new() { BoardId = board, ColumnId = column, Title = "Original" });
        return (d, board, column, task);
    }
    private static WorkspaceEditor Editor(WorkspaceDocument d, int device, long milliseconds = 2000)
    { var clock = Clock(device, milliseconds); clock.Observe(d); return new(d, clock); }

    [Fact]
    public void IndependentFieldsAndAnOpenDraftPreserveRemoteEdits()
    {
        var (d, _, _, taskId) = Example(); var original = TaskData.From(d.Tasks.Single());
        var remote = d.Clone(); Editor(remote, 2).SaveTask(original with { Description = "Remote description" }, original);
        var local = remote.Clone(); Editor(local, 1).SaveTask(original with { Title = "Local title" }, original);
        var task = TaskData.From(WorkspaceMerge.Merge(remote, local).Tasks.Single(x => x.Id == taskId));
        Assert.Equal("Remote description", task.Description); Assert.Equal("Local title", task.Title);
    }
    [Fact]
    public void LastFieldWriteWinsAndTiesConverge()
    {
        var (d, _, _, _) = Example(); var original = TaskData.From(d.Tasks.Single());
        var a = d.Clone(); var b = d.Clone();
        Editor(a, 2).SaveTask(original with { Title = "A" }, original);
        Editor(b, 3).SaveTask(original with { Title = "B" }, original);
        Assert.Equal("B", WorkspaceMerge.Merge(a, b).Tasks.Single().Get<string>(Fields.Title));
        Assert.True(WorkspaceJson.Equal(WorkspaceMerge.Merge(a, b), WorkspaceMerge.Merge(b, a)));
        var later = d.Clone(); Editor(later, 1, 3000).SaveTask(original with { Title = "Later" }, original);
        Assert.Equal("Later", WorkspaceMerge.Merge(b, later).Tasks.Single().Get<string>(Fields.Title));
    }
    [Fact]
    public void ClockSurvivesClockRollbackAndRemoteFutureClock()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(2000);
        var clock = new ChangeClock(Guid.NewGuid(), () => now);
        var first = clock.Next(); now = now.AddDays(-1); var second = clock.Next();
        Assert.True(second.CompareTo(first) > 0);
        var future = new ChangeStamp(9000, 99, Guid.NewGuid()); clock.Observe(future);
        Assert.True(clock.Next().CompareTo(future) > 0);
    }
    [Fact]
    public void ConcurrentCreationIsNotLostWhenOrdersConflict()
    {
        var (d, board, column, _) = Example(); var a = d.Clone(); var b = d.Clone();
        Editor(a, 2).SaveTask(new() { BoardId = board, ColumnId = column, Title = "A" });
        Editor(b, 3).SaveTask(new() { BoardId = board, ColumnId = column, Title = "B" });
        var result = WorkspaceMerge.Merge(a, b);
        Assert.Equal(3, WorkspaceView.Tasks(result, column).Count());
        Assert.Equal(3, WorkspaceView.Tasks(result, column).Select(x => x.Id).Distinct().Count());
    }
    [Theory]
    [InlineData("task")][InlineData("column")][InlineData("board")]
    public void DeletionsSurviveLateFilesAndConcurrentEdits(string kind)
    {
        var (d, board, column, task) = Example(); var deleted = d.Clone(); var edited = d.Clone();
        var editor = Editor(deleted, 2);
        if (kind == "task") editor.DeleteTask(task); else if (kind == "column") editor.DeleteColumn(column); else editor.DeleteBoard(board);
        var original = TaskData.From(d.Tasks.Single());
        Editor(edited, 3, 9000).SaveTask(original with { Title = "Changed later" }, original);
        var merged = WorkspaceMerge.Merge(deleted, edited);
        Assert.Empty(WorkspaceView.AllTasks(merged));
        Assert.Empty(WorkspaceView.AllTasks(WorkspaceMerge.Merge(merged, d)));
    }
    [Fact]
    public void DeletingAColumnDoesNotDeleteMatchingNamesOnOtherBoards()
    {
        var (d, _, column, _) = Example(); var editor = Editor(d, 2);
        var secondBoard = editor.CreateBoard("Other"); var otherColumn = WorkspaceView.Columns(d, secondBoard).First().Id;
        editor.SaveTask(new() { BoardId = secondBoard, ColumnId = otherColumn, Title = "Keep me" });
        editor.DeleteColumn(column);
        Assert.Single(WorkspaceView.AllTasks(d));
        Assert.Equal("Keep me", WorkspaceView.AllTasks(d).Single().Get<string>(Fields.Title));
    }
    [Fact]
    public void ColumnMoveRetainsMembershipAndRoundTrips()
    {
        var (d, board, column, task) = Example(); Editor(d, 2).MoveColumn(column, 4);
        var reloaded = WorkspaceJson.Parse(WorkspaceJson.Serialize(d));
        Assert.Equal(column, WorkspaceView.Columns(reloaded, board).Last().Id);
        Assert.Equal(task, WorkspaceView.Tasks(reloaded, column).Single().Id);
        Editor(reloaded, 3).MoveColumn(column, 0);
        Assert.Equal(column, WorkspaceView.Columns(reloaded, board).First().Id);
    }
    [Fact]
    public void ConcurrentCardMovesProduceOneLocationAndStableOrder()
    {
        var (d, board, _, task) = Example(); var columns = WorkspaceView.Columns(d, board).ToArray();
        var a = d.Clone(); var b = d.Clone();
        Editor(a, 2).MoveTask(task, columns[1].Id, 0); Editor(b, 3).MoveTask(task, columns[2].Id, 0);
        var merged = WorkspaceMerge.Merge(a, b);
        Assert.Single(WorkspaceView.AllTasks(merged));
        Assert.Equal(task, WorkspaceView.Tasks(merged, columns[2].Id).Single().Id);
    }
    [Fact]
    public void MergeAlgebraHoldsForRandomDeliverySequences()
    {
        var (d, board, column, _) = Example(); var replicas = Enumerable.Range(2, 8).Select(device =>
        {
            var copy = d.Clone(); var e = Editor(copy, device);
            e.CreateColumn(board, $"Column {device}", device);
            e.SaveTask(new() { BoardId = board, ColumnId = column, Title = $"Task {device}" });
            return copy;
        }).ToArray();
        var expected = replicas.Aggregate(d, WorkspaceMerge.Merge);
        var random = new Random(1234);
        for (int i = 0; i < 30; i++)
        {
            var actual = replicas.OrderBy(_ => random.Next()).Concat(replicas.OrderBy(_ => random.Next())).Aggregate(d, WorkspaceMerge.Merge);
            Assert.True(WorkspaceJson.Equal(expected, actual));
            Assert.True(WorkspaceJson.Equal(actual, WorkspaceMerge.Merge(actual, actual)));
        }
        Assert.Equal(9, WorkspaceView.AllTasks(expected).Count());
        Assert.Equal(13, WorkspaceView.Columns(expected, board).Count());
    }
    [Fact]
    public void DatesUnicodeAndTagsRoundTrip()
    {
        var (d, _, _, _) = Example(); var original = TaskData.From(d.Tasks.Single());
        Editor(d, 2).SaveTask(original with
        {
            Title = "Überprüfung 日本語", Tags = ["one,two", "💡", "spaces allowed"], DueDate = new(2027, 2, 3), DueTime = new(9, 30),
            ReminderMinutes = 15, StartDate = new(2027, 2, 1), FinishDate = new(2027, 2, 2)
        }, original);
        var result = TaskData.From(WorkspaceJson.Parse(WorkspaceJson.Serialize(d)).Tasks.Single());
        Assert.Equal("Überprüfung 日本語", result.Title); Assert.Equal("one,two", result.Tags[0]);
        Assert.Equal(new DateOnly(2027, 2, 3), result.DueDate); Assert.Equal(15, result.ReminderMinutes);
    }
    [Theory]
    [InlineData("{}")][InlineData("null")][InlineData("{\"schemaVersion\":99}")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1}")]
    public void InvalidFilesAreRejected(string text) => Assert.Throws<InvalidDataException>(() => WorkspaceJson.Parse(Encoding.UTF8.GetBytes(text)));
    [Fact]
    public void SavingAnOpenDraftDoesNotUndoARemoteMoveOrDependOnTheOldColumn()
    {
        var (d, board, column, task) = Example(); var original = TaskData.From(d.Tasks.Single());
        var nextColumn = WorkspaceView.Columns(d, board).Skip(1).First().Id;
        Editor(d, 2).MoveTask(task, nextColumn, 0); Editor(d, 2, 3000).DeleteColumn(column);
        Editor(d, 3, 4000).SaveTask(original with { Title = "Edited after move" }, original);
        Assert.Equal(task, WorkspaceView.Tasks(d, nextColumn).Single().Id);
        Assert.Equal("Edited after move", WorkspaceView.AllTasks(d).Single().Get<string>(Fields.Title));
    }
    [Fact]
    public void ConcurrentColumnMovesAndCreationHaveOneDeterministicOrder()
    {
        var (d, board, first, _) = Example(); var a = d.Clone(); var b = d.Clone();
        Editor(a, 2).MoveColumn(first, 4);
        Editor(b, 3).CreateColumn(board, "New column", 0);
        var merged = WorkspaceMerge.Merge(a, b);
        Assert.Equal(6, WorkspaceView.Columns(merged, board).Count());
        Assert.Equal(WorkspaceView.Columns(merged, board).Select(x => x.Id), WorkspaceView.Columns(WorkspaceMerge.Merge(b, a), board).Select(x => x.Id));
    }
    [Fact]
    public void CrossWorkspaceMergeIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => WorkspaceMerge.Merge(new(), new()));
    }
}
