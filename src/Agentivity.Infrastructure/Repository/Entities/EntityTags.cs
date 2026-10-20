using Agentivity.Domain;
using Agentivity.Domain.Common;

namespace Agentivity.Infrastructure.Repository.Entities;

/// <summary>
/// Changes the tags of an entity of any library, and nothing else: the entity is read, given its new tags, and saved where it is
/// (its folder is kept). Marking an agent, a team or a workflow as a Solution is adding the tag "solution".
/// </summary>
public static class EntityTags
{
    /// <summary>The tag that marks what a person launches to get a need handled from start to finish.</summary>
    public const string Solution = "solution";

    public static async ValueTask<OperationResult<TEntity>> SetAsync<TEntity>(IEntityRepository<TEntity> repository, string id, IEnumerable<string> tags, CancellationToken cancellationToken = default)
        where TEntity : Entity
    {
        var found = await repository.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess || found.Value is not { } entity)
            return OperationResult<TEntity>.Failure(found.Error ?? $"Nothing has the id '{id}'.");

        var tagged = (TEntity)((Entity)entity with { Tags = [.. tags] });
        return await repository.SaveAsync(tagged, folderId: null, cancellationToken).ConfigureAwait(false);
    }
}
