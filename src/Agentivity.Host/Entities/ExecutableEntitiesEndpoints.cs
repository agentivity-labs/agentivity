using Agentivity.Domain.Executable;
using Agentivity.Domain.Execution;
using Agentivity.Domain.Team;
using Agentivity.Infrastructure.Agentic;
using Agentivity.Infrastructure.Execution.Executable;
using Agentivity.Infrastructure.Http;
using Agentivity.Infrastructure.Repository.Entities;
using Agentivity.Infrastructure.Workflows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Agentivity.Host.Entities;

/// <summary>
/// Catalog endpoint for <em>executable</em> entities (agents, teams, workflows) —
/// things a step that calls the library (<c>LibraryCallNode</c>) can invoke as a sub-entity in a
/// graph. Not a generic listing for every <see cref="Agentivity.Domain.Entity"/>-derived
/// kind: non-executable resources (e.g. DataTableEntity) have their own dedicated
/// endpoints (see <c>DataTablesEndpoints</c>) since they don't fit
/// <see cref="ExecutableKind"/>/<see cref="IExecutableResolver"/>.
/// </summary>
internal static class ExecutableEntitiesEndpoints
{
    private const string Base = "/api/v1/entities";

    public static IEndpointRouteBuilder MapExecutableEntitiesApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // GET /api/v1/entities?kind=agent|team|workflow&tag=Solution&tag=Featured&tagMode=any|all
        endpoints
            .MapV1()
            .MapGet(
                "/entities",
                async (
                    [FromQuery] string? kind,
                    [FromQuery(Name = "tag")] string[]? tags,
                    [FromQuery] string? tagMode,
                    [FromServices] IExecutableResolver resolver,
                    [FromServices] IEntityRepository<AgentEntity> agents,
                    [FromServices] IEntityRepository<TeamEntity> teams,
                    [FromServices] IEntityRepository<WorkflowEntity> workflows,
                    CancellationToken ct
                ) =>
                {
                    ExecutableKind? filter = null;
                    if (!string.IsNullOrWhiteSpace(kind))
                    {
                        if (!Enum.TryParse<ExecutableKind>(kind, ignoreCase: true, out var parsed))
                            return Results.BadRequest(
                                new
                                {
                                    status = "failed",
                                    code = "invalid_kind",
                                    message = $"Invalid kind '{kind}'. Must be one of: agent, team, workflow.",
                                }
                            );
                        filter = parsed;
                    }

                    // "any" (OR — has at least one of the requested tags) vs "all" (AND — has every
                    // one of them), same vocabulary as core.if's condition `combinator` (see
                    // ConditionGroup.cs) rather than inventing a second filter vocabulary. "any" is
                    // the default: a white-label portal typically configures one tag ("Solution"),
                    // and "any" is also the intuitive reading when several are given ("show me
                    // what's tagged Solution or Featured").
                    var requireAll = string.Equals(tagMode, "all", StringComparison.OrdinalIgnoreCase);
                    var requestedTags = (tags ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();

                    var items = await resolver.ListAsync(filter, ct).ConfigureAwait(false);

                    if (requestedTags.Count > 0)
                    {
                        items = items
                            .Where(u => requireAll
                                ? requestedTags.All(t => u.Tags.Contains(t, StringComparer.OrdinalIgnoreCase))
                                : requestedTags.Any(t => u.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)))
                            .ToList();
                    }

                    // Every domain (agent, team, workflow) is backed by IEntityRepository<T> —
                    // folders/browse work uniformly across all three, so FolderName is resolved
                    // the same way regardless of kind.
                    var entityFolderName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    await CollectFolderNamesAsync(agents, entityFolderName, ct).ConfigureAwait(false);
                    await CollectFolderNamesAsync(teams, entityFolderName, ct).ConfigureAwait(false);
                    await CollectFolderNamesAsync(workflows, entityFolderName, ct).ConfigureAwait(false);

                    return Results.Ok(items.Select(u => new EntityDto(
                        Id: u.Id,
                        DisplayName: u.Name,
                        Kind: u.Kind.ToString().ToLowerInvariant(),
                        Role: u.Role,
                        Tags: u.Tags,
                        Inputs: u.Inputs.Select(MapPort).ToList(),
                        Outputs: u.Outputs.Select(MapPort).ToList(),
                        FolderName: entityFolderName.GetValueOrDefault(u.Id)
                    )));
                }
            )
            .WithName("ListExecutableEntities")
            .WithSummary("List all executable entities (agents, teams, workflows) from the library, optionally filtered by tag")
            .WithTags("Entities");

        return endpoints;
    }

    private static async ValueTask CollectFolderNamesAsync<TEntity>(IEntityRepository<TEntity> repo, Dictionary<string, string> entityFolderName, CancellationToken ct) where TEntity : Agentivity.Domain.Entity
    {
        var folders = await repo.ListFoldersAsync(null, ct).ConfigureAwait(false);
        foreach (var folder in folders)
        {
            foreach (var entity in await repo.ListAsync(folder.FolderId, ct).ConfigureAwait(false))
                entityFolderName[entity.Id] = folder.Name;
        }
    }

    // ── DTOs ──────────────────────────────────────────────────────────────────

    private static PortDto MapPort(PortDefinition p) => new(p.Name, p.Type, p.Description);

    private sealed record EntityDto(string Id, string DisplayName, string Kind, string? Role, IReadOnlyList<string> Tags, IReadOnlyList<PortDto> Inputs, IReadOnlyList<PortDto> Outputs, string? FolderName = null);

    private sealed record PortDto(string Name, string Type, string? Description);
}
