using System.Text.Json;
using Agentivity.Infrastructure.Core;
using Agentivity.Infrastructure.Core.Json;

namespace Agentivity.Infrastructure.Repository;

/// <summary>
/// Disk-backed <see cref="AbstractStore{TRecord}"/> whose records all live together in ONE JSON
/// file (a list) instead of one file per record (contrast <see cref="AbstractDiskStore{TRecord}"/>)
/// — for stores that never hold more than a handful of records, where physically splitting them per
/// id buys nothing (e.g. a small credentials/config collection).
/// <para>
/// Thread-safety: a single <see cref="SemaphoreSlim"/> serializes every operation across the whole
/// file (there's only one file, so no per-id concurrency is possible or useful).
/// <see cref="UpdateAsync"/> and <see cref="RunExclusiveAsync{TResult}"/> both span the entire
/// read-modify-write under that one lock — atomic, unlike <see cref="AbstractStore{TRecord}"/>'s
/// naive default.
/// </para>
/// </summary>
public abstract class AbstractSingleFileListStore<TRecord> : AbstractStore<TRecord>
{
    private readonly string _filePath;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <param name="filePath">Relative or absolute file path, resolved via <see cref="PathResolver"/>.</param>
    /// <param name="idSelector">Extracts the id from a record instance.</param>
    /// <param name="jsonOptions">Serializer options for the whole-file list. Defaults to <see cref="JsonDefaults.Default"/>.</param>
    protected AbstractSingleFileListStore(string filePath, Func<TRecord, string> idSelector, JsonSerializerOptions? jsonOptions = null)
        : base(idSelector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = PathResolver.Resolve(filePath);
        _jsonOptions = jsonOptions ?? JsonDefaults.Default;
    }

    /// <summary>Resolved absolute file path.</summary>
    protected string FilePath => _filePath;

    public override async ValueTask AddAsync(TRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var id = IdSelector(record);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = await LoadAllAsync(cancellationToken).ConfigureAwait(false);
            var index = all.FindIndex(r => string.Equals(IdSelector(r), id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
                all[index] = record;
            else
                all.Add(record);
            await SaveAllAsync(all, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public override async ValueTask<TRecord?> FindAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = await LoadAllAsync(cancellationToken).ConfigureAwait(false);
            return all.FirstOrDefault(r => string.Equals(IdSelector(r), id, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _lock.Release();
        }
    }

    public override async ValueTask DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = await LoadAllAsync(cancellationToken).ConfigureAwait(false);
            if (all.RemoveAll(r => string.Equals(IdSelector(r), id, StringComparison.OrdinalIgnoreCase)) > 0)
                await SaveAllAsync(all, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public override async ValueTask<IReadOnlyList<TRecord>> FindAllAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadAllAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    // Atomic override: single lock spans the whole read-modify-write, unlike the naive
    // Find-then-Add default in AbstractStore.
    public override async ValueTask UpdateAsync(string id, Func<TRecord, TRecord> mutator, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(mutator);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = await LoadAllAsync(cancellationToken).ConfigureAwait(false);
            var index = all.FindIndex(r => string.Equals(IdSelector(r), id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return;
            all[index] = mutator(all[index]);
            await SaveAllAsync(all, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Runs <paramref name="transaction"/> with exclusive access to the full record list, persisting
    /// whatever list it returns. For subclasses whose business operations need more than a single
    /// record's read-modify-write under the same atomic section as the rest of the store's
    /// operations (e.g. id generation or per-field transforms informed by the record being
    /// replaced) — <see cref="UpdateAsync"/> alone isn't expressive enough for those.
    /// </summary>
    protected async ValueTask<TResult> RunExclusiveAsync<TResult>(Func<List<TRecord>, (List<TRecord> Records, TResult Result)> transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = await LoadAllAsync(cancellationToken).ConfigureAwait(false);
            var (records, result) = transaction(all);
            await SaveAllAsync(records, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<TRecord>> LoadAllAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
            return [];
        var json = await File.ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<List<TRecord>>(json, _jsonOptions) ?? [];
    }

    private async Task SaveAllAsync(List<TRecord> all, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(all, _jsonOptions);
        await File.WriteAllTextAsync(_filePath, json, cancellationToken).ConfigureAwait(false);
    }
}
