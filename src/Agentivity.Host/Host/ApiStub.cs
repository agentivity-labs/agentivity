using Agentivity.Domain.Execution;
using Agentivity.Host.Runs;
using Agentivity.Infrastructure.Agentic;
using Agentivity.Infrastructure.Agentic.Agent;
using Agentivity.Infrastructure.Agentic.Team;
using Agentivity.Infrastructure.Hosting;
using Agentivity.Infrastructure.Stores;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Observability.Contracts;
using Agentivity.Observability.Contracts.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Agentivity.Infrastructure.Execution.Executions;
using Agentivity.Infrastructure.Execution.Executable;

namespace Agentivity.Host.Host;

/// <summary>
/// Production <see cref="IApiStub"/> — resolves <see cref="RunsService"/> from the host's DI
/// container and starts the run directly (no HTTP). Used by <see cref="AgentivityHost"/>.
/// </summary>
public sealed class ApiStub : IApiStub
{
    public async Task<object> PostRunAsync(IServiceProvider services, object body, CancellationToken ct = default)
    {
        var request = body as UnifiedRunRequest ?? throw new ArgumentException($"Expected a {nameof(UnifiedRunRequest)}.", nameof(body));

        var runsService = services.GetRequiredService<RunsService>();
        var resolver = services.GetRequiredService<IExecutableResolver>();
        var eventBus = services.GetRequiredService<IRunEventBus>();
        var teamEngine = services.GetService<ITeamEngine>();
        var catalogue = services.GetRequiredService<IAgenticRepository>();
        var workflowEngine = services.GetService<WorkflowEngine>();
        var executionStore = services.GetRequiredService<IExecutionRecordStore>();
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();

        var result = await runsService.StartRunAsync(request, resolver, eventBus, teamEngine, catalogue, workflowEngine, executionStore, loggerFactory, ct).ConfigureAwait(false);
        return result;
    }
}
