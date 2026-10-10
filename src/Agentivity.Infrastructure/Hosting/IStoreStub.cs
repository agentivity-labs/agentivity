using Microsoft.Extensions.DependencyInjection;

namespace Agentivity.Infrastructure.Hosting;

/// <summary>
/// Configures the storage backing the three definition repositories
/// (<c>IEntityRepository&lt;WorkflowEntity&gt;</c>, <c>IAgenticRepository</c>, <c>ICredentialsRepository</c>)
/// for a built <see cref="IAgentivityHost"/>. The production implementation is a no-op
/// (the host loads the real file-backed repositories via its module loader); the test
/// implementation replaces them with in-memory fakes that can be populated by code.
/// </summary>
public interface IStoreStub
{
    /// <summary>Applies any service overrides to <paramref name="services"/> before the host is built.</summary>
    void Configure(IServiceCollection services);
}
