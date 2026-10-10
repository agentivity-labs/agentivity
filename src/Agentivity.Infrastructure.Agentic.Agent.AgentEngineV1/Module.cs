using Agentivity.Infrastructure.Agentic.Agent;
using Agentivity.Infrastructure.Graph;
using Agentivity.Infrastructure.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentivity.Infrastructure.Agentic.AgentEngineV1;

/// <summary>
/// Module entrypoint for the Agent Engine V1.
/// Loaded dynamically from <c>Modules:Registrations</c> in appsettings.
/// Registers <see cref="AgentEngine"/> as <see cref="IAgentEngine"/> in DI,
/// injecting <see cref="IGraphHost"/> when available.
///
/// <code>
/// {
///   "Type": "Agentivity.Infrastructure.Agentic.AgentEngineV1.Module, Agentivity.Infrastructure.Agentic.Agent.AgentEngineV1",
///   "AssemblyPath": "Agentivity.Infrastructure.Agentic.Agent.AgentEngineV1.dll",
///   "Enabled": true
/// }
/// </code>
/// </summary>
public sealed class Module(bool enabled = true) : IModule
{
    public string Id => AgentEngine.EngineId;
    public string Name => "Agent Engine V1";
    public string ModuleType => "system";
    public bool Enabled => enabled;
    public IReadOnlyCollection<string> Capabilities => ["agent.engine"];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration) => services.TryAddSingleton<IAgentEngine>(_ => new AgentEngine());

    public void MapEndpoints(WebApplication app) { }
}
