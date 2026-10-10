namespace Agentivity.Infrastructure.Repository;

/// <summary>
/// Generic persistence contract for a single record type — pure CRUD, no assumption about the
/// physical storage mechanism (disk, in-memory, database) or wire format. Concrete stores decide
/// how a <typeparamref name="TRecord"/> instance is actually persisted; this interface only
/// describes what callers can do with one. Same vocabulary as <see cref="IRepository{TEntity,TRecord}"/>
/// (Add/Find/Delete) for a coherent repository framework — implementations MUST be thread-safe.
/// </summary>
public interface IStore<TRecord>
{
    /// <summary>Adds or updates the record.</summary>
    ValueTask AddAsync(TRecord record, CancellationToken cancellationToken = default);

    /// <summary>Finds a record by id. Returns <see langword="null"/> if not found.</summary>
    ValueTask<TRecord?> FindAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Deletes the record. No-op if absent.</summary>
    ValueTask DeleteAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Lists every record.</summary>
    ValueTask<IReadOnlyList<TRecord>> FindAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists every record matching <paramref name="predicate"/>.</summary>
    ValueTask<IReadOnlyList<TRecord>> FindWhereAsync(Func<TRecord, bool> predicate, CancellationToken cancellationToken = default);

    /// <summary>Atomic read-modify-write: loads the record, applies <paramref name="mutator"/>, saves the result. No-op if the record is absent.</summary>
    ValueTask UpdateAsync(string id, Func<TRecord, TRecord> mutator, CancellationToken cancellationToken = default);
}
