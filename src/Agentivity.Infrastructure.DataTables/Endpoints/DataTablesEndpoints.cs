using Agentivity.Infrastructure.DataTables.Domain;
using Agentivity.Infrastructure.DataTables.Runtime;
using Agentivity.Infrastructure.Http;
using Agentivity.Infrastructure.Repository.Entities;
using Agentivity.Infrastructure.Storage.Workspace;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Agentivity.Infrastructure.DataTables.Endpoints;

/// <summary>
/// CRUD, browse/folders, versions and row endpoints for shared Data Table assets.
/// Routes: /api/v1/datatables, /api/v1/datatables/{id}/rows. Owned by this project
/// (not Agentivity.Host) so the DataTables vertical stays self-contained — Host only
/// calls <see cref="MapDataTablesApi"/>, it never references <see cref="DataTableEntity"/>
/// or <see cref="DataTableRowStore"/> directly.
/// </summary>
public static class DataTablesEndpoints
{
    public static IEndpointRouteBuilder MapDataTablesApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        MapCrud(endpoints);
        MapBrowseAndFolders(endpoints);
        MapVersions(endpoints);
        MapRows(endpoints);

        return endpoints;
    }

    // ── CRUD (schema) ─────────────────────────────────────────────────────────

    private static void MapCrud(IEndpointRouteBuilder endpoints)
    {
        var v1 = endpoints.MapV1();

        v1.MapGet(
            "/datatables",
            async ([FromQuery] string? folderId, [FromServices] IEntityRepository<DataTableEntity> repo, CancellationToken ct) =>
            {
                var items = await repo.ListAsync(folderId, ct).ConfigureAwait(false);
                return Results.Ok(items);
            }
        );

        v1.MapGet(
            "/datatables/{id}",
            async (string id, [FromServices] IEntityRepository<DataTableEntity> repo, CancellationToken ct) =>
            {
                var result = await repo.GetAsync(id, ct).ConfigureAwait(false);
                return result.IsSuccess && result.Value is not null ? Results.Ok(result.Value) : ApiEndpointHelpers.ApiNotFound(code: "datatable_not_found", userMessage: "This Data Table could not be found.", detail: result.Error ?? $"Data Table '{id}' not found.", context: new { id });
            }
        );

        // Create or update a Data Table's schema (columns). Row data is managed separately below.
        v1.MapPut(
            "/datatables/{id}",
            async (string id, DataTableEntity entity, [FromQuery] string? folderId, [FromServices] IEntityRepository<DataTableEntity> repo, [FromServices] DataTableRowStore rowStore, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    return ApiEndpointHelpers.ApiValidationError(code: "datatable_id_required", userMessage: "A Data Table identifier is required.", detail: "Data Table id is required.");
                }

                var effective = string.Equals(entity.Id, id, StringComparison.Ordinal)
                    ? entity
                    : new DataTableEntity(id, entity.Name) { Columns = entity.Columns, Role = entity.Role, Tags = entity.Tags };
                var result = await repo.SaveAsync(effective, folderId, ct).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    // A table already loaded keeps its rows and takes the new schema now — not at the next restart.
                    await rowStore.RefreshSchemaAsync(id, ct).ConfigureAwait(false);
                }

                return result.IsSuccess && result.Value is not null ? Results.Ok(result.Value) : ApiEndpointHelpers.ApiValidationError(code: "datatable_save_failed", userMessage: "The Data Table could not be saved.", detail: result.Error ?? "The Data Table could not be saved.", context: new { id });
            }
        );

        v1.MapDelete(
            "/datatables/{id}",
            async (string id, [FromServices] IEntityRepository<DataTableEntity> repo, CancellationToken ct) =>
            {
                var result = await repo.DeleteAsync(id, ct).ConfigureAwait(false);
                return result.IsSuccess ? Results.Ok(new { status = "deleted" }) : ApiEndpointHelpers.ApiValidationError(code: "datatable_delete_failed", userMessage: "The Data Table could not be deleted.", detail: result.Error ?? "The Data Table could not be deleted.", context: new { id });
            }
        );
    }

    // ── Browse / folders ──────────────────────────────────────────────────────

    private static void MapBrowseAndFolders(IEndpointRouteBuilder endpoints)
    {
        var v1 = endpoints.MapV1();

        v1.MapGet(
            "/datatables/browse",
            async ([FromQuery] string? folderId, [FromServices] IEntityRepository<DataTableEntity> repo, CancellationToken ct) =>
            {
                var result = await repo.BrowseAsync(folderId, ct).ConfigureAwait(false);
                return Results.Ok(result);
            }
        );

        v1.MapGet(
            "/datatables/folders",
            async ([FromQuery] string? parentFolderId, [FromServices] IEntityRepository<DataTableEntity> repo, CancellationToken ct) =>
            {
                var folders = await repo.ListFoldersAsync(parentFolderId, ct).ConfigureAwait(false);
                return Results.Ok(folders);
            }
        );

        v1.MapPost(
            "/datatables/folders",
            async (CreateFolderRequest body, [FromServices] IEntityRepository<DataTableEntity> repo, CancellationToken ct) =>
            {
                var result = await repo.CreateFolderAsync(body.Name, body.ParentFolderId, ct).ConfigureAwait(false);
                return result.Success && result.Value is not null ? Results.Ok(result.Value) : Results.BadRequest(new { error = result.Error });
            }
        );

        v1.MapPut(
            "/datatables/folders/{folderId}",
            async (string folderId, RenameFolderRequest body, [FromServices] IEntityRepository<DataTableEntity> repo, CancellationToken ct) =>
            {
                var result = await repo.RenameFolderAsync(folderId, body.Name, ct).ConfigureAwait(false);
                return result.Success ? Results.Ok() : Results.BadRequest(new { error = result.Error });
            }
        );

        v1.MapDelete(
            "/datatables/folders/{folderId}",
            async (string folderId, [FromServices] IEntityRepository<DataTableEntity> repo, CancellationToken ct) =>
            {
                var result = await repo.DeleteFolderAsync(folderId, ct).ConfigureAwait(false);
                return result.Success ? Results.Ok() : Results.BadRequest(new { error = result.Error });
            }
        );

        v1.MapPost(
            "/datatables/folders/{folderId}/move",
            async (string folderId, MoveFolderRequest body, [FromServices] IEntityRepository<DataTableEntity> repo, CancellationToken ct) =>
            {
                var result = await repo.MoveFolderAsync(folderId, body.TargetParentFolderId, ct).ConfigureAwait(false);
                return result.Success ? Results.Ok() : Results.BadRequest(new { error = result.Error });
            }
        );

        v1.MapPost(
            "/datatables/{id}/move",
            async (string id, MoveItemRequest body, [FromServices] IEntityRepository<DataTableEntity> repo, CancellationToken ct) =>
            {
                var result = await repo.MoveAsync(id, body.TargetFolderId, ct).ConfigureAwait(false);
                return result.Success ? Results.Ok() : Results.BadRequest(new { error = result.Error });
            }
        );
    }

    // ── Versioning ────────────────────────────────────────────────────────────

    private static void MapVersions(IEndpointRouteBuilder endpoints)
    {
        var v1 = endpoints.MapV1();

        v1.MapGet(
            "/datatables/{id}/versions",
            async (string id, [FromServices] IWorkspaceObjectVersionStore? versionStore, CancellationToken ct) =>
            {
                if (versionStore is null || !versionStore.VersioningCapabilities.Enabled)
                    return Results.NotFound();

                var versions = await versionStore.ListVersionsAsync(DataTableEntity.ObjectType, id, ct).ConfigureAwait(false);
                return Results.Ok(versions);
            }
        );

        v1.MapPost(
            "/datatables/{id}/versions",
            async (string id, CreateVersionRequest body, [FromServices] IWorkspaceObjectVersionStore? versionStore, CancellationToken ct) =>
            {
                if (versionStore is null || !versionStore.VersioningCapabilities.Enabled)
                    return Results.NotFound();

                var result = await versionStore.CreateVersionAsync(DataTableEntity.ObjectType, id, new WorkspaceObjectVersionCreateRequest(body.Label, body.CreatedBy), ct).ConfigureAwait(false);
                return result.Success && result.Value is not null ? Results.Ok(result.Value) : Results.BadRequest(new { error = result.Error });
            }
        );

        v1.MapGet(
            "/datatables/{id}/versions/{versionId}",
            async (string id, string versionId, [FromServices] IWorkspaceObjectVersionStore? versionStore, CancellationToken ct) =>
            {
                if (versionStore is null || !versionStore.VersioningCapabilities.Enabled)
                    return Results.NotFound();

                var result = await versionStore.GetVersionAsync(DataTableEntity.ObjectType, id, versionId, ct).ConfigureAwait(false);
                return result.Success && result.Value is not null ? Results.Ok(result.Value) : Results.NotFound(new { error = result.Error });
            }
        );

        v1.MapPost(
            "/datatables/{id}/versions/{versionId}/restore",
            async (string id, string versionId, RestoreVersionRequest body, [FromServices] IWorkspaceObjectVersionStore? versionStore, CancellationToken ct) =>
            {
                if (versionStore is null || !versionStore.VersioningCapabilities.Enabled)
                    return Results.NotFound();

                var result = await versionStore.RestoreVersionAsync(DataTableEntity.ObjectType, id, versionId, new WorkspaceObjectVersionRestoreRequest(body.Label, body.RestoredBy), ct).ConfigureAwait(false);
                return result.Success && result.Value is not null ? Results.Ok(result.Value) : Results.BadRequest(new { error = result.Error });
            }
        );
    }

    // ── Rows (runtime data, separate from the versioned schema entity) ───────

    private static void MapRows(IEndpointRouteBuilder endpoints)
    {
        var v1 = endpoints.MapV1();

        v1.MapGet(
            "/datatables/{id}/rows",
            async (string id, [FromQuery] string? whereColumn, [FromQuery] string? whereValue, [FromServices] DataTableRowStore rowStore, CancellationToken ct) =>
            {
                var table = await rowStore.GetOrLoadAsync(id, ct).ConfigureAwait(false);
                var rows = string.IsNullOrWhiteSpace(whereColumn) ? table.SelectAll() : table.SelectWhere(whereColumn, whereValue);
                return Results.Ok(rows.Select(r => r.ToDictionary()));
            }
        );

        v1.MapPost(
            "/datatables/{id}/rows",
            async (string id, Dictionary<string, object?> data, [FromServices] DataTableRowStore rowStore, CancellationToken ct) =>
            {
                var table = await rowStore.GetOrLoadAsync(id, ct).ConfigureAwait(false);
                var row = table.Insert(DataTableRowStore.NormalizeRow(data));
                await rowStore.SaveAsync(id, ct).ConfigureAwait(false);
                return Results.Ok(row.ToDictionary());
            }
        );

        v1.MapPut(
            "/datatables/{id}/rows/{rowId:long}",
            async (string id, long rowId, Dictionary<string, object?> data, [FromServices] DataTableRowStore rowStore, CancellationToken ct) =>
            {
                var table = await rowStore.GetOrLoadAsync(id, ct).ConfigureAwait(false);
                var row = table.Update(rowId, DataTableRowStore.NormalizeRow(data));
                if (row is null)
                {
                    return ApiEndpointHelpers.ApiNotFound(code: "datatable_row_not_found", userMessage: "This row could not be found.", detail: $"Row '{rowId}' not found in Data Table '{id}'.", context: new { id, rowId });
                }

                await rowStore.SaveAsync(id, ct).ConfigureAwait(false);
                return Results.Ok(row.ToDictionary());
            }
        );

        v1.MapDelete(
            "/datatables/{id}/rows/{rowId:long}",
            async (string id, long rowId, [FromServices] DataTableRowStore rowStore, CancellationToken ct) =>
            {
                var table = await rowStore.GetOrLoadAsync(id, ct).ConfigureAwait(false);
                var deleted = table.Delete(rowId);
                if (!deleted)
                {
                    return ApiEndpointHelpers.ApiNotFound(code: "datatable_row_not_found", userMessage: "This row could not be found.", detail: $"Row '{rowId}' not found in Data Table '{id}'.", context: new { id, rowId });
                }

                await rowStore.SaveAsync(id, ct).ConfigureAwait(false);
                return Results.Ok(new { status = "deleted" });
            }
        );
    }
}

// ── Request DTOs ──────────────────────────────────────────────────────────────
// Small, self-contained copies of the generic workspace-object request shapes
// (identical in spirit to Agentivity.Host.Agentic's AgenticDtos.cs) — kept local
// so this project never needs to reference Agentivity.Host.

/// <summary>Body for POST /api/v1/datatables/{id}/versions.</summary>
public sealed record CreateVersionRequest(string Label, string? CreatedBy = null);

/// <summary>Body for POST /api/v1/datatables/{id}/versions/{versionId}/restore.</summary>
public sealed record RestoreVersionRequest(string Label, string? RestoredBy = null);

public sealed record CreateFolderRequest(string Name, string? ParentFolderId = null);

public sealed record RenameFolderRequest(string Name);

public sealed record MoveFolderRequest(string? TargetParentFolderId);

public sealed record MoveItemRequest(string? TargetFolderId);
