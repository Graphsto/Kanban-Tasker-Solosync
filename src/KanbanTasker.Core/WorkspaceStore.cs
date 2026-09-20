using System.Security.Cryptography;
using System.Text.Json;

namespace KanbanTasker.Core;

public enum StorageState { Closed, Saved, Pending, Error }
public sealed record WorkspaceStatus(StorageState State, string Message);
public interface IWorkspaceStore : IAsyncDisposable
{
    string? FilePath { get; }
    WorkspaceDocument? Current { get; }
    WorkspaceStatus Status { get; }
    event EventHandler? Changed;
    Task CreateAsync(string path, CancellationToken cancellationToken = default);
    Task OpenAsync(string path, CancellationToken cancellationToken = default);
    Task CommitAsync(Action<WorkspaceEditor> edit, CancellationToken cancellationToken = default);
    Task RefreshAsync(CancellationToken cancellationToken = default);
}

public interface IAtomicFileWriter
{
    Task WriteAsync(string path, ReadOnlyMemory<byte> bytes, bool replace, CancellationToken cancellationToken);
}

public sealed class AtomicFileWriter : IAtomicFileWriter
{
    public async Task WriteAsync(string path, ReadOnlyMemory<byte> bytes, bool replace, CancellationToken cancellationToken)
    {
        var temp = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Replace never creates a missing destination. A deleted data file must not be resurrected.
            if (replace) File.Replace(temp, path, null);
            else File.Move(temp, path, overwrite: false);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException) { /* A stale, uniquely named .tmp is ignored by workspace discovery. */ }
        }
    }
}

/// <summary>
/// Keeps a durable monotonic recovery copy before publishing. A sync client's replacement or a lost
/// read/write race is reconciled on the next refresh, including after restarting the process.
/// </summary>
public sealed class WorkspaceStore : IWorkspaceStore
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ChangeClock clock;
    private readonly string recoveryDirectory;
    private readonly IAtomicFileWriter writer;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task monitor;
    private readonly Dictionary<string, (string Hash, WorkspaceDocument? Document)> siblingCache = new(StringComparer.OrdinalIgnoreCase);
    private WorkspaceDocument? current;
    private FileSystemWatcher? watcher;
    private volatile bool fileChanged;
    private bool disposed;
    public string? FilePath { get; private set; }
    public WorkspaceDocument? Current => current?.Clone();
    public WorkspaceStatus Status { get; private set; } = new(StorageState.Closed, "No data file open.");
    public event EventHandler? Changed;

    public WorkspaceStore(string recoveryDirectory, Guid deviceId, IAtomicFileWriter? writer = null,
        Func<DateTimeOffset>? now = null, TimeSpan? pollInterval = null)
    {
        this.recoveryDirectory = Path.GetFullPath(recoveryDirectory);
        Directory.CreateDirectory(this.recoveryDirectory);
        this.writer = writer ?? new AtomicFileWriter();
        clock = new(deviceId, now);
        monitor = MonitorAsync(pollInterval ?? TimeSpan.FromSeconds(3));
    }
    public async Task CreateAsync(string path, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            path = CheckPath(path);
            var exists = File.Exists(path);
            if (exists && new FileInfo(path).Length != 0)
                throw new IOException("This file already contains data. Choose Open or use a new filename.");
            var document = new WorkspaceDocument();
            await writer.WriteAsync(path, WorkspaceJson.Serialize(document), exists, cancellationToken);
            await SaveRecoveryAsync(document, cancellationToken);
            await RememberPathAsync(path, document.DocumentId, cancellationToken);
            SetWorkspace(path, document);
            SetStatus(StorageState.Saved, "Saved locally.");
        }
        catch (Exception ex) when (IsStorageError(ex)) { SetStatus(StorageState.Error, ex.Message); throw; }
        finally { gate.Release(); Changed?.Invoke(this, EventArgs.Empty); }
    }
    public async Task OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            path = CheckPath(path);
            var document = WorkspaceJson.Parse(await ReadAsync(path, cancellationToken));
            var recoveryPath = RecoveryPath(document.DocumentId);
            if (File.Exists(recoveryPath))
                document = WorkspaceMerge.Merge(document, WorkspaceJson.Parse(await ReadAsync(recoveryPath, cancellationToken)));
            // Do not change the active workspace until both the file and its recovery state are valid.
            await RememberPathAsync(path, document.DocumentId, cancellationToken);
            SetWorkspace(path, document);
            await ReconcileAsync(cancellationToken);
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            if (current is null) await LoadLastGoodAsync(path, cancellationToken);
            SetStatus(StorageState.Error, current is null ? ex.Message : $"File unavailable; showing the last valid local recovery. {ex.Message}");
            throw;
        }
        finally { gate.Release(); Changed?.Invoke(this, EventArgs.Empty); }
    }
    public async Task CommitAsync(Action<WorkspaceEditor> edit, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        bool durable = false;
        try
        {
            if (current is null) throw new InvalidOperationException("Open a data file first.");
            // A bad/missing external file blocks publication, without silently replacing it with local state.
            await ReconcileAsync(cancellationToken);
            var next = current.Clone();
            clock.Observe(next);
            edit(new WorkspaceEditor(next, clock));
            WorkspaceJson.Validate(next);
            if (!WorkspaceJson.Equal(next, current))
            {
                // Retry this prepared snapshot, never the editor callback: creating
                // an entry twice would give it a second ID after a transient failure.
                await RetryFileAccessAsync(() => SaveRecoveryAsync(next, cancellationToken), cancellationToken);
                durable = true;
                current = next;
                SetStatus(StorageState.Pending, "Saved in local recovery; writing the data file…");
                await ReconcileAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            SetStatus(StorageState.Error, durable
                ? $"Saved in local recovery; the data file could not be updated. Retrying automatically. {ex.Message}"
                : $"Not saved to the data file: {ex.Message}");
            // Once durable, the action was accepted. Retrying a creation must not create a duplicate.
            if (!durable) throw;
        }
        finally { gate.Release(); Changed?.Invoke(this, EventArgs.Empty); }
    }
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (disposed) return;
        await gate.WaitAsync(cancellationToken);
        bool changed = false;
        try
        {
            if (current is null) return;
            var previous = WorkspaceJson.Serialize(current); var previousStatus = Status;
            await ReconcileAsync(cancellationToken);
            changed = previousStatus != Status || !previous.AsSpan().SequenceEqual(WorkspaceJson.Serialize(current));
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            var next = new WorkspaceStatus(StorageState.Error, $"File unavailable; last valid data kept. {ex.Message}");
            changed = Status != next; Status = next;
        }
        finally { gate.Release(); if (changed) Changed?.Invoke(this, EventArgs.Empty); }
    }
    private Task ReconcileAsync(CancellationToken ct) => RetryFileAccessAsync(() => ReconcileAttemptAsync(ct), ct);
    private async Task ReconcileAttemptAsync(CancellationToken ct)
    {
        if (current is null || FilePath is null) return;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var bytes = await ReadAsync(FilePath, ct);
            var disk = WorkspaceJson.Parse(bytes);
            var merged = WorkspaceMerge.Merge(current, disk);
            merged = await MergeSiblingsAsync(merged, ct);
            WorkspaceJson.Validate(merged);
            await SaveRecoveryAsync(merged, ct);
            current = merged; clock.Observe(merged);
            if (!WorkspaceJson.Equal(disk, merged))
            {
                // Catch replacements during parsing/merging; races after this check are recovered on refresh.
                var latest = await ReadAsync(FilePath, ct);
                if (!bytes.AsSpan().SequenceEqual(latest)) continue;
                await writer.WriteAsync(FilePath, WorkspaceJson.Serialize(merged), replace: true, ct);
            }
            SetStatus(StorageState.Saved, "Saved locally.");
            return;
        }
        throw new IOException("The file is changing frequently. Changes are kept in local recovery; retrying automatically.");
    }
    private static async Task RetryFileAccessAsync(Func<Task> action, CancellationToken ct)
    {
        // Windows readers, scanners and sync clients can briefly prevent replacement.
        // Retry the whole reconciliation so incoming data is read and merged again,
        // rather than publishing bytes prepared before the wait. Permanent errors
        // still surface; never delete the destination or fall back to truncating it.
        int[] delays = [100, 250, 500];
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { await action(); return; }
            catch (Exception ex) when (attempt < delays.Length && IsTransientFileAccessError(ex))
            { await Task.Delay(delays[attempt], ct); }
        }
    }
    private static bool IsTransientFileAccessError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException && ex.HResult is
            unchecked((int)0x80070005) or // Access denied (also used for files pending deletion).
            unchecked((int)0x80070020) or // Sharing violation.
            unchecked((int)0x80070021) or // Lock violation.
            unchecked((int)0x80070497);   // Unable to remove the file being replaced.
    private async Task<WorkspaceDocument> MergeSiblingsAsync(WorkspaceDocument document, CancellationToken ct)
    {
        foreach (var path in Directory.EnumerateFiles(Path.GetDirectoryName(FilePath!)!, "*.json", SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(path, FilePath, StringComparison.OrdinalIgnoreCase)) continue;
            byte[] bytes;
            try { bytes = await ReadAsync(path, ct); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { continue; }
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            if (!siblingCache.TryGetValue(path, out var cached) || cached.Hash != hash)
            {
                WorkspaceDocument? sibling = null;
                try
                {
                    using var json = JsonDocument.Parse(bytes);
                    if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("documentId", out var id)
                        && id.ValueKind == JsonValueKind.String && id.TryGetGuid(out var guid) && guid == document.DocumentId)
                        sibling = WorkspaceJson.Parse(bytes);
                }
                catch (JsonException) { /* An unrelated JSON document or an incomplete provider copy is ignored until its next change. */ }
                cached = (hash, sibling); siblingCache[path] = cached;
            }
            if (cached.Document is not null) document = WorkspaceMerge.Merge(document, cached.Document);
        }
        return document;
    }
    private async Task SaveRecoveryAsync(WorkspaceDocument document, CancellationToken ct)
    {
        var path = RecoveryPath(document.DocumentId); var bytes = WorkspaceJson.Serialize(document);
        if (File.Exists(path))
        {
            var existing = await ReadAsync(path, ct);
            if (bytes.AsSpan().SequenceEqual(existing)) return;
        }
        await writer.WriteAsync(path, bytes, File.Exists(path), ct);
    }
    private string RecoveryPath(Guid id) => Path.Combine(recoveryDirectory, $"{id:N}.recovery.json");
    private string PathIndex(string path)
    {
        var normalized = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows()) normalized = normalized.ToUpperInvariant();
        return Path.Combine(recoveryDirectory, "path-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized))) + ".link");
    }
    private async Task RememberPathAsync(string path, Guid id, CancellationToken ct)
    {
        var index = PathIndex(path);
        var bytes = System.Text.Encoding.UTF8.GetBytes(id.ToString());
        if (File.Exists(index) && await File.ReadAllTextAsync(index, ct) == id.ToString()) return;
        await writer.WriteAsync(index, bytes, File.Exists(index), ct);
    }
    private async Task LoadLastGoodAsync(string path, CancellationToken ct)
    {
        try
        {
            var index = PathIndex(path);
            if (!File.Exists(index) || !Guid.TryParse(await File.ReadAllTextAsync(index, ct), out var id)) return;
            var recovered = WorkspaceJson.Parse(await ReadAsync(RecoveryPath(id), ct));
            if (recovered.DocumentId == id) SetWorkspace(Path.GetFullPath(path), recovered);
        }
        catch (Exception ex) when (IsStorageError(ex)) { /* Keep the original error; never replace a damaged recovery. */ }
    }
    private string CheckPath(string path)
    {
        path = Path.GetFullPath(path);
        if (path.StartsWith(recoveryDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose a data file outside the application's private recovery directory.");
        return path;
    }
    private void SetWorkspace(string path, WorkspaceDocument document)
    {
        watcher?.Dispose(); siblingCache.Clear();
        FilePath = path; current = document; clock.Observe(document);
        try
        {
            watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false
            };
            watcher.Changed += (_, _) => fileChanged = true;
            watcher.Created += (_, _) => fileChanged = true;
            watcher.Deleted += (_, _) => fileChanged = true;
            watcher.Renamed += (_, _) => fileChanged = true;
            watcher.Error += (_, _) => fileChanged = true;
            watcher.EnableRaisingEvents = true;
        }
        catch (IOException) { watcher?.Dispose(); watcher = null; } // Periodic reads remain available.
    }
    private async Task MonitorAsync(TimeSpan interval)
    {
        var elapsed = TimeSpan.Zero;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(400));
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                elapsed += TimeSpan.FromMilliseconds(400);
                if (fileChanged || elapsed >= interval)
                {
                    fileChanged = false; elapsed = TimeSpan.Zero;
                    await RefreshAsync(lifetime.Token);
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    private static async Task<byte[]> ReadAsync(string path, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous);
                return await WorkspaceJson.ReadAsync(stream, ct);
            }
            catch (IOException) when (attempt < 2) { await Task.Delay(150 * (attempt + 1), ct); }
        }
    }
    private static bool IsStorageError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException;
    private void SetStatus(StorageState state, string message) => Status = new(state, message);
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true; lifetime.Cancel(); watcher?.Dispose();
        await monitor;
        await gate.WaitAsync(); gate.Release();
        lifetime.Dispose(); gate.Dispose();
    }
}
