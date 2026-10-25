using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Domain.Team;
using Agentivity.Infrastructure.Agentic;
using Agentivity.Infrastructure.Core.Json;
using Agentivity.Infrastructure.Repository.Entities;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Module.Assistant.Changesets;

namespace Agentivity.Module.Assistant.Operations;

/// <summary>A saved entity, as the changeset sees it: its kind, its identity and its full JSON.</summary>
public sealed record LiveEntity(string Kind, string Id, string Name, JsonObject Json);

/// <summary>A saved entity without its content: enough to list and to point at.</summary>
/// <param name="Outputs">Its named outputs (beside its answer, "output"): what a step that calls it reads as {{ step.name }}.</param>
public sealed record EntityRef(string Kind, string Id, string Name, IReadOnlyList<string> Tags, IReadOnlyList<string>? Outputs = null);

/// <summary>Reads the saved workspace. The engine goes through this so it can be tested without any storage.</summary>
public interface IEntityLookup
{
    /// <summary>The saved entity with this id, of any kind, or null.</summary>
    Task<LiveEntity?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Every saved entity whose name equals <paramref name="name"/> (ignoring case).</summary>
    Task<IReadOnlyList<LiveEntity>> FindByNameAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>The saved entities, optionally of one kind and whose name or tags contain <paramref name="query"/>, without their content.</summary>
    Task<IReadOnlyList<EntityRef>> ListAsync(string? kind = null, string? query = null, int limit = 50, CancellationToken cancellationToken = default);
}

/// <summary>Reads the saved workspace through the platform's own entity repositories.</summary>
public sealed class RepositoryEntityLookup(IEntityRepository<WorkflowEntity> workflows, IEntityRepository<AgentEntity> agents, IEntityRepository<TeamEntity> teams) : IEntityLookup
{
    public async Task<LiveEntity?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(id, out _))
            return null;

        var workflow = await workflows.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (workflow.IsSuccess && workflow.Value is { } w)
            return Make(EntityKinds.Workflow, w.Id, w.Name, w);

        var agent = await agents.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (agent.IsSuccess && agent.Value is { } a)
            return Make(EntityKinds.Agent, a.Id, a.Name, a);

        var team = await teams.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (team.IsSuccess && team.Value is { } t)
            return Make(EntityKinds.Team, t.Id, t.Name, t);

        return null;
    }

    public async Task<IReadOnlyList<LiveEntity>> FindByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        var found = new List<LiveEntity>();
        foreach (var w in await workflows.ListAsync(null, cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase))
                found.Add(Make(EntityKinds.Workflow, w.Id, w.Name, w));
        }

        foreach (var a in await agents.ListAsync(null, cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
                found.Add(Make(EntityKinds.Agent, a.Id, a.Name, a));
        }

        foreach (var t in await teams.ListAsync(null, cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
                found.Add(Make(EntityKinds.Team, t.Id, t.Name, t));
        }

        return found;
    }

    public async Task<IReadOnlyList<EntityRef>> ListAsync(string? kind = null, string? query = null, int limit = 50, CancellationToken cancellationToken = default)
    {
        var all = new List<EntityRef>();
        if (kind is null or EntityKinds.Workflow)
            all.AddRange((await workflows.ListAsync(null, cancellationToken).ConfigureAwait(false)).Select(w => new EntityRef(EntityKinds.Workflow, w.Id, w.Name, w.Tags, Named(w))));
        if (kind is null or EntityKinds.Agent)
            all.AddRange((await agents.ListAsync(null, cancellationToken).ConfigureAwait(false)).Select(a => new EntityRef(EntityKinds.Agent, a.Id, a.Name, a.Tags, Named(a))));
        if (kind is null or EntityKinds.Team)
            all.AddRange((await teams.ListAsync(null, cancellationToken).ConfigureAwait(false)).Select(t => new EntityRef(EntityKinds.Team, t.Id, t.Name, t.Tags)));

        IEnumerable<EntityRef> filtered = all;
        if (!string.IsNullOrWhiteSpace(query))
            filtered = filtered.Where(e => e.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || e.Tags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase)));
        return [.. filtered.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Take(Math.Max(1, limit))];
    }

    /// <summary>Its named outputs, without its answer.</summary>
    private static IReadOnlyList<string> Named(Agentivity.Domain.Executable.IExecutableDefinition entity) =>
        [.. entity.Outputs.Select(o => o.Name).Where(n => !string.Equals(n, "output", StringComparison.OrdinalIgnoreCase))];

    private static LiveEntity Make<T>(string kind, string id, string name, T entity) => new(kind, id, name, (JsonObject)JsonSerializer.SerializeToNode(entity, JsonDefaults.Default)!);
}
