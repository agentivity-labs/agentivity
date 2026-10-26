using System.Text.Json;
using Agentivity.Domain.Run;
using Agentivity.Infrastructure.Agentic;
using Agentivity.Infrastructure.Agentic.Team;
using Agentivity.Infrastructure.Execution;
using Agentivity.Infrastructure.Execution.Executable;
using Agentivity.Infrastructure.Execution.Executions;
using Agentivity.Infrastructure.Execution.Runs;
using Agentivity.Infrastructure.Identity;
using Agentivity.Infrastructure.Repository;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Observability.Contracts.Events;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ExecutionContext = Agentivity.Infrastructure.Execution.Executions.ExecutionContext;

namespace Agentivity.Host.Executions;

/// <summary>
/// Starts an execution from within the platform (see <see cref="IExecutionLauncher"/>), through <see cref="ExecutionService"/>, as the
/// Studio and the MCP server do: it does not wait for the execution to end.
/// </summary>
public sealed class ExecutionLauncher(IServiceProvider services) : IExecutionLauncher
{
    public async Task<ExecutionLaunch> StartAsync(string entityId, string input, Caller startedBy, string? origin, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var started = await provider.GetRequiredService<ExecutionService>().StartAsync(
            new StartExecutionRequest(entityId, input),
            provider.GetRequiredService<IExecutableResolver>(),
            provider.GetRequiredService<IRunEventBus>(),
            provider.GetService<ITeamEngine>(),
            provider.GetRequiredService<IAgenticRepository>(),
            provider.GetService<WorkflowEngine>(),
            provider.GetRequiredService<IRepository<ExecutionContext, ExecutionRecord>>(),
            provider.GetRequiredService<ILoggerFactory>(),
            cancellationToken,
            provider.GetService<IRepository<RunContext, RunRecord>>(),
            startedBy,
            origin: origin
        ).ConfigureAwait(false);

        var value = (started as IValueHttpResult)?.Value;
        var said = value is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(value);
        if (started is IStatusCodeHttpResult { StatusCode: StatusCodes.Status202Accepted } && said?.TryGetProperty("executionId", out var id) == true && id.GetString() is { } executionId)
            return ExecutionLaunch.Of(executionId);

        string? Text(string name) => said is { ValueKind: JsonValueKind.Object } body && body.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        var status = (started as IStatusCodeHttpResult)?.StatusCode;
        return ExecutionLaunch.Refused(
            Text("error") ?? (status == StatusCodes.Status404NotFound ? "not_found" : "not_started"),
            Text("message") ?? Text("error") ?? $"It could not start ({status?.ToString() ?? "no answer"}).");
    }
}
