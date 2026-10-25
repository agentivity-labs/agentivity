namespace Agentivity.Infrastructure.Repository;

/// <summary>An agent or a workflow with a step that names an asset.</summary>
public sealed record AssetUse(string Kind, string Id, string Name);

/// <summary>Who uses an asset (a Data Table, a knowledge base…), wherever it is: the agents and workflows with a step that names it.</summary>
public interface IAssetUsageFinder
{
    Task<IReadOnlyList<AssetUse>> UsersOfAsync(string assetId, CancellationToken cancellationToken = default);
}
