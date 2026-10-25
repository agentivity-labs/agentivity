using System.Text.Json;
using Agentivity.Infrastructure.Agentic;
using Agentivity.Infrastructure.Workflows;

namespace Agentivity.Host.Entities;

/// <summary>An agent or a workflow that uses an asset of the workspace (a Knowledge Base, a Data Table…).</summary>
public sealed record AssetUser(string Kind, string Id, string Name);

/// <summary>
/// Which agents and workflows use the assets of the workspace: those with a step whose settings name the asset's id. What tells,
/// before an asset is deleted, what would no longer work.
/// </summary>
public sealed class AssetUsageFinder(
    Agentivity.Infrastructure.Repository.Entities.IEntityRepository<AgentEntity> agents,
    Agentivity.Infrastructure.Repository.Entities.IEntityRepository<WorkflowEntity> workflows) : Agentivity.Infrastructure.Repository.IAssetUsageFinder
{
    public async Task<IReadOnlyList<Agentivity.Infrastructure.Repository.AssetUse>> UsersOfAsync(string assetId, CancellationToken cancellationToken = default)
    {
        var usages = AssetUsages.Of([assetId], await agents.ListAsync(null, cancellationToken).ConfigureAwait(false), await workflows.ListAsync(null, cancellationToken).ConfigureAwait(false));
        return [.. usages.Values.SelectMany(u => u).Select(u => new Agentivity.Infrastructure.Repository.AssetUse(u.Kind, u.Id, u.Name))];
    }
}

public static class AssetUsages
{
    public static IReadOnlyDictionary<string, IReadOnlyList<AssetUser>> Of(IReadOnlyCollection<string> assetIds, IEnumerable<AgentEntity> agents, IEnumerable<WorkflowEntity> workflows)
    {
        var users = assetIds.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(id => id, _ => new List<AssetUser>(), StringComparer.OrdinalIgnoreCase);
        void Collect(WorkflowEntity graph, AssetUser user)
        {
            foreach (var id in graph.Nodes.SelectMany(n => NamedIds(n.Parameters)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (users.TryGetValue(id, out var found))
                    found.Add(user);
            }
        }

        foreach (var agent in agents)
            Collect(agent.Graph, new AssetUser("agent", agent.Id, agent.Name));
        foreach (var workflow in workflows)
            Collect(workflow, new AssetUser("workflow", workflow.Id, workflow.Name));
        return users.ToDictionary(u => u.Key, u => (IReadOnlyList<AssetUser>)[.. u.Value.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)], StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> NamedIds(JsonElement inputs) =>
        inputs.ValueKind == JsonValueKind.Object
            ? inputs.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).Select(p => p.Value.GetString()!.Trim())
            : [];
}
