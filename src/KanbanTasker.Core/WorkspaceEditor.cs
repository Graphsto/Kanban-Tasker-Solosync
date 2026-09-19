using System.Text.Json;

namespace KanbanTasker.Core;

/// <summary>One user action against the latest merged document. Unchanged editor fields retain their versions.</summary>
public sealed class WorkspaceEditor(WorkspaceDocument document, ChangeClock clock)
{
    public WorkspaceDocument Document { get; } = document;
    private ChangeStamp? stamp;
    private ChangeStamp Stamp => stamp ??= clock.Next();
    public Guid CreateBoard(string name, string notes = "")
    {
        RequireText(name, "Board name");
        var board = NewEntity(Guid.Empty);
        board.Set(Fields.Name, name.Trim(), Stamp); board.Set(Fields.Notes, notes, Stamp);
        board.Set(Fields.Order, Array.Empty<Guid>(), Stamp);
        Document.Boards.Add(board);
        foreach (var title in new[] { "Backlog", "To Do", "In Progress", "Review", "Completed" }) CreateColumn(board.Id, title, 10);
        return board.Id;
    }
    public void EditBoard(Guid id, string name, string notes, string originalName, string originalNotes)
    {
        RequireText(name, "Board name");
        var board = Board(id);
        if (name.Trim() != originalName) Set(board, Fields.Name, name.Trim());
        if (notes != originalNotes) Set(board, Fields.Notes, notes);
    }
    public Guid CreateColumn(Guid boardId, string name, int limit)
    {
        var board = Board(boardId);
        ValidateColumn(boardId, Guid.Empty, name, limit);
        var column = NewEntity(boardId);
        column.Set(Fields.Name, name.Trim(), Stamp); column.Set(Fields.Limit, limit, Stamp);
        column.Set(Fields.Order, Array.Empty<Guid>(), Stamp);
        var ids = WorkspaceView.Columns(Document, boardId).Select(x => x.Id).Append(column.Id).ToArray();
        Document.Columns.Add(column);
        Set(board, Fields.Order, ids);
        return column.Id;
    }
    public void EditColumn(Guid id, string name, int limit, string originalName, int originalLimit)
    {
        var column = Column(id);
        ValidateColumn(column.BoardId, id, name, limit);
        if (name.Trim() != originalName) Set(column, Fields.Name, name.Trim());
        if (limit != originalLimit) Set(column, Fields.Limit, limit);
    }
    public void MoveColumn(Guid id, int targetIndex)
    {
        var column = Column(id);
        var ids = WorkspaceView.Columns(Document, column.BoardId).Select(x => x.Id).ToList();
        ids.Remove(id); ids.Insert(Math.Clamp(targetIndex, 0, ids.Count), id);
        Set(Board(column.BoardId), Fields.Order, ids.ToArray());
    }
    public Guid SaveTask(TaskData data, TaskData? original = null)
    {
        RequireText(data.Title, "Task title");
        if (data.Priority is not ("Low" or "Medium" or "High")) throw new ArgumentException("Choose a valid priority.");
        if (data.FinishDate < data.StartDate) throw new ArgumentException("Finish date cannot be before start date.");
        if (data.ReminderMinutes is not null && (data.DueDate is null || data.DueTime is null))
            throw new ArgumentException("A reminder needs a due date and time.");
        bool isNew = data.Id == Guid.Empty;
        var task = isNew ? NewEntity(data.BoardId) : Task(data.Id);
        var effectiveColumn = !isNew && original is not null && data.ColumnId == original.ColumnId
            ? task.Get<Guid>(Fields.ColumnId) : data.ColumnId;
        var column = Column(effectiveColumn);
        if (column.BoardId != data.BoardId) throw new ArgumentException("The task and column must belong to the same board.");
        if (task.BoardId != data.BoardId) throw new ArgumentException("A task cannot change boards.");
        // Only fields the user actually changed are written. Remote edits to other fields survive an open draft.
        void Edit<T>(string field, T value, T old)
        {
            if (isNew || !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(value), JsonSerializer.SerializeToElement(old))) Set(task, field, value);
        }
        if (!isNew && original is null) throw new ArgumentException("An existing task requires its original draft snapshot.");
        Edit(Fields.Title, data.Title.Trim(), original?.Title ?? "");
        Edit(Fields.Description, data.Description, original?.Description ?? "");
        Edit(Fields.Priority, data.Priority, original?.Priority ?? "Low");
        Edit(Fields.Tags, data.Tags.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToArray(), original?.Tags ?? []);
        Edit(Fields.DueDate, data.DueDate, original?.DueDate); Edit(Fields.DueTime, data.DueTime, original?.DueTime);
        Edit(Fields.StartDate, data.StartDate, original?.StartDate); Edit(Fields.FinishDate, data.FinishDate, original?.FinishDate);
        Edit(Fields.ReminderMinutes, data.ReminderMinutes, original?.ReminderMinutes);
        if (isNew)
        {
            task.Set(Fields.ColumnId, data.ColumnId, Stamp);
            task.Set(Fields.CreatedAt, DateTimeOffset.FromUnixTimeMilliseconds(Stamp.Milliseconds), Stamp);
            var order = WorkspaceView.Tasks(Document, column.Id).Select(x => x.Id).Append(task.Id).ToArray();
            Document.Tasks.Add(task); Set(column, Fields.Order, order);
        }
        else if (data.ColumnId != original!.ColumnId) MoveTask(task.Id, data.ColumnId, int.MaxValue);
        return task.Id;
    }
    public void MoveTask(Guid id, Guid columnId, int targetIndex)
    {
        var task = Task(id); var target = Column(columnId);
        if (task.BoardId != target.BoardId) throw new ArgumentException("Tasks can only move within their board.");
        var source = Column(task.Get<Guid>(Fields.ColumnId));
        var targetIds = WorkspaceView.Tasks(Document, columnId).Select(x => x.Id).Where(x => x != id).ToList();
        targetIds.Insert(Math.Clamp(targetIndex, 0, targetIds.Count), id);
        if (source.Id != columnId)
        {
            Set(source, Fields.Order, WorkspaceView.Tasks(Document, source.Id).Select(x => x.Id).Where(x => x != id).ToArray());
            Set(task, Fields.ColumnId, columnId);
        }
        Set(target, Fields.Order, targetIds.ToArray());
    }
    public void DeleteTask(Guid id) => Task(id).Deleted = Stamp;
    public void DeleteColumn(Guid id)
    {
        var column = Column(id); column.Deleted = Stamp;
        foreach (var task in Document.Tasks.Where(x => x.BoardId == column.BoardId && x.Get<Guid>(Fields.ColumnId) == id)) task.Deleted ??= Stamp;
    }
    public void DeleteBoard(Guid id)
    {
        Board(id).Deleted = Stamp;
        foreach (var child in Document.Columns.Concat(Document.Tasks).Where(x => x.BoardId == id)) child.Deleted ??= Stamp;
    }
    private void Set<T>(EntityRecord entity, string field, T value)
    {
        var json = JsonSerializer.SerializeToElement(value, WorkspaceJson.Options);
        if (!entity.Fields.TryGetValue(field, out var current) || !JsonElement.DeepEquals(current.Value, json))
            entity.Fields[field] = new(Stamp, json);
    }
    private EntityRecord NewEntity(Guid boardId) => new() { Id = Guid.NewGuid(), BoardId = boardId, Created = Stamp };
    private EntityRecord Board(Guid id) => WorkspaceView.Boards(Document).FirstOrDefault(x => x.Id == id)
        ?? throw new InvalidOperationException("This board was deleted. Your draft has been kept.");
    private EntityRecord Column(Guid id)
    {
        var column = Document.Columns.FirstOrDefault(x => x.Id == id && x.Deleted is null)
            ?? throw new InvalidOperationException("This column was deleted. Your draft has been kept.");
        Board(column.BoardId); return column;
    }
    private EntityRecord Task(Guid id) => WorkspaceView.AllTasks(Document).FirstOrDefault(x => x.Id == id)
        ?? throw new InvalidOperationException("This task was deleted. Your draft has been kept.");
    private void ValidateColumn(Guid boardId, Guid id, string name, int limit)
    {
        RequireText(name, "Column name");
        if (limit < 0) throw new ArgumentException("The task limit cannot be negative.");
        if (WorkspaceView.Columns(Document, boardId).Any(x => x.Id != id && x.Get<string>(Fields.Name).Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("This board already has a column with that name.");
    }
    private static void RequireText(string text, string name)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException($"{name} cannot be empty.");
    }
}
