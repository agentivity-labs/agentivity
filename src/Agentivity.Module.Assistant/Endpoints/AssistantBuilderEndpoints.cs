using Agentivity.Infrastructure.Capabilities;
using System.Text.Json.Nodes;
using Agentivity.Module.Assistant.Loop;
using Agentivity.Module.Assistant.Operations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Agentivity.Module.Assistant.Endpoints;

/// <summary>
/// The builder over HTTP. Routes (all under <c>/api/v1/assistant</c>):
/// <c>POST /changesets</c>, <c>GET /changesets</c>, <c>GET /changesets/{id}</c>, <c>GET /changesets/{id}/entities/{entityId}</c>,
/// <c>POST /changesets/{id}/operations</c>, <c>POST /changesets/{id}/validate</c>, <c>POST /changesets/{id}/revert</c>,
/// <c>POST /changesets/{id}/save</c>, <c>DELETE /changesets/{id}</c>, <c>GET /operations</c>.
/// </summary>
public static class AssistantBuilderEndpoints
{
    public const string RoutePrefix = "/api/v1/assistant";

    public static IEndpointRouteBuilder MapAssistantBuilderEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup(RoutePrefix).WithTags("Assistant");

        group.MapGet("/operations", () => Results.Ok(new { operations = OperationEngine.Operations }));

        // A piece of work started from something already saved (the "improve with the assistant" of the editors).
        group.MapPost(
            "/changesets/open",
            async (IBuilderApi api, OpenEntityRequest request, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(request.EntityId))
                    return Results.BadRequest(new { error = "'entityId' is required." });
                var changeset = await api.OpenEntityAsync(request.EntityId.Trim(), ct);
                return changeset is null ? Results.NotFound(new { error = $"There is no agent, team or workflow '{request.EntityId}' in the workspace." }) : Results.Created($"{RoutePrefix}/changesets/{changeset.Id}", BuilderApi.Summarize(changeset));
            }
        );

        group.MapPost(
            "/changesets",
            async (IBuilderApi api, CreateChangesetRequest? request, CancellationToken ct) =>
            {
                var changeset = await api.CreateChangesetAsync(request?.Name, request?.FolderId, ct);
                return Results.Created($"{RoutePrefix}/changesets/{changeset.Id}", BuilderApi.Summarize(changeset));
            }
        );

        group.MapGet(
            "/changesets",
            async (IBuilderApi api, CancellationToken ct) => Results.Ok((await api.ListChangesetsAsync(ct)).Select(BuilderApi.Summarize))
        );

        group.MapGet(
            "/changesets/{id}",
            async (IBuilderApi api, string id, CancellationToken ct) => await api.GetChangesetAsync(id, ct) is { } changeset ? Results.Ok(BuilderApi.Summarize(changeset)) : NotFound(id)
        );

        group.MapGet(
            "/changesets/{id}/entities/{entityId}",
            async (IBuilderApi api, string id, string entityId, CancellationToken ct) =>
            {
                var changeset = await api.GetChangesetAsync(id, ct);
                if (changeset is null)
                    return NotFound(id);
                var entity = changeset.Find(entityId);
                return entity is null ? Results.NotFound(new { error = $"No entity '{entityId}' in this changeset." }) : Results.Ok(new { entity.Id, entity.Kind, entity.Name, entity.State, entity.BaseFingerprint, draft = entity.Draft });
            }
        );

        group.MapPost(
            "/changesets/{id}/operations",
            async (IBuilderApi api, string id, OperationsRequest request, CancellationToken ct) =>
            {
                if (request.Operations is null || request.Operations.Count == 0)
                    return Results.BadRequest(new { error = "'operations' must contain at least one operation." });

                var operations = request.Operations.OfType<JsonObject>().Select(BuilderOperation.From).ToList();
                var result = await api.ApplyOperationsAsync(id, operations, request.Atomic ?? true, request.Label, ct);
                if (result is null)
                    return NotFound(id);

                var changeset = await api.GetChangesetAsync(id, ct);
                return Results.Ok(new { applied = result.Applied, atomic = result.Atomic, revision = result.Revision, results = result.Results, changeset = changeset is null ? null : BuilderApi.Summarize(changeset) });
            }
        );

        // The same light description the assistant builds from: steps, resources and connections in the catalog's identifiers.
        group.MapPost(
            "/changesets/{id}/build",
            async (SpecBuilder specs, IBuilderApi api, string id, JsonNode spec, CancellationToken ct) =>
            {
                var result = await specs.BuildAsync(id, spec, ct);
                if (result is null)
                    return NotFound(id);
                var changeset = await api.GetChangesetAsync(id, ct);
                var body = new { built = result.Built, problems = result.Problems, notes = result.Notes, created = result.Created, issues = result.Issues, changeset = changeset is null ? null : BuilderApi.Summarize(changeset) };
                return result.Built ? Results.Ok(body) : Results.UnprocessableEntity(body);
            }
        );

        // The catalog as the assistant reads it (and its size), to see exactly what the model is given.
        group.MapGet(
            "/catalog",
            (ICapabilityRegistry registry, int? maxDetailed) =>
            {
                var text = CatalogText.Build(registry.Current, maxDetailed ?? new AssistantLoopOptions().CatalogMaxDetailed);
                return Results.Ok(new { characters = text.Length, approxTokens = text.Length / 4, text });
            }
        );

        group.MapPost(
            "/changesets/{id}/validate",
            async (IBuilderApi api, string id, CancellationToken ct) =>
            {
                var issues = await api.ValidateAsync(id, ct);
                return issues is null
                    ? NotFound(id)
                    : Results.Ok(new { valid = issues.All(i => i.Severity != "error"), errors = issues.Count(i => i.Severity == "error"), warnings = issues.Count(i => i.Severity == "warning"), issues });
            }
        );

        group.MapPost(
            "/changesets/{id}/revert",
            async (IBuilderApi api, string id, RevertRequest request, CancellationToken ct) =>
            {
                var changeset = await api.RevertAsync(id, request.Revision, ct);
                return changeset is null ? Results.NotFound(new { error = "No such changeset or revision." }) : Results.Ok(BuilderApi.Summarize(changeset));
            }
        );

        // The one route that changes the workspace. It is for a person to call: the assistant has no tool that reaches it.
        group.MapPost(
            "/changesets/{id}/save",
            async (IBuilderApi api, string id, SaveRequest? request, CancellationToken ct) =>
            {
                // The person chooses where it goes as they save: the destination travels with the save.
                if (request?.Destination is { } destination && await api.SetDestinationAsync(id, destination.AgenticFolderId, destination.WorkflowFolderId, ct, destination.Path) is null)
                    return NotFound(id);

                var outcome = await api.SaveAsync(id, request?.Entities, request?.Force ?? false, ct);
                if (outcome is null)
                    return NotFound(id);
                return outcome.Saved ? Results.Ok(outcome) : Results.Json(outcome, statusCode: StatusCodes.Status409Conflict);
            }
        );

        // The model connections a person can choose for the agents that get built. This is separate from the model the assistant
        // itself talks to. A connection is listed when the platform has a model step that speaks to its provider.
        group.MapGet(
            "/connections",
            async (IConnectionLookup connections, ICapabilityRegistry registry, CancellationToken ct) =>
            {
                var kinds = ModelConnections.KindsWithAModelStep(registry.Current);
                var all = await connections.ListAsync(null, ct);
                var items = all
                    .Where(c => kinds.ContainsKey(c.Kind))
                    .Select(c => new { id = c.Id, name = c.Name, kind = c.Kind, description = c.Description, modelNode = kinds[c.Kind] });
                return Results.Ok(new { connections = items });
            }
        );

        group.MapPut(
            "/changesets/{id}/agent-model",
            async (IBuilderApi api, string id, AgentModelRequest request, CancellationToken ct) =>
            {
                try
                {
                    var changeset = await api.SetAgentModelAsync(id, request.ConnectionId, ct);
                    return changeset is null ? NotFound(id) : Results.Ok(BuilderApi.Summarize(changeset));
                }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }
        );

        group.MapDelete("/changesets/{id}", async (IBuilderApi api, string id, CancellationToken ct) => await api.DiscardAsync(id, ct) ? Results.NoContent() : NotFound(id));

        return routes;
    }

    private static IResult NotFound(string id) => Results.NotFound(new { error = $"No changeset '{id}'." });
}

public sealed record CreateChangesetRequest(string? Name, string? FolderId);

/// <summary>The saved agent, team or workflow to start a piece of work from.</summary>
public sealed record OpenEntityRequest(string EntityId);

public sealed record AgentModelRequest(string? ConnectionId);

public sealed record OperationsRequest(List<JsonNode?>? Operations, bool? Atomic, string? Label);

public sealed record RevertRequest(int Revision);

public sealed record SaveRequest(List<string>? Entities, bool? Force, SaveDestination? Destination = null);

/// <summary>
/// Where the new entities of a save go. <paramref name="Path"/> is the one place the person chose, as folder names from the
/// top level down (empty for the top level): it is followed in each library the save adds to, creating what is missing.
/// Without it, one folder id per library, each null for its root.
/// </summary>
public sealed record SaveDestination(string? AgenticFolderId = null, string? WorkflowFolderId = null, List<string>? Path = null);
