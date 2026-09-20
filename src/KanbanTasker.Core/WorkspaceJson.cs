using System.Text.Json;
using System.Text.Json.Serialization;

namespace KanbanTasker.Core;

public static class WorkspaceJson
{
    public const int MaximumBytes = 64 * 1024 * 1024;
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        MaxDepth = 32,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    public static WorkspaceDocument Parse(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("The workspace exceeds the 64 MB safety limit.");
        try
        {
            using var json = JsonDocument.Parse(bytes, new() { MaxDepth = 32 });
            CheckDuplicateKeys(json.RootElement);
            foreach (var name in new[] { "schemaVersion", "documentId", "boards", "columns", "tasks" })
                if (!json.RootElement.TryGetProperty(name, out _)) throw new InvalidDataException($"Missing workspace property: {name}.");
            if (json.RootElement.GetProperty("schemaVersion").GetInt32() == 1)
                throw new InvalidDataException("This data file uses the previous format. Create a new data file for this version of Kanban Tasker.");
            if (json.RootElement.GetProperty("schemaVersion").GetInt32() != WorkspaceDocument.CurrentSchemaVersion)
                throw new InvalidDataException("This workspace uses an unsupported format version. Update the app before opening it.");
            if (!json.RootElement.TryGetProperty("groups", out _)) throw new InvalidDataException("Missing workspace property: groups.");
            var document = JsonSerializer.Deserialize<WorkspaceDocument>(bytes.Span, Options)
                ?? throw new InvalidDataException("The workspace is empty.");
            Validate(document);
            return document;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException or ArgumentException or OverflowException)
        {
            throw new InvalidDataException("The workspace file is not valid Kanban Tasker JSON.", ex);
        }
    }
    public static byte[] Serialize(WorkspaceDocument document)
    {
        Validate(document);
        var ordered = document.Clone();
        ordered.Groups = ordered.Groups.OrderBy(x => x.Id).ToList();
        ordered.Boards = ordered.Boards.OrderBy(x => x.Id).ToList();
        ordered.Columns = ordered.Columns.OrderBy(x => x.Id).ToList();
        ordered.Tasks = ordered.Tasks.OrderBy(x => x.Id).ToList();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(ordered, Options);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("The workspace exceeds the 64 MB safety limit.");
        return bytes;
    }
    // Check during copying as well: a sync client can grow a file after its length was checked.
    public static async Task<byte[]> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        if (stream.CanSeek && stream.Length - stream.Position > MaximumBytes)
            throw new InvalidDataException("The workspace exceeds the 64 MB safety limit.");
        using var output = new MemoryStream();
        var chunk = new byte[65536];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, MaximumBytes - output.Length + 1)), ct);
            if (read == 0) return output.ToArray();
            if (output.Length + read > MaximumBytes) throw new InvalidDataException("The workspace exceeds the 64 MB safety limit.");
            output.Write(chunk, 0, read);
        }
    }
    public static bool Equal(WorkspaceDocument a, WorkspaceDocument b) => Serialize(a).AsSpan().SequenceEqual(Serialize(b));
    private static void CheckDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON property.");
                CheckDuplicateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CheckDuplicateKeys(item);
    }
    public static void Validate(WorkspaceDocument document)
    {
        if (document.SchemaVersion != WorkspaceDocument.CurrentSchemaVersion || document.DocumentId == Guid.Empty
            || document.Groups is null || document.Boards is null || document.Columns is null || document.Tasks is null)
            throw new InvalidDataException("Invalid workspace header.");
        var ids = new HashSet<Guid>();
        foreach (var entity in document.Entities)
        {
            if (entity is null || entity.Id == Guid.Empty || !ids.Add(entity.Id) || entity.Fields is null)
                throw new InvalidDataException("Missing or duplicate entry ID.");
            ValidateStamp(entity.Created);
            if (entity.Deleted is { } deleted) ValidateStamp(deleted);
            foreach (var value in entity.Fields.Values)
            {
                if (value is null || value.Value.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("Missing field value.");
                ValidateStamp(value.Stamp);
            }
        }
        foreach (var group in document.Groups)
        {
            CheckFields(group, [Fields.Name]);
            if (group.BoardId != Guid.Empty) throw new InvalidDataException("A group cannot belong to a board.");
            Text(group, Fields.Name, true);
        }
        var groups = document.Groups.Select(x => x.Id).ToHashSet();
        foreach (var board in document.Boards)
        {
            CheckFields(board, [Fields.Name, Fields.Notes, Fields.Order, Fields.GroupId]);
            if (board.BoardId != Guid.Empty) throw new InvalidDataException("A board cannot belong to another board.");
            if (board.Get<Guid?>(Fields.GroupId) is { } groupId && !groups.Contains(groupId))
                throw new InvalidDataException("A board references an unknown group.");
            Text(board, Fields.Name, true); Text(board, Fields.Notes); Order(board);
        }
        var boards = document.Boards.Select(x => x.Id).ToHashSet();
        var columns = document.Columns.ToDictionary(x => x.Id);
        foreach (var column in document.Columns)
        {
            CheckFields(column, [Fields.Name, Fields.Limit, Fields.Order]);
            if (!boards.Contains(column.BoardId)) throw new InvalidDataException("A column references an unknown board.");
            Text(column, Fields.Name, true); Order(column);
            if (column.Get<int>(Fields.Limit) < 0) throw new InvalidDataException("Column limits cannot be negative.");
        }
        foreach (var task in document.Tasks)
        {
            CheckFields(task, [Fields.ColumnId, Fields.Title, Fields.Description, Fields.Priority, Fields.Tags, Fields.CreatedAt,
                Fields.DueDate, Fields.DueTime, Fields.StartDate, Fields.FinishDate, Fields.ReminderMinutes]);
            var data = TaskData.From(task);
            if (!columns.TryGetValue(data.ColumnId, out var parent) || parent.BoardId != task.BoardId)
                throw new InvalidDataException("A task references an unknown column or board.");
            Text(task, Fields.Title, true); Text(task, Fields.Description);
            if (data.Priority is not ("Low" or "Medium" or "High") || data.Tags is null || data.Tags.Any(x => x is null)
                || data.ReminderMinutes < 0) throw new InvalidDataException("Invalid task fields.");
        }
    }
    private static void ValidateStamp(ChangeStamp stamp)
    {
        if (stamp.DeviceId == Guid.Empty || stamp.Counter < 0 || stamp.Milliseconds < 0 || stamp.Milliseconds > 253402300799999)
            throw new InvalidDataException("Invalid change timestamp.");
    }
    private static void CheckFields(EntityRecord entity, string[] fields)
    {
        if (!entity.Fields.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(fields))
            throw new InvalidDataException("An entry has missing or unsupported fields.");
    }
    private static void Text(EntityRecord entity, string field, bool required = false)
    {
        var value = entity.Get<string>(field);
        if (value is null || (required && string.IsNullOrWhiteSpace(value))) throw new InvalidDataException($"Invalid {field}.");
    }
    private static void Order(EntityRecord entity)
    {
        var order = entity.Get<Guid[]>(Fields.Order);
        if (order is null || order.Any(x => x == Guid.Empty) || order.Distinct().Count() != order.Length)
            throw new InvalidDataException("Invalid item order.");
    }
}
