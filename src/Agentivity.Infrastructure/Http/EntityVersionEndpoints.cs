using Agentivity.Infrastructure.Storage.Workspace;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Agentivity.Infrastructure.Http;

public sealed record EntityVersionCreate(string Label, string? CreatedBy = null);

public sealed record EntityVersionRestore(string Label, string? RestoredBy = null);

/// <summary>
/// The versions of a library of the workspace, the same routes whatever it holds: list, save, read and restore a version of an
/// item. <c>{prefix}/{id}/versions</c>, <c>{prefix}/{id}/versions/{versionId}</c>, <c>{prefix}/{id}/versions/{versionId}/restore</c>.
/// Without versioning (the loaded storage module has none), they answer "not found". The companion of
/// <see cref="EntityFolderEndpoints"/>.
/// </summary>
public static class EntityVersionEndpoints
{
    public static void MapVersionsOf(this RouteGroupBuilder v1, string prefix, string objectType, string label)
    {
        static bool Off(IWorkspaceObjectVersionStore? store) => store is null || !store.VersioningCapabilities.Enabled;

        v1.MapGet(
                $"{prefix}/{{id}}/versions",
                async (string id, [FromServices] IWorkspaceObjectVersionStore? versions, CancellationToken ct) =>
                    Off(versions) ? Results.NotFound() : Results.Ok(await versions!.ListVersionsAsync(objectType, id, ct).ConfigureAwait(false))
            )
            .WithSummary($"List the saved versions of a {label}");

        v1.MapPost(
                $"{prefix}/{{id}}/versions",
                async (string id, EntityVersionCreate body, [FromServices] IWorkspaceObjectVersionStore? versions, CancellationToken ct) =>
                {
                    if (Off(versions))
                        return Results.NotFound();
                    var result = await versions!.CreateVersionAsync(objectType, id, new WorkspaceObjectVersionCreateRequest(body.Label, body.CreatedBy), ct).ConfigureAwait(false);
                    return result.Success && result.Value is not null ? Results.Ok(result.Value) : Results.BadRequest(new { error = result.Error });
                }
            )
            .WithSummary($"Save a new version of a {label}");

        v1.MapGet(
                $"{prefix}/{{id}}/versions/{{versionId}}",
                async (string id, string versionId, [FromServices] IWorkspaceObjectVersionStore? versions, CancellationToken ct) =>
                {
                    if (Off(versions))
                        return Results.NotFound();
                    var result = await versions!.GetVersionAsync(objectType, id, versionId, ct).ConfigureAwait(false);
                    return result.Success && result.Value is not null ? Results.Ok(result.Value) : Results.NotFound(new { error = result.Error });
                }
            )
            .WithSummary($"Get one saved version of a {label}");

        v1.MapPost(
                $"{prefix}/{{id}}/versions/{{versionId}}/restore",
                async (string id, string versionId, EntityVersionRestore body, [FromServices] IWorkspaceObjectVersionStore? versions, CancellationToken ct) =>
                {
                    if (Off(versions))
                        return Results.NotFound();
                    var result = await versions!.RestoreVersionAsync(objectType, id, versionId, new WorkspaceObjectVersionRestoreRequest(body.Label, body.RestoredBy), ct).ConfigureAwait(false);
                    return result.Success && result.Value is not null ? Results.Ok(result.Value) : Results.BadRequest(new { error = result.Error });
                }
            )
            .WithSummary($"Restore an earlier version of a {label}");
    }
}
