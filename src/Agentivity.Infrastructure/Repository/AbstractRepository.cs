using System.Collections.Concurrent;

namespace Agentivity.Infrastructure.Repository;

/// <summary>
/// Shared skeleton for the in-memory-cache-with-write-through-to-a-store pattern
/// (<c>RunRepository</c>, and any future repository whose live entity can fully round-trip
/// through a durable record). <typeparamref name="TEntity"/> owns the conversion TO its record via
/// <see cref="IRecordConvertible{TRecord}.ToRecord"/>; reconstructing FROM a record is a delegate
/// (<c>fromRecord</c>), not interface dispatch — same reasoning as documented on
/// <see cref="IRecordConvertible{TRecord}"/>.
/// <para>
/// Only a good fit when the live entity carries (or can be given) everything the durable record
/// needs — if it's asymmetric (e.g. a thin correlation object next to a much richer durable
/// record), don't force this base; implement <see cref="IRepository{TEntity,TRecord}"/> directly.
/// </para>
/// <para>
/// Thread-safety: the live cache is a <see cref="ConcurrentDictionary{TKey,TValue}"/> — no
/// corruption is possible under concurrent <see cref="AddAsync"/>/<see cref="FindAsync"/>/
/// <see cref="DeleteAsync"/> calls. <see cref="FindAsync"/> has one intentional, benign race on a
/// cache miss: two concurrent callers can both reconstruct the entity from the store and both
/// write it to the cache — harmless since <c>fromRecord</c> is a pure function (same input, same
/// output), so the redundant reconstruction is wasted work, never corrupted state. Same accepted
/// pattern as the pre-existing <c>RunRepository.GetRun</c> reload fallback.
/// </para>
/// </summary>
public abstract class AbstractRepository<TEntity, TRecord> : IRepository<TEntity, TRecord>
    where TEntity : IRecordConvertible<TRecord>
{
    private readonly ConcurrentDictionary<string, TEntity> _live = new(StringComparer.OrdinalIgnoreCase);
    private readonly IStore<TRecord>? _store;
    private readonly Func<TRecord, TEntity> _fromRecord;
    private readonly Func<TEntity, string> _idSelector;

    /// <param name="idSelector">Extracts the id from a live entity instance.</param>
    /// <param name="fromRecord">Reconstructs a live entity from its durable record (process-restart fallback).</param>
    /// <param name="store">Durable store; null = in-memory only (tests, embedded).</param>
    protected AbstractRepository(Func<TEntity, string> idSelector, Func<TRecord, TEntity> fromRecord, IStore<TRecord>? store = null)
    {
        _idSelector = idSelector ?? throw new ArgumentNullException(nameof(idSelector));
        _fromRecord = fromRecord ?? throw new ArgumentNullException(nameof(fromRecord));
        _store = store;
    }

    public virtual async ValueTask AddAsync(TEntity entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        _live[_idSelector(entity)] = entity;
        await Persist(entity, cancellationToken).ConfigureAwait(false);
    }

    public virtual async ValueTask<TEntity?> FindAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        if (_live.TryGetValue(id, out var live))
            return live;

        if (_store is null)
            return default;

        TRecord? record;
        try
        {
            record = await _store.FindAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort reload — a corrupt/missing durable record means "unknown", not a crash.
            return default;
        }
        if (record is null)
            return default;

        var reconstructed = _fromRecord(record);
        _live[id] = reconstructed;
        return reconstructed;
    }

    public virtual ValueTask<IReadOnlyList<TEntity>> FindAllAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<TEntity>>([.. _live.Values]);

    public virtual async ValueTask<IReadOnlyList<TEntity>> FindWhereAsync(Func<TEntity, bool> predicate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var all = await FindAllAsync(cancellationToken).ConfigureAwait(false);
        return [.. all.Where(predicate)];
    }

    public virtual async ValueTask DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        _live.TryRemove(id, out _);
        if (_store is not null)
            await _store.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Snapshot of every live (cached) entity — for subclasses that need to enumerate (e.g. listing/filtering across all runs).</summary>
    protected ICollection<TEntity> LiveEntities => _live.Values;

    /// <summary>Direct cache lookup, no store fallback — for subclasses with their own richer read path (e.g. wrapping this in a coarser lock alongside other mutable state).</summary>
    protected bool TryGetLive(string id, out TEntity? entity) => _live.TryGetValue(id, out entity);

    /// <summary>Sets the live cache entry directly, no write-through — for subclasses whose own persistence logic decides separately whether/when to persist.</summary>
    protected void SetLive(string id, TEntity entity) => _live[id] = entity;

    /// <summary>Removes an entity from the live cache only, without touching the durable store — for subclasses whose own delete semantics differ (e.g. extra cleanup) from the base <see cref="DeleteAsync"/>.</summary>
    protected bool RemoveLive(string id) => _live.TryRemove(id, out _);

    /// <summary>
    /// Write-through helper for subclasses that mutate a live entity in place (e.g. a state
    /// transition) and need to re-persist it without duplicating the best-effort try/catch.
    /// </summary>
    protected async ValueTask Persist(TEntity entity, CancellationToken cancellationToken = default)
    {
        if (_store is null)
            return;
        try
        {
            await _store.AddAsync(entity.ToRecord(), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort persistence — do not disrupt the caller.
        }
    }

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _updateLocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Atomic read-modify-write for a single entity: find it, let <paramref name="mutate"/> change
    /// it in place, then write it back — the whole sequence held under one lock keyed by
    /// <paramref name="id"/>. For subclasses (or their callers) that independently update DIFFERENT
    /// parts of the same entity from different call sites (e.g. one part updated after every graph
    /// step, another only at the end of a run) — without this, two such updates each doing their
    /// own unlocked find-then-add can race: the second one's "find" reads a snapshot from BEFORE
    /// the first one's "add" lands, so its write silently overwrites the first one's change with
    /// stale data, even though the writes themselves happen one after another in time. Locking only
    /// the write step does not fix this — the read has to be inside the same lock as the write, so
    /// the second caller's mutation is always based on the first caller's latest result. Confirmed
    /// in production: exactly this race between a team run's graph-progress checkpoint (saved after
    /// every step) and its task-outputs summary (saved once at the end) intermittently discarded
    /// one or the other, silently.
    /// </summary>
    protected async ValueTask<TEntity> UpdateAsync(string id, Action<TEntity> mutate, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(mutate);

        var gate = _updateLocks.GetOrAdd(id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entity = await FindAsync(id, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Entity '{id}' not found.");
            mutate(entity);
            await AddAsync(entity, cancellationToken).ConfigureAwait(false);
            return entity;
        }
        finally
        {
            gate.Release();
        }
    }
}
