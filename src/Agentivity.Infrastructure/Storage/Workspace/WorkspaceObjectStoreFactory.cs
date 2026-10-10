namespace Agentivity.Infrastructure.Storage.Workspace;

public interface IWorkspaceObjectStoreProvider
{
    IWorkspaceObjectStore CreateStore(string rootPath);
}

/// <summary>
/// Extended provider that can also create a companion version store.
/// Optional — not every storage module supports versioning.
/// </summary>
public interface IWorkspaceObjectVersionStoreProvider : IWorkspaceObjectStoreProvider
{
    IWorkspaceObjectVersionStore CreateVersionStore(string versionsRootPath, IWorkspaceObjectStore store);
}

public interface IWorkspaceObjectStoreFactory
{
    IWorkspaceObjectStore CreateStore(string rootPath);
    IWorkspaceObjectVersionStore? CreateVersionStore(string versionsRootPath, IWorkspaceObjectStore store);
}

/// <summary>
/// Builds stores with the one storage module that is loaded. There is no storage "mode": which module DLL is registered is what
/// decides — loading none, or more than one, is a configuration error.
/// </summary>
public sealed class WorkspaceObjectStoreFactory(IEnumerable<IWorkspaceObjectStoreProvider> providers) : IWorkspaceObjectStoreFactory
{
    private readonly IReadOnlyList<IWorkspaceObjectStoreProvider> _providers = providers.ToArray();

    public IWorkspaceObjectStore CreateStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        return ActiveProvider().CreateStore(rootPath);
    }

    public IWorkspaceObjectVersionStore? CreateVersionStore(string versionsRootPath, IWorkspaceObjectStore store) => (ActiveProvider() as IWorkspaceObjectVersionStoreProvider)?.CreateVersionStore(versionsRootPath, store);

    private IWorkspaceObjectStoreProvider ActiveProvider() =>
        _providers.Count switch
        {
            0 => throw new InvalidOperationException("No workspace storage module is loaded. Register exactly one (for example Agentivity.Module.Workspace.Json.Basic) under 'Modules:Registrations'."),
            1 => _providers[0],
            _ => throw new InvalidOperationException($"{_providers.Count} workspace storage modules are loaded; only one may be enabled at a time. Disable the others under 'Modules:Registrations'."),
        };
}
