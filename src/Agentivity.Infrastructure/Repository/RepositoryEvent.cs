namespace Agentivity.Infrastructure.Repository;

/// <summary>
/// Pure data carrier describing one change to apply to the entity identified by
/// <paramref name="EntityId"/> — no behavior of its own (no "apply yourself" method): the
/// repository that owns the entity's shape decides how to interpret it. Concrete subtypes live
/// wherever makes sense for the field they describe (see <c>GraphChangedEvent</c>/
/// <c>OutputsChangedEvent</c> next to <c>RunRepository</c>) — this base type only exists so
/// <see cref="AbstractRepository{TEntity,TRecord}.UpdateAsync"/>-style consumers have one shared
/// vocabulary for "a change happened, here's its target id".
/// </summary>
public abstract record RepositoryEvent(string EntityId);
