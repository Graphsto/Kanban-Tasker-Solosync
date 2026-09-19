using KanbanTasker.Core;

namespace KanbanTasker.Tests;

public sealed class LiveSyncTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "KanbanTasker.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PassiveOpenDeviceReceivesRemoteTaskWithoutWritingItBack()
    {
        var (pathA, pathB) = await SeedDevicesAsync();
        var writes = new CountingWriter(pathB);
        await using var a = Store("a");
        await using var b = Store("b", writes);
        await a.OpenAsync(pathA); await b.OpenAsync(pathB);
        var board = a.Current!.Boards.Single().Id;
        var column = WorkspaceView.Columns(a.Current!, board).First().Id;
        Guid task = default;
        await a.CommitAsync(e => task = e.SaveTask(new() { BoardId = board, ColumnId = column, Title = "Remote live task" }));

        // The transport replaces B's local file while both application stores remain running.
        var downloaded = await File.ReadAllBytesAsync(pathA);
        await ReceiveTaskAsync(b, task, () => new AtomicFileWriter().WriteAsync(pathB, downloaded, true, default));
        Assert.Equal(StorageState.Saved, b.Status.State);
        Assert.Equal(0, writes.Publications);
        Assert.Equal(downloaded, await File.ReadAllBytesAsync(pathB));
        Assert.True(WorkspaceJson.Equal(a.Current!, b.Current!));
    }

    [Fact]
    public async Task TwoOpenDevicesMergeNextcloudConflictCopyAndKeepItForManualCleanup()
    {
        var (pathA, pathB) = await SeedDevicesAsync();
        await using var a = Store("a");
        await using var b = Store("b");
        await a.OpenAsync(pathA); await b.OpenAsync(pathB);
        var board = a.Current!.Boards.Single().Id;
        var column = WorkspaceView.Columns(a.Current!, board).First().Id;
        Guid taskA = default, taskB = default;
        await Task.WhenAll(
            a.CommitAsync(e => taskA = e.SaveTask(new() { BoardId = board, ColumnId = column, Title = "From A" })),
            b.CommitAsync(e => taskB = e.SaveTask(new() { BoardId = board, ColumnId = column, Title = "From B" })));

        // Nextcloud preserves the local version under this filename before installing the server version.
        var conflict = Path.Combine(Path.GetDirectoryName(pathB)!, "KanbanTasker.kanban (conflicted copy 2026-09-19 021914).json");
        await ReceiveTaskAsync(b, taskA, async () =>
        {
            File.Move(pathB, conflict);
            await new AtomicFileWriter().WriteAsync(pathB, await File.ReadAllBytesAsync(pathA), false, default);
        });
        Assert.Contains(b.Current!.Tasks, t => t.Id == taskB);
        var merged = await File.ReadAllBytesAsync(pathB);
        Assert.Contains(WorkspaceJson.Parse(merged).Tasks, t => t.Id == taskB);

        await ReceiveTaskAsync(a, taskB, () => new AtomicFileWriter().WriteAsync(pathA, merged, true, default));
        Assert.True(WorkspaceJson.Equal(a.Current!, b.Current!));
        Assert.Contains(WorkspaceJson.Parse(await File.ReadAllBytesAsync(conflict)).Tasks, t => t.Id == taskB);
        var timestamp = File.GetLastWriteTimeUtc(pathB);
        await b.RefreshAsync(); await a.RefreshAsync(); await b.RefreshAsync();
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(pathB));
    }

    private WorkspaceStore Store(string device, IAtomicFileWriter? writer = null) =>
        new(Path.Combine(root, "recovery-" + device), Guid.NewGuid(), writer, pollInterval: TimeSpan.FromMilliseconds(400));

    private async Task<(string A, string B)> SeedDevicesAsync()
    {
        var document = new WorkspaceDocument();
        new WorkspaceEditor(document, new ChangeClock(Guid.NewGuid())).CreateBoard("Live sync test");
        var bytes = WorkspaceJson.Serialize(document);
        var a = Path.Combine(root, "device-a", "KanbanTasker.kanban.json");
        var b = Path.Combine(root, "device-b", "KanbanTasker.kanban.json");
        Directory.CreateDirectory(Path.GetDirectoryName(a)!); Directory.CreateDirectory(Path.GetDirectoryName(b)!);
        await File.WriteAllBytesAsync(a, bytes); await File.WriteAllBytesAsync(b, bytes);
        return (a, b);
    }

    private static async Task ReceiveTaskAsync(WorkspaceStore store, Guid id, Func<Task> deliver)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, EventArgs args)
        {
            if (store.Status.State == StorageState.Saved && store.Current!.Tasks.Any(t => t.Id == id)) received.TrySetResult();
        }
        store.Changed += Changed;
        try
        {
            await deliver();
            // Changed is raised after publication completes; observing Current alone can race the write.
            await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { store.Changed -= Changed; }
    }

    private sealed class CountingWriter(string path) : IAtomicFileWriter
    {
        private int publications;
        public int Publications => Volatile.Read(ref publications);
        public async Task WriteAsync(string target, ReadOnlyMemory<byte> bytes, bool replace, CancellationToken ct)
        {
            await new AtomicFileWriter().WriteAsync(target, bytes, replace, ct);
            if (target == path) Interlocked.Increment(ref publications);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
