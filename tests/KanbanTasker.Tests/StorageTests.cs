using KanbanTasker.Core;
using System.Text;

namespace KanbanTasker.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "KanbanTasker.Tests", Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(root, "sync", "test.kanban.json");
    public StorageTests() => Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
    private WorkspaceStore Store(string device = "a", IAtomicFileWriter? writer = null) =>
        new(Path.Combine(root, "recovery-" + device), device == "a" ? new Guid(1, 0, 0, new byte[8]) : new Guid(2, 0, 0, new byte[8]), writer,
            pollInterval: TimeSpan.FromHours(1));
    private async Task SeedAsync() => await File.WriteAllBytesAsync(FilePath, WorkspaceJson.Serialize(MergeTests.Example().Document));

    [Fact]
    public async Task CreateSaveReopenAndMovePersist()
    {
        await using (var first = Store())
        {
            await first.CreateAsync(FilePath);
            await first.CommitAsync(e => e.CreateBoard("Personal"));
            var d = first.Current!; var board = d.Boards.Single().Id; var column = WorkspaceView.Columns(d, board).First().Id;
            await first.CommitAsync(e => { e.SaveTask(new() { BoardId = board, ColumnId = column, Title = "Task" }); e.MoveColumn(column, 4); });
        }
        await using var second = Store(); await second.OpenAsync(FilePath);
        Assert.Single(WorkspaceView.AllTasks(second.Current!));
        Assert.Equal("Backlog", WorkspaceView.Columns(second.Current!, second.Current!.Boards.Single().Id).Last().Get<string>(Fields.Name));
    }
    [Fact]
    public async Task OpeningAnotherDocumentDoesNotMergeWorkspaces()
    {
        await SeedAsync(); await using var store = Store(); await store.OpenAsync(FilePath);
        var old = store.Current!.DocumentId;
        var secondPath = Path.Combine(root, "sync", "other.json");
        await store.CreateAsync(secondPath);
        Assert.NotEqual(old, store.Current!.DocumentId); Assert.Empty(store.Current.Boards);
        await store.OpenAsync(FilePath); Assert.Equal(old, store.Current!.DocumentId); Assert.Single(store.Current.Boards);
    }
    [Fact]
    public async Task ConflictCopiesAndDelayedReplacementsConvergeAcrossDevices()
    {
        await SeedAsync(); var initial = await File.ReadAllBytesAsync(FilePath);
        byte[] fromA;
        await using (var offlineA = Store("a"))
        {
            await offlineA.OpenAsync(FilePath);
            await offlineA.CommitAsync(e => e.CreateBoard("From A"));
            fromA = await File.ReadAllBytesAsync(FilePath);
        }
        await File.WriteAllBytesAsync(FilePath, initial); // Another device has not received A yet.
        await using var b = Store("b"); await b.OpenAsync(FilePath);
        await b.CommitAsync(e => e.CreateBoard("From B"));
        await File.WriteAllBytesAsync(Path.Combine(root, "sync", "test (conflicted copy 2026-09-18).kanban.json"), fromA);
        await b.RefreshAsync();
        Assert.Equal(3, WorkspaceView.Boards(b.Current!).Count()); // A is closed; only the conflict copy can supply its edit.
        await using var a = Store("a"); await a.OpenAsync(FilePath); await b.RefreshAsync();
        Assert.True(WorkspaceJson.Equal(a.Current!, b.Current!));
        Assert.Equal(3, WorkspaceView.Boards(a.Current!).Count());
        Assert.True(File.Exists(Path.Combine(root, "sync", "test (conflicted copy 2026-09-18).kanban.json")));
    }
    [Fact]
    public async Task RecoveryKeepsChangesAfterFileReplacementAndRestart()
    {
        await SeedAsync(); var stale = await File.ReadAllBytesAsync(FilePath);
        await using (var store = Store())
        {
            await store.OpenAsync(FilePath); await store.CommitAsync(e => e.CreateBoard("Recover me"));
        }
        await File.WriteAllBytesAsync(FilePath, stale);
        await using var restarted = Store(); await restarted.OpenAsync(FilePath);
        Assert.Equal(2, restarted.Current!.Boards.Count);
        Assert.Equal(2, WorkspaceJson.Parse(await File.ReadAllBytesAsync(FilePath)).Boards.Count);
    }
    [Fact]
    public async Task InterruptedPublicationRetainsDurableActionWithoutDuplicateRetry()
    {
        await SeedAsync(); var fault = new FaultWriter(FilePath);
        await using (var store = Store(writer: fault))
        {
            await store.OpenAsync(FilePath); fault.Fail = true;
            await store.CommitAsync(e => e.CreateBoard("Pending"));
            Assert.Equal(StorageState.Error, store.Status.State);
            Assert.Equal(2, store.Current!.Boards.Count);
            Assert.Single(WorkspaceJson.Parse(await File.ReadAllBytesAsync(FilePath)).Boards);
        }
        await using var restarted = Store(); await restarted.OpenAsync(FilePath);
        Assert.Equal(2, restarted.Current!.Boards.Count);
    }
    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"schemaVersion\":99,\"documentId\":\"11111111-1111-1111-1111-111111111111\",\"boards\":[],\"columns\":[],\"tasks\":[]}")]
    public async Task InvalidExternalFileIsNotOverwritten(string invalid)
    {
        await SeedAsync(); await using var store = Store(); await store.OpenAsync(FilePath);
        var before = WorkspaceJson.Serialize(store.Current!);
        await File.WriteAllTextAsync(FilePath, invalid); await store.RefreshAsync();
        Assert.Equal(StorageState.Error, store.Status.State);
        Assert.Equal(invalid, await File.ReadAllTextAsync(FilePath));
        Assert.Equal(before, WorkspaceJson.Serialize(store.Current!));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.CommitAsync(e => e.CreateBoard("Never written")));
    }
    [Fact]
    public async Task MissingFileIsNotRecreatedAndRecoveryIsNotDiscarded()
    {
        await SeedAsync(); await using var store = Store(); await store.OpenAsync(FilePath);
        File.Delete(FilePath); await store.RefreshAsync();
        Assert.False(File.Exists(FilePath)); Assert.Equal(StorageState.Error, store.Status.State);
        Assert.Single(store.Current!.Boards);
    }
    [Fact]
    public async Task LockedFileKeepsLastValidStateAndRecoversWhenUnlocked()
    {
        await SeedAsync(); await using var store = Store(); await store.OpenAsync(FilePath);
        using (var handle = new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await store.RefreshAsync(); Assert.Equal(StorageState.Error, store.Status.State); Assert.Single(store.Current!.Boards);
        }
        await store.RefreshAsync(); Assert.Equal(StorageState.Saved, store.Status.State);
    }
    [Fact]
    public async Task ReadOnlyFileDoesNotLosePendingChanges()
    {
        await SeedAsync(); await using var store = Store(); await store.OpenAsync(FilePath);
        File.SetAttributes(FilePath, FileAttributes.ReadOnly);
        try
        {
            await store.CommitAsync(e => e.CreateBoard("Pending"));
            Assert.Equal(StorageState.Error, store.Status.State);
            Assert.Equal(2, store.Current!.Boards.Count);
        }
        finally { File.SetAttributes(FilePath, FileAttributes.Normal); }
        await store.RefreshAsync(); Assert.Equal(StorageState.Saved, store.Status.State);
    }
    [Fact]
    public async Task UnchangedRefreshDoesNotRewriteTheDataFile()
    {
        await SeedAsync(); await using var store = Store(); await store.OpenAsync(FilePath);
        var timestamp = File.GetLastWriteTimeUtc(FilePath);
        for (var i = 0; i < 5; i++) await store.RefreshAsync();
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(FilePath));
    }
    [Fact]
    public async Task UnrelatedAndMalformedSiblingJsonIsIgnored()
    {
        await SeedAsync();
        await File.WriteAllTextAsync(Path.Combine(root, "sync", "unrelated.json"), "{broken");
        await File.WriteAllBytesAsync(Path.Combine(root, "sync", "other.json"), WorkspaceJson.Serialize(new()));
        await using var store = Store(); await store.OpenAsync(FilePath);
        Assert.Single(store.Current!.Boards); Assert.Equal(StorageState.Saved, store.Status.State);
    }
    [Fact]
    public async Task ReplacementDuringCommitIsMergedBeforePublication()
    {
        await SeedAsync();
        var replacement = WorkspaceJson.Parse(await File.ReadAllBytesAsync(FilePath));
        var clock = new ChangeClock(Guid.NewGuid()); clock.Observe(replacement);
        new WorkspaceEditor(replacement, clock).CreateBoard("Arrived during save");
        var writer = new ReplacingWriter(FilePath, WorkspaceJson.Serialize(replacement));
        await using var store = Store(writer: writer); await store.OpenAsync(FilePath); writer.Armed = true;
        await store.CommitAsync(e => e.CreateBoard("Local edit"));
        await store.RefreshAsync();
        Assert.Equal(3, store.Current!.Boards.Count);
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task RestartWithMissingOrBrokenFileShowsLastGoodRecovery(bool missing)
    {
        await SeedAsync();
        await using (var first = Store()) { await first.OpenAsync(FilePath); }
        if (missing) File.Delete(FilePath); else await File.WriteAllTextAsync(FilePath, "{broken");
        await using var restarted = Store();
        await Assert.ThrowsAnyAsync<Exception>(() => restarted.OpenAsync(FilePath));
        Assert.Single(restarted.Current!.Boards); Assert.Equal(StorageState.Error, restarted.Status.State);
        if (missing) Assert.False(File.Exists(FilePath)); else Assert.Equal("{broken", await File.ReadAllTextAsync(FilePath));
    }
    [Fact]
    public async Task WriterFailureBeforeAtomicReplaceKeepsOriginalBytes()
    {
        await SeedAsync(); var original = await File.ReadAllBytesAsync(FilePath);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AtomicFileWriter()
            .WriteAsync(FilePath, Encoding.UTF8.GetBytes("new data"), true, cancellation.Token));
        Assert.Equal(original, await File.ReadAllBytesAsync(FilePath));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(FilePath)!, "*.tmp"));
    }
    [Theory]
    [InlineData(false, 0x20)] [InlineData(false, 0x21)] [InlineData(false, 0x497)]
    [InlineData(true, 0x20)] [InlineData(true, 0x497)] [InlineData(true, 5)]
    public async Task BriefWriteLockIsRetriedWithoutReplayingTheActionOrReportingAnError(bool recovery, int code)
    {
        await SeedAsync();
        var writer = new TransientWriter(FilePath, recovery, code);
        await using var store = Store(writer: writer); await store.OpenAsync(FilePath);
        var statuses = new System.Collections.Concurrent.ConcurrentQueue<StorageState>();
        store.Changed += (_, _) => statuses.Enqueue(store.Status.State);
        writer.Failures = 2;
        var edits = 0;
        await store.CommitAsync(editor => { edits++; editor.CreateBoard("Saved after a short lock"); });
        Assert.Equal(1, edits);
        Assert.Equal(2, writer.FailedAttempts);
        Assert.Equal(StorageState.Saved, store.Status.State);
        Assert.DoesNotContain(StorageState.Error, statuses);
        Assert.Equal(2, WorkspaceJson.Parse(await File.ReadAllBytesAsync(FilePath)).Boards.Count);
        Assert.True(WorkspaceJson.Equal(store.Current!, WorkspaceJson.Parse(await File.ReadAllBytesAsync(FilePath))));
    }
    [Fact]
    public async Task PublicationRetryRereadsChangesThatArriveDuringTheWait()
    {
        await SeedAsync();
        var incoming = WorkspaceJson.Parse(await File.ReadAllBytesAsync(FilePath));
        var clock = new ChangeClock(Guid.NewGuid()); clock.Observe(incoming);
        new WorkspaceEditor(incoming, clock).CreateBoard("Arrived during the retry");
        var writer = new TransientWriter(FilePath, false, 0x497);
        await using var store = Store(writer: writer); await store.OpenAsync(FilePath);
        writer.Failures = 1;
        writer.BeforeFailure = () => File.WriteAllBytesAsync(FilePath, WorkspaceJson.Serialize(incoming));
        await store.CommitAsync(editor => editor.CreateBoard("Local action"));
        var saved = WorkspaceJson.Parse(await File.ReadAllBytesAsync(FilePath));
        Assert.Equal(3, saved.Boards.Count);
        Assert.Equal(StorageState.Saved, store.Status.State);
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task InvalidOrMissingFileArrivingDuringRetryIsNeverOverwritten(bool missing)
    {
        await SeedAsync();
        var writer = new TransientWriter(FilePath, false, 0x497);
        await using var store = Store(writer: writer); await store.OpenAsync(FilePath);
        writer.Failures = 1;
        writer.BeforeFailure = async () =>
        {
            if (missing) File.Delete(FilePath); else await File.WriteAllTextAsync(FilePath, "{broken");
        };
        await store.CommitAsync(editor => editor.CreateBoard("Durable local action"));
        Assert.Equal(StorageState.Error, store.Status.State);
        Assert.Equal(2, store.Current!.Boards.Count);
        if (missing) Assert.False(File.Exists(FilePath)); else Assert.Equal("{broken", await File.ReadAllTextAsync(FilePath));
    }
    [Fact]
    public async Task PersistentRecoveryLockFailsAfterBoundedRetriesWithoutAcceptingTheEdit()
    {
        await SeedAsync(); var before = await File.ReadAllBytesAsync(FilePath);
        var writer = new TransientWriter(FilePath, true, 0x497);
        await using var store = Store(writer: writer); await store.OpenAsync(FilePath);
        writer.Failures = 100;
        await Assert.ThrowsAsync<IOException>(() => store.CommitAsync(editor => editor.CreateBoard("Not accepted")));
        Assert.Equal(4, writer.FailedAttempts);
        Assert.Single(store.Current!.Boards);
        Assert.Equal(before, await File.ReadAllBytesAsync(FilePath));
        Assert.StartsWith("Not saved to the data file:", store.Status.Message);
    }
    [Fact]
    public async Task RetryCanBeCancelledBeforeAcceptingTheEdit()
    {
        await SeedAsync();
        var writer = new TransientWriter(FilePath, true, 0x497);
        await using var store = Store(writer: writer); await store.OpenAsync(FilePath);
        using var cancellation = new CancellationTokenSource();
        writer.Failures = 100;
        writer.BeforeFailure = () => { cancellation.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CommitAsync(editor => editor.CreateBoard("Cancelled"), cancellation.Token));
        Assert.Equal(1, writer.FailedAttempts);
        Assert.Single(store.Current!.Boards);
        Assert.Single(WorkspaceJson.Parse(await File.ReadAllBytesAsync(FilePath)).Boards);
    }
    [Fact]
    public async Task PermanentDiskErrorIsNotRetried()
    {
        await SeedAsync();
        var writer = new TransientWriter(FilePath, true, 0x70);
        await using var store = Store(writer: writer); await store.OpenAsync(FilePath);
        writer.Failures = 100;
        await Assert.ThrowsAsync<IOException>(() => store.CommitAsync(editor => editor.CreateBoard("Disk full")));
        Assert.Equal(1, writer.FailedAttempts);
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task RealWindowsReaderWithoutDeleteSharingOnlyDelaysTheSave(bool recovery)
    {
        if (!OperatingSystem.IsWindows()) return;
        await SeedAsync();
        var writer = new UnlockAfterFailureWriter();
        await using var store = Store(writer: writer); await store.OpenAsync(FilePath);
        var path = recovery ? Path.Combine(root, "recovery-a", $"{store.Current!.DocumentId:N}.recovery.json") : FilePath;
        using var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        writer.Unlock = handle.Dispose;
        await store.CommitAsync(editor => editor.CreateBoard("Real Windows lock"));
        Assert.NotNull(writer.Error);
        Assert.True(writer.Error.HResult is unchecked((int)0x80070497) or unchecked((int)0x80070020), writer.Error.ToString());
        Assert.Equal(StorageState.Saved, store.Status.State);
        Assert.Equal(2, WorkspaceJson.Parse(await File.ReadAllBytesAsync(FilePath)).Boards.Count);
        Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
    }
    private sealed class UnlockAfterFailureWriter : IAtomicFileWriter
    {
        public Action? Unlock;
        public IOException? Error;
        public async Task WriteAsync(string path, ReadOnlyMemory<byte> bytes, bool replace, CancellationToken ct)
        {
            try { await new AtomicFileWriter().WriteAsync(path, bytes, replace, ct); }
            catch (IOException ex)
            {
                Error = ex; Unlock?.Invoke(); Unlock = null;
                throw;
            }
        }
    }
    private sealed class TransientWriter(string target, bool recovery, int code) : IAtomicFileWriter
    {
        public int Failures, FailedAttempts;
        public Func<Task>? BeforeFailure;
        public async Task WriteAsync(string path, ReadOnlyMemory<byte> bytes, bool replace, CancellationToken ct)
        {
            if (Failures > 0 && (recovery ? path.EndsWith(".recovery.json", StringComparison.Ordinal) : path == target))
            {
                Failures--; FailedAttempts++;
                if (BeforeFailure is not null) await BeforeFailure();
                throw new IOException("Simulated Windows file error.", unchecked((int)0x80070000) | code);
            }
            await new AtomicFileWriter().WriteAsync(path, bytes, replace, ct);
        }
    }
    private sealed class FaultWriter(string target) : IAtomicFileWriter
    {
        public bool Fail;
        public Task WriteAsync(string path, ReadOnlyMemory<byte> bytes, bool replace, CancellationToken ct) => Fail && path == target
            ? throw new IOException("Simulated interruption before replace.") : new AtomicFileWriter().WriteAsync(path, bytes, replace, ct);
    }
    private sealed class ReplacingWriter(string target, byte[] replacement) : IAtomicFileWriter
    {
        public bool Armed;
        public async Task WriteAsync(string path, ReadOnlyMemory<byte> bytes, bool replace, CancellationToken ct)
        {
            await new AtomicFileWriter().WriteAsync(path, bytes, replace, ct);
            if (Armed && path != target)
            {
                Armed = false; await File.WriteAllBytesAsync(target, replacement, ct);
            }
        }
    }
    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
