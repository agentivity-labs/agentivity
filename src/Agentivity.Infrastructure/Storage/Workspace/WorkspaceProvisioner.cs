namespace Agentivity.Infrastructure.Storage.Workspace;

/// <summary>
/// Domain-facing facade over <see cref="IWorkspaceObjectStoreFactory"/>. Domain modules
/// (Workflow, Agentic, PromptEngine, ...) depend on this — never on the factory directly.
/// Which storage module is loaded is the concern of the Workspace module system, not of any individual domain.
/// </summary>
public interface IWorkspaceProvisioner
{
    IWorkspaceObjectStore CreateStore(string rootPath);

    IWorkspaceObjectVersionStore? CreateVersionStore(string versionsRootPath, IWorkspaceObjectStore store);
}

public sealed class WorkspaceProvisioner(IWorkspaceObjectStoreFactory factory) : IWorkspaceProvisioner
{
    private readonly IWorkspaceObjectStoreFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public IWorkspaceObjectStore CreateStore(string rootPath) => _factory.CreateStore(rootPath);

    public IWorkspaceObjectVersionStore? CreateVersionStore(string versionsRootPath, IWorkspaceObjectStore store) => _factory.CreateVersionStore(versionsRootPath, store);
}
