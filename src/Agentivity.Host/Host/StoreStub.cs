using Agentivity.Host.Host;
using Agentivity.Infrastructure.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Agentivity.Host.Host;

/// <summary>
/// Production <see cref="IStoreStub"/> — a no-op. The real definition repositories
/// (<c>IEntityRepository&lt;WorkflowEntity&gt;</c>, <c>IAgenticRepository</c>, <c>ICredentialsRepository</c>)
/// are registered by <see cref="AgentivityHost"/> via the dynamic module loader.
/// </summary>
public sealed class StoreStub : IStoreStub
{
    public void Configure(IServiceCollection services)
    {
        // Intentionally empty — production uses the real file-backed repositories.
    }
}
