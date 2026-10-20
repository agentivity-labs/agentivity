using System.Text.Json;
using Agentivity.Domain.DataTables;
using Agentivity.Domain.Workflow;
using Agentivity.Host.Agentic;
using Agentivity.Infrastructure.Http;
using Agentivity.Infrastructure.Registry;
using Agentivity.Infrastructure.Repository.Entities;
using Agentivity.Infrastructure.Storage.Workspace;
using Agentivity.Infrastructure.Workflow.Services.WorkflowManagement;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Observability.Contracts;
using Agentivity.Observability.Contracts.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Agentivity.Infrastructure.Execution.Runs;

namespace Agentivity.Host.Workflows;

/// <summary>
/// CRUD and run endpoints for workflows.
/// Routes: /api/v1/workflows, /api/v1/workflows/{workflowId}/runs
/// SSE streams are handled by the unified RunStreamEndpoints (/api/v1/streams/runs/{runId}/events).
/// </summary>
public static class WorkflowsEndpoints
{
    private const string WorkflowObjectType = "workflow.definition";

    public static IEndpointRouteBuilder MapWorkflowsApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        MapWorkflowCrud(endpoints);
        MapWorkflowBrowseAndFolders(endpoints);
        MapWorkflowVersions(endpoints);

        return endpoints;
    }

    // ── Browse / folders ─────────────────────────────────────────────────────

    private static void MapWorkflowBrowseAndFolders(IEndpointRouteBuilder endpoints)
    {
        var v1 = endpoints.MapV1();

        v1.MapGet(
            "/workflows/browse",
            async ([FromQuery] string? folderId, [FromServices] IEntityRepository<WorkflowEntity> repo, CancellationToken ct) =>
            {
                var result = await repo.BrowseAsync(folderId, ct).ConfigureAwait(false);
                return Results.Ok(result);
            }
        )
            .WithSummary("Browse the workflow folders and workflows");

        v1.MapGet(
            "/workflows/folders",
            async ([FromQuery] string? parentFolderId, [FromServices] IEntityRepository<WorkflowEntity> repo, CancellationToken ct) =>
            {
                var folders = await repo.ListFoldersAsync(parentFolderId, ct).ConfigureAwait(false);
                return Results.Ok(folders);
            }
        )
            .WithSummary("List the workflow folders");

        v1.MapPost(
            "/workflows/folders",
            async (CreateFolderRequest body, [FromServices] IEntityRepository<WorkflowEntity> repo, CancellationToken ct) =>
            {
                var result = await repo.CreateFolderAsync(body.Name, body.ParentFolderId, ct).ConfigureAwait(false);
                return result.Success && result.Value is not null ? Results.Ok(result.Value) : Results.BadRequest(new { error = result.Error });
            }
        )
            .WithSummary("Create a workflow folder");

        v1.MapPut(
            "/workflows/folders/{folderId}",
            async (string folderId, RenameFolderRequest body, [FromServices] IEntityRepository<WorkflowEntity> repo, CancellationToken ct) =>
            {
                var result = await repo.RenameFolderAsync(folderId, body.Name, ct).ConfigureAwait(false);
                return result.Success ? Results.Ok() : Results.BadRequest(new { error = result.Error });
            }
        )
            .WithSummary("Rename a workflow folder");

        v1.MapDelete(
            "/workflows/folders/{folderId}",
            async (string folderId, [FromServices] IEntityRepository<WorkflowEntity> repo, CancellationToken ct) =>
            {
                var result = await repo.DeleteFolderAsync(folderId, ct).ConfigureAwait(false);
                return result.Success ? Results.Ok() : Results.BadRequest(new { error = result.Error });
            }
        )
            .WithSummary("Delete a workflow folder");

        v1.MapPost(
            "/workflows/folders/{folderId}/move",
            async (string folderId, MoveFolderRequest body, [FromServices] IEntityRepository<WorkflowEntity> repo, CancellationToken ct) =>
            {
                var result = await repo.MoveFolderAsync(folderId, body.TargetParentFolderId, ct).ConfigureAwait(false);
                return result.Success ? Results.Ok() : Results.BadRequest(new { error = result.Error });
            }
        )
            .WithSummary("Move a workflow folder");

        v1.MapPost(
            "/workflows/{workflowId}/move",
            async (string workflowId, MoveItemRequest body, [FromServices] IEntityRepository<WorkflowEntity> repo, CancellationToken ct) =>
            {
                var result = await repo.MoveAsync(workflowId, body.TargetFolderId, ct).ConfigureAwait(false);
                return result.Success ? Results.Ok() : Results.BadRequest(new { error = result.Error });
            }
        )
            .WithSummary("Move a workflow to another folder");
    }

    // ── Versioning (requires workspace.versioning module) ────────────────────

    private static void MapWorkflowVersions(IEndpointRouteBuilder endpoints)
    {
        var v1 = endpoints.MapV1();

        v1.MapGet(
            "/workflows/{workflowId}/versions",
            async (string workflowId, [FromServices] IWorkspaceObjectVersionStore? versionStore, CancellationToken ct) =>
            {
                if (versionStore is null || !versionStore.VersioningCapabilities.Enabled)
                    return Results.NotFound();

                var versions = await versionStore.ListVersionsAsync(WorkflowObjectType, workflowId, ct).ConfigureAwait(false);
                return Results.Ok(versions);
            }
        )
            .WithSummary("List the saved versions of a workflow");

        v1.MapPost(
            "/workflows/{workflowId}/versions",
            async (string workflowId, CreateVersionRequest body, [FromServices] IWorkspaceObjectVersionStore? versionStore, CancellationToken ct) =>
            {
                if (versionStore is null || !versionStore.VersioningCapabilities.Enabled)
                    return Results.NotFound();

                var result = await versionStore.CreateVersionAsync(WorkflowObjectType, workflowId, new WorkspaceObjectVersionCreateRequest(body.Label, body.CreatedBy), ct).ConfigureAwait(false);
                return result.Success && result.Value is not null ? Results.Ok(result.Value) : Results.BadRequest(new { error = result.Error });
            }
        )
            .WithSummary("Save a new version of a workflow");

        v1.MapGet(
            "/workflows/{workflowId}/versions/{versionId}",
            async (string workflowId, string versionId, [FromServices] IWorkspaceObjectVersionStore? versionStore, CancellationToken ct) =>
            {
                if (versionStore is null || !versionStore.VersioningCapabilities.Enabled)
                    return Results.NotFound();

                var result = await versionStore.GetVersionAsync(WorkflowObjectType, workflowId, versionId, ct).ConfigureAwait(false);
                return result.Success && result.Value is not null ? Results.Ok(result.Value) : Results.NotFound(new { error = result.Error });
            }
        )
            .WithSummary("Get one saved version of a workflow");

        v1.MapPost(
            "/workflows/{workflowId}/versions/{versionId}/restore",
            async (string workflowId, string versionId, RestoreVersionRequest body, [FromServices] IWorkspaceObjectVersionStore? versionStore, CancellationToken ct) =>
            {
                if (versionStore is null || !versionStore.VersioningCapabilities.Enabled)
                    return Results.NotFound();

                var result = await versionStore.RestoreVersionAsync(WorkflowObjectType, workflowId, versionId, new WorkspaceObjectVersionRestoreRequest(body.Label, body.RestoredBy), ct).ConfigureAwait(false);
                return result.Success && result.Value is not null ? Results.Ok(result.Value) : Results.BadRequest(new { error = result.Error });
            }
        )
            .WithSummary("Restore an earlier version of a workflow");
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────

    private static void MapWorkflowCrud(IEndpointRouteBuilder endpoints)
    {
        var v1 = endpoints.MapV1();

        // List all workflows
        v1.MapGet(
            "/workflows",
            async (WorkflowService workflowService, CancellationToken cancellationToken) =>
            {
                var ids = await workflowService.ListAllAsync(cancellationToken).ConfigureAwait(false);

                var items = new List<object>(ids.Count);
                foreach (var id in ids)
                {
                    var workflow = await workflowService.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
                    if (workflow is not null)
                        items.Add(
                            new
                            {
                                id = workflow.Id,
                                name = workflow.Name,
                                updatedAt = workflow.UpdatedAt,
                            }
                        );
                    else
                        items.Add(
                            new
                            {
                                id,
                                name = string.Empty,
                                updatedAt = (DateTimeOffset?)null,
                            }
                        );
                }

                return Results.Ok(items);
            }
        )
            .WithSummary("List all workflows");

        // Get a workflow definition by id
        v1.MapGet(
            "/workflows/{id}",
            async (string id, WorkflowService workflowService, CancellationToken cancellationToken) =>
            {
                var workflow = await workflowService.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
                if (workflow is null)
                {
                    return ApiEndpointHelpers.ApiNotFound(code: "workflow_not_found", userMessage: "This workflow could not be found.", detail: $"Workflow '{id}' not found.", context: new { id });
                }

                return Results.Ok(workflow);
            }
        )
            .WithSummary("Get a workflow definition");

        // Returns stale input keys per node (editor uses this on workflow load)
        v1.MapGet(
            "/workflows/{id}/stale-inputs",
            async (string id, WorkflowService workflowService, WorkflowEngine workflowEngine, CancellationToken cancellationToken) =>
            {
                var workflow = await workflowService.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
                if (workflow is null)
                {
                    return ApiEndpointHelpers.ApiNotFound(code: "workflow_not_found", userMessage: "This workflow could not be found.", detail: $"Workflow '{id}' not found.", context: new { id });
                }

                var result = new Dictionary<string, IReadOnlyList<string>>();

                foreach (var WorkflowNodeDefinition in workflow.Nodes)
                {
                    if (WorkflowNodeDefinition.Parameters.ValueKind != System.Text.Json.JsonValueKind.Object)
                        continue;

                    var catalogNode = workflowEngine.FindNode(WorkflowNodeDefinition.NodeType);
                    if (catalogNode is not BaseNode baseNode)
                        continue;

                    var knownKeys = baseNode.GetInputsWithMetadata().Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

                    var stale = WorkflowNodeDefinition.Parameters.EnumerateObject().Select(p => p.Name).Where(k => !knownKeys.Contains(k)).ToArray();

                    if (stale.Length > 0)
                        result[WorkflowNodeDefinition.Id] = stale;
                }

                return Results.Ok(result);
            }
        )
            .WithSummary("List the obsolete inputs of each workflow node");

        // Get workflow version info
        v1.MapGet(
            "/workflows/{id}/version",
            async (string id, WorkflowService workflowService, CancellationToken cancellationToken) =>
            {
                var versionInfo = await workflowService.GetVersionAsync(id, cancellationToken).ConfigureAwait(false);
                if (versionInfo is null)
                {
                    return ApiEndpointHelpers.ApiNotFound(code: "workflow_not_found", userMessage: "This workflow could not be found.", detail: $"Workflow '{id}' not found.", context: new { id });
                }

                return Results.Ok(
                    new
                    {
                        id = versionInfo.Id,
                        updatedAt = versionInfo.UpdatedAt,
                    }
                );
            }
        )
            .WithSummary("Get when a workflow was last updated");

        // Create or update a workflow definition
        v1.MapPut(
            "/workflows/{id}",
            async (string id, WorkflowEntity definition, WorkflowService workflowService, CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    return ApiEndpointHelpers.ApiValidationError(code: "workflow_id_required", userMessage: "A workflow identifier is required.", detail: "Workflow id is required.");
                }

                var effectiveId = string.IsNullOrWhiteSpace(definition.Id) ? id : definition.Id;
                var effectiveName = string.IsNullOrWhiteSpace(definition.Name) ? definition.Name ?? string.Empty : definition.Name;

                definition = new WorkflowEntity(
                    effectiveId,
                    effectiveName,
                    definition.Role,
                    definition.EntryNode,
                    definition.Nodes,
                    definition.Connections,
                    new Dictionary<string, object?>(definition.Metadata, StringComparer.OrdinalIgnoreCase),
                    definition.UpdatedAt,
                    definition.DataTables.Count > 0 ? new Dictionary<string, DataTableDefinition>(definition.DataTables) : null,
                    definition.Annotations
                )
                {
                    // Tags is a shared Entity base field (see Entity.cs) — carried through here the
                    // same way it already is for Agent/Team saves, rather than being the one save
                    // path that silently drops it.
                    Tags = definition.Tags,
                    DeclaredOutputs = definition.DeclaredOutputs,
                };

                var saveResult = await workflowService.SaveAsync(definition, cancellationToken).ConfigureAwait(false);
                if (!saveResult.IsSuccess)
                {
                    return ApiEndpointHelpers.ApiValidationError(code: "workflow_save_failed", userMessage: "The workflow could not be saved.", detail: saveResult.Error ?? "The workflow could not be saved.", context: new { id });
                }

                return Results.Ok(new { status = "saved" });
            }
        )
            .WithSummary("Create or update a workflow");

        // Delete a workflow definition
        v1.MapDelete(
            "/workflows/{id}",
            async (string id, WorkflowService workflowService, CancellationToken cancellationToken) =>
            {
                var result = await workflowService.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    return ApiEndpointHelpers.ApiValidationError(code: "workflow_delete_failed", userMessage: "The workflow could not be deleted.", detail: result.Error ?? "The workflow could not be deleted.", context: new { id });
                }

                return Results.Ok(new { status = "deleted" });
            }
        )
            .WithSummary("Delete a workflow");

        // Validate a workflow without saving it
        v1.MapPost(
            "/workflows/validate",
            (WorkflowEntity workflow, Agentivity.Infrastructure.Workflows.Validation.WorkflowValidator validator) =>
            {
                if (workflow is null)
                {
                    return ApiEndpointHelpers.ApiValidationError(code: "workflow_definition_required", userMessage: "A workflow definition is required.", detail: "Workflow definition is required.");
                }

                var result = validator.Validate(workflow);

                return Results.Ok(
                    new
                    {
                        isValid = result.IsValid,
                        validatedAt = result.ValidatedAt,
                        errorCount = result.ErrorCount,
                        warningCount = result.WarningCount,
                        infoCount = result.InfoCount,
                        issues = result.Issues,
                    }
                );
            }
        )
            .WithSummary("Validate a workflow without saving it");
    }

    // ── Helpers (internal so RunsEndpointMappings can reuse them) ────────────

    private static WorkflowNodeRunState PickStateByPriority(IEnumerable<WorkflowNodeRunState> states)
    {
        var priority = new Dictionary<WorkflowNodeRunState, int>
        {
            [WorkflowNodeRunState.NotStarted] = 0,
            [WorkflowNodeRunState.Completed] = 1,
            [WorkflowNodeRunState.Failed] = 2,
            [WorkflowNodeRunState.Running] = 3,
        };

        // Return the HIGHEST priority state (most active wins: Running > Failed > Completed > NotStarted).
        return states.DefaultIfEmpty(WorkflowNodeRunState.NotStarted).OrderByDescending(s => priority[s]).First();
    }

    /// <summary>
    /// Maps live in-memory <see cref="RunContext"/> steps to the same nodeStatuses format
    /// as <see cref="MapNodeStatuses"/>, so the Flutter inspector receives real-time data
    /// (current state, inputs, outputs) without waiting for disk persistence.
    /// </summary>
    internal static IEnumerable<object> MapNodeStatusesFromRegistry(RunContext entry)
    {
        foreach (var step in entry.Steps.Values)
        {
            WorkflowNodeRunState effectiveState;

            if (string.Equals(step.SubType, "resource", StringComparison.OrdinalIgnoreCase))
            {
                // Resource node: state derived from all connected parent nodes (the To side). A resource asks nothing: when
                // the step it serves waits for a person, the resource is in use.
                var parentStates = entry.Transitions.Where(c => string.Equals(c.FromStepId, step.Id, StringComparison.OrdinalIgnoreCase)).Select(c => entry.Steps.TryGetValue(c.ToStepId, out var s) ? MapRegistryStateToNodeState(s.Status) : WorkflowNodeRunState.NotStarted).Select(s => s == WorkflowNodeRunState.WaitingForInput ? WorkflowNodeRunState.Running : s).ToList();

                effectiveState = parentStates.Count > 0 ? PickStateByPriority(parentStates) : WorkflowNodeRunState.NotStarted;
            }
            else
            {
                // Standard node: own execution state.
                effectiveState = MapRegistryStateToNodeState(step.Status);
            }

            yield return new
            {
                nodeId = step.Id,
                state = effectiveState.ToString().ToLowerInvariant(),
                error = step.Error,
                inputs = step.Inputs.Count > 0 ? (object)step.Inputs : null,
                outputs = step.Outputs.Count > 0 ? (object)step.Outputs : null,
                logs = (IReadOnlyList<string>)step.Logs,
            };
        }
    }

    internal static IEnumerable<object> MapConnectionStatusesFromRegistry(RunContext entry)
    {
        foreach (var handoff in entry.Transitions)
        {
            bool traversed = handoff.Traversed;

            // Resource link: derive traversal from the linked node's live state.
            bool isResourceLink = string.Equals(handoff.FromPort, "resource", StringComparison.OrdinalIgnoreCase) || string.Equals(handoff.ToPort, "resource", StringComparison.OrdinalIgnoreCase);

            if (isResourceLink && !traversed)
            {
                // The linked node is the non-resource end: FromPort==resource → linked = To, else From.
                var linkedNodeId = string.Equals(handoff.FromPort, "resource", StringComparison.OrdinalIgnoreCase) ? handoff.ToStepId : handoff.FromStepId;
                if (entry.Steps.TryGetValue(linkedNodeId, out var linkedStep))
                    traversed = linkedStep.Status != Agentivity.Domain.Run.RunStatus.Pending;
            }

            yield return new
            {
                from = handoff.FromStepId,
                to = handoff.ToStepId,
                traversed,
                error = (string?)null,
            };
        }
    }

    private static WorkflowNodeRunState MapRegistryStateToNodeState(Agentivity.Domain.Run.RunStatus state) =>
        state switch
        {
            // Active states (highest priority in PickStateByPriority). A step that asked a person says so: it is what
            // tells which step an execution waits on.
            Agentivity.Domain.Run.RunStatus.Running => WorkflowNodeRunState.Running,
            Agentivity.Domain.Run.RunStatus.WaitingForInput => WorkflowNodeRunState.WaitingForInput,
            Agentivity.Domain.Run.RunStatus.Paused => WorkflowNodeRunState.Running,
            // Terminal states.
            Agentivity.Domain.Run.RunStatus.Completed => WorkflowNodeRunState.Completed,
            Agentivity.Domain.Run.RunStatus.Failed => WorkflowNodeRunState.Failed,
            Agentivity.Domain.Run.RunStatus.Cancelled => WorkflowNodeRunState.Failed,
            _ => WorkflowNodeRunState.NotStarted,
        };
}
