namespace Agentivity.Infrastructure.Plugins;

/// <summary>
/// Optional plugin capability for contributing HTTP endpoints to the host application.
/// Plugins that need non-HTTP transports should register their own hosted services in <see cref="IWorkflowPlugin.ConfigureServices"/>.
/// </summary>
public interface IEndpointContributorProvider
{
    IEnumerable<Type> GetEndpointContributorTypes();
}
