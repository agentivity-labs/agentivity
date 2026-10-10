using Agentivity.Infrastructure.Graph;
using Agentivity.Infrastructure.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentivity.Infrastructure.Graph.GraphHostV1;

/// <summary>
/// Module that provisions and exposes the default <see cref="IGraphHost"/> singleton.
/// The default host manages all graphs, handles concurrent execution and load-balances
/// task slots across all active graph runs in the process.
///
/// Responsibilities:
/// - Reads <see cref="GraphHostModuleOptions"/> from the <c>GraphHost</c> configuration section.
/// - Constructs a single <see cref="GraphHost"/> instance.
/// - Registers it as <see cref="IGraphHost"/> in the DI container so every runtime that
///   needs graph scheduling can inject it.
///
/// Register in <c>appsettings.json</c> under <c>Modules:Registrations</c>:
/// <code>
/// {
///   "Type": "Agentivity.Infrastructure.Graph.GraphHostV1.Module, Agentivity.Infrastructure.Graph.GraphHostV1",
///   "AssemblyPath": "Agentivity.Infrastructure.Graph.GraphHostV1.dll",
///   "Enabled": true
/// }
/// </code>
/// </summary>
public sealed class Module(bool enabled = true) : IModule
{
    // ── IModule identity ──────────────────────────────────────────────────────

    public string Id => "graph-host-v1";
    public string Name => "Graph Host V1";
    public string ModuleType => "system";
    public bool Enabled => enabled;
    public IReadOnlyCollection<string> Capabilities => ["graph.host"];

    // ── IModule lifecycle ─────────────────────────────────────────────────────

    /// <summary>
    /// Reads <see cref="GraphHostModuleOptions"/> from configuration and registers a singleton
    /// <see cref="IGraphHost"/> with the resolved concurrency settings.
    /// </summary>
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(GraphHostModuleOptions.SectionName).Get<GraphHostModuleOptions>() ?? new GraphHostModuleOptions();

        services.TryAddSingleton<IGraphHost>(new GraphHost(new GraphHostOptions { GlobalMaxConcurrentTasks = options.GlobalMaxConcurrentTasks }));
    }

    /// <summary>This is a system module — it exposes no HTTP endpoints.</summary>
    public void MapEndpoints(WebApplication app) { }
}
