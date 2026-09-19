using System.Text;
using System.Text.Json.Nodes;
using KanbanTasker.Core;
using KanbanTasker.Updates;

namespace KanbanTasker.Tests;

public sealed class InputSafetyTests
{
    [Fact]
    public void ImportedMaximumCounterStillAllowsTheNextEdit()
    {
        var document = MergeTests.Example().Document;
        var stamp = new ChangeStamp(DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds(), long.MaxValue, Guid.NewGuid());
        document.Boards[0].Created = stamp;
        var parsed = WorkspaceJson.Parse(WorkspaceJson.Serialize(document));
        var clock = new ChangeClock(Guid.NewGuid(), () => DateTimeOffset.UnixEpoch);
        clock.Observe(parsed);
        new WorkspaceEditor(parsed, clock).CreateBoard("Still editable");
        Assert.True(parsed.Boards.Last().Created.CompareTo(stamp) > 0);
        WorkspaceJson.Parse(WorkspaceJson.Serialize(parsed));
    }

    [Fact]
    public void ExhaustedCalendarAndCounterFailWithAHandledDataError()
    {
        var clock = new ChangeClock(Guid.NewGuid());
        clock.Observe(new ChangeStamp(253402300799999, long.MaxValue, Guid.NewGuid()));
        Assert.Throws<InvalidDataException>(() => clock.Next());
    }

    [Theory]
    [InlineData("null")] [InlineData("[]")] [InlineData("true")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1}")]
    public void InvalidRootsFailWithAHandledDataError(string json) =>
        Assert.Throws<InvalidDataException>(() => WorkspaceJson.Parse(Encoding.UTF8.GetBytes(json)));

    [Theory]
    [InlineData("boards")] [InlineData("columns")] [InlineData("tasks")]
    public void NullEntriesAreRejected(string collection)
    {
        var json = JsonNode.Parse(WorkspaceJson.Serialize(MergeTests.Example().Document))!;
        json[collection]!.AsArray().Add((JsonNode?)null);
        Assert.Throws<InvalidDataException>(() => WorkspaceJson.Parse(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    [Fact]
    public void DeterministicMalformedInputCorpusCannotEscapeAsUnexpectedExceptions()
    {
        var original = WorkspaceJson.Serialize(MergeTests.Example().Document);
        var random = new Random(192026);
        for (var i = 0; i < 400; i++)
        {
            var mutated = original.ToArray();
            for (var j = 0; j < 1 + i % 7; j++) mutated[random.Next(mutated.Length)] = (byte)random.Next(256);
            if (i % 3 == 0) Array.Resize(ref mutated, random.Next(mutated.Length));
            try
            {
                var parsed = WorkspaceJson.Parse(mutated);
                Assert.True(WorkspaceJson.Equal(parsed, WorkspaceJson.Parse(WorkspaceJson.Serialize(parsed))));
            }
            catch (InvalidDataException) { }
        }
    }

    [Fact]
    public async Task GrowingOrUnseekableInputStopsAtTheReadBudget()
    {
        using var input = new EndlessStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => WorkspaceJson.ReadAsync(input));
        Assert.Equal(WorkspaceJson.MaximumBytes + 1L, input.BytesRead);
    }

    [Fact]
    public void UpdateManifestRejectsDtdAndExcessiveExpansion()
    {
        foreach (var xml in new[] { "<!DOCTYPE Package [<!ENTITY e 'test'>]><Package>&e;</Package>",
            "<Package>" + new string('x', 1024 * 1024) + "</Package>" })
        {
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(xml));
            Assert.Throws<InvalidDataException>(() => UpdateFiles.ReadManifest(input, "x64"));
        }
    }

    [Fact]
    public void ReminderArithmeticHandlesImportedExtremeDates()
    {
        var task = new TaskData { DueDate = DateOnly.MinValue, DueTime = new(12, 0), ReminderMinutes = int.MaxValue };
        Assert.False(ReminderTime.TryGet(task, out _, out _));
        task = task with { DueDate = new(2026, 10, 21), ReminderMinutes = 15 };
        Assert.True(ReminderTime.TryGet(task, out var due, out var at));
        Assert.Equal(TimeSpan.FromMinutes(15), due - at);
    }

    private sealed class EndlessStream : Stream
    {
        public long BytesRead;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        { Array.Clear(buffer, offset, count); BytesRead += count; return count; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); buffer.Span.Clear(); BytesRead += buffer.Length; return ValueTask.FromResult(buffer.Length); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
