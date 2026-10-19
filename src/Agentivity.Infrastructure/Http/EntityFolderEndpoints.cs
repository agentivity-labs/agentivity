using Agentivity.Domain;
using Agentivity.Infrastructure.Repository.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Agentivity.Infrastructure.Http;

public sealed record EntityFolderCreate(string Name, string? ParentFolderId = null);

public sealed record EntityFolderRename(string Name);

public sealed record EntityFolderMove(string? TargetParentFolderId);

public sealed record EntityItemMove(string? TargetFolderId);

/// <summary>
/// The folders of a library of the workspace, the same routes whatever it holds: browse, list, create, rename, delete and move
/// folders, move an item to another folder. <c>{prefix}/browse</c>, <c>{prefix}/folders</c>, <c>{prefix}/{id}/move</c>.
/// </summary>
public static class EntityFolderEndpoints
{
    public static void MapFoldersOf<TEntity>(this RouteGroupBuilder v1, string prefix, string label)
        where TEntity : Entity
    {
        v1.MapGet($"{prefix}/browse", async ([FromQuery] string? folderId, [FromServices] IEntityRepository<TEntity> repo, CancellationToken ct) => Results.Ok(await repo.BrowseAsync(folderId, ct).ConfigureAwait(false)))
            .WithSummary($"Browse the {label} folders and their content");

        v1.MapGet($"{prefix}/folders", async ([FromQuery] string? parentFolderId, [FromServices] IEntityRepository<TEntity> repo, CancellationToken ct) => Results.Ok(await repo.ListFoldersAsync(parentFolderId, ct).ConfigureAwait(false)))
            .WithSummary($"List the {label} folders");

        v1.MapPost(
                $"{prefix}/folders",
                async (EntityFolderCreate body, [FromServices] IEntityRepository<TEntity> repo, CancellationToken ct) =>
                {
                    var result = await repo.CreateFolderAsync(body.Name, body.ParentFolderId, ct).ConfigureAwait(false);
                    return result.Success && result.Value is not null ? Results.Ok(result.Value) : Results.BadRequest(new { error = result.Error });
                }
            )
            .WithSummary($"Create a {label} folder");

        v1.MapPut(
                $"{prefix}/folders/{{folderId}}",
                async (string folderId, EntityFolderRename body, [FromServices] IEntityRepository<TEntity> repo, CancellationToken ct) =>
                {
                    var result = await repo.RenameFolderAsync(folderId, body.Name, ct).ConfigureAwait(false);
                    return result.Success ? Results.Ok() : Results.BadRequest(new { error = result.Error });
                }
            )
            .WithSummary($"Rename a {label} folder");

        v1.MapDelete(
                $"{prefix}/folders/{{folderId}}",
                async (string folderId, [FromServices] IEntityRepository<TEntity> repo, CancellationToken ct) =>
                {
                    var result = await repo.DeleteFolderAsync(folderId, ct).ConfigureAwait(false);
                    return result.Success ? Results.Ok() : Results.BadRequest(new { error = result.Error });
                }
            )
            .WithSummary($"Delete a {label} folder");

        v1.MapPost(
                $"{prefix}/folders/{{folderId}}/move",
                async (string folderId, EntityFolderMove body, [FromServices] IEntityRepository<TEntity> repo, CancellationToken ct) =>
                {
                    var result = await repo.MoveFolderAsync(folderId, body.TargetParentFolderId, ct).ConfigureAwait(false);
                    return result.Success ? Results.Ok() : Results.BadRequest(new { error = result.Error });
                }
            )
            .WithSummary($"Move a {label} folder");

        v1.MapPost(
                $"{prefix}/{{id}}/move",
                async (string id, EntityItemMove body, [FromServices] IEntityRepository<TEntity> repo, CancellationToken ct) =>
                {
                    var result = await repo.MoveAsync(id, body.TargetFolderId, ct).ConfigureAwait(false);
                    return result.Success ? Results.Ok() : Results.BadRequest(new { error = result.Error });
                }
            )
            .WithSummary($"Move a {label} to another folder");
    }
}
