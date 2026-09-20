using System.Text.Json;

namespace KanbanTasker.Core;

public static class WorkspaceMerge
{
    /// <summary>Pure, commutative, associative and idempotent union of field registers and tombstones.</summary>
    public static WorkspaceDocument Merge(WorkspaceDocument left, WorkspaceDocument right)
    {
        if (left.DocumentId != right.DocumentId || left.SchemaVersion != right.SchemaVersion)
            throw new InvalidDataException("These files belong to different workspaces or format versions.");
        return new()
        {
            DocumentId = left.DocumentId, SchemaVersion = left.SchemaVersion,
            Groups = MergeEntities(left.Groups, right.Groups),
            Boards = MergeEntities(left.Boards, right.Boards), Columns = MergeEntities(left.Columns, right.Columns),
            Tasks = MergeEntities(left.Tasks, right.Tasks)
        };
    }
    private static List<EntityRecord> MergeEntities(List<EntityRecord> left, List<EntityRecord> right)
    {
        var records = left.ToDictionary(x => x.Id, x => x.Clone());
        foreach (var incoming in right)
        {
            if (!records.TryGetValue(incoming.Id, out var current))
            {
                records.Add(incoming.Id, incoming.Clone());
                continue;
            }
            if (current.BoardId != incoming.BoardId || current.Created != incoming.Created)
                throw new InvalidDataException("An entry has conflicting immutable identity information.");
            if (incoming.Deleted is { } deleted && (current.Deleted is null || deleted.CompareTo(current.Deleted.Value) > 0))
                current.Deleted = deleted;
            foreach (var (key, value) in incoming.Fields)
            {
                if (!current.Fields.TryGetValue(key, out var previous) || value.Stamp.CompareTo(previous.Stamp) > 0)
                    current.Fields[key] = value with { Value = value.Value.Clone() };
                else if (value.Stamp == previous.Stamp && !JsonElement.DeepEquals(value.Value, previous.Value))
                    throw new InvalidDataException("Two different values have the same change identifier.");
            }
        }
        return records.Values.OrderBy(x => x.Id).ToList();
    }
}

public static class WorkspaceView
{
    public static IEnumerable<EntityRecord> Groups(WorkspaceDocument d) => d.Groups
        .Where(x => x.Deleted is null).OrderBy(x => x.Created).ThenBy(x => x.Id);
    // A deleted group is only a deleted label, never a deleted board or task.
    // Resolve concurrent assignments to a deleted group as ungrouped too.
    public static Guid? BoardGroupId(WorkspaceDocument d, EntityRecord board) =>
        board.Get<Guid?>(Fields.GroupId) is { } id && Groups(d).Any(x => x.Id == id) ? id : null;
    public static IEnumerable<EntityRecord> BoardsInGroup(WorkspaceDocument d, Guid? groupId) =>
        Boards(d).Where(x => BoardGroupId(d, x) == groupId);
    public static IEnumerable<EntityRecord> Boards(WorkspaceDocument d) => d.Boards
        .Where(x => x.Deleted is null).OrderBy(x => x.Created).ThenBy(x => x.Id);
    public static IEnumerable<EntityRecord> Columns(WorkspaceDocument d, Guid boardId)
    {
        var board = Boards(d).FirstOrDefault(x => x.Id == boardId);
        return board is null ? [] : Ordered(d.Columns.Where(x => x.BoardId == boardId && x.Deleted is null), board.Get<Guid[]>(Fields.Order));
    }
    public static IEnumerable<EntityRecord> Tasks(WorkspaceDocument d, Guid columnId)
    {
        var column = d.Columns.FirstOrDefault(x => x.Id == columnId && x.Deleted is null);
        if (column is null || !Boards(d).Any(x => x.Id == column.BoardId)) return [];
        return Ordered(d.Tasks.Where(x => x.BoardId == column.BoardId && x.Deleted is null && x.Get<Guid>(Fields.ColumnId) == columnId), column.Get<Guid[]>(Fields.Order));
    }
    public static IEnumerable<EntityRecord> AllTasks(WorkspaceDocument d) => Boards(d)
        .SelectMany(b => Columns(d, b.Id)).SelectMany(c => Tasks(d, c.Id));
    private static IEnumerable<EntityRecord> Ordered(IEnumerable<EntityRecord> items, Guid[] order)
    {
        var available = items.ToDictionary(x => x.Id);
        foreach (var id in order.Distinct()) if (available.Remove(id, out var item)) yield return item;
        foreach (var item in available.Values.OrderBy(x => x.Created).ThenBy(x => x.Id)) yield return item;
    }
}
