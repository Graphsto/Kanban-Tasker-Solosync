using System.Text.Json;
using System.Text.Json.Serialization;

namespace KanbanTasker.Core;

/// <summary>Physical time orders concurrent edits; the counter preserves causality.</summary>
public readonly record struct ChangeStamp(long Milliseconds, long Counter, Guid DeviceId) : IComparable<ChangeStamp>
{
    public int CompareTo(ChangeStamp other)
    {
        var result = Milliseconds.CompareTo(other.Milliseconds);
        if (result == 0) result = Counter.CompareTo(other.Counter);
        return result == 0 ? DeviceId.CompareTo(other.DeviceId) : result;
    }
}

public sealed class ChangeClock(Guid deviceId, Func<DateTimeOffset>? now = null)
{
    private ChangeStamp last;
    public void Observe(ChangeStamp stamp)
    {
        if (stamp.CompareTo(last) > 0) last = stamp;
    }
    public void Observe(WorkspaceDocument document)
    {
        foreach (var entity in document.Entities)
        {
            Observe(entity.Created);
            if (entity.Deleted is { } deleted) Observe(deleted);
            foreach (var field in entity.Fields.Values) Observe(field.Stamp);
        }
    }
    public ChangeStamp Next()
    {
        var physical = (now?.Invoke() ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
        if (physical > last.Milliseconds) last = new(physical, 0, deviceId);
        else if (last.Counter < long.MaxValue) last = new(last.Milliseconds, last.Counter + 1, deviceId);
        else if (last.Milliseconds < 253402300799999) last = new(last.Milliseconds + 1, 0, deviceId);
        else throw new InvalidDataException("The change timestamp is exhausted. The data file was not modified.");
        return last;
    }
}

public sealed record VersionedValue(ChangeStamp Stamp, JsonElement Value)
{
    public static VersionedValue From<T>(T value, ChangeStamp stamp) =>
        new(stamp, JsonSerializer.SerializeToElement(value, WorkspaceJson.Options));
}

public sealed class EntityRecord
{
    public Guid Id { get; set; }
    public Guid BoardId { get; set; }
    public ChangeStamp Created { get; set; }
    public ChangeStamp? Deleted { get; set; }
    public SortedDictionary<string, VersionedValue> Fields { get; set; } = new(StringComparer.Ordinal);
    public T Get<T>(string field) => Fields[field].Value.Deserialize<T>(WorkspaceJson.Options)!;
    public void Set<T>(string field, T value, ChangeStamp stamp) => Fields[field] = VersionedValue.From(value, stamp);
    public EntityRecord Clone() => new()
    {
        Id = Id, BoardId = BoardId, Created = Created, Deleted = Deleted,
        Fields = new(Fields.ToDictionary(x => x.Key, x => x.Value with { Value = x.Value.Value.Clone() }), StringComparer.Ordinal)
    };
}

public sealed class WorkspaceDocument
{
    public const int CurrentSchemaVersion = 2;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public Guid DocumentId { get; set; } = Guid.NewGuid();
    public List<EntityRecord> Groups { get; set; } = [];
    public List<EntityRecord> Boards { get; set; } = [];
    public List<EntityRecord> Columns { get; set; } = [];
    public List<EntityRecord> Tasks { get; set; } = [];
    [JsonIgnore] public IEnumerable<EntityRecord> Entities => Groups.Concat(Boards).Concat(Columns).Concat(Tasks);
    public WorkspaceDocument Clone() => new()
    {
        SchemaVersion = SchemaVersion, DocumentId = DocumentId,
        Groups = Groups.Select(x => x.Clone()).ToList(),
        Boards = Boards.Select(x => x.Clone()).ToList(), Columns = Columns.Select(x => x.Clone()).ToList(),
        Tasks = Tasks.Select(x => x.Clone()).ToList()
    };
}

public static class Fields
{
    public const string Name = "name", Notes = "notes", Order = "order", Limit = "limit";
    public const string GroupId = "groupId";
    public const string ColumnId = "columnId", Title = "title", Description = "description";
    public const string Priority = "priority", Tags = "tags", CreatedAt = "createdAt";
    public const string DueDate = "dueDate", DueTime = "dueTime", StartDate = "startDate", FinishDate = "finishDate";
    public const string ReminderMinutes = "reminderMinutes";
}

public sealed record TaskData
{
    public Guid Id { get; init; }
    public Guid BoardId { get; init; }
    public Guid ColumnId { get; init; }
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Priority { get; init; } = "Low";
    public string[] Tags { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; }
    public DateOnly? DueDate { get; init; }
    public TimeOnly? DueTime { get; init; }
    public DateOnly? StartDate { get; init; }
    public DateOnly? FinishDate { get; init; }
    public int? ReminderMinutes { get; init; }
    public static TaskData From(EntityRecord e) => new()
    {
        Id = e.Id, BoardId = e.BoardId, ColumnId = e.Get<Guid>(Fields.ColumnId),
        Title = e.Get<string>(Fields.Title), Description = e.Get<string>(Fields.Description),
        Priority = e.Get<string>(Fields.Priority), Tags = e.Get<string[]>(Fields.Tags),
        CreatedAt = e.Get<DateTimeOffset>(Fields.CreatedAt), DueDate = e.Get<DateOnly?>(Fields.DueDate),
        DueTime = e.Get<TimeOnly?>(Fields.DueTime), StartDate = e.Get<DateOnly?>(Fields.StartDate),
        FinishDate = e.Get<DateOnly?>(Fields.FinishDate), ReminderMinutes = e.Get<int?>(Fields.ReminderMinutes)
    };
}
