using Agentivity.Domain.DataTables;
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
        )
            .WithSummary("List the Data Tables of a folder");

        v1.MapGet(
            "/datatables/{id}",
            async (string id, [FromServices] IEntityRepository<DataTableEntity> repo, CancellationToken ct) =>
            {
                var result = await repo.GetAsync(id, ct).ConfigureAwait(false);
                return result.IsSuccess && result.Value is not null ? Results.Ok(result.Value) : ApiEndpointHelpers.ApiNotFound(code: "datatable_not_found", userMessage: "This Data Table could not be found.", detail: result.Error ?? $"Data Table '{id}' not found.", context: new { id });
            }
        )
            .WithSummary("Get the definition of a Data Table");

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

                // The rows of a table already there must fit the new schema, or they would no longer be readable: such a schema is refused.
                if ((await repo.GetAsync(id, ct).ConfigureAwait(false)).IsSuccess && await rowStore.WhyRowsDoNotFitAsync(id, effective.Columns, ct).ConfigureAwait(false) is { } misfit)
                {
                    return ApiEndpointHelpers.ApiConflictError(code: "datatable_rows_do_not_fit", userMessage: $"These columns do not fit the rows already in the table: {misfit}", detail: misfit, context: new { id });
                }

                var result = await repo.SaveAsync(effective, folderId, ct).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    // The table takes the new schema now, with its rows.
                    await rowStore.RefreshSchemaAsync(id, ct).ConfigureAwait(false);
                }

                return result.IsSuccess && result.Value is not null ? Results.Ok(result.Value) : ApiEndpointHelpers.ApiValidationError(code: "datatable_save_failed", userMessage: "The Data Table could not be saved.", detail: result.Error ?? "The Data Table could not be saved.", context: new { id });
            }
        )
            .WithSummary("Create or update the columns of a Data Table");

        v1.MapDelete(
            "/datatables/{id}",
            async (string id, [FromServices] IEntityRepository<DataTableEntity> repo, [FromServices] DataTableRowStore rowStore, CancellationToken ct) =>
            {
                var result = await repo.DeleteAsync(id, ct).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    // The rows go with the table.
                    await rowStore.DeleteAsync(id, ct).ConfigureAwait(false);
                }

                return result.IsSuccess ? Results.Ok(new { status = "deleted" }) : ApiEndpointHelpers.ApiValidationError(code: "datatable_delete_failed", userMessage: "The Data Table could not be deleted.", detail: result.Error ?? "The Data Table could not be deleted.", context: new { id });
            }
        )
            .WithSummary("Delete a Data Table and its rows");
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
        )
            .WithSummary("Browse the Data Table folders and tables");

        v1.MapGet(
            "/datatables/folders",
            async ([FromQuery] string? parentFolderId, [FromServices] IEntityRepository<DataTableEntity> repo, CancellationToken ct) =>
            {
                var folders = await repo.ListFoldersAsync(parentFolderId, ct).ConfigureAwait(false);
                return Results.Ok(folders);
            }
        )
            .WithSummary("List the Data Table folders");

        v1.MapPost(
            "/datatables/folders",
            async (CreateFolderRequest body, [FromServices] IEntityRepository<DataTableEntity> repo, CancellationToken ct) =>
            {
                var result = await repo.CreateFolderAsync(body.Name, body.ParentFolderId, ct).ConfigureAwait(false);
                return result.Success && result.Value is not null ? Results.Ok(result.Value) : Results.BadRequest(new { error = result.Error });
            }
        )
            .WithSummary("Create a Data Table folder");

        v1.MapPut(
            "/datatables/folders/{folderId}",
            async (string folderId, RenameFolderRequest body, [FromServices] IEntityRepository<DataTableEntity> repo, CancellationToken ct) =>
            {
                var result = await repo.RenameFolderAsync(folderId, body.Name, ct).ConfigureAwait(false);
                return result.Success ? Results.Ok() : Results.BadRequest(new { error = result.Error });
            }
        )
            .WithSummary("Rename a Data Table folder");

        v1.MapDelete(
            "/datatables/folders/{folderId}",
            async (string folderId, [FromServices] IEntityRepository<DataTableEntity> repo, CancellationToken ct) =>
            {
                var result = await repo.DeleteFolderAsync(folderId, ct).ConfigureAwait(false);
                return result.Success ? Results.Ok() : Results.BadRequest(new { error = result.Error });
            }
        )
            .WithSummary("Delete a Data Table folder");

        v1.MapPost(
            "/datatables/folders/{folderId}/move",
            async (string folderId, MoveFolderRequest body, [FromServices] IEntityRepository<DataTableEntity> repo, CancellationToken ct) =>
            {
                var result = await repo.MoveFolderAsync(folderId, body.TargetParentFolderId, ct).ConfigureAwait(false);
                return result.Success ? Results.Ok() : Results.BadRequest(new { error = result.Error });
            }
        )
            .WithSummary("Move a Data Table folder");

        v1.MapPost(
            "/datatables/{id}/move",
            async (string id, MoveItemRequest body, [FromServices] IEntityRepository<DataTableEntity> repo, CancellationToken ct) =>
            {
                var result = await repo.MoveAsync(id, body.TargetFolderId, ct).ConfigureAwait(false);
                return result.Success ? Results.Ok() : Results.BadRequest(new { error = result.Error });
            }
        )
            .WithSummary("Move a Data Table to another folder");
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
        )
            .WithSummary("List the saved versions of a Data Table");

        v1.MapPost(
            "/datatables/{id}/versions",
            async (string id, CreateVersionRequest body, [FromServices] IWorkspaceObjectVersionStore? versionStore, CancellationToken ct) =>
            {
                if (versionStore is null || !versionStore.VersioningCapabilities.Enabled)
                    return Results.NotFound();

                var result = await versionStore.CreateVersionAsync(DataTableEntity.ObjectType, id, new WorkspaceObjectVersionCreateRequest(body.Label, body.CreatedBy), ct).ConfigureAwait(false);
                return result.Success && result.Value is not null ? Results.Ok(result.Value) : Results.BadRequest(new { error = result.Error });
            }
        )
            .WithSummary("Save a new version of a Data Table");

        v1.MapGet(
            "/datatables/{id}/versions/{versionId}",
            async (string id, string versionId, [FromServices] IWorkspaceObjectVersionStore? versionStore, CancellationToken ct) =>
            {
                if (versionStore is null || !versionStore.VersioningCapabilities.Enabled)
                    return Results.NotFound();

                var result = await versionStore.GetVersionAsync(DataTableEntity.ObjectType, id, versionId, ct).ConfigureAwait(false);
                return result.Success && result.Value is not null ? Results.Ok(result.Value) : Results.NotFound(new { error = result.Error });
            }
        )
            .WithSummary("Get one saved version of a Data Table");

        v1.MapPost(
            "/datatables/{id}/versions/{versionId}/restore",
            async (string id, string versionId, RestoreVersionRequest body, [FromServices] IWorkspaceObjectVersionStore? versionStore, [FromServices] DataTableRowStore rowStore, CancellationToken ct) =>
            {
                if (versionStore is null || !versionStore.VersioningCapabilities.Enabled)
                    return Results.NotFound();

                // What the table holds is never lost by going back to an earlier definition: such a version is not restored.
                var version = await versionStore.GetVersionAsync(DataTableEntity.ObjectType, id, versionId, ct).ConfigureAwait(false);
                if (!version.Success || version.Value is null)
                    return Results.NotFound(new { error = version.Error });

                DataTableEntity? restored;
                try
                {
                    restored = EntityPayload.Read<DataTableEntity>(version.Value.Payload, DataTableEntity.ObjectType);
                }
                catch (System.Text.Json.JsonException ex)
                {
                    return ApiEndpointHelpers.ApiValidationError(code: "datatable_version_unreadable", userMessage: "This version of the Data Table cannot be read, so it was not restored.", detail: ex.Message, context: new { id, versionId });
                }

                if (restored is not null && await rowStore.WhyRestoreLosesValuesAsync(id, restored.Columns, ct).ConfigureAwait(false) is { } loss)
                {
                    return ApiEndpointHelpers.ApiConflictError(code: "datatable_restore_loses_values", userMessage: loss, detail: loss, context: new { id, versionId });
                }

                var result = await versionStore.RestoreVersionAsync(DataTableEntity.ObjectType, id, versionId, new WorkspaceObjectVersionRestoreRequest(body.Label, body.RestoredBy), ct).ConfigureAwait(false);
                if (!result.Success || result.Value is null)
                    return Results.BadRequest(new { error = result.Error });

                // The table takes the restored definition now, with its rows.
                await rowStore.RefreshSchemaAsync(id, ct).ConfigureAwait(false);
                return Results.Ok(result.Value);
            }
        )
            .WithSummary("Restore an earlier version of a Data Table");
    }

    // ── Rows (runtime data, separate from the versioned schema entity) ───────

    private static void MapRows(IEndpointRouteBuilder endpoints)
    {
        var v1 = endpoints.MapV1();

        // A search, as the agent's tool and the workflow node do it: where (JSON conditions), orderBy ("amount desc, customer"),
        // limit and offset — every row when none is given. The body is the list of rows, as always; the total found is in X-Total-Count.
        v1.MapGet(
            "/datatables/{id}/rows",
            async (
                string id,
                [FromQuery] string? where,
                [FromQuery] string? whereColumn,
                [FromQuery] string? whereValue,
                [FromQuery] string? orderBy,
                [FromQuery] int? limit,
                [FromQuery] int? offset,
                HttpResponse response,
                [FromServices] DataTableRowStore rowStore,
                CancellationToken ct
            ) =>
            {
                var table = await rowStore.OpenAsync(id, ct).ConfigureAwait(false);
                DataTableSearchResult found;
                try
                {
                    var conditions = new List<DataTableFilter>(DataTableQueryJson.ParseWhere(where));
                    if (!string.IsNullOrWhiteSpace(whereColumn))
                    {
                        conditions.Add(new DataTableCondition(whereColumn, DataTableOperator.Equals, whereValue));
                    }

                    // Done by the database: one page at most, and the total in X-Total-Count.
                    found = await table.SearchAsync(new DataTableQuery(conditions, DataTableQueryJson.ParseOrderBy(orderBy), limit, offset ?? 0), ct).ConfigureAwait(false);
                }
                catch (DataTableQueryException ex)
                {
                    return ApiEndpointHelpers.ApiValidationError(code: "datatable_query_invalid", userMessage: ex.Message, detail: ex.Message, context: new { id });
                }

                response.Headers["X-Total-Count"] = found.Total.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return Results.Ok(found.Rows.Select(r => r.ToDictionary()));
            }
        )
            .WithSummary("Search the rows of a Data Table");

        v1.MapPost(
            "/datatables/{id}/rows",
            async (string id, Dictionary<string, object?> data, [FromServices] DataTableRowStore rowStore, CancellationToken ct) =>
            {
                var table = await rowStore.OpenAsync(id, ct).ConfigureAwait(false);
                var row = await table.InsertAsync(DataTableRowStore.NormalizeRow(data), ct).ConfigureAwait(false);
                return Results.Ok(row.ToDictionary());
            }
        )
            .WithSummary("Add a row to a Data Table");

        v1.MapPut(
            "/datatables/{id}/rows/{rowId:long}",
            async (string id, long rowId, Dictionary<string, object?> data, [FromServices] DataTableRowStore rowStore, CancellationToken ct) =>
            {
                var table = await rowStore.OpenAsync(id, ct).ConfigureAwait(false);
                var row = await table.UpdateAsync(rowId, DataTableRowStore.NormalizeRow(data), ct).ConfigureAwait(false);
                if (row is null)
                {
                    return ApiEndpointHelpers.ApiNotFound(code: "datatable_row_not_found", userMessage: "This row could not be found.", detail: $"Row '{rowId}' not found in Data Table '{id}'.", context: new { id, rowId });
                }

                return Results.Ok(row.ToDictionary());
            }
        )
            .WithSummary("Update a row of a Data Table");

        v1.MapDelete(
            "/datatables/{id}/rows/{rowId:long}",
            async (string id, long rowId, [FromServices] DataTableRowStore rowStore, CancellationToken ct) =>
            {
                var table = await rowStore.OpenAsync(id, ct).ConfigureAwait(false);
                var deleted = await table.DeleteAsync(rowId, ct).ConfigureAwait(false);
                if (!deleted)
                {
                    return ApiEndpointHelpers.ApiNotFound(code: "datatable_row_not_found", userMessage: "This row could not be found.", detail: $"Row '{rowId}' not found in Data Table '{id}'.", context: new { id, rowId });
                }

                return Results.Ok(new { status = "deleted" });
            }
        )
            .WithSummary("Delete a row of a Data Table");
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
