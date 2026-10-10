namespace Agentivity.Infrastructure.Repository;

/// <summary>
/// Generic CRUD-shaped contract for the in-memory hot-cache layer sitting in front of a durable
/// <see cref="IStore{TRecord}"/>: add a live entity (write-through), find it by id (memory hit, or
/// lazily reconstructed from the store on miss), list/filter across every cached entity, delete
/// it. Same vocabulary as <see cref="IStore{TRecord}"/> (Add/Find/Delete) for a coherent
/// repository framework. All async — some implementations (e.g. a repository backed directly by
/// a file, no in-memory cache) do real I/O on every call, not just an in-memory dictionary operation.
/// </summary>
public interface IRepository<TEntity, TRecord>
{
    /// <summary>Adds/updates a live entity, write-through to the durable store.</summary>
    ValueTask AddAsync(TEntity entity, CancellationToken cancellationToken = default);

    /// <summary>Finds the entity for <paramref name="id"/> — memory hit, or reconstructed from the durable store on miss. Null if unknown everywhere.</summary>
    ValueTask<TEntity?> FindAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Lists every live (cached) entity.</summary>
    ValueTask<IReadOnlyList<TEntity>> FindAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists every live (cached) entity matching <paramref name="predicate"/>.</summary>
    ValueTask<IReadOnlyList<TEntity>> FindWhereAsync(Func<TEntity, bool> predicate, CancellationToken cancellationToken = default);

    /// <summary>Deletes the entity.</summary>
    ValueTask DeleteAsync(string id, CancellationToken cancellationToken = default);
}
