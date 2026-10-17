namespace Agentivity.Infrastructure.Storage.Workspace;

/// <summary>
/// Domain-facing facade over <see cref="IWorkspaceObjectStoreFactory"/>. Domain modules
/// (Workflow, Agentic, PromptEngine, ...) depend on this — never on the factory directly.
/// Which storage module is loaded is the concern of the Workspace module system, not of any individual domain.
/// </summary>
public interface IWorkspaceProvisioner
{
    IWorkspaceObjectStore CreateStore(string rootPath);

    /// <summary>
    /// The store of a domain that says which object types it holds (see <see cref="WorkspaceDomainTypes"/>): a module that takes
    /// over what was kept without the types (by Json.Advanced) needs them. A module that does not, ignores them.
    /// </summary>
    IWorkspaceObjectStore CreateStore(string rootPath, WorkspaceDomainTypes types) => CreateStore(rootPath);

    IWorkspaceObjectVersionStore? CreateVersionStore(string versionsRootPath, IWorkspaceObjectStore store);
}

public sealed class WorkspaceProvisioner(IWorkspaceObjectStoreFactory factory) : IWorkspaceProvisioner
{
    private readonly IWorkspaceObjectStoreFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public IWorkspaceObjectStore CreateStore(string rootPath) => _factory.CreateStore(rootPath);

    public IWorkspaceObjectStore CreateStore(string rootPath, WorkspaceDomainTypes types) => _factory.CreateStore(rootPath, types);

    public IWorkspaceObjectVersionStore? CreateVersionStore(string versionsRootPath, IWorkspaceObjectStore store) => _factory.CreateVersionStore(versionsRootPath, store);
}
