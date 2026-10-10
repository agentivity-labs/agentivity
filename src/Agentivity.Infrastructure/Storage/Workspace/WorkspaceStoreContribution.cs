namespace Agentivity.Infrastructure.Storage.Workspace;

/// <summary>
/// Declares that a domain module (Workflow, Agentic, PromptEngine, ...) owns a set of
/// workspace object types, backed by a single store instance (its own root path). Each domain
/// module registers one of these at startup; <see cref="RoutingWorkspaceObjectStore"/> and
/// <see cref="RoutingWorkspaceObjectVersionStore"/> aggregate all contributions into a single
/// app-wide routing table keyed by object type.
/// </summary>
public interface IWorkspaceStoreContribution
{
    IReadOnlyList<string> ObjectTypes { get; }

    IWorkspaceObjectStore Store { get; }

    /// <summary>Null when the loaded storage module provides no versioning (e.g. Basic).</summary>
    IWorkspaceObjectVersionStore? VersionStore { get; }
}

public sealed class WorkspaceStoreContribution : IWorkspaceStoreContribution
{
    public WorkspaceStoreContribution(IReadOnlyList<string> objectTypes, IWorkspaceObjectStore store, IWorkspaceObjectVersionStore? versionStore = null)
    {
        if (objectTypes is null || objectTypes.Count == 0)
        {
            throw new ArgumentException("A workspace store contribution must declare at least one object type.", nameof(objectTypes));
        }

        ObjectTypes = objectTypes;
        Store = store ?? throw new ArgumentNullException(nameof(store));
        VersionStore = versionStore;
    }

    public IReadOnlyList<string> ObjectTypes { get; }
    public IWorkspaceObjectStore Store { get; }
    public IWorkspaceObjectVersionStore? VersionStore { get; }
}
