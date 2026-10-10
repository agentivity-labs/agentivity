namespace Agentivity.Infrastructure.Repository;

/// <summary>
/// Shared skeleton for <see cref="IStore{TRecord}"/> implementations: owns the id-selector and
/// provides naive default implementations of <see cref="FindWhereAsync"/> (scan + filter) and
/// <see cref="UpdateAsync"/> (Find then Add — NOT atomic). A store with a real locking mechanism
/// (e.g. <see cref="AbstractDiskStore{TRecord}"/>) overrides <see cref="UpdateAsync"/> with an
/// atomic read-modify-write. No notion of JSON, disk, or any other physical storage mechanism
/// lives here — that's each concrete store's own business.
/// <para>
/// Thread-safety: this base class holds no mutable state of its own (<see cref="IdSelector"/> is
/// immutable) — thread-safety of the naive <see cref="UpdateAsync"/> default is bounded by
/// whichever <see cref="AddAsync"/>/<see cref="FindAsync"/> the concrete subclass provides.
/// Subclasses MUST make their own state (if any) thread-safe.
/// </para>
/// </summary>
public abstract class AbstractStore<TRecord> : IStore<TRecord>
{
    protected AbstractStore(Func<TRecord, string> idSelector)
    {
        ArgumentNullException.ThrowIfNull(idSelector);
        IdSelector = idSelector;
    }

    /// <summary>Extracts the id from a record instance.</summary>
    protected Func<TRecord, string> IdSelector { get; }

    public abstract ValueTask AddAsync(TRecord record, CancellationToken cancellationToken = default);

    public abstract ValueTask<TRecord?> FindAsync(string id, CancellationToken cancellationToken = default);

    public abstract ValueTask DeleteAsync(string id, CancellationToken cancellationToken = default);

    public abstract ValueTask<IReadOnlyList<TRecord>> FindAllAsync(CancellationToken cancellationToken = default);

    public virtual async ValueTask<IReadOnlyList<TRecord>> FindWhereAsync(Func<TRecord, bool> predicate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var all = await FindAllAsync(cancellationToken).ConfigureAwait(false);
        return [.. all.Where(predicate)];
    }

    public virtual async ValueTask UpdateAsync(string id, Func<TRecord, TRecord> mutator, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutator);
        var existing = await FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            await AddAsync(mutator(existing), cancellationToken).ConfigureAwait(false);
    }
}
