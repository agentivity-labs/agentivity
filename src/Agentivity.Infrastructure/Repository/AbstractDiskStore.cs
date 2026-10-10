using System.Collections.Concurrent;
using System.Text.Json;
using Agentivity.Infrastructure.Core;
using Agentivity.Infrastructure.Core.Json;

namespace Agentivity.Infrastructure.Repository;

/// <summary>
/// Disk-backed <see cref="AbstractStore{TRecord}"/> — the SOLE place in this codebase that knows
/// about JSON serialization and file locking for a stored record; that's a detail of THIS
/// mechanism, not something <see cref="IRecordConvertible{TRecord}"/> or <see cref="IStore{TRecord}"/>
/// need to know about. Records are serialized directly via <see cref="JsonSerializer"/> — no
/// <see cref="IRecordConvertible{TRecord}"/> constraint here (that contract lives at the
/// entity⟷record boundary, e.g. <see cref="AbstractRepository{TEntity,TRecord}"/>, not at the
/// record⟷bytes-on-disk boundary).
/// <para>
/// Records are stored per id: <c>{basePath}/{id}.json</c> (flat), or
/// <c>{basePath}/{partition}/{id}.json</c> when <c>partitionSelector</c> is supplied — in which
/// case <see cref="AddAsync"/> moves the file between partitions as the record's partition value
/// changes (e.g. a run's lifecycle state).
/// </para>
/// <para>
/// Thread-safety: a per-id <see cref="ReaderWriterLockSlim"/> (held in a <see cref="ConcurrentDictionary{TKey,TValue}"/>,
/// itself thread-safe) serialises state transitions — multiple concurrent reads are allowed on
/// the same id, a write is exclusive; different ids never block each other. File I/O is kept
/// synchronous so that the lock is always entered and released on the same thread —
/// <see cref="ReaderWriterLockSlim"/> requires this.
/// </para>
/// </summary>
public abstract class AbstractDiskStore<TRecord> : AbstractStore<TRecord>
{
    private readonly string _basePath;
    private readonly Func<TRecord, string>? _partitionSelector;
    private static readonly JsonSerializerOptions _json = JsonDefaults.Default;

    // Per-id read/write lock. Thread-affine — works because we keep I/O synchronous.
    private readonly ConcurrentDictionary<string, ReaderWriterLockSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="basePath">Relative or absolute cache path, resolved via <see cref="PathResolver"/>.</param>
    /// <param name="idSelector">Extracts the id from a record instance.</param>
    /// <param name="partitionSelector">
    /// Optional: extracts a subfolder key from a record instance (e.g. lifecycle state). Null →
    /// flat layout, no move-on-save. Non-null → <c>{basePath}/{partition}/{id}.json</c>, and
    /// <see cref="AddAsync"/> removes the file from any other partition before writing the new one.
    /// </param>
    protected AbstractDiskStore(string basePath, Func<TRecord, string> idSelector, Func<TRecord, string>? partitionSelector = null)
        : base(idSelector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        _basePath = PathResolver.Resolve(basePath);
        _partitionSelector = partitionSelector;
        Directory.CreateDirectory(_basePath);
    }

    /// <summary>Resolved absolute base path — exposed so subclasses can pre-create known partition subfolders.</summary>
    protected string BasePath => _basePath;

    public override ValueTask AddAsync(TRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var id = IdSelector(record);

        var lk = GetLock(id);
        lk.EnterWriteLock();
        try
        {
            if (_partitionSelector is not null)
                DeleteExistingFiles(id, exceptPartition: _partitionSelector(record));

            var path = RecordPath(id, record);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(record, _json));
        }
        finally
        {
            lk.ExitWriteLock();
        }

        return ValueTask.CompletedTask;
    }

    public override ValueTask<TRecord?> FindAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var lk = GetLock(id);
        lk.EnterReadLock();
        try
        {
            var path = FindExistingFile(id);
            if (path is null)
                return ValueTask.FromResult<TRecord?>(default);

            var record = JsonSerializer.Deserialize<TRecord>(File.ReadAllText(path), _json);
            return ValueTask.FromResult(record);
        }
        finally
        {
            lk.ExitReadLock();
        }
    }

    public override ValueTask DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var lk = GetLock(id);
        lk.EnterWriteLock();
        try
        {
            var path = FindExistingFile(id);
            if (path is not null)
                File.Delete(path);
        }
        finally
        {
            lk.ExitWriteLock();
        }

        return ValueTask.CompletedTask;
    }

    public override ValueTask<IReadOnlyList<TRecord>> FindAllAsync(CancellationToken cancellationToken = default) =>
        ReadAllUnder(_basePath, recurse: true, cancellationToken);

    /// <summary>Lists only the records currently in the given partition's subfolder — avoids scanning the whole tree.</summary>
    protected ValueTask<IReadOnlyList<TRecord>> ListPartitionAsync(string partition, CancellationToken cancellationToken = default) =>
        ReadAllUnder(Path.Combine(_basePath, partition), recurse: false, cancellationToken);

    // Atomic override: single write lock spans the whole read-modify-write, unlike the naive
    // Find-then-Add default in AbstractStore (which is NOT safe under concurrent callers).
    public override ValueTask UpdateAsync(string id, Func<TRecord, TRecord> mutator, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(mutator);

        var lk = GetLock(id);
        lk.EnterWriteLock();
        try
        {
            var path = FindExistingFile(id);
            if (path is null)
                return ValueTask.CompletedTask;

            var existing = JsonSerializer.Deserialize<TRecord>(File.ReadAllText(path), _json);
            if (existing is null)
                return ValueTask.CompletedTask;

            var updated = mutator(existing);

            if (_partitionSelector is not null)
            {
                var newPartitionPath = RecordPath(id, updated);
                if (!string.Equals(newPartitionPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(path);
                    Directory.CreateDirectory(Path.GetDirectoryName(newPartitionPath)!);
                    File.WriteAllText(newPartitionPath, JsonSerializer.Serialize(updated, _json));
                    return ValueTask.CompletedTask;
                }
            }

            File.WriteAllText(path, JsonSerializer.Serialize(updated, _json));
            return ValueTask.CompletedTask;
        }
        finally
        {
            lk.ExitWriteLock();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private ValueTask<IReadOnlyList<TRecord>> ReadAllUnder(string dir, bool recurse, CancellationToken cancellationToken)
    {
        var results = new List<TRecord>();
        if (!Directory.Exists(dir))
            return ValueTask.FromResult<IReadOnlyList<TRecord>>(results);

        var searchOption = recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (var file in Directory.GetFiles(dir, "*.json", searchOption))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Path.GetFileNameWithoutExtension(file);
            var lk = GetLock(id);
            lk.EnterReadLock();
            try
            {
                var record = JsonSerializer.Deserialize<TRecord>(File.ReadAllText(file), _json);
                if (record is not null)
                    results.Add(record);
            }
            catch { /* skip corrupt files */ }
            finally
            {
                lk.ExitReadLock();
            }
        }

        return ValueTask.FromResult<IReadOnlyList<TRecord>>(results);
    }

    private ReaderWriterLockSlim GetLock(string id) =>
        _locks.GetOrAdd(id, static _ => new ReaderWriterLockSlim());

    private string RecordPath(string id, TRecord record)
    {
        var dir = _partitionSelector is null ? _basePath : Path.Combine(_basePath, _partitionSelector(record));
        return Path.Combine(dir, $"{id}.json");
    }

    private string? FindExistingFile(string id)
    {
        if (_partitionSelector is null)
        {
            var flatPath = Path.Combine(_basePath, $"{id}.json");
            return File.Exists(flatPath) ? flatPath : null;
        }

        // Dynamic scan of known partition subfolders — keeps this base neutral about what a
        // partition actually represents (e.g. RunStatus), unlike a fixed enum.
        if (!Directory.Exists(_basePath))
            return null;

        foreach (var partitionDir in Directory.GetDirectories(_basePath))
        {
            var candidate = Path.Combine(partitionDir, $"{id}.json");
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private void DeleteExistingFiles(string id, string exceptPartition)
    {
        if (!Directory.Exists(_basePath))
            return;

        foreach (var partitionDir in Directory.GetDirectories(_basePath))
        {
            if (string.Equals(Path.GetFileName(partitionDir), exceptPartition, StringComparison.OrdinalIgnoreCase))
                continue;

            var candidate = Path.Combine(partitionDir, $"{id}.json");
            if (File.Exists(candidate))
            {
                try { File.Delete(candidate); }
                catch { /* best-effort */ }
            }
        }
    }
}
